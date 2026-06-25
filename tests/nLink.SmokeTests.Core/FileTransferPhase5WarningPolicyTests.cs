using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "AcceptancePhase5Fast")]
[Trait("AcceptancePhase5Suite", "WarningPolicy")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPhase5WarningPolicyTests : FileTransferAcceptancePhase5TestBase
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5CompletedRegularExternalTransportCapExcessIsEnvironmental()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-regular-completed-external-churn");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-regular-completed-external-churn");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_WARNING_CAP_EXCESS"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-nkn-v4-64mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected completed regular-NKN Phase 5 external churn to be classified as environmental, not protocol/integrity.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("PASS", summary["performance_verdict"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.completed"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.sha_ok"]);
            Assert.Equal("external_transport_churn", summary["regular-nkn-v4-64mb.warning_cap_exceeded_kinds"]);
            Assert.Equal("live_transport_external_churn_completed_clean", summary["regular-nkn-v4-64mb.environmental_classification"]);
            Assert.Equal("0", summary["regular-nkn-v4-64mb.measurement_contaminated"]);
            Assert.Equal("none", summary["regular-nkn-v4-64mb.acceptance_failure_class"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5CompletedLiveFallbackWarningCapExcessIsEnvironmental()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-live-fallback-completed-warning-churn");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-live-fallback-completed-warning-churn");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPER_64MB_WARNING_CAP_EXCESS"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "live-switch-off-helper-64mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected completed live-fallback Phase 5 warning cap noise to be classified as environmental, not correctness failure.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("1", summary["live-switch-off-helper-64mb.completed"]);
            Assert.Equal("1", summary["live-switch-off-helper-64mb.sha_ok"]);
            Assert.Equal("external_transport_churn", summary["live-switch-off-helper-64mb.warning_cap_exceeded_kinds"]);
            Assert.Equal("live_transport_recovered_fallback_churn_completed_clean", summary["live-switch-off-helper-64mb.environmental_classification"]);
            Assert.Equal("none", summary["live-switch-off-helper-64mb.acceptance_failure_class"]);
            Assert.Equal("pass", summary["live-switch-off-helper-64mb.live_route_epoch_proof_verdict"]);
            Assert.Equal("pass", summary["live-switch-off-helper-64mb.fallback_leg_authority_proof_verdict"]);
            Assert.Equal("pass", summary["live-switch-off-helper-64mb.bridge_liveness_integration_verdict"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }
}
