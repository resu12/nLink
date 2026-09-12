Set-StrictMode -Version Latest

# This is consumption of an exact peer-first bootstrap committed before the wait,
# not recovery inferred from a later route. Its disposition may finish after the
# wait. Bulk and peer-first triggers deliberately remain distinct.
function Test-FileTransferFallbackV6BootstrapPeerFirstEpisodeConsumed {
    param($Episode, [long]$BeforeSequence)

    if ($Episode.ConflictingStart) { return $false }
    function Field($Record, [string]$Name) { return [string]$Record.Fields[$Name] }
    function Equal($Left, $Right, [string[]]$Names) {
        foreach ($name in $Names) {
            $value = Field $Left $name
            if ([string]::IsNullOrWhiteSpace($value) -or $value -cne (Field $Right $name)) { return $false }
        }
        return $true
    }
    function Positive($Record, [string]$Name) { return (Field $Record $Name) -match '^[1-9][0-9]*$' }
    function Present($Record, [string]$Name) {
        $value = Field $Record $Name
        return -not [string]::IsNullOrWhiteSpace($value) -and $value -cnotin @('(none)', 'none')
    }
    $buckets = @{}
    $seen = @{}
    $laterCompletion = [Collections.Generic.List[object]]::new()
    foreach ($record in $Episode.CompletionRecords) {
        if ($record.WriterSequence -le $Episode.PendingSequence) { continue }
        if ($record.WriterSequence -ge $BeforeSequence) {
            if ($record.EventName -in @('filetransfer_route_transition_disposition_applied', 'filetransfer_live_route_epoch_recovered')) {
                $laterCompletion.Add($record)
            }
            continue
        }
        $key = [string]$record.WriterSequence
        if ($seen.ContainsKey($key)) {
            if ($seen[$key].Line -cne $record.Line) { return $false }
            continue
        }
        $seen[$key] = $record
        if (-not $buckets.ContainsKey($record.EventName)) { $buckets[$record.EventName] = [Collections.Generic.List[object]]::new() }
        $buckets[$record.EventName].Add($record)
    }
    # Preserve the completed-before-wait path. For an in-flight disposition,
    # admit only completion records; no later issuance or commit may be borrowed.
    if (-not $buckets.ContainsKey('filetransfer_route_transition_disposition_applied') -or
        -not $buckets.ContainsKey('filetransfer_live_route_epoch_recovered')) {
        foreach ($record in $laterCompletion) {
            $key = [string]$record.WriterSequence
            if ($seen.ContainsKey($key)) {
                if ($seen[$key].Line -cne $record.Line) { return $false }
                continue
            }
            $seen[$key] = $record
            if (-not $buckets.ContainsKey($record.EventName)) { $buckets[$record.EventName] = [Collections.Generic.List[object]]::new() }
            $buckets[$record.EventName].Add($record)
        }
    }
    function Named([string]$Name) { if ($buckets.ContainsKey($Name)) { return $buckets[$Name].ToArray() } }
    $identity = @('session_id', 'transfer_id', 'direction')
    $source = @('source_route', 'source_protocol_version', 'source_live_route_epoch',
        'source_runtime_generation', 'source_route_publication_generation', 'source_route_commit_generation',
        'source_terminal_generation', 'source_leg_generation', 'source_transport_epoch',
        'source_bridge_recovery_generation', 'source_checkpoint_request_id', 'source_checkpoint_generation')
    $durable = @('source_data_session_generation', 'source_durable_checkpoint_generation',
        'source_state_epoch', 'source_contiguous_frontier', 'source_durable_highest', 'source_chunk_count')
    $trigger = @('observation_generation', 'fallback_epoch', 'trigger_transport_epoch',
        'trigger_bootstrap_generation', 'trigger_frame_fingerprint', 'trigger_data_session_generation')
    $observations = @(Named 'filetransfer_fallback_v6_proof_bootstrap_observed' | Where-Object {
        (Field $_ 'proof_kind') -ceq 'file_transfer_v6_frontier_request_frame_received' -and
        (Field $_ 'direction') -ceq 'inbound' -and
        (Field $_ 'fallback_epoch') -ceq [string]$Episode.FallbackEpoch -and
        (Field $_ 'source_route') -ceq 'file_tuna_v4' -and (Field $_ 'source_protocol_version') -ceq '4' -and
        (Field $_ 'authority_effect') -ceq 'coordinator_evaluation_only' -and
        (Positive $_ 'observation_generation') -and (Positive $_ 'trigger_bootstrap_generation') -and
        (Positive $_ 'trigger_data_session_generation')
    })
    if ($observations.Count -ne 1) { return $false }
    $observation = $observations[0]
    $commands = @(Named 'filetransfer_fallback_v6_proof_bootstrap_decided')
    if ($commands.Count -ne 1) { return $false }
    $command = $commands[0]
    if (-not (Equal $command $observation ($identity + $source + $trigger)) -or
        $command.WriterSequence -le $observation.WriterSequence -or
        (Field $command 'decision_status') -cne 'Issued' -or
        (Field $command 'authority_id') -notmatch '^fta2-filetransfer_fallback_v6_proof_bootstrap-[a-zA-Z0-9-]+$' -or
        -not (Positive $command 'bootstrap_generation') -or
        (Field $command 'trigger_transfer_id') -cne (Field $command 'transfer_id') -or
        (Field $command 'source_data_session_generation') -cne (Field $command 'trigger_data_session_generation') -or
        (Field $command 'target_route') -cne 'post_tuna_fallback_v6' -or
        (Field $command 'target_protocol_version') -cne '6' -or
        (Field $command 'target_transport') -cne 'regular_nkn' -or
        (Field $command 'target_transport_epoch') -cne (Field $command 'trigger_transport_epoch') -or
        (Field $command 'frame_kind') -cne 'ReceiverState') { return $false }
    if (-not (Present $command 'transfer_id') -or -not (Present $command 'trigger_frame_fingerprint')) { return $false }

    # Link the bulk trigger by episode ownership, never by the peer frame's
    # fingerprint or its different transport/bootstrap generation.
    $bulk = @(Named 'filetransfer_fallback_v6_proof_bootstrap_trigger_bound' | Where-Object { (Field $_ 'status') -ceq 'Ready' })
    if ($bulk.Count -ne 1 -or -not (Equal $bulk[0] $command ($identity + $source + @('fallback_epoch'))) -or
        (Field $bulk[0] 'pending_generation') -cne (Field $command 'observation_generation') -or
        (Field $bulk[0] 'data_session_generation') -cne (Field $command 'source_data_session_generation') -or
        -not (Present $bulk[0] 'frame_fingerprint') -or
        (Field $bulk[0] 'bulk_seen') -cne '1' -or (Field $bulk[0] 'control_seen') -cne '1' -or
        (Field $bulk[0] 'authority_effect') -cne 'none') { return $false }

    $receiptKey = $identity + @('authority_id', 'bootstrap_generation', 'fallback_epoch', 'target_transport_epoch', 'frame_kind')
    $executions = @(Named 'filetransfer_fallback_v6_proof_bootstrap_execution_completed')
    $applications = @(Named 'filetransfer_fallback_v6_proof_bootstrap_receipt_applied')
    $bindings = @(Named 'filetransfer_fallback_v6_proof_bootstrap_peer_boundary_bound')
    if ($executions.Count -ne 1 -or $applications.Count -ne 1 -or $bindings.Count -ne 1) { return $false }
    $execution = $executions[0]; $application = $applications[0]; $binding = $bindings[0]
    if (-not (Equal $execution $command $receiptKey) -or
        -not (Equal $application $execution ($receiptKey + @('receipt_status', 'frame_fingerprint'))) -or
        (Field $execution 'receipt_status') -cne 'Delivered' -or
        -not (Present $execution 'frame_fingerprint') -or
        (Field $application 'application_status') -cne 'Applied' -or
        $execution.WriterSequence -le $command.WriterSequence -or $application.WriterSequence -le $execution.WriterSequence -or
        -not (Equal $binding $command ($identity + $source + $durable + @('authority_id', 'bootstrap_generation', 'fallback_epoch', 'target_route', 'target_protocol_version', 'target_transport_epoch'))) -or
        (Field $binding 'local_receipt_status') -cne 'Delivered' -or
        (Field $binding 'local_frame_fingerprint') -cne (Field $execution 'frame_fingerprint') -or
        (Field $binding 'pairing_status') -cne 'Bound' -or
        (Field $binding 'authority_effect') -cne 'peer_proof_binding_only' -or
        (Field $binding 'peer_frame_type') -cne 'filetransfer.frontier_request.v6' -or
        -not (Present $binding 'peer_frame_fingerprint') -or
        (Field $binding 'proof_kind') -cne 'fallback_v6_proof_bootstrap_peer_frame' -or
        -not (Positive $binding 'proof_generation') -or $binding.WriterSequence -le $application.WriterSequence) { return $false }

    $prepared = @(Named 'filetransfer_route_transition_prepared')
    $draining = @(Named 'filetransfer_route_transition_draining')
    $committed = @(Named 'filetransfer_route_transition_committed')
    $dispositions = @(Named 'filetransfer_route_transition_disposition_applied' | Where-Object { (Field $_ 'authority_schema') -ceq '2' })
    $publications = @(Named 'filetransfer_live_route_epoch_recovered')
    if ($prepared.Count -ne 1 -or $draining.Count -ne 1 -or $committed.Count -ne 1 -or
        $dispositions.Count -ne 1 -or $publications.Count -ne 1) { return $false }
    $prepare = $prepared[0]; $drain = $draining[0]; $commit = $committed[0]; $disposition = $dispositions[0]; $publication = $publications[0]
    # All issuance, delivery, binding and preparation stages below must lead to
    # this pre-wait commit. Only its exact disposition/publication may follow the
    # wait; a later bootstrap or route cannot supply the missing authority.
    if ($commit.WriterSequence -ge $BeforeSequence -or $bulk[0].WriterSequence -ge $BeforeSequence) { return $false }
    if (-not (Equal $prepare $command ($identity + $source + $durable + @('target_route', 'target_protocol_version', 'target_transport', 'target_transport_epoch'))) -or
        (Field $prepare 'authority_schema') -cne '2' -or
        (Field $prepare 'authority_id') -notmatch '^fta2-filetransfer_route_transition-[a-zA-Z0-9-]+$' -or
        -not (Positive $prepare 'transition_generation') -or
        (Field $prepare 'transition_kind') -cne 'TunaV4ToPostTunaFallbackV6' -or
        (Field $prepare 'decision_status') -cne 'Prepared' -or
        (Field $prepare 'peer_proof_generation') -cne (Field $binding 'proof_generation') -or
        (Field $prepare 'peer_proof_kind') -cne (Field $binding 'proof_kind') -or
        (Field $prepare 'peer_proof_transport_epoch') -cne (Field $binding 'target_transport_epoch') -or
        (Field $prepare 'retained_frame_fingerprint') -cne (Field $binding 'peer_frame_fingerprint')) { return $false }
    $routeKey = $identity + @('authority_schema', 'authority_id', 'transition_generation', 'transition_kind',
        'source_route', 'source_protocol_version', 'source_transport', 'source_live_route_epoch', 'source_runtime_generation',
        'source_route_publication_generation', 'source_route_commit_generation', 'source_terminal_generation',
        'source_leg_generation', 'source_transport_epoch', 'source_bridge_recovery_generation', 'source_data_session_generation',
        'target_route', 'target_protocol_version', 'target_transport', 'target_transport_epoch', 'peer_proof_generation',
        'peer_proof_kind', 'peer_proof_transport_epoch', 'retained_frame_fingerprint', 'checkpoint_generation',
        'checkpoint_state_epoch', 'checkpoint_contiguous_frontier', 'checkpoint_durable_highest', 'checkpoint_chunk_count')
    if (-not (Equal $drain $prepare $routeKey) -or -not (Equal $commit $prepare $routeKey) -or
        -not (Equal $disposition $prepare ($routeKey + $source + $durable)) -or
        (Field $drain 'decision_status') -cne 'Draining' -or (Field $commit 'decision_status') -cne 'Committed' -or
        (Field $disposition 'decision_status') -cne 'Applied' -or
        (Field $disposition 'decision_reason') -cne 'filetransfer_route_transition_committed' -or
        $prepare.WriterSequence -le $application.WriterSequence -or $drain.WriterSequence -le $prepare.WriterSequence -or
        $commit.WriterSequence -le $drain.WriterSequence -or $commit.WriterSequence -le $binding.WriterSequence -or
        $disposition.WriterSequence -le $commit.WriterSequence -or $publication.WriterSequence -le $disposition.WriterSequence) { return $false }
    if (-not (Equal $publication $commit ($identity + @('transition_generation'))) -or
        (Field $publication 'projection_source') -cne 'coordinator_route_transition' -or
        (Field $publication 'route_publication_action_kind') -cne 'RoutePublication' -or
        (Field $publication 'reason') -cne 'filetransfer_route_transition_committed') { return $false }
    foreach ($name in @('route', 'protocol_version', 'live_route_epoch', 'runtime_generation', 'terminal_generation',
            'leg_generation', 'route_commit_generation', 'route_publication_generation')) {
        $target = Field $commit "target_$name"
        if ([string]::IsNullOrWhiteSpace($target) -or $target -cne (Field $publication $name)) { return $false }
        if ($name -notin @('route', 'protocol_version') -and $target -cne (Field $disposition "resulting_$name")) { return $false }
    }

    # A terminal after closure does not recover anything and cannot reopen the
    # consumed obligation. Before closure, cancellation/mutation poisons it.
    foreach ($record in $Episode.CompletionRecords) {
        if ($record.WriterSequence -le $Episode.PendingSequence) { continue }
        if ($record.WriterSequence -ge $BeforeSequence -and $record.WriterSequence -le $publication.WriterSequence -and
            $record.EventName -in @('filetransfer_fallback_v6_proof_bootstrap_observed', 'filetransfer_fallback_v6_proof_bootstrap_decided',
                'filetransfer_route_transition_prepared', 'filetransfer_route_transition_committed')) { return $false }
        if ($record.EventName -eq 'filetransfer_route_hint_authorized_publication_accepted' -and
            $record.WriterSequence -gt $commit.WriterSequence -and
            -not (Equal $record $publication ($identity + @('route', 'protocol_version', 'live_route_epoch',
                'runtime_generation', 'terminal_generation', 'leg_generation', 'route_commit_generation', 'route_publication_generation')))) { return $false }
        if ($record.EventName -in @('filetransfer_route_transition_invalidated', 'filetransfer_data_session_availability_changed') -or
            ($record.WriterSequence -gt $publication.WriterSequence -and $record.EventName -in @(
                'filetransfer_fallback_v6_proof_bootstrap_observed', 'filetransfer_fallback_v6_proof_bootstrap_decided',
                'filetransfer_route_transition_prepared', 'filetransfer_route_transition_committed')) -or
            ($record.WriterSequence -le $publication.WriterSequence -and $record.EventName -in @(
                'file_transfer_inbound_terminal', 'file_transfer_outbound_terminal', 'transfer_terminal', 'filetransfer_runtime_terminalized'))) { return $false }
    }
    return $true
}

