Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'FileTransferPublicationEvidence.ps1')
. (Join-Path $PSScriptRoot 'FileTransferSupersessionAccounting.ps1')
. (Join-Path $PSScriptRoot 'FileTransferRecentProgressSupersession.ps1')

# Shared by retained indexing and full-log GUI closure checks.
function Get-FileTransferFallbackFrontierGraceCorrelationEventNames {
    return @(
        'filetransfer_v6_frontier_request_deferred',
        'filetransfer_v6_frontier_stall_grace_recheck_decided',
        'filetransfer_v6_frontier_recent_progress_grace_deferred',
        'filetransfer_v6_frontier_recent_progress_grace_recheck_decided',
        'filetransfer_v6_receiver_state_deferred',
        'filetransfer_fallback_frontier_repair_authority_granted',
        'filetransfer_fallback_frontier_repair_contract_settled',
        'filetransfer_fallback_frontier_repair_progress_observed',
        'filetransfer_v6_fallback_frontier_repair_progress_state_forced',
        'filetransfer_v6_receiver_state_sent',
        'filetransfer_v6_sparse_write_committed',
        'filetransfer_fallback_frontier_repair_dispatched',
        'filetransfer_v6_frontier_request_sent',
        'filetransfer_control_plane_delivery_observed',
        'filetransfer_fallback_tail_terminal_proof_consumed',
        'integrity_verify_passed',
        'temp_finalize_succeeded',
        'file_transfer_inbound_terminal',
        'filetransfer_live_route_epoch_terminal',
        'filetransfer_terminalized_by_session_end',
        'filetransfer_route_selected',
        'filetransfer_live_route_epoch_recovered',
        'filetransfer_route_transition_committed',
        'filetransfer_route_transition_disposition_applied',
        'filetransfer_route_hint_authorized_publication_accepted',
        'filetransfer_data_session_recreated',
        'filetransfer_data_session_removed')
}

function New-FileTransferStallGracePublishedSourceIndex {
    param([object[]]$Events, $SnapshotEvidence = $null)
    $transfers = New-FileTransferStallGraceCurrentSourceIndex -Events $Events
    $timelines = @{}
    foreach ($key in $transfers.Keys) {
        if ($key -cin @('__conflicts', '__CheckpointRepairContinuitySnapshotEvidence',
                '__CheckpointRepairContinuityDiagnostics', '__StallGraceDiagnosticRouteIndex')) { continue }
        $timelines[$key] = @($transfers[$key] | Where-Object {
            [string]$_.Fields['event'] -in @('filetransfer_route_hint_authorized_publication_accepted',
                'filetransfer_route_selected', 'filetransfer_live_route_epoch_recovered',
                'filetransfer_data_session_recreated', 'filetransfer_data_session_removed',
                'file_transfer_inbound_terminal', 'filetransfer_live_route_epoch_terminal',
                'filetransfer_terminalized_by_session_end') -or
            [string]$_.Fields['transfer_paused'] -ceq '1'
        })
    }
    return @{ Timelines = $timelines; Conflicts = $transfers['__conflicts'];
        PublicationEvidence = (New-FileTransferPublicationEvidenceIndex $SnapshotEvidence) }
}

# Current-source admission is not a synthetic predecessor repair. Authority scope
# comes from the exact publication; execution/data-session scope stays on the hold.
function Get-FileTransferStallGracePublishedSourceAdmission {
    param([hashtable]$Index, $Candidate)
    function F($Event, [string]$Name) { return [string]$Event.Fields[$Name] }
    function N($Event, [string]$Name) {
        $number = 0L
        if (-not [long]::TryParse((F $Event $Name), [ref]$number)) { return -1L }
        return $number
    }
    $complete = @('log_writer_id', 'session_id', 'transfer_id', 'direction', 'route', 'protocol_version',
        'live_route_epoch', 'runtime_generation', 'terminal_generation', 'leg_generation',
        'transport_epoch', 'bridge_recovery_generation', 'bridge_decision_generation', 'checkpoint_request_id',
        'checkpoint_generation', 'route_publication_generation', 'route_commit_generation',
        'data_session_identity', 'data_session_generation')
    foreach ($field in $complete) {
        if ([string]::IsNullOrWhiteSpace((F $Candidate $field))) { return $null }
    }
    $result = [pscustomobject]@{ Admitted = $false; Source = $null; Episode = $null }
    if ((F $Candidate 'event') -cne 'filetransfer_v6_frontier_request_deferred' -or
        (F $Candidate 'direction') -cne 'inbound' -or (F $Candidate 'route') -cne 'post_tuna_fallback_v6' -or
        (F $Candidate 'protocol_version') -cne '6' -or (F $Candidate 'authority_effect') -cne 'none' -or
        (N $Candidate 'elapsed_ms') -ne 0 -or (N $Candidate 'stall_grace_ms') -ne 750 -or
        (N $Candidate 'frontier_chunk_index') -lt 0 -or
        (N $Candidate 'highest_received_chunk_index') -lt ((N $Candidate 'frontier_chunk_index') - 1) -or
        (F $Candidate 'data_session_identity') -in @('(none)', 'none')) { return $result }
    foreach ($field in @('log_writer_sequence', 'wake_generation', 'live_route_epoch', 'runtime_generation',
            'leg_generation', 'transport_epoch', 'route_publication_generation', 'data_session_generation')) {
        if ((N $Candidate $field) -le 0) { return $result }
    }
    foreach ($field in @('terminal_generation', 'bridge_recovery_generation', 'bridge_decision_generation',
            'checkpoint_generation', 'route_commit_generation')) {
        if ((N $Candidate $field) -lt 0) { return $result }
    }
    $start = [DateTimeOffset]::MinValue; $deadline = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse((F $Candidate 'stall_started_utc'), [ref]$start) -or
        -not [DateTimeOffset]::TryParse((F $Candidate 'wake_deadline_utc'), [ref]$deadline) -or
        $deadline.UtcTicks -ne ($start.UtcTicks + 7500000L)) { return $result }
    $key = '{0}|{1}|{2}' -f (F $Candidate 'log_writer_id'), (F $Candidate 'session_id'), (F $Candidate 'transfer_id')
    if (-not $Index.Timelines.ContainsKey($key) -or
        $Index.Conflicts.ContainsKey("$(F $Candidate 'log_writer_id')|$(F $Candidate 'log_writer_sequence')")) { return $result }
    $scope = @('direction', 'route', 'protocol_version', 'live_route_epoch', 'runtime_generation',
        'terminal_generation', 'leg_generation', 'route_publication_generation', 'route_commit_generation')
    $candidateBinding = Resolve-FileTransferPublicationEvidence -Index $Index.PublicationEvidence -Event $Candidate
    if ($Candidate.Fields.ContainsKey('publication_evidence_version') -and $null -eq $candidateBinding) { return $result }
    $source = $null; $sourceBinding = $null
    foreach ($entry in $Index.Timelines[$key]) {
        if ([long]$entry.Sequence -ge [long]$Candidate.Sequence) { break }
        $name = F $entry 'event'
        if ($null -ne $source -and (F $entry 'transfer_paused') -ceq '1') { return $result }
        if ($name -in @('filetransfer_route_hint_authorized_publication_accepted',
                'filetransfer_route_selected', 'filetransfer_live_route_epoch_recovered')) {
            $binding = Resolve-FileTransferPublicationEvidence -Index $Index.PublicationEvidence -Event $entry
            if ($Index.Conflicts.ContainsKey("$(F $entry 'log_writer_id')|$(F $entry 'log_writer_sequence')") -or
                ($entry.Fields.ContainsKey('publication_evidence_version') -and $null -eq $binding)) { return $result }
            if ($null -ne $binding -and $null -ne $candidateBinding -and (F $entry 'direction') -ceq (F $Candidate 'direction') -and
                ($binding.CoordinatorId -cne $candidateBinding.CoordinatorId -or $binding.SessionId -cne $candidateBinding.SessionId -or
                    $binding.TransferId -cne $candidateBinding.TransferId)) { return $result }
            if ($null -ne $sourceBinding -and $null -ne $candidateBinding -and $null -ne $binding -and
                (F $entry 'direction') -cne (F $Candidate 'direction') -and
                $binding.CoordinatorId -cne $candidateBinding.CoordinatorId -and
                $binding.SessionId -ceq $candidateBinding.SessionId -and $binding.TransferId -ceq $candidateBinding.TransferId) {
                continue
            }
            $same = $true
            foreach ($field in $scope) {
                if ((F $entry $field) -cne (F $Candidate $field)) { $same = $false; break }
            }
            if (-not $same) {
                # Once this exact source has been invalidated, an ABA restoration
                # cannot re-establish the same publication token.
                if ($null -ne $source) { return $result }
                continue
            }
            if ($name -ceq 'filetransfer_route_hint_authorized_publication_accepted') {
                if ((F $entry 'route_publication_action_kind') -cne 'RoutePublication' -or
                    (F $entry 'coordinator_decision_reason') -cne 'accepted' -or
                    (F $entry 'target_route') -cne 'post_tuna_fallback_v6' -or
                    (F $entry 'target_protocol_version') -cne '6' -or
                    (F $entry 'target_transport') -cne 'regular_nkn' -or
                    (F $entry 'fallback_leg_generation') -cne (F $Candidate 'leg_generation') -or
                    (F $entry 'fallback_transport_epoch') -cne (F $Candidate 'transport_epoch') -or
                    (F $entry 'fallback_bridge_recovery_generation') -cne (F $Candidate 'bridge_recovery_generation') -or
                    $Index.Conflicts.ContainsKey("$(F $entry 'log_writer_id')|$(F $entry 'log_writer_sequence')")) { return $result }
                if ($null -eq $source) { $source = $entry }
                if ($null -ne $candidateBinding -or $null -ne $binding) {
                    if ($null -eq $candidateBinding -or $null -eq $binding -or
                        $binding.CoordinatorId -cne $candidateBinding.CoordinatorId -or
                        $binding.SessionId -cne $candidateBinding.SessionId -or $binding.TransferId -cne $candidateBinding.TransferId -or
                        $binding.TokenFingerprint -cne $candidateBinding.TokenFingerprint) { return $result }
                    $sourceBinding = $binding
                }
            }
        }
        elseif ($null -ne $source) {
            if ($name -ne 'filetransfer_data_session_recreated' -or
                (F $entry 'data_session_identity') -cne (F $Candidate 'data_session_identity') -or
                (F $entry 'data_session_generation') -cne (F $Candidate 'data_session_generation')) { return $result }
        }
    }
    if ($null -eq $source) { return $result }
    $episodeKey = @($complete | ForEach-Object { F $Candidate $_ }) + @(
        F $Candidate 'frontier_chunk_index'; F $Candidate 'highest_received_chunk_index'
        F $Candidate 'stall_started_utc'; F $Candidate 'wake_generation'; F $Candidate 'wake_deadline_utc')
    $result.Admitted = $true; $result.Source = $source
    $result.Episode = [pscustomobject]@{
        # Progress is the legacy reducer's scope carrier here, not a repair
        # observation. Keep the actual admission and absent predecessor explicit.
        AdmissionKind = 'CurrentPublishedSource'; HistoricalProgress = $null; ScopeEvidence = $Candidate
        Key = $episodeKey -join '|'; Event = $Candidate; Progress = $Candidate; ScopeFields = $complete
        Authority = ''; Writer = F $Candidate 'log_writer_id'; Session = F $Candidate 'session_id'
        Transfer = F $Candidate 'transfer_id'; Frontier = N $Candidate 'frontier_chunk_index'
        Highest = N $Candidate 'highest_received_chunk_index'; GraceMs = 750L
        DataSessionIdentity = F $Candidate 'data_session_identity'; DataSessionGeneration = F $Candidate 'data_session_generation'
        RawEvents = [Collections.Generic.List[object]]::new()
        PublishedSource = $source
        # Private admission context; candidate records never serialize episodes.
        PublishedSourceIndex = $Index
    }
    return $result
}

