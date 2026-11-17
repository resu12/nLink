namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferStallGraceAdmissionTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    [Trait("OpsSuite", "GuiHarness")]
    public async Task StallGrace_CurrentPublicationWithoutPriorRepair_IsAdmittedByActualGuiAndRetained()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
            if($errors.Count){throw 'GUI parse failed'}
            $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $n.Name -ceq 'Get-TunaGuiFallbackFrontierStallGraceRecheckFailure'},$true))[0]
            Invoke-Expression $definition.Extent.Text
            $start=[DateTimeOffset]::Parse('2026-09-06T04:32:00Z')
            $scope=@{log_writer_id='writer';session_id='session';transfer_id='transfer';direction='inbound';
                route='post_tuna_fallback_v6';protocol_version='6';live_route_epoch='2';runtime_generation='3';
                terminal_generation='0';leg_generation='3';transport_epoch='1';bridge_recovery_generation='1';
                bridge_decision_generation='0';checkpoint_request_id='(none)';checkpoint_generation='1';
                route_publication_generation='3';route_commit_generation='0';data_session_identity='data';data_session_generation='1'}
            function Event([string]$Name,[long]$Ms,[hashtable]$Extra) {
                $f=$scope.Clone();foreach($key in $Extra.Keys){$f[$key]=[string]$Extra[$key]}
                $f.event=$Name;$f.log_writer_sequence=[string]($Ms+1);$f.log_writer_attempt_utc_ticks=[string]$start.AddMilliseconds($Ms).UtcTicks
                [pscustomobject]@{EventName=$Name;Fields=$f;Sequence=$Ms+1;LineNumber=$Ms+1;TimestampUtc=$start.AddMilliseconds($Ms);FilePath='fixture.log'}
            }
            function Fixture {
                Event 'filetransfer_route_hint_authorized_publication_accepted' 0 @{
                    route_publication_action_kind='RoutePublication';coordinator_decision_reason='accepted';
                    target_route='post_tuna_fallback_v6';target_protocol_version='6';target_transport='regular_nkn';
                    fallback_leg_generation='3';fallback_transport_epoch='1';fallback_bridge_recovery_generation='1'}
                Event 'filetransfer_v6_frontier_request_deferred' 1000 @{
                    authority_effect='none';frontier_chunk_index='1593';highest_received_chunk_index='1601';
                    stall_started_utc=$start.AddMilliseconds(1000).ToString('O');wake_deadline_utc=$start.AddMilliseconds(1750).ToString('O');
                    wake_generation='1';elapsed_ms='0';stall_grace_ms='750';reason='chunk_batch_committed'}
                Event 'filetransfer_v6_frontier_request_deferred' 1300 @{
                    authority_effect='none';frontier_chunk_index='1617';highest_received_chunk_index='1620';
                    stall_started_utc=$start.AddMilliseconds(1300).ToString('O');wake_deadline_utc=$start.AddMilliseconds(2050).ToString('O');
                    wake_generation='2';elapsed_ms='0';stall_grace_ms='750';reason='chunk_batch_committed'}
            }
            function Gui($events) {
                $lines=@($events | ForEach-Object {
                    $event=$_
                    (($event.Fields.Keys | Sort-Object | ForEach-Object { "$_=$($event.Fields[$_])" }) -join '; ') + "; gui_evidence_sequence=$($event.Sequence)"
                })
                Get-TunaGuiFallbackFrontierStallGraceRecheckFailure -Lines $lines -IncludeEpisodeAudit
            }
            $events=@(Fixture)
            $gui=Gui $events
            $first=@($gui.EpisodeAudit | Where-Object {$_.Event.Fields.log_writer_sequence -eq '1001'})
            if($first.Count -ne 1 -or -not $first[0].Recovered -or $first[0].Excluded){throw 'GUI omitted exact current-source natural recovery'}
            $retained=Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $events
            $first=@($retained.CandidateRecords | Where-Object {$_.Event.Fields.log_writer_sequence -eq '1001'})
            if($first.Count -ne 1 -or -not $first[0].Recovered){throw 'Retained current-source admission differs'}
            if($retained.RawCount -ne 2 -or $retained.DistinctCount -ne 2 -or
                $retained.RecoveredCount -ne 1 -or $retained.UnresolvedCount -ne 1){throw 'Two admitted holds lost their independent raw counts'}
            $cancelled=@(Fixture)+(Event 'file_transfer_inbound_terminal' 1900 @{state='Failed';error_code='fixture_end'})
            $retained=Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $cancelled
            if($retained.RawCount -ne 2 -or $retained.DistinctCount -ne 1 -or
                $retained.RecoveredCount -ne 1 -or $retained.UnresolvedCount -ne 0){throw 'Predeadline cancellation erased admitted raw evidence'}
            if((Gui $cancelled).Unresolved){throw 'GUI disagrees with admitted successor cancellation'}
            $supportOnly=@(Fixture);$supportOnly[2].Fields.Remove('log_writer_sequence')
            $admission=Get-FileTransferStallGracePublishedSourceAdmission -Index (New-FileTransferStallGracePublishedSourceIndex $supportOnly) -Candidate $supportOnly[2]
            if($null -eq $admission -or $admission.Admitted){throw 'Scope-complete successor without writer sequence became an admitted hold'}
            $retained=Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $supportOnly
            if($retained.RawCount -ne 1 -or $retained.DistinctCount -ne 1 -or
                $retained.RecoveredCount -ne 1 -or $retained.UnresolvedCount -ne 0){throw 'Supporting successor became raw authority evidence or lost natural recovery'}
            if((Gui $supportOnly).Unresolved){throw 'GUI disagrees with support-only successor recovery'}
            $missing=@(Fixture)[0..1];$gui=Gui $missing
            if(-not $gui.Unresolved -or $gui.FirstSequence -ne 1001 -or @($gui.EpisodeAudit).Count -ne 1){throw 'GUI silently excluded missing recovery'}
            $wrongScope=@(Fixture);$wrongScope[2].Fields.data_session_identity='other'
            $gui=Gui $wrongScope
            if(-not $gui.Unresolved -or $gui.FirstSequence -ne 1001){throw 'foreign data-session progress recovered the hold'}
            $aba=@(Fixture)
            $changed=Event 'filetransfer_route_selected' 100 @{route_publication_generation='4'}
            $restored=Event 'filetransfer_route_hint_authorized_publication_accepted' 200 $aba[0].Fields
            $restored.Sequence=201;$restored.Fields.log_writer_sequence='201'
            $aba+=@($changed,$restored)
            if((Get-FileTransferStallGracePublishedSourceAdmission -Index (New-FileTransferStallGracePublishedSourceIndex $aba) -Candidate $aba[1]).Admitted){throw 'ABA source restoration passed'}
            # Direction or diagnostic owner stamps alone cannot establish that a
            # same-writer publication belongs to an independently bound peer.
            foreach($stamped in @($false,$true)){
                $unbound=@(Fixture)
                $peer=Event 'filetransfer_route_selected' 100 @{direction='outbound';route='file_tuna_v4';protocol_version='4';route_publication_generation='4'}
                if($stamped){
                    $unbound[0].Fields.continuity_coordinator_id='fte1-111111111111111111111111'
                    $unbound[1].Fields.continuity_coordinator_id='fte1-111111111111111111111111'
                    $peer.Fields.continuity_coordinator_id='fte1-222222222222222222222222'
                }
                $unbound+=@($peer)
                if((Get-FileTransferStallGracePublishedSourceAdmission -Index (New-FileTransferStallGracePublishedSourceIndex $unbound) -Candidate $unbound[1]).Admitted){throw 'unbound opposite peer bypassed source invalidation'}
            }
            foreach($mutation in @(@(0,'route_publication_generation','4'),@(0,'coordinator_decision_reason','rejected'),
                    @(0,'target_transport','Tuna'),@(0,'fallback_transport_epoch','2'),@(1,'data_session_generation','0'),
                    @(1,'wake_generation','0'),@(1,'authority_effect','grant'),@(1,'wake_deadline_utc','2026-09-06T04:32:09Z'))){
                $bad=@(Fixture);$bad[$mutation[0]].Fields[$mutation[1]]=$mutation[2]
                $admission=Get-FileTransferStallGracePublishedSourceAdmission -Index (New-FileTransferStallGracePublishedSourceIndex $bad) -Candidate $bad[1]
                if($null -ne $admission -and $admission.Admitted){throw "bad admission passed: $mutation"}
            }
            $duplicate=@(Fixture);$duplicate+=@($duplicate[0],$duplicate[1])
            if(-not (Get-FileTransferStallGracePublishedSourceAdmission -Index (New-FileTransferStallGracePublishedSourceIndex $duplicate) -Candidate $duplicate[1]).Admitted){throw 'identical copy poisoned admission'}
            $conflict=@(Fixture);$copy=Event 'filetransfer_route_hint_authorized_publication_accepted' 0 @{target_transport='Tuna'};$conflict+=@($copy)
            if((Get-FileTransferStallGracePublishedSourceAdmission -Index (New-FileTransferStallGracePublishedSourceIndex $conflict) -Candidate $conflict[1]).Admitted){throw 'conflicting publication passed'}
            'current-publication-admission-ok'
            """;
        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()]);
        Assert.True(result.ExitCode == 0, $"Admission failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("current-publication-admission-ok", result.Stdout, StringComparison.Ordinal);
    }
}
