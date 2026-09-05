namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "RetainedAuthorityProof")]
[Trait("RetainedAuthorityProofSuite", "RouteSelectionReuse")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferRouteSelectionReuseTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task RouteSelectionReuse_SelectorPrecedenceAndSelectedObjectIdentityEqualBaseline()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $a = Selection 10 @{live_route_epoch='1'; route_publication_generation='9'}
            $b = Selection 20 @{live_route_epoch='2'; route_publication_generation='3'}
            $c = Selection 25 @{live_route_epoch='3'; route_publication_generation='3'}
            $d = Selection 30 @{route='post_tuna_fallback_v6'; protocol_version='6'; live_route_epoch='4'; route_publication_generation='4'}
            $e = Selection 35 @{route='post_tuna_fallback_v6'; protocol_version='6'; direction='inbound'; live_route_epoch='5'; route_publication_generation='5'}
            $queries = @(
                (Event 40 'filetransfer_data_path_send_result' @{live_route_epoch='1'; route_publication_generation='3'}),
                (Event 41 'filetransfer_data_path_send_result' @{live_route_epoch='99'; route_publication_generation='3'}),
                (Event 42 'filetransfer_data_path_send_result' @{route='post_tuna_fallback_v6'; protocol_version='6'}),
                (Event 43 'filetransfer_data_path_send_result' @{route='unknown'}),
                (Event 44 'filetransfer_data_path_send_result' @{route=''}),
                (Event 45 'filetransfer_data_path_send_result' @{route='unknown'; direction=''}),
                (Event 46 'filetransfer_data_path_send_result' @{direction='inbound'; live_route_epoch='1'}),
                (Event 47 'filetransfer_data_path_send_result' @{route='post_tuna_fallback_v6'; protocol_version='6'; route_publication_generation='1'}))
            $events = @($a, $b, $c, $d, $e) + $queries + $queries
            $null = Compare-Routes $events
            foreach ($pair in @(@(40,$a), @(41,$c), @(42,$d), @(43,$d), @(44,$d), @(45,$e), @(46,$e), @(47,$d))) {
                Assert-Selected $pair[0] $pair[1]
            }
            if ($script:ActualCalls -ne 8 -or $script:BaselineCalls -ne 16) { throw 'selector precedence queries were not reused exactly' }
            """);
    }

    [Fact]
    public async Task RouteSelectionReuse_SequenceTiesReorderingDuplicatesAndAbaAdvancePrefix()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $a = Selection 10 @{live_route_epoch='1'; route_publication_generation='1'}
            $b = Selection 10 @{live_route_epoch='2'; route_publication_generation='2'}
            $a.LineNumber = 9; $b.LineNumber = 2
            $tie = Event 10 'filetransfer_data_path_send_result'; $tie.LineNumber = 1
            $fallback = Selection 30 @{route='post_tuna_fallback_v6'; protocol_version='6'; live_route_epoch='3'}
            $again = Selection 50 @{live_route_epoch='4'}
            $before = Event 5 'filetransfer_data_path_send_result'
            $after = Event 60 'filetransfer_data_path_send_result'
            $events = @($after, $b, $before, $tie, $again, $fallback, $a, $before,
                (Event 20 'filetransfer_data_path_send_result'), (Event 40 'filetransfer_data_path_send_result'), $after, $a)
            $null = Compare-Routes $events
            if ($script:ActualCalls -ne 4 -or $script:BaselineCalls -ne 7) { throw 'eligible prefix identity ignored ties or ABA transitions' }
            Assert-Selected 60 $again
            [array]::Reverse($events)
            $null = Compare-Routes $events
            if ($script:ActualCalls -ne 4) { throw 'reordered input changed exact query count' }
            Assert-Selected 60 $again
            """);
    }

    [Fact]
    public async Task RouteSelectionReuse_NullResultsBlankTransfersAndResolvedDirectionsRemainDistinct()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $a = Selection 10
            $none = Selection 10 @{transfer_id=''}
            $queries = @(
                (Event 20 'filetransfer_data_path_send_result' @{transfer_id='missing'}),
                (Event 21 'filetransfer_data_path_send_result' @{direction='inbound'}),
                (Event 22 'filetransfer_data_path_send_result' @{direction='OUTBOUND'}),
                (Event 23 'filetransfer_data_path_send_result' @{direction='unknown'; role='SENDER'}),
                (Event 24 'filetransfer_data_path_send_result' @{direction='unknown'}),
                (Event 25 'filetransfer_data_path_send_result' @{direction=''}),
                (Event 26 'filetransfer_data_path_send_result' @{transfer_id=''}),
                (Event 27 'filetransfer_data_path_send_result' @{transfer_id='   '}),
                (Event 28 'filetransfer_v4_sender_started' @{direction='unknown'}),
                (Event 29 'file_transfer_outbound_terminal' @{direction='unknown'}))
            $proof = Compare-Routes (@($a,$none) + $queries + $queries)
            if ($script:ActualCalls -ne 5 -or $script:BaselineCalls -ne 20) { throw 'null or resolved-direction keys were not reused' }
            if (@($proof.Findings | Where-Object { $_ -like 'route-aware event has no selected route:*' }).Count -ne 4) { throw 'null selection diagnostics changed' }
            foreach ($sequence in @(22,23,24,25,28,29)) { Assert-Selected $sequence $a }
            foreach ($sequence in @(26,27)) { Assert-Selected $sequence $none }
            """);
    }

    [Fact]
    public async Task RouteSelectionReuse_ExactKeyFieldsParsedNumbersAndInvocationIsolation()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $a = Selection 1
            $events = @($a)
            $variants = @(
                @{}, @{transfer_id='TRANSFER'}, @{transfer_id='other'}, @{direction='inbound'},
                @{direction=''}, @{route='REGULAR_NKN_V4_FAST'}, @{route='unknown'},
                @{live_route_epoch='1'}, @{live_route_epoch='01'}, @{live_route_epoch='bad'},
                @{live_route_epoch='9223372036854775808'}, @{live_route_epoch='-1'},
                @{route_publication_generation='1'}, @{route_publication_generation='01'},
                @{route_publication_generation='bad'}, @{route_publication_generation='-1'},
                @{session_id='other'; leg_generation='999'; protocol_version='6'})
            $sequence = 10
            foreach ($fields in $variants) {
                $event = Event $sequence 'filetransfer_data_path_send_result' $fields
                $events += @($event,$event); $sequence++
            }
            $null = Compare-Routes $events
            if ($script:ActualCalls -ne 11 -or $script:BaselineCalls -ne 34) { throw 'selector key fields or parsed numeric defaults changed' }
            $a.Fields.protocol_version = '6'
            $null = Compare-Routes $events
            if ($script:ActualCalls -ne 11) { throw 'query results leaked across invocations' }
            $events[1].Fields.live_route_epoch = '2'
            $events[1].Fields.route_publication_generation = '2'
            $null = Compare-Routes $events
            $a.TransferId = 'other'
            $a.Fields.direction = 'inbound'
            $a.Fields.route = 'post_tuna_fallback_v6'
            $null = Compare-Routes $events
            $null = Compare-Routes @((Selection 1), (Event 2 'filetransfer_data_path_send_result'))
            if ($script:ActualCalls -ne 1) { throw 'independent invocation reused previous selected object' }
            """);
    }

    [Fact]
    public async Task RouteSelectionReuse_EarlyReturnFallbackAndDiagnosticCasesKeepQueryBoundary()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $selected = Selection 100 @{route='post_tuna_fallback_v6'; protocol_version='6'; live_route_epoch='2'; leg_generation='2'}
            $events = @($selected)
            foreach ($name in @('filetransfer_leg_frozen', 'filetransfer_clean_fallback_leg_frozen',
                'filetransfer_live_route_epoch_started', 'filetransfer_live_route_epoch_recovered', 'filetransfer_live_route_epoch_terminal')) {
                $events += Event 20 $name
            }
            $events += @(
                (Event 30 'filetransfer_leg_started' @{reason='new_transfer'}),
                (Event 31 'filetransfer_clean_fallback_leg_started' @{reason='header_switch_off'; route='post_tuna_fallback_v6'; protocol_version='6'}),
                (Event 32 'filetransfer_fallback_leg_recovery_generation_started' @{route='post_tuna_fallback_v6'; protocol_version='6'; live_route_epoch='2'; leg_generation='2'}),
                (Event 33 'filetransfer_fallback_leg_authority_started' @{route='post_tuna_fallback_v6'; protocol_version='6'; live_route_epoch='2'}),
                (Event 34 'filetransfer_control_plane_delivery_result' @{route='post_tuna_fallback_v6'; protocol_version='6'; kind='fallback_checkpoint_request'}),
                (Event 35 'filetransfer_route_hint_observed_without_publication_authority'),
                (Event 36 'filetransfer_data_path_send_result' @{gui_authoritative_projection='1'}),
                (Event 37 'filetransfer_control_plane_delivery_result' @{kind='receiver_state'; reason='chunk_batch_committed'}))
            $null = Compare-Routes $events
            if ($script:ActualCalls -ne 0 -or $script:BaselineCalls -ne 0) { throw 'early-return evidence entered selector cache' }
            $events += @(
                (Event 40 'filetransfer_data_path_send_result' @{route='post_tuna_fallback_v6'; protocol_version='6'}),
                (Event 41 'filetransfer_data_path_send_result' @{route='post_tuna_fallback_v6'; protocol_version='6'}),
                (Event 101 'filetransfer_fallback_leg_authority_started' @{route='post_tuna_fallback_v6'; protocol_version='6'; live_route_epoch='2'}),
                (Event 102 'filetransfer_fallback_leg_authority_started' @{route='post_tuna_fallback_v6'; protocol_version='6'; live_route_epoch='2'}))
            $null = Compare-Routes $events
            if ($script:ActualCalls -ne 2 -or $script:BaselineCalls -ne 4) { throw 'null-to-selected fallback prefix change was lost' }
            Assert-Selected 101 $selected
            """);
    }

    [Fact]
    public async Task RouteSelectionReuse_EmptyAndLegacyInputsKeepCompleteResult()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $proof = Compare-Routes @()
            if ($proof.Verdict -ne 'legacy') { throw 'empty result changed' }
            $unrelated = Event 1 'session_liveness_heartbeat'; $unrelated.Fields.Remove('route')
            $proof = Compare-Routes @($unrelated)
            if ($proof.Verdict -ne 'legacy' -or $script:ActualCalls -ne 0) { throw 'unrelated input entered selector' }
            $event = Event 2 'filetransfer_data_path_send_result'
            $proof = Compare-Routes @($event,$event)
            if ($proof.Verdict -ne 'fail' -or $script:ActualCalls -ne 1 -or $script:BaselineCalls -ne 2) { throw 'empty-selection null result was not cached' }
            $selected = Selection 1
            $null = Compare-Routes @($event,$selected,$event)
            Assert-Selected 2 $selected
            """);
    }

    [Fact]
    public async Task RouteSelectionReuse_ProductionWidthBoundsSelectorCallsAndPreservesEvidence()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $events = @((Selection 0)) + @(1..17000 | ForEach-Object { Event $_ 'filetransfer_data_path_send_result' })
            $failure = Event 17001 'filetransfer_data_path_send_result' @{protocol_version='6'}
            $events += @($failure) + @(20000..24000 | ForEach-Object {
                $event = Event $_ 'session_liveness_heartbeat'; $event.Fields.Remove('route'); $event
            })
            $proof = Compare-Routes $events
            if ($script:BaselineCalls -ne 17001 -or $script:ActualCalls -ne 1) { throw 'production-width selector bound changed' }
            if ($proof.Verdict -ne 'fail' -or $proof.Findings.Count -ne 1 -or $proof.EvidenceEvents.Count -ne 1 -or
                -not [object]::ReferenceEquals($proof.EvidenceEvents[0], $failure)) { throw 'production-width failure evidence changed' }
            """);
    }

    private static async Task RunHarnessAsync(string body)
    {
        var result = await RunPowerShellScriptTextAsync(Harness + Environment.NewLine + body + "\nWrite-Output 'route-reuse-ok'", [FindRepoRoot()]);
        Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        Assert.Equal("route-reuse-ok", result.Stdout.Trim());
    }

    private const string Harness = """
        param([string]$RepoRoot)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Windows PowerShell 5.1 required' }
        . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
        . (Join-Path $RepoRoot 'tools/FileTransferSoak/SoakSummaryExtraction.ps1')
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/TestData/RouteConsistencyBaseline.ps1')
        $script:Selector = (Get-Command Get-FileTransferSelectedRouteForEvent).ScriptBlock
        $script:Supersession = (Get-Command Test-FileTransferControlPlaneRouteEventSupersededByNewerLiveEpoch).ScriptBlock
        function Get-FileTransferSelectedRouteForEvent($Event, $RouteSelectedEvents) {
            $prefixCount = @($RouteSelectedEvents | Where-Object { $_.Sequence -le $Event.Sequence }).Count
            $key = ConvertTo-Json -InputObject @($prefixCount,
                (Get-FileTransferRouteEventKey -Event $Event),
                (Get-FileTransferRouteEventDirection -Event $Event),
                (Get-FileTransferEventField -Event $Event -Name 'route' -Default ''),
                (Get-FileTransferEventInt64Field -Event $Event -Name 'live_route_epoch' -Default 0),
                (Get-FileTransferEventInt64Field -Event $Event -Name 'route_publication_generation' -Default 0)) -Compress
            $selected = & $script:Selector -Event $Event -RouteSelectedEvents $RouteSelectedEvents
            $script:Queries.Add([pscustomobject]@{Key=$key; Event=$Event; Selected=$selected}) | Out-Null
            return $selected
        }
        function Test-FileTransferControlPlaneRouteEventSupersededByNewerLiveEpoch($Event, $Selected) {
            $script:Observed.Add([pscustomobject]@{Event=$Event; Selected=$Selected}) | Out-Null
            & $script:Supersession -Event $Event -Selected $Selected
        }
        function Event([long]$Sequence, [string]$Name, [hashtable]$Overrides=@{}) {
            $fields = @{session_id='session'; transfer_id='transfer'; direction='outbound'; route='regular_nkn_v4_fast'; protocol_version='4'}
            foreach ($key in $Overrides.Keys) { $fields[$key] = $Overrides[$key] }
            [pscustomobject]@{
                Sequence=$Sequence; LineNumber=$Sequence; EventName=$Name; Fields=$fields; TransferId=$fields.transfer_id;
                FileName='fixture.log'; FilePath='fixture.log'; Message=$Name; RawLine=$Name;
                TimestampUtc=[datetime]'2026-01-01T00:00:00Z'; TimestampText='2026-01-01T00:00:00Z'; Level='Info'; Source='fixture'
            }
        }
        function Selection([long]$Sequence, [hashtable]$Overrides=@{}) { Event $Sequence 'filetransfer_route_selected' $Overrides }
        function Assert-Selected([long]$Sequence, $Selected) {
            $matches = @($script:Observed | Where-Object { $_.Event.Sequence -eq $Sequence })
            if ($matches.Count -eq 0) { throw "selection was not observed for sequence $Sequence" }
            foreach ($match in $matches) {
                if (-not [object]::ReferenceEquals($match.Selected, $Selected)) { throw "wrong selected object for sequence $Sequence" }
            }
        }
        function Compare-Routes([AllowEmptyCollection()][object[]]$Events) {
            $before = ConvertTo-Json -InputObject $Events -Depth 12 -Compress
            Reset-FileTransferRetainedEventIndex
            $script:Queries = New-Object System.Collections.Generic.List[object]
            $script:Observed = New-Object System.Collections.Generic.List[object]
            $expected = Invoke-RouteConsistencyBaseline -TransferEvents $Events
            $baselineQueries = @($script:Queries.ToArray())
            $baselineObserved = @($script:Observed.ToArray())
            $script:BaselineCalls = $baselineQueries.Count
            Reset-FileTransferRetainedEventIndex
            $script:Queries = New-Object System.Collections.Generic.List[object]
            $script:Observed = New-Object System.Collections.Generic.List[object]
            $actual = Get-FileTransferRouteConsistency -TransferEvents $Events
            $script:ActualCalls = $script:Queries.Count
            if ((ConvertTo-Json -InputObject $expected -Depth 12 -Compress) -cne (ConvertTo-Json -InputObject $actual -Depth 12 -Compress)) { throw 'complete route consistency result changed' }
            foreach ($property in @('RouteSelectedEvents', 'RouteAwareEvents', 'EvidenceEvents')) {
                for ($i=0; $i -lt $expected.$property.Count; $i++) {
                    if (-not [object]::ReferenceEquals($expected.$property[$i], $actual.$property[$i])) { throw "event reference/order changed: $property/$i" }
                }
            }
            if ($script:Observed.Count -ne $baselineObserved.Count) { throw 'selected-event count changed' }
            for ($i=0; $i -lt $baselineObserved.Count; $i++) {
                if (-not [object]::ReferenceEquals($baselineObserved[$i].Event, $script:Observed[$i].Event) -or
                    -not [object]::ReferenceEquals($baselineObserved[$i].Selected, $script:Observed[$i].Selected)) { throw "selected event identity/order changed: $i" }
            }
            if ((ConvertTo-Json -InputObject $Events -Depth 12 -Compress) -cne $before) { throw 'input mutated' }
            $distinct = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([System.StringComparer]::Ordinal)
            foreach ($query in $baselineQueries) {
                if (-not $distinct.ContainsKey($query.Key)) { $distinct.Add($query.Key, $query) }
            }
            if ($script:ActualCalls -ne $distinct.Count -or $script:ActualCalls -gt $script:BaselineCalls) {
                throw "selector calls outside exact-query bound: $script:ActualCalls != $($distinct.Count) (baseline $script:BaselineCalls)"
            }
            foreach ($query in $script:Queries) {
                if (-not $distinct.ContainsKey($query.Key) -or
                    -not [object]::ReferenceEquals($distinct[$query.Key].Selected, $query.Selected)) { throw 'cache miss did not use original selector result' }
                $distinct.Remove($query.Key) | Out-Null
            }
            return $actual
        }
        """;
}