# An ordinary repair after checkpoint retirement cannot borrow a scope-free
# recovery path. This layer has no exact checkpoint-to-repair binding; ownership
# is therefore unresolved, never new authority or a reconstructed receipt.
function Get-FileTransferStallGraceCheckpointScopeOwnership {
    param([hashtable]$CurrentSourceIndex, [hashtable]$PublishedSourceIndex, $Candidate)

    function F($Event, [string]$Name) { return [string]$Event.Fields[$Name] }
    function N($Event, [string]$Name) {
        $value = 0L
        if (-not [long]::TryParse((F $Event $Name), [ref]$value)) { return -1L }
        return $value
    }
    $checkpoint = F $Candidate 'checkpoint_request_id'
    if ($checkpoint -in @('', '(none)', 'none')) { return $null }
    $admission = Get-FileTransferStallGracePublishedSourceAdmission -Index $PublishedSourceIndex -Candidate $Candidate
    if ($null -eq $admission -or -not $admission.Admitted) { return $null }
    $key = '{0}|{1}|{2}' -f (F $Candidate 'log_writer_id'), (F $Candidate 'session_id'), (F $Candidate 'transfer_id')
    if (-not $CurrentSourceIndex.ContainsKey($key)) { return $null }
    $rows = $CurrentSourceIndex[$key]
    $low = 0; $high = $rows.Count
    while ($low -lt $high) {
        $mid = $low + [int][Math]::Floor(($high - $low) / 2)
        if ([long]$rows[$mid].Sequence -le [long]$Candidate.Sequence) { $low = $mid + 1 } else { $high = $mid }
    }
    $frontier = N $Candidate 'frontier_chunk_index'
    $grants = [Collections.Generic.Dictionary[string,bool]]::new([StringComparer]::Ordinal)
    $changed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    function SamePublication($Event) {
        foreach ($name in @('direction', 'route', 'protocol_version', 'live_route_epoch', 'runtime_generation',
                'terminal_generation', 'leg_generation', 'route_publication_generation', 'route_commit_generation')) {
            $value = F $Event $name
            if ([string]::IsNullOrWhiteSpace($value) -or $value -cne (F $Candidate $name)) { return $false }
        }
        return $true
    }
    # Use the existing ordered transfer interval, stopping at its first advance.
    # Bare grants and later unrelated cycles cannot poison a closed obligation.
    for ($i = $low; $i -lt $rows.Count; $i++) {
        $event = $rows[$i]; $name = F $event 'event'
        if ((F $event 'log_writer_id') -cne (F $Candidate 'log_writer_id') -or
            (F $event 'session_id') -cne (F $Candidate 'session_id') -or
            (F $event 'transfer_id') -cne (F $Candidate 'transfer_id')) { continue }
        if (($name -ceq 'filetransfer_v6_receiver_state_sent' -and
                (N $event 'contiguous_committed_chunk_index') -gt $frontier) -or
            ($name -ceq 'filetransfer_v6_sparse_write_committed' -and
                (N $event 'next_chunk_index') -gt $frontier)) { return $null }
        if ($name -notin @('filetransfer_fallback_frontier_repair_authority_granted',
                'filetransfer_fallback_frontier_repair_dispatched', 'filetransfer_v6_frontier_request_sent',
                'filetransfer_fallback_frontier_repair_progress_observed')) { continue }
        $isProgress = $name -ceq 'filetransfer_fallback_frontier_repair_progress_observed'
        $authority = F $event 'authority_id'
        if (-not $isProgress -and $authority -in @('', '(none)', 'none')) { continue }
        if ($name -ceq 'filetransfer_fallback_frontier_repair_authority_granted') {
            if ((N $event 'frontier_chunk_index') -ne $frontier) { continue }
            $grants[$authority] = (SamePublication $event) -or ($grants.ContainsKey($authority) -and $grants[$authority])
        }
        if (-not $isProgress -and -not $grants.ContainsKey($authority)) { continue }
        $writerKey = '{0}|{1}' -f (F $event 'log_writer_id'), (F $event 'log_writer_sequence')
        if ((F $event 'checkpoint_request_id') -cne $checkpoint -or
            $CurrentSourceIndex['__conflicts'].ContainsKey($writerKey)) { [void]$changed.Add($authority) }
        if (-not $isProgress -or (F $event 'direction') -cne 'inbound' -or
            (F $event 'route') -cne 'post_tuna_fallback_v6' -or (F $event 'protocol_version') -cne '6' -or
            (F $event 'source_kind') -cne 'ReceiverRequest' -or
            (N $event 'previous_frontier_chunk_index') -ne $frontier -or
            (N $event 'current_frontier_chunk_index') -le $frontier) { continue }
        # Either subject establishes relevance; a mutation of the other subject
        # must remain owned, while an unrelated publication cannot claim the hold.
        if (-not ($grants.ContainsKey($authority) -and $grants[$authority]) -and -not (SamePublication $event)) { continue }
        if ($changed.Contains($authority)) {
            $recovery = Get-FileTransferStallGraceCheckpointContinuityRecovery `
                -Index $CurrentSourceIndex -Candidate $Candidate -Progress $event
            return [pscustomobject]@{
                RecoveryEvent = $recovery
                Reason = if ($null -eq $recovery) { 'checkpoint_retirement_to_successor_repair_scope_not_exactly_bound' }
                    else { 'checkpoint_repair_continuity_exact_current_request_progress' }
            }
        }
        return $null
    }
    return $null
}

# Private evidence-only support for a previously reached frontier under a new
# Coordinator-published fallback source. Historical progress is never authority
# for the current request. Callers use this branch exclusively when scope changed.
function New-FileTransferStallGraceCurrentSourceIndex {
    param([object[]]$Events, $SnapshotEvidence = $null)
    $index = @{}; $seen = @{}; $conflicts = @{}; $continuity = @{}
    foreach ($event in $Events) {
        $fields = $event.Fields
        $writerKey = '{0}|{1}' -f $fields['log_writer_id'], $fields['log_writer_sequence']
        if ($seen.ContainsKey($writerKey)) {
            $prior = $seen[$writerKey]
            $names = @($fields.Keys | Where-Object { $_ -ne 'gui_evidence_sequence' })
            if ($names.Count -ne @($prior.Fields.Keys | Where-Object { $_ -ne 'gui_evidence_sequence' }).Count) { $conflicts[$writerKey] = $true }
            foreach ($name in $names) {
                if ([string]$fields[$name] -cne [string]$prior.Fields[$name]) { $conflicts[$writerKey] = $true; break }
            }
            continue
        }
        $seen[$writerKey] = $event
        $key = '{0}|{1}|{2}' -f $fields['log_writer_id'], $fields['session_id'], $fields['transfer_id']
        if (-not $index.ContainsKey($key)) { $index[$key] = [Collections.Generic.List[object]]::new() }
        $index[$key].Add($event)
        if ($fields.ContainsKey('continuity_receipt_id') -and $fields.ContainsKey('continuity_coordinator_id') -and
            $fields.ContainsKey('continuity_receipt_fingerprint')) {
            $receiptKey = @($fields['continuity_coordinator_id'], $fields['continuity_receipt_id'],
                $fields['continuity_receipt_fingerprint']) -join [char]31
            if (-not $continuity.ContainsKey($receiptKey)) { $continuity[$receiptKey] = [Collections.Generic.List[object]]::new() }
            $continuity[$receiptKey].Add($event)
        }
    }
    foreach ($key in @($index.Keys)) { $index[$key] = @($index[$key] | Sort-Object Sequence) }
    $index['__conflicts'] = $conflicts
    $index['__CheckpointRepairContinuitySnapshotEvidence'] = $SnapshotEvidence
    $index['__CheckpointRepairContinuityDiagnostics'] = $continuity
    return $index
}

# Only the named profile's validated read-only receipt may link a retired
# checkpoint to a different current request. Diagnostic stamps are not authority.
function Get-FileTransferStallGraceCheckpointContinuityRecovery {
    param([hashtable]$Index, $Candidate, $Progress)
    function F($Event, [string]$Name) { return [string]$Event.Fields[$Name] }
    function N($Event, [string]$Name) {
        $value = 0L
        if (-not [long]::TryParse((F $Event $Name), [ref]$value)) { return -1L }
        return $value
    }
    function Equal($Left, $Right, [string[]]$Names) {
        foreach ($name in $Names) {
            if ([string]::IsNullOrWhiteSpace((F $Left $name)) -or (F $Left $name) -cne (F $Right $name)) { return $false }
        }
        return $true
    }
    function MatchesProjectedIdentity([string]$Raw, [string]$Projected) {
        if ($Raw -ceq '(none)') { return $Projected -ceq '(none)' }
        if ([string]::IsNullOrWhiteSpace($Raw) -or $Raw -cnotmatch '^[A-Za-z0-9._:/-]+$' -or
            $Projected -cnotmatch '^fte1-[0-9a-f]{24}$') { return $false }
        $digest = Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([Text.Encoding]::UTF8.GetBytes($Raw))
        return ('fte1-' + $digest.Substring(0, 24)) -ceq $Projected
    }
    function MatchesAuthoredScope($Event, $Scope, [bool]$Source, [bool]$RequireAll) {
        $boundary = $Scope.boundary
        $numbers = [ordered]@{
            protocol_version=$boundary.protocolVersion; live_route_epoch=$boundary.liveRouteEpoch
            runtime_generation=$boundary.runtimeGeneration; terminal_generation=$boundary.terminalGeneration
            leg_generation=$boundary.legGeneration; transport_epoch=$boundary.transportEpoch
            bridge_recovery_generation=$boundary.bridgeRecoveryGeneration
            bridge_decision_generation=$(if ($Source) { $Scope.aggregateBridgeDecisionGeneration } else { $Scope.bridgeDecisionGeneration })
            route_publication_generation=$boundary.routePublicationGeneration; route_commit_generation=$boundary.routeCommitGeneration
        }
        foreach ($name in $numbers.Keys) {
            if ($Event.Fields.ContainsKey($name)) {
                if ((N $Event $name) -ne [long]$numbers[$name]) { return $false }
            } elseif ($RequireAll) { return $false }
        }
        foreach ($entry in @(@('direction', $payload.direction), @('route', $boundary.route))) {
            if (($RequireAll -or $Event.Fields.ContainsKey($entry[0])) -and
                (F $Event $entry[0]) -cne [string]$entry[1]) { return $false }
        }
        foreach ($entry in @(@('checkpoint_generation', $Scope.retainedLegCheckpointGeneration),
                @('data_session_generation', $boundary.dataSessionGeneration))) {
            if (($Source -or $Event.Fields.ContainsKey($entry[0])) -and
                (N $Event $entry[0]) -ne [long]$entry[1]) { return $false }
        }
        foreach ($entry in @(@('session_id', $payload.sessionId), @('transfer_id', $payload.transferId),
                @('checkpoint_request_id', $boundary.checkpointRequestId), @('data_session_identity', $boundary.dataSessionIdentity))) {
            $raw = F $Event $entry[0]
            # Redacted legacy fields supply no identity evidence; the authored
            # scope stamp retains the original identity. Visible contradictions fail.
            if (-not [string]::IsNullOrWhiteSpace($raw) -and -not $raw.Contains('[redacted]') -and
                -not (MatchesProjectedIdentity $raw ([string]$entry[1]))) { return $false }
        }
        return $true
    }
    function MatchesRetainedFallbackDisposition($Event) {
        # This logger's unprefixed checkpoint/data-session fields belong to
        # activation proofs, not the committed fallback scope. Bind the event
        # to the already validated terminal at the continuity origin instead.
        foreach ($entry in @(@('authority_schema','2'), @('transition_kind','TunaV4ToPostTunaFallbackV6'),
                @('handoff_kind','tuna_to_normal_fallback'), @('proof_boundary_kind','fallback_peer_proof'),
                @('decision_status','Applied'), @('decision_reason','filetransfer_route_transition_committed'),
                @('activation_authority_mode','none'), @('responder_transition_mode','none'),
                @('checkpoint_request_id','(none)'), @('data_session_identity','(none)'),
                @('fallback_yield_authority_id','(none)'), @('pair_key','(none)'), @('responder_handoff_authority_id','(none)'),
                @('target_route','post_tuna_fallback_v6'), @('target_protocol_version','6'),
                @('target_transport','regular_nkn'), @('source_route','file_tuna_v4'),
                @('source_protocol_version','4'), @('source_transport','tuna'))) {
            if ((F $Event $entry[0]) -cne $entry[1]) { return $false }
        }
        foreach ($name in @('admission_generation','pending_precommit_generation','transaction_generation','offer_generation',
                'launch_generation','tuna_path_lease_generation','retry_install_generation','fallback_yield_generation',
                'pairing_generation','responder_handoff_generation')) {
            if ((N $Event $name) -ne 0) { return $false }
        }
        if (-not (Equal $Event $Candidate @('log_writer_id','session_id','transfer_id','direction')) -or
            (N $Event 'target_transport_epoch') -ne [long]$payload.currentScope.boundary.transportEpoch) { return $false }
        $subject = $null
        foreach ($operation in $binding.Origin.Boundary.operations) {
            if ($operation.family -cne 'route_transition' -or $operation.operationRole -cne 'terminal' -or
                -not (MatchesProjectedIdentity (F $Event 'authority_id') ([string]$operation.authorityId))) { continue }
            if ($null -ne $subject) { return $false }
            $subject = $operation
        }
        if ($null -eq $subject -or $subject.operationKind -cne 'TunaV4ToPostTunaFallbackV6' -or
            $subject.beforeState -cne 'Committed' -or $subject.afterState -cne 'Committed' -or
            -not $subject.receiptValidated -or [long]$subject.operationGeneration -le 0 -or
            (N $Event 'transition_generation') -ne [long]$subject.operationGeneration -or
            -not (MatchesProjectedIdentity (F $Event 'predecessor_authority_id') ([string]$subject.predecessorAuthorityId))) { return $false }
        $route = $subject.routeTransition
        if ($null -eq $route -or $route.role -cne 'terminal' -or
            $route.transitionKind -cne 'TunaV4ToPostTunaFallbackV6' -or $route.dispositionKind -cne 'Committed' -or
            $route.handoffKind -cne 'TunaToNormalFallback' -or $route.proofAdmissionMode -cne 'fallback_v6_peer_proof' -or
            $route.proofBoundaryKind -cne 'FileTransferFallbackTransitionProofBoundary' -or
            $route.sourceTransport -cne 'Tuna' -or $route.targetTransport -cne 'RegularNkn' -or
            $route.targetRoute -cne 'post_tuna_fallback_v6' -or [long]$route.targetProtocolVersion -ne 6) { return $false }
        $commits = @($route.receipts | Where-Object { $_.stage -ceq 'commit' })
        if ($commits.Count -ne 1 -or -not $commits[0].receiptValidated -or
            $commits[0].receiptStatus -cne 'Committed') { return $false }
        $source = $route.sourceBoundary; $target = $route.targetBoundary
        $current = $payload.currentScope.boundary
        if ($source.route -cne 'file_tuna_v4' -or [long]$source.protocolVersion -ne 4 -or
            $target.route -cne $current.route -or [long]$target.protocolVersion -ne [long]$current.protocolVersion -or
            $source.dataSessionIdentity -cne $current.dataSessionIdentity -or
            [long]$target.dataSessionGeneration -ne [long]$current.dataSessionGeneration) { return $false }
        $sourceFields = [ordered]@{
            live_route_epoch='liveRouteEpoch'; runtime_generation='runtimeGeneration'; terminal_generation='terminalGeneration'
            leg_generation='legGeneration'; route_commit_generation='routeCommitGeneration'
            route_publication_generation='routePublicationGeneration'; transport_epoch='transportEpoch'
            bridge_recovery_generation='bridgeRecoveryGeneration'; data_session_generation='dataSessionGeneration'
        }
        foreach ($name in $sourceFields.Keys) {
            if ((N $Event "source_$name") -ne [long]$source.($sourceFields[$name])) { return $false }
        }
        # Resulting route ownership is aggregate scope; target_transport_epoch
        # above is the physical fallback leg. Historical source checkpoint
        # arguments are optional logger metadata and are not current authority.
        foreach ($name in @('live_route_epoch','runtime_generation','terminal_generation','leg_generation',
                'route_commit_generation','route_publication_generation')) {
            $property = $sourceFields[$name]
            if ((N $Event "resulting_$name") -ne [long]$target.$property -or
                [long]$target.$property -ne [long]$current.$property) { return $false }
        }
        return $true
    }
    if (-not $Index.ContainsKey('__CheckpointRepairContinuitySnapshotEvidence') -or
        $null -eq $Index['__CheckpointRepairContinuitySnapshotEvidence']) { return $null }
    $snapshot = $Index['__CheckpointRepairContinuitySnapshotEvidence']
    $binding = Resolve-FileTransferCheckpointRepairContinuityBinding -SnapshotEvidence $snapshot `
        -SourceFields $Candidate.Fields -CurrentFields $Progress.Fields
    if ($null -eq $binding -or [string]$binding.Boundary.writer -cne (F $Candidate 'log_writer_id')) { return $null }
    $payload = $binding.Operation.checkpointRepairContinuity
    if (-not (MatchesAuthoredScope $Candidate $payload.sourceScope $true $true)) { return $null }
    $key = @((F $Progress 'continuity_coordinator_id'), (F $Progress 'continuity_receipt_id'),
        (F $Progress 'continuity_receipt_fingerprint')) -join [char]31
    $diagnostics = $Index['__CheckpointRepairContinuityDiagnostics']
    if (-not $diagnostics.ContainsKey($key)) { return $null }
    $stages = @{}
    foreach ($event in $diagnostics[$key]) {
        if ([long]$event.Sequence -le [long]$Candidate.Sequence) { return $null }
        $writerKey = '{0}|{1}' -f (F $event 'log_writer_id'), (F $event 'log_writer_sequence')
        if ($Index['__conflicts'].ContainsKey($writerKey) -or
            -not (Equal $event $Candidate @('log_writer_id','session_id','transfer_id'))) { return $null }
        $resolved = Resolve-FileTransferCheckpointRepairContinuityBinding -SnapshotEvidence $snapshot `
            -SourceFields $Candidate.Fields -CurrentFields $event.Fields
        if ($null -eq $resolved -or $resolved.Key -cne $binding.Key) { return $null }
        $name = F $event 'event'
        if ($stages.ContainsKey($name)) { return $null }
        $stages[$name] = $event
    }
    $names = @('filetransfer_fallback_frontier_repair_authority_granted', 'filetransfer_control_plane_delivery_observed',
        'filetransfer_fallback_frontier_repair_dispatched', 'filetransfer_v6_frontier_request_sent',
        'filetransfer_fallback_frontier_repair_contract_settled', 'filetransfer_fallback_frontier_repair_progress_observed',
        'filetransfer_v6_fallback_frontier_repair_progress_state_forced', 'filetransfer_v6_receiver_state_sent')
    if ($stages.Count -ne $names.Count) { return $null }
    foreach ($name in $names) { if (-not $stages.ContainsKey($name)) { return $null } }
    $grant=$stages[$names[0]]; $delivery=$stages[$names[1]]; $dispatch=$stages[$names[2]]; $send=$stages[$names[3]]
    $settlement=$stages[$names[4]]; $advance=$stages[$names[5]]; $forced=$stages[$names[6]]; $state=$stages[$names[7]]
    if (-not (MatchesAuthoredScope $grant $payload.currentScope $false $true)) { return $null }
    foreach ($event in @($delivery, $dispatch, $send, $settlement, $advance, $forced, $state)) {
        if (-not (MatchesAuthoredScope $event $payload.currentScope $false $false)) { return $null }
    }
    foreach ($entry in @(@('authority_id', $payload.repair.authorityId),
            @('predecessor_authority_id', $payload.repair.predecessorAuthorityId),
            @('primary_protocol_repair_request_id', $payload.repair.protocolRepairRequestId),
            @('primary_request_correlation_id', $payload.repair.requestCorrelationId),
            @('repair_request_key', $payload.repair.canonicalRepairKey), @('range_fingerprint', $payload.repair.rangeFingerprint))) {
        if (-not (MatchesProjectedIdentity (F $grant $entry[0]) ([string]$entry[1]))) { return $null }
    }
    if ((N $grant 'durable_received_highest_chunk_index') -ne [long]$payload.repair.durableReceivedHighestChunkIndex -or
        (N $grant 'range_count') -ne [long]$payload.repair.rangeCount -or
        (N $grant 'requested_chunk_count') -ne [long]$payload.repair.requestedChunkCount -or
        (N $forced 'data_session_generation') -ne [long]$payload.currentScope.boundary.dataSessionGeneration) { return $null }
    $frontier = N $Candidate 'frontier_chunk_index'
    $deadline = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse((F $Candidate 'wake_deadline_utc'), [ref]$deadline) -or
        (N $grant 'log_writer_attempt_utc_ticks') -lt $deadline.UtcTicks -or
        (N $grant 'frontier_chunk_index') -ne $frontier -or [long]$payload.repair.frontierChunkIndex -ne $frontier -or
        (F $grant 'source_kind') -cne 'ReceiverRequest' -or (F $grant 'target_transport') -cne 'regular_nkn' -or
        (F $grant 'checkpoint_request_id') -cne '(none)' -or (F $grant 'authority_schema') -cne '2' -or
        (N $grant 'repair_generation') -ne [long]$payload.repair.decisionGeneration) { return $null }
    $scope = @('direction','route','protocol_version','live_route_epoch','runtime_generation','terminal_generation',
        'leg_generation','transport_epoch','bridge_recovery_generation','bridge_decision_generation',
        'checkpoint_request_id','route_publication_generation','route_commit_generation')
    $repair = $scope + @('authority_schema','authority_id','repair_generation','repair_request_key','range_fingerprint',
        'predecessor_authority_id','primary_protocol_repair_request_id','primary_request_correlation_id',
        'frontier_chunk_index','durable_received_highest_chunk_index','range_count','requested_chunk_count','source_kind','target_transport')
    foreach ($event in @($dispatch,$send,$settlement,$advance,$forced)) {
        if (-not (Equal $event $grant $repair)) { return $null }
    }
    foreach ($event in @($grant,$dispatch,$forced)) {
        if ((F $event 'protocol_repair_request_id') -cne (F $grant 'primary_protocol_repair_request_id') -or
            (F $event 'request_correlation_id') -cne (F $grant 'primary_request_correlation_id')) { return $null }
    }
    foreach ($event in @($send,$settlement,$advance)) {
        if ((F $event 'request_correlation_id') -cne (F $grant 'primary_request_correlation_id')) { return $null }
    }
    if (-not (Equal $delivery $grant @('direction','route','protocol_version','live_route_epoch','leg_generation',
            'transport_epoch','bridge_recovery_generation','checkpoint_request_id','repair_request_key','range_fingerprint')) -or
        (F $delivery 'kind') -cne 'frontier_request' -or (F $delivery 'frame_type') -cne 'filetransfer.frontier_request.v6' -or
        (F $delivery 'accepted_any') -cne '1' -or (F $delivery 'peer_visible_any') -cne '1' -or
        (F $delivery 'repair_request_id') -cne (F $grant 'primary_protocol_repair_request_id') -or
        (F $delivery 'request_correlation_id') -cne (F $grant 'primary_request_correlation_id') -or
        (F $send 'repair_request_id') -cne (F $grant 'primary_protocol_repair_request_id') -or
        (F $send 'request_correlation_id') -cne (F $grant 'primary_request_correlation_id') -or
        (F $dispatch 'dispatch_observed') -cne '1' -or (F $settlement 'dispatch_observed') -cne '1' -or
        (F $settlement 'settlement_outcome') -cne 'ProgressObserved' -or
        (N $advance 'previous_frontier_chunk_index') -ne $frontier -or (N $advance 'current_frontier_chunk_index') -le $frontier -or
        -not (Equal $forced $advance @('previous_frontier_chunk_index','current_frontier_chunk_index')) -or
        -not (Equal $forced $Candidate @('data_session_identity','data_session_generation')) -or
        (F $forced 'decision_status') -cne 'Accepted' -or
        (N $forced 'receiver_state_epoch') -le 0 -or (F $state 'epoch') -cne (F $forced 'receiver_state_epoch') -or
        (N $state 'contiguous_committed_chunk_index') -ne (N $advance 'current_frontier_chunk_index') -or
        (N $state 'durable_received_highest_chunk_index') -lt (N $Candidate 'highest_received_chunk_index') -or
        (F $state 'transport_epoch') -cne (F $grant 'transport_epoch') -or (F $state 'transfer_paused') -cne '0') { return $null }
    $prior = $Candidate
    foreach ($name in $names) {
        $event=$stages[$name]
        if ([long]$event.Sequence -le [long]$prior.Sequence -or
            (N $event 'log_writer_attempt_utc_ticks') -lt (N $prior 'log_writer_attempt_utc_ticks')) { return $null }
        $prior=$event
    }
    $finalTicks = N $state 'log_writer_attempt_utc_ticks'
    if ($finalTicks -le 0) { return $null }
    $finalMs = [long][Math]::Floor(($finalTicks - 621355968000000000L) / 10000)
    if ($null -eq (Resolve-FileTransferCheckpointRepairContinuityBinding -SnapshotEvidence $snapshot `
            -SourceFields $Candidate.Fields -CurrentFields $state.Fields -EvidenceUtcMilliseconds $finalMs)) { return $null }
    $transferKey = '{0}|{1}|{2}' -f (F $Candidate 'log_writer_id'),(F $Candidate 'session_id'),(F $Candidate 'transfer_id')
    $scopeEvidenceNames = $names + @('filetransfer_route_selected', 'filetransfer_live_route_epoch_recovered',
        'filetransfer_route_transition_committed', 'filetransfer_route_transition_disposition_applied',
        'filetransfer_route_hint_authorized_publication_accepted', 'filetransfer_v6_sparse_write_committed')
    $timeline = $Index[$transferKey]
    $low = 0; $high = $timeline.Count
    while ($low -lt $high) {
        $mid = $low + [int][Math]::Floor(($high - $low) / 2)
        if ([long]$timeline[$mid].Sequence -le [long]$Candidate.Sequence) { $low = $mid + 1 } else { $high = $mid }
    }
    for ($i = $low; $i -lt $timeline.Count; $i++) {
        $event = $timeline[$i]
        if ([long]$event.Sequence -gt [long]$state.Sequence) { break }
        if ((F $event 'event') -ceq 'filetransfer_v6_frontier_stall_grace_recheck_decided' -and
            (F $event 'wake_generation') -ceq (F $Candidate 'wake_generation')) {
            if ((F $event 'authority_effect') -cne 'none' -or
                (F $event 'decision_status') -cnotin @('stale','rearmed','dispatched','coalesced') -or
                [string]::IsNullOrWhiteSpace((F $event 'decision_reason')) -or
                -not (MatchesAuthoredScope $event $payload.sourceScope $true $true) -or
                -not (Equal $event $Candidate @('continuity_profile','continuity_coordinator_id','continuity_source_fingerprint',
                    'frontier_chunk_index','highest_received_chunk_index','stall_started_utc','wake_deadline_utc'))) { return $null }
        }
        if ((F $event 'event') -in @('file_transfer_inbound_terminal','filetransfer_live_route_epoch_terminal',
                'filetransfer_terminalized_by_session_end','filetransfer_data_session_recreated','filetransfer_data_session_removed',
                'filetransfer_user_paused') -or
            ((F $event 'peer_paused') -ceq '1' -and (F $event 'applied') -ceq '1') -or
            (F $event 'transfer_paused') -ceq '1') { return $null }
        # Attempted/denied diagnostics can describe an absent requested scope.
        # Only current-source evidence contradicts it; snapshot retirement stays authoritative.
        if ((F $event 'event') -cnotin $scopeEvidenceNames) { continue }
        $activationMetadata = (F $event 'event') -ceq 'filetransfer_route_transition_disposition_applied' -and
            ($event.Fields.ContainsKey('authority_schema') -or $event.Fields.ContainsKey('activation_authority_mode') -or
                $event.Fields.ContainsKey('data_session_identity') -or $event.Fields.ContainsKey('checkpoint_request_id'))
        if ($activationMetadata) {
            $writerKey = '{0}|{1}' -f (F $event 'log_writer_id'), (F $event 'log_writer_sequence')
            if ($Index['__conflicts'].ContainsKey($writerKey) -or
                -not (MatchesRetainedFallbackDisposition $event)) { return $null }
        }
        foreach ($field in @('direction','route','protocol_version','live_route_epoch','runtime_generation','terminal_generation',
                'leg_generation','transport_epoch','bridge_recovery_generation','bridge_decision_generation',
                'route_publication_generation','route_commit_generation','data_session_identity','data_session_generation')) {
            if ($activationMetadata -and $field -ceq 'data_session_identity') { continue }
            if ($event.Fields.ContainsKey($field) -and (F $event $field) -cne (F $Candidate $field)) { return $null }
        }
    }
    return $state
}

function Get-FileTransferStallGraceCurrentSourceRecovery {
    param([hashtable]$Index, $Candidate, $HistoricalProgress)
    function Field($Event, [string]$Name) { return [string]$Event.Fields[$Name] }
    function Number($Event, [string]$Name) {
        $value = 0L
        if (-not [long]::TryParse((Field $Event $Name), [ref]$value)) { return -1L }
        return $value
    }
    function Equal($Left, $Right, [string[]]$Names) {
        foreach ($name in $Names) {
            $value = Field $Left $name
            if ([string]::IsNullOrWhiteSpace($value) -or $value -cne (Field $Right $name)) { return $false }
        }
        return $true
    }
    $scope = @('direction', 'route', 'protocol_version', 'live_route_epoch', 'runtime_generation',
        'terminal_generation', 'leg_generation', 'transport_epoch', 'bridge_recovery_generation',
        'bridge_decision_generation', 'checkpoint_request_id', 'route_publication_generation', 'route_commit_generation')
    $changedSource = $false
    foreach ($name in $scope) {
        if (-not [string]::IsNullOrWhiteSpace((Field $HistoricalProgress $name)) -and
            -not [string]::IsNullOrWhiteSpace((Field $Candidate $name)) -and
            (Field $HistoricalProgress $name) -cne (Field $Candidate $name)) { $changedSource = $true; break }
    }
    if ((Field $HistoricalProgress 'event') -cne 'filetransfer_fallback_frontier_repair_progress_observed' -or
        -not $changedSource) { return $null }

    # A non-null result owns changed-source evaluation even when proof is absent.
    $result = [pscustomobject]@{ CurrentSource = $null; HistoricalProgress = $HistoricalProgress; RecoveryEvent = $null }
    $complete = $scope + @('checkpoint_generation', 'data_session_identity', 'data_session_generation')
    $identity = @('log_writer_id', 'session_id', 'transfer_id')
    $stable = @('direction', 'route', 'protocol_version', 'live_route_epoch', 'terminal_generation',
        'transport_epoch', 'bridge_decision_generation', 'checkpoint_request_id', 'route_commit_generation')
    if (-not (Equal $Candidate $Candidate ($complete + $identity)) -or
        -not (Equal $Candidate $HistoricalProgress ($stable + $identity)) -or
        (Field $Candidate 'event') -cne 'filetransfer_v6_frontier_request_deferred' -or
        (Field $Candidate 'authority_effect') -cne 'none' -or (Number $Candidate 'elapsed_ms') -ne 0 -or
        (Number $Candidate 'wake_generation') -le 0 -or (Number $Candidate 'frontier_chunk_index') -lt 0 -or
        (Field $Candidate 'direction') -cne 'inbound' -or (Field $Candidate 'route') -cne 'post_tuna_fallback_v6' -or
        (Field $Candidate 'protocol_version') -cne '6' -or (Number $Candidate 'data_session_generation') -le 0 -or
        (Field $Candidate 'data_session_identity') -in @('', '(none)', 'none') -or
        (Number $Candidate 'frontier_chunk_index') -ne (Number $HistoricalProgress 'current_frontier_chunk_index') -or
        (Number $Candidate 'highest_received_chunk_index') -lt ((Number $Candidate 'frontier_chunk_index') - 1)) { return $result }
    foreach ($name in @('runtime_generation', 'leg_generation', 'route_publication_generation', 'bridge_recovery_generation')) {
        if ((Number $HistoricalProgress $name) -lt 0 -or
            (Number $Candidate $name) -ne ((Number $HistoricalProgress $name) + 1)) { return $result }
    }
    $start = [DateTimeOffset]::MinValue; $deadline = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse((Field $Candidate 'stall_started_utc'), [ref]$start) -or
        -not [DateTimeOffset]::TryParse((Field $Candidate 'wake_deadline_utc'), [ref]$deadline) -or
        (Number $Candidate 'stall_grace_ms') -ne 750 -or $deadline.UtcTicks -ne ($start.UtcTicks + 7500000L)) { return $result }

    $key = '{0}|{1}|{2}' -f $Candidate.Fields['log_writer_id'], $Candidate.Fields['session_id'], $Candidate.Fields['transfer_id']
    if (-not $Index.ContainsKey($key)) { return $result }
    $events = $Index[$key]
    $low = 0; $high = $events.Count
    while ($low -lt $high) {
        $mid = $low + [int][Math]::Floor(($high - $low) / 2)
        if ([long]$events[$mid].Sequence -lt [long]$HistoricalProgress.Sequence) { $low = $mid + 1 } else { $high = $mid }
    }
    $rows = [Collections.Generic.List[object]]::new(); $buckets = @{}
    # Stop at the first advancing published state. A failed proof cannot borrow
    # a later unrelated cycle. All intervening contradictions remain in rows.
    for ($i = $low; $i -lt $events.Count; $i++) {
        $event = $events[$i]; $writerSequence = Field $event 'log_writer_sequence'
        if ($writerSequence -notmatch '^[1-9][0-9]*$' -or
            $Index['__conflicts'].ContainsKey("$(Field $event 'log_writer_id')|$writerSequence")) { return $result }
        $rows.Add($event)
        $name = Field $event 'event'
        if (-not $buckets.ContainsKey($name)) { $buckets[$name] = [Collections.Generic.List[object]]::new() }
        $buckets[$name].Add($event)
        if ([long]$event.Sequence -gt [long]$Candidate.Sequence -and $name -ceq 'filetransfer_v6_receiver_state_sent' -and
            (Number $event 'contiguous_committed_chunk_index') -gt (Number $Candidate 'frontier_chunk_index')) {
            break
        }
    }
    function Named([string]$Name) { if ($buckets.ContainsKey($Name)) { return $buckets[$Name].ToArray() } }
    function Before($Event) { return [long]$Event.Sequence -lt [long]$Candidate.Sequence }
    function After($Event) { return [long]$Event.Sequence -gt [long]$Candidate.Sequence }
    $publications = @(Named 'filetransfer_route_hint_authorized_publication_accepted' | Where-Object { Before $_ })
    if ($publications.Count -eq 0) { return $result }
    $publicationFields = @('direction', 'route', 'protocol_version', 'live_route_epoch', 'runtime_generation',
        'terminal_generation', 'leg_generation', 'route_publication_generation', 'route_commit_generation')
    foreach ($publication in $publications) {
        if (-not (Equal $publication $Candidate $publicationFields) -or
            (Field $publication 'fallback_leg_generation') -cne (Field $Candidate 'leg_generation') -or
            (Field $publication 'fallback_transport_epoch') -cne (Field $Candidate 'transport_epoch') -or
            (Field $publication 'fallback_bridge_recovery_generation') -cne (Field $Candidate 'bridge_recovery_generation') -or
            (Field $publication 'target_route') -cne 'post_tuna_fallback_v6' -or
            (Field $publication 'target_protocol_version') -cne '6' -or (Field $publication 'target_transport') -cne 'regular_nkn' -or
            (Field $publication 'coordinator_decision_reason') -cne 'accepted') { return $result }
    }
    $publication = $publications[0] # All observations above prove the same exact publication.
    $historicalStates = @(Named 'filetransfer_v6_fallback_frontier_repair_progress_state_forced' | Where-Object {
        (Before $_) -and (Equal $_ $HistoricalProgress ($scope + @('authority_id', 'repair_generation', 'repair_request_key', 'range_fingerprint'))) -and
        (Equal $_ $Candidate @('data_session_identity', 'data_session_generation'))
    })
    if ($historicalStates.Count -ne 1) { return $result }
    $currentStates = @(Named 'filetransfer_v6_receiver_state_sent' | Where-Object {
        [long]$_.Sequence -gt [long]$publication.Sequence -and (Before $_)
    })
    if ($currentStates.Count -eq 0) { return $result }
    foreach ($state in $currentStates) {
        if ((Number $state 'contiguous_committed_chunk_index') -ne (Number $Candidate 'frontier_chunk_index') -or
            (Number $state 'durable_received_highest_chunk_index') -ne (Number $Candidate 'highest_received_chunk_index') -or
            (Field $state 'transport_epoch') -cne (Field $Candidate 'transport_epoch') -or
            (Field $state 'transfer_paused') -cne '0' -or (Number $state 'epoch') -le 0) { return $result }
    }
    $result.CurrentSource = $Candidate
    $grants = @(Named 'filetransfer_fallback_frontier_repair_authority_granted' | Where-Object {
        (After $_) -and (Number $_ 'frontier_chunk_index') -eq (Number $Candidate 'frontier_chunk_index')
    })
    if ($grants.Count -ne 1) { return $result }
    $grant = $grants[0]
    if (-not (Equal $grant $Candidate $scope) -or (Number $grant 'log_writer_attempt_utc_ticks') -lt $deadline.UtcTicks -or
        (Field $grant 'source_kind') -cne 'ReceiverRequest' -or (Field $grant 'target_transport') -cne 'regular_nkn' -or
        (Field $grant 'authority_id') -ceq (Field $HistoricalProgress 'authority_id') -or
        (Number $grant 'repair_generation') -le (Number $HistoricalProgress 'repair_generation') -or
        (Field $grant 'authority_schema') -cne '2' -or (Number $grant 'range_count') -ne 1 -or
        (Number $grant 'requested_chunk_count') -ne 1 -or
        (Field $grant 'repair_request_key') -cne "fallback-frontier:$(Field $Candidate 'frontier_chunk_index'):1:$(Field $grant 'range_fingerprint')" -or
        (Number $grant 'repair_generation') -le 0 -or
        (Number $grant 'durable_received_highest_chunk_index') -ne (Number $Candidate 'highest_received_chunk_index')) { return $result }
    foreach ($name in @('authority_id', 'repair_request_key', 'range_fingerprint', 'primary_protocol_repair_request_id', 'primary_request_correlation_id')) {
        if ((Field $grant $name) -in @('', '(none)', 'none')) { return $result }
    }
    $repair = $scope + @('authority_schema', 'authority_id', 'repair_generation', 'repair_request_key', 'range_fingerprint', 'predecessor_authority_id',
        'primary_protocol_repair_request_id', 'primary_request_correlation_id', 'frontier_chunk_index', 'durable_received_highest_chunk_index', 'source_kind', 'target_transport')
    $stages = @{}
    foreach ($name in @('filetransfer_control_plane_delivery_observed', 'filetransfer_fallback_frontier_repair_dispatched',
            'filetransfer_v6_frontier_request_sent', 'filetransfer_fallback_frontier_repair_contract_settled',
            'filetransfer_fallback_frontier_repair_progress_observed', 'filetransfer_v6_fallback_frontier_repair_progress_state_forced')) {
        $matches = @(Named $name | Where-Object {
            (After $_) -and $(if ($name -ceq 'filetransfer_control_plane_delivery_observed') {
                (Field $_ 'repair_request_id') -ceq (Field $grant 'primary_protocol_repair_request_id') -or
                    (Field $_ 'request_correlation_id') -ceq (Field $grant 'primary_request_correlation_id')
            } else { (Field $_ 'authority_id') -ceq (Field $grant 'authority_id') })
        })
        if ($matches.Count -ne 1) { return $result }
        $stages[$name] = $matches[0]
        if ($name -cne 'filetransfer_control_plane_delivery_observed' -and -not (Equal $matches[0] $grant $repair)) { return $result }
    }
    $delivery = $stages['filetransfer_control_plane_delivery_observed']; $dispatch = $stages['filetransfer_fallback_frontier_repair_dispatched']
    $send = $stages['filetransfer_v6_frontier_request_sent']; $settlement = $stages['filetransfer_fallback_frontier_repair_contract_settled']
    $progress = $stages['filetransfer_fallback_frontier_repair_progress_observed']; $forced = $stages['filetransfer_v6_fallback_frontier_repair_progress_state_forced']
    if (-not (Equal $delivery $grant @('direction', 'route', 'protocol_version', 'live_route_epoch', 'leg_generation', 'transport_epoch',
            'bridge_recovery_generation', 'checkpoint_request_id', 'repair_request_key', 'range_fingerprint')) -or
        (Field $delivery 'repair_request_id') -cne (Field $grant 'primary_protocol_repair_request_id') -or
        (Field $delivery 'request_correlation_id') -cne (Field $grant 'primary_request_correlation_id') -or
        (Field $delivery 'kind') -cne 'frontier_request' -or (Field $delivery 'frame_type') -cne 'filetransfer.frontier_request.v6' -or
        (Field $delivery 'authority_schema') -cne '2' -or (Number $delivery 'control_plane_generation') -le 0 -or
        (Number $delivery 'first_start_chunk_index') -ne (Number $Candidate 'frontier_chunk_index') -or
        (Number $delivery 'first_chunk_count') -ne 1 -or
        (Field $delivery 'accepted_any') -cne '1' -or (Field $delivery 'peer_visible_any') -cne '1' -or
        (Field $dispatch 'dispatch_observed') -cne '1' -or (Field $settlement 'dispatch_observed') -cne '1' -or
        (Field $settlement 'settlement_outcome') -cne 'ProgressObserved' -or
        (Field $send 'repair_request_id') -cne (Field $grant 'primary_protocol_repair_request_id') -or
        (Field $send 'request_correlation_id') -cne (Field $grant 'primary_request_correlation_id') -or
        (Number $progress 'previous_frontier_chunk_index') -ne (Number $Candidate 'frontier_chunk_index') -or
        (Number $progress 'current_frontier_chunk_index') -le (Number $Candidate 'frontier_chunk_index') -or
        -not (Equal $forced $progress @('previous_frontier_chunk_index', 'current_frontier_chunk_index')) -or
        -not (Equal $forced $Candidate @('data_session_identity', 'data_session_generation')) -or
        (Field $forced 'decision_status') -cne 'Accepted' -or (Number $forced 'receiver_state_epoch') -le 0) { return $result }
    $states = @(Named 'filetransfer_v6_receiver_state_sent' | Where-Object { After $_ })
    $states = @($states | Where-Object { (Field $_ 'epoch') -ceq (Field $forced 'receiver_state_epoch') })
    if ($states.Count -ne 1) { return $result }
    $state = $states[0]
    if ((Number $state 'contiguous_committed_chunk_index') -ne (Number $progress 'current_frontier_chunk_index') -or
        (Number $state 'durable_received_highest_chunk_index') -lt (Number $Candidate 'highest_received_chunk_index') -or
        (Field $state 'transport_epoch') -cne (Field $Candidate 'transport_epoch') -or (Field $state 'transfer_paused') -cne '0') { return $result }
    $prior = $grant
    foreach ($event in @($delivery, $dispatch, $send, $settlement, $progress, $forced, $state)) {
        if ([long]$event.Sequence -le [long]$prior.Sequence -or
            (Number $event 'log_writer_attempt_utc_ticks') -lt (Number $prior 'log_writer_attempt_utc_ticks')) { return $result }
        $prior = $event
    }
    foreach ($event in $rows) {
        if ((Field $event 'event') -in @('file_transfer_inbound_terminal', 'filetransfer_live_route_epoch_terminal',
                'filetransfer_terminalized_by_session_end', 'filetransfer_data_session_recreated', 'filetransfer_data_session_removed') -or
            (Field $event 'transfer_paused') -ceq '1') { return $result }
        $expected = if ([long]$event.Sequence -lt [long]$publication.Sequence) { $HistoricalProgress } else { $Candidate }
        foreach ($name in $scope) {
            # A checkpoint-proof delivery carries the proof request ID, not
            # the current frontier authority's checkpoint binding.
            if ($name -ceq 'checkpoint_request_id' -and (Field $event 'event') -ceq 'filetransfer_control_plane_delivery_observed' -and
                (Field $event 'kind') -ceq 'fallback_checkpoint_proof') { continue }
            if ($event.Fields.ContainsKey($name) -and (Field $event $name) -cne (Field $expected $name)) { return $result }
        }
        foreach ($name in @('data_session_identity', 'data_session_generation')) {
            if ($event.Fields.ContainsKey($name) -and (Field $event $name) -cne (Field $Candidate $name)) { return $result }
        }
        if ([long]$event.Sequence -ge [long]$Candidate.Sequence -and $event.Fields.ContainsKey('checkpoint_generation') -and
            (Field $event 'checkpoint_generation') -cne (Field $Candidate 'checkpoint_generation')) { return $result }
    }
    $result.RecoveryEvent = $state
    return $result
}

