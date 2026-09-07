namespace NLink.SmokeTests;

public sealed class FileTransferBootstrapConsumedEpisodeTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    [Trait("OpsSuite", "GuiHarness")]
    [Trait("RetainedAuthorityProofSuite", "FallbackV6ProofBootstrap")]
    public async Task FallbackBootstrap_CompletedPeerFirstEpisodeDoesNotReopen_HasFullCompactGuiParity()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferAuthorityMatching.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferFallbackV6BootstrapDirectionCorrelation.ps1')
            . (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')
            $catalog=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach($name in (Get-GuiSmokeSemanticEvidenceEventNames)){[void]$catalog.Add($name)}
            $tokens=$null; $errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
            if($errors.Count){throw 'GUI parse failed'}
            foreach($function in $ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -in @('Get-TunaGuiLogLineFieldValue','Get-TunaGuiFallbackV6ProofBootstrapInvalidFrontierFailure')},$true) |
                Group-Object Name | ForEach-Object {$_.Group[0]}){
                Invoke-Expression $function.Extent.Text
            }
            function Add([string]$name,[int]$sequence,[hashtable]$fields,[hashtable]$extra=@{}){
                $f=$fields.Clone(); foreach($key in $extra.Keys){$f[$key]=[string]$extra[$key]}
                $f.event=$name; $f.log_writer_sequence=[string]$sequence
                $f.log_writer_id='writer'; $f.session_id='session'
                $script:records.Add($f)
            }
            function Fixture([bool]$straddling) {
                $script:records=[Collections.Generic.List[hashtable]]::new()
                $source=@{transfer_id='transfer';direction='inbound';source_route='file_tuna_v4';source_protocol_version='4';
                    source_live_route_epoch='3';source_runtime_generation='4';source_route_publication_generation='4';
                    source_route_commit_generation='2';source_terminal_generation='0';source_leg_generation='4';
                    source_transport_epoch='0';source_bridge_recovery_generation='0';source_checkpoint_request_id='(none)';source_checkpoint_generation='0'}
                Add 'tuna_fallback_started' 1 @{} @{fallback_epoch='2'}
                Add 'filetransfer_fallback_nkn_proof_pending' 2 @{}
                Add 'tuna_fallback_nkn_frame_received' 3 @{} @{fallback_epoch='2';message_type='file_transfer_data_frame';channel='bulk'}
                Add 'filetransfer_fallback_nkn_proof_unconfirmed' 4 @{} @{proof='file_transfer_bulk_frame_received';bulk_seen='1';control_seen='0';requires_control_proof='1';requires_v6_epoch_recovery='0'}
                Add 'filetransfer_fallback_v6_proof_bootstrap_trigger_bound' 5 $source @{fallback_epoch='2';status='Ready';pending_generation='3';transport_epoch='0';bootstrap_generation='0';frame_fingerprint='BULK';data_session_generation='1';bulk_seen='1';control_seen='1';authority_effect='none'}
                $observation=$source.Clone()
                foreach($pair in @{fallback_epoch='2';observation_generation='3';trigger_transport_epoch='1';trigger_bootstrap_generation='2';trigger_frame_fingerprint='PEER';trigger_data_session_generation='1'}.GetEnumerator()){$observation[$pair.Key]=$pair.Value}
                Add 'filetransfer_fallback_v6_proof_bootstrap_observed' 6 $observation @{proof_kind='file_transfer_v6_frontier_request_frame_received';authority_effect='coordinator_evaluation_only'}
                $command=$observation.Clone()
                foreach($pair in @{authority_id='fta2-filetransfer_fallback_v6_proof_bootstrap-A';bootstrap_generation='2';trigger_transfer_id='transfer';source_data_session_generation='1';source_durable_checkpoint_generation='1';source_state_epoch='487';source_contiguous_frontier='4486';source_durable_highest='4972';source_chunk_count='12484';target_route='post_tuna_fallback_v6';target_protocol_version='6';target_transport='regular_nkn';target_transport_epoch='1';frame_kind='ReceiverState'}.GetEnumerator()){$command[$pair.Key]=$pair.Value}
                Add 'filetransfer_fallback_v6_proof_bootstrap_decided' 7 $command @{decision_status='Issued'}
                $receipt=@{transfer_id='transfer';direction='inbound';authority_id=$command.authority_id;bootstrap_generation='2';fallback_epoch='2';target_transport_epoch='1';frame_kind='ReceiverState';frame_fingerprint='LOCAL';receipt_status='Delivered'}
                Add 'filetransfer_fallback_v6_proof_bootstrap_execution_completed' 8 $receipt
                Add 'filetransfer_fallback_v6_proof_bootstrap_receipt_applied' 9 $receipt @{application_status='Applied'}
                $route=$source.Clone()
                foreach($name in @('source_data_session_generation','source_durable_checkpoint_generation','source_state_epoch','source_contiguous_frontier','source_durable_highest','source_chunk_count','target_route','target_protocol_version','target_transport','target_transport_epoch')){$route[$name]=$command[$name]}
                foreach($pair in @{authority_schema='2';authority_id='fta2-filetransfer_route_transition-T';transition_generation='4';transition_kind='TunaV4ToPostTunaFallbackV6';source_transport='tuna';peer_proof_generation='2';peer_proof_kind='fallback_v6_proof_bootstrap_peer_frame';peer_proof_transport_epoch='1';retained_frame_fingerprint='PEER';checkpoint_generation='1';checkpoint_state_epoch='487';checkpoint_contiguous_frontier='4486';checkpoint_durable_highest='4972';checkpoint_chunk_count='12484'}.GetEnumerator()){$route[$pair.Key]=$pair.Value}
                Add 'filetransfer_route_transition_prepared' 10 $route @{decision_status='Prepared'}
                Add 'filetransfer_route_transition_draining' 11 $route @{decision_status='Draining'}
                Add 'filetransfer_fallback_v6_proof_bootstrap_peer_boundary_bound' 12 $command @{local_receipt_status='Delivered';local_frame_fingerprint='LOCAL';peer_frame_type='filetransfer.frontier_request.v6';peer_frame_fingerprint='PEER';proof_generation='2';proof_kind='fallback_v6_proof_bootstrap_peer_frame';pairing_status='Bound';authority_effect='peer_proof_binding_only'}
                $target=@{route='post_tuna_fallback_v6';protocol_version='6';live_route_epoch='4';runtime_generation='5';terminal_generation='0';leg_generation='5';route_commit_generation='0';route_publication_generation='5'}
                $committed=@{decision_status='Committed'}
                $applied=@{decision_status='Applied';decision_reason='filetransfer_route_transition_committed'}
                foreach($name in $target.Keys){$committed["target_$name"]=$target[$name];if($name -notin @('route','protocol_version')){$applied["resulting_$name"]=$target[$name]}}
                Add 'filetransfer_route_transition_committed' 13 $route $committed
                Add 'filetransfer_route_transition_disposition_applied' $(if($straddling){24}else{14}) $route $applied
                Add 'filetransfer_live_route_epoch_recovered' $(if($straddling){25}else{15}) $target @{transfer_id='transfer';direction='inbound';transition_generation='4';projection_source='coordinator_route_transition';route_publication_action_kind='RoutePublication';reason='filetransfer_route_transition_committed'}
                Add 'filetransfer_fallback_nkn_proof_unconfirmed' 16 @{} @{proof='nkn_control_ack_received';bulk_seen='1';control_seen='1';requires_control_proof='0';requires_v6_epoch_recovery='1'}
                Add 'filetransfer_fallback_nkn_proof_waiting_for_v6_epoch' 20 @{} @{proof='nkn_control_ack_received'}
                for($i=0;$i -lt 68;$i++){Add 'filetransfer_fallback_nkn_proof_waiting_for_v6_epoch' (30+$i) @{} @{proof='nkn_control_ack_received'}}
                Add 'file_transfer_inbound_terminal' 98 @{} @{transfer_id='transfer';state='Completed'}
                Add 'filetransfer_fallback_nkn_proof_waiting_for_v6_epoch' 99 @{} @{proof='nkn_control_ack_received'}
                Add 'filetransfer_fallback_nkn_proof_waiting_for_v6_epoch' 100 @{} @{proof='nkn_control_session_end_received'}
                return $script:records.ToArray()
            }
            $cases=@('exact','duplicate','reordered','empty','missing_issue','missing_execution','missing_application','missing_binding',
                'missing_prepared','missing_draining','missing_commit','missing_disposition','missing_publication','wrong_authority',
                'wrong_receipt','missing_fingerprint','wrong_generation','wrong_source','wrong_data_session','wrong_peer',
                'wrong_target','wrong_disposition','wrong_publication','ambiguous_observation','conflict','new_pending',
                'invalidation','replacement','terminal_before_commit','publication_mutation','episode_aba','aba','late_issue','late_commit')
            foreach($straddling in @($false,$true)){
            $order=if($straddling){'straddling'}else{'before_wait'}
            $orderCases=@($cases)
            if($straddling){$orderCases+=@('between_wait_pending','between_wait_invalidation','between_wait_replacement','between_wait_terminal','between_wait_publication_mutation','between_wait_aba','two_exact_episodes','independent_missing_issue')}
            foreach($case in $orderCases){
                $events=@(Fixture $straddling)
                $remove=@{missing_issue=7;missing_execution=8;missing_application=9;missing_binding=12;missing_prepared=10;missing_draining=11;missing_commit=13;missing_disposition=$(if($straddling){24}else{14});missing_publication=$(if($straddling){25}else{15})}
                if($remove.ContainsKey($case)){$events=@($events|Where-Object {[int]$_.log_writer_sequence -ne $remove[$case]})}
                switch($case){
                    'empty' {$events=@()}
                    'duplicate' {$events+= $events[8].Clone()}
                    'reordered' {$events=@($events|Sort-Object {[int]$_.log_writer_sequence} -Descending)}
                    'wrong_authority' {$events[8].authority_id='fta2-filetransfer_fallback_v6_proof_bootstrap-B'}
                    'wrong_receipt' {$events[8].frame_fingerprint='OTHER'}
                    'missing_fingerprint' {$events[7].frame_fingerprint='(none)';$events[8].frame_fingerprint='(none)'}
                    'wrong_generation' {$events[8].bootstrap_generation='3'}
                    'wrong_source' {$events[11].source_leg_generation='7'}
                    'wrong_data_session' {$events[11].source_data_session_generation='2'}
                    'wrong_peer' {$events[11].peer_frame_fingerprint='OTHER'}
                    'wrong_target' {$events[12].target_runtime_generation='6'}
                    'wrong_disposition' {$events[13].authority_id='fta2-filetransfer_route_transition-OTHER'}
                    'wrong_publication' {$events[14].route_publication_generation='6'}
                    'ambiguous_observation' {$copy=$events[5].Clone();$copy.log_writer_sequence='17';$events+=$copy}
                    'conflict' {$copy=$events[8].Clone();$copy.frame_fingerprint='OTHER';$events+=$copy}
                    'new_pending' {$copy=$events[1].Clone();$copy.log_writer_sequence='17';$events+=$copy}
                    'episode_aba' {$copy=$events[0].Clone();$copy.log_writer_sequence='17';$events+=$copy}
                    'invalidation' {$copy=$events[9].Clone();$copy.event='filetransfer_route_transition_invalidated';$copy.log_writer_sequence='17';$events+=$copy}
                    'replacement' {$copy=$events[9].Clone();$copy.event='filetransfer_data_session_availability_changed';$copy.log_writer_sequence='17';$events+=$copy}
                    'publication_mutation' {$copy=$events[14].Clone();$copy.event='filetransfer_route_hint_authorized_publication_accepted';$copy.log_writer_sequence='17';$copy.runtime_generation='6';$events+=$copy}
                    'terminal_before_commit' {
                        foreach($event in $events){if([int]$event.log_writer_sequence -ge 10 -and [int]$event.log_writer_sequence -le 16){$event.log_writer_sequence=[string]([int]$event.log_writer_sequence+1)}}
                        ($events|Where-Object {$_.event -eq 'file_transfer_inbound_terminal'}).log_writer_sequence='10'
                    }
                    'aba' {$copy=$events[9].Clone();$copy.log_writer_sequence='17';$copy.authority_id='fta2-filetransfer_route_transition-B';$events+=$copy;$copy=$events[9].Clone();$copy.log_writer_sequence='18';$events+=$copy}
                    'late_issue' {
                        for($i=6;$i -le 14;$i++){$events[$i].log_writer_sequence=[string]($i+15)}
                    }
                    'late_commit' {$events[12].log_writer_sequence='21';$events[13].log_writer_sequence='24';$events[14].log_writer_sequence='25'}
                    'between_wait_pending' {$copy=$events[1].Clone();$copy.log_writer_sequence='21';$events+=$copy}
                    'between_wait_invalidation' {$copy=$events[9].Clone();$copy.event='filetransfer_route_transition_invalidated';$copy.log_writer_sequence='21';$events+=$copy}
                    'between_wait_replacement' {$copy=$events[9].Clone();$copy.event='filetransfer_data_session_availability_changed';$copy.log_writer_sequence='21';$events+=$copy}
                    'between_wait_terminal' {($events|Where-Object {$_.event -eq 'file_transfer_inbound_terminal'}).log_writer_sequence='21'}
                    'between_wait_publication_mutation' {$copy=$events[14].Clone();$copy.event='filetransfer_route_hint_authorized_publication_accepted';$copy.log_writer_sequence='21';$copy.runtime_generation='6';$events+=$copy}
                    'between_wait_aba' {$copy=$events[9].Clone();$copy.log_writer_sequence='21';$copy.authority_id='fta2-filetransfer_route_transition-B';$events+=$copy;$copy=$events[9].Clone();$copy.log_writer_sequence='22';$events+=$copy}
                }
                if($case -in @('two_exact_episodes','independent_missing_issue')){
                    $other=@(Fixture $true)
                    foreach($event in $other){
                        $event.log_writer_id='writer-secondary';$event.session_id='session-secondary'
                        if($event.ContainsKey('fallback_epoch')){$event.fallback_epoch='3'}
                        foreach($name in @('transfer_id','trigger_transfer_id')){if($event.ContainsKey($name)){$event[$name]='transfer-secondary'}}
                    }
                    if($case -eq 'independent_missing_issue'){$other=@($other|Where-Object {$_.event -ne 'filetransfer_fallback_v6_proof_bootstrap_decided'})}
                    $events+=$other
                }
                $full=@($events|ForEach-Object {$f=$_; 'event='+$f.event+'; '+(@($f.Keys|Where-Object {$_ -ne 'event'}|Sort-Object|ForEach-Object {"$_=$($f[$_])"}) -join '; ')})
                $full+= 'event=unrelated_liveness; session_id=session; log_writer_id=writer; log_writer_sequence=1000'
                $compact=@($full|Where-Object {$catalog.Contains((Get-FileTransferAuthorityLogField -Line $_ -Name 'event'))})
                $a=Get-FileTransferFallbackV6BootstrapDirectionCorrelation -Lines $full
                $b=Get-FileTransferFallbackV6BootstrapDirectionCorrelation -Lines $compact
                $expected=$case -in @('exact','duplicate','reordered','empty','two_exact_episodes')
                if($a.UnresolvedCount -ne [int](-not $expected)){throw "$order/$case unexpected direction result $($a|ConvertTo-Json -Depth 2 -Compress)"}
                if(($a|ConvertTo-Json -Depth 5 -Compress) -cne ($b|ConvertTo-Json -Depth 5 -Compress)){throw "$order/$case compact parity"}
                if(-not $expected -and ($a.RawCount -ne 71 -or $a.CandidateRecords[0].WriterSequence -ne 20)){throw "$order/$case location/quartet changed"}
                if($expected -and ($a.RawCount -ne 0 -or $a.DistinctCount -ne 0 -or $a.RecoveredCount -ne 0)){throw "$order/$case callbacks counted as incidents"}
                if($case -eq 'independent_missing_issue' -and ($a.DistinctCount -ne 1 -or $a.RecoveredCount -ne 0 -or $a.CandidateRecords[0].Writer -cne 'writer-secondary')){throw "$order/$case completed episode borrowed by independent missing issue"}
                if($case -in @('exact','duplicate','reordered','empty','missing_issue','wrong_authority','wrong_data_session','new_pending','late_issue','late_commit','between_wait_pending','between_wait_invalidation','between_wait_terminal','between_wait_aba','two_exact_episodes','independent_missing_issue')){
                    $gui=Get-TunaGuiFallbackV6ProofBootstrapInvalidFrontierFailure -Lines $compact
                    $guiFull=Get-TunaGuiFallbackV6ProofBootstrapInvalidFrontierFailure -Lines $full
                    $guiFields=@('Unresolved','Reason','Variant','FirstSequence','RawCount','DistinctCount','RecoveredCount','UnresolvedCount')
                    if(($gui|Select-Object $guiFields|ConvertTo-Json -Compress) -cne ($guiFull|Select-Object $guiFields|ConvertTo-Json -Compress)){throw "$order/$case GUI full/compact boundary parity"}
                    if($gui.Unresolved -eq $expected){throw "$order/$case GUI mismatch $($gui|ConvertTo-Json -Depth 3 -Compress)"}
                    if(-not $expected -and $gui.FirstSequence -ne 20){throw "$order/$case GUI location"}
                    if($case -eq 'independent_missing_issue' -and ($gui.RawCount -ne 71 -or $gui.DistinctCount -ne 1 -or $gui.RecoveredCount -ne 0 -or $gui.UnresolvedCount -ne 1)){throw "$order/$case GUI quartet"}
                }
                "pass $order/$case"
            }
            }
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()]);
        Assert.True(result.ExitCode == 0, $"{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("pass before_wait/aba", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("pass straddling/exact", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("pass straddling/between_wait_aba", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("pass straddling/two_exact_episodes", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("pass straddling/independent_missing_issue", result.Stdout, StringComparison.Ordinal);
    }
}
