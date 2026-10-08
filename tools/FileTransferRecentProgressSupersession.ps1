Set-StrictMode -Version Latest

# Consumes only the caller's already validated snapshot. The old wake is
# diagnostic-only; no recheck, repair success or runtime authority is invented.
function Get-FileTransferRecentProgressSupersessionClosure {
    param([object[]]$Events,$Episode,$SnapshotEvidence,[ref]$FailureReason)
    function F($e,[string]$n) { [string]$e.Fields[$n] }
    function N($e,[string]$n) {
        $v=0L
        if (-not [long]::TryParse((F $e $n),[ref]$v)) { throw "recent_supersession_number_invalid:$n" }
        return $v
    }
    function T($e,[string]$n) {
        $v=[DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse((F $e $n),[ref]$v)) { throw "recent_supersession_time_invalid:$n" }
        return $v.UtcTicks
    }
    function Equal($a,$b,[string[]]$names) {
        foreach ($n in $names) { if ((F $a $n) -ceq '' -or (F $a $n) -cne (F $b $n)) { return $false } }
        return $true
    }
    try {
        if ($null -eq $SnapshotEvidence) { throw 'recent_supersession_snapshot_required' }
        $repair=Resolve-FileTransferRepairOwnerEvidence $SnapshotEvidence $Episode.Grant
        if ($null -eq $repair -or $repair.Boundary.writer -cne $Episode.Writer) { throw 'recent_supersession_repair_source_unbound' }
        $owner=$repair.Operation.repairOwner.scope
        $sourceNames=@('route','protocol_version','live_route_epoch','runtime_generation','terminal_generation','leg_generation',
            'transport_epoch','bridge_recovery_generation','bridge_decision_generation','checkpoint_request_id',
            'route_publication_generation','route_commit_generation')
        $wakeNames=$sourceNames+@('direction','checkpoint_generation','data_session_identity','data_session_generation',
            'frontier_chunk_index','committed_progress_utc','wake_deadline_utc')
        $seen=@{};$scoped=[Collections.Generic.List[object]]::new()
        foreach ($e in @($Events | Sort-Object Sequence,LineNumber)) {
            if ((F $e 'log_writer_id') -cne $Episode.Writer) { continue }
            $key=F $e 'log_writer_sequence'
            if ($key -cnotmatch '^[1-9][0-9]*$') { throw 'recent_supersession_writer_sequence_missing' }
            $same=(F $e 'session_id') -ceq $Episode.Session -and (F $e 'transfer_id') -ceq $Episode.Transfer
            if ($seen.ContainsKey($key)) {
                $prior=$seen[$key]
                $names=@($e.Fields.Keys | Where-Object { $_ -cne 'gui_evidence_sequence' })
                if ($names.Count -ne @($prior.Fields.Keys | Where-Object { $_ -cne 'gui_evidence_sequence' }).Count -or
                    -not (Equal $e $prior $names)) {
                    if ($same -or ((F $prior 'session_id') -ceq $Episode.Session -and (F $prior 'transfer_id') -ceq $Episode.Transfer)) {
                        throw 'recent_supersession_writer_conflict'
                    }
                }
                continue
            }
            $seen[$key]=$e
            if ($same) { $scoped.Add($e) }
        }
        $commits=@($scoped | Where-Object { $_.EventName -ceq 'filetransfer_route_transition_committed' -and
            (F $_ 'direction') -ceq 'inbound' -and [long]$_.Sequence -gt [long]$Episode.Event.Sequence } | Select-Object -First 1)
        if ($commits.Count -ne 1) { throw 'recent_supersession_commit_missing' }
        $commit=$commits[0]
        $arms=@($scoped | Where-Object { $_.EventName -ceq 'filetransfer_v6_frontier_recent_progress_grace_deferred' -and
            [long]$_.Sequence -gt [long]$Episode.Progress.Sequence -and [long]$_.Sequence -lt [long]$commit.Sequence })
        $roots=@($arms | Where-Object { [long]$_.Sequence -le [long]$Episode.Event.Sequence } | Select-Object -First 1)
        if ($roots.Count -ne 1) { throw 'recent_supersession_arm_missing' }
        $root=$roots[0];$deadline=T $root 'wake_deadline_utc';$progress=T $root 'committed_progress_utc'
        if (-not (Equal $root $Episode.Progress $sourceNames) -or (F $root 'direction') -cne 'inbound' -or
            (N $root 'frontier_chunk_index') -ne $Episode.Frontier -or
            (N $root 'highest_received_chunk_index') -lt (N $Episode.Event 'highest_received_chunk_index') -or
            (F $root 'data_session_identity') -cne $Episode.DataSessionIdentity -or
            (F $root 'data_session_generation') -cne $Episode.DataSessionGeneration -or
            (N $root 'checkpoint_generation') -ne $owner.retainedLegCheckpointGeneration -or
            (ConvertTo-FileTransferCoordinatorEvidenceToken (F $root 'checkpoint_request_id')) -cne $owner.retainedLegCheckpointRequestId -or
            (Get-FileTransferDiagnosticScopeEvidenceToken $root 'data_session_identity') -cne $owner.boundary.dataSessionIdentity -or
            (N $root 'data_session_generation') -ne $owner.boundary.dataSessionGeneration -or
            $deadline -ne $progress+[TimeSpan]::TicksPerSecond -or $progress -le 0 -or
            (N $Episode.Progress 'log_writer_attempt_utc_ticks') -lt $progress -or
            (N $Episode.Progress 'log_writer_attempt_utc_ticks') -gt (N $root 'log_writer_attempt_utc_ticks') -or
            (N $Episode.Event 'log_writer_attempt_utc_ticks') -ge $deadline) { throw 'recent_supersession_arm_scope_invalid' }
        $generation=0L;$highest=-1L;$ticks=0L
        foreach ($arm in @($root)+@($arms | Where-Object { [long]$_.Sequence -gt [long]$root.Sequence })) {
            $next=N $arm 'wake_generation';$nextHighest=N $arm 'highest_received_chunk_index';$time=N $arm 'log_writer_attempt_utc_ticks'
            if (-not (Equal $arm $root $wakeNames) -or (F $arm 'authority_effect') -cne 'none' -or
                (Get-FileTransferDiagnosticScopeEvidenceToken $arm 'data_session_identity') -cne $owner.boundary.dataSessionIdentity -or
                (F $arm 'decision_status') -cnotin @('held','rearmed') -or $next -le 0 -or $time -ge $deadline -or
                $time -lt $progress -or $time -lt $ticks -or ($generation -gt 0 -and
                    ($next -ne $generation+1 -or $nextHighest -le $highest -or (F $arm 'decision_status') -cne 'rearmed'))) {
                throw 'recent_supersession_rearm_chain_invalid'
            }
            $generation=$next;$highest=$nextHighest;$ticks=$time
        }
        $id=ConvertTo-FileTransferCoordinatorEvidenceToken (F $commit 'authority_id')
        $boundaries=@($SnapshotEvidence.Boundaries | Where-Object { $_.writer -ceq $Episode.Writer -and
            $_.coordinatorInstanceId -ceq $repair.Boundary.coordinatorInstanceId -and $_.sessionId -ceq $repair.Boundary.sessionId -and
            $_.transferId -ceq $repair.Boundary.transferId -and $_.direction -ceq 'inbound' -and
            $_.decisionKind -ceq 'CommitFileTransferRouteTransition' -and $_.decisionStatus -ceq 'Committed' -and
            @($_.operations | Where-Object { $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'terminal' -and
                $_.authorityId -ceq $id -and $_.operationGeneration -eq (N $commit 'transition_generation') -and
                $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Committed' -and $_.stateApplied -and
                $_.receiptValidated -and $_.receiptStatus -ceq 'Committed' }).Count -eq 1 })
        if ($boundaries.Count -ne 1) { throw 'recent_supersession_commit_unbound' }
        $boundary=$boundaries[0]
        if (-not (Test-FileTransferCheckpointRepairContinuityAggregateScope $owner $boundary.beforeBoundary) -or
            $repair.Boundary.afterStateGeneration -gt $boundary.beforeStateGeneration) { throw 'recent_supersession_source_changed' }
        $why='';$proof=Get-FileTransferCoordinatorTunaSupersessionProof $SnapshotEvidence $boundary.boundaryId ([ref]$why) $Events
        if ($null -eq $proof) { throw "recent_supersession_target_unproven:$why" }
        $typed=@($boundary.operations | Where-Object { $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'terminal' -and
            $_.authorityId -ceq $id -and $_.operationGeneration -eq (N $commit 'transition_generation') -and
            $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Committed' -and $_.stateApplied -and
            $_.receiptValidated -and $_.receiptStatus -ceq 'Committed' })[0].routeTransition
        $map=@{route='route';protocol_version='protocolVersion';live_route_epoch='liveRouteEpoch';runtime_generation='runtimeGeneration';
            terminal_generation='terminalGeneration';leg_generation='legGeneration';route_publication_generation='routePublicationGeneration';
            route_commit_generation='routeCommitGeneration';transport_epoch='transportEpoch';bridge_recovery_generation='bridgeRecoveryGeneration'}
        foreach ($n in $map.Keys) {
            if ((F $commit "source_$n") -cne (F $root $n) -or
                (F $commit "source_$n") -cne [string]$typed.sourceBoundary.($map[$n])) { throw 'recent_supersession_source_tuple_invalid' }
        }
        if ((F $commit 'authority_schema') -cne '2' -or (F $commit 'decision_status') -cne 'Committed' -or
            (F $commit 'transition_kind') -cne 'PostTunaFallbackV6ToTunaV4' -or (F $commit 'source_transport') -cne 'regular_nkn' -or
            (F $commit 'target_route') -cne 'file_tuna_v4' -or (F $commit 'target_protocol_version') -cne '4' -or (F $commit 'target_transport') -cne 'tuna' -or
            (F $commit 'data_session_identity') -cne $Episode.DataSessionIdentity -or (F $commit 'source_data_session_generation') -cne $Episode.DataSessionGeneration -or
            (N $commit 'checkpoint_contiguous_frontier') -ne $Episode.Frontier -or (N $commit 'checkpoint_durable_highest') -ne $highest -or
            (N $commit 'checkpoint_generation') -ne $typed.sourceBoundary.checkpointGeneration -or
            (N $commit 'checkpoint_generation') -le 0 -or
            $typed.sourceBoundary.checkpointRequestId -cne (ConvertTo-FileTransferCoordinatorEvidenceToken "checkpoint:$(N $commit 'checkpoint_generation')")) {
            throw 'recent_supersession_transition_invalid'
        }
        $dispositions=@($scoped | Where-Object { $_.EventName -ceq 'filetransfer_route_transition_disposition_applied' -and
            (F $_ 'authority_schema') -ceq '2' -and
            (F $_ 'authority_id') -ceq (F $commit 'authority_id') -and (F $_ 'direction') -ceq 'inbound' })
        $publications=@($scoped | Where-Object { $_.EventName -ceq 'filetransfer_live_route_epoch_recovered' -and
            (F $_ 'direction') -ceq 'inbound' -and (F $_ 'transition_generation') -ceq (F $commit 'transition_generation') })
        if ($dispositions.Count -ne 1 -or $publications.Count -ne 1) { throw 'recent_supersession_projection_ambiguous' }
        $disposition=$dispositions[0];$publication=$publications[0]
        $applied=@($SnapshotEvidence.Boundaries | Where-Object boundaryId -CEQ $proof.LocalTarget.DispositionBoundaryId)[0]
        if (-not (Equal $disposition $commit (@('transition_generation','transition_kind','data_session_identity','checkpoint_generation','checkpoint_request_id',
                    'source_data_session_generation','target_route','target_protocol_version','target_transport')+@($map.Keys | ForEach-Object {"source_$_"}))) -or
            (F $disposition 'decision_status') -cne 'Applied' -or (F $disposition 'decision_reason') -cne 'filetransfer_route_transition_committed' -or
            (F $publication 'projection_source') -cne 'coordinator_route_transition' -or (F $publication 'reason') -cne 'filetransfer_route_transition_committed' -or
            (F $publication 'route_publication_action_kind') -cne 'RouteCommitPublication' -or
            [long]$boundary.writerSequence -ge (N $commit 'log_writer_sequence') -or
            [long]$applied.writerSequence -ge (N $disposition 'log_writer_sequence')) { throw 'recent_supersession_projection_invalid' }
        foreach ($n in @('route','protocol_version','live_route_epoch','runtime_generation','terminal_generation','leg_generation','route_publication_generation','route_commit_generation')) {
            if ((F $commit "target_$n") -cne [string]$proof.LocalTarget.TargetBoundary.($map[$n]) -or
                (F $publication $n) -cne (F $commit "target_$n") -or
                ($n -cnotin @('route','protocol_version') -and (F $disposition "resulting_$n") -cne (F $commit "target_$n"))) {
                throw 'recent_supersession_target_tuple_invalid'
            }
        }
        $publicationIndex=New-FileTransferPublicationEvidenceIndex $SnapshotEvidence
        $binding=Resolve-FileTransferPublicationEvidence $publicationIndex $publication
        if ($null -eq $binding -or $binding.CoordinatorId -cne $repair.Boundary.coordinatorInstanceId -or
            $binding.TokenFingerprint -cne $proof.LocalTarget.PublicationFingerprint) { throw 'recent_supersession_publication_unbound' }
        $sequence=[long]$Episode.Event.Sequence;$time=N $Episode.Event 'log_writer_attempt_utc_ticks'
        foreach ($e in @($commit,$disposition,$publication)) {
            $next=N $e 'log_writer_attempt_utc_ticks'
            if ([long]$e.Sequence -le $sequence -or $next -lt $time -or $next -ge $deadline) { throw 'recent_supersession_not_predeadline' }
            $sequence=[long]$e.Sequence;$time=$next
        }
        foreach ($b in $SnapshotEvidence.Boundaries) {
            if ($b.coordinatorInstanceId -cne $repair.Boundary.coordinatorInstanceId -or
                $b.afterStateGeneration -le $repair.Boundary.afterStateGeneration -or $b.afterStateGeneration -gt $boundary.beforeStateGeneration) { continue }
            if (-not (Test-FileTransferCheckpointRepairContinuityAggregateScope $owner $b.beforeBoundary) -or
                -not (Test-FileTransferCheckpointRepairContinuityAggregateScope $owner $b.afterBoundary)) { throw 'recent_supersession_source_interval_changed' }
        }
        foreach ($e in $scoped) {
            if ([long]$e.Sequence -le [long]$Episode.Event.Sequence -or [long]$e.Sequence -ge [long]$publication.Sequence -or
                (F $e 'direction') -ceq 'outbound') { continue }
            if ((F $e 'transfer_paused') -ceq '1' -or $e.EventName -cin @('file_transfer_inbound_terminal','filetransfer_live_route_epoch_terminal',
                'filetransfer_terminalized_by_session_end','filetransfer_data_session_removed','filetransfer_data_session_recreated') -or
                ($e.EventName -ceq 'filetransfer_v6_sparse_write_committed' -and (N $e 'contiguous_chunks_committed') -gt 0) -or
                ($e.EventName -ceq 'filetransfer_route_transition_committed' -and -not [object]::ReferenceEquals($e,$commit))) {
                throw 'recent_supersession_interval_poisoned'
            }
            if ($e.EventName -cin @('filetransfer_route_selected','filetransfer_route_hint_authorized_publication_accepted')) {
                $target=Resolve-FileTransferPublicationEvidence $publicationIndex $e
                if ($null -eq $target -or $target.TokenFingerprint -cne $binding.TokenFingerprint -or
                    $target.CoordinatorId -cne $binding.CoordinatorId -or [long]$e.Sequence -le [long]$commit.Sequence) {
                    throw 'recent_supersession_foreign_publication'
                }
            }
        }
        return [pscustomobject]@{Kind='RecentProgressSourceSuperseded';ObligationDisposition='Superseded';
            SourceCommitBoundaryId=$boundary.boundaryId;PublicationWriterSequence=(N $publication 'log_writer_sequence');
            WakeGeneration=$generation;DeadlineUtcTicks=$deadline;LocalTarget=$proof.LocalTarget;PeerTarget=$proof.PeerTarget}
    } catch {
        if ($null -ne $FailureReason) { $FailureReason.Value=$_.Exception.Message }
        return $null
    }
}