# A highest-only stale callback is diagnostic, not repair authority. Exact
# ordinary progress may nevertheless close its stall obligation after grace.
function Get-FileTransferStallGraceStaleNaturalProgressClosure {
    param([object[]]$Events, [Parameter(Mandatory = $true)]$Episode)

    function Field($Event, [string]$Name) { return [string]$Event.Fields[$Name] }
    function Number($Event, [string]$Name) {
        $value = 0L
        if (-not [long]::TryParse((Field $Event $Name), [ref]$value)) { return -1L }
        return $value
    }
    function Ticks($Event, [string]$Name) {
        $value = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse((Field $Event $Name), [ref]$value)) { return 0L }
        return $value.UtcTicks
    }
    function Equal($Left, $Right, [string[]]$Names) {
        foreach ($name in $Names) {
            $value = Field $Left $name
            if ([string]::IsNullOrWhiteSpace($value) -or $value -cne (Field $Right $name)) { return $false }
        }
        return $true
    }
    $stall = $Episode.Event
    $scope = @('direction', 'route', 'protocol_version', 'live_route_epoch', 'runtime_generation',
        'terminal_generation', 'leg_generation', 'transport_epoch', 'bridge_recovery_generation',
        'bridge_decision_generation', 'checkpoint_request_id', 'route_publication_generation',
        'route_commit_generation', 'checkpoint_generation', 'data_session_identity', 'data_session_generation')
    $wake = @('frontier_chunk_index', 'highest_received_chunk_index', 'wake_generation', 'stall_started_utc', 'wake_deadline_utc')
    $frontier = Number $stall 'frontier_chunk_index'; $highest = Number $stall 'highest_received_chunk_index'
    $deadline = Ticks $stall 'wake_deadline_utc'
    if (-not (Equal $stall $stall ($scope + $wake + @('log_writer_id', 'session_id', 'transfer_id'))) -or
        (Field $stall 'event') -cne 'filetransfer_v6_frontier_request_deferred' -or
        (Field $stall 'direction') -cne 'inbound' -or (Field $stall 'route') -cne 'post_tuna_fallback_v6' -or
        (Field $stall 'protocol_version') -cne '6' -or (Field $stall 'authority_effect') -cne 'none' -or
        $frontier -lt 0 -or $highest -le $frontier -or (Number $stall 'wake_generation') -le 0 -or
        (Field $stall 'data_session_identity') -in @('', '(none)', 'none') -or
        (Number $stall 'stall_grace_ms') -ne 750 -or (Ticks $stall 'stall_started_utc') -le 0 -or
        $deadline -ne ((Ticks $stall 'stall_started_utc') + 7500000L)) { return $null }
    foreach ($name in @('live_route_epoch', 'runtime_generation', 'leg_generation', 'transport_epoch',
            'route_publication_generation', 'data_session_generation')) {
        if ((Number $stall $name) -le 0) { return $null }
    }
    foreach ($name in @('terminal_generation', 'bridge_recovery_generation', 'bridge_decision_generation',
            'route_commit_generation', 'checkpoint_generation')) {
        if ((Number $stall $name) -lt 0) { return $null }
    }

    $index = New-FileTransferStallGraceCurrentSourceIndex -Events $Events
    $key = '{0}|{1}|{2}' -f (Field $stall 'log_writer_id'), (Field $stall 'session_id'), (Field $stall 'transfer_id')
    if (-not $index.ContainsKey($key)) { return $null }
    $ordered = @($index[$key] | Where-Object { [long]$_.Sequence -ge [long]$stall.Sequence })
    # Select the first physical advance and publication, before testing their
    # outcomes. Never skip a contradictory first subject for later recovery.
    $commits = @($ordered | Where-Object {
        (Field $_ 'event') -ceq 'filetransfer_v6_sparse_write_committed' -and
        (Number $_ 'log_writer_attempt_utc_ticks') -ge $deadline -and
        (Number $_ 'next_chunk_index') -gt $frontier
    })
    if ($commits.Count -eq 0) { return $null }
    $commit = $commits[0]
    $states = @($ordered | Where-Object {
        [long]$_.Sequence -gt [long]$commit.Sequence -and (Field $_ 'event') -ceq 'filetransfer_v6_receiver_state_sent'
    })
    if ($states.Count -eq 0) { return $null }
    $state = $states[0]
    $interval = @($ordered | Where-Object { [long]$_.Sequence -le [long]$state.Sequence })
    foreach ($event in $Events) {
        if ((Field $event 'log_writer_id') -cne (Field $stall 'log_writer_id') -or
            [long]$event.Sequence -lt [long]$stall.Sequence -or [long]$event.Sequence -gt [long]$state.Sequence) { continue }
        $writerKey = '{0}|{1}' -f (Field $event 'log_writer_id'), (Field $event 'log_writer_sequence')
        if ($index['__conflicts'].ContainsKey($writerKey) -or (Field $event 'log_writer_sequence') -notmatch '^[1-9][0-9]*$' -or
            ($null -ne $event.PSObject.Properties['EventName'] -and $event.EventName -cne (Field $event 'event'))) { return $null }
    }
    $rechecks = @($interval | Where-Object {
        (Field $_ 'event') -ceq 'filetransfer_v6_frontier_stall_grace_recheck_decided' -and
        (Field $_ 'wake_generation') -ceq (Field $stall 'wake_generation')
    })
    if ($rechecks.Count -ne 1) { return $null }
    $recheck = $rechecks[0]
    if (-not (Equal $recheck $stall ($scope + $wake)) -or
        (Field $recheck 'decision_status') -cne 'stale' -or (Field $recheck 'decision_reason') -cne 'frontier_scope_changed' -or
        (Field $recheck 'authority_effect') -cne 'none' -or
        (Number $recheck 'log_writer_attempt_utc_ticks') -lt $deadline -or
        [long]$recheck.Sequence -ge [long]$commit.Sequence) { return $null }

    $growth = @($interval | Where-Object {
        [long]$_.Sequence -gt [long]$stall.Sequence -and [long]$_.Sequence -lt [long]$recheck.Sequence -and
        (Field $_ 'event') -ceq 'filetransfer_v6_receiver_state_sent'
    })
    if ($growth.Count -eq 0) { return $null }
    $growthEpoch = 0L; $growthHighest = $highest
    foreach ($event in $growth) {
        if ((Number $event 'contiguous_committed_chunk_index') -ne $frontier -or
            (Number $event 'durable_received_highest_chunk_index') -lt $growthHighest -or
            (Number $event 'epoch') -le 0 -or (Number $event 'epoch') -lt $growthEpoch) { return $null }
        $growthEpoch = Number $event 'epoch'; $growthHighest = Number $event 'durable_received_highest_chunk_index'
    }
    if ($growthHighest -le $highest) { return $null }
    $arms = @($interval | Where-Object {
        [long]$_.Sequence -gt [long]$recheck.Sequence -and [long]$_.Sequence -lt [long]$commit.Sequence -and
        (Field $_ 'event') -ceq 'filetransfer_v6_frontier_recent_progress_grace_deferred' -and
        (Number $_ 'frontier_chunk_index') -gt $frontier
    })
    if ($arms.Count -ne 1) { return $null }
    $arm = $arms[0]
    $progressTicks = Ticks $arm 'committed_progress_utc'
    $next = Number $commit 'next_chunk_index'; $nextHighest = Number $commit 'highest_received_chunk_index'
    if (-not (Equal $arm $stall $scope) -or (Number $arm 'wake_generation') -le 0 -or
        (Field $arm 'authority_effect') -cne 'none' -or (Field $arm 'decision_status') -cne 'held' -or
        (Field $arm 'decision_reason') -cne 'recent_progress_grace_current' -or
        $progressTicks -le (Number $recheck 'log_writer_attempt_utc_ticks') -or
        (Ticks $arm 'wake_deadline_utc') -ne ($progressTicks + [TimeSpan]::TicksPerSecond) -or
        (Number $arm 'log_writer_attempt_utc_ticks') -lt $progressTicks -or
        (Number $commit 'log_writer_attempt_utc_ticks') -ge (Ticks $arm 'wake_deadline_utc') -or
        (Number $arm 'frontier_chunk_index') -ne $next -or (Number $arm 'highest_received_chunk_index') -ne $nextHighest -or
        (Number $commit 'contiguous_chunks_committed') -ne ($next - $frontier) -or $nextHighest -le $next -or
        (Number $state 'contiguous_committed_chunk_index') -ne $next -or
        (Number $state 'durable_received_highest_chunk_index') -ne $nextHighest -or
        (Number $state 'epoch') -le $growthEpoch) { return $null }

    $currentFrontier = $frontier; $currentHighest = $highest; $currentEpoch = 0L
    $stateFrontier = $frontier; $stateHighest = $highest
    $priorTicks = Number $stall 'log_writer_attempt_utc_ticks'
    foreach ($event in $interval) {
        $ticks = Number $event 'log_writer_attempt_utc_ticks'
        if ($ticks -le 0 -or $ticks -lt $priorTicks) { return $null }
        $priorTicks = $ticks
        $name = Field $event 'event'
        if ($name -in @('file_transfer_inbound_terminal', 'filetransfer_live_route_epoch_terminal',
                'filetransfer_terminalized_by_session_end', 'filetransfer_data_session_recreated', 'filetransfer_data_session_removed') -or
            (Field $event 'transfer_paused') -ceq '1') { return $null }
        # The full-scope wakes anchor the interval. Scope-poor physical records
        # can corroborate progress, never supply or override authority scope.
        foreach ($fieldName in $scope) {
            if ($event.Fields.ContainsKey($fieldName) -and (Field $event $fieldName) -cne (Field $stall $fieldName)) { return $null }
        }
        if ($name -in @('filetransfer_fallback_frontier_repair_authority_granted', 'filetransfer_control_plane_delivery_observed',
                'filetransfer_control_plane_delivery_result',
                'filetransfer_fallback_frontier_repair_dispatched', 'filetransfer_v6_frontier_request_sent',
                'filetransfer_fallback_frontier_repair_progress_observed', 'filetransfer_fallback_frontier_repair_contract_settled') -and
            ((Number $event 'frontier_chunk_index') -eq $frontier -or
                (Field $event 'repair_request_id').StartsWith("v6-frontier:${frontier}:", [StringComparison]::Ordinal) -or
                (Field $event 'repair_request_key').StartsWith("fallback-frontier:${frontier}:", [StringComparison]::Ordinal))) { return $null }
        if ($name -notin @('filetransfer_v6_receiver_state_sent', 'filetransfer_v6_sparse_write_committed')) { continue }
        $f = if ($name -eq 'filetransfer_v6_receiver_state_sent') { Number $event 'contiguous_committed_chunk_index' } else { Number $event 'next_chunk_index' }
        $h = if ($name -eq 'filetransfer_v6_receiver_state_sent') { Number $event 'durable_received_highest_chunk_index' } else { Number $event 'highest_received_chunk_index' }
        if ($f -lt $currentFrontier -or $h -lt $currentHighest -or $h -lt $f -or
            ([long]$event.Sequence -lt [long]$commit.Sequence -and $f -ne $frontier)) { return $null }
        $currentFrontier = $f; $currentHighest = $h
        if ($name -eq 'filetransfer_v6_receiver_state_sent') {
            $epoch = Number $event 'epoch'
            if ($epoch -le 0 -or $epoch -lt $currentEpoch -or (Field $event 'transfer_paused') -cne '0' -or
                (Field $event 'transport_epoch') -cne (Field $stall 'transport_epoch') -or
                ($epoch -eq $currentEpoch -and ($f -ne $stateFrontier -or $h -ne $stateHighest))) { return $null }
            $currentEpoch = $epoch; $stateFrontier = $f; $stateHighest = $h
        }
    }
    return $state
}

