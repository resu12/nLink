namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
public sealed class FileTransferRouteCycleAcceptanceTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("complete", 0, true)]
    [InlineData("missing_old_send", 1, true)]
    [InlineData("altered_old_envelope", 1, true)]
    [InlineData("missing_security_install", 1, false)]
    [InlineData("missing_security_retire", 1, false)]
    [InlineData("borrowed_publication", 1, true)]
    [InlineData("superseded_guard", 1, true)]
    [InlineData("regular_dispatched", 1, true)]
    [InlineData("missing_first_pair", 0, false)]
    [InlineData("missing_second_pair", 0, false)]
    [InlineData("sender_only_peer_visible", 0, false)]
    [InlineData("cross_activation_pair", 0, false)]
    [InlineData("wrong_protocol", 0, false)]
    [InlineData("incomplete_snapshot", 1, false)]
    [InlineData("duplicate_payload_field", 1, true)]
    [InlineData("later_receipt", 0, false)]
    [InlineData("authority_effect", 1, true)]
    [InlineData("overlapping_security", 1, false)]
    [InlineData("minimal_ignored", 0, true)]
    [InlineData("altered_ignored_envelope", 1, true)]
    [InlineData("missing_companion", 1, true)]
    public async Task RouteCycleAcceptance_SeparatesStaleSafetyFromEachActivationDelivery(
        string mode, int blocking, bool deliveryPass)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var root = FindRepoRoot();
        var script = $$$"""
            $ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
            . '{{{root.Replace("'", "''", StringComparison.Ordinal)}}}/tools/FileTransferOps/AnalyzerOrchestration.ps1'
            function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Extra authority evaluation.' }
            function Token($prefix,$length,$number) { $prefix+'-'+([long]$number).ToString('x'+$length) }
            $session=Token fte1 24 1; $transfer=Token fte1 24 2
            $sendWriter='p1r0000000000000001'; $receiveWriter='p2r0000000000000002'
            function Boundary($sender,$generation,$route,$sequence) {
                $owner=if($sender) {10} else {20}; $id=Token fte1 24 ($owner+$generation)
                $tuple=[pscustomobject]@{route=$route;protocolVersion=$(if($route -ceq 'post_tuna_fallback_v6'){6}else{4})
                    liveRouteEpoch=$generation-1;runtimeGeneration=$owner+$generation;terminalGeneration=0
                    legGeneration=$owner+$generation;routePublicationGeneration=$generation
                    routeCommitGeneration=$(if($route -ceq 'file_tuna_v4'){$generation}else{0})}
                $authority=Token fte1 24 (100+$owner+$generation)
                [pscustomobject]@{boundaryId=$id;coordinatorInstanceId=(Token fte1 24 $owner)
                    writer=$(if($sender){$sendWriter}else{$receiveWriter});writerSequence=$sequence
                    sessionId=$session;transferId=$transfer;direction=$(if($sender){'outbound'}else{'inbound'})
                    verdict='pass';afterStateGeneration=$generation;afterStateHash=(Token fte1 24 (200+$owner+$generation))
                    afterBoundary=$tuple;operations=@([pscustomobject]@{family='route_runtime';operationRole='route_publication'
                        afterState='Published';stateApplied=$true;receiptValidated=$true;receiptStatus='Accepted'
                        receiptIdentity=$authority;authorityId=$authority;receiptFingerprint=$id
                        operationGeneration=$generation;receiptGeneration=$generation;operationKind='RouteCommitPublication'
                        operationKey=('route_runtime|route_publication|'+$authority+'|'+$generation+'|RouteCommitPublication')
                        routeRuntime=[pscustomobject]@{role='route_publication';tokenFingerprint=$id;boundary=$tuple}})}
            }
            $s0=Boundary $true 1 regular_nkn_v4_fast 10
            $s1=Boundary $true 2 file_tuna_v4 30; $sf=Boundary $true 3 post_tuna_fallback_v6 50
            $s2=Boundary $true 4 file_tuna_v4 70; $se=Boundary $true 5 post_tuna_fallback_v6 100
            $r0=Boundary $false 1 regular_nkn_v4_fast 10
            $r1=Boundary $false 2 file_tuna_v4 30; $rf=Boundary $false 3 post_tuna_fallback_v6 50
            $r2=Boundary $false 4 file_tuna_v4 70; $re=Boundary $false 5 post_tuna_fallback_v6 100
            # Peer-local generations deliberately differ; they are never a join key.
            $r2.afterBoundary.liveRouteEpoch=33
            $view=[pscustomobject]@{Mode='SnapshotV1Authoritative';ArtifactEvidenceVerdict='pass';StreamComplete=$true
                AuthoritativeFamilies=@('route_runtime','route_transition');Boundaries=@($s0,$s1,$sf,$s2,$se,$r0,$r1,$rf,$r2,$re)}
            $records=[Collections.Generic.List[object]]::new()
            function Record($name,$b,$sequence,$envelope,$transport,$kind) {
                $owner=if($b.direction -ceq 'outbound'){1}else{2}
                $f=[ordered]@{event=$name;session_id='sess_[redacted]';transfer_id='[redacted]';direction=$b.direction
                    log_writer_id=$b.writer;log_writer_sequence=[string]$sequence;frame_type='filetransfer.chunk_batch.v4';lane='bulk'
                    actual_transport=$transport;delivery_transport=$transport
                    delivery_result=$(if($b.direction -ceq 'outbound'){'sent'}else{'received'})
                    raw_payload_bytes='16384';payload_bytes='16384';batch_chunk_count='1';frame_chunk_count='1'
                    chunk_index=('{0}-{0}' -f $envelope);start_chunk_index=[string]$envelope;last_end_chunk_exclusive=[string]($envelope+1)
                    publication_evidence_version='1';publication_evidence_coordinator_id=$b.coordinatorInstanceId
                    publication_evidence_boundary_id=$b.boundaryId;publication_evidence_session_id=$session
                    publication_evidence_transfer_id=$transfer;publication_evidence_token_fingerprint=$b.boundaryId
                    publication_evidence_operation_key=$b.operations[0].operationKey
                    publication_evidence_state_generation=[string]$b.afterStateGeneration
                    publication_evidence_observed_state_generation=[string]$b.afterStateGeneration;publication_use_kind=$kind
                    security_context_status='captured';security_transport_id=(Token ftst1 32 $owner)
                    security_installation_id=(Token ftsi1 32 $owner);security_installation_generation='1'
                    security_establishment_fingerprint=(Token ftest1 64 1);security_session_id=$session;security_transfer_id=$transfer
                    data_path_session_id=$session;data_path_transfer_id=$transfer;security_sequence=[string]$envelope
                    security_payload_fingerprint=(Token ftpay1 64 $envelope);data_path_envelope_fingerprint=(Token ftenv1 64 $envelope)}
                foreach($pair in @(@('route','route'),@('protocol_version','protocolVersion'),@('live_route_epoch','liveRouteEpoch'),
                    @('runtime_generation','runtimeGeneration'),@('terminal_generation','terminalGeneration'),@('leg_generation','legGeneration'),
                    @('route_publication_generation','routePublicationGeneration'),@('route_commit_generation','routeCommitGeneration'))) {
                    $f[$pair[0]]=[string]$b.afterBoundary.($pair[1])
                }
                $f.target_route=$f.route;$f.target_protocol_version=$f.protocol_version
                $f.target_transport=if($f.route -ceq 'file_tuna_v4'){'tuna'}else{'regular_nkn'}
                if($kind -ceq 'receive_guard') {$f.receive_disposition='rejected_before_dispatch';$f.authority_effect='none'}
                if($kind -ceq 'receive_dispatch') {$f.receive_disposition='dispatch_entered'}
                if($name -ceq 'filetransfer_tuna_data_path_mismatch') {$f.proof_direction='receive';$f.mismatch_reason='file_tuna_v4_resolved_to_regular_nkn'}
                if($name -ceq 'filetransfer_data_frame_ignored') {$f.reason='file_tuna_v4_payload_requires_tuna'}
                [pscustomobject]@{Fields=$f;Name=$name}
            }
            $old=Record filetransfer_data_path_send_result $s0 20 17 regular_nkn v4_physical_submission
            $guard=Record filetransfer_data_path_receive_result $r2 85 17 regular_nkn receive_guard
            $mismatch=Record filetransfer_tuna_data_path_mismatch $r2 86 17 regular_nkn receive_guard
            $ignored=Record filetransfer_data_frame_ignored $r2 87 17 regular_nkn receive_guard
            $firstSend=Record filetransfer_data_path_send_result $s1 35 40 tuna v4_physical_submission
            $firstReceive=Record filetransfer_data_path_receive_result $r1 36 40 tuna receive_dispatch
            $secondSend=Record filetransfer_data_path_send_result $s2 75 41 tuna v4_physical_submission
            $secondReceive=Record filetransfer_data_path_receive_result $r2 76 41 tuna receive_dispatch
            foreach($b in @($s0,$r0)) {
                foreach($stage in @('installed','retired')) {
                    $r=Record ('filetransfer_security_context_'+$stage) $b $(if($stage -ceq 'installed'){1}else{110}) 1 regular_nkn lifecycle
                    $r.Fields.security_context_status=if($stage -ceq 'installed'){'captured'}else{'retired'}
                    $records.Add($r)
                }
            }
            foreach($r in @($old,$firstSend,$firstReceive,$secondSend,$secondReceive,$guard,$mismatch,$ignored)) {$records.Add($r)}
            $mode='{{{mode}}}'
            switch($mode) {
                missing_old_send {[void]$records.Remove($old)}
                altered_old_envelope {$old.Fields.security_payload_fingerprint=Token ftpay1 64 999}
                missing_security_install {$records.RemoveAt(0)}
                missing_security_retire {$records.RemoveAt(1)}
                borrowed_publication {$guard.Fields.publication_evidence_coordinator_id=$s2.coordinatorInstanceId}
                superseded_guard {$re.writerSequence=82}
                regular_dispatched {foreach($r in @($guard,$mismatch,$ignored)){$r.Fields.receive_disposition='dispatch_entered'}}
                missing_first_pair {[void]$records.Remove($firstReceive)}
                missing_second_pair {[void]$records.Remove($secondReceive)}
                sender_only_peer_visible {$secondSend.Fields.event='filetransfer_tuna_data_path_peer_visible'}
                cross_activation_pair {$secondReceive.Fields.publication_evidence_boundary_id=$r1.boundaryId}
                wrong_protocol {$secondReceive.Fields.protocol_version='6'}
                incomplete_snapshot {$view.StreamComplete=$false}
                later_receipt {$secondReceive.Fields.log_writer_sequence='105'}
                authority_effect {$guard.Fields.authority_effect='progress_mutated'}
                overlapping_security {
                    $overlap=Record filetransfer_security_context_installed $s0 15 1 regular_nkn lifecycle
                    $overlap.Fields.security_installation_id=Token ftsi1 32 99
                    $records.Add($overlap)
                }
                minimal_ignored {foreach($name in @('start_chunk_index','last_end_chunk_exclusive','raw_payload_bytes','payload_bytes','batch_chunk_count','frame_chunk_count')) {$ignored.Fields.Remove($name)}}
                altered_ignored_envelope {$ignored.Fields.security_payload_fingerprint=Token ftpay1 64 999}
                missing_companion {[void]$records.Remove($ignored)}
            }
            $events=@(for($i=0;$i -lt $records.Count;$i++) {
                $record=$records[$i];$line=($record.Fields.Keys | ForEach-Object {$_+'='+$record.Fields[$_]}) -join '; '
                if($mode -ceq 'duplicate_payload_field' -and [object]::ReferenceEquals($record,$old)) {$line+='; security_sequence=17'}
                ConvertFrom-FileTransferLogLine -Line $line -FilePath 'fixture.log' -LineNumber ($i+1) -Sequence ($i+1)
            })
            $candidate=@($events | Where-Object EventName -CEQ filetransfer_data_path_receive_result | Where-Object {$_.Fields.actual_transport -ceq 'regular_nkn'})[0]
            $proof=[pscustomobject]@{DistinctCount=1;RecoveredCount=0;UnresolvedCount=1
                CandidateRecords=@([pscustomobject]@{Event=$candidate;Recovered=$false;Variant='legacy';RejectionDiagnostic=$null})}
            $before=$proof | ConvertTo-Json -Depth 9 -Compress
            $result=Get-FileTransferTunaRouteCycleAcceptance -Events $events -MismatchProof $proof -SnapshotEvaluation $view
            if($result.BlockingCandidateRecords.Count -ne {{{blocking}}} -or
                ($result.Report.delivery_verdict -ceq 'pass') -ne ${{{deliveryPass.ToString().ToLowerInvariant()}}} -or
                $result.Report.verdict -cne $(if({{{blocking}}} -eq 0 -and ${{{deliveryPass.ToString().ToLowerInvariant()}}}){'pass'}else{'fail'}) -or
                $result.Report.candidate_count -ne 1 -or $result.Report.candidates.Count -ne 1) {throw ('Approved contract result differs: '+($result.Report | ConvertTo-Json -Depth 8 -Compress))}
            if($mode -ceq 'regular_dispatched' -and $result.BlockingCandidateRecords[0].Class -cne 'file_tuna_data_path_mismatch') {throw 'Dispatched Regular traffic was not a protocol failure.'}
            if($mode -ceq 'missing_second_pair' -and ($result.DeliveryFailureRecords.Count -ne 1 -or
                $result.DeliveryFailureRecords[0].Class -cne 'filetransfer_tuna_activation_delivery_unproven')) {throw 'Missing receiver proof was not kept separate from a runtime defect.'}
            if(($proof | ConvertTo-Json -Depth 9 -Compress) -cne $before) {throw 'Legacy recovery proof mutated.'}
            # Fresh parse and detached completed view must produce the same result.
            $copy=@($events | ForEach-Object {ConvertFrom-FileTransferLogLine -Line $_.RawLine -FilePath $_.FilePath -LineNumber $_.LineNumber -Sequence $_.Sequence})
            $viewCopy=$view | ConvertTo-Json -Depth 20 | ConvertFrom-Json
            $other=Get-FileTransferTunaRouteCycleAcceptance -Events $copy -MismatchProof $proof -SnapshotEvaluation $viewCopy
            if(($result.Report | ConvertTo-Json -Depth 8 -Compress) -cne ($other.Report | ConvertTo-Json -Depth 8 -Compress)) {throw 'Full/compact contract parity changed.'}
            $paired=Get-FileTransferTunaRouteCycleAcceptance -Events $events -MismatchProof $proof -SnapshotEvaluation $view -ComparisonEvaluation $viewCopy
            if($paired.Report.full_compact_parity_verdict -cne 'pass') {throw 'Completed views failed parity.'}
            if($mode -ceq 'complete') {
                $viewCopy.StreamComplete=$false
                $different=Get-FileTransferTunaRouteCycleAcceptance -Events $events -MismatchProof $proof -SnapshotEvaluation $view -ComparisonEvaluation $viewCopy
                if($different.Report.verdict -cne 'fail' -or $different.Report.full_compact_parity_verdict -cne 'fail') {throw 'Parity disagreement was accepted.'}
            }
            'route-cycle-contract-ok'
            """;
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [], host);
            Assert.True(result.ExitCode == 0, $"{host}: {result.Stdout}{Environment.NewLine}{result.Stderr}");
            Assert.Contains("route-cycle-contract-ok", result.Stdout, StringComparison.Ordinal);
        }
    }
}
