namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "RetainedV4Transport")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferCompactRepairReportTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData("clean")]
    [InlineData("selected")]
    [InlineData("latest")]
    [InlineData("all")]
    [InlineData("requested_with_all")]
    public async Task CompactRepairReport_MatchesFullScopeAndRepairOnlyContamination(string scope)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string transfer = "measured-repair";
        var lines = new List<string>(BuildRouteAwareCompletedFixture(
            "other-repair", "regular_nkn_v4_fast", 4, "regular_nkn_v4_fast",
            "regular_nkn_v4_fast", "filetransfer_v4_sender_started"))
        {
            LogLine("event=filetransfer_v4_repair_requested; transfer_id=other-repair"),
            LogLine("event=filetransfer_v4_repair_scheduled; transfer_id=other-repair"),
            LogLine("event=filetransfer_v4_repair_sent; transfer_id=other-repair; repair_delivery_mode=bulk_only")
        };
        var measured = BuildRouteAwareCompletedFixture(
            transfer, "regular_nkn_v4_fast", 4, "regular_nkn_v4_fast",
            "regular_nkn_v4_fast", "filetransfer_v4_sender_started");
        lines.AddRange(measured.Take(measured.Length - 2));
        if (scope != "clean")
        {
            lines.AddRange(new[]
            {
                LogLine($"event=filetransfer_v4_repair_requested; transfer_id={transfer}"),
                LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transfer}"),
                LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transfer}"),
                LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transfer}; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=credit_stall"),
                LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transfer}; repair_delivery_mode=bulk_only; repair_delivery_escalation_reason=frontier_not_advanced"),
                LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transfer}")
            });
        }
        // Neither unscoped observations nor rejected authority are repair delivery.
        lines.Add(LogLine("event=filetransfer_v4_repair_requested"));
        lines.Add(LogLine($"event=filetransfer_v4_repair_authority_rejected; transfer_id={transfer}; repair_delivery_mode=control_bulk_escalated"));
        lines.AddRange(measured.Skip(measured.Length - 2));
        var expected = scope == "clean" ? new[] { 0, 0, 0, 0, 0, 0 }
            : scope == "all" ? new[] { 2, 3, 4, 1, 1, 1 } : new[] { 1, 2, 3, 1, 1, 1 };
        string[] keys =
        [
            "v4_repair_requested_count", "v4_missing_range_repair_scheduled_count",
            "v4_missing_range_repair_sent_count", "v4_repair_delivery_control_bulk_escalated_count",
            "v4_repair_delivery_credit_stall_escalated_count", "v4_repair_delivery_frontier_not_advanced_escalated_count"
        ];
        var roots = new List<string>();
        try
        {
            Dictionary<string, string>? compactReport = null;
            foreach (var compact in new[] { true, false })
            {
                var root = Path.Combine(Path.GetTempPath(), "nlink-compact-repair", Guid.NewGuid().ToString("N"));
                roots.Add(root);
                Directory.CreateDirectory(root);
                var logPath = Path.Combine(root, "nlink.log");
                var artifactDir = Path.Combine(root, "artifacts");
                await File.WriteAllLinesAsync(logPath, lines);
                const string analyze = """
                    param([string]$RepoRoot, [string]$LogPath, [string]$ArtifactDir, [string]$Scope, [string]$Compact)
                    $ErrorActionPreference = 'Stop'
                    $env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT = $Compact
                    . (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
                    $arguments = @{RepoRoot=$RepoRoot; LogPath=$LogPath; ArtifactDir=$ArtifactDir; LiveRouteProofMode='None'}
                    if ($Scope -notin @('latest', 'all')) { $arguments.TransferId = 'measured-repair' }
                    if ($Scope -in @('all', 'requested_with_all')) { $arguments.AllTransfers = $true }
                    Invoke-FileTransferRetainedAnalysis @arguments | Out-Null
                    """;
                var analysis = await RunPowerShellScriptTextAsync(analyze,
                    [FindRepoRoot(), logPath, artifactDir, scope, compact ? "1" : "0"]);
                var result = new AnalyzeFixtureResult(root, artifactDir, analysis);
                Assert.True(result.Script.ExitCode == 0, result.Script.Stdout + Environment.NewLine + result.Script.Stderr);
                Assert.True(File.Exists(Path.Combine(result.ArtifactDir, "repair-reorder-summary.txt")), $"Missing repair report: compact={compact}");
                var report = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
                for (var i = 0; i < keys.Length; i++)
                {
                    Assert.Equal(expected[i].ToString(), report[keys[i]]);
                    if (compactReport is not null) { Assert.Equal(compactReport[keys[i]], report[keys[i]]); }
                }
                if (!compact) { continue; }
                compactReport = report;
                // Exercise the real measurement consumer, not transfer admission.
                const string script = """
                    param([string]$RepoRoot, [string]$ArtifactDir, [string]$Expected)
                    $ErrorActionPreference = 'Stop'
                    Set-StrictMode -Version Latest
                    $errors = $null
                    $ast = [Management.Automation.Language.Parser]::ParseFile(
                        (Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'), [ref]$null, [ref]$errors)
                    if ($errors.Count) { throw 'wrapper_parse_failed' }
                    foreach ($definition in $ast.EndBlock.Statements | Where-Object {
                        $_ -is [Management.Automation.Language.FunctionDefinitionAst]
                    }) { . ([scriptblock]::Create($definition.Extent.Text)) }
                    $script:RouteAcceptanceRetainedMarkerIndexCache = @{}
                    $result = New-RouteAcceptanceRunResult -Name active-tuna-v4-64mb -ArtifactDir $ArtifactDir -ExpectedRoute file_tuna_v4 -ExpectedProtocol 4
                    $result.finalRoute = 'file_tuna_v4'
                    Set-Phase4MeasurementContamination -Scenario ([pscustomobject]@{Name='active-tuna-v4-64mb'; Kind='tuna'}) -Result $result
                    if ($result.measurementContaminated -ne ($Expected -eq '1')) { throw 'wrong_repair_contamination' }
                    $reasons = @($result.measurementContaminationReasons)
                    if ($Expected -eq '1' -and ($reasons.Count -ne 1 -or $reasons[0] -notlike 'active_tuna_v4_repair_pressure:*')) {
                        throw 'repair_only_reason_missing'
                    }
                    if ($Expected -eq '0' -and $reasons.Count -ne 0) { throw 'clean_scope_contaminated' }
                    'repair-measurement-ok'
                    """;
                var consumer = await RunPowerShellScriptTextAsync(script,
                    [FindRepoRoot(), result.ArtifactDir, scope == "clean" ? "0" : "1"]);
                Assert.True(consumer.ExitCode == 0, consumer.Stdout + Environment.NewLine + consumer.Stderr);
                Assert.Contains("repair-measurement-ok", consumer.Stdout, StringComparison.Ordinal);
            }
        }
        finally
        {
            foreach (var root in roots) { TryDeleteDirectory(root); }
        }
    }
}