# Highest-only growth can obsolete a wake before a normal request entry wins.
# The stale callback is evidence only; a new exact grant and committed progress
# close the obligation without manufacturing a repair ProgressObserved receipt.
function Get-FileTransferStallGraceStaleHighestProgressClosure {
    param([object[]]$Events, [Parameter(Mandatory = $true)]$Episode)

    function Field($Event, [string]$Name) { return [string]$Event.Fields[$Name] }
    function Number($Event, [string]$Name) {
        $value = 0L
        if (-not [long]::TryParse((Field $Event $Name), [ref]$value)) { return -1L }
        return $value
    }
    function Ticks($Event, [string]$Name) {
        $value = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse((Field $Event $Name), [ref]$value)) { return 0L }
        return $value.UtcTicks
    }
    function Equal($Left, $Right, [string[]]$Names) {
        foreach ($name in $Names) {
            $value = Field $Left $name
            if ([string]::IsNullOrWhiteSpace($value) -or $value -cne (Field $Right $name)) { return $false }
        }
        return $true
    }
    $stall = $Episode.Event
    $scope = @('direction', 'route', 'protocol_version', 'live_route_epoch', 'runtime_generation',
        'terminal_generation', 'leg_generation', 'transport_epoch', 'bridge_recovery_generation',
        'bridge_decision_generation', 'checkpoint_request_id', 'route_publication_generation', 'route_commit_generation')
    $completeScope = $scope + @('checkpoint_generation', 'data_session_identity', 'data_session_generation')
    $wake = @('frontier_chunk_index', 'highest_received_chunk_index', 'wake_generation', 'stall_started_utc', 'wake_deadline_utc')
    $deadline = Ticks $stall 'wake_deadline_utc'
    $frontier = Number $stall 'frontier_chunk_index'
    $highest = Number $stall 'highest_received_chunk_index'
    if (-not (Equal $stall $stall ($completeScope + $wake + @('log_writer_id', 'session_id', 'transfer_id'))) -or
        (Field $stall 'direction') -cne 'inbound' -or (Field $stall 'route') -cne 'post_tuna_fallback_v6' -or
        (Field $stall 'protocol_version') -cne '6' -or (Field $stall 'authority_effect') -cne 'none' -or
        $frontier -lt 0 -or $highest -le $frontier -or (Number $stall 'wake_generation') -le 0 -or
        (Number $stall 'data_session_generation') -le 0 -or (Field $stall 'data_session_identity') -in @('', '(none)', 'none') -or
        (Number $stall 'stall_grace_ms') -ne 750 -or (Ticks $stall 'stall_started_utc') -le 0 -or
        $deadline -ne ((Ticks $stall 'stall_started_utc') + 7500000L)) { return $null }
    foreach ($name in @('live_route_epoch', 'runtime_generation', 'leg_generation', 'transport_epoch',
            'route_publication_generation', 'data_session_generation')) {
        if ((Number $stall $name) -le 0) { return $null }
    }
    foreach ($name in @('terminal_generation', 'bridge_recovery_generation', 'bridge_decision_generation',
            'route_commit_generation', 'checkpoint_generation')) {
        if ((Number $stall $name) -lt 0) { return $null }
    }

    $seen = @{}
    $conflicts = [Collections.Generic.List[long]]::new()
    $scoped = [Collections.Generic.List[object]]::new()
    foreach ($event in $Events) {
        if ((Field $event 'log_writer_id') -cne (Field $stall 'log_writer_id')) { continue }
        if ($null -ne $event.PSObject.Properties['EventName'] -and $event.EventName -cne (Field $event 'event')) {
            $conflicts.Add([long]$event.Sequence)
        }
        $key = Field $event 'log_writer_sequence'
        if ($key -notmatch '^[1-9][0-9]*$') {
            if ((Equal $event $stall @('session_id', 'transfer_id'))) { $conflicts.Add([long]$event.Sequence) }
            continue
        }
        # Check writer copies before transfer filtering: changed identity is a
        # conflict, not permission for the copy to disappear from the proof.
        if ($seen.ContainsKey($key)) {
            $prior = $seen[$key]
            $names = @($event.Fields.Keys | Where-Object { $_ -ne 'gui_evidence_sequence' })
            $same = $names.Count -eq @($prior.Fields.Keys | Where-Object { $_ -ne 'gui_evidence_sequence' }).Count
            foreach ($name in $names) {
                if ((Field $event $name) -cne (Field $prior $name)) { $same = $false; break }
            }
            if (-not $same) { $conflicts.Add([long]$event.Sequence); $conflicts.Add([long]$prior.Sequence) }
            continue
        }
        $seen[$key] = $event
        if ([long]$event.Sequence -lt [long]$stall.Sequence -or
            -not (Equal $event $stall @('session_id', 'transfer_id'))) { continue }
        $scoped.Add($event)
    }
    # Events supplied by the partition are bounded support, not global input.
    $ordered = @($scoped | Sort-Object Sequence)
    $buckets = @{}
    foreach ($event in $ordered) {
        if ([long]$event.Sequence -le [long]$stall.Sequence) { continue }
        $name = Field $event 'event'
        if (-not $buckets.ContainsKey($name)) { $buckets[$name] = [Collections.Generic.List[object]]::new() }
        $buckets[$name].Add($event)
    }
    function Named([string]$Name) { if ($buckets.ContainsKey($Name)) { return $buckets[$Name].ToArray() } }
    $rechecks = @(Named 'filetransfer_v6_frontier_stall_grace_recheck_decided' | Where-Object {
        (Field $_ 'wake_generation') -ceq (Field $stall 'wake_generation')
    })
    if ($rechecks.Count -ne 1) { return $null }
    $recheck = $rechecks[0]
    if (-not (Equal $recheck $stall ($completeScope + $wake)) -or
        (Field $recheck 'decision_status') -cne 'stale' -or (Field $recheck 'decision_reason') -cne 'frontier_scope_changed' -or
        (Field $recheck 'authority_effect') -cne 'none' -or
        (Number $recheck 'log_writer_attempt_utc_ticks') -lt (Number $stall 'log_writer_attempt_utc_ticks')) { return $null }

    $growthHighest = $highest
    $growthEpoch = 0L
    foreach ($event in @(Named 'filetransfer_v6_receiver_state_sent')) {
        if ([long]$event.Sequence -ge [long]$recheck.Sequence) { break }
        if ((Number $event 'contiguous_committed_chunk_index') -ne $frontier -or
            (Number $event 'durable_received_highest_chunk_index') -lt $growthHighest -or
            (Number $event 'epoch') -le 0 -or (Number $event 'epoch') -lt $growthEpoch -or
            (Field $event 'transfer_paused') -cne '0' -or (Field $event 'transport_epoch') -cne (Field $stall 'transport_epoch')) { return $null }
        $growthHighest = Number $event 'durable_received_highest_chunk_index'
        $growthEpoch = Number $event 'epoch'
    }
    if ($growthHighest -le $highest) { return $null }

    # Select subjects before their successful outcomes. A malformed first
    # grant/advance must not be skipped in favour of a later recovery.
    $grants = @(Named 'filetransfer_fallback_frontier_repair_authority_granted' | Where-Object {
        [long]$_.Sequence -gt [long]$recheck.Sequence -and (Number $_ 'frontier_chunk_index') -eq $frontier
    })
    if ($grants.Count -eq 0) { return $null }
    $grant = $grants[0]
    if (-not (Equal $grant $stall $scope) -or (Field $grant 'authority_schema') -cne '2' -or
        (Field $grant 'source_kind') -cne 'ReceiverRequest' -or (Field $grant 'target_transport') -cne 'regular_nkn' -or
        (Field $grant 'authority_decision_reason') -cne 'fallback_frontier_request' -or
        (Number $grant 'repair_generation') -le 0 -or (Number $grant 'range_count') -le 0 -or
        (Number $grant 'requested_chunk_count') -le 0 -or (Number $grant 'log_writer_attempt_utc_ticks') -lt $deadline -or
        (Number $grant 'log_writer_attempt_utc_ticks') -lt (Number $recheck 'log_writer_attempt_utc_ticks') -or
        (Number $grant 'durable_received_highest_chunk_index') -lt $growthHighest) { return $null }
    foreach ($name in @('authority_id', 'repair_request_key', 'range_fingerprint',
            'primary_protocol_repair_request_id', 'primary_request_correlation_id')) {
        if ((Field $grant $name) -in @('', '(none)', 'none')) { return $null }
    }
    if (-not (Field $grant 'range_fingerprint').StartsWith('frng-', [StringComparison]::Ordinal) -or
        (Field $grant 'repair_request_key') -cne "fallback-frontier:${frontier}:$(Number $grant 'requested_chunk_count'):$(Field $grant 'range_fingerprint')") { return $null }
    $repair = $scope + @('authority_schema', 'authority_id', 'repair_generation', 'repair_request_key', 'range_fingerprint',
        'primary_protocol_repair_request_id', 'primary_request_correlation_id', 'frontier_chunk_index',
        'durable_received_highest_chunk_index', 'source_kind', 'target_transport', 'predecessor_authority_id',
        'range_count', 'requested_chunk_count', 'authority_decision_reason')
    $stages = @{}
    foreach ($name in @('filetransfer_control_plane_delivery_observed', 'filetransfer_fallback_frontier_repair_dispatched',
            'filetransfer_v6_frontier_request_sent')) {
        $subjects = @(Named $name | Where-Object {
            (Field $_ 'authority_id') -ceq (Field $grant 'authority_id') -or
            (Field $_ 'repair_request_id') -ceq (Field $grant 'primary_protocol_repair_request_id') -or
            (Field $_ 'primary_protocol_repair_request_id') -ceq (Field $grant 'primary_protocol_repair_request_id') -or
            (Field $_ 'primary_request_correlation_id') -ceq (Field $grant 'primary_request_correlation_id') -or
            (Field $_ 'request_correlation_id') -ceq (Field $grant 'primary_request_correlation_id')
        })
        if ($subjects.Count -ne 1) { return $null }
        $stages[$name] = $subjects[0]
    }
    $delivery = $stages['filetransfer_control_plane_delivery_observed']
    $dispatch = $stages['filetransfer_fallback_frontier_repair_dispatched']
    $send = $stages['filetransfer_v6_frontier_request_sent']
    if (-not (Equal $dispatch $grant $repair) -or -not (Equal $send $grant $repair) -or
        (Field $dispatch 'dispatch_observed') -cne '1' -or
        (Field $dispatch 'protocol_repair_request_id') -cne (Field $grant 'primary_protocol_repair_request_id') -or
        (Field $dispatch 'request_correlation_id') -cne (Field $grant 'primary_request_correlation_id') -or
        (Field $send 'repair_request_id') -cne (Field $grant 'primary_protocol_repair_request_id') -or
        (Field $send 'protocol_repair_request_id') -cne (Field $grant 'primary_protocol_repair_request_id') -or
        (Field $send 'request_correlation_id') -cne (Field $grant 'primary_request_correlation_id') -or
        (Field $send 'reason') -cnotin @('chunk_batch_committed', 'frontier_stall_repair_due') -or
        -not (Equal $delivery $grant @('authority_schema', 'route', 'protocol_version', 'direction', 'live_route_epoch',
            'leg_generation', 'transport_epoch', 'bridge_recovery_generation', 'checkpoint_request_id',
            'repair_request_key', 'range_fingerprint', 'range_count', 'requested_chunk_count')) -or
        (Field $delivery 'authority_id') -in @('', '(none)', 'none', (Field $grant 'authority_id')) -or
        (Number $delivery 'control_plane_generation') -le 0 -or
        (Field $delivery 'repair_request_id') -cne (Field $grant 'primary_protocol_repair_request_id') -or
        (Field $delivery 'request_correlation_id') -cne (Field $grant 'primary_request_correlation_id') -or
        (Field $delivery 'reason') -cne (Field $send 'reason') -or
        (Field $delivery 'kind') -cne 'frontier_request' -or (Field $delivery 'frame_type') -cne 'filetransfer.frontier_request.v6' -or
        (Field $delivery 'accepted_any') -cne '1' -or (Field $delivery 'peer_visible_any') -cne '1' -or
        (Field $delivery 'local_only_rejected') -cne '0' -or
        (Number $delivery 'first_start_chunk_index') -ne $frontier -or (Number $delivery 'first_chunk_count') -le 0 -or
        (Number $delivery 'first_chunk_count') -gt (Number $grant 'requested_chunk_count')) { return $null }
    $prior = $recheck
    foreach ($event in @($grant, $delivery, $dispatch, $send)) {
        if ([long]$event.Sequence -le [long]$prior.Sequence -or
            (Number $event 'log_writer_attempt_utc_ticks') -lt (Number $prior 'log_writer_attempt_utc_ticks')) { return $null }
        $prior = $event
    }
    $advances = @(Named 'filetransfer_v6_frontier_request_deferred' | Where-Object {
        [long]$_.Sequence -gt [long]$send.Sequence -and (Number $_ 'frontier_chunk_index') -gt $frontier
    })
    if ($advances.Count -eq 0) { return $null }
    $advance = $advances[0]
    if (-not (Equal $advance $stall $completeScope) -or (Field $advance 'reason') -cne 'chunk_batch_committed' -or
        (Field $advance 'authority_effect') -cne 'none' -or (Number $advance 'elapsed_ms') -ne 0 -or
        (Number $advance 'stall_grace_ms') -ne 750 -or (Number $advance 'wake_generation') -le (Number $stall 'wake_generation') -or
        (Number $advance 'highest_received_chunk_index') -lt (Number $grant 'durable_received_highest_chunk_index') -or
        (Ticks $advance 'stall_started_utc') -lt (Number $send 'log_writer_attempt_utc_ticks') -or
        (Ticks $advance 'wake_deadline_utc') -ne ((Ticks $advance 'stall_started_utc') + 7500000L)) { return $null }
    $states = @(Named 'filetransfer_v6_receiver_state_sent' | Where-Object {
        [long]$_.Sequence -gt [long]$advance.Sequence -and
        (Number $_ 'contiguous_committed_chunk_index') -ge (Number $advance 'frontier_chunk_index')
    })
    if ($states.Count -eq 0) { return $null }
    $state = $states[0]
    if ((Number $state 'durable_received_highest_chunk_index') -lt (Number $advance 'highest_received_chunk_index') -or
        (Number $state 'epoch') -le $growthEpoch -or (Number $state 'log_writer_attempt_utc_ticks') -lt (Ticks $advance 'stall_started_utc')) { return $null }
    if (@($conflicts | Where-Object { $_ -ge [long]$stall.Sequence -and $_ -le [long]$state.Sequence }).Count -gt 0) { return $null }

    $predecessor = Field $grant 'predecessor_authority_id'
    if ([string]::IsNullOrWhiteSpace($predecessor) -or $predecessor -ceq (Field $grant 'authority_id')) { return $null }
    if ($predecessor -cnotin @('(none)', 'none')) {
        $retirements = @(Named 'filetransfer_fallback_frontier_repair_contract_settled' | Where-Object {
            [long]$_.Sequence -le [long]$state.Sequence -and (Field $_ 'authority_id') -ceq $predecessor
        })
        if ($retirements.Count -ne 1) { return $null }
        $retired = $retirements[0]
        if ([long]$retired.Sequence -le [long]$grant.Sequence -or -not (Equal $retired $grant $scope) -or
            (Field $retired 'settlement_outcome') -cne 'Superseded' -or
            (Field $retired 'successor_authority_id') -cne (Field $grant 'authority_id') -or
            (Number $retired 'repair_generation') -le 0 -or
            (Number $retired 'repair_generation') -ge (Number $grant 'repair_generation')) { return $null }
    }

    $currentFrontier = $frontier
    $currentHighest = $highest
    $currentEpoch = 0L
    $stateFrontier = $frontier
    $stateHighest = $highest
    $priorTicks = Number $stall 'log_writer_attempt_utc_ticks'
    foreach ($event in $ordered) {
        if ([long]$event.Sequence -le [long]$stall.Sequence -or [long]$event.Sequence -gt [long]$state.Sequence) { continue }
        $ticks = Number $event 'log_writer_attempt_utc_ticks'
        if ($ticks -lt $priorTicks) { return $null }
        $priorTicks = $ticks
        $name = Field $event 'event'
        if ($name -in @('file_transfer_inbound_terminal', 'filetransfer_live_route_epoch_terminal',
                'filetransfer_terminalized_by_session_end', 'filetransfer_data_session_recreated', 'filetransfer_data_session_removed') -or
            (Field $event 'transfer_paused') -ceq '1') { return $null }
        foreach ($fieldName in $completeScope) {
            if ($event.Fields.ContainsKey($fieldName) -and (Field $event $fieldName) -cne (Field $stall $fieldName)) { return $null }
        }
        if ($name -eq 'filetransfer_fallback_frontier_repair_authority_granted' -and $event -ne $grant) { return $null }
        if ($name -eq 'filetransfer_fallback_frontier_repair_contract_settled' -and
            (Field $event 'authority_id') -ceq (Field $grant 'authority_id')) { return $null }
        if ($event -eq $grant) { $currentHighest = Number $grant 'durable_received_highest_chunk_index' }
        if ($name -eq 'filetransfer_v6_frontier_request_deferred' -and
            (Field $event 'reason') -ceq 'chunk_batch_committed' -and
            (Number $event 'wake_generation') -gt (Number $stall 'wake_generation')) {
            $f = Number $event 'frontier_chunk_index'; $h = Number $event 'highest_received_chunk_index'
            if ($f -lt $currentFrontier -or $h -lt $currentHighest -or
                [long]$event.Sequence -lt [long]$send.Sequence) { return $null }
            $currentFrontier = $f; $currentHighest = $h
        }
        # Only current physical progress participates in monotonicity. Frozen
        # wakes and retired predecessor repair fields are historical evidence.
        if ($name -in @('filetransfer_v6_receiver_state_sent', 'filetransfer_v6_sparse_write_committed')) {
            $f = if ($name -eq 'filetransfer_v6_receiver_state_sent') { Number $event 'contiguous_committed_chunk_index' } else { Number $event 'next_chunk_index' }
            $h = if ($name -eq 'filetransfer_v6_receiver_state_sent') { Number $event 'durable_received_highest_chunk_index' } else { Number $event 'highest_received_chunk_index' }
            if ($f -lt $currentFrontier -or $h -lt $currentHighest -or
                ([long]$event.Sequence -lt [long]$send.Sequence -and $f -ne $frontier)) { return $null }
            $currentFrontier = $f; $currentHighest = $h
            if ($name -eq 'filetransfer_v6_receiver_state_sent') {
                $epoch = Number $event 'epoch'
                if ($epoch -le 0 -or $epoch -lt $currentEpoch -or (Field $event 'transfer_paused') -cne '0' -or
                    (Field $event 'transport_epoch') -cne (Field $stall 'transport_epoch') -or
                    ($epoch -eq $currentEpoch -and ($f -ne $stateFrontier -or $h -ne $stateHighest))) { return $null }
                $currentEpoch = $epoch
                $stateFrontier = $f; $stateHighest = $h
            }
        }
    }
    return $state
}