function Test-FileTransferFallbackV6BootstrapSourceHoldConsumed {
    param($Completed, [object[]]$History)

    $exchange = $Completed.Operation.routeTransition
    $initiation = $exchange.bootstrapInitiation
    $holds = @($History | Where-Object {
        $_.Operation.operationRole -ceq 'fallback_bootstrap_source_hold' -and
        $_.Operation.authorityId -ceq $initiation.holdIdentity -and
        $_.Operation.operationGeneration -eq $initiation.holdGeneration -and
        $_.Boundary.decisionStatus -ceq 'Accepted' -and $_.Operation.stateApplied
    })
    $drained = @($holds | Where-Object {
        [long]$_.Boundary.writerSequence -le [long]$Completed.Boundary.writerSequence -and $_.Operation.afterState -ceq 'Drained'
    } | Select-Object -Last 1)
    if ($drained.Count -ne 1) { return $false }
    $hold = $drained[0].Operation.routeTransition.bootstrapInitiation
    foreach ($field in @('holdIdentity','holdGeneration','holdCommandFingerprint','sourceFingerprint','frozenCheckpointFingerprint')) {
        if ([string]$hold.$field -cne [string]$initiation.$field) { return $false }
    }
    if ($hold.drainReceiptFingerprint -cnotmatch '^fte1-[a-f0-9]{24}$' -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $hold.sourceBoundary $exchange.sourceBoundary)) { return $false }
    $lost = @($holds | Where-Object {
        [long]$_.Boundary.writerSequence -ge [long]$Completed.Boundary.writerSequence -and $_.Operation.afterState -cne 'Drained'
    } | Select-Object -First 1)
    if ($lost.Count -eq 0) { return $true }

    # Cleanup cannot reopen an obligation already consumed by this exact
    # Coordinator fallback. A later commit or an observation is not authority.
    $cycle = @($History | Where-Object {
        [long]$_.Boundary.writerSequence -gt [long]$Completed.Boundary.writerSequence -and
        [long]$_.Boundary.writerSequence -lt [long]$lost[0].Boundary.writerSequence
    })
    $commits = @($cycle | Where-Object {
        $_.Boundary.decisionKind -ceq 'CommitFileTransferRouteTransition' -and $_.Boundary.decisionStatus -ceq 'Committed' -and
        $_.Operation.operationRole -ceq 'terminal' -and $_.Operation.beforeState -ceq 'None' -and
        $_.Operation.afterState -ceq 'Committed' -and $_.Operation.stateApplied -and
        $_.Operation.routeTransition.transitionKind -ceq 'TunaV4ToPostTunaFallbackV6'
    })
    if ($commits.Count -ne 1) { return $false }
    $commit = $commits[0]; $operation = $commit.Operation; $route = $operation.routeTransition
    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint $exchange.sourceBoundary $route.sourceBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint $route.sourceBoundary $commit.Boundary.beforeBoundary) -or
        $route.durableCheckpointFingerprint -cne $exchange.durableCheckpointFingerprint -or
        $route.retainedFrameFingerprint -cne $exchange.bootstrapCorrelation.retainedPeerFrameIdentityFingerprint -or
        $commit.Boundary.afterBoundary.route -cne 'post_tuna_fallback_v6' -or $commit.Boundary.afterBoundary.protocolVersion -ne 6 -or
        $commit.Boundary.afterBoundary.liveRouteEpoch -ne ([long]$exchange.sourceBoundary.liveRouteEpoch + 1)) { return $false }
    $prepared = @($cycle | Where-Object {
        [long]$_.Boundary.writerSequence -lt [long]$commit.Boundary.writerSequence -and
        $_.Boundary.decisionKind -ceq 'PrepareFileTransferRouteTransition' -and
        $_.Operation.operationRole -ceq 'current' -and $_.Operation.beforeState -ceq 'None' -and
        $_.Operation.afterState -ceq 'Prepared' -and $_.Operation.stateApplied -and
        $_.Operation.authorityId -ceq $operation.authorityId -and $_.Operation.operationGeneration -eq $operation.operationGeneration
    })
    $disposed = @($cycle | Where-Object {
        [long]$_.Boundary.writerSequence -gt [long]$commit.Boundary.writerSequence -and
        $_.Boundary.decisionKind -ceq 'ApplyFileTransferRouteTransitionDispositionReceipt' -and $_.Boundary.decisionStatus -ceq 'Committed' -and
        $_.Operation.operationRole -ceq 'terminal' -and $_.Operation.afterState -ceq 'Committed' -and $_.Operation.stateApplied -and
        $_.Operation.authorityId -ceq $operation.authorityId -and $_.Operation.operationGeneration -eq $operation.operationGeneration
    })
    if ($prepared.Count -ne 1 -or $disposed.Count -ne 1) { return $false }
    foreach ($other in @($prepared[0].Operation, $disposed[0].Operation)) {
        foreach ($field in @('transitionKind','durableCheckpointFingerprint','proofBoundaryFingerprint','targetRoute','targetProtocolVersion')) {
            if ([string]$other.routeTransition.$field -cne [string]$route.$field) { return $false }
        }
        if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $other.routeTransition.sourceBoundary $route.sourceBoundary)) { return $false }
    }
    foreach ($terminal in @($commit, $disposed[0])) {
        $target = [ordered]@{}
        foreach ($property in $terminal.Operation.routeTransition.targetBoundary.PSObject.Properties) {
            $target[$property.Name] = if ($property.Name -cin @('dataSessionIdentity','checkpointRequestId')) {
                ConvertTo-FileTransferCoordinatorEvidenceToken $property.Value
            } else { $property.Value }
        }
        if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual ([pscustomobject]$target) $terminal.Boundary.afterBoundary) -or
            $terminal.Operation.routeTransition.retainedFrameFingerprint -cne $route.retainedFrameFingerprint) { return $false }
    }
    foreach ($stage in @('pause_bind','drain','commit')) {
        $receipt = @($route.receipts | Where-Object stage -CEQ $stage)
        $preserved = @($disposed[0].Operation.routeTransition.receipts | Where-Object stage -CEQ $stage)
        if ($receipt.Count -ne 1 -or -not $receipt[0].receiptValidated -or $preserved.Count -ne 1 -or
            (ConvertTo-Json $receipt[0] -Compress) -cne (ConvertTo-Json $preserved[0] -Compress)) { return $false }
    }
    foreach ($stage in @('retained_frame','disposition')) {
        $receipt = @($disposed[0].Operation.routeTransition.receipts | Where-Object stage -CEQ $stage)
        if ($receipt.Count -ne 1 -or -not $receipt[0].receiptValidated -or $receipt[0].receiptStatus -cnotin @('Applied','Idempotent')) { return $false }
    }
    return $true
}

