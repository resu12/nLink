using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "AcceptancePhase4")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferAcceptanceWrapperPhase4Tests : FileTransferOpsScriptsTestBase
{
    [Fact]
    public void RunFileTransferRouteAcceptance_AllowsOnlyCurrentFallbackWarningKinds()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1");
        var scriptText = File.ReadAllText(scriptPath);

        Assert.Contains("fallback_v6_send_timeout_churn", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallback_frontier_repair_churn", scriptText, StringComparison.Ordinal);
        Assert.Contains("fallback_receiver_state_churn", scriptText, StringComparison.Ordinal);
        Assert.Contains("recovered_post_tuna_fallback_bridge_clear", scriptText, StringComparison.Ordinal);
        Assert.Contains("'file_tuna_v4' { @('external_transport_churn', 'fallback_frontier_repair_churn', 'fallback_receiver_state_churn') }", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4FakeModeProducesPassArtifacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ROUTE_CYCLE_SOURCE"] =
                await GenerateRouteCycleSourceAsync(repoRoot, Path.Combine(artifactRoot, "peers"), "independent_pairs");
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake Phase 4 route acceptance to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            AssertRequiredPhase4RouteAcceptanceArtifacts(runRoot);
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("PASS", summary["performance_verdict"]);
            Assert.Equal("7", summary["run_count"]);
            Assert.Equal("0", summary["failure_count"]);
            Assert.Equal("0", summary["correctness_failure_count"]);
            Assert.Equal("0", summary["performance_failure_count"]);
            Assert.Equal("regular_nkn_v4_fast", summary["regular-nkn-v4-64mb.final_route"]);
            Assert.Equal("4", summary["regular-nkn-v4-64mb.protocol"]);
            Assert.Equal("file_tuna_v4", summary["active-tuna-v4-64mb.final_route"]);
            Assert.Equal("4", summary["active-tuna-v4-64mb.protocol"]);
            Assert.Equal("post_tuna_fallback_v6", summary["live-switch-off-helpee-64mb.final_route"]);
            Assert.Equal("6", summary["live-switch-off-helpee-64mb.protocol"]);
            Assert.Equal("post_tuna_fallback_v6", summary["live-switch-off-helper-64mb.final_route"]);
            Assert.Equal("6", summary["live-switch-off-helper-64mb.protocol"]);
            Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", summary["live-multi-toggle-off-on-off-64mb.selected_route_sequence"]);
            Assert.Equal("post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", summary["live-multi-toggle-off-on-off-64mb.live_route_epoch_route_changes"]);
            Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.selected_route_sequence"]);
            Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.live_route_epoch_route_changes"]);
            Assert.Equal("file_tuna_v4", summary["second-transfer-after-reactivation.final_route"]);
            Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4", summary["second-transfer-after-reactivation.selected_route_sequence"]);
            Assert.Equal("post_tuna_fallback_v6,file_tuna_v4", summary["second-transfer-after-reactivation.live_route_epoch_route_changes"]);
            Assert.Equal("0", summary["second-transfer-after-reactivation.retry_used"]);
            Assert.Equal("none", summary["second-transfer-after-reactivation.acceptance_failure_class"]);

            var secondSummaryJson = File.ReadAllText(Path.Combine(runRoot, "second-transfer-after-reactivation", "filetransfer-tuna-gui-summary.json"));
            Assert.Contains("\"secondTransfer\"", secondSummaryJson, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(runRoot, "second-transfer-after-reactivation", "filetransfer-second-transfer-retained-log-slice.log")));
            Assert.True(File.Exists(Path.Combine(runRoot, "second-transfer-after-reactivation", "second-transfer-analysis", "filetransfer-route-consistency-summary.txt")));
            Assert.True(File.Exists(Path.Combine(runRoot, "phase4-network-variance-note.md")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_ROUTE", "file_tuna_v6", "active file_tuna_v6")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_HARD_FAILURE", "1", "operator hard failures observed")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_SHA_FAIL", "1", "completion/integrity")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_MISSING_LIVE_PROOF", "1", "live route epoch sequence mismatch")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_TRANSPORT_ONLY_LIVE_PROOF", "1", "live route epoch sequence mismatch")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_LIVE_SWITCH_OFF_HELPEE_64MB_MISSING_LIVE_METADATA", "1", "live route epoch sequence mismatch")]
    [InlineData("NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_SECOND_TRANSFER_AFTER_REACTIVATION_SECOND_ROUTE", "post_tuna_fallback_v6", "second transfer route mismatch after reactivation")]
    public async Task RunFileTransferRouteAcceptance_Phase4FakeModeFailsStrictAcceptance(
        string environmentName,
        string environmentValue,
        string expectedFailure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-fail");
            environment[environmentName] = environmentValue;

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt")), $"Expected Phase 4 failure summary. STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            Assert.Contains("verdict=FAIL", summaryText, StringComparison.Ordinal);
            Assert.Contains(expectedFailure, summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4ScenarioExecutionFailureDoesNotAbortMatrix()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-execution-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-execution-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_EXECUTION_FAIL"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("7", summary["run_count"]);
            Assert.Equal("measured_accept_wait", summary["active-tuna-v4-64mb.setup_failure_phase"]);
            Assert.Equal("offer_sent_accept_not_enabled", summary["active-tuna-v4-64mb.setup_failure_reason"]);
            Assert.Contains("scenario execution failed", File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt")), StringComparison.Ordinal);
            Assert.Equal("post_tuna_fallback_v6", summary["live-switch-off-helpee-64mb.final_route"]);
            Assert.Equal("file_tuna_v4", summary["second-transfer-after-reactivation.final_route"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4GoodputRerunExecutionFailureIsScenarioFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-rerun-execution-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-rerun-execution-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_GOODPUT_BPS"] = "1000";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_RERUN_EXECUTION_FAIL"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("7", summary["run_count"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.retry_used"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.selected_attempt"]);
            Assert.Equal("file_tuna_v4", summary["active-tuna-v4-64mb.final_route"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.completed"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.sha_ok"]);
            Assert.False(summary.ContainsKey("active-tuna-v4-64mb.setup_failure_phase"));
            Assert.Contains("goodput regression exceeded", summary["active-tuna-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Contains("scenario rerun execution failed", summary["active-tuna-v4-64mb.rerun_failure_reason"], StringComparison.Ordinal);
            Assert.Contains("scenario rerun execution failed", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4GoodputOnlyRerunCanPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-goodput-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-goodput-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ROUTE_CYCLE_SOURCE"] =
                await GenerateRouteCycleSourceAsync(repoRoot, Path.Combine(artifactRoot, "peers"), "independent_pairs");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_GOODPUT_BPS"] = "1000";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_RERUN_GOODPUT_BPS"] = "1872542.416";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected goodput-only Phase 4 rerun to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("2", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.Contains("goodput regression exceeded", summary["regular-nkn-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4RerunSetupFailurePreservesFirstAttemptEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-rerun-setup-failure-preserves-first");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-rerun-setup-failure-preserves-first");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_GOODPUT_BPS"] = "1000";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_RERUN_EXECUTION_FAIL"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.EndsWith(
                Path.Combine("phase4-rerun-setup-failure-preserves-first", "active-tuna-v4-64mb"),
                summary["active-tuna-v4-64mb.artifact_dir"],
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("file_tuna_v4", summary["active-tuna-v4-64mb.final_route"]);
            Assert.Equal("4", summary["active-tuna-v4-64mb.protocol"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.completed"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.sha_ok"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.retry_used"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.selected_attempt"]);
            Assert.Contains("goodput regression exceeded", summary["active-tuna-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Contains("scenario rerun execution failed", summary["active-tuna-v4-64mb.rerun_failure_reason"], StringComparison.Ordinal);
            Assert.Contains("active-tuna-v4-64mb-rerun-1", summary["active-tuna-v4-64mb.rerun_artifact_dir"], StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4RegularExternalTransportChurnRerunsAsNetworkVariance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-regular-network-variance-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-regular-network-variance-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ROUTE_CYCLE_SOURCE"] =
                await GenerateRouteCycleSourceAsync(repoRoot, Path.Combine(artifactRoot, "peers"), "independent_pairs");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_WARNING"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected capped regular-NKN external churn to require and pass a clean rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("live_transport_paired_rerun", summary["network_variance_policy"]);
            Assert.Equal("capped_external_transport_churn_requires_clean_rerun", summary["regular_nkn_external_transport_warning_policy"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("2", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.Contains("regular_nkn_external_transport_churn", summary["regular-nkn-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("(none)", summary["regular-nkn-v4-64mb.environmental_classification"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4RegularProgressTimeoutRecoveryStormRerunsAsNetworkVariance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-regular-progress-timeout-recovery-storm-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-regular-progress-timeout-recovery-storm-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ROUTE_CYCLE_SOURCE"] =
                await GenerateRouteCycleSourceAsync(repoRoot, Path.Combine(artifactRoot, "peers"), "independent_pairs");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_PROGRESS_TIMEOUT_RECOVERY_STORM"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected regular-NKN progress-timeout recovery storm to require and pass a clean rerun.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("2", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.Contains("regular_v4_transport_recovery_storm", summary["regular-nkn-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Equal("(none)", summary["regular-nkn-v4-64mb.environmental_classification"]);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4PersistentRegularProgressTimeoutRecoveryStormFailsAsEnvironmental()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-regular-progress-timeout-recovery-storm-rerun-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-regular-progress-timeout-recovery-storm-rerun-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_PROGRESS_TIMEOUT_RECOVERY_STORM"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_RERUN_PROGRESS_TIMEOUT_RECOVERY_STORM"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("0", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.measurement_contaminated"]);
            Assert.Equal("regular_v4_transport_recovery_storm", summary["regular-nkn-v4-64mb.environmental_classification"]);
            Assert.Contains("regular_v4_transport_recovery_storm", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4StartupRecoveryIncompleteProofIsNotRetried()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-regular-startup-recovery-storm-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-regular-startup-recovery-storm-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ROUTE_CYCLE_SOURCE"] =
                await GenerateRouteCycleSourceAsync(repoRoot, Path.Combine(artifactRoot, "peers"), "independent_pairs");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_STARTUP_RECOVERY_STORM"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ScenarioName", "regular-nkn-v4-64mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            // The raw fixture fails strict proof admission before post-artifact policy hooks.
            // Environmental classification is covered independently by RecoveryRetryPolicyTests.
            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("0", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Contains("without complete proof artifacts", summaryText, StringComparison.Ordinal);
            Assert.DoesNotContain("Injected fake Phase 4 scenario execution failure after artifacts", summaryText, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb-rerun-1")));
            using var receipt = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
                runRoot, "regular-nkn-v4-64mb", "filetransfer-retained-analysis-attempt-receipt.json")));
            Assert.Equal("proof_incomplete", receipt.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, receipt.RootElement.GetProperty("exitCode").GetInt32());
            Assert.False(receipt.RootElement.GetProperty("completedProofArtifacts").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4StartupRecoveryIncompleteProofPrecedesPostArtifactFault()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-regular-startup-recovery-storm-execution-failure-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-regular-startup-recovery-storm-execution-failure-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ROUTE_CYCLE_SOURCE"] =
                await GenerateRouteCycleSourceAsync(repoRoot, Path.Combine(artifactRoot, "peers"), "independent_pairs");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_STARTUP_RECOVERY_STORM"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_POST_ARTIFACT_EXECUTION_FAIL"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ScenarioName", "regular-nkn-v4-64mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            // The raw fixture fails strict proof admission before post-artifact policy hooks.
            // Environmental classification is covered independently by RecoveryRetryPolicyTests.
            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("0", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Contains("without complete proof artifacts", summaryText, StringComparison.Ordinal);
            Assert.DoesNotContain("Injected fake Phase 4 scenario execution failure after artifacts", summaryText, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb-rerun-1")));
            using var receipt = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
                runRoot, "regular-nkn-v4-64mb", "filetransfer-retained-analysis-attempt-receipt.json")));
            Assert.Equal("proof_incomplete", receipt.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, receipt.RootElement.GetProperty("exitCode").GetInt32());
            Assert.False(receipt.RootElement.GetProperty("completedProofArtifacts").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4PersistentStartupRecoveryIncompleteProofIsNotRetried()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-regular-startup-recovery-storm-rerun-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-regular-startup-recovery-storm-rerun-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_STARTUP_RECOVERY_STORM"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_RERUN_STARTUP_RECOVERY_STORM"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ScenarioName", "regular-nkn-v4-64mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            // The raw fixture fails strict proof admission before post-artifact policy hooks.
            // Environmental classification is covered independently by RecoveryRetryPolicyTests.
            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("0", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Contains("without complete proof artifacts", summaryText, StringComparison.Ordinal);
            Assert.DoesNotContain("Injected fake Phase 4 scenario execution failure after artifacts", summaryText, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(runRoot, "regular-nkn-v4-64mb-rerun-1")));
            using var receipt = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
                runRoot, "regular-nkn-v4-64mb", "filetransfer-retained-analysis-attempt-receipt.json")));
            Assert.Equal("proof_incomplete", receipt.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, receipt.RootElement.GetProperty("exitCode").GetInt32());
            Assert.False(receipt.RootElement.GetProperty("completedProofArtifacts").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4PersistentRegularExternalTransportChurnFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-regular-network-variance-rerun-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-regular-network-variance-rerun-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_WARNING"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_RERUN_WARNING"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("0", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.measurement_contaminated"]);
            Assert.Equal("regular_v4_external_transport_churn", summary["regular-nkn-v4-64mb.environmental_classification"]);
            Assert.Contains("regular_nkn_external_transport_churn", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4ContaminatedActiveTunaRerunCanPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-contaminated-rerun-pass");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-contaminated-rerun-pass");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ROUTE_CYCLE_SOURCE"] =
                await GenerateRouteCycleSourceAsync(repoRoot, Path.Combine(artifactRoot, "peers"), "independent_pairs");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_CONTAMINATED_MEASUREMENT"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_GOODPUT_BPS"] = "1000";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_RERUN_GOODPUT_BPS"] = "5442738.550";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected contaminated active-Tuna measurement to rerun cleanly.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.retry_used"]);
            Assert.Equal("2", summary["active-tuna-v4-64mb.selected_attempt"]);
            Assert.Equal("0", summary["active-tuna-v4-64mb.measurement_contaminated"]);
            Assert.Contains("measurement contaminated", summary["active-tuna-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.Contains("active_tuna_v4_repair_pressure", summary["active-tuna-v4-64mb.first_failure_reason"], StringComparison.Ordinal);
            Assert.True(Directory.Exists(Path.Combine(runRoot, "active-tuna-v4-64mb-rerun-1")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4PersistentActiveTunaContaminationFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-contaminated-rerun-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-contaminated-rerun-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_CONTAMINATED_MEASUREMENT"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_RERUN_CONTAMINATED_MEASUREMENT"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_GOODPUT_BPS"] = "5442738.550";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_ACTIVE_TUNA_V4_64MB_RERUN_GOODPUT_BPS"] = "5442738.550";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ScenarioName", "active-tuna-v4-64mb",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("FAIL", summary["performance_verdict"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.retry_used"]);
            Assert.Equal("0", summary["active-tuna-v4-64mb.selected_attempt"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.measurement_contaminated"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.completed"]);
            Assert.Equal("1", summary["active-tuna-v4-64mb.sha_ok"]);
            Assert.Equal("performance", summary["active-tuna-v4-64mb.acceptance_failure_class"]);
            Assert.Contains("measurement contaminated", summaryText, StringComparison.Ordinal);
            Assert.Contains("active_tuna_v4_bridge_receive_recovery_window", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4SecondTransferUsesSecondSliceForContamination()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-second-transfer-second-slice-clean");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-second-transfer-second-slice-clean");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ROUTE_CYCLE_SOURCE"] =
                await GenerateRouteCycleSourceAsync(repoRoot, Path.Combine(artifactRoot, "peers"), "independent_pairs");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_SECOND_TRANSFER_AFTER_REACTIVATION_CONTAMINATED_MEASUREMENT"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected first-transfer contamination to stay out of second-transfer acceptance.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("PASS", summary["verdict"]);
            Assert.Equal("0", summary["second-transfer-after-reactivation.measurement_contaminated"]);
            Assert.Equal("(none)", summary["second-transfer-after-reactivation.measurement_contamination_reasons"]);
            Assert.Equal("0", summary["second-transfer-after-reactivation.failure_count"]);
            Assert.True(File.Exists(Path.Combine(runRoot, "second-transfer-after-reactivation", "second-transfer-analysis", "repair-reorder-summary.txt")));
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4SecondTransferContaminationStillFailsWhenSecondSliceDirty()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-second-transfer-second-slice-dirty");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-second-transfer-second-slice-dirty");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_SECOND_TRANSFER_AFTER_REACTIVATION_SECOND_CONTAMINATED_MEASUREMENT"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("1", summary["second-transfer-after-reactivation.measurement_contaminated"]);
            Assert.Contains("measurement contaminated", summaryText, StringComparison.Ordinal);
            Assert.Contains("active_tuna_v4_repair_pressure", summaryText, StringComparison.Ordinal);
            Assert.Contains("active_tuna_v4_bridge_receive_recovery_window", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase4PersistentGoodputRegressionFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase4-goodput-rerun-fail");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase4-goodput-rerun-fail");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_ROUTE_CYCLE_SOURCE"] =
                await GenerateRouteCycleSourceAsync(repoRoot, Path.Combine(artifactRoot, "peers"), "independent_pairs");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_NKN_V4_64MB_GOODPUT_BPS"] = "1000";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase4-ab-acceptance",
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase4-ab-acceptance-summary.txt"));
            Assert.Contains("verdict=FAIL", summaryText, StringComparison.Ordinal);
            Assert.Contains("goodput regression exceeded", summaryText, StringComparison.Ordinal);
            var summary = ReadArtifactReport(runRoot, "phase4-ab-acceptance-summary.txt");
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("FAIL", summary["performance_verdict"]);
            Assert.Equal("0", summary["correctness_failure_count"]);
            Assert.Equal("1", summary["performance_failure_count"]);
            Assert.Equal("1", summary["regular-nkn-v4-64mb.retry_used"]);
            Assert.Equal("0", summary["regular-nkn-v4-64mb.selected_attempt"]);
            Assert.Equal("performance", summary["regular-nkn-v4-64mb.acceptance_failure_class"]);
            var varianceNote = File.ReadAllText(Path.Combine(runRoot, "phase4-network-variance-note.md"));
            Assert.Contains("correctness_verdict=PASS", varianceNote, StringComparison.Ordinal);
            Assert.Contains("performance_verdict=FAIL", varianceNote, StringComparison.Ordinal);
            Assert.Contains("goodput regression exceeded", varianceNote, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

}