# A dispatched wake can be fulfilled by independent committed traffic. This
# closes only the stall obligation; it does not settle the repair authority.
function Get-FileTransferStallGraceDispatchedProgressClosure {
    param([object[]]$Events, [Parameter(Mandatory = $true)]$Episode,
        [switch]$DiagnosticDispatchEvidenceOnly)

    function Field($Event, [string]$Name) { return [string]$Event.Fields[$Name] }
    function Number($Event, [string]$Name) {
        $value = 0L
        if (-not [long]::TryParse((Field $Event $Name), [ref]$value)) { return -1L }
        return $value
    }
    function Ticks($Event, [string]$Name) {
        $value = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse((Field $Event $Name), [ref]$value)) { return 0L }
        return $value.UtcTicks
    }
    function Equal($Left, $Right, [string[]]$Names) {
        foreach ($name in $Names) {
            $value = Field $Left $name
            if ([string]::IsNullOrWhiteSpace($value) -or $value -cne (Field $Right $name)) { return $false }
        }
        return $true
    }
    $stall = $Episode.Event
    $scope = @('direction', 'route', 'protocol_version', 'live_route_epoch', 'runtime_generation',
        'terminal_generation', 'leg_generation', 'transport_epoch', 'bridge_recovery_generation',
        'bridge_decision_generation', 'checkpoint_request_id', 'route_publication_generation', 'route_commit_generation')
    $completeScope = $scope + @('checkpoint_generation', 'data_session_identity', 'data_session_generation')
    $wake = @('frontier_chunk_index', 'highest_received_chunk_index', 'wake_generation', 'stall_started_utc', 'wake_deadline_utc')
    $deadline = Ticks $stall 'wake_deadline_utc'
    if (-not (Equal $stall $stall ($completeScope + $wake)) -or
        (Field $stall 'direction') -cne 'inbound' -or (Field $stall 'route') -cne 'post_tuna_fallback_v6' -or
        (Field $stall 'protocol_version') -cne '6' -or (Number $stall 'wake_generation') -le 0 -or
        (Number $stall 'frontier_chunk_index') -lt 0 -or (Number $stall 'data_session_generation') -le 0 -or
        (Field $stall 'data_session_identity') -in @('', '(none)', 'none') -or
        (Number $stall 'stall_grace_ms') -ne 750 -or (Ticks $stall 'stall_started_utc') -le 0 -or
        $deadline -ne ((Ticks $stall 'stall_started_utc') + 7500000L)) { return $null }

    $buckets = @{}
    $seen = @{}
    $conflicts = [Collections.Generic.List[long]]::new()
    $scoped = [Collections.Generic.List[object]]::new()
    foreach ($event in $Events) {
        if ([long]$event.Sequence -le [long]$stall.Sequence -or
            -not (Equal $event $stall @('log_writer_id', 'session_id', 'transfer_id'))) { continue }
        $key = Field $event 'log_writer_sequence'
        if ($key -notmatch '^[1-9][0-9]*$') { continue }
        if ($seen.ContainsKey($key)) {
            $prior = $seen[$key]
            $names = @($event.Fields.Keys | Where-Object { $_ -ne 'gui_evidence_sequence' })
            $same = $names.Count -eq @($prior.Fields.Keys | Where-Object { $_ -ne 'gui_evidence_sequence' }).Count
            foreach ($name in $names) {
                if ((Field $event $name) -cne (Field $prior $name)) { $same = $false; break }
            }
            if (-not $same) { $conflicts.Add([long]$event.Sequence) }
            continue
        }
        $seen[$key] = $event
        $scoped.Add($event)
        $name = Field $event 'event'
        if (-not $buckets.ContainsKey($name)) { $buckets[$name] = [Collections.Generic.List[object]]::new() }
        $buckets[$name].Add($event)
    }
    function Named([string]$Name) { if ($buckets.ContainsKey($Name)) { return $buckets[$Name].ToArray() } }
    $rechecks = @(Named 'filetransfer_v6_frontier_stall_grace_recheck_decided' | Where-Object {
        (Field $_ 'wake_generation') -ceq (Field $stall 'wake_generation')
    })
    $rearms = @($rechecks | Where-Object { (Field $_ 'decision_status') -ceq 'rearmed' } | Sort-Object Sequence)
    $rechecks = @($rechecks | Where-Object { (Field $_ 'decision_status') -cne 'rearmed' })
    if ($rechecks.Count -ne 1) { return $null }
    $recheck = $rechecks[0]
    if (-not (Equal $recheck $stall ($completeScope + $wake)) -or
        (Field $recheck 'decision_status') -cne 'dispatched' -or
        (Field $recheck 'decision_reason') -cne 'exact_receiver_request_dispatched' -or
        (Field $recheck 'authority_effect') -cne 'none' -or
        (Number $recheck 'log_writer_attempt_utc_ticks') -lt $deadline) { return $null }

    $grants = @(Named 'filetransfer_fallback_frontier_repair_authority_granted' | Where-Object {
        [long]$_.Sequence -lt [long]$recheck.Sequence -and
        (Number $_ 'log_writer_attempt_utc_ticks') -ge $deadline -and
        (Equal $_ $stall ($scope + @('frontier_chunk_index'))) -and
        (Field $_ 'source_kind') -ceq 'ReceiverRequest'
    })
    if ($grants.Count -ne 1) { return $null }
    $grant = $grants[0]
    # An early timer may re-arm the same wake before its one real dispatch.
    # These no-effect records do not supply grant, delivery or dispatch proof.
    # Writer time can cross the deadline after the timer checked it, so bind
    # their unchanged scope and order to the actual grant instead.
    $previousRearmTicks = Number $stall 'log_writer_attempt_utc_ticks'
    foreach ($rearm in $rearms) {
        $rearmTicks = Number $rearm 'log_writer_attempt_utc_ticks'
        if (-not (Equal $rearm $stall ($completeScope + $wake)) -or
            (Field $rearm 'authority_effect') -cne 'none' -or
            (Field $rearm 'decision_reason') -cne 'stall_grace_deadline_pending' -or
            [long]$rearm.Sequence -ge [long]$grant.Sequence -or
            $rearmTicks -lt $previousRearmTicks -or
            $rearmTicks -gt (Number $grant 'log_writer_attempt_utc_ticks')) { return $null }
        $previousRearmTicks = $rearmTicks
    }
    foreach ($name in @('authority_id', 'repair_request_key', 'range_fingerprint',
            'primary_protocol_repair_request_id', 'primary_request_correlation_id')) {
        if ((Field $grant $name) -in @('', '(none)', 'none')) { return $null }
    }
    if ((Number $grant 'repair_generation') -le 0 -or (Field $grant 'target_transport') -cne 'regular_nkn' -or
        (Number $grant 'durable_received_highest_chunk_index') -ne (Number $stall 'highest_received_chunk_index')) { return $null }
    $repair = $scope + @('authority_id', 'repair_generation', 'repair_request_key', 'range_fingerprint',
        'primary_protocol_repair_request_id', 'primary_request_correlation_id', 'frontier_chunk_index',
        'durable_received_highest_chunk_index', 'source_kind', 'target_transport', 'predecessor_authority_id')
    function AfterGrant($Event) {
        return [long]$Event.Sequence -gt [long]$grant.Sequence -and [long]$Event.Sequence -lt [long]$recheck.Sequence
    }
    # Count subjects before validating outcomes, so a conflicting copy cannot
    # disappear merely because its receipt/dispatch status is unsuccessful.
    foreach ($name in @('filetransfer_control_plane_delivery_observed', 'filetransfer_fallback_frontier_repair_dispatched',
            'filetransfer_v6_frontier_request_sent')) {
        $subjects = @(Named $name | Where-Object {
            (AfterGrant $_) -and $(if ($name -eq 'filetransfer_control_plane_delivery_observed') {
                (Field $_ 'repair_request_id') -ceq (Field $grant 'primary_protocol_repair_request_id') -or
                    (Field $_ 'request_correlation_id') -ceq (Field $grant 'primary_request_correlation_id')
            } else { (Field $_ 'authority_id') -ceq (Field $grant 'authority_id') })
        })
        if ($subjects.Count -ne 1) { return $null }
    }
    $deliveries = @(Named 'filetransfer_control_plane_delivery_observed' | Where-Object {
        (AfterGrant $_) -and (Equal $_ $grant @('route', 'protocol_version', 'direction', 'live_route_epoch',
            'leg_generation', 'transport_epoch', 'bridge_recovery_generation', 'checkpoint_request_id',
            'repair_request_key', 'range_fingerprint')) -and
        (Field $_ 'repair_request_id') -ceq (Field $grant 'primary_protocol_repair_request_id') -and
        (Field $_ 'request_correlation_id') -ceq (Field $grant 'primary_request_correlation_id') -and
        (Field $_ 'kind') -ceq 'frontier_request' -and (Field $_ 'frame_type') -ceq 'filetransfer.frontier_request.v6' -and
        (Field $_ 'accepted_any') -ceq '1' -and (Field $_ 'peer_visible_any') -ceq '1'
    })
    $dispatches = @(Named 'filetransfer_fallback_frontier_repair_dispatched' | Where-Object {
        (AfterGrant $_) -and (Equal $_ $grant $repair) -and (Field $_ 'dispatch_observed') -ceq '1'
    })
    $sends = @(Named 'filetransfer_v6_frontier_request_sent' | Where-Object {
        (AfterGrant $_) -and (Equal $_ $grant $repair) -and
        (Field $_ 'repair_request_id') -ceq (Field $grant 'primary_protocol_repair_request_id') -and
        (Field $_ 'request_correlation_id') -ceq (Field $grant 'primary_request_correlation_id') -and
        (Field $_ 'reason') -ceq 'frontier_stall_grace_elapsed'
    })
    if ($deliveries.Count -ne 1 -or $dispatches.Count -ne 1 -or $sends.Count -ne 1 -or
        [long]$deliveries[0].Sequence -ge [long]$dispatches[0].Sequence -or
        [long]$dispatches[0].Sequence -ge [long]$sends[0].Sequence) { return $null }
    $priorTicks = Number $grant 'log_writer_attempt_utc_ticks'
    foreach ($event in @($deliveries[0], $dispatches[0], $sends[0], $recheck)) {
        $ticks = Number $event 'log_writer_attempt_utc_ticks'
        if ($ticks -lt $priorTicks) { return $null }
        $priorTicks = $ticks
    }

    if ($DiagnosticDispatchEvidenceOnly) {
        return [pscustomobject]@{ Grant = $grant; Wake = $recheck; Events = @($scoped)
            Conflicts = @($conflicts); ScopeFields = $completeScope; RepairFields = $repair }
    }

    $advances = @(Named 'filetransfer_v6_frontier_request_deferred' | Where-Object {
        [long]$_.Sequence -gt [long]$recheck.Sequence -and (Equal $_ $stall $completeScope) -and
        (Field $_ 'reason') -ceq 'chunk_batch_committed' -and (Field $_ 'authority_effect') -ceq 'none' -and
        (Number $_ 'elapsed_ms') -eq 0 -and (Number $_ 'stall_grace_ms') -eq 750 -and
        (Number $_ 'frontier_chunk_index') -gt (Number $stall 'frontier_chunk_index') -and
        (Number $_ 'highest_received_chunk_index') -ge (Number $stall 'highest_received_chunk_index') -and
        (Number $_ 'wake_generation') -gt (Number $stall 'wake_generation') -and
        (Ticks $_ 'stall_started_utc') -ge (Number $recheck 'log_writer_attempt_utc_ticks') -and
        (Ticks $_ 'wake_deadline_utc') -eq ((Ticks $_ 'stall_started_utc') + 7500000L)
    } | Sort-Object Sequence)
    foreach ($advance in $advances) {
        $states = @(Named 'filetransfer_v6_receiver_state_sent' | Where-Object {
            [long]$_.Sequence -gt [long]$advance.Sequence -and
            (Number $_ 'log_writer_attempt_utc_ticks') -ge (Ticks $advance 'stall_started_utc') -and
            (Number $_ 'contiguous_committed_chunk_index') -ge (Number $advance 'frontier_chunk_index') -and
            (Number $_ 'durable_received_highest_chunk_index') -ge (Number $advance 'highest_received_chunk_index') -and
            (Field $_ 'transport_epoch') -ceq (Field $stall 'transport_epoch') -and
            (Field $_ 'transfer_paused') -ceq '0' -and (Number $_ 'epoch') -gt 0
        } | Sort-Object Sequence)
        if ($states.Count -eq 0) { continue }
        $state = $states[0]
        if (@($conflicts | Where-Object { $_ -le [long]$state.Sequence }).Count -gt 0) { return $null }
        foreach ($event in $scoped) {
            if ([long]$event.Sequence -gt [long]$state.Sequence) { continue }
            if ((Field $event 'event') -in @('file_transfer_inbound_terminal', 'filetransfer_live_route_epoch_terminal',
                    'filetransfer_terminalized_by_session_end', 'filetransfer_data_session_recreated', 'filetransfer_data_session_removed') -or
                (Field $event 'transfer_paused') -ceq '1') { return $null }
            # Scope-poor physical records corroborate this exact full-scope
            # interval; they cannot supply or replace its authority fields.
            foreach ($name in $completeScope) {
                if ($event.Fields.ContainsKey($name) -and (Field $event $name) -cne (Field $stall $name)) { return $null }
            }
            if ((Field $event 'event') -eq 'filetransfer_fallback_frontier_repair_contract_settled' -and
                (Field $event 'authority_id') -ceq (Field $grant 'authority_id')) { return $null }
            if ((Field $event 'event') -eq 'filetransfer_v6_receiver_state_sent' -and
                (Number $event 'contiguous_committed_chunk_index') -lt (Number $stall 'frontier_chunk_index')) { return $null }
        }
        return $state
    }
    return $null
}

# A fulfilled recent-progress wake transfers the obligation to the ordinary
# stall grace. Natural progress can close that grace without repair authority,
# but a sparse write alone is not publication proof.
function Get-FileTransferRecentProgressCommitClockClosure {
    param([object[]]$Events, [Parameter(Mandatory = $true)]$Episode)

    function Field($Event, [string]$Name) { return [string]$Event.Fields[$Name] }
    function Number($Event, [string]$Name) {
        $value = 0L
        if (-not [long]::TryParse((Field $Event $Name), [ref]$value)) { return -1L }
        return $value
    }
    function Ticks($Event, [string]$Name) {
        $value = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse((Field $Event $Name), [ref]$value)) { return 0L }
        return $value.UtcTicks
    }
    function Equal($Left, $Right, [string[]]$Names) {
        foreach ($name in $Names) {
            $value = Field $Left $name
            if ([string]::IsNullOrWhiteSpace($value) -or $value -cne (Field $Right $name)) { return $false }
        }
        return $true
    }
    # This narrow branch covers one unchanged, pre-diagnostic wake only.
    # A stale callback or a later clock cannot supply closure authority.
    $scoped = @($Events | Where-Object {
        (Field $_ 'log_writer_id') -ceq $Episode.Writer -and
        (Field $_ 'session_id') -ceq $Episode.Session -and (Field $_ 'transfer_id') -ceq $Episode.Transfer
    } | Sort-Object Sequence, LineNumber)
    $commits = @($scoped | Where-Object {
        [long]$_.Sequence -gt [long]$Episode.Event.Sequence -and
        (Field $_ 'event') -ceq 'filetransfer_v6_sparse_write_committed' -and
        ($_.Fields.ContainsKey('commit_decision_utc') -or $_.Fields.ContainsKey('commit_source_status'))
    } | Select-Object -First 1)
    if ($commits.Count -ne 1) { return $null }
    $commit = $commits[0]
    $arms = @($scoped | Where-Object {
        [long]$_.Sequence -lt [long]$commit.Sequence -and
        (Number $_ 'frontier_chunk_index') -eq $Episode.Frontier -and
        (Field $_ 'event') -ceq 'filetransfer_v6_frontier_recent_progress_grace_deferred'
    } | Select-Object -Last 1)
    if ($arms.Count -ne 1 -or [long]$arms[0].Sequence -gt [long]$Episode.Event.Sequence) { return $null }
    $arm = $arms[0]
    $scope = @('direction', 'route', 'protocol_version', 'live_route_epoch', 'runtime_generation',
        'terminal_generation', 'leg_generation', 'transport_epoch', 'bridge_recovery_generation',
        'bridge_decision_generation', 'checkpoint_request_id', 'checkpoint_generation',
        'route_publication_generation', 'route_commit_generation', 'frontier_scope_session_id',
        'frontier_scope_transfer_id', 'frontier_scope_data_session_identity')
    foreach ($name in $scope) {
        $expected = Field $Episode.Progress $name
        if (-not [string]::IsNullOrWhiteSpace($expected) -and $expected -cne (Field $arm $name)) { return $null }
    }
    if (-not (Equal $arm $commit ($scope + @('data_session_identity', 'data_session_generation',
        'frontier_chunk_index', 'highest_received_chunk_index', 'wake_generation', 'committed_progress_utc', 'wake_deadline_utc'))) -or
        (Field $commit 'commit_source_status') -cne 'current' -or (Field $commit 'authority_effect') -cne 'none' -or
        (Field $arm 'authority_effect') -cne 'none' -or (Field $arm 'decision_status') -cnotin @('held', 'rearmed') -or
        (Field $arm 'data_session_identity') -cne $Episode.DataSessionIdentity -or
        (Field $arm 'data_session_generation') -cne $Episode.DataSessionGeneration -or
        (Number $arm 'wake_generation') -le 0 -or (Number $arm 'frontier_chunk_index') -ne $Episode.Frontier -or
        (Number $arm 'highest_received_chunk_index') -lt $Episode.Highest -or
        (Number $commit 'contiguous_chunks_committed') -le 0 -or
        (Number $commit 'next_chunk_index') -ne ($Episode.Frontier + (Number $commit 'contiguous_chunks_committed'))) { return $null }
    $progress = Ticks $arm 'committed_progress_utc'
    $deadline = Ticks $arm 'wake_deadline_utc'
    $decision = Ticks $commit 'commit_decision_utc'
    $armDecision = Ticks $arm 'arm_decision_utc'
    if ($progress -le 0 -or $deadline -ne ($progress + [TimeSpan]::TicksPerSecond) -or
        $Episode.DueTicks -le 0 -or $deadline -gt $Episode.DueTicks -or
        $armDecision -lt $progress -or $armDecision -ge $deadline -or
        $armDecision -gt (Number $arm 'log_writer_attempt_utc_ticks') -or
        (Number $Episode.Event 'log_writer_attempt_utc_ticks') -ge $deadline -or
        $decision -lt (Number $Episode.Event 'log_writer_attempt_utc_ticks') -or $decision -gt $deadline -or
        $decision -gt (Number $commit 'log_writer_attempt_utc_ticks')) { return $null }
    # Do not hide conflicting copies behind a changed transfer identity.
    foreach ($event in $Events) {
        if ((Field $event 'log_writer_id') -cne $Episode.Writer) { continue }
        foreach ($selected in @($arm, $commit)) {
            if ((Field $event 'log_writer_sequence') -cne (Field $selected 'log_writer_sequence')) { continue }
            $names = @($selected.Fields.Keys | Where-Object { $_ -ne 'gui_evidence_sequence' })
            if ($names.Count -ne @($event.Fields.Keys | Where-Object { $_ -ne 'gui_evidence_sequence' }).Count -or
                -not (Equal $selected $event $names)) { return $null }
        }
    }
    $intervening = @($scoped | Where-Object {
        [long]$_.Sequence -gt [long]$arm.Sequence -and [long]$_.Sequence -lt [long]$commit.Sequence -and
        ((Field $_ 'event') -in @('file_transfer_inbound_terminal', 'filetransfer_live_route_epoch_terminal',
            'filetransfer_terminalized_by_session_end', 'filetransfer_route_selected', 'filetransfer_live_route_epoch_recovered',
            'filetransfer_route_hint_authorized_publication_accepted', 'filetransfer_data_session_recreated', 'filetransfer_data_session_removed') -or
            (Field $_ 'transfer_paused') -eq '1')
    })
    if ($intervening.Count -gt 0) { return $null }
    return $commit
}

