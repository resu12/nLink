using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
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
            Assert.Equal("1", summary["manifest_present"]);
            Assert.Matches("^[0-9a-f]{64}$", summary["manifest_sha256"]);
            Assert.Equal("0", summary["current_runtime_proof"]);
            Assert.Equal("pass", summary["cursor_integrity_verdict"]);
            Assert.Equal("complete", summary["transfer_evidence_status"]);
            Assert.Equal("incomplete_proof", summary["runtime_correctness_verdict"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_missing_range_not_preserved_raw_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_missing_range_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_missing_range_not_preserved_recovered_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count"]);
            Assert.Equal("none", summary["regular_v4_frontier_repair_missing_range_preservation_verdict"]);
            Assert.Equal("0", summary["regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_raw_count"]);
            Assert.Equal("0", summary["regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_recovered_count"]);
            Assert.Equal("0", summary["regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_unresolved_count"]);
            Assert.Equal("none", summary["regular_v4_peer_silence_replay_missing_dispatch_range_preservation_verdict"]);
            Assert.Equal("none", summary["fallback_tail_bridge_recovery_generation_pairing_verdict"]);
            Assert.Equal("0", summary["fallback_tail_bridge_recovery_generation_mismatch_raw_count"]);
            Assert.Equal("0", summary["fallback_tail_bridge_recovery_generation_mismatch_distinct_count"]);
            Assert.Equal("0", summary["fallback_tail_bridge_recovery_generation_mismatch_recovered_count"]);
            Assert.Equal("0", summary["fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
            Assert.Equal("0", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count"]);
            Assert.Equal("0", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_recovered_count"]);
            Assert.Equal("0", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count"]);
            Assert.Equal("none", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.manifest_present"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.cursor_integrity_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.current_runtime_proof"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_missing_range_not_preserved_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_missing_range_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_missing_range_not_preserved_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_missing_range_preservation_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_unresolved_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_peer_silence_replay_missing_dispatch_range_preservation_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.fallback_receiver_state_churn_warning_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.fallback_receiver_state_churn_efficiency_class"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.fallback_tail_bridge_recovery_generation_pairing_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.fallback_tail_bridge_recovery_generation_mismatch_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.fallback_tail_bridge_recovery_generation_mismatch_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.fallback_tail_bridge_recovery_generation_mismatch_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict"]);
            var manifestPath = Path.Combine(runRoot, "route-acceptance-run-manifest.json");
            Assert.True(File.Exists(manifestPath), "Expected immutable route-acceptance manifest.");
            await using (var manifestStream = File.OpenRead(manifestPath))
            using (var manifest = await JsonDocument.ParseAsync(manifestStream))
            {
                Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.Equal("fake", manifest.RootElement.GetProperty("executionMode").GetString());
                Assert.Single(manifest.RootElement.GetProperty("scenarioAttempts").EnumerateArray());
            }
            var summaryJsonPath = Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json");
            Assert.True(File.Exists(summaryJsonPath), "Expected Phase 5 JSON summary.");
            await using (var summaryJsonStream = File.OpenRead(summaryJsonPath))
            using (var summaryJson = await JsonDocument.ParseAsync(summaryJsonStream))
            {
                var root = summaryJson.RootElement;
                Assert.Equal("none", root.GetProperty("fallbackTailBridgeRecoveryGenerationPairingVerdict").GetString());
                Assert.Equal(0, root.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount").GetInt32());
                Assert.Equal("none", root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict").GetString());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount").GetInt32());
                Assert.Equal("none", root.GetProperty("regularV4FrontierRepairMissingRangePreservationVerdict").GetString());
                Assert.Equal(0, root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount").GetInt32());
                Assert.Equal("none", root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict").GetString());

                var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
                Assert.Equal("none", run.GetProperty("fallbackTailBridgeRecoveryGenerationPairingVerdict").GetString());
                Assert.Equal(0, run.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount").GetInt32());
                Assert.Equal("none", run.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict").GetString());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount").GetInt32());
                Assert.Equal("none", run.GetProperty("regularV4FrontierRepairMissingRangePreservationVerdict").GetString());
                Assert.Equal(0, run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount").GetInt32());
                Assert.Equal("none", run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict").GetString());
            }
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
    public async Task RunFileTransferRouteAcceptance_Phase5PairingEvidencePropagatesToScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-pairing-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-pairing-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_FALLBACK_TAIL_BRIDGE_RECOVERY_GENERATION_PAIRING_SUMMARY_FIXTURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake pairing-summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            foreach (var reportName in new[]
                     {
                         "filetransfer-route-consistency-summary.txt",
                         "filetransfer-operator-verdict.txt"
                     })
            {
                var evidence = ReadArtifactReport(Path.Combine(runRoot, scenario), reportName);
                Assert.Equal("fail", evidence["fallback_tail_bridge_recovery_generation_pairing_verdict"]);
                Assert.Equal("4", evidence["fallback_tail_bridge_recovery_generation_mismatch_raw_count"]);
                Assert.Equal("1", evidence["fallback_tail_bridge_recovery_generation_mismatch_distinct_count"]);
                Assert.Equal("0", evidence["fallback_tail_bridge_recovery_generation_mismatch_recovered_count"]);
                Assert.Equal("1", evidence["fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
            }

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("fail", summary["fallback_tail_bridge_recovery_generation_pairing_verdict"]);
            Assert.Equal("4", summary["fallback_tail_bridge_recovery_generation_mismatch_raw_count"]);
            Assert.Equal("1", summary["fallback_tail_bridge_recovery_generation_mismatch_distinct_count"]);
            Assert.Equal("0", summary["fallback_tail_bridge_recovery_generation_mismatch_recovered_count"]);
            Assert.Equal("1", summary["fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.fallback_tail_bridge_recovery_generation_pairing_verdict"]);
            Assert.Equal("4", summary[$"{scenario}.fallback_tail_bridge_recovery_generation_mismatch_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.fallback_tail_bridge_recovery_generation_mismatch_distinct_count"]);
            Assert.Equal("0", summary[$"{scenario}.fallback_tail_bridge_recovery_generation_mismatch_recovered_count"]);
            Assert.Equal("1", summary[$"{scenario}.fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal("fail", root.GetProperty("fallbackTailBridgeRecoveryGenerationPairingVerdict").GetString());
            Assert.Equal(4, root.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount").GetInt32());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal("fail", run.GetProperty("fallbackTailBridgeRecoveryGenerationPairingVerdict").GetString());
            Assert.Equal(4, run.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("fallbackTailBridgeRecoveryGenerationMismatchUnresolvedCount").GetInt32());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_RegularV4ProjectionCountsReachScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-projection-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-projection-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_REGULAR_V4_FRONTIER_REPAIR_PROJECTION_SUMMARY_FIXTURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake projection-summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("3", summary["regular_v4_frontier_repair_missing_range_not_preserved_raw_count"]);
            Assert.Equal("2", summary["regular_v4_frontier_repair_missing_range_not_preserved_distinct_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_missing_range_not_preserved_recovered_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count"]);
            Assert.Equal("fail", summary["regular_v4_frontier_repair_missing_range_preservation_verdict"]);
            Assert.Equal("3", summary[$"{scenario}.regular_v4_frontier_repair_missing_range_not_preserved_raw_count"]);
            Assert.Equal("2", summary[$"{scenario}.regular_v4_frontier_repair_missing_range_not_preserved_distinct_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_missing_range_not_preserved_recovered_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.regular_v4_frontier_repair_missing_range_preservation_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(3, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRawCount").GetInt32());
            Assert.Equal(2, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedDistinctCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("regularV4FrontierRepairMissingRangePreservationVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(3, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRawCount").GetInt32());
            Assert.Equal(2, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedDistinctCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("regularV4FrontierRepairMissingRangePreservationVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_PeerSilenceReplayRangePreservationReachesScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-peer-silence-replay-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-peer-silence-replay-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_PEER_SILENCE_REPLAY_RANGE_PRESERVATION_SUMMARY_FIXTURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake peer-silence replay summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("filetransfer_regular_v4_frontier_repair_authority_granted", summary["first_proof_chain_failure_event"]);
            Assert.Equal("peer_silence_replay_range_rotated_after_missing_dispatch", summary["first_proof_chain_failure_reason"]);
            Assert.Equal("2898", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("1", summary["regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_raw_count"]);
            Assert.Equal("1", summary["regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_recovered_count"]);
            Assert.Equal("1", summary["regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_unresolved_count"]);
            Assert.Equal("fail", summary["regular_v4_peer_silence_replay_missing_dispatch_range_preservation_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_distinct_count"]);
            Assert.Equal("0", summary[$"{scenario}.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_recovered_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.regular_v4_peer_silence_replay_missing_dispatch_range_preservation_verdict"]);
            Assert.False(summary.ContainsKey($"{scenario}.gui_reported_failure_phase"));
            Assert.False(summary.ContainsKey($"{scenario}.gui_reported_failure_reason"));

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(1, root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal(1, run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangeNotPreservedUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("regularV4PeerSilenceReplayMissingDispatchRangePreservationVerdict").GetString());
            Assert.Equal(string.Empty, run.GetProperty("guiReportedFailurePhase").GetString());
            Assert.Equal(string.Empty, run.GetProperty("guiReportedFailureReason").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_SuccessorPressureInstallFailureReachesScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "regular_v4_frontier_repair_successor_pressure_install_missing";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-successor-pressure-install-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-successor-pressure-install-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_REGULAR_V4_SUCCESSOR_PRESSURE_INSTALL_SUMMARY_FIXTURE"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake successor-pressure summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("filetransfer_regular_v4_frontier_repair_settlement_rejected", summary["first_proof_chain_failure_event"]);
            Assert.Equal("regular_v4_frontier_repair_pressure_missing", summary["first_proof_chain_failure_reason"]);
            Assert.Equal("2792", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_successor_pressure_install_missing_raw_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_successor_pressure_install_missing_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_successor_pressure_install_missing_recovered_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_successor_pressure_install_missing_unresolved_count"]);
            Assert.Equal("fail", summary["regular_v4_frontier_repair_successor_pressure_install_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_successor_pressure_install_missing_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_successor_pressure_install_missing_distinct_count"]);
            Assert.Equal("0", summary[$"{scenario}.regular_v4_frontier_repair_successor_pressure_install_missing_recovered_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_successor_pressure_install_missing_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.regular_v4_frontier_repair_successor_pressure_install_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairSuccessorPressureInstallMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairSuccessorPressureInstallMissingDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("regularV4FrontierRepairSuccessorPressureInstallMissingRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairSuccessorPressureInstallMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("regularV4FrontierRepairSuccessorPressureInstallVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairSuccessorPressureInstallMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairSuccessorPressureInstallMissingDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("regularV4FrontierRepairSuccessorPressureInstallMissingRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairSuccessorPressureInstallMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("regularV4FrontierRepairSuccessorPressureInstallVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_ClosureWindowLivenessCountsReachScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-closure-liveness-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-closure-liveness-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RECOVERY_LEASE_CLOSURE_LIVENESS_SUMMARY_FIXTURE"] = "1";
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake closure-liveness propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var evidence = ReadArtifactReport(Path.Combine(runRoot, scenario), "filetransfer-operator-verdict.txt");
            Assert.Equal("1", evidence["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count"]);
            Assert.Equal("1", evidence["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_distinct_count"]);
            Assert.Equal("0", evidence["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_recovered_count"]);
            Assert.Equal("1", evidence["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count"]);
            Assert.Equal("fail", evidence["runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict"]);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("1", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count"]);
            Assert.Equal("1", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_recovered_count"]);
            Assert.Equal("1", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count"]);
            Assert.Equal("fail", summary["runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(1, root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessNotPreservedUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("runtimeUnlockRegularV4RecoveryLeaseClosureLivenessPreservationVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_FallbackTailUnblockRecheckCountsReachScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-fallback-tail-unblock-recheck-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-fallback-tail-unblock-recheck-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_FALLBACK_TAIL_UNBLOCK_RECHECK_SUMMARY_FIXTURE"] = "1";
            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", artifactRoot,
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.True(
                result.ExitCode == 0,
                $"Expected fake fallback-tail recheck propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var evidence = ReadArtifactReport(Path.Combine(runRoot, scenario), "filetransfer-operator-verdict.txt");
            Assert.Equal("1", evidence["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_raw_count"]);
            Assert.Equal("1", evidence["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_distinct_count"]);
            Assert.Equal("0", evidence["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_recovered_count"]);
            Assert.Equal("1", evidence["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_unresolved_count"]);
            Assert.Equal("fail", evidence["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_verdict"]);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_raw_count"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_recovered_count"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_unresolved_count"]);
            Assert.Equal("fail", summary["runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_verdict"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("runtimeUnlockRetryDispatchFallbackTailUnblockRecheckVerdict").GetString());
            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRetryDispatchFallbackTailUnblockRecheckMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("runtimeUnlockRetryDispatchFallbackTailUnblockRecheckVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_CurrentArtifactEquivalentIsIncompleteProofWithRetryCandidate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-current-artifact-equivalent");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-current-artifact-equivalent");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RETRY_DISPATCH_SUCCESSOR_ATTEMPT_MISMATCH"] = "1";

            var result = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", artifactRoot,
                    "-GoodputOnlyRerunLimit", "0",
                    "-SetupOnlyRerunLimit", "0",
                    "-EvidenceDryRun",
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                environment);

            Assert.NotEqual(0, result.ExitCode);
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            const string prefix = "regular-v4-live-activation-off-on-off-256mb";
            Assert.Equal("incomplete_proof", summary[$"{prefix}.acceptance_failure_class"]);
            Assert.Equal("incomplete_proof", summary[$"{prefix}.runtime_correctness_verdict"]);
            Assert.Equal("incomplete", summary[$"{prefix}.transfer_evidence_status"]);
            Assert.Equal("fail", summary[$"{prefix}.cursor_integrity_verdict"]);
            Assert.Equal("0", summary[$"{prefix}.current_runtime_proof"]);
            Assert.Equal("runtime_unlock_retry_dispatch_successor_attempt_mismatch", summary[$"{prefix}.runtime_failure_candidate"]);
            Assert.Equal("runtime_unlock_retry_dispatch_authority_rejected", summary[$"{prefix}.first_proof_chain_failure_event"]);
            Assert.Equal("retry_dispatch_successor_attempt_mismatch", summary[$"{prefix}.first_proof_chain_failure_reason"]);
            Assert.NotEqual("fallback_authority", summary[$"{prefix}.runtime_failure_candidate"]);
            Assert.Equal("evidence_dry_run", JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runRoot, "route-acceptance-run-manifest.json"))).RootElement.GetProperty("executionMode").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_EvidenceDryRunValidatesSuppliedCursorWithoutClaimingLiveProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var testRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var sourceArtifactRoot = Path.Combine(testRoot, "source");
        var sourceRunRoot = Path.Combine(sourceArtifactRoot, "phase5-evidence-source");
        var dryArtifactRoot = Path.Combine(testRoot, "dry");
        var dryRunRoot = Path.Combine(dryArtifactRoot, "phase5-evidence-dry-run");

        try
        {
            var source = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", sourceArtifactRoot,
                    "-GoodputOnlyRerunLimit", "0",
                    "-SetupOnlyRerunLimit", "0",
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                BuildFakeRouteAcceptanceEnvironment("phase5-evidence-source"));
            Assert.True(source.ExitCode == 0, $"Expected source fixture run to pass.{Environment.NewLine}{source.Stdout}{Environment.NewLine}{source.Stderr}");

            var dryEnvironment = BuildFakeRouteAcceptanceEnvironment("phase5-evidence-dry-run");
            var dry = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", "regular-v4-live-activation-off-on-off-256mb",
                    "-ArtifactRoot", dryArtifactRoot,
                    "-ExePath", Path.Combine(sourceRunRoot, "evidence-fixture", "nLink.fake.exe"),
                    "-EvidenceDryRun",
                    "-EvidenceDryRunFixtureRoot", sourceRunRoot,
                    "-GoodputOnlyRerunLimit", "0",
                    "-SetupOnlyRerunLimit", "0",
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                dryEnvironment);

            Assert.NotEqual(0, dry.ExitCode);
            var summary = ReadArtifactReport(dryRunRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("1", summary["manifest_present"]);
            Assert.Equal("pass", summary["cursor_integrity_verdict"]);
            Assert.Equal("0", summary["current_runtime_proof"]);
            Assert.Equal("incomplete_proof", summary["runtime_correctness_verdict"]);

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dryRunRoot, "route-acceptance-run-manifest.json")));
            var root = manifest.RootElement;
            Assert.Equal("evidence_dry_run", root.GetProperty("executionMode").GetString());
            Assert.Equal("PASS", root.GetProperty("validation").GetProperty("cursorIntegrityVerdict").GetString());
            Assert.False(root.GetProperty("validation").GetProperty("currentRuntimeProof").GetBoolean());
            Assert.True(root.GetProperty("runtime").GetProperty("hashStable").GetBoolean());
            Assert.True(root.GetProperty("harness").GetProperty("hashStable").GetBoolean());
            Assert.True(root.GetProperty("bridge").GetProperty("hashStable").GetBoolean());

            const string scenario = "regular-v4-live-activation-off-on-off-256mb";
            var sourceLedger = await File.ReadAllBytesAsync(Path.Combine(sourceRunRoot, scenario, "filetransfer-gui-log-ledger.json"));
            var importedLedger = await File.ReadAllBytesAsync(Path.Combine(dryRunRoot, scenario, "filetransfer-gui-log-ledger.json"));
            Assert.Equal(sourceLedger, importedLedger);
        }
        finally
        {
            TryDeleteDirectory(testRoot);
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
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_phase"));
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_reason"));
            Assert.Equal("regular_v4_frontier_repair", summary["regular-v4-live-activation-off-on-off-256mb.gui_reported_failure_phase"]);
            Assert.Equal("regular_v4_frontier_repair_liveness_settlement_missing", summary["regular-v4-live-activation-off-on-off-256mb.gui_reported_failure_reason"]);
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
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_phase"));
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_reason"));
            Assert.Equal("regular_v4_receive_recovery_rejected_before_runtime_unlock", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationClassifiesRetrySourcePublicationMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-runtime-unlock-source-publication-mismatch");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-runtime-unlock-source-publication-mismatch");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "runtime_unlock_retry";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "runtime_unlock_retry_source_publication_mismatch";

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
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.attempt_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.retry_used"]);
            Assert.Equal(
                "runtime_unlock_retry_source_publication_mismatch",
                summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationClassifiesFallbackRepairAsRuntimeFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-fallback-repair-runtime-failure");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-fallback-repair-runtime-failure");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "fallback_frontier_repair";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "fallback_frontier_repair_stalled";

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
            Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", summary["regular-v4-live-activation-off-on-off-256mb.route_sequence"]);
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_phase"));
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_reason"));
            Assert.Equal("fallback_frontier_repair_stalled", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            Assert.Contains("Tuna GUI runtime failure: phase=fallback_frontier_repair; reason=fallback_frontier_repair_stalled", summaryText, StringComparison.Ordinal);
            Assert.DoesNotContain("Tuna GUI setup failure: phase=fallback_frontier_repair", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_Phase5PreservesWorkerHandoffCandidateUnderIncompleteProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-worker-handoff-runtime-failure");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-worker-handoff-runtime-failure");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "outbound_sparse_worker_handoff";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "post_tuna_fallback_outbound_receive_worker_handoff_missing";

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

            const string prefix = "regular-v4-live-activation-off-on-off-256mb";
            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("FAIL", summary["verdict"]);
            Assert.Equal("post_tuna_fallback_v6", summary[$"{prefix}.final_route"]);
            Assert.Equal("post_tuna_fallback_outbound_receive_worker_handoff_missing", summary[$"{prefix}.acceptance_failure_class"]);
            Assert.Equal("incomplete_proof", summary[$"{prefix}.runtime_correctness_verdict"]);
            Assert.False(summary.ContainsKey($"{prefix}.setup_failure_phase"));
            Assert.False(summary.ContainsKey($"{prefix}.setup_failure_reason"));
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            Assert.Contains(
                "Tuna GUI runtime failure: phase=outbound_sparse_worker_handoff; reason=post_tuna_fallback_outbound_receive_worker_handoff_missing",
                summaryText,
                StringComparison.Ordinal);
            Assert.DoesNotContain("fallback_frontier_repair_stalled", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationClassifiesFallbackRepairBeforeMissingSecondTuna()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-fallback-repair-before-second-tuna");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-fallback-repair-before-second-tuna");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "fallback_frontier_repair";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "fallback_frontier_repair_stalled";

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
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_phase"));
            Assert.False(summary.ContainsKey("regular-v4-live-activation-off-on-off-256mb.setup_failure_reason"));
            Assert.Equal("fallback_frontier_repair_stalled", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            Assert.Contains("second_tuna_activation_not_proven", summaryText, StringComparison.Ordinal);
            Assert.Contains("Tuna GUI runtime failure: phase=fallback_frontier_repair; reason=fallback_frontier_repair_stalled", summaryText, StringComparison.Ordinal);
            Assert.DoesNotContain("Tuna GUI setup failure: phase=fallback_frontier_repair", summaryText, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunFileTransferRouteAcceptance_Phase5RegularActivationKeepsRouteInvariantBeforeFallbackRepair()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-route-invariant-before-fallback-repair");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-route-invariant-before-fallback-repair");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_ROUTE"] = "regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v6";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_FAILURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_PHASE"] = "fallback_frontier_repair";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_TRANSIENT_SETUP_REASON"] = "fallback_frontier_repair_stalled";

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
            Assert.Equal("file_tuna_v6", summary["regular-v4-live-activation-off-on-off-256mb.final_route"]);
            Assert.Equal("route_runtime", summary["regular-v4-live-activation-off-on-off-256mb.acceptance_failure_class"]);
            var summaryText = File.ReadAllText(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt"));
            Assert.Contains("active file_tuna_v6", summaryText, StringComparison.Ordinal);
            Assert.Contains("Tuna GUI runtime failure: phase=fallback_frontier_repair; reason=fallback_frontier_repair_stalled", summaryText, StringComparison.Ordinal);
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
