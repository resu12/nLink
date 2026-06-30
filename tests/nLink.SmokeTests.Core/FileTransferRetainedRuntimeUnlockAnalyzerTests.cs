using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "RetainedRuntimeUnlock")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferRetainedRuntimeUnlockAnalyzerTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockProbeDuringFallbackWithoutAuthority_FailsSeparationProof()
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
            LogLine($"event=runtime_unlock_precommit_probe_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; transaction_generation=4; offer_generation=7; tuna_path_lease_generation=3; probe_id=runtime-unlock-precommit:4:7:abc; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation"),
            LogLine($"event=runtime_unlock_precommit_probe_failed; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; transaction_generation=4; offer_generation=7; tuna_path_lease_generation=3; probe_id=runtime-unlock-precommit:4:7:abc; reason=timeout"),
            LogLine($"event=runtime_unlock_precommit_probe_ignored; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; transaction_generation=4; offer_generation=7; tuna_path_lease_generation=3; probe_id=runtime-unlock-precommit:4:7:abc; target_transport=tuna; received_transport=regular_nkn; reason=not_tuna_transport"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["fallback_runtime_unlock_separation_verdict"]);
        Assert.Equal("fail", route["runtime_fallback_separation_verdict"]);
        Assert.Equal("1", route["runtime_unlock_probe_started_without_activation_window_count"]);
        Assert.Equal("1", route["runtime_unlock_regular_nkn_probe_rejected_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_fallback_separation_violation", verdict["recovery_failure_class"]);

        var ledger = ReadArtifactReport(result.ArtifactDir, "phase0-invariant-ledger.txt");
        Assert.Equal("runtime_fallback_separation_violation", ledger["recovery_failure_class"]);
        Assert.Equal("fail", ledger["runtime_fallback_separation_verdict"]);
        Assert.True(int.Parse(ledger["stale_route_mismatch_count"], CultureInfo.InvariantCulture) >= 0);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockAfterFallbackAuthorityGrant_PassesSeparationProof()
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
            LogLine($"event=filetransfer_runtime_unlock_admission_denied; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; source=precommit_probe_start; reason=runtime_unlock_probe_missing; admission_generation=1; admission_reason=recovery_active; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=6; fallback_active=1; fallback_current=1; fallback_terminalized=0; checkpoint_delivery_recovery_pending=0; state_refresh_send_in_flight=0; deferred_state_refresh_pending=0; proof_replay_pending=0; control_plane_pressure_pending=0; bridge_recovery_pending=1; fallback_tail_repair_pending=0; paired_fallback_yield_granted=0"),
            LogLine($"event=runtime_unlock_fallback_unavailable_suppressed; session_id=sess_redacted; transfer_id={transferId}; payer_decision_id=9; generation=3; trigger=post_tuna_fallback_offer_send_prepare; reason=fallback_owner_active; fallback_owner_reason=recovery_active; source=post_tuna_fallback_offer_send_prepare"),
            LogLine($"event=filetransfer_fallback_leg_authority_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=9; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:42; authority_reason=post_tuna_fallback_state_refresh_failed"),
            LogLine($"event=filetransfer_fallback_leg_authority_checkpoint_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=9; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:42; proven_committed_chunk=128; proven_highest_observed_chunk=160; reason=receiver_state"),
            LogLine($"event=filetransfer_runtime_unlock_waiting_for_fallback_survival; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=runtime_unlock_probe_missing; rejection_reason=runtime_unlock_probe_missing; leg_id=leg:6; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=9; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:42; state=checkpoint_pending; can_send_data=0"),
            LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=fallback_tail_quiescent; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=9; checkpoint_request_id=v6-regular-nkn-state-refresh:42"),
            LogLine($"event=filetransfer_fallback_tail_paired_yield_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=fallback_tail_quiescent; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=9; checkpoint_request_id=v6-regular-nkn-state-refresh:42"),
            LogLine($"event=filetransfer_runtime_unlock_admission_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; source=fallback_survival_resume; reason=fallback_tail_quiescent; admission_generation=2; admission_reason=fallback_quiescent; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=6; fallback_active=1; fallback_current=1; fallback_terminalized=0; checkpoint_delivery_recovery_pending=0; state_refresh_send_in_flight=0; deferred_state_refresh_pending=0; proof_replay_pending=0; control_plane_pressure_pending=0; bridge_recovery_pending=0; fallback_tail_repair_pending=0; paired_fallback_yield_granted=1"),
            LogLine($"event=filetransfer_runtime_unlock_activation_window_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=fallback_tail_quiescent; leg_id=leg:6; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=9; bridge_recovery_generation=1; checkpoint_request_id=v6-regular-nkn-state-refresh:42; state=recovery_active; can_send_data=1"),
            LogLine($"event=runtime_unlock_precommit_probe_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; transaction_generation=4; offer_generation=7; tuna_path_lease_generation=3; probe_id=runtime-unlock-precommit:4:7:abc; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation"),
            LogLine($"event=runtime_unlock_precommit_probe_acked; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; transaction_generation=4; offer_generation=7; tuna_path_lease_generation=3; probe_id=runtime-unlock-precommit:4:7:abc; received_transport=tuna"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=3"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["fallback_runtime_unlock_separation_verdict"]);
        Assert.Equal("pass", route["runtime_fallback_separation_verdict"]);
        Assert.Equal("1", route["fallback_runtime_unlock_waiting_count"]);
        Assert.Equal("1", route["runtime_unlock_activation_window_granted_count"]);
        Assert.Equal("0", route["runtime_unlock_probe_started_without_activation_window_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackTailProgressWhileRuntimeUnlockWaitsWithoutYield_IsClassified()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=2"),
            LogLine($"event=filetransfer_runtime_unlock_waiting_for_fallback_survival; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=runtime_unlock_probe_missing; rejection_reason=recovery_active; leg_id=leg:6; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=9; bridge_recovery_generation=1; checkpoint_request_id=(none); state=recovery_active; can_send_data=1"),
            LogLine($"event=filetransfer_v6_post_tuna_fallback_frontier_progress_state_forced; transfer_id={transferId}; session_id=sess_redacted; previous_committed_chunk_index=9503; current_committed_chunk_index=9504; committed_delta=1; last_sent_committed_chunk_index=9503; highest_received_chunk_index=9503; frontier_gap_missing=0; terminal_frontier=0; recovery_active=1; rebind_generation=0; frontier_repair_window_chunks=1; frontier_repair_committed_chunks=0"),
            LogLine($"event=filetransfer_v6_post_tuna_fallback_frontier_progress_state_forced; transfer_id={transferId}; session_id=sess_redacted; previous_committed_chunk_index=9504; current_committed_chunk_index=9505; committed_delta=1; last_sent_committed_chunk_index=9504; highest_received_chunk_index=9504; frontier_gap_missing=0; terminal_frontier=0; recovery_active=1; rebind_generation=0; frontier_repair_window_chunks=1; frontier_repair_committed_chunks=0"),
            LogLine($"event=filetransfer_gui_progress_timeout; scenario=regular-v4-live-activation-off-on-off-256mb; timeout_seconds=900"),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected"),
            LogLine($"event=file_transfer_inbound_terminal; role=Helper; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; saved_path=(none)")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["fallback_tail_quiescence_verdict"]);
        Assert.Equal("2", route["fallback_tail_progress_forced_count"]);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fallback_tail_quiescence_missing", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RecoveredRuntimeUnlockBridgeQueueClear_ReturnsWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = StretchTransferWindowForWarningRate(BuildRouteAwareCompletedFixture(
            "transfer_runtime_unlock_queue_clear",
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
                LogLine("event=tuna_acceleration_activation_offer_not_observed; session_id=sess_redacted; trigger=runtime_unlock; generation=3; recovery_reason=bulk_queue_cleared; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 20),
                LogLine("event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=5", secondsOffset: 25),
                LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=4; frames_enqueued=9; payload_bytes_sent=1946; payload_bytes_enqueued=4321; send_failures=0; queue_clears=5; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; configured_concurrency=4; effective_concurrency=4; sample_window_ms=2000", secondsOffset: 30),
                LogLine("event=tuna_activation_control_send_recovery_requested; session_id=sess_redacted; trigger=runtime_unlock; reason=runtime_unlock_offer_send_not_observed; recovery_reason=bulk_queue_cleared", secondsOffset: 35),
                LogLine("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; session_id=sess_redacted; retired_generation=3; retry_reason=runtime_unlock_offer_send_not_observed", secondsOffset: 40),
                LogLine("event=nkn_bridge_receive_stall_recovery_receive_resumed; connect_key=abc123; recovery_count=1", secondsOffset: 45)
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Contains("recovered_runtime_unlock_bridge_clear", verdict["warning_kinds"], StringComparison.Ordinal);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);
        Assert.Contains("recovered_runtime_unlock_bridge_clear:1", verdict["warning_kind_counts"], StringComparison.Ordinal);
        Assert.Contains("recovered_runtime_unlock_bridge_clear:2", verdict["warning_kind_raw_event_counts"], StringComparison.Ordinal);
        Assert.Contains("recovered_runtime_unlock_bridge_clear:runtime_unlock", verdict["warning_cap_contexts"], StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockFailureBeforeActivation_ClassifiesRecoveryCoordination()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRecoveryCoordinationFailureFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("runtime_unlock_recovery_coordination", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_offer_not_observed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_scheduled_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_queued_behind_active_negotiation_count"]);
        Assert.Equal("1", verdict["session_liveness_timeout_after_runtime_unlock_count"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);

        var routeSummary = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("regular_nkn_v4_fast", routeSummary["selected_route_changes"]);
        Assert.DoesNotContain("file_tuna_v6", File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-operator-verdict.txt")), StringComparison.Ordinal);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_recovery_coordination", stability["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockContractRetryDispatchedWithoutLivenessTimeout_IsNotRecoveryCoordinationFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRecoveryContractDispatchedFixture(),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_offer_not_observed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_authority_observed_count"]);
        Assert.Equal("0", verdict["session_liveness_timeout_after_runtime_unlock_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("(none)", stability["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryReleaseWithoutDispatchOutcome_ClassifiesMissingDispatchProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                "transfer_runtime_unlock_dispatch_missing",
                RuntimeUnlockRetryReleasedLine("transfer_runtime_unlock_dispatch_missing"),
                RuntimeUnlockRetryScheduledLine("transfer_runtime_unlock_dispatch_missing"),
                RuntimeUnlockRetryOfferGenerationLine()),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("runtime_unlock_retry_dispatch_missing", verdict["recovery_failure_class"]);
        Assert.Equal("fail", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryRouteLessScheduleWithOfferGeneration_ClassifiesMissingDispatchProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                "transfer_runtime_unlock_dispatch_routeless_schedule",
                RuntimeUnlockRetryReleasedLine("transfer_runtime_unlock_dispatch_routeless_schedule"),
                RuntimeUnlockRetryRouteLessScheduledLine("transfer_runtime_unlock_dispatch_routeless_schedule"),
                RuntimeUnlockRetryOfferGenerationLine()),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("runtime_unlock_retry_dispatch_missing", verdict["recovery_failure_class"]);
        Assert.Equal("fail", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
    }


    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryReleaseWithDispatchGrantAndObserved_PassesDispatchProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                "transfer_runtime_unlock_dispatch_observed",
                RuntimeUnlockRetryReleasedLine("transfer_runtime_unlock_dispatch_observed"),
                RuntimeUnlockRetryDispatchGrantedLine("transfer_runtime_unlock_dispatch_observed"),
                RuntimeUnlockRetryDispatchObservedLine("transfer_runtime_unlock_dispatch_observed")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("pass", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_authority_granted_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_observed_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryReleaseWithDispatchRejection_PassesDispatchProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                "transfer_runtime_unlock_dispatch_rejected",
                RuntimeUnlockRetryReleasedLine("transfer_runtime_unlock_dispatch_rejected"),
                RuntimeUnlockRetryDispatchRejectedLine("transfer_runtime_unlock_dispatch_rejected")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("pass", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_authority_rejected_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryReleaseWithMissingAuthorityRejection_PassesDispatchProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                "transfer_runtime_unlock_dispatch_missing_authority_rejected",
                RuntimeUnlockRetryReleasedLine("transfer_runtime_unlock_dispatch_missing_authority_rejected"),
                RuntimeUnlockRetryDispatchMissingAuthorityRejectedLine("transfer_runtime_unlock_dispatch_missing_authority_rejected"),
                RuntimeUnlockRetryContractFailedMissingAuthorityLine("transfer_runtime_unlock_dispatch_missing_authority_rejected")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("pass", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_authority_rejected_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_missing_authority_after_release_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryReleaseWithMissingAuthorityButNoRejection_ClassifiesMissingDispatchProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                "transfer_runtime_unlock_dispatch_missing_authority",
                RuntimeUnlockRetryReleasedLine("transfer_runtime_unlock_dispatch_missing_authority"),
                RuntimeUnlockRetryMissingAuthorityLine("transfer_runtime_unlock_dispatch_missing_authority")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("runtime_unlock_retry_dispatch_missing", verdict["recovery_failure_class"]);
        Assert.Equal("fail", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_missing_authority_after_release_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryDispatchedButOfferObservationBlocked_ClassifiesNextBlocker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchedButOfferObservationBlockedFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_offer_observation_blocked_by_receive_recovery", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatched_count"]);
        Assert.Equal("1", verdict["runtime_unlock_offer_not_observed_count"]);
        Assert.Equal("1", verdict["session_liveness_timeout_after_runtime_unlock_count"]);
        Assert.NotEqual("runtime_unlock_recovery_coordination", verdict["recovery_failure_class"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_offer_observation_blocked_by_receive_recovery", stability["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockDispatchDeferredByRegularV4ReceiveRecovery_ClassifiesNextBlocker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockDispatchDeferredByRegularV4ReceiveRecoveryFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_regular_v4_recovery_arbitration", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_offer_not_observed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatched_count"]);
        Assert.Equal("1", verdict["runtime_unlock_dispatch_deferred_for_regular_v4_recovery_count"]);
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_activation_window_started_count"]);
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_activation_window_peer_visible_count"]);
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_activation_window_failed_count"]);
        Assert.Equal("fail", verdict["runtime_unlock_regular_v4_recovery_arbitration_verdict"]);
        Assert.Equal("0", verdict["runtime_unlock_offer_observation_blocked_count"]);
        Assert.Equal("1", verdict["session_liveness_timeout_after_runtime_unlock_count"]);
        Assert.NotEqual("runtime_unlock_offer_observation_blocked_by_receive_recovery", verdict["recovery_failure_class"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_regular_v4_recovery_arbitration", stability["recovery_failure_class"]);
        Assert.Equal("1", stability["runtime_unlock_dispatch_deferred_for_regular_v4_recovery_count"]);
        Assert.Equal("fail", stability["runtime_unlock_regular_v4_recovery_arbitration_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRegularV4ActivationWindowPeerVisible_PassesArbitrationProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRegularV4ActivationWindowPeerVisibleFixture(),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_activation_window_started_count"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_activation_window_peer_visible_count"]);
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_activation_window_failed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_peer_visible_window_started_count"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_peer_visible_window_proof_count"]);
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_peer_visible_window_late_proof_ignored_count"]);
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_peer_visible_window_failed_count"]);
        Assert.Equal("pass", verdict["runtime_unlock_regular_v4_recovery_arbitration_verdict"]);
        Assert.Equal("0", verdict["session_liveness_timeout_after_runtime_unlock_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("(none)", stability["recovery_failure_class"]);
        Assert.Equal("pass", stability["runtime_unlock_regular_v4_recovery_arbitration_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRegularV4ActivationWindowObservedOnly_FailsArbitrationProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRegularV4ActivationWindowObservedOnlyFixture(),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_activation_window_started_count"]);
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_activation_window_peer_visible_count"]);
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_activation_window_failed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_peer_visible_window_started_count"]);
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_peer_visible_window_proof_count"]);
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_peer_visible_window_late_proof_ignored_count"]);
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_peer_visible_window_failed_count"]);
        Assert.Equal("fail", verdict["runtime_unlock_regular_v4_recovery_arbitration_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_transaction_observed_send_count"]);
        Assert.Equal("0", verdict["runtime_unlock_transaction_peer_proof_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRegularV4PeerVisibleWindowDeliveryFailed_ClassifiesDeliveryFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRegularV4PeerVisibleWindowDeliveryFailedFixture(),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_regular_v4_peer_visible_window_delivery_failed", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_peer_visible_window_started_count"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_peer_visible_window_delivery_started_count"]);
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_peer_visible_window_proof_count"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_peer_visible_window_delivery_failed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_peer_visible_window_failed_count"]);
        Assert.Equal("fail", verdict["runtime_unlock_regular_v4_recovery_arbitration_verdict"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_regular_v4_peer_visible_window_delivery_failed", stability["recovery_failure_class"]);
        Assert.Equal("1", stability["runtime_unlock_regular_v4_peer_visible_window_delivery_started_count"]);
        Assert.Equal("1", stability["runtime_unlock_regular_v4_peer_visible_window_delivery_failed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRegularV4ActivationWindowLatePeerProof_ClassifiesRace()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRegularV4ActivationWindowLatePeerProofFixture(),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_peer_visible_window_race", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_peer_visible_window_started_count"]);
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_peer_visible_window_proof_count"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_peer_visible_window_late_proof_ignored_count"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_peer_visible_window_failed_count"]);
        Assert.Equal("fail", verdict["runtime_unlock_regular_v4_recovery_arbitration_verdict"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_peer_visible_window_race", stability["recovery_failure_class"]);
        Assert.Equal("1", stability["runtime_unlock_regular_v4_peer_visible_window_late_proof_ignored_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockListenerRearmFailure_ClassifiesListenerRearmCoordination()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockListenerRearmFailureFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("tuna_listener_rearm_coordination", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_offer_not_observed_count"]);
        Assert.Equal("1", verdict["listener_rearm_required_count"]);
        Assert.Equal("0", verdict["listener_rearm_completed_count"]);
        Assert.Equal("2", verdict["listener_rearm_failed_count"]);
        Assert.Equal("0", verdict["runtime_unlock_offer_dispatched_after_listener_rearm_count"]);
        Assert.Equal("1", verdict["session_liveness_timeout_after_runtime_unlock_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("tuna_listener_rearm_coordination", stability["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockListenerRearmCompletedAndDispatched_IsNotListenerRearmFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockListenerRearmCompletedFixture(),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_offer_not_observed_count"]);
        Assert.Equal("1", verdict["listener_rearm_required_count"]);
        Assert.Equal("1", verdict["listener_rearm_completed_count"]);
        Assert.Equal("0", verdict["listener_rearm_failed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_offer_dispatched_after_listener_rearm_count"]);
        Assert.Equal("0", verdict["session_liveness_timeout_after_runtime_unlock_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("(none)", stability["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockPeerResponseMissingUnderRegularV4Recovery_ClassifiesPeerResponseBlocker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockPeerResponseMissingUnderRegularV4RecoveryFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_peer_response_not_received_under_regular_v4_recovery", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_offer_not_observed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatched_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_authority_observed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_cutthrough_attempt_count"]);
        Assert.Equal("0", verdict["runtime_unlock_cutthrough_peer_received_count"]);
        Assert.Equal("1", verdict["runtime_unlock_cutthrough_timeout_count"]);
        Assert.Equal("fail", verdict["runtime_unlock_cutthrough_verdict"]);
        Assert.Equal("1", verdict["session_liveness_timeout_after_runtime_unlock_count"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_peer_response_not_received_under_regular_v4_recovery", stability["recovery_failure_class"]);
        Assert.Equal("fail", stability["runtime_unlock_cutthrough_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockBrokenStateRetryChurn_ClassifiesBoundedFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockBrokenStateRetryChurnFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_broken_state_retry_churn", verdict["recovery_failure_class"]);
        Assert.Equal("2", verdict["runtime_unlock_offer_not_observed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_broken_state_fresh_retry_consumed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_broken_state_retry_suppressed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_broken_state_transaction_failed_count"]);
        Assert.Equal("fail", verdict["runtime_unlock_transaction_proof_verdict"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_broken_state_retry_churn", stability["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockCutThroughPeerReceivedWithRegularActivationCycle_IsAccepted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockCutThroughPeerReceivedRegularActivationCycleFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_cutthrough_attempt_count"]);
        Assert.Equal("2", verdict["runtime_unlock_cutthrough_peer_received_count"]);
        Assert.Equal("0", verdict["runtime_unlock_cutthrough_timeout_count"]);
        Assert.Equal("pass", verdict["runtime_unlock_cutthrough_verdict"]);
        Assert.Equal("0", verdict["session_liveness_timeout_after_runtime_unlock_count"]);
        Assert.Equal("pass", verdict["live_route_epoch_proof_verdict"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("(none)", stability["recovery_failure_class"]);
        Assert.Equal("pass", stability["runtime_unlock_cutthrough_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockTransactionLocalOnlyObservedSend_ClassifiesTransactionFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockTransactionLocalOnlyObservedSendFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_transaction_local_only", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_transaction_started_count"]);
        Assert.Equal("1", verdict["runtime_unlock_transaction_observed_send_count"]);
        Assert.Equal("0", verdict["runtime_unlock_transaction_peer_proof_count"]);
        Assert.Equal("1", verdict["runtime_unlock_transaction_local_only_rejected_count"]);
        Assert.Equal("fail", verdict["runtime_unlock_transaction_proof_verdict"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_transaction_local_only", stability["recovery_failure_class"]);
        Assert.Equal("fail", stability["runtime_unlock_transaction_proof_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockTransactionPeerProofAndRouteCommit_IsAccepted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockTransactionPeerProofAndRouteCommitFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_transaction_started_count"]);
        Assert.Equal("1", verdict["runtime_unlock_transaction_observed_send_count"]);
        Assert.Equal("2", verdict["runtime_unlock_transaction_peer_proof_count"]);
        Assert.Equal("1", verdict["runtime_unlock_probe_started_count"]);
        Assert.Equal("1", verdict["runtime_unlock_probe_acked_count"]);
        Assert.Equal("pass", verdict["runtime_unlock_make_before_break_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_transaction_commit_pending_count"]);
        Assert.Equal("2", verdict["runtime_unlock_transaction_committed_count"]);
        Assert.Equal("0", verdict["runtime_unlock_transaction_local_only_rejected_count"]);
        Assert.Equal("pass", verdict["runtime_unlock_transaction_proof_verdict"]);
        Assert.Equal("pass", verdict["live_route_epoch_proof_verdict"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("(none)", stability["recovery_failure_class"]);
        Assert.Equal("pass", stability["runtime_unlock_transaction_proof_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockPreCommitRouteEpochProof_IsAccepted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockPreCommitRouteEpochProofFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("2", verdict["runtime_unlock_precommit_probe_acked_count"]);
        Assert.Equal("pass", verdict["runtime_unlock_make_before_break_verdict"]);
        Assert.Equal("pass", verdict["runtime_unlock_transaction_proof_verdict"]);
        Assert.Equal("pass", verdict["live_route_epoch_proof_verdict"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("(none)", stability["recovery_failure_class"]);
        Assert.Equal("pass", stability["runtime_unlock_transaction_proof_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockTunaProbeSidecarClosedRegularTerminal_ClassifiesProbePathFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockTunaProbePathNotEstablishedFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_tuna_probe_sidecar_closed_regular_v4_terminal", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_tuna_probe_sent_count"]);
        Assert.Equal("0", verdict["runtime_unlock_tuna_probe_received_count"]);
        Assert.Equal("0", verdict["runtime_unlock_tuna_probe_ack_received_count"]);
        Assert.Equal("1", verdict["runtime_unlock_tuna_probe_sidecar_failure_count"]);
        Assert.Equal("0", verdict["runtime_unlock_tuna_probe_sidecar_fresh_transaction_scheduled_count"]);
        Assert.Equal("0", verdict["runtime_unlock_tuna_listener_rearm_after_probe_close_failed_count"]);
        Assert.Equal("fail", verdict["runtime_unlock_probe_commit_verdict"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_tuna_probe_sidecar_closed_regular_v4_terminal", stability["recovery_failure_class"]);
        Assert.Equal("1", stability["runtime_unlock_tuna_probe_sent_count"]);
        Assert.Equal("0", stability["runtime_unlock_tuna_probe_ack_received_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockTransactionPendingCommitAfterPriorCommit_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockTransactionPendingCommitAfterPriorCommitFixture());

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_route_commit_before_probe", verdict["recovery_failure_class"]);
        Assert.Equal("0", verdict["runtime_unlock_probe_acked_count"]);
        Assert.Equal("fail", verdict["runtime_unlock_make_before_break_verdict"]);
        Assert.Equal("4", verdict["runtime_unlock_transaction_commit_pending_count"]);
        Assert.Equal("2", verdict["runtime_unlock_transaction_committed_count"]);
        Assert.Equal("fail", verdict["runtime_unlock_transaction_proof_verdict"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_route_commit_before_probe", stability["recovery_failure_class"]);
        Assert.Equal("fail", stability["runtime_unlock_transaction_proof_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockOldV6ProbeRejectedByV4Peer_ClassifiesPreCommitProbeMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockOldV6ProbeRejectedByV4PeerFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_precommit_probe_protocol_mismatch", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_probe_started_count"]);
        Assert.Equal("0", verdict["runtime_unlock_probe_acked_count"]);
        Assert.Equal("0", verdict["runtime_unlock_precommit_probe_started_count"]);
        Assert.Equal("1", verdict["runtime_unlock_precommit_probe_protocol_mismatch_count"]);
        Assert.Equal("fail", verdict["runtime_unlock_make_before_break_verdict"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_precommit_probe_protocol_mismatch", stability["recovery_failure_class"]);
        Assert.Equal("1", stability["runtime_unlock_precommit_probe_protocol_mismatch_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockTunaPathLeaseUnavailableAtAnswer_ClassifiesLeaseFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockTunaPathLeaseUnavailableAtAnswerFixture(),
            ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("tuna_path_lease_unavailable_at_answer", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_tuna_path_lease_started_count"]);
        Assert.Equal("1", verdict["runtime_unlock_tuna_path_lease_ready_count"]);
        Assert.Equal("1", verdict["runtime_unlock_tuna_path_lease_failed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_answer_rejected_tuna_path_lease_unavailable_count"]);
        Assert.Equal("fail", verdict["runtime_unlock_transaction_proof_verdict"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("tuna_path_lease_unavailable_at_answer", stability["recovery_failure_class"]);
        Assert.Equal("1", stability["runtime_unlock_answer_rejected_tuna_path_lease_unavailable_count"]);
    }

    private static string[] BuildRuntimeUnlockRetryDispatchProofFixture(
        string transferId,
        params string[] middleMessages)
    {
        const string sessionId = "sess_runtime_unlock_dispatch_proof";
        var lines = new List<string>
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
        };

        for (var index = 0; index < middleMessages.Length; index++)
        {
            lines.Add(LogLine(middleMessages[index], secondsOffset: 20 + index));
        }

        lines.Add(LogLine($"event=file_transfer_inbound_terminal; role=Helper; session_id={sessionId}; transfer_id={transferId}; state=Completed; error_code=(none); sha256_match=1", secondsOffset: 89));
        lines.Add(LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Completed; error_code=(none); sha256_match=1", secondsOffset: 90));
        lines.Add(LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=134217728; bytes_transferred=134217728; chunks_transferred=6242; chunk_count=6242; error_code=(none); reason=Completed.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4", secondsOffset: 91));
        return lines.ToArray();
    }

    private static string RuntimeUnlockRetryReleasedLine(string transferId)
        => $"event=runtime_unlock_retry_released_after_regular_v4_receive_recovery; direction=outbound; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; retired_generation=11; contract_generation=7; offer_generation=11; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; regular_v4_liveness_generation=3; bridge_decision_generation=5; blocker_reason=regular_v4_bridge_recovery_completed_awaiting_filetransfer_proof; settlement_trigger=regular_v4_receive_proof_observed; decision_reason=accepted";

    private static string RuntimeUnlockRetryScheduledLine(string transferId)
        => $"event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; direction=outbound; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; retired_generation=11; contract_generation=7; offer_generation=11; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; regular_v4_liveness_generation=3; bridge_decision_generation=5; trigger=regular_v4_receive_proof_observed; queued_behind_active_negotiation=1";

    private static string RuntimeUnlockRetryRouteLessScheduledLine(string transferId)
        => $"event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; direction=outbound; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; retired_generation=11; contract_generation=7; offer_generation=11; route=(none); protocol_version=0; live_route_epoch=0; regular_v4_liveness_generation=0; bridge_decision_generation=0; trigger=regular_v4_receive_proof_observed; queued_behind_active_negotiation=1";

    private static string RuntimeUnlockRetryOfferGenerationLine()
        => "event=runtime_unlock_transaction_offer_generation_created; session_id=sess_runtime_unlock_dispatch_proof; transaction_generation=4; offer_generation=12; state=offerpreparing; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); reason=runtime_unlock";

    private static string RuntimeUnlockRetryDispatchGrantedLine(string transferId)
        => $"event=runtime_unlock_retry_dispatch_authority_granted; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; retired_generation=11; offer_generation=12; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; runtime_generation=4; terminal_generation=0; leg_generation=9; regular_v4_liveness_generation=3; bridge_decision_generation=5; queued_behind_active_negotiation=1; decision_reason=regular_v4_recovery_released";

    private static string RuntimeUnlockRetryDispatchObservedLine(string transferId)
        => $"event=runtime_unlock_retry_dispatch_observed; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; retired_generation=11; offer_generation=12; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; runtime_generation=4; terminal_generation=0; leg_generation=9; regular_v4_liveness_generation=3; bridge_decision_generation=5; queued_behind_active_negotiation=1; decision_reason=retry_observed_send";

    private static string RuntimeUnlockRetryDispatchRejectedLine(string transferId)
        => $"event=runtime_unlock_retry_dispatch_authority_rejected; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; retired_generation=11; offer_generation=12; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; runtime_generation=4; terminal_generation=0; leg_generation=9; regular_v4_liveness_generation=3; bridge_decision_generation=5; queued_behind_active_negotiation=1; decision_reason=terminal_retry_contract";

    private static string RuntimeUnlockRetryDispatchMissingAuthorityRejectedLine(string transferId)
        => $"event=runtime_unlock_retry_dispatch_authority_rejected; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; retired_generation=11; offer_generation=12; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; runtime_generation=4; terminal_generation=0; leg_generation=9; regular_v4_liveness_generation=3; bridge_decision_generation=5; queued_behind_active_negotiation=1; decision_reason=missing_runtime_unlock_retry_authority";

    private static string RuntimeUnlockRetryContractFailedMissingAuthorityLine(string transferId)
        => $"event=session_recovery_contract_failed; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; offer_generation=12; retired_offer_generation=11; kind=runtime_unlock_activation; state=failed; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=0; retry_observed=0; queued_behind_active_negotiation=1; retry_authority_pending=0; retry_authority_granted=0; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=(none); authority_failure_reason=missing_runtime_unlock_retry_authority";

    private static string RuntimeUnlockRetryMissingAuthorityLine(string transferId)
        => $"event=session_recovery_contract_retry_authority_observed; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; offer_generation=12; retired_offer_generation=11; kind=runtime_unlock_activation; state=retrydispatched; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=1; retry_observed=0; queued_behind_active_negotiation=1; retry_authority_pending=0; retry_authority_granted=0; observed_send_pending=1; authority_attempt=1; authorized_observed_lane=(none); authority_failure_reason=missing_runtime_unlock_retry_authority";
}
