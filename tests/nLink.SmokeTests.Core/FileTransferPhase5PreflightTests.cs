using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "AcceptancePhase5Fast")]
[Trait("AcceptancePhase5Suite", "Preflight")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPhase5PreflightTests : FileTransferAcceptancePhase5TestBase
{
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
            Assert.Equal(string.Empty, summary["preflight_failure_subreason"]);
            Assert.Equal("2", summary["preflight_attempt_count"]);
            Assert.Equal("1", summary["preflight_retry_used"]);
            Assert.Equal("nkn_bridge_bootstrap_not_ready", summary["preflight_first_failure_reason"]);
            Assert.Equal(string.Empty, summary["preflight_first_failure_subreason"]);
            Assert.Equal("0", summary["run_count"]);
            Assert.Equal("1", summary["failure_count"]);
            Assert.Equal("1", summary["manifest_present"]);
            Assert.Matches("^[0-9a-f]{64}$", summary["manifest_sha256"]);
            Assert.Equal("missing", summary["cursor_integrity_verdict"]);
            Assert.Equal("0", summary["current_runtime_proof"]);
            Assert.Equal("not_started", summary["transfer_evidence_status"]);
            Assert.Equal("incomplete_proof", summary["runtime_correctness_verdict"]);
            Assert.True(File.Exists(Path.Combine(runRoot, "route-acceptance-run-manifest.json")), "Expected preflight-only immutable evidence manifest.");
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
    public async Task RunFileTransferRouteAcceptance_Phase5PreflightBridgeBootstrapFailurePropagatesSubreason()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-preflight-subreason");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-preflight-subreason");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_BRIDGE_BOOTSTRAP_FAIL"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_FAILURE_REASON"] = "nkn_bridge_bootstrap_network_unready";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_FAILURE_SUBREASON"] = "helpee_invite_host_before_bridge_ready";

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
            Assert.Equal("preflight_bridge_bootstrap", summary["preflight_failure_class"]);
            Assert.Equal("nkn_bridge_bootstrap_network_unready", summary["preflight_failure_reason"]);
            Assert.Equal("helpee_invite_host_before_bridge_ready", summary["preflight_failure_subreason"]);
            Assert.Equal("helpee_invite_host_before_bridge_ready", summary["preflight_first_failure_subreason"]);
            Assert.Equal("3", summary["preflight_attempt_count"]);
            Assert.Equal("1", summary["preflight_retry_used"]);
            Assert.Equal("0", summary["run_count"]);
            Assert.Contains("phase5-preflight-bridge-ready: nkn_bridge_bootstrap_network_unready", summaryText, StringComparison.Ordinal);
            Assert.Contains("subreason=helpee_invite_host_before_bridge_ready", summaryText, StringComparison.Ordinal);

            var preflight = ReadArtifactReport(Path.Combine(runRoot, "preflight-bridge-ready"), "phase5-bridge-readiness-preflight-summary.txt");
            Assert.Equal("helpee_invite_host_before_bridge_ready", preflight["failure_subreason"]);

            var attempts = ReadArtifactReport(runRoot, "phase5-bridge-readiness-preflight-attempts.txt");
            Assert.Equal("3", attempts["attempt_count"]);
            Assert.Equal("1", attempts["retry_used"]);
            Assert.Equal("3", attempts["selected_attempt"]);
            Assert.Equal("helpee_invite_host_before_bridge_ready", attempts["final_failure_subreason"]);
            Assert.Equal("helpee_invite_host_before_bridge_ready", attempts["first_failure_subreason"]);
            Assert.True(File.Exists(Path.Combine(runRoot, "preflight-bridge-ready-attempt-2", "phase5-bridge-readiness-preflight-summary.txt")), "Expected second failed preflight attempt to be preserved.");
            Assert.False(Directory.Exists(Path.Combine(runRoot, "regular-v4-live-activation-off-on-off-256mb")), "Preflight failure should not consume the Phase 5 scenario matrix.");
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5PreflightBridgeHostReadyFailureGetsFinalSetupRetry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-preflight-host-ready-connect");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-preflight-host-ready-connect");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_BRIDGE_BOOTSTRAP_FAIL"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_FAILURE_REASON"] = "nkn_bridge_bootstrap_network_unready";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_FAILURE_SUBREASON"] = "helpee_bridge_host_ready_connect_failed";

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
            Assert.Equal("preflight_bridge_bootstrap", summary["preflight_failure_class"]);
            Assert.Equal("nkn_bridge_bootstrap_network_unready", summary["preflight_failure_reason"]);
            Assert.Equal("helpee_bridge_host_ready_connect_failed", summary["preflight_failure_subreason"]);
            Assert.Equal("helpee_bridge_host_ready_connect_failed", summary["preflight_first_failure_subreason"]);
            Assert.Equal("3", summary["preflight_attempt_count"]);
            Assert.Equal("1", summary["preflight_retry_used"]);
            Assert.Equal("0", summary["run_count"]);

            var attempts = ReadArtifactReport(runRoot, "phase5-bridge-readiness-preflight-attempts.txt");
            Assert.Equal("3", attempts["attempt_count"]);
            Assert.Equal("helpee_bridge_host_ready_connect_failed", attempts["final_failure_subreason"]);
            Assert.Equal("helpee_bridge_host_ready_connect_failed", attempts["first_failure_subreason"]);
            Assert.True(File.Exists(Path.Combine(runRoot, "preflight-bridge-ready-attempt-2", "phase5-bridge-readiness-preflight-summary.txt")), "Expected second failed preflight attempt to be preserved.");
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
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5FilteredBridgeHostReadyConnectFailureFailsBeforeScenarios()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-filtered-preflight-host-ready-connect-fails");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-filtered-preflight-host-ready-connect-fails");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_BRIDGE_BOOTSTRAP_FAIL"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_FAILURE_REASON"] = "nkn_bridge_bootstrap_network_unready";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE5_PREFLIGHT_FAILURE_SUBREASON"] = "helpee_bridge_host_ready_connect_failed";

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
            Assert.False(
                File.Exists(Path.Combine(runRoot, "phase5-bridge-readiness-preflight-quarantine.txt")),
                "Host-ready connect-denied failures are hard bridge-bootstrap failures after retries, not filtered-run quarantine evidence.");

            var attempts = ReadArtifactReport(runRoot, "phase5-bridge-readiness-preflight-attempts.txt");
            Assert.Equal("FAIL", attempts["verdict"]);
            Assert.Equal("3", attempts["attempt_count"]);
            Assert.Equal("helpee_bridge_host_ready_connect_failed", attempts["final_failure_subreason"]);
            Assert.Equal("helpee_bridge_host_ready_connect_failed", attempts["first_failure_subreason"]);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("FAIL", summary["preflight_verdict"]);
            Assert.Equal("preflight_bridge_bootstrap", summary["preflight_failure_class"]);
            Assert.Equal("nkn_bridge_bootstrap_network_unready", summary["preflight_failure_reason"]);
            Assert.Equal("helpee_bridge_host_ready_connect_failed", summary["preflight_failure_subreason"]);
            Assert.False(
                Directory.Exists(Path.Combine(runRoot, "regular-v4-live-activation-off-on-off-256mb")),
                "Host-ready connect-denied preflight failure should not consume the filtered target scenario.");
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
        Assert.Contains("helpee_invite_bootstrap_bridge_before_ready_retry", scriptText, StringComparison.Ordinal);
        Assert.Contains("helpee_invite_host_before_bridge_ready", scriptText, StringComparison.Ordinal);
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
    public void RunFileTransferRouteAcceptance_Phase5ScenarioBridgeBootstrapFailureIsSetupOnly()
    {
        var repoRoot = FindRepoRoot();
        var routeAcceptanceText = File.ReadAllText(Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));
        var guiSmokeText = File.ReadAllText(Path.Combine(repoRoot, "tools", "GuiSmoke-Windows.ps1"));
        var tunaWrapperText = File.ReadAllText(Path.Combine(repoRoot, "tools", "Run-FileTransferTunaGuiSmoke.ps1"));

        Assert.Contains("Get-RouteAcceptanceTunaGuiSetupBootstrapFailure", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("$result.setupFailurePhase = [string]$setupBootstrapFailure.Phase", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("not [string]::Equals([string]$result.setupFailurePhase, 'preflight_bridge_bootstrap'", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("Equals([string]$Result.setupFailurePhase, 'preflight_bridge_bootstrap'", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("$routeSelected = ConvertTo-RouteAcceptanceBool", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("$scenarioRuntimeEvidence", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("if ($scenarioRuntimeEvidence)", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("nkn_bridge_bootstrap_network_unready", routeAcceptanceText, StringComparison.Ordinal);
        Assert.Contains("helpee_bridge_host_ready_connect_failed", routeAcceptanceText, StringComparison.Ordinal);

        Assert.Contains("$phase = 'preflight_bridge_bootstrap'", guiSmokeText, StringComparison.Ordinal);
        Assert.Contains("$reason = $bridgeBootstrapSubreason", guiSmokeText, StringComparison.Ordinal);
        Assert.Contains("not [string]::Equals([string]$errorSummary.failurePhase, 'unknown'", tunaWrapperText, StringComparison.Ordinal);
        Assert.Contains("not [string]::Equals([string]$errorSummary.failureReason, 'unknown'", tunaWrapperText, StringComparison.Ordinal);
    }
}

