namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferStallGraceStaleHighestProgressTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    public async Task StallGrace_HighestOnlyStaleThenCurrentRequest_RequiresExactUnpoisonedCommittedProgress()
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
            function Event([string]$Stage,[string]$Name,[long]$Ms,[hashtable]$Extra) {
                $fields=$scope.Clone()
                foreach($key in $Extra.Keys){$fields[$key]=[string]$Extra[$key]}
                $fields['event']=$Name;$fields['log_writer_sequence']=[string]($Ms+1)
                $fields['log_writer_attempt_utc_ticks']=[string]$start.AddMilliseconds($Ms).UtcTicks
                [pscustomobject]@{Stage=$Stage;EventName=$Name;Fields=$fields;Sequence=$Ms+1;LineNumber=$Ms+1;TimestampUtc=$start.AddMilliseconds($Ms);FilePath='fixture.log'}
            }
            function PhysicalState([string]$Stage,[long]$Ms,[long]$Frontier,[long]$Highest,[long]$Epoch) {
                $state=Event $Stage 'filetransfer_v6_receiver_state_sent' $Ms @{
                    contiguous_committed_chunk_index=$Frontier;durable_received_highest_chunk_index=$Highest;
                    transfer_paused='0';epoch=$Epoch;reason='chunk_batch_committed'
                }
                foreach($key in $scope.Keys){if($key -notin @('log_writer_id','session_id','transfer_id','transport_epoch')){$state.Fields.Remove($key)}}
                $state
            }
            function RepairEvent([string]$Stage,[string]$Name,[long]$Ms,[hashtable]$Fields) {
                $event=Event $Stage $Name $Ms $Fields
                foreach($key in @('checkpoint_generation','data_session_identity','data_session_generation')){$event.Fields.Remove($key)}
                $event
            }
            function Fixture([int]$Shape=9118) {
                if($Shape -eq 9118){$front=3003;$highest=3698;$growth=3747;$grantHighest=3753;$advanceFront=3006;$advanceHighest=3759;$stateFront=3006;$stateHighest=3793;$wake=178;$staleMs=1756;$reason='chunk_batch_committed'}
                else{$front=3189;$highest=3857;$growth=3869;$grantHighest=3870;$advanceFront=3195;$advanceHighest=3891;$stateFront=3207;$stateHighest=3918;$wake=195;$staleMs=1749;$reason='frontier_stall_repair_due'}
                $fingerprint=if($Shape -eq 9118){'frng-dee4c89fd0298a92e58ac1e5'}else{'frng-35fa62f8dff3e37b739a7bd4'}
                $stall=@{frontier_chunk_index=$front;highest_received_chunk_index=$highest;wake_generation=$wake;
                    stall_started_utc=$start.AddMilliseconds(1000).ToString('O');wake_deadline_utc=$start.AddMilliseconds(1750).ToString('O');
                    authority_effect='none';stall_grace_ms='750';elapsed_ms='0';reason='chunk_batch_committed'}
                $repair=@{authority_schema='2';authority_id='repair33';repair_generation='33';repair_request_key="fallback-frontier:${front}:1:${fingerprint}";range_fingerprint=$fingerprint;
                    primary_protocol_repair_request_id='request33';primary_request_correlation_id='correlation33';
                    protocol_repair_request_id='request33';request_correlation_id='correlation33';
                    predecessor_authority_id='(none)';frontier_chunk_index=$front;durable_received_highest_chunk_index=$grantHighest;
                    source_kind='ReceiverRequest';target_transport='regular_nkn';decision_reason='fallback_frontier_request';
                    authority_decision_reason='fallback_frontier_request';range_count='1';requested_chunk_count='1'}
                Event 'publication' 'filetransfer_route_hint_authorized_publication_accepted' 0 @{
                    target_route='post_tuna_fallback_v6';target_protocol_version='6';target_transport='regular_nkn';
                    route_publication_action_kind='RoutePublication';coordinator_decision_reason='accepted';
                    fallback_leg_generation='5';fallback_transport_epoch='1';fallback_bridge_recovery_generation='1'}
                PhysicalState 'seed' 500 $front $highest 28
                Event 'stall' 'filetransfer_v6_frontier_request_deferred' 1000 $stall
                PhysicalState 'growth' 1600 $front $growth 29
                $stale=$stall.Clone();$stale['decision_status']='stale';$stale['decision_reason']='frontier_scope_changed'
                Event 'stale' 'filetransfer_v6_frontier_stall_grace_recheck_decided' $staleMs $stale
                RepairEvent 'grant' 'filetransfer_fallback_frontier_repair_authority_granted' 1800 $repair
                $delivery=$repair.Clone();$delivery['authority_id']='control33';$delivery['repair_request_id']='request33';$delivery['request_correlation_id']='correlation33'
                $delivery['kind']='frontier_request';$delivery['frame_type']='filetransfer.frontier_request.v6';$delivery['accepted_any']='1';$delivery['peer_visible_any']='1';$delivery['reason']=$reason
                $delivery['control_plane_generation']='70';$delivery['first_start_chunk_index']=$front;$delivery['first_chunk_count']='1';$delivery['local_only_rejected']='0'
                $receipt=RepairEvent 'delivery' 'filetransfer_control_plane_delivery_observed' 1810 $delivery
                foreach($key in @('runtime_generation','terminal_generation','bridge_decision_generation','route_publication_generation','route_commit_generation')){$receipt.Fields.Remove($key)}
                $receipt
                $dispatch=$repair.Clone();$dispatch['dispatch_observed']='1'
                RepairEvent 'dispatch' 'filetransfer_fallback_frontier_repair_dispatched' 1820 $dispatch
                $sent=$repair.Clone();$sent['repair_request_id']='request33';$sent['request_correlation_id']='correlation33';$sent['reason']=$reason
                RepairEvent 'send' 'filetransfer_v6_frontier_request_sent' 1830 $sent
                $advance=$stall.Clone();$advance['frontier_chunk_index']=$advanceFront;$advance['highest_received_chunk_index']=$advanceHighest;$advance['wake_generation']=$wake+1
                $advance['stall_started_utc']=$start.AddMilliseconds(2000).ToString('O');$advance['wake_deadline_utc']=$start.AddMilliseconds(2750).ToString('O')
                Event 'advance' 'filetransfer_v6_frontier_request_deferred' 2000 $advance
                PhysicalState 'state' 2100 $stateFront $stateHighest 30
                Event 'terminal' 'file_transfer_inbound_terminal' 2200 @{state='Completed'}
            }
            function Stage([object[]]$Events,[string]$Name){return @($Events | Where-Object {$_.Stage -ceq $Name})[0]}
            function Closure([object[]]$Events) {
                $stall=@($Events | Where-Object {$_.Fields.log_writer_sequence -ceq '1001'})[0]
                Get-FileTransferStallGraceStaleHighestProgressClosure -Events $Events -Episode ([pscustomobject]@{Event=$stall})
            }
            function RequireClosed([object[]]$Events,[string]$Label) {
                $closure=Closure $Events
                if($null -eq $closure -or $closure.Fields.log_writer_sequence -cne '2101'){throw "$Label did not close on committed-state witness"}
            }
            function RequireOpen([object[]]$Events,[string]$Label) {
                if($null -ne (Closure $Events)){throw "$Label unexpectedly recovered"}
            }
            function Counts($Result){return @($Result.RawCount,$Result.DistinctCount,$Result.RecoveredCount,$Result.UnresolvedCount) -join '/'}
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
            $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $n.Name -ceq 'Get-TunaGuiFallbackFrontierStallGraceRecheckFailure'},$true))[0]
            Invoke-Expression $definition.Extent.Text
            foreach($shape in @(9118,9601)){
                $events=@(Fixture $shape)
                RequireClosed $events "production shape $shape"
                foreach($missingSend in @($false,$true)){
                    $inputEvents=@($events | Where-Object {-not $missingSend -or $_.Stage -cne 'send'})
                    $lines=@($inputEvents | ForEach-Object {$row=$_; (($row.Fields.Keys | Sort-Object | ForEach-Object {"$_=$($row.Fields[$_])"}) -join '; ') + "; gui_evidence_sequence=$($row.Sequence)"})
                    $gui=Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $lines -IncludeEpisodeAudit
                    $target=@($gui.EpisodeAudit | Where-Object {$_.Event.Fields.log_writer_sequence -ceq '1001'})
                    if($target.Count -ne 1 -or $target[0].Excluded -or $target[0].Recovered -eq $missingSend){throw "actual GUI stale-highest closure/admission differs: $shape"}
                }
                $stall=Stage $events 'stall'
                if($null -ne (Get-FileTransferStallGraceDispatchedProgressClosure -Events $events -Episode ([pscustomobject]@{Event=$stall}))){throw 'stale fixture bypassed the original dispatched-only closure'}
                RequireClosed (@($events)+@((Stage $events 'delivery'),(Stage $events 'stale'),(Stage $events 'state'))) "exact duplicate shape $shape"
            }
            # A predecessor may retire after authorizing the selected current
            # request. Its historical frontier/highest is not current state.
            $events=@(Fixture)
            foreach($stage in @('grant','dispatch','send')){(Stage $events $stage).Fields.predecessor_authority_id='repair32'}
            $old=RepairEvent 'old-retirement' 'filetransfer_fallback_frontier_repair_contract_settled' 1801 @{
                authority_schema='2';authority_id='repair32';successor_authority_id='repair33';repair_generation='32';frontier_chunk_index='3000';
                durable_received_highest_chunk_index='3194';settlement_outcome='Superseded';decision_reason='fallback_frontier_successor_authorized'
            }
            RequireClosed (@($events)+@($old)) 'historical predecessor supersession'
            RequireOpen $events 'missing supplied predecessor retirement'
            $old.Fields.successor_authority_id='other'
            RequireOpen (@($events)+@($old)) 'wrong predecessor successor linkage'
            foreach($missing in @('growth','stale','grant','delivery','dispatch','send','advance','state')){
                $events=@(Fixture | Where-Object {$_.Stage -cne $missing})
                RequireOpen $events "missing $missing"
            }
            foreach($mutation in @(
                @('growth','contiguous_committed_chunk_index','3004'),@('growth','contiguous_committed_chunk_index','3002'),
                @('growth','durable_received_highest_chunk_index','3698'),@('growth','durable_received_highest_chunk_index','3754'),
                @('growth','transport_epoch','2'),@('growth','transfer_paused','1'),@('growth','epoch','0'),
                @('stale','decision_status','dispatched'),@('stale','decision_reason','wake_generation_replaced'),@('stale','decision_reason','unknown'),
                @('stale','authority_effect','dispatch'),@('stale','wake_generation','179'),@('stale','highest_received_chunk_index','3747'),
                @('stale','wake_deadline_utc','2026-09-06T04:32:09Z'),@('stale','data_session_generation','2'),
                @('grant','authority_id','other'),@('grant','repair_generation','0'),@('grant','repair_request_key','other'),
                @('grant','range_fingerprint','other'),@('grant','primary_protocol_repair_request_id','other'),@('grant','primary_request_correlation_id','other'),
                @('grant','source_kind','PeerRequest'),@('grant','target_transport','tuna'),@('grant','runtime_generation','6'),
                @('grant','authority_schema','1'),@('grant','authority_decision_reason','other'),@('grant','range_count','2'),@('grant','requested_chunk_count','2'),
                @('grant','frontier_chunk_index','3004'),@('grant','durable_received_highest_chunk_index','3746'),
                @('delivery','accepted_any','0'),@('delivery','peer_visible_any','0'),@('delivery','repair_request_id','other'),
                @('delivery','request_correlation_id','other'),@('delivery','range_fingerprint','other'),@('delivery','kind','receiver_state'),
                @('delivery','frame_type','filetransfer.receiver_state.v6'),@('delivery','reason','retry'),
                @('delivery','authority_schema','1'),@('delivery','control_plane_generation','0'),@('delivery','authority_id','repair33'),
                @('delivery','first_start_chunk_index','3004'),@('delivery','first_chunk_count','2'),@('delivery','local_only_rejected','1'),
                @('dispatch','dispatch_observed','0'),@('dispatch','repair_generation','34'),@('dispatch','route_publication_generation','6'),
                @('dispatch','protocol_repair_request_id','other'),@('dispatch','request_correlation_id','other'),
                @('send','authority_id','other'),@('send','predecessor_authority_id','other'),@('send','repair_request_key','other'),
                @('send','repair_request_id','other'),@('send','protocol_repair_request_id','other'),@('send','request_correlation_id','other'),@('send','reason','retry'),
                @('advance','frontier_chunk_index','3003'),@('advance','highest_received_chunk_index','3752'),@('advance','wake_generation','178'),
                @('advance','reason','retry'),@('advance','authority_effect','dispatch'),@('advance','elapsed_ms','1'),@('advance','stall_grace_ms','751'),
                @('advance','runtime_generation','6'),@('advance','leg_generation','6'),@('advance','transport_epoch','2'),
                @('advance','bridge_recovery_generation','2'),@('advance','checkpoint_generation','2'),@('advance','route_publication_generation','6'),
                @('advance','data_session_identity','other'),@('advance','data_session_generation','2'),
                @('state','transfer_paused','1'),@('state','contiguous_committed_chunk_index','3003'),@('state','durable_received_highest_chunk_index','3758'),
                @('state','transport_epoch','2'),@('state','epoch','0'),@('send','log_writer_attempt_utc_ticks','0'),@('state','log_writer_attempt_utc_ticks','0'))){
                $events=@(Fixture);(Stage $events $mutation[0]).Fields[$mutation[1]]=$mutation[2]
                RequireOpen $events "mutation $mutation"
            }
            foreach($missingScope in @('runtime_generation','route_publication_generation','data_session_identity','checkpoint_generation')){
                $events=@(Fixture);(Stage $events 'stall').Fields.Remove($missingScope)
                RequireOpen $events "missing original $missingScope"
            }
            # Pairwise equality is not enough when every stage carries the
            # same malformed canonical key or nonnumeric source generation.
            foreach($badRange in @(
                @('fallback-frontier:9999:1:frng-dee4c89fd0298a92e58ac1e5','frng-dee4c89fd0298a92e58ac1e5'),
                @('fallback-frontier:3003:2:frng-dee4c89fd0298a92e58ac1e5','frng-dee4c89fd0298a92e58ac1e5'),
                @('fallback-frontier:3003:1:fingerprint','fingerprint'))){
                $events=@(Fixture)
                foreach($stage in @('grant','delivery','dispatch','send')){
                    (Stage $events $stage).Fields.repair_request_key=$badRange[0]
                    (Stage $events $stage).Fields.range_fingerprint=$badRange[1]
                }
                RequireOpen $events "all-stage invalid canonical range $badRange"
            }
            foreach($field in @('live_route_epoch','runtime_generation','terminal_generation','leg_generation','transport_epoch',
                    'bridge_recovery_generation','bridge_decision_generation','checkpoint_generation','route_publication_generation',
                    'route_commit_generation','data_session_generation')){
                foreach($invalid in @('not-a-number','-1')){
                    $events=@(Fixture)
                    foreach($event in $events){if($event.Fields.ContainsKey($field)){$event.Fields[$field]=$invalid}}
                    RequireOpen $events "all-stage invalid source $field=$invalid"
                }
            }
            foreach($stage in @('stall','growth','stale','grant','delivery','dispatch','send','advance','state')){
                $events=@(Fixture);$stall=Stage $events 'stall'
                (Stage $events $stage).Fields.Remove('log_writer_sequence')
                $closure=Get-FileTransferStallGraceStaleHighestProgressClosure -Events $events -Episode ([pscustomobject]@{Event=$stall})
                if($null -ne $closure){throw "missing writer sequence at $stage recovered"}
            }
            $events=@(Fixture 9601);(Stage $events 'grant').Fields.log_writer_attempt_utc_ticks=[string]$start.AddMilliseconds(1749).UtcTicks
            RequireOpen $events 'grant before original deadline'
            $events=@(Fixture);(Stage $events 'growth').Sequence=1771;(Stage $events 'growth').Fields.log_writer_attempt_utc_ticks=[string]$start.AddMilliseconds(1770).UtcTicks
            RequireOpen $events 'growth observed only after stale'
            $events=@(Fixture);(Stage $events 'send').Sequence=1809
            RequireOpen $events 'send before delivery and dispatch'
            $events=@(Fixture);$advance=Stage $events 'advance';$advance.Sequence=1826
            $advance.Fields.stall_started_utc=$start.AddMilliseconds(1825).ToString('O');$advance.Fields.wake_deadline_utc=$start.AddMilliseconds(2575).ToString('O')
            $advance.Fields.log_writer_attempt_utc_ticks=[string]$start.AddMilliseconds(1825).UtcTicks
            RequireOpen $events 'committed advance before send'
            foreach($poison in @('filetransfer_data_session_removed','filetransfer_data_session_recreated',
                    'file_transfer_inbound_terminal','filetransfer_live_route_epoch_terminal','filetransfer_terminalized_by_session_end')){
                RequireOpen (@(Fixture)+@(Event 'poison' $poison 1950 @{})) $poison
            }
            RequireOpen (@(Fixture)+@(PhysicalState 'pause' 1950 3003 3753 30 | ForEach-Object {$_.Fields.transfer_paused='1';$_})) 'pause before progress'
            RequireOpen (@(Fixture)+@(Event 'changed' 'filetransfer_route_selected' 1950 @{runtime_generation='6'})+@(Event 'restored' 'filetransfer_route_selected' 1960 @{})) 'ABA restoration'
            RequireOpen (@(Fixture)+@(RepairEvent 'retired' 'filetransfer_fallback_frontier_repair_contract_settled' 1950 @{authority_id='repair33';settlement_outcome='Superseded'})) 'selected authority retirement'
            foreach($stage in @('growth','stale','grant','delivery','dispatch','send','advance','state')){
                $events=@(Fixture);$original=Stage $events $stage
                $copy=Event 'conflict' $original.EventName ([long]$original.Sequence-1) $original.Fields
                $copy.Fields['log_writer_attempt_utc_ticks']=$original.Fields.log_writer_attempt_utc_ticks
                $copy.Fields['conflicting_payload']='1'
                RequireOpen (@($events)+@($copy)) "conflicting duplicate $stage"
            }
            foreach($changedIdentity in @('session_id','transfer_id')){
                $events=@(Fixture);$original=Stage $events 'delivery'
                $copy=[pscustomobject]@{EventName=$original.EventName;Fields=$original.Fields.Clone();Sequence=$original.Sequence;
                    LineNumber=$original.LineNumber;TimestampUtc=$original.TimestampUtc;FilePath=$original.FilePath}
                $copy.Fields[$changedIdentity]='other'
                RequireOpen (@($events)+@($copy)) "conflicting writer copy changed $changedIdentity"
                RequireOpen (@($copy)+@($events)) "reordered conflicting writer copy changed $changedIdentity"
            }
            $events=@(Fixture);(Stage $events 'delivery').EventName='filetransfer_v6_receiver_state_sent'
            RequireOpen $events 'EventName disagrees with event field'
            $events=@(Fixture);$original=Stage $events 'delivery'
            $copy=[pscustomobject]@{EventName='filetransfer_v6_receiver_state_sent';Fields=$original.Fields.Clone();Sequence=$original.Sequence;
                LineNumber=$original.LineNumber;TimestampUtc=$original.TimestampUtc;FilePath=$original.FilePath}
            RequireOpen (@($events)+@($copy)) 'writer copy EventName disagreement'
            $events=@(Fixture);$original=Stage $events 'delivery'
            $copy=[pscustomobject]@{EventName=$original.EventName;Fields=$original.Fields.Clone();Sequence=999;
                LineNumber=999;TimestampUtc=$original.TimestampUtc;FilePath=$original.FilePath}
            $copy.Fields.transfer_id='other'
            RequireOpen (@($copy)+@($events)) 'pre-stall copy sequence cannot hide conflicting writer identity'
            foreach($stage in @('grant','delivery','dispatch','send','stale')){
                $events=@(Fixture);$original=Stage $events $stage
                $copy=Event 'competing' $original.EventName ([long]$original.Sequence) $original.Fields
                $copy.Fields['accepted_any']='0';$copy.Fields['dispatch_observed']='0'
                RequireOpen (@($events)+@($copy)) "competing subject $stage"
            }
            $events=@(Fixture);$other=RepairEvent 'competing' 'filetransfer_fallback_frontier_repair_authority_granted' 1790 (Stage $events 'grant').Fields
            $other.Fields.authority_id='other';$other.Fields.primary_protocol_repair_request_id='other';$other.Fields.primary_request_correlation_id='other'
            RequireOpen (@($events)+@($other)) 'earlier competing current grant'
            $events=@(Fixture);$other=RepairEvent 'competing' 'filetransfer_v6_frontier_request_sent' 1829 (Stage $events 'send').Fields
            foreach($field in @('authority_id','primary_protocol_repair_request_id','protocol_repair_request_id','repair_request_id','request_correlation_id')){$other.Fields[$field]='other'}
            RequireOpen (@($events)+@($other)) 'primary-correlation-only competing send'
            RequireOpen (@(Fixture)+@(PhysicalState 'same-epoch-mutation' 1900 3003 3753 29)) 'same receiver epoch changed physical state'
            foreach($regression in @(@('3005','3759'),@('3006','3758'))){
                $events=@(Fixture);$extra=Event 'later-regression' 'filetransfer_v6_frontier_request_deferred' 2050 (Stage $events 'advance').Fields
                $extra.Fields.frontier_chunk_index=$regression[0];$extra.Fields.highest_received_chunk_index=$regression[1];$extra.Fields.wake_generation='180'
                $extra.Fields.stall_started_utc=$start.AddMilliseconds(2050).ToString('O');$extra.Fields.wake_deadline_utc=$start.AddMilliseconds(2800).ToString('O')
                RequireOpen (@($events)+@($extra)) "later full-scope committed deferral regressed $regression"
            }
            # Other-frontier negative evidence must survive the bounded slice,
            # not merely the positive same-frontier request bucket.
            foreach($width in @(1,8,16)){
                $script:FileTransferFallbackFrontierGracePartitionWidth=$width
                foreach($shape in @(9118,9601)){
                    $events=@(Fixture $shape);$index=New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $events
                    $result=Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $events -Index $index
                    if((Counts $result) -cne '2/1/1/0'){throw "shape $shape width ${width}: $(Counts $result)"}
                    $stall=Stage $events 'stall';$front=[long]$stall.Fields.frontier_chunk_index
                    $batch=if($width -eq 1){@($front)}else{@($front,[long](Stage $events 'advance').Fields.frontier_chunk_index)}
                    $slice=@(Get-FileTransferFallbackFrontierGracePartitionSlice -Index $index -TransferKey (Get-FileTransferFallbackFrontierGraceTransferKey -Event $stall) -Frontiers $batch)
                    RequireClosed $slice "shape $shape bounded width $width"
                    $owned=[Collections.Generic.HashSet[object]]::new();[void]$owned.Add($stall)
                    $full=& $script:FileTransferFallbackFrontierStallGraceLegacyCorrelation -Events $events -OwnedCandidateEvents $owned
                    $bounded=& $script:FileTransferFallbackFrontierStallGraceLegacyCorrelation -Events $slice -OwnedCandidateEvents $owned
                    if((Counts $full) -cne '1/1/1/0' -or (Counts $bounded) -cne (Counts $full)){throw 'owned singleton full/slice parity changed'}
                    if($bounded.CandidateRecords[0].Event.Fields.log_writer_sequence -cne '1001'){throw 'support became the owned candidate'}
                }
                foreach($negative in @('regression','scope','first-advance')){
                    $events=@(Fixture)
                    $extra=if($negative -eq 'regression'){PhysicalState 'regression' 1950 3002 3753 30}
                    elseif($negative -eq 'scope'){Event 'scope' 'filetransfer_v6_receiver_state_deferred' 1950 @{next_chunk_index='3077';highest_received_chunk_index='3800';runtime_generation='6'}}
                    else{
                        $invalidAdvance=Event 'first-advance' 'filetransfer_v6_frontier_request_deferred' 1930 (Stage $events 'advance').Fields
                        $invalidAdvance.Fields.frontier_chunk_index='3004';$invalidAdvance.Fields.highest_received_chunk_index='3755';$invalidAdvance.Fields.reason='retry'
                        $invalidAdvance.Fields.stall_started_utc=$start.AddMilliseconds(1930).ToString('O');$invalidAdvance.Fields.wake_deadline_utc=$start.AddMilliseconds(2680).ToString('O')
                        $invalidAdvance
                    }
                    $events=@($events)+@($extra)
                    if($negative -eq 'scope'){$events+=@(Event 'restored' 'filetransfer_v6_receiver_state_deferred' 1960 @{next_chunk_index='3077';highest_received_chunk_index='3800'})}
                    RequireOpen $events "full $negative"
                    $stall=Stage $events 'stall';$index=New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $events
                    $slice=@(Get-FileTransferFallbackFrontierGracePartitionSlice -Index $index -TransferKey (Get-FileTransferFallbackFrontierGraceTransferKey -Event $stall) -Frontiers @([long]$stall.Fields.frontier_chunk_index))
                    RequireOpen $slice "bounded $negative width $width"
                    $owned=[Collections.Generic.HashSet[object]]::new();[void]$owned.Add($stall)
                    $bounded=& $script:FileTransferFallbackFrontierStallGraceLegacyCorrelation -Events $slice -OwnedCandidateEvents $owned
                    if((Counts $bounded) -cne '1/1/0/1'){throw "negative $negative ownership/result changed"}
                }
            }
            # Independent source-location projections, including GUI-shaped
            # objects, exercise this primitive, not actual GUI admission.
            foreach($prefix in @(0,17,53)){
                $position=$prefix
                $projected=@(foreach($event in @(Fixture)){
                    $line='[2026-09-06 04:32:00Z] [INFO] [Fixture] '+(@($event.Fields.Keys | Sort-Object | ForEach-Object {"$_=$($event.Fields[$_])"}) -join '; ')
                    $parsed=ConvertFrom-FileTransferLogLine -Line $line -FilePath 'projected.log' -LineNumber (++$position) -Sequence $position
                    $parsed.Fields['gui_evidence_sequence']=[string]$position
                    if($prefix -eq 53){[pscustomobject]@{Fields=$parsed.Fields;Sequence=$parsed.Sequence}}else{$parsed}
                })
                RequireClosed $projected "projection $prefix"
            }
            'stale-highest-progress-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()]);
        Assert.True(result.ExitCode == 0, $"Stale highest progress failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("stale-highest-progress-ok", result.Stdout, StringComparison.Ordinal);
    }
}
