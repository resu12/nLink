namespace NLink.SmokeTests;

public sealed class FileTransferFrontierGraceIndexBuildTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(0)]
    [InlineData(50000)]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    public async Task FrontierGraceIndex_BuildPreservesEveryOrderedBucketAndOriginalReference(int noiseCount)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var script = """
            param([string]$RepoRoot, [int]$NoiseCount)
            $ErrorActionPreference='Stop'
            Set-StrictMode -Version Latest
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1')
            . (Join-Path $RepoRoot 'tools/FileTransferSoak/FrontierGraceCorrelationIndex.ps1')
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/FrontierGraceIndexReference.ps1')
            function Assert-References($Expected, $Actual, [string]$Label) {
                $left=$Expected; $right=$Actual
                if($left.Count -ne $right.Count){throw "count changed: $Label"}
                for($i=0;$i -lt $left.Count;$i++) {
                    if(-not [object]::ReferenceEquals($left[$i],$right[$i])){throw "reference/order changed: $Label/$i"}
                }
            }
            function Assert-Index($Expected, $Actual) {
                $names=@('EventsByTransfer','EventsByTransferAndName','EventsByTransferAndFrontier',
                    'EventsByTransferAndAuthority','EventsByWriterSequence','EventsByWriterIdentity','SourceBoundariesByTransfer')
                Assert-References $Expected.RelevantEvents $Actual.RelevantEvents 'RelevantEvents'
                if($Expected.IndexBuildVisitCount -ne $Actual.IndexBuildVisitCount -or
                    $Expected.BoundedLookupVisitCount -ne $Actual.BoundedLookupVisitCount){throw 'counter changed'}
                foreach($name in $names) {
                    $left=$Expected.$name; $right=$Actual.$name
                    if((@($left.Keys | Sort-Object) | ConvertTo-Json -Compress) -cne
                        (@($right.Keys | Sort-Object) | ConvertTo-Json -Compress)){throw "keys changed: $name"}
                    foreach($key in $left.Keys) {
                        if($left[$key].GetType() -ne $right[$key].GetType()){throw "bucket type changed: $name/$key"}
                        Assert-References $left[$key] $right[$key] "$name/$key"
                    }
                }
            }
            $events=[Collections.Generic.List[object]]::new()
            $scope=@{log_writer_id='WRITER';session_id='SESSION';transfer_id='TRANSFER';direction='inbound';
                route='post_tuna_fallback_v6';protocol_version='6';live_route_epoch='4';runtime_generation='5';
                terminal_generation='0';leg_generation='5';transport_epoch='3';bridge_recovery_generation='2';
                bridge_decision_generation='1';checkpoint_request_id='(none)';checkpoint_generation='1';
                route_publication_generation='5';route_commit_generation='4';data_session_identity='A';data_session_generation='1'}
            function Add-Event([string]$Name, [hashtable]$Extra, [long]$Sequence=-1) {
                $fields=$scope.Clone()
                $fields['log_writer_sequence']=[string]($events.Count+1)
                foreach($key in $Extra.Keys){$fields[$key]=$Extra[$key]}
                if($Sequence -lt 0){$Sequence=$events.Count+1}
                $event=[pscustomobject]@{EventName=$Name;Fields=$fields;Sequence=$Sequence;LineNumber=$Sequence;FilePath='fixture.log';TimestampUtc=$null}
                $events.Add($event)
                return $event
            }
            # Mixed case, repeated references, conflicting copies, missing scope,
            # strict writer sequences, numeric parsing, pause, replacement and ABA.
            $first=Add-Event 'FILETRANSFER_V6_FRONTIER_REQUEST_DEFERRED' @{frontier_chunk_index=' +12 ';next_chunk_index=12;authority_id='Authority-A'} 2
            $events.Add($first)
            [void](Add-Event 'unrelated_copy' @{log_writer_sequence='1';session_id=$null;authority_id='conflict'} 2)
            foreach($value in @('0','01','-1','+1',' 1 ','1.0','9223372036854775808','[redacted]',$null)) {
                [void](Add-Event 'filetransfer_v6_receiver_state_sent' @{log_writer_sequence=$value;frontier_chunk_index=$value;authority_id='(none)'})
            }
            foreach($value in @('A','B','A')) {
                [void](Add-Event 'filetransfer_data_session_recreated' @{data_session_identity=$value;checkpoint_generation='2';transfer_paused='1';next_chunk_index='9223372036854775807'})
            }
            [void](Add-Event 'filetransfer_live_route_epoch_terminal' @{terminal_generation='1';authority_id='none'})
            [void](Add-Event 'filetransfer_v6_receiver_state_sent' @{log_writer_id=' ';transfer_id='';frontier_chunk_index='0'})
            [void](Add-Event 'filetransfer_v6_receiver_state_sent' @{log_writer_id='OTHER';session_id='SESSION2';authority_id='Other-A';frontier_chunk_index='0'})
            foreach($fields in @($null,'not-a-dictionary',@{})) {
                $events.Add([pscustomobject]@{EventName='filetransfer_v6_receiver_state_sent';Fields=$fields;Sequence=0L;LineNumber=0L})
            }
            $events.Add([pscustomobject]@{EventName='filetransfer_v6_receiver_state_sent';Sequence=0L;LineNumber=0L})
            $events.Add($null)
            $caseSensitive=[hashtable]::new([StringComparer]::Ordinal)
            $caseSensitive['LOG_WRITER_ID']='not-an-alias';$caseSensitive['session_id']='s';$caseSensitive['transfer_id']='t'
            $events.Add([pscustomobject]@{EventName='filetransfer_v6_receiver_state_sent';Fields=$caseSensitive;Sequence=0L;LineNumber=0L})
            $names=@(Get-FileTransferFallbackFrontierGraceCorrelationEventNames)
            for($i=0;$i -lt 2308;$i++) {
                [void](Add-Event $names[$i%$names.Count] @{frontier_chunk_index=[string]($i%70);current_frontier_chunk_index=[string]($i%70);
                    previous_frontier_chunk_index=[string](($i%70)-1);next_chunk_index=[string]($i%70);authority_id="authority-$($i%80)"})
            }
            for($i=0;$i -lt $NoiseCount;$i++) {
                [void](Add-Event 'session_liveness_probe' @{authority_id="noise-$i"})
            }
            $inputEvents=$events.ToArray()
            $timer=[Diagnostics.Stopwatch]::StartNew()
            $expected=New-ReferenceFrontierGraceCorrelationIndex -Events $inputEvents
            $beforeMs=$timer.ElapsedMilliseconds
            $timer.Restart()
            $actual=New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $inputEvents
            $afterMs=$timer.ElapsedMilliseconds
            Assert-Index $expected $actual
            Assert-Index (New-ReferenceFrontierGraceCorrelationIndex -Events @()) (New-FileTransferFallbackFrontierGraceCorrelationIndex -Events @())
            Assert-Index (New-ReferenceFrontierGraceCorrelationIndex -Events @($first)) (New-FileTransferFallbackFrontierGraceCorrelationIndex -Events @($first))
            if($actual.EventsByWriterIdentity['WRITER|1'].Count -ne 3){throw 'unrelated/conflicting writer copy dropped'}
            "index-parity-ok events=$($inputEvents.Count) relevant=$($actual.IndexBuildVisitCount) before_ms=$beforeMs after_ms=$afterMs"
            """;

        var result = await RunPowerShellScriptTextAsync(script,
            [FindRepoRoot(), noiseCount.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        Assert.True(result.ExitCode == 0, $"Index build parity failed.{Environment.NewLine}{result.Stdout}{Environment.NewLine}{result.Stderr}");
        Assert.Contains("index-parity-ok", result.Stdout, StringComparison.Ordinal);
    }
}
