namespace NLink.SmokeTests;

public sealed class FileTransferRecentProgressGraceHandoffTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("recent_progress")]
    [InlineData("stall")]
    [InlineData("stall_progress")]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    [Trait("OpsSuite", "GuiHarness")]
    public async Task RecentProgressGrace_ExactDispatchedRepairTunaRetirement_HasIndexedAndGuiParity(string kind)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var script = """
            param([string]$RepoRoot, [string]$Kind)
            $ErrorActionPreference='Stop'
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
                $f.durable_received_highest_chunk_index='80'
                $f.primary_protocol_repair_request_id="request:$frontier"; $f.primary_request_correlation_id="correlation:$frontier"
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
                Add 'filetransfer_v6_receiver_state_sent' 102 $scope @{epoch='7';contiguous_committed_chunk_index='41';durable_received_highest_chunk_index='60'}
                Add 'filetransfer_v6_receiver_state_deferred' 150 $scope @{next_chunk_index='41';highest_received_chunk_index='60';reason='frontier_stalled'}
                $wake=$scope.Clone(); $wake.frontier_chunk_index='41'; $wake.highest_received_chunk_index='70'
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
                Add 'filetransfer_v6_receiver_state_sent' 1450 $scope @{epoch='8';contiguous_committed_chunk_index='43';durable_received_highest_chunk_index='80';transfer_paused='0'}
                return $script:fixtureEvents.ToArray()
            }
            $cases=@('exact','duplicate','missing_wake','missing_stall','early_grant','missing_delivery','missing_dispatch','missing_send',
                'missing_retirement','wrong_retirement','wrong_scope','missing_commit','rejected_commit','missing_disposition','missing_publication',
                'wrong_data','wrong_transition','wrong_target','malformed_grant_time','malformed_target_generation',
                'terminal','pause','replacement','conflict','stall_progress_only')
            if($Kind -eq 'stall'){
                $cases=@($cases|Where-Object {$_ -ne 'missing_stall'}) + @('missing_grant','wrong_predecessor','ambiguous_grant',
                    'highest_regression','malformed_deadline','moved_deadline','wrong_stall_data','wrong_dispatch_order','unrelated_publication')
            }
            if($Kind -eq 'stall_progress'){
                $cases=@('exact','duplicate','natural_missing_arm','natural_missing_recheck','natural_missing_stall',
                    'natural_missing_write','natural_missing_state','natural_wrong_wake','natural_wrong_data','natural_wrong_scope',
                    'natural_moved_deadline','natural_late_write','natural_late_state','natural_wrong_frontier','natural_highest_regression',
                    'natural_wrong_epoch','natural_wrong_transport','natural_pause','natural_terminal','natural_replacement',
                    'natural_conflict','natural_ambiguous_wake','natural_ambiguous_state','natural_unrelated',
                    'natural_wrong_progress_time','natural_wrong_commit_count','natural_early_rearm')
            }
            foreach($case in $cases){
                [object[]]$events=if($Kind -eq 'stall_progress'){NaturalFixture}else{Fixture}
                if($Kind -eq 'stall'){
                    $stale=$events[9].Fields.Clone();$stale.event='filetransfer_v6_frontier_stall_grace_recheck_decided'
                    $stale.decision_status='stale';$stale.decision_reason='frontier_scope_changed';$stale.authority_effect='none'
                    $stale.log_writer_sequence='31';$stale.log_writer_attempt_utc_ticks=[string]$origin.AddMilliseconds(1845).UtcTicks
                    $events += [pscustomobject]@{EventName=$stale.event;Fields=$stale;Sequence=11L;LineNumber=31;TimestampUtc=$origin.AddMilliseconds(1845)}
                }
                $remove=@{missing_wake=11;missing_stall=10;missing_delivery=13;missing_dispatch=14;missing_send=15;missing_retirement=16;missing_commit=17;missing_disposition=19;missing_publication=20}
                if($remove.ContainsKey($case)){$events=@($events|Where-Object Sequence -ne $remove[$case])}
                switch($case){
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
                    'highest_regression' {$events[11].Fields.durable_received_highest_chunk_index='69'}
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
                $expected=$case -in @('exact','duplicate','natural_unrelated','natural_early_rearm') -or ($Kind -eq 'stall' -and $case -in @('missing_wake','stall_progress_only'))
                $legacy=if($Kind -eq 'stall'){$script:FileTransferFallbackFrontierStallGraceLegacyCorrelation}else{$script:FileTransferFallbackFrontierRecentProgressGraceLegacyCorrelation}
                $plain=& $legacy -Events $events
                $index=New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $events
                $indexed=if($Kind -eq 'stall'){Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $events -Index $index}else{Get-FileTransferFallbackFrontierRecentProgressGraceRecheckCorrelation -Events $events -Index $index}
                $lines=@($events|Sort-Object Sequence|ForEach-Object {
                    $f=$_.Fields; $parts=@($f.Keys|Sort-Object|ForEach-Object {"$_=$($f[$_])"})
                    "[$($_.TimestampUtc.ToString('yyyy-MM-dd HH:mm:ss.fffffffZ'))] [INFO] [FileTransfer] $($parts -join '; '); gui_evidence_sequence=$($_.Sequence)"
                })
                $guiResult=if($Kind -eq 'stall'){Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $lines}else{Get-TunaGuiFallbackFrontierRecentProgressGraceRecheckFailure -Lines $lines}
                foreach($result in @($plain,$indexed)){
                    if($result.RawCount -ne 1 -or $result.DistinctCount -ne 1 -or $result.RecoveredCount -ne [int]$expected){throw "$case parity mismatch $($result|ConvertTo-Json -Depth 2 -Compress)"}
                }
                if($guiResult.Unresolved -eq $expected){throw "$case GUI mismatch"}
                if(-not $expected -and $guiResult.FirstSequence -ne $plain.UnresolvedCandidateRecords[0].Event.Sequence){throw "$case GUI location mismatch"}
                "pass $case"
            }
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), kind]);
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("pass exact", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(kind == "stall_progress" ? "pass natural_missing_state" : "pass stall_progress_only", result.Stdout, StringComparison.Ordinal);
    }
}
