using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

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
    public async Task AnalyzeRetained_EfficiencySummary_PostYieldFallbackChurnIsTreadmill()
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
        Assert.Equal("fail", efficiency["efficiency_verdict"]);
        Assert.Equal("fallback_tail_efficiency_treadmill", efficiency["efficiency_failure_class"]);
        Assert.Equal("1", efficiency["post_yield_recovery_activity_count"]);
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

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PeerYieldAcceptedButLocalYieldBlocked_ClassifiesLocalYieldMissing()
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
            LogLine($"event=filetransfer_fallback_tail_yield_proof_received; direction=outbound; peer_direction=inbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; peer_leg_generation=4; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=(none); owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|4|2|9|(none); pair_key={pairKey}; peer_owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|4|2|9|(none); peer_pair_key={pairKey}; yield_generation=1"),
            LogLine($"event=filetransfer_fallback_tail_local_yield_blocked; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:5; leg_generation=5; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=2; checkpoint_request_id=(none); checkpoint_request_id_snapshot=(none); canonical_owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|5|2|9|(none); canonical_pair_key={pairKey}; local_owner_key=(none); peer_owner_key=sess_redacted|{transferId}|post_tuna_fallback_v6|6|4|4|2|9|(none); local_pair_key=(none); peer_pair_key={pairKey}; decision_reason=fallback_frontier_state_missing; source=fallback_tail_peer_yield"),
            LogLine($"event=filetransfer_gui_progress_timeout; transfer_id={transferId}; session_id=sess_redacted; reason=normal_to_tuna_live_epoch_missing"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("1", route["fallback_tail_yield_proof_received_count"]);
        Assert.Equal("1", route["fallback_tail_local_yield_blocked_count"]);
        Assert.Equal("fail", route["fallback_tail_paired_yield_verdict"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fallback_tail_local_yield_missing", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["classification_fallback_tail_local_yield_blocked_count"]);
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
            var descriptor = $"authority_schema=2; direction=outbound; session_id=sess_a; transfer_id={transferId}; predecessor_authority_id={predecessorAuthorityId}; predecessor_yield_generation=1; reauthorization_generation=1; source_owner_key={sourceOwner}; source_pair_key={sourcePair}; target_owner_key={targetOwner}; target_pair_key={targetPair}; source_transport_epoch=1; target_transport_epoch=2; source_runtime_generation=3; target_runtime_generation=4; predecessor_route=4; predecessor_protocol_version=6; predecessor_live_route_epoch=2; predecessor_runtime_generation=3; predecessor_terminal_generation=0; predecessor_leg_generation=3; predecessor_bridge_recovery_generation=1; predecessor_checkpoint_request_id=(none); terminal_generation=0; leg_generation=4; bridge_recovery_generation=2; bridge_decision_generation=2; route_publication_generation=6; route_commit_generation=0; checkpoint_request_id=(none); data_session_generation=2";
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

        var descriptor = $"authority_schema=2; direction=outbound; session_id=sess_a; transfer_id={transferId}; predecessor_authority_id={predecessorAuthorityId}; predecessor_yield_generation=3; reauthorization_generation=1; source_owner_key={sourceOwner}; source_pair_key={sourcePair}; target_owner_key={currentOwner}; target_pair_key={currentPair}; source_transport_epoch=1; target_transport_epoch=2; source_runtime_generation=3; target_runtime_generation=3; predecessor_route=4; predecessor_protocol_version=6; predecessor_live_route_epoch=2; predecessor_runtime_generation=3; predecessor_terminal_generation=0; predecessor_leg_generation=3; predecessor_bridge_recovery_generation=1; predecessor_checkpoint_request_id=(none); terminal_generation=0; leg_generation=3; bridge_recovery_generation=1; bridge_decision_generation=2; route_publication_generation=5; route_commit_generation=0; checkpoint_request_id=(none); data_session_generation=2";
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