# A validated correlated exchange owns its exact legacy diagnostic; this does
# not recover a route, publish anything, or waive any independent snapshot gate.
function Test-FileTransferFallbackV6BootstrapCorrelatedEpisodeConsumed {
    param($Episode, [long]$BeforeSequence, $SnapshotEvaluation)

    if ($null -eq $SnapshotEvaluation -or $Episode.ConflictingStart -or
        $SnapshotEvaluation.Mode -cne 'SnapshotV1Authoritative' -or
        $SnapshotEvaluation.ArtifactEvidenceVerdict -cne 'pass' -or -not $SnapshotEvaluation.StreamComplete -or
        $SnapshotEvaluation.AuthoritativeFamilies -cnotcontains 'route_transition' -or
        $SnapshotEvaluation.ShadowFamilies -ccontains 'route_transition' -or
        -not (Test-FileTransferCoordinatorBootstrapCorrelationCatalogCapability $SnapshotEvaluation.CatalogVersion)) { return $false }

    $triggers = @($Episode.TriggerRecords | Where-Object {
        $_.WriterSequence -gt $Episode.PendingSequence -and $_.WriterSequence -lt $BeforeSequence -and
        $_.Fields['status'] -ceq 'Ready'
    } | Sort-Object Line -Unique)
    if ($triggers.Count -ne 1) { return $false }
    $trigger = $triggers[0]
    $fields = $trigger.Fields
    foreach ($record in $Episode.TriggerRecords.ToArray() + $Episode.Candidates.ToArray()) {
        foreach ($name in @('transfer_id', 'direction')) {
            if ($record.Fields.ContainsKey($name) -and $record.Fields[$name] -cne $fields[$name]) { return $false }
        }
        $seen = @{}
        foreach ($match in [regex]::Matches($record.Line, '(?:^|;\s*)([^=;]+)=([^;]*)')) {
            $name = $match.Groups[1].Value.Trim(); $value = $match.Groups[2].Value.Trim()
            if ($seen.ContainsKey($name) -and $seen[$name] -cne $value) { return $false }
            $seen[$name] = $value
        }
    }
    if ($fields['direction'] -cne 'inbound' -or $fields['bulk_seen'] -cne '1' -or
        $fields['control_seen'] -cne '1' -or $fields['authority_effect'] -cne 'none' -or
        $fields['pending_generation'] -notmatch '^[1-9][0-9]*$' -or
        $fields['source_route'] -cne 'file_tuna_v4' -or $fields['source_protocol_version'] -cne '4') { return $false }

    function IdentityMatches([string]$Physical, [string]$Snapshot) {
        if ([string]::IsNullOrWhiteSpace($Physical) -or $Physical -cin @('none', '(none)')) { return $false }
        # Redaction cannot identify a stream. Only the unique writer/source join
        # below can bind these diagnostics to validated Coordinator history.
        if ($Physical -cin @('[redacted]', 'sess_[redacted]')) { return $true }
        if ($Physical -cmatch '^fte1-[a-f0-9]{24}$') { return $Physical -ceq $Snapshot }
        return (ConvertTo-FileTransferCoordinatorEvidenceToken $Physical) -ceq $Snapshot
    }
    $sourceFields = @(
        @('source_route','route'), @('source_protocol_version','protocolVersion'),
        @('source_live_route_epoch','liveRouteEpoch'), @('source_runtime_generation','runtimeGeneration'),
        @('source_route_publication_generation','routePublicationGeneration'), @('source_route_commit_generation','routeCommitGeneration'),
        @('source_terminal_generation','terminalGeneration'), @('source_leg_generation','legGeneration'),
        @('source_transport_epoch','transportEpoch'), @('source_bridge_recovery_generation','bridgeRecoveryGeneration'),
        @('source_checkpoint_request_id','checkpointRequestId'), @('source_checkpoint_generation','checkpointGeneration'),
        @('data_session_generation','dataSessionGeneration'))
    foreach ($record in $Episode.TriggerRecords) {
        if ($record.WriterSequence -le $Episode.PendingSequence -or $record.Fields['status'] -cne 'Ready') { continue }
        foreach ($name in @('session_id','transfer_id','direction','pending_generation') + @($sourceFields | ForEach-Object { $_[0] })) {
            if ($record.Fields[$name] -cne $fields[$name]) { return $false }
        }
    }
    $history = [Collections.Generic.List[object]]::new()
    $owners = @{}
    foreach ($boundary in $SnapshotEvaluation.Boundaries) {
        if ($boundary.writer -cne $Episode.Writer -or $boundary.direction -cne $fields['direction'] -or
            [long]$boundary.writerSequence -ge $BeforeSequence -or
            -not (IdentityMatches $fields['session_id'] $boundary.sessionId) -or
            -not (IdentityMatches $fields['transfer_id'] $boundary.transferId)) { continue }
        foreach ($operation in @($boundary.operations | Where-Object {
            $_.operationRole -cin @('fallback_bootstrap_exchange','fallback_bootstrap_source_hold','current','terminal')
        })) {
            $stream = "$($boundary.coordinatorInstanceId)|$($boundary.sessionId)|$($boundary.transferId)|$($boundary.direction)"
            $key = "$stream|$($operation.operationKey)"
            $entry = [pscustomobject]@{ Boundary=$boundary; Operation=$operation; Stream=$stream; Key=$key }
            $history.Add($entry)
            if ($operation.operationRole -cne 'fallback_bootstrap_exchange') { continue }
            $matches = $true
            foreach ($mapping in $sourceFields) {
                $value = [string]$fields[$mapping[0]]
                if ([string]::IsNullOrWhiteSpace($value) -or
                    $value -cne [string]$operation.routeTransition.sourceBoundary.($mapping[1])) { $matches = $false; break }
            }
            if ($matches -and $boundary.decisionStatus -ceq 'Accepted') { $owners[$key] = $entry }
        }
    }
    if ($owners.Count -ne 1) { return $false }
    $owner = @($owners.Values)[0]
    $streamHistory = @($history | Where-Object Stream -CEQ $owner.Stream | Sort-Object { [long]$_.Boundary.writerSequence })
    $entries = @($streamHistory | Where-Object { $_.Operation.operationRole -ceq 'fallback_bootstrap_exchange' })
    $completed = @($entries | Where-Object {
        $_.Key -ceq $owner.Key -and $_.Boundary.decisionStatus -ceq 'Accepted' -and
        [long]$_.Boundary.writerSequence -gt $Episode.PendingSequence -and
        $_.Operation.stateApplied -and $_.Operation.beforeState -ceq 'Pending' -and $_.Operation.afterState -ceq 'Completed'
    })
    if ($completed.Count -ne 1) { return $false }
    if (@($entries | Where-Object {
        [long]$_.Boundary.writerSequence -gt [long]$completed[0].Boundary.writerSequence -and
        $_.Boundary.decisionStatus -ceq 'Accepted' -and
        ($_.Key -cne $owner.Key -or $_.Operation.afterState -cne 'Completed' -or
            $_.Operation.routeTransition.bootstrapCorrelation.invalidated)
    }).Count -gt 0) { return $false }
    $latest = @($entries | Where-Object { $_.Boundary.decisionStatus -ceq 'Accepted' })[-1]
    $correlation = $latest.Operation.routeTransition.bootstrapCorrelation
    if ($SnapshotEvaluation.CatalogVersion -cin @($script:FileTransferCoordinatorBootstrapInitiationCatalogVersion,
            $script:FileTransferCoordinatorBootstrapCallbackCatalogVersion) -and
        -not (Test-FileTransferFallbackV6BootstrapSourceHoldConsumed $completed[0] $streamHistory)) { return $false }
    return $latest.Key -ceq $owner.Key -and $latest.Operation.afterState -ceq 'Completed' -and
        $correlation.agreementEnabled -and $correlation.version -eq 1 -and
        $correlation.completed -and -not $correlation.invalidated -and
        $correlation.peerIdFingerprint -cmatch '^fte1-[a-f0-9]{24}$' -and
        $correlation.peerIdFingerprint -ceq $correlation.completedPeerIdFingerprint -and
        $correlation.retainedPeerFrameIdentityFingerprint -cmatch '^fte1-[a-f0-9]{24}$'
}

