using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "AcceptancePhase5Fast")]
[Trait("AcceptancePhase5Suite", "SecondTransfer")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPhase5SecondTransferTests : FileTransferAcceptancePhase5TestBase
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5SecondTransferReadinessUnstableRerunsCleanAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-second-transfer-readiness-unstable-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-second-transfer-readiness-unstable-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ROUTE_CYCLE_SOURCE"] =
                await GenerateRouteCycleSourceAsync(repoRoot, Path.Combine(artifactRoot, "peers"), "independent_pairs");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_SECOND_TRANSFER_AFTER_REACTIVATION_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_SECOND_TRANSFER_AFTER_REACTIVATION_TRANSIENT_SETUP_PHASE"] = "preactivation_readiness";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_SECOND_TRANSFER_AFTER_REACTIVATION_TRANSIENT_SETUP_REASON"] = "second_transfer_tuna_readiness_unstable";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected second-transfer readiness instability to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["second-transfer-after-reactivation.retry_used"]);
            Assert.Equal("2", summary["second-transfer-after-reactivation.selected_attempt"]);
            Assert.False(summary.ContainsKey("second-transfer-after-reactivation.setup_failure_phase"));
            Assert.False(summary.ContainsKey("second-transfer-after-reactivation.setup_failure_reason"));
            Assert.Contains("second_transfer_tuna_readiness_unstable", summary["second-transfer-after-reactivation.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("none", summary["second-transfer-after-reactivation.acceptance_failure_class"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "second-transfer-after-reactivation-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5SecondTransferRejectsFailedCombinedProofDespiteScopedRoutePass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-second-transfer-combined-slice-noisy");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-second-transfer-combined-slice-noisy");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ROUTE_CYCLE_SOURCE"] =
                await GenerateRouteCycleSourceAsync(repoRoot, Path.Combine(artifactRoot, "peers"), "independent_pairs");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_SECOND_TRANSFER_AFTER_REACTIVATION_COMBINED_ROUTE_ANALYSIS_CONTAMINATED"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "second-transfer-after-reactivation",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            // This fixture injects failed parent authority, not an allowed warning.
            // A scoped route pass must not erase those failures. The accepted-parent
            // positive is covered by SecondTransferReport_FreshCoordinatorStreamsAreReused.
            Assert.NotEqual(0, result.ExitCode);
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.True(int.Parse(summary["second-transfer-after-reactivation.failure_count"], CultureInfo.InvariantCulture) > 0);
            Assert.Equal("route_runtime", summary["second-transfer-after-reactivation.acceptance_failure_class"]);
            Assert.Equal("fail", summary["second-transfer-after-reactivation.route_consistency_verdict"]);
            Assert.Equal("0", summary["second-transfer-after-reactivation.retry_used"]);
            Assert.Equal("file_tuna_v4", summary["second-transfer-after-reactivation.final_route"]);
            Assert.Equal("4", summary["second-transfer-after-reactivation.protocol"]);
            Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4", summary["second-transfer-after-reactivation.route_sequence"]);
            var scenarioDir = Path.Combine(runRoot, "second-transfer-after-reactivation");
            var parent = ReadArtifactReport(scenarioDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("FAIL", parent["verdict"]);
            Assert.Equal("3", parent["hard_failure_count"]);
            var scoped = ReadArtifactReport(Path.Combine(scenarioDir, "second-transfer-analysis"), "filetransfer-route-consistency-summary.txt");
            Assert.Equal("pass", scoped["route_consistency_verdict"]);
            Assert.Equal("file_tuna_v4", scoped["selected_route_changes"]);
            Assert.False(File.Exists(Path.Combine(scenarioDir, "second-transfer-analysis", "primary-derived-report.json")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }
}

