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
}

