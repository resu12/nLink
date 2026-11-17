# Frozen pre-query-reuse route consistency evaluator; only the function name differs from the original.
function Invoke-RouteConsistencyBaseline {
    param([object[]]$TransferEvents)

    $routeSelectedEvents = @(
        Get-FileTransferCanonicalRouteSelectionEvents -Events $TransferEvents
    )
    $routeStateEvents = @($routeSelectedEvents)
    $routeAwareEvents = @(
        $TransferEvents |
            Where-Object {
                (Test-FileTransferProductionRouteEvidenceEvent -Event $_) -and
                (Test-FileTransferRouteAwareEvent -Event $_)
            } |
            Sort-Object Sequence, LineNumber
    )
    $terminalEvents = @(Get-FileTransferTerminalEvents -Events $TransferEvents | Sort-Object Sequence)
    $findings = New-Object System.Collections.ArrayList
    $evidenceEvents = New-Object System.Collections.ArrayList
    $lastSelectedByDirectionKey = @{}

    foreach ($selected in @($routeStateEvents)) {
        $route = Get-FileTransferEventField -Event $selected -Name 'route' -Default ''
        $directionKey = Get-FileTransferRouteEventKey -Event $selected -IncludeDirection
        if ($lastSelectedByDirectionKey.ContainsKey($directionKey)) {
            $previous = $lastSelectedByDirectionKey[$directionKey]
            $previousRoute = Get-FileTransferEventField -Event $previous -Name 'route' -Default ''
            if ($route -ne $previousRoute -and
                -not (Test-FileTransferRouteTerminalBetween -TerminalEvents $terminalEvents -DirectionKey $directionKey -AfterSequence $previous.Sequence -BeforeSequence $selected.Sequence) -and
                -not (Test-FileTransferAllowedLiveFallbackRouteTransition -Previous $previous -Selected $selected)) {
                Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route changed before prior terminal: transfer_direction={0}; previous_route={1}; new_route={2}; event={3}" -f $directionKey, $previousRoute, $route, (Format-FileTransferEvidenceLine -Event $selected)) -Event $selected
            }
        }

        $lastSelectedByDirectionKey[$directionKey] = $selected

        $expectedProtocol = Get-FileTransferRouteExpectedProtocol -Route $route
        if ([string]::IsNullOrWhiteSpace($expectedProtocol)) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("unknown route selected: {0}" -f (Format-FileTransferEvidenceLine -Event $selected)) -Event $selected
            continue
        }

        $protocolVersion = Get-FileTransferEventField -Event $selected -Name 'protocol_version' -Default ''
        if ($protocolVersion -ne $expectedProtocol) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route selected protocol mismatch: route={0}; expected_protocol={1}; event={2}" -f $route, $expectedProtocol, (Format-FileTransferEvidenceLine -Event $selected)) -Event $selected
        }

        $runtimeProfile = Normalize-FileTransferRouteRuntimeProfile -Value (Get-FileTransferEventField -Event $selected -Name 'runtime_profile' -Default '')
        $expectedRuntimeProfile = Get-FileTransferRouteExpectedRuntimeProfile -Route $route
        if (-not [string]::IsNullOrWhiteSpace($runtimeProfile) -and
            -not (Test-FileTransferRouteRuntimeProfileMatches -Expected $expectedRuntimeProfile -Actual $runtimeProfile)) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route selected runtime mismatch: route={0}; expected_runtime={1}; actual_runtime={2}; event={3}" -f $route, $expectedRuntimeProfile, $runtimeProfile, (Format-FileTransferEvidenceLine -Event $selected)) -Event $selected
        }

        $bridgePolicy = Get-FileTransferEventField -Event $selected -Name 'bridge_recovery_policy' -Default ''
        $expectedBridgePolicy = Get-FileTransferRouteExpectedBridgePolicy -Route $route
        if (-not [string]::IsNullOrWhiteSpace($bridgePolicy) -and $bridgePolicy -ne $expectedBridgePolicy) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route selected bridge policy mismatch: route={0}; expected_bridge_policy={1}; actual_bridge_policy={2}; event={3}" -f $route, $expectedBridgePolicy, $bridgePolicy, (Format-FileTransferEvidenceLine -Event $selected)) -Event $selected
        }

        $diagnosticEnabled = Get-FileTransferEventField -Event $selected -Name 'diagnostic_regular_nkn_v6' -Default '0'
        if ($route -eq 'diagnostic_regular_nkn_v6' -and $diagnosticEnabled -ne '1') {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("diagnostic route selected without diagnostic marker: {0}" -f (Format-FileTransferEvidenceLine -Event $selected)) -Event $selected
        }
    }

    if ($routeAwareEvents.Count -gt 0 -and $routeStateEvents.Count -eq 0) {
        Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding 'route-aware transfer events were present but no filetransfer_route_selected event was found' -Event $routeAwareEvents[0]
    }

    foreach ($event in @($TransferEvents | Sort-Object Sequence)) {
        $frameType = Get-FileTransferEventField -Event $event -Name 'frame_type' -Default ''
        $originalFrameType = Get-FileTransferEventField -Event $event -Name 'original_frame_type' -Default ''
        $protocolVersion = Get-FileTransferEventField -Event $event -Name 'protocol_version' -Default ''
        if ($event.EventName -like 'filetransfer_v5_*' -or
            $frameType -like '*.v5' -or
            $originalFrameType -like '*.v5' -or
            $protocolVersion -eq '5') {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("obsolete_protocol_v5: {0}" -f (Format-FileTransferEvidenceLine -Event $event)) -Event $event
        }
    }

    foreach ($event in @($routeAwareEvents)) {
        if ($event.EventName -eq 'filetransfer_route_selected') {
            continue
        }

        if (Test-FileTransferLegHistoryRouteEvent -Event $event) {
            Add-FileTransferRouteSelfConsistencyFindings -Findings $findings -EvidenceEvents $evidenceEvents -Event $event -Context 'transfer leg history'
            continue
        }

        if (Test-FileTransferLiveRouteEpochRouteEvent -Event $event) {
            Add-FileTransferRouteSelfConsistencyFindings -Findings $findings -EvidenceEvents $evidenceEvents -Event $event -Context 'live route epoch'
            continue
        }

        if (Test-FileTransferRouteEventCanPrecedeSelection -Event $event) {
            Add-FileTransferRouteSelfConsistencyFindings -Findings $findings -EvidenceEvents $evidenceEvents -Event $event -Context 'preselection transfer leg'
            continue
        }

        if (Test-FileTransferFallbackLegStartRouteEventBeforeSelection -Event $event -RouteSelectedEvents $routeStateEvents) {
            Add-FileTransferRouteSelfConsistencyFindings -Findings $findings -EvidenceEvents $evidenceEvents -Event $event -Context 'fallback preselection transfer leg'
            continue
        }

        if (Test-FileTransferFallbackAuthorityRouteEventBeforeSelection -Event $event -RouteSelectedEvents $routeStateEvents) {
            Add-FileTransferRouteSelfConsistencyFindings -Findings $findings -EvidenceEvents $evidenceEvents -Event $event -Context 'fallback authority'
            continue
        }

        $selected = Get-FileTransferSelectedRouteForEvent -Event $event -RouteSelectedEvents $routeStateEvents
        if ($null -eq $selected) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route-aware event has no selected route: {0}" -f (Format-FileTransferEvidenceLine -Event $event)) -Event $event
            continue
        }

        $selectedRoute = Get-FileTransferEventField -Event $selected -Name 'route' -Default ''
        $expectedProtocol = Get-FileTransferRouteExpectedProtocol -Route $selectedRoute
        $expectedRuntimeProfile = Get-FileTransferRouteExpectedRuntimeProfile -Route $selectedRoute
        $expectedBridgePolicy = Get-FileTransferRouteExpectedBridgePolicy -Route $selectedRoute

        $eventRoute = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
        $eventSupersededBySelectedRoute = Test-FileTransferRouteEventSupersededBySelectedRoute -Event $event -SelectedRoute $selectedRoute -ExpectedProtocol $expectedProtocol
        $eventSupersededByNewerLiveRouteEpoch = Test-FileTransferControlPlaneRouteEventSupersededByNewerLiveEpoch -Event $event -Selected $selected
        if (-not [string]::IsNullOrWhiteSpace($eventRoute) -and $eventRoute -ne $selectedRoute) {
            if (-not $eventSupersededBySelectedRoute -and -not $eventSupersededByNewerLiveRouteEpoch) {
                Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route token mismatch: selected_route={0}; event_route={1}; event={2}" -f $selectedRoute, $eventRoute, (Format-FileTransferEvidenceLine -Event $event)) -Event $event
            }
        }

        if ($eventSupersededBySelectedRoute -or $eventSupersededByNewerLiveRouteEpoch) {
            Add-FileTransferRouteSelfConsistencyFindings -Findings $findings -EvidenceEvents $evidenceEvents -Event $event -Context 'superseded route event'
            continue
        }

        $eventProtocol = Get-FileTransferEventField -Event $event -Name 'protocol_version' -Default ''
        if (-not [string]::IsNullOrWhiteSpace($eventProtocol) -and -not [string]::IsNullOrWhiteSpace($expectedProtocol) -and $eventProtocol -ne $expectedProtocol) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route protocol mismatch: route={0}; expected_protocol={1}; actual_protocol={2}; event={3}" -f $selectedRoute, $expectedProtocol, $eventProtocol, (Format-FileTransferEvidenceLine -Event $event)) -Event $event
        }

        $eventRuntimeProfile = Normalize-FileTransferRouteRuntimeProfile -Value (Get-FileTransferEventField -Event $event -Name 'runtime_profile' -Default '')
        if (-not [string]::IsNullOrWhiteSpace($eventRuntimeProfile) -and
            -not [string]::IsNullOrWhiteSpace($expectedRuntimeProfile) -and
            -not (Test-FileTransferRouteRuntimeProfileMatches -Expected $expectedRuntimeProfile -Actual $eventRuntimeProfile)) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route runtime mismatch: route={0}; expected_runtime={1}; actual_runtime={2}; event={3}" -f $selectedRoute, $expectedRuntimeProfile, $eventRuntimeProfile, (Format-FileTransferEvidenceLine -Event $event)) -Event $event
        }

        $eventBridgePolicy = Get-FileTransferEventField -Event $event -Name 'bridge_recovery_policy' -Default ''
        if (-not [string]::IsNullOrWhiteSpace($eventBridgePolicy) -and -not [string]::IsNullOrWhiteSpace($expectedBridgePolicy) -and $eventBridgePolicy -ne $expectedBridgePolicy) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route bridge policy mismatch: route={0}; expected_bridge_policy={1}; actual_bridge_policy={2}; event={3}" -f $selectedRoute, $expectedBridgePolicy, $eventBridgePolicy, (Format-FileTransferEvidenceLine -Event $event)) -Event $event
        }

        if ($selectedRoute -eq 'regular_nkn_v4_fast' -and
            ($event.EventName -eq 'filetransfer_v6_sender_started' -or $event.EventName -eq 'filetransfer_v6_receiver_started')) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("regular NKN V4 route entered V6 runtime: {0}" -f (Format-FileTransferEvidenceLine -Event $event)) -Event $event
        }

        if (($selectedRoute -eq 'post_tuna_fallback_v6' -or $selectedRoute -eq 'diagnostic_regular_nkn_v6') -and
            ($event.EventName -eq 'filetransfer_v4_sender_started' -or $event.EventName -eq 'filetransfer_v4_receiver_started')) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("V6 route entered regular V4 runtime: route={0}; event={1}" -f $selectedRoute, (Format-FileTransferEvidenceLine -Event $event)) -Event $event
        }

        if ($selectedRoute -eq 'file_tuna_v4' -and
            ($event.EventName -eq 'filetransfer_v6_sender_started' -or $event.EventName -eq 'filetransfer_v6_receiver_started')) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("file Tuna V4 route entered V6 runtime: {0}" -f (Format-FileTransferEvidenceLine -Event $event)) -Event $event
        }
    }

    $verdict = if ($findings.Count -gt 0) {
        'fail'
    }
    elseif ($routeAwareEvents.Count -eq 0) {
        'legacy'
    }
    else {
        'pass'
    }

    return [pscustomobject]@{
        Verdict = $verdict
        RouteSelectedEvents = @($routeSelectedEvents)
        RouteAwareEvents = @($routeAwareEvents)
        Findings = @($findings.ToArray())
        EvidenceEvents = @($evidenceEvents.ToArray())
    }
}
