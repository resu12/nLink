namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "ScriptSurface")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferReconciliationProgressIndexTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public async Task ReconciliationProgressIndex_PreservesExactAndLegacyMatchingAndEventReferences()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $anchor = New-Event 10 'A' 2 'key'
            $current = New-Event 20 'A' 2 'key'
            $legacy = New-Event 20 'unused' 1 'Key'
            $legacyAnchor = New-Event 10 'unused' 1 'key'
            $events = [Collections.Generic.List[object]]::new()
            $events.Add($current); $events.Add($current)
            $events.Add((New-Event 5 'A' 2 'key'))
            $events.Add($legacy)
            $events.Add((New-Event 20 'a' 2 'key'))
            $events.Add((New-Event 20 'B' 2 'key'))
            $events.Add((ConvertFrom-FileTransferLogLine -Line ($current.RawLine + '; authority_id=B') -FilePath 'fixture.log' -LineNumber 21 -Sequence 20))
            foreach ($field in @('authority_id', 'predecessor_authority_id', 'session_id', 'transfer_id',
                    'repair_generation', 'repair_request_key', 'range_fingerprint', 'route', 'protocol_version',
                    'transport_epoch', 'bridge_recovery_generation', 'checkpoint_request_id',
                    'data_session_identity', 'data_session_generation')) {
                $events.Add((New-Event 30 'A' 2 'key' $field 'mutated'))
            }
            foreach ($value in @('', '(none)', 'NONE', 'null')) {
                $events.Add((New-Event 20 $value 2 'key'))
                $events.Add((New-Event 20 'unused' 1 $value))
            }
            # Restore A only as a distinct later reference: no cache may reuse B's result.
            $events.Add((New-Event 40 'A' 2 'key'))
            $all = $events.ToArray()
            $index = New-FallbackFrontierRepairProgressIndex -Events $all
            foreach ($subject in @($anchor, $legacyAnchor, (New-Event 10 'NONE' 2 'key'),
                    (New-Event 10 '(none)' 2 'key'), (New-Event 10 '' 2 'key'),
                    (New-Event 10 'unused' 1 '(none)'))) {
                Assert-MatchingReferences $subject $all $index
            }
            $candidates = @(Get-FallbackFrontierRepairProgressCandidates -Index $index -Anchor $anchor)
            if (-not [object]::ReferenceEquals($candidates[0], $current) -or
                -not [object]::ReferenceEquals($candidates[1], $current)) { throw 'duplicate references/order changed' }
            if ($index.IndexBuildVisitCount -ne $all.Count) { throw 'index revisited source events' }
            $empty = New-FallbackFrontierRepairProgressIndex -Events @()
            if ($empty.IndexBuildVisitCount -ne 0 -or @(Get-FallbackFrontierRepairProgressCandidates -Index $empty -Anchor $anchor).Count) { throw 'empty index changed' }
            Write-Output 'reconciliation-index-ok'
            """);
    }

    [Fact]
    public async Task ReconciliationProgressIndex_ProductionWidthBoundsCandidateVisitsWithoutGlobalState()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await RunHarnessAsync("""
            $progress = [Collections.Generic.List[object]]::new()
            for ($i = 0; $i -lt 726; $i++) { $progress.Add((New-Event (1000 + $i) "A$($i % 324)" 2 'key')) }
            $all = $progress.ToArray()
            $index = New-FallbackFrontierRepairProgressIndex -Events $all
            for ($i = 0; $i -lt 324; $i++) {
                $anchor = New-Event $i "A$i" 2 'key'
                $bucket = @(Get-FallbackFrontierRepairProgressCandidates -Index $index -Anchor $anchor)
                if ($bucket.Count -lt 2 -or $bucket.Count -gt 3) { throw 'wrong authority bucket' }
                foreach ($event in $bucket) {
                    if (-not (Test-FallbackFrontierRepairTupleMatch -Anchor $anchor -Candidate $event)) { throw 'exact tuple changed' }
                }
            }
            if ($index.IndexBuildVisitCount -ne 726 -or $index.BoundedLookupVisitCount -ne 726) { throw 'multiplicative candidate scan returned' }
            $noise = [Collections.Generic.List[object]]::new()
            for ($i = 0; $i -lt 50000; $i++) { $noise.Add([pscustomobject]@{ EventName = 'unrelated_liveness' }) }
            # Production constructs this index from its already selected progress bucket,
            # never from the full event stream; preserve that integration structurally.
            $assignments = @($reconciliation.Body.EndBlock.Statements | Where-Object {
                $_.Extent.Text -match '^\$frontierRepairProgressIndex\s*=' })
            if ($assignments.Count -ne 1 -or $assignments[0].Extent.Text -cne
                '$frontierRepairProgressIndex = New-FallbackFrontierRepairProgressIndex -Events $frontierRepairProgressObservedEvents') { throw 'index escaped progress scope' }
            $selection = @($reconciliation.Body.EndBlock.Statements | Where-Object {
                $_.Extent.Text -match '^\[object\[\]\]\$frontierRepairProgressObservedEvents\s*=' })
            if ($selection.Count -ne 1) { throw 'production progress selection changed' }
            $events = @($noise.ToArray()) + $all
            . ([scriptblock]::Create($selection[0].Extent.Text))
            if ($frontierRepairProgressObservedEvents.Count -ne 726) { throw 'unrelated events entered progress scope' }
            for ($i = 0; $i -lt $all.Count; $i++) {
                if (-not [object]::ReferenceEquals($frontierRepairProgressObservedEvents[$i], $all[$i])) { throw 'noise changed progress ordering' }
            }
            $again = New-FallbackFrontierRepairProgressIndex -Events $frontierRepairProgressObservedEvents
            if ($again.IndexBuildVisitCount -ne 726 -or $again.BoundedLookupVisitCount -ne 0 -or
                [object]::ReferenceEquals($again.Buckets, $index.Buckets)) { throw 'index state leaked across invocations' }
            Write-Output 'reconciliation-index-ok'
            """);
    }

    private static async Task RunHarnessAsync(string body)
    {
        var result = await RunPowerShellScriptTextAsync(Harness + Environment.NewLine + body, [FindRepoRoot()]);
        Assert.True(result.ExitCode == 0, $"STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        Assert.Equal("reconciliation-index-ok", result.Stdout.Trim());
    }

    private const string Harness = """
        param([string]$RepoRoot)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        . (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/FileTransferSoak/ArtifactWriters.ps1'), [ref]$tokens, [ref]$errors)
        if (@($errors).Count) { throw ($errors | Out-String) }
        $reconciliation = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Get-FileTransferFallbackTailReconciliationProof' }, $false))[0]
        foreach ($name in @('Get-FallbackFrontierRepairProgressKey', 'New-FallbackFrontierRepairProgressIndex',
                'Get-FallbackFrontierRepairProgressCandidates', 'Test-FallbackFrontierRepairScopeMatch',
                'Test-FallbackFrontierRepairTupleMatch')) {
            $functions = @($reconciliation.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name }, $true))
            if ($functions.Count -ne 1) { throw "missing exact helper $name" }
            . ([scriptblock]::Create($functions[0].Extent.Text))
        }
        function New-Event([long]$Sequence, [string]$Authority, [int]$Schema, [string]$Key, [string]$Mutation = '', [string]$Value = '') {
            $fields = [ordered]@{ event = 'filetransfer_fallback_frontier_repair_progress_observed'; authority_schema = $Schema
                authority_id = $Authority; predecessor_authority_id = '(none)'; transfer_id = 'transfer'; session_id = 'session'
                direction = 'inbound'; route = 'post_tuna_fallback_v6'; protocol_version = 6; live_route_epoch = 4
                runtime_generation = 5; terminal_generation = 0; leg_generation = 5; transport_epoch = 1
                bridge_recovery_generation = 1; bridge_decision_generation = 0; checkpoint_request_id = '(none)'
                route_publication_generation = 5; route_commit_generation = 4; repair_generation = 10; repair_request_key = $Key
                range_fingerprint = 'range'; frontier_chunk_index = 50; durable_received_highest_chunk_index = 100
                range_count = 1; requested_chunk_count = 1; source_kind = 'ReceiverRequest'
                primary_protocol_repair_request_id = 'request'; primary_request_correlation_id = 'correlation'
                priority = 'frontier_repair'; recovery_mode = 'post_tuna_fallback_v6'; target_transport = 'regular_nkn'
                authority_decision_reason = 'receiver_request'; data_session_identity = 'data'; data_session_generation = 1 }
            if ($Mutation) { $fields[$Mutation] = $Value }
            $line = ($fields.Keys | ForEach-Object { "$_=$($fields[$_])" }) -join '; '
            return ConvertFrom-FileTransferLogLine -Line $line -FilePath 'fixture.log' -LineNumber ([int]$Sequence + 1) -Sequence ([int]$Sequence)
        }
        function Assert-MatchingReferences($Anchor, [object[]]$Events, $Index) {
            $expected = @($Events | Where-Object { [long]$_.Sequence -gt [long]$Anchor.Sequence -and
                (Test-FallbackFrontierRepairTupleMatch -Anchor $Anchor -Candidate $_) })
            $actual = @(Get-FallbackFrontierRepairProgressCandidates -Index $Index -Anchor $Anchor | Where-Object {
                [long]$_.Sequence -gt [long]$Anchor.Sequence -and (Test-FallbackFrontierRepairTupleMatch -Anchor $Anchor -Candidate $_) })
            if ($expected.Count -ne $actual.Count) { throw 'candidate match count changed' }
            for ($i = 0; $i -lt $expected.Count; $i++) {
                if (-not [object]::ReferenceEquals($expected[$i], $actual[$i])) { throw 'candidate identity/order changed' }
            }
        }
        """;
}
