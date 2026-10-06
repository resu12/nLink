using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "AcceptancePhase5Fast")]
[Trait("AcceptancePhase5Suite", "RerunPolicy")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPhase5RerunPolicyTests : FileTransferAcceptancePhase5TestBase
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5ReceiveRecoveryExhaustionCannotOverrideMissingDelivery()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-canonical-receive-recovery-exhaustion-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-canonical-receive-recovery-exhaustion-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RECEIVE_RECOVERY_EXHAUSTED_BEFORE_RUNTIME_UNLOCK"] = "1";

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

            // Legacy route-only input does not prove Tuna delivery. Recovery observations
            // cannot replace that proof; classifier-only coverage uses explicit policy inputs.
            Assert.NotEqual(0, result.ExitCode);
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            const string prefix = "regular-v4-live-activation-off-on-off-256mb";
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("0", summary[$"{prefix}.retry_used"]);
            Assert.Equal("filetransfer_tuna_activation_delivery_unproven", summary[$"{prefix}.runtime_failure_candidate"]);
            Assert.Equal("(none)", summary[$"{prefix}.environmental_classification"]);
            Assert.Equal("0", summary[$"{prefix}.measurement_contaminated"]);
            Assert.False(Directory.Exists(Path.Combine(runRoot, prefix + "-rerun-1")));
            var retainedText = File.ReadAllText(Path.Combine(runRoot, prefix, "filetransfer-retained-log-slice.log"));
            var retryBlockedIndex = retainedText.IndexOf("event=tuna_activation_control_send_waiting_for_regular_v4_recovery", StringComparison.Ordinal);
            var recoveryExhaustedIndex = retainedText.IndexOf("event=nkn_bridge_receive_stall_recovery_failed", StringComparison.Ordinal);
            Assert.True(retryBlockedIndex >= 0 && recoveryExhaustedIndex > retryBlockedIndex,
                "The raw fixture must retain the intended recovery observations despite the earlier missing delivery proof.");
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5ReceiveRecoveryLivenessCannotOverrideMissingDelivery()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-canonical-receive-recovery-liveness-timeout-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-canonical-receive-recovery-liveness-timeout-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RECEIVE_RECOVERY_LIVENESS_TIMEOUT_BEFORE_RUNTIME_UNLOCK"] = "1";

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

            // Legacy route-only input does not prove Tuna delivery. Recovery observations
            // cannot replace that proof; classifier-only coverage uses explicit policy inputs.
            Assert.NotEqual(0, result.ExitCode);
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            const string prefix = "regular-v4-live-activation-off-on-off-256mb";
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("0", summary[$"{prefix}.retry_used"]);
            Assert.Equal("filetransfer_tuna_activation_delivery_unproven", summary[$"{prefix}.runtime_failure_candidate"]);
            Assert.Equal("(none)", summary[$"{prefix}.environmental_classification"]);
            Assert.Equal("0", summary[$"{prefix}.measurement_contaminated"]);
            Assert.False(Directory.Exists(Path.Combine(runRoot, prefix + "-rerun-1")));
            var retainedText = File.ReadAllText(Path.Combine(runRoot, prefix, "filetransfer-retained-log-slice.log"));
            Assert.Contains("event=session_liveness_timeout", retainedText, StringComparison.Ordinal);
            Assert.Contains("regular_v4_unproven_recovery_escalation", retainedText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5PersistentReceiveRecoveryCannotOverrideMissingDelivery()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-canonical-receive-recovery-exhaustion-rerun-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-canonical-receive-recovery-exhaustion-rerun-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RECEIVE_RECOVERY_EXHAUSTED_BEFORE_RUNTIME_UNLOCK"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RERUN_RECEIVE_RECOVERY_EXHAUSTED_BEFORE_RUNTIME_UNLOCK"] = "1";

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

            // Legacy route-only input does not prove Tuna delivery. Recovery observations
            // cannot replace that proof; classifier-only coverage uses explicit policy inputs.
            Assert.NotEqual(0, result.ExitCode);
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            const string prefix = "regular-v4-live-activation-off-on-off-256mb";
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("0", summary[$"{prefix}.retry_used"]);
            Assert.Equal("filetransfer_tuna_activation_delivery_unproven", summary[$"{prefix}.runtime_failure_candidate"]);
            Assert.Equal("(none)", summary[$"{prefix}.environmental_classification"]);
            Assert.Equal("0", summary[$"{prefix}.measurement_contaminated"]);
            Assert.False(Directory.Exists(Path.Combine(runRoot, prefix + "-rerun-1")));
            var retainedText = File.ReadAllText(Path.Combine(runRoot, prefix, "filetransfer-retained-log-slice.log"));
            var retryBlockedIndex = retainedText.IndexOf("event=tuna_activation_control_send_waiting_for_regular_v4_recovery", StringComparison.Ordinal);
            var recoveryExhaustedIndex = retainedText.IndexOf("event=nkn_bridge_receive_stall_recovery_failed", StringComparison.Ordinal);
            Assert.True(retryBlockedIndex >= 0 && recoveryExhaustedIndex > retryBlockedIndex,
                "The raw fixture must retain the intended recovery observations despite the earlier missing delivery proof.");
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularStartupPeerDisconnectRerunsCleanAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-regular-startup-peer-disconnect-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-regular-startup-peer-disconnect-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_STARTUP_PEER_DISCONNECT"] = "1";

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
                $"Expected regular-NKN startup peer disconnect to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("2", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.Contains("regular_v4_startup_local_only_no_ack", summary["regular-nkn-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("none", summary["regular-nkn-v4-64mb.acceptance_failure_class"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5PerformanceFirstAttemptKeepsClassWhenRerunSetupFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-performance-first-rerun-setup-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-performance-first-rerun-setup-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_GOODPUT_BPS"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_RERUN_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_RERUN_TRANSIENT_SETUP_PHASE"] = "activation_offer_answer";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_RERUN_TRANSIENT_SETUP_REASON"] = "activation_offer_sent_waiting_answer";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "active-tuna-v4-64mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected Phase 5 performance variance to be reported without failing release correctness.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("FAIL", summary["performance_verdict"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.retry_used"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.selected_attempt"]);
            Assert.Equal("performance", summary["active-tuna-v4-64mb.acceptance_failure_class"]);
            Assert.Contains("activation_offer_sent_waiting_answer", summary["active-tuna-v4-64mb.rerun_failure_reason"], StringComparison.Ordinal);
            Assert.DoesNotContain("scenario rerun setup failed; preserving first-attempt evidence", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5PerformanceFirstAttemptKeepsEvidenceWhenRerunHasWarningPolicyFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-performance-first-rerun-warning-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-performance-first-rerun-warning-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_GOODPUT_BPS"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_RERUN_WARNING_CAP_EXCESS"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "active-tuna-v4-64mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected Phase 5 to preserve performance-only first-attempt evidence when the goodput rerun introduces warning-policy noise.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("FAIL", summary["performance_verdict"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.retry_used"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.selected_attempt"]);
            Assert.Equal("performance", summary["active-tuna-v4-64mb.acceptance_failure_class"]);
            Assert.Contains("goodput regression exceeded", summary["active-tuna-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Contains("rerun introduced warning_policy failure", summary["active-tuna-v4-64mb.rerun_failure_reason"], StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5SingleScenarioApprovalTimeoutRerunsCleanAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-single-scenario-approval-timeout-rerun");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-single-scenario-approval-timeout-rerun");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ROUTE_CYCLE_SOURCE"] =
                await GenerateRouteCycleSourceAsync(repoRoot, Path.Combine(artifactRoot, "peers"), "independent_pairs");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "measured_terminal";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "terminal_before_accept";

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
                $"Expected fake single-scenario approval timeout to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["run_count"]);
            Assert.Equal("1", summary["expected_run_count"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.retry_used"]);
            Assert.Equal("2", summary["regular-v4-live-activation-off-on-off-256mb.selected_attempt"]);
            Assert.Contains("terminal_before_accept", summary["regular-v4-live-activation-off-on-off-256mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-v4-live-activation-off-on-off-256mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5HarnessCursorFailureRerunsCleanAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-harness-cursor-rerun");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-harness-cursor-rerun");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ROUTE_CYCLE_SOURCE"] =
                await GenerateRouteCycleSourceAsync(repoRoot, Path.Combine(artifactRoot, "peers"), "independent_pairs");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "harness_log_cursor";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "gui_smoke_log_cursor_gap";

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
                $"Expected a cursor-invalid first attempt to select only the clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.retry_used"]);
            Assert.Equal("2", summary["regular-v4-live-activation-off-on-off-256mb.selected_attempt"]);
            Assert.Contains("harness_log_cursor", summary["regular-v4-live-activation-off-on-off-256mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Contains("gui_smoke_log_cursor_gap", summary["regular-v4-live-activation-off-on-off-256mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-v4-live-activation-off-on-off-256mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5HarnessCursorFailureCannotBecomeFallbackAuthority()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-harness-cursor-no-rerun");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-harness-cursor-no-rerun");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "harness_log_cursor";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "gui_smoke_log_cursor_gap";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-SetupOnlyRerunLimit", "0",
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.retry_used"]);
            Assert.Equal("harness_log_cursor", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            Assert.Contains("gui_smoke_log_cursor_gap", summaryText, StringComparison.Ordinal);
            Assert.DoesNotContain("acceptance_failure_class=fallback_authority", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }
}


