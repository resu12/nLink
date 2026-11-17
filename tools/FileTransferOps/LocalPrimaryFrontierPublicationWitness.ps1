Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'LocalPublicationRows.ps1')

# Optional, detached OFFLINE diagnostics. The caller supplies its already chosen
# first failure and original proof objects; this function never runs a correlator
# or Snapshot evaluator and never issues the publication-use capability.
function Resolve-FileTransferPrimaryFrontierPublicationWitness {
    param($Context, $Custody, $Evaluations, $Index, $Failure, $Candidates, $RecentProof, [string]$ReceiptJson)
    $writer=$null; $selected=[Collections.Generic.SortedSet[int]]::new()
    $work=@{Records=0L;CodeUnits=0L}
    function Check {
        if($Context.Disposed -or -not $Context.Associated -or
            $Context.Work.ElapsedMilliseconds -gt [NLink.Tools.LocalPublicationInputLimits]::ProducerMilliseconds -or
            -not $Custody.Owns($Evaluations,$Context,$Context.FullLines,$Context.CompactLines,$Context.FullView,$Context.CompactView)) {
            throw 'frontier_primary_custody_or_budget'
        }
    }
    function Fields([string]$Line,[switch]$Opaque) {
        Check
        if($null -eq $Line -or $Line.Length -gt 262144 -or
            ++$work.Records -gt [NLink.Tools.LocalPublicationInputLimits]::ProducerRecords -or
            $Line.Length -gt [NLink.Tools.LocalPublicationInputLimits]::ProducerCodeUnits-$work.CodeUnits -or
            $Line.Split([char[]]@(';'),257).Length -gt 256) { throw 'frontier_input_budget' }
        $work.CodeUnits+=$Line.Length
        $fields=ConvertFrom-GuiSmokeLogLedgerStructuredFields $Line
        if(-not $Opaque -and $fields.ContainsKey('__nlink_duplicate_structured_field')) { throw 'frontier_duplicate_field' }
        return $fields
    }
    function SelectLine([int]$Position) {
        if($Position -lt 0 -or $Position -ge $Context.FullLines.Length) { throw 'frontier_origin' }
        [void]$selected.Add($Position)
        if($selected.Count -gt 64) { throw 'frontier_witness_budget' }
    }
    function Evidence([string]$Value) { return 'fte1-'+(Get-FileTransferLocalCarrierHash $Value).Substring(0,24) }
    function One([string]$Name,[scriptblock]$Match) {
        $found=@($owned | Where-Object { $_.Fields.event -ceq $Name -and (& $Match $_.Fields) })
        if($found.Count -ne 1) { throw ('frontier_chain_'+$Name) }
        if($found[0].Fields.ContainsKey('__nlink_duplicate_structured_field')) { throw 'frontier_duplicate_field' }
        SelectLine $found[0].Position
        return $found[0]
    }
    try {
        if($null -eq $Context -or $Custody -isnot [NLink.Tools.LocalRetainedSourceRequest+PrimaryEvaluationCustody] -or
            $Evaluations -isnot [NLink.Tools.LocalRetainedSourceRequest+CompletedEvaluations] -or
            $null -eq $Failure -or $Failure.Class -cne 'filetransfer_fallback_frontier_recent_progress_grace_recheck_missing' -or
            $null -eq $RecentProof -or $RecentProof.UnresolvedCount -lt 1 -or
            $ReceiptJson.Length -lt 1 -or $ReceiptJson.Length -gt 262144) { return $null }
        $Context.Work.Start(); Check
        if($Candidates.Count -gt 8192 -or $RecentProof.UnresolvedCandidateRecords.Count -gt 8192 -or
            @($Candidates | Where-Object { [object]::ReferenceEquals($_,$Failure) }).Count -ne 1 -or
            @($RecentProof.UnresolvedCandidateRecords | Where-Object {
                [object]::ReferenceEquals($_.Event,$Failure.Event) -and $_.Reason -ceq $Failure.Reason -and
                $_.Variant -ceq $Failure.Variant -and -not $_.Recovered
            }).Count -ne 1) { throw 'frontier_failure_owner' }
        $position=[Array]::BinarySearch($Context.FullOrigins,[int]$Failure.Event.LineNumber)
        if($position -lt 0 -or $Index.AllEvents.Count -ne $Context.FullLines.Length -or
            $Index.LogFiles.Count -ne 1 -or $Index.LogFiles[0] -cne $Context.SourceOwner.SourcePath -or
            -not [object]::ReferenceEquals($Failure.Event,$Index.AllEvents[$position]) -or
            -not [object]::ReferenceEquals($Failure.Event.RawLine,$Context.FullOriginal[$position])) { throw 'frontier_failure_origin' }
        $failureFields=Fields $Context.FullLines[$position]
        if($Failure.Event.EventName -cne 'filetransfer_v6_receiver_state_deferred' -or
            $failureFields.event -cne $Failure.Event.EventName -or $failureFields.reason -cne 'frontier_stalled' -or
            $failureFields.route -cne 'post_tuna_fallback_v6' -or $failureFields.protocol_version -cne '6') { throw 'frontier_failure_kind' }
        $work.Records+=$Context.RecordVisitCeiling; $work.CodeUnits+=$Context.NativeOwner.CodeUnitVisits
        $writer=$Context.SourceOwner.TryOpenWriter()
        if($null -eq $writer) { throw 'frontier_writer_missing' }
        $membership=$writer.TryVerifyMembership($Context.FullLines,$Context.FullOriginal,$failureFields.log_writer_id,
            [long]$failureFields.log_writer_sequence,$work.CodeUnits,$Context.Work)
        if($null -eq $membership -or -not $writer.IsVerified -or $membership.SelectedIndex -ne $position) { throw 'frontier_writer_membership' }
        $work.Records+=$membership.RecordVisits; $work.CodeUnits=$membership.CodeUnitVisits
        if(-not $Context.SourceOwner.ValidateFullInputOrigins($Context.SourceReader,$Context.NativeOwner,$Context.NativeInputs,
            $Index.AllEvents,$Context.FullLines,$Context.FullOriginal,$Context.FullOrigins,$Context.Work)) { throw 'frontier_native_origins' }
        $previous=0
        for($i=0;$i -lt $Context.CompactLines.Length;$i++) {
            Check
            $physical=$Context.CompactOrigins[$i]; $full=[Array]::BinarySearch($Context.FullOrigins,$physical)
            if($physical -le $previous -or $full -lt 0 -or
                -not [object]::ReferenceEquals($Context.CompactLines[$i],$Context.FullLines[$full]) -or
                -not [object]::ReferenceEquals($Context.CompactLines[$i],$Context.CompactOriginal[$i])) { throw 'frontier_compact_origins' }
            $previous=$physical
        }
        # Match exact bytes to the native verified writer BEFORE interpreting any
        # payload fields. A caller-supplied digest/renamed receipt is insufficient.
        $receiptHash='ftfr1-'+(Get-FileTransferLocalCarrierHash $ReceiptJson)
        $owned=[Collections.Generic.List[object]]::new(); $markers=[Collections.Generic.List[object]]::new()
        for($i=0;$i -lt $Context.FullLines.Length;$i++) {
            # Uninterpreted primary diagnostics are retained as opaque original
            # lines (some existing events repeat fields). Every record used to
            # bind this receipt is checked for unique fields separately.
            $fields=Fields $Context.FullLines[$i] -Opaque
            if($fields.log_writer_id -cne $failureFields.log_writer_id) { continue }
            $row=[pscustomobject]@{Position=$i;Fields=$fields}
            if($fields.event -ceq 'filetransfer_frontier_publication_receipt_closed' -and
                $fields.frontier_receipt_hash -ceq $receiptHash) { $markers.Add($row) }
            if($fields['session_id'] -ceq $failureFields.session_id -and $fields['transfer_id'] -ceq $failureFields.transfer_id) { $owned.Add($row) }
        }
        if($markers.Count -ne 1) { throw 'frontier_receipt_writer_hash' }
        $marker=$markers[0]; $manifest=$marker.Fields
        if($manifest.ContainsKey('__nlink_duplicate_structured_field') -or
            $manifest.authority_effect -cne 'none' -or $manifest.frontier_receipt_version -cne '1' -or
            $manifest.frontier_receipt_clock -cne 'offline_injected_v1' -or $marker.Position -le $position) { throw 'frontier_manifest' }
        $payload=ConvertFrom-Json -InputObject $ReceiptJson
        $r=$payload.Receipt
        if($payload.HashDomain -cne 'sha256-utf8-canonical-offline-frontier-receipt-v2' -or
            $payload.ClockDomain -cne 'injected_observation_separate_snapshot_clock_v1' -or
            $r.ContractVersion -ne 2 -or $r.Lifecycle.ContractVersion -ne 1 -or
            $r.Lifecycle.Coverage -cne 'offline_service_source_mutations_v1' -or
            $r.Lifecycle.Entries.Count -lt 1 -or $r.Lifecycle.Entries.Count -gt 64 -or
            $r.Lifecycle.LastSequence -ne $r.Lifecycle.Entries.Count -or
            $r.Outcome -cnotin @('PublishedBeforeOriginalDeadline','PublishedWithinObservationBound') -or
            $r.Lifecycle.Outcome -cne $r.Outcome -or $manifest.frontier_receipt_outcome -cne $r.Outcome -or
            $manifest.frontier_lifecycle_end -cne [string]$r.Lifecycle.LastSequence -or
            $manifest.frontier_observation_id -cne ('ftfr1-'+(Get-FileTransferLocalCarrierHash $r.Lifecycle.ObservationId)) -or
            $manifest.frontier_coordinator_id -cne (Evidence $r.Completion.CoordinatorInstanceId) -or
            $manifest.frontier_completion_id -cne (Evidence $r.Completion.TransitionId) -or
            $r.Frame.SessionId -cne $failureFields.session_id -or $r.Frame.TransferId -cne $failureFields.transfer_id -or
            $r.RecentArms.Count -lt 1 -or $r.RecentArms.Count -gt 64 -or $r.Commits.Count -lt 1 -or $r.Commits.Count -gt 64) { throw 'frontier_receipt_profile' }
        for($i=0;$i -lt $r.Lifecycle.Entries.Count;$i++) {
            if($r.Lifecycle.Entries[$i].Sequence -ne $i+1) { throw 'frontier_lifecycle_order' }
        }
        SelectLine $position; SelectLine $marker.Position
        $root=$r.RecentArms[0]; $a=$r.RepairAuthority; $s=$r.Source.Boundary
        if($failureFields.next_chunk_index -cne [string]$root.Frontier -or $r.Commits[0].Before -ne $root.Frontier -or
            $r.RecentGeneration -ne $r.RecentArms[-1].Generation -or $s.Route -cne 'post_tuna_fallback_v6' -or $s.ProtocolVersion -ne 6) {
            throw 'frontier_original_obligation'
        }
        $progress=One 'filetransfer_fallback_frontier_repair_progress_observed' { param($f)
            $f.authority_id -ceq $a.AuthorityId.Value -and $f.repair_generation -ceq [string]$r.Repair.RepairGeneration -and
            $f.repair_request_key -ceq $a.CanonicalRepairKey -and $f.range_fingerprint -ceq $a.RangeFingerprint -and
            $f.primary_protocol_repair_request_id -ceq $r.Repair.ProtocolRepairRequestId -and
            $f.primary_request_correlation_id -ceq $r.Repair.RequestCorrelationId -and
            $f.current_frontier_chunk_index -ceq [string]$root.Frontier -and
            $f.continuity_receipt_id -ceq (Evidence $r.Repair.ContinuityReceiptIdentity) -and
            $f.continuity_receipt_fingerprint -ceq (Evidence $r.Repair.ContinuityReceiptFingerprint) -and
            $f.continuity_origin_receipt_id -ceq (Evidence $r.Repair.OriginReceiptIdentity) -and
            $f.continuity_origin_receipt_fingerprint -ceq (Evidence $r.Repair.OriginReceiptFingerprint) -and
            $f.continuity_current_scope_fingerprint -ceq (Evidence $r.Source.ScopeFingerprint)
        }
        if($progress.Position -ge $position) { throw 'frontier_progress_order' }
        foreach($pair in @(@('route','Route'),@('protocol_version','ProtocolVersion'),@('live_route_epoch','LiveRouteEpoch'),
            @('leg_generation','LegGeneration'),@('transport_epoch','TransportEpoch'),@('checkpoint_request_id','CheckpointRequestId'))) {
            if($failureFields[$pair[0]] -cne [string]$s.($pair[1])) { throw 'frontier_failure_scope' }
        }
        # The proof's failure is rooted in the nearest preceding progress for
        # this frontier; never substitute an older matching repair receipt.
        if(@($owned | Where-Object { $_.Position -gt $progress.Position -and $_.Position -lt $position -and
            $_.Fields.event -ceq 'filetransfer_fallback_frontier_repair_progress_observed' -and
            $_.Fields.current_frontier_chunk_index -ceq [string]$root.Frontier }).Count -ne 0) { throw 'frontier_progress_replaced' }
        foreach($arm in $r.RecentArms) {
            $null=One 'filetransfer_v6_frontier_recent_progress_grace_deferred' { param($f)
                $f.wake_generation -ceq [string]$arm.Generation -and $f.frontier_chunk_index -ceq [string]$root.Frontier -and
                [DateTimeOffset]$f.committed_progress_utc -eq [DateTimeOffset]$arm.ProgressUtc -and
                [DateTimeOffset]$f.wake_deadline_utc -eq [DateTimeOffset]$arm.Deadline
            }
        }
        $stall=One 'filetransfer_v6_frontier_request_deferred' { param($f)
            $f.wake_generation -ceq [string]$r.StallGeneration -and $f.frontier_chunk_index -ceq [string]$root.Frontier -and
            [DateTimeOffset]$f.stall_started_utc -eq [DateTimeOffset]$r.StallStartedUtc -and
            [DateTimeOffset]$f.wake_deadline_utc -eq [DateTimeOffset]$r.StallDeadline -and
            $f.continuity_source_fingerprint -ceq (Evidence $r.Source.ScopeFingerprint)
        }
        $recheck=One 'filetransfer_v6_frontier_recent_progress_grace_recheck_decided' { param($f)
            $f.wake_generation -ceq [string]$r.RecentGeneration -and $f.frontier_chunk_index -ceq [string]$root.Frontier -and
            $f.decision_status -ceq 'rechecked' -and $f.decision_reason -ceq 'stall_grace_armed'
        }
        if($stall.Position -le $position -or $recheck.Position -lt $stall.Position) { throw 'frontier_wake_order' }
        foreach($commit in $r.Commits) {
            $write=One 'filetransfer_v6_sparse_write_committed' { param($f)
                $f.next_chunk_index -ceq [string]$commit.After -and $f.contiguous_chunks_committed -ceq [string]$commit.Count -and
                $f.highest_received_chunk_index -ceq [string]$commit.Highest
            }
            if($write.Position -le $recheck.Position -or $write.Position -ge $marker.Position) { throw 'frontier_write_order' }
        }
        # Search the entire owned input for this frame identity, including
        # conflicting copies after closure; do not filter to favorable fields.
        if(@($owned | Where-Object {
            $_.Fields.event -ceq 'filetransfer_v6_receiver_state_sent' -and
            $_.Fields.epoch -ceq [string]$r.Frame.Epoch -and $_.Fields.transport_epoch -ceq [string]$r.Frame.TransportEpoch
        }).Count -ne 1) { throw 'frontier_state_identity' }
        $state=One 'filetransfer_v6_receiver_state_sent' { param($f)
            $f.epoch -ceq [string]$r.Frame.Epoch -and $f.contiguous_committed_chunk_index -ceq [string]$r.Frame.ContiguousCommittedChunkIndex -and
            $f.durable_received_highest_chunk_index -ceq [string]$r.Frame.DurableReceivedHighestChunkIndex -and
            $f.bytes_committed -ceq [string]$r.Frame.BytesCommitted -and $f.transport_epoch -ceq [string]$r.Frame.TransportEpoch -and $f.transfer_paused -ceq '0'
        }
        $locations=[Collections.Generic.List[int]]::new()
        foreach($key in @('ContractVersion','CatalogVersion','CatalogHash','BoundaryHash')) {
            if($Context.FullView.$key -cne $Context.CompactView.$key) { throw 'frontier_view_parity' }
        }
        foreach($kind in @('full','compact')) {
            $view=$Context.FullView; $lines=$Context.FullLines; $origins=$Context.FullOrigins
            if($kind -ceq 'compact') { $view=$Context.CompactView; $lines=$Context.CompactLines; $origins=$Context.CompactOrigins }
            if($view.Mode -cne 'SnapshotV1Authoritative' -or $view.ContractVersion -ne 1 -or
                $view.ArtifactEvidenceVerdict -cne 'pass' -or -not $view.StreamComplete -or
                'route_runtime' -cnotin $view.AuthoritativeFamilies -or 'route_transition' -cnotin $view.AuthoritativeFamilies -or
                $view.Boundaries.Count -gt 8192) { throw 'frontier_view_invalid' }
            $matches=@($view.Boundaries | Where-Object {
                $_.coordinatorInstanceId -ceq $manifest.frontier_coordinator_id -and $_.boundaryId -ceq $manifest.frontier_completion_id
            })
            if($matches.Count -ne 1) { throw 'frontier_completion_unique' }
            $b=$matches[0]; $i=[int]$b.evidenceLineIndex
            if($i -lt 0 -or $i -ge $lines.Length -or $b.verdict -cne 'pass' -or
                $b.decisionKind -cne 'FallbackControlPlaneSendCompleted' -or $b.decisionStatus -cnotin @('Accepted','Idempotent') -or
                ('ftfr1-'+$b.boundaryHash) -cne $manifest.frontier_completion_hash) { throw 'frontier_completion_boundary' }
            $f=Fields $lines[$i]; $physical=[Array]::BinarySearch($Context.FullOrigins,[int]$origins[$i])
            if($physical -lt 0 -or $physical -ge $marker.Position -or $physical -le $write.Position -or
                $f.event -cne 'filetransfer_coordinator_transition_snapshot' -or $f.transition_id -cne $b.boundaryId -or
                $f.coordinator_instance_id -cne $b.coordinatorInstanceId -or $f.snapshot_sha256 -cne $b.boundaryHash -or
                $f.log_writer_id -cne $failureFields.log_writer_id -or $f.log_writer_id -cne $b.writer -or
                $f.log_writer_sequence -cne [string]$b.writerSequence -or
                [long]$f.log_writer_sequence -ge [long]$manifest.log_writer_sequence) { throw 'frontier_completion_origin' }
            SelectLine $physical; $locations.Add($physical)
        }
        if($locations[0] -ne $locations[1] -or $state.Position -le $locations[0]) { throw 'frontier_completion_parity' }
        # Keep this episode's diagnostic coverage and exact earlier repair
        # links. The selected state diagnostic is emitted AFTER the marker.
        # Full input validation and all unique-link searches remain unbounded
        # by this selection interval (but subject to the original work limits).
        foreach($row in $owned) {
            $f=$row.Fields
            $episode=$row.Position -ge $progress.Position -and $row.Position -le $state.Position
            $repairLink=$row.Position -lt $progress.Position -and $f['authority_id'] -ceq $a.AuthorityId.Value -and
                $f['repair_generation'] -ceq [string]$r.Repair.RepairGeneration -and
                $f.event -cmatch '\Afiletransfer_(fallback_frontier_repair_|v6_frontier_request_sent\z)'
            if($repairLink -or ($episode -and
                $f.event -cmatch '\Afiletransfer_(fallback_frontier_repair_|control_plane_delivery_observed|v6_frontier_|v6_fallback_frontier_repair_progress_state_forced|v6_receiver_state_)')) {
                SelectLine $row.Position
            }
        }
        $starts=0; $ends=0
        for($i=0;$i -lt $Context.FullLines.Length;$i++) {
            $f=Fields $Context.FullLines[$i] -Opaque
            if($f['coordinator_instance_id'] -cne $manifest.frontier_coordinator_id) { continue }
            if($f.ContainsKey('__nlink_duplicate_structured_field') -or
                $f.log_writer_id -cne $failureFields.log_writer_id) { throw 'frontier_coordinator_writer' }
            if($f.event -ceq 'filetransfer_coordinator_snapshot_stream_started') {
                $starts++; if($i -ge $progress.Position) { throw 'frontier_stream_start' }; SelectLine $i
            } elseif($f.event -ceq 'filetransfer_coordinator_snapshot_stream_completed') {
                $ends++; if($i -le $marker.Position) { throw 'frontier_stream_end' }; SelectLine $i
            } elseif($i -ge $progress.Position -and $i -le $locations[0]) {
                # Preserve every owned Coordinator record across the episode,
                # including invalidations/effects, not just the passing end.
                SelectLine $i
            }
        }
        if($starts -ne 1 -or $ends -ne 1 -or -not $writer.IsVerified) { throw 'frontier_stream_coverage' }
        Check
        $witnesses=@(foreach($i in $selected) { @{SourceLine=$Context.FullOrigins[$i];ContentSha256=(Get-FileTransferLocalCarrierHash $Context.FullLines[$i])} })
        $result=[pscustomobject]@{Status='matched_primary_offline_frontier_witness';LiveProofEligible=$false;PublicationCompletionIssued=$false
            RequestId=$Context.SourceOwner.RequestId.ToString('D');SourcePath=$Context.SourceOwner.SourcePath
            SourceSha256=$Context.SourceOwner.SourceSha256;WriterId=$failureFields.log_writer_id
            ReportClass=$Failure.Class;ReportReason=$Failure.Reason;FailureSourceLine=$Failure.Event.LineNumber
            ReceiptHash=$receiptHash;CompletionHash=$manifest.frontier_completion_hash;CompletionSourceLine=$Context.FullOrigins[$locations[0]]
            ManifestSourceLine=$Context.FullOrigins[$marker.Position];Witnesses=$witnesses;DiagnosticOutcome=$r.Outcome}
        Check
        return $result
    }
    catch { if($null -ne $Context) { $Context.FrontierPublicationOmissionReason=$_.Exception.Message }; return $null }
    finally { if($null -ne $writer) { $writer.Dispose() }; if($null -ne $Context) { $Context.Work.Stop() } }
}
