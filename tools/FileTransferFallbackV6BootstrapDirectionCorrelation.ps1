Set-StrictMode -Version Latest

function Get-FileTransferFallbackV6BootstrapDirectionCorrelation {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Lines
    )

    $relevantEvents = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($eventName in @(
            'tuna_fallback_started',
            'filetransfer_fallback_nkn_proof_pending',
            'tuna_fallback_nkn_frame_received',
            'filetransfer_fallback_nkn_proof_unconfirmed',
            'filetransfer_fallback_nkn_proof_waiting_for_v6_epoch',
            'filetransfer_fallback_v6_proof_bootstrap_trigger_bound',
            'filetransfer_fallback_v6_proof_bootstrap_observed',
            'filetransfer_fallback_v6_proof_bootstrap_decided')) {
        $relevantEvents.Add($eventName) | Out-Null
    }

    $records = New-Object System.Collections.Generic.List[object]
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        $rawLine = [string]$Lines[$index]
        $eventOffset = $rawLine.IndexOf('event=', [System.StringComparison]::OrdinalIgnoreCase)
        if ($eventOffset -lt 0) {
            continue
        }
        $line = $rawLine.Substring($eventOffset)
        $eventName = Get-FileTransferAuthorityLogField -Line $line -Name 'event'
        if (-not $relevantEvents.Contains($eventName)) {
            continue
        }

        $writer = Get-FileTransferAuthorityLogField -Line $line -Name 'log_writer_id'
        $session = Get-FileTransferAuthorityLogField -Line $line -Name 'session_id'
        $writerSequence = 0L
        if ([string]::IsNullOrWhiteSpace($writer) -or
            [string]::IsNullOrWhiteSpace($session) -or
            -not [long]::TryParse(
                (Get-FileTransferAuthorityLogField -Line $line -Name 'log_writer_sequence'),
                [ref]$writerSequence) -or
            $writerSequence -le 0) {
            continue
        }

        $records.Add([pscustomobject]@{
                EventName = $eventName
                Writer = $writer
                Session = $session
                WriterSequence = $writerSequence
                InputIndex = [long]$index
                Line = $line
                RawLine = $rawLine
            }) | Out-Null
    }

    $currentByWriterSession = @{}
    $episodes = @{}
    foreach ($record in @($records | Sort-Object Writer, WriterSequence, InputIndex)) {
        $scopeKey = "$($record.Writer)|$($record.Session)".ToLowerInvariant()
        if ($record.EventName -eq 'tuna_fallback_started') {
            $fallbackEpoch = 0L
            if (-not [long]::TryParse(
                    (Get-FileTransferAuthorityLogField -Line $record.Line -Name 'fallback_epoch'),
                    [ref]$fallbackEpoch) -or
                $fallbackEpoch -le 1) {
                $currentByWriterSession.Remove($scopeKey)
                continue
            }

            $episodeKey = "$scopeKey|$fallbackEpoch"
            if (-not $episodes.ContainsKey($episodeKey)) {
                $episodes[$episodeKey] = [pscustomobject]@{
                    Key = $episodeKey
                    Writer = $record.Writer
                    Session = $record.Session
                    FallbackEpoch = $fallbackEpoch
                    StartedSequence = $record.WriterSequence
                    Pending = $false
                    PendingSequence = 0L
                    BulkFrameObserved = $false
                    BulkFrameSequence = 0L
                    BulkProofObserved = $false
                    BulkProofSequence = 0L
                    ControlProofObserved = $false
                    ControlProofSequence = 0L
                    Candidates = New-Object System.Collections.Generic.List[object]
                    TriggerRecords = New-Object System.Collections.Generic.List[object]
                    ObservationRecords = New-Object System.Collections.Generic.List[object]
                    DecisionRecords = New-Object System.Collections.Generic.List[object]
                }
            }
            $currentByWriterSession[$scopeKey] = $episodes[$episodeKey]
            continue
        }

        if (-not $currentByWriterSession.ContainsKey($scopeKey)) {
            continue
        }
        $episode = $currentByWriterSession[$scopeKey]

        switch ($record.EventName) {
            'filetransfer_fallback_nkn_proof_pending' {
                $episode.Pending = $true
                $episode.PendingSequence = $record.WriterSequence
            }
            'tuna_fallback_nkn_frame_received' {
                $eventEpoch = 0L
                if ($episode.Pending -and
                    [long]::TryParse(
                        (Get-FileTransferAuthorityLogField -Line $record.Line -Name 'fallback_epoch'),
                        [ref]$eventEpoch) -and
                    $eventEpoch -eq $episode.FallbackEpoch -and
                    (Get-FileTransferAuthorityLogField -Line $record.Line -Name 'message_type') -eq
                        'file_transfer_data_frame' -and
                    (Get-FileTransferAuthorityLogField -Line $record.Line -Name 'channel') -eq 'bulk') {
                    $episode.BulkFrameObserved = $true
                    $episode.BulkFrameSequence = $record.WriterSequence
                }
            }
            'filetransfer_fallback_nkn_proof_unconfirmed' {
                $proof = Get-FileTransferAuthorityLogField -Line $record.Line -Name 'proof'
                $bulkSeen = Get-FileTransferAuthorityLogField -Line $record.Line -Name 'bulk_seen'
                $controlSeen = Get-FileTransferAuthorityLogField -Line $record.Line -Name 'control_seen'
                $requiresControl = Get-FileTransferAuthorityLogField `
                    -Line $record.Line -Name 'requires_control_proof'
                $requiresV6Recovery = Get-FileTransferAuthorityLogField `
                    -Line $record.Line -Name 'requires_v6_epoch_recovery'
                if ($episode.Pending -and
                    $episode.BulkFrameObserved -and
                    $proof -eq 'file_transfer_bulk_frame_received' -and
                    $bulkSeen -eq '1' -and
                    $controlSeen -eq '0' -and
                    $requiresControl -eq '1' -and
                    $requiresV6Recovery -eq '0') {
                    $episode.BulkProofObserved = $true
                    $episode.BulkProofSequence = $record.WriterSequence
                }
                elseif ($episode.BulkProofObserved -and
                    $proof -eq 'nkn_control_ack_received' -and
                    $bulkSeen -eq '1' -and
                    $controlSeen -eq '1' -and
                    $requiresControl -eq '0' -and
                    $requiresV6Recovery -eq '1') {
                    $episode.ControlProofObserved = $true
                    $episode.ControlProofSequence = $record.WriterSequence
                }
            }
            'filetransfer_fallback_nkn_proof_waiting_for_v6_epoch' {
                if ($episode.ControlProofObserved) {
                    $episode.Candidates.Add($record) | Out-Null
                }
            }
            'filetransfer_fallback_v6_proof_bootstrap_trigger_bound' {
                $eventEpoch = 0L
                if ([long]::TryParse(
                        (Get-FileTransferAuthorityLogField -Line $record.Line -Name 'fallback_epoch'),
                        [ref]$eventEpoch) -and
                    $eventEpoch -eq $episode.FallbackEpoch) {
                    $episode.TriggerRecords.Add($record) | Out-Null
                }
            }
            'filetransfer_fallback_v6_proof_bootstrap_observed' {
                $eventEpoch = 0L
                if ([long]::TryParse(
                        (Get-FileTransferAuthorityLogField -Line $record.Line -Name 'fallback_epoch'),
                        [ref]$eventEpoch) -and
                    $eventEpoch -eq $episode.FallbackEpoch) {
                    $episode.ObservationRecords.Add($record) | Out-Null
                }
            }
            'filetransfer_fallback_v6_proof_bootstrap_decided' {
                $eventEpoch = 0L
                if ([long]::TryParse(
                        (Get-FileTransferAuthorityLogField -Line $record.Line -Name 'fallback_epoch'),
                        [ref]$eventEpoch) -and
                    $eventEpoch -eq $episode.FallbackEpoch) {
                    $episode.DecisionRecords.Add($record) | Out-Null
                }
            }
        }
    }

    $rawCandidates = New-Object System.Collections.Generic.List[object]
    $distinctCandidates = New-Object System.Collections.Generic.List[object]
    $recoveredKeys = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($episode in @($episodes.Values | Sort-Object Writer, StartedSequence)) {
        if ($episode.Candidates.Count -eq 0) {
            continue
        }

        $expectedDirection = 'inbound'
        $firstWait = $episode.Candidates[0]
        $triggerIdentities = @{}
        foreach ($trigger in $episode.TriggerRecords.ToArray()) {
            $status = Get-FileTransferAuthorityLogField -Line $trigger.Line -Name 'status'
            if ($status -ne 'Ready' -or
                $trigger.WriterSequence -le $episode.PendingSequence) {
                continue
            }

            $pendingGeneration = 0L
            $transportEpoch = 0L
            $dataSessionGeneration = 0L
            $transferId = Get-FileTransferAuthorityLogField `
                -Line $trigger.Line -Name 'transfer_id'
            $frameFingerprint = Get-FileTransferAuthorityLogField `
                -Line $trigger.Line -Name 'frame_fingerprint'
            if ((Get-FileTransferAuthorityLogField -Line $trigger.Line -Name 'direction') -ne
                    $expectedDirection -or
                -not [long]::TryParse(
                    (Get-FileTransferAuthorityLogField -Line $trigger.Line -Name 'pending_generation'),
                    [ref]$pendingGeneration) -or
                $pendingGeneration -le 0 -or
                -not [long]::TryParse(
                    (Get-FileTransferAuthorityLogField -Line $trigger.Line -Name 'transport_epoch'),
                    [ref]$transportEpoch) -or
                $transportEpoch -lt 0 -or
                -not [long]::TryParse(
                    (Get-FileTransferAuthorityLogField -Line $trigger.Line -Name 'data_session_generation'),
                    [ref]$dataSessionGeneration) -or
                $dataSessionGeneration -le 0 -or
                [string]::IsNullOrWhiteSpace($transferId) -or
                $transferId -eq '(none)' -or
                [string]::IsNullOrWhiteSpace($frameFingerprint) -or
                $frameFingerprint -eq '(none)' -or
                (Get-FileTransferAuthorityLogField -Line $trigger.Line -Name 'bulk_seen') -ne '1' -or
                (Get-FileTransferAuthorityLogField -Line $trigger.Line -Name 'control_seen') -ne '1' -or
                (Get-FileTransferAuthorityLogField -Line $trigger.Line -Name 'authority_effect') -ne 'none') {
                continue
            }

            $triggerIdentity = @(
                $transferId
                $expectedDirection
                $pendingGeneration
                $transportEpoch
                $frameFingerprint
                $dataSessionGeneration) -join '|'
            $triggerIdentities[$triggerIdentity.ToLowerInvariant()] = [pscustomobject]@{
                Record = $trigger
                TransferId = $transferId
                PendingGeneration = $pendingGeneration
                TransportEpoch = $transportEpoch
                FrameFingerprint = $frameFingerprint
                DataSessionGeneration = $dataSessionGeneration
            }
        }

        $recovered = $false
        if ($triggerIdentities.Count -eq 1) {
            $trigger = @($triggerIdentities.Values)[0]
            $observationIdentities = @{}
            $observationInvalid = $false
            foreach ($observation in @($episode.ObservationRecords | Where-Object {
                        $_.WriterSequence -gt $firstWait.WriterSequence -and
                        (Get-FileTransferAuthorityLogField -Line $_.Line -Name 'direction') -eq
                            $expectedDirection
                    })) {
                $matchesTrigger =
                    (Get-FileTransferAuthorityLogField -Line $observation.Line -Name 'transfer_id') -eq
                        $trigger.TransferId -and
                    (Get-FileTransferAuthorityLogField -Line $observation.Line -Name 'observation_generation') -eq
                        [string]$trigger.PendingGeneration -and
                    (Get-FileTransferAuthorityLogField -Line $observation.Line -Name 'proof_kind') -eq
                        'file_transfer_bulk_frame_received' -and
                    (Get-FileTransferAuthorityLogField -Line $observation.Line -Name 'trigger_transport_epoch') -eq
                        [string]$trigger.TransportEpoch -and
                    (Get-FileTransferAuthorityLogField -Line $observation.Line -Name 'trigger_frame_fingerprint') -eq
                        $trigger.FrameFingerprint -and
                    (Get-FileTransferAuthorityLogField -Line $observation.Line -Name 'trigger_data_session_generation') -eq
                        [string]$trigger.DataSessionGeneration -and
                    (Get-FileTransferAuthorityLogField -Line $observation.Line -Name 'authority_effect') -eq
                        'coordinator_evaluation_only'
                if (-not $matchesTrigger) {
                    $observationInvalid = $true
                    continue
                }

                $observationIdentity = @(
                    $trigger.TransferId
                    $trigger.PendingGeneration
                    $trigger.TransportEpoch
                    $trigger.FrameFingerprint
                    $trigger.DataSessionGeneration) -join '|'
                if (-not $observationIdentities.ContainsKey($observationIdentity)) {
                    $observationIdentities[$observationIdentity] = $observation
                }
            }

            if (-not $observationInvalid -and $observationIdentities.Count -eq 1) {
                $observation = @($observationIdentities.Values)[0]
                $decisionIdentities = @{}
                $decisionInvalid = $false
                foreach ($decision in @($episode.DecisionRecords | Where-Object {
                            $_.WriterSequence -gt $observation.WriterSequence -and
                            (Get-FileTransferAuthorityLogField -Line $_.Line -Name 'direction') -eq
                                $expectedDirection
                        })) {
                    $bootstrapGeneration = 0L
                    $sourceDataSessionGeneration = 0L
                    $authorityId = Get-FileTransferAuthorityLogField `
                        -Line $decision.Line -Name 'authority_id'
                    $matchesTrigger =
                        (Get-FileTransferAuthorityLogField -Line $decision.Line -Name 'transfer_id') -eq
                            $trigger.TransferId -and
                        (Get-FileTransferAuthorityLogField -Line $decision.Line -Name 'trigger_transfer_id') -eq
                            $trigger.TransferId -and
                        (Get-FileTransferAuthorityLogField -Line $decision.Line -Name 'observation_generation') -eq
                            [string]$trigger.PendingGeneration -and
                        (Get-FileTransferAuthorityLogField -Line $decision.Line -Name 'trigger_transport_epoch') -eq
                            [string]$trigger.TransportEpoch -and
                        (Get-FileTransferAuthorityLogField -Line $decision.Line -Name 'trigger_frame_fingerprint') -eq
                            $trigger.FrameFingerprint -and
                        (Get-FileTransferAuthorityLogField -Line $decision.Line -Name 'trigger_data_session_generation') -eq
                            [string]$trigger.DataSessionGeneration -and
                        [long]::TryParse(
                            (Get-FileTransferAuthorityLogField -Line $decision.Line -Name 'source_data_session_generation'),
                            [ref]$sourceDataSessionGeneration) -and
                        $sourceDataSessionGeneration -gt 0 -and
                        (Get-FileTransferAuthorityLogField -Line $decision.Line -Name 'decision_status') -in
                            @('Issued', 'Idempotent') -and
                        -not [string]::IsNullOrWhiteSpace($authorityId) -and
                        $authorityId -ne '(none)' -and
                        [long]::TryParse(
                            (Get-FileTransferAuthorityLogField -Line $decision.Line -Name 'bootstrap_generation'),
                            [ref]$bootstrapGeneration) -and
                        $bootstrapGeneration -gt 0
                    if (-not $matchesTrigger) {
                        $decisionInvalid = $true
                        continue
                    }

                    $decisionIdentity = @(
                        $trigger.TransferId
                        $trigger.PendingGeneration
                        $trigger.TransportEpoch
                        $trigger.FrameFingerprint
                        $trigger.DataSessionGeneration
                        $sourceDataSessionGeneration
                        $authorityId
                        $bootstrapGeneration) -join '|'
                    if (-not $decisionIdentities.ContainsKey($decisionIdentity)) {
                        $decisionIdentities[$decisionIdentity] = $decision
                    }
                }
                $recovered = -not $decisionInvalid -and $decisionIdentities.Count -eq 1
            }
        }

        $first = $firstWait
        $distinctKey = "fallback-v6-bootstrap-direction|$($episode.Key)|$expectedDirection"
        $distinctRecord = [pscustomobject]@{
            Class = 'filetransfer_route_transition_tuna_v4_to_post_tuna_fallback_v6_proof_bootstrap_missing'
            Reason = 'combined_v6_bulk_and_control_proof_lost_bootstrap_direction'
            Variant = 'unbound_second_cycle_pending_proof_received_v6_bulk_before_control_ack'
            DistinctKey = $distinctKey.ToLowerInvariant()
            Writer = $episode.Writer
            WriterSequence = [long]$first.WriterSequence
            Line = $first.Line
            Recovered = $recovered
        }
        $distinctCandidates.Add($distinctRecord) | Out-Null
        if ($recovered) {
            $recoveredKeys.Add($distinctRecord.DistinctKey) | Out-Null
        }
        foreach ($candidate in $episode.Candidates) {
            $rawCandidates.Add([pscustomobject]@{
                    Class = $distinctRecord.Class
                    Reason = $distinctRecord.Reason
                    Variant = $distinctRecord.Variant
                    DistinctKey = $distinctRecord.DistinctKey
                    Writer = $episode.Writer
                    WriterSequence = [long]$candidate.WriterSequence
                    Line = $candidate.RawLine
                    Recovered = $recovered
                }) | Out-Null
        }
    }

    return [pscustomobject]@{
        RawCount = $rawCandidates.Count
        DistinctCount = $distinctCandidates.Count
        RecoveredCount = $recoveredKeys.Count
        UnresolvedCount = $distinctCandidates.Count - $recoveredKeys.Count
        RawCandidateRecords = @($rawCandidates.ToArray())
        CandidateRecords = @($distinctCandidates.ToArray())
        UnresolvedCandidateRecords = @($distinctCandidates | Where-Object { -not $_.Recovered })
    }
}

function Get-FileTransferFallbackV6BootstrapPeerFirstCorrelation {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Lines
    )

    $relevantEvents = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($eventName in @(
            'filetransfer_route_hint_authorized_publication_accepted',
            'tuna_fallback_started',
            'filetransfer_fallback_v6_proof_bootstrap_peer_frame_decided',
            'filetransfer_fallback_v6_proof_bootstrap_peer_first_wake_decided',
            'filetransfer_fallback_v6_proof_bootstrap_pending_write_deferred',
            'filetransfer_fallback_v6_proof_bootstrap_pending_write_recheck_decided',
            'filetransfer_fallback_v6_proof_bootstrap_observed',
            'filetransfer_fallback_v6_proof_bootstrap_decided',
            'filetransfer_fallback_v6_proof_bootstrap_execution_completed',
            'filetransfer_fallback_v6_proof_bootstrap_receipt_applied',
            'filetransfer_fallback_v6_proof_bootstrap_peer_boundary_bound',
            'filetransfer_route_transition_prepared',
            'filetransfer_route_transition_invalidated',
            'filetransfer_data_session_availability_changed',
            'file_transfer_outbound_terminal',
            'file_transfer_inbound_terminal',
            'transfer_terminal',
            'filetransfer_runtime_terminalized')) {
        $relevantEvents.Add($eventName) | Out-Null
    }

    function Get-PeerFirstField {
        param(
            [Parameter(Mandatory = $true)]$Record,
            [Parameter(Mandatory = $true)][string]$Name
        )

        return Get-FileTransferAuthorityLogField -Line $Record.Line -Name $Name
    }

    function Test-PeerFirstValuePresent {
        param([AllowNull()][string]$Value)

        return -not [string]::IsNullOrWhiteSpace($Value) -and
            $Value -notin @('(none)', 'none', '(missing)', '__missing__')
    }

    function Test-PeerFirstFieldEqual {
        param(
            [Parameter(Mandatory = $true)]$Left,
            [Parameter(Mandatory = $true)]$Right,
            [Parameter(Mandatory = $true)][string]$LeftName,
            [string]$RightName = ''
        )

        if ([string]::IsNullOrWhiteSpace($RightName)) {
            $RightName = $LeftName
        }
        $expected = Get-PeerFirstField -Record $Left -Name $LeftName
        $actual = Get-PeerFirstField -Record $Right -Name $RightName
        return (Test-PeerFirstValuePresent -Value $expected) -and
            (Test-PeerFirstValuePresent -Value $actual) -and
            [string]::Equals(
                $expected,
                $actual,
                [System.StringComparison]::OrdinalIgnoreCase)
    }

    function Get-PeerFirstTransferScopeKey {
        param([Parameter(Mandatory = $true)]$Record)

        $values = New-Object System.Collections.Generic.List[string]
        foreach ($fieldName in @('log_writer_id', 'session_id', 'transfer_id', 'direction')) {
            $value = Get-PeerFirstField -Record $Record -Name $fieldName
            if (-not (Test-PeerFirstValuePresent -Value $value)) {
                return ''
            }
            $values.Add($value.ToLowerInvariant()) | Out-Null
        }
        return [string]::Join('|', $values)
    }

    function Get-PeerFirstRouteScopeFingerprint {
        param([Parameter(Mandatory = $true)]$Record)

        $values = New-Object System.Collections.Generic.List[string]
        foreach ($fieldName in @(
                'live_route_epoch',
                'runtime_generation',
                'route_publication_generation',
                'route_commit_generation',
                'terminal_generation',
                'leg_generation',
                'fallback_transport_epoch',
                'fallback_bridge_recovery_generation')) {
            $value = Get-PeerFirstField -Record $Record -Name $fieldName
            if (-not (Test-PeerFirstValuePresent -Value $value)) {
                return ''
            }
            $values.Add($value.ToLowerInvariant()) | Out-Null
        }
        $checkpointRequestId = Get-PeerFirstField `
            -Record $Record `
            -Name 'fallback_checkpoint_request_id'
        if ([string]::IsNullOrWhiteSpace($checkpointRequestId)) {
            # The route-publication compatibility event predates this field;
            # a File-Tuna publication has no active fallback checkpoint.
            $checkpointRequestId = '(none)'
        }
        $values.Add($checkpointRequestId.ToLowerInvariant()) | Out-Null
        return [string]::Join('|', $values)
    }

    function Test-PeerFirstCommandSourceScope {
        param(
            [Parameter(Mandatory = $true)]$RouteScope,
            [Parameter(Mandatory = $true)]$Command,
            [switch]$AllowSourceOnly
        )

        if ((Get-PeerFirstField -Record $Command -Name 'source_route') -ne 'file_tuna_v4' -or
            (Get-PeerFirstField -Record $Command -Name 'source_protocol_version') -ne '4') {
            return $false
        }
        if (-not $AllowSourceOnly -and
            ((Get-PeerFirstField -Record $Command -Name 'target_route') -ne
                'post_tuna_fallback_v6' -or
             (Get-PeerFirstField -Record $Command -Name 'target_protocol_version') -ne '6' -or
             (Get-PeerFirstField -Record $Command -Name 'target_transport') -ne
                'regular_nkn')) {
            return $false
        }

        $fieldPairs = @(
            @('live_route_epoch', 'source_live_route_epoch'),
            @('runtime_generation', 'source_runtime_generation'),
            @('route_publication_generation', 'source_route_publication_generation'),
            @('route_commit_generation', 'source_route_commit_generation'),
            @('terminal_generation', 'source_terminal_generation'),
            @('leg_generation', 'source_leg_generation'),
            @('fallback_transport_epoch', 'source_transport_epoch'),
            @('fallback_bridge_recovery_generation', 'source_bridge_recovery_generation'))
        foreach ($pair in $fieldPairs) {
            if (-not (Test-PeerFirstFieldEqual `
                    -Left $RouteScope `
                    -Right $Command `
                    -LeftName $pair[0] `
                    -RightName $pair[1])) {
                return $false
            }
        }
        if ($AllowSourceOnly) {
            $routeCheckpointRequest = Get-PeerFirstField `
                -Record $RouteScope `
                -Name 'fallback_checkpoint_request_id'
            $commandCheckpointRequest = Get-PeerFirstField `
                -Record $Command `
                -Name 'source_checkpoint_request_id'
            if ([string]::IsNullOrWhiteSpace($routeCheckpointRequest)) {
                $routeCheckpointRequest = '(none)'
            }
            if ([string]::IsNullOrWhiteSpace($commandCheckpointRequest)) {
                $commandCheckpointRequest = '(none)'
            }
            if (-not [string]::Equals(
                    $routeCheckpointRequest,
                    $commandCheckpointRequest,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                return $false
            }
        }
        return $true
    }

    function Test-PeerFirstSourceScopeEqual {
        param(
            [Parameter(Mandatory = $true)]$Left,
            [Parameter(Mandatory = $true)]$Right
        )

        return Test-FileTransferAuthorityFieldsEqual `
            -LeftLine $Left.Line `
            -RightLine $Right.Line `
            -Fields @(
                'source_route',
                'source_protocol_version',
                'source_live_route_epoch',
                'source_runtime_generation',
                'source_route_publication_generation',
                'source_route_commit_generation',
                'source_terminal_generation',
                'source_leg_generation',
                'source_transport_epoch',
                'source_bridge_recovery_generation')
    }

    function Get-PeerFirstStageBindingFingerprint {
        param([Parameter(Mandatory = $true)]$Record)

        if ((Get-PeerFirstField -Record $Record -Name 'peer_binding_present') -ne '1') {
            return ''
        }
        $values = New-Object System.Collections.Generic.List[string]
        foreach ($fieldName in @(
                'peer_data_session_reference_generation',
                'peer_data_session_generation',
                'source_route',
                'source_protocol_version',
                'source_live_route_epoch',
                'source_runtime_generation',
                'source_route_publication_generation',
                'source_route_commit_generation',
                'source_terminal_generation',
                'source_leg_generation',
                'source_transport_epoch',
                'source_bridge_recovery_generation',
                'source_checkpoint_request_id',
                'source_checkpoint_generation',
                'source_durable_checkpoint_generation',
                'source_state_epoch',
                'source_contiguous_frontier',
                'source_durable_highest',
                'source_chunk_count')) {
            $value = Get-PeerFirstField -Record $Record -Name $fieldName
            if ([string]::IsNullOrWhiteSpace($value)) {
                return ''
            }
            $values.Add($value.ToLowerInvariant()) | Out-Null
        }
        return [string]::Join('|', $values)
    }

    function Test-PeerFirstStageWakeScope {
        param(
            [Parameter(Mandatory = $true)]$Stage,
            [Parameter(Mandatory = $true)]$Wake
        )

        return (
            (Get-PeerFirstField -Record $Stage -Name 'peer_data_session_reference_generation') -eq
                (Get-PeerFirstField -Record $Wake -Name 'trigger_data_session_generation') -and
            (Test-FileTransferAuthorityFieldsEqual `
                -LeftLine $Stage.Line `
                -RightLine $Wake.Line `
                -Fields @(
                    'source_route',
                    'source_protocol_version',
                    'source_live_route_epoch',
                    'source_runtime_generation',
                    'source_route_publication_generation',
                    'source_route_commit_generation',
                    'source_terminal_generation',
                    'source_leg_generation',
                    'source_transport_epoch',
                    'source_bridge_recovery_generation',
                    'source_checkpoint_request_id',
                    'source_checkpoint_generation')))
    }

    function Test-PeerFirstStageCommandScope {
        param(
            [Parameter(Mandatory = $true)]$Stage,
            [Parameter(Mandatory = $true)]$Command
        )

        return (
            (Get-PeerFirstField -Record $Stage -Name 'peer_data_session_generation') -eq
                (Get-PeerFirstField -Record $Command -Name 'source_data_session_generation') -and
            (Test-FileTransferAuthorityFieldsEqual `
                -LeftLine $Stage.Line `
                -RightLine $Command.Line `
                -Fields @(
                    'source_route',
                    'source_protocol_version',
                    'source_live_route_epoch',
                    'source_runtime_generation',
                    'source_route_publication_generation',
                    'source_route_commit_generation',
                    'source_terminal_generation',
                    'source_leg_generation',
                    'source_transport_epoch',
                    'source_bridge_recovery_generation',
                    'source_checkpoint_request_id',
                    'source_checkpoint_generation',
                    'source_durable_checkpoint_generation',
                    'source_state_epoch',
                    'source_contiguous_frontier',
                    'source_durable_highest',
                    'source_chunk_count')))
    }

    function Test-PeerFirstStageBoundaryScope {
        param(
            [Parameter(Mandatory = $true)]$Stage,
            [Parameter(Mandatory = $true)]$Boundary
        )

        return (
            (Get-PeerFirstField -Record $Stage -Name 'peer_data_session_generation') -eq
                (Get-PeerFirstField -Record $Boundary -Name 'source_data_session_generation') -and
            (Test-FileTransferAuthorityFieldsEqual `
                -LeftLine $Stage.Line `
                -RightLine $Boundary.Line `
                -Fields @(
                    'source_route',
                    'source_protocol_version',
                    'source_live_route_epoch',
                    'source_runtime_generation',
                    'source_route_publication_generation',
                    'source_route_commit_generation',
                    'source_terminal_generation',
                    'source_leg_generation',
                    'source_transport_epoch',
                    'source_bridge_recovery_generation',
                    'source_checkpoint_request_id',
                    'source_checkpoint_generation',
                    'source_durable_checkpoint_generation',
                    'source_state_epoch',
                    'source_contiguous_frontier',
                    'source_durable_highest',
                    'source_chunk_count')))
    }

    function Test-PeerFirstStagePreparedScope {
        param(
            [Parameter(Mandatory = $true)]$Stage,
            [Parameter(Mandatory = $true)]$Prepared
        )

        return Test-PeerFirstStageBoundaryScope `
            -Stage $Stage `
            -Boundary $Prepared
    }

    function Test-PeerFirstCommandIdentity {
        param(
            [Parameter(Mandatory = $true)]$Command,
            [Parameter(Mandatory = $true)]$Candidate,
            [switch]$RequireFrameKind
        )

        $fields = @(
                'log_writer_id',
                'session_id',
                'transfer_id',
                'direction',
                'authority_id',
                'bootstrap_generation',
                'fallback_epoch',
                'target_transport_epoch')
        if ($RequireFrameKind) {
            $fields += 'frame_kind'
        }
        return Test-FileTransferAuthorityFieldsEqual `
            -LeftLine $Command.Line `
            -RightLine $Candidate.Line `
            -Fields $fields
    }

    function Test-PeerFirstPendingWriteDiagnosticTuple {
        param(
            [Parameter(Mandatory = $true)]$Diagnostic,
            [Parameter(Mandatory = $true)]$Episode,
            [Parameter(Mandatory = $true)]$Wake,
            [Parameter(Mandatory = $true)]$Observation
        )

        return (
            (Get-PeerFirstField -Record $Diagnostic -Name 'direction') -eq 'inbound' -and
            (Get-PeerFirstField -Record $Diagnostic -Name 'observation_generation') -eq
                [string]$Wake.PendingGeneration -and
            (Get-PeerFirstField -Record $Diagnostic -Name 'fallback_epoch') -eq
                [string]$Episode.FallbackEpoch -and
            (Get-PeerFirstField -Record $Diagnostic -Name 'proof_kind') -eq
                $Wake.ProofKind -and
            (Get-PeerFirstField -Record $Diagnostic -Name 'evaluation_source') -eq
                (Get-PeerFirstField -Record $Observation -Name 'source') -and
            (Get-PeerFirstField -Record $Diagnostic -Name 'trigger_transport_epoch') -eq
                [string]$Episode.FrameTransportEpoch -and
            (Get-PeerFirstField -Record $Diagnostic -Name 'trigger_frame_fingerprint') -eq
                $Episode.FrameFingerprint -and
            (Get-PeerFirstField -Record $Diagnostic -Name 'trigger_data_session_generation') -eq
                [string]$Wake.DataSessionGeneration -and
            ($Wake.BootstrapGeneration -le 0 -or
             (Get-PeerFirstField -Record $Diagnostic -Name 'trigger_bootstrap_generation') -eq
                [string]$Wake.BootstrapGeneration) -and
            $Episode.HasExactStageBinding -and
            (Get-PeerFirstField -Record $Diagnostic -Name 'source_data_session_generation') -eq
                (Get-PeerFirstField `
                    -Record $Episode.FirstStage `
                    -Name 'peer_data_session_generation') -and
            (Get-PeerFirstField -Record $Diagnostic -Name 'source_checkpoint_request_id') -eq
                (Get-PeerFirstField -Record $Wake.Record -Name 'source_checkpoint_request_id') -and
            (Get-PeerFirstField -Record $Diagnostic -Name 'source_checkpoint_generation') -eq
                (Get-PeerFirstField -Record $Wake.Record -Name 'source_checkpoint_generation') -and
            (Get-PeerFirstField -Record $Diagnostic -Name 'authority_effect') -eq 'none' -and
            (Test-PeerFirstSourceScopeEqual -Left $Wake.Record -Right $Diagnostic))
    }

    function Get-PeerFirstPendingWriteCounts {
        param([Parameter(Mandatory = $true)]$Record)

        $sparse = -1L
        $contiguous = -1L
        if (-not [long]::TryParse(
                (Get-PeerFirstField -Record $Record -Name 'pending_sparse_write_count'),
                [ref]$sparse) -or
            -not [long]::TryParse(
                (Get-PeerFirstField -Record $Record -Name 'pending_contiguous_chunk_count'),
                [ref]$contiguous) -or
            $sparse -lt 0 -or
            $contiguous -lt 0) {
            return $null
        }

        return [pscustomobject]@{
            Sparse = $sparse
            Contiguous = $contiguous
        }
    }

    function Test-PeerFirstPendingWriteReplacementLink {
        param(
            [Parameter(Mandatory = $true)]$Predecessor,
            [Parameter(Mandatory = $true)]$Successor
        )

        $declaredPredecessorGeneration = 0L
        if ((Get-PeerFirstField -Record $Successor.Deferral -Name 'decision_status') -ne
                'Replaced' -or
            -not [long]::TryParse(
                (Get-PeerFirstField `
                    -Record $Successor.Deferral `
                    -Name 'predecessor_wake_generation'),
                [ref]$declaredPredecessorGeneration) -or
            $declaredPredecessorGeneration -ne $Predecessor.WakeGeneration -or
            $Successor.WakeGeneration -ne ($Predecessor.WakeGeneration + 1) -or
            $Successor.ObservationGeneration -le $Predecessor.ObservationGeneration -or
            [string]::Equals(
                $Successor.Episode.FrameFingerprint,
                $Predecessor.Episode.FrameFingerprint,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            [long]$Successor.Deferral.WriterSequence -le
                [long]$Predecessor.Wake.Record.WriterSequence) {
            return $false
        }

        return Test-FileTransferAuthorityFieldsEqual `
            -LeftLine $Predecessor.Deferral.Line `
            -RightLine $Successor.Deferral.Line `
            -Fields @(
                'log_writer_id',
                'session_id',
                'transfer_id',
                'direction',
                'fallback_epoch',
                'evaluation_source',
                'proof_kind',
                'trigger_transport_epoch',
                'trigger_data_session_generation',
                'source_route',
                'source_protocol_version',
                'source_live_route_epoch',
                'source_runtime_generation',
                'source_route_publication_generation',
                'source_route_commit_generation',
                'source_terminal_generation',
                'source_leg_generation',
                'source_transport_epoch',
                'source_bridge_recovery_generation',
                'source_data_session_generation',
                'source_checkpoint_request_id',
                'source_checkpoint_generation')
    }

    function Test-PeerFirstPendingWriteReplacementIntervalClean {
        param(
            [Parameter(Mandatory = $true)]$Predecessor,
            [Parameter(Mandatory = $true)]$Successor
        )

        $startSequence = [long]$Predecessor.Deferral.WriterSequence
        $endSequence = [long]$Successor.Deferral.WriterSequence
        foreach ($record in $Predecessor.ScopeRecords) {
            if ([long]$record.WriterSequence -le $startSequence -or
                [long]$record.WriterSequence -ge $endSequence) {
                continue
            }
            if ($record.EventName -in @(
                    'file_transfer_outbound_terminal',
                    'file_transfer_inbound_terminal',
                    'transfer_terminal',
                    'filetransfer_runtime_terminalized',
                    'filetransfer_route_transition_invalidated') -or
                ($record.EventName -eq
                    'filetransfer_fallback_v6_proof_bootstrap_pending_write_recheck_decided' -and
                 (Get-PeerFirstField -Record $record -Name 'wake_generation') -eq
                    [string]$Predecessor.WakeGeneration -and
                 (Test-PeerFirstPendingWriteDiagnosticTuple `
                    -Diagnostic $record `
                    -Episode $Predecessor.Episode `
                    -Wake $Predecessor.Wake `
                    -Observation $Predecessor.Observation)) -or
                ($record.EventName -eq
                    'filetransfer_data_session_availability_changed' -and
                 (Get-PeerFirstField -Record $record -Name 'available') -eq '0') -or
                ($record.EventName -eq
                    'filetransfer_route_hint_authorized_publication_accepted' -and
                 ((Get-PeerFirstField -Record $record -Name 'route') -ne
                    'file_tuna_v4' -or
                  (Get-PeerFirstRouteScopeFingerprint -Record $record) -ne
                    (Get-PeerFirstRouteScopeFingerprint `
                        -Record $Predecessor.Episode.RouteScope)))) {
                return $false
            }
        }
        return $true
    }

    $records = New-Object System.Collections.Generic.List[object]
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        $rawLine = [string]$Lines[$index]
        $eventOffset = $rawLine.IndexOf('event=', [System.StringComparison]::OrdinalIgnoreCase)
        if ($eventOffset -lt 0) {
            continue
        }
        $line = $rawLine.Substring($eventOffset)
        $eventName = Get-FileTransferAuthorityLogField -Line $line -Name 'event'
        if (-not $relevantEvents.Contains($eventName)) {
            continue
        }

        $writerSequence = 0L
        $writer = Get-FileTransferAuthorityLogField -Line $line -Name 'log_writer_id'
        if ([string]::IsNullOrWhiteSpace($writer) -or
            -not [long]::TryParse(
                (Get-FileTransferAuthorityLogField -Line $line -Name 'log_writer_sequence'),
                [ref]$writerSequence) -or
            $writerSequence -le 0) {
            continue
        }

        $records.Add([pscustomobject]@{
                EventName = $eventName
                Writer = $writer
                WriterSequence = $writerSequence
                InputIndex = [long]$index
                Line = $line
                RawLine = $rawLine
            }) | Out-Null
    }

    $orderedRecords = @($records | Sort-Object Writer, WriterSequence, InputIndex)
    $routeScopeByTransfer = @{}
    $fallbackEpochByWriterSession = @{}
    $recordsByTransferScope = @{}
    $episodes = @{}

    foreach ($record in $orderedRecords) {
        $session = Get-PeerFirstField -Record $record -Name 'session_id'
        $writerSessionKey = if (Test-PeerFirstValuePresent -Value $session) {
            "$($record.Writer)|$session".ToLowerInvariant()
        }
        else {
            ''
        }

        if ($record.EventName -eq 'tuna_fallback_started') {
            $fallbackEpoch = 0L
            if (-not [string]::IsNullOrWhiteSpace($writerSessionKey) -and
                [long]::TryParse(
                    (Get-PeerFirstField -Record $record -Name 'fallback_epoch'),
                    [ref]$fallbackEpoch) -and
                $fallbackEpoch -gt 1) {
                $fallbackEpochByWriterSession[$writerSessionKey] = [pscustomobject]@{
                    Epoch = $fallbackEpoch
                    Sequence = $record.WriterSequence
                }
            }
            continue
        }

        $transferScopeKey = Get-PeerFirstTransferScopeKey -Record $record
        if (-not [string]::IsNullOrWhiteSpace($transferScopeKey)) {
            if (-not $recordsByTransferScope.ContainsKey($transferScopeKey)) {
                $recordsByTransferScope[$transferScopeKey] =
                    New-Object System.Collections.Generic.List[object]
            }
            $recordsByTransferScope[$transferScopeKey].Add($record) | Out-Null
        }

        if ($record.EventName -eq 'filetransfer_route_hint_authorized_publication_accepted') {
            if ([string]::IsNullOrWhiteSpace($transferScopeKey)) {
                continue
            }
            $routeScopeFingerprint = Get-PeerFirstRouteScopeFingerprint -Record $record
            if ((Get-PeerFirstField -Record $record -Name 'route') -eq 'file_tuna_v4' -and
                (Get-PeerFirstField -Record $record -Name 'protocol_version') -eq '4' -and
                (Get-PeerFirstField -Record $record -Name 'target_route') -eq 'file_tuna_v4' -and
                (Get-PeerFirstField -Record $record -Name 'target_transport') -eq 'tuna' -and
                -not [string]::IsNullOrWhiteSpace($routeScopeFingerprint)) {
                $routeScopeByTransfer[$transferScopeKey] = [pscustomobject]@{
                    Record = $record
                    Fingerprint = $routeScopeFingerprint
                }
            }
            else {
                $routeScopeByTransfer.Remove($transferScopeKey)
            }
            continue
        }

        if ($record.EventName -ne 'filetransfer_fallback_v6_proof_bootstrap_peer_frame_decided' -or
            [string]::IsNullOrWhiteSpace($transferScopeKey) -or
            -not $routeScopeByTransfer.ContainsKey($transferScopeKey) -or
            [string]::IsNullOrWhiteSpace($writerSessionKey) -or
            -not $fallbackEpochByWriterSession.ContainsKey($writerSessionKey)) {
            continue
        }

        $status = Get-PeerFirstField -Record $record -Name 'decision_status'
        $reason = Get-PeerFirstField -Record $record -Name 'reason_code'
        if ($status -ne 'staged' -or
            $reason -notin @(
                'precommand_peer_frame_staged',
                'precommand_peer_frame_duplicate')) {
            continue
        }

        $direction = Get-PeerFirstField -Record $record -Name 'direction'
        $frameType = Get-PeerFirstField -Record $record -Name 'frame_type'
        $expectedFrameType = if ($direction -eq 'outbound') {
            'filetransfer.receiver_state.v6'
        }
        elseif ($direction -eq 'inbound') {
            'filetransfer.frontier_request.v6'
        }
        else {
            ''
        }
        $expectedProofKind = if ($direction -eq 'outbound') {
            'file_transfer_v6_state_frame_received'
        }
        elseif ($direction -eq 'inbound') {
            'file_transfer_v6_frontier_request_frame_received'
        }
        else {
            ''
        }
        $frameTransportEpoch = 0L
        $frameFingerprint = Get-PeerFirstField -Record $record -Name 'frame_fingerprint'
        if ([string]::IsNullOrWhiteSpace($expectedFrameType) -or
            $frameType -ne $expectedFrameType -or
            -not [long]::TryParse(
                (Get-PeerFirstField -Record $record -Name 'frame_transport_epoch'),
                [ref]$frameTransportEpoch) -or
            $frameTransportEpoch -le 0 -or
            -not (Test-PeerFirstValuePresent -Value $frameFingerprint) -or
            (Get-PeerFirstField -Record $record -Name 'authority_id') -notin @('', '(none)', 'none') -or
            (Get-PeerFirstField -Record $record -Name 'bootstrap_generation') -notin @('', '0') -or
            (Get-PeerFirstField -Record $record -Name 'fallback_epoch') -notin @('', '0') -or
            (Get-PeerFirstField -Record $record -Name 'target_transport_epoch') -notin @('', '0') -or
            (Get-PeerFirstField -Record $record -Name 'frame_kind') -notin @('', '(none)', 'none') -or
            (Get-PeerFirstField -Record $record -Name 'authority_effect') -ne 'evidence_only') {
            continue
        }

        $fallback = $fallbackEpochByWriterSession[$writerSessionKey]
        $routeScope = $routeScopeByTransfer[$transferScopeKey]
        if ($fallback.Sequence -le $routeScope.Record.WriterSequence -or
            $record.WriterSequence -le $fallback.Sequence) {
            continue
        }

        $bindingPresence = Get-PeerFirstField `
            -Record $record `
            -Name 'peer_binding_present'
        $bindingDeclared = -not [string]::IsNullOrWhiteSpace($bindingPresence)
        $stageBindingFingerprint =
            Get-PeerFirstStageBindingFingerprint -Record $record
        $hasExactStageBinding = $bindingPresence -eq '1' -and
            -not [string]::IsNullOrWhiteSpace($stageBindingFingerprint)
        $stageBindingMalformed = $bindingDeclared -and
            -not $hasExactStageBinding
        $episodeKey = @(
            'fallback-v6-bootstrap-peer-first'
            $transferScopeKey
            $(if ($hasExactStageBinding) {
                    $stageBindingFingerprint
                }
                else {
                    $(if ($bindingDeclared) {
                            "invalid:$($record.InputIndex)"
                        }
                        else {
                            "legacy:$($routeScope.Fingerprint)"
                        })
                })
            [string]$fallback.Epoch
            $frameType.ToLowerInvariant()
            [string]$frameTransportEpoch
            $frameFingerprint.ToLowerInvariant()) -join '|'
        if (-not $episodes.ContainsKey($episodeKey)) {
            $episodes[$episodeKey] = [pscustomobject]@{
                Key = $episodeKey
                TransferScopeKey = $transferScopeKey
                RouteScope = $routeScope.Record
                FallbackStartSequence = [long]$fallback.Sequence
                FallbackEpoch = [long]$fallback.Epoch
                FrameType = $frameType
                ExpectedProofKind = $expectedProofKind
                FrameTransportEpoch = $frameTransportEpoch
                FrameFingerprint = $frameFingerprint
                HasExactStageBinding = $hasExactStageBinding
                StageBindingMalformed = $stageBindingMalformed
                StageBindingFingerprint = $stageBindingFingerprint
                FirstStage = $record
                StageRecords = New-Object System.Collections.Generic.List[object]
            }
        }
        $episodes[$episodeKey].StageRecords.Add($record) | Out-Null
    }

    $distinctCandidates = New-Object System.Collections.Generic.List[object]
    $rawCandidates = New-Object System.Collections.Generic.List[object]
    $recoveredKeys = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $pendingWriteStates = New-Object System.Collections.Generic.List[object]

    foreach ($episode in @($episodes.Values | Sort-Object {
                $_.FirstStage.InputIndex
            })) {
        [object[]]$scopeRecords = if (
            $recordsByTransferScope.ContainsKey($episode.TransferScopeKey)) {
            @($recordsByTransferScope[$episode.TransferScopeKey].ToArray())
        }
        else {
            @()
        }
        $stageSequence = [long]$episode.FirstStage.WriterSequence
        [object[]]$episodeRecords = @($scopeRecords | Where-Object {
                [long]$_.WriterSequence -gt [long]$episode.FallbackStartSequence
            } | Sort-Object WriterSequence, InputIndex)
        [object[]]$afterStage = @($scopeRecords | Where-Object {
                [long]$_.WriterSequence -gt $stageSequence
            } | Sort-Object WriterSequence, InputIndex)

        $wakeIdentities = @{}
        $poisoned = [bool]$episode.StageBindingMalformed
        foreach ($wake in @($episodeRecords | Where-Object {
                    $_.EventName -eq
                        'filetransfer_fallback_v6_proof_bootstrap_peer_first_wake_decided'
                })) {
            $wakeStatus = Get-PeerFirstField -Record $wake -Name 'decision_status'
            $wakeResult = Get-PeerFirstField -Record $wake -Name 'decision_result'
            $pendingGeneration = 0L
            $fallbackEpoch = 0L
            $transportEpoch = 0L
            $bootstrapGeneration = 0L
            $dataSessionGeneration = 0L
            $proofKind = Get-PeerFirstField -Record $wake -Name 'proof_kind'
            $bootstrapGenerationText =
                Get-PeerFirstField -Record $wake -Name 'bootstrap_generation'
            if ((Get-PeerFirstField -Record $wake -Name 'authority_effect') -ne 'none' -or
                $proofKind -ne $episode.ExpectedProofKind -or
                -not [long]::TryParse(
                    (Get-PeerFirstField -Record $wake -Name 'pending_generation'),
                    [ref]$pendingGeneration) -or
                $pendingGeneration -le 0 -or
                -not [long]::TryParse(
                    (Get-PeerFirstField -Record $wake -Name 'fallback_epoch'),
                    [ref]$fallbackEpoch) -or
                $fallbackEpoch -ne $episode.FallbackEpoch -or
                -not [long]::TryParse(
                    (Get-PeerFirstField -Record $wake -Name 'transport_epoch'),
                    [ref]$transportEpoch) -or
                $transportEpoch -ne $episode.FrameTransportEpoch -or
                -not [long]::TryParse(
                    (Get-PeerFirstField -Record $wake -Name 'trigger_data_session_generation'),
                    [ref]$dataSessionGeneration) -or
                $dataSessionGeneration -le 0 -or
                (-not [string]::IsNullOrWhiteSpace($bootstrapGenerationText) -and
                 (-not [long]::TryParse(
                        $bootstrapGenerationText,
                        [ref]$bootstrapGeneration) -or
                  $bootstrapGeneration -le 0 -or
                  $bootstrapGeneration -ne $fallbackEpoch)) -or
                -not [string]::Equals(
                    (Get-PeerFirstField -Record $wake -Name 'frame_fingerprint'),
                    $episode.FrameFingerprint,
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            if ($wakeStatus -in @('Conflict', 'Stale', 'Unavailable', 'Failed', 'Invalid') -or
                $wakeResult -in @(
                    'peer_trigger_conflict',
                    'source_changed',
                    'evaluation_subscriber_unavailable',
                    'evaluation_callback_failed',
                    'evaluation_invalid')) {
                $poisoned = $true
                continue
            }
            if ($wakeStatus -notin @('Published', 'Coalesced') -or
                $wakeResult -notin @(
                    'local_bootstrap_evaluation_published',
                    'existing_bootstrap_evaluation_current') -or
                -not (Test-PeerFirstCommandSourceScope `
                    -RouteScope $episode.RouteScope `
                    -Command $wake `
                    -AllowSourceOnly) -or
                (Get-PeerFirstField -Record $wake -Name 'source_checkpoint_request_id') -ne
                    '(none)') {
                continue
            }
            if ($episode.HasExactStageBinding -and
                -not (Test-PeerFirstStageWakeScope `
                    -Stage $episode.FirstStage `
                    -Wake $wake)) {
                continue
            }
            $wakeIdentity = @(
                [string]$pendingGeneration
                [string]$fallbackEpoch
                [string]$bootstrapGeneration
                $proofKind.ToLowerInvariant()
                [string]$transportEpoch
                $episode.FrameFingerprint.ToLowerInvariant()
                [string]$dataSessionGeneration) -join '|'
            if (-not $wakeIdentities.ContainsKey($wakeIdentity)) {
                $wakeIdentities[$wakeIdentity] = [pscustomobject]@{
                    Record = $wake
                    PendingGeneration = $pendingGeneration
                    BootstrapGeneration = $bootstrapGeneration
                    DataSessionGeneration = $dataSessionGeneration
                    ProofKind = $proofKind
                }
            }
        }

        $recovered = $false
        $candidateRecord = $episode.FirstStage
        $candidateReason = 'peer_first_bootstrap_frame_staged_without_exact_coordinator_evaluation_wake'
        $candidateVariant = if ((Get-PeerFirstField `
                -Record $episode.FirstStage `
                -Name 'direction') -eq 'outbound') {
            'second_cycle_receiver_state_completed_transport_proof_before_outbound_bootstrap_observation'
        }
        else {
            'second_cycle_frontier_request_completed_transport_proof_before_inbound_bootstrap_observation'
        }
        [object[]]$rawEvidenceRecords = @($episode.StageRecords.ToArray())
        $pendingWriteDeferral = $null
        $pendingWriteDeferralGeneration = 0L
        $pendingWriteObservation = $null
        $pendingWriteWake = $null
        $pendingWriteRecheckSucceeded = $false
        if (-not $poisoned -and $wakeIdentities.Count -eq 1) {
            $wake = @($wakeIdentities.Values)[0]
            [object[]]$observations = @($episodeRecords | Where-Object {
                    $_.EventName -eq 'filetransfer_fallback_v6_proof_bootstrap_observed' -and
                    [long]$_.WriterSequence -lt [long]$wake.Record.WriterSequence -and
                    (Get-PeerFirstField -Record $_ -Name 'observation_generation') -eq
                        [string]$wake.PendingGeneration -and
                    (Get-PeerFirstField -Record $_ -Name 'fallback_epoch') -eq
                        [string]$episode.FallbackEpoch -and
                    (Get-PeerFirstField -Record $_ -Name 'proof_kind') -eq
                        $wake.ProofKind -and
                    (Get-PeerFirstField -Record $_ -Name 'transfer_id') -eq
                        (Get-PeerFirstField -Record $episode.FirstStage -Name 'transfer_id') -and
                    (Get-PeerFirstField -Record $_ -Name 'trigger_transport_epoch') -eq
                        [string]$episode.FrameTransportEpoch -and
                    (Get-PeerFirstField -Record $_ -Name 'trigger_frame_fingerprint') -eq
                        $episode.FrameFingerprint -and
                    (Get-PeerFirstField -Record $_ -Name 'trigger_data_session_generation') -eq
                        [string]$wake.DataSessionGeneration -and
                    (Get-PeerFirstField -Record $_ -Name 'authority_effect') -eq
                        'coordinator_evaluation_only'
                } | Select-Object -First 2)
            if ($observations.Count -eq 1) {
                $observation = $observations[0]
                if ($wake.BootstrapGeneration -gt 0) {
                    if ((Get-PeerFirstField `
                            -Record $observation `
                            -Name 'trigger_bootstrap_generation') -ne
                        [string]$wake.BootstrapGeneration -or
                        ((Get-PeerFirstField `
                                -Record $episode.FirstStage `
                                -Name 'direction') -eq 'inbound' -and
                         (Get-PeerFirstField `
                                -Record $episode.FirstStage `
                                -Name 'request_correlation_id') -ne
                            "fallback-v6-proof-bootstrap:$($wake.BootstrapGeneration)")) {
                        $poisoned = $true
                    }
                    else {
                        $candidateRecord = $wake.Record
                        $candidateReason =
                            'peer_first_bootstrap_evaluation_published_before_exact_coordinator_admission'
                        $candidateVariant =
                            'runtime_unlock_tuna_reactivation_exact_successor_generation_not_admitted'
                        $rawEvidenceRecords = @($wake.Record)
                    }
                }
                $pendingWriteRecheckSequence = 0L
                $pendingWriteRecheckRequired = $false
                if ((Get-PeerFirstField -Record $episode.FirstStage -Name 'direction') -eq
                    'inbound') {
                    $currentDeferral = $null
                    $currentDeferralGeneration = 0L
                    foreach ($diagnostic in @($episodeRecords | Where-Object {
                                $_.EventName -eq
                                    'filetransfer_fallback_v6_proof_bootstrap_pending_write_deferred' -and
                                [long]$_.WriterSequence -gt [long]$observation.WriterSequence -and
                                [long]$_.WriterSequence -lt [long]$wake.Record.WriterSequence -and
                                (Get-PeerFirstField -Record $_ -Name 'observation_generation') -eq
                                    [string]$wake.PendingGeneration -and
                                (Get-PeerFirstField -Record $_ -Name 'fallback_epoch') -eq
                                    [string]$episode.FallbackEpoch -and
                                (Get-PeerFirstField -Record $_ -Name 'proof_kind') -eq
                                    $wake.ProofKind -and
                                (Get-PeerFirstField -Record $_ -Name 'trigger_transport_epoch') -eq
                                    [string]$episode.FrameTransportEpoch -and
                                (Get-PeerFirstField -Record $_ -Name 'trigger_frame_fingerprint') -eq
                                    $episode.FrameFingerprint -and
                                (Get-PeerFirstField -Record $_ -Name 'trigger_data_session_generation') -eq
                                    [string]$wake.DataSessionGeneration
                            } | Sort-Object WriterSequence, InputIndex)) {
                        $diagnosticGeneration = 0L
                        $declaredPredecessorGeneration = -1L
                        $counts = Get-PeerFirstPendingWriteCounts -Record $diagnostic
                        $diagnosticStatus =
                            Get-PeerFirstField -Record $diagnostic -Name 'decision_status'
                        $diagnosticValid =
                            (Test-PeerFirstPendingWriteDiagnosticTuple `
                                -Diagnostic $diagnostic `
                                -Episode $episode `
                                -Wake $wake `
                                -Observation $observation) -and
                            [long]::TryParse(
                                (Get-PeerFirstField -Record $diagnostic -Name 'wake_generation'),
                                [ref]$diagnosticGeneration) -and
                            $diagnosticGeneration -gt 0 -and
                            [long]::TryParse(
                                (Get-PeerFirstField `
                                    -Record $diagnostic `
                                    -Name 'predecessor_wake_generation'),
                                [ref]$declaredPredecessorGeneration) -and
                            (($diagnosticStatus -eq 'Replaced' -and
                              $declaredPredecessorGeneration -gt 0 -and
                              $declaredPredecessorGeneration -lt $diagnosticGeneration) -or
                             ($diagnosticStatus -ne 'Replaced' -and
                              $declaredPredecessorGeneration -eq 0)) -and
                            $null -ne $counts -and
                            ([long]$counts.Sparse + [long]$counts.Contiguous) -gt 0 -and
                            (Get-PeerFirstField -Record $diagnostic -Name 'decision_result') -eq
                                'inbound_pending_write_current' -and
                            $diagnosticStatus -in
                                @('Held', 'Coalesced', 'Replaced')
                        if (-not $diagnosticValid) {
                            $poisoned = $true
                            continue
                        }

                        if ($diagnosticStatus -eq 'Coalesced') {
                            if ($null -eq $currentDeferral -or
                                $diagnosticGeneration -ne $currentDeferralGeneration) {
                                $poisoned = $true
                            }
                            continue
                        }

                        $currentDeferral = $diagnostic
                        $currentDeferralGeneration = $diagnosticGeneration
                    }

                    if (-not $poisoned -and $null -ne $currentDeferral) {
                        $pendingWriteRecheckRequired = $true
                        $candidateRecord = $wake.Record
                        $candidateReason =
                            'peer_first_bootstrap_evaluation_published_but_inbound_pending_write_recheck_missing'
                        $candidateVariant =
                            'second_cycle_frontier_request_arrived_during_sparse_write_and_one_shot_evaluation_was_dropped'
                        $rawEvidenceRecords = @($wake.Record)

                        [object[]]$matchingRechecks = @($episodeRecords | Where-Object {
                                $_.EventName -eq
                                    'filetransfer_fallback_v6_proof_bootstrap_pending_write_recheck_decided' -and
                                [long]$_.WriterSequence -gt
                                    [long]$currentDeferral.WriterSequence -and
                                (Get-PeerFirstField -Record $_ -Name 'wake_generation') -eq
                                    [string]$currentDeferralGeneration -and
                                (Test-PeerFirstPendingWriteDiagnosticTuple `
                                    -Diagnostic $_ `
                                    -Episode $episode `
                                    -Wake $wake `
                                    -Observation $observation)
                            } | Sort-Object WriterSequence, InputIndex)
                        [object[]]$successfulRechecks = @($matchingRechecks | Where-Object {
                                $counts = Get-PeerFirstPendingWriteCounts -Record $_
                                $null -ne $counts -and
                                [long]$counts.Sparse -eq 0 -and
                                [long]$counts.Contiguous -eq 0 -and
                                (Get-PeerFirstField -Record $_ -Name 'decision_status') -eq
                                    'Rechecked' -and
                                (Get-PeerFirstField -Record $_ -Name 'decision_result') -eq
                                    'pending_writes_drained'
                            } | Select-Object -First 2)
                        if ($matchingRechecks.Count -eq 1 -and
                            $successfulRechecks.Count -eq 1) {
                            $pendingWriteRecheckSequence =
                                [long]$successfulRechecks[0].WriterSequence
                        }
                        $pendingWriteDeferral = $currentDeferral
                        $pendingWriteDeferralGeneration =
                            $currentDeferralGeneration
                        $pendingWriteObservation = $observation
                        $pendingWriteWake = $wake
                        $pendingWriteRecheckSucceeded =
                            $pendingWriteRecheckSequence -gt 0
                    }
                }

                [object[]]$commands = @($afterStage | Where-Object {
                        -not $poisoned -and
                        $_.EventName -eq 'filetransfer_fallback_v6_proof_bootstrap_decided' -and
                        [long]$_.WriterSequence -gt [long]$observation.WriterSequence -and
                        (-not $pendingWriteRecheckRequired -or
                         ($pendingWriteRecheckSequence -gt 0 -and
                          [long]$_.WriterSequence -gt $pendingWriteRecheckSequence)) -and
                        (Get-PeerFirstField -Record $_ -Name 'decision_status') -in
                            @('Issued', 'Idempotent') -and
                        (Get-PeerFirstField -Record $_ -Name 'observation_generation') -eq
                            [string]$wake.PendingGeneration -and
                        (Get-PeerFirstField -Record $_ -Name 'fallback_epoch') -eq
                            [string]$episode.FallbackEpoch -and
                        (Get-PeerFirstField -Record $_ -Name 'trigger_transfer_id') -eq
                            (Get-PeerFirstField -Record $episode.FirstStage -Name 'transfer_id') -and
                        (Get-PeerFirstField -Record $_ -Name 'trigger_transport_epoch') -eq
                            [string]$episode.FrameTransportEpoch -and
                        (Get-PeerFirstField -Record $_ -Name 'trigger_frame_fingerprint') -eq
                            $episode.FrameFingerprint -and
                        (Get-PeerFirstField -Record $_ -Name 'trigger_data_session_generation') -eq
                            [string]$wake.DataSessionGeneration -and
                        ($wake.BootstrapGeneration -le 0 -or
                         (Get-PeerFirstField -Record $_ -Name 'trigger_bootstrap_generation') -eq
                            [string]$wake.BootstrapGeneration) -and
                        (Test-PeerFirstCommandSourceScope `
                            -RouteScope $episode.RouteScope `
                            -Command $_)
                    } | Select-Object -First 2)
                if ($commands.Count -eq 1) {
                    $command = $commands[0]
                    $expectedFrameKind = if (
                        (Get-PeerFirstField -Record $command -Name 'direction') -eq 'outbound') {
                        'FrontierRequest'
                    }
                    else {
                        'ReceiverState'
                    }
                    $authority = Get-PeerFirstField -Record $command -Name 'authority_id'
                    $bootstrapGeneration = 0L
                    $commandValid =
                        (Test-PeerFirstValuePresent -Value $authority) -and
                        [long]::TryParse(
                            (Get-PeerFirstField -Record $command -Name 'bootstrap_generation'),
                            [ref]$bootstrapGeneration) -and
                        $bootstrapGeneration -gt 0 -and
                        (Get-PeerFirstField -Record $command -Name 'target_transport_epoch') -eq
                            [string]$episode.FrameTransportEpoch -and
                        (Get-PeerFirstField -Record $command -Name 'frame_kind') -eq
                            $expectedFrameKind -and
                        (Get-PeerFirstField -Record $command -Name 'source_data_session_generation') -notin
                            @('', '0') -and
                        (Test-PeerFirstSourceScopeEqual `
                            -Left $wake.Record `
                            -Right $command)
                    if ($commandValid -and $episode.HasExactStageBinding) {
                        $commandValid = Test-PeerFirstStageCommandScope `
                            -Stage $episode.FirstStage `
                            -Command $command
                    }
                    if ($commandValid -and
                        (Get-PeerFirstField -Record $command -Name 'direction') -eq 'inbound') {
                        $commandValid =
                            (Get-PeerFirstField `
                                -Record $episode.FirstStage `
                                -Name 'request_correlation_id') -eq
                            "fallback-v6-proof-bootstrap:$bootstrapGeneration"
                    }
                    if ($commandValid) {
                        [object[]]$acceptedFrames = @($afterStage | Where-Object {
                                $_.EventName -eq
                                    'filetransfer_fallback_v6_proof_bootstrap_peer_frame_decided' -and
                                [long]$_.WriterSequence -gt [long]$observation.WriterSequence -and
                                (Test-PeerFirstCommandIdentity `
                                    -Command $command `
                                    -Candidate $_ `
                                    -RequireFrameKind) -and
                                (Get-PeerFirstField -Record $_ -Name 'frame_type') -eq
                                    $episode.FrameType -and
                                (Get-PeerFirstField -Record $_ -Name 'frame_transport_epoch') -eq
                                    [string]$episode.FrameTransportEpoch -and
                                (Get-PeerFirstField -Record $_ -Name 'frame_fingerprint') -eq
                                    $episode.FrameFingerprint -and
                                (Get-PeerFirstField -Record $_ -Name 'decision_status') -eq
                                    'accepted' -and
                                (Get-PeerFirstField -Record $_ -Name 'reason_code') -eq
                                    'precommand_peer_frame_admitted' -and
                                (Get-PeerFirstField -Record $_ -Name 'authority_effect') -eq
                                    'peer_frame_retention_only' -and
                                (-not $episode.HasExactStageBinding -or
                                 (Get-PeerFirstStageBindingFingerprint -Record $_) -eq
                                    $episode.StageBindingFingerprint)
                            } | Select-Object -First 2)
                        [object[]]$executions = @($afterStage | Where-Object {
                                $acceptedFrames.Count -eq 1 -and
                                $_.EventName -eq
                                    'filetransfer_fallback_v6_proof_bootstrap_execution_completed' -and
                                [long]$_.WriterSequence -gt [long]$command.WriterSequence -and
                                [long]$_.WriterSequence -gt
                                    [long]$acceptedFrames[0].WriterSequence -and
                                (Test-PeerFirstCommandIdentity `
                                    -Command $command `
                                    -Candidate $_ `
                                    -RequireFrameKind) -and
                                (Get-PeerFirstField -Record $_ -Name 'receipt_status') -eq 'Delivered' -and
                                (Test-PeerFirstValuePresent -Value (
                                    Get-PeerFirstField -Record $_ -Name 'frame_fingerprint'))
                            } | Select-Object -First 2)
                        [object[]]$receipts = @($afterStage | Where-Object {
                                $executions.Count -eq 1 -and
                                $_.EventName -eq
                                    'filetransfer_fallback_v6_proof_bootstrap_receipt_applied' -and
                                [long]$_.WriterSequence -gt [long]$executions[0].WriterSequence -and
                                (Test-PeerFirstCommandIdentity `
                                    -Command $command `
                                    -Candidate $_ `
                                    -RequireFrameKind) -and
                                (Get-PeerFirstField -Record $_ -Name 'receipt_status') -eq 'Delivered' -and
                                (Get-PeerFirstField -Record $_ -Name 'application_status') -in
                                    @('Applied', 'Idempotent') -and
                                (Get-PeerFirstField -Record $_ -Name 'frame_fingerprint') -eq
                                    (Get-PeerFirstField -Record $executions[0] -Name 'frame_fingerprint')
                            } | Select-Object -First 2)
                        [object[]]$boundaries = @($afterStage | Where-Object {
                                $receipts.Count -eq 1 -and
                                $_.EventName -eq
                                    'filetransfer_fallback_v6_proof_bootstrap_peer_boundary_bound' -and
                                [long]$_.WriterSequence -gt [long]$executions[0].WriterSequence -and
                                (Test-PeerFirstCommandIdentity `
                                    -Command $command `
                                    -Candidate $_) -and
                                (Test-PeerFirstSourceScopeEqual `
                                    -Left $command `
                                    -Right $_) -and
                                (Get-PeerFirstField -Record $_ -Name 'pairing_status') -in
                                    @('Bound', 'Idempotent') -and
                                (Get-PeerFirstField -Record $_ -Name 'peer_frame_type') -eq
                                    $episode.FrameType -and
                                (Get-PeerFirstField -Record $_ -Name 'peer_frame_fingerprint') -eq
                                    $episode.FrameFingerprint -and
                                (Get-PeerFirstField -Record $_ -Name 'proof_kind') -eq
                                    $wake.ProofKind -and
                                (Get-PeerFirstField -Record $_ -Name 'proof_generation') -notin
                                    @('', '0') -and
                                (-not $episode.HasExactStageBinding -or
                                 (Test-PeerFirstStageBoundaryScope `
                                    -Stage $episode.FirstStage `
                                    -Boundary $_))
                            } | Select-Object -First 2)
                        [object[]]$prepared = @($afterStage | Where-Object {
                                $boundaries.Count -eq 1 -and
                                $_.EventName -eq 'filetransfer_route_transition_prepared' -and
                                [long]$_.WriterSequence -gt [long]$executions[0].WriterSequence -and
                                (Get-PeerFirstField -Record $_ -Name 'source_route') -eq
                                    'file_tuna_v4' -and
                                (Get-PeerFirstField -Record $_ -Name 'source_protocol_version') -eq '4' -and
                                (Get-PeerFirstField -Record $_ -Name 'target_route') -eq
                                    'post_tuna_fallback_v6' -and
                                (Get-PeerFirstField -Record $_ -Name 'target_protocol_version') -eq '6' -and
                                (Get-PeerFirstField -Record $_ -Name 'target_transport') -eq
                                    'regular_nkn' -and
                                (Get-PeerFirstField -Record $_ -Name 'retained_frame_fingerprint') -eq
                                    $episode.FrameFingerprint -and
                                (Get-PeerFirstField -Record $_ -Name 'peer_proof_generation') -notin
                                    @('', '0') -and
                                (Test-PeerFirstValuePresent -Value (
                                    Get-PeerFirstField -Record $_ -Name 'authority_id')) -and
                                (Get-PeerFirstField -Record $_ -Name 'transition_generation') -notin
                                    @('', '0') -and
                                (Test-PeerFirstCommandSourceScope `
                                    -RouteScope $episode.RouteScope `
                                    -Command $_) -and
                                 (Test-PeerFirstSourceScopeEqual `
                                     -Left $command `
                                     -Right $_) -and
                                 (Get-PeerFirstField -Record $_ -Name 'source_data_session_generation') -eq
                                     (Get-PeerFirstField -Record $command -Name 'source_data_session_generation') -and
                                 (-not $episode.HasExactStageBinding -or
                                  (Test-PeerFirstStagePreparedScope `
                                      -Stage $episode.FirstStage `
                                      -Prepared $_))
                            } | Select-Object -First 2)

                        if ($prepared.Count -eq 1) {
                            $preparedSequence = [long]$prepared[0].WriterSequence
                            $preparedAuthority = Get-PeerFirstField `
                                -Record $prepared[0] `
                                -Name 'authority_id'
                            $preparedGeneration = Get-PeerFirstField `
                                -Record $prepared[0] `
                                -Name 'transition_generation'
                            $poison = @($afterStage | Where-Object {
                                    [long]$_.WriterSequence -lt $preparedSequence -and
                                    (
                                        $_.EventName -in @(
                                            'file_transfer_outbound_terminal',
                                            'file_transfer_inbound_terminal',
                                            'transfer_terminal',
                                            'filetransfer_runtime_terminalized') -or
                                        ($_.EventName -eq
                                            'filetransfer_route_transition_invalidated' -and
                                         (Get-PeerFirstField -Record $_ -Name 'authority_id') -eq
                                            $preparedAuthority -and
                                         (Get-PeerFirstField -Record $_ -Name 'transition_generation') -eq
                                            $preparedGeneration) -or
                                        ($_.EventName -eq
                                            'filetransfer_data_session_availability_changed' -and
                                         (Get-PeerFirstField -Record $_ -Name 'available') -eq '0') -or
                                        ($_.EventName -eq
                                            'filetransfer_route_hint_authorized_publication_accepted' -and
                                         ((Get-PeerFirstField -Record $_ -Name 'route') -ne
                                            'file_tuna_v4' -or
                                          (Get-PeerFirstRouteScopeFingerprint -Record $_) -ne
                                            (Get-PeerFirstRouteScopeFingerprint `
                                                -Record $episode.RouteScope)))
                                    )
                                } | Select-Object -First 1)
                            $recovered = $poison.Count -eq 0
                        }
                    }
                }
            }
        }

        $distinctRecord = [pscustomobject]@{
            Class = 'filetransfer_route_transition_tuna_v4_to_post_tuna_fallback_v6_proof_bootstrap_missing'
            Reason = $candidateReason
            Variant = $candidateVariant
            DistinctKey = $episode.Key.ToLowerInvariant()
            Writer = $candidateRecord.Writer
            WriterSequence = [long]$candidateRecord.WriterSequence
            InputIndex = [long]$candidateRecord.InputIndex
            Line = $candidateRecord.Line
            Recovered = $recovered
        }
        $distinctCandidates.Add($distinctRecord) | Out-Null
        if ($recovered) {
            $recoveredKeys.Add($distinctRecord.DistinctKey) | Out-Null
        }
        foreach ($stageRecord in $rawEvidenceRecords) {
            $rawRecord = [pscustomobject]@{
                    Class = $distinctRecord.Class
                    Reason = $distinctRecord.Reason
                    Variant = $distinctRecord.Variant
                    DistinctKey = $distinctRecord.DistinctKey
                    Writer = $stageRecord.Writer
                    WriterSequence = [long]$stageRecord.WriterSequence
                    InputIndex = [long]$stageRecord.InputIndex
                    Line = $stageRecord.RawLine
                    Recovered = $recovered
                }
            $rawCandidates.Add($rawRecord) | Out-Null
        }
        if (-not $poisoned -and
            $null -ne $pendingWriteDeferral -and
            $null -ne $pendingWriteObservation -and
            $null -ne $pendingWriteWake) {
            $observationGeneration = 0L
            if ([long]::TryParse(
                    (Get-PeerFirstField `
                        -Record $pendingWriteObservation `
                        -Name 'observation_generation'),
                    [ref]$observationGeneration) -and
                $observationGeneration -gt 0) {
                $pendingWriteStates.Add([pscustomobject]@{
                        Episode = $episode
                        ScopeRecords = $scopeRecords
                        Observation = $pendingWriteObservation
                        ObservationGeneration = $observationGeneration
                        Wake = $pendingWriteWake
                        WakeGeneration = $pendingWriteDeferralGeneration
                        Deferral = $pendingWriteDeferral
                        RecheckSucceeded = $pendingWriteRecheckSucceeded
                        DistinctRecord = $distinctRecord
                    }) | Out-Null
            }
        }
    }

    foreach ($predecessor in $pendingWriteStates) {
        if ($predecessor.RecheckSucceeded -or
            $predecessor.DistinctRecord.Recovered) {
            continue
        }
        [object[]]$successors = @($pendingWriteStates | Where-Object {
                Test-PeerFirstPendingWriteReplacementLink `
                    -Predecessor $predecessor `
                    -Successor $_
            } | Select-Object -First 2)
        if ($successors.Count -ne 1 -or
            -not (Test-PeerFirstPendingWriteReplacementIntervalClean `
                -Predecessor $predecessor `
                -Successor $successors[0])) {
            continue
        }

        $predecessor.DistinctRecord.Recovered = $true
        $recoveredKeys.Add($predecessor.DistinctRecord.DistinctKey) | Out-Null
        foreach ($rawRecord in $rawCandidates) {
            if ($rawRecord.DistinctKey -eq
                $predecessor.DistinctRecord.DistinctKey) {
                $rawRecord.Recovered = $true
            }
        }
    }

    return [pscustomobject]@{
        RawCount = $rawCandidates.Count
        DistinctCount = $distinctCandidates.Count
        RecoveredCount = $recoveredKeys.Count
        UnresolvedCount = $distinctCandidates.Count - $recoveredKeys.Count
        RawCandidateRecords = @($rawCandidates.ToArray())
        CandidateRecords = @($distinctCandidates.ToArray())
        UnresolvedCandidateRecords = @($distinctCandidates | Where-Object { -not $_.Recovered })
    }
}

function Add-FileTransferFallbackV6BootstrapDirectionToSnapshotProof {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Events,
        [Parameter(Mandatory = $true)]$SnapshotProof
    )

    [string[]]$lines = @($Events | ForEach-Object { [string]$_.RawLine })
    $directionCorrelation =
        Get-FileTransferFallbackV6BootstrapDirectionCorrelation -Lines $lines
    $peerFirstCorrelation =
        Get-FileTransferFallbackV6BootstrapPeerFirstCorrelation -Lines $lines
    $eventsByWriterSequence = @{}
    # Keep the raw-field accessor's first-match and trimming rules while
    # reusing these two fixed patterns for this projection only.
    $writerPattern = [regex]::new(
        '(?:^|;\s*)log_writer_id=([^;]*)',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $writerSequencePattern = [regex]::new(
        '(?:^|;\s*)log_writer_sequence=([^;]*)',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    foreach ($event in $Events) {
        $rawLine = [string]$event.RawLine
        $writerMatch = $writerPattern.Match($rawLine)
        $writerSequenceMatch = $writerSequencePattern.Match($rawLine)
        if ($writerMatch.Success -and $writerSequenceMatch.Success) {
            $writer = $writerMatch.Groups[1].Value.Trim()
            $writerSequence = $writerSequenceMatch.Groups[1].Value.Trim()
            if (-not [string]::IsNullOrWhiteSpace($writer) -and
                -not [string]::IsNullOrWhiteSpace($writerSequence)) {
                $eventsByWriterSequence["$writer|$writerSequence".ToLowerInvariant()] = $event
            }
        }
    }

    [object[]]$physicalRecords = @(
        foreach ($candidate in @(
                $directionCorrelation.CandidateRecords +
                $peerFirstCorrelation.CandidateRecords)) {
            $key = "$($candidate.Writer)|$($candidate.WriterSequence)".ToLowerInvariant()
            if (-not $eventsByWriterSequence.ContainsKey($key)) {
                continue
            }
            [pscustomobject]@{
                Event = $eventsByWriterSequence[$key]
                Class = [string]$candidate.Class
                Reason = [string]$candidate.Reason
                Variant = [string]$candidate.Variant
                Recovered = [bool]$candidate.Recovered
            }
        })
    [object[]]$physicalRecovered = @($physicalRecords | Where-Object { $_.Recovered })
    [object[]]$physicalUnresolved = @($physicalRecords | Where-Object { -not $_.Recovered })
    [object[]]$candidateRecords = @($SnapshotProof.CandidateRecords) + $physicalRecords
    [object[]]$recoveredRecords = @($SnapshotProof.RecoveredCandidateRecords) + $physicalRecovered
    [object[]]$unresolvedRecords = @($SnapshotProof.UnresolvedCandidateRecords) + $physicalUnresolved

    return [pscustomobject]@{
        RawCount = [int]$SnapshotProof.RawCount +
            [int]$directionCorrelation.RawCount + [int]$peerFirstCorrelation.RawCount
        DistinctCount = [int]$SnapshotProof.DistinctCount +
            [int]$directionCorrelation.DistinctCount + [int]$peerFirstCorrelation.DistinctCount
        RecoveredCount = [int]$SnapshotProof.RecoveredCount +
            [int]$directionCorrelation.RecoveredCount + [int]$peerFirstCorrelation.RecoveredCount
        UnresolvedCount = [int]$SnapshotProof.UnresolvedCount +
            [int]$directionCorrelation.UnresolvedCount + [int]$peerFirstCorrelation.UnresolvedCount
        Verdict = if ($unresolvedRecords.Count -gt 0) { 'fail' } else { 'pass' }
        CandidateRecords = $candidateRecords
        RecoveredCandidateRecords = $recoveredRecords
        UnresolvedCandidateRecords = $unresolvedRecords
        CandidateEvents = @($candidateRecords | ForEach-Object { $_.Event })
        RecoveredCandidateEvents = @($recoveredRecords | ForEach-Object { $_.Event })
        UnresolvedCandidateEvents = @($unresolvedRecords | ForEach-Object { $_.Event })
        AuthoritativeRecords = @($SnapshotProof.AuthoritativeRecords)
    }
}
