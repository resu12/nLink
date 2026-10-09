using System.Text;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "AcceptancePhase5Full")]
[Trait("AcceptancePhase5Suite", "FullMatrix")]
[Trait("Category", "Slow")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPhase5FullMatrixTests : FileTransferAcceptancePhase5TestBase
{
    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("independent_pairs", true)]
    [InlineData("independent_missing_receive", false)]
    public async Task RunFileTransferRouteAcceptance_Phase5FakeModeProducesPassArtifacts(string payloadMode, bool expectedPass)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ROUTE_CYCLE_SOURCE"] =
                await GenerateRouteCycleSourceAsync(repoRoot, Path.Combine(artifactRoot, "peers"), payloadMode);
            string[] scenarioArguments = expectedPass ? [] :
                ["-ScenarioName", "regular-v4-live-activation-off-on-off-256mb", "-GoodputOnlyRerunLimit", "0", "-SetupOnlyRerunLimit", "0"];
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30",
                    ..scenarioArguments
                ],
                environment);

            if (!expectedPass)
            {
                var failed = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
                Assert.NotEqual(0, result.ExitCode);
                Assert.Equal("FAIL", failed["correctness_verdict"]);
                Assert.Equal("filetransfer_tuna_activation_delivery_unproven",
                    failed["regular-v4-live-activation-off-on-off-256mb.runtime_failure_candidate"]);
                return;
            }

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake Phase 5 route acceptance to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            AssertRequiredPhase5RouteAcceptanceArtifacts(runRoot);
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("phase5", summary["acceptance_phase"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("PASS", summary["performance_verdict"]);
            Assert.Equal("7", summary["run_count"]);
            Assert.Equal("0", summary["failure_count"]);
            var snapshot = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(runRoot,
                "regular-v4-live-activation-off-on-off-256mb", "filetransfer-coordinator-snapshot-full-summary.json")));
            using (snapshot) { Assert.True(snapshot.RootElement.GetProperty("BoundaryCount").GetInt32() > 0); }
            Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.route_sequence"]);
            Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.live_epoch_route_changes"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.live_route_epoch_proof_verdict"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.fallback_leg_authority_proof_verdict"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.bridge_liveness_integration_verdict"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            Assert.Equal("none", summary["active-tuna-v4-64mb.acceptance_failure_class"]);
            Assert.Equal("none", summary["active-tuna-v4-64mb.bridge_liveness_integration_verdict"]);
            Assert.Equal("file_tuna_v4", summary["second-transfer-after-reactivation.final_route"]);
            Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4", summary["second-transfer-after-reactivation.route_sequence"]);
            Assert.Equal("post_tuna_fallback_v6,file_tuna_v4", summary["second-transfer-after-reactivation.live_epoch_route_changes"]);
            Assert.Equal("0", summary["second-transfer-after-reactivation.retry_used"]);
            Assert.True(File.Exists(Path.Combine(runRoot, "phase5-network-variance-note.md")));
            Assert.True(File.Exists(Path.Combine(runRoot, "route-acceptance-summary.txt")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

}
