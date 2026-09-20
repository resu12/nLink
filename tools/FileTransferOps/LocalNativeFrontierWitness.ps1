Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'LocalPublicationRows.ps1')

# Optional native diagnostic only. The trusted primary supplies its already
# chosen failure and original proof/views. No correlator or evaluator is called.
function Resolve-FileTransferNativeFrontierWitness {
    param($Custody,$Evaluations,$Context,[string[]]$Full,[string[]]$Compact,$FullView,$CompactView,
        $Payloads,$Index,$Failure,$Candidates,$RecentProof,[ref]$OmissionReason)
    $state=@{Error='native_frontier_unavailable';Details=$null;Marker=$null;Completion=0;Failure=0}
    $batch=$null
    try {
        if($Custody -isnot [NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+EvaluationCustody] -or
            $Payloads -isnot [NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+NativePayloadSet]) {return $null}
        if(-not $Custody.Owns($Evaluations,$Context,$Full,$Compact,$FullView,$CompactView)) {return $null}
        $nativeRows=$Payloads.GetPayloads()
        if($null -eq $nativeRows) {return $null}
        $resolve=[Func[Action,NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+NativeWitnessSelection]]{
            param([Action]$visit)
            $selected=[Collections.Generic.SortedSet[int]]::new(); $rows=[Collections.Generic.List[object]]::new()
            $work=@{Units=0L}; $owned=[Collections.Generic.List[object]]::new(); $receipt=$null; $completionPosition=-1
            function Require([bool]$Value,[string]$Reason) {if(-not $Value) {throw $Reason}}
            function Pick($Row,[switch]$Opaque) {
                Require ($null -ne $Row -and -not $Row.Fields.ContainsKey('__nlink_duplicate_writer_evidence_field') -and
                    ($Opaque -or -not $Row.Fields.ContainsKey('__nlink_duplicate_structured_field'))) 'native_frontier_duplicate_field'
                [void]$selected.Add([int]$Row.Position+1); Require ($selected.Count -le 64) 'native_frontier_witness_capacity'
            }
            function Token([string]$Value) {ConvertTo-FileTransferCoordinatorEvidenceToken $Value}
            function Ticks([string]$Value) {([DateTimeOffset]::Parse($Value,[Globalization.CultureInfo]::InvariantCulture)).UtcTicks}
            function Named([string]$Name,[scriptblock]$Predicate) {
                foreach($row in $owned) {$visit.Invoke(); if($row.Fields.event -ceq $Name -and (& $Predicate $row.Fields)) {$row}}
            }
            function One([string]$Name,[scriptblock]$Predicate) {
                $found=@(Named $Name $Predicate); Require ($found.Count -eq 1) ('native_frontier_unique_'+$Name)
                Pick $found[0]; return $found[0]
            }
            function Scope($Fields,[switch]$Wake) {
                $s=$receipt.Source
                foreach($pair in @(@('route','Route'),@('protocol_version','ProtocolVersion'),@('live_route_epoch','LiveRouteEpoch'),
                    @('leg_generation','LegGeneration'),@('transport_epoch','TransportEpoch'))) {
                    Require ($Fields[$pair[0]] -ceq [string]$s.($pair[1])) 'native_frontier_source_scope'
                }
                Require ((Token $Fields.checkpoint_request_id) -ceq $s.CheckpointRequestId) 'native_frontier_checkpoint'
                if($Wake) {
                    foreach($pair in @(@('runtime_generation','RuntimeGeneration'),@('terminal_generation','TerminalGeneration'),
                        @('route_publication_generation','RoutePublicationGeneration'),@('route_commit_generation','RouteCommitGeneration'),
                        @('bridge_recovery_generation','BridgeRecoveryGeneration'),@('bridge_decision_generation','BridgeDecisionGeneration'),
                        @('checkpoint_generation','CheckpointGeneration'),@('data_session_generation','DataSessionGeneration'))) {
                        Require ($Fields[$pair[0]] -ceq [string]$s.($pair[1])) 'native_frontier_wake_scope'
                    }
                    Require ((Token $Fields.data_session_identity) -ceq $s.DataSessionIdentity) 'native_frontier_data_session'
                }
            }
            function Snapshot {
                Require ($null -ne $Failure -and $Failure.Class -ceq 'filetransfer_fallback_frontier_recent_progress_grace_recheck_missing' -and
                    $Failure.Reason -is [string] -and $Failure.Reason.Length -le 1024 -and $Failure.Variant -is [string] -and $Failure.Variant.Length -le 1024 -and
                    $Candidates.Count -le 8192 -and $RecentProof.UnresolvedCount -ge 1 -and $RecentProof.UnresolvedCandidateRecords.Count -le 8192) 'native_frontier_failure_shape'
                $hits=0; foreach($candidate in $Candidates) {$visit.Invoke(); if([object]::ReferenceEquals($candidate,$Failure)) {$hits++}}
                Require ($hits -eq 1) 'native_frontier_failure_owner'
                $hits=0; foreach($candidate in $RecentProof.UnresolvedCandidateRecords) {
                    $visit.Invoke()
                    if([object]::ReferenceEquals($candidate.Event,$Failure.Event) -and $candidate.Reason -ceq $Failure.Reason -and
                        $candidate.Variant -ceq $Failure.Variant -and -not $candidate.Recovered) {$hits++}
                }
                Require ($hits -eq 1) 'native_frontier_unresolved_owner'
                $viewFields=[Collections.Generic.List[object]]::new()
                foreach($view in @($FullView,$CompactView)) {
                    Require ($view.Mode -ceq 'SnapshotV1Authoritative' -and $view.ContractVersion -eq 1 -and $view.ArtifactEvidenceVerdict -ceq 'pass' -and
                        $view.StreamComplete -and $view.AuthoritativeFamilies.Count -le 64 -and 'route_runtime' -cin $view.AuthoritativeFamilies -and
                        'route_transition' -cin $view.AuthoritativeFamilies -and $view.Boundaries.Count -le 8192) 'native_frontier_view'
                    $viewFields.Add([ordered]@{Mode=$view.Mode;Contract=$view.ContractVersion;Catalog=$view.CatalogVersion;Hash=$view.CatalogHash;
                        BoundaryHash=$view.BoundaryHash;Verdict=$view.ArtifactEvidenceVerdict;Stream=$view.StreamComplete;Families=@($view.AuthoritativeFamilies)})
                }
                foreach($key in @('ContractVersion','CatalogVersion','CatalogHash','BoundaryHash')) {Require ($FullView.$key -ceq $CompactView.$key) 'native_frontier_view_parity'}
                return ([ordered]@{Class=$Failure.Class;Reason=$Failure.Reason;Variant=$Failure.Variant;Event=$Failure.Event.EventName;
                    Line=$Failure.Event.LineNumber;Unresolved=$RecentProof.UnresolvedCount;Recovered=$RecentProof.RecoveredCount;Views=$viewFields.ToArray()} | ConvertTo-Json -Depth 8 -Compress)
            }
            try {
                $before=Snapshot
                Require ($Index.AllEvents.Count -eq $Full.Length -and $Index.LogFiles.Count -eq 1) 'native_frontier_index'
                $failurePosition=-1
                for($i=0;$i -lt $Full.Length;$i++) {
                    $visit.Invoke(); $line=$Full[$i]; $event=$Index.AllEvents[$i]
                    Require ([object]::ReferenceEquals($line,$event.RawLine) -and $event.FilePath -ceq $Index.LogFiles[0] -and
                        $line.Length -le 262144 -and $line.Length -le [NLink.Tools.LocalPublicationInputLimits]::ProducerCodeUnits-$work.Units -and
                        $line.Split([char[]]@(';'),257).Length -le 256) 'native_frontier_input'
                    $work.Units+=$line.Length
                    $fields=ConvertFrom-GuiSmokeLogLedgerStructuredFields $line
                    Require ([regex]::Matches($line,'(?i)(?:^|;)\s*session_id\s*=').Count -le 1 -and
                        [regex]::Matches($line,'(?i)(?:^|;)\s*transfer_id\s*=').Count -le 1) 'native_frontier_duplicate_scope'
                    $rows.Add([pscustomobject]@{Position=$i;Fields=$fields})
                    if([object]::ReferenceEquals($event,$Failure.Event)) {Require ($failurePosition -eq -1) 'native_frontier_failure_duplicate'; $failurePosition=$i}
                }
                Require ($failurePosition -ge 0) 'native_frontier_failure_origin'
                $failureRow=$rows[$failurePosition]; $f=$failureRow.Fields; Pick $failureRow
                Require ($Failure.Event.EventName -ceq 'filetransfer_v6_receiver_state_deferred' -and $f.event -ceq $Failure.Event.EventName -and
                    $f.reason -ceq 'frontier_stalled' -and $f.route -ceq 'post_tuna_fallback_v6' -and $f.protocol_version -ceq '6') 'native_frontier_failure_kind'
                $matches=[Collections.Generic.List[object]]::new()
                foreach($native in $nativeRows) {
                    $visit.Invoke(); $r=$native.Text | ConvertFrom-Json
                    Require ($r.Format -ceq 'nlink-frontier-boundary-producer-v1' -and $r.HashDomain -ceq 'sha256-utf8-frontier-boundary-producer-v1' -and
                        $r.ClockDomain -ceq 'frontier_publication_boundary_utc_qpc_v1' -and [guid]$r.ObservationId -eq $native.ObservationId -and
                        $r.Outcome -cin @('PublishedBeforeOriginalDeadline','PublishedWithinObservationBound') -and -not $r.LiveProofEligible -and
                        -not $r.NativeWriterCustodyEstablished -and -not $r.PublicationCompletionIssued -and $r.Lifecycle.Count -ge 8 -and $r.Lifecycle.Count -le 64) 'native_frontier_receipt_profile'
                    if($native.WriterId -ceq $f.log_writer_id -and $r.Source.SessionId -ceq (Token $f.session_id) -and $r.Source.TransferId -ceq (Token $f.transfer_id) -and
                        @($r.Lifecycle | Where-Object {$_.Kind -ceq 'RepairProgress' -and $_.Binding.After -eq [long]$f.next_chunk_index}).Count -eq 1) {
                        $matches.Add(@{Native=$native;Receipt=$r})
                    }
                }
                Require ($matches.Count -eq 1) 'native_frontier_receipt_ambiguity'
                $chosen=$matches[0].Native; $receipt=$matches[0].Receipt; Scope $f
                foreach($row in $rows) {$visit.Invoke(); if($row.Fields.log_writer_id -ceq $f.log_writer_id -and
                    $row.Fields['session_id'] -ceq $f.session_id -and $row.Fields['transfer_id'] -ceq $f.transfer_id) {$owned.Add($row)}}
                $entries=@{}; $last=0
                foreach($entry in $receipt.Lifecycle) {
                    $visit.Invoke(); $last++; Require ($entry.Sequence -eq $last) 'native_frontier_lifecycle_sequence'
                    if(-not $entries.ContainsKey($entry.Kind)) {$entries[$entry.Kind]=[Collections.Generic.List[object]]::new()}; $entries[$entry.Kind].Add($entry)
                }
                foreach($kind in @('RepairProgress','StallCaptured','RecentRechecked','FrameCaptured','DeliveryBound','AcceptedCompletion',$receipt.Outcome)) {
                    Require ($entries.ContainsKey($kind) -and $entries[$kind].Count -eq 1) 'native_frontier_lifecycle_kind'
                }
                Require ($entries.ContainsKey('RecentArmed') -and $entries.ContainsKey('Write') -and
                    @($entries.Keys | Where-Object {$_ -cnotin @('RepairProgress','RecentArmed','StallCaptured','StallSuccessor','RecentRechecked','Write','FrameCaptured','DeliveryBound','AcceptedCompletion',$receipt.Outcome)}).Count -eq 0 -and
                    $receipt.Lifecycle[0].Kind -ceq 'RepairProgress' -and $receipt.Lifecycle[-1].Kind -ceq $receipt.Outcome) 'native_frontier_lifecycle_order'
                $progressBinding=$entries.RepairProgress[0].Binding; $root=$entries.StallCaptured[0].Binding; $repair=$receipt.Repair
                $progresses=@(Named 'filetransfer_fallback_frontier_repair_progress_observed' {param($x) $x.current_frontier_chunk_index -ceq [string]$progressBinding.After})
                $prior=@($progresses | Where-Object {$_.Position -lt $failurePosition})
                Require ($prior.Count -ge 1) 'native_frontier_progress_missing'; $progress=$prior[-1]; Pick $progress; Scope $progress.Fields
                foreach($pair in @(@('continuity_repair_authority_id','AuthorityId'),@('continuity_repair_generation','DecisionGeneration'),
                    @('continuity_source_fingerprint','SourceScopeFingerprint'),@('continuity_current_scope_fingerprint','CurrentScopeFingerprint'),
                    @('continuity_acceptance_id','AcceptanceIdentity'),@('continuity_accepted_state_generation','AcceptedStateGeneration'),
                    @('continuity_origin_receipt_id','OriginReceiptIdentity'),@('continuity_origin_receipt_fingerprint','OriginReceiptFingerprint'),
                    @('continuity_receipt_id','ContinuityReceiptIdentity'),@('continuity_receipt_fingerprint','ContinuityReceiptFingerprint'))) {
                    Require ($progress.Fields[$pair[0]] -ceq [string]$repair.($pair[1])) 'native_frontier_repair_identity'
                }
                foreach($pair in @(@('repair_request_key','CanonicalRepairKey'),@('range_fingerprint','RangeFingerprint'),
                    @('primary_protocol_repair_request_id','ProtocolRepairRequestId'),@('primary_request_correlation_id','RequestCorrelationId'))) {
                    Require ((Token $progress.Fields[$pair[0]]) -ceq $repair.($pair[1])) 'native_frontier_repair_link'
                }
                Require ($progress.Fields.previous_frontier_chunk_index -ceq [string]$progressBinding.Before -and
                    $root.FrontierChunkIndex -eq $progressBinding.After -and $root.WakeGeneration -eq $receipt.RootGeneration -and
                    $root.DeadlineUtcTicks -eq $receipt.OriginalDeadlineUtcTicks) 'native_frontier_root'
                foreach($entry in $entries.RecentArmed) {
                    $arm=$entry.Binding
                    $row=One 'filetransfer_v6_frontier_recent_progress_grace_deferred' {param($x) $x.wake_generation -ceq [string]$arm.WakeGeneration -and $x.frontier_chunk_index -ceq [string]$root.FrontierChunkIndex}
                    Scope $row.Fields -Wake
                    Require ($row.Position -gt $progress.Position -and (Ticks $row.Fields.committed_progress_utc) -eq $arm.ProgressUtcTicks -and
                        (Ticks $row.Fields.wake_deadline_utc) -eq $arm.DeadlineUtcTicks -and $row.Fields.highest_received_chunk_index -ceq [string]$arm.HighestReceivedChunkIndex -and
                        $row.Fields.authority_effect -ceq 'none' -and $entry.Sequence -lt $entries.StallCaptured[0].Sequence) 'native_frontier_recent_arm'
                }
                $stall=One 'filetransfer_v6_frontier_request_deferred' {param($x) $x.wake_generation -ceq [string]$root.WakeGeneration -and $x.frontier_chunk_index -ceq [string]$root.FrontierChunkIndex}
                Scope $stall.Fields -Wake
                Require ($stall.Position -gt $failurePosition -and $stall.Fields.reason -ceq 'frontier_recent_progress_grace_elapsed' -and
                    (Ticks $stall.Fields.stall_started_utc) -eq $root.StartedUtcTicks -and (Ticks $stall.Fields.wake_deadline_utc) -eq $root.DeadlineUtcTicks) 'native_frontier_stall'
                Require ([long]$f.log_writer_attempt_utc_ticks -lt [long]$entries.RecentArmed[-1].Binding.DeadlineUtcTicks -and
                    [long]$f.highest_received_chunk_index -le [long]$entries.RecentArmed[-1].Binding.HighestReceivedChunkIndex) 'native_frontier_original_obligation'
                $tip=[long]$root.WakeGeneration; $rechecked=$entries.RecentRechecked[0].Binding
                foreach($entry in $receipt.Lifecycle) {
                    if($entry.Kind -ceq 'StallSuccessor') {
                        $link=$entry.Binding; Require ($link.PredecessorGeneration -eq $tip -and $link.SuccessorGeneration -eq $tip+1 -and
                            $entry.Sequence -lt $entries.Write[0].Sequence) 'native_frontier_successor_chain'; $tip=[long]$link.SuccessorGeneration
                        $row=One 'filetransfer_v6_frontier_stall_grace_recheck_decided' {param($x) $x.wake_generation -ceq [string]$tip -and $x.frontier_chunk_index -ceq [string]$root.FrontierChunkIndex -and $x.decision_status -ceq 'rearmed'}
                        Scope $row.Fields -Wake
                        Require ($row.Fields.decision_reason -ceq 'highest_received_advanced' -and $row.Fields.highest_received_chunk_index -ceq [string]$link.HighestReceivedChunkIndex -and
                            (Ticks $row.Fields.wake_deadline_utc) -eq $root.DeadlineUtcTicks -and (Ticks $row.Fields.stall_started_utc) -eq $root.StartedUtcTicks) 'native_frontier_successor'
                    } elseif($entry.Kind -ceq 'RecentRechecked') {Require ($tip -eq $rechecked.StallGeneration -and $tip -eq $receipt.RecheckedGeneration) 'native_frontier_rechecked_tip'}
                }
                Require ($tip -eq $receipt.FirstWriteGeneration -and $rechecked.RecentGeneration -eq $entries.RecentArmed[-1].Binding.WakeGeneration) 'native_frontier_write_tip'
                $recheck=One 'filetransfer_v6_frontier_recent_progress_grace_recheck_decided' {param($x) $x.wake_generation -ceq [string]$rechecked.RecentGeneration -and $x.frontier_chunk_index -ceq [string]$root.FrontierChunkIndex -and $x.decision_status -ceq 'rechecked'}
                Scope $recheck.Fields -Wake; Require ($recheck.Fields.decision_reason -ceq 'stall_grace_armed' -and $recheck.Position -ge $stall.Position) 'native_frontier_recheck'
                $frontier=[long]$root.FrontierChunkIndex; $write=$null
                foreach($entry in $entries.Write) {
                    $w=$entry.Binding; Require ($w.Path -cin @('v4_sparse','v6_sparse') -and $w.Before -eq $frontier -and $w.Count -ge 0 -and $w.After-$w.Before -eq $w.Count -and
                        $entry.Sequence -gt $entries.RecentRechecked[0].Sequence -and $entry.Sequence -lt $entries.FrameCaptured[0].Sequence) 'native_frontier_write_chain'
                    $write=One ('filetransfer_'+$w.Path+'_write_committed') {param($x) $x.next_chunk_index -ceq [string]$w.After -and $x.contiguous_chunks_committed -ceq [string]$w.Count -and $x.highest_received_chunk_index -ceq [string]$w.Highest}
                    Require ($write.Position -gt $recheck.Position) 'native_frontier_write_order'; $frontier=[long]$w.After
                }
                $frame=$receipt.Frame
                Require ($entries.Write[0].Binding.Count -gt 0 -and $frame.ContiguousCommittedChunkIndex -ge $frontier -and
                    $entries.FrameCaptured[0].Binding.Epoch -eq $frame.Epoch -and $entries.FrameCaptured[0].Binding.TransportEpoch -eq $frame.TransportEpoch -and
                    $entries.DeliveryBound[0].Binding.AuthorityId -ceq $receipt.Delivery.AuthorityId -and
                    $entries.DeliveryBound[0].Binding.TokenGeneration -eq $receipt.Delivery.TokenGeneration -and
                    $entries.AcceptedCompletion[0].Binding.TransitionId -ceq $receipt.Completion.TransitionId) 'native_frontier_frame_delivery'
                $sent=One 'filetransfer_v6_receiver_state_sent' {param($x) $x.epoch -ceq [string]$frame.Epoch -and $x.transport_epoch -ceq [string]$frame.TransportEpoch}
                foreach($pair in @(@('contiguous_committed_chunk_index','ContiguousCommittedChunkIndex'),@('durable_received_highest_chunk_index','DurableReceivedHighestChunkIndex'),
                    @('bytes_committed','BytesCommitted'),@('requested_until_chunk_index_exclusive','CreditUntilChunkIndexExclusive'))) {
                    Require ($sent.Fields[$pair[0]] -ceq [string]$frame.($pair[1])) 'native_frontier_state_frame'
                }
                Require ($sent.Position -gt $write.Position -and $sent.Fields.transfer_paused -ceq '0' -and -not $frame.TransferPaused -and
                    $sent.Fields.missing_range_count -ceq [string]$frame.MissingRanges.Count) 'native_frontier_state'
                $markerRows=@($rows | Where-Object {$visit.Invoke(); $_.Fields.event -ceq 'filetransfer_frontier_payload_marker' -and $_.Fields['frontier_payload_observation_id'] -ceq $chosen.ObservationId.ToString('N')})
                Require ($markerRows.Count -eq 1) 'native_frontier_marker_unique'; $marker=$markerRows[0]; Pick $marker; $m=$marker.Fields
                Require ($m.log_writer_id -ceq $chosen.WriterId -and $m.frontier_payload_format -ceq $receipt.Format -and $m.frontier_payload_hash_domain -ceq $receipt.HashDomain -and
                    $m.frontier_payload_sha256 -ceq $chosen.Sha256 -and $m.frontier_payload_byte_count -ceq [string]$chosen.ByteCount -and $m.authority_effect -ceq 'none') 'native_frontier_marker'
                $boundaries=[Collections.Generic.List[object]]::new()
                foreach($view in @($FullView,$CompactView)) {
                    $found=@($view.Boundaries | Where-Object {$visit.Invoke(); $_.coordinatorInstanceId -ceq $receipt.Completion.CoordinatorInstanceId -and $_.boundaryId -ceq $receipt.Completion.TransitionId})
                    Require ($found.Count -eq 1) 'native_frontier_completion_unique'; $b=$found[0]; $lines=$Full
                    if([object]::ReferenceEquals($view,$CompactView)) {$lines=$Compact}
                    $i=[int]$b.evidenceLineIndex
                    Require ($i -ge 0 -and $i -lt $lines.Length -and $b.verdict -ceq 'pass' -and $b.decisionKind -ceq 'FallbackControlPlaneSendCompleted' -and
                        $b.decisionKind -ceq $receipt.Completion.DecisionKind -and $b.decisionStatus -ceq $receipt.Completion.DecisionStatus -and
                        $b.decisionStatus -cin @('Accepted','Idempotent') -and $b.boundaryHash -ceq $receipt.Completion.SnapshotSha256) 'native_frontier_completion'
                    if($completionPosition -lt 0) {$completionPosition=$i}
                    Require ([object]::ReferenceEquals($lines[$i],$Full[$completionPosition])) 'native_frontier_completion_projection'
                    $row=$rows[$completionPosition]; Pick $row; $c=$row.Fields
                    Require ($c.event -ceq 'filetransfer_coordinator_transition_snapshot' -and $c.coordinator_instance_id -ceq $b.coordinatorInstanceId -and
                        $c.transition_id -ceq $b.boundaryId -and $c.snapshot_sha256 -ceq $b.boundaryHash -and $c.log_writer_id -ceq $chosen.WriterId -and
                        $c.log_writer_id -ceq $b.writer -and $c.log_writer_sequence -ceq [string]$b.writerSequence -and $completionPosition -gt $write.Position -and
                        $completionPosition -lt $marker.Position) 'native_frontier_completion_origin'
                    $boundaries.Add(($b | Select-Object coordinatorInstanceId,boundaryId,evidenceLineIndex,verdict,decisionKind,decisionStatus,boundaryHash,writer,writerSequence))
                }
                $boundarySnapshot=$boundaries.ToArray() | ConvertTo-Json -Depth 4 -Compress
                $startCount=0; $endCount=0
                foreach($row in $rows) {
                    $visit.Invoke(); $x=$row.Fields
                    if($x['coordinator_instance_id'] -ceq $receipt.Completion.CoordinatorInstanceId) {
                        Require ($x.log_writer_id -ceq $chosen.WriterId) 'native_frontier_coordinator_writer'
                        if($x.event -ceq 'filetransfer_coordinator_snapshot_stream_started') {$startCount++; Require ($row.Position -lt $progress.Position) 'native_frontier_stream_start'; Pick $row}
                        elseif($x.event -ceq 'filetransfer_coordinator_snapshot_stream_completed') {$endCount++; Require ($row.Position -gt $completionPosition -and $row.Position -lt $marker.Position) 'native_frontier_stream_end'; Pick $row}
                        elseif($row.Position -ge $progress.Position -and $row.Position -le $completionPosition) {Pick $row -Opaque}
                    }
                }
                Require ($startCount -eq 1 -and $endCount -eq 1 -and $sent.Position -lt $marker.Position) 'native_frontier_stream_coverage'
                foreach($row in $owned) {
                    $visit.Invoke(); $x=$row.Fields
                    $episode=$row.Position -ge $progress.Position -and $row.Position -le [Math]::Max($sent.Position,$completionPosition)
                    $repairLink=$row.Position -lt $progress.Position -and $x['authority_id'] -ceq $progress.Fields.authority_id -and $x['repair_generation'] -ceq $progress.Fields.repair_generation
                    # Preserve uninterpreted diagnostics verbatim (the existing
                    # forced-state event repeats range_fingerprint). They do
                    # not satisfy a required unique link or grant authority.
                    if($repairLink -or ($episode -and $x.event -cmatch '\Afiletransfer_(fallback_frontier_repair_|control_plane_delivery_observed|v[46]_sparse_write_committed|v6_frontier_|v6_fallback_frontier_repair_progress_state_forced|v6_receiver_state_)')) {Pick $row -Opaque}
                }
                Require ((Snapshot) -ceq $before -and [object]::ReferenceEquals($Failure.Event,$Index.AllEvents[$failurePosition]) -and
                    [object]::ReferenceEquals($Failure.Event.RawLine,$Full[$failurePosition])) 'native_frontier_failure_changed'
                $afterBoundaries=@(foreach($view in @($FullView,$CompactView)) {foreach($b in $view.Boundaries) {
                    $visit.Invoke(); if($b.coordinatorInstanceId -ceq $receipt.Completion.CoordinatorInstanceId -and $b.boundaryId -ceq $receipt.Completion.TransitionId) {
                        $b | Select-Object coordinatorInstanceId,boundaryId,evidenceLineIndex,verdict,decisionKind,decisionStatus,boundaryHash,writer,writerSequence
                    }
                }}) | ConvertTo-Json -Depth 4 -Compress
                Require ($afterBoundaries -ceq $boundarySnapshot) 'native_frontier_completion_changed'
                $state.Marker=$m; $state.Completion=$completionPosition+1; $state.MarkerPosition=$marker.Position+1; $state.Failure=$failurePosition+1
                $state.Outcome=$receipt.Outcome; $state.Error=''
                $state.Details=([ordered]@{OriginalFailureAndViews=$before;OriginalCompletionViews=$boundarySnapshot;Outcome=$receipt.Outcome;
                    FailureFullIndex=$failurePosition+1;CompletionFullIndex=$completionPosition+1;MarkerFullIndex=$marker.Position+1} | ConvertTo-Json -Compress)
                return [NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+NativeWitnessSelection]::new($chosen,[int[]]@($selected),$state.Details)
            } catch {$state.Error=$_.Exception.Message; return $null}
        }
        $batch=$Custody.TryResolveWitnesses($Evaluations,$Context,$Full,$Compact,$FullView,$CompactView,$Payloads,$Failure,$Candidates,$RecentProof,$Index,$resolve)
        if($null -eq $batch) {if([string]::IsNullOrEmpty($state.Error)) {$state.Error='native_frontier_custody_or_budget'}; return $null}
        $witnesses=$batch.GetWitnesses(); $completion=@($witnesses | Where-Object FullIndex -eq $state.Completion); $marker=@($witnesses | Where-Object FullIndex -eq $state.MarkerPosition)
        $failureWitness=@($witnesses | Where-Object FullIndex -eq $state.Failure)
        if($failureWitness.Count -ne 1 -or $Failure.Event.LineNumber -isnot [int] -or $Failure.Event.Sequence -isnot [int] -or
            $Failure.Event.LineNumber -ne $failureWitness[0].SemanticIndex -or $Failure.Event.Sequence -ne $failureWitness[0].SemanticIndex -or
            $completion.Count -ne 1 -or $marker.Count -ne 1 -or $completion[0].SourceLine -ne $batch.Completion.FirstSourceLine -or
            $completion[0].SourceLine -ne $batch.Completion.LastSourceLine -or $marker[0].SourceLine -ne $batch.Marker.FirstSourceLine -or
            $marker[0].SourceLine -ne $batch.Marker.LastSourceLine -or $state.Marker.frontier_completion_batch_generation -cne [string]$batch.Completion.BatchGeneration -or
            $state.Marker.frontier_completion_first_sequence -cne [string]$batch.Completion.FirstSequence -or $state.Marker.frontier_completion_last_sequence -cne [string]$batch.Completion.LastSequence -or
            $state.Marker.frontier_completion_sha256 -cne $batch.Completion.BatchSha256 -or -not $batch.IsCurrent) {
            $state.Error='native_frontier_native_range_join'; $batch.Dispose(); return $null
        }
        return [pscustomobject]@{Status='matched_native_frontier_diagnostic';Batch=$batch;OriginalSnapshot=$batch.DiagnosticSnapshot;DiagnosticOutcome=$state.Outcome;
            FailureFullIndex=$state.Failure;LiveProofEligible=$false;PublicationCompletionIssued=$false;NativeWriterCustodyEstablished=$false}
    } catch {if($null -ne $batch) {$batch.Dispose()}; $state.Error=$_.Exception.Message; return $null}
    finally {if($null -ne $OmissionReason) {$OmissionReason.Value=$state.Error}}
}
