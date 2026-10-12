namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferRecentProgressGraceHandoffTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    [Trait("OpsSuite", "GuiHarness")]
    public Task RecentProgressGrace_CoalescedSparseWritesRequireExactPublishedChain(string host)
        => RecentProgressGrace_ExactDispatchedRepairTunaRetirement_HasIndexedAndGuiParity("stall_progress_chain", host);

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    [Trait("OpsSuite", "GuiHarness")]
    public Task StallGrace_CaughtUpPublishedFrontierRequiresExactTunaHandoff(string host)
        => RecentProgressGrace_ExactDispatchedRepairTunaRetirement_HasIndexedAndGuiParity("stall_published_caught_up", host);

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    [Trait("OpsSuite", "GuiHarness")]
    public Task StallGrace_CaughtUpPublishedFrontierWithRetainedProgressRequiresExactTunaHandoff(string host)
        => RecentProgressGrace_ExactDispatchedRepairTunaRetirement_HasIndexedAndGuiParity("stall_published_caught_up_with_progress", host);

    [Theory]
    [InlineData("recent_progress", "powershell")]
    [InlineData("stall", "powershell")]
    [InlineData("stall_progress", "powershell")]
    [InlineData("stall_published", "powershell")]
    [InlineData("stall_published", "pwsh")]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    [Trait("OpsSuite", "GuiHarness")]
    public async Task RecentProgressGrace_ExactDispatchedRepairTunaRetirement_HasIndexedAndGuiParity(string kind, string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var script = """
            param([string]$RepoRoot, [string]$Kind)
            $ErrorActionPreference='Stop'
            $retainProgress=$Kind -eq 'stall_published_caught_up_with_progress'
            $caughtUp=$retainProgress -or $Kind -eq 'stall_published_caught_up'
            if($caughtUp){$Kind='stall_published'}
            $stallHighest=if($caughtUp){'40'}else{'70'}
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')
            $tokens=$null; $errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
            if($errors.Count){throw 'GUI parse failed'}
            $gui=$ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -in @('Get-TunaGuiFallbackFrontierRecentProgressGraceRecheckFailure', 'Get-TunaGuiFallbackFrontierStallGraceRecheckFailure')},$true)
            foreach($function in $gui){Invoke-Expression $function.Extent.Text}
            $origin=[DateTimeOffset]::Parse('2026-09-05T17:30:00Z')
            $source='ambient source must not be used for candidate scope'
            $scope=@{direction='inbound'; session_id='session'; transfer_id='transfer'; log_writer_id='writer';
                route='post_tuna_fallback_v6'; protocol_version='6'; live_route_epoch='2'; runtime_generation='3';
                terminal_generation='0'; leg_generation='3'; transport_epoch='1'; bridge_recovery_generation='1';
                bridge_decision_generation='0'; checkpoint_request_id='(none)'; checkpoint_generation='1';
                route_publication_generation='3'; route_commit_generation='0'}
            function Repair([string]$id,[int]$generation,[int]$frontier,[string]$predecessor){
                $f=$scope.Clone(); $f.authority_schema='2'; $f.authority_id=$id; $f.predecessor_authority_id=$predecessor
                $f.repair_generation=[string]$generation; $f.repair_request_key="frontier:$frontier"; $f.range_fingerprint="range:$frontier"
                $f.frontier_chunk_index=[string]$frontier; $f.source_kind='ReceiverRequest'; $f.target_transport='regular_nkn'
                $f.durable_received_highest_chunk_index=if($caughtUp){[string]($frontier-1)}else{'80'}
                $f.primary_protocol_repair_request_id="request:$frontier"; $f.primary_request_correlation_id="correlation:$frontier"
                if($Kind -eq 'stall_published'){$f.range_count='1';$f.requested_chunk_count='1';$f.repair_request_key="fallback-frontier:${frontier}:1:range:$frontier"}
                return $f
            }
            function Add([string]$name,[int]$ms,[hashtable]$fields,[hashtable]$extra=@{}){
                $f=$fields.Clone(); foreach($key in $extra.Keys){$f[$key]=[string]$extra[$key]}
                $f.event=$name; $f.log_writer_sequence=[string]($script:fixtureEvents.Count+1)
                $f.log_writer_attempt_utc_ticks=[string]$origin.AddMilliseconds($ms).UtcTicks
                $script:fixtureEvents.Add([pscustomobject]@{EventName=$name;Fields=$f;Sequence=[long]($script:fixtureEvents.Count+1);
                    LineNumber=$script:fixtureEvents.Count+1;FilePath='fixture';TimestampUtc=$origin.AddMilliseconds($ms)})
            }
            function Fixture {
                $script:fixtureEvents=[Collections.Generic.List[object]]::new()
                $a=Repair 'fta2-repair_replay-A' 60 40 '(none)'; $b=Repair 'fta2-repair_replay-B' 61 41 'fta2-repair_replay-A'
                Add 'filetransfer_fallback_frontier_repair_authority_granted' 0 $a
                Add 'filetransfer_control_plane_delivery_observed' 1 $a @{kind='frontier_request';repair_request_id='request:40';request_correlation_id='correlation:40';peer_visible_any='1';accepted_any='1'}
                Add 'filetransfer_fallback_frontier_repair_dispatched' 2 $a @{dispatch_observed='1'}
                Add 'filetransfer_v6_frontier_request_sent' 3 $a @{repair_request_id='request:40';request_correlation_id='correlation:40'}
                Add 'filetransfer_fallback_frontier_repair_contract_settled' 4 $a @{settlement_outcome='ProgressObserved'}
                Add 'filetransfer_fallback_frontier_repair_progress_observed' 100 $a @{previous_frontier_chunk_index='40';current_frontier_chunk_index='41'}
                Add 'filetransfer_v6_fallback_frontier_repair_progress_state_forced' 101 $a @{current_frontier_chunk_index='41';receiver_state_epoch='7';data_session_identity='data';data_session_generation='1';decision_status='Accepted'}
                Add 'filetransfer_v6_receiver_state_sent' 102 $scope @{epoch='7';contiguous_committed_chunk_index='41';durable_received_highest_chunk_index=$(if($caughtUp){'40'}else{'60'})}
                Add 'filetransfer_v6_receiver_state_deferred' 150 $scope @{next_chunk_index='41';highest_received_chunk_index='60';reason='frontier_stalled'}
                $wake=$scope.Clone(); $wake.frontier_chunk_index='41'; $wake.highest_received_chunk_index=$stallHighest
                $wake.data_session_identity='data'; $wake.data_session_generation='1'
                Add 'filetransfer_v6_frontier_request_deferred' 1100 $wake @{elapsed_ms='0';stall_grace_ms='750';wake_generation='41';reason='frontier_recent_progress_grace_elapsed';stall_started_utc=$origin.AddMilliseconds(1100).ToString('o');wake_deadline_utc=$origin.AddMilliseconds(1850).ToString('o')}
                Add 'filetransfer_v6_frontier_recent_progress_grace_recheck_decided' 1101 $wake @{decision_status='rechecked';decision_reason='stall_grace_armed';wake_generation='51';committed_progress_utc=$origin.AddMilliseconds(100).ToString('o');wake_deadline_utc=$origin.AddMilliseconds(1100).ToString('o')}
                Add 'filetransfer_fallback_frontier_repair_authority_granted' 1860 $b
                Add 'filetransfer_control_plane_delivery_observed' 1861 $b @{kind='frontier_request';repair_request_id='request:41';request_correlation_id='correlation:41';peer_visible_any='1';accepted_any='1'}
                Add 'filetransfer_fallback_frontier_repair_dispatched' 1862 $b @{dispatch_observed='1'}
                Add 'filetransfer_v6_frontier_request_sent' 1863 $b @{repair_request_id='request:41';request_correlation_id='correlation:41'}
                Add 'filetransfer_fallback_frontier_repair_contract_settled' 2100 $b @{settlement_outcome='StaleRejected';dispatch_observed='1';decision_reason='tuna_reactivation_replaced_fallback_owner'}
                $transition=@{authority_schema='2';authority_id='fta2-filetransfer_route_transition-T';transition_generation='3';transition_kind='PostTunaFallbackV6ToTunaV4';
                    direction='inbound';session_id='session';transfer_id='transfer';log_writer_id='writer';
                    source_transport='regular_nkn';source_data_session_generation='1';data_session_identity='data';
                    checkpoint_generation='1';checkpoint_request_id='(none)';target_route='file_tuna_v4';target_protocol_version='4';target_transport='tuna';
                    target_live_route_epoch='3';target_runtime_generation='4';target_terminal_generation='0';target_leg_generation='4';target_route_commit_generation='2';target_route_publication_generation='4'}
                foreach($field in @('route','protocol_version','live_route_epoch','runtime_generation','terminal_generation','leg_generation','route_commit_generation','route_publication_generation','transport_epoch','bridge_recovery_generation')){$transition["source_$field"]=$scope[$field]}
                Add 'filetransfer_route_transition_committed' 2101 $transition @{decision_status='Committed'}
                $target=@{direction='inbound';session_id='session';transfer_id='transfer';log_writer_id='writer';projection_source='coordinator_route_transition';transition_generation='3';reason='filetransfer_route_transition_committed';route_publication_action_kind='RouteCommitPublication'}
                foreach($field in @('route','protocol_version','live_route_epoch','runtime_generation','terminal_generation','leg_generation','route_commit_generation','route_publication_generation')){$target[$field]=$transition["target_$field"]}
                Add 'filetransfer_route_selected' 2102 $target
                $applied=@{decision_status='Applied';decision_reason='filetransfer_route_transition_committed'}
                foreach($field in @('live_route_epoch','runtime_generation','terminal_generation','leg_generation','route_commit_generation','route_publication_generation')){$applied["resulting_$field"]=$transition["target_$field"]}
                Add 'filetransfer_route_transition_disposition_applied' 2110 $transition $applied
                Add 'filetransfer_live_route_epoch_recovered' 2111 $target
                return $script:fixtureEvents.ToArray()
            }
            function NaturalFixture {
                param([switch]$Coalesced)
                $base=@(Fixture | Select-Object -First 8)
                $script:fixtureEvents=[Collections.Generic.List[object]]::new()
                foreach($event in $base){$script:fixtureEvents.Add($event)}
                $wake=$scope.Clone();$wake.frontier_chunk_index='41';$wake.highest_received_chunk_index='70'
                $wake.data_session_identity='data';$wake.data_session_generation='1';$wake.authority_effect='none'
                $recent=@{wake_generation='51';committed_progress_utc=$origin.AddMilliseconds(100).ToString('o');wake_deadline_utc=$origin.AddMilliseconds(1100).ToString('o')}
                Add 'filetransfer_v6_frontier_recent_progress_grace_deferred' 140 $wake ($recent + @{decision_status='held'})
                Add 'filetransfer_v6_receiver_state_deferred' 150 $scope @{next_chunk_index='41';highest_received_chunk_index='70';reason='frontier_stalled'}
                Add 'filetransfer_v6_frontier_request_deferred' 1100 $wake @{elapsed_ms='0';stall_grace_ms='750';wake_generation='41';reason='frontier_recent_progress_grace_elapsed';stall_started_utc=$origin.AddMilliseconds(1100).ToString('o');wake_deadline_utc=$origin.AddMilliseconds(1850).ToString('o')}
                Add 'filetransfer_v6_frontier_recent_progress_grace_recheck_decided' 1101 $wake ($recent + @{decision_status='rechecked';decision_reason='stall_grace_armed'})
                Add 'filetransfer_v6_sparse_write_committed' 1400 $scope @{contiguous_chunks_committed='2';next_chunk_index='43';highest_received_chunk_index='80'}
                if($Coalesced){
                    Add 'filetransfer_v6_sparse_write_committed' 1410 $scope @{contiguous_chunks_committed='2';next_chunk_index='45';highest_received_chunk_index='81'}
                    Add 'filetransfer_v6_sparse_write_committed' 1420 $scope @{contiguous_chunks_committed='3';next_chunk_index='48';highest_received_chunk_index='82'}
                    Add 'unrelated_diagnostic' 1430 $scope @{note='no authority'}
                }
                Add 'filetransfer_v6_receiver_state_sent' 1450 $scope @{epoch='8';contiguous_committed_chunk_index=$(if($Coalesced){'48'}else{'43'});durable_received_highest_chunk_index=$(if($Coalesced){'82'}else{'80'});transfer_paused='0'}
                return $script:fixtureEvents.ToArray()
            }
            $cases=@('exact','duplicate','missing_wake','missing_stall','early_grant','missing_delivery','missing_dispatch','missing_send',
                'missing_retirement','wrong_retirement','wrong_scope','missing_commit','rejected_commit','missing_disposition','missing_publication',
                'wrong_data','wrong_transition','wrong_target','malformed_grant_time','malformed_target_generation',
                'terminal','pause','replacement','conflict','stall_progress_only')
            if($Kind -eq 'stall'){
                $cases=@($cases|Where-Object {$_ -ne 'missing_stall'}) + @('missing_grant','wrong_predecessor','ambiguous_grant',
                    'highest_regression','historical_caught_up','malformed_deadline','moved_deadline','wrong_stall_data','wrong_dispatch_order','unrelated_publication')
            }
            if($Kind -eq 'stall_progress'){
                $cases=@('exact','duplicate','natural_missing_arm','natural_missing_recheck','natural_missing_stall',
                    'natural_missing_write','natural_missing_state','natural_wrong_wake','natural_wrong_data','natural_wrong_scope',
                    'natural_moved_deadline','natural_late_write','natural_late_state','natural_wrong_frontier','natural_highest_regression',
                    'natural_wrong_epoch','natural_wrong_transport','natural_pause','natural_terminal','natural_replacement',
                    'natural_conflict','natural_ambiguous_wake','natural_ambiguous_state','natural_unrelated',
                    'natural_wrong_progress_time','natural_wrong_commit_count','natural_early_rearm')
            }
            if($Kind -eq 'stall_progress_chain'){
                $cases=@('exact','duplicate','natural_chain_gap','natural_chain_wrong_count','natural_chain_regression',
                    'natural_chain_highest_regression','natural_chain_late_write','natural_chain_late_state',
                    'natural_chain_early_state','natural_chain_wrong_scope','natural_chain_wrong_data',
                    'natural_chain_wrong_state','natural_chain_pause','natural_chain_ambiguous_state','natural_chain_conflict',
                    'natural_chain_tail_only','natural_chain_zero_jump','natural_chain_clock_regression','natural_chain_missing_state',
                    'natural_chain_frame_epoch','natural_chain_owner_epoch','natural_chain_route_replaced')
            }
            if($Kind -eq 'stall_published'){
                $cases=@('exact','duplicate','missing_grant','early_grant','missing_delivery','missing_dispatch','missing_send',
                    'missing_retirement','wrong_retirement','wrong_retirement_reason','wrong_scope','missing_commit','rejected_commit',
                    'missing_disposition','missing_publication','wrong_data','wrong_transition','wrong_target','ambiguous_grant',
                    'highest_regression','wrong_stall_data','wrong_dispatch_order','unrelated_publication','terminal','pause','replacement','conflict',
                    'published_missing_wake','published_wrong_wake','published_early_wake','published_wrong_deadline','published_wrong_frontier',
                    'published_wrong_wake_data','published_alias_only','published_wrong_primary','published_wrong_correlation',
                    'published_wrong_request_key','published_missing_predecessor','published_foreign_predecessor','published_prior_authority_case',
                    'published_prior_duplicate','published_empty_optional_diagnostic','published_prior_event_conflict','published_prior_session_conflict','published_unrelated_progress')
                if($retainProgress){$cases+=@('published_missing_source','published_wrong_source','published_aba_source')}
            }
            foreach($case in $cases){
                [object[]]$events=if($Kind -in @('stall_progress','stall_progress_chain')){NaturalFixture -Coalesced:($Kind -eq 'stall_progress_chain')}else{Fixture}
                if($Kind -eq 'stall'){
                    $stale=$events[9].Fields.Clone();$stale.event='filetransfer_v6_frontier_stall_grace_recheck_decided'
                    $stale.decision_status='stale';$stale.decision_reason='frontier_scope_changed';$stale.authority_effect='none'
                    $stale.log_writer_sequence='31';$stale.log_writer_attempt_utc_ticks=[string]$origin.AddMilliseconds(1845).UtcTicks
                    $events += [pscustomobject]@{EventName=$stale.event;Fields=$stale;Sequence=11L;LineNumber=31;TimestampUtc=$origin.AddMilliseconds(1845)}
                }
                $remove=@{missing_wake=11;missing_stall=10;missing_delivery=13;missing_dispatch=14;missing_send=15;missing_retirement=16;missing_commit=17;missing_disposition=19;missing_publication=20}
                if($remove.ContainsKey($case)){$events=@($events|Where-Object Sequence -ne $remove[$case])}
                switch($case){
                    'natural_chain_gap' {$events=@($events|Where-Object Sequence -ne 14)}
                    'natural_chain_wrong_count' {$events[13].Fields.contiguous_chunks_committed='1'}
                    'natural_chain_regression' {$events[13].Fields.next_chunk_index='42'}
                    'natural_chain_highest_regression' {$events[13].Fields.highest_received_chunk_index='79'}
                    'natural_chain_late_write' {$events[13].Fields.log_writer_attempt_utc_ticks=[string]$origin.AddMilliseconds(1851).UtcTicks}
                    'natural_chain_late_state' {$events[-1].Fields.log_writer_attempt_utc_ticks=[string]$origin.AddMilliseconds(1851).UtcTicks}
                    'natural_chain_early_state' {$events[-1].Fields.log_writer_attempt_utc_ticks=[string]$origin.AddMilliseconds(1415).UtcTicks}
                    'natural_chain_wrong_scope' {$events[13].Fields.runtime_generation='4'}
                    'natural_chain_wrong_data' {$events[13].Fields.data_session_identity='other'}
                    'natural_chain_wrong_state' {$events[-1].Fields.contiguous_committed_chunk_index='49'}
                    'natural_chain_pause' {$events[13].Fields.transfer_paused='1'}
                    'natural_chain_tail_only' {$events[13].Fields.contiguous_chunks_committed='0';$events[13].Fields.next_chunk_index='43';$events[14].Fields.contiguous_chunks_committed='5'}
                    'natural_chain_zero_jump' {$events[13].Fields.contiguous_chunks_committed='0'}
                    'natural_chain_clock_regression' {$events[13].Fields.log_writer_attempt_utc_ticks=[string]$origin.AddMilliseconds(1399).UtcTicks}
                    'natural_chain_missing_state' {$events=@($events|Where-Object Sequence -ne 17)}
                    'natural_chain_owner_epoch' {$events[13].Fields.transport_epoch='0'}
                    {$_ -in @('natural_chain_frame_epoch','natural_chain_route_replaced')} {
                        $f=$scope.Clone();$f.event='filetransfer_data_path_receive_result';$f.transport_epoch='0';$f.frame_type='filetransfer.chunk_batch.v6'
                        if($case -eq 'natural_chain_route_replaced'){$f.event='filetransfer_route_selected';$f.transport_epoch='1';$f.runtime_generation='4'}
                        $f.log_writer_sequence='30';$f.log_writer_attempt_utc_ticks=[string]$origin.AddMilliseconds(1435).UtcTicks
                        $events += [pscustomobject]@{EventName=$f.event;Fields=$f;Sequence=16L;LineNumber=30;TimestampUtc=$origin.AddMilliseconds(1435)}
                    }
                    {$_ -in @('natural_chain_ambiguous_state','natural_chain_conflict')} {
                        $copy=$events[-1].PSObject.Copy();$copy.Fields=$events[-1].Fields.Clone();$copy.Fields.epoch='9';$copy.LineNumber=30
                        if($case -eq 'natural_chain_ambiguous_state'){$copy.Fields.log_writer_sequence='30'}
                        $events += $copy
                    }
                    'natural_missing_arm' {$events=@($events|Where-Object Sequence -ne 9)}
                    'natural_missing_recheck' {$events=@($events|Where-Object Sequence -ne 12)}
                    'natural_missing_stall' {$events=@($events|Where-Object Sequence -ne 11)}
                    'natural_missing_write' {$events=@($events|Where-Object Sequence -ne 13)}
                    'natural_missing_state' {$events=@($events|Where-Object Sequence -ne 14)}
                    'natural_wrong_wake' {$events[11].Fields.wake_generation='52'}
                    'natural_wrong_data' {$events[10].Fields.data_session_identity='other'}
                    'natural_wrong_scope' {$events[10].Fields.route_publication_generation='4'}
                    'natural_moved_deadline' {$events[10].Fields.wake_deadline_utc=$origin.AddMilliseconds(1950).ToString('o')}
                    'natural_late_write' {$events[12].Fields.log_writer_attempt_utc_ticks=[string]$origin.AddMilliseconds(1851).UtcTicks}
                    'natural_late_state' {$events[13].Fields.log_writer_attempt_utc_ticks=[string]$origin.AddMilliseconds(1851).UtcTicks}
                    'natural_wrong_frontier' {$events[13].Fields.contiguous_committed_chunk_index='44'}
                    'natural_highest_regression' {$events[12].Fields.highest_received_chunk_index='69';$events[13].Fields.durable_received_highest_chunk_index='69'}
                    'natural_wrong_epoch' {$events[13].Fields.epoch='7'}
                    'natural_wrong_transport' {$events[13].Fields.transport_epoch='2'}
                    'natural_pause' {$events[13].Fields.transfer_paused='1'}
                    {$_ -in @('natural_terminal','natural_replacement','natural_unrelated')} {
                        $f=$scope.Clone();$f.event=switch($case){'natural_terminal' {'file_transfer_inbound_terminal'} 'natural_replacement' {'filetransfer_data_session_recreated'} 'natural_unrelated' {'unrelated_diagnostic'}}
                        $f.log_writer_sequence='30';$f.log_writer_attempt_utc_ticks=[string]$origin.AddMilliseconds(1300).UtcTicks
                        $events += [pscustomobject]@{EventName=$f.event;Fields=$f;Sequence=12L;LineNumber=30;TimestampUtc=$origin.AddMilliseconds(1300)}
                    }
                    'natural_conflict' {$copy=$events[13].PSObject.Copy();$copy.Fields=$events[13].Fields.Clone();$copy.Fields.epoch='9';$copy.LineNumber=30;$events += $copy}
                    'natural_ambiguous_wake' {$copy=$events[11].PSObject.Copy();$copy.Fields=$events[11].Fields.Clone();$copy.Fields.log_writer_sequence='30';$copy.LineNumber=30;$events += $copy}
                    'natural_ambiguous_state' {$copy=$events[13].PSObject.Copy();$copy.Fields=$events[13].Fields.Clone();$copy.Fields.log_writer_sequence='30';$copy.Fields.epoch='9';$copy.LineNumber=30;$events += $copy}
                    'natural_wrong_progress_time' {$events[11].Fields.committed_progress_utc=$origin.AddMilliseconds(101).ToString('o')}
                    'natural_wrong_commit_count' {$events[12].Fields.contiguous_chunks_committed='1'}
                    'natural_early_rearm' {
                        $copy=$events[11].PSObject.Copy();$copy.Fields=$events[11].Fields.Clone();$copy.Fields.log_writer_sequence='30'
                        $copy.Fields.decision_status='rearmed';$copy.Fields.decision_reason='grace_remaining'
                        $copy.Fields.log_writer_attempt_utc_ticks=[string]$origin.AddMilliseconds(1099).UtcTicks
                        $copy.Sequence=10L;$copy.LineNumber=30;$copy.TimestampUtc=$origin.AddMilliseconds(1099);$events += $copy
                    }
                    'duplicate' {$events += $events[13]}
                    'early_grant' {$events[11].Fields.log_writer_attempt_utc_ticks=[string]$origin.AddMilliseconds(1200).UtcTicks}
                    'wrong_retirement' {$events[15].Fields.authority_id='fta2-repair_replay-other'}
                    'wrong_retirement_reason' {$events[15].Fields.decision_reason='another_reason'}
                    'wrong_scope' {$events[15].Fields.transport_epoch='2'}
                    'rejected_commit' {$events[16].Fields.decision_status='Rejected'}
                    'wrong_data' {$events[18].Fields.data_session_identity='replacement'}
                    'wrong_transition' {$events[18].Fields.authority_id='fta2-filetransfer_route_transition-other'}
                    'wrong_target' {$events[19].Fields.route_publication_generation='5'}
                    'malformed_grant_time' {$events[11].Fields.log_writer_attempt_utc_ticks='invalid'}
                    'malformed_target_generation' {$events[16].Fields.target_runtime_generation='invalid'}
                    'missing_grant' {$events=@($events|Where-Object Sequence -ne 12)}
                    'wrong_predecessor' {$events[11].Fields.predecessor_authority_id='fta2-repair_replay-other'}
                    'ambiguous_grant' {$copy=$events[11].PSObject.Copy();$copy.Fields=$events[11].Fields.Clone();$copy.Fields.log_writer_sequence='32';$copy.Fields.authority_id='fta2-repair_replay-other';$events += $copy}
                    'highest_regression' {$events[11].Fields.durable_received_highest_chunk_index=if($caughtUp){'39'}else{'69'}}
                    'historical_caught_up' {$events[9].Fields.highest_received_chunk_index='40';$events[11].Fields.durable_received_highest_chunk_index='40'}
                    'malformed_deadline' {$events[9].Fields.wake_deadline_utc='invalid'}
                    'moved_deadline' {$events[9].Fields.wake_deadline_utc=$origin.AddMilliseconds(1800).ToString('o')}
                    'wrong_stall_data' {$events[9].Fields.data_session_identity='other'}
                    'wrong_dispatch_order' {$events[13].Sequence=15L}
                    'unrelated_publication' {$events[19].Fields.transition_generation='4'}
                    {$_ -in @('terminal','pause','replacement')} {
                        $f=$scope.Clone();$f.event=switch($case){'terminal' {'file_transfer_inbound_terminal'} 'pause' {'filetransfer_v6_receiver_state_sent'} 'replacement' {'filetransfer_data_session_recreated'}}
                        $f.transfer_paused='1';$f.log_writer_sequence='30'
                        $events += [pscustomobject]@{EventName=$f.event;Fields=$f;Sequence=18L;LineNumber=30;TimestampUtc=$origin.AddMilliseconds(2105)}
                    }
                    'conflict' {$copy=$events[13].PSObject.Copy();$copy.Fields=$events[13].Fields.Clone();$copy.Fields.range_fingerprint='conflict';$events += $copy}
                    'stall_progress_only' {
                        $events=@($events|Where-Object Sequence -le 11)
                        $f=$scope.Clone();$f.event='filetransfer_v6_sparse_write_committed';$f.contiguous_chunks_committed='2';$f.next_chunk_index='43';$f.log_writer_attempt_utc_ticks=[string]$origin.AddMilliseconds(1400).UtcTicks
                        $events += [pscustomobject]@{EventName=$f.event;Fields=$f;Sequence=12L;LineNumber=12;TimestampUtc=$origin.AddMilliseconds(1400)}
                        $carry=$scope.Clone();$carry.event='filetransfer_route_selected';$carry.log_writer_sequence='32'
                        $carry.log_writer_attempt_utc_ticks=[string]$origin.AddMilliseconds(1200).UtcTicks
                        $events += [pscustomobject]@{EventName=$carry.event;Fields=$carry;Sequence=11L;LineNumber=32;TimestampUtc=$origin.AddMilliseconds(1200)}
                    }
                }
                if($Kind -eq 'stall_published'){
                    # Exercise current publication both with and without retained predecessor progress.
                    $removed=if($retainProgress){@(9,11)}else{@(6,7,8,9,11)}
                    $events=@($events|Where-Object {$_.Sequence -notin $removed})
                    $candidate=@($events|Where-Object EventName -eq 'filetransfer_v6_frontier_request_deferred')[0]
                    $candidate.Fields.authority_effect='none'
                    $script:fixtureEvents=[Collections.Generic.List[object]]::new()
                    Add 'filetransfer_route_hint_authorized_publication_accepted' -100 $scope @{
                        route_publication_action_kind='RoutePublication';coordinator_decision_reason='accepted';target_route='post_tuna_fallback_v6';
                        target_protocol_version='6';target_transport='regular_nkn';fallback_leg_generation='3';fallback_transport_epoch='1';fallback_bridge_recovery_generation='1'}
                    Add 'filetransfer_v6_sparse_write_committed' 100 $scope @{contiguous_chunks_committed='1';next_chunk_index='41';highest_received_chunk_index=$stallHighest}
                    Add 'filetransfer_v6_receiver_state_sent' 102 $scope @{epoch='7';contiguous_committed_chunk_index='41';durable_received_highest_chunk_index=$stallHighest}
                    Add 'filetransfer_v6_frontier_stall_grace_recheck_decided' 1864 $candidate.Fields @{
                        decision_status='dispatched';decision_reason='exact_receiver_request_dispatched';authority_effect='none'}
                    $wake=$script:fixtureEvents[3]
                    $alias=Repair 'fta2-repair_replay-B' 61 41 'fta2-repair_replay-A'
                    Add 'filetransfer_v6_frontier_request_sent' 2000 $alias @{repair_request_id='request:41:alias';request_correlation_id='correlation:41:alias'}
                    Add 'unrelated_liveness_diagnostic' 1200 $scope @{}
                    foreach($extra in $script:fixtureEvents){$extra.Fields.log_writer_sequence=[string](100+$extra.Sequence)}
                    $events+=@($script:fixtureEvents)
                    switch($case){
                        'published_missing_source' {$events=@($events|Where-Object EventName -ne 'filetransfer_route_hint_authorized_publication_accepted')}
                        'published_wrong_source' {@($events|Where-Object EventName -eq 'filetransfer_route_hint_authorized_publication_accepted')[0].Fields.coordinator_decision_reason='rejected'}
                        'published_aba_source' {
                            $changed=$scope.Clone();$changed.route_publication_generation='4'
                            $script:fixtureEvents=[Collections.Generic.List[object]]::new()
                            Add 'filetransfer_route_selected' -50 $changed
                            $script:fixtureEvents[0].Fields.log_writer_sequence='120';$events+=@($script:fixtureEvents)
                            $publication=@($events|Where-Object EventName -eq 'filetransfer_route_hint_authorized_publication_accepted')[0]
                            $restored=$publication.PSObject.Copy();$restored.Fields=$publication.Fields.Clone()
                            $restored.TimestampUtc=$origin.AddMilliseconds(-25);$restored.Fields.log_writer_attempt_utc_ticks=[string]$restored.TimestampUtc.UtcTicks
                            $restored.Fields.log_writer_sequence='121';$events+=@($restored)
                        }
                        'published_missing_wake' {$events=@($events|Where-Object EventName -ne 'filetransfer_v6_frontier_stall_grace_recheck_decided')}
                        'published_wrong_wake' {$wake.Fields.wake_generation='42'}
                        'published_early_wake' {$wake.Fields.log_writer_attempt_utc_ticks=[string]$origin.AddMilliseconds(1849).UtcTicks}
                        'published_wrong_deadline' {$wake.Fields.wake_deadline_utc=$origin.AddMilliseconds(1851).ToString('o')}
                        'published_wrong_frontier' {$wake.Fields.frontier_chunk_index='42'}
                        'published_wrong_wake_data' {$wake.Fields.data_session_identity='other'}
                        'published_alias_only' {$events=@($events|Where-Object {$_.EventName -ne 'filetransfer_v6_frontier_request_sent' -or $_.Fields.repair_request_id -ne 'request:41'})}
                        'published_wrong_primary' {@($events|Where-Object {$_.EventName -eq 'filetransfer_v6_frontier_request_sent' -and $_.Fields.repair_request_id -eq 'request:41'})[0].Fields.repair_request_id='request:other'}
                        'published_wrong_correlation' {@($events|Where-Object {$_.EventName -eq 'filetransfer_control_plane_delivery_observed' -and $_.Fields.repair_request_id -eq 'request:41'})[0].Fields.request_correlation_id='correlation:other'}
                        'published_wrong_request_key' {@($events|Where-Object {$_.EventName -eq 'filetransfer_fallback_frontier_repair_dispatched' -and $_.Fields.frontier_chunk_index -eq '41'})[0].Fields.repair_request_key='frontier:other'}
                        'published_missing_predecessor' {$events=@($events|Where-Object {$_.EventName -ne 'filetransfer_fallback_frontier_repair_authority_granted' -or $_.Fields.authority_id -ne 'fta2-repair_replay-A'})}
                        'published_foreign_predecessor' {@($events|Where-Object {$_.EventName -eq 'filetransfer_fallback_frontier_repair_authority_granted' -and $_.Fields.authority_id -eq 'fta2-repair_replay-A'})[0].Fields.route_publication_generation='2'}
                        'published_prior_authority_case' {@($events|Where-Object {$_.EventName -eq 'filetransfer_fallback_frontier_repair_authority_granted' -and $_.Fields.authority_id -ceq 'fta2-repair_replay-A'})[0].Fields.authority_id='fta2-repair_replay-a'}
                        'published_empty_optional_diagnostic' {$candidate.Fields.optional_diagnostic='';@($events|Where-Object EventName -eq 'filetransfer_route_hint_authorized_publication_accepted')[0].Fields.optional_diagnostic=''}
                        {$_ -in @('published_prior_duplicate','published_prior_event_conflict','published_prior_session_conflict')} {
                            $prior=@($events|Where-Object {$_.EventName -eq 'filetransfer_fallback_frontier_repair_authority_granted' -and $_.Fields.authority_id -eq 'fta2-repair_replay-A'})[0]
                            $copy=$prior.PSObject.Copy();$copy.Fields=$prior.Fields.Clone()
                            if($case -eq 'published_prior_event_conflict'){$copy.EventName='filetransfer_route_selected';$copy.Fields.event=$copy.EventName}
                            if($case -eq 'published_prior_session_conflict'){$copy.Fields.session_id='foreign'}
                            $events+=@($copy)
                        }
                        'wrong_dispatch_order' {$dispatch=@($events|Where-Object {$_.EventName -eq 'filetransfer_fallback_frontier_repair_dispatched' -and $_.Fields.frontier_chunk_index -eq '41'})[0];$dispatch.TimestampUtc=$origin.AddMilliseconds(1865);$dispatch.Fields.log_writer_attempt_utc_ticks=[string]$dispatch.TimestampUtc.UtcTicks}
                        'published_unrelated_progress' {
                            $events=@($events|Where-Object {$_.EventName -notin @('filetransfer_route_transition_committed','filetransfer_route_transition_disposition_applied','filetransfer_live_route_epoch_recovered')})
                            $script:fixtureEvents=[Collections.Generic.List[object]]::new()
                            $other=Repair 'fta2-repair_replay-Tuna' 62 41 '(none)';$other.route='file_tuna_v4';$other.protocol_version='4';$other.target_transport='tuna'
                            Add 'filetransfer_fallback_frontier_repair_progress_observed' 2200 $other @{previous_frontier_chunk_index='41';current_frontier_chunk_index='59'}
                            $script:fixtureEvents[0].Fields.log_writer_sequence='110';$events+=@($script:fixtureEvents)
                        }
                    }
                    # PowerShell 5 does not preserve input order for equal sort
                    # keys. Keep the original record before its conflicting copy;
                    # the conflict must still reject recovery at the closure gate.
                    $sortable=@(for($i=0;$i -lt $events.Count;$i++){
                        [pscustomobject]@{Event=$events[$i];InputOrder=$i}
                    })
                    $events=@($sortable|Sort-Object @{Expression={$_.Event.TimestampUtc}},
                        @{Expression={$_.Event.LineNumber}},InputOrder|ForEach-Object {$_.Event})
                    $sequence=0L;foreach($event in $events){$event.Sequence=++$sequence;$event.LineNumber=[int]$sequence}
                    if(@($events|Where-Object {$_.EventName -eq 'filetransfer_fallback_frontier_repair_progress_observed' -and $_.Fields.current_frontier_chunk_index -eq '41'}).Count -ne [int]$retainProgress){throw 'fixture predecessor progress coverage differs'}
                    $publishedIndex=New-FileTransferStallGracePublishedSourceIndex $events
                    $admission=Get-FileTransferStallGracePublishedSourceAdmission -Index $publishedIndex -Candidate $candidate
                    if($case -in @('published_missing_source','published_wrong_source','published_aba_source')){
                        if($admission.Admitted){throw "$case admitted invalid current publication"}
                    }elseif(-not $admission.Admitted -or $admission.Episode.AdmissionKind -cne 'CurrentPublishedSource' -or $null -ne $admission.Episode.HistoricalProgress){throw "$case did not use exact current-publication admission"}
                    if($case -eq 'exact'){
                        if($caughtUp){
                            foreach($invalidHighest in @('39','','invalid')){
                                $badCandidate=$candidate.PSObject.Copy();$badCandidate.Fields=$candidate.Fields.Clone()
                                $badCandidate.Fields.highest_received_chunk_index=$invalidHighest
                                $badAdmission=Get-FileTransferStallGracePublishedSourceAdmission -Index $publishedIndex -Candidate $badCandidate
                                if($badAdmission.Admitted){throw "invalid caught-up highest '$invalidHighest' admitted"}
                                $forged=$admission.Episode.PSObject.Copy();$forged.Event=$badCandidate;$forged.ScopeEvidence=$badCandidate
                                if($null -ne (Get-FileTransferStallGraceTunaHandoffClosure -Events $events -Episode $forged -PublishedSourceIndex $publishedIndex)){throw "invalid caught-up highest '$invalidHighest' closed"}
                            }
                        }
                        foreach($badSource in @('missing','wrong_scope','wrong_receipt','aba')){
                            $badEvents=@($events|ForEach-Object {$copy=$_.PSObject.Copy();$copy.Fields=$_.Fields.Clone();$copy})
                            $publication=@($badEvents|Where-Object EventName -eq 'filetransfer_route_hint_authorized_publication_accepted')[0]
                            switch($badSource){
                                'missing' {$badEvents=@($badEvents|Where-Object EventName -ne 'filetransfer_route_hint_authorized_publication_accepted')}
                                'wrong_scope' {$publication.Fields.route_publication_generation='4'}
                                'wrong_receipt' {$publication.Fields.coordinator_decision_reason='rejected'}
                                'aba' {$copy=$publication.PSObject.Copy();$copy.Fields=$publication.Fields.Clone();$copy.Fields.event='filetransfer_route_selected';$copy.EventName=$copy.Fields.event;$copy.Fields.route_publication_generation='4';$copy.Sequence=2L;$copy.Fields.log_writer_sequence='120';$badEvents+=@($copy)
                                    $restored=$publication.PSObject.Copy();$restored.Fields=$publication.Fields.Clone();$restored.Sequence=3L;$restored.Fields.log_writer_sequence='121';$badEvents+=@($restored)}
                            }
                            $badIndex=New-FileTransferStallGracePublishedSourceIndex $badEvents
                            if($null -ne (Get-FileTransferStallGraceTunaHandoffClosure -Events $badEvents -Episode $admission.Episode -PublishedSourceIndex $badIndex)){throw "$badSource publication accepted through forged admission"}
                        }
                        foreach($field in @('AdmissionKind','PublishedSource')){
                            $forged=$admission.Episode.PSObject.Copy()
                            if($field -eq 'AdmissionKind'){$forged.AdmissionKind='forged'}else{$forged.PublishedSource=$null}
                            if($null -ne (Get-FileTransferStallGraceTunaHandoffClosure -Events $events -Episode $forged -PublishedSourceIndex $publishedIndex)){throw "$field forged admission accepted"}
                        }
                    }
                }
                $expected=$case -in @('exact','duplicate','published_prior_duplicate','published_empty_optional_diagnostic','natural_unrelated','natural_early_rearm','natural_chain_tail_only','natural_chain_frame_epoch') -or ($Kind -eq 'stall' -and $case -in @('missing_wake','stall_progress_only'))
                $isStall=$Kind -in @('stall','stall_published')
                $legacy=if($isStall){$script:FileTransferFallbackFrontierStallGraceLegacyCorrelation}else{$script:FileTransferFallbackFrontierRecentProgressGraceLegacyCorrelation}
                $plain=& $legacy -Events $events
                $index=New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $events
                $indexed=if($isStall){Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $events -Index $index}else{Get-FileTransferFallbackFrontierRecentProgressGraceRecheckCorrelation -Events $events -Index $index}
                $lines=@($events|Sort-Object Sequence|ForEach-Object {
                    $f=$_.Fields; $parts=@($f.Keys|Sort-Object|ForEach-Object {"$_=$($f[$_])"})
                    "[$($_.TimestampUtc.ToString('yyyy-MM-dd HH:mm:ss.fffffffZ'))] [INFO] [FileTransfer] $($parts -join '; '); gui_evidence_sequence=$($_.Sequence)"
                })
                $guiResult=if($isStall){Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $lines}else{Get-TunaGuiFallbackFrontierRecentProgressGraceRecheckFailure -Lines $lines}
                if($Kind -eq 'stall_progress_chain'){
                    $catalog=@(Get-FileTransferFallbackFrontierGraceCorrelationEventNames)
                    $compactLines=@($lines|Where-Object {$_ -match '(?:^|; )event=([^;]+)' -and $Matches[1] -cin $catalog})
                    if($compactLines.Count -ge $lines.Count){throw 'Compact chain fixture was not independently regenerated'}
                    $compactGui=Get-TunaGuiFallbackFrontierRecentProgressGraceRecheckFailure -Lines $compactLines
                    if($guiResult.Unresolved -ne $compactGui.Unresolved -or $guiResult.FirstSequence -ne $compactGui.FirstSequence){throw "$case full/compact GUI differs"}
                    function ChainBoundaryJson($result){
                        $records=@(foreach($record in $result.CandidateRecords){[ordered]@{
                            reason=$record.Reason;variant=$record.Variant;recovered=$record.Recovered;
                            writer=$record.Event.Fields.log_writer_id;sequence=$record.Event.Fields.log_writer_sequence;
                            frontier=$record.Event.Fields.next_chunk_index}})
                        ConvertTo-Json -InputObject ([ordered]@{raw=$result.RawCount;distinct=$result.DistinctCount;recovered=$result.RecoveredCount;unresolved=$result.UnresolvedCount;boundaries=$records}) -Depth 10 -Compress
                    }
                    foreach($inputLines in @($lines,$compactLines)){
                        $parsed=@(for($i=0;$i -lt $inputLines.Count;$i++){ConvertFrom-FileTransferLogLine -Line $inputLines[$i] -FilePath 'serialized.log' -LineNumber ($i+1) -Sequence ($i+1)})
                        $serialized=Get-FileTransferFallbackFrontierRecentProgressGraceRecheckCorrelation -Events $parsed -Index (New-FileTransferFallbackFrontierGraceCorrelationIndex $parsed)
                        if((ChainBoundaryJson $serialized) -cne (ChainBoundaryJson $indexed)){throw "$case serialized full/compact retained boundary differs"}
                    }
                    $owned=[Collections.Generic.HashSet[object]]::new()
                    [void]$owned.Add(@($events|Where-Object EventName -eq 'filetransfer_v6_receiver_state_deferred')[0])
                    $key=Get-FileTransferFallbackFrontierGraceTransferKey -Event @($owned)[0]
                    foreach($selection in @(@(41L),@(41L,43L,45L,48L))){
                        $slice=@(Get-FileTransferFallbackFrontierGracePartitionSlice -Index $index -TransferKey $key -Frontiers $selection)
                        $partition=& $legacy -Events $slice -OwnedCandidateEvents $owned
                        if($partition.RawCount -ne 1 -or $partition.DistinctCount -ne 1 -or $partition.RecoveredCount -ne [int]$expected){
                            throw "$case coalesced chain changed when publication frontiers crossed the partition boundary"
                        }
                    }
                }
                if($Kind -eq 'stall_published'){
                    # Regenerate compact input from this independent full fixture using the explicit catalog.
                    $catalog=@(Get-FileTransferFallbackFrontierGraceCorrelationEventNames)
                    $compactLines=@($lines|Where-Object {$_ -match '(?:^|; )event=([^;]+)' -and $Matches[1] -cin $catalog})
                    $compactGui=Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $compactLines
                    if($guiResult.Unresolved -ne $compactGui.Unresolved -or $guiResult.FirstSequence -ne $compactGui.FirstSequence){throw "$case full/compact GUI boundary differs"}
                    function ParseLines([string[]]$inputLines,[string]$path){
                        for($i=0;$i -lt $inputLines.Count;$i++){ConvertFrom-FileTransferLogLine -Line $inputLines[$i] -FilePath $path -LineNumber ($i+1) -Sequence ($i+1)}
                    }
                    $fullEvents=@(ParseLines $lines 'full.log');$compactEvents=@(ParseLines $compactLines 'compact.log')
                    $serializedFull=Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $fullEvents -Index (New-FileTransferFallbackFrontierGraceCorrelationIndex $fullEvents)
                    $compact=Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $compactEvents -Index (New-FileTransferFallbackFrontierGraceCorrelationIndex $compactEvents)
                    if($compact.RawCount -ne $indexed.RawCount -or $compact.DistinctCount -ne $indexed.DistinctCount -or $compact.RecoveredCount -ne $indexed.RecoveredCount -or $compact.UnresolvedCount -ne $indexed.UnresolvedCount){throw "$case regenerated compact quartet differs"}
                    function BoundaryJson($result){
                        $records=@(foreach($record in $result.CandidateRecords){[ordered]@{
                            reason=$record.Reason;variant=$record.Variant;recovered=$record.Recovered;
                            writer=$record.Event.Fields.log_writer_id;sequence=$record.Event.Fields.log_writer_sequence;
                            frontier=$record.Event.Fields.frontier_chunk_index;
                            recoveryWriter=$(if($null -ne $record.RecoveryEvent){$record.RecoveryEvent.Fields.log_writer_id}else{''});
                            recoverySequence=$(if($null -ne $record.RecoveryEvent){$record.RecoveryEvent.Fields.log_writer_sequence}else{''})}})
                        ConvertTo-Json -InputObject ([ordered]@{raw=$result.RawCount;distinct=$result.DistinctCount;recovered=$result.RecoveredCount;unresolved=$result.UnresolvedCount;verdict=$result.Verdict;boundaries=$records}) -Depth 10 -Compress
                    }
                    $fullBoundary=BoundaryJson $serializedFull;$compactBoundary=BoundaryJson $compact
                    if($fullBoundary -cne $compactBoundary -or $fullBoundary -cne (BoundaryJson $indexed)){throw "$case full/compact normalized boundary differs"}
                }
                foreach($result in @($plain,$indexed)){
                    if($result.RawCount -ne 1 -or $result.DistinctCount -ne 1 -or $result.RecoveredCount -ne [int]$expected){throw "$case parity mismatch $($result|ConvertTo-Json -Depth 2 -Compress)"}
                    if($Kind -eq 'stall_published' -and $expected -and $result.CandidateRecords[0].RecoveryEvent.EventName -cne 'filetransfer_live_route_epoch_recovered'){throw "$case closed without the exact handoff publication"}
                }
                if($guiResult.Unresolved -eq $expected){throw "$case GUI mismatch"}
                if(-not $expected -and $guiResult.FirstSequence -ne $plain.UnresolvedCandidateRecords[0].Event.Sequence){throw "$case GUI location mismatch"}
                "pass $case"
            }
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), kind], host);
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("pass exact", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(kind switch { "stall_progress" => "pass natural_missing_state", "stall_progress_chain" => "pass natural_chain_gap", "stall_published" or "stall_published_caught_up" or "stall_published_caught_up_with_progress" => "pass published_unrelated_progress", _ => "pass stall_progress_only" }, result.Stdout, StringComparison.Ordinal);
    }
}
