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

function Add-FileTransferFallbackV6BootstrapDirectionToSnapshotProof {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Events,
        [Parameter(Mandatory = $true)]$SnapshotProof
    )

    $correlation = Get-FileTransferFallbackV6BootstrapDirectionCorrelation -Lines @(
        $Events | ForEach-Object { [string]$_.RawLine })
    $eventsByWriterSequence = @{}
    foreach ($event in $Events) {
        $writer = Get-FileTransferAuthorityLogField -Line ([string]$event.RawLine) -Name 'log_writer_id'
        $writerSequence = Get-FileTransferAuthorityLogField -Line ([string]$event.RawLine) -Name 'log_writer_sequence'
        if (-not [string]::IsNullOrWhiteSpace($writer) -and
            -not [string]::IsNullOrWhiteSpace($writerSequence)) {
            $eventsByWriterSequence["$writer|$writerSequence".ToLowerInvariant()] = $event
        }
    }

    [object[]]$physicalRecords = @(
        foreach ($candidate in $correlation.CandidateRecords) {
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
        RawCount = [int]$SnapshotProof.RawCount + [int]$correlation.RawCount
        DistinctCount = [int]$SnapshotProof.DistinctCount + [int]$correlation.DistinctCount
        RecoveredCount = [int]$SnapshotProof.RecoveredCount + [int]$correlation.RecoveredCount
        UnresolvedCount = [int]$SnapshotProof.UnresolvedCount + [int]$correlation.UnresolvedCount
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
