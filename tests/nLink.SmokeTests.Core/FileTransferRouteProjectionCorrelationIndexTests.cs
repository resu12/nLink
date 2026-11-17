namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferRouteProjectionCorrelationIndexTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    [Trait("RetainedAuthorityProofSuite", "FileTransferRouteTransition")]
    public async Task RouteProjectionIndex_VisitsRelevantEvidenceOnceAndBoundsLookupsAtProductionWidth()
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
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/RouteProjectionCorrelationIndex.ps1')

            function New-ProjectionEvent(
                [string]$name,
                [long]$sequence,
                [int]$epoch,
                [string]$direction,
                [string]$route,
                [hashtable]$overrides=@{}) {
                $fields=@{
                    session_id='session'; transfer_id='transfer'; direction=$direction;
                    route=$route; protocol_version=if($route -eq 'post_tuna_fallback_v6'){'6'}else{'4'};
                    runtime_generation=[string](10+$epoch); terminal_generation='0';
                    leg_generation=[string](20+$epoch);
                    route_publication_generation=[string](30+$epoch);
                    route_commit_generation=[string]$epoch;
                    log_writer_id='writer'; log_writer_sequence=[string]$sequence
                }
                foreach($key in $overrides.Keys){$fields[$key]=$overrides[$key]}
                return [pscustomobject]@{
                    EventName=$name; Sequence=$sequence; LineNumber=$sequence; Fields=$fields
                }
            }

            $relevant=New-Object System.Collections.Generic.List[object]
            $all=New-Object System.Collections.Generic.List[object]
            $sequence=0L
            $projectionNames=@(
                'filetransfer_route_transitioned',
                'filetransfer_route_selected',
                'filetransfer_live_route_epoch_started',
                'filetransfer_live_route_epoch_recovered')
            for($epoch=1;$epoch -le 8;$epoch++){
                $direction=if(($epoch%2)-eq 0){'inbound'}else{'outbound'}
                $route=if(($epoch%2)-eq 0){'post_tuna_fallback_v6'}else{'file_tuna_v4'}
                $authority="authority-$epoch"
                $relevant.Add((New-ProjectionEvent 'filetransfer_route_transition_committed' (++$sequence) $epoch $direction $route @{
                    authority_id=$authority; transition_generation=[string]$epoch; decision_status='Committed'
                }))
                $relevant.Add((New-ProjectionEvent 'filetransfer_route_hint_authorized_publication_accepted' (++$sequence) $epoch $direction $route))
                if($route -eq 'post_tuna_fallback_v6'){
                    $relevant.Add((New-ProjectionEvent 'filetransfer_route_transition_retained_frame_applied' (++$sequence) $epoch $direction $route @{
                        authority_id=$authority; transition_generation=[string]$epoch
                    }))
                }
                $relevant.Add((New-ProjectionEvent 'filetransfer_route_transition_disposition_applied' (++$sequence) $epoch $direction $route @{
                    authority_id=$authority; transition_generation=[string]$epoch;
                    disposition='committed'; decision_status='Applied'
                }))
                foreach($projectionName in $projectionNames){
                    $relevant.Add((New-ProjectionEvent $projectionName (++$sequence) $epoch $direction $route))
                }
                for($payload=0;$payload -lt 3750;$payload++){
                    $relevant.Add((New-ProjectionEvent 'filetransfer_data_path_send_result' (++$sequence) $epoch $direction $route @{
                        delivery_result='sent'
                    }))
                }
            }
            foreach($event in $relevant){$all.Add($event)}
            for($unrelated=0;$unrelated -lt 50000;$unrelated++){
                $all.Add([pscustomobject]@{
                    EventName='session_liveness_probe'; Sequence=(++$sequence);
                    LineNumber=$sequence; Fields=@{log_writer_id='writer'}
                })
            }

            $timer=[System.Diagnostics.Stopwatch]::StartNew()
            $index=New-FileTransferRouteTransitionLiveEpochProjectionIndex -Events $all.ToArray()
            $indexMs=$timer.ElapsedMilliseconds
            $timer.Restart()
            $proof=Get-FileTransferRouteTransitionLiveEpochProjectionCorrelation `
                -Events $all.ToArray() `
                -CorrelationIndex $index
            $correlationMs=$timer.ElapsedMilliseconds
            $maximum=128L*8L+64L
            if([long]$index.IndexBuildVisitCount -ne [long]$relevant.Count -or
               [long]$proof.IndexBuildVisitCount -ne [long]$relevant.Count -or
               [long]$proof.BoundedLookupVisitCount -gt $maximum -or
               $proof.Verdict -ne 'pass' -or
               [int]$proof.RawCount -ne 0 -or
               @($proof.AuthoritativeRecords).Count -ne 8 -or
               $indexMs -ge 90000L -or
               $correlationMs -ge 30000L){
                throw "route projection complexity contract failed relevant=$($relevant.Count) build=$($index.IndexBuildVisitCount) lookup=$($proof.BoundedLookupVisitCount) max=$maximum indexMs=$indexMs correlationMs=$correlationMs verdict=$($proof.Verdict) authoritative=$(@($proof.AuthoritativeRecords).Count)"
            }
            "ok relevant=$($relevant.Count) lookup=$($proof.BoundedLookupVisitCount) indexMs=$indexMs correlationMs=$correlationMs"
            """;

        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()]);
        Assert.True(
            result.ExitCode == 0,
            $"Bounded route-projection index failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("ok relevant=30060", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "FileTransferRouteTransition")]
    public async Task RouteProjectionIndex_PreservesMissingProjectionBoundaryAndEmptyLookupBehavior()
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
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/RouteProjectionCorrelationIndex.ps1')

            function New-Event([string]$name,[long]$sequence,[hashtable]$overrides=@{}){
                $fields=@{
                    session_id='session'; transfer_id='transfer'; direction='outbound';
                    route='file_tuna_v4'; protocol_version='4'; runtime_generation='2';
                    terminal_generation='0'; leg_generation='2';
                    route_publication_generation='2'; route_commit_generation='1';
                    log_writer_id='writer'; log_writer_sequence=[string]$sequence
                }
                foreach($key in $overrides.Keys){$fields[$key]=$overrides[$key]}
                [pscustomobject]@{EventName=$name;Sequence=$sequence;LineNumber=$sequence;Fields=$fields}
            }
            function New-Fixture([bool]$omitRecovered){
                $events=New-Object System.Collections.Generic.List[object]
                $events.Add((New-Event 'filetransfer_route_transition_committed' 1 @{
                    authority_id='authority';transition_generation='1';decision_status='Committed'}))
                $events.Add((New-Event 'filetransfer_route_hint_authorized_publication_accepted' 2))
                $events.Add((New-Event 'filetransfer_route_transition_disposition_applied' 3 @{
                    authority_id='authority';transition_generation='1';disposition='committed';decision_status='Applied'}))
                $events.Add((New-Event 'filetransfer_route_transitioned' 4))
                $events.Add((New-Event 'filetransfer_route_selected' 5))
                $events.Add((New-Event 'filetransfer_live_route_epoch_started' 6))
                if(-not $omitRecovered){$events.Add((New-Event 'filetransfer_live_route_epoch_recovered' 7))}
                $events.Add((New-Event 'filetransfer_data_path_send_result' 8 @{delivery_result='sent'}))
                return $events.ToArray()
            }

            $complete=New-Fixture $false
            $completeIndex=New-FileTransferRouteTransitionLiveEpochProjectionIndex -Events $complete
            $completeProof=Get-FileTransferRouteTransitionLiveEpochProjectionCorrelation -Events $complete -CorrelationIndex $completeIndex
            $missing=New-Fixture $true
            $missingIndex=New-FileTransferRouteTransitionLiveEpochProjectionIndex -Events $missing
            $missingProof=Get-FileTransferRouteTransitionLiveEpochProjectionCorrelation -Events $missing -CorrelationIndex $missingIndex
            $emptyProof=Get-FileTransferRouteTransitionLiveEpochProjectionCorrelation -Events @()
            if($completeProof.Verdict -ne 'pass' -or
               @($completeProof.AuthoritativeRecords).Count -ne 1 -or
               $missingProof.RawCount -ne 1 -or $missingProof.DistinctCount -ne 1 -or
               $missingProof.RecoveredCount -ne 0 -or $missingProof.UnresolvedCount -ne 1 -or
               $missingProof.CandidateRecords[0].Reason -ne 'committed_route_transition_not_projected_to_live_route_epoch_evidence' -or
               $emptyProof.Verdict -ne 'pass' -or $emptyProof.IndexBuildVisitCount -ne 0){
                throw "route projection semantic parity failed complete=$($completeProof.Verdict) missing=$($missingProof.RawCount)/$($missingProof.DistinctCount)/$($missingProof.RecoveredCount)/$($missingProof.UnresolvedCount) empty=$($emptyProof.Verdict)"
            }
            'semantic-parity-ok'
            """;

        var result = await RunPowerShellScriptTextAsync(script, [FindRepoRoot()]);
        Assert.True(
            result.ExitCode == 0,
            $"Route-projection semantic parity failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("semantic-parity-ok", result.Stdout, StringComparison.Ordinal);
    }
}
