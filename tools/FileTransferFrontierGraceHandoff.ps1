Set-StrictMode -Version Latest

# A fulfilled recent-progress wake transfers the obligation to the ordinary
# stall grace. Natural progress can close that grace without repair authority,
# but a sparse write alone is not publication proof.
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
    $buckets = @{}
    $seen = @{}
    $scoped = [Collections.Generic.List[object]]::new()
    foreach ($event in $Events) {
        if ([long]$event.Sequence -le [long]$Episode.Progress.Sequence -or
            (Field $event 'log_writer_id') -cne $Episode.Writer -or
            (Field $event 'session_id') -cne $Episode.Session -or (Field $event 'transfer_id') -cne $Episode.Transfer) { continue }
        $key = Field $event 'log_writer_sequence'
        if ($key -notmatch '^[1-9][0-9]*$') { continue }
        if ($seen.ContainsKey($key)) {
            $prior = $seen[$key]
            $names = @($event.Fields.Keys | Where-Object { $_ -ne 'gui_evidence_sequence' })
            if ($names.Count -ne @($prior.Fields.Keys | Where-Object { $_ -ne 'gui_evidence_sequence' }).Count -or
                -not (Equal $event $prior $names)) { return $null }
            continue
        }
        $seen[$key] = $event
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
        (Number $Episode.Event 'log_writer_attempt_utc_ticks') -lt (Ticks $_ 'wake_deadline_utc')
    })
    if ($arms.Count -ne 1) { return $null }
    $stalls = @(Named 'filetransfer_v6_frontier_request_deferred' | Where-Object {
        [long]$_.Sequence -gt [long]$Episode.Event.Sequence -and (Equal $_ $recheck $wakeScope) -and
        (Field $_ 'reason') -ceq 'frontier_recent_progress_grace_elapsed' -and
        (Field $_ 'authority_effect') -ceq 'none' -and (Number $_ 'wake_generation') -gt 0 -and
        (Number $_ 'elapsed_ms') -eq 0 -and (Number $_ 'stall_grace_ms') -eq 750 -and
        (Number $_ 'highest_received_chunk_index') -ge (Number $recheck 'highest_received_chunk_index') -and
        (Ticks $_ 'stall_started_utc') -ge (Ticks $recheck 'wake_deadline_utc') -and
        (Ticks $_ 'wake_deadline_utc') -eq ((Ticks $_ 'stall_started_utc') + 7500000L)
    })
    if ($stalls.Count -ne 1) { return $null }
    $stall = $stalls[0]
    $deadline = Ticks $stall 'wake_deadline_utc'
    $writes = @(Named 'filetransfer_v6_sparse_write_committed' | Where-Object {
        [long]$_.Sequence -gt [Math]::Max([long]$stall.Sequence, [long]$recheck.Sequence) -and
        (Number $_ 'log_writer_attempt_utc_ticks') -gt (Number $recheck 'log_writer_attempt_utc_ticks') -and
        (Number $_ 'log_writer_attempt_utc_ticks') -lt $deadline -and
        (Number $_ 'contiguous_chunks_committed') -gt 0 -and (Number $_ 'next_chunk_index') -gt $Episode.Frontier -and
        (Number $_ 'next_chunk_index') -eq ($Episode.Frontier + (Number $_ 'contiguous_chunks_committed')) -and
        (Number $_ 'highest_received_chunk_index') -ge (Number $stall 'highest_received_chunk_index')
    })
    $initialStates = @(Named 'filetransfer_v6_receiver_state_sent' | Where-Object {
        [long]$_.Sequence -lt [long]$Episode.Event.Sequence -and
        (Number $_ 'contiguous_committed_chunk_index') -eq $Episode.Frontier -and
        (Field $_ 'transport_epoch') -ceq (Field $stall 'transport_epoch') -and (Number $_ 'epoch') -gt 0
    })
    if ($initialStates.Count -eq 0) { return $null }
    foreach ($write in $writes) {
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
            if ((Field $event 'event') -in @('file_transfer_inbound_terminal', 'filetransfer_live_route_epoch_terminal',
                    'filetransfer_terminalized_by_session_end', 'filetransfer_data_session_recreated', 'filetransfer_data_session_removed') -or
                (Field $event 'transfer_paused') -ceq '1') { $poisoned = $true; break }
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

# This closes a fulfilled wake/request obligation, not an absent wake or a repair
# that merely happens to be followed by traffic on another route. Both callers
# supply already-admitted episodes; all handoff evidence is matched here.
function Get-FileTransferRecentProgressGraceTunaHandoffClosure {
    param([object[]]$Events, [Parameter(Mandatory = $true)]$Episode)
    return Get-FileTransferFrontierGraceTunaHandoffClosure -Events $Events -Episode $Episode -Kind 'recent_progress'
}

function Get-FileTransferStallGraceTunaHandoffClosure {
    param([object[]]$Events, [Parameter(Mandatory = $true)]$Episode)
    return Get-FileTransferFrontierGraceTunaHandoffClosure -Events $Events -Episode $Episode -Kind 'stall'
}

function Get-FileTransferFrontierGraceTunaHandoffClosure {
    param([object[]]$Events, [Parameter(Mandatory = $true)]$Episode,
        [ValidateSet('recent_progress', 'stall')][string]$Kind)

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

    $buckets = @{}
    $writerRecords = @{}
    $scopedEvents = [System.Collections.Generic.List[object]]::new()
    foreach ($event in $Events) {
        if ([long]$event.Sequence -le [long]$Episode.Event.Sequence -or
            (Field $event 'log_writer_id') -cne $Episode.Writer -or
            (Field $event 'session_id') -cne $Episode.Session -or
            (Field $event 'transfer_id') -cne $Episode.Transfer) { continue }
        $name = Field $event 'event'
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
        if ((Field $stall 'event') -ne 'filetransfer_v6_frontier_request_deferred' -or
            -not (EqualFields $stall $Episode.Progress $scope) -or
            (Field $stall 'route') -ne 'post_tuna_fallback_v6' -or (Field $stall 'protocol_version') -ne '6' -or
            (Field $stall 'direction') -ne 'inbound' -or
            (Field $stall 'frontier_chunk_index') -cne [string]$Episode.Frontier -or
            (Field $stall 'data_session_identity') -cne $Episode.DataSessionIdentity -or
            (Field $stall 'data_session_generation') -cne $Episode.DataSessionGeneration -or
            (Number $stall 'data_session_generation') -le 0 -or (Number $stall 'checkpoint_generation') -lt 0 -or
            (Number $stall 'wake_generation') -le 0 -or
            (Number $stall 'highest_received_chunk_index') -lt [long]$Episode.Frontier -or
            (Field $stall 'elapsed_ms') -ne '0' -or (Field $stall 'stall_grace_ms') -ne '750' -or
            (UtcTicks $stall 'stall_started_utc') -le 0 -or
            (UtcTicks $stall 'wake_deadline_utc') -ne ((UtcTicks $stall 'stall_started_utc') + 7500000L) -or
            (Field $Episode.Progress 'authority_id') -notmatch '^fta2-repair_replay-.+' -or
            (Number $Episode.Progress 'repair_generation') -le 0) { return $null }
    }
    $grants = @(Named 'filetransfer_fallback_frontier_repair_authority_granted' | Where-Object {
        [long]$_.Sequence -gt [long]$recheck.Sequence -and (EqualFields $_ $Episode.Progress $scope) -and
        (Field $_ 'frontier_chunk_index') -eq [string]$Episode.Frontier -and
        (Field $_ 'source_kind') -eq 'ReceiverRequest' -and (Field $_ 'target_transport') -eq 'regular_nkn' -and
        (Field $_ 'authority_id') -match '^fta2-repair_replay-.+' -and
        (Field $_ 'predecessor_authority_id') -ceq (Field $Episode.Progress 'authority_id') -and
        (Field $_ 'repair_generation') -match '^[1-9][0-9]*$' -and
        (Number $_ 'repair_generation') -gt (Number $Episode.Progress 'repair_generation') -and
        (Number $_ 'log_writer_attempt_utc_ticks') -ge (UtcTicks $stall 'wake_deadline_utc')
    })
    if ($Kind -eq 'stall' -and $grants.Count -ne 1) { return $null }
    $closures = [System.Collections.Generic.List[object]]::new()
    foreach ($grant in $grants) {
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
