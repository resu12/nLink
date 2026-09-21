using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "RetainedFallback")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferRetainedFallbackAnalyzerTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    [Trait("RetainedFallbackSuite", "FrontierRequestDispatch")]
    [Trait("Category", "Smoke")]
    public void V4SenderFallbackRepairLogsUseScheduledDataPathAuthority()
    {
        var repoRoot = FindRepoRoot();
        var servicePath = Path.Combine(repoRoot, "src", "nLink.Core", "FileTransfer", "SessionFileTransferService.cs");
        var senderPath = Path.Combine(repoRoot, "src", "nLink.Core", "FileTransfer", "SessionFileTransferService.PullTransferSessionV4.cs");
        var serviceSource = File.ReadAllText(servicePath);
        var senderSource = File.ReadAllText(senderPath);

        Assert.Contains("FileTransferDataPathSendAuthority? SendAuthority", serviceSource, StringComparison.Ordinal);
        Assert.Contains("FormatV4DataPathAuthoritySuffix(context, \"filetransfer_v4_chunk_batch_sent\", pendingSend.SendAuthority)", senderSource, StringComparison.Ordinal);
        Assert.Contains("FormatV4DataPathAuthoritySuffix(context, \"filetransfer_v4_repair_sent\", repairSendAuthority)", senderSource, StringComparison.Ordinal);
        Assert.Contains("observeSendAuthority?.Invoke(sendAuthority)", senderSource, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "TerminalWins")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_CompactPostTerminalLivenessTimeout_RemainsPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_post_terminal_liveness_timeout";
        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-filetransfer-ops", Guid.NewGuid().ToString("N"));
        var logPath = Path.Combine(tempRoot, "nlink.log");
        var artifactDir = Path.Combine(tempRoot, "artifacts");
        Directory.CreateDirectory(artifactDir);

        var lines = BuildRouteAwareCompletedFixture(
                transferId,
                route: "post_tuna_fallback_v6",
                protocolVersion: 6,
                runtimeProfile: "default_v6",
                bridgeRecoveryPolicy: "post_tuna_fallback_strict",
                runtimeEventName: "filetransfer_v6_sender_started")
            .ToList();
        lines.Add(LogLine("event=session_liveness_timeout; session_id=sess_a; generation=1; silence_ms=19000; terminal_timeout_ms=18000; role=Helper", secondsOffset: 30));
        lines.Add(LogLine($"event=filetransfer_terminal_teardown_skipped_terminal_wins; direction=inbound; transfer_id={transferId}; session_id=sess_a; current_state=completed; current_error_code=(none); attempted_state=failed; attempted_error_code=peer_disconnected; source=filetransfer_peer_disconnect_terminalization; reason=session_liveness_timeout", secondsOffset: 31));
        await File.WriteAllLinesAsync(logPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await File.WriteAllTextAsync(
            Path.Combine(artifactDir, "filetransfer-tuna-gui-summary.json"),
            """
            {
              "completed": true,
              "integrityOk": true,
              "hardFailureCount": 0,
              "goodputBytesPerSecond": 1000000,
              "inboundTerminalState": "Completed",
              "outboundTerminalState": "Completed",
              "selectedRouteChanges": ["post_tuna_fallback_v6"],
              "liveRouteEpochRouteChanges": ["post_tuna_fallback_v6"]
            }
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var result = await RunFileTransferOpsAsync(
            repoRoot,
            [
                "-Mode",
                "AnalyzeRetained",
                "-LogPath",
                logPath,
                "-ArtifactDir",
                artifactDir
            ],
            new Dictionary<string, string>
            {
                ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
            });

        Assert.Equal(0, result.ExitCode);
        var verdict = ReadArtifactReport(artifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("pass", verdict["gate_status"]);
        Assert.Equal("none", verdict["acceptance_failure_class"]);
        Assert.Equal("1", verdict["session_liveness_timeout_count"]);
        Assert.Equal("0", verdict["session_liveness_timeout_before_terminal_count"]);
        Assert.Equal("1", verdict["session_liveness_timeout_after_terminal_count"]);

        var stability = ReadArtifactReport(artifactDir, "stability-gates-summary.txt");
        Assert.Equal("0", stability["liveness_timeout_before_terminal_count"]);
        Assert.Equal("1", stability["liveness_timeout_after_terminal_count"]);

        var efficiency = ReadArtifactReport(artifactDir, "filetransfer-efficiency-summary.txt");
        Assert.NotEqual("not_evaluated_due_correctness_failure", efficiency["efficiency_verdict"]);
        Assert.DoesNotContain("liveness_timeout", efficiency["correctness_blockers"], StringComparison.Ordinal);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_BoundedFallbackChurnIsWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_fallback_warning";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1", secondsOffset: 0),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=1", secondsOffset: 1),
            LogLine($"event=filetransfer_v6_receiver_state_coalesced; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; leg_generation=2; transport_epoch=3; checkpoint_request_id=v6-regular-nkn-state-refresh:1; contiguous_committed_chunk_index=100; durable_received_highest_chunk_index=128; missing_range_count=1", secondsOffset: 2),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1", secondsOffset: 10),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1", secondsOffset: 10)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        Assert.Equal("warning", efficiency["efficiency_verdict"]);
        Assert.Equal("efficiency_warning", efficiency["efficiency_failure_class"]);
        Assert.Equal("1", efficiency["v6_receiver_state_coalesced_count"]);
        Assert.Equal("post_tuna_fallback_v6", efficiency["route_epoch.0.route"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_UnpairedYieldFallbackChurnIsWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_fallback_treadmill";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1", secondsOffset: 0),
            LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; leg_generation=2; transport_epoch=3; checkpoint_request_id=(none); reason=tail_clean", secondsOffset: 3),
            LogLine($"event=filetransfer_v6_receiver_state_coalesced; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; leg_generation=2; transport_epoch=3; checkpoint_request_id=(none); contiguous_committed_chunk_index=100; durable_received_highest_chunk_index=128; missing_range_count=1", secondsOffset: 4),
            LogLine($"event=filetransfer_post_tuna_fallback_control_plane_pressure_marked; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; leg_generation=2; transport_epoch=3; checkpoint_request_id=(none); reason=frontier_stalled", secondsOffset: 5),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1", secondsOffset: 10),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1", secondsOffset: 10)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        Assert.Equal("1", efficiency["post_yield_recovery_activity_count"]);
        AssertExactTreadmillSummary(
            efficiency,
            scopedEffectCount: 0,
            distinctOperationCount: 0,
            exactWindowCount: 0,
            pressureEffectCount: 0,
            rawFailureCount: 0,
            distinctFailureCount: 0,
            recoveredFailureCount: 0,
            unresolvedFailureCount: 0,
            verdict: "warning",
            failureClass: "efficiency_warning");
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_OneSidedYieldDoesNotMakeOtherFallbackRecoveryTreadmill()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_fallback_one_sided_yield";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=2", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=2; leg_generation=4; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none)", secondsOffset: 0),
            LogLine($"event=filetransfer_fallback_tail_recovery_owner_retired_after_yield; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=1; previous_handoff_epoch=0; previous_handoff_state=none; discarded_repair_frame_count=0; discarded_repair_chunk_count=0; reason=fallback_tail_quiescent", secondsOffset: 1),
            LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=1; bridge_recovery_generation=1; committed_chunk=684; highest_observed_chunk=1124; state=active; can_send_data=1; reason=receiver_state_sparse_runtime", secondsOffset: 2),
            LogLine($"event=filetransfer_post_tuna_fallback_control_plane_pressure_marked; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=4; transport_epoch=2; checkpoint_request_id=(none); reason=frontier_stall_repair_due", secondsOffset: 3),
            LogLine($"event=nkn_bridge_receive_stall_recovery_started; connect_key=abc; stall_reason=all_channels_zero_receive; attempt=3; max_restarts=4; consecutive_zero_receive_windows=4; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1", secondsOffset: 4),
            LogLine($"event=filetransfer_v6_receiver_state_coalesced; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=4; transport_epoch=2; checkpoint_request_id=(none); previous_committed_chunk_index=1200; current_committed_chunk_index=1201; committed_delta=1; last_sent_committed_chunk_index=1200; suppressed_repeat_count=0", secondsOffset: 5),
            LogLine($"event=filetransfer_fallback_frontier_repair_progress_observed; direction=inbound; transfer_id={transferId}; session_id=sess_a; repair_request_key=v6-frontier:1201:1; reason=frontier_advanced; committed_frontier_before=1200; committed_frontier_after=1201; requested_chunk_count=1", secondsOffset: 6),
            LogLine($"event=filetransfer_fallback_tail_terminal_proof_consumed; direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:4; leg_generation=4; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=2; committed_chunk=12484; chunk_count=12484; reason=transfer_terminal", secondsOffset: 8),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1", secondsOffset: 10),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1", secondsOffset: 10)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        Assert.Equal("warning", efficiency["efficiency_verdict"]);
        Assert.Equal("efficiency_warning", efficiency["efficiency_failure_class"]);
        Assert.Equal("0", efficiency["post_yield_recovery_activity_count"]);
        Assert.Equal("0", efficiency["post_yield_repeated_unchanged_frontier_tuple_count"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_RetiredOwnerSuppressionIsDiagnostic()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_fallback_retired_owner_suppressed";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1", secondsOffset: 0),
            LogLine($"event=filetransfer_fallback_tail_recovery_owner_retired_after_yield; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:2; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=3; previous_handoff_epoch=3; previous_handoff_state=backfill_repair; discarded_repair_frame_count=1; discarded_repair_chunk_count=1; reason=fallback_tail_quiescent", secondsOffset: 2),
            LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:2; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=3; bridge_recovery_generation=1; committed_chunk=128; highest_observed_chunk=129; state=active; can_send_data=1; reason=fallback_tail_quiescent", secondsOffset: 3),
            LogLine($"event=filetransfer_fallback_tail_retired_owner_update_suppressed; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:2; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=3; bridge_recovery_generation=1; checkpoint_request_id=(none); source=v4_post_rebind_frontier_only_repair; attempted_action=activate_frontier_only_repair; owner_key=sess_a|{transferId}|post_tuna_fallback_v6|6|1|2|1|3|(none); reason=post_yield_replay", secondsOffset: 4),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1", secondsOffset: 10),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1", secondsOffset: 10)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        Assert.Equal("pass", efficiency["efficiency_verdict"]);
        Assert.Equal("none", efficiency["efficiency_failure_class"]);
        Assert.Equal("1", efficiency["fallback_tail_retired_owner_update_suppressed_count"]);
        Assert.Equal("1", efficiency["fallback_tail_recovery_owner_retired_after_yield_count"]);
        Assert.Equal("0", efficiency["post_yield_recovery_activity_count"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_PostYieldProgressAndLivenessProofsAreNotTreadmill()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_fallback_post_yield_progress";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4; leg_generation=6; transport_epoch=3; bridge_recovery_generation=3; checkpoint_request_id=(none)", secondsOffset: 0),
            LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:6; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=3; bridge_recovery_generation=3; committed_chunk=9000; highest_observed_chunk=9000; state=active; can_send_data=1; reason=receiver_state_sparse_runtime", secondsOffset: 1),
            LogLine($"event=session_liveness_proof_received; transport=nkn; session_id=sess_a; generation=1; sequence=42; proof_kind=file_transfer_data_frame; lane=bulk; observed_utc_ms=1", secondsOffset: 2),
            LogLine($"event=session_liveness_peer_proof_observed; session_id=sess_a; generation=1; sequence=42; proof_kind=file_transfer_data_frame; lane=bulk; role=Helper", secondsOffset: 2),
            LogLine($"event=filetransfer_control_plane_delivery_result; transport=nkn; direction=inbound; kind=receiver_state; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=6; bridge_recovery_generation=3; transport_epoch=3; checkpoint_request_id=(none); reason=chunk_batch_committed; frame_type=filetransfer.receiver_state.v6; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1", secondsOffset: 3),
            LogLine($"event=filetransfer_v6_receiver_state_coalesced; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=6; transport_epoch=3; checkpoint_request_id=(none); previous_committed_chunk_index=9000; current_committed_chunk_index=9001; committed_delta=1; last_sent_committed_chunk_index=9000; suppressed_repeat_count=0", secondsOffset: 3),
            LogLine($"event=filetransfer_v6_receiver_state_coalesced; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=6; transport_epoch=3; checkpoint_request_id=(none); previous_committed_chunk_index=9001; current_committed_chunk_index=9002; committed_delta=1; last_sent_committed_chunk_index=9000; suppressed_repeat_count=0", secondsOffset: 4),
            LogLine($"event=filetransfer_fallback_frontier_repair_progress_observed; direction=inbound; transfer_id={transferId}; session_id=sess_a; repair_request_key=v6-frontier:9001:1; reason=frontier_advanced; committed_frontier_before=9000; committed_frontier_after=9001; requested_chunk_count=1", secondsOffset: 5),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1", secondsOffset: 10),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1", secondsOffset: 10)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        Assert.Equal("warning", efficiency["efficiency_verdict"]);
        Assert.Equal("efficiency_warning", efficiency["efficiency_failure_class"]);
        Assert.Equal("0", efficiency["post_yield_recovery_activity_count"]);
        Assert.Equal("0", efficiency["post_yield_liveness_activity_count"]);
        Assert.Equal("0", efficiency["repeated_unchanged_frontier_tuple_count"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("RetainedFallbackSuite", "FallbackTailRetryCycleSuccessor")]
    public async Task AnalyzeRetained_ProbeRollbackRequiresFreshPairedYieldCycle(
        bool compact,
        bool recovered)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string session = "sess_retry_cycle";
        const string transfer = "transfer_retry_cycle";
        const string writer = "writer_retry_cycle";
        const string oldYield = "fta2-fallback_tail_yield-old";
        const string newYield = "fta2-fallback_tail_yield-new";
        const string transition = "fta2-filetransfer_route_transition-old";
        const string scope =
            "source_route=post_tuna_fallback_v6; source_protocol_version=6; " +
            "source_live_route_epoch=2; source_runtime_generation=3; source_terminal_generation=0; " +
            "source_leg_generation=3; source_route_publication_generation=3; " +
            "source_route_commit_generation=0; source_transport_epoch=1; " +
            "source_bridge_recovery_generation=1; source_checkpoint_generation=1; " +
            "source_checkpoint_request_id=(none); source_data_session_identity=data-session-a; " +
            "source_data_session_generation=1";
        var lines = new List<string>
        {
            LogLine($"event=filetransfer_route_transition_disposition_applied; session_id={session}; transfer_id={transfer}; direction=outbound; authority_id={transition}; transition_generation=3; transition_kind=PostTunaFallbackV6ToTunaV4; {scope}; release_authority_id={oldYield}; release_generation=1; install_generation=1; transaction_generation=1; offer_generation=2; tuna_path_lease_generation=2; disposition=rolledback; apply_status=applied; log_writer_id={writer}; log_writer_sequence=1"),
            LogLine($"event=runtime_unlock_precommit_probe_failed; session_id={session}; transfer_id={transfer}; direction=outbound; transaction_generation=1; offer_generation=2; tuna_path_lease_generation=2; reason=inbound_fallback_tail_not_yielded; log_writer_id={writer}; log_writer_sequence=2", secondsOffset: 1),
            LogLine($"event=filetransfer_runtime_unlock_probe_failed_fallback_resumed; session_id={session}; transfer_id={transfer}; direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; reason=inbound_fallback_tail_not_yielded; log_writer_id={writer}; log_writer_sequence=3", secondsOffset: 2),
            LogLine($"event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested; session_id={session}; transfer_id={transfer}; direction=outbound; authority_id={oldYield}; yield_generation=1; yield_kind=Quiescent; decision_status=Held; decision_reason=fallback_paired_tail_yield_pending; log_writer_id={writer}; log_writer_sequence=4", secondsOffset: 3),
            LogLine($"event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested; session_id={session}; transfer_id={transfer}; direction=outbound; authority_id={oldYield}; yield_generation=1; yield_kind=Quiescent; decision_status=Held; decision_reason=fallback_paired_tail_yield_pending; log_writer_id={writer}; log_writer_sequence=5", secondsOffset: 4)
        };
        if (recovered)
        {
            lines.Add(LogLine($"event=filetransfer_fallback_tail_retry_cycle_supersession_decided; session_id={session}; transfer_id={transfer}; direction=outbound; role=initiator; authority_id={oldYield}; yield_generation=1; peer_authority_id=fta2-fallback_tail_yield-peer; peer_yield_generation=1; status=Accepted; reason=pressure_rejected_precommit_probe_rollback; log_writer_id={writer}; log_writer_sequence=6", secondsOffset: 5));
            lines.Add(LogLine($"event=filetransfer_fallback_tail_retry_cycle_supersession_decided; session_id={session}; transfer_id={transfer}; direction=inbound; role=responder; authority_id=fta2-fallback_tail_yield-peer; yield_generation=1; peer_authority_id={oldYield}; peer_yield_generation=1; status=Accepted; reason=pressure_rejected_precommit_probe_rollback; log_writer_id=peer_writer; log_writer_sequence=1", secondsOffset: 6));
            lines.Add(LogLine($"event=filetransfer_fallback_tail_yield_issued; session_id={session}; transfer_id={transfer}; direction=outbound; authority_id={newYield}; predecessor_authority_id={oldYield}; yield_generation=2; yield_kind=Quiescent; log_writer_id={writer}; log_writer_sequence=7", secondsOffset: 7));
            lines.Add(LogLine($"event=filetransfer_route_transition_prepared; session_id={session}; transfer_id={transfer}; direction=outbound; authority_id=fta2-filetransfer_route_transition-new; transition_generation=4; transition_kind=PostTunaFallbackV6ToTunaV4; source_route=post_tuna_fallback_v6; fallback_yield_authority_id={newYield}; decision_status=Prepared; log_writer_id={writer}; log_writer_sequence=8", secondsOffset: 8));
        }

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        const string prefix =
            "runtime_unlock_retry_dispatch_paired_yield_successor_missing_after_probe_rollback";
        Assert.Equal("2", verdict[$"{prefix}_raw_count"]);
        Assert.Equal("1", verdict[$"{prefix}_distinct_count"]);
        Assert.Equal(recovered ? "1" : "0", verdict[$"{prefix}_recovered_count"]);
        Assert.Equal(recovered ? "0" : "1", verdict[$"{prefix}_unresolved_count"]);
        Assert.Equal(recovered ? "pass" : "fail", verdict[$"{prefix}_verdict"]);
        if (!recovered)
        {
            Assert.Equal(prefix, verdict["recovery_failure_class"]);
            Assert.Equal(
                "release_consumed_gen1_not_retired_for_fresh_quiescent_pair",
                verdict["failure_variant"]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_LaterRouteCycleDoesNotExtendExactPairedYieldWindow(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_exact_window_next_cycle";
        var lines = BuildExactPairedYieldEfficiencyPrefix(transferId);
        lines.Add(LogLine(
            $"event=filetransfer_fallback_leg_transport_epoch_adopted; " +
            $"{ExactYieldDirectScope(transferId, transportEpoch: 3)}; previous_transport_epoch=2; " +
            "retired_checkpoint_request_id=(none); current_checkpoint_request_id=(none); " +
            "state=recovery_active; can_send_data=1; reason=transport_recovered_unproven; " +
            "log_writer_id=writer_efficiency_exact",
            secondsOffset: 8));
        for (var operation = 0; operation < 26; operation++)
        {
            AppendMirroredEfficiencyDeliveryEffect(
                lines,
                transferId,
                operation,
                liveRouteEpoch: 4,
                runtimeGeneration: 5,
                legGeneration: 5,
                routePublicationGeneration: 7,
                transportEpoch: 4,
                bridgeRecoveryGeneration: 2,
                secondsOffset: 10 + operation);
        }
        lines.Add(LogLine(
            $"event=filetransfer_v6_receiver_state_coalesced; transfer_id={transferId}; session_id=sess_efficiency_exact; " +
            "direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; " +
            "runtime_generation=5; terminal_generation=0; leg_generation=5; route_publication_generation=7; " +
            "transport_epoch=4; bridge_recovery_generation=2; checkpoint_request_id=(none); " +
            "previous_committed_chunk_index=5970; current_committed_chunk_index=5971; committed_delta=1; " +
            "last_sent_committed_chunk_index=5970; suppressed_repeat_count=0; log_writer_id=writer_efficiency_exact",
            secondsOffset: 40));
        AppendEfficiencyCompletedTerminalLines(lines, transferId, secondsOffset: 50);

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string> { ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1" }
                : null);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Exact-window next-cycle analysis failed.{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        Assert.Equal("52", efficiency["post_yield_recovery_activity_count"]);
        AssertExactTreadmillSummary(
            efficiency,
            scopedEffectCount: 0,
            distinctOperationCount: 0,
            exactWindowCount: 1,
            pressureEffectCount: 0,
            rawFailureCount: 0,
            distinctFailureCount: 0,
            recoveredFailureCount: 0,
            unresolvedFailureCount: 0,
            verdict: "warning",
            failureClass: "efficiency_warning");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_MoreThanFiftyDistinctExactSourceEffectsAreTreadmill(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_exact_window_threshold";
        var lines = BuildExactPairedYieldEfficiencyPrefix(transferId);
        for (var operation = 0; operation < 51; operation++)
        {
            AppendMirroredEfficiencyDeliveryEffect(
                lines,
                transferId,
                operation,
                secondsOffset: 8 + operation);
        }
        AppendEfficiencyCompletedTerminalLines(lines, transferId, secondsOffset: 70);

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string> { ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1" }
                : null);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Exact-window threshold analysis failed.{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        Assert.Equal("102", efficiency["post_yield_recovery_activity_count"]);
        AssertExactTreadmillSummary(
            efficiency,
            scopedEffectCount: 51,
            distinctOperationCount: 51,
            exactWindowCount: 1,
            pressureEffectCount: 0,
            rawFailureCount: 1,
            distinctFailureCount: 1,
            recoveredFailureCount: 0,
            unresolvedFailureCount: 1,
            verdict: "fail",
            failureClass: "fallback_tail_efficiency_treadmill");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_ProductiveFrontierEpisodesDoNotAggregateIntoTreadmill(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_productive_frontier_episodes";
        var lines = BuildExactPairedYieldEfficiencyPrefix(transferId);
        var operation = 0;
        foreach (var episode in new[]
                 {
                     (Frontier: 4174, NextFrontier: 4177, EffectCount: 31),
                     (Frontier: 4177, NextFrontier: 4201, EffectCount: 30),
                     (Frontier: 4201, NextFrontier: 6249, EffectCount: 31),
                     (Frontier: 6249, NextFrontier: 6250, EffectCount: 30)
                 })
        {
            for (var index = 0; index < episode.EffectCount; index++)
            {
                AppendEfficiencyFrontierStallDeliveryEffect(
                    lines,
                    transferId,
                    operation++,
                    episode.Frontier,
                    secondsOffset: 8 + operation);
            }

            AppendEfficiencyFrontierProgressClosure(
                lines,
                transferId,
                episode.Frontier,
                episode.NextFrontier,
                operation,
                secondsOffset: 8 + operation);
        }
        AppendEfficiencyCompletedTerminalLines(lines, transferId, secondsOffset: 200);

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string> { ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1" }
                : null);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Productive frontier-episode analysis failed.{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        Assert.Equal("244", efficiency["post_yield_recovery_activity_count"]);
        AssertExactTreadmillSummary(
            efficiency,
            scopedEffectCount: 122,
            distinctOperationCount: 122,
            exactWindowCount: 1,
            pressureEffectCount: 0,
            rawFailureCount: 0,
            distinctFailureCount: 0,
            recoveredFailureCount: 0,
            unresolvedFailureCount: 0,
            verdict: "warning",
            failureClass: "efficiency_warning",
            frontierEpisodeCount: 4,
            progressClosedEpisodeCount: 4,
            unresolvedEpisodeCount: 0,
            maxNoProgressEffectCount: 31);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_OneNoProgressFrontierEpisodeStillTripsThreshold(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_single_frontier_episode";
        var lines = BuildExactPairedYieldEfficiencyPrefix(transferId);
        for (var operation = 0; operation < 51; operation++)
        {
            AppendEfficiencyFrontierStallDeliveryEffect(
                lines,
                transferId,
                operation,
                frontier: 4174,
                secondsOffset: 8 + operation);
        }
        AppendEfficiencyCompletedTerminalLines(lines, transferId, secondsOffset: 70);

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string> { ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1" }
                : null);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Single no-progress frontier-episode analysis failed.{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        AssertExactTreadmillSummary(
            efficiency,
            scopedEffectCount: 51,
            distinctOperationCount: 51,
            exactWindowCount: 1,
            pressureEffectCount: 0,
            rawFailureCount: 1,
            distinctFailureCount: 1,
            recoveredFailureCount: 0,
            unresolvedFailureCount: 1,
            verdict: "fail",
            failureClass: "fallback_tail_efficiency_treadmill",
            frontierEpisodeCount: 1,
            progressClosedEpisodeCount: 0,
            unresolvedEpisodeCount: 1,
            maxNoProgressEffectCount: 51);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_DeduplicatesMirroredDeliveryAndStateRefreshLifecycle(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_exact_window_dedupe";
        const string requestId = "v6-regular-nkn-state-refresh:41";
        var lines = BuildExactPairedYieldEfficiencyPrefix(transferId, requestId);
        AppendMirroredEfficiencyDeliveryEffect(
            lines,
            transferId,
            operation: 0,
            checkpointRequestId: requestId,
            secondsOffset: 8);
        var directScope = ExactYieldDirectScope(transferId, requestId);
        lines.Add(LogLine(
            $"event=filetransfer_fallback_checkpoint_requested; {directScope}; checkpoint_generation=41; " +
            "reason=state_refresh_recovery; decision_status=Accepted; authority_effect=positive; " +
            "log_writer_id=writer_efficiency_exact",
            secondsOffset: 9));
        lines.Add(LogLine(
            $"event=filetransfer_v6_regular_nkn_state_refresh_requested; transfer_id={transferId}; " +
            $"session_id=sess_efficiency_exact; request_id={requestId}; request_sequence=41; " +
            "reason=state_refresh_recovery; priority=state_refresh; transport_epoch=2; " +
            "log_writer_id=writer_efficiency_exact",
            secondsOffset: 10));
        lines.Add(LogLine(
            $"event=filetransfer_v6_regular_nkn_state_refresh_send_queued; transfer_id={transferId}; " +
            $"session_id=sess_efficiency_exact; request_id={requestId}; generation=41; priority=state_refresh; " +
            "source=post_tuna_fallback; log_writer_id=writer_efficiency_exact",
            secondsOffset: 11));
        lines.Add(LogLine(
            $"event=filetransfer_v6_regular_nkn_state_refresh_sent; transfer_id={transferId}; " +
            $"session_id=sess_efficiency_exact; request_id={requestId}; generation=41; transport_epoch=2; " +
            "reused_pending_checkpoint=0; log_writer_id=writer_efficiency_exact",
            secondsOffset: 12));
        var received = LogLine(
            $"event=filetransfer_v6_regular_nkn_state_refresh_received; transfer_id={transferId}; " +
            $"session_id=sess_efficiency_exact; request_id={requestId}; generation=41; transport_epoch=2; " +
            "recovery_mode=state_refresh; log_writer_id=writer_efficiency_exact",
            secondsOffset: 13);
        var resent = LogLine(
            $"event=filetransfer_v6_regular_nkn_state_refresh_state_resent; transfer_id={transferId}; " +
            $"session_id=sess_efficiency_exact; request_id={requestId}; generation=41; sent=1; " +
            "log_writer_id=writer_efficiency_exact",
            secondsOffset: 14);
        lines.Add(received);
        lines.Add(received);
        lines.Add(resent);
        lines.Add(resent);
        AppendEfficiencyCompletedTerminalLines(lines, transferId, secondsOffset: 20);

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string> { ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1" }
                : null);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Exact-window dedupe analysis failed.{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        Assert.Equal("9", efficiency["post_yield_recovery_activity_count"]);
        AssertExactTreadmillSummary(
            efficiency,
            scopedEffectCount: 2,
            distinctOperationCount: 2,
            exactWindowCount: 1,
            pressureEffectCount: 0,
            rawFailureCount: 0,
            distinctFailureCount: 0,
            recoveredFailureCount: 0,
            unresolvedFailureCount: 0,
            verdict: "warning",
            failureClass: "efficiency_warning");
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_ProductionStateRefreshDeliveryBindsItsOwnGenerationDomain()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_state_refresh_production_binding";
        const string requestId = "v6-regular-nkn-state-refresh:41";
        var lines = BuildExactPairedYieldEfficiencyPrefix(transferId, requestId);
        var directScope = ExactYieldDirectScope(transferId, requestId);
        lines.Add(LogLine(
            $"event=filetransfer_control_plane_delivery_result; {directScope}; kind=fallback_checkpoint_request; " +
            "authority_id=fta2-control-plane-production-state-refresh; control_plane_generation=1; " +
            "reason=post_tuna_fallback_state_refresh; recovery_mode=regular_nkn_state_refresh; " +
            "accepted_any=1; peer_visible_any=1; local_only_rejected=0; authority_effect=positive; " +
            "decision_status=Accepted; log_writer_id=writer_efficiency_exact",
            secondsOffset: 8));
        AppendEfficiencyStateRefreshLifecycle(lines, transferId, requestId, generation: 41);
        AppendEfficiencyCompletedTerminalLines(lines, transferId, secondsOffset: 20);

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Production state-refresh binding analysis failed.{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        AssertExactTreadmillSummary(
            efficiency,
            scopedEffectCount: 1,
            distinctOperationCount: 1,
            exactWindowCount: 1,
            pressureEffectCount: 0,
            rawFailureCount: 0,
            distinctFailureCount: 0,
            recoveredFailureCount: 0,
            unresolvedFailureCount: 0,
            verdict: "warning",
            failureClass: "efficiency_warning");
    }

    [Theory]
    [InlineData(false, "one_sided")]
    [InlineData(true, "one_sided")]
    [InlineData(false, "stale")]
    [InlineData(true, "stale")]
    [InlineData(false, "wrong_scope")]
    [InlineData(true, "wrong_scope")]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_ExcludesUnpairedStaleAndWrongScopeEvidence(
        bool compact,
        string mutation)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_exact_window_negative";
        var lines = mutation == "one_sided"
            ? BuildOneSidedYieldEfficiencyPrefix(transferId)
            : BuildExactPairedYieldEfficiencyPrefix(transferId);
        if (mutation == "one_sided")
        {
            AppendMirroredEfficiencyDeliveryEffect(lines, transferId, operation: 0, secondsOffset: 4);
        }
        else if (mutation == "stale")
        {
            var directScope = ExactYieldDirectScope(transferId);
            lines.Add(LogLine(
                $"event=filetransfer_runtime_owner_stale_update_rejected; {directScope}; " +
                "source=retired_fallback_owner; attempted_action=checkpoint_reissue; authority_effect=none; " +
                "decision_status=Rejected; reason=runtime_generation_mismatch; log_writer_id=writer_efficiency_exact",
                secondsOffset: 8));
            lines.Add(LogLine(
                $"event=filetransfer_fallback_tail_retired_owner_update_suppressed; {directScope}; " +
                "source=retired_fallback_owner; attempted_action=checkpoint_reissue; authority_effect=none; " +
                "decision_status=Rejected; reason=post_yield_replay; log_writer_id=writer_efficiency_exact",
                secondsOffset: 9));
            lines.Add(LogLine(
                $"event=filetransfer_control_plane_delivery_result; {directScope}; kind=receiver_state; " +
                "authority_schema=2; authority_id=fta2-control-plane-stale; control_plane_generation=1; " +
                "reason=state_refresh_recovery; frame_type=filetransfer.receiver_state.v6; accepted_any=0; " +
                "peer_visible_any=0; local_only_rejected=1; authority_effect=none; decision_status=Rejected; " +
                "log_writer_id=writer_efficiency_exact",
                secondsOffset: 10));
            lines.Add(LogLine(
                $"event=filetransfer_fallback_checkpoint_reissue_requested; {directScope}; " +
                "authority_effect=none; decision_status=Rejected; reason=stale_owner; " +
                "log_writer_id=writer_efficiency_exact",
                secondsOffset: 11));
        }
        else
        {
            AppendMirroredEfficiencyDeliveryEffect(
                lines,
                transferId,
                operation: 0,
                writerId: "writer_other",
                secondsOffset: 8);
            AppendMirroredEfficiencyDeliveryEffect(
                lines,
                transferId,
                operation: 1,
                sessionId: "sess_other",
                secondsOffset: 9);
            AppendMirroredEfficiencyDeliveryEffect(
                lines,
                transferId,
                operation: 2,
                liveRouteEpoch: 4,
                runtimeGeneration: 5,
                legGeneration: 5,
                routePublicationGeneration: 7,
                transportEpoch: 4,
                bridgeRecoveryGeneration: 2,
                secondsOffset: 10);
        }
        AppendEfficiencyCompletedTerminalLines(lines, transferId, secondsOffset: 20);

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string> { ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1" }
                : null);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Exact-window negative analysis failed ({mutation}).{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        AssertExactTreadmillSummary(
            efficiency,
            scopedEffectCount: 0,
            distinctOperationCount: 0,
            exactWindowCount: mutation == "one_sided" ? 0 : 1,
            pressureEffectCount: 0,
            rawFailureCount: 0,
            distinctFailureCount: 0,
            recoveredFailureCount: 0,
            unresolvedFailureCount: 0,
            verdict: mutation == "stale" ? "warning" : "pass",
            failureClass: mutation == "stale" ? "efficiency_warning" : "none");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_ExactSourcePressureRemainsTreadmill(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_exact_window_pressure";
        var lines = BuildExactPairedYieldEfficiencyPrefix(transferId);
        lines.Add(LogLine(
            $"event=filetransfer_post_tuna_fallback_control_plane_pressure_marked; {ExactYieldDirectScope(transferId)}; " +
            "pressure_generation=1; kind=receiver_state; reason=frontier_stalled; authority_effect=positive; " +
            "decision_status=Accepted; outcome=StaleRejected; log_writer_id=writer_efficiency_exact",
            secondsOffset: 8));
        AppendEfficiencyCompletedTerminalLines(lines, transferId, secondsOffset: 20);

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string> { ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1" }
                : null);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Exact-window pressure analysis failed.{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        Assert.Equal("1", efficiency["post_yield_recovery_activity_count"]);
        AssertExactTreadmillSummary(
            efficiency,
            scopedEffectCount: 1,
            distinctOperationCount: 1,
            exactWindowCount: 1,
            pressureEffectCount: 1,
            rawFailureCount: 1,
            distinctFailureCount: 1,
            recoveredFailureCount: 0,
            unresolvedFailureCount: 1,
            verdict: "fail",
            failureClass: "fallback_tail_efficiency_treadmill");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_DuplicateAndNonAuthoritativeFrontierRowsDoNotTripThreshold(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_frontier_duplicate_noise";
        var lines = BuildExactPairedYieldEfficiencyPrefix(transferId);
        AppendEfficiencyFrontierRequestEffect(lines, transferId, operation: 1);
        var exactDuplicate = lines[^1];
        for (var copy = 0; copy < 60; copy++)
        {
            lines.Add(exactDuplicate);
        }
        for (var operation = 2; operation < 62; operation++)
        {
            var decisionStatus = operation % 3 == 0 ? "Stale" : operation % 3 == 1 ? "Rejected" : "Accepted";
            AppendEfficiencyFrontierRequestEffect(
                lines,
                transferId,
                operation,
                decisionStatus,
                authorityEffect: "none",
                secondsOffset: 9 + operation);
        }
        AppendEfficiencyCompletedTerminalLines(lines, transferId, secondsOffset: 80);

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string> { ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1" }
                : null);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Frontier duplicate/no-authority analysis failed.{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        Assert.Equal("0", efficiency["post_yield_repeated_unchanged_frontier_tuple_count"]);
        AssertExactTreadmillSummary(
            efficiency,
            scopedEffectCount: 0,
            distinctOperationCount: 0,
            exactWindowCount: 1,
            pressureEffectCount: 0,
            rawFailureCount: 0,
            distinctFailureCount: 0,
            recoveredFailureCount: 0,
            unresolvedFailureCount: 0,
            verdict: "warning",
            failureClass: "efficiency_warning");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_DistinctSameSourceFrontierEffectsCanTripThreshold(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_frontier_distinct_effects";
        var lines = BuildExactPairedYieldEfficiencyPrefix(transferId);
        for (var operation = 0; operation < 52; operation++)
        {
            AppendEfficiencyFrontierRequestEffect(
                lines,
                transferId,
                operation,
                secondsOffset: 8 + operation);
        }
        AppendEfficiencyCompletedTerminalLines(lines, transferId, secondsOffset: 70);

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string> { ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1" }
                : null);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Distinct frontier-effect analysis failed.{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        Assert.Equal("51", efficiency["post_yield_repeated_unchanged_frontier_tuple_count"]);
        AssertExactTreadmillSummary(
            efficiency,
            scopedEffectCount: 0,
            distinctOperationCount: 0,
            exactWindowCount: 1,
            pressureEffectCount: 0,
            rawFailureCount: 1,
            distinctFailureCount: 1,
            recoveredFailureCount: 0,
            unresolvedFailureCount: 1,
            verdict: "fail",
            failureClass: "fallback_tail_efficiency_treadmill");
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_MutatedRepeatedOperationIdentityIsNotCounted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_frontier_identity_mutation";
        var lines = BuildExactPairedYieldEfficiencyPrefix(transferId);
        AppendEfficiencyFrontierRequestEffect(lines, transferId, operation: 1, secondsOffset: 8);
        lines.Add(lines[^1].Replace(
            "request_correlation_id=frontier-effect-1",
            "request_correlation_id=frontier-effect-mutated",
            StringComparison.Ordinal));
        AppendEfficiencyFrontierRequestEffect(lines, transferId, operation: 2, secondsOffset: 9);
        AppendEfficiencyCompletedTerminalLines(lines, transferId, secondsOffset: 20);

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Repeated-operation mutation analysis failed.{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        Assert.Equal("0", efficiency["post_yield_repeated_unchanged_frontier_tuple_count"]);
        AssertExactTreadmillSummary(
            efficiency,
            scopedEffectCount: 0,
            distinctOperationCount: 0,
            exactWindowCount: 1,
            pressureEffectCount: 0,
            rawFailureCount: 0,
            distinctFailureCount: 0,
            recoveredFailureCount: 0,
            unresolvedFailureCount: 0,
            verdict: "warning",
            failureClass: "efficiency_warning");
    }

    [Theory]
    [InlineData("wrong_writer")]
    [InlineData("wrong_direction")]
    [InlineData("wrong_scope")]
    [InlineData("aba_generation")]
    [InlineData("competing_binding")]
    [InlineData("missing_generation")]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_AmbiguousStateRefreshLifecycleIsNotAttributed(
        string mutation)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_state_refresh_ambiguous";
        const string requestId = "v6-regular-nkn-state-refresh:41";
        var lines = BuildExactPairedYieldEfficiencyPrefix(transferId, requestId);
        var binding = LogLine(
            $"event=filetransfer_fallback_checkpoint_requested; {ExactYieldDirectScope(transferId, requestId)}; " +
            "checkpoint_generation=41; reason=state_refresh_recovery; decision_status=Accepted; " +
            "authority_effect=positive; log_writer_id=writer_efficiency_exact",
            secondsOffset: 8);
        lines.Add(binding);
        switch (mutation)
        {
            case "wrong_writer":
                AppendEfficiencyStateRefreshLifecycle(
                    lines,
                    transferId,
                    requestId,
                    generation: 41,
                    writerId: "writer_other");
                break;
            case "wrong_direction":
                AppendEfficiencyStateRefreshLifecycle(
                    lines,
                    transferId,
                    requestId,
                    generation: 41,
                    extraScope: "direction=inbound");
                break;
            case "wrong_scope":
                AppendEfficiencyStateRefreshLifecycle(
                    lines,
                    transferId,
                    requestId,
                    generation: 41,
                    extraScope: "direction=outbound; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; runtime_generation=5; terminal_generation=0; leg_generation=5; route_publication_generation=7; bridge_recovery_generation=2; data_session_identity=data-session-2; data_session_generation=2");
                break;
            case "aba_generation":
                AppendEfficiencyStateRefreshLifecycle(lines, transferId, requestId, generation: 41);
                AppendEfficiencyStateRefreshLifecycle(lines, transferId, requestId, generation: 42, secondsOffset: 12);
                AppendEfficiencyStateRefreshLifecycle(lines, transferId, requestId, generation: 41, secondsOffset: 14);
                break;
            case "competing_binding":
                lines.Add(binding.Replace("checkpoint_generation=41", "checkpoint_generation=42", StringComparison.Ordinal));
                AppendEfficiencyStateRefreshLifecycle(lines, transferId, requestId, generation: 41);
                break;
            case "missing_generation":
                var lifecycleStart = lines.Count;
                AppendEfficiencyStateRefreshLifecycle(lines, transferId, requestId, generation: 41);
                for (var index = lifecycleStart; index < lines.Count; index++)
                {
                    lines[index] = lines[index]
                        .Replace("request_sequence=41", "request_sequence=(none)", StringComparison.Ordinal)
                        .Replace("generation=41", "generation=(none)", StringComparison.Ordinal);
                }
                break;
        }
        AppendEfficiencyCompletedTerminalLines(lines, transferId, secondsOffset: 30);

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Ambiguous state-refresh analysis failed ({mutation}).{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        AssertExactTreadmillSummary(
            efficiency,
            scopedEffectCount: 0,
            distinctOperationCount: 0,
            exactWindowCount: 1,
            pressureEffectCount: 0,
            rawFailureCount: 0,
            distinctFailureCount: 0,
            recoveredFailureCount: 0,
            unresolvedFailureCount: 0,
            verdict: "pass",
            failureClass: "none");
    }

    [Theory]
    [InlineData("unique", 1)]
    [InlineData("competing_generation", 0)]
    [InlineData("missing_generation", 0)]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_BridgeLifecycleRequiresOneExactGeneration(
        string mutation,
        int expectedEffectCount)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_bridge_binding";
        const string leaseAuthorityId = "fta2-recovery-liveness-lease-efficiency";
        var lines = BuildExactPairedYieldEfficiencyPrefix(transferId);
        var directScope = ExactYieldDirectScope(transferId);
        lines.Add(LogLine(
            $"event=filetransfer_bridge_recovery_authority_granted; {directScope}; " +
            $"recovery_liveness_lease_authority_id={leaseAuthorityId}; recovery_liveness_lease_generation=41; " +
            "decision_status=Accepted; authority_effect=positive; log_writer_id=writer_efficiency_exact",
            secondsOffset: 8));
        if (mutation == "competing_generation")
        {
            lines.Add(LogLine(
                $"event=filetransfer_bridge_recovery_authority_granted; {directScope}; " +
                $"recovery_liveness_lease_authority_id={leaseAuthorityId}; recovery_liveness_lease_generation=42; " +
                "decision_status=Accepted; authority_effect=positive; log_writer_id=writer_efficiency_exact",
                secondsOffset: 9));
        }
        var lifecycleGeneration = mutation == "missing_generation"
            ? "(none)"
            : "41";
        lines.Add(LogLine(
            $"event=filetransfer_bridge_recovery_execution_command_issued; {directScope}; " +
            $"recovery_liveness_lease_authority_id={leaseAuthorityId}; " +
            $"recovery_liveness_lease_generation={lifecycleGeneration}; decision_status=Accepted; " +
            "authority_effect=positive; log_writer_id=writer_efficiency_exact",
            secondsOffset: 10));
        AppendEfficiencyCompletedTerminalLines(lines, transferId, secondsOffset: 20);

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Bridge binding analysis failed ({mutation}).{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        AssertExactTreadmillSummary(
            efficiency,
            scopedEffectCount: expectedEffectCount,
            distinctOperationCount: expectedEffectCount,
            exactWindowCount: 1,
            pressureEffectCount: 0,
            rawFailureCount: 0,
            distinctFailureCount: 0,
            recoveredFailureCount: 0,
            unresolvedFailureCount: 0,
            verdict: expectedEffectCount == 1 ? "warning" : "pass",
            failureClass: expectedEffectCount == 1 ? "efficiency_warning" : "none");
    }

    [Theory]
    [InlineData("identical")]
    [InlineData("identical_data_session")]
    [InlineData("rejected")]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_IdenticalOrRejectedBarrierDoesNotCloseWindow(
        string barrierKind)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_non_closing_barrier";
        var lines = BuildExactPairedYieldEfficiencyPrefix(transferId);
        lines.Add(barrierKind switch
        {
            "identical" => LogLine(
                $"event=filetransfer_runtime_started; {ExactYieldDirectScope(transferId)}; " +
                "runtime_profile=default_v6; decision_status=Accepted; authority_effect=positive; " +
                "log_writer_id=writer_efficiency_exact",
                secondsOffset: 8),
            "identical_data_session" => LogLine(
                $"event=filetransfer_data_session_recreated; {ExactYieldRouteScope(transferId)}; " +
                "previous_data_session_identity=data-session-1; previous_data_session_generation=1; " +
                "current_data_session_identity=data-session-1; current_data_session_generation=1; " +
                "decision_status=Accepted; reason=idempotent_current_session; log_writer_id=writer_efficiency_exact",
                secondsOffset: 8),
            _ => LogLine(
                $"event=filetransfer_route_selected; {ExactYieldDirectScope(transferId, liveRouteEpoch: 4, runtimeGeneration: 5, legGeneration: 5, routePublicationGeneration: 7, transportEpoch: 4, bridgeRecoveryGeneration: 2, dataSessionIdentity: "data-session-2", dataSessionGeneration: 2)}; " +
                "target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; " +
                "route_publication_action_kind=RoutePublication; coordinator_decision_reason=rejected; " +
                "decision_status=Rejected; authority_effect=none; log_writer_id=writer_efficiency_exact",
                secondsOffset: 8)
        });
        for (var operation = 0; operation < 51; operation++)
        {
            AppendMirroredEfficiencyDeliveryEffect(lines, transferId, operation, secondsOffset: 10 + operation);
        }
        AppendEfficiencyCompletedTerminalLines(lines, transferId, secondsOffset: 70);

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Non-closing barrier analysis failed ({barrierKind}).{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        AssertExactTreadmillSummary(
            efficiency,
            scopedEffectCount: 51,
            distinctOperationCount: 51,
            exactWindowCount: 1,
            pressureEffectCount: 0,
            rawFailureCount: 1,
            distinctFailureCount: 1,
            recoveredFailureCount: 0,
            unresolvedFailureCount: 1,
            verdict: "fail",
            failureClass: "fallback_tail_efficiency_treadmill");
    }

    [Theory]
    [InlineData("checkpoint")]
    [InlineData("data_session")]
    [InlineData("disposal")]
    [InlineData("runtime_disposal")]
    [InlineData("route_hint")]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_GenuineSourceBoundaryClosesWindow(
        string barrierKind)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_closing_barrier";
        var lines = BuildExactPairedYieldEfficiencyPrefix(transferId);
        lines.Add(barrierKind switch
        {
            "checkpoint" => LogLine(
                $"event=filetransfer_fallback_checkpoint_exchange_started; {ExactYieldRouteScope(transferId)}; " +
                "transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:42; " +
                "current_checkpoint_request_id=v6-regular-nkn-state-refresh:42; previous_checkpoint_request_id=(none); " +
                "decision_status=Accepted; authority_effect=positive; log_writer_id=writer_efficiency_exact",
                secondsOffset: 8),
            "data_session" => LogLine(
                $"event=filetransfer_data_session_recreated; {ExactYieldRouteScope(transferId)}; " +
                "previous_data_session_identity=data-session-1; previous_data_session_generation=1; " +
                "current_data_session_identity=data-session-2; current_data_session_generation=2; " +
                "decision_status=Accepted; reason=replaced_current_session; log_writer_id=writer_efficiency_exact",
                secondsOffset: 8),
            "runtime_disposal" => LogLine(
                $"event=filetransfer_runtime_disposed; {ExactYieldDirectScope(transferId)}; " +
                "decision_status=Accepted; reason=disposed; log_writer_id=writer_efficiency_exact",
                secondsOffset: 8),
            "route_hint" => LogLine(
                $"event=filetransfer_route_hint_authorized_publication_accepted; {ExactYieldDirectScope(transferId)}; " +
                "target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; " +
                "route_publication_action_kind=RouteCommitPublication; coordinator_decision_reason=accepted; " +
                "log_writer_id=writer_efficiency_exact",
                secondsOffset: 8),
            _ => LogLine(
                $"event=filetransfer_data_session_removed; {ExactYieldDirectScope(transferId)}; " +
                "decision_status=Accepted; reason=disposed; log_writer_id=writer_efficiency_exact",
                secondsOffset: 8)
        });
        for (var operation = 0; operation < 51; operation++)
        {
            AppendMirroredEfficiencyDeliveryEffect(lines, transferId, operation, secondsOffset: 10 + operation);
        }
        AppendEfficiencyCompletedTerminalLines(lines, transferId, secondsOffset: 70);

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Closing barrier analysis failed ({barrierKind}).{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        AssertExactTreadmillSummary(
            efficiency,
            scopedEffectCount: 0,
            distinctOperationCount: 0,
            exactWindowCount: 1,
            pressureEffectCount: 0,
            rawFailureCount: 0,
            distinctFailureCount: 0,
            recoveredFailureCount: 0,
            unresolvedFailureCount: 0,
            verdict: "pass",
            failureClass: "none");
    }

    [Theory]
    [InlineData("wrong_reason")]
    [InlineData("ambiguous")]
    [InlineData("negative_grant")]
    [InlineData("negative_retirement")]
    [InlineData("ambiguous_pairing")]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_RetirementMustBeUniqueAndQuiescent(
        string mutation)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_retirement_strict";
        var lines = BuildExactPairedYieldEfficiencyPrefix(transferId);
        var retirementIndex = lines.FindIndex(line =>
            line.Contains("event=filetransfer_fallback_tail_recovery_owner_retired_after_yield", StringComparison.Ordinal));
        Assert.True(retirementIndex >= 0);
        if (mutation == "wrong_reason")
        {
            lines[retirementIndex] = lines[retirementIndex].Replace(
                "reason=fallback_tail_quiescent",
                "reason=cleanup_only",
                StringComparison.Ordinal);
        }
        else if (mutation == "ambiguous")
        {
            lines.Insert(
                retirementIndex + 1,
                lines[retirementIndex].Replace(
                    "previous_handoff_epoch=2",
                    "previous_handoff_epoch=3",
                    StringComparison.Ordinal));
        }
        else if (mutation == "negative_grant")
        {
            var grantIndex = lines.FindIndex(line =>
                line.Contains("event=filetransfer_fallback_tail_paired_yield_granted", StringComparison.Ordinal));
            Assert.True(grantIndex >= 0);
            lines[grantIndex] = lines[grantIndex].Replace(
                "decision_status=Accepted",
                "decision_status=Rejected; authority_effect=none",
                StringComparison.Ordinal);
        }
        else if (mutation == "negative_retirement")
        {
            lines[retirementIndex] = lines[retirementIndex].Replace(
                "reason=fallback_tail_quiescent",
                "decision_status=StaleRejected; authority_effect=none; reason=fallback_tail_quiescent",
                StringComparison.Ordinal);
        }
        else
        {
            var pairingIndex = lines.FindIndex(line =>
                line.Contains("event=filetransfer_fallback_tail_pairing_committed", StringComparison.Ordinal));
            Assert.True(pairingIndex >= 0);
            var competingPairing = lines[pairingIndex].Replace(
                "peer_authority_id=fta2-fallback_tail_yield-89abcdef0123456701234567",
                "peer_authority_id=fta2-fallback_tail_yield-fedcba9876543210fedcba98",
                StringComparison.Ordinal);
            Assert.NotEqual(lines[pairingIndex], competingPairing);
            lines.Insert(pairingIndex + 1, competingPairing);
        }
        AppendMirroredEfficiencyDeliveryEffect(lines, transferId, operation: 1, secondsOffset: 10);
        AppendEfficiencyCompletedTerminalLines(lines, transferId, secondsOffset: 20);

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Strict retirement analysis failed ({mutation}).{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        AssertExactTreadmillSummary(
            efficiency,
            scopedEffectCount: 0,
            distinctOperationCount: 0,
            exactWindowCount: 0,
            pressureEffectCount: 0,
            rawFailureCount: 0,
            distinctFailureCount: 0,
            recoveredFailureCount: 0,
            unresolvedFailureCount: 0,
            verdict: "pass",
            failureClass: "none");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackV6RouteWithV4Runtime_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_post_fallback_v4_runtime";
        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareCompletedFixture(
            transferId,
            route: "post_tuna_fallback_v6",
            protocolVersion: 4,
            runtimeProfile: "file_tuna_v4_fast",
            bridgeRecoveryPolicy: "post_tuna_fallback_strict",
            runtimeEventName: "filetransfer_v4_sender_started"));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("route_consistency_verdict=fail", routeText, StringComparison.Ordinal);
        Assert.Contains("route selected protocol mismatch", routeText, StringComparison.Ordinal);
        Assert.Contains("V6 route entered regular V4 runtime", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveFileTunaFallbackRouteChangeBeforeTerminal_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareControlledRestartFixture(includeSetupTerminal: false));

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Contains("file_tuna_v4", route["selected_routes"], StringComparison.Ordinal);
        Assert.Contains("post_tuna_fallback_v6", route["selected_routes"], StringComparison.Ordinal);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6", route["selected_route_changes"]);
        Assert.Equal("(none)", route["live_route_epoch_route_changes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveFallbackLegHistory_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_leg_started; direction=outbound; transfer_id={transferId}; session_id=(none); leg_id=leg:1; leg_generation=1; route=file_tuna_v4; protocol_version=4; live_route_epoch=0; transport_epoch=0; state=active; reason=new_transfer; start_committed_chunk=0; bridge_recovery_generation=0; can_send_data=1"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_leg_frozen; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:1; leg_generation=1; route=file_tuna_v4; protocol_version=4; live_route_epoch=0; transport_epoch=0; reason=header_switch_off; proven_committed_chunk=0; proven_highest_observed_chunk=-1"),
            LogLine($"event=filetransfer_clean_fallback_leg_frozen; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:1; leg_generation=1; route=file_tuna_v4; protocol_version=4; live_route_epoch=0; transport_epoch=0; reason=header_switch_off; proven_committed_chunk=0; proven_highest_observed_chunk=-1"),
            LogLine($"event=filetransfer_leg_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:2; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=0; state=checkpoint_pending; reason=header_switch_off; start_committed_chunk=128; bridge_recovery_generation=0; can_send_data=0"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=1"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6", route["selected_route_changes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackLegAuthorityEvidence_IsReported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=filetransfer_fallback_leg_authority_bridge_recovery_requested; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; proven_committed_chunk=128; proven_highest_observed_chunk=160; reason=receiver_state"),
            LogLine($"event=filetransfer_fallback_leg_authority_completed; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed; proof=post_tuna_receiver_state"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["fallback_leg_authority_proof_verdict"]);
        Assert.Equal("2", route["fallback_leg_authority_generation_sequence"]);
        Assert.Equal("1", route["fallback_leg_authority_started_count"]);
        Assert.Equal("1", route["fallback_leg_authority_checkpoint_accepted_count"]);
        Assert.Equal("1", route["fallback_leg_authority_bridge_recovery_requested_count"]);
        Assert.Equal("1", route["fallback_leg_authority_completed_count"]);
        Assert.Equal("0", route["fallback_leg_authority_metadata_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackAuthorityBeforeRouteSelection_SelfValidatesAgainstFallbackWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed", secondsOffset: 1),
            LogLine($"event=filetransfer_fallback_checkpoint_requested; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; reason=post_tuna_fallback_state_refresh_failed", secondsOffset: 2),
            LogLine($"event=filetransfer_clean_fallback_checkpoint_requested; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; reason=post_tuna_fallback_state_refresh_failed", secondsOffset: 3),
            LogLine($"event=filetransfer_control_plane_delivery_result; transport=nkn; direction=outbound; kind=fallback_checkpoint_request; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=2; bridge_recovery_generation=1; transport_epoch=7; checkpoint_request_id=v6-regular-nkn-state-refresh:1; reason=post_tuna_fallback_state_refresh; frame_type=filetransfer.frontier_request.v6; peer_visible_any=1; accepted_any=1; local_only_rejected=0", secondsOffset: 4),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=2", secondsOffset: 5),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1", secondsOffset: 5),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1", secondsOffset: 5)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6", route["selected_route_changes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_InboundOnlyFallbackRoute_DoesNotRequireSenderAuthorityProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_leg_frozen; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:1; leg_generation=1; route=file_tuna_v4; protocol_version=4; live_route_epoch=0; transport_epoch=0; reason=sidecar_remote_closed; proven_committed_chunk=0; proven_highest_observed_chunk=-1"),
            LogLine($"event=filetransfer_clean_fallback_leg_frozen; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:1; leg_generation=1; route=file_tuna_v4; protocol_version=4; live_route_epoch=0; transport_epoch=0; reason=sidecar_remote_closed; proven_committed_chunk=0; proven_highest_observed_chunk=-1"),
            LogLine($"event=filetransfer_leg_started; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:2; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=0; state=checkpoint_pending; reason=sidecar_remote_closed; start_committed_chunk=128; bridge_recovery_generation=0; can_send_data=0"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("none", route["fallback_leg_authority_proof_verdict"]);
        Assert.Equal("0", route["fallback_leg_authority_metadata_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackLegAuthoritySupersededOnly_DoesNotCreateFallbackProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=2"),
            LogLine($"event=filetransfer_fallback_leg_authority_superseded_by_route_hint; session_id=sess_redacted; transfer_id={transferId}; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed; superseded_by_route=file_tuna_v4; superseded_by_protocol_version=4; source=runtime_unlock_route_commit_accepted"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal("none", route["fallback_leg_authority_proof_verdict"]);
        Assert.Equal("none", route["bridge_liveness_integration_verdict"]);
        Assert.Equal("0", route["fallback_leg_authority_metadata_missing_count"]);
        Assert.Equal("file_tuna_v4", route["selected_route_changes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SupersededControlPlaneDeliveryFromOlderLiveEpoch_FailsControlPlaneIsolationWithoutRouteMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=2"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=3"),
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=fallback_checkpoint_request; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=3; bridge_recovery_generation=1; transport_epoch=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; reason=superseded_fallback_checkpoint_request; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy=1; bulk_copy=0; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal("fail", route["control_plane_isolation_verdict"]);
        Assert.Equal("0", route["control_plane_metadata_missing_count"]);
        Assert.Equal("1", route["fallback_control_plane_non_current_leg_generation_send_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fallback_control_plane_stale_generation_treadmill", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ControlPlaneNonCheckpointMessagesWithoutCheckpointId_DoNotFailMetadata()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=fallback_checkpoint_request; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=2; transport_epoch=9; checkpoint_request_id=v6-regular-nkn-state-refresh:42; reason=checkpoint_request; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy=1; bulk_copy=0; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=receiver_state; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=2; transport_epoch=9; checkpoint_request_id=(none); reason=receiver_state_progress; frame_type=filetransfer.receiver_state.v6; control_queue=1; control_copy=0; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=frontier_request; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=2; transport_epoch=9; checkpoint_request_id=(none); reason=frontier_stall_repair_due; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["control_plane_isolation_verdict"]);
        Assert.Equal("0", route["control_plane_metadata_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_CurrentFallbackReceiverStateProofWithoutCheckpointExchange_PassesIsolation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=2"),
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=receiver_state; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=6; bridge_recovery_generation=1; transport_epoch=3; checkpoint_request_id=(none); reason=chunk_batch_committed; frame_type=filetransfer.receiver_state.v6; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=receiver_state; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=6; bridge_recovery_generation=1; transport_epoch=3; checkpoint_request_id=(none); reason=terminal_progress; frame_type=filetransfer.receiver_state.v6; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["fallback_leg_authority_proof_verdict"]);
        Assert.Equal("2", route["fallback_leg_authority_continuous_receiver_state_proof_count"]);
        Assert.Equal("pass", route["control_plane_isolation_verdict"]);
        Assert.Equal("0", route["control_plane_checkpoint_exchange_count"]);
        Assert.Equal("2", route["control_plane_continuous_receiver_state_proof_count"]);
        Assert.Equal("0", route["fallback_stale_generation_treadmill_score"]);
        Assert.Equal("0", route["fallback_control_plane_non_current_leg_generation_send_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_CheckpointFreeReceiverStateWithoutCurrentRoute_DoesNotInventNonCurrentSend()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=receiver_state; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=6; bridge_recovery_generation=1; transport_epoch=3; checkpoint_request_id=(none); reason=chunk_batch_committed; frame_type=filetransfer.receiver_state.v6; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=receiver_state; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=6; bridge_recovery_generation=1; transport_epoch=3; checkpoint_request_id=(none); reason=terminal_progress; frame_type=filetransfer.receiver_state.v6; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["control_plane_isolation_verdict"]);
        Assert.Equal("2", route["control_plane_continuous_receiver_state_proof_count"]);
        Assert.Equal("0", route["fallback_control_plane_non_current_leg_generation_send_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_CurrentFallbackReceiverStateAcrossDirectionRouteEpoch_PassesIsolation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=2"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=3"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=3; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:42; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=receiver_state; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=6; bridge_recovery_generation=1; transport_epoch=3; checkpoint_request_id=(none); reason=chunk_batch_committed; frame_type=filetransfer.receiver_state.v6; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal("pass", route["control_plane_isolation_verdict"]);
        Assert.Equal("1", route["control_plane_continuous_receiver_state_proof_count"]);
        Assert.Equal("0", route["fallback_control_plane_non_current_leg_generation_send_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RecoveredControlPlaneDeliveryFailure_DoesNotFailIsolation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=fallback_checkpoint_request; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=2; transport_epoch=9; checkpoint_request_id=v6-regular-nkn-state-refresh:42; reason=checkpoint_request_timeout; frame_type=filetransfer.frontier_request.v6; control_queue=0; control_copy=0; bulk_copy=0; peer_visible_any=0; accepted_any=0; local_only_rejected=0; control_queue_error=TimeoutException; control_copy_error=OperationCanceledException; bulk_copy_error=OperationCanceledException"),
            LogLine($"event=filetransfer_fallback_checkpoint_exchange_retired; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; retired_leg_id=leg:5; retired_leg_generation=5; current_leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; retired_checkpoint_request_id=v6-regular-nkn-state-refresh:42; current_checkpoint_request_id=(none); reason=delivery_failure_retry"),
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=fallback_checkpoint_request; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=2; transport_epoch=9; checkpoint_request_id=v6-regular-nkn-state-refresh:43; reason=checkpoint_request_retry; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy=1; bulk_copy=0; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=fallback_checkpoint_proof; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=2; transport_epoch=9; checkpoint_request_id=v6-regular-nkn-state-refresh:43; reason=checkpoint_proof_retry; frame_type=filetransfer.receiver_state.v6; control_queue=1; control_copy=0; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["control_plane_isolation_verdict"]);
        Assert.Equal("1", route["control_plane_delivery_failure_count"]);
        Assert.Equal("1", route["control_plane_delivery_failure_recovered_count"]);
        Assert.Equal("0", route["control_plane_liveness_timeout_during_valid_exchange_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ControlPlaneCheckpointProofWithoutCheckpointId_FailsMetadata()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=fallback_checkpoint_proof; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=2; transport_epoch=9; checkpoint_request_id=(none); reason=checkpoint_proof; frame_type=filetransfer.receiver_state.v6; control_queue=1; control_copy=0; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["control_plane_isolation_verdict"]);
        Assert.Equal("1", route["control_plane_metadata_missing_count"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("control-plane metadata missing", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_BoundedStaleFallbackControlPlaneCleanup_DoesNotFailIsolation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=6"),
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=fallback_checkpoint_request; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; leg_generation=6; bridge_recovery_generation=3; transport_epoch=1; checkpoint_request_id=v6-regular-nkn-state-refresh:6; reason=current_checkpoint_request; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy=1; bulk_copy=0; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=filetransfer_fallback_checkpoint_exchange_retired; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; retired_leg_id=leg:5; retired_leg_generation=5; current_leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=5; transport_epoch=1; retired_checkpoint_request_id=v6-regular-nkn-state-refresh:5; current_checkpoint_request_id=v6-regular-nkn-state-refresh:6; reason=new_fallback_leg"),
            LogLine($"event=filetransfer_control_plane_send_retired_stale_generation; direction=outbound; kind=fallback_checkpoint_request; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=5; leg_generation=5; current_leg_generation=6; bridge_recovery_generation=2; current_bridge_recovery_generation=3; transport_epoch=1; current_transport_epoch=1; checkpoint_request_id=v6-regular-nkn-state-refresh:5; current_checkpoint_request_id=v6-regular-nkn-state-refresh:6; reason=stale_leg_generation; frame_type=filetransfer.frontier_request.v6"),
            LogLine($"event=filetransfer_fallback_stale_proof_ignored; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:6; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; transport_epoch=1; checkpoint_request_id=v6-regular-nkn-state-refresh:5; expected_checkpoint_request_id=v6-regular-nkn-state-refresh:6; reason=checkpoint_request_mismatch"),
            LogLine($"event=filetransfer_fallback_stale_proof_ignored; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:6; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; transport_epoch=1; checkpoint_request_id=v6-regular-nkn-state-refresh:5; expected_checkpoint_request_id=v6-regular-nkn-state-refresh:6; reason=checkpoint_request_mismatch"),
            LogLine($"event=filetransfer_fallback_stale_proof_replay_suppressed; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:6; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; transport_epoch=1; checkpoint_request_id=v6-regular-nkn-state-refresh:5; expected_checkpoint_request_id=v6-regular-nkn-state-refresh:6; reason=checkpoint_request_mismatch"),
            LogLine($"event=filetransfer_fallback_stale_proof_replay_suppressed; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:6; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; transport_epoch=1; checkpoint_request_id=v6-regular-nkn-state-refresh:5; expected_checkpoint_request_id=v6-regular-nkn-state-refresh:6; reason=checkpoint_request_mismatch"),
            LogLine($"event=filetransfer_post_tuna_fallback_state_refresh_deferred_discarded; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=checkpoint_request_mismatch; deferred_checkpoint_request_id=v6-regular-nkn-state-refresh:5; current_checkpoint_request_id=v6-regular-nkn-state-refresh:6; deferred_transport_epoch=1; current_transport_epoch=1"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["control_plane_isolation_verdict"]);
        Assert.Equal("5", route["fallback_stale_generation_treadmill_score"]);
        Assert.Equal("6", route["fallback_stale_generation_treadmill_score_limit"]);
        Assert.Equal("1", route["control_plane_stale_send_retired_count"]);
        Assert.Equal("2", route["fallback_stale_proof_ignored_count"]);
        Assert.Equal("2", route["fallback_stale_proof_replay_suppressed_count"]);
        Assert.Equal("1", route["fallback_stale_deferred_replay_discarded_count"]);
        Assert.Equal("0", route["fallback_checkpoint_owner_violation_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackPressureDiagnosticsWithoutSelectedRoute_DoNotFailRouteConsistency()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_fallback_leg_authority_completed; direction=inbound; session_id=sess_redacted; transfer_id={transferId}; leg_generation=7; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=4; bridge_recovery_generation=4; checkpoint_request_id=none; authority_reason=chunk_batch_committed; proof=receiver_state_control_plane"),
            LogLine($"event=filetransfer_control_plane_delivery_result; transport=nkn; direction=inbound; kind=receiver_state; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=7; bridge_recovery_generation=4; transport_epoch=4; checkpoint_request_id=(none); reason=chunk_batch_committed; frame_type=filetransfer.receiver_state.v6; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=filetransfer_control_plane_delivery_observed; direction=inbound; kind=receiver_state; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=7; bridge_recovery_generation=4; transport_epoch=4; checkpoint_request_id=(none); reason=chunk_batch_committed; frame_type=filetransfer.receiver_state.v6; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=filetransfer_post_tuna_fallback_control_plane_pressure_marked; session_id=sess_redacted; transfer_id={transferId}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=7; bridge_recovery_generation=4; transport_epoch=4; checkpoint_request_id=none; kind=receiver_state; reason=chunk_batch_committed; ttl_ms=20000; active_file_transfer_sessions=1"),
            LogLine($"event=filetransfer_post_tuna_fallback_control_plane_pressure_cleared; reason=receiver_state_progress; session_id=sess_redacted; transfer_id={transferId}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=7; bridge_recovery_generation=4; transport_epoch=4; checkpoint_request_id=none; pressure_kind=receiver_state; pressure_reason=chunk_batch_committed; recovery_attempted=0; active_ms=75"),
            LogLine($"event=filetransfer_v6_post_tuna_fallback_control_coalesced; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; frame_kind=receiver_state; frontier_chunk_index=1937; elapsed_ms=368; retry_interval_ms=750; reason=frontier_stall_repair_due"),
            LogLine($"event=filetransfer_fallback_stale_proof_replay_suppressed; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:7; leg_generation=7; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=4; checkpoint_request_id=v6-regular-nkn-state-refresh:1; expected_checkpoint_request_id=(none); reason=checkpoint_not_pending")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.NotEqual("fail", route["route_consistency_verdict"]);
        Assert.Equal("0", route["fallback_control_plane_non_current_leg_generation_send_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_StaleFallbackControlPlaneReplay_IsClassifiedAsTreadmill()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new List<string>
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=6"),
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=fallback_checkpoint_request; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; leg_generation=6; bridge_recovery_generation=3; transport_epoch=1; checkpoint_request_id=v6-regular-nkn-state-refresh:6; reason=current_checkpoint_request; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy=1; bulk_copy=0; peer_visible_any=1; accepted_any=1; local_only_rejected=0")
        };

        for (var i = 0; i < 7; i++)
        {
            lines.Add(LogLine($"event=filetransfer_fallback_stale_proof_ignored; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:6; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; transport_epoch=1; checkpoint_request_id=v6-regular-nkn-state-refresh:5; expected_checkpoint_request_id=(none); reason=checkpoint_not_pending"));
        }

        lines.Add(LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"));
        lines.Add(LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["control_plane_isolation_verdict"]);
        Assert.Equal("7", route["fallback_control_plane_stale_replay_treadmill_count"]);
        Assert.Equal("7", route["fallback_stale_generation_treadmill_score"]);
        Assert.Equal("6", route["fallback_stale_generation_treadmill_score_limit"]);
        Assert.Equal("7", route["fallback_stale_proof_ignored_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fallback_control_plane_stale_generation_treadmill", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackControlLaneStarvationUnderBulkReceive_IsClassified()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new List<string>
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=6", secondsOffset: 1),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; transport_epoch=12; bridge_recovery_generation=3; checkpoint_request_id=v6-regular-nkn-state-refresh:77; authority_reason=post_tuna_fallback_state_refresh_failed", secondsOffset: 2),
            LogLine($"event=filetransfer_post_tuna_fallback_control_plane_pressure_marked; session_id=sess_redacted; transfer_id={transferId}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; leg_generation=6; bridge_recovery_generation=3; transport_epoch=12; checkpoint_request_id=v6-regular-nkn-state-refresh:77; kind=receiverstate; reason=post_tuna_fallback_control_plane_pressure; ttl_ms=20000; active_file_transfer_data_sessions=1; active_file_transfer_data_bytes=268435456", secondsOffset: 3),
            LogLine($"event=nkn_bridge_control_receive_degraded; control_messages_received_since_last=0; bulk_messages_received_since_last=24; control_last_received_age_ms=770000; bulk_last_received_age_ms=11; regular_v4_control_feedback_pressure=0; post_tuna_fallback_control_plane_pressure=1; sample_window_ms=5000", secondsOffset: 20),
            LogLine($"event=nkn_bridge_control_receive_recovery_suppressed; reason=filetransfer_bulk_receive_active; control_messages_received_since_last=0; bulk_messages_received_since_last=24; control_last_received_age_ms=770000; bulk_last_received_age_ms=11; regular_v4_control_feedback_pressure=0; post_tuna_fallback_control_plane_pressure=1; pressure_grace_exhausted=1; protocol_repair_grace_windows=2; protocol_repair_grace_max_age_ms=15000; session_id=sess_redacted; transfer_id={transferId}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; leg_generation=6; transport_epoch=12; bridge_recovery_generation=3; checkpoint_request_id=v6-regular-nkn-state-refresh:77; pressure_kind=receiverstate; pressure_reason=post_tuna_fallback_control_plane_pressure; pressure_recovery_attempted=0", secondsOffset: 21),
            LogLine("event=session_liveness_timeout; session_id=sess_redacted; generation=1; silence_ms=90000; terminal_timeout_ms=18000; role=Helper", secondsOffset: 90),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected", secondsOffset: 91),
            LogLine($"event=file_transfer_inbound_terminal; role=Helper; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)", secondsOffset: 92)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("1", verdict["post_tuna_fallback_control_plane_pressure_marked_count"]);
        Assert.Equal("1", verdict["post_tuna_fallback_bulk_active_suppressed_during_pressure_count"]);
        Assert.Equal("0", verdict["post_tuna_fallback_control_plane_pressure_allowed_recovery_count"]);
        Assert.Equal("fallback_control_lane_starvation_under_bulk_receive", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SameFallbackLegUsingSecondCheckpointIdWithoutRetirement_FailsOwnerGuardrail()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=fallback_checkpoint_request; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=2; transport_epoch=9; checkpoint_request_id=v6-regular-nkn-state-refresh:42; reason=checkpoint_request; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy=1; bulk_copy=0; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=fallback_checkpoint_request; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=2; transport_epoch=9; checkpoint_request_id=v6-regular-nkn-state-refresh:43; reason=checkpoint_request_replay; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy=1; bulk_copy=0; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["control_plane_isolation_verdict"]);
        Assert.Equal("1", route["fallback_checkpoint_owner_violation_count"]);
        Assert.Equal("2", route["fallback_checkpoint_id_count_max_per_leg"]);
        Assert.Contains("5:v6-regular-nkn-state-refresh:42|v6-regular-nkn-state-refresh:43", route["fallback_checkpoint_ids_by_leg_generation"], StringComparison.Ordinal);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fallback_control_plane_stale_generation_treadmill", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SameFallbackLegUsingSecondCheckpointIdAfterRetirement_PassesOwnerGuardrail()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=fallback_checkpoint_request; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=2; transport_epoch=1; checkpoint_request_id=v6-regular-nkn-state-refresh:42; reason=checkpoint_request; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy=1; bulk_copy=0; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=filetransfer_fallback_checkpoint_exchange_retired; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; retired_leg_id=leg:5; retired_leg_generation=5; current_leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=1; retired_checkpoint_request_id=v6-regular-nkn-state-refresh:42; current_checkpoint_request_id=(none); reason=transport_epoch_adopted"),
            LogLine($"event=filetransfer_fallback_leg_transport_epoch_adopted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; previous_transport_epoch=1; transport_epoch=2; bridge_recovery_generation=2; retired_checkpoint_request_id=v6-regular-nkn-state-refresh:42; current_checkpoint_request_id=(none); state=checkpoint_pending; can_send_data=0; reason=receiver_state_sparse_runtime"),
            LogLine($"event=filetransfer_control_plane_delivery_result; kind=fallback_checkpoint_request; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=2; transport_epoch=2; checkpoint_request_id=v6-regular-nkn-state-refresh:43; reason=checkpoint_request_after_transport_epoch_adopted; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy=1; bulk_copy=0; peer_visible_any=1; accepted_any=1; local_only_rejected=0"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["control_plane_isolation_verdict"]);
        Assert.Equal("0", route["fallback_checkpoint_owner_violation_count"]);
        Assert.Equal("2", route["fallback_checkpoint_id_count_max_per_leg"]);
        Assert.Equal("1", route["fallback_checkpoint_exchange_retired_count"]);
        Assert.Contains("5:v6-regular-nkn-state-refresh:42|v6-regular-nkn-state-refresh:43", route["fallback_checkpoint_ids_by_leg_generation"], StringComparison.Ordinal);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackTailReconciliationEvidence_IsReported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=v6-regular-nkn-state-refresh:tail; authority_reason=post_tuna_fallback_tail_reconciliation_failed"),
            LogLine($"event=filetransfer_fallback_tail_zero_credit_breaker; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=tail_reconciliation_zero_credit; feedback_silence_ms=180000; remote_frontier_chunk_index=1975; highest_accepted_chunk_index=1974; transport_backlog_chunks=0; available_credit_chunks=0; in_flight_frames=1; pending_repair_count=1; queued_repair_chunk_count=0; state_refresh_failure_count=3; rebind_generation=2"),
            LogLine($"event=filetransfer_fallback_tail_stale_frontier_retired; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; retired_remote_frontier_chunk_index=1975; highest_accepted_chunk_index=1974; transport_backlog_chunks=0; reason=tail_reconciliation_zero_credit"),
            LogLine($"event=filetransfer_fallback_checkpoint_exchange_retired; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; retired_leg_id=leg:5; retired_leg_generation=5; current_leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; retired_checkpoint_request_id=v6-regular-nkn-state-refresh:tail; current_checkpoint_request_id=(none); reason=tail_reconciliation_forced"),
            LogLine($"event=filetransfer_fallback_tail_reconciliation_requested; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; request_sequence=42; route=post_tuna_fallback_v6; protocol_version=6; leg_generation=5; live_route_epoch=4; transport_epoch=9; checkpoint_request_id=v6-regular-nkn-state-refresh:42; remote_frontier_chunk_index=1975; refresh_hint_chunk_index=1975; highest_accepted_chunk_index=1974; transport_backlog_chunks=0; available_credit_chunks=0; in_flight_frames=1; pending_repair_count=1; queued_repair_chunk_count=0; reason=post_tuna_fallback_tail_reconciliation"),
            LogLine($"event=filetransfer_post_tuna_fallback_state_refresh_send_inflight_retired; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=tail_reconciliation_forced; request_id=v6-regular-nkn-state-refresh:old; replacement_request_id=v6-regular-nkn-state-refresh:42; replacement_priority=state_refresh_tail_reconciliation"),
            LogLine($"event=filetransfer_fallback_tail_reconciliation_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; checkpoint_request_id=v6-regular-nkn-state-refresh:42; proven_committed_chunk=1975; proven_highest_observed_chunk=2100; receiver_committed_chunk=1975; receiver_highest_observed_chunk=2100; receiver_credit_until_chunk_index_exclusive=2231; priority=state_refresh_tail_reconciliation; reason=receiver_state_sparse_runtime"),
            LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; committed_chunk=1975; highest_observed_chunk=2100; state=active; can_send_data=1; reason=fallback_tail_quiescent"),
            LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; committed_chunk=1975; highest_observed_chunk=2100; state=active; can_send_data=1; reason=fallback_tail_quiescent"),
            LogLine($"event=filetransfer_fallback_tail_yield_proof_sent; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=(none); owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|5|2|9|(none); yield_generation=1; peer_visible=1; reason=fallback_tail_paired_yield"),
            LogLine($"event=filetransfer_fallback_tail_yield_proof_received; direction=inbound; peer_direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=(none); owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|5|2|9|(none); peer_owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|5|2|9|(none); yield_generation=1"),
            LogLine($"event=filetransfer_fallback_tail_paired_yield_granted; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=(none); owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|5|2|9|(none); yield_generation=1; reason=fallback_tail_paired_yield"),
            LogLine($"event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=v6-regular-nkn-state-refresh:42; proven_committed_chunk=1975; proven_highest_observed_chunk=2100; reason=receiver_state_sparse_runtime"),
            LogLine($"event=filetransfer_fallback_leg_authority_completed; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=v6-regular-nkn-state-refresh:42; authority_reason=post_tuna_fallback_tail_reconciliation_failed; proof=post_tuna_receiver_state"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["fallback_tail_reconciliation_verdict"]);
        Assert.Equal("1", route["fallback_tail_reconciliation_requested_count"]);
        Assert.Equal("1", route["fallback_tail_reconciliation_accepted_count"]);
        Assert.Equal("1", route["fallback_tail_zero_credit_breaker_count"]);
        Assert.Equal("1", route["fallback_tail_stale_frontier_retired_count"]);
        Assert.Equal("1", route["fallback_tail_state_refresh_send_slot_retired_count"]);
        Assert.Equal("0", route["fallback_tail_reconciliation_metadata_missing_count"]);
        Assert.Equal("pass", route["fallback_tail_quiescence_verdict"]);
        Assert.Equal("2", route["fallback_tail_quiescent_yield_granted_count"]);
        Assert.Equal("1", route["fallback_tail_outbound_yield_granted_count"]);
        Assert.Equal("1", route["fallback_tail_inbound_yield_granted_count"]);
        Assert.Equal("1", route["fallback_tail_yield_proof_sent_count"]);
        Assert.Equal("1", route["fallback_tail_yield_proof_received_count"]);
        Assert.Equal("1", route["fallback_tail_paired_yield_granted_count"]);
        Assert.Equal("0", route["fallback_tail_owner_generation_mismatch_count"]);
        Assert.Equal("pass", route["fallback_tail_paired_yield_verdict"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("pass", verdict["fallback_tail_reconciliation_verdict"]);
        Assert.Equal("pass", verdict["fallback_tail_quiescence_verdict"]);
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_OneSidedFallbackTailYield_FailsPairedYieldProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=(none); authority_reason=post_tuna_fallback_tail_reconciliation_failed"),
            LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; committed_chunk=1975; highest_observed_chunk=2100; state=active; can_send_data=1; reason=fallback_tail_quiescent"),
            LogLine($"event=runtime_unlock_tuna_probe_ack_sent; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; transaction_generation=2; offer_generation=3; tuna_path_lease_generation=2; probe_id=probe-2; accepted=0; reason=inbound_fallback_tail_not_yielded"),
            LogLine($"event=runtime_unlock_precommit_probe_rejected_for_fallback_survival; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; transaction_generation=2; offer_generation=3; tuna_path_lease_generation=2; probe_id=probe-2; received_transport=tuna; reason=inbound_fallback_tail_not_yielded"),
            LogLine($"event=filetransfer_gui_progress_timeout; transfer_id={transferId}; session_id=sess_redacted; reason=normal_to_tuna_live_epoch_missing"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["fallback_tail_quiescence_verdict"]);
        Assert.Equal("1", route["fallback_tail_quiescent_yield_granted_count"]);
        Assert.Equal("1", route["fallback_tail_outbound_yield_granted_count"]);
        Assert.Equal("0", route["fallback_tail_inbound_yield_granted_count"]);
        Assert.Equal("fail", route["fallback_tail_paired_yield_verdict"]);
        Assert.Equal("2", route["runtime_unlock_probe_rejected_by_fallback_owner_count"]);
        Assert.Equal("2", route["runtime_unlock_probe_rejected_by_inbound_fallback_owner_count"]);
    }

    [Theory]
    [InlineData(false, "unresolved")]
    [InlineData(true, "unresolved")]
    [InlineData(false, "duplicate_unresolved")]
    [InlineData(true, "duplicate_unresolved")]
    [InlineData(false, "recovered")]
    [InlineData(true, "recovered")]
    [InlineData(false, "wrong_pairing")]
    [InlineData(true, "wrong_pairing")]
    [InlineData(false, "redacted_without_snapshot")]
    [InlineData(true, "redacted_without_snapshot")]
    [InlineData(true, "terminal_poison")]
    [InlineData(true, "conflicting_duplicate")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PeerYieldAcceptedButLocalYieldBlocked_ClassifiesOneExactIncident(
        bool compact,
        string outcome)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var pairKey = $"sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|2|9|(none)";
        var peerOwnerKey = $"sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|4|2|9|(none)";
        const string peerAuthority = "fta2-fallback_tail_yield-peer000000000000000001";
        const string peerObservation = "peer-observation-1";
        const string peerFingerprint = "peer-fingerprint-1";
        const string localScope = "direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; " +
            "leg_id=leg:5; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; " +
            "live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; " +
            "checkpoint_generation=1; checkpoint_request_id=(none); route_publication_generation=3; " +
            "route_commit_generation=0; runtime_generation=3; terminal_generation=0; " +
            "data_session_identity=data-session-1; data_session_generation=1; log_writer_id=writer-a";
        var pendingLine = LogLine($"event=filetransfer_fallback_tail_peer_proof_pending_decided; {localScope}; authority_id=(none); yield_generation=0; owner_key=local-owner; pair_key={pairKey}; peer_authority_id={peerAuthority}; peer_predecessor_authority_id=(none); peer_yield_generation=1; peer_direction=inbound; peer_owner_key={peerOwnerKey}; peer_pair_key={pairKey}; peer_live_route_epoch=4; peer_leg_generation=4; peer_bridge_recovery_generation=2; peer_transport_epoch=9; peer_checkpoint_request_id=(none); peer_observation_identity={peerObservation}; peer_frame_fingerprint={peerFingerprint}; observed_utc_ms=1000; decision_status=Accepted; reason=fallback_tail_initial_peer_proof_receipt_retained; decision_reason=fallback_tail_initial_peer_proof_receipt_retained");
        var pairingLine = LogLine($"event=filetransfer_fallback_tail_pairing_committed; {localScope}; authority_id=fta2-fallback-tail-yield-local00000000000000001; predecessor_authority_id=(none); yield_generation=1; yield_kind=Quiescent; owner_key=local-owner; pair_key={pairKey}; pairing_state=Paired; pairing_generation=1; local_proof_identity=local-proof-1; local_frame_fingerprint=local-fingerprint-1; peer_authority_id={peerAuthority}; peer_predecessor_authority_id=(none); peer_yield_generation=1; peer_direction=inbound; peer_owner_key={peerOwnerKey}; peer_pair_key={pairKey}; peer_live_route_epoch=4; peer_leg_generation=4; peer_bridge_recovery_generation=2; peer_transport_epoch=9; peer_checkpoint_request_id=(none); peer_observation_identity={peerObservation}; peer_frame_fingerprint={peerFingerprint}; failed_predecessor_authority_id=(none); failed_predecessor_yield_generation=0; failed_successor_authority_id=(none); failed_successor_yield_generation=0; failed_supersession_carried_peer_proof=0; release_kind=FallbackPairedTailYield; release_outcome=Quiescent; decision_status=Accepted");
        var lines = new List<string>
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=(none); authority_reason=post_tuna_fallback_tail_reconciliation_failed"),
            pendingLine,
            LogLine($"event=filetransfer_fallback_tail_yield_proof_received; {localScope}; authority_schema=2; authority_id={peerAuthority}; predecessor_authority_id=(none); peer_direction=inbound; peer_leg_generation=4; peer_live_route_epoch=4; peer_transport_epoch=9; peer_bridge_recovery_generation=2; peer_checkpoint_request_id=(none); owner_key={peerOwnerKey}; pair_key={pairKey}; canonical_pair_key={pairKey}; yield_generation=1"),
            LogLine($"event=filetransfer_fallback_tail_local_yield_blocked; {localScope}; checkpoint_request_id_snapshot=(none); canonical_owner_key=(none); canonical_pair_key=(none); local_owner_key=(none); peer_owner_key=(none); local_pair_key=(none); peer_pair_key=(none); decision_reason=fallback_tail_local_owner_work_pending; source=fallback_tail_peer_yield"),
            LogLine($"event=filetransfer_fallback_tail_local_yield_blocked; {localScope}; checkpoint_request_id_snapshot=(none); canonical_owner_key=(none); canonical_pair_key=(none); local_owner_key=(none); peer_owner_key=(none); local_pair_key=(none); peer_pair_key=(none); decision_reason=fallback_tail_local_owner_work_pending; source=fallback_tail_peer_yield"),
            LogLine($"event=filetransfer_fallback_tail_local_yield_blocked; {localScope}; checkpoint_request_id_snapshot=(none); canonical_owner_key=(none); canonical_pair_key=(none); local_owner_key=(none); peer_owner_key=(none); local_pair_key=(none); peer_pair_key=(none); decision_reason=fallback_tail_local_owner_work_pending; source=fallback_tail_peer_yield"),
            LogLine($"event=filetransfer_fallback_tail_local_yield_blocked; {localScope}; checkpoint_request_id_snapshot=(none); canonical_owner_key=(none); canonical_pair_key=(none); local_owner_key=(none); peer_owner_key=(none); local_pair_key=(none); peer_pair_key=(none); decision_reason=fallback_tail_local_owner_work_pending; source=fallback_tail_peer_yield")
        };
        if (outcome == "redacted_without_snapshot")
        {
            lines[2] = pendingLine.Replace(
                $"peer_authority_id={peerAuthority}",
                "peer_authority_id=[redacted]",
                StringComparison.Ordinal);
        }
        else if (outcome == "duplicate_unresolved")
        {
            lines.Insert(3, pendingLine);
        }
        else if (outcome == "conflicting_duplicate")
        {
            lines.Insert(
                3,
                pendingLine.Replace(
                    "observed_utc_ms=1000",
                    "observed_utc_ms=1001",
                    StringComparison.Ordinal));
            lines.Add(pairingLine);
        }
        else if (outcome == "recovered")
        {
            lines.Add(pairingLine);
        }
        else if (outcome == "wrong_pairing")
        {
            lines.Add(pairingLine.Replace(
                "data_session_generation=1",
                "data_session_generation=2",
                StringComparison.Ordinal));
        }
        else if (outcome == "terminal_poison")
        {
            lines.Add(LogLine($"event=file_transfer_outbound_terminal; {localScope}; role=helpee; state=Failed; error_code=peer_disconnected"));
            lines.Add(pairingLine);
        }
        lines.Add(LogLine($"event=filetransfer_gui_progress_timeout; transfer_id={transferId}; session_id=sess_redacted; reason=normal_to_tuna_live_epoch_missing"));
        if (outcome != "terminal_poison")
        {
            lines.Add(LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected"));
        }
        lines.Add(LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)"));

        var result = compact
            ? await RunAnalyzeFixtureAsync(
                lines,
                environment: new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                })
            : await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        if (!compact)
        {
            Assert.Equal("1", route["fallback_tail_yield_proof_received_count"]);
            Assert.Equal("4", route["fallback_tail_local_yield_blocked_count"]);
        }
        var exactIncident = outcome != "redacted_without_snapshot";
        var recovered = outcome == "recovered";
        Assert.Equal(exactIncident ? "4" : "0", route["fallback_tail_local_yield_missing_raw_count"]);
        Assert.Equal(exactIncident ? "1" : "0", route["fallback_tail_local_yield_missing_distinct_count"]);
        Assert.Equal(recovered ? "1" : "0", route["fallback_tail_local_yield_missing_recovered_count"]);
        Assert.Equal(exactIncident && !recovered ? "1" : "0", route["fallback_tail_local_yield_missing_unresolved_count"]);
        if (exactIncident && !recovered)
        {
            Assert.Equal("fail", route["fallback_tail_paired_yield_verdict"]);
        }

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        if (recovered)
        {
            Assert.NotEqual("fallback_tail_local_yield_missing", verdict["recovery_failure_class"]);
            Assert.NotEqual("fallback_tail_local_yield_missing", verdict["first_proof_chain_failure_class"]);
        }
        else if (exactIncident)
        {
            Assert.Equal("fallback_tail_local_yield_missing", verdict["recovery_failure_class"]);
            Assert.Equal("fallback_tail_local_yield_missing", verdict["first_proof_chain_failure_class"]);
            Assert.Equal("filetransfer_fallback_tail_local_yield_blocked", verdict["first_proof_chain_failure_event"]);
            Assert.Equal("fallback_tail_local_owner_work_pending", verdict["first_proof_chain_failure_reason"]);
            Assert.Equal(
                "route_transition_checkpoint_progress_left_transport_handoff_current",
                verdict["failure_variant"]);
        }
        Assert.Equal(exactIncident ? "4" : "0", verdict["fallback_tail_local_yield_missing_raw_count"]);
        Assert.Equal(exactIncident ? "1" : "0", verdict["fallback_tail_local_yield_missing_distinct_count"]);
        Assert.Equal(recovered ? "1" : "0", verdict["fallback_tail_local_yield_missing_recovered_count"]);
        Assert.Equal(exactIncident && !recovered ? "1" : "0", verdict["fallback_tail_local_yield_missing_unresolved_count"]);
        if (compact)
        {
            if (exactIncident && !recovered)
            {
                Assert.Equal("route_runtime", verdict["acceptance_failure_class"]);
                Assert.Equal("route_runtime", verdict["semantic_failure_domain"]);
                Assert.Equal("fail", verdict["runtime_correctness_verdict"]);
            }
        }
        else if (!recovered)
        {
            Assert.Equal("4", verdict["classification_fallback_tail_local_yield_blocked_count"]);
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PeerYieldAcceptedButProofReplayPending_ClassifiesReplayUnsettled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var pairKey = $"sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|2|9|(none)";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=(none); authority_reason=post_tuna_fallback_tail_reconciliation_failed"),
            LogLine($"event=filetransfer_fallback_tail_yield_proof_received; direction=inbound; peer_direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; peer_leg_generation=4; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=(none); owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|4|2|9|(none); pair_key={pairKey}; peer_owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|4|2|9|(none); peer_pair_key={pairKey}; yield_generation=1"),
            LogLine($"event=filetransfer_fallback_tail_local_yield_blocked; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=(none); checkpoint_request_id_snapshot=(none); canonical_owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|5|2|9|(none); canonical_pair_key={pairKey}; local_owner_key=(none); peer_owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|4|2|9|(none); local_pair_key=(none); peer_pair_key={pairKey}; decision_reason=fallback_proof_replay_pending; source=fallback_tail_peer_yield"),
            LogLine($"event=filetransfer_gui_progress_timeout; transfer_id={transferId}; session_id=sess_redacted; reason=normal_to_tuna_live_epoch_missing"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("1", route["fallback_tail_local_yield_blocked_count"]);
        Assert.Equal("1", route["fallback_tail_proof_replay_local_yield_blocked_count"]);
        Assert.Equal("fail", route["fallback_tail_paired_yield_verdict"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("post_fallback_frontier_replay_unsettled", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["classification_fallback_tail_proof_replay_local_yield_blocked_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackTailOwnerGenerationMismatch_FailsFallbackAuthority()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=(none); authority_reason=post_tuna_fallback_tail_reconciliation_failed"),
            LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; committed_chunk=1975; highest_observed_chunk=2100; state=active; can_send_data=1; reason=fallback_tail_quiescent"),
            LogLine($"event=filetransfer_fallback_tail_yield_proof_received; direction=inbound; peer_direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; peer_leg_generation=4; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=(none); owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|5|2|9|(none); pair_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|2|9|(none); peer_owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|4|2|9|(none); yield_generation=1"),
            LogLine($"event=filetransfer_fallback_tail_owner_generation_mismatch; direction=inbound; peer_direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=(none); peer_live_route_epoch=4; peer_leg_generation=4; peer_transport_epoch=9; peer_bridge_recovery_generation=2; peer_checkpoint_request_id=(none); expected_owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|5|2|9|(none); expected_pair_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|2|9|(none); peer_owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|4|2|9|(none); peer_pair_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|3|9|(none); yield_generation=1"),
            LogLine($"event=filetransfer_gui_progress_timeout; transfer_id={transferId}; session_id=sess_redacted; reason=normal_to_tuna_live_epoch_missing"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("1", route["fallback_tail_owner_generation_mismatch_count"]);
        Assert.Equal("fail", route["fallback_tail_paired_yield_verdict"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fallback_tail_owner_generation_mismatch", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackTailDirectionLocalLegDifferenceWithPairKey_PassesFallbackAuthority()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var pairKey = $"sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|2|9|(none)";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=(none); authority_reason=post_tuna_fallback_tail_reconciliation_failed"),
            LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; committed_chunk=1975; highest_observed_chunk=2100; state=active; can_send_data=1; reason=fallback_tail_quiescent"),
            LogLine($"event=filetransfer_fallback_tail_yield_proof_received; direction=outbound; peer_direction=inbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; peer_leg_generation=4; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=(none); owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|4|2|9|(none); pair_key={pairKey}; yield_generation=1"),
            LogLine($"event=filetransfer_fallback_tail_paired_yield_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=(none); owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|5|2|9|(none); pair_key={pairKey}; yield_generation=1; reason=fallback_tail_paired_yield"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none)"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("0", route["fallback_tail_owner_generation_mismatch_count"]);
        Assert.Equal("pass", route["fallback_tail_paired_yield_verdict"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackZeroCreditTailWithoutReconciliation_IsClassified()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=filetransfer_v6_regular_nkn_state_refresh_requested; transfer_id={transferId}; session_id=sess_redacted; reason=post_tuna_fallback_zero_credit_tail; request_sequence=41; priority=state_refresh; feedback_silence_ms=180000; refresh_cooldown_ms=1000; stale_credit_recovery_delay_ms=30000; transport_epoch=9; epoch_state=bridge_recovery_completed_awaiting_proof; remote_frontier_chunk_index=1975; refresh_hint_chunk_index=1975; highest_accepted_chunk_index=1975; transport_backlog_chunks=0; available_credit_chunks=0; credit_ceiling_chunk_index=1975; in_flight_frames=1; pending_repair_count=1; queued_repair_chunk_count=0; rebind_generation=2"),
            LogLine($"event=filetransfer_post_tuna_fallback_state_refresh_receive_recovery_requested; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=post_tuna_fallback_state_refresh_send_timeout; request_id=v6-regular-nkn-state-refresh:41; stale_inflight_recovery=0; stale_credit_recovery=0; tail_reconciliation=0; state_refresh_send_timeout=1; recovery_reason=post_tuna_fallback_state_refresh_failed; failure_count=4; feedback_silence_ms=190000; remote_frontier_chunk_index=1975; highest_accepted_chunk_index=1975; transport_backlog_chunks=0; available_credit_chunks=0; credit_ceiling_chunk_index=1975; rebind_generation=2; bridge_recovery_policy=post_tuna_fallback_strict"),
            LogLine($"event=session_liveness_timeout; session_id=sess_redacted; generation=1; silence_ms=90000; terminal_timeout_ms=18000; role=Helper"),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected"),
            LogLine($"event=file_transfer_inbound_terminal; role=Helper; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["fallback_tail_reconciliation_verdict"]);
        Assert.Equal("1", route["fallback_tail_normal_zero_credit_state_refresh_count"]);
        Assert.Equal("0", route["fallback_tail_reconciliation_requested_count"]);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fallback_tail_reconciliation", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["classification_fallback_tail_normal_zero_credit_state_refresh_count"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("post-Tuna fallback zero-credit tail stall ended without tail reconciliation proof", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackZeroCreditTailWithCleanServiceTerminal_IsNotClassified()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=4"),
            LogLine($"event=filetransfer_v6_regular_nkn_state_refresh_requested; transfer_id={transferId}; session_id=sess_redacted; reason=feedback_stalled_with_inflight; request_sequence=41; priority=state_refresh; feedback_silence_ms=30099; refresh_cooldown_ms=5000; stale_credit_recovery_delay_ms=30000; transport_epoch=2; epoch_state=frontier_repair_only; remote_frontier_chunk_index=1963; refresh_hint_chunk_index=1963; highest_accepted_chunk_index=2474; transport_backlog_chunks=512; available_credit_chunks=0; credit_ceiling_chunk_index=2475; in_flight_frames=7; pending_repair_count=1; queued_repair_chunk_count=0; rebind_generation=1"),
            LogLine($"event=transfer_terminal; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; file_size_bytes=67108864; bytes_transferred=67108864; chunks_transferred=3121; chunk_count=3121; error_code=(none); reason=Transfer complete.; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active"),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; file_size_bytes=67108864; bytes_transferred=67108864; chunks_transferred=3121; chunk_count=3121; error_code=(none); reason=Transfer complete.; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("none", route["fallback_tail_reconciliation_verdict"]);
        Assert.Equal("1", route["fallback_tail_normal_zero_credit_state_refresh_count"]);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackLegAuthorityMissingMetadata_IsReported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=0; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["fallback_leg_authority_proof_verdict"]);
        Assert.Equal("1", route["fallback_leg_authority_metadata_missing_count"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("fallback leg authority metadata missing", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_BridgeLivenessDefersThenFallbackProofPasses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=filetransfer_fallback_leg_authority_bridge_recovery_requested; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=session_liveness_timeout_deferred_for_current_filetransfer_recovery; session_id=sess_redacted; transfer_id={transferId}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; leg_generation=2; bridge_recovery_generation=1; transport_epoch=7; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed; state=bridge_recovery_completed_awaiting_proof; bridge_recovery_requested=1; bridge_recovery_started=1; bridge_recovery_completed=1; silence_ms=18000; liveness_deferral_deadline_utc_ms=999999"),
            LogLine($"event=bridge_receive_stall_recovery_receive_resumed; session_id=sess_redacted; exit_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; proven_committed_chunk=128; proven_highest_observed_chunk=160; reason=receiver_state"),
            LogLine($"event=filetransfer_fallback_leg_authority_completed; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed; proof=post_tuna_receiver_state"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["bridge_liveness_integration_verdict"]);
        Assert.Equal("1", route["session_liveness_deferred_for_current_recovery_count"]);
        Assert.Equal("0", route["session_liveness_timeout_during_valid_recovery_count"]);
        Assert.Equal("1", route["bridge_recovery_receive_resumed_count"]);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("pass", verdict["bridge_liveness_integration_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_BridgeLivenessDeferralWithLaterFallbackProofPasses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=1; bridge_recovery_generation=1; checkpoint_request_id=(none); authority_reason=post_tuna_fallback_bridge_restart_send_failure"),
            LogLine($"event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=1; bridge_recovery_generation=1; checkpoint_request_id=(none); proven_committed_chunk=64; proven_highest_observed_chunk=96; reason=receiver_state_sparse_runtime"),
            LogLine($"event=session_liveness_timeout_deferred_for_current_filetransfer_recovery; session_id=sess_redacted; transfer_id={transferId}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; leg_generation=2; bridge_recovery_generation=1; transport_epoch=1; checkpoint_request_id=none; authority_reason=post_tuna_fallback_bridge_restart_send_failure; state=bridgerecoverycompletedawaitingproof; bridge_recovery_requested=0; bridge_recovery_started=0; bridge_recovery_completed=1; silence_ms=20835; liveness_deferral_deadline_utc_ms=999999"),
            LogLine($"event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none); proven_committed_chunk=128; proven_highest_observed_chunk=160; reason=receiver_state_sparse_runtime"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["bridge_liveness_integration_verdict"]);
        Assert.Equal("1", route["session_liveness_deferred_for_current_recovery_count"]);
        Assert.Equal("0", route["bridge_liveness_stale_deferral_count"]);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("pass", verdict["bridge_liveness_integration_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LivenessTimeoutDuringValidFallbackRecoveryFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=filetransfer_fallback_leg_authority_bridge_recovery_requested; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=session_liveness_timeout; session_id=sess_redacted; generation=1; silence_ms=20000; terminal_timeout_ms=18000; role=Helper"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("fail", verdict["bridge_liveness_integration_verdict"]);
        Assert.Equal("1", verdict["session_liveness_timeout_during_valid_recovery_count"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("session liveness timeout during valid fallback recovery authority", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_BridgeExhaustedSiblingDeferralSuppressedThenReceiveProofPasses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=bridge_receive_stall_recovery_exhausted_deferred_for_active_filetransfer_progress; session_id=sess_redacted; transfer_id={transferId}; direction=outbound; bridge_reason=post_tuna_fallback_stale_state_refresh_send_retired_recovery_failed; progress_bytes=18903040; deferral_count=1; deferral_limit=1; deferral_ms=35000; liveness_deferral_deadline_utc_ms=999999"),
            LogLine($"event=bridge_receive_stall_recovery_exhausted_suppressed_by_sibling_deferral; session_id=sess_redacted; transfer_id={transferId}; recovery_family=post_tuna_fallback_recovery; bridge_reason=post_tuna_fallback_state_refresh_failed_recovery_failed; route=post_tuna_fallback_v6; protocol_version=6; leg_generation=0; bridge_recovery_generation=0; source=active_filetransfer_progress; liveness_deferral_deadline_utc_ms=999999"),
            LogLine($"event=bridge_receive_stall_recovery_receive_resumed; session_id=sess_redacted; exit_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; proven_committed_chunk=128; proven_highest_observed_chunk=160; reason=receiver_state"),
            LogLine($"event=filetransfer_fallback_leg_authority_completed; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed; proof=post_tuna_receiver_state"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["bridge_liveness_integration_verdict"]);
        Assert.Equal("1", route["bridge_exhausted_sibling_deferral_suppressed_count"]);
        Assert.Equal("0", route["bridge_exhausted_terminal_during_valid_deferral_count"]);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("pass", verdict["bridge_liveness_integration_verdict"]);
        Assert.Equal("1", verdict["bridge_exhausted_sibling_deferral_suppressed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_BridgeExhaustedTerminalDuringSiblingDeferralFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=bridge_receive_stall_recovery_exhausted_deferred_for_active_filetransfer_progress; session_id=sess_redacted; transfer_id={transferId}; direction=outbound; bridge_reason=post_tuna_fallback_stale_state_refresh_send_retired_recovery_failed; progress_bytes=18903040; deferral_count=1; deferral_limit=1; deferral_ms=35000; liveness_deferral_deadline_utc_ms=999999"),
            LogLine($"event=peer_liveness_visible_disconnect; reason=receive_stall_recovery_exhausted; bridge_reason=post_tuna_fallback_state_refresh_failed_recovery_failed; role=Helper; state=Connected; transport_state=Connected; session_id=sess_redacted"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["bridge_liveness_integration_verdict"]);
        Assert.Equal("1", route["bridge_exhausted_terminal_during_valid_deferral_count"]);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fail", verdict["bridge_liveness_integration_verdict"]);
        Assert.Equal("1", verdict["bridge_exhausted_terminal_during_valid_deferral_count"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("bridge exhausted terminalized during valid sibling deferral", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_MeasuredFallbackSliceOnly_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareMeasuredFallbackFixture());

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("post_tuna_fallback_v6", route["selected_routes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_MeasuredFallbackLateSetupCancelFeedback_IsNotHardFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareMeasuredFallbackFixture().ToList();
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.Insert(
            firstTerminalIndex,
            LogLine("event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id=[redacted]; session_id=sess_redacted; frame_type=filetransfer.cancel.v4; first_lane=control; second_lane=bulk; first_error=OperationCanceledException; second_error=OperationCanceledException"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("0", stability["hard_failure_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackRecoveredBridgeQueueClear_ReturnsWarningNotFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture());
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=filetransfer_transport_epoch_started_while_unavailable; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; reason=transport_recovered_unproven; target_transport=regular_nkn", secondsOffset: 20),
                LogLine("event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=5", secondsOffset: 30),
                LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=17; frames_enqueued=22; payload_bytes_sent=847447; payload_bytes_per_second=423724; payload_bytes_enqueued=850237; payload_bytes_enqueued_per_second=425119; send_failures=0; queue_clears=5; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=3; worker_utilization_percent=19; sample_window_ms=2000", secondsOffset: 60)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("recovered_post_tuna_fallback_bridge_clear", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Contains("recovered post-Tuna fallback bridge queue clear", File.ReadAllText(Path.Combine(result.ArtifactDir, "stability-gates-summary.txt")), StringComparison.Ordinal);
        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("0", stability["hard_failure_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackV6SendTimeoutChurn_ReturnsSpecificExternalWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture());
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=filetransfer_v6_chunk_batch_send_timeout; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; start_chunk_index=128; chunk_count=32; timeout_ms=2500; transport_epoch=3", secondsOffset: 20),
                LogLine("event=filetransfer_v6_post_tuna_fallback_send_timeout_requeued; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; requeued_chunk_count=32; exact_frontier_requeued_chunk_count=1; frontier_chunk_index=96", secondsOffset: 50),
                LogLine("event=filetransfer_v6_post_tuna_fallback_send_timeout_frontier_repair_queued; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index=96; removed_prepared_in_flight_count=32; queued_chunk_count=1", secondsOffset: 90)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("repair-reorder-summary.txt", verdict["next_artifact"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Equal("fallback_v6_send_timeout_churn", verdict["warning_kinds"]);
        Assert.Equal("incident", verdict["warning_cap_count_unit"]);
        Assert.Equal("fallback_v6_send_timeout_churn:1", verdict["warning_kind_counts"]);
        Assert.Equal("fallback_v6_send_timeout_churn:3", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("fallback_v6_send_timeout_churn:post_tuna_fallback", verdict["warning_cap_contexts"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Contains("post-Tuna fallback V6 send timeout churn", File.ReadAllText(Path.Combine(result.ArtifactDir, "stability-gates-summary.txt")), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackRepeatedFrontierRepairSameGap_CountsAsSingleIncident()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture());
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        var churnLines = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            churnLines.Add(LogLine($"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index=512; requested_chunk_count=1; post_tuna_fallback_survival=1; duplicate_request={i}", secondsOffset: 30));
        }

        lines.InsertRange(firstTerminalIndex, churnLines);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn:1", verdict["warning_kind_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:10", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:post_tuna_fallback", verdict["warning_cap_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackFrontierRepairSameGapAcrossBuckets_CountsAsSingleIncident()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture());
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        var churnLines = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var secondsOffset = 30 + (i * 45);
            var eventText = (i % 4) switch
            {
                0 => "event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; start_chunk_index=512; requested_chunk_count=32; post_tuna_fallback_survival=1",
                1 => "event=filetransfer_v6_post_tuna_fallback_frontier_rescue_requested; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index=512; requested_chunk_count=32; rescue_step=3",
                2 => "event=filetransfer_v6_post_tuna_fallback_frontier_rescue_widened; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index=512; previous_rescue_step=2; rescue_step=3",
                _ => "event=filetransfer_v6_frontier_request_duplicate_ignored; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; first_start_chunk_index=512; first_chunk_count=32"
            };
            churnLines.Add(LogLine(eventText, secondsOffset: secondsOffset));
        }

        lines.InsertRange(firstTerminalIndex, churnLines);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn:1", verdict["warning_kind_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:10", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:post_tuna_fallback", verdict["warning_cap_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackUnknownFrontierUsesRepairRequestIdForIncidentKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture(), terminalOffsetSeconds: 30);
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=transport_epoch; transport_epoch=1; repair_request_id=v6-frontier:1:1773:1; recovery_mode=frontier_repair_only; start_chunk_index=1773; requested_chunk_count=1; total_requested_chunk_count=1; range_count=1; post_tuna_fallback_survival=1", secondsOffset: 5),
                LogLine("event=filetransfer_v6_post_tuna_fallback_frontier_rescue_requested; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=transport_epoch; frontier_chunk_index=-1; rescue_step=0; rescue_request_count=1; transport_epoch=1; repair_request_id=v6-frontier:1:1773:1; recovery_mode=frontier_repair_only; requested_chunk_count=1; total_requested_chunk_count=1; range_count=1", secondsOffset: 5),
                LogLine("event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=retry; transport_epoch=1; repair_request_id=v6-frontier:2008:2; recovery_mode=regular_nkn_frontier_stall_control_bulk; start_chunk_index=2008; requested_chunk_count=1; total_requested_chunk_count=1; range_count=1; post_tuna_fallback_survival=1", secondsOffset: 10),
                LogLine("event=filetransfer_v6_post_tuna_fallback_frontier_rescue_requested; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=retry; frontier_chunk_index=2008; rescue_step=0; rescue_request_count=1; transport_epoch=1; repair_request_id=v6-frontier:2008:2; recovery_mode=regular_nkn_frontier_stall_control_bulk; requested_chunk_count=1; total_requested_chunk_count=1; range_count=1", secondsOffset: 10),
                LogLine("event=filetransfer_v6_post_tuna_fallback_frontier_rescue_widened; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index=2008; previous_rescue_step=0; rescue_step=1; rescue_request_count=2", secondsOffset: 11),
                LogLine("event=filetransfer_v6_frontier_request_duplicate_ignored; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; first_start_chunk_index=2008; first_chunk_count=1", secondsOffset: 11),
                LogLine("event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=retry; transport_epoch=1; repair_request_id=v6-frontier:3094:4; recovery_mode=regular_nkn_frontier_stall_control_bulk; start_chunk_index=3094; requested_chunk_count=1; total_requested_chunk_count=1; range_count=1; post_tuna_fallback_survival=1", secondsOffset: 18),
                LogLine("event=filetransfer_v6_post_tuna_fallback_frontier_rescue_requested; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=retry; frontier_chunk_index=3094; rescue_step=0; rescue_request_count=1; transport_epoch=1; repair_request_id=v6-frontier:3094:4; recovery_mode=regular_nkn_frontier_stall_control_bulk; requested_chunk_count=1; total_requested_chunk_count=1; range_count=1", secondsOffset: 18),
                LogLine("event=filetransfer_v6_post_tuna_fallback_frontier_rescue_widened; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index=3094; previous_rescue_step=0; rescue_step=1; rescue_request_count=2", secondsOffset: 19),
                LogLine("event=filetransfer_v6_frontier_request_duplicate_ignored; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; first_start_chunk_index=3094; first_chunk_count=1", secondsOffset: 19),
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("repair-reorder-summary.txt", verdict["next_artifact"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_cap_exempted_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn:3", verdict["warning_kind_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:10", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:post_tuna_fallback", verdict["warning_cap_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackRepeatedReceiverStateChurnSameBurst_CountsAsSingleIncident()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture());
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        var churnLines = new List<string>();
        for (var i = 0; i < 300; i++)
        {
            var eventName = i % 2 == 0
                ? "filetransfer_v6_receiver_state_deferred"
                : "filetransfer_v6_receiver_state_coalesced";
            churnLines.Add(LogLine($"event={eventName}; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=frontier_stalled; next_chunk_index={512 + i}; highest_received_chunk_index={640 + i}", secondsOffset: 30 + (i % 60)));
        }

        lines.InsertRange(firstTerminalIndex, churnLines);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("fallback_receiver_state_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("fallback_receiver_state_churn:1", verdict["warning_kind_counts"]);
        Assert.Equal("fallback_receiver_state_churn:10", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("fallback_receiver_state_churn:post_tuna_fallback", verdict["warning_cap_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackReceiverStateRateOnlyOverCap_WithTerminalProofReturnsWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture(), terminalOffsetSeconds: 30);
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        var churnLines = new List<string>();
        for (var i = 0; i < 300; i++)
        {
            var chunk = i % 2 == 0 ? 512 : 768;
            var secondsOffset = i % 2 == 0 ? 0 : 2;
            var eventName = i % 2 == 0
                ? "filetransfer_v6_receiver_state_deferred"
                : "filetransfer_v6_receiver_state_coalesced";
            churnLines.Add(LogLine($"event={eventName}; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=frontier_stalled; next_chunk_index={chunk}; highest_received_chunk_index={chunk + 32}", secondsOffset: secondsOffset));
        }

        lines.InsertRange(firstTerminalIndex, churnLines);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Equal("fallback_receiver_state_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("fallback_receiver_state_churn", verdict["warning_cap_exempted_kinds"]);
        Assert.Equal("fallback_receiver_state_churn:2", verdict["warning_kind_counts"]);
        Assert.Equal("fallback_receiver_state_churn:10", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("fallback_receiver_state_churn:post_tuna_fallback", verdict["warning_cap_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackRepeatedBridgeClearSameBurst_CountsAsSingleIncident()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture());
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=filetransfer_transport_epoch_started_while_unavailable; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; reason=transport_recovered_unproven; target_transport=regular_nkn", secondsOffset: 20),
                LogLine("event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=5", secondsOffset: 30),
                LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=8; frames_enqueued=13; payload_bytes_sent=3731; payload_bytes_enqueued=6523; send_failures=0; queue_clears=5; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; configured_concurrency=4; effective_concurrency=4; sample_window_ms=2000", secondsOffset: 45),
                LogLine("event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=3", secondsOffset: 60),
                LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=6; frames_enqueued=9; payload_bytes_sent=3471; payload_bytes_enqueued=5327; send_failures=0; queue_clears=3; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; configured_concurrency=4; effective_concurrency=4; sample_window_ms=2000", secondsOffset: 75),
                LogLine("event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=1", secondsOffset: 90),
                LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=4; frames_enqueued=5; payload_bytes_sent=1946; payload_bytes_enqueued=2436; send_failures=0; queue_clears=1; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; configured_concurrency=4; effective_concurrency=4; sample_window_ms=2000", secondsOffset: 105)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("recovered_post_tuna_fallback_bridge_clear", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("recovered_post_tuna_fallback_bridge_clear:1", verdict["warning_kind_counts"]);
        Assert.Equal("recovered_post_tuna_fallback_bridge_clear:6", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("recovered_post_tuna_fallback_bridge_clear:post_tuna_fallback", verdict["warning_cap_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackFrontierRepairCountOverCapRateUnderCap_RemainsWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture(), terminalOffsetSeconds: 240);
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        var churnLines = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            churnLines.Add(LogLine($"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index={512 + i}; requested_chunk_count=1; post_tuna_fallback_survival=1; duplicate_request=0", secondsOffset: 10 + (i * 40)));
            churnLines.Add(LogLine($"event=filetransfer_v6_frontier_request_duplicate_ignored; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; first_start_chunk_index={512 + i}; first_chunk_count=1", secondsOffset: 11 + (i * 40)));
        }

        lines.InsertRange(firstTerminalIndex, churnLines);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("repair-reorder-summary.txt", verdict["next_artifact"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_cap_exempted_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn:5", verdict["warning_kind_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:10", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:post_tuna_fallback", verdict["warning_cap_contexts"]);
        Assert.Contains("post-Tuna fallback frontier repair churn", File.ReadAllText(Path.Combine(result.ArtifactDir, "stability-gates-summary.txt")), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackFrontierRepairRateAtCountLimitWithTerminalProof_ReturnsWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture(), terminalOffsetSeconds: 30);
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        var churnLines = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var frontier = 512 + (i % 3);
            var eventText = i % 2 == 0
                ? $"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index={frontier}; requested_chunk_count=1; post_tuna_fallback_survival=1; duplicate_request=0"
                : $"event=filetransfer_v6_frontier_request_duplicate_ignored; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; first_start_chunk_index={frontier}; first_chunk_count=1";
            churnLines.Add(LogLine(eventText, secondsOffset: 5 + i));
        }

        lines.InsertRange(firstTerminalIndex, churnLines);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("repair-reorder-summary.txt", verdict["next_artifact"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_cap_exempted_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn:3", verdict["warning_kind_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:10", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:post_tuna_fallback", verdict["warning_cap_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackFrontierRepairCountAndRateOverCap_WithCleanFallbackProofReturnsWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture(), terminalOffsetSeconds: 30);
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; leg_id=leg:2; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=1; checkpoint_request_id=v6-regular-nkn-state-refresh:1; proven_committed_chunk=512; proven_highest_observed_chunk=511; state=recovery_active; reason=receiver_state_sparse_runtime", secondsOffset: 4),
            ]);
        var churnLines = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var frontier = 512 + (i % 4);
            var eventText = i % 2 == 0
                ? $"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index={frontier}; requested_chunk_count=1; post_tuna_fallback_survival=1; duplicate_request=0"
                : $"event=filetransfer_v6_frontier_request_duplicate_ignored; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; first_start_chunk_index={frontier}; first_chunk_count=1";
            churnLines.Add(LogLine(eventText, secondsOffset: 5 + i));
        }

        lines.InsertRange(firstTerminalIndex, churnLines);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("repair-reorder-summary.txt", verdict["next_artifact"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_cap_exempted_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn:4", verdict["warning_kind_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:10", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("fallback_frontier_repair_churn:post_tuna_fallback", verdict["warning_cap_contexts"]);
        Assert.Equal("pass", verdict["fallback_leg_authority_proof_verdict"]);
        Assert.Equal("pass", verdict["bridge_liveness_integration_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackFrontierRepairRateOverCap_ReturnsExternalChurnFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture(), terminalOffsetSeconds: 30);
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        var churnLines = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var frontier = 512 + (i % 4);
            var eventText = i % 2 == 0
                ? $"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index={frontier}; requested_chunk_count=1; post_tuna_fallback_survival=1; duplicate_request=0"
                : $"event=filetransfer_v6_frontier_request_duplicate_ignored; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; first_start_chunk_index={frontier}; first_chunk_count=1";
            churnLines.Add(LogLine(eventText, secondsOffset: 5 + i));
        }

        lines.InsertRange(firstTerminalIndex, churnLines);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_EXTERNAL_TRANSPORT_CHURN", verdict["verdict"]);
        Assert.Equal("stability-gates-summary.txt", verdict["next_artifact"]);
        Assert.NotEqual("0", verdict["hard_failure_count"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exempted_kinds"]);
        Assert.Equal("fallback_frontier_repair_churn:post_tuna_fallback", verdict["warning_cap_exceeded_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ProgressTimeoutSparseGapClassifiesFrontierRepairStalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_sparse_progress_timeout";
        var lines = new[]
        {
            LogLine($"event=filetransfer_profile_selected; transport=nkn; transfer_id={transferId}; session_id=sess_a; protocol_version=6; profile=v4_live; target_window_bytes=16777216; granted_window_bytes=16777216"),
            LogLine($"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; chunk_index=0-2; raw_chunk_bytes=64512; chunk_count=3"),
            LogLine($"event=filetransfer_receiver_sparse_mode_selected; transfer_id={transferId}; session_id=sess_a; reason=seekable_readwrite_destination; can_read=1; can_write=1; can_seek=1"),
            LogLine($"event=filetransfer_v4_sender_throughput_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=516096; raw_bytes_per_second=258048; chunk_frames_sent=0; batch_frames_sent=8; chunk_count_sent=24; chunks_accepted_for_transport=2860; remote_next_expected_chunk_index=2507; remote_granted_until_chunk_index_exclusive=2890; remote_granted_window_bytes=8232960; sent_cache_chunk_count=400; sent_cache_bytes=8601600; send_wait_count=12; repair_send_count=2"),
            LogLine($"event=filetransfer_v4_receiver_throughput_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=516096; raw_bytes_received_per_second=258048; contiguous_bytes_committed=516096; contiguous_bytes_committed_per_second=258048; pending_chunk_count=0; pending_bytes=0; next_chunk_index=2507; highest_received_chunk_index=2860; late_arrival_distance=353; oldest_gap_age_ms=42000; granted_until_chunk_index_exclusive=2890; granted_window_bytes=8232960; write_batch_count=8; write_batch_bytes=516096; write_duration_ms=0; sparse_mode=1; sparse_write_bytes_per_second=258048; sparse_written_ahead_bytes=7587072; sparse_gap_count=1"),
            LogLine($"event=filetransfer_v4_gap_stall_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; gap_start_chunk_index=2507; highest_received_chunk_index=2860; late_arrival_distance=353; stall_duration_ms=42294; pending_bytes=0; granted_window_bytes=8232960"),
            LogLine($"event=filetransfer_frontier_gap_repair_requested; transfer_id={transferId}; session_id=sess_a; start_chunk_index=2507; requested_chunk_count=32; gap_stall_age_ms=12000; late_arrival_distance=353; highest_received_chunk_index=2860; granted_until_chunk_index_exclusive=2890; granted_window_bytes=8232960; reason=proactive_frontier_gap"),
            LogLine($"event=filetransfer_frontier_gap_repair_requested; transfer_id={transferId}; session_id=sess_a; start_chunk_index=2507; requested_chunk_count=32; gap_stall_age_ms=18000; late_arrival_distance=353; highest_received_chunk_index=2860; granted_until_chunk_index_exclusive=2890; granted_window_bytes=8232960; reason=proactive_frontier_gap"),
            LogLine($"event=filetransfer_v4_receiver_feedback_enqueued; transfer_id={transferId}; session_id=sess_a; mode=pump; frame_type=filetransfer.receiver_state.v6; queue_depth=2; coalesced_count=1"),
            LogLine($"event=filetransfer_v4_receiver_feedback_sent; transfer_id={transferId}; session_id=sess_a; mode=pump; frame_type=filetransfer.receiver_state.v6; queue_depth=1; enqueue_to_send_age_ms=1030; send_duration_ms=649"),
            LogLine("event=nkn_bridge_bulk_send_summary; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; frames_sent=200; send_failures=0; queue_clears=0; payload_bytes_sent=65536000; payload_bytes_per_second=3276800; send_p95_ms=4; configured_concurrency=4; effective_concurrency=4; in_flight_max=3"),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 120s; total_wait_s=379; progress_timeout_seconds=120; receiver_next_chunk=2507; receiver_highest_chunk=2860; progress_events=4729"),
            LogLine($"event=filetransfer_artifact_slice_summary; transfer_id={transferId}; artifact_slice_start_reason=live_soak_failure_context; artifact_slice_end_reason=gui_progress_timeout")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("INCONCLUSIVE_PROGRESS_TIMEOUT", verdict["verdict"]);
        Assert.Contains("progress_timeout_with_receiver_gap_stall", File.ReadAllText(Path.Combine(result.ArtifactDir, "stability-gates-summary.txt")), StringComparison.Ordinal);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("sparse_frontier_gap_repair_stalled", decomposition["likely_limiter"]);
        Assert.Equal("1", decomposition["receiver_feedback_pump_active_count"]);
        Assert.Equal("1", decomposition["slice_started_after_pump_start"]);
        Assert.Equal("1", decomposition["gui_progress_timeout_count"]);
        Assert.Equal("1", decomposition["terminal_missing_after_progress_timeout"]);
        Assert.Equal("1", decomposition["progress_timeout_with_receiver_gap_stall"]);
        Assert.Equal("live_soak_failure_context", decomposition["artifact_slice_start_reason"]);
        Assert.Equal("gui_progress_timeout", decomposition["artifact_slice_end_reason"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackProgressTimeout_EmitsFallbackDiagnostics()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_timeout_diagnostics";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transport=nkn; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; bridge_recovery_policy=post_tuna_fallback_strict; selection_reason=post_tuna_fallback"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transport=nkn; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; bridge_recovery_policy=post_tuna_fallback_strict; selection_reason=post_tuna_fallback"),
            LogLine($"event=filetransfer_v6_receiver_state_sent; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; contiguous_committed_chunk_index=100; durable_received_highest_chunk_index=190; oldest_gap_age_ms=45000"),
            LogLine($"event=filetransfer_v6_chunk_batch_send_timeout; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; start_chunk_index=101; chunk_count=32; timeout_ms=2500; transport_epoch=3"),
            LogLine($"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index=101; requested_chunk_count=1; post_tuna_fallback_survival=1"),
            LogLine($"event=filetransfer_v6_receiver_state_deferred; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; next_chunk_index=101; highest_received_chunk_index=190"),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 120s; total_wait_s=379; progress_timeout_seconds=120; receiver_next_chunk=101; receiver_highest_chunk=190; progress_events=4888"),
            LogLine($"event=filetransfer_artifact_slice_summary; transfer_id={transferId}; artifact_slice_start_reason=live_soak_failure_context; artifact_slice_end_reason=gui_progress_timeout")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("INCONCLUSIVE_PROGRESS_TIMEOUT", verdict["verdict"]);
        Assert.Equal("terminal_evidence_missing", verdict["fallback_v6_terminal_missing_reason"]);
        Assert.Equal("100", verdict["fallback_v6_last_committed_chunk_index"]);
        Assert.Equal("190", verdict["fallback_v6_highest_observed_chunk_index"]);
        Assert.Equal("45000", verdict["fallback_v6_oldest_unrecovered_gap_age_ms"]);
        Assert.Equal("1", verdict["fallback_v6_chunk_send_timeout_count"]);
        Assert.Equal("1", verdict["fallback_v6_frontier_request_count"]);
        Assert.Equal("1", verdict["fallback_v6_receiver_state_deferred_count"]);
        Assert.Equal("1", verdict["fallback_v6_sender_still_repairing"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FrontierRequestDispatch")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PeerVisibleFallbackFrontierRequestWithoutSenderDispatch_FailsAtDispatchEdge()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_frontier_request_dispatch_missing";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=4", secondsOffset: 0),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none)", secondsOffset: 1),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=1; transport_epoch=2; checkpoint_request_id=(none); reason=transport_epoch; frame_type=filetransfer.frontier_request.v6; repair_request_id=v6-frontier:2:1149:1; request_correlation_id=frq-missing; first_start_chunk_index=1149; first_chunk_count=1; range_count=1; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0", secondsOffset: 2),
            LogLine($"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id={transferId}; session_id=sess_a; reason=transport_epoch; transport_epoch=2; repair_request_id=v6-frontier:2:1149:1; recovery_mode=frontier_repair_only; start_chunk_index=1149; requested_chunk_count=1; total_requested_chunk_count=1; range_count=1; post_tuna_fallback_survival=1", secondsOffset: 2),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 180s; total_wait_s=218; progress_timeout_seconds=180; receiver_next_chunk=1149; receiver_highest_chunk=1148; progress_events=297", secondsOffset: 190)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fallback_frontier_request_dispatch_missing", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["fallback_frontier_request_delivery_accepted_count"]);
        Assert.Equal("1", verdict["fallback_frontier_request_dispatch_missing_count"]);
        Assert.Equal("fail", verdict["fallback_frontier_request_dispatch_verdict"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FrontierRequestDispatch")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaPayloadMismatchOutranksFallbackFrontierRequestDispatchMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_payload_mismatch_priority";
        var fileTunaAuthoritySuffix =
            $"direction=outbound; transfer_id={transferId}; session_id=sess_a; route=file_tuna_v4; protocol_version=4; live_route_epoch=3; runtime_generation=7; terminal_generation=0; leg_generation=4; route_publication_generation=9; route_commit_generation=0; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; intended_target_transport=tuna; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; live_route_epoch=3", secondsOffset: 0),
            LogLine($"event=filetransfer_v4_chunk_batch_sent; {fileTunaAuthoritySuffix}; repair_request_key=none; start_chunk_index=0; batch_chunk_count=2; raw_bytes=65536; repair_send=0; mixed_screenshare=0; screen_share_active=0; screen_share_degraded=0; screen_share_observed=0; repair_delivery_mode=none; repair_delivery_escalation_reason=none; repair_batch_send_attempt=1; repair_batch_send_attempt_count=1", secondsOffset: 1),
            LogLine($"event=filetransfer_data_path_send_result; {fileTunaAuthoritySuffix}; actual_transport=regular_nkn; delivery_transport=regular_nkn; lane=bulk; sent_via_acceleration=0; force_regular_nkn_bulk=0; payload_bytes=65536; raw_payload_bytes=65536; batch_count=1; batch_chunk_count=2; batch_profile=v4_batch; frame_type=filetransfer.chunk_batch.v4; delivery_result=accepted; proof_direction=none; chunk_index=0-1; start_chunk_index=0; last_end_chunk_exclusive=2; frame_chunk_count=2", secondsOffset: 2),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=4", secondsOffset: 3),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none)", secondsOffset: 4),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=1; transport_epoch=2; checkpoint_request_id=(none); reason=transport_epoch; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0", secondsOffset: 5),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 180s; total_wait_s=218; progress_timeout_seconds=180; receiver_next_chunk=1149; receiver_highest_chunk=1148; progress_events=297", secondsOffset: 190)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("1", route["file_tuna_v4_data_path_mismatch_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("file_tuna_data_path_mismatch", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["fallback_frontier_request_dispatch_missing_count"]);
        Assert.Equal("fail", verdict["fallback_frontier_request_dispatch_verdict"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FrontierRequestDispatch")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackFrontierRequestDispatchMissingOutranksFileTunaNonPayloadDiagnostic()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_frontier_request_dispatch_diagnostic_priority";
        var fileTunaAuthoritySuffix =
            $"direction=outbound; transfer_id={transferId}; session_id=sess_a; route=file_tuna_v4; protocol_version=4; live_route_epoch=3; runtime_generation=7; terminal_generation=0; leg_generation=4; route_publication_generation=9; route_commit_generation=0; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; intended_target_transport=tuna; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; live_route_epoch=3", secondsOffset: 0),
            LogLine($"event=filetransfer_data_path_receive_result; {fileTunaAuthoritySuffix}; actual_transport=regular_nkn; delivery_transport=regular_nkn; lane=control; sent_via_acceleration=0; force_regular_nkn_bulk=0; payload_bytes=0; raw_payload_bytes=0; batch_count=0; batch_chunk_count=0; frame_type=filetransfer.state.v4; delivery_result=accepted; proof_direction=none", secondsOffset: 1),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=4", secondsOffset: 3),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none)", secondsOffset: 4),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=1; transport_epoch=2; checkpoint_request_id=(none); reason=transport_epoch; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0", secondsOffset: 5),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 180s; total_wait_s=218; progress_timeout_seconds=180; receiver_next_chunk=1149; receiver_highest_chunk=1148; progress_events=297", secondsOffset: 190)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.NotEqual("fail", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("0", route["file_tuna_v4_data_path_mismatch_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fallback_frontier_request_dispatch_missing", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["fallback_frontier_request_dispatch_missing_count"]);
        Assert.Equal("fail", verdict["fallback_frontier_request_dispatch_verdict"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FrontierRequestDispatch")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PeerVisibleFallbackFrontierRequestWithOnlyStaleDispatch_FailsAtDispatchEdge()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_frontier_request_stale_dispatch";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=3", secondsOffset: 0),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=3; leg_generation=4; bridge_recovery_generation=1; transport_epoch=1; checkpoint_request_id=(none); reason=old_frontier; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0", secondsOffset: 1),
            LogLine($"event=filetransfer_v6_frontier_request_receive_dispatched; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=3; leg_generation=4; transport_epoch=1; current_transport_epoch=1; bridge_recovery_generation=1; checkpoint_request_id=(none); repair_request_id=v6-frontier:1:400:1; first_start_chunk_index=400; first_chunk_count=1; range_count=1; received_transport=regular_nkn; decision_reason=queued_for_sender_repair", secondsOffset: 2),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=2; transport_epoch=2; checkpoint_request_id=(none); reason=current_frontier; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0", secondsOffset: 3),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 180s; total_wait_s=218; progress_timeout_seconds=180; receiver_next_chunk=1149; receiver_highest_chunk=1148; progress_events=297", secondsOffset: 190)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fallback_frontier_request_dispatch_missing", verdict["recovery_failure_class"]);
        Assert.Equal("2", verdict["fallback_frontier_request_delivery_accepted_count"]);
        Assert.Equal("1", verdict["fallback_frontier_request_receive_dispatched_count"]);
        Assert.Equal("1", verdict["fallback_frontier_request_dispatch_missing_count"]);
        Assert.Equal("fail", verdict["fallback_frontier_request_dispatch_verdict"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FrontierRequestDispatch")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PeerVisibleFallbackFrontierRequestWithDifferentRepairKeyDispatch_FailsAtDispatchEdge()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_frontier_request_unrelated_dispatch";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=4", secondsOffset: 0),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none)", secondsOffset: 1),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=3; bridge_recovery_generation=1; transport_epoch=2; checkpoint_request_id=(none); reason=transport_epoch; frame_type=filetransfer.frontier_request.v6; repair_request_id=v6-frontier:2:1149:1; request_correlation_id=frq-accepted; first_start_chunk_index=1149; first_chunk_count=1; range_count=1; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0", secondsOffset: 2),
            LogLine($"event=filetransfer_v6_frontier_request_receive_dispatched; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; transport_epoch=2; current_transport_epoch=2; bridge_recovery_generation=2; checkpoint_request_id=v6-regular-nkn-state-refresh:6; repair_request_id=v6-frontier:2:1150:1; request_correlation_id=frq-unrelated; first_start_chunk_index=1150; first_chunk_count=1; range_count=1; received_transport=regular_nkn; decision_reason=queued_for_sender_repair", secondsOffset: 3),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 180s; total_wait_s=218; progress_timeout_seconds=180; receiver_next_chunk=1149; receiver_highest_chunk=1148; progress_events=297", secondsOffset: 190)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fallback_frontier_request_dispatch_missing", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["fallback_frontier_request_delivery_accepted_count"]);
        Assert.Equal("1", verdict["fallback_frontier_request_receive_dispatched_count"]);
        Assert.Equal("1", verdict["fallback_frontier_request_dispatch_missing_count"]);
        Assert.Equal("fail", verdict["fallback_frontier_request_dispatch_verdict"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FrontierRequestDispatch")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PeerVisibleFallbackFrontierRequestWithRepairKeyOnlyMismatchedOwner_FailsAtDispatchEdge()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_frontier_request_repair_key_only_dispatch";
        const string repairKey = "v6-frontier:2:1149:1";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=4", secondsOffset: 0),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none)", secondsOffset: 1),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=3; bridge_recovery_generation=1; transport_epoch=2; checkpoint_request_id=(none); reason=transport_epoch; frame_type=filetransfer.frontier_request.v6; repair_request_id={repairKey}; first_start_chunk_index=1149; first_chunk_count=1; range_count=1; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0", secondsOffset: 2),
            LogLine($"event=filetransfer_v6_frontier_request_receive_dispatched; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; transport_epoch=2; current_transport_epoch=2; bridge_recovery_generation=2; checkpoint_request_id=v6-regular-nkn-state-refresh:6; repair_request_id={repairKey}; first_start_chunk_index=1149; first_chunk_count=1; range_count=1; received_transport=regular_nkn; decision_reason=queued_for_sender_repair", secondsOffset: 3),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 180s; total_wait_s=218; progress_timeout_seconds=180; receiver_next_chunk=1149; receiver_highest_chunk=1148; progress_events=297", secondsOffset: 190)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fallback_frontier_request_dispatch_missing", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["fallback_frontier_request_delivery_accepted_count"]);
        Assert.Equal("1", verdict["fallback_frontier_request_receive_dispatched_count"]);
        Assert.Equal("1", verdict["fallback_frontier_request_dispatch_missing_count"]);
        Assert.Equal("fail", verdict["fallback_frontier_request_dispatch_verdict"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FrontierRequestDispatch")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PeerVisibleFallbackFrontierRequestWithSenderDispatchAndProgress_PassesDispatchEdge()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_frontier_request_dispatch_pass";
        const string repairKey = "v6-frontier:2:1149:1";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=4", secondsOffset: 0),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none)", secondsOffset: 1),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=3; bridge_recovery_generation=1; transport_epoch=2; checkpoint_request_id=(none); reason=transport_epoch; frame_type=filetransfer.frontier_request.v6; repair_request_id={repairKey}; request_correlation_id=frq-correlated; first_start_chunk_index=1149; first_chunk_count=1; range_count=1; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0", secondsOffset: 2),
            LogLine($"event=filetransfer_v6_frontier_request_receive_dispatch_attempted; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; transport_epoch=2; current_transport_epoch=2; bridge_recovery_generation=2; checkpoint_request_id=v6-regular-nkn-state-refresh:6; repair_request_id={repairKey}; request_correlation_id=frq-correlated; first_start_chunk_index=1149; first_chunk_count=1; range_count=1; received_transport=regular_nkn; decision_reason=core_receive", secondsOffset: 3),
            LogLine($"event=filetransfer_v6_frontier_request_receive_dispatched; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; transport_epoch=2; current_transport_epoch=2; bridge_recovery_generation=2; checkpoint_request_id=v6-regular-nkn-state-refresh:6; repair_request_id={repairKey}; request_correlation_id=frq-correlated; first_start_chunk_index=1149; first_chunk_count=1; range_count=1; received_transport=regular_nkn; decision_reason=queued_for_sender_repair", secondsOffset: 3),
            LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; direction=outbound; transfer_id={transferId}; session_id=sess_a; repair_request_key={repairKey}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none); frontier_chunk_index=1149; requested_chunk_count=1; decision_generation=7", secondsOffset: 4),
            LogLine($"event=filetransfer_fallback_frontier_repair_dispatched; direction=outbound; transfer_id={transferId}; session_id=sess_a; repair_request_key={repairKey}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none); frontier_chunk_index=1149; requested_chunk_count=1; decision_generation=7", secondsOffset: 5),
            LogLine($"event=filetransfer_fallback_frontier_repair_progress_observed; direction=inbound; transfer_id={transferId}; session_id=sess_a; repair_request_key={repairKey}; reason=frontier_advanced; committed_frontier_before=1149; committed_frontier_after=1150; requested_chunk_count=1", secondsOffset: 6),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 180s; total_wait_s=218; progress_timeout_seconds=180; receiver_next_chunk=1150; receiver_highest_chunk=1149; progress_events=300", secondsOffset: 190)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("fallback_frontier_request_dispatch_missing", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["fallback_frontier_request_delivery_accepted_count"]);
        Assert.Equal("1", verdict["fallback_frontier_request_receive_dispatched_count"]);
        Assert.Equal("0", verdict["fallback_frontier_request_dispatch_missing_count"]);
        Assert.Equal("pass", verdict["fallback_frontier_request_dispatch_verdict"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FrontierRequestDispatch")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PeerVisibleFallbackFrontierRequestWithObservedCoordinatorRepairDispatch_PassesDispatchEdge()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_frontier_observed_repair_dispatch";
        const string controlAuthority = "fta2-fallback_control_plane-control";
        const string repairAuthority = "fta2-repair_replay-repair";
        const string repairRequestId = "v6-frontier:1:522:5";
        const string correlationId = "frq-correlated-observed";
        const string repairKey = "fallback-frontier:522:1:frng-correlated";
        const string scope = $"direction=inbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=3; bridge_recovery_generation=1; transport_epoch=1; checkpoint_request_id=(none)";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=2", secondsOffset: 0),
            LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; authority_schema=2; authority_id={repairAuthority}; predecessor_authority_id=(none); {scope}; runtime_generation=3; terminal_generation=0; bridge_decision_generation=0; route_publication_generation=4; route_commit_generation=0; repair_generation=8; repair_request_key={repairKey}; range_fingerprint=frng-correlated; frontier_chunk_index=522; range_count=1; requested_chunk_count=1; source_kind=ReceiverRequest; primary_protocol_repair_request_id={repairRequestId}; primary_request_correlation_id={correlationId}; priority=frontier; recovery_mode=frontier_repair_only; stage=request", secondsOffset: 1),
            LogLine($"event=filetransfer_control_plane_delivery_result; authority_schema=2; authority_id={controlAuthority}; control_plane_generation=9; {scope}; kind=frontier_request; reason=transport_epoch; frame_type=filetransfer.frontier_request.v6; repair_request_id={repairRequestId}; request_correlation_id={correlationId}; first_start_chunk_index=522; first_chunk_count=1; range_count=1; recovery_mode=frontier_repair_only; priority=frontier; peer_visible_any=1; accepted_any=1", secondsOffset: 2),
            LogLine($"event=filetransfer_control_plane_delivery_observed; authority_schema=2; authority_id={controlAuthority}; control_plane_generation=9; {scope}; kind=frontier_request; reason=transport_epoch; frame_type=filetransfer.frontier_request.v6; repair_request_id={repairRequestId}; request_correlation_id={correlationId}; repair_request_key={repairKey}; range_fingerprint=frng-correlated; first_start_chunk_index=522; first_chunk_count=1; range_count=1; requested_chunk_count=1; recovery_mode=frontier_repair_only; priority=frontier; peer_visible_any=1; accepted_any=1", secondsOffset: 3),
            LogLine($"event=filetransfer_fallback_frontier_repair_dispatched; authority_schema=2; authority_id={repairAuthority}; predecessor_authority_id=(none); {scope}; runtime_generation=3; terminal_generation=0; bridge_decision_generation=0; route_publication_generation=4; route_commit_generation=0; repair_generation=8; repair_request_key={repairKey}; range_fingerprint=frng-correlated; frontier_chunk_index=522; range_count=1; requested_chunk_count=1; source_kind=ReceiverRequest; primary_protocol_repair_request_id={repairRequestId}; primary_request_correlation_id={correlationId}; priority=frontier; recovery_mode=frontier_repair_only; stage=receiver_request_send; dispatched_chunk_count=1; dispatch_observed=1", secondsOffset: 4),
            LogLine($"event=filetransfer_fallback_frontier_repair_contract_settled; authority_schema=2; authority_id={repairAuthority}; predecessor_authority_id=(none); {scope}; runtime_generation=3; terminal_generation=0; bridge_decision_generation=0; route_publication_generation=4; route_commit_generation=0; repair_generation=8; repair_request_key={repairKey}; range_fingerprint=frng-correlated; frontier_chunk_index=522; range_count=1; requested_chunk_count=1; source_kind=ReceiverRequest; primary_protocol_repair_request_id={repairRequestId}; primary_request_correlation_id={correlationId}; priority=frontier; recovery_mode=frontier_repair_only; settlement_outcome=ProgressObserved", secondsOffset: 5),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; session_id=sess_a; reason=no useful data progress for 180s", secondsOffset: 190)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("0", verdict["fallback_frontier_request_dispatch_missing_count"]);
        Assert.Equal("pass", verdict["fallback_frontier_request_dispatch_verdict"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FrontierRequestDispatch")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PeerVisibleFallbackFrontierRequestWithCoalescedDispatchProof_PassesDispatchEdge()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_frontier_request_coalesced_dispatch_pass";
        const string repairRequestId = "v6-frontier:3:5733:351";
        const string repairKey = "v6-frontier:5733:150";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; live_route_epoch=4", secondsOffset: 0),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=3; bridge_recovery_generation=1; checkpoint_request_id=(none)", secondsOffset: 1),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=1; transport_epoch=3; checkpoint_request_id=(none); reason=frontier_stall_repair_due; frame_type=filetransfer.frontier_request.v6; repair_request_id={repairRequestId}; request_correlation_id=frq-coalesced; first_start_chunk_index=5733; first_chunk_count=1; range_count=1; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0", secondsOffset: 2),
            LogLine($"event=filetransfer_v6_frontier_request_receive_dispatch_coalesced; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; transport_epoch=3; current_transport_epoch=3; bridge_recovery_generation=1; checkpoint_request_id=(none); route_publication_generation=8; repair_request_id={repairRequestId}; repair_request_key={repairKey}; request_correlation_id=frq-coalesced; first_start_chunk_index=5733; first_chunk_count=1; range_count=1; received_transport=post_tuna_fallback_control_plane; decision_reason=same_frontier_rescue_unsettled", secondsOffset: 3),
            LogLine($"event=filetransfer_fallback_frontier_repair_stalled; direction=inbound; transfer_id={transferId}; session_id=sess_a; repair_request_key={repairKey}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; transport_epoch=3; bridge_recovery_generation=1; checkpoint_request_id=(none); frontier_chunk_index=5733; durable_received_highest_chunk_index=5732; requested_chunk_count=1; attempt_count=2; frontier_stall_age_ms=30000", secondsOffset: 4),
            LogLine($"event=filetransfer_fallback_frontier_repair_settled_stalled; direction=inbound; transfer_id={transferId}; session_id=sess_a; repair_request_key={repairKey}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; runtime_generation=10; terminal_generation=0; leg_generation=5; transport_epoch=3; current_transport_epoch=3; bridge_recovery_generation=1; checkpoint_request_id=(none); route_publication_generation=8; repair_request_id={repairRequestId}; request_correlation_id=frq-coalesced; reason=post_tuna_fallback_v6_frontier_rescue_without_progress; frontier_chunk_index=5733; durable_received_highest_chunk_index=5732; requested_chunk_count=1; attempt_count=2; frontier_stall_age_ms=30000; settlement=stalled; decision_reason=post_tuna_fallback_frontier_repair_settled_stalled", secondsOffset: 5),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 180s; total_wait_s=218; progress_timeout_seconds=180; receiver_next_chunk=5733; receiver_highest_chunk=5732; progress_events=297", secondsOffset: 190)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("fallback_frontier_request_dispatch_missing", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["fallback_frontier_request_delivery_accepted_count"]);
        Assert.Equal("1", verdict["fallback_frontier_request_receive_dispatch_coalesced_count"]);
        Assert.Equal("0", verdict["fallback_frontier_request_dispatch_missing_count"]);
        Assert.Equal("pass", verdict["fallback_frontier_request_dispatch_verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("1", route["fallback_frontier_repair_settled_stalled_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ProactiveFrontierRepair_PopulatesRepairSummary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_frontier_repair")
            .Append(LogLine("event=filetransfer_frontier_gap_repair_eligible; transfer_id=transfer_frontier_repair; session_id=sess_a; repair_request_key=100:1; attempt_count=1; range_count=1; start_chunk_index=100; requested_chunk_count=1; gap_stall_age_ms=900; late_arrival_distance=48; highest_received_chunk_index=148; granted_until_chunk_index_exclusive=148; granted_window_bytes=1032192; min_gap_ms=500; repeat_ms=500; max_repair_chunks=32; proactive_repair_pressure_state=benign_grace; proactive_repair_age_ms=0; same_frontier_unfilled_ms=0; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_expanded"))
            .Append(LogLine("event=filetransfer_frontier_gap_repair_requested; transfer_id=transfer_frontier_repair; session_id=sess_a; repair_request_key=100:1; attempt_count=1; range_count=1; start_chunk_index=100; requested_chunk_count=1; gap_stall_age_ms=900; late_arrival_distance=48; highest_received_chunk_index=148; granted_until_chunk_index_exclusive=148; granted_window_bytes=1032192; reason=proactive_frontier_gap; proactive_repair_pressure_state=benign_grace; proactive_repair_age_ms=0; same_frontier_unfilled_ms=0; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_expanded"))
            .Append(LogLine("event=filetransfer_frontier_gap_repair_sender_received; transfer_id=transfer_frontier_repair; session_id=sess_a; repair_request_key=100:1; range_count=1; requested_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101; scheduled_chunk_count=1; remote_next_expected_chunk_index=100; chunks_accepted_for_transport=148; skipped_obsolete_count=0; skipped_future_count=0; skipped_out_of_bounds_count=0"))
            .Append(LogLine("event=filetransfer_frontier_gap_repair_sender_scheduled; transfer_id=transfer_frontier_repair; session_id=sess_a; repair_request_key=100:1; range_count=1; requested_chunk_count=1; scheduled_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101; queue_depth=1; remote_next_expected_chunk_index=100; chunks_accepted_for_transport=148"))
            .Append(LogLine("event=filetransfer_frontier_gap_repair_sender_sent; transfer_id=transfer_frontier_repair; session_id=sess_a; repair_request_key=100:1; range_count=1; requested_chunk_count=1; sent_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101; remote_next_expected_chunk_index=100; chunks_accepted_for_transport=148; skipped_obsolete_count=0; skipped_future_count=0; skipped_out_of_bounds_count=0"))
            .Append(LogLine("event=filetransfer_frontier_gap_repair_filled; transfer_id=transfer_frontier_repair; session_id=sess_a; repair_request_key=100:1; start_chunk_index=100; requested_chunk_count=1; request_to_fill_ms=450; next_chunk_index=149; highest_received_chunk_index=149; committed_chunk_count=49; sparse_written_ahead_bytes=0; same_frontier_unfilled_ms=0"))
            .Append(LogLine("event=filetransfer_frontier_gap_repair_skipped; transfer_id=transfer_frontier_repair; session_id=sess_a; reason=duplicate_recent; start_chunk_index=100; requested_chunk_count=1; gap_stall_age_ms=1200; late_arrival_distance=48; highest_received_chunk_index=148; granted_until_chunk_index_exclusive=148; granted_window_bytes=1032192; min_gap_ms=500; repeat_ms=500; max_repair_chunks=32; proactive_repair_pressure_state=benign_grace; proactive_repair_age_ms=300; same_frontier_unfilled_ms=300; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_expanded"))
            .Append(LogLine("event=filetransfer_frontier_gap_repair_suppressed; transfer_id=transfer_frontier_repair; session_id=sess_a; reason=duplicate_recent; start_chunk_index=100; requested_chunk_count=1; gap_stall_age_ms=1200; late_arrival_distance=48; highest_received_chunk_index=148; granted_until_chunk_index_exclusive=148; granted_window_bytes=1032192; proactive_repair_pressure_state=benign_grace; proactive_repair_age_ms=300; same_frontier_unfilled_ms=300; proactive_repair_grace_ms=2500; grant_policy_after_repair=healthy_expanded"))
            .Append(LogLine("event=filetransfer_proactive_frontier_repair_state_reset; transfer_id=transfer_frontier_repair; session_id=sess_a; reason=frontier_advanced; start_chunk_index=100; requested_chunk_count=1; next_chunk_index=101; highest_received_at_repair=148; current_highest_received_chunk_index=148; proactive_repair_age_ms=350; same_frontier_unfilled_ms=0"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_RECOVERED_PRESSURE", verdict["verdict"]);
        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["proactive_frontier_repair_eligible_count"]);
        Assert.Equal("1", repair["proactive_frontier_repair_requested_count"]);
        Assert.Equal("1", repair["proactive_frontier_repair_sender_received_count"]);
        Assert.Equal("1", repair["proactive_frontier_repair_sender_scheduled_count"]);
        Assert.Equal("1", repair["proactive_frontier_repair_sender_sent_count"]);
        Assert.Equal("1", repair["proactive_frontier_repair_filled_count"]);
        Assert.Equal("450", repair["max_frontier_repair_request_to_fill_ms"]);
        Assert.Equal("1", repair["proactive_frontier_repair_skipped_count"]);
        Assert.Equal("1", repair["proactive_frontier_repair_skipped_duplicate_recent_count"]);
        Assert.Equal("1", repair["proactive_frontier_repair_suppressed_count"]);
        Assert.Equal("1", repair["stale_proactive_repair_state_reset_count"]);
        Assert.Equal("0", repair["benign_gap_skip_limited_policy_count"]);
        Assert.Equal("1200", repair["max_proactive_frontier_repair_gap_age_ms"]);
        Assert.Equal("4", repair["proactive_repair_benign_count"]);
        Assert.Equal("0", repair["proactive_repair_hard_limited_count"]);
        Assert.Equal("300", repair["max_proactive_repair_age_ms"]);
        Assert.Equal("300", repair["max_same_frontier_unfilled_ms"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PrimaryRegularNknQuietCheckpointFeedbackCanceledAfterCompletion_IsRecoverable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_primary_quiet_checkpoint_feedback_canceled";
        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedV4TransferFixture(transferId));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine($"event=filetransfer_bridge_recovery_policy_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; runtime_profile=PrimaryRegularNknBulkV6; bridge_recovery_policy=primary_regular_nkn_quiet; selection_reason=conservative_regular_nkn", secondsOffset: 10),
                LogLine($"event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; protocol_version=6; runtime_profile=PrimaryRegularNknBulkV6; credit_profile=v4_sparse; frame_profile=v6; recovery_profile=regular_nkn_quiet; bridge_recovery_policy=primary_regular_nkn_quiet; activation=primary_regular_nkn", secondsOffset: 10),
                LogLine($"event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.frontier_request.v6; first_lane=control; second_lane=bulk; first_error=OperationCanceledException; second_error=OperationCanceledException", secondsOffset: 20),
                LogLine($"event=filetransfer_primary_regular_nkn_frontier_feedback_failed_recoverable; direction=outbound; transfer_id={transferId}; session_id=sess_a; reason=checkpoint_sync_send_timeout; recovery_action=request_bridge_recovery; request_id=v6-regular-nkn-checkpoint-sync:7; frame_type=filetransfer.frontier_request.v6; recovery_mode=regular_nkn_checkpoint_sync; priority=checkpoint_sync; failure_count=2; bridge_recovery_policy=primary_regular_nkn_quiet", secondsOffset: 20),
                LogLine($"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_send_timeout; direction=outbound; transfer_id={transferId}; session_id=sess_a; request_id=v6-regular-nkn-checkpoint-sync:7; timeout_ms=7500", secondsOffset: 30),
                LogLine($"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_receive_recovery_requested; direction=outbound; transfer_id={transferId}; session_id=sess_a; reason=checkpoint_sync_send_timeout; request_id=v6-regular-nkn-checkpoint-sync:7; failure_count=2; bridge_recovery_policy=primary_regular_nkn_quiet", secondsOffset: 30),
                LogLine("event=nkn_bridge_receive_stall_recovery_started; connect_key=test; stall_reason=control_receive_stalled; attempt=1; max_restarts=4; consecutive_zero_receive_windows=2; frames_sent_since_last=6; control_last_received_age_ms=12000; bulk_last_received_age_ms=90", secondsOffset: 40),
                LogLine("event=nkn_bridge_receive_stall_recovery_completed; connect_key=test; recovery_count=1; elapsed_ms=1200; control_ready=1; bulk_ready=1", secondsOffset: 80),
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("0", stability["hard_failure_count"]);

        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["v4_feedback_both_failed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackFrontierRequestCanceledDuringRecovery_IsRecoverable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture().ToList());
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine($"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; frontier_chunk_index=3113; requested_chunk_count=1; post_tuna_fallback_survival=1", secondsOffset: 20),
                LogLine($"event=filetransfer_v6_regular_nkn_state_refresh_send_timeout; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; request_id=v6-regular-nkn-state-refresh:test; timeout_ms=7500", secondsOffset: 30),
                LogLine($"event=filetransfer_post_tuna_fallback_state_refresh_receive_recovery_requested; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=post_tuna_fallback_state_refresh_send_timeout; request_id=v6-regular-nkn-state-refresh:test; failure_count=1; feedback_silence_ms=1200; remote_frontier_chunk_index=3113; highest_accepted_chunk_index=3120; transport_backlog_chunks=7; available_credit_chunks=0; credit_ceiling_chunk_index=3121; rebind_generation=3; bridge_recovery_policy=PostTunaFallbackStrictRecovery", secondsOffset: 30),
                LogLine($"event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.frontier_request.v6; first_lane=control; second_lane=bulk; first_error=OperationCanceledException; second_error=OperationCanceledException", secondsOffset: 40)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("0", verdict["hard_failure_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("0", stability["hard_failure_count"]);

        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["v4_feedback_both_failed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTunaFallbackStaleFrontierRequestCanceledAfterCleanProof_IsRecoverable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareMeasuredFallbackFixture().ToList());
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine($"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; reason=frontier_stall_repair_due; transport_epoch=3; repair_request_id=v6-frontier:3:4168:39; recovery_mode=frontier_repair_only; start_chunk_index=4168; requested_chunk_count=1; total_requested_chunk_count=1; range_count=1; post_tuna_fallback_survival=1", secondsOffset: 20),
                LogLine($"event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:6; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=3; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:4; proven_committed_chunk=4168; proven_highest_observed_chunk=4707; reason=receiver_state_sparse_runtime", secondsOffset: 25),
                LogLine($"event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.frontier_request.v6; first_lane=control; second_lane=bulk; first_error=InvalidOperationException; second_error=OperationCanceledException", secondsOffset: 30)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("0", verdict["hard_failure_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("0", stability["hard_failure_count"]);

        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["v4_feedback_both_failed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4ProgressTimeoutWithNoTailRepair_ClassifiesFrontierTailRepairNeeded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_tail_repair_needed";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=3121"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_state_sent; transfer_id={transferId}; session_id=sess_a; epoch=41; contiguous_committed_chunk_index=2786; durable_received_highest_chunk_index=2785; credit_until_chunk_index_exclusive=3121; missing_range_count=0; frontier_stall_age_ms=45000; terminal_ready=0"),
            LogLine($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id=sess_a; epoch=41; contiguous_committed_chunk_index=2786; durable_received_highest_chunk_index=2785; credit_until_chunk_index_exclusive=3121; missing_range_count=0; available_credit_bytes=0; terminal_ready=0"),
            LogLine($"event=filetransfer_v4_sender_pump_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; scheduled_frames=0; completed_frames=0; failed_frames=0; in_flight_frames=0; raw_bytes_sent=0; repair_send_count=0; credit_exhausted_time_ms=24000; available_credit_bytes=0; next_unsent_chunk_index=3121; credit_ceiling_chunk_index=3121; remote_frontier_chunk_index=2786; terminal_ready=0"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=300; payload_bytes_sent=65536000; payload_bytes_per_second=3276800; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=3; worker_utilization_percent=50"),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 120s; total_wait_s=191; progress_timeout_seconds=120; receiver_next_chunk=-1; receiver_highest_chunk=-1; progress_events=3303"),
            LogLine($"event=filetransfer_artifact_slice_summary; transfer_id={transferId}; artifact_slice_start_reason=live_soak_failure_context; artifact_slice_end_reason=gui_progress_timeout")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("INCONCLUSIVE_PROGRESS_TIMEOUT", verdict["verdict"]);
        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_frontier_tail_repair_needed", decomposition["likely_limiter"]);
        Assert.Equal("-1", decomposition["last_receiver_next_chunk"]);
        Assert.Equal("1", decomposition["terminal_missing_after_progress_timeout"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4TailRepairSentButProgressTimeout_ClassifiesTailRepairNotFilled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_tail_repair_unfilled";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=3121"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_frontier_stall_missing_range_due; transfer_id={transferId}; session_id=sess_a; start_chunk_index=2786; requested_chunk_count=64; frontier_stall_age_ms=47000; credit_until_chunk_index_exclusive=3121; durable_received_highest_chunk_index=2785"),
            LogLine($"event=filetransfer_v4_repair_requested; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; attempt_count=1; range_count=1; requested_chunk_count=64; first_start_chunk_index=2786; last_end_chunk_exclusive=2850; frontier_tail_repair=1"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; range_count=1; requested_chunk_count=64; scheduled_chunk_count=64; frontier_tail_repair=1"),
            LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; range_count=1; requested_chunk_count=64; sent_chunk_count=64; frontier_tail_repair=1"),
            LogLine($"event=filetransfer_v4_sender_pump_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; scheduled_frames=1; repair_scheduled_frames=1; completed_frames=1; failed_frames=0; in_flight_frames=0; raw_bytes_sent=1376256; repair_send_count=64; credit_exhausted_time_ms=12000; available_credit_bytes=0; next_unsent_chunk_index=3121; credit_ceiling_chunk_index=3121; remote_frontier_chunk_index=2786; terminal_ready=0"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=300; payload_bytes_sent=65536000; payload_bytes_per_second=3276800; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=3; worker_utilization_percent=50"),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 120s; total_wait_s=191; progress_timeout_seconds=120; receiver_next_chunk=-1; receiver_highest_chunk=-1; progress_events=3303"),
            LogLine($"event=filetransfer_artifact_slice_summary; transfer_id={transferId}; artifact_slice_start_reason=live_soak_failure_context; artifact_slice_end_reason=gui_progress_timeout")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_repair_sent_not_observed_by_receiver", decomposition["likely_limiter"]);
        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["v4_frontier_tail_repair_due_count"]);
        Assert.Equal("0", repair["v4_frontier_tail_repair_filled_count"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackTailYieldAuthorityRejectionPrecedesLaterFrontierStall(bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_tail_yield_authority_invalid";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; live_route_epoch=6"),
            LogLine($"event=filetransfer_fallback_tail_yield_proof_stale_ignored; direction=inbound; authority_schema=0; authority_id=(none); predecessor_authority_id=(none); transfer_id={transferId}; session_id=sess_a; leg_generation=8; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; transport_epoch=3; bridge_recovery_generation=2; checkpoint_request_id=(none); peer_owner_key=sess_a|{transferId}|post_tuna_fallback_v6|6|6|8|2|3|(none); yield_generation=1; reason=legacy_authority_missing", secondsOffset: 1),
            LogLine($"event=filetransfer_fallback_frontier_repair_stalled; direction=inbound; transfer_id={transferId}; session_id=sess_a; repair_request_key=v6-frontier:2786:2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; leg_generation=8; transport_epoch=3; bridge_recovery_generation=2; reason=repeated_frontier_repair_without_progress; frontier_chunk_index=2786; durable_received_highest_chunk_index=2785; requested_chunk_count=64; attempt_count=2", secondsOffset: 2),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=peer_disconnected", secondsOffset: 3),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)", secondsOffset: 3)
        };

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("1", route["fallback_tail_yield_authority_rejected_count"]);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fallback_tail_yield_authority_invalid", verdict["recovery_failure_class"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackTailFailedYieldQuiescentSuccessorMissing_Deduplicates(
        bool compact,
        bool recovered)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_failed_yield_successor";
        const string authorityId =
            "fta2-fallback_tail_yield-0123456789abcdef01234567";
        const string scope =
            "direction=outbound; session_id=sess_a; " +
            $"transfer_id={transferId}; route=post_tuna_fallback_v6; " +
            "protocol_version=6; live_route_epoch=2; " +
            "route_publication_generation=3; route_commit_generation=0; " +
            "runtime_generation=3; terminal_generation=0; leg_generation=3; " +
            "transport_epoch=1; bridge_recovery_generation=1; " +
            "checkpoint_generation=1; checkpoint_request_id=(none); " +
            "data_session_identity=data-session-1; data_session_generation=1";
        var ownerKey =
            $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|3|1|1|(none)";
        var pairKey =
            $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|1|(none)";
        var lines = new List<string>
        {
            LogLine(
                $"event=filetransfer_route_selected; {scope}; " +
                "target_route=post_tuna_fallback_v6; target_protocol_version=6; " +
                "target_transport=regular_nkn; " +
                "route_publication_action_kind=RoutePublication; " +
                "coordinator_decision_reason=accepted; " +
                "log_writer_id=writer_a; log_writer_sequence=1",
                secondsOffset: 0),
            LogLine(
                $"event=filetransfer_fallback_tail_yield_issued; {scope}; " +
                $"authority_id={authorityId}; predecessor_authority_id=(none); " +
                $"yield_generation=1; yield_kind=Failed; owner_key={ownerKey}; " +
                $"pair_key={pairKey}; pairing_state=YieldIssued; " +
                "decision_status=Accepted; decision_reason=missing_frontier_state; " +
                "log_writer_id=writer_a; log_writer_sequence=2",
                secondsOffset: 1),
            LogLine(
                $"event=filetransfer_fallback_tail_quiescence_timeout; {scope}; " +
                "reason=missing_frontier_state; " +
                "log_writer_id=writer_a; log_writer_sequence=3",
                secondsOffset: 2),
            LogLine(
                $"event=filetransfer_fallback_tail_frontier_repair_cleared; {scope}; " +
                "reason=receiver_state_sparse_runtime; " +
                "log_writer_id=writer_a; log_writer_sequence=4",
                secondsOffset: 3),
        };
        for (var index = 0; index < 3; index++)
        {
            lines.Add(LogLine(
                $"event=filetransfer_runtime_owner_stale_update_rejected; {scope}; " +
                "source=receiver_state_sparse_runtime; " +
                "attempted_action=fallback_tail_quiescent; " +
                "reason=fallback_tail_yield_operation_conflict; " +
                $"log_writer_id=writer_a; log_writer_sequence={5 + index}",
                secondsOffset: 4 + index));
        }
        const string successorAuthorityId =
            "fta2-fallback_tail_yield-fedcba9876543210fedcba98";
        if (recovered)
        {
            lines.AddRange(
            [
                LogLine(
                    $"event=filetransfer_fallback_tail_failed_yield_superseded; {scope}; " +
                    $"authority_id={successorAuthorityId}; yield_generation=2; yield_kind=Quiescent; " +
                    $"failed_predecessor_authority_id={authorityId}; failed_predecessor_yield_generation=1; " +
                    "decision_status=Accepted; log_writer_id=writer_a; log_writer_sequence=8",
                    secondsOffset: 8),
                LogLine(
                    $"event=filetransfer_fallback_tail_local_proof_receipt_decided; {scope}; " +
                    $"authority_id={successorAuthorityId}; yield_generation=2; decision_status=Applied; " +
                    "log_writer_id=writer_a; log_writer_sequence=9",
                    secondsOffset: 9),
                LogLine(
                    $"event=filetransfer_fallback_tail_peer_proof_receipt_decided; {scope}; " +
                    $"authority_id={successorAuthorityId}; yield_generation=2; decision_status=Accepted; " +
                    "log_writer_id=writer_a; log_writer_sequence=10",
                    secondsOffset: 10),
                LogLine(
                    $"event=filetransfer_fallback_tail_pairing_committed; {scope}; " +
                    $"authority_id={successorAuthorityId}; yield_generation=2; " +
                    "log_writer_id=writer_a; log_writer_sequence=11",
                    secondsOffset: 11),
                LogLine(
                    $"event=filetransfer_fallback_tail_paired_release_reserved; {scope}; " +
                    $"authority_id={successorAuthorityId}; yield_generation=2; " +
                    "log_writer_id=writer_a; log_writer_sequence=12",
                    secondsOffset: 12),
                LogLine(
                    $"event=filetransfer_fallback_tail_paired_release_consumed; {scope}; " +
                    $"authority_id={successorAuthorityId}; yield_generation=2; " +
                    "log_writer_id=writer_a; log_writer_sequence=13",
                    secondsOffset: 13),
                LogLine(
                    $"event=filetransfer_route_transition_prepared; {scope}; " +
                    "source_route=post_tuna_fallback_v6; target_route=file_tuna_v4; " +
                    $"fallback_yield_authority_id={successorAuthorityId}; " +
                    "log_writer_id=writer_a; log_writer_sequence=14",
                    secondsOffset: 14),
            ]);
        }
        lines.Add(LogLine(
            $"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; " +
            $"transfer_id={transferId}; state={(recovered ? "Completed" : "Failed")}; " +
            $"error_code={(recovered ? "none" : "route_cycle_missing")}; " +
            $"log_writer_id=writer_a; log_writer_sequence={(recovered ? 15 : 8)}",
            secondsOffset: recovered ? 15 : 8));

        var result = await RunAnalyzeFixtureAsync(
            lines.ToArray(),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal("3", verdict["paired_fallback_tail_yield_missing_raw_count"]);
        Assert.Equal("1", verdict["paired_fallback_tail_yield_missing_distinct_count"]);
        Assert.Equal(recovered ? "1" : "0", verdict["paired_fallback_tail_yield_missing_recovered_count"]);
        Assert.Equal(recovered ? "0" : "1", verdict["paired_fallback_tail_yield_missing_unresolved_count"]);
        if (!recovered)
        {
            Assert.Equal("paired_fallback_tail_yield_missing", verdict["recovery_failure_class"]);
            Assert.Equal(
                "same_scope_failed_fallback_tail_yield_blocked_quiescent_successor",
                verdict["first_proof_chain_failure_reason"]);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackTailRecoveredWithoutPairedYieldInstallReceipt_CorrelatesAndRecovers(
        bool compact,
        bool recovered)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackTailRecoveredWithoutInstallReceiptFixture(recovered),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal("2", verdict["paired_fallback_tail_yield_missing_raw_count"]);
        Assert.Equal("1", verdict["paired_fallback_tail_yield_missing_distinct_count"]);
        Assert.Equal(recovered ? "1" : "0", verdict["paired_fallback_tail_yield_missing_recovered_count"]);
        Assert.Equal(recovered ? "0" : "1", verdict["paired_fallback_tail_yield_missing_unresolved_count"]);
        if (!recovered)
        {
            Assert.Equal("paired_fallback_tail_yield_missing", verdict["recovery_failure_class"]);
            Assert.Equal(
                "recovered_tail_paired_yield_install_receipt_missing",
                verdict["failure_variant"]);
            Assert.Equal(
                "filetransfer_runtime_unlock_handoff_held_for_retry_authority",
                verdict["first_proof_chain_failure_event"]);
            Assert.Equal(
                "fallback_tail_recovered_without_paired_yield_install_receipt",
                verdict["first_proof_chain_failure_reason"]);
            Assert.Equal("route_runtime", verdict["acceptance_failure_class"]);
            Assert.Equal("fail", verdict["runtime_correctness_verdict"]);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackPairedYieldRetrySourceScopeQueueRejection_CorrelatesAndRecovers(
        bool compact,
        bool recovered)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackPairedYieldRetrySourceScopeQueueRejectionFixture(recovered),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal("2", verdict["paired_fallback_tail_yield_missing_raw_count"]);
        Assert.Equal("1", verdict["paired_fallback_tail_yield_missing_distinct_count"]);
        Assert.Equal(recovered ? "1" : "0", verdict["paired_fallback_tail_yield_missing_recovered_count"]);
        Assert.Equal(recovered ? "0" : "1", verdict["paired_fallback_tail_yield_missing_unresolved_count"]);
        if (!recovered)
        {
            Assert.Equal("paired_fallback_tail_yield_missing", verdict["recovery_failure_class"]);
            Assert.Equal(
                "paired_yield_retry_request_source_scope_not_current",
                verdict["failure_variant"]);
            Assert.Equal(
                "runtime_unlock_retry_dispatch_authority_request_queue_rejected",
                verdict["first_proof_chain_failure_event"]);
            Assert.Equal(
                "fallback_paired_tail_yield_retry_request_source_scope_not_current",
                verdict["first_proof_chain_failure_reason"]);
            Assert.Equal("route_runtime", verdict["semantic_failure_domain"]);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackPairedYieldRetryBlockedByGenericActivation_CorrelatesAndRecovers(
        bool compact,
        bool recovered)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackPairedYieldRetryBlockedByGenericActivationFixture(recovered),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal("1", verdict["paired_fallback_tail_yield_missing_raw_count"]);
        Assert.Equal("1", verdict["paired_fallback_tail_yield_missing_distinct_count"]);
        Assert.Equal(recovered ? "1" : "0", verdict["paired_fallback_tail_yield_missing_recovered_count"]);
        Assert.Equal(recovered ? "0" : "1", verdict["paired_fallback_tail_yield_missing_unresolved_count"]);
        if (!recovered && !compact)
        {
            Assert.Equal("paired_fallback_tail_yield_missing", verdict["recovery_failure_class"]);
            Assert.Equal(
                "paired_yield_retry_intent_blocked_by_preexisting_initial_activation",
                verdict["failure_variant"]);
            Assert.Equal(
                "runtime_unlock_retry_negotiation_slot_decided",
                verdict["first_proof_chain_failure_event"]);
            Assert.Equal(
                "fallback_paired_tail_yield_retry_intent_blocked_by_preexisting_initial_activation",
                verdict["first_proof_chain_failure_reason"]);
            Assert.Equal("route_runtime", verdict["semantic_failure_domain"]);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackPairedYieldRetryIntentNotRecheckedAfterUnlock_CorrelatesAndRecovers(
        bool compact,
        bool recovered)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackPairedYieldRetryIntentNotRecheckedAfterUnlockFixture(recovered),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal("1", verdict["paired_fallback_tail_yield_missing_raw_count"]);
        Assert.Equal("1", verdict["paired_fallback_tail_yield_missing_distinct_count"]);
        Assert.Equal(recovered ? "1" : "0", verdict["paired_fallback_tail_yield_missing_recovered_count"]);
        Assert.Equal(recovered ? "0" : "1", verdict["paired_fallback_tail_yield_missing_unresolved_count"]);
        if (!recovered && !compact)
        {
            Assert.Equal("paired_fallback_tail_yield_missing", verdict["recovery_failure_class"]);
            Assert.Equal(
                "paired_yield_retry_intent_not_rechecked_after_runtime_unlock_eligibility_restored",
                verdict["failure_variant"]);
            Assert.Equal(
                "runtime_unlock_post_fallback_negotiation_deferred",
                verdict["first_proof_chain_failure_event"]);
            Assert.Equal(
                "fallback_paired_tail_yield_retry_intent_not_rechecked_after_runtime_unlock_eligibility_restored",
                verdict["first_proof_chain_failure_reason"]);
            Assert.Equal("route_runtime", verdict["semantic_failure_domain"]);
        }
    }

    [Theory]
    [InlineData(false, "retry_contract")]
    [InlineData(true, "retry_contract")]
    [InlineData(false, "outbound_terminal")]
    [InlineData(true, "outbound_terminal")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackTailRecoveredWithoutPairedYieldInstallReceipt_MutatedRecoveryRemainsUnresolved(
        bool compact,
        string mutation)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackTailRecoveredWithoutInstallReceiptFixture(
                recovered: true,
                mutation: mutation),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(!compact && mutation == "outbound_terminal" ? 1 : 0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal("2", verdict["paired_fallback_tail_yield_missing_raw_count"]);
        Assert.Equal("1", verdict["paired_fallback_tail_yield_missing_distinct_count"]);
        Assert.Equal("0", verdict["paired_fallback_tail_yield_missing_recovered_count"]);
        Assert.Equal("1", verdict["paired_fallback_tail_yield_missing_unresolved_count"]);
        Assert.Equal("paired_fallback_tail_yield_missing", verdict["recovery_failure_class"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackTailRecoveredWithoutPairedYieldInstallReceipt_InboundTerminalDoesNotPoisonOutboundRecovery(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackTailRecoveredWithoutInstallReceiptFixture(
                recovered: true,
                mutation: "inbound_terminal"),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(compact ? 0 : 1, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal("2", verdict["paired_fallback_tail_yield_missing_raw_count"]);
        Assert.Equal("1", verdict["paired_fallback_tail_yield_missing_distinct_count"]);
        Assert.Equal("1", verdict["paired_fallback_tail_yield_missing_recovered_count"]);
        Assert.Equal("0", verdict["paired_fallback_tail_yield_missing_unresolved_count"]);
    }

    [Theory]
    [InlineData(false, "missing_caught_up", "0", "0", "0", "0")]
    [InlineData(true, "missing_caught_up", "0", "0", "0", "0")]
    [InlineData(false, "missing_unblocked", "0", "0", "0", "0")]
    [InlineData(true, "missing_unblocked", "0", "0", "0", "0")]
    [InlineData(false, "wrong_writer", "0", "0", "0", "0")]
    [InlineData(true, "wrong_writer", "0", "0", "0", "0")]
    [InlineData(false, "wrong_session", "0", "0", "0", "0")]
    [InlineData(true, "wrong_session", "0", "0", "0", "0")]
    [InlineData(false, "scope_replacement", "0", "0", "0", "0")]
    [InlineData(true, "scope_replacement", "0", "0", "0", "0")]
    [InlineData(false, "unrelated_install", "2", "1", "0", "1")]
    [InlineData(true, "unrelated_install", "2", "1", "0", "1")]
    [InlineData(false, "pairing_reason", "2", "1", "0", "1")]
    [InlineData(true, "pairing_reason", "2", "1", "0", "1")]
    [InlineData(false, "none", "2", "1", "1", "0")]
    [InlineData(true, "none", "2", "1", "1", "0")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackTailRecoveredWithoutPairedYieldInstallReceipt_EnforcesCorrelationBoundaries(
        bool compact,
        string mutation,
        string expectedRaw,
        string expectedDistinct,
        string expectedRecovered,
        string expectedUnresolved)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackTailRecoveredWithoutInstallReceiptFixture(
                recovered: true,
                mutation: mutation),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal(expectedRaw, verdict["paired_fallback_tail_yield_missing_raw_count"]);
        Assert.Equal(expectedDistinct, verdict["paired_fallback_tail_yield_missing_distinct_count"]);
        Assert.Equal(expectedRecovered, verdict["paired_fallback_tail_yield_missing_recovered_count"]);
        Assert.Equal(expectedUnresolved, verdict["paired_fallback_tail_yield_missing_unresolved_count"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackTailPeerProofBeforeLocalYieldNotRetained_ClassifiesAdmissionBoundary(
        bool compact,
        bool asymmetricRecoveryScope)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_peer_first_tail_yield";
        const string localAuthority =
            "fta2-fallback_tail_yield-111111111111111111111111";
        const string reciprocalAuthority =
            "fta2-fallback_tail_yield-222222222222222222222222";
        const string retryAuthority =
            "fta2-runtime_unlock_retry_dispatch-333333333333333333333333";
        var pairKey =
            $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|1|(none)";
        var localLegGeneration = asymmetricRecoveryScope ? 4 : 3;
        var localBridgeGeneration = asymmetricRecoveryScope ? 2 : 1;
        var ownerKey =
            $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|{localLegGeneration}|{localBridgeGeneration}|1|(none)";
        var peerOwnerKey =
            $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|3|1|1|(none)";
        var common =
            $"session_id=sess_a; transfer_id={transferId}; route=post_tuna_fallback_v6; " +
            "protocol_version=6; live_route_epoch=2; route_publication_generation=3; " +
            "route_commit_generation=0; runtime_generation=3; terminal_generation=0; " +
            $"leg_generation={localLegGeneration}; transport_epoch=1; bridge_recovery_generation={localBridgeGeneration}; " +
            "checkpoint_generation=1; checkpoint_request_id=(none); " +
            "data_session_identity=data-session-1; data_session_generation=1";
        var localProofIdentity =
            $"sess_a:{transferId}:inbound:{localAuthority}:1:local-frame";
        var peerProofIdentity =
            $"sess_a:{transferId}:outbound:{reciprocalAuthority}:1:peer-frame";
        var rejectionReason = asymmetricRecoveryScope
            ? "fallback_tail_peer_proof_scope_mismatch"
            : "fallback_tail_yield_operation_missing";
        var rejectionCount = asymmetricRecoveryScope ? 4 : 2;
        var lines = new List<string>();
        for (var index = 0; index < rejectionCount; index++)
        {
            lines.Add(LogLine(
                $"event=filetransfer_fallback_tail_peer_proof_receipt_decided; direction=outbound; {common}; authority_id=(none); yield_generation=0; owner_key=(none); pair_key=(none); peer_authority_id={reciprocalAuthority}; peer_predecessor_authority_id=(none); peer_yield_generation=1; peer_direction=outbound; peer_live_route_epoch=2; peer_leg_generation=3; peer_transport_epoch=1; peer_bridge_recovery_generation=1; peer_checkpoint_request_id=(none); peer_owner_key={peerOwnerKey}; peer_pair_key={pairKey}; peer_observation_identity={peerProofIdentity}; peer_frame_fingerprint=peer-frame; decision_status=Rejected; decision_reason={rejectionReason}; log_writer_id=writer_b; log_writer_sequence={index + 1}",
                secondsOffset: index));
        }
        lines.AddRange(
        [
            LogLine($"event=filetransfer_fallback_tail_yield_issued; direction=outbound; {common}; authority_id={reciprocalAuthority}; predecessor_authority_id=(none); yield_generation=1; yield_kind=Quiescent; owner_key={ownerKey}; pair_key={pairKey}; pairing_state=YieldIssued; decision_status=Accepted; log_writer_id=writer_a; log_writer_sequence=1", secondsOffset: 2),
            LogLine($"event=filetransfer_fallback_tail_local_proof_receipt_decided; direction=outbound; {common}; authority_id={reciprocalAuthority}; predecessor_authority_id=(none); yield_generation=1; owner_key={ownerKey}; pair_key={pairKey}; local_proof_identity={peerProofIdentity}; local_frame_fingerprint=peer-frame; decision_status=Accepted; log_writer_id=writer_a; log_writer_sequence=2", secondsOffset: 3),
            LogLine($"event=filetransfer_fallback_tail_yield_issued; direction=inbound; {common}; authority_id={localAuthority}; predecessor_authority_id=(none); yield_generation=1; yield_kind=Quiescent; owner_key={ownerKey}; pair_key={pairKey}; pairing_state=YieldIssued; decision_status=Accepted; log_writer_id=writer_b; log_writer_sequence=3", secondsOffset: 4),
            LogLine($"event=filetransfer_fallback_tail_yield_proof_sent; direction=inbound; authority_schema=2; authority_id={localAuthority}; predecessor_authority_id=(none); transfer_id={transferId}; session_id=sess_a; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; transport_epoch=1; bridge_recovery_generation=1; checkpoint_request_id=(none); owner_key={ownerKey}; pair_key={pairKey}; yield_generation=1; accepted_any=1; log_writer_id=writer_b; log_writer_sequence=4", secondsOffset: 5),
            LogLine($"event=filetransfer_fallback_tail_local_proof_receipt_decided; direction=inbound; {common}; authority_id={localAuthority}; predecessor_authority_id=(none); yield_generation=1; owner_key={ownerKey}; pair_key={pairKey}; local_proof_identity={localProofIdentity}; local_frame_fingerprint=local-frame; decision_status=Accepted; log_writer_id=writer_b; log_writer_sequence=5", secondsOffset: 6),
            LogLine($"event=filetransfer_fallback_tail_peer_proof_receipt_decided; direction=outbound; {common}; authority_id={reciprocalAuthority}; predecessor_authority_id=(none); yield_generation=1; owner_key={ownerKey}; pair_key={pairKey}; peer_authority_id={localAuthority}; peer_predecessor_authority_id=(none); peer_yield_generation=1; peer_direction=inbound; peer_owner_key={ownerKey}; peer_pair_key={pairKey}; peer_observation_identity={localProofIdentity}; peer_frame_fingerprint=local-frame; decision_status=Accepted; log_writer_id=writer_a; log_writer_sequence=3", secondsOffset: 6),
            LogLine($"event=filetransfer_fallback_tail_pairing_committed; direction=outbound; {common}; authority_id={reciprocalAuthority}; predecessor_authority_id=(none); yield_generation=1; yield_kind=Quiescent; owner_key={ownerKey}; pair_key={pairKey}; pairing_state=Paired; pairing_generation=1; local_proof_identity={peerProofIdentity}; local_frame_fingerprint=peer-frame; peer_authority_id={localAuthority}; peer_predecessor_authority_id=(none); peer_yield_generation=1; peer_direction=inbound; peer_owner_key={ownerKey}; peer_pair_key={pairKey}; peer_observation_identity={localProofIdentity}; peer_frame_fingerprint=local-frame; decision_status=Accepted; log_writer_id=writer_a; log_writer_sequence=4", secondsOffset: 7),
            LogLine($"event=filetransfer_fallback_tail_paired_release_reserved; direction=outbound; {common}; authority_id={reciprocalAuthority}; predecessor_authority_id=(none); yield_generation=1; yield_kind=Quiescent; owner_key={ownerKey}; pair_key={pairKey}; pairing_state=ReleaseReserved; pairing_generation=1; release_kind=FallbackPairedTailYield; release_outcome=Quiescent; reservation_generation=1; retry_authority_id={retryAuthority}; retry_attempt_generation=1; dispatch_attempt_generation=1; decision_status=Accepted; log_writer_id=writer_a; log_writer_sequence=5", secondsOffset: 8),
            LogLine($"event=runtime_unlock_retry_dispatch_install_committed; direction=outbound; session_id=sess_a; transfer_id={transferId}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=3; route_commit_generation=0; authority_id={retryAuthority}; dispatch_attempt_generation=1; install_generation=1; release_kind=FallbackPairedTailYield; release_authority_id={reciprocalAuthority}; release_generation=1; release_outcome=Quiescent; install_status=Installed; log_writer_id=writer_a; log_writer_sequence=6", secondsOffset: 9),
            LogLine($"event=filetransfer_fallback_tail_paired_release_consumed; direction=outbound; {common}; authority_id={reciprocalAuthority}; predecessor_authority_id=(none); yield_generation=1; yield_kind=Quiescent; owner_key={ownerKey}; pair_key={pairKey}; pairing_state=ReleaseConsumed; pairing_generation=1; release_kind=FallbackPairedTailYield; release_outcome=Quiescent; reservation_generation=1; retry_authority_id={retryAuthority}; retry_attempt_generation=1; dispatch_attempt_generation=1; install_generation=1; decision_status=Accepted; log_writer_id=writer_a; log_writer_sequence=7", secondsOffset: 10),
            LogLine($"event=filetransfer_runtime_unlock_admission_denied; direction=inbound; {common}; source=precommit_probe_responder; reason=runtime_unlock_answer_ack_probe_ready; admission_generation=0; admission_reason={(asymmetricRecoveryScope ? "diagnostic_only" : "paired_fallback_tail_yield_missing")}; log_writer_id=writer_b; log_writer_sequence=5", secondsOffset: 11),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=route_cycle_missing; log_writer_id=writer_a; log_writer_sequence=8", secondsOffset: 12),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=route_cycle_missing; log_writer_id=writer_b; log_writer_sequence=6", secondsOffset: 12),
        ]);
        if (asymmetricRecoveryScope)
        {
            lines.Insert(
                rejectionCount + 4,
                LogLine(
                    $"event=filetransfer_runtime_unlock_admission_denied; direction=inbound; {common}; source=precommit_probe_responder; reason=runtime_unlock_answer_ack_probe_ready; admission_generation=0; admission_reason=paired_fallback_tail_yield_missing; log_writer_id=writer_b; log_writer_sequence=5",
                    secondsOffset: 5));
        }

        var result = await RunAnalyzeFixtureAsync(
            lines.ToArray(),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal(asymmetricRecoveryScope ? "4" : "1", verdict["paired_fallback_tail_yield_missing_raw_count"]);
        Assert.Equal("1", verdict["paired_fallback_tail_yield_missing_distinct_count"]);
        Assert.Equal("0", verdict["paired_fallback_tail_yield_missing_recovered_count"]);
        Assert.Equal("1", verdict["paired_fallback_tail_yield_missing_unresolved_count"]);
        Assert.Equal("0", verdict["failed_yield_quiescent_successor_missing_unresolved_count"]);
        Assert.Equal("1", verdict["peer_proof_before_local_yield_not_retained_unresolved_count"]);
        Assert.Equal(
            asymmetricRecoveryScope ? "4" : "0",
            verdict["peer_proof_before_local_yield_asymmetric_recovery_scope_not_retained_raw_count"]);
        Assert.Equal(
            asymmetricRecoveryScope ? "1" : "0",
            verdict["peer_proof_before_local_yield_asymmetric_recovery_scope_not_retained_unresolved_count"]);
        if (asymmetricRecoveryScope)
        {
            Assert.Equal("route_runtime", verdict["acceptance_failure_class"]);
            Assert.Equal("fail", verdict["runtime_correctness_verdict"]);
            Assert.Equal("route_runtime", verdict["semantic_failure_domain"]);
            Assert.Equal(
                "direction_local_recovery_generation_treated_as_shared_pair_scope",
                verdict["first_proof_chain_failure_reason"]);
        }
    }

    [Theory]
    [InlineData(2, 1, "canonical_7", "pass")]
    [InlineData(2, 1, "legacy_bridge_scoped_8", "fail")]
    [InlineData(2, 1, "owner_derived", "fail")]
    [InlineData(1, 1, "legacy_bridge_scoped_8", "pass")]
    [Trait("RetainedFallbackSuite", "FallbackTailBridgeRecoveryPairing")]
    public async Task AnalyzeRetained_CurrentFallbackTailYieldAuthorityChainMatchesCompleteTuple(
        int localBridgeGeneration,
        int peerBridgeGeneration,
        string pairFormat,
        string expectedVerdict)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_tail_yield_schema2";
        const string localAuthorityId = "fta2-fallback_tail_yield-0123456789abcdef01234567";
        const string peerAuthorityId = "fta2-fallback_tail_yield-89abcdef0123456701234567";
        var ownerKey = $"sess_a|{transferId}|post_tuna_fallback_v6|6|6|8|{localBridgeGeneration}|3|(none)";
        var peerOwnerKey = $"sess_a|{transferId}|post_tuna_fallback_v6|6|6|9|{peerBridgeGeneration}|3|(none)";
        var canonicalPairKey = $"sess_a|{transferId}|post_tuna_fallback_v6|6|6|3|(none)";
        var pairKey = pairFormat switch
        {
            "canonical_7" => canonicalPairKey,
            "owner_derived" => ownerKey,
            _ => $"sess_a|{transferId}|post_tuna_fallback_v6|6|6|{localBridgeGeneration}|3|(none)"
        };
        var peerRawPairKey = pairFormat switch
        {
            "canonical_7" => canonicalPairKey,
            "owner_derived" => peerOwnerKey,
            _ => $"sess_a|{transferId}|post_tuna_fallback_v6|6|6|{peerBridgeGeneration}|3|(none)"
        };
        var localScope = $"transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; runtime_generation=10; terminal_generation=0; leg_generation=8; transport_epoch=3; bridge_recovery_generation={localBridgeGeneration}; checkpoint_request_id=(none)";
        var stableTuple = $"{localScope}; owner_key={ownerKey}; pair_key={pairKey}; yield_generation=1";
        var peerTuple = $"{localScope}; peer_live_route_epoch=6; peer_leg_generation=9; peer_transport_epoch=3; peer_bridge_recovery_generation={peerBridgeGeneration}; peer_checkpoint_request_id=(none); owner_key={peerOwnerKey}; pair_key={peerRawPairKey}; raw_pair_key={peerRawPairKey}; canonical_pair_key={canonicalPairKey}; pair_key_format={pairFormat}; yield_generation=1";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; live_route_epoch=6; route_publication_generation=5; runtime_generation=10; terminal_generation=0; leg_generation=8; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted"),
            LogLine($"event=filetransfer_fallback_leg_transport_epoch_adopted; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:8; leg_generation=8; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; previous_transport_epoch=2; transport_epoch=3; bridge_recovery_generation={localBridgeGeneration}; retired_checkpoint_request_id=(none); current_checkpoint_request_id=(none); state=recovery_active; can_send_data=1; reason=transport_recovered_unproven", secondsOffset: 1),
            LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=outbound; authority_schema=2; authority_id={localAuthorityId}; predecessor_authority_id=(none); {stableTuple}; committed_chunk=2786; highest_observed_chunk=2785; reason=fallback_tail_quiescent", secondsOffset: 2),
            LogLine($"event=filetransfer_fallback_tail_yield_proof_sent; direction=outbound; authority_schema=2; authority_id={localAuthorityId}; predecessor_authority_id=(none); {stableTuple}; peer_visible_any=1; accepted_any=1; reason=fallback_tail_paired_yield", secondsOffset: 3),
            LogLine($"event=filetransfer_fallback_tail_yield_proof_received; direction=outbound; authority_schema=2; authority_id={peerAuthorityId}; predecessor_authority_id=(none); peer_direction=inbound; {peerTuple}", secondsOffset: 4),
            LogLine($"event=filetransfer_fallback_tail_paired_yield_granted; direction=outbound; authority_schema=2; authority_id={localAuthorityId}; predecessor_authority_id=(none); peer_authority_id={peerAuthorityId}; peer_predecessor_authority_id=(none); peer_yield_generation=1; peer_leg_generation=9; peer_live_route_epoch=6; peer_transport_epoch=3; peer_bridge_recovery_generation={peerBridgeGeneration}; peer_checkpoint_request_id=(none); peer_owner_key={peerOwnerKey}; peer_raw_pair_key={peerRawPairKey}; peer_canonical_pair_key={canonicalPairKey}; peer_pair_key_format={pairFormat}; {stableTuple}; reason=fallback_tail_paired_yield", secondsOffset: 5),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)", secondsOffset: 6),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=x", secondsOffset: 6)
        };

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    lines,
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(lines);
            Assert.Equal(0, result.Script.ExitCode);
            var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
            if (!compact)
            {
                Assert.Equal(
                    expectedVerdict == "pass" ? "0" : "1",
                    route["fallback_tail_yield_authority_rejected_count"]);
            }
            Assert.Equal(expectedVerdict, route["fallback_tail_paired_yield_verdict"]);
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailBridgeRecoveryPairing")]
    public async Task AnalyzeRetained_RedactedCopiedPeerAuthorityUsesSingleExactSchema2Proof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_tail_yield_redacted_peer_authority";
        const string localAuthorityId = "fta2-fallback_tail_yield-0123456789abcdef01234567";
        const string peerAuthorityId = "fta2-fallback_tail_yield-89abcdef0123456701234567";
        const string localOwnerKey =
            "sess_a|transfer_fallback_tail_yield_redacted_peer_authority|post_tuna_fallback_v6|6|6|8|2|3|(none)";
        const string peerOwnerKey =
            "sess_a|transfer_fallback_tail_yield_redacted_peer_authority|post_tuna_fallback_v6|6|6|9|1|3|(none)";
        const string canonicalPairKey =
            "sess_a|transfer_fallback_tail_yield_redacted_peer_authority|post_tuna_fallback_v6|6|6|3|(none)";
        var localScope =
            $"transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; runtime_generation=10; terminal_generation=0; leg_generation=8; transport_epoch=3; bridge_recovery_generation=2; checkpoint_request_id=(none)";
        var localTuple = $"{localScope}; owner_key={localOwnerKey}; pair_key={canonicalPairKey}; yield_generation=1";
        var peerTuple =
            $"{localScope}; peer_live_route_epoch=6; peer_leg_generation=9; peer_transport_epoch=3; peer_bridge_recovery_generation=1; peer_checkpoint_request_id=(none); owner_key={peerOwnerKey}; pair_key={canonicalPairKey}; raw_pair_key={canonicalPairKey}; canonical_pair_key={canonicalPairKey}; pair_key_format=canonical_7; yield_generation=1";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; live_route_epoch=6; route_publication_generation=5; runtime_generation=10; terminal_generation=0; leg_generation=8; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted"),
            LogLine($"event=filetransfer_fallback_leg_transport_epoch_adopted; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:8; leg_generation=8; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; previous_transport_epoch=2; transport_epoch=3; bridge_recovery_generation=2; retired_checkpoint_request_id=(none); current_checkpoint_request_id=(none); state=recovery_active; can_send_data=1; reason=transport_recovered_unproven", secondsOffset: 1),
            LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=outbound; authority_schema=2; authority_id={localAuthorityId}; predecessor_authority_id=(none); {localTuple}; committed_chunk=2786; highest_observed_chunk=2785; reason=fallback_tail_quiescent", secondsOffset: 2),
            LogLine($"event=filetransfer_fallback_tail_yield_proof_sent; direction=outbound; authority_schema=2; authority_id={localAuthorityId}; predecessor_authority_id=(none); {localTuple}; peer_visible_any=1; accepted_any=1; reason=fallback_tail_paired_yield", secondsOffset: 3),
            LogLine($"event=filetransfer_fallback_tail_yield_proof_received; direction=outbound; authority_schema=2; authority_id={peerAuthorityId}; predecessor_authority_id=(none); peer_direction=inbound; {peerTuple}", secondsOffset: 4),
            LogLine($"event=filetransfer_fallback_tail_paired_yield_granted; direction=outbound; authority_schema=2; authority_id={localAuthorityId}; predecessor_authority_id=(none); peer_authority_id=[redacted]; peer_predecessor_authority_id=(none); peer_yield_generation=1; peer_leg_generation=9; peer_live_route_epoch=6; peer_transport_epoch=3; peer_bridge_recovery_generation=1; peer_checkpoint_request_id=(none); peer_owner_key={peerOwnerKey}; peer_raw_pair_key={canonicalPairKey}; peer_canonical_pair_key={canonicalPairKey}; peer_pair_key_format=canonical_7; {localTuple}; reason=fallback_tail_paired_yield", secondsOffset: 5),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)", secondsOffset: 6),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=x", secondsOffset: 6)
        };

        var observed = new List<(string PairedYieldVerdict, string AuthorityRejectedCount)>();
        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    lines,
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(lines);
            Assert.Equal(0, result.Script.ExitCode);
            var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
            Assert.Equal("0", route["fallback_tail_yield_authority_rejected_count"]);
            Assert.Equal("pass", route["fallback_tail_paired_yield_verdict"]);
            observed.Add((
                route["fallback_tail_paired_yield_verdict"],
                route["fallback_tail_yield_authority_rejected_count"]));
        }

        Assert.Equal(observed[0], observed[1]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackFrontierRepairDispatchedWithoutProgress_ClassifiesRepairStalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_frontier_repair_stalled";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=6"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_generation=8; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; transport_epoch=3; bridge_recovery_generation=2; checkpoint_request_id=(none); authority_reason=post_tuna_fallback_frontier_repair"),
            LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=v4_repair_schedule; stage=schedule; repair_request_key=v6-frontier:100:1; repair_replay_kind=FrontierStallRepair; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; runtime_generation=10; terminal_generation=0; leg_generation=8; transport_epoch=3; bridge_recovery_generation=2; repair_replay_generation=3; attempted_leg_generation=8; attempted_transport_epoch=3; attempted_bridge_recovery_generation=2; range_count=1; requested_chunk_count=16; scheduled_chunk_count=16; decision_reason=frontier_stall_repair"),
            LogLine($"event=filetransfer_fallback_frontier_repair_dispatched; direction=outbound; transfer_id={transferId}; session_id=sess_a; stage=send; repair_request_key=v6-frontier:100:1; protocol_repair_request_id=v6-frontier:100:1; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; leg_generation=8; transport_epoch=3; bridge_recovery_generation=2; sent_chunk_count=16; remote_next_expected_chunk_index=100; chunks_accepted_for_transport=16"),
            LogLine($"event=filetransfer_fallback_frontier_repair_progress_observed; direction=inbound; transfer_id={transferId}; session_id=sess_a; repair_request_key=v6-frontier:100:1; reason=repair_filled; committed_frontier_before=100; committed_frontier_after=116; requested_chunk_count=16"),
            LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=v4_repair_schedule; stage=schedule; repair_request_key=v6-frontier:2786:1; repair_replay_kind=FrontierStallRepair; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; runtime_generation=10; terminal_generation=0; leg_generation=8; transport_epoch=3; bridge_recovery_generation=2; repair_replay_generation=4; attempted_leg_generation=8; attempted_transport_epoch=3; attempted_bridge_recovery_generation=2; range_count=1; requested_chunk_count=64; scheduled_chunk_count=64; decision_reason=frontier_stall_repair"),
            LogLine($"event=filetransfer_fallback_frontier_repair_dispatched; direction=outbound; transfer_id={transferId}; session_id=sess_a; stage=send; repair_request_key=v6-frontier:2786:1; protocol_repair_request_id=v6-frontier:2786:1; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; leg_generation=8; transport_epoch=3; bridge_recovery_generation=2; sent_chunk_count=64; remote_next_expected_chunk_index=2786; chunks_accepted_for_transport=64"),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 120s; total_wait_s=191; progress_timeout_seconds=120; receiver_next_chunk=2786; receiver_highest_chunk=2785; progress_events=3303"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=peer_disconnected"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("2", route["fallback_frontier_repair_authority_granted_count"]);
        Assert.Equal("2", route["fallback_frontier_repair_dispatched_count"]);
        Assert.Equal("1", route["fallback_frontier_repair_progress_observed_count"]);
        Assert.Equal("fail", route["fallback_frontier_repair_verdict"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fallback_frontier_repair_stalled", verdict["recovery_failure_class"]);
        Assert.Equal("fail", verdict["classification_fallback_frontier_repair_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackFrontierRepairNoSchedulableChunks_ClassifiesExplicitStall()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_frontier_repair_no_chunks";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=6"),
            LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=v4_repair_schedule; stage=schedule; repair_request_key=v6-frontier:0:1; repair_replay_kind=FrontierStallRepair; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; runtime_generation=10; terminal_generation=0; leg_generation=8; transport_epoch=3; bridge_recovery_generation=2; repair_replay_generation=3; attempted_leg_generation=8; attempted_transport_epoch=3; attempted_bridge_recovery_generation=2; range_count=1; requested_chunk_count=1; scheduled_chunk_count=0; decision_reason=frontier_stall_repair"),
            LogLine($"event=filetransfer_fallback_frontier_repair_stalled; direction=outbound; transfer_id={transferId}; session_id=sess_a; repair_request_key=v6-frontier:0:1; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; leg_generation=8; transport_epoch=3; bridge_recovery_generation=2; reason=no_schedulable_repair_chunks; frontier_chunk_index=0; durable_received_highest_chunk_index=143; requested_chunk_count=1; attempt_count=1; frontier_stall_age_ms=0"),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 180s; total_wait_s=213; progress_timeout_seconds=180; receiver_next_chunk=0; receiver_highest_chunk=143; progress_events=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=peer_disconnected"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("1", route["fallback_frontier_repair_authority_granted_count"]);
        Assert.Equal("0", route["fallback_frontier_repair_dispatched_count"]);
        Assert.Equal("1", route["fallback_frontier_repair_stalled_count"]);
        Assert.Equal("fail", route["fallback_frontier_repair_verdict"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fallback_frontier_repair_stalled", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["classification_fallback_frontier_repair_stalled_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ExplicitFallbackFrontierRepairStalledEvent_ClassifiesRepairStalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_frontier_repair_explicit_stalled";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=6"),
            LogLine($"event=filetransfer_fallback_frontier_repair_stalled; direction=inbound; transfer_id={transferId}; session_id=sess_a; repair_request_key=v6-frontier:2786:2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; leg_generation=8; transport_epoch=3; bridge_recovery_generation=2; reason=repeated_frontier_repair_without_progress; frontier_chunk_index=2786; durable_received_highest_chunk_index=2785; requested_chunk_count=64; attempt_count=2; frontier_stall_age_ms=180000"),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 120s; total_wait_s=191; progress_timeout_seconds=120; receiver_next_chunk=2786; receiver_highest_chunk=2785; progress_events=3303"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("1", route["fallback_frontier_repair_stalled_count"]);
        Assert.Equal("fail", route["fallback_frontier_repair_verdict"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fallback_frontier_repair_stalled", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["classification_fallback_frontier_repair_stalled_count"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    public async Task AnalyzeRetained_FallbackFrontierRepairStallWithDispatchedSuccessor_HasFullCompactParity(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_frontier_repair_successor";
        const string common = "authority_schema=2; direction=inbound; session_id=sess_a; " +
            "route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; " +
            "terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; " +
            "bridge_decision_generation=0; checkpoint_request_id=(none); route_publication_generation=3; " +
            "route_commit_generation=0; frontier_chunk_index=2616; source_kind=ReceiverRequest; " +
            "primary_protocol_repair_request_id=v6-frontier:2616:1; primary_request_correlation_id=frq-successor; " +
            "priority=frontier_repair; recovery_mode=post_tuna_fallback_v6; target_transport=regular_nkn; " +
            "authority_decision_reason=receiver_request; data_session_identity=data-session-a; data_session_generation=1";
        const string predecessor = "fta2-repair_replay-465ac09dca3f88ab46cfe195";
        const string successor = "fta2-repair_replay-cb8d36da1ba44b4932d785c9";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; bridge_decision_generation=0; checkpoint_request_id=(none); route_publication_generation=3; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn"),
            LogLine($"event=filetransfer_fallback_frontier_repair_stalled; {common}; transfer_id={transferId}; authority_id={predecessor}; predecessor_authority_id=(none); repair_generation=4; repair_request_key=fallback-frontier:2616:1:old; range_fingerprint=2616:1; durable_received_highest_chunk_index=2774; range_count=1; requested_chunk_count=1; attempt_count=2; decision_reason=authorized_fallback_frontier_repair_stalled"),
            LogLine($"event=filetransfer_fallback_frontier_repair_contract_settled; {common}; transfer_id={transferId}; authority_id={predecessor}; predecessor_authority_id=(none); repair_generation=4; repair_request_key=fallback-frontier:2616:1:old; range_fingerprint=2616:1; durable_received_highest_chunk_index=2774; range_count=1; requested_chunk_count=1; settlement_outcome=SettledStalled; dispatch_observed=1"),
            LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; {common}; transfer_id={transferId}; authority_id={successor}; predecessor_authority_id={predecessor}; repair_generation=5; repair_request_key=fallback-frontier:2616:3:new; range_fingerprint=2616:3; durable_received_highest_chunk_index=2780; range_count=1; requested_chunk_count=3"),
            LogLine($"event=filetransfer_fallback_frontier_repair_dispatched; {common}; transfer_id={transferId}; authority_id={successor}; predecessor_authority_id={predecessor}; repair_generation=5; repair_request_key=fallback-frontier:2616:3:new; range_fingerprint=2616:3; durable_received_highest_chunk_index=2780; range_count=1; requested_chunk_count=3; dispatch_observed=1"),
            LogLine($"event=filetransfer_fallback_frontier_repair_progress_observed; {common}; transfer_id={transferId}; authority_id={successor}; predecessor_authority_id={predecessor}; repair_generation=5; repair_request_key=fallback-frontier:2616:3:new; range_fingerprint=2616:3; durable_received_highest_chunk_index=2780; range_count=1; requested_chunk_count=3; dispatch_observed=1"),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; session_id=sess_a; reason=fixture_terminal_after_successor"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; direction=inbound; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=peer_disconnected")
        };

        var result = compact
            ? await RunAnalyzeFixtureAsync(
                lines,
                environment: new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                })
            : await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        if (!compact)
        {
            Assert.Equal("1", verdict["classification_fallback_frontier_repair_stalled_count"]);
            Assert.Equal("pass", verdict["classification_fallback_frontier_repair_verdict"]);
        }
        Assert.NotEqual("fallback_frontier_repair_stalled", verdict["recovery_failure_class"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    public async Task AnalyzeRetained_PostFallbackFrontierStallGraceRecheck_HasFullCompactParity(
        bool compact,
        bool recovered)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_frontier_stall_grace";
        const string authorityId = "fta2-repair_replay-frontier-stall-grace";
        const string requestId = "v6-frontier:40:1";
        const string correlationId = "frq-frontier-stall-grace";
        const string repairKey = "fallback-frontier:40:1:frontier-stall-grace";
        const string rangeFingerprint = "frontier-stall-grace";
        const string scope = "direction=inbound; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; " +
            "live_route_epoch=4; runtime_generation=5; terminal_generation=0; leg_generation=5; transport_epoch=1; " +
            "bridge_recovery_generation=1; bridge_decision_generation=0; checkpoint_request_id=(none); " +
            "route_publication_generation=5; route_commit_generation=0; log_writer_id=writer";
        var repair = $"authority_schema=2; authority_id={authorityId}; predecessor_authority_id=(none); {scope}; " +
            $"repair_generation=4; repair_request_key={repairKey}; range_fingerprint={rangeFingerprint}; " +
            $"frontier_chunk_index=40; durable_received_highest_chunk_index=60; range_count=1; requested_chunk_count=1; " +
            $"source_kind=ReceiverRequest; primary_protocol_repair_request_id={requestId}; " +
            $"primary_request_correlation_id={correlationId}; target_transport=regular_nkn";
        var lines = new List<string>
        {
            LogLine($"event=filetransfer_route_selected; transfer_id={transferId}; {scope}; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn"),
            LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; transfer_id={transferId}; {repair}"),
            LogLine($"event=filetransfer_fallback_frontier_repair_contract_settled; transfer_id={transferId}; {repair}; settlement_outcome=ProgressObserved; dispatch_observed=1"),
            LogLine($"event=filetransfer_fallback_frontier_repair_progress_observed; transfer_id={transferId}; {repair}; previous_frontier_chunk_index=40; current_frontier_chunk_index=41"),
            LogLine($"event=filetransfer_v6_fallback_frontier_repair_progress_state_forced; transfer_id={transferId}; {repair}; previous_frontier_chunk_index=40; current_frontier_chunk_index=41; receiver_state_epoch=7; data_session_identity=data-session-a; data_session_generation=1; decision_status=Accepted"),
            LogLine($"event=filetransfer_v6_receiver_state_sent; transfer_id={transferId}; {scope}; epoch=7; contiguous_committed_chunk_index=41; durable_received_highest_chunk_index=60; log_writer_attempt_utc_ticks=639000000000000000"),
            LogLine($"event=filetransfer_v6_frontier_request_deferred; transfer_id={transferId}; direction=inbound; session_id=sess_a; frontier_chunk_index=41; highest_received_chunk_index=60; elapsed_ms=0; stall_grace_ms=750; stall_started_utc=2026-04-26T11:17:59Z; reason=chunk_batch_committed; log_writer_id=writer; log_writer_attempt_utc_ticks=639000000001000000", secondsOffset: 1)
        };
        if (recovered)
        {
            lines.Add(LogLine($"event=filetransfer_v6_sparse_write_committed; transfer_id={transferId}; session_id=sess_a; written_chunk_count=1; contiguous_chunks_committed=1; next_chunk_index=42; highest_received_chunk_index=60; log_writer_id=writer; log_writer_attempt_utc_ticks=639000000002000000", secondsOffset: 1));
        }

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string> { ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1" }
                : null);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Stall-grace analysis failed.{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("1", verdict["filetransfer_fallback_frontier_stall_grace_recheck_missing_raw_count"]);
        Assert.Equal("1", verdict["filetransfer_fallback_frontier_stall_grace_recheck_missing_distinct_count"]);
        Assert.Equal(recovered ? "1" : "0", verdict["filetransfer_fallback_frontier_stall_grace_recheck_missing_recovered_count"]);
        Assert.Equal(recovered ? "0" : "1", verdict["filetransfer_fallback_frontier_stall_grace_recheck_missing_unresolved_count"]);
        Assert.Equal(recovered ? "pass" : "fail", verdict["filetransfer_fallback_frontier_stall_grace_recheck_missing_verdict"]);
        if (!recovered)
        {
            Assert.Equal("filetransfer_fallback_frontier_stall_grace_recheck_missing", verdict["recovery_failure_class"]);
            Assert.Equal("settled_frontier_progress_successor_not_rechecked_after_stall_grace", verdict["failure_variant"]);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    public async Task AnalyzeRetained_PostFallbackFrontierRecentProgressGraceRecheck_HasFullCompactParity(
        bool compact,
        bool recovered)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_frontier_recent_progress_grace";
        const string authorityId = "fta2-repair_replay-frontier-recent-progress-grace";
        const string requestId = "v6-frontier:40:1";
        const string correlationId = "frq-frontier-recent-progress-grace";
        const string repairKey = "fallback-frontier:40:1:frontier-recent-progress-grace";
        const string rangeFingerprint = "frontier-recent-progress-grace";
        const string scope = "direction=inbound; session_id=sess_recent; route=post_tuna_fallback_v6; protocol_version=6; " +
            "live_route_epoch=4; runtime_generation=5; terminal_generation=0; leg_generation=5; transport_epoch=1; " +
            "bridge_recovery_generation=1; bridge_decision_generation=0; checkpoint_request_id=(none); checkpoint_generation=1; " +
            "route_publication_generation=5; route_commit_generation=0; log_writer_id=writer_recent";
        var repair = $"authority_schema=2; authority_id={authorityId}; predecessor_authority_id=(none); {scope}; " +
            $"repair_generation=4; repair_request_key={repairKey}; range_fingerprint={rangeFingerprint}; " +
            $"frontier_chunk_index=40; durable_received_highest_chunk_index=60; range_count=1; requested_chunk_count=1; " +
            $"source_kind=ReceiverRequest; primary_protocol_repair_request_id={requestId}; " +
            $"primary_request_correlation_id={correlationId}; target_transport=regular_nkn";
        var lines = new List<string>
        {
            LogLine($"event=filetransfer_route_selected; transfer_id={transferId}; {scope}; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn"),
            LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; transfer_id={transferId}; {repair}"),
            LogLine($"event=filetransfer_control_plane_delivery_observed; transfer_id={transferId}; {scope}; kind=frontier_request; reason=frontier_stall_grace_elapsed; repair_request_id={requestId}; request_correlation_id={correlationId}; repair_request_key={repairKey}; range_fingerprint={rangeFingerprint}; peer_visible_any=1; accepted_any=1"),
            LogLine($"event=filetransfer_fallback_frontier_repair_dispatched; transfer_id={transferId}; {repair}; dispatch_observed=1"),
            LogLine($"event=filetransfer_v6_frontier_request_sent; transfer_id={transferId}; {repair}; repair_request_id={requestId}; request_correlation_id={correlationId}; reason=frontier_stall_grace_elapsed"),
            LogLine($"event=filetransfer_fallback_frontier_repair_contract_settled; transfer_id={transferId}; {repair}; settlement_outcome=ProgressObserved; dispatch_observed=1"),
            LogLine($"event=filetransfer_fallback_frontier_repair_progress_observed; transfer_id={transferId}; {repair}; previous_frontier_chunk_index=40; current_frontier_chunk_index=41"),
            LogLine($"event=filetransfer_v6_fallback_frontier_repair_progress_state_forced; transfer_id={transferId}; {repair}; previous_frontier_chunk_index=40; current_frontier_chunk_index=41; receiver_state_epoch=7; data_session_identity=data-session-recent; data_session_generation=1; decision_status=Accepted"),
            LogLine($"event=filetransfer_v6_receiver_state_sent; transfer_id={transferId}; {scope}; epoch=7; contiguous_committed_chunk_index=41; durable_received_highest_chunk_index=60; data_session_identity=data-session-recent; data_session_generation=1"),
            LogLine($"event=filetransfer_v6_receiver_state_deferred; transfer_id={transferId}; {scope}; next_chunk_index=41; highest_received_chunk_index=60; reason=frontier_stalled; authority_effect=none")
        };
        if (recovered)
        {
            lines.Add(LogLine($"event=filetransfer_fallback_frontier_repair_progress_observed; transfer_id={transferId}; {repair}; previous_frontier_chunk_index=41; current_frontier_chunk_index=42"));
        }

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string> { ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1" }
                : null);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Recent-progress-grace analysis failed.{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("1", verdict["filetransfer_fallback_frontier_recent_progress_grace_recheck_missing_raw_count"]);
        Assert.Equal("1", verdict["filetransfer_fallback_frontier_recent_progress_grace_recheck_missing_distinct_count"]);
        Assert.Equal(recovered ? "1" : "0", verdict["filetransfer_fallback_frontier_recent_progress_grace_recheck_missing_recovered_count"]);
        Assert.Equal(recovered ? "0" : "1", verdict["filetransfer_fallback_frontier_recent_progress_grace_recheck_missing_unresolved_count"]);
        Assert.Equal(recovered ? "pass" : "fail", verdict["filetransfer_fallback_frontier_recent_progress_grace_recheck_missing_verdict"]);
        if (!recovered)
        {
            Assert.Equal("filetransfer_fallback_frontier_recent_progress_grace_recheck_missing", verdict["recovery_failure_class"]);
            Assert.Equal("settled_frontier_progress_successor_not_rechecked_after_recent_progress_grace", verdict["failure_variant"]);
        }
    }

    [Theory]
    [InlineData(false, "successor_deferral", true)]
    [InlineData(true, "successor_deferral", true)]
    [InlineData(false, "route_scope_recheck", true)]
    [InlineData(true, "route_scope_recheck", true)]
    [InlineData(false, "unknown_stale_reason", false)]
    [InlineData(true, "unknown_stale_reason", false)]
    [InlineData(false, "late_successor", false)]
    [InlineData(true, "late_successor", false)]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    public async Task AnalyzeRetained_PostFallbackFrontierStallGraceSupersession_HasFullCompactParity(
        bool compact,
        string closure,
        bool recovered)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_frontier_supersession";
        const string predecessorAuthority = "fta2-repair_replay-stall-predecessor";
        const string successorAuthority = "fta2-repair_replay-stall-successor";
        const string scope = "direction=inbound; session_id=sess_stall; route=post_tuna_fallback_v6; protocol_version=6; " +
            "live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; " +
            "bridge_recovery_generation=1; bridge_decision_generation=0; checkpoint_request_id=(none); " +
            "route_publication_generation=3; route_commit_generation=0; log_writer_id=writer_stall";
        const string stallScope = scope + "; checkpoint_generation=1; data_session_identity=data-session-stall; data_session_generation=1";
        var predecessor = $"authority_schema=2; authority_id={predecessorAuthority}; predecessor_authority_id=(none); {scope}; " +
            "repair_generation=4; repair_request_key=fallback-frontier:40:1:predecessor; range_fingerprint=predecessor; " +
            "frontier_chunk_index=40; durable_received_highest_chunk_index=60; range_count=1; requested_chunk_count=1; " +
            "source_kind=ReceiverRequest; primary_protocol_repair_request_id=v6-frontier:40:4; " +
            "primary_request_correlation_id=frq-stall-predecessor; target_transport=regular_nkn";
        var successor = $"authority_schema=2; authority_id={successorAuthority}; predecessor_authority_id={predecessorAuthority}; {scope}; " +
            "repair_generation=5; repair_request_key=fallback-frontier:41:1:successor; range_fingerprint=successor; " +
            "frontier_chunk_index=41; durable_received_highest_chunk_index=60; range_count=1; requested_chunk_count=1; " +
            "source_kind=ReceiverRequest; primary_protocol_repair_request_id=v6-frontier:41:5; " +
            "primary_request_correlation_id=frq-stall-successor; target_transport=regular_nkn";
        var lines = new List<string>
        {
            LogLine($"event=filetransfer_route_selected; transfer_id={transferId}; {scope}; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn"),
            LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; transfer_id={transferId}; {predecessor}"),
            LogLine($"event=filetransfer_fallback_frontier_repair_contract_settled; transfer_id={transferId}; {predecessor}; settlement_outcome=ProgressObserved; dispatch_observed=1"),
            LogLine($"event=filetransfer_fallback_frontier_repair_progress_observed; transfer_id={transferId}; {predecessor}; previous_frontier_chunk_index=40; current_frontier_chunk_index=41"),
            LogLine($"event=filetransfer_v6_fallback_frontier_repair_progress_state_forced; transfer_id={transferId}; {predecessor}; previous_frontier_chunk_index=40; current_frontier_chunk_index=41; receiver_state_epoch=7; data_session_identity=data-session-stall; data_session_generation=1; decision_status=Accepted"),
            LogLine($"event=filetransfer_v6_receiver_state_sent; transfer_id={transferId}; {scope}; epoch=7; contiguous_committed_chunk_index=41; durable_received_highest_chunk_index=60; transfer_paused=0; log_writer_attempt_utc_ticks=639000000000000000"),
            LogLine($"event=filetransfer_v6_frontier_request_deferred; transfer_id={transferId}; {stallScope}; frontier_chunk_index=41; highest_received_chunk_index=60; elapsed_ms=0; stall_grace_ms=750; stall_started_utc=2026-04-26T11:17:59.0000000+00:00; wake_generation=8; wake_deadline_utc=2026-04-26T11:17:59.7500000+00:00; reason=chunk_batch_committed; authority_effect=none; log_writer_attempt_utc_ticks=639000000001000000", secondsOffset: 1)
        };

        if (closure is "successor_deferral" or "unknown_stale_reason" or "late_successor")
        {
            var successorTicks = closure == "late_successor"
                ? "639000000009000000"
                : "639000000007000000";
            var successorStall = closure == "late_successor"
                ? "2026-04-26T11:17:59.8000000+00:00"
                : "2026-04-26T11:17:59.6000000+00:00";
            var successorDeadline = closure == "late_successor"
                ? "2026-04-26T11:18:00.5500000+00:00"
                : "2026-04-26T11:18:00.3500000+00:00";
            lines.Add(LogLine($"event=filetransfer_v6_frontier_request_deferred; transfer_id={transferId}; {stallScope}; frontier_chunk_index=44; highest_received_chunk_index=65; elapsed_ms=0; stall_grace_ms=750; stall_started_utc={successorStall}; wake_generation=9; wake_deadline_utc={successorDeadline}; reason=chunk_batch_committed; authority_effect=none; log_writer_attempt_utc_ticks={successorTicks}", secondsOffset: 2));
            var staleReason = closure == "unknown_stale_reason" ? "unexpected_stale_reason" : "wake_generation_replaced";
            lines.Add(LogLine($"event=filetransfer_v6_frontier_stall_grace_recheck_decided; transfer_id={transferId}; {stallScope}; frontier_chunk_index=41; highest_received_chunk_index=60; stall_started_utc=2026-04-26T11:17:59.0000000+00:00; wake_generation=8; wake_deadline_utc=2026-04-26T11:17:59.7500000+00:00; decision_status=stale; decision_reason={staleReason}; authority_effect=none; log_writer_attempt_utc_ticks=639000000010000000", secondsOffset: 3));
            lines.Add(LogLine($"event=file_transfer_inbound_terminal; direction=inbound; session_id=sess_stall; transfer_id={transferId}; state=Failed; error_code=fixture_end; saved_path=(none); log_writer_id=writer_stall; log_writer_attempt_utc_ticks=639000000011000000", secondsOffset: 4));
        }
        else
        {
            lines.Add(LogLine($"event=filetransfer_v6_frontier_stall_grace_recheck_decided; transfer_id={transferId}; {stallScope}; frontier_chunk_index=41; highest_received_chunk_index=60; stall_started_utc=2026-04-26T11:17:59.0000000+00:00; wake_generation=8; wake_deadline_utc=2026-04-26T11:17:59.7500000+00:00; decision_status=stale; decision_reason=route_scope_changed; authority_effect=none; log_writer_attempt_utc_ticks=639000000009000000", secondsOffset: 2));
            lines.Add(LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; transfer_id={transferId}; {successor}", secondsOffset: 3));
            lines.Add(LogLine($"event=filetransfer_control_plane_delivery_observed; direction=inbound; session_id=sess_stall; transfer_id={transferId}; log_writer_id=writer_stall; kind=frontier_request; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=3; bridge_recovery_generation=1; transport_epoch=1; checkpoint_request_id=(none); reason=retry; repair_request_id=v6-frontier:41:5; request_correlation_id=frq-stall-successor; repair_request_key=fallback-frontier:41:1:successor; range_fingerprint=successor; peer_visible_any=1; accepted_any=1", secondsOffset: 4));
            lines.Add(LogLine($"event=filetransfer_fallback_frontier_repair_dispatched; transfer_id={transferId}; {successor}; dispatch_observed=1", secondsOffset: 5));
            lines.Add(LogLine($"event=filetransfer_v6_frontier_request_sent; transfer_id={transferId}; {successor}; repair_request_id=v6-frontier:41:5; request_correlation_id=frq-stall-successor; reason=retry", secondsOffset: 6));
        }

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string> { ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1" }
                : null);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Superseded stall-grace analysis failed.{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        // The frontier-44 deferral is recovery evidence, not a separately admitted hold:
        // it has neither a matching repair-progress lineage nor exact published-source admission.
        Assert.Equal(
            "1",
            verdict["filetransfer_fallback_frontier_stall_grace_recheck_missing_raw_count"]);
        Assert.Equal("1", verdict["filetransfer_fallback_frontier_stall_grace_recheck_missing_distinct_count"]);
        Assert.Equal(recovered ? "1" : "0", verdict["filetransfer_fallback_frontier_stall_grace_recheck_missing_recovered_count"]);
        Assert.Equal(recovered ? "0" : "1", verdict["filetransfer_fallback_frontier_stall_grace_recheck_missing_unresolved_count"]);
        Assert.Equal(recovered ? "pass" : "fail", verdict["filetransfer_fallback_frontier_stall_grace_recheck_missing_verdict"]);
    }

    [Theory]
    [InlineData(false, "complete", true)]
    [InlineData(true, "complete", true)]
    [InlineData(false, "missing_finalize", false)]
    [InlineData(true, "missing_finalize", false)]
    [InlineData(false, "wrong_frontier", false)]
    [InlineData(true, "failed_terminal", false)]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    public async Task AnalyzeRetained_PostFallbackFrontierStallGraceTerminalFrontier_HasFullCompactParity(
        bool compact,
        string outcome,
        bool recovered)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_frontier_terminal";
        const string sessionId = "sess_terminal";
        const string writer = "writer_terminal";
        const string predecessorAuthority = "fta2-repair_replay-terminal-predecessor";
        const string successorAuthority = "fta2-repair_replay-terminal-successor";
        const string successorRequest = "v6-frontier:41:2";
        const string successorCorrelation = "frq-terminal-successor";
        const string predecessorKey = "fallback-frontier:40:1:predecessor-range";
        const string successorKey = "fallback-frontier:41:1:successor-range";
        const string scope = "direction=inbound; session_id=sess_terminal; route=post_tuna_fallback_v6; protocol_version=6; " +
            "live_route_epoch=4; runtime_generation=5; terminal_generation=0; leg_generation=5; transport_epoch=1; " +
            "bridge_recovery_generation=1; bridge_decision_generation=0; checkpoint_request_id=(none); " +
            "route_publication_generation=5; route_commit_generation=0; log_writer_id=writer_terminal";
        var predecessor = $"authority_schema=2; authority_id={predecessorAuthority}; predecessor_authority_id=(none); {scope}; " +
            $"repair_generation=4; repair_request_key={predecessorKey}; range_fingerprint=predecessor-range; " +
            "frontier_chunk_index=40; durable_received_highest_chunk_index=60; range_count=1; requested_chunk_count=1; " +
            "source_kind=ReceiverRequest; primary_protocol_repair_request_id=v6-frontier:40:1; " +
            "primary_request_correlation_id=frq-terminal-predecessor; target_transport=regular_nkn";
        var successor = $"authority_schema=2; authority_id={successorAuthority}; predecessor_authority_id={predecessorAuthority}; {scope}; " +
            $"repair_generation=5; repair_request_key={successorKey}; range_fingerprint=successor-range; " +
            "frontier_chunk_index=41; durable_received_highest_chunk_index=60; range_count=1; requested_chunk_count=1; " +
            $"source_kind=ReceiverRequest; primary_protocol_repair_request_id={successorRequest}; " +
            $"primary_request_correlation_id={successorCorrelation}; target_transport=regular_nkn";
        var terminalFrontier = outcome == "wrong_frontier" ? 60 : 61;
        var lines = new List<string>
        {
            LogLine($"event=filetransfer_route_selected; transfer_id={transferId}; {scope}; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn"),
            LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; transfer_id={transferId}; {predecessor}"),
            LogLine($"event=filetransfer_fallback_frontier_repair_contract_settled; transfer_id={transferId}; {predecessor}; settlement_outcome=ProgressObserved; dispatch_observed=1"),
            LogLine($"event=filetransfer_fallback_frontier_repair_progress_observed; transfer_id={transferId}; {predecessor}; previous_frontier_chunk_index=40; current_frontier_chunk_index=41"),
            LogLine($"event=filetransfer_v6_fallback_frontier_repair_progress_state_forced; transfer_id={transferId}; {predecessor}; previous_frontier_chunk_index=40; current_frontier_chunk_index=41; receiver_state_epoch=7; data_session_identity=data-session-terminal; data_session_generation=1; decision_status=Accepted"),
            LogLine($"event=filetransfer_v6_receiver_state_sent; transfer_id={transferId}; {scope}; epoch=7; contiguous_committed_chunk_index=41; durable_received_highest_chunk_index=60; log_writer_attempt_utc_ticks=639000000000000000"),
            LogLine($"event=filetransfer_v6_frontier_request_deferred; transfer_id={transferId}; {scope}; data_session_identity=data-session-terminal; data_session_generation=1; frontier_chunk_index=41; highest_received_chunk_index=60; elapsed_ms=0; stall_grace_ms=750; stall_started_utc=2026-04-26T11:17:59.0000000+00:00; wake_generation=8; wake_deadline_utc=2026-04-26T11:17:59.7500000+00:00; reason=retry; log_writer_attempt_utc_ticks=639000000001000000", secondsOffset: 1),
            LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; transfer_id={transferId}; {successor}", secondsOffset: 2),
            LogLine($"event=filetransfer_control_plane_delivery_observed; transfer_id={transferId}; direction=inbound; session_id={sessionId}; log_writer_id={writer}; kind=frontier_request; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=1; transport_epoch=1; checkpoint_request_id=(none); reason=frontier_stall_grace_elapsed; repair_request_id={successorRequest}; request_correlation_id={successorCorrelation}; repair_request_key={successorKey}; range_fingerprint=successor-range; peer_visible_any=1; accepted_any=1", secondsOffset: 2),
            LogLine($"event=filetransfer_fallback_frontier_repair_dispatched; transfer_id={transferId}; {successor}; protocol_repair_request_id={successorRequest}; request_correlation_id={successorCorrelation}; dispatch_observed=1", secondsOffset: 2),
            LogLine($"event=filetransfer_v6_frontier_request_sent; transfer_id={transferId}; {successor}; repair_request_id={successorRequest}; request_correlation_id={successorCorrelation}; reason=frontier_stall_grace_elapsed", secondsOffset: 2),
            LogLine($"event=filetransfer_v6_frontier_stall_grace_recheck_decided; transfer_id={transferId}; {scope}; data_session_identity=data-session-terminal; data_session_generation=1; frontier_chunk_index=41; highest_received_chunk_index=60; stall_started_utc=2026-04-26T11:17:59.0000000+00:00; wake_generation=8; wake_deadline_utc=2026-04-26T11:17:59.7500000+00:00; decision_status=dispatched; decision_reason=exact_receiver_request_dispatched; authority_effect=none", secondsOffset: 2),
            LogLine($"event=filetransfer_fallback_frontier_repair_contract_settled; transfer_id={transferId}; {successor}; settlement_outcome=ProgressObserved; dispatch_observed=1", secondsOffset: 3),
            LogLine($"event=filetransfer_fallback_frontier_repair_progress_observed; transfer_id={transferId}; {successor}; previous_frontier_chunk_index=41; current_frontier_chunk_index={terminalFrontier}", secondsOffset: 3),
            LogLine($"event=filetransfer_fallback_tail_terminal_proof_consumed; direction=inbound; transfer_id={transferId}; session_id={sessionId}; leg_id=leg:5; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=1; committed_chunk=61; chunk_count=61; reason=chunk_batch_committed; log_writer_id={writer}", secondsOffset: 3),
            LogLine($"event=integrity_verify_passed; direction=inbound; transfer_id={transferId}; session_id={sessionId}; file_size_bytes=268435456; log_writer_id={writer}", secondsOffset: 4)
        };
        if (outcome != "missing_finalize")
        {
            lines.Add(LogLine($"event=temp_finalize_succeeded; direction=inbound; transfer_id={transferId}; session_id={sessionId}; file_size_bytes=268435456; saved_path=C:\\received\\payload.bin; log_writer_id={writer}", secondsOffset: 5));
        }
        lines.Add(LogLine($"event=file_transfer_inbound_terminal; role=Helper; session_id={sessionId}; transfer_id={transferId}; state={(outcome == "failed_terminal" ? "Failed" : "Completed")}; error_code={(outcome == "failed_terminal" ? "integrity_failed" : "(none)")}; saved_path=C:\\received\\payload.bin; log_writer_id={writer}", secondsOffset: 6));

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string> { ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1" }
                : null);

        Assert.True(
            result.Script.ExitCode == 0,
            $"Terminal-frontier analysis failed.{Environment.NewLine}{result.Script.Stdout}{Environment.NewLine}{result.Script.Stderr}");
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("1", verdict["filetransfer_fallback_frontier_stall_grace_recheck_missing_raw_count"]);
        Assert.Equal("1", verdict["filetransfer_fallback_frontier_stall_grace_recheck_missing_distinct_count"]);
        Assert.Equal(recovered ? "1" : "0", verdict["filetransfer_fallback_frontier_stall_grace_recheck_missing_recovered_count"]);
        Assert.Equal(recovered ? "0" : "1", verdict["filetransfer_fallback_frontier_stall_grace_recheck_missing_unresolved_count"]);
        Assert.Equal(recovered ? "pass" : "fail", verdict["filetransfer_fallback_frontier_stall_grace_recheck_missing_verdict"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    public async Task AnalyzeRetained_PostFallbackQueuedPredecessorSupersessionMissingDispatch_HasFullCompactParity(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_frontier_successor_dispatch";
        const string predecessor = "fta2-repair_replay-predecessor";
        const string successor = "fta2-repair_replay-successor";
        const string scope = "direction=outbound; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; " +
            "live_route_epoch=4; runtime_generation=6; terminal_generation=0; leg_generation=6; transport_epoch=1; " +
            "bridge_recovery_generation=1; bridge_decision_generation=0; checkpoint_request_id=(none); " +
            "route_publication_generation=6; route_commit_generation=0; frontier_chunk_index=6204; " +
            "source_kind=PeerRequest; priority=frontier; target_transport=regular_nkn; log_writer_id=writer";
        const string predecessorTuple = "repair_generation=13; repair_request_key=fallback-frontier:6204:32:range-a; " +
            "range_fingerprint=range-a; primary_protocol_repair_request_id=v6-frontier:6204:23; " +
            "primary_request_correlation_id=correlation-a";
        const string successorTuple = "repair_generation=14; repair_request_key=fallback-frontier:6204:12:range-b; " +
            "range_fingerprint=range-b; primary_protocol_repair_request_id=v6-frontier:1:6204:24; " +
            "primary_request_correlation_id=correlation-b";
        var lines = new[]
        {
            LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; transfer_id={transferId}; {scope}; authority_id={predecessor}; predecessor_authority_id=(none); {predecessorTuple}; requested_chunk_count=32; log_writer_sequence=1"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; log_writer_id=writer; repair_request_key=fallback-frontier:6204:32:range-a; protocol_repair_request_id=v6-frontier:6204:23; first_start_chunk_index=6204; last_end_chunk_exclusive=6205; requested_chunk_count=1; scheduled_chunk_count=1; log_writer_sequence=2"),
            LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; transfer_id={transferId}; {scope}; authority_id={successor}; predecessor_authority_id={predecessor}; {successorTuple}; requested_chunk_count=12; log_writer_sequence=3"),
            LogLine($"event=filetransfer_fallback_frontier_repair_contract_settled; transfer_id={transferId}; {scope}; authority_id={predecessor}; predecessor_authority_id=(none); successor_authority_id={successor}; {predecessorTuple}; requested_chunk_count=32; settlement_outcome=Superseded; dispatch_observed=0; decision_reason=fallback_frontier_successor_authorized; log_writer_sequence=4"),
            LogLine($"event=filetransfer_fallback_frontier_repair_contract_settled; transfer_id={transferId}; {scope}; authority_id={successor}; predecessor_authority_id={predecessor}; successor_authority_id=(none); {successorTuple}; requested_chunk_count=12; settlement_outcome=MissingDispatch; dispatch_observed=0; decision_reason=chunks_already_queued_before_dispatch; log_writer_sequence=5")
        };

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string> { ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1" }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("1", verdict["filetransfer_fallback_frontier_repair_successor_dispatch_missing_raw_count"]);
        Assert.Equal("1", verdict["filetransfer_fallback_frontier_repair_successor_dispatch_missing_distinct_count"]);
        Assert.Equal("0", verdict["filetransfer_fallback_frontier_repair_successor_dispatch_missing_recovered_count"]);
        Assert.Equal("1", verdict["filetransfer_fallback_frontier_repair_successor_dispatch_missing_unresolved_count"]);
        Assert.Equal("fail", verdict["filetransfer_fallback_frontier_repair_successor_dispatch_missing_verdict"]);
        Assert.Equal("filetransfer_fallback_frontier_repair_successor_dispatch_missing", verdict["recovery_failure_class"]);
        Assert.Equal("successor_missing_dispatch_due_predecessor_queued_chunk_ownership", verdict["failure_variant"]);
        Assert.Equal("route_runtime", verdict["semantic_failure_domain"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    public async Task AnalyzeRetained_PostFallbackDuplicateStateReplayAmplification_HasFullCompactParity(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_duplicate_state_replay";
        const string scope = "direction=outbound; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; " +
            "live_route_epoch=4; runtime_generation=5; terminal_generation=0; leg_generation=5; transport_epoch=1; " +
            "bridge_recovery_generation=1; route_publication_generation=5; route_commit_generation=0; log_writer_id=writer";
        const string fingerprint = "v4sf1-duplicate-state";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v4_state_received; transfer_id={transferId}; {scope}; data_session_generation=1; epoch=8; previous_epoch=7; applied=1; stale=0; duplicate=0; v4_state_wire_fingerprint={fingerprint}; contiguous_committed_chunk_index=40; missing_range_count=1; log_writer_sequence=1"),
            LogLine($"event=filetransfer_v4_state_received; transfer_id={transferId}; {scope}; data_session_generation=1; epoch=8; previous_epoch=8; applied=0; duplicate=1; v4_state_wire_fingerprint={fingerprint}; contiguous_committed_chunk_index=40; missing_range_count=1; log_writer_sequence=2"),
            LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; transfer_id={transferId}; {scope}; authority_id=repair-a; repair_generation=1; repair_request_key=fallback-frontier:40:64:range; range_fingerprint=range; frontier_chunk_index=40; requested_chunk_count=64; source_kind=SenderLivenessRescue; authority_decision_reason=duplicate_frontier_rebind; protocol_repair_request_id=transport_rebind_safety_replay:1:40:40:64; log_writer_sequence=3"),
            LogLine($"event=filetransfer_transport_rebind_safety_replay_started; transfer_id={transferId}; session_id=sess_a; log_writer_id=writer; reason=duplicate_frontier_rebind; rebind_generation=1; remote_next_expected_chunk_index=40; replay_start_chunk_index=40; replay_end_chunk_exclusive=104; requested_chunk_count=64; scheduled_chunk_count=64; post_tuna_v6_frontier_sweep=1; log_writer_sequence=4")
        };

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string> { ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1" }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("1", verdict["filetransfer_post_tuna_fallback_duplicate_state_replay_amplification_raw_count"]);
        Assert.Equal("1", verdict["filetransfer_post_tuna_fallback_duplicate_state_replay_amplification_distinct_count"]);
        Assert.Equal("0", verdict["filetransfer_post_tuna_fallback_duplicate_state_replay_amplification_recovered_count"]);
        Assert.Equal("1", verdict["filetransfer_post_tuna_fallback_duplicate_state_replay_amplification_unresolved_count"]);
        Assert.Equal("fail", verdict["filetransfer_post_tuna_fallback_duplicate_state_replay_amplification_verdict"]);
        Assert.Equal("filetransfer_post_tuna_fallback_duplicate_state_replay_amplification", verdict["recovery_failure_class"]);
        Assert.Equal("redundant_control_bulk_state_copy_started_64_chunk_repair_sweep", verdict["failure_variant"]);
        Assert.Equal("route_runtime", verdict["semantic_failure_domain"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "FallbackFrontierRepair")]
    [Trait("RetainedFallbackSuite", "Efficiency")]
    public async Task AnalyzeRetained_PostFallbackExactFrontierRepairPreemptedBySenderWait_HasFullCompactParity(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_exact_frontier_preemption";
        const string scope = "direction=outbound; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; " +
            "live_route_epoch=4; runtime_generation=5; terminal_generation=0; leg_generation=5; transport_epoch=1; " +
            "bridge_recovery_generation=1; bridge_decision_generation=2; checkpoint_request_id=checkpoint-a; " +
            "route_publication_generation=5; route_commit_generation=4; log_writer_id=writer";
        const string requestId = "peer-request-1";
        const string correlationId = "correlation-1";
        const string repairKey = "fallback-frontier:1079:1:range-a";
        var lines = new[]
        {
            LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; transfer_id={transferId}; {scope}; authority_id=repair-a; repair_generation=1; repair_request_key={repairKey}; range_fingerprint=range-a; frontier_chunk_index=1079; requested_chunk_count=1; source_kind=PeerRequest; primary_protocol_repair_request_id={requestId}; primary_request_correlation_id={correlationId}; log_writer_sequence=1"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; log_writer_id=writer; repair_request_key={repairKey}; protocol_repair_request_id={requestId}; first_start_chunk_index=1079; requested_chunk_count=1; log_writer_sequence=2"),
            LogLine($"event=filetransfer_v6_frontier_request_receive_dispatched; transfer_id={transferId}; {scope}; repair_request_id={requestId}; request_correlation_id={correlationId}; first_start_chunk_index=1079; first_chunk_count=1; data_session_generation=3; log_writer_sequence=3"),
            LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; {scope}; protocol_repair_request_id={requestId}; first_start_chunk_index=1079; sent_chunk_count=1; data_session_generation=3; log_writer_sequence=4"),
            LogLine($"event=filetransfer_fallback_frontier_repair_dispatched; transfer_id={transferId}; {scope}; authority_id=repair-a; repair_request_key={repairKey}; range_fingerprint=range-a; dispatch_observed=1; log_writer_attempt_utc_ticks=100000000; log_writer_sequence=5"),
            LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; transfer_id={transferId}; {scope}; authority_id=repair-b; predecessor_authority_id=repair-a; repair_generation=2; repair_request_key=fallback-frontier:1079:64:range-b; range_fingerprint=range-b; frontier_chunk_index=1079; requested_chunk_count=64; source_kind=SenderLivenessRescue; authority_decision_reason=post_fallback_sender_wait; primary_protocol_repair_request_id=transport_rebind_safety_replay:7:1079:1079:64; log_writer_attempt_utc_ticks=102580000; log_writer_sequence=6"),
            LogLine($"event=filetransfer_fallback_frontier_repair_contract_settled; transfer_id={transferId}; {scope}; authority_id=repair-a; successor_authority_id=repair-b; settlement_outcome=Superseded; decision_reason=post_fallback_sender_wait; log_writer_sequence=7"),
            LogLine($"event=filetransfer_transport_rebind_safety_replay_started; transfer_id={transferId}; session_id=sess_a; log_writer_id=writer; reason=post_fallback_sender_wait; rebind_generation=7; remote_next_expected_chunk_index=1079; replay_start_chunk_index=1079; replay_end_chunk_exclusive=1143; requested_chunk_count=64; scheduled_chunk_count=64; post_tuna_v6_frontier_sweep=1; log_writer_sequence=8")
        };

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string> { ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1" }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("1", verdict["filetransfer_post_tuna_fallback_exact_frontier_repair_preemption_raw_count"]);
        Assert.Equal("1", verdict["filetransfer_post_tuna_fallback_exact_frontier_repair_preemption_distinct_count"]);
        Assert.Equal("0", verdict["filetransfer_post_tuna_fallback_exact_frontier_repair_preemption_recovered_count"]);
        Assert.Equal("1", verdict["filetransfer_post_tuna_fallback_exact_frontier_repair_preemption_unresolved_count"]);
        Assert.Equal("fail", verdict["filetransfer_post_tuna_fallback_exact_frontier_repair_preemption_verdict"]);
        Assert.Equal("filetransfer_post_tuna_fallback_exact_frontier_repair_preempted_by_sender_liveness_replay", verdict["recovery_failure_class"]);
        Assert.Equal("canonical_fallback_frontier_key_not_resolved_by_legacy_exact_repair_suppression", verdict["failure_variant"]);
        Assert.Equal("route_runtime", verdict["semantic_failure_domain"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ExplicitFallbackFrontierRepairStalledThenTerminalCompletion_DoesNotFail()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_frontier_repair_stalled_then_complete";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=6"),
            LogLine($"event=filetransfer_fallback_frontier_repair_stalled; direction=inbound; transfer_id={transferId}; session_id=sess_a; repair_request_key=v6-frontier:2786:2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=6; leg_generation=8; transport_epoch=3; bridge_recovery_generation=2; reason=post_tuna_fallback_v6_frontier_rescue_without_progress; frontier_chunk_index=2786; durable_received_highest_chunk_index=2785; requested_chunk_count=64; attempt_count=2; frontier_stall_age_ms=2000"),
            LogLine($"event=transfer_terminal; direction=inbound; transfer_id={transferId}; session_id=sess_a; file_size_bytes=67108864; bytes_transferred=67108864; chunks_transferred=3121; chunk_count=3121; error_code=(none); reason=Transfer complete.; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active"),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id=sess_a; file_size_bytes=67108864; bytes_transferred=67108864; chunks_transferred=3121; chunk_count=3121; error_code=(none); reason=Transfer complete.; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("1", route["fallback_frontier_repair_stalled_count"]);
        Assert.Equal("pass", route["fallback_frontier_repair_verdict"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("fallback_frontier_repair_stalled", verdict["recovery_failure_class"]);
        Assert.Equal("pass", verdict["classification_fallback_frontier_repair_verdict"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "OutboundReceiveWorkerHandoff")]
    public async Task AnalyzeRetained_StaleV4WorkerFollowedByUnconsumedFallbackFrontier_ClassifiesMissingHandoff()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_receive_worker_handoff_missing";
        const string currentTuple =
            "direction=outbound; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=5; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn";
        var lines = new[]
        {
            LogLine($"event=filetransfer_runtime_started; role=sender; transfer_id={transferId}; {currentTuple}; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 0),
            LogLine($"event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=v4_sender_failed; attempted_action=read_failed; reason=runtime_generation_mismatch; runtime_generation=1; current_runtime_generation=3; terminal_generation=0; current_terminal_generation=0; route=regular_nkn_v4_fast; current_route=post_tuna_fallback_v6; protocol_version=4; current_protocol_version=6; live_route_epoch=0; current_live_route_epoch=2; runtime_family=regular_v4; current_runtime_family=post_tuna_fallback_v6", secondsOffset: 1),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=3; bridge_recovery_generation=1; transport_epoch=1; frame_type=filetransfer.frontier_request.v6; repair_request_id=v6-frontier:2709:33; request_correlation_id=frq-missing; first_start_chunk_index=2709; first_chunk_count=32; range_count=1; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0", secondsOffset: 2),
            LogLine($"event=filetransfer_v6_frontier_request_transport_queued; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.frontier_request.v6; lane=control; actual_transport=regular_nkn; priority=1; queue_capacity_bypassed=1; queued_frames=144; queued_bytes=147456; transport_epoch=1; repair_request_id=v6-frontier:2709:33; request_correlation_id=frq-missing; range_count=1; first_start_chunk_index=2709; requested_chunk_count=32; decision_reason=queued_for_core", secondsOffset: 3),
            LogLine($"event=filetransfer_v6_frontier_request_transport_rejected; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.frontier_request.v6; lane=control; actual_transport=regular_nkn; priority=1; queue_capacity_bypassed=1; queued_frames=144; queued_bytes=147456; transport_epoch=1; repair_request_id=v6-frontier:2709:33; request_correlation_id=frq-missing; decision_reason=priority_control_queue_capacity_exceeded", secondsOffset: 4),
            LogLine($"event=filetransfer_data_frame_ignored; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.frontier_request.v6; route=post_tuna_fallback_v6; protocol_version=6; reason=route_hint_missing", secondsOffset: 5)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("post_tuna_fallback_outbound_receive_worker_handoff_missing", verdict["recovery_failure_class"]);
        Assert.Equal("post_tuna_fallback_outbound_receive_worker_handoff_missing", verdict["first_proof_chain_failure_class"]);
        Assert.Equal("filetransfer_runtime_owner_stale_update_rejected", verdict["first_proof_chain_failure_event"]);
        Assert.Equal("runtime_generation_mismatch", verdict["first_proof_chain_failure_reason"]);
        Assert.Equal("1", verdict["post_tuna_fallback_outbound_receive_worker_handoff_missing_count"]);
        Assert.NotEqual("fallback_frontier_request_dispatch_missing", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "OutboundReceiveWorkerHandoff")]
    public async Task AnalyzeRetained_ExactWorkerInstallAndFallbackDispatch_ClearsMissingHandoffCandidate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_receive_worker_handoff_resolved";
        const string repairKey = "v6-frontier:2709:1";
        const string currentTuple =
            "direction=outbound; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=5; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn";
        var installDescriptor = OutboundSparseInstallDescriptor(transferId);
        var lines = new[]
        {
            LogLine($"event=filetransfer_runtime_started; role=sender; transfer_id={transferId}; {currentTuple}; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 0),
            LogLine($"event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=v4_sender_failed; attempted_action=read_failed; reason=runtime_generation_mismatch; runtime_generation=1; current_runtime_generation=3; terminal_generation=0; current_terminal_generation=0; route=regular_nkn_v4_fast; current_route=post_tuna_fallback_v6; protocol_version=4; current_protocol_version=6; live_route_epoch=0; current_live_route_epoch=2; runtime_family=regular_v4; current_runtime_family=post_tuna_fallback_v6", secondsOffset: 1),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=3; bridge_recovery_generation=1; transport_epoch=1; frame_type=filetransfer.frontier_request.v6; repair_request_id={repairKey}; request_correlation_id=frq-resolved; first_start_chunk_index=2709; first_chunk_count=1; range_count=1; control_queue=1; control_copy=1; bulk_copy=1; peer_visible_any=1; accepted_any=1; local_only_rejected=0", secondsOffset: 2),
            LogLine($"event=filetransfer_outbound_sparse_worker_handoff_required; transfer_id={transferId}; {currentTuple}; {installDescriptor}; decision_reason=stale_worker_superseded", secondsOffset: 3),
            LogLine($"event=filetransfer_outbound_sparse_worker_handoff_installed; transfer_id={transferId}; {currentTuple}; {installDescriptor}; decision_reason=stale_worker_superseded", secondsOffset: 4),
            LogLine($"event=filetransfer_v6_frontier_request_receive_dispatched; transfer_id={transferId}; {currentTuple}; {installDescriptor}; transport_epoch=1; bridge_recovery_generation=1; repair_request_id={repairKey}; request_correlation_id=frq-resolved; first_start_chunk_index=2709; first_chunk_count=1; range_count=1; received_transport=regular_nkn; decision_reason=queued_for_sender_repair", secondsOffset: 5),
            LogLine($"event=filetransfer_fallback_frontier_repair_authority_granted; direction=outbound; transfer_id={transferId}; session_id=sess_a; repair_request_key={repairKey}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; frontier_chunk_index=2709; requested_chunk_count=1; decision_generation=7", secondsOffset: 6),
            LogLine($"event=filetransfer_fallback_frontier_repair_dispatched; direction=outbound; transfer_id={transferId}; session_id=sess_a; repair_request_key={repairKey}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=3; transport_epoch=1; bridge_recovery_generation=1; frontier_chunk_index=2709; requested_chunk_count=1; decision_generation=7", secondsOffset: 7),
            LogLine($"event=filetransfer_fallback_frontier_repair_progress_observed; direction=inbound; transfer_id={transferId}; session_id=sess_a; repair_request_key={repairKey}; reason=frontier_advanced; committed_frontier_before=2709; committed_frontier_after=2710; requested_chunk_count=1", secondsOffset: 8)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("post_tuna_fallback_outbound_receive_worker_handoff_missing", verdict["recovery_failure_class"]);
        Assert.Equal("0", verdict["post_tuna_fallback_outbound_receive_worker_handoff_missing_count"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "OutboundReceiveWorkerHandoff")]
    public async Task AnalyzeRetained_StaleWorkerFollowedByOutboundTerminal_ClearsMissingHandoffCandidate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_receive_worker_terminal";
        const string currentTuple =
            "direction=outbound; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=5; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn";
        var lines = new[]
        {
            LogLine($"event=filetransfer_runtime_started; role=sender; transfer_id={transferId}; {currentTuple}; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 0),
            LogLine($"event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=v4_sender_failed; attempted_action=read_failed; reason=runtime_generation_mismatch; runtime_generation=1; current_runtime_generation=3; terminal_generation=0; current_terminal_generation=0; route=regular_nkn_v4_fast; current_route=post_tuna_fallback_v6; protocol_version=4; current_protocol_version=6; live_route_epoch=0; current_live_route_epoch=2; runtime_family=regular_v4; current_runtime_family=post_tuna_fallback_v6", secondsOffset: 1),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=3; bridge_recovery_generation=1; transport_epoch=1; frame_type=filetransfer.frontier_request.v6; repair_request_id=v6-frontier:2709:1; request_correlation_id=frq-terminal; first_start_chunk_index=2709; first_chunk_count=1; range_count=1; peer_visible_any=1; accepted_any=1", secondsOffset: 2),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=peer_disconnected", secondsOffset: 3)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("post_tuna_fallback_outbound_receive_worker_handoff_missing", verdict["recovery_failure_class"]);
        Assert.Equal("0", verdict["post_tuna_fallback_outbound_receive_worker_handoff_missing_count"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "OutboundReceiveWorkerHandoff")]
    public async Task AnalyzeRetained_SupersedingPublicationInvalidatesOldWorkerCandidateInFullAndCompactModes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_receive_worker_superseded";
        const string firstTuple =
            "direction=outbound; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=5; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn";
        const string successorTuple =
            "direction=outbound; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; runtime_generation=5; terminal_generation=0; leg_generation=5; route_publication_generation=8; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn";
        var lines = new[]
        {
            LogLine($"event=filetransfer_runtime_started; role=sender; transfer_id={transferId}; {firstTuple}; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 0),
            LogLine($"event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=v4_sender_failed; attempted_action=read_failed; reason=runtime_generation_mismatch; runtime_generation=1; current_runtime_generation=3; terminal_generation=0; current_terminal_generation=0; route=regular_nkn_v4_fast; current_route=post_tuna_fallback_v6; protocol_version=4; current_protocol_version=6; live_route_epoch=0; current_live_route_epoch=2; runtime_family=regular_v4; current_runtime_family=post_tuna_fallback_v6", secondsOffset: 1),
            LogLine($"event=filetransfer_runtime_started; role=sender; transfer_id={transferId}; {successorTuple}; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 2),
            LogLine($"event=filetransfer_outbound_sparse_worker_handoff_installed; transfer_id={transferId}; {successorTuple}; data_session_generation=2; predecessor_install_generation=2; install_generation=3; installation_reason=publication_superseded", secondsOffset: 3),
            LogLine($"event=filetransfer_v6_frontier_request_receive_dispatched; transfer_id={transferId}; {successorTuple}; transport_epoch=2; bridge_recovery_generation=2; repair_request_id=v6-frontier:2710:1; request_correlation_id=frq-successor; first_start_chunk_index=2710; first_chunk_count=1; range_count=1; decision_reason=queued_for_sender_repair", secondsOffset: 4),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=2; transport_epoch=2; frame_type=filetransfer.frontier_request.v6; repair_request_id=v6-frontier:2710:1; request_correlation_id=frq-successor; first_start_chunk_index=2710; first_chunk_count=1; range_count=1; peer_visible_any=1; accepted_any=1", secondsOffset: 5)
        };

        var full = await RunAnalyzeFixtureAsync(lines);
        Assert.Equal(0, full.Script.ExitCode);
        var fullVerdict = ReadArtifactReport(full.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("post_tuna_fallback_outbound_receive_worker_handoff_missing", fullVerdict["recovery_failure_class"]);
        Assert.Equal("0", fullVerdict["post_tuna_fallback_outbound_receive_worker_handoff_missing_count"]);

        var compact = await RunAnalyzeFixtureAsync(
            lines,
            environment: new Dictionary<string, string>
            {
                ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
            });
        Assert.Equal(0, compact.Script.ExitCode);
        var compactVerdict = ReadArtifactReport(compact.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("post_tuna_fallback_outbound_receive_worker_handoff_missing", compactVerdict["recovery_failure_class"]);
        Assert.Equal("(none)", compactVerdict["first_proof_chain_failure_class"]);
        Assert.Equal("0", compactVerdict["post_tuna_fallback_outbound_receive_worker_handoff_missing_count"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "OutboundReceiveWorkerHandoff")]
    public async Task AnalyzeRetained_RecoveredEarlierWorkerPressureDoesNotHideLaterMissingHandoff()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_receive_worker_later_missing";
        const string firstTuple =
            "direction=outbound; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=5; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn";
        const string secondTuple =
            "direction=outbound; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; runtime_generation=5; terminal_generation=0; leg_generation=5; route_publication_generation=8; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn";
        var firstInstallDescriptor = OutboundSparseInstallDescriptor(transferId);
        var lines = new[]
        {
            LogLine($"event=filetransfer_runtime_started; role=sender; transfer_id={transferId}; {firstTuple}; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 0),
            LogLine($"event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=v4_sender_failed; attempted_action=read_failed; reason=runtime_generation_mismatch; runtime_generation=1; current_runtime_generation=3; terminal_generation=0; current_terminal_generation=0; route=regular_nkn_v4_fast; current_route=post_tuna_fallback_v6; protocol_version=4; current_protocol_version=6; live_route_epoch=0; current_live_route_epoch=2; runtime_family=regular_v4; current_runtime_family=post_tuna_fallback_v6", secondsOffset: 1),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=3; bridge_recovery_generation=1; transport_epoch=1; frame_type=filetransfer.frontier_request.v6; repair_request_id=v6-frontier:2709:1; request_correlation_id=frq-first; first_start_chunk_index=2709; first_chunk_count=1; range_count=1; peer_visible_any=1; accepted_any=1", secondsOffset: 2),
            LogLine($"event=filetransfer_outbound_sparse_worker_handoff_required; transfer_id={transferId}; {firstTuple}; {firstInstallDescriptor}; decision_reason=stale_worker_superseded", secondsOffset: 3),
            LogLine($"event=filetransfer_outbound_sparse_worker_handoff_installed; transfer_id={transferId}; {firstTuple}; {firstInstallDescriptor}; decision_reason=stale_worker_superseded", secondsOffset: 4),
            LogLine($"event=filetransfer_v6_frontier_request_receive_dispatched; transfer_id={transferId}; {firstTuple}; {firstInstallDescriptor}; transport_epoch=1; bridge_recovery_generation=1; repair_request_id=v6-frontier:2709:1; request_correlation_id=frq-first; first_start_chunk_index=2709; first_chunk_count=1; range_count=1; decision_reason=queued_for_sender_repair", secondsOffset: 5),
            LogLine($"event=filetransfer_fallback_frontier_repair_progress_observed; direction=inbound; transfer_id={transferId}; session_id=sess_a; repair_request_key=v6-frontier:2709:1; reason=frontier_advanced; committed_frontier_before=2709; committed_frontier_after=2710; requested_chunk_count=1", secondsOffset: 6),
            LogLine($"event=filetransfer_runtime_started; role=sender; transfer_id={transferId}; {secondTuple}; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 7),
            LogLine($"event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=v4_sender_failed; attempted_action=read_failed; reason=runtime_generation_mismatch; runtime_generation=3; current_runtime_generation=5; terminal_generation=0; current_terminal_generation=0; route=post_tuna_fallback_v6; current_route=post_tuna_fallback_v6; protocol_version=6; current_protocol_version=6; live_route_epoch=2; current_live_route_epoch=4; runtime_family=post_tuna_fallback_v6; current_runtime_family=post_tuna_fallback_v6", secondsOffset: 8),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; leg_generation=5; bridge_recovery_generation=2; transport_epoch=2; frame_type=filetransfer.frontier_request.v6; repair_request_id=v6-frontier:2710:2; request_correlation_id=frq-second; first_start_chunk_index=2710; first_chunk_count=1; range_count=1; peer_visible_any=1; accepted_any=1", secondsOffset: 9),
            LogLine($"event=filetransfer_v6_frontier_request_transport_rejected; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.frontier_request.v6; priority=1; queue_capacity_bypassed=1; queued_frames=144; queued_bytes=147456; transport_epoch=2; repair_request_id=v6-frontier:2710:2; request_correlation_id=frq-second; decision_reason=priority_control_queue_capacity_exceeded", secondsOffset: 10)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("post_tuna_fallback_outbound_receive_worker_handoff_missing", verdict["recovery_failure_class"]);
        Assert.Equal("post_tuna_fallback_outbound_receive_worker_handoff_missing", verdict["first_proof_chain_failure_class"]);
        Assert.Equal("filetransfer_runtime_owner_stale_update_rejected", verdict["first_proof_chain_failure_event"]);
        Assert.Equal("9", verdict["first_proof_chain_failure_line_number"]);
        Assert.Equal("1", verdict["post_tuna_fallback_outbound_receive_worker_handoff_missing_count"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "OutboundReceiveWorkerHandoff")]
    public async Task AnalyzeRetained_CompactModePreservesMissingWorkerHandoffFirstEdge()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_compact_fallback_receive_worker_missing";
        const string currentTuple =
            "direction=outbound; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=5; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn";
        var lines = new[]
        {
            LogLine($"event=filetransfer_runtime_started; role=sender; transfer_id={transferId}; {currentTuple}; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 0),
            LogLine($"event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=v4_sender_failed; attempted_action=read_failed; reason=runtime_generation_mismatch; runtime_generation=1; current_runtime_generation=3; terminal_generation=0; current_terminal_generation=0; route=regular_nkn_v4_fast; current_route=post_tuna_fallback_v6; protocol_version=4; current_protocol_version=6; live_route_epoch=0; current_live_route_epoch=2; runtime_family=regular_v4; current_runtime_family=post_tuna_fallback_v6", secondsOffset: 1),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=3; bridge_recovery_generation=1; transport_epoch=1; frame_type=filetransfer.frontier_request.v6; repair_request_id=v6-frontier:2709:1; request_correlation_id=frq-compact-missing; first_start_chunk_index=2709; first_chunk_count=1; range_count=1; peer_visible_any=1; accepted_any=1", secondsOffset: 2),
            LogLine($"event=filetransfer_v6_frontier_request_transport_rejected; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.frontier_request.v6; priority=1; queue_capacity_bypassed=1; queued_frames=144; queued_bytes=147456; transport_epoch=1; repair_request_id=v6-frontier:2709:1; request_correlation_id=frq-compact-missing; decision_reason=priority_control_queue_capacity_exceeded", secondsOffset: 3)
        };

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: new Dictionary<string, string>
            {
                ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
            });

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("INCONCLUSIVE", verdict["verdict"]);
        Assert.Equal("incomplete_proof", verdict["acceptance_failure_class"]);
        Assert.Equal("post_tuna_fallback_outbound_receive_worker_handoff_missing", verdict["recovery_failure_class"]);
        Assert.Equal("post_tuna_fallback_outbound_receive_worker_handoff_missing", verdict["first_proof_chain_failure_class"]);
        Assert.Equal("filetransfer_runtime_owner_stale_update_rejected", verdict["first_proof_chain_failure_event"]);
        Assert.Equal("runtime_generation_mismatch", verdict["first_proof_chain_failure_reason"]);
        Assert.Equal("2", verdict["first_proof_chain_failure_line_number"]);
        Assert.Equal("1", verdict["post_tuna_fallback_outbound_receive_worker_handoff_missing_count"]);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("post_tuna_fallback_outbound_receive_worker_handoff_missing", route["first_proof_chain_failure_class"]);
        Assert.Equal("filetransfer_runtime_owner_stale_update_rejected", route["first_proof_chain_failure_event"]);
        var compactJson = await File.ReadAllTextAsync(Path.Combine(result.ArtifactDir, "filetransfer-retained-compact-summary.json"));
        using var compactDocument = System.Text.Json.JsonDocument.Parse(compactJson);
        Assert.Equal("incomplete_proof", compactDocument.RootElement.GetProperty("acceptance_failure_class").GetString());
        Assert.Equal(2L, compactDocument.RootElement.GetProperty("first_proof_chain_failure_sequence").GetInt64());
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "OutboundReceiveWorkerHandoff")]
    public async Task AnalyzeRetained_CompactModeExactInstallAndDispatchClearsMissingHandoff()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_compact_fallback_receive_worker_resolved";
        const string currentTuple =
            "direction=outbound; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=5; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn";
        var installDescriptor = OutboundSparseInstallDescriptor(transferId);
        var lines = new[]
        {
            LogLine($"event=filetransfer_runtime_started; role=sender; transfer_id={transferId}; {currentTuple}; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 0),
            LogLine($"event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=v4_sender_failed; attempted_action=read_failed; reason=runtime_generation_mismatch; runtime_generation=1; current_runtime_generation=3; terminal_generation=0; current_terminal_generation=0; route=regular_nkn_v4_fast; current_route=post_tuna_fallback_v6; protocol_version=4; current_protocol_version=6; live_route_epoch=0; current_live_route_epoch=2; runtime_family=regular_v4; current_runtime_family=post_tuna_fallback_v6", secondsOffset: 1),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=3; bridge_recovery_generation=1; transport_epoch=1; frame_type=filetransfer.frontier_request.v6; repair_request_id=v6-frontier:2709:1; request_correlation_id=frq-compact-resolved; first_start_chunk_index=2709; first_chunk_count=1; range_count=1; peer_visible_any=1; accepted_any=1", secondsOffset: 2),
            LogLine($"event=filetransfer_outbound_sparse_worker_handoff_required; transfer_id={transferId}; {currentTuple}; {installDescriptor}; decision_reason=stale_worker_superseded", secondsOffset: 3),
            LogLine($"event=filetransfer_outbound_sparse_worker_handoff_installed; transfer_id={transferId}; {currentTuple}; {installDescriptor}; decision_reason=stale_worker_superseded", secondsOffset: 4),
            LogLine($"event=filetransfer_v6_frontier_request_receive_dispatched; transfer_id={transferId}; {currentTuple}; {installDescriptor}; transport_epoch=1; bridge_recovery_generation=1; repair_request_id=v6-frontier:2709:1; request_correlation_id=frq-compact-resolved; first_start_chunk_index=2709; first_chunk_count=1; range_count=1; decision_reason=queued_for_sender_repair", secondsOffset: 5)
        };

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: new Dictionary<string, string>
            {
                ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
            });

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("post_tuna_fallback_outbound_receive_worker_handoff_missing", verdict["recovery_failure_class"]);
        Assert.Equal("(none)", verdict["first_proof_chain_failure_class"]);
        Assert.Equal("0", verdict["post_tuna_fallback_outbound_receive_worker_handoff_missing_count"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "OutboundReceiveWorkerHandoff")]
    public async Task AnalyzeRetained_NonterminalSupervisorStopInvalidatesConsumerUntilFreshInstallAndDispatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_receive_supervisor_stopped";
        const string tuple =
            "direction=outbound; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=5; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn";
        var descriptor2 = OutboundSparseInstallDescriptor(transferId);
        var stoppedLines = new[]
        {
            LogLine($"event=filetransfer_runtime_started; role=sender; transfer_id={transferId}; {tuple}; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 0),
            LogLine($"event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=v4_sender_failed; attempted_action=read_failed; reason=runtime_generation_mismatch; runtime_generation=1; current_runtime_generation=3; terminal_generation=0; current_terminal_generation=0; route=regular_nkn_v4_fast; current_route=post_tuna_fallback_v6; protocol_version=4; current_protocol_version=6; live_route_epoch=0; current_live_route_epoch=2", secondsOffset: 1),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=3; frame_type=filetransfer.frontier_request.v6; repair_request_id=v6-frontier:1; request_correlation_id=frq-stopped; peer_visible_any=1; accepted_any=1", secondsOffset: 2),
            LogLine($"event=filetransfer_outbound_sparse_worker_handoff_required; transfer_id={transferId}; {tuple}; {descriptor2}; decision_reason=stale_worker_superseded", secondsOffset: 3),
            LogLine($"event=filetransfer_outbound_sparse_worker_handoff_installed; transfer_id={transferId}; {tuple}; {descriptor2}; decision_reason=stale_worker_superseded", secondsOffset: 4),
            LogLine($"event=filetransfer_v6_frontier_request_receive_dispatched; transfer_id={transferId}; {tuple}; {descriptor2}; repair_request_id=v6-frontier:1; request_correlation_id=frq-first; decision_reason=queued_for_sender_repair", secondsOffset: 5),
            LogLine($"event=filetransfer_outbound_sparse_receive_supervisor_stopped; transfer_id={transferId}; {tuple}; {descriptor2}; receive_supervisor_generation=1; terminal=0; decision_reason=read_failed", secondsOffset: 6),
            LogLine($"event=filetransfer_v6_frontier_request_transport_queued; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.frontier_request.v6; decision_reason=queued_for_core", secondsOffset: 7)
        };

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    stoppedLines,
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(stoppedLines);
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("post_tuna_fallback_outbound_receive_worker_handoff_missing", verdict["recovery_failure_class"]);
            Assert.Equal("1", verdict["post_tuna_fallback_outbound_receive_worker_handoff_missing_count"]);
        }

        var descriptor3 = OutboundSparseInstallDescriptor(
            transferId,
            installGeneration: 3,
            predecessorInstallGeneration: 2,
            installationReason: "repair_after_supervisor_restart");
        var recoveredLines = stoppedLines.Concat(new[]
        {
            LogLine($"event=filetransfer_outbound_sparse_worker_handoff_required; transfer_id={transferId}; {tuple}; {descriptor3}; decision_reason=repair_after_supervisor_restart", secondsOffset: 8),
            LogLine($"event=filetransfer_outbound_sparse_worker_handoff_installed; transfer_id={transferId}; {tuple}; {descriptor3}; decision_reason=repair_after_supervisor_restart", secondsOffset: 9),
            LogLine($"event=filetransfer_v6_frontier_request_receive_dispatched; transfer_id={transferId}; {tuple}; {descriptor3}; repair_request_id=v6-frontier:2; request_correlation_id=frq-second; decision_reason=queued_for_sender_repair", secondsOffset: 10)
        }).ToArray();

        var recovered = await RunAnalyzeFixtureAsync(recoveredLines);
        Assert.Equal(0, recovered.Script.ExitCode);
        var recoveredVerdict = ReadArtifactReport(recovered.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("post_tuna_fallback_outbound_receive_worker_handoff_missing", recoveredVerdict["recovery_failure_class"]);
        Assert.Equal("0", recoveredVerdict["post_tuna_fallback_outbound_receive_worker_handoff_missing_count"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "OutboundReceiveWorkerHandoff")]
    public async Task AnalyzeRetained_MismatchedOrPartialInstallDescriptorCannotResolveMissingHandoff()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_receive_descriptor_mismatch";
        const string tuple =
            "direction=outbound; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=5; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn";
        var descriptor = OutboundSparseInstallDescriptor(transferId);
        var prefix = new[]
        {
            LogLine($"event=filetransfer_runtime_started; role=sender; transfer_id={transferId}; {tuple}; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 0),
            LogLine($"event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=v4_sender_failed; attempted_action=read_failed; reason=runtime_generation_mismatch; runtime_generation=1; current_runtime_generation=3; terminal_generation=0; current_terminal_generation=0; route=regular_nkn_v4_fast; current_route=post_tuna_fallback_v6; protocol_version=4; current_protocol_version=6; live_route_epoch=0; current_live_route_epoch=2", secondsOffset: 1),
            LogLine($"event=filetransfer_control_plane_delivery_observed; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=3; frame_type=filetransfer.frontier_request.v6; repair_request_id=v6-frontier:1; request_correlation_id=frq-mismatch; peer_visible_any=1; accepted_any=1", secondsOffset: 2),
            LogLine($"event=filetransfer_outbound_sparse_worker_handoff_required; transfer_id={transferId}; {tuple}; {descriptor}", secondsOffset: 3)
        };
        var installed = LogLine($"event=filetransfer_outbound_sparse_worker_handoff_installed; transfer_id={transferId}; {tuple}; {descriptor}", secondsOffset: 4);
        var dispatch = LogLine($"event=filetransfer_v6_frontier_request_receive_dispatched; transfer_id={transferId}; {tuple}; {descriptor}; repair_request_id=v6-frontier:1; request_correlation_id=frq-mismatch; decision_reason=queued_for_sender_repair", secondsOffset: 5);
        var cases = new[]
        {
            prefix.Concat(new[]
            {
                installed.Replace("data_session_generation=2", "data_session_generation=3", StringComparison.Ordinal),
                dispatch
            }).ToArray(),
            prefix.Concat(new[]
            {
                installed.Replace("predecessor_install_generation=1; ", string.Empty, StringComparison.Ordinal),
                dispatch
            }).ToArray(),
            prefix.Concat(new[]
            {
                installed,
                dispatch.Replace("install_generation=2", "install_generation=9", StringComparison.Ordinal)
            }).ToArray(),
            prefix.Concat(new[]
            {
                installed.Replace("installation_reason=stale_worker_superseded; ", string.Empty, StringComparison.Ordinal),
                dispatch
            }).ToArray(),
            prefix.Concat(new[]
            {
                installed,
                dispatch.Replace("installation_reason=stale_worker_superseded", "installation_reason=wrong_install_reason", StringComparison.Ordinal)
            }).ToArray(),
            prefix.Concat(new[]
            {
                installed,
                dispatch.Replace("route_publication_owner_runtime_generation=3", "route_publication_owner_runtime_generation=9", StringComparison.Ordinal)
            }).ToArray(),
            prefix.Concat(new[]
            {
                installed,
                dispatch.Replace("descriptor_terminal_generation=0", "descriptor_terminal_generation=9", StringComparison.Ordinal)
            }).ToArray()
        };

        foreach (var lines in cases)
        {
            var result = await RunAnalyzeFixtureAsync(lines);
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("post_tuna_fallback_outbound_receive_worker_handoff_missing", verdict["recovery_failure_class"]);
            Assert.Equal("1", verdict["post_tuna_fallback_outbound_receive_worker_handoff_missing_count"]);
        }

        var compactResult = await RunAnalyzeFixtureAsync(
            cases[5],
            environment: new Dictionary<string, string>
            {
                ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
            });
        Assert.Equal(0, compactResult.Script.ExitCode);
        var compactVerdict = ReadArtifactReport(compactResult.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("post_tuna_fallback_outbound_receive_worker_handoff_missing", compactVerdict["recovery_failure_class"]);
        Assert.Equal("1", compactVerdict["post_tuna_fallback_outbound_receive_worker_handoff_missing_count"]);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "OutboundReceiveWorkerHandoff")]
    public async Task AnalyzeRetained_PreInstallDispatchRequiresPostInstallConsumptionProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_receive_preinstall_dispatch";
        const string repairRequestId = "v6-frontier:pending:1";
        const string tuple =
            "direction=outbound; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=5; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn";
        var descriptor = OutboundSparseInstallDescriptor(transferId);
        var publication = LogLine($"event=filetransfer_runtime_started; role=sender; transfer_id={transferId}; {tuple}; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 0);
        var stale = LogLine($"event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=v4_sender_failed; attempted_action=read_failed; reason=runtime_generation_mismatch; runtime_generation=1; current_runtime_generation=3; terminal_generation=0; current_terminal_generation=0; route=regular_nkn_v4_fast; current_route=post_tuna_fallback_v6; protocol_version=4; current_protocol_version=6; live_route_epoch=0; current_live_route_epoch=2", secondsOffset: 1);
        var delivery = LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=3; frame_type=filetransfer.frontier_request.v6; repair_request_id={repairRequestId}; request_correlation_id=frq-pending; peer_visible_any=1; accepted_any=1", secondsOffset: 2);
        var required = LogLine($"event=filetransfer_outbound_sparse_worker_handoff_required; transfer_id={transferId}; {tuple}; {descriptor}; decision_reason=stale_worker_superseded", secondsOffset: 3);
        var preInstallDispatch = LogLine($"event=filetransfer_v6_frontier_request_receive_dispatched; transfer_id={transferId}; {tuple}; {descriptor}; repair_request_id={repairRequestId}; request_correlation_id=frq-pending; decision_reason=queued_for_sender_repair", secondsOffset: 4);
        var installed = LogLine($"event=filetransfer_outbound_sparse_worker_handoff_installed; transfer_id={transferId}; {tuple}; {descriptor}; decision_reason=stale_worker_superseded", secondsOffset: 5);
        var progress = LogLine($"event=filetransfer_fallback_frontier_repair_progress_observed; direction=inbound; transfer_id={transferId}; session_id=sess_a; repair_request_key={repairRequestId}; reason=frontier_advanced; committed_frontier_before=2709; committed_frontier_after=2710; requested_chunk_count=1", secondsOffset: 6);

        async Task AssertHandoffVerdictAsync(string[] lines, bool expectedMissing, bool compact)
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    lines,
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(lines);
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal(
                expectedMissing ? "1" : "0",
                verdict["post_tuna_fallback_outbound_receive_worker_handoff_missing_count"]);
            if (expectedMissing)
            {
                Assert.Equal("post_tuna_fallback_outbound_receive_worker_handoff_missing", verdict["recovery_failure_class"]);
            }
            else
            {
                Assert.NotEqual("post_tuna_fallback_outbound_receive_worker_handoff_missing", verdict["recovery_failure_class"]);
            }
        }

        var preInstallDispatchOnly = new[]
        {
            publication, stale, delivery, required, preInstallDispatch, installed
        };
        var postInstallProgress = preInstallDispatchOnly.Append(progress).ToArray();
        foreach (var compact in new[] { false, true })
        {
            await AssertHandoffVerdictAsync(preInstallDispatchOnly, expectedMissing: true, compact);
            await AssertHandoffVerdictAsync(postInstallProgress, expectedMissing: false, compact);
        }

        var progressBeforeInstall = new[]
        {
            publication,
            stale,
            delivery,
            required,
            preInstallDispatch,
            progress,
            installed
        };
        await AssertHandoffVerdictAsync(progressBeforeInstall, expectedMissing: true, compact: false);
        await AssertHandoffVerdictAsync(
            postInstallProgress[..^1].Append(
                progress.Replace($"repair_request_key={repairRequestId}", "repair_request_key=v6-frontier:unrelated:1", StringComparison.Ordinal)).ToArray(),
            expectedMissing: true,
            compact: false);
        await AssertHandoffVerdictAsync(
            postInstallProgress[..^1].Append(
                progress.Replace("committed_frontier_after=2710", "committed_frontier_after=2709", StringComparison.Ordinal)).ToArray(),
            expectedMissing: true,
            compact: false);
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "OutboundReceiveWorkerHandoff")]
    public async Task AnalyzeRetained_PartialPublicationCannotManufactureWorkerHandoffAuthority()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_receive_partial_publication";
        var lines = new[]
        {
            LogLine($"event=filetransfer_runtime_started; role=sender; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=5; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted; fixture_skip_publication_authority_fields=1", secondsOffset: 0),
            LogLine($"event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=v4_sender_failed; attempted_action=read_failed; reason=runtime_generation_mismatch; runtime_generation=1; current_runtime_generation=3; terminal_generation=0; current_terminal_generation=0; route=regular_nkn_v4_fast; current_route=post_tuna_fallback_v6; protocol_version=4; current_protocol_version=6; live_route_epoch=0; current_live_route_epoch=2", secondsOffset: 1),
            LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=3; frame_type=filetransfer.frontier_request.v6; repair_request_id=v6-frontier:1; request_correlation_id=frq-partial-publication; peer_visible_any=1; accepted_any=1", secondsOffset: 2)
        };

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    lines,
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(lines);
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.NotEqual("post_tuna_fallback_outbound_receive_worker_handoff_missing", verdict["recovery_failure_class"]);
            Assert.Equal("0", verdict["post_tuna_fallback_outbound_receive_worker_handoff_missing_count"]);
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "OutboundReceiveWorkerHandoff")]
    public async Task AnalyzeRetained_WrongFrontierTupleAndUncorrelatedQueueCannotManufactureMissingHandoff()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        static string Publication(string transferId, string sessionId) =>
            $"event=filetransfer_runtime_started; role=sender; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; route_publication_generation=5; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted";
        static string StaleFailure(string transferId, string sessionId) =>
            $"event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; source=v4_sender_failed; attempted_action=read_failed; reason=runtime_generation_mismatch; runtime_generation=1; current_runtime_generation=3; terminal_generation=0; current_terminal_generation=0; route=regular_nkn_v4_fast; current_route=post_tuna_fallback_v6; protocol_version=4; current_protocol_version=6; live_route_epoch=0; current_live_route_epoch=2";
        static string Delivery(string transferId, string sessionId, int liveRouteEpoch, int legGeneration) =>
            $"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id={sessionId}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch={liveRouteEpoch}; leg_generation={legGeneration}; frame_type=filetransfer.frontier_request.v6; repair_request_id=v6-frontier:2709:1; request_correlation_id=frq-{transferId}; peer_visible_any=1; accepted_any=1";

        const string epochTransfer = "transfer_worker_handoff_wrong_epoch";
        const string legTransfer = "transfer_worker_handoff_wrong_leg";
        const string queueTransfer = "transfer_worker_handoff_uncorrelated_queue";
        var lines = new[]
        {
            LogLine(Publication(epochTransfer, "sess_epoch"), secondsOffset: 0),
            LogLine(StaleFailure(epochTransfer, "sess_epoch"), secondsOffset: 1),
            LogLine(Delivery(epochTransfer, "sess_epoch", liveRouteEpoch: 9, legGeneration: 3), secondsOffset: 2),
            LogLine(Publication(legTransfer, "sess_leg"), secondsOffset: 3),
            LogLine(StaleFailure(legTransfer, "sess_leg"), secondsOffset: 4),
            LogLine(Delivery(legTransfer, "sess_leg", liveRouteEpoch: 2, legGeneration: 9), secondsOffset: 5),
            LogLine(Publication(queueTransfer, "sess_queue"), secondsOffset: 6),
            LogLine(StaleFailure(queueTransfer, "sess_queue"), secondsOffset: 7),
            LogLine($"event=filetransfer_v6_frontier_request_transport_rejected; transfer_id={queueTransfer}; session_id=sess_queue; frame_type=filetransfer.frontier_request.v6; repair_request_id=v6-frontier:2709:99; request_correlation_id=frq-unbound-queue; decision_reason=priority_control_queue_capacity_exceeded", secondsOffset: 8),
        };

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    lines,
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(lines);
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.NotEqual(
                "post_tuna_fallback_outbound_receive_worker_handoff_missing",
                verdict["recovery_failure_class"]);
            Assert.Equal(
                "0",
                verdict["post_tuna_fallback_outbound_receive_worker_handoff_missing_count"]);
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailBridgeRecoveryPairing")]
    public async Task AnalyzeRetained_CurrentArtifactBridgeRecoveryMismatchIsDeduplicatedAndPrecedesDownstreamSymptoms()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildFallbackTailBridgeRecoveryMismatchFixture(includeDownstreamSymptoms: true);
        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    lines,
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(lines);

            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal(
                "post_tuna_fallback_paired_yield_bridge_recovery_generation_mismatch",
                verdict["recovery_failure_class"]);
            Assert.Equal(
                "post_tuna_fallback_paired_yield_bridge_recovery_generation_mismatch",
                verdict["first_proof_chain_failure_class"]);
            Assert.Equal(
                "filetransfer_fallback_tail_owner_generation_mismatch",
                verdict["first_proof_chain_failure_event"]);
            Assert.Equal("bridge_recovery_generation_mismatch", verdict["first_proof_chain_failure_reason"]);
            Assert.Equal("6", verdict["first_proof_chain_failure_sequence"]);
            Assert.Equal("6", verdict["first_proof_chain_failure_line_number"]);
            Assert.Equal("fail", verdict["fallback_tail_bridge_recovery_generation_pairing_verdict"]);
            Assert.Equal("4", verdict["fallback_tail_bridge_recovery_generation_mismatch_raw_count"]);
            Assert.Equal("1", verdict["fallback_tail_bridge_recovery_generation_mismatch_distinct_count"]);
            Assert.Equal("0", verdict["fallback_tail_bridge_recovery_generation_mismatch_recovered_count"]);
            Assert.Equal("1", verdict["fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);

            var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
            Assert.Equal(
                "post_tuna_fallback_paired_yield_bridge_recovery_generation_mismatch",
                route["first_proof_chain_failure_class"]);
            Assert.Equal("6", route["first_proof_chain_failure_line_number"]);
            Assert.Equal("0", route["fallback_tail_owner_generation_mismatch_count"]);
            Assert.NotEqual("fallback_frontier_request_dispatch_missing", verdict["recovery_failure_class"]);
            Assert.NotEqual("fallback_frontier_repair_stalled", verdict["recovery_failure_class"]);

            if (compact)
            {
                Assert.Equal("incomplete_proof", verdict["acceptance_failure_class"]);
                var compactJson = await File.ReadAllTextAsync(
                    Path.Combine(result.ArtifactDir, "filetransfer-retained-compact-summary.json"));
                using var compactDocument = System.Text.Json.JsonDocument.Parse(compactJson);
                Assert.Equal(
                    "post_tuna_fallback_paired_yield_bridge_recovery_generation_mismatch",
                    compactDocument.RootElement.GetProperty("first_proof_chain_failure_class").GetString());
                Assert.Equal(
                    1,
                    compactDocument.RootElement
                        .GetProperty("fallback_tail_bridge_recovery_generation_mismatch_unresolved_count")
                        .GetInt32());
            }
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailTransportEpochReauthorization")]
    public async Task AnalyzeRetained_CrossEpochRetiredOwnerSuppressionIsDeduplicatedAndPrecedesPairMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_transport_reauthorization";
        const string authorityId = "fta2-fallback_tail_yield-0123456789abcdef01234567";
        var oldOwner = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|3|1|1|(none)";
        var oldPair = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|1|(none)";
        var currentOwner = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|3|1|2|(none)";
        var currentPair = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|2|(none)";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; route_publication_generation=5; runtime_generation=3; terminal_generation=0; leg_generation=3; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 0),
            LogLine($"event=filetransfer_fallback_tail_recovery_owner_retired_after_yield; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=1; reason=fallback_tail_quiescent", secondsOffset: 1),
            LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=outbound; authority_schema=2; authority_id={authorityId}; predecessor_authority_id=(none); transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; transport_epoch=1; bridge_recovery_generation=1; checkpoint_request_id=(none); owner_key={oldOwner}; pair_key={oldPair}; yield_generation=1; committed_chunk=1353; highest_observed_chunk=1352; reason=receiver_state_sparse_runtime", secondsOffset: 2),
            LogLine($"event=filetransfer_fallback_leg_transport_epoch_adopted; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; previous_transport_epoch=1; transport_epoch=2; bridge_recovery_generation=1; retired_checkpoint_request_id=(none); current_checkpoint_request_id=(none); state=active; can_send_data=1; reason=transport_recovered_unproven", secondsOffset: 3),
            LogLine($"event=filetransfer_fallback_tail_retired_owner_update_suppressed; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=2; current_transport_epoch=2; retired_transport_epoch=1; bridge_recovery_generation=1; checkpoint_request_id=(none); source=post_tuna_fallback_recovery_restart; attempted_action=start_post_tuna_recovery; owner_key={currentOwner}; current_owner_key={currentOwner}; retired_owner_key={oldOwner}; owner_key_relation=different; scope_relation=cross_transport_epoch; reason=post_yield_recovery_restart; decision_reason=retired_owner_cross_transport_epoch_suppression", secondsOffset: 4),
            LogLine($"event=filetransfer_fallback_tail_owner_generation_mismatch; authority_schema=2; authority_id={authorityId}; predecessor_authority_id=(none); direction=outbound; peer_direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none); peer_live_route_epoch=2; peer_leg_generation=3; peer_transport_epoch=1; peer_bridge_recovery_generation=1; peer_checkpoint_request_id=(none); expected_owner_key={currentOwner}; expected_pair_key={currentPair}; peer_owner_key={oldOwner}; peer_pair_key={oldPair}; yield_generation=1; mismatch_kind=pair_scope_mismatch; decision_reason=pair_scope_mismatch", secondsOffset: 5),
            LogLine($"event=filetransfer_fallback_tail_owner_generation_mismatch; authority_schema=2; authority_id={authorityId}; predecessor_authority_id=(none); direction=outbound; peer_direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none); peer_live_route_epoch=2; peer_leg_generation=3; peer_transport_epoch=1; peer_bridge_recovery_generation=1; peer_checkpoint_request_id=(none); expected_owner_key={currentOwner}; expected_pair_key={currentPair}; peer_owner_key={oldOwner}; peer_pair_key={oldPair}; yield_generation=1; mismatch_kind=pair_scope_mismatch; decision_reason=pair_scope_mismatch", secondsOffset: 6),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=route_cycle_missing; integrity_ok=0", secondsOffset: 7),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=route_cycle_missing; saved_path=(none); integrity_ok=0", secondsOffset: 7)
        };

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    lines,
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(lines);
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal(
                "post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing",
                verdict["recovery_failure_class"]);
            Assert.Equal(
                "post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing",
                verdict["first_proof_chain_failure_class"]);
            Assert.Equal(
                "filetransfer_fallback_tail_retired_owner_update_suppressed",
                verdict["first_proof_chain_failure_event"]);
            Assert.Equal(
                "retired_owner_cross_transport_epoch_suppression",
                verdict["first_proof_chain_failure_reason"]);
            Assert.Equal("5", verdict["first_proof_chain_failure_sequence"]);
            Assert.Equal("route_runtime", verdict["acceptance_failure_class"]);
            Assert.Equal("fail", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict"]);
            Assert.Equal("1", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count"]);
            Assert.Equal("1", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count"]);
            Assert.Equal("0", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count"]);
            Assert.Equal("1", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count"]);
            Assert.Equal("0", verdict["fallback_tail_owner_generation_mismatch_count"]);
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailTransportEpochReauthorization")]
    public async Task AnalyzeRetained_PeerAdoptedEpochWithoutStagedLineageAnchorsLocalYieldBlock()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    BuildFallbackTailPeerAdoptionReauthorizationFixture(stageBeforeBlock: false),
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(
                    BuildFallbackTailPeerAdoptionReauthorizationFixture(stageBeforeBlock: false));
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal(
                "post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing",
                verdict["recovery_failure_class"]);
            Assert.Equal(
                "post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing",
                verdict["first_proof_chain_failure_class"]);
            Assert.Equal(
                "filetransfer_fallback_tail_local_yield_blocked",
                verdict["first_proof_chain_failure_event"]);
            Assert.Equal(
                "local_yield_transport_epoch_reauthorization_missing",
                verdict["first_proof_chain_failure_reason"]);
            Assert.Equal("7", verdict["first_proof_chain_failure_sequence"]);
            Assert.Equal("route_runtime", verdict["acceptance_failure_class"]);
            Assert.Equal("route_runtime", verdict["semantic_failure_domain"]);
            Assert.Equal("fail", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict"]);
            Assert.Equal("1", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count"]);
            Assert.Equal("1", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count"]);
            Assert.Equal("0", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count"]);
            Assert.Equal("1", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count"]);
            Assert.Equal("0", verdict["fallback_tail_owner_generation_mismatch_count"]);
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailTransportEpochReauthorization")]
    public async Task AnalyzeRetained_PeerAdoptedEpochWithExactStagedLineageKeepsOwnerWorkHoldDiagnostic()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    BuildFallbackTailPeerAdoptionReauthorizationFixture(stageBeforeBlock: true),
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(
                    BuildFallbackTailPeerAdoptionReauthorizationFixture(stageBeforeBlock: true));
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("none", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict"]);
            Assert.Equal("0", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count"]);
            Assert.Equal("0", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count"]);
            Assert.Equal("0", verdict["fallback_tail_owner_generation_mismatch_count"]);
            Assert.NotEqual(
                "post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing",
                verdict["first_proof_chain_failure_class"]);
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailTransportEpochReauthorization")]
    public async Task AnalyzeRetained_PeerAdoptedEpochLaterExactSuccessorRecoversDeduplicatedBoundary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    BuildFallbackTailPeerAdoptionReauthorizationFixture(
                        stageBeforeBlock: false,
                        stageAfterBlock: true,
                        duplicateBlock: true),
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(
                    BuildFallbackTailPeerAdoptionReauthorizationFixture(
                        stageBeforeBlock: false,
                        stageAfterBlock: true,
                        duplicateBlock: true));
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("pass", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict"]);
            Assert.Equal("2", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count"]);
            Assert.Equal("1", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count"]);
            Assert.Equal("1", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count"]);
            Assert.Equal("0", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count"]);
            Assert.Equal("0", verdict["fallback_tail_owner_generation_mismatch_count"]);
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailTransportEpochReauthorization")]
    public async Task AnalyzeRetained_PeerAdoptedEpochRequiresExactScopeKeysAndEligibleHold()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var mutations = new Func<string, string>[]
        {
            line => line.Contains("event=filetransfer_fallback_tail_local_yield_blocked;", StringComparison.Ordinal)
                ? line.Replace("direction=outbound", "direction=inbound", StringComparison.Ordinal)
                : line,
            line => line.Contains("event=filetransfer_fallback_tail_local_yield_blocked;", StringComparison.Ordinal)
                ? line.Replace("peer_pair_key=sess_a|transfer_fallback_peer_adoption_reauthorization|post_tuna_fallback_v6|6|2|2|(none)", "peer_pair_key=wrong", StringComparison.Ordinal)
                : line,
            line => line.Contains("event=filetransfer_fallback_leg_transport_epoch_adopted;", StringComparison.Ordinal)
                ? line.Replace("previous_transport_epoch=1", "previous_transport_epoch=2", StringComparison.Ordinal)
                : line,
            line => line.Contains("event=filetransfer_fallback_tail_local_yield_blocked;", StringComparison.Ordinal)
                ? line.Replace("decision_reason=fallback_tail_local_owner_work_pending", "decision_reason=fallback_proof_replay_pending", StringComparison.Ordinal)
                : line,
            line => line.Contains("event=filetransfer_fallback_tail_local_yield_blocked;", StringComparison.Ordinal)
                ? line.Replace("transfer_id=transfer_fallback_peer_adoption_reauthorization", "transfer_id=other_transfer", StringComparison.Ordinal)
                : line
        };

        foreach (var mutation in mutations)
        {
            var lines = BuildFallbackTailPeerAdoptionReauthorizationFixture(stageBeforeBlock: false)
                .Select(mutation)
                .ToArray();
            foreach (var compact in new[] { false, true })
            {
                var result = compact
                    ? await RunAnalyzeFixtureAsync(
                        lines,
                        environment: new Dictionary<string, string>
                        {
                            ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                        })
                    : await RunAnalyzeFixtureAsync(lines);
                Assert.Equal(0, result.Script.ExitCode);
                var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
                Assert.Equal("none", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict"]);
                Assert.Equal("0", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count"]);
                Assert.Equal("0", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count"]);
            }
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailTransportEpochReauthorization")]
    public async Task AnalyzeRetained_PeerAdoptedEpochDoesNotHideUnrelatedOwnerMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_peer_adoption_reauthorization";
        const string unrelatedAuthorityId = "fta2-fallback_tail_yield-deadbeefdeadbeefdeadbeef";
        var currentOwner = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|3|1|2|(none)";
        var currentPair = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|2|(none)";
        var unrelatedOwner = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|3|1|1|checkpoint-other";
        var unrelatedPair = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|1|checkpoint-other";
        var lines = BuildFallbackTailPeerAdoptionReauthorizationFixture(stageBeforeBlock: false).ToList();
        var terminalIndex = lines.FindIndex(line => line.Contains(
            "event=file_transfer_outbound_terminal;",
            StringComparison.Ordinal));
        Assert.True(terminalIndex >= 0);
        lines.Insert(
            terminalIndex,
            LogLine($"event=filetransfer_fallback_tail_owner_generation_mismatch; authority_schema=2; authority_id={unrelatedAuthorityId}; predecessor_authority_id=(none); direction=outbound; peer_direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none); peer_live_route_epoch=2; peer_leg_generation=3; peer_transport_epoch=1; peer_bridge_recovery_generation=1; peer_checkpoint_request_id=checkpoint-other; expected_owner_key={currentOwner}; expected_pair_key={currentPair}; peer_owner_key={unrelatedOwner}; peer_pair_key={unrelatedPair}; yield_generation=1; mismatch_kind=pair_scope_mismatch; decision_reason=pair_scope_mismatch", secondsOffset: 6));

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    lines,
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(lines);
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal(
                "post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing",
                verdict["first_proof_chain_failure_class"]);
            Assert.Equal("7", verdict["first_proof_chain_failure_sequence"]);
            Assert.Equal("1", verdict["fallback_tail_owner_generation_mismatch_count"]);
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailTransportEpochReauthorization")]
    public async Task AnalyzeRetained_TransportEpochReauthorizationDoesNotHideUnrelatedStalePeerMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_transport_reauthorization";
        const string unrelatedAuthorityId = "fta2-fallback_tail_yield-deadbeefdeadbeefdeadbeef";
        var currentOwner = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|3|1|2|(none)";
        var currentPair = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|2|(none)";
        var unrelatedPeerOwner = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|3|1|1|checkpoint-other";
        var unrelatedPeerPair = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|1|checkpoint-other";
        var lines = BuildFallbackTailTransportEpochReauthorizationFixture("none").ToList();
        var terminalIndex = lines.FindIndex(line => line.Contains(
            "event=file_transfer_outbound_terminal;",
            StringComparison.Ordinal));
        Assert.True(terminalIndex >= 0);
        lines.Insert(
            terminalIndex,
            LogLine($"event=filetransfer_fallback_tail_owner_generation_mismatch; authority_schema=2; authority_id={unrelatedAuthorityId}; predecessor_authority_id=(none); direction=outbound; peer_direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none); peer_live_route_epoch=2; peer_leg_generation=3; peer_transport_epoch=1; peer_bridge_recovery_generation=1; peer_checkpoint_request_id=checkpoint-other; expected_owner_key={currentOwner}; expected_pair_key={currentPair}; peer_owner_key={unrelatedPeerOwner}; peer_pair_key={unrelatedPeerPair}; yield_generation=1; mismatch_kind=pair_scope_mismatch; decision_reason=pair_scope_mismatch", secondsOffset: 5));

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    lines,
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(lines);
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal(
                "post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing",
                verdict["first_proof_chain_failure_class"]);
            Assert.Equal("1", verdict["fallback_tail_owner_generation_mismatch_count"]);
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailTransportEpochReauthorization")]
    public async Task AnalyzeRetained_TransportEpochReauthorizationRecoveryUsesExplicitTargetScopeAndExactLineage()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var recoveryKind in new[] { "paired", "terminal_exact", "terminal_mismatch" })
        {
            foreach (var compact in new[] { false, true })
            {
                var result = compact
                    ? await RunAnalyzeFixtureAsync(
                        BuildFallbackTailTransportEpochReauthorizationFixture(recoveryKind, duplicateSuppression: true),
                        environment: new Dictionary<string, string>
                        {
                            ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                        })
                    : await RunAnalyzeFixtureAsync(
                        BuildFallbackTailTransportEpochReauthorizationFixture(recoveryKind, duplicateSuppression: true));
                Assert.Equal(0, result.Script.ExitCode);
                var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
                Assert.Equal("2", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count"]);
                Assert.Equal("1", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count"]);
                if (recoveryKind == "terminal_mismatch")
                {
                    Assert.Equal("fail", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict"]);
                    Assert.Equal("0", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count"]);
                    Assert.Equal("1", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count"]);
                    Assert.Equal(
                        "post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing",
                        verdict["first_proof_chain_failure_class"]);
                }
                else
                {
                    Assert.Equal("pass", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict"]);
                    Assert.Equal("1", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count"]);
                    Assert.Equal("0", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count"]);
                    Assert.NotEqual(
                        "post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing",
                        verdict["first_proof_chain_failure_class"]);
                }
            }
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailTransportEpochReauthorization")]
    public async Task AnalyzeRetained_SameOwnerSuppressionIsDiagnosticOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    BuildFallbackTailTransportEpochReauthorizationFixture("none", sameOwnerSuppression: true),
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(
                    BuildFallbackTailTransportEpochReauthorizationFixture("none", sameOwnerSuppression: true));
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("none", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict"]);
            Assert.Equal("0", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count"]);
            Assert.Equal("0", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count"]);
            Assert.Equal("0", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count"]);
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailTransportEpochReauthorization")]
    public async Task AnalyzeRetained_LegacyCrossEpochSuppressionRemainsClassifiable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    BuildFallbackTailTransportEpochReauthorizationFixture("none", legacySuppression: true),
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(
                    BuildFallbackTailTransportEpochReauthorizationFixture("none", legacySuppression: true));
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("fail", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict"]);
            Assert.Equal("1", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count"]);
            Assert.Equal("1", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count"]);
            Assert.Equal("1", verdict["post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count"]);
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailBridgeRecoveryPairing")]
    public async Task AnalyzeRetained_CurrentTupleUsesMaximumPublicationAndCoreMutationOrdering()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_bridge_pairing";
        var lines = BuildFallbackTailBridgeRecoveryMismatchFixture(includeDownstreamSymptoms: true).ToList();
        var firstMismatch = lines.FindIndex(line => line.Contains(
            "event=filetransfer_fallback_tail_owner_generation_mismatch;",
            StringComparison.Ordinal));
        lines.InsertRange(
            firstMismatch,
            new[]
            {
                LogLine($"event=filetransfer_leg_frozen; direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:2; leg_generation=2; route=file_tuna_v4; protocol_version=4; live_route_epoch=1; transport_epoch=0; bridge_recovery_generation=0; reason=older_leg_replaced", secondsOffset: 4),
                LogLine($"event=filetransfer_fallback_tail_recovery_owner_retired_after_yield; direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=2; reason=cleanup_only", secondsOffset: 4),
                LogLine($"event=filetransfer_fallback_leg_authority_completed; direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=2; bridge_recovery_generation=9; checkpoint_request_id=(none); authority_reason=late_infra_evidence; proof=receiver_state_control_plane", secondsOffset: 4),
                LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; route_publication_generation=3; runtime_generation=2; terminal_generation=0; leg_generation=2; route_commit_generation=0; target_route=regular_nkn_v4_fast; target_protocol_version=4; target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 4),
                LogLine($"event=filetransfer_runtime_started; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; route_publication_generation=4; runtime_generation=3; terminal_generation=0; leg_generation=3", secondsOffset: 4)
            });

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    lines.ToArray(),
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(lines.ToArray());
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("fail", verdict["fallback_tail_bridge_recovery_generation_pairing_verdict"]);
            Assert.Equal("4", verdict["fallback_tail_bridge_recovery_generation_mismatch_raw_count"]);
            Assert.Equal("1", verdict["fallback_tail_bridge_recovery_generation_mismatch_distinct_count"]);
            Assert.Equal("1", verdict["fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailBridgeRecoveryPairing")]
    public async Task AnalyzeRetained_LaterCompleteCrossGenerationPairClearsMismatchInFullAndCompactModes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildFallbackTailBridgeRecoveryMismatchFixture(includeDownstreamSymptoms: false).ToList();
        AppendCompleteCrossGenerationPair(lines);

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    lines.ToArray(),
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(lines.ToArray());

            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.NotEqual(
                "post_tuna_fallback_paired_yield_bridge_recovery_generation_mismatch",
                verdict["recovery_failure_class"]);
            Assert.Equal("pass", verdict["fallback_tail_bridge_recovery_generation_pairing_verdict"]);
            Assert.Equal("4", verdict["fallback_tail_bridge_recovery_generation_mismatch_raw_count"]);
            Assert.Equal("1", verdict["fallback_tail_bridge_recovery_generation_mismatch_distinct_count"]);
            Assert.Equal("1", verdict["fallback_tail_bridge_recovery_generation_mismatch_recovered_count"]);
            Assert.Equal("0", verdict["fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailBridgeRecoveryPairing")]
    public async Task AnalyzeRetained_ProductionShapedTerminalProofClearsMismatchInFullAndCompactModes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_bridge_pairing";
        var lines = BuildFallbackTailBridgeRecoveryMismatchFixture(includeDownstreamSymptoms: false)
            .Append(LogLine(
                $"event=filetransfer_fallback_tail_terminal_proof_consumed; direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=2; committed_chunk=12484; chunk_count=12484; reason=chunk_batch_committed",
                secondsOffset: 9))
            .Append(LogLine(
                $"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1",
                secondsOffset: 10))
            .Append(LogLine(
                $"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=x; integrity_ok=1",
                secondsOffset: 10))
            .ToArray();

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    lines,
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(lines);

            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.NotEqual(
                "post_tuna_fallback_paired_yield_bridge_recovery_generation_mismatch",
                verdict["recovery_failure_class"]);
            Assert.Equal("pass", verdict["fallback_tail_bridge_recovery_generation_pairing_verdict"]);
            Assert.Equal("4", verdict["fallback_tail_bridge_recovery_generation_mismatch_raw_count"]);
            Assert.Equal("1", verdict["fallback_tail_bridge_recovery_generation_mismatch_distinct_count"]);
            Assert.Equal("1", verdict["fallback_tail_bridge_recovery_generation_mismatch_recovered_count"]);
            Assert.Equal("0", verdict["fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
            Assert.Equal("pass", verdict["fallback_tail_paired_yield_verdict"]);
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailBridgeRecoveryPairing")]
    public async Task AnalyzeRetained_OnlyAuthorizedSecondTunaPublicationClearsBridgeMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var prefix = BuildFallbackTailBridgeRecoveryMismatchFixture(includeDownstreamSymptoms: false);
        const string transferId = "transfer_fallback_bridge_pairing";
        var unrelatedFallbackPublication = LogLine(
            $"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; route_publication_generation=6; runtime_generation=4; terminal_generation=0; leg_generation=4; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted",
            secondsOffset: 10);
        var secondTunaPublication = LogLine(
            $"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=file_tuna_v4; protocol_version=4; live_route_epoch=3; route_publication_generation=6; runtime_generation=4; terminal_generation=0; leg_generation=4; route_commit_generation=2; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; route_publication_action_kind=RouteCommitPublication; coordinator_decision_reason=accepted",
            secondsOffset: 10);
        var nonCommitTunaPublication = LogLine(
            $"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=file_tuna_v4; protocol_version=4; live_route_epoch=3; route_publication_generation=6; runtime_generation=4; terminal_generation=0; leg_generation=4; route_commit_generation=2; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted",
            secondsOffset: 10);

        foreach (var compact in new[] { false, true })
        {
            async Task<Dictionary<string, string>> AnalyzeAsync(string publication)
            {
                var lines = prefix
                    .Append(publication)
                    .Append(LogLine(
                        $"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1",
                        secondsOffset: 11))
                    .Append(LogLine(
                        $"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=x; integrity_ok=1",
                        secondsOffset: 11))
                    .ToArray();
                var result = compact
                    ? await RunAnalyzeFixtureAsync(
                        lines,
                        environment: new Dictionary<string, string>
                        {
                            ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                        })
                    : await RunAnalyzeFixtureAsync(lines);
                Assert.Equal(0, result.Script.ExitCode);
                return ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            }

            var unrelated = await AnalyzeAsync(unrelatedFallbackPublication);
            Assert.Equal("1", unrelated["fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
            Assert.Equal(
                "post_tuna_fallback_paired_yield_bridge_recovery_generation_mismatch",
                unrelated["recovery_failure_class"]);

            var nonCommitTuna = await AnalyzeAsync(nonCommitTunaPublication);
            Assert.Equal("1", nonCommitTuna["fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
            Assert.Equal(
                "post_tuna_fallback_paired_yield_bridge_recovery_generation_mismatch",
                nonCommitTuna["recovery_failure_class"]);

            var secondTuna = await AnalyzeAsync(secondTunaPublication);
            Assert.Equal("1", secondTuna["fallback_tail_bridge_recovery_generation_mismatch_recovered_count"]);
            Assert.Equal("0", secondTuna["fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
            Assert.NotEqual(
                "post_tuna_fallback_paired_yield_bridge_recovery_generation_mismatch",
                secondTuna["recovery_failure_class"]);
        }
    }

    [Theory]
    [InlineData("owner_key")]
    [InlineData("authority_id")]
    [InlineData("reason")]
    [InlineData("canonical_pair")]
    [InlineData("owner_derived_pair")]
    [InlineData("peer_direction")]
    [Trait("RetainedFallbackSuite", "FallbackTailBridgeRecoveryPairing")]
    public async Task AnalyzeRetained_UncorrelatedNewSchemaBridgeMismatchRemainsGenericCoverage(
        string corruption)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string peerAuthorityId = "fta2-fallback_tail_yield-0123456789abcdef01234567";
        const string peerPredecessorAuthorityId = "fta2-fallback_tail_yield-89abcdef0123456701234567";
        const string mismatchEvent = "event=filetransfer_fallback_tail_owner_generation_mismatch;";
        var lines = BuildFallbackTailBridgeRecoveryMismatchFixture(includeDownstreamSymptoms: true)
            .Select(line =>
            {
                if (!line.Contains(mismatchEvent, StringComparison.Ordinal))
                {
                    return line;
                }

                var current = line.Replace(
                    mismatchEvent,
                    $"{mismatchEvent} authority_schema=2; authority_id={peerAuthorityId}; predecessor_authority_id={peerPredecessorAuthorityId}; mismatch_kind=bridge_recovery_generation; decision_reason=bridge_recovery_generation_mismatch;",
                    StringComparison.Ordinal);
                return corruption switch
                {
                    "owner_key" => current.Replace(
                        "peer_owner_key=sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|3|2|2|(none)",
                        "peer_owner_key=sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|3|9|2|(none)",
                        StringComparison.Ordinal),
                    "authority_id" => current.Replace(
                        $"authority_id={peerAuthorityId}",
                        "authority_id=fta2-fallback_tail_yield-deadbeefdeadbeefdeadbeef",
                        StringComparison.Ordinal),
                    "reason" => current.Replace(
                        "mismatch_kind=bridge_recovery_generation; decision_reason=bridge_recovery_generation_mismatch",
                        "mismatch_kind=leg_generation; decision_reason=owner_generation_mismatch",
                        StringComparison.Ordinal),
                    "canonical_pair" => current
                        .Replace(
                            "expected_pair_key=sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|1|2|(none)",
                            "expected_pair_key=sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|2|(none)",
                            StringComparison.Ordinal)
                        .Replace(
                            "peer_pair_key=sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|2|2|(none)",
                            "peer_pair_key=sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|2|(none)",
                            StringComparison.Ordinal),
                    "owner_derived_pair" => current
                        .Replace(
                            "expected_pair_key=sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|1|2|(none)",
                            "expected_pair_key=sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|3|1|2|(none)",
                            StringComparison.Ordinal)
                        .Replace(
                            "peer_pair_key=sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|2|2|(none)",
                            "peer_pair_key=sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|3|2|2|(none)",
                            StringComparison.Ordinal),
                    "peer_direction" => current.Replace(
                        "direction=inbound; peer_direction=outbound",
                        "direction=inbound; peer_direction=inbound",
                        StringComparison.Ordinal),
                    _ => throw new InvalidOperationException(corruption)
                };
            })
            .ToArray();

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    lines,
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(lines);
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("4", verdict["fallback_tail_owner_generation_mismatch_count"]);
            Assert.Equal("0", verdict["fallback_tail_bridge_recovery_generation_mismatch_distinct_count"]);
            Assert.Equal("0", verdict["fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
            Assert.NotEqual(
                "post_tuna_fallback_paired_yield_bridge_recovery_generation_mismatch",
                verdict["recovery_failure_class"]);
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailBridgeRecoveryPairing")]
    public async Task AnalyzeRetained_DeduplicationKeepsDistinctMatchedPredecessorAuthorities()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string authorityId = "fta2-fallback_tail_yield-0123456789abcdef01234567";
        const string firstPredecessor = "fta2-fallback_tail_yield-89abcdef0123456701234567";
        const string secondPredecessor = "fta2-fallback_tail_yield-aaaaaaaaaaaaaaaaaaaaaaaa";
        const string mismatchMarker = "event=filetransfer_fallback_tail_owner_generation_mismatch;";
        var baseLines = BuildFallbackTailBridgeRecoveryMismatchFixture(includeDownstreamSymptoms: false).ToList();
        baseLines.Insert(
            5,
            LogLine(
                $"event=filetransfer_fallback_tail_yield_proof_sent; direction=outbound; authority_schema=2; authority_id={authorityId}; predecessor_authority_id={secondPredecessor}; transfer_id=transfer_fallback_bridge_pairing; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; transport_epoch=2; bridge_recovery_generation=2; checkpoint_request_id=(none); owner_key=sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|3|2|2|(none); pair_key=sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|2|2|(none); yield_generation=2; peer_visible_any=1; accepted_any=1; reason=fallback_tail_quiescent",
                secondsOffset: 4));
        var mismatchOrdinal = 0;
        var lines = baseLines.Select(line =>
        {
            if (!line.Contains(mismatchMarker, StringComparison.Ordinal))
            {
                return line;
            }

            var predecessor = mismatchOrdinal++ < 2 ? firstPredecessor : secondPredecessor;
            return line.Replace(
                mismatchMarker,
                $"{mismatchMarker} authority_schema=2; authority_id={authorityId}; predecessor_authority_id={predecessor}; mismatch_kind=bridge_recovery_generation; decision_reason=bridge_recovery_generation_mismatch;",
                StringComparison.Ordinal);
        }).ToArray();
        lines = lines
            .Append(LogLine("event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id=transfer_fallback_bridge_pairing; state=Completed; error_code=(none); integrity_ok=1", secondsOffset: 10))
            .Append(LogLine("event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id=transfer_fallback_bridge_pairing; state=Completed; error_code=(none); saved_path=x; integrity_ok=1", secondsOffset: 10))
            .ToArray();

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    lines,
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(lines);
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("4", verdict["fallback_tail_bridge_recovery_generation_mismatch_raw_count"]);
            Assert.Equal("2", verdict["fallback_tail_bridge_recovery_generation_mismatch_distinct_count"]);
            Assert.Equal("0", verdict["fallback_tail_bridge_recovery_generation_mismatch_recovered_count"]);
            Assert.Equal("2", verdict["fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
        }
    }

    [Theory]
    [InlineData("route_replaced")]
    [InlineData("authority_superseded")]
    [InlineData("leg_frozen")]
    [InlineData("transfer_terminal")]
    [Trait("RetainedFallbackSuite", "FallbackTailBridgeRecoveryPairing")]
    public async Task AnalyzeRetained_LateMismatchFromRetiredLifecycleIsDiagnosticOnly(string retirement)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_bridge_pairing";
        var retirementEvent = retirement switch
        {
            "route_replaced" => LogLine(
                $"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=file_tuna_v4; protocol_version=4; live_route_epoch=3; route_publication_generation=5; runtime_generation=4; terminal_generation=0; leg_generation=4; route_commit_generation=2; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; route_publication_action_kind=RouteCommitPublication; coordinator_decision_reason=accepted",
                secondsOffset: 4),
            "authority_superseded" => LogLine(
                $"event=filetransfer_fallback_leg_authority_superseded_by_route_hint; direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none); reason=route_hint_authority_replacement",
                secondsOffset: 4),
            "leg_frozen" => LogLine(
                $"event=filetransfer_leg_frozen; direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=2; bridge_recovery_generation=1; reason=route_replacement",
                secondsOffset: 4),
            "transfer_terminal" => LogLine(
                $"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=x; integrity_ok=1",
                secondsOffset: 4),
            _ => throw new InvalidOperationException(retirement)
        };
        var lines = BuildFallbackTailBridgeRecoveryMismatchFixture(includeDownstreamSymptoms: false).ToList();
        lines.Insert(5, retirementEvent);
        if (retirement is "authority_superseded" or "leg_frozen" or "transfer_terminal")
        {
            lines.Insert(
                6,
                LogLine(
                    $"event=filetransfer_fallback_leg_authority_started; direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none); authority_reason=late_same_leg_evidence",
                    secondsOffset: 4));
        }

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    lines.ToArray(),
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(lines.ToArray());
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("none", verdict["fallback_tail_bridge_recovery_generation_pairing_verdict"]);
            Assert.Equal("0", verdict["fallback_tail_bridge_recovery_generation_mismatch_distinct_count"]);
            Assert.Equal("0", verdict["fallback_tail_owner_generation_mismatch_count"]);
            Assert.NotEqual(
                "post_tuna_fallback_paired_yield_bridge_recovery_generation_mismatch",
                verdict["recovery_failure_class"]);
            Assert.NotEqual("fallback_tail_owner_generation_mismatch", verdict["recovery_failure_class"]);
        }
    }

    [Theory]
    [InlineData("peer_predecessor")]
    [InlineData("route_replaced")]
    [InlineData("transfer_terminal")]
    [Trait("RetainedFallbackSuite", "FallbackTailBridgeRecoveryPairing")]
    public async Task AnalyzeRetained_InvalidOrStaleGrantCannotRecoverBridgeMismatchInEitherMode(
        string invalidation)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildFallbackTailBridgeRecoveryMismatchFixture(includeDownstreamSymptoms: false).ToList();
        AppendCompleteCrossGenerationPair(
            lines,
            mismatchPeerPredecessor: invalidation == "peer_predecessor",
            barrierBeforeGrant: invalidation == "peer_predecessor" ? null : invalidation);
        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    lines.ToArray(),
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(lines.ToArray());
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("fail", verdict["fallback_tail_bridge_recovery_generation_pairing_verdict"]);
            Assert.Equal("0", verdict["fallback_tail_bridge_recovery_generation_mismatch_recovered_count"]);
            Assert.Equal("1", verdict["fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
            Assert.Equal("fail", verdict["fallback_tail_paired_yield_verdict"]);
        }
    }

    [Theory]
    [InlineData("peer_owner_key")]
    [InlineData("peer_pair_key_format")]
    [Trait("RetainedFallbackSuite", "FallbackTailBridgeRecoveryPairing")]
    public async Task AnalyzeRetained_PairedGrantPeerIdentityMutationCannotRecoverMismatch(
        string mutation)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string pairedGrant = "event=filetransfer_fallback_tail_paired_yield_granted;";
        var lines = BuildFallbackTailBridgeRecoveryMismatchFixture(includeDownstreamSymptoms: false).ToList();
        AppendCompleteCrossGenerationPair(lines);
        lines = lines.Select(line =>
        {
            if (!line.Contains(pairedGrant, StringComparison.Ordinal))
            {
                return line;
            }

            return mutation switch
            {
                "peer_owner_key" => line.Replace(
                    "peer_owner_key=sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|3|2|2|(none)",
                    "peer_owner_key=sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|3|9|2|(none)",
                    StringComparison.Ordinal),
                "peer_pair_key_format" => line.Replace(
                    "peer_pair_key_format=canonical_7",
                    "peer_pair_key_format=legacy_bridge_scoped_8",
                    StringComparison.Ordinal),
                _ => throw new InvalidOperationException(mutation)
            };
        }).ToList();

        foreach (var compact in new[] { false, true })
        {
            var result = compact
                ? await RunAnalyzeFixtureAsync(
                    lines.ToArray(),
                    environment: new Dictionary<string, string>
                    {
                        ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                    })
                : await RunAnalyzeFixtureAsync(lines.ToArray());
            Assert.Equal(0, result.Script.ExitCode);
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("fail", verdict["fallback_tail_bridge_recovery_generation_pairing_verdict"]);
            Assert.Equal("0", verdict["fallback_tail_bridge_recovery_generation_mismatch_recovered_count"]);
            Assert.Equal("1", verdict["fallback_tail_bridge_recovery_generation_mismatch_unresolved_count"]);
            Assert.Equal("fail", verdict["fallback_tail_paired_yield_verdict"]);
        }
    }

    [Fact]
    [Trait("RetainedFallbackSuite", "FallbackTailCoordinatorAggregate")]
    public async Task AnalyzeRetained_FallbackTailCoordinatorAggregate_PendingReceiptUsesExactLocalScope()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var repoRoot = FindRepoRoot();
        var lines = BuildFallbackTailCoordinatorAggregateFixture(peerFirst: true, includeConsumption: false)
            .Select(line => line + "; log_writer_id=writer-pending-scope");
        var fixture = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(lines)));
        var script = File.ReadAllText(Path.Combine(repoRoot, "tests", "nLink.SmokeTests.Core",
            "Support", "FallbackPendingReceiptScope.ps1"));
        foreach (var host in new[] { "powershell", "pwsh" })
        {
            var result = await RunPowerShellScriptTextAsync(script, [repoRoot, fixture], host);
            Assert.True(result.ExitCode == 0,
                $"{host}: STDOUT:{result.Stdout}{Environment.NewLine}STDERR:{result.Stderr}");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("RetainedFallbackSuite", "FallbackTailCoordinatorAggregate")]
    public async Task AnalyzeRetained_FallbackTailCoordinatorAggregate_PairsAndConsumesExactlyOnce(
        bool compact,
        bool peerFirst)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackTailCoordinatorAggregateFixture(
                peerFirst,
                duplicateEvidence: true,
                includeStaleRejectedReceipt: true),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal("pass", verdict["fallback_tail_coordinator_aggregate_verdict"]);
        Assert.Equal("1", verdict["fallback_tail_coordinator_yield_issued_count"]);
        Assert.Equal("3", verdict["fallback_tail_coordinator_local_proof_receipt_decided_count"]);
        Assert.Equal("2", verdict["fallback_tail_coordinator_peer_proof_receipt_decided_count"]);
        Assert.Equal("2", verdict["fallback_tail_coordinator_pairing_committed_count"]);
        Assert.Equal("1", verdict["fallback_tail_coordinator_valid_pairing_count"]);
        Assert.Equal("1", verdict["fallback_tail_coordinator_paired_release_reserved_count"]);
        Assert.Equal(
            "1",
            verdict["fallback_tail_coordinator_valid_paired_release_reserved_count"]);
        Assert.Equal("2", verdict["fallback_tail_coordinator_paired_release_consumed_count"]);
        Assert.Equal(
            "1",
            verdict["fallback_tail_coordinator_valid_paired_release_consumed_count"]);
        Assert.Equal("0", verdict["fallback_tail_coordinator_invalid_unresolved_count"]);
        Assert.Equal("pass", verdict["fallback_tail_paired_yield_verdict"]);

        if (compact)
        {
            using var compactDocument = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllTextAsync(
                    Path.Combine(
                        result.ArtifactDir,
                        "filetransfer-retained-compact-summary.json")));
            Assert.Equal(
                "pass",
                compactDocument.RootElement
                    .GetProperty("fallback_tail_coordinator_aggregate_verdict")
                    .GetString());
            Assert.Equal(
                1,
                compactDocument.RootElement
                    .GetProperty("fallback_tail_coordinator_valid_paired_release_consumed_count")
                    .GetInt32());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("RetainedFallbackSuite", "FallbackTailCoordinatorAggregate")]
    public async Task AnalyzeRetained_FallbackTailCoordinatorAggregate_AcceptsCarriedPredecessorPeerProof(
        bool compact,
        bool redactNestedAuthorities)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackTailCoordinatorCarriedPeerProofFixture(redactNestedAuthorities),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.True(
            verdict["fallback_tail_coordinator_aggregate_verdict"] == "pass",
            string.Join(
                Environment.NewLine,
                verdict.Where(pair =>
                    pair.Key.StartsWith("fallback_tail_coordinator_", StringComparison.Ordinal) ||
                    pair.Key is "recovery_failure_class" or "first_proof_chain_failure_reason")
                    .Select(pair => $"{pair.Key}={pair.Value}")));
        Assert.Equal("1", verdict["fallback_tail_coordinator_valid_pairing_count"]);
        Assert.Equal(
            "1",
            verdict["fallback_tail_coordinator_valid_paired_release_reserved_count"]);
        Assert.Equal(
            "1",
            verdict["fallback_tail_coordinator_valid_paired_release_consumed_count"]);
        Assert.Equal("0", verdict["fallback_tail_coordinator_invalid_raw_count"]);
        Assert.Equal("0", verdict["fallback_tail_coordinator_invalid_distinct_count"]);
        Assert.Equal("0", verdict["fallback_tail_coordinator_invalid_recovered_count"]);
        Assert.Equal("0", verdict["fallback_tail_coordinator_invalid_unresolved_count"]);
    }

    [Theory]
    [InlineData("carry_false")]
    [InlineData("visible_mismatch")]
    [InlineData("ambiguous_redacted_predecessor")]
    [Trait("RetainedFallbackSuite", "FallbackTailCoordinatorAggregate")]
    public async Task AnalyzeRetained_FallbackTailCoordinatorAggregate_CarriedPeerProofFailsClosed(
        string scenario)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var redact = scenario == "ambiguous_redacted_predecessor";
        var lines = BuildFallbackTailCoordinatorCarriedPeerProofFixture(redact).ToList();
        if (scenario == "carry_false")
        {
            for (var index = 0; index < lines.Count; index++)
            {
                lines[index] = lines[index].Replace(
                    "failed_supersession_carried_peer_proof=1",
                    "failed_supersession_carried_peer_proof=0",
                    StringComparison.Ordinal);
            }
        }
        else if (scenario == "visible_mismatch")
        {
            for (var index = 0; index < lines.Count; index++)
            {
                lines[index] = lines[index].Replace(
                    "failed_predecessor_authority_id=fta2-fallback-tail-yield-aaaaaaaaaaaaaaaaaaaaaaaa",
                    "failed_predecessor_authority_id=fta2-fallback-tail-yield-eeeeeeeeeeeeeeeeeeeeeeee",
                    StringComparison.Ordinal);
            }
        }
        else
        {
            var failedYieldIndex = lines.FindIndex(line =>
                line.Contains("event=filetransfer_fallback_tail_yield_issued", StringComparison.Ordinal) &&
                line.Contains("yield_kind=Failed", StringComparison.Ordinal));
            Assert.True(failedYieldIndex >= 0);
            lines.Insert(
                failedYieldIndex + 1,
                lines[failedYieldIndex].Replace(
                    "authority_id=fta2-fallback-tail-yield-aaaaaaaaaaaaaaaaaaaaaaaa",
                    "authority_id=fta2-fallback-tail-yield-eeeeeeeeeeeeeeeeeeeeeeee",
                    StringComparison.Ordinal));
        }

        var result = await RunAnalyzeFixtureAsync(lines.ToArray());

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal("fail", verdict["fallback_tail_coordinator_aggregate_verdict"]);
        Assert.Equal("3", verdict["fallback_tail_coordinator_invalid_raw_count"]);
        Assert.Equal("3", verdict["fallback_tail_coordinator_invalid_distinct_count"]);
        Assert.Equal("0", verdict["fallback_tail_coordinator_invalid_recovered_count"]);
        Assert.Equal("3", verdict["fallback_tail_coordinator_invalid_unresolved_count"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "FallbackTailCoordinatorAggregate")]
    public async Task AnalyzeRetained_FallbackTailCoordinatorAggregate_MutatedPairingUsesExistingFailureClass(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackTailCoordinatorAggregateFixture(
                peerFirst: false,
                mutatePairing: true,
                includeConsumption: false),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal("fail", verdict["fallback_tail_coordinator_aggregate_verdict"]);
        Assert.Equal("1", verdict["fallback_tail_coordinator_invalid_raw_count"]);
        Assert.Equal("1", verdict["fallback_tail_coordinator_invalid_distinct_count"]);
        Assert.Equal("0", verdict["fallback_tail_coordinator_invalid_recovered_count"]);
        Assert.Equal("1", verdict["fallback_tail_coordinator_invalid_unresolved_count"]);
        Assert.Equal(
            "fallback_tail_yield_authority_invalid",
            verdict["recovery_failure_class"]);
        Assert.Equal("route_runtime", verdict["acceptance_failure_class"]);
        Assert.Equal("route_runtime", verdict["semantic_failure_domain"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "FallbackTailCoordinatorAggregate")]
    public async Task AnalyzeRetained_FallbackTailCoordinatorAggregate_RequiresExactProofReceiptIdentity(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackTailCoordinatorAggregateFixture(
                peerFirst: false,
                mutatePairingProofIdentity: true,
                includeConsumption: false),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal("fail", verdict["fallback_tail_coordinator_aggregate_verdict"]);
        Assert.Equal("1", verdict["fallback_tail_coordinator_invalid_unresolved_count"]);
        Assert.Equal(
            "fallback_tail_yield_authority_invalid",
            verdict["recovery_failure_class"]);
        Assert.Equal("route_runtime", verdict["acceptance_failure_class"]);
        Assert.Equal("route_runtime", verdict["semantic_failure_domain"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "FallbackTailCoordinatorAggregate")]
    public async Task AnalyzeRetained_FallbackTailCoordinatorAggregate_DeduplicatesReservationBoundary(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackTailCoordinatorAggregateFixture(
                peerFirst: false,
                duplicateReservation: true),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal("pass", verdict["fallback_tail_coordinator_aggregate_verdict"]);
        Assert.Equal("2", verdict["fallback_tail_coordinator_paired_release_reserved_count"]);
        Assert.Equal(
            "1",
            verdict["fallback_tail_coordinator_valid_paired_release_reserved_count"]);
        Assert.Equal("0", verdict["fallback_tail_coordinator_invalid_unresolved_count"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("RetainedFallbackSuite", "FallbackTailCoordinatorAggregate")]
    public async Task AnalyzeRetained_FallbackTailCoordinatorAggregate_RejectsInvalidOrStageOnlyReservation(
        bool compact,
        bool stageOnly)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackTailCoordinatorAggregateFixture(
                peerFirst: false,
                mutateReservation: !stageOnly,
                phase2StageOnly: stageOnly),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal("fail", verdict["fallback_tail_coordinator_aggregate_verdict"]);
        Assert.NotEqual("0", verdict["fallback_tail_coordinator_invalid_unresolved_count"]);
        Assert.Equal(
            "fallback_tail_yield_authority_invalid",
            verdict["recovery_failure_class"]);
        Assert.Equal("route_runtime", verdict["acceptance_failure_class"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "FallbackTailCoordinatorAggregate")]
    public async Task AnalyzeRetained_FallbackTailCoordinatorAggregate_MutatedPeerTupleUsesExistingFailureClass(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackTailCoordinatorAggregateFixture(
                peerFirst: false,
                mutatePeerTuple: true,
                includeConsumption: false),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal("fail", verdict["fallback_tail_coordinator_aggregate_verdict"]);
        Assert.Equal("1", verdict["fallback_tail_coordinator_invalid_unresolved_count"]);
        Assert.Equal(
            "fallback_tail_yield_authority_invalid",
            verdict["recovery_failure_class"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("RetainedFallbackSuite", "FallbackTailCoordinatorAggregate")]
    public async Task AnalyzeRetained_FallbackTailCoordinatorAggregate_RebindSkipsIntermediateEpoch(
        bool compact,
        bool inventIntermediateSuccessor)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackTailCoordinatorEpochRebindFixture(inventIntermediateSuccessor),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal(
            "1",
            verdict["fallback_tail_coordinator_reauthorization_staged_count"]);
        Assert.Equal(
            "1",
            verdict["fallback_tail_coordinator_reauthorization_rebound_count"]);
        Assert.Equal(
            inventIntermediateSuccessor ? "2" : "1",
            verdict["fallback_tail_coordinator_reauthorization_successor_issued_count"]);
        Assert.Equal(
            inventIntermediateSuccessor ? "fail" : "pass",
            verdict["fallback_tail_coordinator_aggregate_verdict"]);
        Assert.Equal(
            inventIntermediateSuccessor ? "1" : "0",
            verdict["fallback_tail_coordinator_invalid_unresolved_count"]);
        if (inventIntermediateSuccessor)
        {
            Assert.Equal(
                "fallback_tail_yield_authority_invalid",
                verdict["recovery_failure_class"]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "FallbackTailCoordinatorAggregate")]
    public async Task AnalyzeRetained_FallbackTailCoordinatorAggregate_RejectsMutatedReauthorizationDescriptor(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackTailCoordinatorEpochRebindFixture(
                inventIntermediateSuccessor: false,
                mutateReboundDescriptor: true),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal("fail", verdict["fallback_tail_coordinator_aggregate_verdict"]);
        Assert.Equal("1", verdict["fallback_tail_coordinator_invalid_unresolved_count"]);
        Assert.Equal(
            "fallback_tail_yield_authority_invalid",
            verdict["recovery_failure_class"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("RetainedFallbackSuite", "FallbackTailCoordinatorAggregate")]
    public async Task AnalyzeRetained_FallbackTailCoordinatorAggregate_RequiresExactReauthorizationLineage(
        bool compact,
        bool mutateSuccessorTarget)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackTailCoordinatorEpochRebindFixture(
                inventIntermediateSuccessor: false,
                mutateSharedPredecessorLineage: !mutateSuccessorTarget,
                mutateSuccessorTargetTuple: mutateSuccessorTarget),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal("fail", verdict["fallback_tail_coordinator_aggregate_verdict"]);
        Assert.Equal("1", verdict["fallback_tail_coordinator_invalid_unresolved_count"]);
        Assert.Equal(
            "fallback_tail_yield_authority_invalid",
            verdict["recovery_failure_class"]);
        Assert.Equal("route_runtime", verdict["acceptance_failure_class"]);
        Assert.Equal("route_runtime", verdict["semantic_failure_domain"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedFallbackSuite", "FallbackTailCoordinatorAggregate")]
    public async Task AnalyzeRetained_FallbackTailCoordinatorAggregate_AcceptsExactPairedReleaseCarryForward(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildFallbackTailCoordinatorCarryForwardFixture(),
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal(
            "pass",
            verdict["fallback_tail_coordinator_aggregate_verdict"]);
        Assert.Equal(
            "1",
            verdict[
                "fallback_tail_coordinator_valid_paired_release_reserved_count"]);
        Assert.Equal(
            "1",
            verdict[
                "fallback_tail_coordinator_valid_paired_release_consumed_count"]);
        Assert.Equal(
            "1",
            verdict[
                "fallback_tail_coordinator_paired_release_carry_forward_count"]);
        Assert.Equal(
            "1",
            verdict[
                "fallback_tail_coordinator_valid_paired_release_carry_forward_count"]);
        Assert.Equal(
            "0",
            verdict["fallback_tail_coordinator_invalid_unresolved_count"]);
    }

    private static string[] BuildFallbackTailRecoveredWithoutInstallReceiptFixture(
        bool recovered,
        string mutation = "none")
    {
        const string sessionId = "sess_recovered_tail_install_missing";
        const string transferId = "transfer_recovered_tail_install_missing";
        const string yieldAuthorityId =
            "fta2-fallback_tail_yield-0123456789abcdef01234567";
        const string peerAuthorityId =
            "fta2-fallback_tail_yield-89abcdef0123456701234567";
        const string retryAuthorityId =
            "fta2-runtime-unlock-retry-0123456789abcdef01234567";
        const string ownerKey =
            "sess_recovered_tail_install_missing|transfer_recovered_tail_install_missing|post_tuna_fallback_v6|6|2|3|2|3|fallback-child:1";
        const string pairKey =
            "sess_recovered_tail_install_missing|transfer_recovered_tail_install_missing|post_tuna_fallback_v6|6|2|3|fallback-child:1";
        const string scope =
            "session_id=sess_recovered_tail_install_missing; " +
            "transfer_id=transfer_recovered_tail_install_missing; direction=outbound; " +
            "route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; " +
            "route_publication_generation=5; route_commit_generation=0; " +
            "runtime_generation=3; terminal_generation=0; leg_generation=3; " +
            "transport_epoch=3; bridge_recovery_generation=2; " +
            "checkpoint_generation=1; checkpoint_request_id=fallback-child:1; " +
            "data_session_identity=data-session-1; data_session_generation=1";
        const string yieldIdentity =
            "authority_id=fta2-fallback_tail_yield-0123456789abcdef01234567; " +
            "predecessor_authority_id=(none); yield_generation=1; yield_kind=Quiescent; " +
            "owner_key=sess_recovered_tail_install_missing|transfer_recovered_tail_install_missing|post_tuna_fallback_v6|6|2|3|2|3|fallback-child:1; " +
            "pair_key=sess_recovered_tail_install_missing|transfer_recovered_tail_install_missing|post_tuna_fallback_v6|6|2|3|fallback-child:1";
        const string holdLifecycle =
            "offer_authority_mode=InitialActivation; hold_boundary=OfferPreSend; " +
            "payer_decision_id=6; offer_generation=2; offer_nonce=offer-nonce-2; " +
            "transaction_generation=1; tuna_path_lease_generation=2; " +
            "tuna_path_lease_listener_run_id=listener-start-2; " +
            "semantic_offer_fingerprint=semantic-offer-2; envelope_generation=1; " +
            "envelope_sent_at_unix_ms=1000; envelope_expires_at_unix_ms=121000; " +
            "control_envelope_fingerprint=envelope-fingerprint-1";
        const string expectedScope =
            "expected_route=post_tuna_fallback_v6; expected_protocol_version=6; " +
            "expected_live_route_epoch=2; expected_runtime_generation=3; " +
            "expected_terminal_generation=0; expected_leg_generation=3; " +
            "expected_route_publication_generation=5; expected_route_commit_generation=0; " +
            "expected_transport_epoch=3; expected_bridge_recovery_generation=2; " +
            "expected_checkpoint_request_id=(none); " +
            "expected_data_session_identity=data-session-1; expected_data_session_generation=1";

        var lines = new List<string>
        {
            LogLine(
                $"event=filetransfer_route_selected; {scope}; " +
                "target_route=post_tuna_fallback_v6; target_protocol_version=6; " +
                "target_transport=regular_nkn; route_publication_action_kind=RoutePublication; " +
                "coordinator_decision_reason=accepted; " +
                "log_writer_id=writer_a; log_writer_sequence=1",
                secondsOffset: 0),
            LogLine(
                $"event=filetransfer_v6_handoff_recovered; session_id={sessionId}; " +
                $"transfer_id={transferId}; direction=outbound; transport_epoch=3; " +
                "reason=frontier_caught_up_to_accepted_tail; " +
                "log_writer_id=writer_a; log_writer_sequence=2",
                secondsOffset: 1),
            LogLine(
                $"event=filetransfer_v6_handoff_tail_unblocked; session_id={sessionId}; " +
                $"transfer_id={transferId}; direction=outbound; transport_epoch=3; " +
                "reason=frontier_caught_up_to_accepted_tail; " +
                "log_writer_id=writer_a; log_writer_sequence=3",
                secondsOffset: 2),
            LogLine(
                $"event=filetransfer_v6_handoff_tail_caught_up_to_accepted; session_id={sessionId}; " +
                $"transfer_id={transferId}; direction=outbound; transport_epoch=3; " +
                "log_writer_id=writer_a; log_writer_sequence=4",
                secondsOffset: 3),
            LogLine(
                $"event=runtime_unlock_initial_activation_receive_recovery_hold_requested; " +
                $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
                $"{holdLifecycle}; {expectedScope}; decision_reason=hold_requested; " +
                "log_writer_id=writer_a; log_writer_sequence=5",
                secondsOffset: 4),
            LogLine(
                $"event=runtime_unlock_initial_activation_receive_recovery_hold_requested; " +
                $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
                $"{holdLifecycle}; {expectedScope}; decision_reason=hold_requested; " +
                "log_writer_id=writer_a; log_writer_sequence=6",
                secondsOffset: 5),
            LogLine(
                $"event=filetransfer_runtime_unlock_handoff_held_for_retry_authority; " +
                $"session_id={sessionId}; lanes=file; source=tuna_activation_handoff; " +
                "decision_status=Held; " +
                "decision_reason=fallback_paired_tail_yield_install_receipt_missing; " +
                "log_writer_id=writer_a; log_writer_sequence=7",
                secondsOffset: 6),
            LogLine(
                $"event=filetransfer_runtime_unlock_handoff_held_for_retry_authority; " +
                $"session_id={sessionId}; lanes=file; source=tuna_activation_handoff; " +
                "decision_status=Held; " +
                "decision_reason=fallback_paired_tail_yield_install_receipt_missing; " +
                "log_writer_id=writer_a; log_writer_sequence=8",
                secondsOffset: 7)
        };

        if (mutation == "missing_caught_up")
        {
            lines.RemoveAll(line => line.Contains(
                "event=filetransfer_v6_handoff_tail_caught_up_to_accepted",
                StringComparison.Ordinal));
        }
        else if (mutation == "missing_unblocked")
        {
            lines.RemoveAll(line => line.Contains(
                "event=filetransfer_v6_handoff_tail_unblocked",
                StringComparison.Ordinal));
        }
        else if (mutation == "wrong_writer")
        {
            var index = lines.FindIndex(line => line.Contains(
                "event=filetransfer_v6_handoff_tail_caught_up_to_accepted",
                StringComparison.Ordinal));
            lines[index] = lines[index].Replace(
                "log_writer_id=writer_a",
                "log_writer_id=writer_b",
                StringComparison.Ordinal);
        }
        else if (mutation == "wrong_session")
        {
            var index = lines.FindIndex(line => line.Contains(
                "event=filetransfer_v6_handoff_tail_caught_up_to_accepted",
                StringComparison.Ordinal));
            lines[index] = lines[index].Replace(
                $"session_id={sessionId}",
                "session_id=sess_wrong",
                StringComparison.Ordinal);
        }
        else if (mutation == "scope_replacement")
        {
            var firstHoldRequestIndex = lines.FindIndex(line => line.Contains(
                "event=runtime_unlock_initial_activation_receive_recovery_hold_requested",
                StringComparison.Ordinal));
            lines.Insert(
                firstHoldRequestIndex,
                LogLine(
                    $"event=filetransfer_route_selected; session_id={sessionId}; " +
                    $"transfer_id={transferId}; direction=outbound; route=file_tuna_v4; " +
                    "protocol_version=4; live_route_epoch=3; runtime_generation=4; " +
                    "terminal_generation=0; leg_generation=4; " +
                    "route_publication_generation=6; route_commit_generation=0; " +
                    "target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; " +
                    "route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted; " +
                    "log_writer_id=writer_a; log_writer_sequence=5",
                    secondsOffset: 4));
        }

        if (!recovered)
        {
            lines.Add(LogLine(
                $"event=file_transfer_outbound_terminal; role=helpee; session_id={sessionId}; " +
                $"transfer_id={transferId}; state=Failed; error_code=route_cycle_missing; " +
                "log_writer_id=writer_a; log_writer_sequence=9",
                secondsOffset: 8));
            return lines.ToArray();
        }

        if (mutation is "outbound_terminal" or "inbound_terminal")
        {
            var direction = mutation == "outbound_terminal" ? "outbound" : "inbound";
            lines.Add(LogLine(
                $"event=file_transfer_{direction}_terminal; direction={direction}; role=helper; " +
                $"session_id={sessionId}; transfer_id={transferId}; state=Failed; " +
                "error_code=diagnostic_terminal; log_writer_id=writer_a; log_writer_sequence=9",
                secondsOffset: 8));
        }

        lines.AddRange(
        [
            LogLine(
                $"event=filetransfer_fallback_tail_yield_issued; {scope}; {yieldIdentity}; " +
                "pairing_state=YieldIssued; decision_status=Accepted; reason=accepted; " +
                "decision_reason=accepted; log_writer_id=writer_a; log_writer_sequence=10",
                secondsOffset: 9),
            LogLine(
                $"event=filetransfer_fallback_tail_local_proof_receipt_decided; {scope}; {yieldIdentity}; " +
                "pairing_state=YieldIssued; local_proof_identity=local-proof-1; " +
                "local_frame_fingerprint=local-frame-1; decision_status=Applied; reason=accepted; " +
                "decision_reason=accepted; log_writer_id=writer_a; log_writer_sequence=11",
                secondsOffset: 10),
            LogLine(
                $"event=filetransfer_fallback_tail_peer_proof_receipt_decided; {scope}; {yieldIdentity}; " +
                $"pairing_state=YieldIssued; peer_authority_id={peerAuthorityId}; " +
                "peer_predecessor_authority_id=(none); peer_yield_generation=1; " +
                "peer_observation_identity=peer-proof-1; peer_frame_fingerprint=peer-frame-1; " +
                $"peer_owner_key={ownerKey}; peer_pair_key={pairKey}; decision_status=Applied; " +
                "reason=accepted; decision_reason=accepted; " +
                "log_writer_id=writer_a; log_writer_sequence=12",
                secondsOffset: 11),
            LogLine(
                $"event=filetransfer_fallback_tail_pairing_committed; {scope}; {yieldIdentity}; " +
                "pairing_state=Paired; pairing_generation=1; " +
                "local_proof_identity=local-proof-1; local_frame_fingerprint=local-frame-1; " +
                $"peer_authority_id={peerAuthorityId}; " +
                "peer_predecessor_authority_id=(none); peer_yield_generation=1; " +
                "peer_observation_identity=peer-proof-1; peer_frame_fingerprint=peer-frame-1; " +
                $"peer_owner_key={ownerKey}; peer_pair_key={pairKey}; " +
                "release_kind=FallbackPairedTailYield; release_outcome=Quiescent; " +
                "decision_status=Accepted; reason=accepted; decision_reason=accepted; " +
                "log_writer_id=writer_a; log_writer_sequence=13",
                secondsOffset: 12),
            LogLine(
                $"event=filetransfer_fallback_tail_paired_release_reserved; {scope}; {yieldIdentity}; " +
                "pairing_state=ReleaseReserved; pairing_generation=1; " +
                "release_kind=FallbackPairedTailYield; release_outcome=Quiescent; " +
                $"reservation_generation=1; retry_authority_id={retryAuthorityId}; " +
                "retry_attempt_generation=1; install_generation=0; decision_status=Accepted; " +
                "reason=accepted; decision_reason=accepted; " +
                "log_writer_id=writer_a; log_writer_sequence=14",
                secondsOffset: 13)
        ]);

        if (mutation == "pairing_reason")
        {
            lines.Add(LogLine(
                $"event=filetransfer_fallback_tail_pairing_committed; {scope}; {yieldIdentity}; " +
                "pairing_state=Paired; pairing_generation=1; " +
                "local_proof_identity=local-proof-1; local_frame_fingerprint=local-frame-1; " +
                $"peer_authority_id={peerAuthorityId}; " +
                "peer_predecessor_authority_id=(none); peer_yield_generation=1; " +
                "peer_observation_identity=peer-proof-1; peer_frame_fingerprint=peer-frame-1; " +
                $"peer_owner_key={ownerKey}; peer_pair_key={pairKey}; " +
                "release_kind=FallbackPairedTailYield; release_outcome=Quiescent; " +
                "decision_status=Accepted; reason=mutated; decision_reason=mutated; " +
                "log_writer_id=writer_a; log_writer_sequence=15",
                secondsOffset: 14));
        }

        var requestContract = mutation == "retry_contract" ? 2 : 1;
        lines.Add(LogLine(
            $"event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested; " +
            $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
            "source_route=post_tuna_fallback_v6; source_protocol_version=6; " +
            "source_live_route_epoch=2; source_runtime_generation=3; " +
            "source_terminal_generation=0; source_leg_generation=3; " +
            "source_route_publication_generation=5; source_route_commit_generation=0; " +
            $"authority_id={yieldAuthorityId}; predecessor_authority_id=(none); " +
            $"yield_generation=1; yield_kind=Quiescent; recovery_contract_generation={requestContract}; " +
            "retired_offer_generation=0; dispatch_attempt_generation=1; " +
            "missing_dispatch_successor_generation=0; request_active_predecessor_supersession=0; " +
            "decision_status=Queued; decision_reason=paired_tail_yield_queued; " +
            "log_writer_id=writer_a; log_writer_sequence=16",
            secondsOffset: 15));

        var installSequence = 17;
        foreach (var installLine in BuildFallbackTailCoordinatorPhase2InstallLines(
                     sessionId,
                     transferId,
                     yieldAuthorityId,
                     retryAuthorityId,
                     transportEpoch: 3,
                     bridgeRecoveryGeneration: 2))
        {
            var line = installLine
                .Replace("retired_generation=1", "retired_generation=0", StringComparison.Ordinal)
                .Replace(
                    "source_publication_reason=accepted",
                    "source_publication_reason=fallback_paired_tail_yield",
                    StringComparison.Ordinal);
            if (mutation == "unrelated_install")
            {
                line = line.Replace(
                    retryAuthorityId,
                    "fta2-runtime-unlock-retry-unrelated",
                    StringComparison.Ordinal);
            }
            if (line.Contains("event=runtime_unlock_retry_dispatch_decision_staged", StringComparison.Ordinal))
            {
                line += "; stage_disposition=Retained; stage_cleared=0";
            }
            else if (line.Contains("event=runtime_unlock_retry_dispatch_install_receipt_issued", StringComparison.Ordinal))
            {
                line += "; stage_status=Staged; stage_disposition=Retained; stage_cleared=0";
            }
            else if (line.Contains("event=runtime_unlock_retry_dispatch_install_committed", StringComparison.Ordinal))
            {
                line += "; stage_status=Staged; stage_disposition=Consumed; stage_cleared=1";
            }
            line += $"; log_writer_id=writer_a; log_writer_sequence={installSequence++}";
            lines.Add(line);
        }

        lines.AddRange(
        [
            LogLine(
                $"event=filetransfer_fallback_tail_paired_release_consumed; {scope}; {yieldIdentity}; " +
                "pairing_state=ReleaseConsumed; pairing_generation=1; " +
                "release_kind=FallbackPairedTailYield; release_outcome=Quiescent; " +
                $"reservation_generation=1; retry_authority_id={retryAuthorityId}; " +
                "retry_attempt_generation=1; install_generation=7; decision_status=Accepted; " +
                "reason=accepted; decision_reason=accepted; " +
                "log_writer_id=writer_a; log_writer_sequence=21",
                secondsOffset: 20),
            LogLine(
                $"event=filetransfer_route_transition_prepared; session_id={sessionId}; " +
                $"transfer_id={transferId}; direction=outbound; " +
                "source_route=post_tuna_fallback_v6; source_protocol_version=6; " +
                "source_live_route_epoch=2; source_runtime_generation=3; " +
                "source_terminal_generation=0; source_leg_generation=3; " +
                "source_route_publication_generation=5; source_route_commit_generation=0; " +
                "source_transport_epoch=3; source_bridge_recovery_generation=2; " +
                "source_data_session_generation=1; transition_kind=PostTunaFallbackV6ToTunaV4; " +
                "target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; " +
                $"fallback_yield_authority_id={yieldAuthorityId}; fallback_yield_generation=1; " +
                "pairing_generation=1; retry_install_generation=7; decision_status=Prepared; " +
                "decision_reason=prepared; log_writer_id=writer_a; log_writer_sequence=22",
                secondsOffset: 21),
            LogLine(
                $"event=file_transfer_outbound_terminal; role=helpee; session_id={sessionId}; " +
                $"transfer_id={transferId}; state=Completed; error_code=(none); " +
                "log_writer_id=writer_a; log_writer_sequence=23",
                secondsOffset: 22)
        ]);
        return lines.ToArray();
    }

    private static string[] BuildFallbackPairedYieldRetryBlockedByGenericActivationFixture(
        bool recovered)
    {
        const string sessionId = "sess_retry_blocked_by_generic";
        const string transferId = "transfer_retry_blocked_by_generic";
        const string yieldAuthority = "fta2-fallback_tail_yield-111111111111111111111111";
        const string retryAuthority = "fta2-runtime_unlock-retry-222222222222222222222222";
        const string writer = "writer_a";
        var scope =
            $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
            "route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; " +
            "runtime_generation=3; terminal_generation=0; leg_generation=3; " +
            "route_publication_generation=5; route_commit_generation=0; transport_epoch=2; " +
            "bridge_recovery_generation=1; checkpoint_generation=0; checkpoint_request_id=(none); " +
            "data_session_identity=data-session-1; data_session_generation=1";
        var source =
            "source_route=post_tuna_fallback_v6; source_protocol_version=6; " +
            "source_live_route_epoch=2; source_runtime_generation=3; source_terminal_generation=0; " +
            "source_leg_generation=3; source_route_publication_generation=5; " +
            "source_route_commit_generation=(none); source_route_commit_generation_present=0; " +
            "source_target_route=post_tuna_fallback_v6; source_target_protocol_version=6; " +
            "source_target_transport=regular_nkn; source_route_publication_action_kind=RoutePublication; " +
            "source_coordinator_decision_reason=fallback_paired_tail_yield";
        var lines = new List<string>
        {
            LogLine(
                $"event=filetransfer_route_selected; {scope}; target_route=post_tuna_fallback_v6; " +
                "target_protocol_version=6; target_transport=regular_nkn; " +
                "route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted; " +
                $"log_writer_id={writer}; log_writer_sequence=1",
                secondsOffset: 1),
            LogLine(
                $"event=filetransfer_fallback_tail_local_proof_receipt_decided; {scope}; " +
                $"authority_id={yieldAuthority}; yield_generation=1; yield_kind=Quiescent; " +
                $"decision_status=Applied; log_writer_id={writer}; log_writer_sequence=2",
                secondsOffset: 2),
            LogLine(
                $"event=filetransfer_fallback_tail_peer_proof_receipt_decided; {scope}; " +
                $"authority_id={yieldAuthority}; yield_generation=1; yield_kind=Quiescent; " +
                $"decision_status=Applied; log_writer_id={writer}; log_writer_sequence=3",
                secondsOffset: 3),
            LogLine(
                $"event=tuna_payer_decision_started; payer_decision_id=6; reason=runtime_unlock; " +
                $"log_writer_id={writer}; log_writer_sequence=4",
                secondsOffset: 4),
            LogLine(
                $"event=runtime_unlock_filetransfer_owner_accepted_before_path_lease; {scope}; " +
                "trigger=runtime_unlock; decision_reason=current_coordinator_route_publication_authority; " +
                $"log_writer_id={writer}; log_writer_sequence=5",
                secondsOffset: 5),
            LogLine(
                "event=tuna_acceleration_payer_intent_queued; intent=will_listen; role=helpee; " +
                "trigger=runtime_unlock; lanes=file; payer_decision_id=6; " +
                $"log_writer_id={writer}; log_writer_sequence=6",
                secondsOffset: 6),
            LogLine(
                $"event=tuna_path_lease_started; session_id={sessionId}; transfer_id={transferId}; " +
                "transaction_generation=0; offer_generation=0; lease_generation=2; " +
                "listener_run_id=listener-start-2; payer_decision_id=6; state=listenerstarting; " +
                $"reason=runtime_unlock; log_writer_id={writer}; log_writer_sequence=7",
                secondsOffset: 7),
            LogLine(
                $"event=filetransfer_fallback_tail_pairing_committed; {scope}; " +
                $"authority_id={yieldAuthority}; yield_generation=1; yield_kind=Quiescent; " +
                "decision_status=Accepted; release_kind=FallbackPairedTailYield; release_outcome=Quiescent; " +
                $"log_writer_id={writer}; log_writer_sequence=8",
                secondsOffset: 8),
            LogLine(
                $"event=filetransfer_fallback_tail_paired_yield_granted; {scope}; " +
                $"authority_id={yieldAuthority}; yield_generation=1; reason=fallback_tail_paired_yield; " +
                $"log_writer_id={writer}; log_writer_sequence=9",
                secondsOffset: 9),
            LogLine(
                $"event=runtime_unlock_retry_dispatch_authority_request_queued; session_id={sessionId}; " +
                $"transfer_id={transferId}; direction=outbound; contract_generation=1; retired_generation=1; " +
                "dispatch_attempt_generation=1; missing_dispatch_successor_generation=0; " +
                "predecessor_authority_id=(none); request_active_predecessor_supersession=0; " +
                "retry_reason=fallback_paired_tail_yield; recovery_reason=fallback_tail_pairing_committed; " +
                $"{source}; queue_generation=1; delay_ms=1000; decision_reason=coordinator_request_queued; " +
                $"log_writer_id={writer}; log_writer_sequence=10",
                secondsOffset: 10),
            LogLine(
                $"event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested; " +
                $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
                $"authority_id={yieldAuthority}; yield_generation=1; yield_kind=Quiescent; " +
                "source_route=post_tuna_fallback_v6; source_protocol_version=6; " +
                "source_live_route_epoch=2; source_runtime_generation=3; source_terminal_generation=0; " +
                "source_leg_generation=3; source_route_publication_generation=5; " +
                "source_route_commit_generation=0; recovery_contract_generation=1; " +
                "retired_offer_generation=1; dispatch_attempt_generation=1; " +
                "missing_dispatch_successor_generation=0; predecessor_authority_id=(none); " +
                "request_active_predecessor_supersession=0; decision_status=Queued; " +
                $"decision_reason=fallback_paired_tail_yield_request_queued; log_writer_id={writer}; " +
                "log_writer_sequence=11",
                secondsOffset: 11),
            LogLine(
                $"event=runtime_unlock_retry_negotiation_slot_decided; session_id={sessionId}; " +
                $"transfer_id={transferId}; decision_status=Deferred; " +
                "intent_kind=CoordinatorPreparedRequest; intent_generation=1; slot_generation=0; " +
                "contract_generation=1; retired_generation=1; source_route=post_tuna_fallback_v6; " +
                "source_publication_generation=5; source_transport_epoch=2; " +
                "source_bridge_recovery_generation=1; source_checkpoint_request_id=(none); " +
                "data_session_identity=data-session-1; data_session_generation=1; authority_effect=none; " +
                "decision_reason=retry_dispatch_negotiation_slot_busy; " +
                $"log_writer_id={writer}; log_writer_sequence=12",
                secondsOffset: 12)
        };

        if (recovered)
        {
            lines.Add(LogLine(
                $"event=runtime_unlock_retry_negotiation_slot_decided; session_id={sessionId}; " +
                $"transfer_id={transferId}; decision_status=Reserved; " +
                "intent_kind=CoordinatorPreparedRequest; intent_generation=1; slot_generation=7; " +
                "contract_generation=1; retired_generation=1; source_route=post_tuna_fallback_v6; " +
                "source_publication_generation=5; source_transport_epoch=2; " +
                "source_bridge_recovery_generation=1; source_checkpoint_request_id=(none); " +
                "data_session_identity=data-session-1; data_session_generation=1; authority_effect=none; " +
                $"decision_reason=retry_dispatch_slot_reserved; log_writer_id={writer}; log_writer_sequence=13",
                secondsOffset: 13));
            lines.Add(LogLine(
                $"event=filetransfer_fallback_tail_paired_release_reserved; {scope}; " +
                $"authority_id={yieldAuthority}; yield_generation=1; retry_authority_id={retryAuthority}; " +
                "reservation_generation=1; decision_status=Accepted; " +
                $"log_writer_id={writer}; log_writer_sequence=14",
                secondsOffset: 14));
            foreach (var installLine in BuildFallbackTailCoordinatorPhase2InstallLines(
                         sessionId,
                         transferId,
                         yieldAuthority,
                         retryAuthority,
                         secondsOffsetBase: 15))
            {
                lines.Add(installLine + $"; log_writer_id={writer}");
            }
            lines.Add(LogLine(
                $"event=filetransfer_fallback_tail_paired_release_consumed; {scope}; " +
                $"authority_id={yieldAuthority}; yield_generation=1; retry_authority_id={retryAuthority}; " +
                "reservation_generation=1; install_generation=7; decision_status=Accepted; " +
                $"log_writer_id={writer}; log_writer_sequence=19",
                secondsOffset: 19));
            lines.Add(LogLine(
                $"event=runtime_unlock_retry_dispatch_settlement_decided; session_id={sessionId}; " +
                $"transfer_id={transferId}; authority_id={retryAuthority}; install_generation=7; " +
                "settlement_outcome=DispatchObserved; decision_status=Accepted; " +
                $"log_writer_id={writer}; log_writer_sequence=20",
                secondsOffset: 20));
            lines.Add(LogLine(
                $"event=filetransfer_route_transition_prepared; session_id={sessionId}; transfer_id={transferId}; " +
                "direction=outbound; transition_kind=PostTunaFallbackV6ToTunaV4; " +
                "source_route=post_tuna_fallback_v6; target_route=file_tuna_v4; " +
                $"fallback_yield_authority_id={yieldAuthority}; fallback_yield_generation=1; " +
                "retry_install_generation=7; decision_status=Prepared; " +
                $"log_writer_id={writer}; log_writer_sequence=21",
                secondsOffset: 21));
        }

        lines.Add(LogLine(
            $"event=file_transfer_outbound_terminal; session_id={sessionId}; transfer_id={transferId}; " +
            $"state={(recovered ? "Completed" : "Failed")}; error_code={(recovered ? "(none)" : "route_cycle_missing")}; " +
            $"log_writer_id={writer}; log_writer_sequence=22",
            secondsOffset: 22));
        return lines.ToArray();
    }

    private static string[] BuildFallbackPairedYieldRetryIntentNotRecheckedAfterUnlockFixture(
        bool recovered)
    {
        const string sessionId = "sess_retry_recheck_after_unlock";
        const string transferId = "transfer_retry_recheck_after_unlock";
        const string yieldAuthority = "fta2-fallback_tail_yield-333333333333333333333333";
        const string retryAuthority = "fta2-runtime_unlock-retry-444444444444444444444444";
        const string writer = "writer_a";
        var scope =
            $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
            "route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; " +
            "runtime_generation=3; terminal_generation=0; leg_generation=3; " +
            "route_publication_generation=5; route_commit_generation=0; transport_epoch=2; " +
            "bridge_recovery_generation=1; checkpoint_generation=0; checkpoint_request_id=(none); " +
            "data_session_identity=data-session-1; data_session_generation=1";
        var source =
            "source_route=post_tuna_fallback_v6; source_protocol_version=6; " +
            "source_live_route_epoch=2; source_runtime_generation=3; source_terminal_generation=0; " +
            "source_leg_generation=3; source_route_publication_generation=5; " +
            "source_route_commit_generation=(none); source_route_commit_generation_present=0; " +
            "source_target_route=post_tuna_fallback_v6; source_target_protocol_version=6; " +
            "source_target_transport=regular_nkn; source_route_publication_action_kind=RoutePublication; " +
            "source_coordinator_decision_reason=fallback_paired_tail_yield";
        var slotScope =
            "source_route=post_tuna_fallback_v6; source_publication_generation=5; " +
            "source_transport_epoch=2; source_bridge_recovery_generation=1; " +
            "source_checkpoint_request_id=(none); data_session_identity=data-session-1; " +
            "data_session_generation=1";
        var lines = new List<string>
        {
            LogLine(
                $"event=filetransfer_route_selected; {scope}; target_route=post_tuna_fallback_v6; " +
                "target_protocol_version=6; target_transport=regular_nkn; " +
                "route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted; " +
                $"log_writer_id={writer}; log_writer_sequence=1",
                secondsOffset: 1),
            LogLine(
                $"event=tuna_runtime_locked; source=header; stop_requested=1; reason=header_switch_off; " +
                $"log_writer_id={writer}; log_writer_sequence=2",
                secondsOffset: 2),
            LogLine(
                $"event=filetransfer_fallback_tail_local_proof_receipt_decided; {scope}; " +
                $"authority_id={yieldAuthority}; yield_generation=1; yield_kind=Quiescent; " +
                $"decision_status=Applied; log_writer_id={writer}; log_writer_sequence=3",
                secondsOffset: 3),
            LogLine(
                $"event=filetransfer_fallback_tail_peer_proof_receipt_decided; {scope}; " +
                $"authority_id={yieldAuthority}; yield_generation=1; yield_kind=Quiescent; " +
                $"decision_status=Applied; log_writer_id={writer}; log_writer_sequence=4",
                secondsOffset: 4),
            LogLine(
                $"event=filetransfer_fallback_tail_pairing_committed; {scope}; " +
                $"authority_id={yieldAuthority}; yield_generation=1; yield_kind=Quiescent; " +
                "decision_status=Accepted; release_kind=FallbackPairedTailYield; release_outcome=Quiescent; " +
                $"log_writer_id={writer}; log_writer_sequence=5",
                secondsOffset: 5),
            LogLine(
                $"event=filetransfer_fallback_tail_paired_yield_granted; {scope}; " +
                $"authority_id={yieldAuthority}; yield_generation=1; reason=fallback_tail_paired_yield; " +
                $"log_writer_id={writer}; log_writer_sequence=6",
                secondsOffset: 6),
            LogLine(
                $"event=runtime_unlock_retry_dispatch_authority_request_queued; session_id={sessionId}; " +
                $"transfer_id={transferId}; direction=outbound; contract_generation=1; retired_generation=0; " +
                "dispatch_attempt_generation=1; missing_dispatch_successor_generation=0; " +
                "predecessor_authority_id=(none); request_active_predecessor_supersession=0; " +
                "retry_reason=fallback_paired_tail_yield; recovery_reason=fallback_tail_pairing_committed; " +
                $"{source}; queue_generation=1; delay_ms=1000; decision_reason=coordinator_request_queued; " +
                $"log_writer_id={writer}; log_writer_sequence=7",
                secondsOffset: 7),
            LogLine(
                "event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested; " +
                $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
                $"authority_id={yieldAuthority}; yield_generation=1; yield_kind=Quiescent; " +
                "source_route=post_tuna_fallback_v6; source_protocol_version=6; source_live_route_epoch=2; " +
                "source_runtime_generation=3; source_terminal_generation=0; source_leg_generation=3; " +
                "source_route_publication_generation=5; source_route_commit_generation=0; " +
                "recovery_contract_generation=1; retired_offer_generation=0; dispatch_attempt_generation=1; " +
                "missing_dispatch_successor_generation=0; predecessor_authority_id=(none); " +
                "request_active_predecessor_supersession=0; decision_status=Queued; " +
                $"decision_reason=fallback_paired_tail_yield_request_queued; log_writer_id={writer}; " +
                "log_writer_sequence=8",
                secondsOffset: 8),
            LogLine(
                $"event=runtime_unlock_retry_negotiation_slot_decided; session_id={sessionId}; " +
                $"transfer_id={transferId}; decision_status=Deferred; intent_kind=CoordinatorPreparedRequest; " +
                "intent_generation=1; slot_generation=0; contract_generation=1; retired_generation=0; " +
                $"{slotScope}; authority_effect=none; " +
                $"decision_reason=retry_dispatch_authority_backoff_pending; log_writer_id={writer}; " +
                "log_writer_sequence=9",
                secondsOffset: 9),
            LogLine(
                $"event=runtime_unlock_retry_negotiation_slot_decided; session_id={sessionId}; " +
                $"transfer_id={transferId}; decision_status=Deferred; intent_kind=CoordinatorPreparedRequest; " +
                "intent_generation=1; slot_generation=0; contract_generation=1; retired_generation=0; " +
                $"{slotScope}; authority_effect=none; " +
                $"decision_reason=retry_dispatch_negotiation_slot_ineligible; log_writer_id={writer}; " +
                "log_writer_sequence=10",
                secondsOffset: 10),
            LogLine(
                $"event=runtime_unlock_post_fallback_negotiation_deferred; session_id={sessionId}; " +
                $"transfer_id={transferId}; direction=outbound; decision_status=Deferred; " +
                "authority_mode=InitialActivation; authority_effect=none; trigger=runtime_unlock; " +
                "decision_reason=post_fallback_retry_authority_pending; source_route=post_tuna_fallback_v6; " +
                "source_protocol_version=6; source_live_route_epoch=2; source_runtime_generation=3; " +
                "source_terminal_generation=0; source_leg_generation=3; source_publication_generation=5; " +
                "source_transport_epoch=2; source_bridge_recovery_generation=1; " +
                "source_checkpoint_request_id=(none); data_session_identity=data-session-1; " +
                $"data_session_generation=1; log_writer_id={writer}; log_writer_sequence=11",
                secondsOffset: 11)
        };

        if (recovered)
        {
            lines.Add(LogLine(
                $"event=runtime_unlock_retry_negotiation_slot_decided; session_id={sessionId}; " +
                $"transfer_id={transferId}; decision_status=Reserved; intent_kind=CoordinatorPreparedRequest; " +
                "intent_generation=1; slot_generation=7; contract_generation=1; retired_generation=0; " +
                $"{slotScope}; authority_effect=none; decision_reason=retry_dispatch_slot_reserved; " +
                $"log_writer_id={writer}; log_writer_sequence=12",
                secondsOffset: 12));
            lines.Add(LogLine(
                $"event=filetransfer_fallback_tail_paired_release_reserved; {scope}; " +
                $"authority_id={yieldAuthority}; yield_generation=1; retry_authority_id={retryAuthority}; " +
                "reservation_generation=1; decision_status=Accepted; " +
                $"log_writer_id={writer}; log_writer_sequence=13",
                secondsOffset: 13));
            foreach (var installLine in BuildFallbackTailCoordinatorPhase2InstallLines(
                         sessionId,
                         transferId,
                         yieldAuthority,
                         retryAuthority,
                         secondsOffsetBase: 14))
            {
                lines.Add(installLine + $"; log_writer_id={writer}");
            }
            lines.Add(LogLine(
                $"event=filetransfer_fallback_tail_paired_release_consumed; {scope}; " +
                $"authority_id={yieldAuthority}; yield_generation=1; retry_authority_id={retryAuthority}; " +
                "reservation_generation=1; install_generation=7; decision_status=Accepted; " +
                $"log_writer_id={writer}; log_writer_sequence=18",
                secondsOffset: 18));
            lines.Add(LogLine(
                $"event=runtime_unlock_retry_dispatch_settlement_decided; session_id={sessionId}; " +
                $"transfer_id={transferId}; authority_id={retryAuthority}; install_generation=7; " +
                "settlement_outcome=DispatchObserved; decision_status=Accepted; " +
                $"log_writer_id={writer}; log_writer_sequence=19",
                secondsOffset: 19));
            lines.Add(LogLine(
                $"event=filetransfer_route_transition_prepared; session_id={sessionId}; transfer_id={transferId}; " +
                "direction=outbound; transition_kind=PostTunaFallbackV6ToTunaV4; " +
                "source_route=post_tuna_fallback_v6; target_route=file_tuna_v4; " +
                $"fallback_yield_authority_id={yieldAuthority}; fallback_yield_generation=1; " +
                "retry_install_generation=7; decision_status=Prepared; " +
                $"log_writer_id={writer}; log_writer_sequence=20",
                secondsOffset: 20));
        }

        lines.Add(LogLine(
            $"event=file_transfer_outbound_terminal; session_id={sessionId}; transfer_id={transferId}; " +
            $"state={(recovered ? "Completed" : "Failed")}; " +
            $"error_code={(recovered ? "(none)" : "route_cycle_missing")}; " +
            $"log_writer_id={writer}; log_writer_sequence=21",
            secondsOffset: 21));
        return lines.ToArray();
    }

    private static string[] BuildFallbackPairedYieldRetrySourceScopeQueueRejectionFixture(
        bool recovered)
    {
        const string sessionId = "sess_fallback_tail_aggregate";
        const string transferId = "transfer_fallback_tail_aggregate";
        const string yieldAuthority = "fta2-fallback_tail_yield-0123456789abcdef01234567";
        const string retryAuthority = "fta2-runtime_unlock-retry-0123456789abcdef01234567";
        var scope =
            $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
            "route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; " +
            "runtime_generation=3; terminal_generation=0; leg_generation=3; " +
            "route_publication_generation=5; route_commit_generation=0; " +
            "transport_epoch=2; bridge_recovery_generation=1; checkpoint_generation=0; " +
            "checkpoint_request_id=(none); data_session_identity=data-session-1; " +
            "data_session_generation=1";
        const string ownerKey =
            "sess_fallback_tail_aggregate|transfer_fallback_tail_aggregate|post_tuna_fallback_v6|6|2|3|1|2|(none)";
        const string pairKey =
            "sess_fallback_tail_aggregate|transfer_fallback_tail_aggregate|post_tuna_fallback_v6|6|2|2|(none)";
        var yield =
            $"authority_id={yieldAuthority}; predecessor_authority_id=(none); " +
            $"yield_generation=1; yield_kind=Quiescent; owner_key={ownerKey}; pair_key={pairKey}";
        var lines = BuildFallbackTailCoordinatorAggregateFixture(
                peerFirst: false,
                includeConsumption: false)
            .Where(line =>
                !line.Contains("event=file_transfer_", StringComparison.Ordinal) &&
                !line.Contains("event=transfer_terminal", StringComparison.Ordinal))
            .Select(line => line + "; log_writer_id=writer_a")
            .ToList();
        var pairing = lines.Single(line =>
            line.Contains("event=filetransfer_fallback_tail_pairing_committed", StringComparison.Ordinal));
        lines.Add(
            pairing.Replace(
                    "event=filetransfer_fallback_tail_pairing_committed",
                    "event=filetransfer_fallback_tail_paired_yield_granted",
                    StringComparison.Ordinal)
                .Replace(
                    "decision_status=Accepted; decision_reason=accepted",
                    "reason=fallback_tail_paired_yield; decision_status=Accepted; decision_reason=accepted",
                    StringComparison.Ordinal));

        for (var copy = 0; copy < 2; copy++)
        {
            var sequence = 7 + (copy * 2);
            lines.Add(LogLine(
                $"event=runtime_unlock_retry_dispatch_authority_request_queue_rejected; " +
                $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
                "contract_generation=1; retired_generation=0; dispatch_attempt_generation=1; " +
                "missing_dispatch_successor_generation=0; predecessor_authority_id=(none); " +
                "request_active_predecessor_supersession=0; retry_reason=fallback_paired_tail_yield; " +
                "recovery_reason=fallback_tail_pairing_committed; source_route=post_tuna_fallback_v6; " +
                "source_protocol_version=6; source_live_route_epoch=2; source_runtime_generation=3; " +
                "source_terminal_generation=0; source_leg_generation=3; " +
                "source_route_publication_generation=5; source_route_commit_generation=(none); " +
                "source_route_commit_generation_present=0; source_target_route=post_tuna_fallback_v6; " +
                "source_target_protocol_version=6; source_target_transport=regular_nkn; " +
                "source_route_publication_action_kind=RoutePublication; " +
                "source_coordinator_decision_reason=fallback_paired_tail_yield; " +
                "decision_reason=coordinator_request_source_scope_not_current; " +
                $"log_writer_id=writer_a; log_writer_sequence={sequence}",
                secondsOffset: sequence));
            lines.Add(LogLine(
                $"event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested; " +
                $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
                $"authority_id={yieldAuthority}; yield_generation=1; yield_kind=Quiescent; " +
                "source_route=post_tuna_fallback_v6; source_protocol_version=6; " +
                "source_live_route_epoch=2; source_runtime_generation=3; source_terminal_generation=0; " +
                "source_leg_generation=3; source_route_publication_generation=5; " +
                "source_route_commit_generation=0; recovery_contract_generation=1; " +
                "retired_offer_generation=0; dispatch_attempt_generation=1; " +
                "missing_dispatch_successor_generation=0; predecessor_authority_id=(none); " +
                "request_active_predecessor_supersession=0; decision_status=Rejected; " +
                "decision_reason=fallback_paired_tail_yield_request_queue_rejected; " +
                $"log_writer_id=writer_a; log_writer_sequence={sequence + 1}",
                secondsOffset: sequence + 1));
        }

        if (recovered)
        {
            lines.Add(LogLine(
                $"event=filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested; " +
                $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
                $"authority_id={yieldAuthority}; yield_generation=1; yield_kind=Quiescent; " +
                "source_route=post_tuna_fallback_v6; source_protocol_version=6; " +
                "source_live_route_epoch=2; source_runtime_generation=3; source_terminal_generation=0; " +
                "source_leg_generation=3; source_route_publication_generation=5; " +
                "source_route_commit_generation=0; recovery_contract_generation=1; " +
                "retired_offer_generation=0; dispatch_attempt_generation=1; " +
                "missing_dispatch_successor_generation=0; predecessor_authority_id=(none); " +
                "request_active_predecessor_supersession=0; decision_status=Queued; " +
                "decision_reason=paired_tail_yield_queued; log_writer_id=writer_a; log_writer_sequence=11",
                secondsOffset: 11));
            lines.Add(LogLine(
                $"event=filetransfer_fallback_tail_paired_release_reserved; {scope}; {yield}; " +
                $"retry_authority_id={retryAuthority}; retry_attempt_generation=1; " +
                "reservation_generation=1; install_generation=0; decision_status=Accepted; " +
                "log_writer_id=writer_a; log_writer_sequence=12",
                secondsOffset: 12));
            foreach (var installLine in BuildFallbackTailCoordinatorPhase2InstallLines(
                         sessionId,
                         transferId,
                         yieldAuthority,
                         retryAuthority,
                         secondsOffsetBase: 13))
            {
                lines.Add(installLine
                    .Replace("retired_generation=1", "retired_generation=0", StringComparison.Ordinal)
                    .Replace("source_publication_reason=accepted", "source_publication_reason=fallback_paired_tail_yield", StringComparison.Ordinal) +
                    "; log_writer_id=writer_a");
            }
            lines.Add(LogLine(
                $"event=filetransfer_fallback_tail_paired_release_consumed; {scope}; {yield}; " +
                $"retry_authority_id={retryAuthority}; retry_attempt_generation=1; " +
                "reservation_generation=1; install_generation=7; decision_status=Accepted; " +
                "log_writer_id=writer_a; log_writer_sequence=17",
                secondsOffset: 17));
            lines.Add(LogLine(
                $"event=filetransfer_route_transition_prepared; session_id={sessionId}; transfer_id={transferId}; " +
                "direction=outbound; transition_kind=PostTunaFallbackV6ToTunaV4; " +
                "source_route=post_tuna_fallback_v6; target_route=file_tuna_v4; " +
                $"fallback_yield_authority_id={yieldAuthority}; fallback_yield_generation=1; " +
                "retry_install_generation=7; decision_status=Prepared; " +
                "log_writer_id=writer_a; log_writer_sequence=18",
                secondsOffset: 18));
        }

        lines.Add(LogLine(
            $"event=file_transfer_outbound_terminal; session_id={sessionId}; transfer_id={transferId}; " +
            $"state={(recovered ? "Completed" : "Failed")}; error_code={(recovered ? "(none)" : "route_cycle_missing")}; " +
            "log_writer_id=writer_a; log_writer_sequence=19",
            secondsOffset: 19));
        return lines.ToArray();
    }

    private static string[] BuildFallbackTailCoordinatorAggregateFixture(
        bool peerFirst,
        bool duplicateEvidence = false,
        bool includeStaleRejectedReceipt = false,
        bool mutatePairing = false,
        bool mutatePeerTuple = false,
        bool includeConsumption = true,
        bool mutatePairingProofIdentity = false,
        bool duplicateReservation = false,
        bool mutateReservation = false,
        bool phase2StageOnly = false)
    {
        const string sessionId = "sess_fallback_tail_aggregate";
        const string transferId = "transfer_fallback_tail_aggregate";
        const string localAuthorityId =
            "fta2-fallback_tail_yield-0123456789abcdef01234567";
        const string peerAuthorityId =
            "fta2-fallback_tail_yield-89abcdef0123456701234567";
        const string retryAuthorityId =
            "fta2-runtime-unlock-retry-0123456789abcdef01234567";
        const string ownerKey =
            "sess_fallback_tail_aggregate|transfer_fallback_tail_aggregate|post_tuna_fallback_v6|6|2|3|1|2|(none)";
        const string pairKey =
            "sess_fallback_tail_aggregate|transfer_fallback_tail_aggregate|post_tuna_fallback_v6|6|2|2|(none)";
        var common =
            $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
            "route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; " +
            "route_publication_generation=5; route_commit_generation=0; " +
            "runtime_generation=3; terminal_generation=0; " +
            "leg_generation=3; transport_epoch=2; bridge_recovery_generation=1; " +
            "checkpoint_generation=0; checkpoint_request_id=(none); " +
            "data_session_identity=data-session-1; " +
            $"data_session_generation=1; authority_id={localAuthorityId}; " +
            "predecessor_authority_id=(none); yield_generation=1; " +
            $"owner_key={ownerKey}; pair_key={pairKey}";
        var lines = new List<string>
        {
            LogLine(
                $"event=filetransfer_route_selected; {common}; route_commit_generation=0; " +
                "target_route=post_tuna_fallback_v6; target_protocol_version=6; " +
                "target_transport=regular_nkn; route_publication_action_kind=RoutePublication; " +
                "coordinator_decision_reason=accepted",
                secondsOffset: 0),
            LogLine(
                $"event=filetransfer_fallback_tail_yield_issued; {common}; " +
                "decision_status=Accepted; decision_reason=accepted",
                secondsOffset: 1)
        };
        var localReceipt = LogLine(
            $"event=filetransfer_fallback_tail_local_proof_receipt_decided; {common}; " +
            "local_proof_identity=local-proof-1; local_frame_fingerprint=local-frame-1; " +
            "decision_status=Applied; decision_reason=accepted",
            secondsOffset: peerFirst ? 3 : 2);
        var peerReceipt = LogLine(
            $"event=filetransfer_fallback_tail_peer_proof_receipt_decided; {common}; " +
            "peer_observation_identity=peer-proof-1; peer_frame_fingerprint=peer-frame-1; " +
            $"peer_authority_id={peerAuthorityId}; peer_predecessor_authority_id=(none); " +
            "peer_yield_generation=1; peer_direction=inbound; " +
            $"peer_owner_key={ownerKey}; peer_pair_key={pairKey}; " +
            "peer_live_route_epoch=2; peer_leg_generation=3; " +
            $"peer_bridge_recovery_generation={(mutatePeerTuple ? 9 : 1)}; " +
            "peer_transport_epoch=2; peer_checkpoint_request_id=(none); " +
            "decision_status=Applied; decision_reason=accepted",
            secondsOffset: peerFirst ? 2 : 3);
        lines.Add(peerFirst ? peerReceipt : localReceipt);
        lines.Add(peerFirst ? localReceipt : peerReceipt);
        if (includeStaleRejectedReceipt)
        {
            lines.Add(LogLine(
                $"event=filetransfer_fallback_tail_local_proof_receipt_decided; {common}; " +
                "local_proof_identity=stale-local-proof; local_frame_fingerprint=stale-local-frame; " +
                "decision_status=Rejected; decision_reason=proof_scope_stale",
                secondsOffset: 4));
        }

        var pairingOwnerKey = mutatePairing ? ownerKey + "-mutated" : ownerKey;
        var pairingLocalProofIdentity = mutatePairingProofIdentity
            ? "local-proof-mutated"
            : "local-proof-1";
        var pairing =
            LogLine(
                 $"event=filetransfer_fallback_tail_pairing_committed; {common.Replace($"owner_key={ownerKey}", $"owner_key={pairingOwnerKey}", StringComparison.Ordinal)}; " +
                 $"local_proof_identity={pairingLocalProofIdentity}; local_frame_fingerprint=local-frame-1; " +
                 "peer_observation_identity=peer-proof-1; peer_frame_fingerprint=peer-frame-1; " +
                 $"peer_authority_id={peerAuthorityId}; peer_predecessor_authority_id=(none); " +
                 "peer_yield_generation=1; peer_direction=inbound; " +
                 $"peer_owner_key={ownerKey}; peer_pair_key={pairKey}; " +
                 "peer_live_route_epoch=2; peer_leg_generation=3; " +
                 "peer_bridge_recovery_generation=1; peer_transport_epoch=2; " +
                 "peer_checkpoint_request_id=(none); pairing_generation=1; " +
                 "release_kind=FallbackPairedTailYield; release_outcome=Quiescent; " +
                "decision_status=Accepted; decision_reason=accepted",
                secondsOffset: 5);
        lines.Add(pairing);
        if (duplicateEvidence)
        {
            lines.Add(localReceipt);
            lines.Add(peerReceipt);
            lines.Add(pairing);
        }

        if (!includeConsumption)
        {
            lines.AddRange(FallbackTailCoordinatorTerminalLines(sessionId, transferId));
            return lines.ToArray();
        }

        var reservationCommon = mutateReservation
            ? common.Replace($"owner_key={ownerKey}", $"owner_key={ownerKey}-mutated", StringComparison.Ordinal)
            : common;
        var reservation = LogLine(
            $"event=filetransfer_fallback_tail_paired_release_reserved; {reservationCommon}; " +
            "pairing_generation=1; release_kind=FallbackPairedTailYield; " +
            "release_outcome=Quiescent; reservation_generation=1; " +
            $"retry_authority_id={retryAuthorityId}; retry_attempt_generation=1; " +
            "install_generation=0; decision_status=Accepted; decision_reason=accepted",
            secondsOffset: 6);
        lines.Add(reservation);
        if (duplicateReservation)
        {
            lines.Add(reservation);
        }

        var installLines = BuildFallbackTailCoordinatorPhase2InstallLines(
            sessionId,
            transferId,
            localAuthorityId,
            retryAuthorityId).ToArray();
        if (phase2StageOnly)
        {
            lines.Add(installLines[0]);
            lines.AddRange(FallbackTailCoordinatorTerminalLines(sessionId, transferId));
            return lines.ToArray();
        }

        lines.AddRange(installLines);
        var consumption = LogLine(
            $"event=filetransfer_fallback_tail_paired_release_consumed; {common}; " +
            "pairing_generation=1; release_kind=FallbackPairedTailYield; " +
            "release_outcome=Quiescent; reservation_generation=1; " +
            $"retry_authority_id={retryAuthorityId}; retry_attempt_generation=1; " +
            "install_generation=7; decision_status=Accepted; decision_reason=accepted",
            secondsOffset: 12);
        lines.Add(consumption);
        if (duplicateEvidence)
        {
            lines.Add(consumption);
        }
        lines.AddRange(FallbackTailCoordinatorTerminalLines(sessionId, transferId));
        return lines.ToArray();
    }

    private static string[] BuildFallbackTailCoordinatorCarriedPeerProofFixture(
        bool redactNestedAuthorities)
    {
        const string sessionId = "sess_fallback_tail_carried_peer";
        const string transferId = "transfer_fallback_tail_carried_peer";
        const string failedAuthorityId =
            "fta2-fallback-tail-yield-aaaaaaaaaaaaaaaaaaaaaaaa";
        const string successorAuthorityId =
            "fta2-fallback-tail-yield-bbbbbbbbbbbbbbbbbbbbbbbb";
        const string peerAuthorityId =
            "fta2-fallback-tail-yield-cccccccccccccccccccccccc";
        const string retryAuthorityId =
            "fta2-runtime-unlock-retry-dddddddddddddddddddddddd";
        const string ownerKey =
            "sess_fallback_tail_carried_peer|transfer_fallback_tail_carried_peer|post_tuna_fallback_v6|6|2|3|1|2|(none)";
        const string pairKey =
            "sess_fallback_tail_carried_peer|transfer_fallback_tail_carried_peer|post_tuna_fallback_v6|6|2|2|(none)";
        var scope =
            $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
            "route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; " +
            "route_publication_generation=5; route_commit_generation=0; " +
            "runtime_generation=3; terminal_generation=0; leg_generation=3; " +
            "transport_epoch=2; bridge_recovery_generation=1; " +
            "checkpoint_generation=0; checkpoint_request_id=(none); " +
            "data_session_identity=data-session-1; data_session_generation=1; " +
            $"owner_key={ownerKey}; pair_key={pairKey}";
        var failed =
            $"{scope}; authority_id={failedAuthorityId}; predecessor_authority_id=(none); " +
            "yield_generation=1; yield_kind=Failed; " +
            "failed_predecessor_authority_id=(none); failed_predecessor_yield_generation=0; " +
            "failed_successor_authority_id=(none); failed_successor_yield_generation=0; " +
            "failed_supersession_carried_peer_proof=0";
        var nestedFailedAuthorityId = redactNestedAuthorities
            ? "[redacted]"
            : failedAuthorityId;
        var nestedSuccessorAuthorityId = redactNestedAuthorities
            ? "[redacted]"
            : successorAuthorityId;
        var peerAuthority = redactNestedAuthorities ? "[redacted]" : peerAuthorityId;
        var successor =
            $"{scope}; authority_id={successorAuthorityId}; predecessor_authority_id=(none); " +
            "yield_generation=2; yield_kind=Quiescent; " +
            $"failed_predecessor_authority_id={nestedFailedAuthorityId}; " +
            "failed_predecessor_yield_generation=1; " +
            $"failed_successor_authority_id={nestedSuccessorAuthorityId}; " +
            "failed_successor_yield_generation=2; failed_supersession_carried_peer_proof=1";
        var peerTuple =
            "peer_observation_identity=peer-proof-1; peer_frame_fingerprint=peer-frame-1; " +
            $"peer_authority_id={peerAuthority}; peer_predecessor_authority_id=(none); " +
            "peer_yield_generation=1; peer_direction=inbound; " +
            $"peer_owner_key={ownerKey}; peer_pair_key={pairKey}; " +
            "peer_live_route_epoch=2; peer_leg_generation=3; " +
            "peer_bridge_recovery_generation=1; peer_transport_epoch=2; " +
            "peer_checkpoint_request_id=(none)";
        var peerReceipt = LogLine(
            $"event=filetransfer_fallback_tail_peer_proof_receipt_decided; {failed}; " +
            $"{peerTuple}; proof_identity=peer-proof-1; frame_fingerprint=peer-frame-1; " +
            "decision_status=Accepted; decision_reason=fallback_tail_peer_proof_receipt_applied; " +
            "log_writer_id=writer_a",
            secondsOffset: 2);
        var lines = new List<string>
        {
            LogLine(
                $"event=filetransfer_route_selected; {scope}; " +
                "target_route=post_tuna_fallback_v6; target_protocol_version=6; " +
                "target_transport=regular_nkn; route_publication_action_kind=RoutePublication; " +
                "coordinator_decision_reason=accepted; log_writer_id=writer_a",
                secondsOffset: 0),
            LogLine(
                $"event=filetransfer_fallback_tail_yield_issued; {failed}; " +
                "decision_status=Accepted; decision_reason=missing_frontier_state; " +
                "log_writer_id=writer_a",
                secondsOffset: 1),
            peerReceipt,
            peerReceipt.Replace(
                "decision_status=Accepted; decision_reason=fallback_tail_peer_proof_receipt_applied",
                "decision_status=Idempotent; decision_reason=fallback_tail_peer_proof_receipt_idempotent",
                StringComparison.Ordinal),
            LogLine(
                $"event=filetransfer_fallback_tail_yield_issued; {successor}; " +
                "decision_status=Accepted; decision_reason=receiver_state_sparse_runtime; " +
                "log_writer_id=writer_a",
                secondsOffset: 4),
            LogLine(
                $"event=filetransfer_fallback_tail_failed_yield_superseded; {successor}; " +
                "decision_status=Accepted; decision_reason=receiver_state_sparse_runtime; " +
                "log_writer_id=writer_a",
                secondsOffset: 5),
            LogLine(
                $"event=filetransfer_fallback_tail_local_proof_receipt_decided; {successor}; " +
                "local_proof_identity=local-proof-2; local_frame_fingerprint=local-frame-2; " +
                "decision_status=Accepted; decision_reason=fallback_tail_local_proof_receipt_applied; " +
                "log_writer_id=writer_a",
                secondsOffset: 6),
            LogLine(
                $"event=filetransfer_fallback_tail_pairing_committed; {successor}; " +
                "local_proof_identity=local-proof-2; local_frame_fingerprint=local-frame-2; " +
                $"{peerTuple}; pairing_generation=1; pairing_state=Paired; " +
                "release_kind=FallbackPairedTailYield; release_outcome=Quiescent; " +
                "decision_status=Accepted; decision_reason=fallback_tail_yield_proof_delivered; " +
                "log_writer_id=writer_a",
                secondsOffset: 7),
            LogLine(
                $"event=filetransfer_fallback_tail_paired_release_reserved; {successor}; " +
                "pairing_generation=1; release_kind=FallbackPairedTailYield; " +
                "release_outcome=Quiescent; reservation_generation=1; " +
                $"retry_authority_id={retryAuthorityId}; retry_attempt_generation=1; " +
                "dispatch_attempt_generation=1; install_generation=0; " +
                "decision_status=Accepted; decision_reason=runtime_unlock_retry_lifecycle_transition; " +
                "log_writer_id=writer_a",
                secondsOffset: 8)
        };
        lines.AddRange(BuildFallbackTailCoordinatorPhase2InstallLines(
            sessionId,
            transferId,
            successorAuthorityId,
            retryAuthorityId,
            secondsOffsetBase: 9,
            releaseGeneration: 2));
        lines.Add(LogLine(
            $"event=filetransfer_fallback_tail_paired_release_consumed; {successor}; " +
            "pairing_generation=1; release_kind=FallbackPairedTailYield; " +
            "release_outcome=Quiescent; reservation_generation=1; " +
            $"retry_authority_id={retryAuthorityId}; retry_attempt_generation=1; " +
            "dispatch_attempt_generation=1; install_generation=7; " +
            "decision_status=Accepted; decision_reason=runtime_unlock_retry_lifecycle_transition; " +
            "log_writer_id=writer_a",
            secondsOffset: 13));
        lines.AddRange(FallbackTailCoordinatorTerminalLines(sessionId, transferId));
        return lines.ToArray();
    }

    private static string[] BuildFallbackTailCoordinatorCarryForwardFixture()
    {
        const string sessionId = "sess_fallback_tail_carry_forward";
        const string transferId = "transfer_fallback_tail_carry_forward";
        const string localAuthorityId =
            "fta2-fallback_tail_yield-0123456789abcdef01234567";
        const string peerAuthorityId =
            "fta2-fallback_tail_yield-89abcdef0123456701234567";
        const string retryAuthorityId =
            "fta2-runtime-unlock-retry-0123456789abcdef01234567";
        const string sourceOwnerKey =
            "sess_fallback_tail_carry_forward|transfer_fallback_tail_carry_forward|post_tuna_fallback_v6|6|2|3|1|2|(none)";
        const string sourcePairKey =
            "sess_fallback_tail_carry_forward|transfer_fallback_tail_carry_forward|post_tuna_fallback_v6|6|2|2|(none)";
        const string targetOwnerKey =
            "sess_fallback_tail_carry_forward|transfer_fallback_tail_carry_forward|post_tuna_fallback_v6|6|2|3|2|3|(none)";
        const string targetPairKey =
            "sess_fallback_tail_carry_forward|transfer_fallback_tail_carry_forward|post_tuna_fallback_v6|6|2|3|(none)";
        var source =
             $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
             "route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; " +
             "route_publication_generation=5; route_commit_generation=0; " +
             "runtime_generation=3; terminal_generation=0; " +
             "leg_generation=3; transport_epoch=2; bridge_recovery_generation=1; " +
             "checkpoint_generation=0; checkpoint_request_id=(none); " +
             "data_session_identity=data-session-1; " +
            $"data_session_generation=1; authority_id={localAuthorityId}; " +
            "predecessor_authority_id=(none); yield_generation=1; " +
            $"owner_key={sourceOwnerKey}; pair_key={sourcePairKey}";
        var target =
             $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
             "route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; " +
             "route_publication_generation=5; route_commit_generation=0; " +
             "runtime_generation=3; terminal_generation=0; " +
             "leg_generation=3; transport_epoch=3; bridge_recovery_generation=2; " +
             "checkpoint_generation=0; checkpoint_request_id=(none); " +
             "data_session_identity=data-session-1; " +
            $"data_session_generation=1; authority_id={localAuthorityId}; " +
            "predecessor_authority_id=(none); yield_generation=1; " +
            $"owner_key={targetOwnerKey}; pair_key={targetPairKey}";
        var lines = new List<string>
        {
            LogLine(
                $"event=filetransfer_fallback_tail_yield_issued; {source}; " +
                "decision_status=Accepted; decision_reason=accepted",
                secondsOffset: 1),
            LogLine(
                $"event=filetransfer_fallback_tail_local_proof_receipt_decided; {source}; " +
                "local_proof_identity=local-proof-1; local_frame_fingerprint=local-frame-1; " +
                "decision_status=Applied; decision_reason=accepted",
                secondsOffset: 2),
            LogLine(
                 $"event=filetransfer_fallback_tail_peer_proof_receipt_decided; {source}; " +
                 "peer_observation_identity=peer-proof-1; peer_frame_fingerprint=peer-frame-1; " +
                 $"peer_authority_id={peerAuthorityId}; peer_predecessor_authority_id=(none); " +
                 "peer_yield_generation=1; peer_direction=inbound; " +
                 $"peer_owner_key={sourceOwnerKey}; peer_pair_key={sourcePairKey}; " +
                 "peer_live_route_epoch=2; peer_leg_generation=3; " +
                 "peer_bridge_recovery_generation=1; peer_transport_epoch=2; " +
                 "peer_checkpoint_request_id=(none); " +
                 "decision_status=Applied; decision_reason=accepted",
                secondsOffset: 3),
            LogLine(
                 $"event=filetransfer_fallback_tail_pairing_committed; {source}; " +
                 "local_proof_identity=local-proof-1; local_frame_fingerprint=local-frame-1; " +
                 "peer_observation_identity=peer-proof-1; peer_frame_fingerprint=peer-frame-1; " +
                 $"peer_authority_id={peerAuthorityId}; peer_predecessor_authority_id=(none); " +
                 "peer_yield_generation=1; peer_direction=inbound; " +
                 $"peer_owner_key={sourceOwnerKey}; peer_pair_key={sourcePairKey}; " +
                 "peer_live_route_epoch=2; peer_leg_generation=3; " +
                 "peer_bridge_recovery_generation=1; peer_transport_epoch=2; " +
                 "peer_checkpoint_request_id=(none); pairing_generation=1; " +
                "release_kind=FallbackPairedTailYield; release_outcome=Quiescent; " +
                "decision_status=Accepted; decision_reason=accepted",
                secondsOffset: 4),
            LogLine(
                $"event=filetransfer_fallback_tail_paired_release_carried_forward; {target}; " +
                "pairing_generation=1; release_kind=FallbackPairedTailYield; " +
                "release_outcome=Quiescent; carry_forward_source_transport_epoch=2; " +
                "carry_forward_target_transport_epoch=3; " +
                $"carry_forward_source_owner_key={sourceOwnerKey}; " +
                $"carry_forward_target_owner_key={targetOwnerKey}; " +
                $"carry_forward_source_pair_key={sourcePairKey}; " +
                $"carry_forward_target_pair_key={targetPairKey}; " +
                $"historical_release_authority_id={localAuthorityId}; " +
                "historical_release_generation=1; decision_status=Accepted; " +
                "decision_reason=paired_yield_same_leg_transport_carry_forward",
                secondsOffset: 5),
            LogLine(
                $"event=filetransfer_fallback_tail_paired_release_reserved; {target}; " +
                "pairing_generation=1; release_kind=FallbackPairedTailYield; " +
                "release_outcome=Quiescent; reservation_generation=1; " +
                $"retry_authority_id={retryAuthorityId}; retry_attempt_generation=1; " +
                "install_generation=0; decision_status=Accepted; decision_reason=accepted",
                secondsOffset: 6)
        };
        lines.AddRange(
            BuildFallbackTailCoordinatorPhase2InstallLines(
                sessionId,
                transferId,
                localAuthorityId,
                retryAuthorityId,
                transportEpoch: 3,
                bridgeRecoveryGeneration: 2));
        lines.Add(LogLine(
            $"event=filetransfer_fallback_tail_paired_release_consumed; {target}; " +
            "pairing_generation=1; release_kind=FallbackPairedTailYield; " +
            "release_outcome=Quiescent; reservation_generation=1; " +
            $"retry_authority_id={retryAuthorityId}; retry_attempt_generation=1; " +
            "install_generation=7; decision_status=Accepted; decision_reason=accepted",
            secondsOffset: 12));
        lines.AddRange(FallbackTailCoordinatorTerminalLines(sessionId, transferId));
        return lines.ToArray();
    }

    private static IEnumerable<string> BuildFallbackTailCoordinatorPhase2InstallLines(
        string sessionId,
        string transferId,
        string releaseAuthorityId,
        string retryAuthorityId,
        int transportEpoch = 2,
        int bridgeRecoveryGeneration = 1,
        int secondsOffsetBase = 7,
        int releaseGeneration = 1)
    {
        var authority =
            $"authority_schema=2; authority_id={retryAuthorityId}; authority_generation=1; " +
            $"predecessor_authority_id=(none); session_id={sessionId}; transfer_id={transferId}; " +
            "direction=outbound; route=post_tuna_fallback_v6; route_kind=PostTunaFallbackV6; " +
            "protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; " +
            $"leg_generation=3; transport_epoch={transportEpoch}; " +
            $"bridge_recovery_generation={bridgeRecoveryGeneration}; " +
            "bridge_decision_generation=0; route_publication_generation=5; route_commit_generation=0; " +
            "contract_generation=1; retired_generation=1; dispatch_attempt_generation=1; " +
            "missing_dispatch_successor_generation=0; missing_dispatch_successor_limit=1; " +
            "retry_reason=fallback_tail_paired_yield; recovery_reason=fallback_tail_paired_yield; " +
            $"release_kind=FallbackPairedTailYield; release_authority_id={releaseAuthorityId}; " +
            $"release_generation={releaseGeneration}; release_outcome=Quiescent; target_route=file_tuna_v4; " +
            "target_route_kind=FileTunaV4; target_protocol_version=4; target_transport=tuna; " +
            $"runtime_owner_session_id={sessionId}; runtime_owner_transfer_id={transferId}; " +
            "runtime_owner_direction=outbound; runtime_owner_route=post_tuna_fallback_v6; " +
            "runtime_owner_route_kind=PostTunaFallbackV6; runtime_owner_protocol_version=6; " +
            "runtime_owner_live_route_epoch=2; runtime_owner_runtime_generation=3; " +
            "runtime_owner_terminal_generation=0; runtime_owner_family=post_tuna_fallback_v6; " +
            "runtime_owner_created_reason=fallback_route_current; " +
            $"source_publication_session_id={sessionId}; source_publication_transfer_id={transferId}; " +
            "source_publication_direction=outbound; source_publication_route=post_tuna_fallback_v6; " +
            "source_publication_route_kind=PostTunaFallbackV6; source_publication_protocol_version=6; " +
            "source_publication_live_route_epoch=2; source_publication_runtime_generation=3; " +
            "source_publication_terminal_generation=0; source_publication_leg_generation=3; " +
            "source_publication_route_commit_generation=0; source_publication_target_route=post_tuna_fallback_v6; " +
            "source_publication_target_route_kind=PostTunaFallbackV6; " +
            "source_publication_target_protocol_version=6; source_publication_target_transport=regular_nkn; " +
            "source_publication_generation=5; source_publication_action_kind=RoutePublication; " +
            "source_publication_reason=accepted; " +
            $"source_publication_runtime_owner_session_id={sessionId}; " +
            $"source_publication_runtime_owner_transfer_id={transferId}; " +
            "source_publication_runtime_owner_direction=outbound; " +
            "source_publication_runtime_owner_route=post_tuna_fallback_v6; " +
            "source_publication_runtime_owner_route_kind=PostTunaFallbackV6; " +
            "source_publication_runtime_owner_protocol_version=6; " +
            "source_publication_runtime_owner_live_route_epoch=2; " +
            "source_publication_runtime_owner_runtime_generation=3; " +
            "source_publication_runtime_owner_terminal_generation=0; " +
            "source_publication_runtime_owner_family=post_tuna_fallback_v6; " +
            "source_publication_runtime_owner_created_reason=fallback_route_current; " +
            "request_regular_v4_liveness_generation=0; request_bridge_decision_generation=0; " +
            "request_active_predecessor_supersession=0";
        return
        [
            LogLine(
                $"event=runtime_unlock_retry_dispatch_decision_staged; {authority}; " +
                "install_generation=0; stage_status=Staged; decision_status=Granted; " +
                "decision_reason=retry_dispatch_decision_staged",
                secondsOffset: secondsOffsetBase),
            LogLine(
                $"event=runtime_unlock_retry_dispatch_install_receipt_issued; {authority}; " +
                "install_generation=7; decision_status=Granted; " +
                "decision_reason=retry_dispatch_install_receipt_issued",
                secondsOffset: secondsOffsetBase + 1),
            LogLine(
                $"event=runtime_unlock_retry_dispatch_install_committed; {authority}; " +
                "install_generation=7; install_status=Installed; decision_status=Granted; " +
                "decision_reason=retry_dispatch_install_committed",
                secondsOffset: secondsOffsetBase + 2),
            LogLine(
                $"event=runtime_unlock_retry_dispatch_authority_granted; {authority}; " +
                "install_generation=7; install_status=Installed; decision_status=Granted; " +
                "decision_reason=runtime_unlock_retry_dispatch_authority_granted",
                secondsOffset: secondsOffsetBase + 3)
        ];
    }

    private static string[] BuildFallbackTailCoordinatorEpochRebindFixture(
        bool inventIntermediateSuccessor,
        bool mutateReboundDescriptor = false,
        bool mutateSharedPredecessorLineage = false,
        bool mutateSuccessorTargetTuple = false)
    {
        const string sessionId = "sess_fallback_tail_epoch_rebind";
        const string transferId = "transfer_fallback_tail_epoch_rebind";
        const string predecessorAuthorityId =
            "fta2-fallback_tail_yield-0123456789abcdef01234567";
        const string epoch2AuthorityId =
            "fta2-fallback_tail_yield-222222222222222222222222";
        const string epoch3AuthorityId =
            "fta2-fallback_tail_yield-333333333333333333333333";
        var lines = new List<string>
        {
            FallbackTailCoordinatorYieldIssuedLine(
                sessionId,
                transferId,
                predecessorAuthorityId,
                predecessorAuthorityId: "(none)",
                yieldGeneration: 1,
                transportEpoch: 1,
                runtimeGeneration: 3,
                dataSessionGeneration: 1,
                secondsOffset: 1),
            LogLine(
                $"event=filetransfer_fallback_tail_transport_epoch_reauthorization_staged; session_id={sessionId}; " +
                $"transfer_id={transferId}; direction=outbound; predecessor_authority_id={predecessorAuthorityId}; " +
                "reauthorization_generation=1; source_transport_epoch=1; target_transport_epoch=2; " +
                FallbackTailCoordinatorReauthorizationScope(
                    sessionId,
                    transferId,
                    runtimeGeneration: 4,
                    dataSessionGeneration: 2,
                    targetTransportEpoch: 2) +
                "; decision_status=Accepted; decision_reason=accepted",
                secondsOffset: 2),
            LogLine(
                $"event=filetransfer_fallback_tail_transport_epoch_reauthorization_rebound; session_id={sessionId}; " +
                $"transfer_id={transferId}; direction=outbound; predecessor_authority_id={predecessorAuthorityId}; " +
                "reauthorization_generation=2; source_transport_epoch=1; target_transport_epoch=3; " +
                FallbackTailCoordinatorReauthorizationScope(
                    sessionId,
                    transferId,
                    runtimeGeneration: 5,
                    dataSessionGeneration: 3,
                    targetTransportEpoch: 3,
                    checkpointGeneration:
                        mutateReboundDescriptor ? 9 : 0,
                    predecessorYieldGeneration:
                        mutateSharedPredecessorLineage ? 9 : 1) +
                "; decision_status=Accepted; decision_reason=accepted",
                secondsOffset: 3)
        };
        if (inventIntermediateSuccessor)
        {
            AppendFallbackTailCoordinatorSuccessorPair(
                lines,
                sessionId,
                transferId,
                predecessorAuthorityId,
                epoch2AuthorityId,
                targetEpoch: 2,
                yieldGeneration: 2,
                runtimeGeneration: 4,
                dataSessionGeneration: 2,
                secondsOffset: 4);
        }
        AppendFallbackTailCoordinatorSuccessorPair(
            lines,
            sessionId,
            transferId,
            predecessorAuthorityId,
            epoch3AuthorityId,
            targetEpoch: 3,
            yieldGeneration: 2,
            runtimeGeneration: 5,
            dataSessionGeneration: 3,
            secondsOffset: inventIntermediateSuccessor ? 9 : 4,
            predecessorYieldGeneration:
                mutateSharedPredecessorLineage ? 9 : 1,
            mutateSuccessorTargetTuple: mutateSuccessorTargetTuple);
        lines.AddRange(FallbackTailCoordinatorTerminalLines(sessionId, transferId));
        return lines.ToArray();
    }

    private static void AppendFallbackTailCoordinatorSuccessorPair(
        List<string> lines,
        string sessionId,
        string transferId,
        string predecessorAuthorityId,
        string successorAuthorityId,
        int targetEpoch,
        int yieldGeneration,
        int runtimeGeneration,
        int dataSessionGeneration,
        int secondsOffset,
        int predecessorYieldGeneration = 1,
        bool mutateSuccessorTargetTuple = false)
    {
        var peerAuthorityId = targetEpoch == 2
            ? "fta2-fallback_tail_yield-aaaaaaaaaaaaaaaaaaaaaa22"
            : "fta2-fallback_tail_yield-bbbbbbbbbbbbbbbbbbbbbb33";
        var reauthorizationGeneration = targetEpoch == 2 ? 1 : 2;
        lines.Add(FallbackTailCoordinatorYieldIssuedLine(
            sessionId,
            transferId,
            successorAuthorityId,
            predecessorAuthorityId,
            yieldGeneration,
            targetEpoch,
            runtimeGeneration,
            dataSessionGeneration,
            secondsOffset));
        var common = FallbackTailCoordinatorScope(
            sessionId,
            transferId,
            successorAuthorityId,
            predecessorAuthorityId,
            yieldGeneration,
            targetEpoch,
            runtimeGeneration,
            dataSessionGeneration);
        var ownerKey =
            $"{sessionId}|{transferId}|post_tuna_fallback_v6|6|2|3|1|{targetEpoch}|(none)";
        var pairKey =
            $"{sessionId}|{transferId}|post_tuna_fallback_v6|6|2|{targetEpoch}|(none)";
        var successorOwnerKey = mutateSuccessorTargetTuple
            ? ownerKey + "-mutated"
            : ownerKey;
        lines.Add(LogLine(
             $"event=filetransfer_fallback_tail_transport_epoch_reauthorization_successor_issued; " +
             $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
             $"predecessor_authority_id={predecessorAuthorityId}; successor_authority_id={successorAuthorityId}; " +
             $"successor_yield_generation={yieldGeneration}; successor_owner_key={successorOwnerKey}; " +
             $"successor_pair_key={pairKey}; successor_route={(int)FileTransferRoute.PostTunaFallbackV6}; " +
             $"successor_protocol_version=6; successor_live_route_epoch=2; " +
             $"successor_runtime_generation={runtimeGeneration}; successor_terminal_generation=0; " +
             $"successor_leg_generation=3; successor_bridge_recovery_generation=1; " +
             "successor_checkpoint_request_id=(none); " +
             $"reauthorization_generation={reauthorizationGeneration}; source_transport_epoch=1; target_transport_epoch={targetEpoch}; " +
             FallbackTailCoordinatorReauthorizationScope(
                 sessionId,
                 transferId,
                 runtimeGeneration,
                 dataSessionGeneration,
                 targetEpoch,
                 predecessorYieldGeneration: predecessorYieldGeneration) +
             "; decision_status=Accepted; decision_reason=accepted",
             secondsOffset: secondsOffset + 1));
        lines.Add(LogLine(
            $"event=filetransfer_fallback_tail_local_proof_receipt_decided; {common}; " +
            $"local_proof_identity=local-{targetEpoch}; local_frame_fingerprint=local-frame-{targetEpoch}; " +
            "decision_status=Applied; decision_reason=accepted",
            secondsOffset: secondsOffset + 2));
        lines.Add(LogLine(
            $"event=filetransfer_fallback_tail_peer_proof_receipt_decided; {common}; " +
            $"peer_observation_identity=peer-{targetEpoch}; peer_frame_fingerprint=peer-frame-{targetEpoch}; " +
             $"peer_authority_id={peerAuthorityId}; " +
             $"peer_predecessor_authority_id=(none); peer_yield_generation={yieldGeneration}; " +
             $"peer_direction=inbound; peer_owner_key={ownerKey}; peer_pair_key={pairKey}; " +
             $"peer_live_route_epoch=2; peer_leg_generation=3; peer_bridge_recovery_generation=1; " +
             $"peer_transport_epoch={targetEpoch}; peer_checkpoint_request_id=(none); " +
             "decision_status=Applied; decision_reason=accepted",
             secondsOffset: secondsOffset + 3));
        lines.Add(LogLine(
            $"event=filetransfer_fallback_tail_pairing_committed; {common}; " +
             $"local_proof_identity=local-{targetEpoch}; local_frame_fingerprint=local-frame-{targetEpoch}; " +
             $"peer_observation_identity=peer-{targetEpoch}; peer_frame_fingerprint=peer-frame-{targetEpoch}; " +
             $"peer_authority_id={peerAuthorityId}; " +
             $"peer_predecessor_authority_id=(none); peer_yield_generation={yieldGeneration}; " +
             $"peer_direction=inbound; peer_owner_key={ownerKey}; peer_pair_key={pairKey}; " +
             $"peer_live_route_epoch=2; peer_leg_generation=3; peer_bridge_recovery_generation=1; " +
             $"peer_transport_epoch={targetEpoch}; peer_checkpoint_request_id=(none); " +
             $"pairing_generation={targetEpoch}; release_kind=FallbackPairedTailYield; " +
            "release_outcome=Quiescent; decision_status=Accepted; decision_reason=accepted",
            secondsOffset: secondsOffset + 4));
    }

    private static string FallbackTailCoordinatorYieldIssuedLine(
        string sessionId,
        string transferId,
        string authorityId,
        string predecessorAuthorityId,
        int yieldGeneration,
        int transportEpoch,
        int runtimeGeneration,
        int dataSessionGeneration,
        int secondsOffset)
        => LogLine(
            $"event=filetransfer_fallback_tail_yield_issued; " +
            FallbackTailCoordinatorScope(
                sessionId,
                transferId,
                authorityId,
                predecessorAuthorityId,
                yieldGeneration,
                transportEpoch,
                runtimeGeneration,
                dataSessionGeneration) +
            "; decision_status=Accepted; decision_reason=accepted",
            secondsOffset);

    private static string FallbackTailCoordinatorScope(
        string sessionId,
        string transferId,
        string authorityId,
        string predecessorAuthorityId,
        int yieldGeneration,
        int transportEpoch,
        int runtimeGeneration,
        int dataSessionGeneration)
    {
        var ownerKey =
            $"{sessionId}|{transferId}|post_tuna_fallback_v6|6|2|3|1|{transportEpoch}|(none)";
        var pairKey =
            $"{sessionId}|{transferId}|post_tuna_fallback_v6|6|2|{transportEpoch}|(none)";
        return
            $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; " +
            "route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; " +
            $"route_publication_generation={runtimeGeneration + 2}; runtime_generation={runtimeGeneration}; " +
            "route_commit_generation=0; terminal_generation=0; leg_generation=3; " +
            $"transport_epoch={transportEpoch}; bridge_recovery_generation=1; " +
            "checkpoint_generation=0; checkpoint_request_id=(none); " +
            $"data_session_identity=data-session-{dataSessionGeneration}; " +
            $"data_session_generation={dataSessionGeneration}; authority_id={authorityId}; " +
            $"predecessor_authority_id={predecessorAuthorityId}; yield_generation={yieldGeneration}; " +
             $"owner_key={ownerKey}; pair_key={pairKey}";
    }

    private static string FallbackTailCoordinatorReauthorizationScope(
        string sessionId,
        string transferId,
        int runtimeGeneration,
        int dataSessionGeneration,
        int targetTransportEpoch,
        int bridgeRecoveryGeneration = 1,
        int checkpointGeneration = 0,
        int predecessorYieldGeneration = 1)
    {
        var sourceOwnerKey =
            $"{sessionId}|{transferId}|post_tuna_fallback_v6|6|2|3|1|1|(none)";
        var sourcePairKey =
            $"{sessionId}|{transferId}|post_tuna_fallback_v6|6|2|1|(none)";
        var targetOwnerKey =
            $"{sessionId}|{transferId}|post_tuna_fallback_v6|6|2|3|1|{targetTransportEpoch}|(none)";
        var targetPairKey =
            $"{sessionId}|{transferId}|post_tuna_fallback_v6|6|2|{targetTransportEpoch}|(none)";
        return
            $"source_owner_key={sourceOwnerKey}; source_pair_key={sourcePairKey}; " +
            $"target_owner_key={targetOwnerKey}; target_pair_key={targetPairKey}; " +
            "source_runtime_generation=3; source_route_publication_generation=5; " +
            $"source_route_commit_generation=0; predecessor_yield_generation={predecessorYieldGeneration}; " +
            $"predecessor_route={(int)FileTransferRoute.PostTunaFallbackV6}; " +
            "predecessor_protocol_version=6; predecessor_live_route_epoch=2; " +
            "predecessor_runtime_generation=3; predecessor_terminal_generation=0; " +
            "predecessor_leg_generation=3; predecessor_bridge_recovery_generation=1; " +
            "predecessor_checkpoint_request_id=(none); " +
            $"target_runtime_generation={runtimeGeneration}; terminal_generation=0; " +
            $"leg_generation=3; bridge_recovery_generation={bridgeRecoveryGeneration}; " +
            "bridge_decision_generation=1; " +
            $"target_transport_decision_generation={targetTransportEpoch}; " +
            "target_transport_decision_action=Adopt; " +
            "target_transport_decision_reason=transport_epoch_adopted; " +
            $"target_transport_decision_epoch={targetTransportEpoch}; " +
            $"target_transport_decision_bridge_recovery_generation={bridgeRecoveryGeneration}; " +
            $"route_publication_generation={runtimeGeneration + 2}; route_commit_generation=0; " +
            $"checkpoint_request_id=(none); checkpoint_generation={checkpointGeneration}; " +
            "checkpoint_lease_generation=0; checkpoint_lease_state=(none); " +
            "checkpoint_lease_request_id=(none); checkpoint_lease_reason=(none); " +
            $"data_session_identity=data-session-{dataSessionGeneration}; " +
            $"data_session_generation={dataSessionGeneration}";
    }

    private static IEnumerable<string> FallbackTailCoordinatorTerminalLines(
        string sessionId,
        string transferId)
        =>
        [
            LogLine(
                $"event=file_transfer_outbound_terminal; role=helpee; session_id={sessionId}; " +
                $"transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1",
                secondsOffset: 30),
            LogLine(
                $"event=file_transfer_inbound_terminal; role=helper; session_id={sessionId}; " +
                $"transfer_id={transferId}; state=Completed; error_code=(none); " +
                "saved_path=x; integrity_ok=1",
                secondsOffset: 30)
        ];

    private static string[] BuildFallbackTailTransportEpochReauthorizationFixture(
        string recoveryKind,
        bool duplicateSuppression = false,
        bool sameOwnerSuppression = false,
        bool legacySuppression = false)
    {
        const string transferId = "transfer_fallback_transport_reauthorization";
        const string predecessorAuthorityId = "fta2-fallback_tail_yield-0123456789abcdef01234567";
        const string successorAuthorityId = "fta2-fallback_tail_yield-fedcba987654321001234567";
        const string peerAuthorityId = "fta2-fallback_tail_yield-13579bdf2468ace013579bdf";
        var sourceOwner = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|3|1|1|(none)";
        var sourcePair = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|1|(none)";
        var currentOwner = sameOwnerSuppression
            ? sourceOwner
            : $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|3|1|2|(none)";
        var currentEpoch = sameOwnerSuppression ? 1 : 2;
        var targetOwner = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|4|2|2|(none)";
        var targetPair = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|2|(none)";
        var lines = new List<string>
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; route_publication_generation=5; runtime_generation=3; terminal_generation=0; leg_generation=3; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 0),
            LogLine($"event=filetransfer_fallback_tail_recovery_owner_retired_after_yield; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=1; reason=fallback_tail_quiescent", secondsOffset: 1),
            LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=outbound; authority_schema=2; authority_id={predecessorAuthorityId}; predecessor_authority_id=(none); transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; transport_epoch=1; bridge_recovery_generation=1; checkpoint_request_id=(none); owner_key={sourceOwner}; pair_key={sourcePair}; yield_generation=1; committed_chunk=1353; highest_observed_chunk=1352; reason=receiver_state_sparse_runtime", secondsOffset: 2)
        };
        if (!sameOwnerSuppression)
        {
            lines.Add(LogLine($"event=filetransfer_fallback_leg_transport_epoch_adopted; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; previous_transport_epoch=1; transport_epoch=2; bridge_recovery_generation=1; retired_checkpoint_request_id=(none); current_checkpoint_request_id=(none); state=active; can_send_data=1; reason=transport_recovered_unproven", secondsOffset: 3));
        }

        var scopeRelation = sameOwnerSuppression ? "same_owner" : "cross_transport_epoch";
        var ownerRelation = sameOwnerSuppression ? "exact" : "different";
        var decisionReason = sameOwnerSuppression
            ? "retired_owner_exact_scope_update_suppressed"
            : "retired_owner_cross_transport_epoch_suppression";
        var suppression = legacySuppression
            ? LogLine($"event=filetransfer_fallback_tail_retired_owner_update_suppressed; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch={currentEpoch}; bridge_recovery_generation=1; checkpoint_request_id=(none); source=post_tuna_fallback_recovery_restart; attempted_action=start_post_tuna_recovery; owner_key={currentOwner}; reason=post_yield_recovery_restart", secondsOffset: 4)
            : LogLine($"event=filetransfer_fallback_tail_retired_owner_update_suppressed; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch={currentEpoch}; current_transport_epoch={currentEpoch}; retired_transport_epoch=1; bridge_recovery_generation=1; checkpoint_request_id=(none); source=post_tuna_fallback_recovery_restart; attempted_action=start_post_tuna_recovery; owner_key={currentOwner}; current_owner_key={currentOwner}; retired_owner_key={sourceOwner}; owner_key_relation={ownerRelation}; scope_relation={scopeRelation}; reason=post_yield_recovery_restart; decision_reason={decisionReason}", secondsOffset: 4);
        lines.Add(suppression);
        if (duplicateSuppression)
        {
            lines.Add(suppression);
        }

        if (!sameOwnerSuppression && recoveryKind != "none")
        {
            lines.Add(LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; route_publication_generation=6; runtime_generation=4; terminal_generation=0; leg_generation=4; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 6));
            var descriptor = $"authority_schema=2; direction=outbound; session_id=sess_a; transfer_id={transferId}; predecessor_authority_id={predecessorAuthorityId}; predecessor_yield_generation=1; reauthorization_generation=1; source_owner_key={sourceOwner}; source_pair_key={sourcePair}; target_owner_key={targetOwner}; target_pair_key={targetPair}; source_transport_epoch=1; target_transport_epoch=2; source_runtime_generation=3; target_runtime_generation=4; predecessor_route=4; predecessor_protocol_version=6; predecessor_live_route_epoch=2; predecessor_runtime_generation=3; predecessor_terminal_generation=0; predecessor_leg_generation=3; predecessor_bridge_recovery_generation=1; predecessor_checkpoint_request_id=(none); terminal_generation=0; leg_generation=4; bridge_recovery_generation=2; bridge_decision_generation=2; target_transport_decision_generation=2; target_transport_decision_action=Adopt; target_transport_decision_reason=transport_epoch_adopted; target_transport_decision_epoch=2; target_transport_decision_bridge_recovery_generation=2; route_publication_generation=6; route_commit_generation=0; checkpoint_request_id=(none); checkpoint_generation=0; checkpoint_lease_generation=0; checkpoint_lease_state=(none); checkpoint_lease_request_id=(none); checkpoint_lease_reason=(none); data_session_identity=data-session-2; data_session_generation=2";
            lines.Add(LogLine($"event=filetransfer_fallback_tail_transport_epoch_reauthorization_staged; {descriptor}; successor_authority_id=(none); successor_yield_generation=0; successor_owner_key=(none); successor_pair_key=(none); successor_route=0; successor_protocol_version=0; successor_live_route_epoch=0; successor_runtime_generation=0; successor_terminal_generation=0; successor_leg_generation=0; successor_bridge_recovery_generation=0; successor_checkpoint_request_id=(none); reauthorization_reason=transport_epoch_reauthorization; decision_reason=accepted", secondsOffset: 7));
            lines.Add(LogLine($"event=filetransfer_fallback_tail_transport_epoch_reauthorization_successor_issued; {descriptor}; successor_authority_id={successorAuthorityId}; successor_yield_generation=2; successor_owner_key={targetOwner}; successor_pair_key={targetPair}; successor_route=4; successor_protocol_version=6; successor_live_route_epoch=2; successor_runtime_generation=4; successor_terminal_generation=0; successor_leg_generation=4; successor_bridge_recovery_generation=2; successor_checkpoint_request_id=(none); reauthorization_reason=transport_epoch_reauthorization; decision_reason=accepted", secondsOffset: 8));

            if (recoveryKind == "paired")
            {
                var localTuple = $"transfer_id={transferId}; session_id=sess_a; leg_id=leg:4; leg_generation=4; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=4; terminal_generation=0; transport_epoch=2; bridge_recovery_generation=2; checkpoint_request_id=(none); owner_key={targetOwner}; pair_key={targetPair}; yield_generation=2";
                lines.Add(LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=outbound; authority_schema=2; authority_id={successorAuthorityId}; predecessor_authority_id={predecessorAuthorityId}; {localTuple}; committed_chunk=1353; highest_observed_chunk=1352; reason=transport_epoch_reauthorization", secondsOffset: 9));
                lines.Add(LogLine($"event=filetransfer_fallback_tail_yield_proof_sent; direction=outbound; authority_schema=2; authority_id={successorAuthorityId}; predecessor_authority_id={predecessorAuthorityId}; {localTuple}; peer_visible_any=1; accepted_any=1; reason=transport_epoch_reauthorization", secondsOffset: 10));
                lines.Add(LogLine($"event=filetransfer_fallback_tail_yield_proof_received; direction=outbound; authority_schema=2; authority_id={peerAuthorityId}; predecessor_authority_id=(none); peer_direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:4; leg_generation=4; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=4; terminal_generation=0; transport_epoch=2; bridge_recovery_generation=2; checkpoint_request_id=(none); peer_live_route_epoch=2; peer_leg_generation=4; peer_transport_epoch=2; peer_bridge_recovery_generation=2; peer_checkpoint_request_id=(none); owner_key={targetOwner}; pair_key={targetPair}; raw_pair_key={targetPair}; canonical_pair_key={targetPair}; pair_key_format=canonical_7; yield_generation=2", secondsOffset: 11));
                lines.Add(LogLine($"event=filetransfer_fallback_tail_paired_yield_granted; direction=outbound; authority_schema=2; authority_id={successorAuthorityId}; predecessor_authority_id={predecessorAuthorityId}; peer_authority_id={peerAuthorityId}; peer_predecessor_authority_id=(none); peer_yield_generation=2; peer_leg_generation=4; peer_live_route_epoch=2; peer_transport_epoch=2; peer_bridge_recovery_generation=2; peer_checkpoint_request_id=(none); peer_owner_key={targetOwner}; peer_raw_pair_key={targetPair}; peer_canonical_pair_key={targetPair}; peer_pair_key_format=canonical_7; {localTuple}; reason=fallback_tail_paired_yield", secondsOffset: 12));
            }
            else
            {
                var terminalBridgeGeneration = recoveryKind == "terminal_exact" ? 2 : 9;
                var terminalCheckpoint = recoveryKind == "terminal_exact" ? "(none)" : "checkpoint_other";
                lines.Add(LogLine($"event=filetransfer_fallback_tail_terminal_proof_consumed; authority_schema=2; direction=outbound; session_id=sess_a; transfer_id={transferId}; authority_id={successorAuthorityId}; predecessor_authority_id={predecessorAuthorityId}; successor_authority_id={successorAuthorityId}; reauthorization_generation=1; leg_id=leg:4; leg_generation=4; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=4; terminal_generation=0; transport_epoch=2; bridge_recovery_generation={terminalBridgeGeneration}; checkpoint_request_id={terminalCheckpoint}; committed_chunk=1353; chunk_count=1353; reason=fallback_tail_terminal", secondsOffset: 9));
            }
        }

        var completed = recoveryKind is "paired" or "terminal_exact";
        lines.Add(LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state={(completed ? "Completed" : "Failed")}; error_code={(completed ? "(none)" : "route_cycle_missing")}; integrity_ok={(completed ? 1 : 0)}", secondsOffset: 14));
        lines.Add(LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state={(completed ? "Completed" : "Failed")}; error_code={(completed ? "(none)" : "route_cycle_missing")}; saved_path={(completed ? "x" : "(none)")}; integrity_ok={(completed ? 1 : 0)}", secondsOffset: 14));
        return lines.ToArray();
    }

    private static string[] BuildFallbackTailPeerAdoptionReauthorizationFixture(
        bool stageBeforeBlock,
        bool stageAfterBlock = false,
        bool duplicateBlock = false)
    {
        const string transferId = "transfer_fallback_peer_adoption_reauthorization";
        const string predecessorAuthorityId = "fta2-fallback_tail_yield-0123456789abcdef01234567";
        const string peerAuthorityId = "fta2-fallback_tail_yield-13579bdf2468ace013579bdf";
        var sourceOwner = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|3|1|1|(none)";
        var sourcePair = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|1|(none)";
        var currentOwner = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|3|1|2|(none)";
        var currentPair = $"sess_a|{transferId}|post_tuna_fallback_v6|6|2|2|(none)";
        var lines = new List<string>
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; route_publication_generation=5; runtime_generation=3; terminal_generation=0; leg_generation=3; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 0),
            LogLine($"event=filetransfer_fallback_tail_recovery_owner_retired_after_yield; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=1; reason=fallback_tail_quiescent", secondsOffset: 1),
            LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=outbound; authority_schema=2; authority_id={predecessorAuthorityId}; predecessor_authority_id=(none); transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; transport_epoch=1; bridge_recovery_generation=1; checkpoint_request_id=(none); owner_key={sourceOwner}; pair_key={sourcePair}; yield_generation=3; committed_chunk=1897; highest_observed_chunk=2359; reason=receiver_state_sparse_runtime", secondsOffset: 2),
            LogLine($"event=filetransfer_fallback_tail_owner_generation_mismatch; authority_schema=2; authority_id={peerAuthorityId}; predecessor_authority_id=(none); direction=outbound; peer_direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=1; bridge_recovery_generation=1; checkpoint_request_id=(none); peer_live_route_epoch=2; peer_leg_generation=3; peer_transport_epoch=2; peer_bridge_recovery_generation=1; peer_checkpoint_request_id=(none); expected_owner_key={sourceOwner}; expected_pair_key={sourcePair}; peer_owner_key={currentOwner}; peer_pair_key={currentPair}; yield_generation=1; mismatch_kind=pair_scope_mismatch; decision_reason=pair_scope_mismatch", secondsOffset: 3),
            LogLine($"event=filetransfer_fallback_leg_transport_epoch_adopted; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; previous_transport_epoch=1; transport_epoch=2; bridge_recovery_generation=1; retired_checkpoint_request_id=(none); current_checkpoint_request_id=(none); state=active; can_send_data=1; reason=transport_recovered_unproven", secondsOffset: 4),
            LogLine($"event=filetransfer_fallback_tail_yield_proof_received; direction=outbound; authority_schema=2; authority_id={peerAuthorityId}; predecessor_authority_id=(none); peer_direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; peer_leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; peer_live_route_epoch=2; transport_epoch=2; peer_transport_epoch=2; bridge_recovery_generation=1; peer_bridge_recovery_generation=1; checkpoint_request_id=(none); peer_checkpoint_request_id=(none); owner_key={currentOwner}; pair_key={currentPair}; raw_pair_key={currentPair}; canonical_pair_key={currentPair}; pair_key_format=canonical_7; yield_generation=1; committed_chunk=1848; highest_observed_chunk=1847", secondsOffset: 5)
        };

        var descriptor = $"authority_schema=2; direction=outbound; session_id=sess_a; transfer_id={transferId}; predecessor_authority_id={predecessorAuthorityId}; predecessor_yield_generation=3; reauthorization_generation=1; source_owner_key={sourceOwner}; source_pair_key={sourcePair}; target_owner_key={currentOwner}; target_pair_key={currentPair}; source_transport_epoch=1; target_transport_epoch=2; source_runtime_generation=3; target_runtime_generation=3; predecessor_route=4; predecessor_protocol_version=6; predecessor_live_route_epoch=2; predecessor_runtime_generation=3; predecessor_terminal_generation=0; predecessor_leg_generation=3; predecessor_bridge_recovery_generation=1; predecessor_checkpoint_request_id=(none); terminal_generation=0; leg_generation=3; bridge_recovery_generation=1; bridge_decision_generation=2; target_transport_decision_generation=2; target_transport_decision_action=Adopt; target_transport_decision_reason=transport_epoch_adopted; target_transport_decision_epoch=2; target_transport_decision_bridge_recovery_generation=1; route_publication_generation=5; route_commit_generation=0; checkpoint_request_id=(none); checkpoint_generation=0; checkpoint_lease_generation=0; checkpoint_lease_state=(none); checkpoint_lease_request_id=(none); checkpoint_lease_reason=(none); data_session_identity=data-session-2; data_session_generation=2";
        if (stageBeforeBlock)
        {
            lines.Add(LogLine($"event=filetransfer_fallback_tail_transport_epoch_reauthorization_staged; {descriptor}; successor_authority_id=(none); successor_yield_generation=0; successor_owner_key=(none); successor_pair_key=(none); successor_route=0; successor_protocol_version=0; successor_live_route_epoch=0; successor_runtime_generation=0; successor_terminal_generation=0; successor_leg_generation=0; successor_bridge_recovery_generation=0; successor_checkpoint_request_id=(none); reauthorization_reason=transport_epoch_reauthorization; decision_reason=accepted", secondsOffset: 5));
        }

        var blocked = LogLine($"event=filetransfer_fallback_tail_local_yield_blocked; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none); checkpoint_request_id_snapshot=(none); canonical_owner_key={currentOwner}; canonical_pair_key={currentPair}; local_owner_key={sourceOwner}; peer_owner_key={currentOwner}; local_pair_key={sourcePair}; peer_pair_key={currentPair}; decision_reason=fallback_tail_local_owner_work_pending; source=fallback_tail_peer_yield", secondsOffset: 6);
        lines.Add(blocked);
        if (duplicateBlock)
        {
            lines.Add(blocked);
        }

        if (stageAfterBlock)
        {
            const string successorAuthorityId = "fta2-fallback_tail_yield-fedcba987654321001234567";
            const string pairedAuthorityId = "fta2-fallback_tail_yield-2468ace013579bdf2468ace0";
            lines.Add(LogLine($"event=filetransfer_fallback_tail_transport_epoch_reauthorization_staged; {descriptor}; successor_authority_id=(none); successor_yield_generation=0; successor_owner_key=(none); successor_pair_key=(none); successor_route=0; successor_protocol_version=0; successor_live_route_epoch=0; successor_runtime_generation=0; successor_terminal_generation=0; successor_leg_generation=0; successor_bridge_recovery_generation=0; successor_checkpoint_request_id=(none); reauthorization_reason=transport_epoch_reauthorization; decision_reason=accepted", secondsOffset: 8));
            lines.Add(LogLine($"event=filetransfer_fallback_tail_transport_epoch_reauthorization_successor_issued; {descriptor}; successor_authority_id={successorAuthorityId}; successor_yield_generation=4; successor_owner_key={currentOwner}; successor_pair_key={currentPair}; successor_route=4; successor_protocol_version=6; successor_live_route_epoch=2; successor_runtime_generation=3; successor_terminal_generation=0; successor_leg_generation=3; successor_bridge_recovery_generation=1; successor_checkpoint_request_id=(none); reauthorization_reason=transport_epoch_reauthorization; decision_reason=accepted", secondsOffset: 9));
            var localTuple = $"transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none); owner_key={currentOwner}; pair_key={currentPair}; yield_generation=4";
            lines.Add(LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=outbound; authority_schema=2; authority_id={successorAuthorityId}; predecessor_authority_id={predecessorAuthorityId}; {localTuple}; committed_chunk=1897; highest_observed_chunk=2359; reason=transport_epoch_reauthorization", secondsOffset: 10));
            lines.Add(LogLine($"event=filetransfer_fallback_tail_yield_proof_sent; direction=outbound; authority_schema=2; authority_id={successorAuthorityId}; predecessor_authority_id={predecessorAuthorityId}; {localTuple}; peer_visible_any=1; accepted_any=1; reason=transport_epoch_reauthorization", secondsOffset: 11));
            lines.Add(LogLine($"event=filetransfer_fallback_tail_yield_proof_received; direction=outbound; authority_schema=2; authority_id={pairedAuthorityId}; predecessor_authority_id=(none); peer_direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; peer_leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; peer_live_route_epoch=2; transport_epoch=2; peer_transport_epoch=2; bridge_recovery_generation=1; peer_bridge_recovery_generation=1; checkpoint_request_id=(none); peer_checkpoint_request_id=(none); owner_key={currentOwner}; pair_key={currentPair}; raw_pair_key={currentPair}; canonical_pair_key={currentPair}; pair_key_format=canonical_7; yield_generation=4", secondsOffset: 12));
            lines.Add(LogLine($"event=filetransfer_fallback_tail_paired_yield_granted; direction=outbound; authority_schema=2; authority_id={successorAuthorityId}; predecessor_authority_id={predecessorAuthorityId}; peer_authority_id={pairedAuthorityId}; peer_predecessor_authority_id=(none); peer_yield_generation=4; peer_leg_generation=3; peer_live_route_epoch=2; peer_transport_epoch=2; peer_bridge_recovery_generation=1; peer_checkpoint_request_id=(none); peer_owner_key={currentOwner}; peer_raw_pair_key={currentPair}; peer_canonical_pair_key={currentPair}; peer_pair_key_format=canonical_7; {localTuple}; reason=fallback_tail_paired_yield", secondsOffset: 13));
        }

        lines.Add(LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=route_cycle_missing; integrity_ok=0", secondsOffset: 7));
        lines.Add(LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=route_cycle_missing; saved_path=(none); integrity_ok=0", secondsOffset: 7));
        return lines.ToArray();
    }

    private static List<string> BuildExactPairedYieldEfficiencyPrefix(
        string transferId,
        string checkpointRequestId = "(none)")
    {
        const string sessionId = "sess_efficiency_exact";
        const string writerId = "writer_efficiency_exact";
        var pairKey =
            $"{sessionId}|{transferId}|post_tuna_fallback_v6|6|2|2|{checkpointRequestId}";
        var lines = BuildFallbackTailCoordinatorAggregateFixture(
                peerFirst: false)
            .Where(line => !line.Contains("event=file_transfer_", StringComparison.Ordinal))
            .Select(line => line
                .Replace("sess_fallback_tail_aggregate", sessionId, StringComparison.Ordinal)
                .Replace("transfer_fallback_tail_aggregate", transferId, StringComparison.Ordinal)
                .Replace(
                    "checkpoint_request_id=(none)",
                    $"checkpoint_request_id={checkpointRequestId}",
                    StringComparison.Ordinal)
                .Replace("|(none)", $"|{checkpointRequestId}", StringComparison.Ordinal) +
                $"; log_writer_id={writerId}")
            .ToList();
        var pairingIndex = lines.FindIndex(line =>
            line.Contains("event=filetransfer_fallback_tail_pairing_committed", StringComparison.Ordinal));
        lines.Insert(pairingIndex, LogLine(
            $"event=filetransfer_fallback_tail_recovery_owner_retired_after_yield; {ExactYieldDirectScope(transferId, checkpointRequestId)}; " +
            "leg_id=leg:3; previous_handoff_epoch=2; previous_handoff_state=backfill_repair; " +
            "discarded_repair_frame_count=0; discarded_repair_chunk_count=0; reason=fallback_tail_quiescent; " +
            $"log_writer_id={writerId}",
            secondsOffset: 4));
        pairingIndex++;
        lines[pairingIndex] = lines[pairingIndex]
            .Replace(
                "release_kind=FallbackPairedTailYield",
                "pairing_state=Paired; release_kind=FallbackPairedTailYield",
                StringComparison.Ordinal)
            .Replace(
                "decision_status=Accepted; decision_reason=accepted",
                $"peer_canonical_pair_key={pairKey}; decision_status=Accepted; decision_reason=accepted",
                StringComparison.Ordinal);
        lines.Add(LogLine(
            $"event=filetransfer_fallback_tail_quiescent_yield_granted; session_id={sessionId}; " +
            $"transfer_id={transferId}; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; " +
            "leg_generation=3; transport_epoch=2; bridge_recovery_generation=1; " +
            $"checkpoint_request_id={checkpointRequestId}; committed_chunk=5970; highest_observed_chunk=5970; " +
            $"state=active; can_send_data=1; reason=fallback_tail_quiescent; log_writer_id={writerId}",
            secondsOffset: 6));
        lines.Add(lines[pairingIndex]
            .Replace(
                "event=filetransfer_fallback_tail_pairing_committed",
                "event=filetransfer_fallback_tail_paired_yield_granted",
                StringComparison.Ordinal)
            .Replace(
                "decision_status=Accepted; decision_reason=accepted",
                "reason=fallback_tail_paired_yield; decision_status=Accepted; decision_reason=accepted",
                StringComparison.Ordinal));
        return lines;
    }

    private static List<string> BuildOneSidedYieldEfficiencyPrefix(string transferId)
    {
        const string writerId = "writer_efficiency_exact";
        var directScope = ExactYieldDirectScope(transferId);
        return
        [
            LogLine(
                $"event=filetransfer_route_selected; {directScope}; target_route=post_tuna_fallback_v6; " +
                "target_protocol_version=6; target_transport=regular_nkn; " +
                "route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted; " +
                $"log_writer_id={writerId}",
                secondsOffset: 0),
            LogLine(
                $"event=filetransfer_fallback_tail_recovery_owner_retired_after_yield; {directScope}; " +
                "leg_id=leg:3; previous_handoff_epoch=2; previous_handoff_state=backfill_repair; " +
                "discarded_repair_frame_count=0; discarded_repair_chunk_count=0; " +
                $"reason=fallback_tail_quiescent; log_writer_id={writerId}",
                secondsOffset: 1),
            LogLine(
                $"event=filetransfer_fallback_tail_quiescent_yield_granted; {directScope}; leg_id=leg:3; " +
                "committed_chunk=5970; highest_observed_chunk=5970; state=active; can_send_data=1; " +
                $"reason=fallback_tail_quiescent; log_writer_id={writerId}",
                secondsOffset: 2)
        ];
    }

    private static string ExactYieldRouteScope(
        string transferId,
        string route = "post_tuna_fallback_v6",
        int protocolVersion = 6,
        int liveRouteEpoch = 2,
        int runtimeGeneration = 3,
        int legGeneration = 3,
        int routePublicationGeneration = 5,
        string sessionId = "sess_efficiency_exact")
        => $"session_id={sessionId}; transfer_id={transferId}; direction=outbound; route={route}; " +
           $"protocol_version={protocolVersion}; live_route_epoch={liveRouteEpoch}; " +
           $"route_publication_generation={routePublicationGeneration}; route_commit_generation=0; " +
           $"runtime_generation={runtimeGeneration}; terminal_generation=0; leg_generation={legGeneration}";

    private static string ExactYieldDirectScope(
        string transferId,
        string checkpointRequestId = "(none)",
        string route = "post_tuna_fallback_v6",
        int protocolVersion = 6,
        int liveRouteEpoch = 2,
        int runtimeGeneration = 3,
        int legGeneration = 3,
        int routePublicationGeneration = 5,
        int transportEpoch = 2,
        int bridgeRecoveryGeneration = 1,
        string dataSessionIdentity = "data-session-1",
        int dataSessionGeneration = 1,
        string sessionId = "sess_efficiency_exact")
        => $"{ExactYieldRouteScope(transferId, route, protocolVersion, liveRouteEpoch, runtimeGeneration, legGeneration, routePublicationGeneration, sessionId)}; " +
           $"transport_epoch={transportEpoch}; bridge_recovery_generation={bridgeRecoveryGeneration}; " +
           $"checkpoint_request_id={checkpointRequestId}; data_session_identity={dataSessionIdentity}; " +
           $"data_session_generation={dataSessionGeneration}";

    private static void AppendMirroredEfficiencyDeliveryEffect(
        List<string> lines,
        string transferId,
        int operation,
        string checkpointRequestId = "(none)",
        string writerId = "writer_efficiency_exact",
        string sessionId = "sess_efficiency_exact",
        int liveRouteEpoch = 2,
        int runtimeGeneration = 3,
        int legGeneration = 3,
        int routePublicationGeneration = 5,
        int transportEpoch = 2,
        int bridgeRecoveryGeneration = 1,
        int secondsOffset = 8)
    {
        var directScope = ExactYieldDirectScope(
            transferId,
            checkpointRequestId,
            liveRouteEpoch: liveRouteEpoch,
            runtimeGeneration: runtimeGeneration,
            legGeneration: legGeneration,
            routePublicationGeneration: routePublicationGeneration,
            transportEpoch: transportEpoch,
            bridgeRecoveryGeneration: bridgeRecoveryGeneration,
            sessionId: sessionId);
        var effect =
            $"{directScope}; kind=receiver_state; authority_schema=2; " +
            $"authority_id=fta2-control-plane-{operation:D24}; control_plane_generation={operation + 1}; " +
            "reason=state_refresh_recovery; frame_type=filetransfer.receiver_state.v6; " +
            "accepted_any=1; peer_visible_any=1; local_only_rejected=0; authority_effect=positive; " +
            $"decision_status=Accepted; log_writer_id={writerId}";
        lines.Add(LogLine(
            $"event=filetransfer_control_plane_delivery_result; {effect}",
            secondsOffset));
        lines.Add(LogLine(
            $"event=filetransfer_control_plane_delivery_observed; {effect}",
            secondsOffset));
    }

    private static void AppendEfficiencyFrontierRequestEffect(
        List<string> lines,
        string transferId,
        int operation,
        string decisionStatus = "Accepted",
        string authorityEffect = "positive",
        int secondsOffset = 8)
    {
        var directScope = ExactYieldDirectScope(transferId);
        lines.Add(LogLine(
            $"event=filetransfer_v6_frontier_request_sent; {directScope}; " +
            $"authority_id=fta2-frontier-{operation:D24}; repair_generation={operation + 1}; " +
            $"repair_request_id=v6-frontier:5970:{operation + 1}; request_correlation_id=frontier-effect-{operation}; " +
            "frontier_chunk_index=5970; contiguous_committed_chunk_index=5970; " +
            "durable_received_highest_chunk_index=6000; missing_range_count=1; " +
            $"decision_status={decisionStatus}; authority_effect={authorityEffect}; " +
            "reason=frontier_stall_grace_elapsed; log_writer_id=writer_efficiency_exact",
            secondsOffset));
    }

    private static void AppendEfficiencyFrontierStallDeliveryEffect(
        List<string> lines,
        string transferId,
        int operation,
        int frontier,
        int secondsOffset)
    {
        var directScope = ExactYieldDirectScope(transferId);
        var effect =
            $"{directScope}; kind=receiver_state; authority_schema=2; " +
            $"authority_id=fta2-frontier-state-{operation:D24}; control_plane_generation={operation + 1}; " +
            "reason=frontier_stall_repair_due; frame_type=filetransfer.receiver_state.v6; " +
            "accepted_any=1; peer_visible_any=1; local_only_rejected=0; authority_effect=positive; " +
            "decision_status=Accepted; log_writer_id=writer_efficiency_exact";
        lines.Add(LogLine($"event=filetransfer_control_plane_delivery_result; {effect}", secondsOffset));
        lines.Add(LogLine($"event=filetransfer_control_plane_delivery_observed; {effect}", secondsOffset));
        lines.Add(LogLine(
            $"event=filetransfer_v6_receiver_state_sent; {directScope}; " +
            $"contiguous_committed_chunk_index={frontier}; highest_received_chunk_index={frontier + 64}; " +
            $"receiver_state_epoch={operation + 1}; reason=frontier_stall_repair_due; " +
            "decision_status=Accepted; authority_effect=positive; log_writer_id=writer_efficiency_exact",
            secondsOffset));
    }

    private static void AppendEfficiencyFrontierProgressClosure(
        List<string> lines,
        string transferId,
        int previousFrontier,
        int currentFrontier,
        int operation,
        int secondsOffset)
    {
        var directScope = ExactYieldDirectScope(transferId);
        var authorityId = $"fta2-frontier-progress-{operation:D24}";
        var repairGeneration = operation + 1;
        var repairKey = $"v6-frontier:{previousFrontier}:{repairGeneration}";
        var correlationId = $"frontier-progress-{operation}";
        var repairIdentity =
            $"authority_id={authorityId}; repair_generation={repairGeneration}; " +
            $"repair_request_key={repairKey}; request_correlation_id={correlationId}";
        lines.Add(LogLine(
            $"event=filetransfer_fallback_frontier_repair_contract_settled; {directScope}; {repairIdentity}; " +
            "settlement_outcome=ProgressObserved; decision_status=Accepted; authority_effect=positive; " +
            "log_writer_id=writer_efficiency_exact",
            secondsOffset));
        lines.Add(LogLine(
            $"event=filetransfer_fallback_frontier_repair_progress_observed; {directScope}; {repairIdentity}; " +
            $"previous_frontier_chunk_index={previousFrontier}; current_frontier_chunk_index={currentFrontier}; " +
            "decision_status=Accepted; authority_effect=positive; log_writer_id=writer_efficiency_exact",
            secondsOffset));
        lines.Add(LogLine(
            $"event=filetransfer_v6_sparse_write_committed; {directScope}; " +
            $"next_chunk_index={currentFrontier}; contiguous_chunks_committed={currentFrontier - previousFrontier}; " +
            "decision_status=Accepted; authority_effect=positive; log_writer_id=writer_efficiency_exact",
            secondsOffset));
        lines.Add(LogLine(
            $"event=filetransfer_v6_receiver_state_sent; {directScope}; " +
            $"contiguous_committed_chunk_index={currentFrontier}; highest_received_chunk_index={currentFrontier}; " +
            "reason=chunk_batch_committed; decision_status=Accepted; authority_effect=positive; " +
            "log_writer_id=writer_efficiency_exact",
            secondsOffset));
    }

    private static void AppendEfficiencyStateRefreshLifecycle(
        List<string> lines,
        string transferId,
        string requestId,
        int generation,
        string writerId = "writer_efficiency_exact",
        string extraScope = "",
        int secondsOffset = 10)
    {
        var suffix = string.IsNullOrWhiteSpace(extraScope) ? string.Empty : $"; {extraScope}";
        lines.Add(LogLine(
            $"event=filetransfer_v6_regular_nkn_state_refresh_requested; transfer_id={transferId}; " +
            $"session_id=sess_efficiency_exact; request_id={requestId}; request_sequence={generation}; " +
            $"reason=state_refresh_recovery; priority=state_refresh; transport_epoch=2{suffix}; " +
            $"log_writer_id={writerId}",
            secondsOffset));
        lines.Add(LogLine(
            $"event=filetransfer_v6_regular_nkn_state_refresh_sent; transfer_id={transferId}; " +
            $"session_id=sess_efficiency_exact; request_id={requestId}; generation={generation}; " +
            $"transport_epoch=2; reused_pending_checkpoint=0{suffix}; log_writer_id={writerId}",
            secondsOffset + 1));
    }

    private static void AppendEfficiencyCompletedTerminalLines(
        List<string> lines,
        string transferId,
        int secondsOffset)
    {
        lines.Add(LogLine(
            $"event=file_transfer_inbound_terminal; role=helper; session_id=sess_efficiency_exact; " +
            $"transfer_id={transferId}; state=Completed; error_code=(none); saved_path=C:\\received\\payload.bin; " +
            "integrity_ok=1; log_writer_id=writer_efficiency_exact",
            secondsOffset));
        lines.Add(LogLine(
            $"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_efficiency_exact; " +
            $"transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1; " +
            "log_writer_id=writer_efficiency_exact",
            secondsOffset));
    }

    private static void AssertExactTreadmillSummary(
        IReadOnlyDictionary<string, string> efficiency,
        int scopedEffectCount,
        int distinctOperationCount,
        int exactWindowCount,
        int pressureEffectCount,
        int rawFailureCount,
        int distinctFailureCount,
        int recoveredFailureCount,
        int unresolvedFailureCount,
        string verdict,
        string failureClass,
        int? frontierEpisodeCount = null,
        int? progressClosedEpisodeCount = null,
        int? unresolvedEpisodeCount = null,
        int? maxNoProgressEffectCount = null,
        int? maxNoProgressRepeatedTupleCount = null)
    {
        Assert.Equal(scopedEffectCount.ToString(CultureInfo.InvariantCulture), efficiency["post_yield_scoped_recovery_effect_count"]);
        Assert.Equal(distinctOperationCount.ToString(CultureInfo.InvariantCulture), efficiency["post_yield_scoped_recovery_distinct_operation_count"]);
        Assert.Equal(exactWindowCount.ToString(CultureInfo.InvariantCulture), efficiency["post_yield_exact_window_count"]);
        Assert.Equal(pressureEffectCount.ToString(CultureInfo.InvariantCulture), efficiency["post_yield_scoped_pressure_effect_count"]);
        Assert.Equal(rawFailureCount.ToString(CultureInfo.InvariantCulture), efficiency["fallback_tail_efficiency_treadmill_raw_count"]);
        Assert.Equal(distinctFailureCount.ToString(CultureInfo.InvariantCulture), efficiency["fallback_tail_efficiency_treadmill_distinct_count"]);
        Assert.Equal(recoveredFailureCount.ToString(CultureInfo.InvariantCulture), efficiency["fallback_tail_efficiency_treadmill_recovered_count"]);
        Assert.Equal(unresolvedFailureCount.ToString(CultureInfo.InvariantCulture), efficiency["fallback_tail_efficiency_treadmill_unresolved_count"]);
        if (frontierEpisodeCount.HasValue)
        {
            Assert.Equal(frontierEpisodeCount.Value.ToString(CultureInfo.InvariantCulture), efficiency["post_yield_frontier_episode_count"]);
        }
        if (progressClosedEpisodeCount.HasValue)
        {
            Assert.Equal(progressClosedEpisodeCount.Value.ToString(CultureInfo.InvariantCulture), efficiency["post_yield_frontier_progress_closed_episode_count"]);
        }
        if (unresolvedEpisodeCount.HasValue)
        {
            Assert.Equal(unresolvedEpisodeCount.Value.ToString(CultureInfo.InvariantCulture), efficiency["post_yield_frontier_unresolved_episode_count"]);
        }
        if (maxNoProgressEffectCount.HasValue)
        {
            Assert.Equal(maxNoProgressEffectCount.Value.ToString(CultureInfo.InvariantCulture), efficiency["post_yield_max_no_progress_frontier_effect_count"]);
        }
        if (maxNoProgressRepeatedTupleCount.HasValue)
        {
            Assert.Equal(maxNoProgressRepeatedTupleCount.Value.ToString(CultureInfo.InvariantCulture), efficiency["post_yield_max_no_progress_repeated_tuple_count"]);
        }
        Assert.Equal(verdict, efficiency["efficiency_verdict"]);
        Assert.Equal(failureClass, efficiency["efficiency_failure_class"]);
    }

    private static void AppendCompleteCrossGenerationPair(
        List<string> lines,
        bool mismatchPeerPredecessor = false,
        string? barrierBeforeGrant = null)
    {
        const string transferId = "transfer_fallback_bridge_pairing";
        const string priorPeerAuthorityId = "fta2-fallback_tail_yield-0123456789abcdef01234567";
        const string peerAuthorityId = "fta2-fallback_tail_yield-13579bdf2468ace013579bdf";
        const string peerPredecessorAuthorityId = priorPeerAuthorityId;
        const string localAuthorityId = "fta2-fallback_tail_yield-fedcba987654321001234567";
        const string localOwnerKey = "sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|3|3|2|(none)";
        const string peerOwnerKey = "sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|3|2|2|(none)";
        const string canonicalPairKey = "sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|2|(none)";
        const string peerRawPairKey = canonicalPairKey;
        var receivedPredecessorAuthorityId = mismatchPeerPredecessor
            ? "fta2-fallback_tail_yield-deadbeefdeadbeefdeadbeef"
            : peerPredecessorAuthorityId;
        var localScope =
            $"transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; transport_epoch=2; bridge_recovery_generation=3; checkpoint_request_id=(none)";
        var localTuple = $"{localScope}; owner_key={localOwnerKey}; pair_key={canonicalPairKey}";
        lines.Add(LogLine($"event=filetransfer_fallback_leg_transport_epoch_adopted; direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; previous_transport_epoch=2; transport_epoch=2; bridge_recovery_generation=3; retired_checkpoint_request_id=(none); current_checkpoint_request_id=(none); state=recovery_active; can_send_data=1; reason=bridge_reauthenticated", secondsOffset: 9));
        lines.Add(LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=inbound; authority_schema=2; authority_id={localAuthorityId}; predecessor_authority_id=(none); {localTuple}; yield_generation=3; committed_chunk=6955; highest_observed_chunk=9954; reason=bridge_generation_pairing_reconciled", secondsOffset: 10));
        lines.Add(LogLine($"event=filetransfer_fallback_tail_yield_proof_sent; direction=inbound; authority_schema=2; authority_id={localAuthorityId}; predecessor_authority_id=(none); {localTuple}; yield_generation=3; peer_visible_any=1; accepted_any=1; reason=bridge_generation_pairing_reconciled", secondsOffset: 11));
        if (barrierBeforeGrant is not null)
        {
            lines.Add(barrierBeforeGrant switch
            {
                "route_replaced" => LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=3; route_publication_generation=5; runtime_generation=4; terminal_generation=0; leg_generation=4; route_commit_generation=0; target_route=regular_nkn_v4_fast; target_protocol_version=4; target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 12),
                "transfer_terminal" => LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)", secondsOffset: 12),
                _ => throw new InvalidOperationException(barrierBeforeGrant)
            });
        }
        lines.Add(LogLine($"event=filetransfer_fallback_tail_yield_proof_received; direction=inbound; authority_schema=2; authority_id={peerAuthorityId}; predecessor_authority_id={receivedPredecessorAuthorityId}; peer_direction=outbound; {localScope}; peer_live_route_epoch=2; peer_leg_generation=3; peer_transport_epoch=2; peer_bridge_recovery_generation=2; peer_checkpoint_request_id=(none); owner_key={peerOwnerKey}; pair_key={peerRawPairKey}; raw_pair_key={peerRawPairKey}; canonical_pair_key={canonicalPairKey}; pair_key_format=canonical_7; yield_generation=3", secondsOffset: 13));
        lines.Add(LogLine($"event=filetransfer_fallback_tail_paired_yield_granted; direction=inbound; authority_schema=2; authority_id={localAuthorityId}; predecessor_authority_id=(none); peer_authority_id={peerAuthorityId}; peer_predecessor_authority_id={peerPredecessorAuthorityId}; peer_yield_generation=3; peer_leg_generation=3; peer_live_route_epoch=2; peer_transport_epoch=2; peer_bridge_recovery_generation=2; peer_checkpoint_request_id=(none); peer_owner_key={peerOwnerKey}; peer_raw_pair_key={peerRawPairKey}; peer_canonical_pair_key={canonicalPairKey}; peer_pair_key_format=canonical_7; {localTuple}; yield_generation=3; reason=fallback_tail_paired_yield", secondsOffset: 14));
        lines.Add(LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1", secondsOffset: 14));
        lines.Add(LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=x; integrity_ok=1", secondsOffset: 14));
    }

    private static string[] BuildFallbackTailBridgeRecoveryMismatchFixture(bool includeDownstreamSymptoms)
    {
        const string transferId = "transfer_fallback_bridge_pairing";
        const string peerAuthorityId = "fta2-fallback_tail_yield-0123456789abcdef01234567";
        const string peerPredecessorAuthorityId = "fta2-fallback_tail_yield-89abcdef0123456701234567";
        const string expectedOwnerKey = "sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|3|1|2|(none)";
        const string expectedPairKey = "sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|1|2|(none)";
        const string peerOwnerKey = "sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|3|2|2|(none)";
        const string peerPairKey = "sess_a|transfer_fallback_bridge_pairing|post_tuna_fallback_v6|6|2|2|2|(none)";
        var lines = new List<string>
        {
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; live_route_epoch=2; route_publication_generation=4; runtime_generation=3; terminal_generation=0; leg_generation=3; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 0),
            LogLine($"event=filetransfer_fallback_leg_transport_epoch_adopted; direction=inbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; previous_transport_epoch=1; transport_epoch=2; bridge_recovery_generation=1; retired_checkpoint_request_id=(none); current_checkpoint_request_id=(none); state=recovery_active; can_send_data=1; reason=transport_recovered_unproven", secondsOffset: 1),
            LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=outbound; authority_schema=2; authority_id={peerAuthorityId}; predecessor_authority_id={peerPredecessorAuthorityId}; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; transport_epoch=2; bridge_recovery_generation=2; checkpoint_request_id=(none); owner_key={peerOwnerKey}; pair_key={peerPairKey}; yield_generation=2; committed_chunk=6955; highest_observed_chunk=9954; reason=receiver_state_sparse_runtime", secondsOffset: 2),
            LogLine($"event=filetransfer_runtime_unlock_admission_denied; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=route_hint; reason=fallback_tail_quiescent; admission_generation=0; admission_reason=paired_fallback_tail_yield_missing; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=3; fallback_active=1; fallback_current=1; fallback_terminalized=0; paired_fallback_yield_granted=0", secondsOffset: 3),
            LogLine($"event=filetransfer_fallback_tail_yield_proof_sent; direction=outbound; authority_schema=2; authority_id={peerAuthorityId}; predecessor_authority_id={peerPredecessorAuthorityId}; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; transport_epoch=2; bridge_recovery_generation=2; checkpoint_request_id=(none); owner_key={peerOwnerKey}; pair_key={peerPairKey}; yield_generation=2; peer_visible_any=1; accepted_any=1; reason=fallback_tail_quiescent", secondsOffset: 4)
        };
        var mismatch = LogLine($"event=filetransfer_fallback_tail_owner_generation_mismatch; direction=inbound; peer_direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:3; leg_generation=3; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=2; bridge_recovery_generation=1; checkpoint_request_id=(none); peer_live_route_epoch=2; peer_leg_generation=3; peer_transport_epoch=2; peer_bridge_recovery_generation=2; peer_checkpoint_request_id=(none); expected_owner_key={expectedOwnerKey}; expected_pair_key={expectedPairKey}; peer_owner_key={peerOwnerKey}; peer_pair_key={peerPairKey}; yield_generation=2", secondsOffset: 5);
        lines.AddRange(new[] { mismatch, mismatch, mismatch, mismatch });

        if (includeDownstreamSymptoms)
        {
            lines.Add(LogLine($"event=filetransfer_control_plane_delivery_result; direction=inbound; kind=frontier_request; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=3; bridge_recovery_generation=1; transport_epoch=2; frame_type=filetransfer.frontier_request.v6; repair_request_id=v6-frontier:8476:29; request_correlation_id=frq-downstream; first_start_chunk_index=8476; first_chunk_count=1; range_count=1; peer_visible_any=1; accepted_any=1", secondsOffset: 6));
            lines.Add(LogLine($"event=filetransfer_fallback_frontier_repair_stalled; direction=inbound; transfer_id={transferId}; session_id=sess_a; repair_request_key=v6-frontier:8476:29; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=3; transport_epoch=2; bridge_recovery_generation=1; reason=repeated_frontier_repair_without_progress; frontier_chunk_index=8476; durable_received_highest_chunk_index=8475; requested_chunk_count=1; attempt_count=2", secondsOffset: 7));
            lines.Add(LogLine($"event=filetransfer_v6_frontier_request_transport_rejected; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.frontier_request.v6; priority=1; queue_capacity_bypassed=1; queued_frames=64; queued_bytes=65536; transport_epoch=2; repair_request_id=v6-frontier:8476:29; request_correlation_id=frq-downstream; decision_reason=priority_control_queue_capacity_exceeded", secondsOffset: 8));
            lines.Add(LogLine($"event=filetransfer_gui_progress_timeout; transfer_id={transferId}; session_id=sess_a; reason=normal_to_tuna_live_epoch_missing", secondsOffset: 9));
            lines.Add(LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=peer_disconnected", secondsOffset: 10));
            lines.Add(LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)", secondsOffset: 10));
        }

        return lines.ToArray();
    }

    private static string OutboundSparseInstallDescriptor(
        string transferId,
        int installGeneration = 2,
        int predecessorInstallGeneration = 1,
        long dataSessionGeneration = 2,
        int liveRouteEpoch = 2,
        int runtimeGeneration = 3,
        int terminalGeneration = 0,
        int legGeneration = 3,
        int routePublicationGeneration = 5,
        string installationReason = "stale_worker_superseded")
        => $"install_generation={installGeneration}; predecessor_install_generation={predecessorInstallGeneration}; data_session_generation={dataSessionGeneration}; runtime_family=post_tuna_fallback_v6; descriptor_terminal_generation={terminalGeneration}; descriptor_leg_generation={legGeneration}; installation_reason={installationReason}; runtime_owner_session_id=sess_a; runtime_owner_transfer_id={transferId}; runtime_owner_direction=outbound; runtime_owner_route=post_tuna_fallback_v6; runtime_owner_route_token=post_tuna_fallback_v6; runtime_owner_protocol_version=6; runtime_owner_live_route_epoch={liveRouteEpoch}; runtime_owner_runtime_generation={runtimeGeneration}; runtime_owner_terminal_generation={terminalGeneration}; runtime_owner_runtime_family=post_tuna_fallback_v6; runtime_owner_created_reason=post_tuna_file_fallback_active; route_publication_session_id=sess_a; route_publication_transfer_id={transferId}; route_publication_direction=outbound; route_publication_route=post_tuna_fallback_v6; route_publication_route_token=post_tuna_fallback_v6; route_publication_protocol_version=6; route_publication_live_route_epoch={liveRouteEpoch}; route_publication_runtime_generation={runtimeGeneration}; route_publication_terminal_generation={terminalGeneration}; route_publication_leg_generation={legGeneration}; route_publication_route_commit_generation=0; route_publication_target_route=post_tuna_fallback_v6; route_publication_target_protocol_version=6; route_publication_target_transport=regular_nkn; route_publication_token_generation={routePublicationGeneration}; route_publication_action_kind=RoutePublication; route_publication_reason=accepted; route_publication_owner_session_id=sess_a; route_publication_owner_transfer_id={transferId}; route_publication_owner_direction=outbound; route_publication_owner_route=post_tuna_fallback_v6; route_publication_owner_route_token=post_tuna_fallback_v6; route_publication_owner_protocol_version=6; route_publication_owner_live_route_epoch={liveRouteEpoch}; route_publication_owner_runtime_generation={runtimeGeneration}; route_publication_owner_terminal_generation={terminalGeneration}; route_publication_owner_runtime_family=post_tuna_fallback_v6; route_publication_owner_created_reason=post_tuna_file_fallback_active";
}
