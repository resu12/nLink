using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "AcceptancePhase5")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferAcceptanceWrapperPhase5Tests : FileTransferOpsScriptsTestBase
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5CanonicalReceiveRecoveryExhaustionRerunsAsEnvironmentalVariance()
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
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected canonical receive-recovery exhaustion to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.retry_used"]);
            Assert.Equal("2", summary["regular-v4-live-activation-off-on-off-256mb.selected_attempt"]);
            Assert.Contains("live_transport_receive_recovery_exhausted_before_runtime_unlock", summary["regular-v4-live-activation-off-on-off-256mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.live_route_epoch_proof_verdict"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.fallback_leg_authority_proof_verdict"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.bridge_liveness_integration_verdict"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-v4-live-activation-off-on-off-256mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5CanonicalReceiveRecoveryLivenessTimeoutRerunsAsEnvironmentalVariance()
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
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected canonical receive-recovery liveness timeout to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.retry_used"]);
            Assert.Equal("2", summary["regular-v4-live-activation-off-on-off-256mb.selected_attempt"]);
            Assert.Contains("live_transport_receive_recovery_exhausted_before_runtime_unlock", summary["regular-v4-live-activation-off-on-off-256mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.live_route_epoch_proof_verdict"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.fallback_leg_authority_proof_verdict"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.bridge_liveness_integration_verdict"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-v4-live-activation-off-on-off-256mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5PersistentCanonicalReceiveRecoveryExhaustionFailsAsEnvironmental()
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
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.retry_used"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.selected_attempt"]);
            Assert.Equal("environmental", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            Assert.Equal("live_transport_receive_recovery_exhausted_before_runtime_unlock", summary["regular-v4-live-activation-off-on-off-256mb.environmental_classification"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.measurement_contaminated"]);
            Assert.Contains("live_transport_receive_recovery_exhausted_before_runtime_unlock", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5TransientSetupFailureRerunsCleanAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-transient-setup-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-transient-setup-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSIENT_SETUP_PHASE"] = "helper_request_acceptance";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSIENT_SETUP_REASON"] = "helper_incoming_request_not_observed";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_HARD_FAILURE"] = "1";

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
                $"Expected transient setup failure to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["live-switch-off-helpee-64mb.retry_used"]);
            Assert.Equal("2", summary["live-switch-off-helpee-64mb.selected_attempt"]);
            Assert.Contains("helper_incoming_request_not_observed", summary["live-switch-off-helpee-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("none", summary["live-switch-off-helpee-64mb.acceptance_failure_class"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "live-switch-off-helpee-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5ListenerReadinessContradictionRerunsCleanAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-listener-readiness-contradiction-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-listener-readiness-contradiction-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSIENT_SETUP_PHASE"] = "preactivation_readiness";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSIENT_SETUP_REASON"] = "listener_ready_unavailable_contradiction";

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
                $"Expected listener readiness contradiction to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["live-switch-off-helpee-64mb.retry_used"]);
            Assert.Equal("2", summary["live-switch-off-helpee-64mb.selected_attempt"]);
            Assert.False(summary.ContainsKey("live-switch-off-helpee-64mb.setup_failure_phase"));
            Assert.False(summary.ContainsKey("live-switch-off-helpee-64mb.setup_failure_reason"));
            Assert.Contains("listener_ready_unavailable_contradiction", summary["live-switch-off-helpee-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("none", summary["live-switch-off-helpee-64mb.acceptance_failure_class"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "live-switch-off-helpee-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5ScenarioIsolationFailureRerunsCleanAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-scenario-isolation-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-scenario-isolation-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_SCENARIO_ISOLATION_FAIL"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_SCENARIO_ISOLATION_REASON"] = "listener_ready_unavailable_contradiction";

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
                $"Expected scenario-isolation preflight failure to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.retry_used"]);
            Assert.Equal("2", summary["active-tuna-v4-64mb.selected_attempt"]);
            Assert.False(summary.ContainsKey("active-tuna-v4-64mb.setup_failure_phase"));
            Assert.False(summary.ContainsKey("active-tuna-v4-64mb.setup_failure_reason"));
            Assert.Contains("phase=scenario_isolation", summary["active-tuna-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Contains("listener_ready_unavailable_contradiction", summary["active-tuna-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("1", summary["active-tuna-v4-64mb.scenario_isolation_preflight_count"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.scenario_isolation_clean_window_passed"]);
            Assert.Contains("scenario-isolation-active-tuna-v4-64mb-attempt-2", summary["active-tuna-v4-64mb.scenario_isolation_artifact_dirs"], StringComparison.Ordinal);
            Assert.Equal("none", summary["active-tuna-v4-64mb.acceptance_failure_class"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "scenario-isolation-active-tuna-v4-64mb-attempt-1")));
            Assert.True(Directory.Exists(Path.Combine(runRoot, "scenario-isolation-active-tuna-v4-64mb-attempt-2")));
            Assert.True(Directory.Exists(Path.Combine(runRoot, "active-tuna-v4-64mb-rerun-1")));

            var firstIsolation = ReadArtifactReport(
                Path.Combine(runRoot, "scenario-isolation-active-tuna-v4-64mb-attempt-1"),
                "phase5-scenario-isolation-summary.txt");
            Assert.Equal("FAIL", firstIsolation["verdict"]);
            Assert.Equal("listener_ready_unavailable_contradiction", firstIsolation["failure_reason"]);

            var selectedIsolation = ReadArtifactReport(
                Path.Combine(runRoot, "scenario-isolation-active-tuna-v4-64mb-attempt-2"),
                "phase5-scenario-isolation-summary.txt");
            Assert.Equal("PASS", selectedIsolation["verdict"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5HelpeeInviteReadinessTimeoutRerunsCleanAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-helpee-invite-readiness-timeout-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-helpee-invite-readiness-timeout-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSIENT_SETUP_PHASE"] = "preactivation_readiness";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSIENT_SETUP_REASON"] = "helpee_invite_readiness_timeout";

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
                $"Expected helpee invite readiness timeout to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["live-switch-off-helpee-64mb.retry_used"]);
            Assert.Equal("2", summary["live-switch-off-helpee-64mb.selected_attempt"]);
            Assert.False(summary.ContainsKey("live-switch-off-helpee-64mb.setup_failure_phase"));
            Assert.False(summary.ContainsKey("live-switch-off-helpee-64mb.setup_failure_reason"));
            Assert.Contains("helpee_invite_readiness_timeout", summary["live-switch-off-helpee-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("none", summary["live-switch-off-helpee-64mb.acceptance_failure_class"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "live-switch-off-helpee-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

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
    public async Task RunFileTransferRouteAcceptance_Phase5RegularV4ReceiveRecoveryUnprovenRerunsCleanAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-regular-v4-receive-recovery-unproven-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-regular-v4-receive-recovery-unproven-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "preactivation_readiness";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "regular_v4_receive_recovery_unproven";

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
                $"Expected regular-V4 receive-recovery defer to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.retry_used"]);
            Assert.Equal("2", summary["regular-v4-live-activation-off-on-off-256mb.selected_attempt"]);
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_phase"));
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_reason"));
            Assert.Contains("regular_v4_receive_recovery_unproven", summary["regular-v4-live-activation-off-on-off-256mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.scenario_isolation_preflight_count"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.scenario_isolation_clean_window_passed"]);
            Assert.Contains("scenario-isolation-regular-v4-live-activation-off-on-off-256mb-attempt-2", summary["regular-v4-live-activation-off-on-off-256mb.scenario_isolation_artifact_dirs"], StringComparison.Ordinal);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "scenario-isolation-regular-v4-live-activation-off-on-off-256mb-attempt-1")));
            Assert.True(Directory.Exists(Path.Combine(runRoot, "scenario-isolation-regular-v4-live-activation-off-on-off-256mb-attempt-2")));
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-v4-live-activation-off-on-off-256mb-rerun-1")));
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
    public async Task RunFileTransferRouteAcceptance_Phase5RegularPreTransferApprovalExpiryRerunsCleanAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-regular-pretransfer-expiry-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-regular-pretransfer-expiry-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_PRETRANSFER_SETUP_EXPIRED"] = "1";

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
                $"Expected regular-NKN pre-transfer approval expiry to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("2", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.False(summary.ContainsKey("regular-nkn-v4-64mb.setup_failure_phase"));
            Assert.False(summary.ContainsKey("regular-nkn-v4-64mb.setup_failure_reason"));
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
    public async Task RunFileTransferRouteAcceptance_Phase5CanonicalTerminalEvidenceTimeoutRerunsCleanAttempt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-canonical-terminal-evidence-timeout-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-canonical-terminal-evidence-timeout-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "unknown";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "Timed out waiting for live file-transfer terminal evidence.";

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
                $"Expected canonical terminal-evidence timeout to require and pass a clean Phase 5 rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.retry_used"]);
            Assert.Equal("2", summary["regular-v4-live-activation-off-on-off-256mb.selected_attempt"]);
            Assert.Contains("live file-transfer terminal evidence", summary["regular-v4-live-activation-off-on-off-256mb.first_failure_reason"], StringComparison.Ordinal);
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
    public async Task RunFileTransferRouteAcceptance_Phase5PersistentTransientSetupFailureFailsAsSetup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-transient-setup-rerun-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-transient-setup-rerun-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RERUN_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "activation_offer_not_observed";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RERUN_TRANSIENT_SETUP_REASON"] = "activation_offer_not_observed";

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

            Assert.NotEqual(0, result.ExitCode);
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.retry_used"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.selected_attempt"]);
            Assert.Equal("setup", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            Assert.Contains("activation_offer_not_observed", summary["regular-v4-live-activation-off-on-off-256mb.rerun_failure_reason"], StringComparison.Ordinal);
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
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_GOODPUT_BPS"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_RERUN_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_RERUN_TRANSIENT_SETUP_PHASE"] = "activation_offer_answer";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_RERUN_TRANSIENT_SETUP_REASON"] = "activation_offer_sent_waiting_answer";

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
                $"Expected Phase 5 performance variance to be reported without failing release correctness.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("FAIL", summary["performance_verdict"]);
            Assert.Equal("1", summary["live-switch-off-helpee-64mb.retry_used"]);
            Assert.Equal("1", summary["live-switch-off-helpee-64mb.selected_attempt"]);
            Assert.Equal("performance", summary["live-switch-off-helpee-64mb.acceptance_failure_class"]);
            Assert.Contains("activation_offer_sent_waiting_answer", summary["live-switch-off-helpee-64mb.rerun_failure_reason"], StringComparison.Ordinal);
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
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPER_64MB_GOODPUT_BPS"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPER_64MB_RERUN_WARNING_CAP_EXCESS"] = "1";

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
                $"Expected Phase 5 to preserve performance-only first-attempt evidence when the goodput rerun introduces warning-policy noise.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("FAIL", summary["performance_verdict"]);
            Assert.Equal("1", summary["live-switch-off-helper-64mb.retry_used"]);
            Assert.Equal("1", summary["live-switch-off-helper-64mb.selected_attempt"]);
            Assert.Equal("performance", summary["live-switch-off-helper-64mb.acceptance_failure_class"]);
            Assert.Contains("goodput regression exceeded", summary["live-switch-off-helper-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Contains("rerun introduced warning_policy failure", summary["live-switch-off-helper-64mb.rerun_failure_reason"], StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5SecondTransferUsesSplitProofWhenCombinedSliceIsNoisy()
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
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_SECOND_TRANSFER_AFTER_REACTIVATION_COMBINED_ROUTE_ANALYSIS_CONTAMINATED"] = "1";

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
                $"Expected split second-transfer proof to override noisy combined retained analysis.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("0", summary["second-transfer-after-reactivation.failure_count"]);
            Assert.Equal("none", summary["second-transfer-after-reactivation.acceptance_failure_class"]);
            Assert.Equal("pass", summary["second-transfer-after-reactivation.route_consistency_verdict"]);
            Assert.Equal("file_tuna_v4", summary["second-transfer-after-reactivation.final_route"]);
            Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4", summary["second-transfer-after-reactivation.route_sequence"]);
            Assert.True(File.Exists(Path.Combine(runRoot, "second-transfer-after-reactivation", "second-transfer-analysis", "filetransfer-route-consistency-summary.txt")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5FakeModeProducesPassArtifacts()
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
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                BuildFakeRouteAcceptanceEnvironment("phase5-pass"));

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake Phase 5 route acceptance to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            AssertRequiredPhase5RouteAcceptanceArtifacts(runRoot);
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("phase5", summary["acceptance_phase"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("PASS", summary["performance_verdict"]);
            Assert.Equal("6", summary["run_count"]);
            Assert.Equal("0", summary["failure_count"]);
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

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5PreflightBridgeBootstrapFailureFailsBeforeMatrix()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-preflight-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-preflight-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_BRIDGE_BOOTSTRAP_FAIL"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_FAILURE_REASON"] = "nkn_bridge_bootstrap_not_ready";

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

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt")), $"Expected Phase 5 preflight failure summary. STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            Assert.True(File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")), "Expected Phase 5 preflight JSON summary.");
            Assert.True(File.Exists(Path.Combine(runRoot, "preflight-bridge-ready", "phase5-bridge-readiness-preflight-summary.txt")), "Expected bridge preflight artifact summary.");
            Assert.True(File.Exists(Path.Combine(runRoot, "preflight-bridge-ready-attempt-1", "phase5-bridge-readiness-preflight-summary.txt")), "Expected first failed preflight attempt to be preserved.");
            Assert.True(File.Exists(Path.Combine(runRoot, "phase5-bridge-readiness-preflight-attempts.txt")), "Expected bridge preflight attempt summary.");
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("FAIL", summary["preflight_verdict"]);
            Assert.Equal("preflight_bridge_bootstrap", summary["preflight_failure_class"]);
            Assert.Equal("nkn_bridge_bootstrap_not_ready", summary["preflight_failure_reason"]);
            Assert.Equal("2", summary["preflight_attempt_count"]);
            Assert.Equal("1", summary["preflight_retry_used"]);
            Assert.Equal("nkn_bridge_bootstrap_not_ready", summary["preflight_first_failure_reason"]);
            Assert.Equal("0", summary["run_count"]);
            Assert.Equal("1", summary["failure_count"]);
            Assert.Contains("phase5-preflight-bridge-ready: nkn_bridge_bootstrap_not_ready", summaryText, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb")), "Preflight failure should not consume the Phase 5 scenario matrix.");
            Assert.False(Directory.Exists(Path.Combine(runRoot, "regular-v4-live-activation-off-on-off-256mb")), "Preflight failure should not create canonical stress artifacts.");
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5PreflightHelperWindowOwnershipFailureIsClassified()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-preflight-helper-window-ownership");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-preflight-helper-window-ownership");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_BRIDGE_BOOTSTRAP_FAIL"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_FAILURE_REASON"] = "helper_identity_window_ownership_mismatch";

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

            Assert.NotEqual(0, result.ExitCode);
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("FAIL", summary["preflight_verdict"]);
            Assert.Equal("preflight_helper_bootstrap_window_ownership", summary["preflight_failure_class"]);
            Assert.Equal("helper_identity_window_ownership_mismatch", summary["preflight_failure_reason"]);
            Assert.Equal("2", summary["preflight_attempt_count"]);
            Assert.Equal("1", summary["preflight_retry_used"]);
            Assert.True(File.Exists(Path.Combine(runRoot, "preflight-cleanup-after-attempt-1", "process-cleanup-preflight_after_attempt_1.txt")), "Expected preflight cleanup artifact before retry.");
            Assert.False(Directory.Exists(Path.Combine(runRoot, "regular-v4-live-activation-off-on-off-256mb")), "Preflight ownership failure should not consume the Phase 5 matrix.");
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5PreflightBootstrapFailureRetriesOnceThenRunsMatrix()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-preflight-retry-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-preflight-retry-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_BRIDGE_BOOTSTRAP_FAIL_ONCE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_FAILURE_REASON"] = "helpee_invite_readiness_timeout";

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
                $"Expected fake Phase 5 route acceptance to pass after one preflight retry.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            AssertRequiredPhase5RouteAcceptanceArtifacts(runRoot);
            Assert.True(File.Exists(Path.Combine(runRoot, "preflight-bridge-ready-attempt-1", "phase5-bridge-readiness-preflight-summary.txt")), "Expected first failed preflight attempt to be preserved.");
            Assert.True(File.Exists(Path.Combine(runRoot, "preflight-bridge-ready", "phase5-bridge-readiness-preflight-summary.txt")), "Expected selected retry preflight artifact summary.");
            var preflightAttempts = ReadArtifactReport(runRoot, "phase5-bridge-readiness-preflight-attempts.txt");
            Assert.Equal("PASS", preflightAttempts["verdict"]);
            Assert.Equal("2", preflightAttempts["attempt_count"]);
            Assert.Equal("1", preflightAttempts["retry_used"]);
            Assert.Equal("2", preflightAttempts["selected_attempt"]);
            Assert.Equal("helpee_invite_readiness_timeout", preflightAttempts["first_failure_reason"]);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("6", summary["run_count"]);
            Assert.Equal("0", summary["failure_count"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5FilteredHelpeeInviteBootstrapFailureIsQuarantined()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-filtered-preflight-quarantine");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-filtered-preflight-quarantine");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_BRIDGE_BOOTSTRAP_FAIL"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_FAILURE_REASON"] = "helpee_invite_connection_failed_before_ready";

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
                $"Expected filtered Phase 5 route acceptance to quarantine setup-only helpee bootstrap preflight failure and run the requested scenario.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            Assert.True(File.Exists(Path.Combine(runRoot, "phase5-bridge-readiness-preflight-quarantine.txt")), "Expected preflight quarantine summary.");
            var quarantine = ReadArtifactReport(runRoot, "phase5-bridge-readiness-preflight-quarantine.txt");
            Assert.Equal("QUARANTINED", quarantine["verdict"]);
            Assert.Equal("preflight_helpee_invite_bootstrap", quarantine["failure_class"]);
            Assert.Equal("helpee_invite_connection_failed_before_ready", quarantine["failure_reason"]);

            var preflightAttempts = ReadArtifactReport(runRoot, "phase5-bridge-readiness-preflight-attempts.txt");
            Assert.Equal("FAIL", preflightAttempts["verdict"]);
            Assert.Equal("2", preflightAttempts["attempt_count"]);
            Assert.Equal("1", preflightAttempts["retry_used"]);
            Assert.Equal("helpee_invite_connection_failed_before_ready", preflightAttempts["first_failure_reason"]);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["run_count"]);
            Assert.Equal("0", summary["failure_count"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-v4-live-activation-off-on-off-256mb")), "Expected filtered target scenario to run after quarantined setup-only preflight.");
            Assert.False(Directory.Exists(Path.Combine(runRoot, "active-tuna-v4-64mb")), "Filtered run should not execute unrelated Phase 5 scenarios.");
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5FilteredHelpeeInviteBootstrapMixedNetworkLabelIsQuarantined()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-filtered-preflight-mixed-helpee-bootstrap");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-filtered-preflight-mixed-helpee-bootstrap");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_BRIDGE_BOOTSTRAP_FAIL"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_FAILURE_REASON"] =
                "nkn_bridge_bootstrap_network_unready: role=helpee; failure_markers=Helpee hosting failed before invite readiness";

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
                $"Expected filtered Phase 5 route acceptance to quarantine mixed-label setup-only helpee bootstrap preflight failure and run the requested scenario.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var quarantine = ReadArtifactReport(runRoot, "phase5-bridge-readiness-preflight-quarantine.txt");
            Assert.Equal("QUARANTINED", quarantine["verdict"]);
            Assert.Equal("preflight_helpee_invite_bootstrap", quarantine["failure_class"]);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["run_count"]);
            Assert.Equal("0", summary["failure_count"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-v4-live-activation-off-on-off-256mb")), "Expected filtered target scenario to run after quarantined setup-only preflight.");
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public void RunFileTransferRouteAcceptance_Phase5ClassifiesHelpeeRequestFlowNotEnabledAsSetupBootstrap()
    {
        var repoRoot = FindRepoRoot();
        var scriptText = File.ReadAllText(Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));

        Assert.Contains("helpee_request_flow_not_enabled_after_identity_paste", scriptText, StringComparison.Ordinal);
        Assert.Contains("Timed out waiting for helpee connection entry mode", scriptText, StringComparison.Ordinal);
        Assert.Contains("nkn_bridge_bootstrap_network_unready", scriptText, StringComparison.Ordinal);
        Assert.Contains("RPC call failed", scriptText, StringComparison.Ordinal);
        Assert.Contains("Connect failed", scriptText, StringComparison.Ordinal);
        Assert.Contains("connect EACCES", scriptText, StringComparison.Ordinal);
        Assert.Contains("HostByAddressAsync failed", scriptText, StringComparison.Ordinal);
        Assert.Contains("preflight_helpee_invite_bootstrap", scriptText, StringComparison.Ordinal);
        Assert.Contains("preflight_bridge_bootstrap", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-Phase5BridgeReadinessPreflightRetryableFailure", scriptText, StringComparison.Ordinal);
        Assert.Contains("Test-Phase5BridgeReadinessPreflightQuarantinableFailure", scriptText, StringComparison.Ordinal);
        Assert.Contains("-not [string]::IsNullOrEmpty($retainedText)", scriptText, StringComparison.Ordinal);
    }

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

    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_BRIDGE_LIVENESS_FAIL", "1", "bridge_liveness", "bridge liveness integration verdict is fail")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_FALLBACK_AUTHORITY_METADATA_MISSING", "1", "fallback_authority", "fallback leg authority proof verdict is fail")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSPORT_ONLY_LIVE_PROOF", "1", "live_route_proof", "live route epoch sequence mismatch")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_MISSING_LIVE_METADATA", "1", "live_route_proof", "live route epoch sequence mismatch")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_ROUTE", "file_tuna_v6", "route_runtime", "active file_tuna_v6")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_SHA_FAIL", "1", "protocol_or_integrity", "completion/integrity")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_HARD_FAILURE", "1", "protocol_or_integrity", "operator hard failures observed")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_WARNING_CAP_EXCESS", "1", "warning_policy", "warning cap exceeded")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_SECOND_TRANSFER_AFTER_REACTIVATION_SECOND_ROUTE", "post_tuna_fallback_v6", "route_runtime", "second transfer route mismatch after reactivation")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_SECOND_TRANSFER_AFTER_REACTIVATION_SECOND_TRANSFER_NOT_STARTED", "1", "setup", "second transfer setup failure after reactivation")]
    public async Task RunFileTransferRouteAcceptance_Phase5FakeModeFailsStrictAcceptance(
        string environmentName,
        string environmentValue,
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
            var scenarioName = environmentName.Contains("REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB", StringComparison.Ordinal)
                ? "regular-v4-live-activation-off-on-off-256mb"
                : environmentName.Contains("LIVE_SWITCH_OFF_HELPEE_64MB", StringComparison.Ordinal)
                    ? "live-switch-off-helpee-64mb"
                    : environmentName.Contains("SECOND_TRANSFER_AFTER_REACTIVATION", StringComparison.Ordinal)
                        ? "second-transfer-after-reactivation"
                        : environmentName.Contains("REGULAR_NKN_V4_64MB", StringComparison.Ordinal)
                            ? "regular-nkn-v4-64mb"
                            : "active-tuna-v4-64mb";
            Assert.Equal(expectedFailureClass, summary[$"{scenarioName}.acceptance_failure_class"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }
}
