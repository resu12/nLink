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
[Trait("FileTransferEvidenceSuite", "OperationalLogIntegrity")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferPhase5SummaryWriterTests : FileTransferAcceptancePhase5TestBase
{
    [Fact]
    public async Task RunFileTransferRouteAcceptance_SubmissionBarrierDrainCountersReachScenarioAndRootSummaries()
    {
        var repoRoot = FindRepoRoot();
        var script = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));

        Assert.Contains(
            "$Result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount = ConvertTo-RouteAcceptanceInt",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount = [int]$Result.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount += [int]$runDto.fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "{0}.filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_unresolved_count={1}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainVerdict = if ($fileTransferBridgeRecoveryExecutionSubmissionBarrierDrainMissingUnresolvedCount -gt 0)",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_CompletedUnprovenSuccessorCountersReachScenarioAndRootSummaries()
    {
        var repoRoot = FindRepoRoot();
        var script = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));

        Assert.Contains(
            "$Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount = ConvertTo-RouteAcceptanceInt",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount = [int]$Result.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount += [int]$runDto.fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "{0}.filetransfer_receive_recovery_completed_unproven_successor_missing_unresolved_count={1}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferReceiveRecoveryCompletedUnprovenSuccessorVerdict = if ($fileTransferReceiveRecoveryCompletedUnprovenSuccessorMissingUnresolvedCount -gt 0)",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_ReceiveRecoveryOperationCountersReachScenarioAndRootSummaries()
    {
        var repoRoot = FindRepoRoot();
        var script = await File.ReadAllTextAsync(
            Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));

        Assert.Contains(
            "$Result.fileTransferReceiveRecoveryOperationRawCount = ConvertTo-RouteAcceptanceInt",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferReceiveRecoveryOperationRawCount = [int]$Result.fileTransferReceiveRecoveryOperationRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$fileTransferReceiveRecoveryOperationRawCount += [int]$runDto.fileTransferReceiveRecoveryOperationRawCount",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "filetransfer_receive_recovery_operation_unresolved_count={0}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "fileTransferReceiveRecoveryOperationVerdict = if ($fileTransferReceiveRecoveryOperationUnresolvedCount -gt 0)",
            script,
            StringComparison.Ordinal);
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
            Assert.Equal("1", summary["manifest_present"]);
            Assert.Matches("^[0-9a-f]{64}$", summary["manifest_sha256"]);
            Assert.Equal("0", summary["current_runtime_proof"]);
            Assert.Equal("pass", summary["cursor_integrity_verdict"]);
            Assert.Equal("complete", summary["transfer_evidence_status"]);
            Assert.Equal("incomplete_proof", summary["runtime_correctness_verdict"]);
            Assert.Equal(
                "0",
                summary["filetransfer_receive_recovery_completed_unproven_successor_missing_raw_count"]);
            Assert.Equal(
                "0",
                summary["filetransfer_receive_recovery_completed_unproven_successor_missing_distinct_count"]);
            Assert.Equal(
                "0",
                summary["filetransfer_receive_recovery_completed_unproven_successor_missing_recovered_count"]);
            Assert.Equal(
                "0",
                summary["filetransfer_receive_recovery_completed_unproven_successor_missing_unresolved_count"]);
            Assert.Equal(
                "none",
                summary["filetransfer_receive_recovery_completed_unproven_successor_verdict"]);
            Assert.Equal(
                "0",
                summary[
                    "regular-v4-live-activation-off-on-off-256mb." +
                    "filetransfer_receive_recovery_completed_unproven_successor_missing_unresolved_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_missing_range_not_preserved_raw_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_missing_range_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_missing_range_not_preserved_recovered_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count"]);
            Assert.Equal("none", summary["regular_v4_frontier_repair_missing_range_preservation_verdict"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_projection_reauthorization_missing_raw_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_projection_reauthorization_missing_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_projection_reauthorization_missing_recovered_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_projection_reauthorization_missing_unresolved_count"]);
            Assert.Equal("none", summary["regular_v4_frontier_repair_projection_reauthorization_verdict"]);
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
            Assert.Equal("0", summary["filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_raw_count"]);
            Assert.Equal("0", summary["filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_distinct_count"]);
            Assert.Equal("0", summary["filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_recovered_count"]);
            Assert.Equal("0", summary["filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_unresolved_count"]);
            Assert.Equal("none", summary["filetransfer_bridge_recovery_execution_soft_to_hard_handoff_verdict"]);
            Assert.Equal("0", summary["runtime_unlock_precommit_fallback_recovery_handoff_suppressed_raw_count"]);
            Assert.Equal("0", summary["runtime_unlock_precommit_fallback_recovery_handoff_suppressed_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_precommit_fallback_recovery_handoff_suppressed_recovered_count"]);
            Assert.Equal("0", summary["runtime_unlock_precommit_fallback_recovery_handoff_suppressed_unresolved_count"]);
            Assert.Equal("none", summary["runtime_unlock_precommit_fallback_recovery_handoff_suppression_verdict"]);
            Assert.Equal("0", summary["runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_raw_count"]);
            Assert.Equal("0", summary["runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_recovered_count"]);
            Assert.Equal("0", summary["runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_unresolved_count"]);
            Assert.Equal("none", summary["runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_verdict"]);
            Assert.Equal("1", summary["regular-v4-live-activation-off-on-off-256mb.manifest_present"]);
            Assert.Equal("pass", summary["regular-v4-live-activation-off-on-off-256mb.cursor_integrity_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.current_runtime_proof"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_unresolved_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.filetransfer_bridge_recovery_execution_soft_to_hard_handoff_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_missing_range_not_preserved_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_missing_range_not_preserved_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_missing_range_not_preserved_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_missing_range_preservation_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_projection_reauthorization_missing_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_projection_reauthorization_missing_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_projection_reauthorization_missing_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_projection_reauthorization_missing_unresolved_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.regular_v4_frontier_repair_projection_reauthorization_verdict"]);
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
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_unresolved_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_verdict"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_raw_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_distinct_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_recovered_count"]);
            Assert.Equal("0", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_unresolved_count"]);
            Assert.Equal("none", summary["regular-v4-live-activation-off-on-off-256mb.runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_verdict"]);
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
                Assert.Equal(0, root.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedUnresolvedCount").GetInt32());
                Assert.Equal("none", root.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressionVerdict").GetString());
                Assert.Equal(0, root.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidUnresolvedCount").GetInt32());
                Assert.Equal("none", root.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseVerdict").GetString());
                Assert.Equal(0, root.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount").GetInt32());
                Assert.Equal("none", root.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryVerdict").GetString());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount").GetInt32());
                Assert.Equal("none", root.GetProperty("regularV4FrontierRepairMissingRangePreservationVerdict").GetString());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingRawCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount").GetInt32());
                Assert.Equal(0, root.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount").GetInt32());
                Assert.Equal("none", root.GetProperty("regularV4FrontierRepairProjectionReauthorizationVerdict").GetString());
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
                Assert.Equal(0, run.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressedUnresolvedCount").GetInt32());
                Assert.Equal("none", run.GetProperty("runtimeUnlockPrecommitFallbackRecoveryHandoffSuppressionVerdict").GetString());
                Assert.Equal(0, run.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseInvalidUnresolvedCount").GetInt32());
                Assert.Equal("none", run.GetProperty("runtimeUnlockRetryDispatchFallbackPairedYieldSuccessorReleaseVerdict").GetString());
                Assert.Equal(0, run.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryMissingUnresolvedCount").GetInt32());
                Assert.Equal("none", run.GetProperty("runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryVerdict").GetString());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairMissingRangeNotPreservedUnresolvedCount").GetInt32());
                Assert.Equal("none", run.GetProperty("regularV4FrontierRepairMissingRangePreservationVerdict").GetString());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingRawCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount").GetInt32());
                Assert.Equal(0, run.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount").GetInt32());
                Assert.Equal("none", run.GetProperty("regularV4FrontierRepairProjectionReauthorizationVerdict").GetString());
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
    public async Task RunFileTransferRouteAcceptance_ProjectionReauthorizationFailureReachesScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "regular_v4_frontier_repair_projection_reauthorization_missing_after_recovery";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-projection-reauthorization-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-projection-reauthorization-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_REGULAR_V4_PROJECTION_REAUTHORIZATION_SUMMARY_FIXTURE"] = "1";

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
                $"Expected fake projection-reauthorization summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("filetransfer_regular_v4_frontier_repair_projection_rejected", summary["first_proof_chain_failure_event"]);
            Assert.Equal("fresh_projection_required_before_unrestricted_state", summary["first_proof_chain_failure_reason"]);
            Assert.Equal("4675", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("1438", summary["regular_v4_frontier_repair_projection_reauthorization_missing_raw_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_projection_reauthorization_missing_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_projection_reauthorization_missing_recovered_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_projection_reauthorization_missing_unresolved_count"]);
            Assert.Equal("fail", summary["regular_v4_frontier_repair_projection_reauthorization_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("1438", summary[$"{scenario}.regular_v4_frontier_repair_projection_reauthorization_missing_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_projection_reauthorization_missing_distinct_count"]);
            Assert.Equal("0", summary[$"{scenario}.regular_v4_frontier_repair_projection_reauthorization_missing_recovered_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_projection_reauthorization_missing_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.regular_v4_frontier_repair_projection_reauthorization_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(1438, root.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("regularV4FrontierRepairProjectionReauthorizationVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(1438, run.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairProjectionReauthorizationMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("regularV4FrontierRepairProjectionReauthorizationVerdict").GetString());
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
    public async Task RunFileTransferRouteAcceptance_RouteCommitSendDrainFailureReachesScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "runtime_unlock_regular_v4_route_commit_send_drain_missing";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-route-commit-send-drain-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-route-commit-send-drain-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_REGULAR_V4_ROUTE_COMMIT_SEND_DRAIN_SUMMARY_FIXTURE"] = "1";

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
                $"Expected fake route-commit send-drain summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("filetransfer_data_path_send_result", summary["first_proof_chain_failure_event"]);
            Assert.Equal(
                "retired_regular_v4_send_completed_after_tuna_owner_install",
                summary["first_proof_chain_failure_reason"]);
            Assert.Equal("3900", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("1315", summary["first_proof_chain_failure_writer_sequence"]);
            Assert.Equal("route_runtime", summary["semantic_failure_domain"]);
            Assert.Equal("6", summary["runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count"]);
            Assert.Equal("1", summary["runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_regular_v4_route_commit_send_drain_missing_recovered_count"]);
            Assert.Equal("1", summary["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
            Assert.Equal("fail", summary["runtime_unlock_regular_v4_route_commit_send_drain_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("route_runtime", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.runtime_correctness_verdict"]);
            Assert.Equal("1315", summary[$"{scenario}.first_proof_chain_failure_writer_sequence"]);
            Assert.Equal("route_runtime", summary[$"{scenario}.semantic_failure_domain"]);
            Assert.Equal("6", summary[$"{scenario}.runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count"]);
            Assert.Equal("0", summary[$"{scenario}.runtime_unlock_regular_v4_route_commit_send_drain_missing_recovered_count"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.runtime_unlock_regular_v4_route_commit_send_drain_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(6, root.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainVerdict").GetString());
            Assert.Equal(1315, root.GetProperty("firstProofChainFailureWriterSequence").GetInt64());
            Assert.Equal("route_runtime", root.GetProperty("semanticFailureDomain").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("route_runtime", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal(1315, run.GetProperty("firstProofChainFailureWriterSequence").GetInt64());
            Assert.Equal("route_runtime", run.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal(6, run.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainMissingDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainMissingRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("runtimeUnlockRegularV4RouteCommitSendDrainVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_PeerEpochReauthorizationFailureIsConclusiveWithIncompleteTransfer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-peer-epoch-reauthorization-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment(
                "phase5-peer-epoch-reauthorization-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_FALLBACK_TAIL_TRANSPORT_EPOCH_REAUTHORIZATION_SUMMARY_FIXTURE"] = "1";
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_SHA_FAIL"] = "1";

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

            Assert.NotEqual(0, result.ExitCode);
            Assert.True(
                File.Exists(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.txt")),
                $"Expected fake peer-epoch reauthorization failure summary.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("fail", summary["runtime_correctness_verdict"]);
            Assert.Equal("filetransfer_fallback_tail_local_yield_blocked", summary["first_proof_chain_failure_event"]);
            Assert.Equal("local_yield_transport_epoch_reauthorization_missing", summary["first_proof_chain_failure_reason"]);
            Assert.Equal("27225", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("13864", summary["first_proof_chain_failure_writer_sequence"]);
            Assert.Equal("route_runtime", summary["semantic_failure_domain"]);
            Assert.Equal("fail", summary["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict"]);
            Assert.Equal("1", summary["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count"]);
            Assert.Equal("1", summary["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count"]);
            Assert.Equal("0", summary["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count"]);
            Assert.Equal("1", summary["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("route_runtime", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("incomplete", summary[$"{scenario}.transfer_evidence_status"]);
            Assert.Equal("fail", summary[$"{scenario}.runtime_correctness_verdict"]);
            Assert.Equal("route_runtime", summary[$"{scenario}.semantic_failure_domain"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete", root.GetProperty("transferEvidenceStatus").GetString());
            Assert.Equal("fail", root.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal(13864, root.GetProperty("firstProofChainFailureWriterSequence").GetInt64());
            Assert.Equal("route_runtime", root.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal("fail", root.GetProperty("fallbackTailTransportEpochReauthorizationVerdict").GetString());
            Assert.Equal(1, root.GetProperty("fallbackTailTransportEpochReauthorizationMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("fallbackTailTransportEpochReauthorizationMissingDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("fallbackTailTransportEpochReauthorizationMissingRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("fallbackTailTransportEpochReauthorizationMissingUnresolvedCount").GetInt32());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("route_runtime", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal("incomplete", run.GetProperty("transferEvidenceStatus").GetString());
            Assert.Equal("fail", run.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal(13864, run.GetProperty("firstProofChainFailureWriterSequence").GetInt64());
            Assert.Equal("route_runtime", run.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal("fail", run.GetProperty("fallbackTailTransportEpochReauthorizationVerdict").GetString());
            Assert.Equal(1, run.GetProperty("fallbackTailTransportEpochReauthorizationMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("fallbackTailTransportEpochReauthorizationMissingDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("fallbackTailTransportEpochReauthorizationMissingRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("fallbackTailTransportEpochReauthorizationMissingUnresolvedCount").GetInt32());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_PreCommitProbeStartHandoffCountsReachScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "runtime_unlock_precommit_probe_start_handoff_missing";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-precommit-probe-start-handoff-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-precommit-probe-start-handoff-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_PRECOMMIT_PROBE_START_HANDOFF_SUMMARY_FIXTURE"] = "1";

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
                $"Expected fake precommit-probe summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("PASS", summary["correctness_verdict"]);
            Assert.Equal("incomplete_proof", summary["runtime_correctness_verdict"]);
            Assert.Equal("runtime_unlock_precommit_probe_start_timeout", summary["first_proof_chain_failure_event"]);
            Assert.Equal("10960", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("2647", summary["first_proof_chain_failure_writer_sequence"]);
            Assert.Equal("route_runtime", summary["semantic_failure_domain"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_post_grant_hold_count"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_post_grant_hold_raw_count"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_post_grant_hold_distinct_count"]);
            Assert.Equal("1", summary["runtime_unlock_retry_dispatch_post_grant_hold_recovered_count"]);
            Assert.Equal("0", summary["runtime_unlock_retry_dispatch_post_grant_hold_unresolved_count"]);
            Assert.Equal("pass", summary["runtime_unlock_retry_dispatch_post_grant_hold_verdict"]);
            Assert.Equal("1", summary["runtime_unlock_precommit_probe_start_handoff_missing_raw_count"]);
            Assert.Equal("1", summary["runtime_unlock_precommit_probe_start_handoff_missing_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_precommit_probe_start_handoff_missing_recovered_count"]);
            Assert.Equal("1", summary["runtime_unlock_precommit_probe_start_handoff_missing_unresolved_count"]);
            Assert.Equal("fail", summary["runtime_unlock_precommit_probe_start_handoff_verdict"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_retry_dispatch_post_grant_hold_recovered_count"]);
            Assert.Equal("0", summary[$"{scenario}.runtime_unlock_retry_dispatch_post_grant_hold_unresolved_count"]);
            Assert.Equal("pass", summary[$"{scenario}.runtime_unlock_retry_dispatch_post_grant_hold_verdict"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_precommit_probe_start_handoff_missing_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.runtime_unlock_precommit_probe_start_handoff_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("PASS", root.GetProperty("correctnessVerdict").GetString());
            Assert.Equal("incomplete_proof", root.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldDistinctCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldRecoveredCount").GetInt32());
            Assert.Equal(0, root.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldUnresolvedCount").GetInt32());
            Assert.Equal("pass", root.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldVerdict").GetString());
            Assert.Equal(1, root.GetProperty("runtimeUnlockPreCommitProbeStartHandoffMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockPreCommitProbeStartHandoffMissingDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("runtimeUnlockPreCommitProbeStartHandoffMissingRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockPreCommitProbeStartHandoffMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("runtimeUnlockPreCommitProbeStartHandoffVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldRecoveredCount").GetInt32());
            Assert.Equal(0, run.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldUnresolvedCount").GetInt32());
            Assert.Equal("pass", run.GetProperty("runtimeUnlockRetryDispatchPostGrantHoldVerdict").GetString());
            Assert.Equal(1, run.GetProperty("runtimeUnlockPreCommitProbeStartHandoffMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockPreCommitProbeStartHandoffMissingDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("runtimeUnlockPreCommitProbeStartHandoffMissingRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockPreCommitProbeStartHandoffMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("runtimeUnlockPreCommitProbeStartHandoffVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_SettlementDispositionFailureIsIncompleteProofInScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "regular_v4_frontier_repair_settlement_disposition_unverifiable";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-settlement-disposition-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-settlement-disposition-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_REGULAR_V4_SETTLEMENT_DISPOSITION_SUMMARY_FIXTURE"] = "1";

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
                $"Expected fake settlement-disposition summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("filetransfer_regular_v4_frontier_repair_settlement_accepted", summary["first_proof_chain_failure_event"]);
            Assert.Equal("accepted_settlement_disposition_evidence_missing", summary["first_proof_chain_failure_reason"]);
            Assert.Equal("5298", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_settlement_disposition_unverifiable_raw_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_settlement_disposition_unverifiable_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_settlement_disposition_unverifiable_recovered_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_settlement_disposition_unverifiable_unresolved_count"]);
            Assert.Equal("fail", summary["regular_v4_frontier_repair_settlement_disposition_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_settlement_disposition_unverifiable_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_settlement_disposition_unverifiable_distinct_count"]);
            Assert.Equal("0", summary[$"{scenario}.regular_v4_frontier_repair_settlement_disposition_unverifiable_recovered_count"]);
            Assert.Equal("1", summary[$"{scenario}.regular_v4_frontier_repair_settlement_disposition_unverifiable_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.regular_v4_frontier_repair_settlement_disposition_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairSettlementDispositionUnverifiableRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairSettlementDispositionUnverifiableDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("regularV4FrontierRepairSettlementDispositionUnverifiableRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairSettlementDispositionUnverifiableUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("regularV4FrontierRepairSettlementDispositionVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairSettlementDispositionUnverifiableRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairSettlementDispositionUnverifiableDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("regularV4FrontierRepairSettlementDispositionUnverifiableRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairSettlementDispositionUnverifiableUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("regularV4FrontierRepairSettlementDispositionVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_PressureAuthorityLifecycleSplitReachesScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "regular_v4_frontier_repair_pressure_authority_lifecycle_split";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-pressure-authority-lifecycle-split-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-pressure-authority-lifecycle-split-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_REGULAR_V4_PRESSURE_AUTHORITY_LIFECYCLE_SPLIT_SUMMARY_FIXTURE"] = "1";

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
                $"Expected fake pressure-lifecycle summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary["runtime_correctness_verdict"]);
            Assert.Equal("filetransfer_v4_receive_liveness_summary", summary["first_proof_chain_failure_event"]);
            Assert.Equal("coordinator_repair_authority_outlived_legacy_pressure_ttl", summary["first_proof_chain_failure_reason"]);
            Assert.Equal("4224", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_pressure_authority_lifecycle_split_raw_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_pressure_authority_lifecycle_split_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_pressure_authority_lifecycle_split_recovered_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_pressure_authority_lifecycle_split_unresolved_count"]);
            Assert.Equal("fail", summary["regular_v4_frontier_repair_pressure_authority_lifecycle_split_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("route_runtime", summary[$"{scenario}.semantic_failure_domain"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", root.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal("route_runtime", run.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("regularV4FrontierRepairPressureAuthorityLifecycleSplitVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_PressureRecoveryHandoffCountsReachScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "regular_v4_frontier_repair_pressure_recovery_handoff_missing";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-pressure-recovery-handoff-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment("phase5-pressure-recovery-handoff-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_REGULAR_V4_PRESSURE_RECOVERY_HANDOFF_SUMMARY_FIXTURE"] = "1";

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
                $"Expected fake pressure-recovery handoff summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary["runtime_correctness_verdict"]);
            Assert.Equal("filetransfer_recovery_authority_rejected", summary["first_proof_chain_failure_event"]);
            Assert.Equal("installed_repair_pressure_not_retired_before_receive_recovery", summary["first_proof_chain_failure_reason"]);
            Assert.Equal("1769", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("1287", summary["first_proof_chain_failure_writer_sequence"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_pressure_recovery_handoff_missing_raw_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_pressure_recovery_handoff_missing_distinct_count"]);
            Assert.Equal("0", summary["regular_v4_frontier_repair_pressure_recovery_handoff_missing_recovered_count"]);
            Assert.Equal("1", summary["regular_v4_frontier_repair_pressure_recovery_handoff_missing_unresolved_count"]);
            Assert.Equal("fail", summary["regular_v4_frontier_repair_pressure_recovery_handoff_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("route_runtime", summary[$"{scenario}.semantic_failure_domain"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", root.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffMissingDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffMissingRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal("route_runtime", run.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffMissingDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffMissingRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("regularV4FrontierRepairPressureRecoveryHandoffVerdict").GetString());
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
    public async Task RunFileTransferRouteAcceptance_BridgeRecoveryStartupPayloadIsolationReachesScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "filetransfer_bridge_recovery_execution_startup_payload_isolation_missing";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(
            repoRoot,
            "artifacts",
            "filetransfer-route-acceptance-test",
            Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(
            artifactRoot,
            "phase5-bridge-recovery-startup-payload-isolation-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment(
                "phase5-bridge-recovery-startup-payload-isolation-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_BRIDGE_RECOVERY_STARTUP_PAYLOAD_ISOLATION_SUMMARY_FIXTURE"] = "1";
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
                $"Expected fake bridge-recovery startup propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var evidence = ReadArtifactReport(Path.Combine(runRoot, scenario), "filetransfer-operator-verdict.txt");
            Assert.Equal(candidate, evidence["first_proof_chain_failure_class"]);
            Assert.Equal("nkn_bridge_recovery_execution_completed", evidence["first_proof_chain_failure_event"]);
            Assert.Equal(
                "bridge_recovery_execution_timeout_after_pre_ready_payload_submission",
                evidence["first_proof_chain_failure_reason"]);
            Assert.Equal("3258", evidence["first_proof_chain_failure_sequence"]);
            Assert.Equal("1597", evidence["first_proof_chain_failure_writer_sequence"]);
            Assert.Equal("1", evidence["filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_raw_count"]);
            Assert.Equal("1", evidence["filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_distinct_count"]);
            Assert.Equal("0", evidence["filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_recovered_count"]);
            Assert.Equal("1", evidence["filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_unresolved_count"]);
            Assert.Equal("fail", evidence["filetransfer_bridge_recovery_execution_startup_payload_isolation_verdict"]);

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("route_runtime", summary["semantic_failure_domain"]);
            Assert.Equal("1", summary["filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_raw_count"]);
            Assert.Equal("1", summary["filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_distinct_count"]);
            Assert.Equal("0", summary["filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_recovered_count"]);
            Assert.Equal("1", summary["filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_unresolved_count"]);
            Assert.Equal("fail", summary["filetransfer_bridge_recovery_execution_startup_payload_isolation_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("1", summary[$"{scenario}.filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.filetransfer_bridge_recovery_execution_startup_payload_isolation_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("route_runtime", root.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal(1, root.GetProperty("fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("fileTransferBridgeRecoveryExecutionStartupPayloadIsolationVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("route_runtime", run.GetProperty("semanticFailureDomain").GetString());
            Assert.Equal(1, run.GetProperty("fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("fileTransferBridgeRecoveryExecutionStartupPayloadIsolationMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("fileTransferBridgeRecoveryExecutionStartupPayloadIsolationVerdict").GetString());
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
        var tamperedDryArtifactRoot = Path.Combine(testRoot, "dry-tampered-writer-marker");
        var tamperedDryRunRoot = Path.Combine(tamperedDryArtifactRoot, "phase5-evidence-dry-run-tampered-writer-marker");

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
            Assert.Equal("3", summary["writer_evidence_version"]);
            Assert.Equal("pass", summary["writer_integrity_verdict"]);
            Assert.Equal("0", summary["writer_gap_count"]);
            Assert.Equal("0", summary["writer_duplicate_count"]);
            Assert.Equal("0", summary["writer_identity_mismatch_count"]);
            Assert.Equal("0", summary["writer_write_failure_count"]);
            Assert.Equal("0", summary["writer_chronology_inversion_count"]);
            Assert.Equal("0", summary["writer_chronology_collision_count"]);
            Assert.Equal("pass", summary["writer_chronology_verdict"]);
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
            var ledgerEvidence = root.GetProperty("scenarioAttempts")[0].GetProperty("logLedger");
            Assert.Equal(3, ledgerEvidence.GetProperty("writerEvidenceVersion").GetInt64());
            Assert.Equal("PASS", ledgerEvidence.GetProperty("writerIntegrityVerdict").GetString());
            Assert.Equal("pass", ledgerEvidence.GetProperty("producerWriterIntegrityVerdict").GetString());

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(dryRunRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var summaryRoot = summaryJson.RootElement;
            Assert.Equal(3, summaryRoot.GetProperty("writerEvidenceVersion").GetInt64());
            Assert.Equal(0, summaryRoot.GetProperty("writerChronologyInversionCount").GetInt64());
            Assert.Equal(0, summaryRoot.GetProperty("writerChronologyCollisionCount").GetInt64());
            Assert.Equal("pass", summaryRoot.GetProperty("writerChronologyVerdict").GetString());
            var summaryRun = Assert.Single(summaryRoot.GetProperty("runs").EnumerateArray());
            Assert.Equal(3, summaryRun.GetProperty("writerEvidenceVersion").GetInt64());
            Assert.Equal(0, summaryRun.GetProperty("writerChronologyInversionCount").GetInt64());
            Assert.Equal(0, summaryRun.GetProperty("writerChronologyCollisionCount").GetInt64());
            Assert.Equal("pass", summaryRun.GetProperty("writerChronologyVerdict").GetString());

            const string scenario = "regular-v4-live-activation-off-on-off-256mb";
            var sourceLedger = await File.ReadAllBytesAsync(Path.Combine(sourceRunRoot, scenario, "filetransfer-gui-log-ledger.json"));
            var importedLedger = await File.ReadAllBytesAsync(Path.Combine(dryRunRoot, scenario, "filetransfer-gui-log-ledger.json"));
            Assert.Equal(sourceLedger, importedLedger);

            var sourceWriterMarkerPath = Path.Combine(
                sourceRunRoot,
                scenario,
                "filetransfer-gui-log-writer-failure-markers.log");
            await File.WriteAllTextAsync(
                sourceWriterMarkerPath,
                "event=operational_log_write_failed; log_writer_id=tampered; log_writer_sequence=2; failure_code=io_error" + Environment.NewLine);

            var tamperedDryEnvironment = BuildFakeRouteAcceptanceEnvironment("phase5-evidence-dry-run-tampered-writer-marker");
            var tamperedDry = await RunPowerShellFileAsync(
                repoRoot,
                Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"),
                [
                    "-MatrixMode", "phase5-analyzer-gui-acceptance",
                    "-ScenarioName", scenario,
                    "-ArtifactRoot", tamperedDryArtifactRoot,
                    "-ExePath", Path.Combine(sourceRunRoot, "evidence-fixture", "nLink.fake.exe"),
                    "-EvidenceDryRun",
                    "-EvidenceDryRunFixtureRoot", sourceRunRoot,
                    "-GoodputOnlyRerunLimit", "0",
                    "-SetupOnlyRerunLimit", "0",
                    "-TimeoutSeconds", "30",
                    "-ProgressTimeoutSeconds", "30"
                ],
                tamperedDryEnvironment);

            Assert.NotEqual(0, tamperedDry.ExitCode);
            var tamperedSummary = ReadArtifactReport(
                tamperedDryRunRoot,
                "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal("fail", tamperedSummary["writer_integrity_verdict"]);
            Assert.Equal("fail", tamperedSummary["cursor_integrity_verdict"]);
            Assert.Equal("0", tamperedSummary["current_runtime_proof"]);

            using var tamperedManifest = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(tamperedDryRunRoot, "route-acceptance-run-manifest.json")));
            var tamperedManifestRoot = tamperedManifest.RootElement;
            Assert.Equal(
                "FAIL",
                tamperedManifestRoot.GetProperty("validation").GetProperty("cursorIntegrityVerdict").GetString());
            var tamperedLedgerEvidence = tamperedManifestRoot
                .GetProperty("scenarioAttempts")[0]
                .GetProperty("logLedger");
            Assert.Equal("FAIL", tamperedLedgerEvidence.GetProperty("writerIntegrityVerdict").GetString());
            Assert.Equal("pass", tamperedLedgerEvidence.GetProperty("producerWriterIntegrityVerdict").GetString());
            Assert.Contains(
                tamperedLedgerEvidence.GetProperty("writerIntegrityReasons").EnumerateArray(),
                reason => reason.GetString() == "cursor_writer_failure_marker_sha256_mismatch");
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

    [Fact]
    public async Task RunFileTransferRouteAcceptance_RetryDispatchInstallGrantEvidenceFailureReachesScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "runtime_unlock_retry_dispatch_install_grant_evidence_incomplete";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-retry-dispatch-install-grant-evidence-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment(
                "phase5-retry-dispatch-install-grant-evidence-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_RETRY_DISPATCH_INSTALL_GRANT_EVIDENCE_SUMMARY_FIXTURE"] = "1";

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
                $"Expected fake retry-dispatch install-grant evidence summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("runtime_unlock_retry_dispatch_authority_granted", summary["first_proof_chain_failure_event"]);
            Assert.Equal(
                "retry_dispatch_grant_request_active_predecessor_supersession_missing",
                summary["first_proof_chain_failure_reason"]);
            Assert.Equal("5510", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("2", summary["runtime_unlock_retry_dispatch_authority_granted_raw_count"]);
            Assert.Equal("0", summary["runtime_unlock_retry_dispatch_authority_granted_count"]);
            Assert.Equal("2", summary["runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_raw_count"]);
            Assert.Equal("2", summary["runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_recovered_count"]);
            Assert.Equal("2", summary["runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_unresolved_count"]);
            Assert.Equal("fail", summary["runtime_unlock_retry_dispatch_install_grant_evidence_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.runtime_correctness_verdict"]);
            Assert.Equal("2", summary[$"{scenario}.runtime_unlock_retry_dispatch_authority_granted_raw_count"]);
            Assert.Equal("0", summary[$"{scenario}.runtime_unlock_retry_dispatch_authority_granted_count"]);
            Assert.Equal("2", summary[$"{scenario}.runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_raw_count"]);
            Assert.Equal("2", summary[$"{scenario}.runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_distinct_count"]);
            Assert.Equal("0", summary[$"{scenario}.runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_recovered_count"]);
            Assert.Equal("2", summary[$"{scenario}.runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.runtime_unlock_retry_dispatch_install_grant_evidence_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(2, root.GetProperty("runtimeUnlockRetryDispatchAuthorityGrantedRawCount").GetInt32());
            Assert.Equal(0, root.GetProperty("runtimeUnlockRetryDispatchAuthorityGrantedCount").GetInt32());
            Assert.Equal(2, root.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRawCount").GetInt32());
            Assert.Equal(2, root.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRecoveredCount").GetInt32());
            Assert.Equal(2, root.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal(2, run.GetProperty("runtimeUnlockRetryDispatchAuthorityGrantedRawCount").GetInt32());
            Assert.Equal(0, run.GetProperty("runtimeUnlockRetryDispatchAuthorityGrantedCount").GetInt32());
            Assert.Equal(2, run.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRawCount").GetInt32());
            Assert.Equal(2, run.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteRecoveredCount").GetInt32());
            Assert.Equal(2, run.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceIncompleteUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("runtimeUnlockRetryDispatchInstallGrantEvidenceVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public async Task RunFileTransferRouteAcceptance_PostTunaFallbackTransportProofHandoffFailureReachesScenarioAndRootSummaries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string scenario = "regular-v4-live-activation-off-on-off-256mb";
        const string candidate = "runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing";
        var repoRoot = FindRepoRoot();
        var artifactRoot = Path.Combine(repoRoot, "artifacts", "filetransfer-route-acceptance-test", Guid.NewGuid().ToString("N"));
        var runRoot = Path.Combine(artifactRoot, "phase5-post-tuna-fallback-transport-proof-handoff-summary-propagation");

        try
        {
            var environment = BuildFakeRouteAcceptanceEnvironment(
                "phase5-post-tuna-fallback-transport-proof-handoff-summary-propagation");
            environment["NLINK_FILETRANSFER_ROUTE_ACCEPTANCE_FAKE_PHASE4_REGULAR_V4_LIVE_ACTIVATION_OFF_ON_OFF_256MB_POST_TUNA_FALLBACK_TRANSPORT_PROOF_HANDOFF_SUMMARY_FIXTURE"] = "1";

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
                $"Expected fake post-Tuna fallback transport-proof handoff summary propagation run to pass.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.Stdout}{Environment.NewLine}STDERR:{Environment.NewLine}{result.Stderr}");

            var summary = ReadArtifactReport(runRoot, "phase5-analyzer-gui-acceptance-summary.txt");
            Assert.Equal(candidate, summary["runtime_failure_candidate"]);
            Assert.Equal("tuna_acceleration_timeline", summary["first_proof_chain_failure_event"]);
            Assert.Equal("scheduled_runtime_unlock_handoff_not_started", summary["first_proof_chain_failure_reason"]);
            Assert.Equal("22360", summary["first_proof_chain_failure_sequence"]);
            Assert.Equal("1", summary["runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_raw_count"]);
            Assert.Equal("1", summary["runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_distinct_count"]);
            Assert.Equal("0", summary["runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_recovered_count"]);
            Assert.Equal("1", summary["runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_unresolved_count"]);
            Assert.Equal("fail", summary["runtime_unlock_post_tuna_fallback_transport_proof_handoff_verdict"]);
            Assert.Equal(candidate, summary[$"{scenario}.runtime_failure_candidate"]);
            Assert.Equal("route_runtime", summary[$"{scenario}.acceptance_failure_class"]);
            Assert.Equal("incomplete_proof", summary[$"{scenario}.runtime_correctness_verdict"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_raw_count"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_distinct_count"]);
            Assert.Equal("0", summary[$"{scenario}.runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_recovered_count"]);
            Assert.Equal("1", summary[$"{scenario}.runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_unresolved_count"]);
            Assert.Equal("fail", summary[$"{scenario}.runtime_unlock_post_tuna_fallback_transport_proof_handoff_verdict"]);

            using var summaryJson = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(runRoot, "phase5-analyzer-gui-acceptance-summary.json")));
            var root = summaryJson.RootElement;
            Assert.Equal(candidate, root.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal(1, root.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRawCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffMissingDistinctCount").GetInt32());
            Assert.Equal(0, root.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRecoveredCount").GetInt32());
            Assert.Equal(1, root.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", root.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffVerdict").GetString());

            var run = Assert.Single(root.GetProperty("runs").EnumerateArray());
            Assert.Equal(candidate, run.GetProperty("runtimeFailureCandidate").GetString());
            Assert.Equal("route_runtime", run.GetProperty("acceptanceFailureClass").GetString());
            Assert.Equal("incomplete_proof", run.GetProperty("runtimeCorrectnessVerdict").GetString());
            Assert.Equal(1, run.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRawCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffMissingDistinctCount").GetInt32());
            Assert.Equal(0, run.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffMissingRecoveredCount").GetInt32());
            Assert.Equal(1, run.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffMissingUnresolvedCount").GetInt32());
            Assert.Equal("fail", run.GetProperty("runtimeUnlockPostTunaFallbackTransportProofHandoffVerdict").GetString());
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [Fact]
    public void RunFileTransferRouteAcceptance_LiveRetainedAnalyzerScopesCompactModeToChildProcess()
    {
        var repoRoot = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(repoRoot, "tools", "Run-FileTransferRouteAcceptance.ps1"));

        Assert.Contains(
            "$startInfo.Environment['NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT'] = '1'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "if ($script:RouteAcceptanceExecutionMode -eq 'live')",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$oldRetainedAnalysisCompact = $env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT = '1'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Remove-Item Env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT -ErrorAction SilentlyContinue",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT = $oldRetainedAnalysisCompact",
            script,
            StringComparison.Ordinal);

        var childScopeAssignments = script.Split(
            "$env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT = '1'",
            StringSplitOptions.None).Length - 1;
        Assert.Equal(2, childScopeAssignments);
    }
}
