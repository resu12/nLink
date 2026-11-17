namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferFrontierGraceCorrelationIndexTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(false, 24)]
    [InlineData(true, 24)]
    [InlineData(false, 406)]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    public async Task FrontierGraceIndex_SupportOnlySuccessorsCannotOverrideTheirOwningPartition(bool conflictingRecheck, int obligationCount)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var script = """
            param([string]$RepoRoot, [string]$ConflictingRecheck, [int]$ObligationCount)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')
            $start=[DateTimeOffset]::Parse('2026-09-06T04:32:00Z')
            $events=[Collections.Generic.List[object]]::new()
            $scope=@{
                log_writer_id='writer'; session_id='session'; transfer_id='transfer';
                direction='inbound'; route='post_tuna_fallback_v6'; protocol_version='6';
                live_route_epoch='2'; runtime_generation='3'; terminal_generation='0'; leg_generation='3';
                route_publication_generation='3'; route_commit_generation='0'; transport_epoch='1';
                bridge_recovery_generation='1'; bridge_decision_generation='0'; checkpoint_request_id='(none)';
                checkpoint_generation='1'; data_session_identity='data-session'; data_session_generation='1'
            }
            function Add-Event([string]$Name,[long]$Milliseconds,[hashtable]$Extra) {
                $fields=$scope.Clone()
                foreach($key in $Extra.Keys){$fields[$key]=[string]$Extra[$key]}
                $fields['event']=$Name
                $time=$start.AddMilliseconds($Milliseconds)
                $fields['log_writer_attempt_utc_ticks']=[string]$time.UtcTicks
                $events.Add([pscustomobject]@{
                    EventName=$Name; Fields=$fields; Sequence=0L; LineNumber=0L;
                    TimestampUtc=$time; FilePath='fixture.log'
                })
            }
            Add-Event 'filetransfer_route_hint_authorized_publication_accepted' 0 @{
                target_route='post_tuna_fallback_v6'; target_protocol_version='6'; target_transport='regular_nkn';
                route_publication_action_kind='RoutePublication'; coordinator_decision_reason='accepted';
                fallback_leg_generation='3'; fallback_transport_epoch='1'; fallback_bridge_recovery_generation='1'
            }
            for($i=0;$i -lt $ObligationCount;$i++) {
                $time=1000+300*$i
                $frontier=40+3*$i
                $wake=@{
                    frontier_chunk_index=$frontier; highest_received_chunk_index=($frontier+5);
                    stall_started_utc=$start.AddMilliseconds($time).ToString('O'); wake_generation=($i+1);
                    wake_deadline_utc=$start.AddMilliseconds($time+750).ToString('O'); authority_effect='none'
                }
                $deferred=$wake.Clone()
                $deferred['elapsed_ms']='0'; $deferred['stall_grace_ms']='750'; $deferred['reason']='chunk_batch_committed'
                Add-Event 'filetransfer_v6_frontier_request_deferred' $time $deferred
                $recheck=$wake.Clone()
                $recheck['decision_status']='stale'; $recheck['decision_reason']='wake_generation_replaced'
                if($ConflictingRecheck -eq 'true' -and $i -eq 8){$recheck['decision_reason']='unexpected_stale_reason'}
                Add-Event 'filetransfer_v6_frontier_stall_grace_recheck_decided' ($time+751) $recheck
            }
            # Last obligation is cancelled before its deadline: raw evidence must
            # survive even when its owner emits no candidate record (width one).
            Add-Event 'file_transfer_inbound_terminal' (1000+300*($ObligationCount-1)+600) @{state='Failed'; error_code='fixture_end'}
            $sequence=0L
            [object[]]$ordered=@($events | Sort-Object TimestampUtc | ForEach-Object {
                $_.Sequence=++$sequence; $_.LineNumber=$sequence; $_.Fields['log_writer_sequence']=[string]$sequence; $_
            })
            function Normalize($Result) {
                [pscustomobject]@{
                    counts=@($Result.RawCount,$Result.DistinctCount,$Result.RecoveredCount,$Result.UnresolvedCount)
                    records=@($Result.CandidateRecords | ForEach-Object {
                        [pscustomobject]@{sequence=$_.Event.Sequence; recovered=$_.Recovered; reason=$_.Reason; variant=$_.Variant}
                    })
                } | ConvertTo-Json -Depth 8 -Compress
            }
            $expectedUnresolved=if($ConflictingRecheck -eq 'true'){1}else{0}
            $expected=$null
            $widths=@(8)
            if($ObligationCount -eq 24){
                $baseline=& $script:FileTransferFallbackFrontierStallGraceLegacyCorrelation -Events $ordered
                $expected=Normalize $baseline
                $widths=@(1,8,16)
            }
            $relevantCount=$ordered.Count
            if($ObligationCount -gt 24){
                $noise=@(for($i=0;$i -lt 50000;$i++){
                    [pscustomobject]@{EventName='session_liveness_probe';Sequence=(++$sequence);LineNumber=$sequence;Fields=@{}}
                })
                $ordered=@($ordered)+$noise
            }
            foreach($width in $widths) {
                $script:FileTransferFallbackFrontierGracePartitionWidth=$width
                $index=New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $ordered
                $timer=[Diagnostics.Stopwatch]::StartNew()
                $actual=Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $ordered -Index $index
                if($actual.RawCount -ne $ObligationCount -or $actual.DistinctCount -ne ($ObligationCount-1) -or
                    $actual.UnresolvedCount -ne $expectedUnresolved -or
                    ($null -ne $expected -and (Normalize $actual) -cne $expected)){
                    throw "partition width $width changed ownership: $(Normalize $actual)"
                }
                if($index.IndexBuildVisitCount -ne $relevantCount -or
                    $actual.BoundedLookupVisitCount -gt (2L*$relevantCount+128L*$ObligationCount) -or $timer.ElapsedMilliseconds -ge 90000L){
                    throw 'admitted frontier-grace complexity limit exceeded'
                }
            }
            # Empty ownership is not null ownership. Evidence-only slices may
            # close another obligation but cannot admit/count any themselves.
            $empty=[Collections.Generic.HashSet[object]]::new()
            foreach($reducer in @($script:FileTransferFallbackFrontierStallGraceLegacyCorrelation,
                    $script:FileTransferFallbackFrontierRecentProgressGraceLegacyCorrelation)) {
                $result=& $reducer -Events $ordered -OwnedCandidateEvents $empty
                if($result.RawCount -ne 0 -or $result.DistinctCount -ne 0){throw 'evidence-only slice admitted candidates'}
            }
            'ownership-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot(), conflictingRecheck.ToString().ToLowerInvariant(), obligationCount.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        Assert.True(result.ExitCode == 0, $"Partition ownership failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("ownership-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    public async Task FrontierGraceIndex_VisitsRelevantEvidenceOnceAndBoundsConsumerLookups()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var script = """
            param([string]$RepoRoot)
            $ErrorActionPreference='Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')

            function New-TestEvent([string]$name,[long]$sequence,[long]$frontier) {
                $fields=@{
                    log_writer_id='writer'; session_id='session'; transfer_id='transfer';
                    direction='outbound'; route='regular_nkn_v4_fast'; protocol_version='4';
                    frontier_chunk_index=[string]$frontier;
                    current_frontier_chunk_index=[string]$frontier;
                    previous_frontier_chunk_index=[string]([Math]::Max(0,$frontier-1));
                    next_chunk_index=[string]$frontier;
                    authority_id=('authority-{0}' -f ($frontier % 70));
                    elapsed_ms='0'; stall_grace_ms='750'; highest_received_chunk_index=[string]($frontier+10)
                }
                return [pscustomobject]@{
                    EventName=$name; Sequence=$sequence; LineNumber=$sequence;
                    TimestampUtc=[DateTimeOffset]::UtcNow.AddMilliseconds($sequence); Fields=$fields
                }
            }

            $relevant=New-Object System.Collections.Generic.List[object]
            $sequence=0L
            for($i=0;$i -lt 406;$i++){$relevant.Add((New-TestEvent 'filetransfer_v6_frontier_request_deferred' (++$sequence) (40+($i%70))))}
            for($i=0;$i -lt 657;$i++){$relevant.Add((New-TestEvent 'filetransfer_v6_receiver_state_deferred' (++$sequence) (40+($i%70))))}
            for($i=0;$i -lt 346;$i++){$relevant.Add((New-TestEvent 'filetransfer_fallback_frontier_repair_authority_granted' (++$sequence) (40+($i%70))))}
            for($i=0;$i -lt 346;$i++){$relevant.Add((New-TestEvent 'filetransfer_fallback_frontier_repair_contract_settled' (++$sequence) (40+($i%70))))}
            for($i=0;$i -lt 553;$i++){$relevant.Add((New-TestEvent 'filetransfer_fallback_frontier_repair_progress_observed' (++$sequence) (40+($i%70))))}

            $all=New-Object System.Collections.Generic.List[object]
            foreach($event in $relevant){$all.Add($event)}
            for($i=0;$i -lt 50000;$i++){$all.Add((New-TestEvent 'session_liveness_probe' (++$sequence) ($i%70)))}

            $index=New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $all.ToArray()
            $timer=[System.Diagnostics.Stopwatch]::StartNew()
            $stall=Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -Events $all.ToArray() -Index $index
            $stallMs=$timer.ElapsedMilliseconds
            $timer.Restart()
            $recent=Get-FileTransferFallbackFrontierRecentProgressGraceRecheckCorrelation -Events $all.ToArray() -Index $index
            $recentMs=$timer.ElapsedMilliseconds
            $maximum=2L*$relevant.Count+128L*(406L+70L)
            if([long]$index.IndexBuildVisitCount -ne [long]$relevant.Count -or
               [long]$stall.BoundedLookupVisitCount -gt $maximum -or
               [long]$recent.BoundedLookupVisitCount -gt $maximum -or
               $stallMs -ge 90000L -or $recentMs -ge 90000L -or
               $stall.Verdict -ne 'pass' -or $recent.Verdict -ne 'pass'){
                throw "frontier grace complexity contract failed relevant=$($relevant.Count) build=$($index.IndexBuildVisitCount) stall=$($stall.BoundedLookupVisitCount) recent=$($recent.BoundedLookupVisitCount) max=$maximum"
            }
            "ok relevant=$($relevant.Count) stall=$($stall.BoundedLookupVisitCount)/$stallMs recent=$($recent.BoundedLookupVisitCount)/$recentMs"
            """;

        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()]);
        Assert.True(
            result.ExitCode == 0,
            $"Bounded frontier-grace index failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("ok relevant=2308", result.Stdout, StringComparison.Ordinal);
    }
}
