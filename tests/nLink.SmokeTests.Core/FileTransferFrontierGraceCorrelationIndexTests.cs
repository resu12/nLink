namespace NLink.SmokeTests;

public sealed class FileTransferFrontierGraceCorrelationIndexTests : FileTransferOpsScriptsTestBase
{
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
