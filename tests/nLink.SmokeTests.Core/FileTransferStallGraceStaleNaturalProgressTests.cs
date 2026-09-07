namespace NLink.SmokeTests;

public sealed class FileTransferStallGraceStaleNaturalProgressTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("positive")]
    [InlineData("missing")]
    [InlineData("mutation")]
    [InlineData("poison_identity")]
    [InlineData("old_request")]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    public async Task StallGrace_StaleNaturalProgress_RequiresExactUnpoisonedPublishedCommit(string testCase)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var script = """
            param([string]$RepoRoot,[string]$Case)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')
            $start=[DateTimeOffset]::Parse('2026-09-06T20:42:02Z')
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
            function Physical([string]$Stage,[string]$Name,[long]$Ms,[hashtable]$Extra) {
                $event=Event $Stage $Name $Ms $Extra
                foreach($key in $scope.Keys){if($key -notin @('log_writer_id','session_id','transfer_id','transport_epoch')){$event.Fields.Remove($key)}}
                $event
            }
            function State([string]$Stage,[long]$Ms,[long]$Frontier,[long]$Highest,[long]$Epoch) {
                Physical $Stage 'filetransfer_v6_receiver_state_sent' $Ms @{
                    contiguous_committed_chunk_index=$Frontier;durable_received_highest_chunk_index=$Highest;
                    transfer_paused='0';epoch=$Epoch;reason='chunk_batch_committed'
                }
            }
            function Fixture([switch]$Request) {
                Event 'publication' 'filetransfer_route_hint_authorized_publication_accepted' 0 @{
                    target_route='post_tuna_fallback_v6';target_protocol_version='6';target_transport='regular_nkn';
                    route_publication_action_kind='RoutePublication';coordinator_decision_reason='accepted';
                    fallback_leg_generation='5';fallback_transport_epoch='1';fallback_bridge_recovery_generation='1'
                }
                State 'seed' 500 7836 8054 287
                $stall=@{frontier_chunk_index='7836';highest_received_chunk_index='8054';wake_generation='9';
                    stall_started_utc=$start.AddMilliseconds(1000).ToString('O');wake_deadline_utc=$start.AddMilliseconds(1750).ToString('O');
                    authority_effect='none';stall_grace_ms='750';elapsed_ms='0';reason='frontier_recent_progress_grace_elapsed'}
                if($Request){$stall.reason='chunk_batch_committed'}
                Event 'stall' 'filetransfer_v6_frontier_request_deferred' 1000 $stall
                if($Request){
                    $repair=@{authority_id='repair32';repair_generation='32';repair_request_key='range7836';range_fingerprint='fingerprint';
                        primary_protocol_repair_request_id='request32';primary_request_correlation_id='correlation32';
                        predecessor_authority_id='repair31';frontier_chunk_index='7836';durable_received_highest_chunk_index='8054';
                        source_kind='ReceiverRequest';target_transport='regular_nkn'}
                    Event 'grant' 'filetransfer_fallback_frontier_repair_authority_granted' 1760 $repair
                    $delivery=$repair.Clone();$delivery.authority_id='control32';$delivery.repair_request_id='request32';$delivery.request_correlation_id='correlation32'
                    $delivery.kind='frontier_request';$delivery.frame_type='filetransfer.frontier_request.v6';$delivery.accepted_any='1';$delivery.peer_visible_any='1'
                    Event 'delivery' 'filetransfer_control_plane_delivery_observed' 1770 $delivery
                    $dispatch=$repair.Clone();$dispatch.dispatch_observed='1'
                    Event 'dispatch' 'filetransfer_fallback_frontier_repair_dispatched' 1780 $dispatch
                    $sent=$repair.Clone();$sent.repair_request_id='request32';$sent.request_correlation_id='correlation32';$sent.reason='frontier_stall_grace_elapsed'
                    Event 'send' 'filetransfer_v6_frontier_request_sent' 1790 $sent
                    $recheck=$stall.Clone();$recheck.decision_status='dispatched';$recheck.decision_reason='exact_receiver_request_dispatched'
                    Event 'recheck' 'filetransfer_v6_frontier_stall_grace_recheck_decided' 1900 $recheck
                    $advance=$stall.Clone();$advance.frontier_chunk_index='7839';$advance.highest_received_chunk_index='8114';$advance.wake_generation='10'
                    $advance.stall_started_utc=$start.AddMilliseconds(1950).ToString('O');$advance.wake_deadline_utc=$start.AddMilliseconds(2700).ToString('O')
                    Event 'advance' 'filetransfer_v6_frontier_request_deferred' 1950 $advance
                }
                else{
                    State 'growth' 1100 7836 8057 288
                    Physical 'pending' 'filetransfer_v6_sparse_write_committed' 1600 @{
                        contiguous_chunks_committed='0';next_chunk_index='7836';highest_received_chunk_index='8114'
                    }
                    $stale=$stall.Clone();$stale.decision_status='stale';$stale.decision_reason='frontier_scope_changed'
                    Event 'stale' 'filetransfer_v6_frontier_stall_grace_recheck_decided' 1754 $stale
                    Event 'recent' 'filetransfer_v6_frontier_recent_progress_grace_deferred' 1950 @{
                        frontier_chunk_index='7839';highest_received_chunk_index='8114';wake_generation='849';
                        committed_progress_utc=$start.AddMilliseconds(1950).ToString('O');wake_deadline_utc=$start.AddMilliseconds(2950).ToString('O');
                        authority_effect='none';decision_status='held';decision_reason='recent_progress_grace_current'
                    }
                    Physical 'write' 'filetransfer_v6_sparse_write_committed' 1951 @{
                        contiguous_chunks_committed='3';next_chunk_index='7839';highest_received_chunk_index='8114'
                    }
                }
                State 'state' 1960 7839 8114 289
                Event 'terminal' 'file_transfer_inbound_terminal' 2200 @{state='Completed'}
            }
            function Stage([object[]]$Events,[string]$Name){return @($Events | Where-Object {$_.Stage -ceq $Name})[0]}
            function Closure([object[]]$Events) {
                $stall=@($Events | Where-Object {
                    if($null -ne $_.PSObject.Properties['Stage']){$_.Stage -ceq 'stall'}
                    else{$_.Fields['log_writer_sequence'] -ceq '1001'}
                })[0]
                Get-FileTransferStallGraceStaleNaturalProgressClosure -Events $Events -Episode ([pscustomobject]@{Event=$stall})
            }
            function RequireClosed([object[]]$Events,[string]$Label) {
                $closed=Closure $Events
                if($null -eq $closed -or $closed.Fields.log_writer_sequence -cne '1961'){throw "$Label did not close at published receiver state"}
            }
            function RequireOpen([object[]]$Events,[string]$Label) {
                if($null -ne (Closure $Events)){throw "$Label unexpectedly recovered"}
            }
            function Counts($Result){return @($Result.RawCount,$Result.DistinctCount,$Result.RecoveredCount,$Result.UnresolvedCount) -join '/'}
            function Lines([object[]]$Events) {
                foreach($row in $Events){(($row.Fields.Keys | Sort-Object | ForEach-Object {"$_=$($row.Fields[$_])"}) -join '; ') + "; gui_evidence_sequence=$($row.Sequence)"}
            }
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
            $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $n.Name -ceq 'Get-TunaGuiFallbackFrontierStallGraceRecheckFailure'},$true))[0]
            Invoke-Expression $definition.Extent.Text
            function AssertConsumers([object[]]$Events,[bool]$Recovered,[string]$Label,[int]$Raw=1) {
                $expected=if($Recovered){"$Raw/1/1/0"}else{"$Raw/1/0/1"}
                $full=& $script:FileTransferFallbackFrontierStallGraceLegacyCorrelation -Events $Events
                if((Counts $full) -cne $expected){throw "$Label full: $(Counts $full), expected $expected"}
                $fullCandidate=@($full.CandidateRecords | Where-Object {$_.Event.Fields.log_writer_sequence -ceq '1001'})
                if($fullCandidate.Count -ne 1 -or ($Recovered -and $fullCandidate[0].RecoveryEvent.Fields.log_writer_sequence -cne '1961')){throw "$Label full changed the closing publication"}
                foreach($width in @(1,8,16)){
                    $script:FileTransferFallbackFrontierGracePartitionWidth=$width
                    $index=New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $Events
                    $result=Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $Events -Index $index
                    if((Counts $result) -cne $expected){throw "$Label width ${width}: $(Counts $result), expected $expected"}
                    $candidate=@($result.CandidateRecords | Where-Object {$_.Event.Fields.log_writer_sequence -ceq '1001'})
                    if($candidate.Count -ne 1 -or $candidate[0].Recovered -ne $Recovered){throw "$Label width $width changed candidate ownership"}
                    if($Recovered -and $candidate[0].RecoveryEvent.Fields.log_writer_sequence -cne '1961'){throw "$Label width $width changed recovery witness"}
                    if(-not $Recovered -and $null -ne $candidate[0].RecoveryEvent){throw "$Label width $width retained an unproved recovery witness"}
                }
                $gui=Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines @(Lines $Events) -IncludeEpisodeAudit
                $candidate=@($gui.EpisodeAudit | Where-Object {$_.Event.Fields.log_writer_sequence -ceq '1001'})
                if($candidate.Count -ne 1 -or $candidate[0].Excluded -or $candidate[0].Recovered -ne $Recovered){throw "$Label actual GUI admission/recovery differs"}
                if($Recovered -and $candidate[0].RecoveryEvent.Fields.log_writer_sequence -cne '1961'){throw "$Label actual GUI changed recovery witness"}
                if(-not $Recovered -and -not $gui.Unresolved){throw "$Label actual GUI hid the unresolved episode"}
            }
            switch($Case){
                'positive' {
                    $events=@(Fixture)
                    RequireClosed $events 'natural shape'
                    AssertConsumers $events $true 'natural shape'
                    if(@($events | Where-Object {$_.Fields['source_kind'] -ceq 'ReceiverRequest'}).Count -ne 0){throw 'natural fixture fabricated request authority'}
                    $episode=[pscustomobject]@{Event=(Stage $events 'stall')}
                    if($null -ne (Get-FileTransferStallGraceDispatchedProgressClosure -Events $events -Episode $episode) -or
                        $null -ne (Get-FileTransferStallGraceStaleHighestProgressClosure -Events $events -Episode $episode)){throw 'natural shape bypassed a request-only helper'}
                    RequireClosed (@($events)+@((Stage $events 'stale'),(Stage $events 'recent'),(Stage $events 'state'))) 'identical writer copies'
                    RequireClosed @($events | Where-Object {$_.Stage -cne 'pending'}) 'intermediate zero-contiguous observation is optional'
                    foreach($name in @('filetransfer_fallback_frontier_repair_authority_granted','filetransfer_control_plane_delivery_observed',
                        'filetransfer_fallback_frontier_repair_dispatched','filetransfer_v6_frontier_request_sent')){
                        $later=Event 'later-request' $name 2100 @{
                            authority_id='later';source_kind='ReceiverRequest';frontier_chunk_index='7836';first_start_chunk_index='7836';
                            kind='frontier_request';frame_type='filetransfer.frontier_request.v6';accepted_any='1';peer_visible_any='1';dispatch_observed='1'
                        }
                        $withLater=@($events)+@($later)
                        RequireClosed $withLater "later request after closure $name"
                        AssertConsumers $withLater $true "later request after closure $name"
                    }
                    # Reparse the same retained evidence independently with compact
                    # source locations; writer identities and timestamps stay exact.
                    foreach($prefix in @(0,17,53)){
                        $position=$prefix
                        $projected=@(foreach($line in @(Lines $events)){
                            $parsed=ConvertFrom-FileTransferLogLine -Line ('[2026-09-06 20:42:02Z] [INFO] [Fixture] '+$line) -FilePath 'regenerated-compact.log' -LineNumber (++$position) -Sequence $position
                            $parsed.Fields.gui_evidence_sequence=[string]$position
                            $parsed
                        })
                        RequireClosed $projected "regenerated compact $prefix"
                        AssertConsumers $projected $true "regenerated compact $prefix"
                    }
                }
                'missing' {
                    foreach($missing in @('growth','stale','recent','write','state')){
                        $events=@(Fixture | Where-Object {$_.Stage -cne $missing})
                        RequireOpen $events "missing $missing"
                        AssertConsumers $events $false "missing $missing"
                    }
                    $events=@(Fixture | Where-Object {$_.Stage -cne 'publication'})
                    $full=& $script:FileTransferFallbackFrontierStallGraceLegacyCorrelation -Events $events
                    if($full.DistinctCount -ne 0){throw 'missing published source was admitted'}
                    $gui=Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines @(Lines $events) -IncludeEpisodeAudit
                    $candidate=@($gui.EpisodeAudit | Where-Object {$_.Event.Fields.log_writer_sequence -ceq '1001'})
                    # Unadmitted deferrals never enter the GUI episode audit;
                    # Excluded is reserved for a source ended after admission.
                    if($candidate.Count -ne 0){throw 'GUI admitted an unpublished source'}
                }
                'mutation' {
                    foreach($mutation in @(
                        @('growth','contiguous_committed_chunk_index','7837'),@('growth','contiguous_committed_chunk_index','7835'),
                        @('growth','durable_received_highest_chunk_index','8054'),@('growth','epoch','0'),@('growth','transfer_paused','1'),@('growth','transport_epoch','2'),
                        @('stale','decision_status','dispatched'),@('stale','decision_reason','wake_generation_replaced'),@('stale','decision_reason','unknown'),
                        @('stale','authority_effect','dispatch'),@('stale','wake_generation','10'),@('stale','highest_received_chunk_index','8057'),
                        @('stale','wake_deadline_utc','2026-09-06T20:42:11Z'),@('stale','data_session_generation','2'),
                        @('recent','frontier_chunk_index','7836'),@('recent','highest_received_chunk_index','8056'),@('recent','wake_generation','0'),
                        @('recent','decision_status','rechecked'),@('recent','decision_reason','other'),@('recent','authority_effect','dispatch'),
                        @('recent','committed_progress_utc','2026-09-06T20:42:03.7500000Z'),@('recent','committed_progress_utc','invalid'),
                        @('recent','wake_deadline_utc','2026-09-06T20:42:03.7500000Z'),
                        @('write','contiguous_chunks_committed','0'),@('write','contiguous_chunks_committed','2'),@('write','next_chunk_index','7838'),
                        @('write','highest_received_chunk_index','8113'),@('write','transport_epoch','2'),
                        @('state','contiguous_committed_chunk_index','7836'),@('state','durable_received_highest_chunk_index','8113'),
                        @('state','epoch','288'),@('state','transfer_paused','1'),@('state','transport_epoch','2'),
                        @('stale','log_writer_attempt_utc_ticks','0'),@('state','log_writer_attempt_utc_ticks','0'))){
                        $events=@(Fixture);(Stage $events $mutation[0]).Fields[$mutation[1]]=$mutation[2]
                        RequireOpen $events "mutation $mutation"
                    }
                    foreach($stage in @('stall','growth','recent','write')){
                        $events=@(Fixture);(Stage $events $stage).Fields.Remove('log_writer_attempt_utc_ticks')
                        RequireOpen $events "missing $stage writer-attempt timestamp"
                        foreach($ticks in @('0','-1')){
                            $events=@(Fixture);(Stage $events $stage).Fields.log_writer_attempt_utc_ticks=$ticks
                            RequireOpen $events "nonpositive $stage writer-attempt timestamp $ticks"
                        }
                    }
                    foreach($field in @('direction','route','protocol_version','live_route_epoch','runtime_generation','terminal_generation','leg_generation',
                        'transport_epoch','bridge_recovery_generation','bridge_decision_generation','checkpoint_request_id','checkpoint_generation',
                        'route_publication_generation','route_commit_generation','data_session_identity','data_session_generation')){
                        foreach($stage in @('stall','stale','recent')){
                            $events=@(Fixture);(Stage $events $stage).Fields[$field]='other'
                            RequireOpen $events "changed $stage $field"
                            $events=@(Fixture);(Stage $events $stage).Fields.Remove($field)
                            RequireOpen $events "missing $stage $field"
                        }
                    }
                    foreach($stage in @('growth','stale','recent','write','state')){
                        $events=@(Fixture);(Stage $events $stage).Fields.Remove('log_writer_sequence')
                        RequireOpen $events "missing $stage writer sequence"
                    }
                    $events=@(Fixture);(Stage $events 'growth').Sequence=1801
                    RequireOpen $events 'growth only after stale'
                    $events=@(Fixture);(Stage $events 'recent').Sequence=1701
                    RequireOpen $events 'recent before exact stale callback'
                    $events=@(Fixture);(Stage $events 'state').Sequence=1951
                    RequireOpen $events 'published state before sparse commit'
                    $events=@(Fixture);(Stage $events 'recent').Fields.data_session_identity='other'
                    AssertConsumers $events $false 'data-session mismatch'
                }
                'poison_identity' {
                    foreach($poison in @('filetransfer_data_session_removed','filetransfer_data_session_recreated','file_transfer_inbound_terminal',
                        'filetransfer_live_route_epoch_terminal','filetransfer_terminalized_by_session_end')){
                        $events=@(Fixture)+@(Event 'poison' $poison 1900 @{})
                        RequireOpen $events $poison
                        AssertConsumers $events $false $poison
                    }
                    foreach($change in @(
                        @('filetransfer_route_selected','runtime_generation','6'),
                        @('filetransfer_v6_receiver_state_deferred','data_session_identity','other'),
                        @('filetransfer_v6_frontier_recent_progress_grace_deferred','data_session_generation','2'))){
                        $changed=Event 'changed' $change[0] 1900 @{frontier_chunk_index='7836';next_chunk_index='7836';highest_received_chunk_index='8114'}
                        $changed.Fields[$change[1]]=$change[2]
                        $restored=Event 'restored' $change[0] 1910 @{frontier_chunk_index='7836';next_chunk_index='7836';highest_received_chunk_index='8114'}
                        $events=@(Fixture)+@($changed,$restored)
                        RequireOpen $events "ABA $change"
                        AssertConsumers $events $false "ABA $change"
                    }
                    $paused=State 'paused' 1900 7836 8114 289;$paused.Fields.transfer_paused='1'
                    RequireOpen (@(Fixture)+@($paused)) 'pause before progress'
                    RequireOpen (@(Fixture)+@(State 'regression' 1900 7835 8114 289)) 'physical frontier regression'
                    foreach($stage in @('growth','stale','recent','write','state')){
                        foreach($field in @('conflicting_payload','session_id','transfer_id')){
                            $events=@(Fixture);$original=Stage $events $stage
                            $copy=[pscustomobject]@{EventName=$original.EventName;Fields=$original.Fields.Clone();Sequence=$original.Sequence;
                                LineNumber=$original.LineNumber;TimestampUtc=$original.TimestampUtc;FilePath=$original.FilePath}
                            $copy.Fields[$field]='other'
                            RequireOpen (@($events)+@($copy)) "conflicting $stage writer copy $field"
                            RequireOpen (@($copy)+@($events)) "reordered conflicting $stage writer copy $field"
                        }
                    }
                    $events=@(Fixture);(Stage $events 'recent').EventName='filetransfer_v6_receiver_state_sent'
                    RequireOpen $events 'EventName disagrees with event field'
                    foreach($name in @('filetransfer_fallback_frontier_repair_authority_granted','filetransfer_control_plane_delivery_observed',
                        'filetransfer_fallback_frontier_repair_dispatched','filetransfer_v6_frontier_request_sent')){
                        $other=Event 'competing' $name 1850 @{
                            authority_id='other';source_kind='ReceiverRequest';frontier_chunk_index='7836';first_start_chunk_index='7836';
                            kind='frontier_request';frame_type='filetransfer.frontier_request.v6';accepted_any='1';peer_visible_any='1';dispatch_observed='1'
                        }
                        $events=@(Fixture)+@($other)
                        RequireOpen $events "competing old-frontier $name"
                        AssertConsumers $events $false "competing old-frontier $name"
                    }
                    # This diagnostic event is outside the frontier index catalog;
                    # exercise the shared helper without broadening that catalog.
                    $deliveryResult=Event 'competing-result' 'filetransfer_control_plane_delivery_result' 1850 @{
                        frontier_chunk_index='7836';kind='frontier_request';frame_type='filetransfer.frontier_request.v6';
                        accepted_any='1';peer_visible_any='1'
                    }
                    RequireOpen (@(Fixture)+@($deliveryResult)) 'competing old-frontier delivery result'
                }
                'old_request' {
                    $events=@(Fixture -Request)
                    $episode=[pscustomobject]@{Event=(Stage $events 'stall')}
                    $closed=Get-FileTransferStallGraceDispatchedProgressClosure -Events $events -Episode $episode
                    if($null -eq $closed -or $closed.Fields.log_writer_sequence -cne '1961'){throw 'existing exact request branch no longer closes'}
                    RequireOpen $events 'request branch is not natural progress'
                    AssertConsumers $events $true 'existing exact request' 2
                    foreach($missing in @('grant','delivery','dispatch','send','recheck','advance','state')){
                        $events=@(Fixture -Request | Where-Object {$_.Stage -cne $missing})
                        RequireOpen $events "incomplete request missing $missing"
                        $episode=[pscustomobject]@{Event=(Stage $events 'stall')}
                        if($null -ne (Get-FileTransferStallGraceDispatchedProgressClosure -Events $events -Episode $episode)){throw "old request missing $missing recovered"}
                        if($missing -in @('grant','delivery','dispatch','send')){AssertConsumers $events $false "old request missing $missing" 2}
                    }
                }
                default {throw "Unknown test case: $Case"}
            }
            "stale-natural-progress-$Case-ok"
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), testCase]);
        Assert.True(result.ExitCode == 0, $"Stale natural progress ({testCase}) failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains($"stale-natural-progress-{testCase}-ok", result.Stdout, StringComparison.Ordinal);
    }
}
