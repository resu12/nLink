using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "AcceptancePhase5Fast")]
[Trait("AcceptancePhase5Suite", "SummaryWriter")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPhase5SummaryWriterTests : FileTransferAcceptancePhase5TestBase
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5SingleScenarioSummaryUsesFilteredRunCount()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-single-scenario");

        try
        {
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                BuildFakeRouteAcceptanceEnvironment("phase5-single-scenario"));

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake single-scenario Phase 5 route acceptance to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["run_count"]);
            Assert.Equal("1", summary["expected_run_count"]);
            Assert.Equal("regular-v4-live-activation-off-on-off-256mb", summary["requested_scenarios"]);
            Assert.Equal("0", summary["summary_json_failed"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.fallback_receiver_state_churn_warning_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.fallback_receiver_state_churn_efficiency_class"]);
            Assert.True(File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")), "Expected Phase 5 JSON summary.");
            Assert.True(File.Exists(Path.Combine(runRoot, "phase5-summary-writer-diagnostics.txt")), "Expected Phase 5 summary writer diagnostics.");
            Assert.True(File.Exists(Path.Combine(runRoot, "phase5-summary-writer-diagnostics.json")), "Expected Phase 5 summary writer JSON diagnostics.");

            var diagnostics = ReadArtifactReport(runRoot, "phase5-summary-writer-diagnostics.txt");
            Assert.Equal("regular-v4-live-activation-off-on-off-256mb", diagnostics["requested_scenarios"]);
            Assert.Equal("1", diagnostics["expected_run_count"]);
            Assert.Equal("1", diagnostics["observed_run_count"]);
            Assert.Equal("0", diagnostics["summary_json_failed"]);
            Assert.Equal("PASS", diagnostics["final_verdict"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationFailsTerminalBeforeFallbackSwitchOff()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-terminal-before-fallback-switch-off");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-terminal-before-fallback-switch-off");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast,file_tuna_v4";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TERMINAL_BEFORE_FALLBACK_BOUNDARY"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("FAIL", summary["correctness_verdict"]);
            Assert.Equal("file_tuna_v4", summary["regular-v4-live-activation-off-on-off-256mb.final_route"]);
            Assert.Equal("regular_nkn_v4_fast,file_tuna_v4", summary["regular-v4-live-activation-off-on-off-256mb.route_sequence"]);
            Assert.Equal("file_tuna_v4", summary["regular-v4-live-activation-off-on-off-256mb.live_epoch_route_changes"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.completed"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.sha_ok"]);
            Assert.Equal("terminal_before_fallback_switch_off", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            Assert.Contains("terminal_before_fallback_switch_off", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationClassifiesRegularV4RepairBeforeSecondTuna()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-regular-v4-repair-before-second-tuna");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-regular-v4-repair-before-second-tuna");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "regular_v4_frontier_repair";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "regular_v4_frontier_repair_liveness_settlement_missing";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("FAIL", summary["correctness_verdict"]);
            Assert.Equal("regular_nkn_v4_fast", summary["regular-v4-live-activation-off-on-off-256mb.final_route"]);
            Assert.Equal("regular_nkn_v4_fast", summary["regular-v4-live-activation-off-on-off-256mb.route_sequence"]);
            Assert.Equal("regular_v4_frontier_repair_liveness_settlement_missing", summary["regular-v4-live-activation-off-on-off-256mb.setup_failure_reason"]);
            Assert.Equal("regular_v4_frontier_repair_liveness_settlement_missing", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationClassifiesReceiveRecoveryRejectedBeforeRuntimeUnlock()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-regular-v4-rejected-before-runtime-unlock");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-regular-v4-rejected-before-runtime-unlock");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "runtime_unlock_retry";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "regular_v4_receive_recovery_rejected_before_runtime_unlock";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("FAIL", summary["correctness_verdict"]);
            Assert.Equal("regular_nkn_v4_fast", summary["regular-v4-live-activation-off-on-off-256mb.final_route"]);
            Assert.Equal("regular_nkn_v4_fast", summary["regular-v4-live-activation-off-on-off-256mb.route_sequence"]);
            Assert.Equal("regular_v4_receive_recovery_rejected_before_runtime_unlock", summary["regular-v4-live-activation-off-on-off-256mb.setup_failure_reason"]);
            Assert.Equal("regular_v4_receive_recovery_rejected_before_runtime_unlock", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationFailsTerminalBeforeSecondTunaActivation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-terminal-before-second-tuna");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-terminal-before-second-tuna");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TERMINAL_BEFORE_LIVE_REACTIVATION"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("FAIL", summary["correctness_verdict"]);
            Assert.Equal("post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.final_route"]);
            Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.route_sequence"]);
            Assert.Equal("file_tuna_v4,post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.live_epoch_route_changes"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.completed"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.sha_ok"]);
            Assert.Equal("second_tuna_activation_not_proven", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            Assert.Contains("terminal_before_second_tuna_activation", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationAllowsCleanTerminalAfterSecondTunaBeforeFinalFallback()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-terminal-after-second-tuna");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-terminal-after-second-tuna");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TERMINAL_BEFORE_FALLBACK_BOUNDARY"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected clean terminal-before-fallback-boundary prefix to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("file_tuna_v4", summary["regular-v4-live-activation-off-on-off-256mb.final_route"]);
            Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4", summary["regular-v4-live-activation-off-on-off-256mb.route_sequence"]);
            Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4", summary["regular-v4-live-activation-off-on-off-256mb.live_epoch_route_changes"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.completed"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.sha_ok"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5SummaryJsonFailurePreservesTextSummary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-summary-json-forced-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-summary-json-forced-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FORCE_SUMMARY_JSON_FAILURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt")), "Expected text summary to survive JSON failure.");
            Assert.False(File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")), "JSON summary should not be reported as successful when serialization fails.");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("1", summary["run_count"]);
            Assert.Equal("1", summary["expected_run_count"]);
            Assert.Equal("1", summary["summary_json_failed"]);
            Assert.Contains("forced_summary_json_failure", summary["summary_json_failure_reason"], StringComparison.Ordinal);

            var diagnostics = ReadArtifactReport(runRoot, "phase5-summary-writer-diagnostics.txt");
            Assert.Equal("1", diagnostics["summary_json_failed"]);
            Assert.Contains("forced_summary_json_failure", diagnostics["summary_json_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("FAIL", diagnostics["final_verdict"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }
}