function Get-FileTransferFallbackV6BootstrapDirectionCorrelation {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Lines,
        $SnapshotEvaluation = $null
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
            'filetransfer_fallback_v6_proof_bootstrap_decided',
            'filetransfer_fallback_v6_proof_bootstrap_execution_completed',
            'filetransfer_fallback_v6_proof_bootstrap_receipt_applied',
            'filetransfer_fallback_v6_proof_bootstrap_peer_boundary_bound',
            'filetransfer_route_transition_prepared',
            'filetransfer_route_transition_draining',
            'filetransfer_route_transition_committed',
            'filetransfer_route_transition_disposition_applied',
            'filetransfer_live_route_epoch_recovered',
            'filetransfer_route_hint_authorized_publication_accepted',
            'filetransfer_route_transition_invalidated',
            'filetransfer_data_session_availability_changed',
            'file_transfer_inbound_terminal', 'file_transfer_outbound_terminal',
            'transfer_terminal', 'filetransfer_runtime_terminalized')) {
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

        $fields = @{}
        foreach ($match in [regex]::Matches($line, '(?:^|;\s*)([^=;]+)=([^;]*)')) {
            $name = $match.Groups[1].Value.Trim()
            if (-not $fields.ContainsKey($name)) { $fields[$name] = $match.Groups[2].Value.Trim() }
        }
        $records.Add([pscustomobject]@{
                EventName = $eventName
                Writer = $writer
                Session = $session
                WriterSequence = $writerSequence
                InputIndex = [long]$index
                Line = $line
                RawLine = $rawLine
                Fields = $fields
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
                    StartedLine = $record.Line
                    ConflictingStart = $false
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
                    CompletionRecords = New-Object System.Collections.Generic.List[object]
                }
            }
            elseif ($episodes[$episodeKey].StartedSequence -ne $record.WriterSequence -or
                $episodes[$episodeKey].StartedLine -cne $record.Line) {
                $episodes[$episodeKey].ConflictingStart = $true
            }
            $currentByWriterSession[$scopeKey] = $episodes[$episodeKey]
            continue
        }

        if (-not $currentByWriterSession.ContainsKey($scopeKey)) {
            continue
        }
        $episode = $currentByWriterSession[$scopeKey]
        $episode.CompletionRecords.Add($record) | Out-Null

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
        if (Test-FileTransferFallbackV6BootstrapCorrelatedEpisodeConsumed -Episode $episode -BeforeSequence $firstWait.WriterSequence -SnapshotEvaluation $SnapshotEvaluation) {
            continue
        }
        if (Test-FileTransferFallbackV6BootstrapPeerFirstEpisodeConsumed -Episode $episode -BeforeSequence $firstWait.WriterSequence) {
            continue
        }
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
        [Parameter(Mandatory = $true)]$SnapshotProof,
        $SnapshotEvaluation = $null
    )

    [string[]]$lines = @($Events | ForEach-Object { [string]$_.RawLine })
    $directionCorrelation = if ($null -eq $SnapshotEvaluation) {
        Get-FileTransferFallbackV6BootstrapDirectionCorrelation -Lines $lines
    } else {
        Get-FileTransferFallbackV6BootstrapDirectionCorrelation -Lines $lines -SnapshotEvaluation $SnapshotEvaluation
    }
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