function Get-FileTransferRecentProgressGraceStallProgressClosure {
    param([object[]]$Events, [Parameter(Mandatory = $true)]$Episode)

    function Field($Event, [string]$Name) { return [string]$Event.Fields[$Name] }
    function Number($Event, [string]$Name) {
        $value = 0L
        if (-not [long]::TryParse((Field $Event $Name), [ref]$value)) { return -1L }
        return $value
    }
    function Ticks($Event, [string]$Name) {
        $value = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse((Field $Event $Name), [ref]$value)) { return 0L }
        return $value.UtcTicks
    }
    function ArmTicks($Event) {
        # New records bind the clock used by the runtime arm guard to this exact
        # source/wake. Historical records retain the stricter writer-time rule.
        if ($Event.Fields.ContainsKey('arm_decision_utc')) { return Ticks $Event 'arm_decision_utc' }
        return Number $Event 'log_writer_attempt_utc_ticks'
    }
    function Equal($Left, $Right, [string[]]$Names) {
        foreach ($name in $Names) {
            $value = Field $Left $name
            if ([string]::IsNullOrWhiteSpace($value) -or $value -cne (Field $Right $name)) { return $false }
        }
        return $true
    }
    $scope = @('direction', 'route', 'protocol_version', 'live_route_epoch', 'runtime_generation',
        'terminal_generation', 'leg_generation', 'transport_epoch', 'bridge_recovery_generation',
        'bridge_decision_generation', 'checkpoint_request_id', 'route_publication_generation', 'route_commit_generation')
    $wakeScope = $scope + @('checkpoint_generation', 'data_session_identity', 'data_session_generation', 'frontier_chunk_index')
    $scopeEvents = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in Get-FileTransferFallbackFrontierGraceCorrelationEventNames) { [void]$scopeEvents.Add($name) }
    $buckets = @{}
    $seen = @{}
    $scoped = [Collections.Generic.List[object]]::new()
    foreach ($event in $Events) {
        if ([long]$event.Sequence -le [long]$Episode.Progress.Sequence -or
            (Field $event 'log_writer_id') -cne $Episode.Writer) { continue }
        $sameTransfer = (Field $event 'session_id') -ceq $Episode.Session -and
            (Field $event 'transfer_id') -ceq $Episode.Transfer
        $key = Field $event 'log_writer_sequence'
        if ($key -notmatch '^[1-9][0-9]*$') { continue }
        if ($seen.ContainsKey($key)) {
            $prior = $seen[$key]
            $names = @($event.Fields.Keys | Where-Object { $_ -ne 'gui_evidence_sequence' })
            if ($names.Count -ne @($prior.Fields.Keys | Where-Object { $_ -ne 'gui_evidence_sequence' }).Count -or
                -not (Equal $event $prior $names)) {
                if ($sameTransfer -or ((Field $prior 'session_id') -ceq $Episode.Session -and
                    (Field $prior 'transfer_id') -ceq $Episode.Transfer)) { return $null }
            }
            continue
        }
        $seen[$key] = $event
        # A conflicting copy must not disappear behind its altered identity.
        if (-not $sameTransfer) { continue }
        $scoped.Add($event)
        $name = Field $event 'event'
        if (-not $buckets.ContainsKey($name)) { $buckets[$name] = [Collections.Generic.List[object]]::new() }
        $buckets[$name].Add($event)
    }
    function Named([string]$Name) { if ($buckets.ContainsKey($Name)) { return $buckets[$Name].ToArray() } }
    $rechecks = @(Named 'filetransfer_v6_frontier_recent_progress_grace_recheck_decided' | Where-Object {
        [long]$_.Sequence -gt [long]$Episode.Event.Sequence -and
        (Equal $_ $Episode.Progress $scope) -and (Number $_ 'frontier_chunk_index') -eq $Episode.Frontier -and
        (Field $_ 'data_session_identity') -ceq $Episode.DataSessionIdentity -and
        (Field $_ 'data_session_generation') -ceq $Episode.DataSessionGeneration -and
        (Field $_ 'decision_status') -ceq 'rechecked' -and (Field $_ 'decision_reason') -ceq 'stall_grace_armed' -and
        (Field $_ 'authority_effect') -ceq 'none' -and (Number $_ 'wake_generation') -gt 0 -and
        (Ticks $_ 'committed_progress_utc') -gt 0 -and
        (Ticks $_ 'wake_deadline_utc') -eq ((Ticks $_ 'committed_progress_utc') + [TimeSpan]::TicksPerSecond) -and
        (Number $_ 'log_writer_attempt_utc_ticks') -ge (Ticks $_ 'wake_deadline_utc')
    })
    if ($rechecks.Count -ne 1) { return $null }
    $recheck = $rechecks[0]
    $arms = @(Named 'filetransfer_v6_frontier_recent_progress_grace_deferred' | Where-Object {
        [long]$_.Sequence -le [long]$Episode.Event.Sequence -and
        (Equal $_ $recheck ($wakeScope + @('wake_generation', 'committed_progress_utc', 'wake_deadline_utc'))) -and
        (Field $_ 'decision_status') -cin @('held', 'rearmed') -and (Field $_ 'authority_effect') -ceq 'none' -and
        (Number $_ 'highest_received_chunk_index') -ge (Number $Episode.Event 'highest_received_chunk_index') -and
        (Number $_ 'highest_received_chunk_index') -le (Number $recheck 'highest_received_chunk_index') -and
        (Number $Episode.Event 'log_writer_attempt_utc_ticks') -lt (Ticks $_ 'wake_deadline_utc') -and
        (ArmTicks $_) -ge (Ticks $_ 'committed_progress_utc') -and
        (ArmTicks $_) -le (Number $_ 'log_writer_attempt_utc_ticks') -and
        (ArmTicks $_) -lt (Ticks $_ 'wake_deadline_utc')
    })
    if ($arms.Count -gt 1) { return $null }
    $recentArms = @(Named 'filetransfer_v6_frontier_recent_progress_grace_deferred' | Where-Object {
        [long]$_.Sequence -lt [long]$recheck.Sequence
    } | Sort-Object Sequence, LineNumber)
    $links = @($recentArms | Where-Object { [long]$_.Sequence -gt [long]$Episode.Event.Sequence })
    if ($arms.Count -eq 0 -or $links.Count -gt 0) {
        # Select before validating: a contradictory latest arm cannot be skipped
        # in favor of an earlier convenient generation or scope.
        $roots = @($recentArms | Where-Object { [long]$_.Sequence -le [long]$Episode.Event.Sequence } | Select-Object -Last 1)
        if ($roots.Count -ne 1) { return $null }
        $root = $roots[0]
        $chainScope = $wakeScope + @('committed_progress_utc', 'wake_deadline_utc')
        $recentDeadline = Ticks $recheck 'wake_deadline_utc'
        $candidateTicks = Number $Episode.Event 'log_writer_attempt_utc_ticks'
        $rootTicks = Number $root 'log_writer_attempt_utc_ticks'
        $rootDecisionTicks = ArmTicks $root
        $decisionClock = $root.Fields.ContainsKey('arm_decision_utc')
        $generation = Number $root 'wake_generation'
        $highest = Number $root 'highest_received_chunk_index'
        if (-not (Equal $root $recheck $chainScope) -or
            (Field $root 'decision_status') -cnotin @('held', 'rearmed') -or (Field $root 'authority_effect') -cne 'none' -or
            $generation -le 0 -or $generation -ge (Number $recheck 'wake_generation') -or
            $highest -lt (Number $Episode.Event 'highest_received_chunk_index') -or
            $highest -gt (Number $recheck 'highest_received_chunk_index') -or
            $rootTicks -lt (Ticks $root 'committed_progress_utc') -or $rootTicks -gt $candidateTicks -or
            $rootDecisionTicks -lt (Ticks $root 'committed_progress_utc') -or
            $rootDecisionTicks -gt $rootTicks -or $rootDecisionTicks -ge $recentDeadline -or
            $candidateTicks -ge $recentDeadline) { return $null }
        if ($links.Count -eq 0) { return $null }
        $previousTicks = $candidateTicks
        foreach ($link in $links) {
            $nextGeneration = Number $link 'wake_generation'
            $nextHighest = Number $link 'highest_received_chunk_index'
            $nextTicks = Number $link 'log_writer_attempt_utc_ticks'
            $nextDecisionTicks = ArmTicks $link
            if (-not (Equal $link $root $chainScope) -or
                (Field $link 'decision_status') -cne 'rearmed' -or
                (Field $link 'decision_reason') -cne 'highest_received_advanced' -or (Field $link 'authority_effect') -cne 'none' -or
                $nextGeneration -le 0 -or ($nextGeneration - $generation) -ne 1 -or $nextHighest -le $highest -or
                $link.Fields.ContainsKey('arm_decision_utc') -ne $decisionClock -or
                $nextTicks -lt $previousTicks -or $nextDecisionTicks -lt $previousTicks -or
                $nextDecisionTicks -gt $nextTicks -or $nextDecisionTicks -ge $recentDeadline) { return $null }
            $generation = $nextGeneration
            $highest = $nextHighest
            $previousTicks = $nextTicks
        }
        if (-not (Equal $links[-1] $recheck ($chainScope + @('wake_generation', 'highest_received_chunk_index')))) { return $null }
    }
    $stalls = @(Named 'filetransfer_v6_frontier_request_deferred' | Where-Object {
        [long]$_.Sequence -gt [long]$Episode.Event.Sequence -and (Equal $_ $recheck $wakeScope) -and
        (Field $_ 'reason') -cin @('frontier_recent_progress_grace_elapsed', 'frontier_stall_repair_due') -and
        (Field $_ 'authority_effect') -ceq 'none' -and (Number $_ 'wake_generation') -gt 0 -and
        (Number $_ 'elapsed_ms') -eq 0 -and (Number $_ 'stall_grace_ms') -eq 750 -and
        (Number $_ 'highest_received_chunk_index') -ge (Number $recheck 'highest_received_chunk_index') -and
        (Ticks $_ 'stall_started_utc') -ge (Ticks $recheck 'wake_deadline_utc') -and
        (Ticks $_ 'wake_deadline_utc') -eq ((Ticks $_ 'stall_started_utc') + 7500000L)
    })
    if ($stalls.Count -ne 1) { return $null }
    $stall = $stalls[0]
    $deadline = Ticks $stall 'wake_deadline_utc'
    $watchdogFirst = (Field $stall 'reason') -ceq 'frontier_stall_repair_due'
    if ($watchdogFirst) {
        # The watchdog can arm the current stall before the recent callback.
        # Its origin is not authority: require the exact, still-current interval.
        $stallTicks = Number $stall 'log_writer_attempt_utc_ticks'
        $recheckTicks = Number $recheck 'log_writer_attempt_utc_ticks'
        if ([long]$stall.Sequence -ge [long]$recheck.Sequence -or
            $stallTicks -lt (Ticks $stall 'stall_started_utc') -or $stallTicks -gt $recheckTicks -or
            $recheckTicks -ge $deadline -or
            (Number $stall 'highest_received_chunk_index') -ne (Number $recheck 'highest_received_chunk_index')) { return $null }
    }
    $writes = @(Named 'filetransfer_v6_sparse_write_committed' | Where-Object {
        [long]$_.Sequence -gt [Math]::Max([long]$stall.Sequence, [long]$recheck.Sequence)
    })
    $initialStates = @(Named 'filetransfer_v6_receiver_state_sent' | Where-Object {
        [long]$_.Sequence -lt [long]$Episode.Event.Sequence -and
        (Number $_ 'contiguous_committed_chunk_index') -eq $Episode.Frontier -and
        (Field $_ 'transport_epoch') -ceq (Field $stall 'transport_epoch') -and (Number $_ 'epoch') -gt 0
    })
    if ($initialStates.Count -eq 0) { return $null }
    $frontier = [long]$Episode.Frontier
    $highest = Number $stall 'highest_received_chunk_index'
    $writeTicks = Number $recheck 'log_writer_attempt_utc_ticks'
    foreach ($write in $writes) {
        $next = Number $write 'next_chunk_index'
        $count = Number $write 'contiguous_chunks_committed'
        $nextHighest = Number $write 'highest_received_chunk_index'
        $nextTicks = Number $write 'log_writer_attempt_utc_ticks'
        # Receiver publication may coalesce several writes. Each link must
        # account for the entire advance within the same bounded wake interval.
        if ($count -lt 0 -or $next -lt $frontier -or ($next - $frontier) -ne $count -or
            $nextHighest -lt $highest -or $nextTicks -lt $writeTicks -or
            $nextTicks -le (Number $recheck 'log_writer_attempt_utc_ticks') -or $nextTicks -ge $deadline) { return $null }
        $frontier = $next
        $highest = $nextHighest
        $writeTicks = $nextTicks
        if ($frontier -le $Episode.Frontier) { continue }
        $states = @(Named 'filetransfer_v6_receiver_state_sent' | Where-Object {
            [long]$_.Sequence -gt [long]$write.Sequence -and
            (Number $_ 'log_writer_attempt_utc_ticks') -ge (Number $write 'log_writer_attempt_utc_ticks') -and
            (Number $_ 'log_writer_attempt_utc_ticks') -lt $deadline -and
            (Number $_ 'contiguous_committed_chunk_index') -eq (Number $write 'next_chunk_index') -and
            (Number $_ 'durable_received_highest_chunk_index') -eq (Number $write 'highest_received_chunk_index') -and
            (Field $_ 'transport_epoch') -ceq (Field $stall 'transport_epoch') -and
            (Field $_ 'transfer_paused') -ceq '0' -and (Number $_ 'epoch') -gt 0
        })
        if ($states.Count -ne 1) { continue }
        $state = $states[0]
        if (@($initialStates | Where-Object { (Number $_ 'epoch') -ge (Number $state 'epoch') }).Count -gt 0) { continue }
        $poisoned = $false
        foreach ($event in $scoped) {
            if ([long]$event.Sequence -gt [long]$state.Sequence) { continue }
            if ($watchdogFirst -and [long]$event.Sequence -gt [long]$Episode.Event.Sequence -and
                (Field $event 'event') -ceq 'filetransfer_v6_frontier_request_deferred' -and
                (Number $event 'elapsed_ms') -eq 0 -and -not [object]::ReferenceEquals($event, $stall)) {
                # Do not skip a competing/replacement root because its reason
                # or scope failed the candidate filter above.
                $poisoned = $true; break
            }
            if ((Field $event 'event') -in @('file_transfer_inbound_terminal', 'filetransfer_live_route_epoch_terminal',
                    'filetransfer_terminalized_by_session_end', 'filetransfer_data_session_recreated', 'filetransfer_data_session_removed') -or
                (Field $event 'transfer_paused') -ceq '1') { $poisoned = $true; break }
            # Only contract evidence carries owner scope. Transport diagnostics
            # can expose frame transport_epoch, not the fallback owner's epoch.
            if (-not $scopeEvents.Contains((Field $event 'event'))) { continue }
            # Sparse/state records are scope-poor. They inherit only this one
            # fully bound wake interval; every present scope field must agree.
            foreach ($name in $wakeScope | Where-Object { $_ -ne 'frontier_chunk_index' }) {
                if ($event.Fields.ContainsKey($name) -and (Field $event $name) -cne (Field $stall $name)) { $poisoned = $true; break }
            }
            if ($poisoned) { break }
        }
        if (-not $poisoned) { return $state }
    }
    return $null
}

