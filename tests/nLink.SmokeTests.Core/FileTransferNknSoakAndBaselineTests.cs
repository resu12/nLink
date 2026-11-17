using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "NknSoakAndBaseline")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferNknSoakAndBaselineTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [InlineData(4, true)]
    [InlineData(6, false)]
    [Trait("Category", "Smoke")]
    public async Task NknReportingProjection_PreservesObservationsWithoutRouteEvaluation(int protocol, bool terminal)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var result = await RunPowerShellScriptTextAsync(
            """
            param([string]$RepoRoot, [int]$Protocol, [int]$Terminal)
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            . (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
            $lines = @(
                '[2026-09-22 00:00:00Z] [INFO] [Test] event=nkn_bridge_bulk_send_summary; send_failures=100',
                '[2026-09-22 00:00:10Z] [INFO] [Test] event=filetransfer_v4_sender_pump_summary; transfer_id=t; batch_frames_sent_total=7',
                '[2026-09-22 00:00:10Z] [INFO] [Test] event=nkn_bridge_bulk_send_summary; send_failures=3',
                '[2026-09-22 00:00:10Z] [INFO] [Test] event=screenshare_bridge_media_send_summary; queue_drops=2; send_failures=4',
                "[2026-09-22 00:00:11Z] [INFO] [Test] event=filetransfer_binary_frame_sent; transfer_id=t; frame_type=filetransfer.chunk_batch.v$Protocol"
            )
            foreach ($name in @('filetransfer_chunk_batch_split_for_transport', 'filetransfer_reorder_pressure',
                'filetransfer_request_timeout_detected', 'filetransfer_chunk_retry_requested',
                'filetransfer_transport_payload_rejected', 'filetransfer_data_frame_decode_failed', 'filetransfer_message_rejected')) {
                $lines += "[2026-09-22 00:00:12Z] [INFO] [Test] event=$name; transfer_id=t"
            }
            if ($Terminal -eq 1) {
                $lines += '[2026-09-22 00:00:13Z] [INFO] [Test] event=transfer_terminal; transfer_id=t; direction=inbound; state=Completed'
            }
            $sequence = 0
            $events = @(foreach ($line in $lines) {
                $sequence++
                ConvertFrom-FileTransferLogLine -Line $line -FilePath 'fixture.log' -LineNumber $sequence -Sequence $sequence
            })
            $script:routeEvaluations = 0
            function Get-FileTransferRouteConsistency {
                param($TransferEvents)
                $script:routeEvaluations++
                return [pscustomobject]@{}
            }
            $full = New-FileTransferRetainedSummary -Events $events -LogFiles @('fixture.log') -AllTransfers
            if ($script:routeEvaluations -ne 1) { throw 'full control did not evaluate once' }
            function Get-FileTransferRouteConsistency { throw 'reporting invoked route authority' }
            $report = New-FileTransferRetainedSummary -Events $events -LogFiles @('fixture.log') -AllTransfers -ReportingOnly
            foreach ($property in $report.PSObject.Properties) {
                $expected = ConvertTo-Json -InputObject $full.($property.Name) -Depth 12 -Compress
                $actual = ConvertTo-Json -InputObject $property.Value -Depth 12 -Compress
                if ($expected -cne $actual) { throw "report mismatch: $($property.Name)" }
            }
            if ($report.BatchSentAsBatchCount -ne 7 -or $report.BatchSplitCount -ne 1 -or
                $report.RequestTimeoutCount -ne 1 -or $report.RetryRequestedCount -ne 1 -or
                $report.PayloadRejectedCount -ne 1 -or $report.DataFrameDecodeFailedCount -ne 1 -or
                $report.MessageRejectedCount -ne 1 -or $report.ReorderEventCount -ne 1 -or
                $report.HasTerminalEvidence -ne ($Terminal -eq 1) -or
                -not $report.FrameTypeCounts.ContainsKey("filetransfer.chunk_batch.v$Protocol") -or
                $report.GlobalEvents.Count -ne 2 -or
                $report.GlobalEvents[0].Fields['send_failures'] -ne '3' -or
                $report.GlobalEvents[1].Fields['send_failures'] -ne '4' -or
                $report.PSObject.Properties.Name -contains 'RouteConsistency') { throw 'report evidence changed' }
            $empty = New-FileTransferRetainedSummary -Events @() -LogFiles @() -AllTransfers -ReportingOnly
            if ($empty.HasTerminalEvidence -or $empty.FrameTypeCounts.Count -ne 0 -or
                $empty.FirstTimestamp -ne '' -or $empty.GlobalEvents.Count -ne 0) { throw 'empty evidence invented' }
            Write-Output 'reporting-projection-ok'
            """,
            [FindRepoRoot(), protocol.ToString(CultureInfo.InvariantCulture), terminal ? "1" : "0"]);
        Assert.True(result.ExitCode == 0, $"{result.Stdout}\n{result.Stderr}");
        Assert.Contains("reporting-projection-ok", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nkn-fast", false)]
    [InlineData("nkn-fast", true)]
    [InlineData("nkn-mixed", false)]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferNknSoak_CompactReportMatchesFullReport(string mode, bool progressTimeout)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-nkn-report-contract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            Dictionary<string, string>? fullReport = null;
            foreach (var compact in new[] { false, true })
            {
                var artifactDir = Path.Combine(tempRoot, compact ? "compact" : "full");
                var environment = BuildFakeLiveNknEnvironment();
                environment["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = compact ? "1" : "0";
                environment["NLINK_FILETRANSFER_NKN_SOAK_FAKE_PROGRESS_TIMEOUT"] = progressTimeout ? "1" : "0";
                var result = await RunPowerShellFileAsync(
                    repoRoot,
                    Path.Combine(repoRoot, "tools", "Run-FileTransferNknSoak.ps1"),
                    ["-Mode", mode, "-ArtifactDir", artifactDir, "-PayloadSizes", "64KiB",
                     "-Cycles", "1", "-TimeoutSeconds", "30", "-FailOnGate"],
                    environment);

                var reportPath = Path.Combine(artifactDir, "filetransfer-live-nkn-summary.txt");
                Assert.True(File.Exists(reportPath), $"Missing live report (compact={compact}).\n{result.Stdout}\n{result.Stderr}");
                Assert.Equal(progressTimeout ? 1 : 0, result.ExitCode);
                var report = ReadArtifactReport(artifactDir, "filetransfer-live-nkn-summary.txt");
                Assert.Equal(progressTimeout ? "INCONCLUSIVE_PROGRESS_TIMEOUT" : "PASS", report["verdict"]);
                Assert.Equal(progressTimeout ? "0" : "1", report["cycles_completed"]);
                Assert.Equal(progressTimeout ? "1" : "0", report["terminal_missing_after_progress_timeout"]);
                if (fullReport is null)
                {
                    fullReport = report;
                    continue;
                }

                Assert.Equal(fullReport.Keys.OrderBy(key => key), report.Keys.OrderBy(key => key));
                foreach (var key in fullReport.Keys.Except(["exe_path", "observed_start_utc", "observed_end_utc"]))
                {
                    Assert.True(fullReport[key] == report[key], $"Compact/full mismatch for {key}: {fullReport[key]} != {report[key]}");
                }
            }
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task BaselineComparison_ReadsLiveNknSummaryAndFlagsSafeRegression()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-live-baseline", Guid.NewGuid().ToString("N"));
        var currentDir = Path.Combine(tempRoot, "current");
        var safeDir = Path.Combine(tempRoot, "safe");
        Directory.CreateDirectory(currentDir);
        Directory.CreateDirectory(safeDir);

        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(currentDir, "filetransfer-live-nkn-summary.txt"),
                BuildLiveSummaryLines(averageGoodput: 10, minimumGoodput: 10, bridgeWaiting: 2),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await File.WriteAllLinesAsync(
                Path.Combine(safeDir, "filetransfer-live-nkn-summary.txt"),
                BuildLiveSummaryLines(averageGoodput: 100, minimumGoodput: 100, bridgeWaiting: 0),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var result = await RunPowerShellScriptTextAsync(
                """
param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string]$CurrentDir,
    [Parameter(Mandatory = $true)][string]$SafeDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools\FileTransferSoak\BaselineComparison.ps1')
$result = Write-FileTransferBaselineComparison -ArtifactDir $CurrentDir -SafeBaselineArtifactDir $SafeDir
if (-not $result.RegressionFailed) {
    throw 'Expected live NKN safe baseline regression to be detected.'
}
""",
                [repoRoot, currentDir, safeDir]);

            Assert.Equal(0, result.ExitCode);
            var comparison = ReadArtifactReport(currentDir, "baseline-comparison.txt");
            Assert.Equal("live-nkn", comparison["current_artifact_kind"]);
            Assert.Equal("1", comparison["regression_failed"]);
            Assert.Contains("average goodput regressed", File.ReadAllText(Path.Combine(currentDir, "baseline-comparison.txt")), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task BaselineComparison_CrossProtocolSafeBaselineIsReportOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-protocol-baseline", Guid.NewGuid().ToString("N"));
        var currentDir = Path.Combine(tempRoot, "current-v4");
        var safeDir = Path.Combine(tempRoot, "safe-v3");
        Directory.CreateDirectory(currentDir);
        Directory.CreateDirectory(safeDir);

        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(currentDir, "filetransfer-live-nkn-summary.txt"),
                BuildLiveSummaryLines(averageGoodput: 10, minimumGoodput: 10, bridgeWaiting: 0, protocolVersion: "6", v4BatchRatio: 1.0, v4PayloadFillPercent: 96.0),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await File.WriteAllLinesAsync(
                Path.Combine(safeDir, "filetransfer-live-nkn-summary.txt"),
                BuildLiveSummaryLines(averageGoodput: 1000, minimumGoodput: 1000, bridgeWaiting: 0, protocolVersion: "3"),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var result = await RunPowerShellScriptTextAsync(
                """
param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string]$CurrentDir,
    [Parameter(Mandatory = $true)][string]$SafeDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools\FileTransferSoak\BaselineComparison.ps1')
$result = Write-FileTransferBaselineComparison -ArtifactDir $CurrentDir -SafeBaselineArtifactDir $SafeDir
if ($result.RegressionFailed) {
    throw 'Cross-protocol baseline mismatch must be report-only.'
}
""",
                [repoRoot, currentDir, safeDir]);

            Assert.Equal(0, result.ExitCode);
            var comparison = ReadArtifactReport(currentDir, "baseline-comparison.txt");
            Assert.Equal("1", comparison["baseline_protocol_mismatch"]);
            Assert.Equal("0", comparison["regression_failed"]);
            Assert.Equal("6", comparison["current_data_protocol_version"]);
            Assert.Equal("3", comparison["safe_data_protocol_version"]);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task BaselineComparison_V4SameProtocolSafeBaselineCanGate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-v4-baseline", Guid.NewGuid().ToString("N"));
        var currentDir = Path.Combine(tempRoot, "current-v4");
        var safeDir = Path.Combine(tempRoot, "safe-v4");
        Directory.CreateDirectory(currentDir);
        Directory.CreateDirectory(safeDir);

        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(currentDir, "filetransfer-live-nkn-summary.txt"),
                BuildLiveSummaryLines(averageGoodput: 10, minimumGoodput: 10, bridgeWaiting: 0, protocolVersion: "6", v4BatchRatio: 0.40, v4PayloadFillPercent: 40.0),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await File.WriteAllLinesAsync(
                Path.Combine(safeDir, "filetransfer-live-nkn-summary.txt"),
                BuildLiveSummaryLines(averageGoodput: 100, minimumGoodput: 100, bridgeWaiting: 0, protocolVersion: "6", v4BatchRatio: 1.0, v4PayloadFillPercent: 95.0),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var result = await RunPowerShellScriptTextAsync(
                """
param(
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][string]$CurrentDir,
    [Parameter(Mandatory = $true)][string]$SafeDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools\FileTransferSoak\BaselineComparison.ps1')
$result = Write-FileTransferBaselineComparison -ArtifactDir $CurrentDir -SafeBaselineArtifactDir $SafeDir
if (-not $result.RegressionFailed) {
    throw 'Expected same-protocol V4 safe baseline regression to be detected.'
}
""",
                [repoRoot, currentDir, safeDir]);

            Assert.Equal(0, result.ExitCode);
            var comparison = ReadArtifactReport(currentDir, "baseline-comparison.txt");
            Assert.Equal("0", comparison["baseline_protocol_mismatch"]);
            Assert.Equal("1", comparison["regression_failed"]);
            Assert.Equal("6", comparison["current_data_protocol_version"]);
            Assert.Contains("V6 batch ratio regressed", File.ReadAllText(Path.Combine(currentDir, "baseline-comparison.txt")), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferNknSoak_FakeNknFastProducesPassArtifacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-live-fake", Guid.NewGuid().ToString("N"));
        var artifactDir = Path.Combine(tempRoot, "nkn-fast");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferNknSoak.ps1"),
                [
                    "-Mode", "nkn-fast",
                    "-ArtifactDir", artifactDir,
                    "-PayloadSizes", "64KiB",
                    "-Cycles", "1",
                    "-TimeoutSeconds", "30"
                ],
                BuildFakeLiveNknEnvironment());

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake live NKN fast soak to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            AssertRequiredLiveArtifacts(artifactDir);

            var verdict = ReadArtifactReport(artifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("PASS", verdict["verdict"]);

            var summary = ReadArtifactReport(artifactDir, "filetransfer-live-nkn-summary.txt");
            Assert.Equal("live-nkn", summary["artifact_kind"]);
            Assert.Equal("nkn-fast", summary["mode"]);
            Assert.Equal("1", summary["cycles_completed"]);
            Assert.Equal("4", summary["data_protocol_version"]);
            Assert.Equal("v4_default_21k", summary["payload_efficiency_profile"]);
            Assert.Equal("expected", summary["bridge_config_status"]);
            Assert.Equal("4/8/4", summary["bridge_observed_topology"]);
            Assert.Equal("4", summary["bridge_observed_bulk_send_concurrency"]);
            Assert.Equal("fanout", summary["bridge_observed_bulk_send_mode"]);
            Assert.Equal("1.000000", summary["v4_batch_ratio"]);
            Assert.Equal("1", summary["v4_feedback_redundant_success_count"]);
            Assert.Equal("0", summary["payload_rejected_count"]);
            Assert.Equal("0", summary["bridge_bulk_send_failure_count"]);

            var protocolShape = File.ReadAllText(Path.Combine(artifactDir, "protocol-shape-summary.txt"));
            Assert.Contains("filetransfer.chunk_batch.v4", protocolShape, StringComparison.Ordinal);
            Assert.Contains("v4_sender_started_count=1", protocolShape, StringComparison.Ordinal);
            Assert.Contains("v6_sender_started_count=0", protocolShape, StringComparison.Ordinal);
            Assert.Contains("v4_feedback_redundant_success_count=1", protocolShape, StringComparison.Ordinal);

            var baseline = ReadArtifactReport(artifactDir, "baseline-comparison.txt");
            Assert.Equal("4", baseline["current_data_protocol_version"]);
            Assert.Equal("1.000000", baseline["current_v4_batch_ratio"]);

            var promotion = ReadArtifactReport(artifactDir, "v4-promotion-decision.txt");
            Assert.Equal("hold_inconclusive", promotion["decision"]);
            Assert.Equal("non_v6_protocol", promotion["reason"]);
            Assert.Equal("4", promotion["data_protocol_version"]);
            Assert.Equal("1", promotion["current_long_proof_completed_cycle_count"]);
            Assert.Equal("0", promotion["long_proof_matrix_complete"]);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferNknSoak_FakeV4LongProofAndBaselineRerunHoldsRegularV4()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-live-v4-promotion-fake", Guid.NewGuid().ToString("N"));
        var safeDir = Path.Combine(tempRoot, "nkn-fast-safe");
        var rerunDir = Path.Combine(tempRoot, "nkn-fast-rerun");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var longProof = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferNknSoak.ps1"),
                [
                    "-Mode", "nkn-fast",
                    "-ArtifactDir", safeDir,
                    "-PayloadSizes", "16MiB,64MiB",
                    "-Cycles", "2",
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                BuildFakeLiveNknEnvironment());

            Assert.True(
                longProof.ExitCode == 0,
                $"Expected fake V4 long proof to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{longProof.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{longProof.Stderr}");
            AssertRequiredLiveArtifacts(safeDir);

            var longDecision = ReadArtifactReport(safeDir, "v4-promotion-decision.txt");
            Assert.Equal("hold_inconclusive", longDecision["decision"]);
            Assert.Equal("non_v6_protocol", longDecision["reason"]);
            Assert.Equal("4", longDecision["data_protocol_version"]);
            Assert.Equal("1500000.000", longDecision["target_goodput_bytes_per_second"]);
            Assert.Equal("1", longDecision["current_long_proof_matrix_complete"]);
            Assert.Equal("2", longDecision["current_long_proof_16m_completed_count"]);
            Assert.Equal("2", longDecision["current_long_proof_64m_completed_count"]);
            Assert.Equal("1", longDecision["goodput_target_met"]);

            var rerun = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferNknSoak.ps1"),
                [
                    "-Mode", "nkn-fast",
                    "-ArtifactDir", rerunDir,
                    "-PayloadSizes", "64MiB",
                    "-Cycles", "1",
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30",
                    "-SafeBaselineArtifactDir", safeDir,
                    "-FailOnGate"
                ],
                BuildFakeLiveNknEnvironment());

            Assert.True(
                rerun.ExitCode == 0,
                $"Expected fake V4 baseline rerun to complete cleanly.{Environment.NewLine}STDOUT:{Environment.NewLine}{rerun.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{rerun.Stderr}");
            AssertRequiredLiveArtifacts(rerunDir);

            var promotion = ReadArtifactReport(rerunDir, "v4-promotion-decision.txt");
            Assert.Equal("hold_inconclusive", promotion["decision"]);
            Assert.Equal("hold", promotion["promotion_status"]);
            Assert.Equal("non_v6_protocol", promotion["reason"]);
            Assert.Equal("4", promotion["data_protocol_version"]);
            Assert.Equal("1", promotion["safe_long_proof_matrix_complete"]);
            Assert.Equal("0", promotion["same_protocol_v6_baseline_pass"]);
            Assert.Equal("0", promotion["baseline_protocol_mismatch"]);
            Assert.Equal("0", promotion["baseline_regression_failed"]);
            Assert.Equal("4", promotion["safe_data_protocol_version"]);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferNknSoak_FakeV4LongProofBelowTargetHoldsRegularV4()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-live-v4-iterate-fake", Guid.NewGuid().ToString("N"));
        var artifactDir = Path.Combine(tempRoot, "nkn-fast-slow");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferNknSoak.ps1"),
                [
                    "-Mode", "nkn-fast",
                    "-ArtifactDir", artifactDir,
                    "-PayloadSizes", "16MiB,64MiB",
                    "-Cycles", "2",
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                BuildFakeLiveNknEnvironment(fakeGoodputBytesPerSecond: 1_400_000));

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake V4 below-target proof to complete cleanly.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            AssertRequiredLiveArtifacts(artifactDir);

            var decomposition = ReadArtifactReport(artifactDir, "throughput-decomposition-summary.txt");
            Assert.Equal("v4_sender_pump_underfed", decomposition["likely_limiter"]);

            var promotion = ReadArtifactReport(artifactDir, "v4-promotion-decision.txt");
            Assert.Equal("hold_inconclusive", promotion["decision"]);
            Assert.Equal("hold", promotion["promotion_status"]);
            Assert.Equal("non_v6_protocol", promotion["reason"]);
            Assert.Equal("fix_harness_or_analyzer_evidence", promotion["next_focus"]);
            Assert.Equal("4", promotion["data_protocol_version"]);
            Assert.Equal("1500000.000", promotion["target_goodput_bytes_per_second"]);
            Assert.Equal("1", promotion["long_proof_matrix_complete"]);
            Assert.Equal("0", promotion["goodput_target_met"]);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferNknSoak_FakeProgressTimeoutWritesStableFailureFields()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-live-fake-timeout", Guid.NewGuid().ToString("N"));
        var artifactDir = Path.Combine(tempRoot, "nkn-fast-timeout");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var environment = BuildFakeLiveNknEnvironment();
            environment["NLINK_FILETRANSFER_NKN_SOAK_FAKE_PROGRESS_TIMEOUT"] = "1";
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferNknSoak.ps1"),
                [
                    "-Mode", "nkn-fast",
                    "-ArtifactDir", artifactDir,
                    "-PayloadSizes", "64MiB",
                    "-Cycles", "1",
                    "-TimeoutSeconds", "30",
                    "-FailOnGate"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            AssertRequiredLiveArtifacts(artifactDir);

            var liveSummary = ReadArtifactReport(artifactDir, "filetransfer-live-nkn-summary.txt");
            Assert.Equal("INCONCLUSIVE_PROGRESS_TIMEOUT", liveSummary["verdict"]);
            Assert.Equal("0", liveSummary["cycles_observed"]);
            Assert.Equal("1", liveSummary["gui_progress_timeout_count"]);
            Assert.Equal("-1", liveSummary["last_receiver_next_chunk"]);
            Assert.Equal("-1", liveSummary["last_receiver_highest_chunk"]);
            Assert.Equal("4729", liveSummary["last_progress_event_count"]);
            Assert.Equal("1", liveSummary["terminal_missing_after_progress_timeout"]);

            var decomposition = ReadArtifactReport(artifactDir, "throughput-decomposition-summary.txt");
            Assert.Equal("sparse_frontier_gap_repair_stalled", decomposition["likely_limiter"]);
            Assert.Equal("1", decomposition["progress_timeout_with_receiver_gap_stall"]);

            var promotion = ReadArtifactReport(artifactDir, "v4-promotion-decision.txt");
            Assert.Equal("hold_inconclusive", promotion["decision"]);
            Assert.Equal("progress_timeout_incomplete_long_proof", promotion["reason"]);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferOps_NknMixedFakeRunnerProducesCleanMediaEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-live-mixed-fake", Guid.NewGuid().ToString("N"));
        var artifactDir = Path.Combine(tempRoot, "nkn-mixed");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var result = await RunFileTransferOpsAsync(
                repoRoot,
                [
                    "-Mode", "NknMixed",
                    "-ArtifactDir", artifactDir,
                    "-PayloadSizes", "64KiB",
                    "-Cycles", "1",
                    "-TimeoutSeconds", "30"
                ],
                BuildFakeLiveNknEnvironment());

            Assert.True(
                result.ExitCode == 0,
                $"Expected public NknMixed fake runner to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            AssertRequiredLiveArtifacts(artifactDir);

            var verdict = ReadArtifactReport(artifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("PASS", verdict["verdict"]);

            var summary = ReadArtifactReport(artifactDir, "filetransfer-live-nkn-summary.txt");
            Assert.Equal("nkn-mixed", summary["mode"]);
            Assert.Equal("1", summary["cycles_completed"]);
            Assert.Equal("0", summary["media_queue_drop_count"]);
            Assert.Equal("0", summary["media_send_failure_count"]);
            Assert.Equal("0", summary["media_queue_severe_count"]);

            var retainedSlice = File.ReadAllText(Path.Combine(artifactDir, "filetransfer-retained-log-slice.log"));
            Assert.Contains("screenshare_bridge_media_send_summary", retainedSlice, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferNknSoak_FakeSafeBaselineRegressionFailsWhenGated(bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-live-regression-fake", Guid.NewGuid().ToString("N"));
        var artifactDir = Path.Combine(tempRoot, "current");
        var safeDir = Path.Combine(tempRoot, "safe");
        Directory.CreateDirectory(artifactDir);
        Directory.CreateDirectory(safeDir);

        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(safeDir, "filetransfer-live-nkn-summary.txt"),
                BuildLiveSummaryLines(averageGoodput: 100_000, minimumGoodput: 100_000, bridgeWaiting: 0, protocolVersion: "4", v4BatchRatio: 1.0, v4PayloadFillPercent: 95.0),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var environment = BuildFakeLiveNknEnvironment(fakeGoodputBytesPerSecond: 1_000);
            environment["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = compact ? "1" : "0";
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferNknSoak.ps1"),
                [
                    "-Mode", "nkn-fast",
                    "-ArtifactDir", artifactDir,
                    "-PayloadSizes", "64KiB",
                    "-Cycles", "1",
                    "-TimeoutSeconds", "30",
                    "-SafeBaselineArtifactDir", safeDir,
                    "-FailOnGate"
                ],
                environment);

            Assert.Equal(1, result.ExitCode);
            if (!compact)
            {
                // Compact analysis intentionally omits the full throughput report set.
                AssertRequiredLiveArtifacts(artifactDir);
            }

            var verdict = ReadArtifactReport(artifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("FAIL_REGRESSION_BUDGET", verdict["verdict"]);
            Assert.Equal("baseline-comparison.txt", verdict["next_artifact"]);

            var comparison = ReadArtifactReport(artifactDir, "baseline-comparison.txt");
            Assert.Equal("live-nkn", comparison["current_artifact_kind"]);
            Assert.Equal("1", comparison["regression_failed"]);
            Assert.Contains("average goodput regressed", File.ReadAllText(Path.Combine(artifactDir, "baseline-comparison.txt")), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }
}
