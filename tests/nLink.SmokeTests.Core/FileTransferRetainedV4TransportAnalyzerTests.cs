using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "RetainedV4Transport")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferRetainedV4TransportAnalyzerTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    [Trait("RetainedV4Suite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_CleanRegularV4ReportsPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_regular_v4";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn", secondsOffset: 0),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast; live_route_epoch=0", secondsOffset: 1),
            LogLine($"event=filetransfer_v4_state_sent; transfer_id={transferId}; session_id=sess_a; epoch=1; contiguous_committed_chunk_index=0; durable_received_highest_chunk_index=-1; credit_until_chunk_index_exclusive=3; missing_range_count=0; bytes_committed=0", secondsOffset: 2),
            LogLine($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id=sess_a; epoch=1; contiguous_committed_chunk_index=3; durable_received_highest_chunk_index=2; credit_until_chunk_index_exclusive=3; missing_range_count=0", secondsOffset: 3),
            LogLine($"event=filetransfer_v4_sender_throughput_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=64512; raw_bytes_per_second=32256; chunk_count_sent=3", secondsOffset: 4),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1", secondsOffset: 10),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1", secondsOffset: 10)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        Assert.Equal("pass", efficiency["efficiency_verdict"]);
        Assert.Equal("none", efficiency["efficiency_failure_class"]);
        Assert.Equal(transferId, efficiency["transfer_id"]);
        Assert.Equal("regular_nkn_v4_fast", efficiency["route_sequence"]);
        Assert.Equal("(none)", efficiency["live_epoch_sequence"]);
        Assert.Equal("Completed", efficiency["terminal_state_inbound"]);
        Assert.Equal("Completed", efficiency["terminal_state_outbound"]);
        Assert.Equal("1", efficiency["sha_ok"]);
        Assert.Equal("1", efficiency["v4_state_sent_count"]);
        Assert.Equal("1", efficiency["v4_state_received_count"]);
        Assert.Equal("1", efficiency["route_epoch_count"]);
        Assert.Equal("regular_nkn_v4_fast", efficiency["route_epoch.0.route"]);
        Assert.Equal("4", efficiency["route_epoch.0.protocol_version"]);
        Assert.Equal("regular_v4", efficiency["route_epoch.0.runtime_family"]);
        Assert.Equal("available", efficiency["route_epoch.0.bytes_attribution_status"]);
    }

    [Fact]
    [Trait("RetainedV4Suite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_CompactModeAlsoWritesArtifacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_compact";
        var lines = BuildRouteAwareCompletedFixture(
            transferId,
            route: "regular_nkn_v4_fast",
            protocolVersion: 4,
            runtimeProfile: "regular_nkn_v4_fast",
            bridgeRecoveryPolicy: "regular_nkn_v4_fast",
            runtimeEventName: "filetransfer_v4_sender_started");

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: new Dictionary<string, string>
            {
                ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
            });

        Assert.Equal(0, result.Script.ExitCode);
        Assert.True(File.Exists(Path.Combine(result.ArtifactDir, "filetransfer-efficiency-summary.json")));
        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        Assert.Equal("pass", efficiency["efficiency_verdict"]);
        Assert.Equal("regular_nkn_v4_fast", efficiency["route_sequence"]);
    }

    [Fact]
    [Trait("RetainedV4Suite", "Efficiency")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_EfficiencySummary_CorrectnessFailureDefersEfficiency()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_efficiency_file_tuna_v6";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=file_tuna_v6; protocol_version=6; runtime_profile=file_tuna_v6_fast; frame_family=v6; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v6; selection_reason=test; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var efficiency = ReadArtifactReport(result.ArtifactDir, "filetransfer-efficiency-summary.txt");
        Assert.Equal("not_evaluated_due_correctness_failure", efficiency["efficiency_verdict"]);
        Assert.Equal("none", efficiency["efficiency_failure_class"]);
        Assert.Contains("file_tuna_v6", efficiency["correctness_blockers"], StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_CleanCompletedV4Transfer_ReturnsPassAndWritesArtifacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildCleanCompletedTransferFixture("transfer_clean"));

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("transfer_clean", verdict["transfer_id"]);
        Assert.Equal("transfer-terminal-summary.txt", verdict["next_artifact"]);

        foreach (var fileName in RequiredArtifactFiles)
        {
            Assert.True(File.Exists(Path.Combine(result.ArtifactDir, fileName)), $"Expected artifact: {fileName}");
        }

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("inconclusive", decomposition["likely_limiter"]);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("legacy", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);

        var ledger = ReadArtifactReport(result.ArtifactDir, "phase0-invariant-ledger.txt");
        var invariant = ReadArtifactReport(result.ArtifactDir, "filetransfer-invariant-summary.txt");
        Assert.Equal("unknown", ledger["final_route"]);
        Assert.Equal("unknown", ledger["protocol_version"]);
        Assert.Equal("unknown", ledger["runtime_family"]);
        Assert.Equal("v6", ledger["frame_family"]);
        Assert.Equal("completed", ledger["terminal_winner"]);
        Assert.Equal("1", ledger["sha_ok"]);
        Assert.Equal("0", ledger["stale_route_mismatch_count"]);
        Assert.Equal("legacy", invariant["release_route_law_verdict"]);
        Assert.Equal("pass", invariant["terminal_lifecycle_invariant_verdict"]);
        Assert.Equal("pass", invariant["control_priority_invariant_verdict"]);
        Assert.Equal("none", invariant["invariant_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SupersededRouteHint_DoesNotCreateRouteMismatch()
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
            LogLine($"event=filetransfer_regular_v4_recovery_liveness_completed; session_id=sess_redacted; transfer_id={transferId}; generation=1; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; authority_reason=session_liveness_timeout_pending; reason=superseded_by_tuna_activation; superseded_by_route=file_tuna_v4; superseded_by_protocol_version=4; source=runtime_unlock_route_commit_accepted"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_DeferredNormalToTunaRouteHint_DoesNotCountAsRouteMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn"),
            LogLine($"event=filetransfer_normal_to_tuna_handoff_route_hint_deferred; transfer_id={transferId}; source=handoff_broadcast; reason=tuna_activation_negotiated; route=file_tuna_v4; protocol_version=4"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal("regular_nkn_v4_fast,file_tuna_v4", route["selected_route_changes"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.DoesNotContain("filetransfer_normal_to_tuna_handoff_route_hint_deferred", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularRouteCompletedBridgeQueueClear_ReturnsWarningNotFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareCompletedFixture(
            "transfer_regular_queue_clear",
            route: "regular_nkn_v4_fast",
            protocolVersion: 4,
            runtimeProfile: "regular_nkn_v4_fast",
            bridgeRecoveryPolicy: "regular_nkn_v4_fast",
            runtimeEventName: "filetransfer_v4_sender_started")).ToList();
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=4", secondsOffset: 30),
                LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=17; frames_enqueued=20; payload_bytes_sent=847447; payload_bytes_per_second=423724; payload_bytes_enqueued=848728; payload_bytes_enqueued_per_second=424364; send_failures=0; queue_clears=4; queue_depth=1; queued_bytes=427; oldest_queued_age_ms=299; in_flight=0; configured_concurrency=4; effective_concurrency=4; sample_window_ms=2000", secondsOffset: 45),
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Equal("recovered_regular_v4_bridge_clear", verdict["warning_kinds"]);
        Assert.Equal("recovered_regular_v4_bridge_clear:1", verdict["warning_kind_counts"]);
        Assert.Equal("recovered_regular_v4_bridge_clear:2", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("recovered_regular_v4_bridge_clear:regular_nkn", verdict["warning_cap_contexts"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);

        var stabilityText = File.ReadAllText(Path.Combine(result.ArtifactDir, "stability-gates-summary.txt"));
        Assert.Contains("recovered regular NKN V4 bridge queue clear", stabilityText, StringComparison.Ordinal);
        Assert.DoesNotContain("bridge bulk send failure/clear", stabilityText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularV4ProgressTimeoutRecoveryStorm_DowngradesQueueClearToEnvironmentalWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_regular_recovery_storm";
        var lines = BuildRouteAwareCompletedFixture(
                transferId,
                route: "regular_nkn_v4_fast",
                protocolVersion: 4,
                runtimeProfile: "regular_nkn_v4_fast",
                bridgeRecoveryPolicy: "regular_nkn_v4_fast",
                runtimeEventName: "filetransfer_v4_sender_started")
            .Where(line => !line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal) &&
                           !line.Contains("event=file_transfer_outbound_terminal", StringComparison.Ordinal))
            .ToList();
        lines.Add(LogLine($"event=filetransfer_binary_frame_received; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; chunk_index=0-31; raw_chunk_bytes=67108864; chunk_count=32", secondsOffset: 5));
        lines.Add(LogLine("event=nkn_bridge_receive_stall_recovery_suppressed; reason=filetransfer_protocol_repair_only; stall_reason=bulk_receive_stalled; attempt=1; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1", secondsOffset: 20));
        lines.Add(LogLine("event=nkn_bridge_receive_stall_recovery_protocol_repair_exhausted; trigger=filetransfer_protocol_repair_only; requested_reason=regular_v4_peer_silence; recovery_count=1; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1", secondsOffset: 21));
        lines.Add(LogLine("event=nkn_bridge_receive_stall_recovery_regular_v4_unproven_escalation_allowed; requested_reason=regular_v4_peer_silence; stall_reason=regular_v4_unproven_recovery_escalation; recovery_count=1; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1", secondsOffset: 22));
        lines.Add(LogLine("event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=2", secondsOffset: 23));
        lines.Add(LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; session_id=sess_a; reason=no useful data progress for 180s; total_wait_s=360; progress_timeout_seconds=180; receiver_next_chunk=1614; receiver_highest_chunk=1613; progress_events=23", secondsOffset: 360));
        lines.Add(LogLine($"event=filetransfer_artifact_slice_summary; transfer_id={transferId}; session_id=sess_a; artifact_slice_start_reason=live_soak_failure_context; artifact_slice_end_reason=gui_progress_timeout", secondsOffset: 361));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("INCONCLUSIVE_PROGRESS_TIMEOUT", verdict["verdict"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Contains("regular_v4_transport_recovery_storm", verdict["warning_kinds"], StringComparison.Ordinal);

        var stabilityText = File.ReadAllText(Path.Combine(result.ArtifactDir, "stability-gates-summary.txt"));
        Assert.Contains("regular_v4_transport_recovery_storm", stabilityText, StringComparison.Ordinal);
        Assert.DoesNotContain("bridge bulk send failure/clear", stabilityText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularV4RecoveryRewindTreadmill_IsClassified()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_regular_v4_rewind_treadmill";
        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareCompletedFixture(
            transferId,
            route: "regular_nkn_v4_fast",
            protocolVersion: 4,
            runtimeProfile: "regular_nkn_v4_fast",
            bridgeRecoveryPolicy: "regular_nkn_v4_fast",
            runtimeEventName: "filetransfer_v4_sender_started"));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine($"event=filetransfer_regular_v4_receive_recovery_frontier_rebind; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; remote_next_expected_chunk_index=1400; chunks_accepted_before=3900; frontier_lag_chunks=2500; replay_or_recovery_active=1", secondsOffset: 20),
                LogLine($"event=filetransfer_v4_frontier_stall_missing_range_due; direction=inbound; transfer_id={transferId}; session_id=sess_a; next_chunk_index=1400; highest_received_chunk_index=3900; missing_range_count=12; missing_chunk_count=512", secondsOffset: 21),
                LogLine($"event=filetransfer_v4_repair_requested; direction=inbound; transfer_id={transferId}; session_id=sess_a; request_key=frontier:1400-1911; missing_chunk_count=512; reason=frontier_stall_missing_range", secondsOffset: 22),
                LogLine($"event=filetransfer_sender_repair_cache_pressure_entered; direction=outbound; transfer_id={transferId}; session_id=sess_a; sent_cache_chunk_count=2048; sent_cache_bytes=44040192", secondsOffset: 23),
                LogLine($"event=filetransfer_sender_repair_chunk_skipped; transfer_id={transferId}; session_id=sess_a; reason=not_yet_sent; chunk_index=1400; remote_next_expected_chunk_index=1400; chunks_accepted_for_transport=1400; chunk_count=12483", secondsOffset: 24),
                LogLine($"event=filetransfer_regular_v4_receive_recovery_frontier_rebind; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; remote_next_expected_chunk_index=1400; chunks_accepted_before=2500; frontier_lag_chunks=1100; replay_or_recovery_active=1", secondsOffset: 26),
                LogLine($"event=filetransfer_regular_v4_receive_recovery_frontier_rebind; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; remote_next_expected_chunk_index=1400; chunks_accepted_before=2200; frontier_lag_chunks=800; replay_or_recovery_active=1", secondsOffset: 28),
                LogLine($"event=filetransfer_regular_v4_receive_recovery_frontier_rebind; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; remote_next_expected_chunk_index=1400; chunks_accepted_before=2100; frontier_lag_chunks=700; replay_or_recovery_active=1", secondsOffset: 30),
                LogLine($"event=filetransfer_v4_efficiency_summary; direction=outbound; transfer_id={transferId}; session_id=sess_a; terminal_state=completed; route=regular_nkn_v4_fast; protocol_version=4; file_size_bytes=268435456; bytes_acknowledged_by_receiver=268435456; bytes_transferred=268435456; chunks_accepted_for_transport=12483; remote_frontier_chunk_index=12483; raw_bytes_sent_total=1500000000; normal_raw_bytes_sent_total=700000000; repair_raw_bytes_sent_total=800000000; raw_to_file_permille=5588; normal_to_file_permille=2607; repair_to_file_permille=2980; repair_to_raw_permille=533; send_wait_count_total=42", secondsOffset: 36)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("regular_v4_recovery_rewind_treadmill", verdict["regular_v4_efficiency_pressure_class"]);
        Assert.Equal("4", verdict["regular_v4_frontier_rebind_count"]);
        Assert.Equal("0", verdict["regular_v4_frontier_rebind_rejected_count"]);
        Assert.Equal("1", verdict["regular_v4_frontier_stall_missing_range_count"]);
        Assert.Equal("1", verdict["regular_v4_repair_requested_count"]);
        Assert.Equal("1", verdict["regular_v4_repair_cache_pressure_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("regular_v4_recovery_rewind_treadmill", stability["regular_v4_efficiency_pressure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularV4FrontierRepairAuthorityMissing_IsClassifiedBeforeRuntimeUnlock()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_regular_v4_frontier_authority_missing";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn", secondsOffset: 0),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast; live_route_epoch=1; runtime_generation=1; leg_generation=1", secondsOffset: 1),
            LogLine($"event=filetransfer_v4_frontier_stall_missing_range_due; direction=inbound; transfer_id={transferId}; session_id=sess_a; next_chunk_index=1024; highest_received_chunk_index=4096; missing_range_count=9; missing_chunk_count=384; frontier_stall_age_ms=42000", secondsOffset: 20),
            LogLine($"event=filetransfer_v4_repair_requested; direction=inbound; transfer_id={transferId}; session_id=sess_a; request_key=frontier:1024-1407; missing_chunk_count=384; reason=frontier_stall_missing_range", secondsOffset: 21),
            LogLine($"event=filetransfer_sender_repair_cache_pressure_entered; direction=outbound; transfer_id={transferId}; session_id=sess_a; sent_cache_chunk_count=4096; sent_cache_bytes=67108864", secondsOffset: 22),
            LogLine($"event=session_liveness_timeout; transfer_id={transferId}; session_id=sess_a; reason=active_filetransfer_progress_observed_without_recovery_authority; route=regular_nkn_v4_fast; protocol_version=4", secondsOffset: 45),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Failed; route=regular_nkn_v4_fast; protocol_version=4; error_code=peer_disconnected; integrity_ok=0", secondsOffset: 50)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("regular_v4_repair_liveness_authority_missing", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["regular_v4_frontier_stall_missing_range_count"]);
        Assert.Equal("1", verdict["regular_v4_repair_requested_count"]);
        Assert.Equal("0", verdict["regular_v4_frontier_repair_authority_granted_count"]);
        Assert.Equal("fail", verdict["regular_v4_frontier_repair_verdict"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("regular_v4_repair_liveness_authority_missing", stability["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularV4FrontierRepairAuthorityEpochZero_WithProgressPasses()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_regular_v4_frontier_epoch_zero_authorized";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; route_publication_generation=1", secondsOffset: 0),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast; live_route_epoch=0; runtime_generation=1; leg_generation=1", secondsOffset: 1),
            LogLine($"event=filetransfer_v4_frontier_stall_missing_range_due; direction=inbound; transfer_id={transferId}; session_id=sess_a; next_chunk_index=1024; highest_received_chunk_index=4096; missing_range_count=9; missing_chunk_count=384; frontier_stall_age_ms=42000", secondsOffset: 20),
            LogLine($"event=filetransfer_v4_repair_requested; direction=inbound; transfer_id={transferId}; session_id=sess_a; request_key=frontier:1024-1407; missing_chunk_count=384; reason=frontier_stall_missing_range", secondsOffset: 21),
            LogLine($"event=filetransfer_regular_v4_frontier_repair_authority_granted; source=nkn_bridge_pressure; stage=control_feedback_pressure; session_id=sess_a; transfer_id={transferId}; direction=inbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=0; route_publication_generation=1; route_commit_generation=0; repair_request_key=1024:384:1024:1407; frontier_chunk_index=1024; durable_received_highest_chunk_index=1023; pending_repair_count=384; decision_generation=1; decision_reason=regular_v4_frontier_repair_authority", secondsOffset: 22),
            LogLine($"event=filetransfer_regular_v4_frontier_repair_dispatched; direction=outbound; transfer_id={transferId}; session_id=sess_a; stage=v4_repair_send; repair_request_key=1024:384:1024:1407; protocol_repair_request_id=regular-v4-repair-1; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=0; route_publication_generation=1; route_commit_generation=0; decision_generation=1; sent_chunk_count=384; remote_next_expected_chunk_index=1024; chunks_accepted_for_transport=384", secondsOffset: 23),
            LogLine($"event=filetransfer_regular_v4_frontier_repair_progress_observed; direction=inbound; transfer_id={transferId}; session_id=sess_a; repair_request_key=1024:384:1024:1407; reason=frontier_repair_committed; committed_frontier_before=1023; committed_frontier_after=1407; requested_chunk_count=384", secondsOffset: 24),
            LogLine($"event=filetransfer_v4_efficiency_summary; direction=outbound; transfer_id={transferId}; session_id=sess_a; terminal_state=completed; route=regular_nkn_v4_fast; protocol_version=4; file_size_bytes=268435456; bytes_acknowledged_by_receiver=268435456; bytes_transferred=268435456; chunks_accepted_for_transport=12483; remote_frontier_chunk_index=12483; raw_bytes_sent_total=268435456; normal_raw_bytes_sent_total=260177920; repair_raw_bytes_sent_total=8257536; raw_to_file_permille=1000; normal_to_file_permille=969; repair_to_file_permille=31; repair_to_raw_permille=31; send_wait_count_total=1", secondsOffset: 35),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; route=regular_nkn_v4_fast; protocol_version=4; integrity_ok=1", secondsOffset: 36)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["regular_v4_frontier_repair_authority_granted_count"]);
        Assert.Equal("1", verdict["regular_v4_frontier_repair_dispatched_count"]);
        Assert.Equal("1", verdict["regular_v4_frontier_repair_progress_observed_count"]);
        Assert.Equal("pass", verdict["regular_v4_frontier_repair_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_BridgeConfigSummary_RecordsFrozenDefaults()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_bridge_defaults")
            .Append(LogLine("event=bridge_bundle_loaded; bridge_script_path=C:\\nLink\\bridge\\win-x64\\index.js; bridge_manifest_path=C:\\nLink\\bridge\\win-x64\\bridge-manifest.json; manifest_status=ok; manifest_reason=ok; manifest_version=1; app_version=0.7.0; bridge_script_sha256=abc123; manifest_bridge_script_sha256=abc123; node_version=v24.13.1; owner_pid_watchdog=true; kill_on_close_job=true"))
            .Append(LogLine("event=screenshare_bridge_transport_health_summary; selected_rpc=fake; selected_rpc_key=fake; selected_rpc_stage=initial; connect_id=fake; connect_key=fake; ready_emitted=1; client_ready_age_ms=100; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=0; latest_disconnect_reason=(none); sample_window_ms=2000; control_subclients=4; media_subclients=8; bulk_subclients=4; bulk_send_concurrency=4; bulk_send_mode=fanout; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=-1; media_last_received_age_ms=-1; bulk_last_received_age_ms=-1"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var bridgeConfig = ReadArtifactReport(result.ArtifactDir, "bridge-config-summary.txt");
        Assert.Equal("expected", bridgeConfig["bridge_config_status"]);
        Assert.Equal("4/8/4", bridgeConfig["expected_topology"]);
        Assert.Equal("4/8/4", bridgeConfig["observed_topology"]);
        Assert.Equal("4", bridgeConfig["observed_bulk_send_concurrency"]);
        Assert.Equal("fanout", bridgeConfig["observed_bulk_send_mode"]);
        Assert.Equal("ok", bridgeConfig["manifest_status"]);
        Assert.Equal("v24.13.1", bridgeConfig["node_version"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_BridgeConfigSummary_FlagsUnexpectedDefaultDrift()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_bridge_drift")
            .Append(LogLine("event=screenshare_bridge_transport_health_summary; selected_rpc=fake; selected_rpc_key=fake; selected_rpc_stage=initial; connect_id=fake; connect_key=fake; ready_emitted=1; client_ready_age_ms=100; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=0; latest_disconnect_reason=(none); sample_window_ms=2000; control_subclients=4; media_subclients=8; bulk_subclients=8; bulk_send_concurrency=6; bulk_send_mode=round_robin; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=-1; media_last_received_age_ms=-1; bulk_last_received_age_ms=-1"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var bridgeConfig = ReadArtifactReport(result.ArtifactDir, "bridge-config-summary.txt");
        Assert.Equal("unexpected_drift", bridgeConfig["bridge_config_status"]);
        Assert.Equal("4/8/4", bridgeConfig["expected_topology"]);
        Assert.Equal("4/8/8", bridgeConfig["observed_topology"]);
        Assert.Equal("6", bridgeConfig["observed_bulk_send_concurrency"]);
        Assert.Equal("round_robin", bridgeConfig["observed_bulk_send_mode"]);
        Assert.Equal("0", bridgeConfig["settings_match_expected"]);
    }

    [Theory]
    [MemberData(nameof(ThroughputLimiterFixtures))]
    [Trait("RetainedV4Suite", "ThroughputDecomposition")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ThroughputDecomposition_ClassifiesLimiter(string fixtureName, string expectedLimiter, string[] extraLines)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = "transfer_" + fixtureName;
        var lines = BuildCleanCompletedTransferFixture(transferId)
            .Concat(extraLines.Select(LogLine))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.True(throughput.ContainsKey("max_sender_raw_bytes_per_second"));
        Assert.True(throughput.ContainsKey("max_receiver_raw_bytes_per_second"));
        Assert.True(throughput.ContainsKey("max_contiguous_commit_bytes_per_second"));
        Assert.True(throughput.ContainsKey("max_bridge_bulk_payload_bytes_per_second"));
        Assert.True(throughput.ContainsKey("max_bridge_bulk_in_flight"));
        Assert.True(throughput.ContainsKey("max_bridge_bulk_configured_concurrency"));
        Assert.True(throughput.ContainsKey("max_bridge_bulk_effective_concurrency"));
        Assert.True(throughput.ContainsKey("sender_pipeline_summary_count"));
        Assert.True(throughput.ContainsKey("sender_feed_summary_count"));
        Assert.True(throughput.ContainsKey("max_sender_pipeline_effective_depth"));
        Assert.True(throughput.ContainsKey("max_sender_pipeline_in_flight_frames"));
        Assert.True(throughput.ContainsKey("sender_feed_raw_bytes_prepared"));
        Assert.True(throughput.ContainsKey("sender_feed_credit_wait_ratio_percent"));
        Assert.True(throughput.ContainsKey("receiver_feedback_pump_started_count"));
        Assert.True(throughput.ContainsKey("receiver_feedback_pump_active_count"));
        Assert.True(throughput.ContainsKey("slice_started_after_pump_start"));
        Assert.True(throughput.ContainsKey("receiver_feedback_enqueued_count"));
        Assert.True(throughput.ContainsKey("receiver_feedback_sent_count"));
        Assert.True(throughput.ContainsKey("receiver_feedback_coalesced_count"));
        Assert.True(throughput.ContainsKey("receiver_feedback_failed_count"));
        Assert.True(throughput.ContainsKey("max_receiver_feedback_queue_depth"));
        Assert.True(throughput.ContainsKey("max_receiver_feedback_enqueue_to_send_age_ms"));
        Assert.True(throughput.ContainsKey("max_receiver_feedback_send_duration_ms"));
        Assert.True(throughput.ContainsKey("grant_send_count"));
        Assert.True(throughput.ContainsKey("grant_delivery_bulk_count"));
        Assert.True(throughput.ContainsKey("average_effective_grant_window_bytes"));
        Assert.True(throughput.ContainsKey("grant_credit_base_sparse_count"));
        Assert.True(throughput.ContainsKey("grant_credit_base_contiguous_count"));
        Assert.True(throughput.ContainsKey("grant_base_blocked_by_gap_count"));
        Assert.True(throughput.ContainsKey("sparse_credit_topup_count"));
        Assert.True(throughput.ContainsKey("average_credit_remaining_bytes"));
        Assert.True(throughput.ContainsKey("max_sparse_credit_advance_bytes"));
        Assert.True(throughput.ContainsKey("sparse_credit_eligible_count"));
        Assert.True(throughput.ContainsKey("sparse_credit_used_count"));
        Assert.True(throughput.ContainsKey("sparse_credit_blocked_count"));
        Assert.True(throughput.ContainsKey("sparse_credit_reorder_use_ratio_percent"));
        Assert.True(throughput.ContainsKey("proactive_frontier_repair_eligible_count"));
        Assert.True(throughput.ContainsKey("proactive_frontier_repair_skipped_count"));
        Assert.True(throughput.ContainsKey("proactive_frontier_repair_sender_received_count"));
        Assert.True(throughput.ContainsKey("proactive_frontier_repair_sender_sent_count"));
        Assert.True(throughput.ContainsKey("proactive_frontier_repair_filled_count"));
        Assert.True(throughput.ContainsKey("max_frontier_repair_request_to_fill_ms"));
        Assert.True(throughput.ContainsKey("proactive_repair_benign_count"));
        Assert.True(throughput.ContainsKey("proactive_repair_grace_active_count"));
        Assert.True(throughput.ContainsKey("proactive_repair_repeated_unfilled_count"));
        Assert.True(throughput.ContainsKey("proactive_repair_hard_limited_count"));
        Assert.True(throughput.ContainsKey("proactive_repair_hard_limited_during_grace_count"));
        Assert.True(throughput.ContainsKey("max_proactive_repair_age_ms"));
        Assert.True(throughput.ContainsKey("max_same_frontier_unfilled_ms"));
        Assert.True(throughput.ContainsKey("stale_proactive_repair_state_reset_count"));
        Assert.True(throughput.ContainsKey("benign_gap_skip_limited_policy_count"));
        Assert.True(throughput.ContainsKey("max_sparse_write_bytes_per_second"));
        Assert.True(throughput.ContainsKey("max_sparse_written_ahead_bytes"));
        Assert.True(throughput.ContainsKey("max_sparse_gap_count"));

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal(expectedLimiter, decomposition["likely_limiter"]);
        Assert.True(decomposition.ContainsKey("max_bridge_bulk_in_flight"));
        Assert.True(decomposition.ContainsKey("max_bridge_bulk_effective_concurrency"));
        Assert.True(decomposition.ContainsKey("sender_pipeline_sample_count"));
        Assert.True(decomposition.ContainsKey("sender_feed_sample_count"));
        Assert.True(decomposition.ContainsKey("max_sender_pipeline_effective_depth"));
        Assert.True(decomposition.ContainsKey("max_sender_pipeline_in_flight_frames"));
        Assert.True(decomposition.ContainsKey("max_bridge_bulk_worker_saturation_percent"));
        Assert.True(decomposition.ContainsKey("sender_feed_credit_wait_ratio_percent"));
        Assert.True(decomposition.ContainsKey("sender_grant_apply_count"));
        Assert.True(decomposition.ContainsKey("sender_grant_apply_async_pump_count"));
        Assert.True(decomposition.ContainsKey("max_sender_grant_apply_credit_wait_active_ms"));
        Assert.True(decomposition.ContainsKey("max_sender_grant_apply_available_credit_bytes_after"));
        Assert.True(decomposition.ContainsKey("sender_credit_stall_summary_count"));
        Assert.True(decomposition.ContainsKey("max_sender_credit_stall_active_ms"));
        Assert.True(decomposition.ContainsKey("max_sender_credit_stall_last_grant_age_ms"));
        Assert.True(decomposition.ContainsKey("receiver_grant_decision_summary_count"));
        Assert.True(decomposition.ContainsKey("receiver_grant_decision_should_grant_count"));
        Assert.True(decomposition.ContainsKey("receiver_grant_decision_no_send_count"));
        Assert.True(decomposition.ContainsKey("receiver_grant_decision_coalesce_blocked_count"));
        Assert.True(decomposition.ContainsKey("receiver_feedback_pump_started_count"));
        Assert.True(decomposition.ContainsKey("receiver_feedback_pump_active_count"));
        Assert.True(decomposition.ContainsKey("slice_started_after_pump_start"));
        Assert.True(decomposition.ContainsKey("receiver_feedback_enqueued_count"));
        Assert.True(decomposition.ContainsKey("receiver_feedback_sent_count"));
        Assert.True(decomposition.ContainsKey("receiver_feedback_coalesced_count"));
        Assert.True(decomposition.ContainsKey("receiver_feedback_failed_count"));
        Assert.True(decomposition.ContainsKey("max_receiver_feedback_queue_depth"));
        Assert.True(decomposition.ContainsKey("max_receiver_feedback_enqueue_to_send_age_ms"));
        Assert.True(decomposition.ContainsKey("max_receiver_feedback_send_duration_ms"));
        Assert.True(decomposition.ContainsKey("grant_send_rate_per_second"));
        Assert.True(decomposition.ContainsKey("grant_delivery_bulk_count"));
        Assert.True(decomposition.ContainsKey("average_effective_grant_window_bytes"));
        Assert.True(decomposition.ContainsKey("file_only_reorder_soft_limited_sample_count"));
        Assert.True(decomposition.ContainsKey("grant_base_sparse_ahead_count"));
        Assert.True(decomposition.ContainsKey("grant_base_contiguous_frontier_count"));
        Assert.True(decomposition.ContainsKey("grant_base_gap_stall_count"));
        Assert.True(decomposition.ContainsKey("grant_base_blocked_by_gap_count"));
        Assert.True(decomposition.ContainsKey("grant_credit_base_sparse_count"));
        Assert.True(decomposition.ContainsKey("grant_credit_base_contiguous_count"));
        Assert.True(decomposition.ContainsKey("sparse_credit_topup_count"));
        Assert.True(decomposition.ContainsKey("average_credit_remaining_bytes"));
        Assert.True(decomposition.ContainsKey("max_sparse_credit_advance_bytes"));
        Assert.True(decomposition.ContainsKey("sparse_credit_eligible_count"));
        Assert.True(decomposition.ContainsKey("sparse_credit_used_count"));
        Assert.True(decomposition.ContainsKey("sparse_credit_blocked_count"));
        Assert.True(decomposition.ContainsKey("sparse_credit_reorder_use_ratio_percent"));
        Assert.True(decomposition.ContainsKey("proactive_frontier_repair_eligible_count"));
        Assert.True(decomposition.ContainsKey("proactive_frontier_repair_skipped_count"));
        Assert.True(decomposition.ContainsKey("proactive_frontier_repair_sender_received_count"));
        Assert.True(decomposition.ContainsKey("proactive_frontier_repair_sender_sent_count"));
        Assert.True(decomposition.ContainsKey("proactive_frontier_repair_filled_count"));
        Assert.True(decomposition.ContainsKey("max_frontier_repair_request_to_fill_ms"));
        Assert.True(decomposition.ContainsKey("proactive_repair_benign_count"));
        Assert.True(decomposition.ContainsKey("proactive_repair_grace_active_count"));
        Assert.True(decomposition.ContainsKey("proactive_repair_repeated_unfilled_count"));
        Assert.True(decomposition.ContainsKey("proactive_repair_hard_limited_count"));
        Assert.True(decomposition.ContainsKey("proactive_repair_hard_limited_during_grace_count"));
        Assert.True(decomposition.ContainsKey("max_proactive_repair_age_ms"));
        Assert.True(decomposition.ContainsKey("max_same_frontier_unfilled_ms"));
        Assert.True(decomposition.ContainsKey("average_effective_grant_window_bytes_healthy_expanded"));
        Assert.True(decomposition.ContainsKey("average_effective_grant_window_bytes_healthy_file_only_soft_limited"));
        Assert.True(decomposition.ContainsKey("sticky_limited_without_pressure_count"));
        Assert.True(decomposition.ContainsKey("limited_recovery_fast_exit_count"));
        Assert.True(decomposition.ContainsKey("stale_proactive_repair_state_reset_count"));
        Assert.True(decomposition.ContainsKey("benign_gap_skip_limited_policy_count"));
        Assert.True(decomposition.ContainsKey("max_limited_recovery_clean_ms"));
        Assert.True(decomposition.ContainsKey("limited_recovery_block_none_count"));
        Assert.True(decomposition.ContainsKey("max_file_only_target_window_bytes"));
        Assert.True(decomposition.ContainsKey("file_only_sparse_window_capacity_proven"));
        Assert.True(decomposition.ContainsKey("adaptive_window_underprovisioned_signal"));
        Assert.True(decomposition.ContainsKey("fixed_file_only_window_active_count"));
        Assert.True(decomposition.ContainsKey("max_fixed_file_only_window_bytes"));
        Assert.True(decomposition.ContainsKey("cycle_goodput_average_bytes_per_second"));
        Assert.True(decomposition.ContainsKey("gui_progress_timeout_count"));
        Assert.True(decomposition.ContainsKey("terminal_missing_after_progress_timeout"));
        Assert.True(decomposition.ContainsKey("progress_timeout_with_receiver_gap_stall"));
        Assert.True(decomposition.ContainsKey("artifact_slice_start_reason"));
        Assert.True(decomposition.ContainsKey("artifact_slice_end_reason"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_HealthyLimitedInsideRecoveryHold_IsNotStickyLimited()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_limited_recovery_hold";
        var lines = BuildCleanCompletedTransferFixture(transferId)
            .Append(LogLine("event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_limited_recovery_hold; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=8; chunk_count_prepared=22; raw_bytes_prepared=473088; read_duration_ms=0; batch_prepare_duration_ms=0; send_async_schedule_duration_ms=5; inter_schedule_gap_p95_ms=500; inter_schedule_gap_max_ms=500; credit_wait_duration_ms=1500; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0"))
            .Append(LogLine("event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_limited_recovery_hold; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=2472960; raw_bytes_received_per_second=1236480; contiguous_bytes_committed=2408448; contiguous_bytes_committed_per_second=1204224; pending_chunk_count=0; pending_bytes=0; next_chunk_index=1470; highest_received_chunk_index=1478; late_arrival_distance=8; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=1538; granted_window_bytes=1462272; write_batch_count=39; write_batch_bytes=2472960; write_duration_ms=0; sparse_mode=1; sparse_write_bytes_per_second=1236480; sparse_written_ahead_bytes=64512; sparse_gap_count=0"))
            .Append(LogLine("event=filetransfer_v4_grant_window_summary; transfer_id=transfer_limited_recovery_hold; session_id=sess_a; reason=ack_only; file_only_sparse_cadence=1; profile=healthy_limited; target_window_bytes=537600; effective_granted_window_bytes=1462272; current_credit_chunks=68; desired_credit_chunks=68; low_watermark_credit_chunks=62; credit_remaining_chunks=68; credit_desired_chunks=68; credit_remaining_bytes=1462272; credit_desired_bytes=1462272; granted_until_chunk_index_exclusive=1538; target_granted_until_chunk_index_exclusive=1538; grant_base_chunk_index=1470; grant_base_reason=sparse_ahead; sparse_ahead_bytes=129024; credit_base_chunk_index=1470; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_hold_active=0; sparse_credit_eligible=1; sparse_credit_advance_bytes=2623488; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); proactive_repair_pressure_state=(none); proactive_repair_age_ms=400; same_frontier_unfilled_ms=0; limited_recovery_clean_ms=400; limited_recovery_hold_ms=750; limited_recovery_block_reason=(none); fixed_file_only_window_active=0; fixed_file_only_window_bytes=0; next_chunk_index=1464; highest_received_chunk_index=1469; late_arrival_distance=5; pending_chunk_count=0; pending_bytes=0"))
            .Append(LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=8; frames_enqueued=8; payload_bytes_sent=520000; payload_bytes_per_second=260000; payload_bytes_enqueued=520000; payload_bytes_enqueued_per_second=260000; inter_enqueue_gap_p95_ms=500; inter_enqueue_gap_max_ms=500; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=16; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=8; sample_window_ms=2000"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);
        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");

        Assert.NotEqual("sticky_limited_without_pressure", decomposition["likely_limiter"]);
        Assert.Equal("0", decomposition["sticky_limited_without_pressure_count"]);
        Assert.Equal("1", decomposition["limited_recovery_block_none_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PumpModeEventsInferActiveReceiverFeedbackPump()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_pump_inferred")
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_enqueued; transfer_id=transfer_pump_inferred; session_id=sess_a; mode=pump; frame_type=filetransfer.receiver_state.v6; queue_depth=2; coalesced_count=1"))
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_sent; transfer_id=transfer_pump_inferred; session_id=sess_a; mode=pump; frame_type=filetransfer.receiver_state.v6; queue_depth=1; enqueue_to_send_age_ms=900; send_duration_ms=120"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("0", throughput["receiver_feedback_pump_started_count"]);
        Assert.Equal("1", throughput["receiver_feedback_pump_active_count"]);
        Assert.Equal("1", throughput["slice_started_after_pump_start"]);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("1", decomposition["receiver_feedback_pump_active_count"]);
        Assert.Equal("1", decomposition["slice_started_after_pump_start"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ProgressTimeoutAfterSecureDataEnvelopeWithoutDispatch_ClassifiesDispatchMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_data_dispatch_missing";
        var lines = new[]
        {
            LogLine($"event=nkn_inbound_envelope_received; channel=bulk; reason=(none); envelope_type=file_transfer_data_frame; payload_len=64917; envelope_payload_len=64840; msg_id=msg1; source_len=84; source_matches_local=0; expected_source_available=1; source_matches_expected=1; is_topic=0"),
            LogLine($"event=filetransfer_envelope_received; transport=nkn; message_type=file_transfer_data_frame; transfer_id={transferId}; source=nlink-test-bulk.example"),
            LogLine("event=nkn_bridge_bulk_send_summary; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; frames_sent=200; send_failures=0; queue_clears=0; payload_bytes_sent=65536000; payload_bytes_per_second=3276800; send_p95_ms=4; configured_concurrency=4; effective_concurrency=4; in_flight_max=3"),
            LogLine($"event=filetransfer_live_progress_timeout; transfer_id={transferId}; reason=no useful data progress for 120s; total_wait_s=379; progress_timeout_seconds=120; receiver_next_chunk=-1; receiver_highest_chunk=-1; progress_events=2855"),
            LogLine($"event=filetransfer_artifact_slice_summary; transfer_id={transferId}; artifact_slice_start_reason=live_soak_failure_context; artifact_slice_end_reason=gui_progress_timeout")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("INCONCLUSIVE_PROGRESS_TIMEOUT", verdict["verdict"]);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("filetransfer_data_session_dispatch_missing", decomposition["likely_limiter"]);
        Assert.Equal("1", decomposition["inbound_filetransfer_data_frame_envelope_received_count"]);
        Assert.Equal("1", decomposition["filetransfer_secure_data_frame_envelope_received_count"]);
        Assert.Equal("0", decomposition["filetransfer_data_frame_dispatched_count"]);
        Assert.Equal("1", decomposition["filetransfer_data_frame_dispatch_missing_count"]);

        var promotion = ReadArtifactReport(result.ArtifactDir, "v4-promotion-decision.txt");
        Assert.Equal("hold_inconclusive", promotion["decision"]);
        Assert.Equal("filetransfer_data_session_dispatch_missing", promotion["reason"]);
        Assert.Equal("transport_data_session_lifecycle_dispatch", promotion["next_focus"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SparseCreditGrantSummary_ReportsCreditBaseAndTopup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_sparse_credit")
            .Append(LogLine("event=filetransfer_v4_grant_window_summary; transfer_id=transfer_sparse_credit; session_id=sess_a; reason=sparse_credit_topup; file_only_sparse_cadence=1; profile=healthy_expanded; target_window_bytes=8388608; effective_granted_window_bytes=8440320; current_credit_chunks=120; desired_credit_chunks=392; low_watermark_credit_chunks=353; credit_remaining_chunks=120; credit_desired_chunks=392; credit_remaining_bytes=2580480; credit_desired_bytes=8429568; granted_until_chunk_index_exclusive=1272; target_granted_until_chunk_index_exclusive=1272; target_base_chunk_index=880; target_base_reason=sparse_ahead; grant_base_chunk_index=880; grant_base_reason=sparse_ahead; sparse_ahead_bytes=3870720; credit_base_chunk_index=880; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_hold_active=0; sparse_credit_eligible=1; sparse_credit_advance_bytes=301056; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); next_chunk_index=700; highest_received_chunk_index=879; late_arrival_distance=179; pending_chunk_count=0; pending_bytes=0"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("1", throughput["grant_credit_base_sparse_count"]);
        Assert.Equal("0", throughput["grant_credit_base_contiguous_count"]);
        Assert.Equal("1", throughput["sparse_credit_topup_count"]);
        Assert.Equal("2580480.00", throughput["average_credit_remaining_bytes"]);
        Assert.Equal("301056", throughput["max_sparse_credit_advance_bytes"]);
        Assert.Equal("1", throughput["sparse_credit_eligible_count"]);
        Assert.Equal("1", throughput["sparse_credit_used_count"]);
        Assert.Equal("0", throughput["sparse_credit_blocked_count"]);
        Assert.Equal("100.000", throughput["sparse_credit_reorder_use_ratio_percent"]);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("1", decomposition["grant_credit_base_sparse_count"]);
        Assert.Equal("1", decomposition["sparse_credit_topup_count"]);
        Assert.Equal("100.000", decomposition["sparse_credit_reorder_use_ratio_percent"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PayloadEfficiencySummary_UsesDominantConcreteBatchProfile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_payload_profile")
            .Append(LogLine("event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id=transfer_payload_profile; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.06"))
            .Append(LogLine("event=filetransfer_transport_payload_budget; transport=nkn; transfer_id=transfer_payload_profile; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v6; lane=bulk; serialized_payload_bytes=64615; secure_payload_bytes=64840; bridge_payload_bytes=64917; bridge_command_bytes=65017; max_allowed_bytes=65536; batch_profile=Packed3x21KiB; batch_chunk_count=3; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.06"))
            .Append(LogLine("event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id=transfer_payload_profile; session_id=sess_a; chunk_range=3-5; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.06"))
            .Append(LogLine("event=filetransfer_transport_payload_budget; transport=nkn; transfer_id=transfer_payload_profile; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v6; lane=bulk; serialized_payload_bytes=21595; secure_payload_bytes=21820; bridge_payload_bytes=21897; bridge_command_bytes=21997; max_allowed_bytes=65536; batch_profile=Current; batch_chunk_count=1; raw_to_bridge_payload_ratio=0.982; bridge_payload_fill_percent=33.41"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var payload = ReadArtifactReport(result.ArtifactDir, "payload-efficiency-summary.txt");
        Assert.Equal("Packed3x21KiB", payload["payload_efficiency_profile"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FramesWithoutTerminal_ReturnsInconclusive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync([
            LogLine("event=filetransfer_binary_frame_sent; transfer_id=transfer_open; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; raw_chunk_bytes=49152; chunk_count=2")
        ]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("INCONCLUSIVE", verdict["verdict"]);
        Assert.Equal("transfer-terminal-summary.txt", verdict["next_artifact"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostCompletionLateSenderIgnored_DoesNotFailCleanCompletion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_post_completion_late_sender";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Append(LogLine($"event=filetransfer_data_frame_ignored; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; chunk_index=0; reason=post_completion_late_sender_frame; source=nlink-helper-bulk.test; msg_id=late_frame_after_terminal"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var invariant = ReadArtifactReport(result.ArtifactDir, "filetransfer-invariant-summary.txt");
        Assert.Equal("benign_after_clean_completion", invariant["late_frame_classification"]);
        Assert.Equal("pass", invariant["terminal_lifecycle_invariant_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostCompletionLifecycleDataFrameReject_DoesNotFailCleanCompletion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_post_completion_lifecycle_reject";
        var lines = BuildCleanCompletedV4TransferFixture(transferId).ToList();
        var inboundTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(inboundTerminalIndex > 0);
        lines.Insert(
            inboundTerminalIndex + 1,
            LogLine($"event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=lifecycle_data_frame_unsupported; session_id=sess_a; transfer_id={transferId}; source=nlink-helper-bulk.test; msg_id=late_lifecycle_after_terminal"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var invariant = ReadArtifactReport(result.ArtifactDir, "filetransfer-invariant-summary.txt");
        Assert.Equal("benign_after_clean_completion", invariant["late_frame_classification"]);
        Assert.Equal("pass", invariant["terminal_lifecycle_invariant_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LateDataAfterSessionEndCancel_FailsTerminalLifecycleInvariant()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_late_after_session_end";
        var lines = BuildRouteAwareCompletedFixture(
                transferId,
                route: "regular_nkn_v4_fast",
                protocolVersion: 4,
                runtimeProfile: "regular_nkn_v4_fast",
                bridgeRecoveryPolicy: "regular_nkn_v4_fast",
                runtimeEventName: "filetransfer_v4_sender_started")
            .Where(line => !line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal) &&
                           !line.Contains("event=file_transfer_outbound_terminal", StringComparison.Ordinal))
            .Concat(new[]
            {
                LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=CanceledRemote; error_code=session_end; status_message=session_end; saved_path=(none)"),
                LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=CanceledLocal; error_code=session_end; status_message=session_end"),
                LogLine($"event=filetransfer_data_frame_ignored; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v4; chunk_index=0; reason=post_completion_late_sender_frame; source=nlink-helper-bulk.test; msg_id=late_frame_after_session_end")
            })
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("terminal_lifecycle", verdict["acceptance_failure_class"]);
        var invariant = ReadArtifactReport(result.ArtifactDir, "filetransfer-invariant-summary.txt");
        Assert.Equal("remote_session_end", invariant["terminal_winner"]);
        Assert.Equal("non_benign_after_terminal", invariant["late_frame_classification"]);
        Assert.Equal("fail", invariant["terminal_lifecycle_invariant_verdict"]);
        Assert.Equal("terminal_lifecycle", invariant["invariant_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ControlPriorityLocalOnlyUnderBacklog_FailsInvariant()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_control_priority_local_only";
        var lines = BuildRouteAwareCompletedFixture(
                transferId,
                route: "regular_nkn_v4_fast",
                protocolVersion: 4,
                runtimeProfile: "regular_nkn_v4_fast",
                bridgeRecoveryPolicy: "regular_nkn_v4_fast",
                runtimeEventName: "filetransfer_v4_sender_started")
            .Append(LogLine($"event=filetransfer_control_priority_delivery_result; kind=session_end; transfer_id={transferId}; session_id=sess_a; control_queue=1; control_copy=0; bulk_copy=0; peer_visible_any=0; accepted_any=1; local_only_rejected=1; data_backlog=1"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("lifecycle_control_priority", verdict["acceptance_failure_class"]);
        Assert.Equal("filetransfer-invariant-summary.txt", verdict["next_artifact"]);
        var invariant = ReadArtifactReport(result.ArtifactDir, "filetransfer-invariant-summary.txt");
        Assert.Equal("fail", invariant["control_priority_invariant_verdict"]);
        Assert.Equal("lifecycle_control_priority", invariant["invariant_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ControlPriorityPeerVisibleUnderBacklog_PassesInvariant()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_control_priority_peer_visible";
        var lines = BuildRouteAwareCompletedFixture(
                transferId,
                route: "regular_nkn_v4_fast",
                protocolVersion: 4,
                runtimeProfile: "regular_nkn_v4_fast",
                bridgeRecoveryPolicy: "regular_nkn_v4_fast",
                runtimeEventName: "filetransfer_v4_sender_started")
            .Append(LogLine($"event=filetransfer_control_priority_delivery_result; kind=session_end; transfer_id={transferId}; session_id=sess_a; control_queue=1; control_copy=1; bulk_copy=0; peer_visible_any=1; accepted_any=1; local_only_rejected=0; data_backlog=1"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("none", verdict["acceptance_failure_class"]);
        var invariant = ReadArtifactReport(result.ArtifactDir, "filetransfer-invariant-summary.txt");
        Assert.Equal("pass", invariant["control_priority_invariant_verdict"]);
        Assert.Equal("none", invariant["invariant_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeOwnerForbiddenStaleEffect_FailsInvariant()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_owner_forbidden_effect";
        var lines = BuildRouteAwareCompletedFixture(
                transferId,
                route: "regular_nkn_v4_fast",
                protocolVersion: 4,
                runtimeProfile: "regular_nkn_v4_fast",
                bridgeRecoveryPolicy: "regular_nkn_v4_fast",
                runtimeEventName: "filetransfer_v4_sender_started")
            .Append(LogLine($"event=filetransfer_runtime_owner_forbidden_stale_effect; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=v4_peer_silence_safety_replay; attempted_action=schedule_bridge_recovery; reason=stale_owner_mutated_state; runtime_generation=1; current_runtime_generation=2; terminal_generation=0; current_terminal_generation=0; route=regular_nkn_v4_fast; current_route=post_tuna_fallback_v6; protocol_version=4; current_protocol_version=6"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("runtime_owner", verdict["acceptance_failure_class"]);
        var invariant = ReadArtifactReport(result.ArtifactDir, "filetransfer-invariant-summary.txt");
        Assert.Equal("fail", invariant["runtime_owner_invariant_verdict"]);
        Assert.Equal("2", invariant["runtime_owner_generation"]);
        Assert.Equal("1", invariant["runtime_owner_forbidden_stale_effect_count"]);
        Assert.Equal("runtime_owner", invariant["invariant_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FailOnGate_ReturnsNonZeroForHardFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_fail_gate")
            .Append(LogLine("event=filetransfer_data_frame_decode_failed; transport=nkn; transfer_id=transfer_fail_gate; session_id=sess_a"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines, ["-FailOnGate"]);

        Assert.Equal(1, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_HighReorderButCompleted_ReturnsRecoveredPressureWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_reorder")
            .Append(LogLine("event=filetransfer_v4_throughput_summary; transfer_id=transfer_reorder; session_id=sess_a; useful_payload_bytes_per_second=4000000; control_frames_per_mib=1.00; granted_window_bytes=4194304; chunk_size_bytes=24576; profile=healthy_expanded"))
            .Concat(Enumerable.Range(0, 50).Select(index =>
                LogLine($"event=filetransfer_reorder_pressure; transfer_id=transfer_reorder; session_id=sess_a; next_expected_chunk={index}; highest_received_chunk={index + 82}; late_arrival_distance=82; outstanding_count=4; pipeline_depth=4; chunk_size_bytes=24576")))
            .Append(LogLine("event=filetransfer_v4_profile_changed; transfer_id=transfer_reorder; session_id=sess_a; previous_profile=healthy_expanded; updated_profile=healthy_limited; reason=high_reorder; target_window_bytes=524288; granted_window_bytes=4194304; late_arrival_distance=82; timeout_streak=0"))
            .Concat(Enumerable.Range(50, 50).Select(index =>
                LogLine($"event=filetransfer_reorder_pressure; transfer_id=transfer_reorder; session_id=sess_a; next_expected_chunk={index}; highest_received_chunk={index + 82}; late_arrival_distance=82; outstanding_count=4; pipeline_depth=4; chunk_size_bytes=24576")))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_RECOVERED_PRESSURE", verdict["verdict"]);
        Assert.Equal("repair-reorder-summary.txt", verdict["next_artifact"]);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("100", repair["reorder_event_count"]);
        Assert.Equal("82", repair["max_late_arrival_distance"]);
        Assert.Equal("82", repair["p95_late_arrival_distance"]);
        Assert.Equal("4194304", repair["max_v4_granted_window_bytes"]);
        Assert.Equal("1", repair["v4_profile_changed_count"]);
        Assert.Equal("50", repair["reorder_by_profile.healthy_expanded.count"]);
        Assert.Equal("50", repair["reorder_by_profile.healthy_limited.count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_CleanConservativeStartupFastRamp_ReportsStartupEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_startup_fast_ramp")
            .Append(LogLine("event=filetransfer_v4_profile_changed; transfer_id=transfer_startup_fast_ramp; session_id=sess_a; previous_profile=nkn_conservative_startup; updated_profile=nkn_conservative_startup_probe; reason=startup_probe; target_window_bytes=1048576; granted_window_bytes=524288; late_arrival_distance=0; timeout_streak=0; conservative_startup_duration_ms=550; bytes_before_startup_exit=524288; startup_exit_reason=(none); startup_probe_window_bytes=1048576; first_repair_or_timeout_before_startup_exit=0"))
            .Append(LogLine("event=filetransfer_v4_profile_changed; transfer_id=transfer_startup_fast_ramp; session_id=sess_a; previous_profile=nkn_conservative_startup_probe; updated_profile=healthy; reason=startup_fast_clean; target_window_bytes=2097152; granted_window_bytes=1048576; late_arrival_distance=0; timeout_streak=0; conservative_startup_duration_ms=1100; bytes_before_startup_exit=1048576; startup_exit_reason=startup_fast_clean; startup_probe_window_bytes=0; first_repair_or_timeout_before_startup_exit=0"))
            .Append(LogLine("event=filetransfer_v4_throughput_summary; transfer_id=transfer_startup_fast_ramp; session_id=sess_a; useful_payload_bytes_per_second=5000000; control_frames_per_mib=1.00; granted_window_bytes=2097152; chunk_size_bytes=24576; profile=healthy; conservative_startup_duration_ms=1100; bytes_before_startup_exit=1048576; startup_exit_reason=startup_fast_clean; startup_probe_window_bytes=0; first_repair_or_timeout_before_startup_exit=0"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("1", throughput["conservative_startup_probe_count"]);
        Assert.Equal("1", throughput["conservative_startup_fast_clean_count"]);
        Assert.Equal("0", throughput["conservative_startup_adverse_count"]);
        Assert.Equal("1100", throughput["max_conservative_startup_duration_ms"]);
        Assert.Equal("1048576", throughput["max_bytes_before_startup_exit"]);
        Assert.Equal("1048576", throughput["max_startup_probe_window_bytes"]);
        Assert.Equal("0", throughput["first_repair_or_timeout_before_startup_exit_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ReceiverBufferPressureButCompleted_ReturnsRecoveredPressureWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_receiver_pressure")
            .Append(LogLine("event=filetransfer_receiver_buffer_pressure_entered; transfer_id=transfer_receiver_pressure; session_id=sess_a; reason=soft_limit; pending_chunk_count=2100; pending_bytes=8601600; soft_limit_bytes=8388608; severe_limit_bytes=16777216; emergency_limit_bytes=67108864; next_chunk_index=0; highest_received_chunk_index=2100; late_arrival_distance=2100; granted_window_bytes=2097152"))
            .Append(LogLine("event=filetransfer_receiver_grant_clamped_for_buffer; transfer_id=transfer_receiver_pressure; session_id=sess_a; reason=soft_limit; pending_chunk_count=2100; pending_bytes=8601600; previous_target_granted_until_exclusive=512; clamped_target_granted_until_exclusive=128; next_chunk_index=0; highest_received_chunk_index=2100; late_arrival_distance=2100; granted_window_bytes=2097152; soft_limit_bytes=8388608; severe_limit_bytes=16777216"))
            .Append(LogLine("event=filetransfer_receiver_write_batch_committed; transfer_id=transfer_receiver_pressure; session_id=sess_a; batch_chunk_count=64; batch_bytes=262144; write_duration_ms=12; pending_chunk_count=2036; pending_bytes=8339456; next_chunk_index=64; highest_received_chunk_index=2100; late_arrival_distance=2036; granted_window_bytes=1835008"))
            .Append(LogLine("event=filetransfer_receiver_buffer_pressure_exited; transfer_id=transfer_receiver_pressure; session_id=sess_a; pending_chunk_count=1000; pending_bytes=4096000; duration_ms=1250; exit_limit_bytes=4194304; next_chunk_index=1100; highest_received_chunk_index=2100; late_arrival_distance=1000; granted_window_bytes=524288"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_RECOVERED_PRESSURE", verdict["verdict"]);
        Assert.Equal("repair-reorder-summary.txt", verdict["next_artifact"]);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["receiver_buffer_pressure_entered_count"]);
        Assert.Equal("1", repair["receiver_buffer_pressure_exited_count"]);
        Assert.Equal("1", repair["receiver_buffer_grant_clamped_count"]);
        Assert.Equal("1", repair["receiver_write_batch_committed_count"]);
        Assert.Equal("8601600", repair["max_receiver_pending_bytes"]);
        Assert.Equal("262144", repair["max_receiver_write_batch_bytes"]);
        Assert.Equal("12", repair["max_receiver_write_duration_ms"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SparseReceiverTelemetry_PopulatesStableSummaryKeys()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_sparse_receiver")
            .Append(LogLine("event=filetransfer_receiver_sparse_mode_selected; transfer_id=transfer_sparse_receiver; session_id=sess_a; reason=seekable_readable_destination; stream_can_read=1; stream_can_seek=1; stream_can_write=1; file_size_bytes=1048576; chunk_count=256; chunk_size_bytes=4096"))
            .Append(LogLine("event=filetransfer_receiver_sparse_write_summary; transfer_id=transfer_sparse_receiver; session_id=sess_a; written_chunk_count=64; written_bytes=262144; sparse_write_bytes_per_second=5242880; write_duration_ms=50; pending_chunk_count=0; pending_bytes=0; queued_memory_bytes=0; sparse_written_ahead_chunks=63; sparse_written_ahead_bytes=258048; sparse_gap_count=1; next_chunk_index=0; highest_received_chunk_index=64; late_arrival_distance=64; granted_window_bytes=1048576"))
            .Append(LogLine("event=filetransfer_receiver_sparse_commit_summary; transfer_id=transfer_sparse_receiver; session_id=sess_a; contiguous_chunks_committed=64; contiguous_bytes_committed=262144; next_chunk_index=64; bytes_committed=262144; sparse_written_ahead_chunks=0; sparse_written_ahead_bytes=0; sparse_gap_count=0; pending_chunk_count=0; pending_bytes=0; queued_memory_bytes=0; highest_received_chunk_index=64; late_arrival_distance=0; granted_window_bytes=1048576"))
            .Append(LogLine("event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_sparse_receiver; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1048576; raw_bytes_received_per_second=524288; contiguous_bytes_committed=1048576; contiguous_bytes_committed_per_second=524288; pending_chunk_count=0; pending_bytes=0; next_chunk_index=256; highest_received_chunk_index=255; late_arrival_distance=0; oldest_gap_age_ms=0; granted_until_chunk_index_exclusive=256; granted_window_bytes=0; write_batch_count=4; write_batch_bytes=1048576; write_duration_ms=40; sparse_mode=1; sparse_write_bytes_per_second=524288; sparse_written_ahead_bytes=258048; sparse_gap_count=1"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("5242880", throughput["max_sparse_write_bytes_per_second"]);
        Assert.Equal("258048", throughput["max_sparse_written_ahead_bytes"]);
        Assert.Equal("1", throughput["max_sparse_gap_count"]);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["receiver_sparse_mode_selected_count"]);
        Assert.Equal("1", repair["receiver_sparse_write_summary_count"]);
        Assert.Equal("1", repair["receiver_sparse_commit_summary_count"]);
        Assert.Equal("258048", repair["max_sparse_written_ahead_bytes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ReceiverFeedbackPumpTelemetry_PopulatesStableSummaryKeys()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_receiver_feedback")
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_pump_started; transfer_id=transfer_receiver_feedback; session_id=sess_a; queue_limit=64; mode=pump"))
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_enqueued; transfer_id=transfer_receiver_feedback; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; reason=low_watermark; mode=pump; queue_depth=1; queue_limit=64"))
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_coalesced; transfer_id=transfer_receiver_feedback; session_id=sess_a; previous_frame_type=filetransfer.receiver_state.v6; frame_type=filetransfer.receiver_state.v6; reason=ack_only; mode=pump; queue_depth=1; coalesced_count=1"))
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_sent; transfer_id=transfer_receiver_feedback; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; reason=ack_only; mode=pump; send_duration_ms=23; enqueue_to_send_age_ms=117; queue_depth=0"))
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_summary; transfer_id=transfer_receiver_feedback; session_id=sess_a; mode=pump; queue_depth=0; queue_limit=64; enqueued=2; sent=1; coalesced=1; failed=0; max_queue_depth=1; max_enqueue_to_send_age_ms=117; max_send_duration_ms=23"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("1", throughput["receiver_feedback_pump_started_count"]);
        Assert.Equal("1", throughput["receiver_feedback_enqueued_count"]);
        Assert.Equal("1", throughput["receiver_feedback_sent_count"]);
        Assert.Equal("1", throughput["receiver_feedback_coalesced_count"]);
        Assert.Equal("0", throughput["receiver_feedback_failed_count"]);
        Assert.Equal("1", throughput["max_receiver_feedback_queue_depth"]);
        Assert.Equal("117", throughput["max_receiver_feedback_enqueue_to_send_age_ms"]);
        Assert.Equal("23", throughput["max_receiver_feedback_send_duration_ms"]);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["receiver_feedback_pump_started_count"]);
        Assert.Equal("1", repair["receiver_feedback_sent_count"]);
        Assert.Equal("1", repair["receiver_feedback_coalesced_count"]);
        Assert.Equal("0", repair["receiver_feedback_failed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SlowDirectReceiverFeedback_ClassifiesAsReceiverFeedbackBlocking()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_receiver_feedback_blocking")
            .Append(LogLine("event=filetransfer_payload_efficiency_profile_selected; transfer_id=transfer_receiver_feedback_blocking; session_id=sess_a; profile=Packed3x21KiB; chunk_size_bytes=21504; max_batch_chunks=3; target_raw_batch_bytes=64512; reason=nkn_file_only_default"))
            .Append(LogLine("event=filetransfer_chunk_batch_sent_as_batch; transfer_id=transfer_receiver_feedback_blocking; session_id=sess_a; chunk_range=0-2; chunk_frame_count=3; batch_chunk_count=3; raw_bytes=64512; lane=bulk; batch_profile=Packed3x21KiB; raw_to_bridge_payload_ratio=0.994; bridge_payload_fill_percent=99.0"))
            .Append(LogLine("event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_receiver_feedback_blocking; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1806336; raw_bytes_per_second=903168; chunk_frames_sent=0; batch_frames_sent=28; chunk_count_sent=84; chunks_accepted_for_transport=900; remote_next_expected_chunk_index=500; remote_granted_until_chunk_index_exclusive=1272; remote_granted_window_bytes=16602112; sent_cache_chunk_count=400; sent_cache_bytes=8601600; send_wait_count=8; repair_send_count=0"))
            .Append(LogLine("event=filetransfer_v4_sender_feed_summary; transfer_id=transfer_receiver_feedback_blocking; session_id=sess_a; sample_window_ms=2000; chunk_frames_prepared=0; batch_frames_prepared=28; chunk_count_prepared=84; raw_bytes_prepared=1806336; read_duration_ms=3; batch_prepare_duration_ms=5; send_async_schedule_duration_ms=4; inter_schedule_gap_p95_ms=42; inter_schedule_gap_max_ms=80; credit_wait_duration_ms=1700; pipeline_slot_wait_duration_ms=0; effective_depth=8; pending_bytes=0; pending_bytes_limit=2097152; source_read_error_count=0"))
            .Append(LogLine("event=filetransfer_v4_receiver_throughput_summary; transfer_id=transfer_receiver_feedback_blocking; session_id=sess_a; sample_window_ms=2000; raw_bytes_received=1806336; raw_bytes_received_per_second=903168; contiguous_bytes_committed=1806336; contiguous_bytes_committed_per_second=903168; pending_chunk_count=0; pending_bytes=0; next_chunk_index=500; highest_received_chunk_index=879; late_arrival_distance=379; oldest_gap_age_ms=700; granted_until_chunk_index_exclusive=1272; granted_window_bytes=8429568; write_batch_count=2; write_batch_bytes=1806336; write_duration_ms=5; sparse_mode=1; sparse_write_bytes_per_second=903168; sparse_written_ahead_bytes=8150016; sparse_gap_count=1"))
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_sent; transfer_id=transfer_receiver_feedback_blocking; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; reason=low_watermark; mode=direct; send_duration_ms=900; enqueue_to_send_age_ms=0; queue_depth=0"))
            .Append(LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=28; frames_enqueued=28; payload_bytes_sent=1850000; payload_bytes_per_second=925000; payload_bytes_enqueued=1850000; payload_bytes_enqueued_per_second=925000; inter_enqueue_gap_p95_ms=55; inter_enqueue_gap_max_ms=88; send_failures=0; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight_max=3; configured_concurrency=4; effective_concurrency=4; send_p95_ms=7; send_max_ms=7; worker_utilization_percent=28; worker_idle_slot_samples=120; worker_saturation_percent=0; drain_wake_count=28; sample_window_ms=2000"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);
        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");

        Assert.Equal("receiver_feedback_blocking_limited", decomposition["likely_limiter"]);
        Assert.Equal("1", decomposition["receiver_feedback_sent_count"]);
        Assert.Equal("900", decomposition["max_receiver_feedback_send_duration_ms"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SenderCacheTelemetry_PopulatesStableSummaryKeys()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_sender_cache")
            .Append(LogLine("event=filetransfer_sender_repair_cache_policy; transfer_id=transfer_sender_cache; session_id=sess_a; source_can_seek=1; seekable_target_bytes=8388608; seekable_hard_limit_bytes=16777216; non_seekable_hard_limit_bytes=67108864; cache_hard_limit_bytes=16777216"))
            .Append(LogLine("event=filetransfer_v4_sender_throughput_summary; transfer_id=transfer_sender_cache; session_id=sess_a; sample_window_ms=2000; raw_bytes_sent=1048576; raw_bytes_per_second=524288; chunk_frames_sent=0; batch_frames_sent=7; chunk_count_sent=28; chunks_accepted_for_transport=64; remote_next_expected_chunk_index=32; remote_granted_until_chunk_index_exclusive=96; remote_granted_window_bytes=2621440; sent_cache_chunk_count=32; sent_cache_bytes=1310720; source_can_seek=1; cache_hard_limit_bytes=16777216; cache_hit_count=2; cache_miss_count=1; source_reread_count=1; cache_eviction_count=3; repair_chunk_skipped_count=1; send_wait_count=0; repair_send_count=2"))
            .Append(LogLine("event=filetransfer_sender_repair_cache_summary; transfer_id=transfer_sender_cache; session_id=sess_a; source_can_seek=1; cache_chunk_count=32; cache_bytes=1310720; cache_hard_limit_bytes=16777216; cache_target_bytes=8388608; cache_eviction_count=3; reason=evicted_to_target"))
            .Append(LogLine("event=filetransfer_sender_repair_chunk_skipped; transfer_id=transfer_sender_cache; session_id=sess_a; reason=not_yet_sent; chunk_index=100; remote_next_expected_chunk_index=32; chunks_accepted_for_transport=64; chunk_count=256"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("1310720", throughput["max_sender_repair_cache_bytes"]);
        Assert.Equal("16777216", throughput["max_sender_repair_cache_hard_limit_bytes"]);
        Assert.Equal("2", throughput["sender_repair_cache_hit_count"]);
        Assert.Equal("1", throughput["sender_repair_cache_miss_count"]);
        Assert.Equal("1", throughput["sender_repair_source_reread_count"]);
        Assert.Equal("3", throughput["sender_repair_cache_eviction_count"]);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["sender_repair_chunk_skipped_count"]);
        Assert.Equal("1", repair["sender_repair_chunk_skipped_not_yet_sent_count"]);
        Assert.Equal("0", repair["sender_cache_exhausted_count"]);
        Assert.Equal("0", repair["sender_repair_unavailable_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RepairSetActivityButCompleted_ReturnsRecoveredPressureWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_repair_set")
            .Append(LogLine("event=filetransfer_repair_set_requested; transfer_id=transfer_repair_set; session_id=sess_a; range_count=3; requested_chunk_count=4; first_start_chunk_index=20; last_end_chunk_exclusive=91; next_chunk_index=20; highest_received_chunk_index=120; granted_until_chunk_index_exclusive=128; pending_chunk_count=96; pending_bytes=3932160; late_arrival_distance=100; reason=timeout"))
            .Append(LogLine("event=filetransfer_repair_set_received; transfer_id=transfer_repair_set; session_id=sess_a; range_count=3; requested_chunk_count=4; first_start_chunk_index=20; last_end_chunk_exclusive=91; remote_next_expected_chunk_index=20; skipped_obsolete_count=0"))
            .Append(LogLine("event=filetransfer_repair_set_sent; transfer_id=transfer_repair_set; session_id=sess_a; range_count=3; requested_chunk_count=4; sent_chunk_count=4; first_start_chunk_index=20; last_end_chunk_exclusive=91; remote_next_expected_chunk_index=20; skipped_obsolete_count=0"))
            .Append(LogLine("event=filetransfer_repair_request_suppressed; transfer_id=transfer_repair_set; session_id=sess_a; reason=duplicate_recent; range_count=3; requested_chunk_count=4; next_chunk_index=20; highest_received_chunk_index=120; granted_until_chunk_index_exclusive=128; pending_chunk_count=96; pending_bytes=3932160"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_RECOVERED_PRESSURE", verdict["verdict"]);
        Assert.Equal("repair-reorder-summary.txt", verdict["next_artifact"]);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["repair_set_requested_count"]);
        Assert.Equal("1", repair["repair_set_received_count"]);
        Assert.Equal("1", repair["repair_set_sent_count"]);
        Assert.Equal("1", repair["repair_request_suppressed_count"]);
        Assert.Equal("3", repair["max_repair_set_ranges"]);
        Assert.Equal("4", repair["max_repair_set_chunks"]);
        Assert.Equal("1", repair["total_repair_control_frame_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_CleanV4Transfer_IsFirstClassPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildCleanCompletedV4TransferFixture("transfer_v4_clean"));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("6", throughput["data_protocol_version"]);
        Assert.Equal("1.000000", throughput["v4_batch_ratio"]);
        Assert.Equal("1", throughput["v4_state_feedback_count"]);
        Assert.Equal("1", throughput["v4_feedback_redundant_success_count"]);

        var payload = ReadArtifactReport(result.ArtifactDir, "payload-efficiency-summary.txt");
        Assert.Equal("v4_default_21k", payload["payload_efficiency_profile"]);

        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["v6_sender_started_count"]);
        Assert.Equal("1", protocol["v6_receiver_started_count"]);
        Assert.Equal("0", protocol["legacy_data_protocol_started_count"]);
        Assert.Equal("0", protocol["unexpected_legacy_data_frame_during_v4_count"]);

        var promotion = ReadArtifactReport(result.ArtifactDir, "v4-promotion-decision.txt");
        Assert.Equal("hold_inconclusive", promotion["decision"]);
        Assert.Equal("long_live_matrix_incomplete", promotion["reason"]);
        Assert.Equal("6", promotion["data_protocol_version"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_CleanRegularNknV4Transfer_IsFirstClassPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildCleanCompletedRegularNknV4TransferFixture("transfer_regular_v4_clean"));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("4", throughput["data_protocol_version"]);
        Assert.Equal("1.000000", throughput["v4_batch_ratio"]);

        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("2", protocol["v4_negotiated_count"]);
        Assert.Equal("1", protocol["v4_sender_started_count"]);
        Assert.Equal("1", protocol["v4_receiver_started_count"]);
        Assert.Equal("0", protocol["v6_sender_started_count"]);
        Assert.Equal("0", protocol["v6_receiver_started_count"]);
        Assert.Equal("0", protocol["legacy_data_protocol_started_count"]);
        Assert.Equal("0", protocol["unexpected_legacy_data_frame_during_v4_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V6ControlHealth_IsTrackedInThroughputSummary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v6_control_health";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Append(LogLine($"event=filetransfer_v6_receiver_state_sent; transfer_id={transferId}; session_id=sess_a; reason=periodic; epoch=2; contiguous_committed_chunk_index=10; durable_received_highest_chunk_index=12; requested_until_chunk_index_exclusive=18; missing_range_count=1; bytes_committed=215040; destination_mode=sparse; transfer_paused=0"))
            .Append(LogLine($"event=filetransfer_v6_receiver_state_received; transfer_id={transferId}; session_id=sess_a; epoch=2; previous_remote_frontier_chunk_index=9; committed_frontier_chunk_index=10; diagnostic_credit_until_chunk_index_exclusive=18; missing_range_count=1; bytes_committed=215040; transfer_paused=0"))
            .Append(LogLine($"event=filetransfer_v6_receiver_request_window_sent; transfer_id={transferId}; session_id=sess_a; reason=periodic; epoch=2; requested_chunk_count=8; requested_until_chunk_index_exclusive=18; missing_range_count=1; request_window_chunks=8; frontier_stalled=1; transport_epoch=1; recovery_mode=regular_nkn"))
            .Append(LogLine($"event=filetransfer_v6_receiver_state_deferred; transfer_id={transferId}; session_id=sess_a; reason=frontier_stalled; next_chunk_index=10; highest_received_chunk_index=18"))
            .Append(LogLine($"event=filetransfer_v6_receiver_state_coalesced; transfer_id={transferId}; session_id=sess_a; reason=frontier_stalled_tail_window; current_committed_chunk_index=10; highest_received_chunk_index=18; accept_window_end_chunk_index=22; tail_chunks_remaining=5; elapsed_since_state_ms=220"))
            .Append(LogLine($"event=filetransfer_v6_frontier_request_sent; direction=inbound; transfer_id={transferId}; session_id=sess_a; reason=frontier_stalled; transport_epoch=1; repair_request_id=req-a; recovery_mode=regular_nkn; start_chunk_index=10; requested_chunk_count=1"))
            .Append(LogLine($"event=filetransfer_v6_frontier_request_failed; direction=inbound; transfer_id={transferId}; session_id=sess_a; reason=frontier_stalled; error=TimeoutException"))
            .Append(LogLine($"event=filetransfer_v6_frontier_request_deferred; direction=inbound; transfer_id={transferId}; session_id=sess_a; frontier_chunk_index=10; highest_received_chunk_index=18; elapsed_ms=300; stall_grace_ms=500; reason=frontier_gap; utc=2026-05-15T12:00:00.0000000Z"))
            .Append(LogLine($"event=filetransfer_v6_frontier_request_received; direction=outbound; transfer_id={transferId}; session_id=sess_a; transport_epoch=1; repair_request_id=req-a; first_start_chunk_index=10; first_chunk_count=1; range_count=1"))
            .Append(LogLine($"event=filetransfer_v6_frontier_request_coalesced; direction=inbound; transfer_id={transferId}; session_id=sess_a; previous_frontier_chunk_index=10; current_frontier_chunk_index=10; elapsed_ms=120; retry_interval_ms=500; reason=frontier_stalled"))
            .Append(LogLine($"event=filetransfer_v6_frontier_request_duplicate_ignored; direction=outbound; transfer_id={transferId}; session_id=sess_a; transport_epoch=1; repair_request_id=req-a; first_start_chunk_index=10; first_chunk_count=1"))
            .Append(LogLine($"event=filetransfer_v6_frontier_request_preempted_normal_pipeline; direction=outbound; transfer_id={transferId}; session_id=sess_a; transport_epoch=1; repair_request_id=req-a; normal_request_count=128; in_flight_send_count=6; requested_chunk_already_in_flight=0; sender_pipeline_generation=3"))
            .Append(LogLine($"event=filetransfer_v6_receiver_state_frontier_preempted_normal_pipeline; direction=outbound; transfer_id={transferId}; session_id=sess_a; receiver_state_epoch=2; transport_epoch=1; normal_request_count=128; in_flight_send_count=6; requested_chunk_already_in_flight=0; sender_pipeline_generation=4; remote_frontier_chunk_index=10"))
            .Append(LogLine($"event=filetransfer_v6_normal_refill_deferred; direction=outbound; transfer_id={transferId}; session_id=sess_a; request_key=normal-a; remote_frontier_chunk_index=10; pending_ahead_chunk_count=256; refill_low_watermark_chunks=64; deferred_chunk_count=128; sent_awaiting_ack_count=96; in_flight_send_count=4"))
            .Append(LogLine($"event=filetransfer_v6_normal_send_ahead_limited; direction=outbound; transfer_id={transferId}; session_id=sess_a; request_key=normal-a; remote_frontier_chunk_index=10; send_ahead_end_exclusive=522; suppressed_chunk_count=24; sent_awaiting_ack_count=96; in_flight_send_count=4"))
            .Append(LogLine($"event=filetransfer_v6_regular_nkn_frontier_pressure_entered; direction=outbound; transfer_id={transferId}; session_id=sess_a; receiver_state_epoch=2; remote_frontier_chunk_index=10; durable_received_highest_chunk_index=18; missing_range_count=2; pressure_until_chunk_index=522; send_ahead_limit_chunks=256; refill_low_watermark_chunks=128"))
            .Append(LogLine($"event=filetransfer_v6_regular_nkn_frontier_pressure_cleared; direction=outbound; transfer_id={transferId}; session_id=sess_a; receiver_state_epoch=3; reason=receiver_state_progress; pressure_start_chunk_index=10; pressure_until_chunk_index=522; remote_frontier_chunk_index=522; active_ms=1200"))
            .Append(LogLine($"event=filetransfer_v6_sender_waiting_for_requests; transfer_id={transferId}; session_id=sess_a; reason=no_receiver_requests; priority_request_count=0; normal_request_count=0"))
            .Append(LogLine($"event=filetransfer_v6_unsolicited_chunk_ignored; transfer_id={transferId}; session_id=sess_a; mode=sparse_seekable; reason=outside_accept_window; chunk_index=60; committed_frontier_chunk_index=10; request_window_end_chunk_index=18; accept_window_end_chunk_index=22"))
            .Append(LogLine($"event=filetransfer_v6_chunk_batch_sent; transfer_id={transferId}; session_id=sess_a; start_chunk_index=10; batch_chunk_count=1; raw_bytes=21504; request_key=req-a; priority=1; regular_nkn_redundant=0; repair_request_id=req-a"))
            .Append(LogLine($"event=filetransfer_v6_chunk_batch_sent; transfer_id={transferId}; session_id=sess_a; start_chunk_index=18; batch_chunk_count=3; raw_bytes=64512; request_key=normal-a; priority=0; regular_nkn_redundant=1; repair_request_id=(none)"))
            .Append(LogLine($"event=filetransfer_v6_chunk_batch_send_deferred_for_recovery; transfer_id={transferId}; session_id=sess_a; start_chunk_index=21; batch_chunk_count=3; raw_bytes=64512; request_key=normal-a; priority=0; transport_epoch=1; current_transport_epoch=2; epoch_state=waiting; handoff_kind=regular_nkn_recovery; target_transport=regular_nkn; pull_transport_paused=1; resume_request_pending=0; post_tuna_recovery_active=1; unresolved_epoch=1; requeued_chunk_count=3; error=InvalidOperationException; message=Bridge_disconnected"))
            .Append(LogLine($"event=filetransfer_v6_chunk_batch_send_timeout; transfer_id={transferId}; session_id=sess_a; start_chunk_index=24; batch_chunk_count=3; raw_bytes=64512; request_key=normal-a; priority=0; transport_epoch=1; timeout_ms=1500; regular_nkn_redundant=1; repair_request_id=(none)"))
            .Append(LogLine($"event=filetransfer_v6_chunk_batch_send_canceled_for_pipeline; transfer_id={transferId}; session_id=sess_a; start_chunk_index=27; batch_chunk_count=3; raw_bytes=64512; request_key=normal-a; priority=0; transport_epoch=1; sender_pipeline_generation=5; regular_nkn_redundant=1; repair_request_id=(none)"))
            .Append(LogLine($"event=filetransfer_v6_chunk_batch_send_late_completed; transfer_id={transferId}; session_id=sess_a; start_chunk_index=24; batch_chunk_count=3; request_key=normal-a; priority=0; transport_epoch=1"))
            .Append(LogLine($"event=filetransfer_v6_chunk_batch_send_late_canceled; transfer_id={transferId}; session_id=sess_a; start_chunk_index=27; batch_chunk_count=3; request_key=normal-a; priority=0; transport_epoch=1"))
            .Append(LogLine($"event=filetransfer_v6_chunk_batch_send_late_failed; transfer_id={transferId}; session_id=sess_a; start_chunk_index=30; batch_chunk_count=3; request_key=normal-a; priority=0; transport_epoch=1; error=InvalidOperationException"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("1", throughput["v6_receiver_state_sent_count"]);
        Assert.Equal("1", throughput["v6_receiver_state_received_count"]);
        Assert.Equal("1", throughput["v6_receiver_request_window_sent_count"]);
        Assert.Equal("1", throughput["v6_receiver_state_deferred_count"]);
        Assert.Equal("1", throughput["v6_receiver_state_coalesced_count"]);
        Assert.Equal("1", throughput["v6_frontier_request_sent_count"]);
        Assert.Equal("1", throughput["v6_frontier_request_failed_count"]);
        Assert.Equal("1", throughput["v6_frontier_request_deferred_count"]);
        Assert.Equal("1", throughput["v6_frontier_request_received_count"]);
        Assert.Equal("1", throughput["v6_frontier_request_coalesced_count"]);
        Assert.Equal("1", throughput["v6_frontier_request_duplicate_ignored_count"]);
        Assert.Equal("1", throughput["v6_frontier_request_preempted_normal_pipeline_count"]);
        Assert.Equal("1", throughput["v6_receiver_state_frontier_preempted_normal_pipeline_count"]);
        Assert.Equal("1", throughput["v6_normal_refill_deferred_count"]);
        Assert.Equal("1", throughput["v6_normal_send_ahead_limited_count"]);
        Assert.Equal("1", throughput["v6_regular_nkn_frontier_pressure_entered_count"]);
        Assert.Equal("1", throughput["v6_regular_nkn_frontier_pressure_cleared_count"]);
        Assert.Equal("1", throughput["v6_sender_waiting_for_requests_count"]);
        Assert.Equal("1", throughput["v6_unsolicited_chunk_ignored_count"]);
        Assert.Equal("2", throughput["v6_chunk_batch_sent_count"]);
        Assert.Equal("1", throughput["v6_normal_chunk_batch_sent_count"]);
        Assert.Equal("1", throughput["v6_priority_chunk_batch_sent_count"]);
        Assert.Equal("1", throughput["v6_regular_nkn_redundant_chunk_batch_sent_count"]);
        Assert.Equal("1", throughput["v6_chunk_batch_send_deferred_for_recovery_count"]);
        Assert.Equal("1", throughput["v6_chunk_batch_send_timeout_count"]);
        Assert.Equal("1", throughput["v6_chunk_batch_send_canceled_for_pipeline_count"]);
        Assert.Equal("1", throughput["v6_chunk_batch_send_late_completed_count"]);
        Assert.Equal("1", throughput["v6_chunk_batch_send_late_canceled_count"]);
        Assert.Equal("1", throughput["v6_chunk_batch_send_late_failed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4RepairBatchProfile_DoesNotOverrideRunPayloadProfile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_profile";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Append(LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_repair_21k; batch_chunk_count=3; chunk_range=4-6; raw_bytes=64512; lane=bulk; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"))
            .Append(LogLine($"event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={transferId}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_repair_21k; lane=bulk; batch_chunk_count=3; serialized_payload_bytes=64700; secure_payload_bytes=64925; bridge_payload_bytes=65024; bridge_command_bytes=65124; max_allowed_bytes=65536; raw_to_bridge_payload_ratio=0.992; bridge_payload_fill_percent=99.219"))
            .Append(LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_repair_21k; batch_chunk_count=3; chunk_range=7-9; raw_bytes=64512; lane=control; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"))
            .Append(LogLine($"event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={transferId}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_repair_21k; lane=control; batch_chunk_count=3; serialized_payload_bytes=64700; secure_payload_bytes=64925; bridge_payload_bytes=65024; bridge_command_bytes=65124; max_allowed_bytes=65536; raw_to_bridge_payload_ratio=0.992; bridge_payload_fill_percent=99.219"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var payload = ReadArtifactReport(result.ArtifactDir, "payload-efficiency-summary.txt");
        Assert.Equal("v4_default_21k", payload["payload_efficiency_profile"]);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("v4_default_21k", throughput["payload_efficiency_profile"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4RepairOnlyProfileEvidence_InfersDefaultRunPayloadProfile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_only_profile";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Where(line => !line.Contains("batch_profile=v4_default_21k", StringComparison.Ordinal))
            .Append(LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_repair_21k; batch_chunk_count=3; chunk_range=4-6; raw_bytes=64512; lane=control_bulk; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"))
            .Append(LogLine($"event=filetransfer_transport_payload_budget; transport=nkn; transfer_id={transferId}; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_repair_21k; lane=control_bulk; batch_chunk_count=3; serialized_payload_bytes=64700; secure_payload_bytes=64925; bridge_payload_bytes=65024; bridge_command_bytes=65124; max_allowed_bytes=65536; raw_to_bridge_payload_ratio=0.992; bridge_payload_fill_percent=99.219"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var payload = ReadArtifactReport(result.ArtifactDir, "payload-efficiency-summary.txt");
        Assert.Equal("v4_default_21k", payload["payload_efficiency_profile"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_GenericTransferTerminal_IsCleanTerminalEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_generic_terminal";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Where(line =>
                !line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal) &&
                !line.Contains("event=file_transfer_outbound_terminal", StringComparison.Ordinal))
            .Append(LogLine($"event=transfer_terminal; direction=inbound; transfer_id={transferId}; session_id=sess_a; file_size_bytes=64512; bytes_transferred=64512; chunks_transferred=3; chunk_count=3; error_code=(none); reason=Transfer complete.; saved_path=(none)"))
            .Append(LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id=sess_a; file_size_bytes=64512; bytes_transferred=64512; chunks_transferred=3; chunk_count=3; error_code=(none); reason=Transfer complete.; saved_path=(none)"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);

        var terminal = ReadArtifactReport(result.ArtifactDir, "transfer-terminal-summary.txt");
        Assert.Equal("1", terminal["inbound_terminal_count"]);
        Assert.Equal("1", terminal["outbound_terminal_count"]);
        Assert.Equal("Completed,Completed", terminal["terminal_states"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_AllTransferProgressTimeout_DoesNotOverrideCleanGenericTerminalPair()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_generic_terminal_after_timeout";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Where(line =>
                !line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal) &&
                !line.Contains("event=file_transfer_outbound_terminal", StringComparison.Ordinal))
            .Append(LogLine($"event=transfer_terminal; direction=inbound; transfer_id={transferId}; session_id=sess_a; file_size_bytes=64512; bytes_transferred=64512; chunks_transferred=3; chunk_count=3; error_code=(none); reason=Transfer complete.; saved_path=(none)"))
            .Append(LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id=sess_a; file_size_bytes=64512; bytes_transferred=64512; chunks_transferred=3; chunk_count=3; error_code=(none); reason=Transfer complete.; saved_path=(none)"))
            .Append(LogLine("event=filetransfer_live_progress_timeout; transfer_id=(all); reason=no useful data progress for 120s; total_wait_s=155; progress_timeout_seconds=120; receiver_next_chunk=-1; receiver_highest_chunk=-1; progress_events=3142"))
            .Append(LogLine("event=filetransfer_artifact_slice_summary; transfer_id=(all); artifact_slice_start_reason=live_soak_failure_context; artifact_slice_end_reason=gui_progress_timeout"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);

        var terminal = ReadArtifactReport(result.ArtifactDir, "transfer-terminal-summary.txt");
        Assert.Equal("1", terminal["inbound_terminal_count"]);
        Assert.Equal("1", terminal["outbound_terminal_count"]);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("0", decomposition["gui_progress_timeout_count"]);
        Assert.Equal("0", decomposition["terminal_missing_after_progress_timeout"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4MissingRangeRepair_IsReported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedV4TransferFixture("transfer_v4_repair")
            .Append(LogLine("event=filetransfer_v4_state_received; transfer_id=transfer_v4_repair; session_id=sess_a; epoch=3; contiguous_committed_chunk_index=4; durable_received_highest_chunk_index=12; credit_until_chunk_index_exclusive=1024; missing_range_count=2; requested_chunk_count=5"))
            .Append(LogLine("event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_repair; session_id=sess_a; range_count=2; requested_chunk_count=5; skipped_obsolete_count=1; skipped_future_count=1; skipped_out_of_bounds_count=1"))
            .Append(LogLine("event=filetransfer_v4_repair_sent; transfer_id=transfer_v4_repair; session_id=sess_a; range_count=1; sent_chunk_count=3; skipped_obsolete_count=1; skipped_future_count=1; skipped_out_of_bounds_count=1"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("2", repair["v4_state_received_count"]);
        Assert.Equal("1", repair["v4_missing_range_repair_scheduled_count"]);
        Assert.Equal("1", repair["v4_missing_range_repair_sent_count"]);
        Assert.Equal("5", repair["v4_repair_requested_chunk_count"]);
        Assert.Equal("3", repair["v4_repair_sent_chunk_count"]);
        Assert.Equal("2", repair["v4_repair_skipped_obsolete_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4RepeatedMissingRangeSchedules_ClassifiesRepairSpam()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_spam";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id=sess_a; epoch=1; contiguous_committed_chunk_index=2385; durable_received_highest_chunk_index=2806; credit_until_chunk_index_exclusive=3121; missing_range_count=1"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2385:64:2385:2806:2385:64; range_count=1; requested_chunk_count=64; scheduled_chunk_count=64"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2385:64:2385:2806:2385:64; range_count=1; requested_chunk_count=64; scheduled_chunk_count=64"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2385:64:2385:2806:2385:64; range_count=1; requested_chunk_count=64; scheduled_chunk_count=64"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2385:64:2385:2806:2385:64; range_count=1; requested_chunk_count=64; scheduled_chunk_count=64"),
            LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=2385:64:2385:2806:2385:64; range_count=1; requested_chunk_count=64; sent_chunk_count=64"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; payload_bytes_sent=65024; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_missing_range_repair_spam_limited", decomposition["likely_limiter"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4RepairRequestedButNotServed_ClassifiesRepairRequestNotServed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_not_served";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_repair_requested; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; attempt_count=1; range_count=1; requested_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; payload_bytes_sent=65024; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_repair_requested_not_received_by_sender", decomposition["likely_limiter"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4RepairSentButNotFilled_ClassifiesRepairSentButNotFilled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_sent_unfilled";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_repair_requested; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; attempt_count=1; range_count=1; requested_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; range_count=1; requested_chunk_count=1; scheduled_chunk_count=1"),
            LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; range_count=1; requested_chunk_count=1; sent_chunk_count=1"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; payload_bytes_sent=65024; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_repair_sent_not_observed_by_receiver", decomposition["likely_limiter"]);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["v4_repair_requested_count"]);
        Assert.Equal("1", repair["v4_missing_range_repair_sent_count"]);
        Assert.Equal("0", repair["v4_repair_chunk_observed_count"]);
        Assert.Equal("0", repair["v4_repair_filled_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4RepairObservedButNotAccepted_ClassifiesReceiverAcceptance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_observed_stale";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_repair_requested; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; attempt_count=1; range_count=1; requested_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; range_count=1; requested_chunk_count=1; scheduled_chunk_count=1"),
            LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; range_count=1; requested_chunk_count=1; sent_chunk_count=1"),
            LogLine($"event=filetransfer_v4_repair_chunk_observed; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; matched_key_count=1; overlap_chunk_count=1; accepted_chunk_count=0; duplicate_or_stale_chunk_count=1; frontier_before=100; frontier_after=100; frontier_advanced=0"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; payload_bytes_sent=65024; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_repair_observed_but_not_accepted", decomposition["likely_limiter"]);
        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["v4_repair_chunk_observed_count"]);
        Assert.Equal("1", repair["v4_repair_observed_duplicate_or_stale_chunk_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4RepairAcceptedButFrontierNotAdvanced_ClassifiesFrontierAdvance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_observed_no_frontier";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_repair_requested; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; attempt_count=1; range_count=1; requested_chunk_count=1; first_start_chunk_index=100; last_end_chunk_exclusive=101"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; range_count=1; requested_chunk_count=1; scheduled_chunk_count=1"),
            LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; range_count=1; requested_chunk_count=1; sent_chunk_count=1"),
            LogLine($"event=filetransfer_v4_repair_chunk_observed; transfer_id={transferId}; session_id=sess_a; repair_request_key=100:1:100:148:100:1; matched_key_count=1; overlap_chunk_count=1; accepted_chunk_count=1; duplicate_or_stale_chunk_count=0; frontier_before=100; frontier_after=100; frontier_advanced=0"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; payload_bytes_sent=65024; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_repair_accepted_but_frontier_not_advanced", decomposition["likely_limiter"]);
        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["v4_repair_observed_accepted_chunk_count"]);
        Assert.Equal("0", repair["v4_repair_observed_frontier_advanced_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4CompletedSlowRepairFill_ClassifiesMissingRangeRepairLimited()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_completed_slow_repair_fill";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=3121"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_frontier_stall_missing_range_due; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; start_chunk_index=2786; requested_chunk_count=64; frontier_stall_age_ms=4200; credit_until_chunk_index_exclusive=3121; durable_received_highest_chunk_index=2785"),
            LogLine($"event=filetransfer_v4_repair_requested; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; attempt_count=1; range_count=1; requested_chunk_count=64; first_start_chunk_index=2786; last_end_chunk_exclusive=2850; frontier_tail_repair=1"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; range_count=1; requested_chunk_count=64; scheduled_chunk_count=64; frontier_tail_repair=1"),
            LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; range_count=1; requested_chunk_count=64; sent_chunk_count=64; frontier_tail_repair=1"),
            LogLine($"event=filetransfer_v4_repair_chunk_observed; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; matched_key_count=1; overlap_chunk_count=64; accepted_chunk_count=64; duplicate_or_stale_chunk_count=0; frontier_before=2786; frontier_after=2850; frontier_advanced=1"),
            LogLine($"event=filetransfer_v4_repair_filled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2786:64:2786:2785:2786:64; request_to_fill_ms=3600; contiguous_committed_chunk_index=2850; durable_received_highest_chunk_index=3120"),
            LogLine($"event=filetransfer_v4_sender_pump_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; scheduled_frames=1; repair_scheduled_frames=1; completed_frames=1; failed_frames=0; in_flight_frames=0; raw_bytes_sent=1376256; repair_send_count=64; credit_exhausted_time_ms=12000; available_credit_bytes=0; next_unsent_chunk_index=3121; credit_ceiling_chunk_index=3121; remote_frontier_chunk_index=2786; terminal_ready=0"),
            LogLine($"event=filetransfer_v4_complete_sent; transfer_id={transferId}; session_id=sess_a; file_size_bytes=67108864"),
            LogLine($"event=filetransfer_v4_complete_received; transfer_id={transferId}; session_id=sess_a; file_size_bytes=67108864"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=300; payload_bytes_sent=65536000; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=3; worker_utilization_percent=50"),
            LogLine($"event=transfer_terminal; direction=inbound; session_id=sess_a; transfer_id={transferId}; error_code=(none); reason=Transfer complete.; saved_path=(none)"),
            LogLine($"event=transfer_terminal; direction=outbound; session_id=sess_a; transfer_id={transferId}; error_code=(none); reason=Transfer complete.")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_missing_range_repair_limited", decomposition["likely_limiter"]);
        Assert.Equal("3600", decomposition["v4_repair_request_to_fill_p95_ms"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4CleanSlowLowWorkerUtilization_ClassifiesBulkUnderutilized()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_clean_slow_bulk_underutilized";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=3121"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_default_21k; batch_chunk_count=3; raw_bytes=64512; raw_to_bridge_payload_ratio=0.972; bridge_payload_fill_percent=97.2"),
            LogLine($"event=filetransfer_v4_complete_sent; transfer_id={transferId}; session_id=sess_a; file_size_bytes=67108864"),
            LogLine($"event=filetransfer_v4_complete_received; transfer_id={transferId}; session_id=sess_a; file_size_bytes=67108864"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=112; frames_enqueued=112; payload_bytes_sent=67108864; payload_bytes_per_second=826350; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=4; worker_utilization_percent=19; worker_saturation_percent=2; send_p95_ms=4"),
            LogLine($"event=transfer_terminal; direction=inbound; session_id=sess_a; transfer_id={transferId}; file_size_bytes=67108864; bytes_transferred=67108864; chunks_transferred=3121; chunk_count=3121; error_code=(none); reason=Transfer complete.; saved_path=(none)"),
            LogLine($"event=transfer_terminal; direction=outbound; session_id=sess_a; transfer_id={transferId}; file_size_bytes=67108864; bytes_transferred=67108864; chunks_transferred=3121; chunk_count=3121; error_code=(none); reason=Transfer complete.")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("nkn_bulk_underutilized", decomposition["likely_limiter"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4DueEventWithEmptyState_ClassifiesStateMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_due_state_mismatch";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_frontier_stall_missing_range_due; transfer_id={transferId}; session_id=sess_a; epoch=7; repair_request_key=100:1:100:99:100:1; start_chunk_index=100; requested_chunk_count=1; frontier_stall_age_ms=900; credit_until_chunk_index_exclusive=200; durable_received_highest_chunk_index=99"),
            LogLine($"event=filetransfer_v4_state_sent; transfer_id={transferId}; session_id=sess_a; reason=frontier_stall_repair_due; epoch=7; contiguous_committed_chunk_index=100; durable_received_highest_chunk_index=99; credit_until_chunk_index_exclusive=200; missing_range_count=0; frontier_stall_age_ms=900; terminal_ready=0"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; payload_bytes_sent=65024; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_missing_range_due_state_mismatch", decomposition["likely_limiter"]);
        Assert.Equal("1", decomposition["v4_missing_range_due_state_mismatch_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4DueEventWithSameEpochDifferentFrontier_DoesNotClassifyStateMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_due_epoch_reused_different_frontier";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_frontier_stall_missing_range_due; transfer_id={transferId}; session_id=sess_a; epoch=7; repair_request_key=100:1:100:99:100:1; start_chunk_index=100; requested_chunk_count=1; frontier_stall_age_ms=900; credit_until_chunk_index_exclusive=200; durable_received_highest_chunk_index=99"),
            LogLine($"event=filetransfer_v4_state_sent; transfer_id={transferId}; session_id=sess_a; reason=chunk_batch_committed; epoch=7; contiguous_committed_chunk_index=180; durable_received_highest_chunk_index=179; credit_until_chunk_index_exclusive=200; missing_range_count=0; frontier_stall_age_ms=0; terminal_ready=0"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; payload_bytes_sent=65024; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("0", decomposition["v4_missing_range_due_state_mismatch_count"]);
        Assert.NotEqual("v4_missing_range_due_state_mismatch", decomposition["likely_limiter"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4DueEventWithEarlierSameEpochState_DoesNotClassifyStateMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_due_epoch_reused_earlier_state";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; transfer_id={transferId}; session_id=sess_a; chunk_size_bytes=21504; chunk_count=4096"),
            LogLine($"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id=sess_a; protocol_version=6"),
            LogLine($"event=filetransfer_v4_state_sent; transfer_id={transferId}; session_id=sess_a; reason=chunk_batch_committed; epoch=7; contiguous_committed_chunk_index=100; durable_received_highest_chunk_index=99; credit_until_chunk_index_exclusive=200; missing_range_count=0; frontier_stall_age_ms=0; terminal_ready=0"),
            LogLine($"event=filetransfer_v4_frontier_stall_missing_range_due; transfer_id={transferId}; session_id=sess_a; epoch=7; repair_request_key=100:1:100:99:100:1; start_chunk_index=100; requested_chunk_count=1; frontier_stall_age_ms=900; credit_until_chunk_index_exclusive=200; durable_received_highest_chunk_index=99"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; payload_bytes_sent=65024; payload_bytes_per_second=800000; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=25"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none)"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("0", decomposition["v4_missing_range_due_state_mismatch_count"]);
        Assert.NotEqual("v4_missing_range_due_state_mismatch", decomposition["likely_limiter"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4TerminalMissingWithRepairBackfill_ClassifiesRepairBeforeZeroReceiveSamples()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_backfill_timeout";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup"),
            LogLine($"event=filetransfer_protocol_negotiated; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4"),
            LogLine($"event=filetransfer_v4_sender_started; transfer_id={transferId}; session_id=sess_a; protocol_version=4; route=regular_nkn_v4_fast; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast; chunk_size_bytes=21504; chunk_count=6242; pipeline_depth=8"),
            LogLine($"event=filetransfer_v4_state_sent; transfer_id={transferId}; session_id=sess_a; reason=frontier_stall_repair_due; epoch=1589; contiguous_committed_chunk_index=2904; durable_received_highest_chunk_index=5639; credit_until_chunk_index_exclusive=6242; frontier_lag_chunks=2736; missing_range_count=1; frontier_stall_age_ms=847; terminal_ready=0"),
            LogLine($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id=sess_a; epoch=1589; previous_epoch=1587; applied=1; stale=0; duplicate=0; contiguous_committed_chunk_index=2904; durable_received_highest_chunk_index=5639; credit_until_chunk_index_exclusive=6242; effective_credit_until_chunk_index_exclusive=6242; available_credit_bytes=0; missing_range_count=1; bytes_committed=88574976; terminal_ready=0"),
            LogLine($"event=filetransfer_v4_repair_requested; transfer_id={transferId}; session_id=sess_a; repair_request_key=2904:12:2904:12; epoch=1589; attempt_count=1; range_count=1; requested_chunk_count=12; first_start_chunk_index=2904; last_end_chunk_exclusive=2916; frontier_stall_age_ms=847; repair_interval_ms=750"),
            LogLine($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2904:12:2904:12; range_count=1; requested_chunk_count=12; scheduled_chunk_count=12; first_start_chunk_index=2904; last_end_chunk_exclusive=2916"),
            LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=2904:12:2904:12; range_count=1; requested_chunk_count=12; sent_chunk_count=12; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=first_send_credit_stall; first_start_chunk_index=2904; last_end_chunk_exclusive=2916; credit_exhausted_time_ms_at_repair=127766"),
            LogLine($"event=filetransfer_v4_repair_filled; transfer_id={transferId}; session_id=sess_a; repair_request_key=2904:12:2904:12; first_start_chunk_index=2904; last_end_chunk_exclusive=2916; requested_chunk_count=12; attempt_count=1; request_to_fill_ms=731; contiguous_committed_chunk_index=2916; durable_received_highest_chunk_index=5639"),
            LogLine($"event=filetransfer_v4_sender_pump_summary; transfer_id={transferId}; session_id=sess_a; sample_window_ms=2000; configured_depth=8; effective_depth=8; in_flight_frames=0; scheduled_frames=8; normal_scheduled_frames=0; repair_scheduled_frames=8; completed_frames=8; failed_frames=0; raw_bytes_sent=516096; batch_frames_sent=8; chunk_count_sent=24; repair_send_count=24; raw_bytes_sent_total=147507200; normal_raw_bytes_sent_total=134217728; repair_raw_bytes_sent_total=13289472; normal_chunk_count_sent_total=6242; repair_chunk_count_sent_total=618; credit_exhausted_time_ms=128084; available_credit_bytes=0; next_unsent_chunk_index=6242; credit_ceiling_chunk_index=6242; remote_frontier_chunk_index=2904; terminal_ready=0"),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=344; frames_enqueued=344; payload_bytes_sent=22333168; payload_bytes_per_second=11166584; send_failures=0; queue_clears=0; queue_depth=0; configured_concurrency=4; effective_concurrency=4; in_flight_max=1; worker_utilization_percent=12; worker_saturation_percent=0; send_p95_ms=8"),
            LogLine("event=screenshare_bridge_transport_health_summary; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=346; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_last_received_age_ms=5000; media_last_received_age_ms=-1; bulk_last_received_age_ms=4000"),
            LogLine("event=screenshare_bridge_transport_health_summary; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=12; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_last_received_age_ms=7000; media_last_received_age_ms=-1; bulk_last_received_age_ms=6000")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("INCONCLUSIVE", verdict["verdict"]);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("v4_missing_range_repair_limited", decomposition["likely_limiter"]);
        Assert.Equal("2", decomposition["transport_ready_sending_zero_receive_window_count"]);
        Assert.Equal("134217728", decomposition["v4_max_sender_pump_normal_raw_bytes_sent_total"]);
        Assert.Equal("1", decomposition["v4_full_normal_payload_sent"]);
        Assert.Equal("2904", decomposition["v4_latest_sender_remote_frontier_chunk_index"]);
        Assert.Equal("2736", decomposition["v4_max_frontier_lag_chunks"]);
        Assert.NotEqual("external_transport_limited", decomposition["likely_limiter"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RecoveredV6FeedbackAndBridgeFailuresDuringReceiveRecovery_ReturnExternalWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_recovered_receive_recovery";
        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedV4TransferFixture(transferId));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=nkn_bridge_receive_stall_detected; connect_key=test; reason=control_receive_stalled; consecutive_zero_receive_windows=6; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; frames_sent_since_last=15; control_messages_received_since_last=0; bulk_messages_received_since_last=3; total_messages_received_since_last=3; control_last_received_age_ms=23845; bulk_last_received_age_ms=63; sample_window_ms=2000", secondsOffset: 20),
                LogLine("event=nkn_bridge_receive_stall_recovery_started; connect_key=test; stall_reason=control_receive_stalled; attempt=2; max_restarts=4; consecutive_zero_receive_windows=6; frames_sent_since_last=15; control_last_received_age_ms=23845; bulk_last_received_age_ms=63", secondsOffset: 50),
                LogLine($"event=filetransfer_data_session_availability_observed; session_id=sess_a; transfer_id={transferId}; is_available=0; reason=receive_stall_recovery; requires_resume_request=1; handoff_kind=regular_nkn_recovery; target_transport=regular_nkn", secondsOffset: 50),
                LogLine($"event=filetransfer_v6_epoch_started; direction=inbound; transfer_id={transferId}; session_id=sess_a; transport_epoch=2; handoff_kind=regular_nkn_recovery; source_transport=regular_nkn; target_transport=regular_nkn; reason=receive_stall_recovery; state=target_proof_pending; starting_committed_chunk=10; starting_highest_observed_chunk=9", secondsOffset: 50),
                LogLine($"event=filetransfer_v6_receiver_state_deferred_for_recovery; transfer_id={transferId}; session_id=sess_a; reason=transport_epoch; transport_epoch=2; recovery_mode=frontier_repair_only; error=InvalidOperationException", secondsOffset: 60),
                LogLine($"event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; first_lane=control; second_lane=bulk; first_error=InvalidOperationException; second_error=InvalidOperationException", secondsOffset: 60),
                LogLine($"event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.frontier_request.v6; first_lane=control; second_lane=bulk; first_error=InvalidOperationException; second_error=InvalidOperationException", secondsOffset: 60),
                LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=1; frames_enqueued=4; payload_bytes_sent=458; payload_bytes_enqueued=1832; payload_bytes_per_second=229; payload_bytes_enqueued_per_second=916; send_failures=3; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=2; effective_concurrency=2; in_flight_max=1; in_flight_bytes_max=458; worker_utilization_percent=24; worker_idle_slot_samples=50; worker_saturation_percent=0; sample_window_ms=2000", secondsOffset: 70),
                LogLine("event=nkn_bridge_receive_stall_recovery_receive_resumed; connect_key=test; recovery_count=1; resume_after_recovery_ms=1755; total_messages_received_since_last=13; total_bytes_received_since_last=6246; control_messages_received_since_last=13; media_messages_received_since_last=0; bulk_messages_received_since_last=0", secondsOffset: 90)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("external-transport-health-summary.txt", verdict["next_artifact"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Equal("external_transport_churn", verdict["warning_kinds"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("0", stability["hard_failure_count"]);

        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("2", protocol["v4_feedback_both_failed_count"]);

        var bridge = ReadArtifactReport(result.ArtifactDir, "bridge-bulk-summary.txt");
        Assert.Equal("3", bridge["bulk_send_failures"]);

        var external = ReadArtifactReport(result.ArtifactDir, "external-transport-health-summary.txt");
        Assert.Equal("1", external["receive_stall_detected_count"]);
        Assert.Equal("1", external["receive_stall_recovery_started_count"]);
        Assert.Equal("1", external["receive_stall_recovery_receive_resumed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ExternalTransportHealthIssue_ReturnsExternalWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedTransferFixture("transfer_external"));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.Insert(firstTerminalIndex, LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=2; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 60));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("external-transport-health-summary.txt", verdict["next_artifact"]);
        Assert.Equal("external_transport_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_TwoSparseExternalWarnings_RemainWarningUnderCap()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedTransferFixture("transfer_external_sparse"));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 30),
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=1; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 90)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("external_transport_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("incident", verdict["warning_cap_count_unit"]);
        Assert.Equal("external_transport_churn:2", verdict["warning_kind_counts"]);
        Assert.Equal("external_transport_churn:2", verdict["warning_kind_raw_event_counts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RepeatedBridgeHealthChurnSameConnectKey_CountsAsSingleIncident()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedTransferFixture("transfer_external_same_connect_key"));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=screenshare_bridge_transport_health_summary; connect_key=test; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=1; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 10),
                LogLine("event=screenshare_bridge_transport_health_summary; connect_key=test; disconnect_count_since_last=0; connect_failed_count_since_last=1; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 30),
                LogLine("event=screenshare_bridge_transport_health_summary; connect_key=test; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=1; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 60),
                LogLine("event=screenshare_bridge_transport_health_summary; connect_key=test; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=1; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 90)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("external_transport_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("external_transport_churn:1", verdict["warning_kind_counts"]);
        Assert.Equal("external_transport_churn:4", verdict["warning_kind_raw_event_counts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FourExternalWarnings_ExceedCountCap()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedTransferFixture("transfer_external_count_cap"));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 10),
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=1; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 30),
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=1; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 60),
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=1; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 90)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_EXTERNAL_TRANSPORT_CHURN", verdict["verdict"]);
        Assert.Equal("external_transport_churn", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("external_transport_churn:4", verdict["warning_kind_counts"]);
        Assert.Equal("external_transport_churn:4", verdict["warning_kind_raw_event_counts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ThreeDenseExternalWarnings_ExceedRateCap()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedTransferFixture("transfer_external_rate_cap"), terminalOffsetSeconds: 30);
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=1; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 5),
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=1; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 10),
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=1; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1", secondsOffset: 20)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_EXTERNAL_TRANSPORT_CHURN", verdict["verdict"]);
        Assert.Equal("external_transport_churn", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("external_transport_churn:3", verdict["warning_kind_counts"]);
        Assert.Equal("external_transport_churn:3", verdict["warning_kind_raw_event_counts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RepeatedPassiveZeroReceiveHealthSummaries_WarnWithoutCapFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedTransferFixture("transfer_passive_zero_receive"), terminalOffsetSeconds: 120);
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            Enumerable.Range(1, 6).Select(index =>
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=12; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=32000; media_last_received_age_ms=31000; bulk_last_received_age_ms=33000", secondsOffset: index * 10)));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("external_transport_churn", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("(none)", verdict["warning_kind_counts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ActiveRuntimeZeroReceiveHealthSummaries_DoNotWarnWithoutReceiveStall()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareCompletedFixture(
            "transfer_active_runtime_zero_receive",
            route: "file_tuna_v4",
            protocolVersion: 4,
            runtimeProfile: "file_tuna_v4_fast",
            bridgeRecoveryPolicy: "tuna_strict",
            runtimeEventName: "filetransfer_v4_sender_started"),
            terminalOffsetSeconds: 120).ToList();
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=23; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=10226; media_last_received_age_ms=10300; bulk_last_received_age_ms=10110", secondsOffset: 15),
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=21; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=12265; media_last_received_age_ms=12340; bulk_last_received_age_ms=12149", secondsOffset: 17)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("(none)", verdict["warning_kinds"]);
        var external = ReadArtifactReport(result.ArtifactDir, "external-transport-health-summary.txt");
        Assert.Equal("2", external["ready_sending_zero_receive_window_count"]);
        Assert.Equal("0", external["receive_stall_detected_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ExternalTransportEventsOutsideObservedTransferWindow_DoNotWarn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareCompletedFixture(
            "transfer_external_outside_window",
            route: "file_tuna_v4",
            protocolVersion: 4,
            runtimeProfile: "file_tuna_v4_fast",
            bridgeRecoveryPolicy: "tuna_strict",
            runtimeEventName: "filetransfer_v4_sender_started"),
            terminalOffsetSeconds: 120).ToList();
        lines.InsertRange(
            0,
            [
                LogLine("event=nkn_bridge_receive_stall_recovery_unproven; connect_key=setup; recovery_count=1; requires_control_proof=1; requires_bulk_proof=1; total_messages_received_since_last=1; total_bytes_received_since_last=544; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=1; control_last_received_age_ms=14250; bulk_last_received_age_ms=1095", secondsOffset: -4),
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=3; active_file_transfer_sessions=0; active_file_transfer_runtime_sessions=0; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=12080; media_last_received_age_ms=-1; bulk_last_received_age_ms=12197", secondsOffset: -3)
            ]);
        lines.Add(LogLine("event=nkn_bridge_receive_stall_recovery_receive_resumed; connect_key=teardown; recovery_count=1; resume_after_recovery_ms=2500; total_messages_received_since_last=3; total_bytes_received_since_last=1632; control_messages_received_since_last=2; media_messages_received_since_last=0; bulk_messages_received_since_last=1", secondsOffset: 124));
        lines.Add(LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=4; active_file_transfer_sessions=0; active_file_transfer_runtime_sessions=0; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=15634; media_last_received_age_ms=-1; bulk_last_received_age_ms=16342", secondsOffset: 125));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("(none)", verdict["warning_kinds"]);
        var external = ReadArtifactReport(result.ArtifactDir, "external-transport-health-summary.txt");
        Assert.Equal("1", external["receive_stall_recovery_unproven_count"]);
        Assert.Equal("1", external["receive_stall_recovery_receive_resumed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ReadyBridgeSendingButNotReceivingBurst_CountsAsSingleExternalIncident()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareCompletedFixture(
            "transfer_receive_stall",
            route: "regular_nkn_v4_fast",
            protocolVersion: 4,
            runtimeProfile: "regular_nkn_v4_fast",
            bridgeRecoveryPolicy: "regular_nkn_v4_fast",
            runtimeEventName: "filetransfer_v4_sender_started"));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=12; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=32000; media_last_received_age_ms=31000; bulk_last_received_age_ms=33000", secondsOffset: 30),
                LogLine("event=nkn_bridge_receive_stall_detected; connect_key=test; consecutive_zero_receive_windows=3; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; frames_sent_since_last=12; total_messages_received_since_last=0; control_last_received_age_ms=32000; media_last_received_age_ms=31000; bulk_last_received_age_ms=33000; sample_window_ms=2000", secondsOffset: 31),
                LogLine("event=nkn_bridge_receive_stall_recovery_started; connect_key=test; attempt=1; max_restarts=2; consecutive_zero_receive_windows=3; frames_sent_since_last=12; control_last_received_age_ms=32000; media_last_received_age_ms=31000; bulk_last_received_age_ms=33000", secondsOffset: 32),
                LogLine("event=nkn_bridge_inbound_delivery_summary; channel=bulk; messages=4; payload_bytes=4096; subscriber_present_count=4; subscriber_missing_count=0; handler_failure_count=0; source_matches_local_control_count=0; source_matches_local_media_count=0; source_matches_local_bulk_count=0; source_matches_any_local_count=0; topic_count=0; last_source_len=32; last_source_hash=abc123; initial=0", secondsOffset: 33),
                LogLine("event=nkn_inbound_envelope_received; channel=bulk; reason=(none); envelope_type=file_transfer_data_frame; payload_len=1024; envelope_payload_len=768; msg_id=msg1; source_len=32; source_matches_local=0; expected_source_available=1; source_matches_expected=1; is_topic=0", secondsOffset: 34),
                LogLine("event=nkn_bridge_receive_stall_recovery_unproven; connect_key=test; recovery_count=1; requires_control_proof=1; requires_bulk_proof=1; total_messages_received_since_last=4; total_bytes_received_since_last=4096; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=4; control_last_received_age_ms=34000; bulk_last_received_age_ms=100", secondsOffset: 35),
                LogLine("event=nkn_bridge_receive_stall_recovery_receive_resumed; connect_key=test; recovery_count=1; resume_after_recovery_ms=1750; total_messages_received_since_last=4; total_bytes_received_since_last=4096; control_messages_received_since_last=2; media_messages_received_since_last=1; bulk_messages_received_since_last=1", secondsOffset: 36)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("external_transport_churn:1", verdict["warning_kind_counts"]);
        Assert.Equal("external_transport_churn:4", verdict["warning_kind_raw_event_counts"]);

        var external = ReadArtifactReport(result.ArtifactDir, "external-transport-health-summary.txt");
        Assert.Equal("1", external["ready_sending_zero_receive_window_count"]);
        Assert.Equal("1", external["receive_stall_detected_count"]);
        Assert.Equal("1", external["receive_stall_recovery_started_count"]);
        Assert.Equal("1", external["receive_stall_recovery_unproven_count"]);
        Assert.Equal("1", external["receive_stall_recovery_receive_resumed_count"]);
        Assert.Equal("1750", external["max_receive_stall_recovery_resume_after_ms"]);
        Assert.Equal("4", external["inbound_delivery_bulk_messages"]);
        Assert.Equal("0", external["inbound_delivery_subscriber_missing_count"]);
        Assert.Equal("1", external["inbound_filetransfer_data_frame_envelope_received_count"]);
        Assert.Equal("33000", external["max_bulk_last_received_age_ms"]);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("1", decomposition["transport_ready_sending_zero_receive_window_count"]);
        Assert.Equal("1", decomposition["receive_stall_detected_count"]);
        Assert.Equal("4", decomposition["inbound_delivery_bulk_messages"]);
        Assert.Equal("1", decomposition["inbound_filetransfer_data_frame_envelope_received_count"]);
        Assert.Equal("external_transport_limited", decomposition["likely_limiter"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_DistinctReceiveStallEpisodesOverCap_ReturnsExternalChurnFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareCompletedFixture(
            "transfer_receive_stall_episodes",
            route: "regular_nkn_v4_fast",
            protocolVersion: 4,
            runtimeProfile: "regular_nkn_v4_fast",
            bridgeRecoveryPolicy: "regular_nkn_v4_fast",
            runtimeEventName: "filetransfer_v4_sender_started"),
            terminalOffsetSeconds: 620);
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);

        var churnLines = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            churnLines.Add(LogLine($"event=nkn_bridge_receive_stall_detected; connect_key=test-{i}; consecutive_zero_receive_windows=3; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; frames_sent_since_last=12; total_messages_received_since_last=0; control_last_received_age_ms=32000; media_last_received_age_ms=31000; bulk_last_received_age_ms=33000; sample_window_ms=2000", secondsOffset: 20 + (i * 140)));
        }

        lines.InsertRange(firstTerminalIndex, churnLines);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_EXTERNAL_TRANSPORT_CHURN", verdict["verdict"]);
        Assert.Equal("external_transport_churn", verdict["warning_cap_exceeded_kinds"]);
        Assert.Equal("external_transport_churn:4", verdict["warning_kind_counts"]);
        Assert.Equal("external_transport_churn:4", verdict["warning_kind_raw_event_counts"]);
        Assert.Equal("external_transport_churn:regular_nkn", verdict["warning_cap_exceeded_contexts"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ControlStaleButBulkFlowing_DoesNotWarnExternalTransport()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_control_degraded")
            .Append(LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=12; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=14; total_messages_received_since_last=14; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=900000; total_bytes_received_since_last=900000; control_last_received_age_ms=32000; media_last_received_age_ms=0; bulk_last_received_age_ms=100"))
            .Append(LogLine("event=nkn_bridge_control_receive_degraded; connect_key=test; consecutive_control_zero_receive_windows=2; active_file_transfer_sessions=1; frames_sent_since_last=12; control_messages_received_since_last=0; bulk_messages_received_since_last=14; total_messages_received_since_last=14; control_last_received_age_ms=32000; bulk_last_received_age_ms=100; sample_window_ms=2000"))
            .Append(LogLine("event=nkn_bridge_control_receive_recovery_suppressed; reason=bulk_receive_active; connect_key=test; consecutive_control_zero_receive_windows=2; active_file_transfer_sessions=1; recovery_count=0; control_messages_received_since_last=0; bulk_messages_received_since_last=14; control_last_received_age_ms=32000; bulk_last_received_age_ms=100"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var external = ReadArtifactReport(result.ArtifactDir, "external-transport-health-summary.txt");
        Assert.Equal("0", external["receive_stall_detected_count"]);
        Assert.Equal("0", external["receive_stall_recovery_started_count"]);
        Assert.Equal("1", external["control_receive_degraded_count"]);
        Assert.Equal("1", external["control_receive_recovery_suppressed_count"]);
        Assert.Equal("1", external["control_receive_recovery_suppressed_bulk_active_count"]);
        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("1", decomposition["control_receive_degraded_count"]);
        Assert.Equal("1", decomposition["control_receive_recovery_suppressed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ControlStaleButBulkFreshZeroWindow_DoesNotWarnExternalTransport()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_control_fresh")
            .Append(LogLine("event=screenshare_bridge_transport_health_summary; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=6; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=20251; media_last_received_age_ms=0; bulk_last_received_age_ms=2726"))
            .Append(LogLine("event=nkn_bridge_control_receive_degraded; connect_key=test; consecutive_control_zero_receive_windows=7; active_file_transfer_sessions=1; frames_sent_since_last=6; control_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_last_received_age_ms=20251; bulk_last_received_age_ms=2726; sample_window_ms=2000"))
            .Append(LogLine("event=nkn_bridge_control_receive_recovery_suppressed; reason=filetransfer_bulk_receive_fresh; connect_key=test; consecutive_control_zero_receive_windows=7; active_file_transfer_sessions=1; recovery_count=0; control_messages_received_since_last=0; bulk_messages_received_since_last=0; control_last_received_age_ms=20251; bulk_last_received_age_ms=2726"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var external = ReadArtifactReport(result.ArtifactDir, "external-transport-health-summary.txt");
        Assert.Equal("0", external["receive_stall_detected_count"]);
        Assert.Equal("0", external["receive_stall_recovery_started_count"]);
        Assert.Equal("1", external["control_receive_degraded_count"]);
        Assert.Equal("1", external["control_receive_recovery_suppressed_count"]);
        Assert.Equal("1", external["control_receive_recovery_suppressed_bulk_fresh_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ScreenShareMediaDrops_ReturnsCohabitationWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildCleanCompletedTransferFixture("transfer_cohab"));
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.Insert(firstTerminalIndex, LogLine("event=screenshare_bridge_media_send_summary; frames_sent=20; send_failures=0; queue_drops=2; queue_mode=normal; queue_depth=0; oldest_queued_age_ms=0", secondsOffset: 60));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_COHABITATION_PRESSURE", verdict["verdict"]);
        Assert.Equal("coexistence-summary.txt", verdict["next_artifact"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_NoTransferEvidence_ReturnsInvalidSetup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync([
            LogLine("event=session_connected; session_id=sess_a")
        ]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("INVALID_SETUP", verdict["verdict"]);
        Assert.Equal("filetransfer-operator-verdict.txt", verdict["next_artifact"]);
    }
}