# This describes an unresolved request; source supersession is not recovery.
function Get-FileTransferStallGraceUnresolvedDiagnostic {
    param([hashtable]$Index, $Candidate)
    function F($Event, [string]$Name) { return [string]$Event.Fields[$Name] }
    function N($Event, [string]$Name) {
        $value = 0L
        if (-not [long]::TryParse((F $Event $Name), [ref]$value)) { return -1L }
        return $value
    }
    function P($Object, [string]$Name, $Default = $null) {
        if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) { return $Default }
        return $Object.$Name
    }
    function Equal($Left, $Right, [string[]]$Names) {
        foreach ($name in $Names) {
            if ([string]::IsNullOrWhiteSpace((F $Left $name)) -or (F $Left $name) -cne (F $Right $name)) { return $false }
        }
        return $true
    }
    function Project([string]$Raw) {
        if ($Raw -cnotmatch '^fta2-filetransfer_route_transition-[0-9a-f]{24}$') { return '' }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Raw))).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
        return 'fte1-' + $hash.Substring(0, 24)
    }
    if ($null -eq $Index -or $null -eq $Candidate -or
        (F $Candidate 'event') -cne 'filetransfer_v6_frontier_request_deferred' -or
        (F $Candidate 'authority_effect') -cne 'none' -or (N $Candidate 'elapsed_ms') -ne 0 -or
        (F $Candidate 'log_writer_sequence') -cnotmatch '^[1-9][0-9]*$') { return $null }
    $key = '{0}|{1}|{2}' -f (F $Candidate 'log_writer_id'), (F $Candidate 'session_id'), (F $Candidate 'transfer_id')
    if (-not $Index.ContainsKey($key) -or -not $Index.ContainsKey('__conflicts') -or
        $Index['__conflicts'].ContainsKey("$(F $Candidate 'log_writer_id')|$(F $Candidate 'log_writer_sequence')")) { return $null }
    $dispatch = Get-FileTransferStallGraceDispatchedProgressClosure -Events $Index[$key] `
        -Episode ([pscustomobject]@{ Event = $Candidate }) -DiagnosticDispatchEvidenceOnly
    if ($null -eq $dispatch) { return $null }
    $grant = $dispatch.Grant; $wake = $dispatch.Wake; $events = @($dispatch.Events)
    $scope = @($dispatch.ScopeFields); $repair = @($dispatch.RepairFields)
    if ((F $grant 'authority_schema') -cne '2' -or
        (F $grant 'authority_id') -cnotmatch '^fta2-repair_replay-[0-9a-f]{24}$' -or
        (F $grant 'range_fingerprint') -cnotmatch '^frng-[0-9a-f]{24}$' -or
        (N $grant 'requested_chunk_count') -le 0 -or (N $grant 'range_count') -le 0 -or
        (F $grant 'repair_request_key') -cne "fallback-frontier:$(F $grant 'frontier_chunk_index'):$(F $grant 'requested_chunk_count'):$(F $grant 'range_fingerprint')") { return $null }
    $settlements = @($events | Where-Object {
        (F $_ 'event') -ceq 'filetransfer_fallback_frontier_repair_contract_settled' -and
        (F $_ 'authority_id') -ceq (F $grant 'authority_id')
    })
    if ($settlements.Count -ne 1) { return $null }
    $settlement = $settlements[0]
    if ([long]$settlement.Sequence -le [long]$wake.Sequence -or -not (Equal $settlement $grant ($repair + @('authority_schema'))) -or
        (F $settlement 'settlement_outcome') -cne 'SettledStalled' -or (F $settlement 'dispatch_observed') -cne '1' -or
        (F $settlement 'successor_authority_id') -cne '(none)' -or
        (F $settlement 'protocol_repair_request_id') -cne (F $grant 'primary_protocol_repair_request_id') -or
        (F $settlement 'request_correlation_id') -cne (F $grant 'primary_request_correlation_id') -or
        (N $settlement 'log_writer_attempt_utc_ticks') -lt (N $wake 'log_writer_attempt_utc_ticks')) { return $null }
    $scopeEvents = @('filetransfer_v6_frontier_request_deferred', 'filetransfer_v6_frontier_stall_grace_recheck_decided',
        'filetransfer_fallback_frontier_repair_authority_granted', 'filetransfer_fallback_frontier_repair_contract_settled',
        'filetransfer_fallback_frontier_repair_progress_observed', 'filetransfer_fallback_frontier_repair_dispatched',
        'filetransfer_v6_frontier_request_sent', 'filetransfer_control_plane_delivery_observed',
        'filetransfer_v6_receiver_state_sent', 'filetransfer_v6_sparse_write_committed',
        'filetransfer_route_selected', 'filetransfer_live_route_epoch_recovered', 'filetransfer_route_hint_authorized_publication_accepted')
    function Poisoned($End, $Commit = $null) {
        foreach ($event in $events) {
            if ([long]$event.Sequence -gt [long]$End.Sequence) { continue }
            $writerKey = "$(F $event 'log_writer_id')|$(F $event 'log_writer_sequence')"
            if ((F $event 'log_writer_sequence') -cnotmatch '^[1-9][0-9]*$' -or $Index['__conflicts'].ContainsKey($writerKey) -or
                (F $event 'event') -cin @('file_transfer_inbound_terminal', 'filetransfer_live_route_epoch_terminal',
                    'filetransfer_terminalized_by_session_end', 'filetransfer_data_session_removed', 'filetransfer_data_session_recreated') -or
                (F $event 'transfer_paused') -ceq '1') { return $true }
            if ((F $event 'event') -ceq 'filetransfer_route_transition_committed') {
                if ($null -eq $Commit -or [long]$event.Sequence -ne [long]$Commit.Sequence) { return $true }
                continue
            }
            if ((F $event 'event') -cnotin $scopeEvents) { continue }
            $targetProjection = $null -ne $Commit -and [long]$event.Sequence -gt [long]$Commit.Sequence -and
                (F $event 'event') -cin @('filetransfer_route_selected', 'filetransfer_live_route_epoch_recovered', 'filetransfer_route_hint_authorized_publication_accepted') -and
                (F $event 'route_publication_action_kind') -ceq 'RouteCommitPublication'
            if ($targetProjection) {
                foreach ($name in @('route', 'protocol_version', 'live_route_epoch', 'runtime_generation', 'terminal_generation',
                        'leg_generation', 'route_commit_generation', 'route_publication_generation')) {
                    if ((F $event $name) -cne (F $Commit "target_$name")) { $targetProjection = $false; break }
                }
            }
            if ($targetProjection) {
                $targetScope = @{
                    data_session_identity = F $Candidate 'data_session_identity'
                    data_session_generation = F $Candidate 'data_session_generation'
                    transport_epoch = $typed.targetBoundary.transportEpoch
                    bridge_recovery_generation = $typed.targetBoundary.bridgeRecoveryGeneration
                    bridge_decision_generation = F $Candidate 'bridge_decision_generation'
                    checkpoint_request_id = $typed.targetBoundary.checkpointRequestId
                    checkpoint_generation = $typed.targetBoundary.checkpointGeneration
                }
                foreach ($name in $targetScope.Keys) {
                    if ($event.Fields.ContainsKey($name) -and (F $event $name) -cne [string]$targetScope[$name]) { return $true }
                }
                continue
            }
            foreach ($name in $scope) {
                if ($event.Fields.ContainsKey($name) -and (F $event $name) -cne (F $Candidate $name)) { return $true }
            }
            if ((F $event 'event') -ceq 'filetransfer_v6_receiver_state_sent' -and
                (N $event 'contiguous_committed_chunk_index') -ne (N $Candidate 'frontier_chunk_index')) { return $true }
        }
        return $false
    }
    if (Poisoned $settlement) { return $null }
    $result = [pscustomobject][ordered]@{
        Kind = 'DispatchedStalled'; WakeWriterSequence = N $wake 'log_writer_sequence'
        RepairAuthorityId = F $grant 'authority_id'; RepairGeneration = N $grant 'repair_generation'
        SettlementWriterSequence = N $settlement 'log_writer_sequence'; SourceDisposition = 'Unproven'
        SupersedingTransitionId = '(none)'; SupersedingTransitionGeneration = 0L
        RetirementWriterSequence = 0L; PublicationWriterSequence = 0L; ObligationDisposition = 'Unresolved'
    }
    # The shared snapshot was evaluated by the caller. Cache only its existing
    # validated Family1 history; never reevaluate or reconstruct an authority.
    if (-not $Index.ContainsKey('__StallGraceDiagnosticRouteIndex')) {
        $cache = @{ Commits = @{}; Dispositions = @{}; BoundariesByWriter = @{}; Snapshot = $null; PublicationEvidence = $null }
        $Index['__StallGraceDiagnosticRouteIndex'] = $cache
        $snapshot = if ($Index.ContainsKey('__CheckpointRepairContinuitySnapshotEvidence')) { $Index['__CheckpointRepairContinuitySnapshotEvidence'] } else { $null }
        if ((P $snapshot 'Mode') -ceq 'SnapshotV1Authoritative' -and (P $snapshot 'ArtifactEvidenceVerdict') -ceq 'pass' -and
            (P $snapshot 'StreamComplete') -eq $true -and 'route_transition' -cin @(P $snapshot 'AuthoritativeFamilies' @())) {
            $cache.Snapshot = $snapshot
            $cache.PublicationEvidence = New-FileTransferPublicationEvidenceIndex $snapshot
            foreach ($boundary in @(P $snapshot 'Boundaries' @())) {
                if ((P $boundary 'verdict') -cne 'pass') { continue }
                $writer = [string](P $boundary 'writer' '')
                if (-not $cache.BoundariesByWriter.ContainsKey($writer)) { $cache.BoundariesByWriter[$writer] = [Collections.Generic.List[object]]::new() }
                $cache.BoundariesByWriter[$writer].Add($boundary)
                $kind = P $boundary 'decisionKind'
                if ($kind -cnotin @('CommitFileTransferRouteTransition', 'ApplyFileTransferRouteTransitionDispositionReceipt') -or
                    (P $boundary 'decisionStatus') -cne 'Committed') { continue }
                foreach ($operation in @(P $boundary 'operations' @())) {
                    if ((P $operation 'family') -cne 'route_transition' -or (P $operation 'operationRole') -cne 'terminal' -or
                        (P $operation 'afterState') -cne 'Committed' -or (P $operation 'receiptValidated') -ne $true -or
                        (P $operation 'operationKind') -cne 'PostTunaFallbackV6ToTunaV4') { continue }
                    $entryKey = "$writer|$(P $operation 'authorityId')|$(P $operation 'operationGeneration')"
                    $map = if ($kind -ceq 'CommitFileTransferRouteTransition') { $cache.Commits } else { $cache.Dispositions }
                    if (-not $map.ContainsKey($entryKey)) { $map[$entryKey] = [Collections.Generic.List[object]]::new() }
                    $map[$entryKey].Add([pscustomobject]@{ Boundary = $boundary; Operation = $operation })
                }
            }
        }
    }
    $cache = $Index['__StallGraceDiagnosticRouteIndex']
    $exactRepairOwner = (P $cache.Snapshot 'CatalogVersion') -cin @($script:FileTransferCoordinatorRepairOwnerCatalogVersion,
        $script:FileTransferCoordinatorActivationRetryCatalogVersion, $script:FileTransferCoordinatorReceiverPreAdmissionCatalogVersion,
        $script:FileTransferCoordinatorInitialDataSessionCatalogVersion, $script:FileTransferCoordinatorEarlyCheckpointProofCatalogVersion, $script:FileTransferCoordinatorFallbackRepairHandoffCatalogVersion)
    $sourcePublication = $null
    function BoundRepair($Event) {
        $binding = Resolve-FileTransferRepairOwnerEvidence $cache.Snapshot $Event
        if ($null -eq $binding -or $null -eq $sourcePublication) { return $null }
        $payload = $binding.Operation.repairOwner; $ownerScope = $payload.scope
        if ($payload.coordinatorInstanceId -cne $sourcePublication.CoordinatorId -or
            $payload.sessionId -cne $sourcePublication.SessionId -or $payload.transferId -cne $sourcePublication.TransferId -or
            $ownerScope.publicationFingerprint -cne $sourcePublication.TokenFingerprint -or
            $ownerScope.scopeFingerprint -cne (F $Candidate 'continuity_source_fingerprint') -or
            $ownerScope.retainedLegCheckpointGeneration -ne (N $Candidate 'checkpoint_generation') -or
            $ownerScope.retainedLegCheckpointRequestId -cne (ConvertTo-FileTransferCoordinatorEvidenceToken (F $Candidate 'checkpoint_request_id')) -or
            $ownerScope.boundary.dataSessionIdentity -cne (Get-FileTransferDiagnosticScopeEvidenceToken $Candidate 'data_session_identity') -or
            $ownerScope.boundary.dataSessionGeneration -ne (N $Candidate 'data_session_generation') -or
            $binding.Boundary.writer -cne (F $Candidate 'log_writer_id') -or
            [long]$binding.Boundary.afterStateGeneration -le (N $Candidate 'publication_evidence_observed_state_generation')) { return $null }
        # The reference was captured before the grant, but its queued snapshot
        # may be written later. A new repair advances Coordinator state after
        # the hold; the commit bound below closes that authored interval.
        return $binding
    }
    function SameRepairReceipt($Left, $Right) {
        return $null -ne $Left -and $null -ne $Right -and
            $Left.Boundary.boundaryId -ceq $Right.Boundary.boundaryId -and
            $Left.Operation.operationKey -ceq $Right.Operation.operationKey -and
            $Left.Operation.receiptIdentity -ceq $Right.Operation.receiptIdentity -and
            $Left.Operation.receiptFingerprint -ceq $Right.Operation.receiptFingerprint
    }
    $grantBinding = $null
    if ($exactRepairOwner) {
        $sourcePublication = Resolve-FileTransferPublicationEvidence $cache.PublicationEvidence $Candidate
        $grantBinding = BoundRepair $grant
        if (-not (SameRepairReceipt $grantBinding (BoundRepair $settlement))) { return $result }
    }
    $closures = [Collections.Generic.List[object]]::new()
    $commits = @($events | Where-Object { (F $_ 'event') -ceq 'filetransfer_route_transition_committed' -and
        [long]$_.Sequence -gt [long]$settlement.Sequence })
    foreach ($commit in $commits) {
        $projected = Project (F $commit 'authority_id'); $generation = N $commit 'transition_generation'
        $entryKey = "$(F $commit 'log_writer_id')|$projected|$generation"
        if ($projected -ceq '' -or $generation -le 0 -or -not $cache.Commits.ContainsKey($entryKey) -or
            $cache.Commits[$entryKey].Count -ne 1 -or -not $cache.Dispositions.ContainsKey($entryKey) -or
            $cache.Dispositions[$entryKey].Count -ne 1) { continue }
        $entry = $cache.Commits[$entryKey][0]; $applied = $cache.Dispositions[$entryKey][0]
        $boundary = $entry.Boundary; $typed = $entry.Operation.routeTransition
        if ((F $commit 'authority_schema') -cne '2' -or (F $commit 'decision_status') -cne 'Committed' -or
            (F $commit 'transition_kind') -cne 'PostTunaFallbackV6ToTunaV4' -or
            (F $commit 'target_route') -cne 'file_tuna_v4' -or (F $commit 'target_protocol_version') -cne '4' -or
            (F $commit 'target_transport') -cne 'tuna' -or (F $commit 'source_transport') -cne 'regular_nkn' -or
            -not (Equal $commit $Candidate @('direction', 'checkpoint_request_id', 'data_session_identity')) -or
            (-not $exactRepairOwner -and -not (Equal $commit $Candidate @('checkpoint_generation'))) -or
            (N $commit 'source_data_session_generation') -ne (N $Candidate 'data_session_generation') -or
            (N $commit 'checkpoint_contiguous_frontier') -ne (N $Candidate 'frontier_chunk_index') -or
            (N $commit 'checkpoint_durable_highest') -lt (N $Candidate 'highest_received_chunk_index') -or
            (F $Candidate 'continuity_coordinator_id') -cnotmatch '^fte1-[0-9a-f]{24}$' -or
            (F $Candidate 'continuity_coordinator_id') -cne [string]$boundary.coordinatorInstanceId -or
            (-not $exactRepairOwner -and (F $grant 'continuity_coordinator_id') -cne (F $Candidate 'continuity_coordinator_id')) -or
            $boundary.coordinatorInstanceId -cne $applied.Boundary.coordinatorInstanceId -or
            $boundary.sessionId -cne $applied.Boundary.sessionId -or $boundary.transferId -cne $applied.Boundary.transferId -or
            $boundary.direction -cne 'inbound' -or $boundary.afterStateGeneration -ne ($boundary.beforeStateGeneration + 1) -or
            $boundary.beforeStateHash -ceq $boundary.afterStateHash -or
            $typed.dispositionKind -cne 'Committed' -or $typed.sourceTransport -cne 'RegularNkn' -or $typed.targetTransport -cne 'Tuna' -or
            $entry.Operation.receiptStatus -cne 'Committed' -or $applied.Operation.receiptStatus -cne 'Applied' -or
            @($typed.receipts | Where-Object { $_.stage -ceq 'commit' -and $_.receiptStatus -ceq 'Committed' -and $_.receiptValidated }).Count -ne 1 -or
            @($applied.Operation.routeTransition.receipts | Where-Object { $_.stage -ceq 'disposition' -and $_.receiptStatus -ceq 'Applied' -and $_.receiptValidated }).Count -ne 1) { continue }
        if ($exactRepairOwner) {
            $ownerScope = $grantBinding.Operation.repairOwner.scope
            $durableGeneration = N $commit 'checkpoint_generation'
            if ($durableGeneration -le 0 -or $durableGeneration -ne [long]$typed.sourceBoundary.checkpointGeneration -or
                $typed.sourceBoundary.checkpointRequestId -cne (ConvertTo-FileTransferCoordinatorEvidenceToken "checkpoint:$durableGeneration") -or
                $typed.sourceBoundary.checkpointRequestId -cne $applied.Operation.routeTransition.sourceBoundary.checkpointRequestId -or
                $typed.sourceBoundary.checkpointGeneration -ne $applied.Operation.routeTransition.sourceBoundary.checkpointGeneration -or
                $typed.durableCheckpointFingerprint -cnotmatch '^fte1-[0-9a-f]{24}$' -or
                -not (Test-FileTransferCheckpointRepairContinuityAggregateScope $ownerScope $boundary.beforeBoundary) -or
                $grantBinding.Boundary.afterStateGeneration -gt $boundary.beforeStateGeneration) { continue }
        }
        $mapping = [ordered]@{ route='route'; protocol_version='protocolVersion'; live_route_epoch='liveRouteEpoch'
            runtime_generation='runtimeGeneration'; terminal_generation='terminalGeneration'; leg_generation='legGeneration'
            route_commit_generation='routeCommitGeneration'; route_publication_generation='routePublicationGeneration'
            transport_epoch='transportEpoch'; bridge_recovery_generation='bridgeRecoveryGeneration' }
        $matches = $true
        foreach ($name in $mapping.Keys) {
            if ((F $commit "source_$name") -cne (F $Candidate $name) -or
                (F $commit "source_$name") -cne [string]$typed.sourceBoundary.($mapping[$name])) { $matches = $false; break }
        }
        # The route payload retains a historical checkpoint and an unprojected
        # target data identity. Neither is the current diagnostic checkpoint.
        $targetNames = @('route', 'protocol_version', 'live_route_epoch', 'runtime_generation', 'terminal_generation',
            'leg_generation', 'route_commit_generation', 'route_publication_generation', 'transport_epoch')
        foreach ($name in $targetNames) {
            if ((F $commit "target_$name") -cne [string]$typed.targetBoundary.($mapping[$name])) { $matches = $false; break }
        }
        foreach ($name in @('route', 'protocolVersion', 'liveRouteEpoch', 'runtimeGeneration', 'terminalGeneration',
                'legGeneration', 'routeCommitGeneration', 'routePublicationGeneration', 'dataSessionGeneration')) {
            if ([string]$typed.sourceBoundary.$name -cne [string]$boundary.beforeBoundary.$name -or
                [string]$typed.targetBoundary.$name -cne [string]$boundary.afterBoundary.$name) { $matches = $false; break }
        }
        if (-not $matches -or $typed.sourceBoundary.dataSessionIdentity -cne $boundary.beforeBoundary.dataSessionIdentity -or
            $boundary.beforeBoundary.dataSessionIdentity -cne $boundary.afterBoundary.dataSessionIdentity -or
            $typed.sourceBoundary.dataSessionGeneration -ne (N $Candidate 'data_session_generation') -or
            $boundary.beforeBoundary.dataSessionGeneration -ne $boundary.afterBoundary.dataSessionGeneration -or
            $typed.durableCheckpointFingerprint -cne $applied.Operation.routeTransition.durableCheckpointFingerprint -or
            $typed.proofBoundaryFingerprint -cne $applied.Operation.routeTransition.proofBoundaryFingerprint -or
            (-not $exactRepairOwner -and [long]$boundary.writerSequence -ge (N $commit 'log_writer_sequence')) -or
            [long]$applied.Boundary.writerSequence -le [long]$boundary.writerSequence) { continue }
        # Exact source/repair receipts bind this complete authored history.
        # Its queued snapshots may drain after synchronous runtime diagnostics;
        # use Coordinator generations, not cross-queue writer order, for causality.
        if ($exactRepairOwner -and
            ([long]$applied.Boundary.beforeStateGeneration -lt [long]$boundary.afterStateGeneration -or
             [long]$applied.Boundary.afterStateGeneration -ne ([long]$applied.Boundary.beforeStateGeneration + 1))) { continue }
        $retirements = @($events | Where-Object { (F $_ 'event') -ceq 'filetransfer_fallback_frontier_repair_contract_settled' -and
            [long]$_.Sequence -gt [long]$settlement.Sequence -and [long]$_.Sequence -lt [long]$commit.Sequence -and
            (F $_ 'settlement_outcome') -ceq 'StaleRejected' -and
            (F $_ 'decision_reason') -ceq 'tuna_reactivation_replaced_fallback_owner' })
        if ($retirements.Count -ne 1) { continue }
        $retirement = $retirements[0]
        $lastGrants = @($events | Where-Object { (F $_ 'event') -ceq 'filetransfer_fallback_frontier_repair_authority_granted' -and
            (F $_ 'authority_id') -ceq (F $retirement 'authority_id') })
        if ($lastGrants.Count -ne 1 -or -not (Equal $retirement $lastGrants[0] ($repair + @('authority_schema'))) -or
            -not (Equal $retirement $grant @($scope | Where-Object { $_ -cnotin @('checkpoint_generation', 'data_session_identity', 'data_session_generation') })) -or
            (F $retirement 'dispatch_observed') -cne '1' -or (F $retirement 'successor_authority_id') -cne '(none)' -or
            (-not $exactRepairOwner -and (F $retirement 'continuity_coordinator_id') -cne (F $Candidate 'continuity_coordinator_id')) -or
            (-not $exactRepairOwner -and (F $lastGrants[0] 'continuity_coordinator_id') -cne (F $Candidate 'continuity_coordinator_id')) -or
            (F $retirement 'frontier_chunk_index') -cne (F $grant 'frontier_chunk_index') -or
            (F $retirement 'protocol_repair_request_id') -cne (F $retirement 'primary_protocol_repair_request_id') -or
            (F $retirement 'request_correlation_id') -cne (F $retirement 'primary_request_correlation_id') -or
            [long]$lastGrants[0].Sequence -le [long]$settlement.Sequence -or [long]$lastGrants[0].Sequence -ge [long]$retirement.Sequence -or
            (N $retirement 'repair_generation') -le (N $grant 'repair_generation') -or
            @($events | Where-Object { (F $_ 'event') -ceq 'filetransfer_fallback_frontier_repair_contract_settled' -and
                (F $_ 'authority_id') -ceq (F $retirement 'authority_id') }).Count -ne 1 -or
            @($events | Where-Object { (F $_ 'event') -ceq 'filetransfer_fallback_frontier_repair_authority_granted' -and
                [long]$_.Sequence -gt [long]$lastGrants[0].Sequence -and [long]$_.Sequence -lt [long]$retirement.Sequence }).Count -ne 0) { continue }
        if ($exactRepairOwner) {
            $lastBinding = BoundRepair $lastGrants[0]
            if (-not (SameRepairReceipt $lastBinding (BoundRepair $retirement)) -or
                $lastBinding.Operation.repairOwner.scope.scopeFingerprint -cne $grantBinding.Operation.repairOwner.scope.scopeFingerprint -or
                $lastBinding.Boundary.afterStateGeneration -le $grantBinding.Boundary.afterStateGeneration -or
                $lastBinding.Boundary.afterStateGeneration -gt $boundary.beforeStateGeneration) { continue }
        }
        $dispositions = @($events | Where-Object { (F $_ 'event') -ceq 'filetransfer_route_transition_disposition_applied' -and
            (F $_ 'authority_schema') -ceq '2' -and (F $_ 'authority_id') -ceq (F $commit 'authority_id') })
        $publications = @($events | Where-Object { (F $_ 'event') -ceq 'filetransfer_live_route_epoch_recovered' -and
            (F $_ 'transition_generation') -ceq (F $commit 'transition_generation') })
        if ($dispositions.Count -ne 1 -or $publications.Count -ne 1) { continue }
        $disposition = $dispositions[0]; $publication = $publications[0]
        if ([long]$disposition.Sequence -le [long]$commit.Sequence -or [long]$publication.Sequence -le [long]$disposition.Sequence -or
            (-not $exactRepairOwner -and [long]$applied.Boundary.writerSequence -ge (N $disposition 'log_writer_sequence')) -or
            (F $disposition 'decision_status') -cne 'Applied' -or (F $disposition 'decision_reason') -cne 'filetransfer_route_transition_committed' -or
            -not (Equal $disposition $commit @('transition_generation', 'transition_kind', 'checkpoint_request_id', 'checkpoint_generation', 'data_session_identity')) -or
            (F $publication 'projection_source') -cne 'coordinator_route_transition' -or
            (F $publication 'reason') -cne 'filetransfer_route_transition_committed' -or
            (F $publication 'route_publication_action_kind') -cne 'RouteCommitPublication') { continue }
        foreach ($name in $mapping.Keys) {
            if ((F $disposition "source_$name") -cne (F $commit "source_$name")) { $matches = $false; break }
        }
        foreach ($name in @('target_route', 'target_protocol_version', 'target_transport', 'source_data_session_generation')) {
            if ((F $disposition $name) -cne (F $commit $name)) { $matches = $false; break }
        }
        foreach ($name in @('live_route_epoch', 'runtime_generation', 'terminal_generation', 'leg_generation', 'route_commit_generation', 'route_publication_generation')) {
            if ((F $disposition "resulting_$name") -cne (F $commit "target_$name") -or
                (F $publication $name) -cne (F $commit "target_$name")) { $matches = $false; break }
        }
        foreach ($name in @('route', 'protocol_version')) {
            if ((F $publication $name) -cne (F $commit "target_$name")) { $matches = $false; break }
        }
        if (-not $matches -or (Poisoned $publication $commit)) { continue }
        foreach ($prior in $cache.BoundariesByWriter[[string]$boundary.writer]) {
            if ($prior.coordinatorInstanceId -cne $boundary.coordinatorInstanceId) { continue }
            if ($exactRepairOwner) {
                if ([long]$prior.afterStateGeneration -le (N $Candidate 'publication_evidence_observed_state_generation') -or
                    [long]$prior.afterStateGeneration -gt [long]$boundary.beforeStateGeneration) { continue }
            } elseif ([long]$prior.writerSequence -le (N $Candidate 'log_writer_sequence') -or
                [long]$prior.writerSequence -ge [long]$boundary.writerSequence) { continue }
            foreach ($name in @('route', 'protocolVersion', 'liveRouteEpoch', 'runtimeGeneration', 'terminalGeneration',
                    'legGeneration', 'routeCommitGeneration', 'routePublicationGeneration', 'dataSessionIdentity', 'dataSessionGeneration')) {
                if ([string]$prior.beforeBoundary.$name -cne [string]$boundary.beforeBoundary.$name -or
                    [string]$prior.afterBoundary.$name -cne [string]$boundary.beforeBoundary.$name) { $matches = $false; break }
            }
            if ($exactRepairOwner) {
                foreach ($name in @('checkpointGeneration', 'checkpointRequestId')) {
                    if ([string]$prior.beforeBoundary.$name -cne [string]$boundary.beforeBoundary.$name -or
                        [string]$prior.afterBoundary.$name -cne [string]$boundary.beforeBoundary.$name) { $matches = $false; break }
                }
            }
            foreach ($operation in $prior.operations) {
                if (-not $operation.stateApplied -or $operation.beforeState -ceq $operation.afterState) { continue }
                if (($operation.family -ceq 'route_runtime' -and $operation.operationRole -cin @(
                            'runtime_owner', 'route_commit', 'route_publication', 'checkpoint_acceptance_retained')) -or
                    ($operation.family -ceq 'route_transition' -and
                        ($operation.afterState -cin @('Invalidated', 'Failed', 'Cancelled', 'Superseded') -or
                            ($operation.operationRole -ceq 'current' -and $operation.authorityId -cne $projected)))) {
                    $matches = $false; break
                }
            }
            if (-not $matches) { break }
        }
        # TransitionId names the snapshot commit boundary. Generation belongs
        # to its route authority; that authority's projected id is join-only.
        if ($matches) { $closures.Add([pscustomobject]@{ Id=$boundary.boundaryId; Generation=$generation; Retirement=$retirement; Publication=$publication }) }
    }
    if ($closures.Count -eq 1) {
        $result.Kind = 'SourceSuperseded'; $result.SourceDisposition = 'Superseded'
        $result.SupersedingTransitionId = $closures[0].Id; $result.SupersedingTransitionGeneration = $closures[0].Generation
        $result.RetirementWriterSequence = N $closures[0].Retirement 'log_writer_sequence'
        $result.PublicationWriterSequence = N $closures[0].Publication 'log_writer_sequence'
    }
    return $result
}

# This closes a fulfilled wake/request obligation, not an absent wake or a repair
# that merely happens to be followed by traffic on another route. Both callers
# supply already-admitted episodes; all handoff evidence is matched here.
function Get-FileTransferRecentProgressGraceTunaHandoffClosure {
    param([object[]]$Events, [Parameter(Mandatory = $true)]$Episode)
    return Get-FileTransferFrontierGraceTunaHandoffClosure -Events $Events -Episode $Episode -Kind 'recent_progress'
}

function Get-FileTransferStallGraceTunaHandoffClosure {
    param([object[]]$Events, [Parameter(Mandatory = $true)]$Episode,
        [AllowNull()][hashtable]$PublishedSourceIndex = $null)
    return Get-FileTransferFrontierGraceTunaHandoffClosure -Events $Events -Episode $Episode -Kind 'stall' `
        -PublishedSourceIndex $PublishedSourceIndex
}

