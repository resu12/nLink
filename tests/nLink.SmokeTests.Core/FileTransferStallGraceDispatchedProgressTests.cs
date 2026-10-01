namespace NLink.SmokeTests;

public sealed class FileTransferStallGraceDispatchedProgressTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    public async Task StallGrace_ExactDispatchThenIndependentCommit_RequiresCompleteUnpoisonedProof(string host)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')
            $start=[DateTimeOffset]::Parse('2026-09-06T04:32:00Z')
            $scope=@{
                log_writer_id='writer';session_id='session';transfer_id='transfer';direction='inbound';
                route='post_tuna_fallback_v6';protocol_version='6';live_route_epoch='4';runtime_generation='5';
                terminal_generation='0';leg_generation='5';route_publication_generation='5';route_commit_generation='0';
                transport_epoch='1';bridge_recovery_generation='1';bridge_decision_generation='0';checkpoint_request_id='(none)';
                checkpoint_generation='1';data_session_identity='data';data_session_generation='1'
            }
            function Event([string]$Name,[long]$Ms,[hashtable]$Extra) {
                $fields=$scope.Clone()
                foreach($key in $Extra.Keys){$fields[$key]=[string]$Extra[$key]}
                $fields['event']=$Name;$fields['log_writer_sequence']=[string]($Ms+1)
                $fields['log_writer_attempt_utc_ticks']=[string]$start.AddMilliseconds($Ms).UtcTicks
                [pscustomobject]@{EventName=$Name;Fields=$fields;Sequence=$Ms+1;LineNumber=$Ms+1;TimestampUtc=$start.AddMilliseconds($Ms);FilePath='fixture.log'}
            }
            function Fixture {
                $wake=@{frontier_chunk_index='3000';highest_received_chunk_index='3194';wake_generation='177';
                    stall_started_utc=$start.AddMilliseconds(1000).ToString('O');wake_deadline_utc=$start.AddMilliseconds(1750).ToString('O');
                    authority_effect='none';stall_grace_ms='750';elapsed_ms='0';reason='chunk_batch_committed'}
                $repair=@{authority_id='repair32';repair_generation='32';repair_request_key='range3000';range_fingerprint='fingerprint';
                    primary_protocol_repair_request_id='request32';primary_request_correlation_id='correlation32';
                    predecessor_authority_id='repair31';frontier_chunk_index='3000';durable_received_highest_chunk_index='3194';
                    source_kind='ReceiverRequest';target_transport='regular_nkn'}
                Event 'filetransfer_route_hint_authorized_publication_accepted' 0 @{
                    target_route='post_tuna_fallback_v6';target_protocol_version='6';target_transport='regular_nkn';
                    route_publication_action_kind='RoutePublication';coordinator_decision_reason='accepted';
                    fallback_leg_generation='5';fallback_transport_epoch='1';fallback_bridge_recovery_generation='1'}
                Event 'filetransfer_v6_frontier_request_deferred' 1000 $wake
                Event 'filetransfer_fallback_frontier_repair_authority_granted' 1760 $repair
                $delivery=$repair.Clone();$delivery['authority_id']='control32';$delivery['repair_request_id']='request32';$delivery['request_correlation_id']='correlation32'
                $delivery['kind']='frontier_request';$delivery['frame_type']='filetransfer.frontier_request.v6';$delivery['accepted_any']='1';$delivery['peer_visible_any']='1'
                Event 'filetransfer_control_plane_delivery_observed' 1770 $delivery
                $dispatch=$repair.Clone();$dispatch['dispatch_observed']='1'
                Event 'filetransfer_fallback_frontier_repair_dispatched' 1780 $dispatch
                $sent=$repair.Clone();$sent['repair_request_id']='request32';$sent['request_correlation_id']='correlation32';$sent['reason']='frontier_stall_grace_elapsed'
                Event 'filetransfer_v6_frontier_request_sent' 1790 $sent
                $recheck=$wake.Clone();$recheck['decision_status']='dispatched';$recheck['decision_reason']='exact_receiver_request_dispatched'
                Event 'filetransfer_v6_frontier_stall_grace_recheck_decided' 1900 $recheck
                $advance=$wake.Clone();$advance['frontier_chunk_index']='3003';$advance['highest_received_chunk_index']='3698';$advance['wake_generation']='178'
                $advance['stall_started_utc']=$start.AddMilliseconds(2000).ToString('O');$advance['wake_deadline_utc']=$start.AddMilliseconds(2750).ToString('O')
                Event 'filetransfer_v6_frontier_request_deferred' 2000 $advance
                $state=Event 'filetransfer_v6_receiver_state_sent' 2100 @{contiguous_committed_chunk_index='3003';durable_received_highest_chunk_index='3747';transfer_paused='0';epoch='30'}
                foreach($key in $scope.Keys){if($key -notin @('log_writer_id','session_id','transfer_id','transport_epoch')){$state.Fields.Remove($key)}}
                $state
                Event 'file_transfer_inbound_terminal' 2200 @{state='Completed'}
            }
            function Closure([object[]]$Events) {
                $stall=@($Events | Where-Object {$_.Sequence -eq 1001})[0]
                Get-FileTransferStallGraceDispatchedProgressClosure -Events $Events -Episode ([pscustomobject]@{Event=$stall})
            }
            $good=@(Fixture)
            if((Closure $good).Sequence -ne 2101){throw 'exact dispatched wake did not close on independently published commit'}
            function WithEarlyWake([int]$Count) {
                $events=@(Fixture)
                for($i=0;$i -lt $Count;$i++){
                    $fields=$events[1].Fields.Clone()
                    $fields['decision_status']='rearmed';$fields['decision_reason']='stall_grace_deadline_pending'
                    $events+=Event 'filetransfer_v6_frontier_stall_grace_recheck_decided' (1740+$i) $fields
                    $events[-1].Fields['log_writer_sequence']=[string](1741+$i)
                    $events[-1].Fields['log_writer_attempt_utc_ticks']=[string]$start.AddMilliseconds(1740+$i).UtcTicks
                }
                @($events | Sort-Object Sequence)
            }
            foreach($count in @(1,2)){
                $events=@(WithEarlyWake $count)
                if($null -eq (Closure $events)){throw "pending deadline rearm count $count hid the real dispatch"}
                $stall=@($events | Where-Object EventName -CEQ 'filetransfer_v6_frontier_request_deferred')[0]
                $dispatch=Get-FileTransferStallGraceDispatchedProgressClosure -Events $events -Episode ([pscustomobject]@{Event=$stall}) -DiagnosticDispatchEvidenceOnly
                if($null -eq $dispatch -or $dispatch.Wake.Fields.decision_status -cne 'dispatched'){throw 'rearm supplied dispatch authority'}
            }
            # Writing the no-effect rearm can cross its deadline after the
            # timer checked it. Only the real grant/dispatch proves execution.
            $lateLog=@(WithEarlyWake 1)
            $early=@($lateLog | Where-Object {$_.Fields['decision_status'] -ceq 'rearmed'})[0]
            $early.Fields.log_writer_attempt_utc_ticks=[string]$start.AddMilliseconds(1755).UtcTicks
            if($null -eq (Closure $lateLog)){throw 'delayed rearm logging hid the real dispatch'}
            foreach($mutation in @(
                @('decision_status','stale'),@('decision_reason','highest_received_advanced'),@('authority_effect','runtime'),
                @('runtime_generation','6'),@('checkpoint_generation','2'),@('data_session_identity','foreign'),
                @('highest_received_chunk_index','3195'),@('wake_deadline_utc','2026-09-06T04:32:09Z'),
                @('log_writer_attempt_utc_ticks','0'))){
                $events=@(WithEarlyWake 1)
                $early=@($events | Where-Object {$_.Fields['decision_status'] -ceq 'rearmed'})[0]
                $early.Fields[$mutation[0]]=$mutation[1]
                if($null -ne (Closure $events)){throw "invalid rearm $mutation recovered"}
            }
            $events=@(WithEarlyWake 1)
            $early=@($events | Where-Object {$_.Fields['decision_status'] -ceq 'rearmed'})[0]
            $early.Sequence=1801;$early.Fields.log_writer_sequence='1801'
            if($null -ne (Closure $events)){throw 'rearm after repair grant recovered'}
            $events=@(WithEarlyWake 2 | Where-Object {$_.Fields['decision_status'] -cne 'dispatched'})
            if($null -ne (Closure $events)){throw 'rearms without dispatch recovered'}
            $events=@(WithEarlyWake 1)
            $early=@($events | Where-Object {$_.Fields['decision_status'] -ceq 'rearmed'})[0]
            $conflict=Event $early.EventName 1740 $early.Fields
            $conflict.Fields.authority_effect='runtime'
            if($null -ne (Closure (@($events)+@($conflict)))){throw 'conflicting rearm duplicate recovered'}
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
            $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $n.Name -ceq 'Get-TunaGuiFallbackFrontierStallGraceRecheckFailure'},$true))[0]
            Invoke-Expression $definition.Extent.Text
            foreach($count in @(0,1,2)){ foreach($missingSend in @($false,$true)){
                $inputEvents=@(WithEarlyWake $count | Where-Object {-not $missingSend -or $_.EventName -cne 'filetransfer_v6_frontier_request_sent'})
                $lines=@($inputEvents | ForEach-Object {$row=$_; (($row.Fields.Keys | Sort-Object | ForEach-Object {"$_=$($row.Fields[$_])"}) -join '; ') + "; gui_evidence_sequence=$($row.Sequence)"})
                $gui=Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $lines -IncludeEpisodeAudit
                $target=@($gui.EpisodeAudit | Where-Object {$_.Event.Fields.log_writer_sequence -ceq '1001'})
                if($target.Count -ne 1 -or $target[0].Excluded -or $target[0].Recovered -eq $missingSend){throw "actual GUI dispatched closure/admission differs: rearms=$count missingSend=$missingSend target=$($target | ConvertTo-Json -Depth 2 -Compress)"}
            } }
            $second=@(Fixture)
            foreach($event in $second){
                if($event.Fields.ContainsKey('frontier_chunk_index')){$event.Fields.frontier_chunk_index='3378'}
                if($event.Fields.ContainsKey('highest_received_chunk_index')){$event.Fields.highest_received_chunk_index='4066'}
                if($event.Fields.ContainsKey('durable_received_highest_chunk_index')){$event.Fields.durable_received_highest_chunk_index='4066'}
            }
            $second[7].Fields.frontier_chunk_index='3447';$second[8].Fields.contiguous_committed_chunk_index='3483'
            if((Closure $second).Sequence -ne 2101){throw 'larger committed advance with unchanged highest did not close'}
            $duplicates=@($good)+@($good[3],$good[6],$good[8])
            if((Closure $duplicates).Sequence -ne 2101){throw 'byte-identical duplicate changed closure'}
            foreach($missing in @(2,3,4,5,6,7,8)){
                $events=@(Fixture);$events=@(for($i=0;$i -lt $events.Count;$i++){if($i -ne $missing){$events[$i]}})
                if($null -ne (Closure $events)){throw "missing stage $missing recovered"}
            }
            foreach($mutation in @(
                @(2,'authority_id','other'),@(2,'repair_generation','0'),@(2,'range_fingerprint','other'),@(2,'runtime_generation','6'),
                @(3,'accepted_any','0'),@(3,'peer_visible_any','0'),@(3,'request_correlation_id','other'),@(3,'repair_request_id','other'),
                @(4,'dispatch_observed','0'),@(4,'repair_generation','33'),@(5,'repair_request_key','other'),@(5,'reason','retry'),
                @(6,'wake_generation','178'),@(6,'wake_deadline_utc','2026-09-06T04:32:09Z'),@(6,'decision_status','stale'),
                @(6,'decision_reason','frontier_scope_changed'),@(7,'frontier_chunk_index','3000'),@(7,'highest_received_chunk_index','3193'),
                @(7,'wake_generation','177'),@(7,'reason','retry'),@(7,'route_publication_generation','6'),@(7,'runtime_generation','6'),
                @(7,'leg_generation','6'),@(7,'transport_epoch','2'),@(7,'bridge_recovery_generation','2'),@(7,'checkpoint_generation','2'),
                @(7,'data_session_identity','other'),@(7,'data_session_generation','2'),@(8,'transfer_paused','1'),
                @(8,'contiguous_committed_chunk_index','3000'),@(8,'transport_epoch','2'),@(8,'epoch','0'),
                @(5,'log_writer_attempt_utc_ticks','0'),@(8,'log_writer_attempt_utc_ticks','0'))){
                $events=@(Fixture);$events[$mutation[0]].Fields[$mutation[1]]=$mutation[2]
                if($null -ne (Closure $events)){throw "mutation $mutation recovered"}
            }
            foreach($poison in @('filetransfer_data_session_removed','filetransfer_data_session_recreated',
                    'file_transfer_inbound_terminal','filetransfer_live_route_epoch_terminal','filetransfer_terminalized_by_session_end')){
                $events=@(Fixture)+@(Event $poison 1950 @{})
                if($null -ne (Closure $events)){throw "$poison recovered"}
            }
            $events=@(Fixture)+@(Event 'filetransfer_route_selected' 1950 @{runtime_generation='6'})+@(Event 'filetransfer_route_selected' 1960 @{})
            if($null -ne (Closure $events)){throw 'ABA source restoration recovered'}
            $events=@(Fixture);$conflict=Event $events[3].EventName 1770 @{accepted_any='0'}
            if($null -ne (Closure (@($events)+@($conflict)))){throw 'conflicting duplicate recovered'}
            foreach($position in @(2,3,4,5,6)){
                $events=@(Fixture);$competing=Event $events[$position].EventName (1761+($position-2)*10) $events[$position].Fields
                $competing.Fields['log_writer_sequence']=[string](1762+($position-2)*10)
                $competing.Fields['accepted_any']='0';$competing.Fields['dispatch_observed']='0'
                if($null -ne (Closure (@($events)+@($competing)))){throw "competing stage $position recovered"}
            }
            $events=@(Fixture)+@(Event 'filetransfer_fallback_frontier_repair_contract_settled' 1950 @{authority_id='repair32';settlement_outcome='Superseded'})
            if($null -ne (Closure $events)){throw 'retired request recovered'}
            # No repair_progress_observed exists. The new deferral is support for
            # the first candidate and a separately cancelled owned obligation.
            foreach($count in @(0,1,2)){ foreach($width in @(1,8,16)){
                $script:FileTransferFallbackFrontierGracePartitionWidth=$width
                $events=@(WithEarlyWake $count)
                $index=New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $events
                $result=Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $events -Index $index
                if((@($result.RawCount,$result.DistinctCount,$result.RecoveredCount,$result.UnresolvedCount) -join '/') -cne '2/1/1/0'){
                    throw "width ${width}: $($result | ConvertTo-Json -Depth 3 -Compress)"
                }
            } }
            # Independently projected full/compact/GUI locations must not alter
            # the exact writer-bound recovery witness.
            foreach($prefix in @(0,17,53)){
                $position=$prefix
                $projected=@(foreach($event in $good){
                    $line='[2026-09-06 04:32:00Z] [INFO] [Fixture] '+(@($event.Fields.Keys | Sort-Object | ForEach-Object {"$_=$($event.Fields[$_])"}) -join '; ')
                    $parsed=ConvertFrom-FileTransferLogLine -Line $line -FilePath 'projected.log' -LineNumber (++$position) -Sequence $position
                    $parsed.Fields['gui_evidence_sequence']=[string]$position
                    if($prefix -eq 53){[pscustomobject]@{Fields=$parsed.Fields;Sequence=$parsed.Sequence}}else{$parsed}
                })
                $candidate=@($projected | Where-Object {$_.Fields.log_writer_sequence -eq '1001'})[0]
                $closure=Get-FileTransferStallGraceDispatchedProgressClosure -Events $projected -Episode ([pscustomobject]@{Event=$candidate})
                if($null -eq $closure -or $closure.Fields.log_writer_sequence -cne '2101'){throw 'evidence-location projection changed closure'}
            }
            'dispatched-progress-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()], host);
        Assert.True(result.ExitCode == 0, $"Dispatched progress failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("dispatched-progress-ok", result.Stdout, StringComparison.Ordinal);
    }
}
