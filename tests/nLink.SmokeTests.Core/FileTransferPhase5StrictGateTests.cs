using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "AcceptancePhase5Fast")]
[Trait("AcceptancePhase5Suite", "StrictGate")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPhase5StrictGateTests : FileTransferAcceptancePhase5TestBase
{
    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_BRIDGE_LIVENESS_FAIL", "1", "regular-v4-live-activation-off-on-off-256mb", "incomplete_proof", "bridge liveness integration verdict is fail")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_FALLBACK_AUTHORITY_METADATA_MISSING", "1", "regular-v4-live-activation-off-on-off-256mb", "fallback_authority", "fallback leg authority proof verdict is fail")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSPORT_ONLY_LIVE_PROOF", "1", "live-switch-off-helpee-64mb", "live_route_proof", "live route epoch sequence mismatch")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_ROUTE", "file_tuna_v6", "active-tuna-v4-64mb", "incomplete_proof", "active file_tuna_v6")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_SHA_FAIL", "1", "active-tuna-v4-64mb", "protocol_or_integrity", "completion/integrity")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_WARNING_CAP_EXCESS", "1", "active-tuna-v4-64mb", "warning_policy", "warning cap exceeded")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_SECOND_TRANSFER_AFTER_REACTIVATION_SECOND_TRANSFER_NOT_STARTED", "1", "second-transfer-after-reactivation", "setup", "second transfer setup failure after reactivation")]
    public async Task RunFileTransferRouteAcceptance_Phase5FakeModeFailsStrictAcceptance(
        string environmentName,
        string environmentValue,
        string scenarioName,
        string expectedFailureClass,
        string expectedFailure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-fail");
            environment[environmentName] = environmentValue;

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenarioName,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt")), $"Expected Phase 5 failure summary. STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Contains(expectedFailure, summaryText, StringComparison.Ordinal);
            Assert.Equal(expectedFailureClass, summary[$"{scenarioName}.acceptance_failure_class"]);
            if (environmentName.EndsWith("_BRIDGE_LIVENESS_FAIL", StringComparison.Ordinal))
            {
                Assert.Equal("fallback_control_plane_starvation", summary[$"{scenarioName}.runtime_failure_candidate"]);
                var retainedText = File.ReadAllText(Path.Combine(runRoot, scenarioName, "filetransfer-retained-log-slice.log"));
                var currentFallbackRecoveryIndex = retainedText.LastIndexOf("event=filetransfer_fallback_leg_authority_bridge_recovery_requested", StringComparison.Ordinal);
                var livenessTimeoutIndex = currentFallbackRecoveryIndex < 0
                    ? -1
                    : retainedText.IndexOf("event=session_liveness_timeout", currentFallbackRecoveryIndex, StringComparison.Ordinal);
                var checkpointAcceptedIndex = currentFallbackRecoveryIndex < 0
                    ? -1
                    : retainedText.IndexOf("event=filetransfer_fallback_leg_authority_checkpoint_accepted", currentFallbackRecoveryIndex, StringComparison.Ordinal);
                Assert.True(
                    currentFallbackRecoveryIndex >= 0 &&
                    livenessTimeoutIndex > currentFallbackRecoveryIndex &&
                    (checkpointAcceptedIndex < 0 || checkpointAcceptedIndex > livenessTimeoutIndex),
                    "Expected the current fallback leg to time out without checkpoint acceptance, making control-plane starvation more specific than the downstream bridge-liveness symptom.");
            }
            else if (environmentName.EndsWith("_ACTIVE_TUNA_V4_64MB_ROUTE", StringComparison.Ordinal))
            {
                Assert.Equal("active_file_tuna_v6_evidence", summary[$"{scenarioName}.runtime_failure_candidate"]);
            }
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }
}