function Get-FileTransferFrontierGraceTunaHandoffClosure {
    param([object[]]$Events, [Parameter(Mandatory = $true)]$Episode,
        [ValidateSet('recent_progress', 'stall')][string]$Kind,
        [AllowNull()][hashtable]$PublishedSourceIndex = $null)

    function Field($Event, [string]$Name) { return [string]$Event.Fields[$Name] }
    function Number($Event, [string]$Name) {
        $value = 0L
        if (-not [long]::TryParse((Field $Event $Name), [ref]$value)) { return -1L }
        return $value
    }
    function EqualFields($Left, $Right, [string[]]$Names) {
        foreach ($name in $Names) {
            $value = Field $Left $name
            if ([string]::IsNullOrWhiteSpace($value) -or $value -cne (Field $Right $name)) { return $false }
        }
        return $true
    }
    function UtcTicks($Event, [string]$Name) {
        $value = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse((Field $Event $Name), [ref]$value)) { return 0L }
        return $value.UtcTicks
    }

    function SameAdmissionEvidence($Left, $Right) {
        if ($Left.Fields.Count -ne $Right.Fields.Count) { return $false }
        foreach ($name in $Right.Fields.Keys) {
            if (-not $Left.Fields.ContainsKey($name) -or
                (Field $Left $name) -cne (Field $Right $name)) { return $false }
        }
        return $true
    }
    $publishedAdmission = $Kind -ceq 'stall' -and
        $null -ne $Episode.PSObject.Properties['AdmissionKind'] -and
        $Episode.AdmissionKind -ceq 'CurrentPublishedSource'
    if ($publishedAdmission) {
        if ($null -eq $PublishedSourceIndex -and $null -ne $Episode.PSObject.Properties['PublishedSourceIndex']) {
            $PublishedSourceIndex = $Episode.PublishedSourceIndex
        }
        if ($null -eq $PublishedSourceIndex -or
            $null -eq $Episode.PSObject.Properties['PublishedSource'] -or
            $null -eq $Episode.PSObject.Properties['ScopeEvidence'] -or
            $null -eq $Episode.PSObject.Properties['HistoricalProgress'] -or
            $null -eq $Episode.PublishedSource -or $null -eq $Episode.ScopeEvidence -or
            $null -ne $Episode.HistoricalProgress) { return $null }
        # Reuse the caller's once-built index. An admission label alone is not proof.
        $admission = Get-FileTransferStallGracePublishedSourceAdmission -Index $PublishedSourceIndex -Candidate $Episode.Event
        if ($null -eq $admission -or -not $admission.Admitted -or
            $admission.Episode.Key -cne $Episode.Key -or
            -not (SameAdmissionEvidence $Episode.ScopeEvidence $Episode.Event) -or
            -not (SameAdmissionEvidence $Episode.PublishedSource $admission.Source)) { return $null }
    }

    $buckets = @{}
    $priorGrants = @{}
    $writerRecords = @{}
    $scopedEvents = [System.Collections.Generic.List[object]]::new()
    foreach ($event in $Events) {
        if ((Field $event 'log_writer_id') -cne $Episode.Writer -or
            (Field $event 'session_id') -cne $Episode.Session -or
            (Field $event 'transfer_id') -cne $Episode.Transfer) { continue }
        $name = Field $event 'event'
        $priorGrant = $publishedAdmission -and [long]$event.Sequence -lt [long]$Episode.Event.Sequence -and
            $name -ceq 'filetransfer_fallback_frontier_repair_authority_granted'
        if ([long]$event.Sequence -le [long]$Episode.Event.Sequence -and -not $priorGrant) { continue }
        $writerSequence = Field $event 'log_writer_sequence'
        if ($writerSequence -match '^[1-9][0-9]*$') {
            if ($writerRecords.ContainsKey($writerSequence)) {
                $prior = $writerRecords[$writerSequence]
                $names = @($event.Fields.Keys | Where-Object { $_ -ne 'gui_evidence_sequence' })
                $priorNames = @($prior.Fields.Keys | Where-Object { $_ -ne 'gui_evidence_sequence' })
                if ($names.Count -ne $priorNames.Count -or -not (EqualFields $event $prior $names)) { return $null }
                continue
            }
            $writerRecords[$writerSequence] = $event
        }
        if ($priorGrant) {
            $authority = Field $event 'authority_id'
            if (-not $priorGrants.ContainsKey($authority)) { $priorGrants[$authority] = [Collections.Generic.List[object]]::new() }
            $priorGrants[$authority].Add($event)
            continue
        }
        $scopedEvents.Add($event)
        if (-not $buckets.ContainsKey($name)) { $buckets[$name] = [System.Collections.Generic.List[object]]::new() }
        $buckets[$name].Add($event)
    }
    function Named([string]$Name) { if ($buckets.ContainsKey($Name)) { return $buckets[$Name].ToArray() } }
    $scope = @('direction', 'route', 'protocol_version', 'live_route_epoch',
        'route_publication_generation', 'route_commit_generation', 'runtime_generation',
        'terminal_generation', 'leg_generation', 'transport_epoch',
        'bridge_recovery_generation', 'bridge_decision_generation', 'checkpoint_request_id')
    $repairFields = $scope + @('authority_id', 'predecessor_authority_id', 'repair_generation',
        'repair_request_key', 'range_fingerprint', 'frontier_chunk_index', 'source_kind',
        'primary_protocol_repair_request_id', 'primary_request_correlation_id', 'target_transport')
    function ValidPublishedGrant($Grant) {
        if ((Field $Grant 'authority_schema') -cne '2' -or
            (Field $Grant 'authority_id') -notmatch '^fta2-repair_replay-.+' -or
            (Number $Grant 'repair_generation') -le 0 -or (Number $Grant 'frontier_chunk_index') -lt 0 -or
            (Number $Grant 'range_count') -le 0 -or (Number $Grant 'requested_chunk_count') -le 0 -or
            (Field $Grant 'source_kind') -cne 'ReceiverRequest' -or (Field $Grant 'target_transport') -cne 'regular_nkn' -or
            (Field $Grant 'repair_request_key') -cne "fallback-frontier:$(Field $Grant 'frontier_chunk_index'):$(Field $Grant 'range_count'):$(Field $Grant 'range_fingerprint')") { return $false }
        foreach ($name in @('range_fingerprint', 'primary_protocol_repair_request_id', 'primary_request_correlation_id')) {
            if ((Field $Grant $name) -in @('', '(none)', 'none')) { return $false }
        }
        return $true
    }

    if ($Kind -eq 'recent_progress') {
        $rechecks = @(Named 'filetransfer_v6_frontier_recent_progress_grace_recheck_decided' | Where-Object {
            (EqualFields $_ $Episode.Progress $scope) -and
            (Field $_ 'decision_status') -eq 'rechecked' -and (Field $_ 'decision_reason') -eq 'stall_grace_armed' -and
            (Field $_ 'frontier_chunk_index') -eq [string]$Episode.Frontier -and
            (Field $_ 'data_session_identity') -ceq $Episode.DataSessionIdentity -and
            (Field $_ 'data_session_generation') -ceq $Episode.DataSessionGeneration -and
            (Field $_ 'wake_generation') -match '^[1-9][0-9]*$' -and
            (UtcTicks $_ 'committed_progress_utc') -gt 0 -and
            (UtcTicks $_ 'wake_deadline_utc') -eq ((UtcTicks $_ 'committed_progress_utc') + 10000000L)
        })
        if ($rechecks.Count -ne 1) { return $null }
        $recheck = $rechecks[0]
        $stalls = @(Named 'filetransfer_v6_frontier_request_deferred' | Where-Object {
            (EqualFields $_ $recheck ($scope + @('checkpoint_generation', 'data_session_identity', 'data_session_generation', 'frontier_chunk_index'))) -and
            (Field $_ 'reason') -eq 'frontier_recent_progress_grace_elapsed' -and
            (Field $_ 'elapsed_ms') -eq '0' -and (Field $_ 'stall_grace_ms') -eq '750' -and
            (UtcTicks $_ 'stall_started_utc') -ge (UtcTicks $recheck 'wake_deadline_utc') -and
            (UtcTicks $_ 'wake_deadline_utc') -eq ((UtcTicks $_ 'stall_started_utc') + 7500000L)
        })
        if ($stalls.Count -ne 1) { return $null }
        $stall = $stalls[0]
    }
    else {
        # The admitted stall is the obligation itself, not a later initialization.
        $stall = $Episode.Event
        $recheck = $stall
        # Current-publication admission includes a caught-up receiver at F-1;
        # historical repair-progress admission retains its buffered-frontier bound.
        $minimumHighest = [long]$Episode.Frontier
        if ($publishedAdmission) { $minimumHighest-- }
        $stallHighest = 0L
        if (-not [long]::TryParse((Field $stall 'highest_received_chunk_index'), [ref]$stallHighest)) { return $null }
        if ((Field $stall 'event') -ne 'filetransfer_v6_frontier_request_deferred' -or
            -not (EqualFields $stall $Episode.Progress $scope) -or
            (Field $stall 'route') -ne 'post_tuna_fallback_v6' -or (Field $stall 'protocol_version') -ne '6' -or
            (Field $stall 'direction') -ne 'inbound' -or
            (Field $stall 'frontier_chunk_index') -cne [string]$Episode.Frontier -or
            (Field $stall 'data_session_identity') -cne $Episode.DataSessionIdentity -or
            (Field $stall 'data_session_generation') -cne $Episode.DataSessionGeneration -or
            (Number $stall 'data_session_generation') -le 0 -or (Number $stall 'checkpoint_generation') -lt 0 -or
            (Number $stall 'wake_generation') -le 0 -or
            $stallHighest -lt $minimumHighest -or
            (Field $stall 'elapsed_ms') -ne '0' -or (Field $stall 'stall_grace_ms') -ne '750' -or
            (UtcTicks $stall 'stall_started_utc') -le 0 -or
            (UtcTicks $stall 'wake_deadline_utc') -ne ((UtcTicks $stall 'stall_started_utc') + 7500000L) -or
            (-not $publishedAdmission -and
                ((Field $Episode.Progress 'authority_id') -notmatch '^fta2-repair_replay-.+' -or
                    (Number $Episode.Progress 'repair_generation') -le 0))) { return $null }
    }
    $grants = @(Named 'filetransfer_fallback_frontier_repair_authority_granted' | Where-Object {
        [long]$_.Sequence -gt [long]$recheck.Sequence -and (EqualFields $_ $Episode.Progress $scope) -and
        (Field $_ 'frontier_chunk_index') -eq [string]$Episode.Frontier -and
        (Field $_ 'source_kind') -eq 'ReceiverRequest' -and (Field $_ 'target_transport') -eq 'regular_nkn' -and
        (Field $_ 'authority_id') -match '^fta2-repair_replay-.+' -and
        ($publishedAdmission -or (Field $_ 'predecessor_authority_id') -ceq (Field $Episode.Progress 'authority_id')) -and
        (Field $_ 'repair_generation') -match '^[1-9][0-9]*$' -and
        ($publishedAdmission -or (Number $_ 'repair_generation') -gt (Number $Episode.Progress 'repair_generation')) -and
        (Number $_ 'log_writer_attempt_utc_ticks') -ge (UtcTicks $stall 'wake_deadline_utc')
    })
    if ($Kind -eq 'stall' -and $grants.Count -ne 1) { return $null }
    $closures = [System.Collections.Generic.List[object]]::new()
    foreach ($grant in $grants) {
        if ($publishedAdmission) {
            if (-not (ValidPublishedGrant $grant)) { continue }
            $predecessor = Field $grant 'predecessor_authority_id'
            if ($predecessor -cne '(none)') {
                if (-not $priorGrants.ContainsKey($predecessor) -or $priorGrants[$predecessor].Count -ne 1) { continue }
                $prior = $priorGrants[$predecessor][0]
                if ($PublishedSourceIndex.Conflicts.ContainsKey("$(Field $prior 'log_writer_id')|$(Field $prior 'log_writer_sequence')") -or
                    (Field $prior 'authority_id') -cne $predecessor -or
                    -not (ValidPublishedGrant $prior) -or -not (EqualFields $prior $grant $scope) -or
                    (Field $prior 'authority_id') -ceq (Field $grant 'authority_id') -or
                    (Number $prior 'repair_generation') -ge (Number $grant 'repair_generation')) { continue }
            }
        }
        if ($Kind -eq 'stall') {
            $highest = Number $stall 'highest_received_chunk_index'
            # Stale wake diagnostics retain the captured highest; only committed
            # state between admission and grant can advance the observed frontier.
            foreach ($event in @($scopedEvents | Sort-Object Sequence)) {
                if ([long]$event.Sequence -ge [long]$grant.Sequence -or
                    (Field $event 'event') -ne 'filetransfer_v6_receiver_state_sent') { continue }
                if ((Field $event 'contiguous_committed_chunk_index') -cne [string]$Episode.Frontier) { return $null }
                $observedHighest = Number $event 'durable_received_highest_chunk_index'
                if ($observedHighest -lt $highest) { return $null }
                $highest = $observedHighest
            }
            if ((Number $grant 'durable_received_highest_chunk_index') -lt $highest) { continue }
        }
        $retirements = @(Named 'filetransfer_fallback_frontier_repair_contract_settled' | Where-Object {
            [long]$_.Sequence -gt [long]$grant.Sequence -and (EqualFields $_ $grant $repairFields) -and
            (Field $_ 'settlement_outcome') -eq 'StaleRejected' -and (Field $_ 'dispatch_observed') -eq '1' -and
            (Field $_ 'decision_reason') -eq 'tuna_reactivation_replaced_fallback_owner'
        })
        if ($retirements.Count -ne 1) { continue }
        $retirement = $retirements[0]
        $dispatches = @(Named 'filetransfer_fallback_frontier_repair_dispatched' | Where-Object {
            [long]$_.Sequence -gt [long]$grant.Sequence -and [long]$_.Sequence -lt [long]$retirement.Sequence -and
            (EqualFields $_ $grant $repairFields) -and (Field $_ 'dispatch_observed') -eq '1'
        })
        $sends = @(Named 'filetransfer_v6_frontier_request_sent' | Where-Object {
            [long]$_.Sequence -gt [long]$grant.Sequence -and [long]$_.Sequence -lt [long]$retirement.Sequence -and
            (EqualFields $_ $grant $repairFields) -and
            (Field $_ 'repair_request_id') -ceq (Field $grant 'primary_protocol_repair_request_id') -and
            (Field $_ 'request_correlation_id') -ceq (Field $grant 'primary_request_correlation_id')
        })
        $deliveries = @(Named 'filetransfer_control_plane_delivery_observed' | Where-Object {
            [long]$_.Sequence -gt [long]$grant.Sequence -and [long]$_.Sequence -lt [long]$retirement.Sequence -and
            (EqualFields $_ $grant @('route', 'protocol_version', 'live_route_epoch', 'repair_request_key', 'range_fingerprint')) -and
            (Field $_ 'kind') -eq 'frontier_request' -and (Field $_ 'peer_visible_any') -eq '1' -and
            (Field $_ 'accepted_any') -eq '1' -and
            (Field $_ 'repair_request_id') -ceq (Field $grant 'primary_protocol_repair_request_id') -and
            (Field $_ 'request_correlation_id') -ceq (Field $grant 'primary_request_correlation_id')
        })
        if ($dispatches.Count -ne 1 -or $sends.Count -ne 1 -or $deliveries.Count -ne 1) { continue }
        if ($Kind -eq 'stall' -and
            ([long]$deliveries[0].Sequence -ge [long]$dispatches[0].Sequence -or
                [long]$dispatches[0].Sequence -ge [long]$sends[0].Sequence)) { continue }
        if ($publishedAdmission) {
            $wakeFields = $scope + @('checkpoint_generation', 'data_session_identity', 'data_session_generation',
                'wake_generation', 'stall_started_utc', 'wake_deadline_utc', 'frontier_chunk_index', 'highest_received_chunk_index')
            $wakes = @(Named 'filetransfer_v6_frontier_stall_grace_recheck_decided' | Where-Object {
                (Field $_ 'wake_generation') -ceq (Field $stall 'wake_generation')
            })
            if ($wakes.Count -ne 1 -or -not (EqualFields $wakes[0] $stall $wakeFields) -or
                (Field $wakes[0] 'authority_effect') -cne 'none' -or
                (Field $wakes[0] 'decision_status') -cne 'dispatched' -or
                (Field $wakes[0] 'decision_reason') -cne 'exact_receiver_request_dispatched' -or
                [long]$wakes[0].Sequence -le [long]$sends[0].Sequence -or
                [long]$wakes[0].Sequence -ge [long]$retirement.Sequence -or
                (Number $wakes[0] 'log_writer_attempt_utc_ticks') -lt (UtcTicks $stall 'wake_deadline_utc')) { continue }
        }

        $commits = @(Named 'filetransfer_route_transition_committed' | Where-Object {
            if ([long]$_.Sequence -le [long]$sends[0].Sequence -or
                (Field $_ 'authority_schema') -ne '2' -or (Field $_ 'authority_id') -notmatch '^fta2-filetransfer_route_transition-.+' -or
                (Field $_ 'transition_generation') -notmatch '^[1-9][0-9]*$' -or
                (Field $_ 'transition_kind') -ne 'PostTunaFallbackV6ToTunaV4' -or
                (Field $_ 'decision_status') -ne 'Committed' -or
                (Field $_ 'target_route') -ne 'file_tuna_v4' -or (Field $_ 'target_protocol_version') -ne '4' -or
                (Field $_ 'target_transport') -ne 'tuna' -or
                (Field $_ 'data_session_identity') -cne $Episode.DataSessionIdentity -or
                (Field $_ 'source_data_session_generation') -cne $Episode.DataSessionGeneration -or
                -not (EqualFields $_ $recheck @('checkpoint_request_id', 'checkpoint_generation'))) { return $false }
            foreach ($field in @('route', 'protocol_version', 'live_route_epoch', 'runtime_generation', 'terminal_generation',
                    'leg_generation', 'route_commit_generation', 'route_publication_generation', 'transport_epoch', 'bridge_recovery_generation')) {
                if ((Field $_ "source_$field") -cne (Field $grant $field)) { return $false }
            }
            return (Field $_ 'direction') -eq 'inbound'
        })
        if ($commits.Count -ne 1) { continue }
        $commit = $commits[0]
        if ((Number $commit 'target_live_route_epoch') -ne ((Number $grant 'live_route_epoch') + 1) -or
            (Number $commit 'target_runtime_generation') -le (Number $grant 'runtime_generation') -or
            (Number $commit 'target_leg_generation') -le (Number $grant 'leg_generation') -or
            (Number $commit 'target_route_publication_generation') -le (Number $grant 'route_publication_generation') -or
            (Number $commit 'target_route_commit_generation') -le 0 -or
            (Field $commit 'target_terminal_generation') -cne (Field $grant 'terminal_generation')) { continue }
        $transitionFields = @('authority_id', 'transition_generation', 'transition_kind', 'direction',
            'source_route', 'source_protocol_version', 'source_transport', 'source_live_route_epoch', 'source_runtime_generation',
            'source_terminal_generation', 'source_leg_generation', 'source_route_commit_generation', 'source_route_publication_generation',
            'source_transport_epoch', 'source_bridge_recovery_generation', 'source_data_session_generation',
            'data_session_identity', 'checkpoint_generation', 'checkpoint_request_id', 'target_route', 'target_protocol_version', 'target_transport')
        $dispositions = @(Named 'filetransfer_route_transition_disposition_applied' | Where-Object {
            if ([long]$_.Sequence -le [long]$commit.Sequence -or [long]$_.Sequence -le [long]$retirement.Sequence -or
                -not (EqualFields $_ $commit $transitionFields) -or (Field $_ 'decision_status') -ne 'Applied' -or
                (Field $_ 'decision_reason') -ne 'filetransfer_route_transition_committed') { return $false }
            foreach ($field in @('live_route_epoch', 'runtime_generation', 'terminal_generation', 'leg_generation', 'route_commit_generation', 'route_publication_generation')) {
                $target = Field $commit "target_$field"
                if ($target -notmatch '^[0-9]+$' -or (Field $_ "resulting_$field") -cne $target) { return $false }
            }
            return $true
        })
        if ($dispositions.Count -ne 1) { continue }
        $disposition = $dispositions[0]
        $publications = @(Named 'filetransfer_live_route_epoch_recovered' | Where-Object {
            if ([long]$_.Sequence -le [long]$disposition.Sequence -or
                (Field $_ 'direction') -ne 'inbound' -or (Field $_ 'projection_source') -ne 'coordinator_route_transition' -or
                (Field $_ 'transition_generation') -cne (Field $commit 'transition_generation') -or
                (Field $_ 'reason') -ne 'filetransfer_route_transition_committed') { return $false }
            foreach ($field in @('route', 'protocol_version', 'live_route_epoch', 'runtime_generation', 'terminal_generation',
                    'leg_generation', 'route_commit_generation', 'route_publication_generation')) {
                if ((Field $_ $field) -cne (Field $commit "target_$field")) { return $false }
            }
            return $true
        })
        if ($publications.Count -ne 1) { continue }
        $publication = $publications[0]
        $poisoned = $false
        foreach ($event in $scopedEvents) {
            if ([long]$event.Sequence -le [long]$Episode.Event.Sequence -or [long]$event.Sequence -ge [long]$publication.Sequence -or
                (Field $event 'log_writer_id') -cne $Episode.Writer -or
                (Field $event 'session_id') -cne $Episode.Session -or (Field $event 'transfer_id') -cne $Episode.Transfer) { continue }
            $name = Field $event 'event'
            if ($name -in @('file_transfer_inbound_terminal', 'filetransfer_live_route_epoch_terminal', 'filetransfer_terminalized_by_session_end',
                    'filetransfer_data_session_removed', 'filetransfer_data_session_recreated') -or
                ($name -eq 'filetransfer_v6_receiver_state_sent' -and (Field $event 'transfer_paused') -eq '1') -or
                ($name -eq 'filetransfer_route_transition_committed' -and [long]$event.Sequence -ne [long]$commit.Sequence)) {
                $poisoned = $true; break
            }
            if ($name -in @('filetransfer_route_selected', 'filetransfer_live_route_epoch_recovered', 'filetransfer_route_hint_authorized_publication_accepted')) {
                # The exact committed target can be projected before disposition
                # telemetry. It is not an unrelated source mutation.
                $targetProjection = [long]$event.Sequence -gt [long]$commit.Sequence -and
                    (Field $event 'route_publication_action_kind') -eq 'RouteCommitPublication'
                foreach ($field in @('route', 'protocol_version', 'live_route_epoch', 'runtime_generation', 'terminal_generation',
                        'leg_generation', 'route_commit_generation', 'route_publication_generation')) {
                    if ((Field $event $field) -cne (Field $commit "target_$field")) { $targetProjection = $false }
                }
                if ($targetProjection) { continue }
                foreach ($field in $scope) {
                    $actual = Field $event $field
                    if ($actual -ne '' -and $actual -cne (Field $Episode.Progress $field)) { $poisoned = $true; break }
                }
            }
        }
        if (-not $poisoned) { $closures.Add($publication) }
    }
    if ($closures.Count -eq 1) { return $closures[0] }
    return $null
}
