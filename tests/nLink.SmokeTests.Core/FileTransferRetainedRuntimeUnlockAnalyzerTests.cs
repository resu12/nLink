using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
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
    public async Task AnalyzeRetained_PostFallbackAdmissionWaitingForTransportProof_ClassifiesResumeBlocked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; selection_reason=regular_nkn_default_v4; live_route_epoch=0"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; selection_reason=file_tuna_active; file_tuna_active=1; live_route_epoch=1"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; selection_reason=post_tuna_file_fallback_active; post_tuna_fallback_active=1; live_route_epoch=2"),
            LogLine($"event=filetransfer_fallback_tail_quiescent_yield_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=fallback_tail_quiescent; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=9; checkpoint_request_id=(none)"),
            LogLine($"event=filetransfer_fallback_tail_paired_yield_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=fallback_tail_quiescent; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=9; checkpoint_request_id=(none)"),
            LogLine($"event=filetransfer_runtime_unlock_admission_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; source=fallback_survival_resume; reason=fallback_tail_quiescent; admission_generation=4; admission_reason=fallback_quiescent; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=6; fallback_active=1; fallback_current=1; fallback_terminalized=0; checkpoint_delivery_recovery_pending=0; state_refresh_send_in_flight=0; deferred_state_refresh_pending=0; proof_replay_pending=0; control_plane_pressure_pending=0; bridge_recovery_pending=0; fallback_tail_repair_pending=0; paired_fallback_yield_granted=1"),
            LogLine($"event=filetransfer_runtime_unlock_precommit_probe_waiting_for_transport_proof; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=fallback_tail_quiescent; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation"),
            LogLine($"event=filetransfer_runtime_unlock_precommit_probe_pending_held; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; source=runtime_unlock_precommit_probe_pending; reason=fallback_tail_quiescent; hold_reason=transport_proof_missing; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=6; admission_generation=4; pending_probe_generation=0; transaction_generation=0; offer_generation=0; tuna_path_lease_generation=0; tuna_path_lease_listener_run_id=(none); target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation"),
            LogLine($"event=filetransfer_gui_progress_timeout; transfer_id={transferId}; session_id=sess_redacted; reason=normal_to_tuna_live_epoch_missing"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("post_fallback_runtime_unlock_resume_blocked", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_fallback_quiescent_admission_granted_count"]);
        Assert.Equal("1", verdict["runtime_unlock_precommit_probe_waiting_for_transport_proof_count"]);
        Assert.Equal("1", verdict["runtime_unlock_precommit_probe_pending_held_count"]);
        Assert.Equal("1", verdict["post_fallback_runtime_unlock_resume_blocked_count"]);
        Assert.Equal("0", verdict["runtime_unlock_precommit_probe_started_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostFallbackAdmissionHeldWithOtherTransferRejection_StillClassifiesResumeBlocked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted-a]";
        const string otherTransferId = "[redacted-b]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; selection_reason=regular_nkn_default_v4; live_route_epoch=0"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; selection_reason=file_tuna_active; file_tuna_active=1; live_route_epoch=1"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; selection_reason=post_tuna_file_fallback_active; post_tuna_fallback_active=1; live_route_epoch=2"),
            LogLine($"event=filetransfer_fallback_tail_paired_yield_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=fallback_tail_quiescent; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=9; checkpoint_request_id=(none)"),
            LogLine($"event=filetransfer_runtime_unlock_admission_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; source=fallback_survival_resume; reason=fallback_tail_quiescent; admission_generation=4; admission_reason=fallback_quiescent; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=6; fallback_active=1; fallback_current=1; fallback_terminalized=0; checkpoint_delivery_recovery_pending=0; state_refresh_send_in_flight=0; deferred_state_refresh_pending=0; proof_replay_pending=0; control_plane_pressure_pending=0; bridge_recovery_pending=0; fallback_tail_repair_pending=0; paired_fallback_yield_granted=1"),
            LogLine($"event=filetransfer_runtime_unlock_precommit_probe_pending_held; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; source=runtime_unlock_precommit_probe_pending; reason=fallback_tail_quiescent; hold_reason=transport_proof_missing; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=6; admission_generation=4; pending_probe_generation=0; transaction_generation=0; offer_generation=0; tuna_path_lease_generation=0; tuna_path_lease_listener_run_id=(none); target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation"),
            LogLine($"event=filetransfer_runtime_unlock_precommit_probe_pending_rejected; direction=outbound; transfer_id={otherTransferId}; session_id=sess_redacted; source=runtime_unlock_precommit_probe_pending; reason=snapshot_not_usable; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=6; admission_generation=4; pending_probe_generation=0; transaction_generation=0; offer_generation=0; tuna_path_lease_generation=0; tuna_path_lease_listener_run_id=(none); target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation; rejection_reason=snapshot_not_usable"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("post_fallback_runtime_unlock_resume_blocked", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["post_fallback_runtime_unlock_resume_blocked_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostFallbackAdmissionWaitingThenPendingRejected_DoesNotClassifyResumeBlocked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; selection_reason=regular_nkn_default_v4; live_route_epoch=0"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; selection_reason=file_tuna_active; file_tuna_active=1; live_route_epoch=1"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; selection_reason=post_tuna_file_fallback_active; post_tuna_fallback_active=1; live_route_epoch=2"),
            LogLine($"event=filetransfer_fallback_tail_paired_yield_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=fallback_tail_quiescent; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=9; checkpoint_request_id=(none)"),
            LogLine($"event=filetransfer_runtime_unlock_admission_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; source=fallback_survival_resume; reason=fallback_tail_quiescent; admission_generation=4; admission_reason=fallback_quiescent; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=6; fallback_active=1; fallback_current=1; fallback_terminalized=0; checkpoint_delivery_recovery_pending=0; state_refresh_send_in_flight=0; deferred_state_refresh_pending=0; proof_replay_pending=0; control_plane_pressure_pending=0; bridge_recovery_pending=0; fallback_tail_repair_pending=0; paired_fallback_yield_granted=1"),
            LogLine($"event=filetransfer_runtime_unlock_precommit_probe_waiting_for_transport_proof; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=snapshot_not_usable; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation"),
            LogLine($"event=filetransfer_runtime_unlock_precommit_probe_pending_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; source=runtime_unlock_precommit_probe_pending; reason=snapshot_not_usable; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=6; admission_generation=4; pending_probe_generation=0; transaction_generation=8; offer_generation=13; tuna_path_lease_generation=5; tuna_path_lease_listener_run_id=listener-start-5; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation; rejection_reason=snapshot_not_usable"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Failed; error_code=peer_disconnected")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("0", verdict["post_fallback_runtime_unlock_resume_blocked_count"]);
        Assert.Equal("0", verdict["runtime_unlock_precommit_probe_pending_held_count"]);
        Assert.NotEqual("post_fallback_runtime_unlock_resume_blocked", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_TransientFallbackProofReplayPendingThenProbeCommit_DoesNotClassifyResumeBlocked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; selection_reason=regular_nkn_default_v4; live_route_epoch=0"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; selection_reason=file_tuna_active; file_tuna_active=1; live_route_epoch=1"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; selection_reason=post_tuna_file_fallback_active; post_tuna_fallback_active=1; live_route_epoch=2"),
            LogLine($"event=filetransfer_fallback_tail_paired_yield_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=fallback_tail_quiescent; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=9; checkpoint_request_id=(none)"),
            LogLine($"event=filetransfer_runtime_unlock_admission_denied; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; source=fallback_survival_resume; reason=fallback_tail_quiescent; admission_generation=3; admission_reason=fallback_proof_replay_pending; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=6; proof_replay_pending=1; bridge_recovery_pending=0; paired_fallback_yield_granted=1"),
            LogLine($"event=filetransfer_runtime_unlock_admission_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; source=fallback_survival_resume; reason=fallback_tail_quiescent; admission_generation=4; admission_reason=fallback_quiescent; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=0; leg_generation=6; fallback_active=1; fallback_current=1; fallback_terminalized=0; checkpoint_delivery_recovery_pending=0; state_refresh_send_in_flight=0; deferred_state_refresh_pending=0; proof_replay_pending=0; control_plane_pressure_pending=0; bridge_recovery_pending=0; fallback_tail_repair_pending=0; paired_fallback_yield_granted=1"),
            LogLine($"event=filetransfer_runtime_unlock_activation_window_granted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=fallback_tail_quiescent; leg_id=leg:6; leg_generation=6; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; transport_epoch=9; bridge_recovery_generation=1; checkpoint_request_id=(none); state=recovery_active; can_send_data=1"),
            LogLine($"event=runtime_unlock_precommit_probe_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; transaction_generation=4; offer_generation=7; tuna_path_lease_generation=3; probe_id=runtime-unlock-precommit:4:7:settled; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation"),
            LogLine($"event=runtime_unlock_precommit_probe_acked; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; transaction_generation=4; offer_generation=7; tuna_path_lease_generation=3; probe_id=runtime-unlock-precommit:4:7:settled; received_transport=tuna"),
            LogLine($"event=filetransfer_runtime_unlock_route_commit_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; transaction_generation=4; offer_generation=7; route_commit_generation=2; reason=normal_to_tuna_activation"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; selection_reason=file_tuna_active; file_tuna_active=1; live_route_epoch=3"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_fallback_proof_replay_pending_after_paired_yield_count"]);
        Assert.Equal("0", verdict["runtime_unlock_fallback_proof_replay_pending_after_paired_yield_unresolved_count"]);
        Assert.Equal("0", verdict["post_fallback_runtime_unlock_resume_blocked_count"]);
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
    public async Task AnalyzeRetained_RuntimeUnlockRetryQueuedGrantWithoutObservedSend_ClassifiesMissingDispatchProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                "transfer_runtime_unlock_dispatch_queued_grant_missing_observed",
                RuntimeUnlockRetryDispatchGrantedLine("transfer_runtime_unlock_dispatch_queued_grant_missing_observed"),
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
    public async Task AnalyzeRetained_RuntimeUnlockRetryGrantWithoutObservedSend_ClassifiesMissingAfterAuthorityGranted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                "transfer_runtime_unlock_dispatch_grant_missing_observed",
                RuntimeUnlockRetryDispatchGrantedLine(
                    "transfer_runtime_unlock_dispatch_grant_missing_observed",
                    queuedBehindActiveNegotiation: false),
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
    public async Task AnalyzeRetained_RuntimeUnlockRetryExplicitMissingAfterAuthorityGranted_ClassifiesSpecificEdge()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                "transfer_runtime_unlock_dispatch_missing_after_grant",
                RuntimeUnlockRetryDispatchGrantedLine(
                    "transfer_runtime_unlock_dispatch_missing_after_grant",
                    queuedBehindActiveNegotiation: false),
                RuntimeUnlockRetryDispatchMissingAfterAuthorityGrantedLine("transfer_runtime_unlock_dispatch_missing_after_grant"),
                RuntimeUnlockRetryContractFailedDispatchMissingAfterAuthorityGrantedLine("transfer_runtime_unlock_dispatch_missing_after_grant")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("runtime_unlock_retry_dispatch_missing_after_authority_granted", verdict["recovery_failure_class"]);
        Assert.Equal("fail", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_missing_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_missing_after_authority_granted_count"]);
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
    public async Task AnalyzeRetained_RuntimeUnlockRetrySchema2FullTupleAndSettlement_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_schema2_pass";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_released_after_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_observed", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=DispatchObserved; dispatch_observed=1")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("pass", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_contract_settled_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_duplicate_observed_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_duplicate_settlement_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_post_grant_hold_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryMissingDispatchWithoutSuccessor_ClassifiesFirstEdge()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_missing_successor";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=MissingDispatch; dispatch_observed=0"),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_rejected",
                    transferId,
                    suffix: "; decision_reason=runtime_unlock_retry_authority_settled"),
                "event=filetransfer_regular_v4_frontier_repair_stalled; session_id=sess_runtime_unlock_dispatch_proof; transfer_id=" + transferId),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySuccessorAttemptMismatch_PrecedesLaterFallbackSymptoms()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_successor_attempt_mismatch";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_rejected",
                    transferId,
                    suffix: "; decision_reason=retry_dispatch_successor_attempt_mismatch"),
                $"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_runtime_unlock_dispatch_proof; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; target_transport=regular_nkn; live_route_epoch=2",
                $"event=filetransfer_fallback_tail_local_yield_blocked; direction=outbound; transfer_id={transferId}; session_id=sess_runtime_unlock_dispatch_proof; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; leg_generation=7; reason=paired_yield_missing",
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_rejected",
                    transferId,
                    dispatchAttemptGeneration: 5,
                    missingDispatchSuccessorGeneration: 2,
                    suffix: "; decision_reason=retry_dispatch_successor_attempt_mismatch")),
            ["-LiveRouteProofMode", "None"]);

        Assert.Equal(0, result.Script.ExitCode);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("runtime_unlock_retry_dispatch_successor_attempt_mismatch", verdict["recovery_failure_class"]);
        Assert.Equal("runtime_unlock_retry_dispatch_successor_attempt_mismatch", verdict["first_proof_chain_failure_class"]);
        Assert.Equal("runtime_unlock_retry_dispatch_authority_rejected", verdict["first_proof_chain_failure_event"]);
        Assert.Equal("retry_dispatch_successor_attempt_mismatch", verdict["first_proof_chain_failure_reason"]);
        Assert.Equal("2", verdict["first_proof_chain_failure_sequence"]);
        Assert.Equal("2", verdict["first_proof_chain_failure_line_number"]);
        Assert.Equal("nlink.log", Path.GetFileName(verdict["first_proof_chain_failure_source_path"]));
        Assert.Equal("2", verdict["runtime_unlock_retry_dispatch_successor_attempt_mismatch_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal(verdict["first_proof_chain_failure_class"], stability["first_proof_chain_failure_class"]);
        Assert.Equal(verdict["first_proof_chain_failure_sequence"], stability["first_proof_chain_failure_sequence"]);
        Assert.Equal(verdict["first_proof_chain_failure_source_path"], stability["first_proof_chain_failure_source_path"]);

        var ledger = ReadArtifactReport(result.ArtifactDir, "phase0-invariant-ledger.txt");
        Assert.Equal(verdict["first_proof_chain_failure_class"], ledger["first_proof_chain_failure_class"]);
        Assert.Equal(verdict["first_proof_chain_failure_event"], ledger["first_proof_chain_failure_event"]);
        Assert.Equal(verdict["first_proof_chain_failure_reason"], ledger["first_proof_chain_failure_reason"]);
        Assert.Equal(verdict["first_proof_chain_failure_sequence"], ledger["first_proof_chain_failure_sequence"]);
        Assert.Equal(verdict["first_proof_chain_failure_line_number"], ledger["first_proof_chain_failure_line_number"]);

        await using var ledgerStream = File.OpenRead(Path.Combine(result.ArtifactDir, "phase0-invariant-ledger.json"));
        using var ledgerJson = await JsonDocument.ParseAsync(ledgerStream);
        Assert.Equal(
            "runtime_unlock_retry_dispatch_successor_attempt_mismatch",
            ledgerJson.RootElement.GetProperty("first_proof_chain_failure_class").GetString());
        Assert.Equal("2", ledgerJson.RootElement.GetProperty("first_proof_chain_failure_sequence").GetString());
        Assert.Equal("2", ledgerJson.RootElement.GetProperty("first_proof_chain_failure_line_number").GetString());
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FirstProofChainFailure_UsesEarlierSourceMismatchBeforeLaterOwnerAndAttemptFailures()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_first_source_mismatch";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_rejected",
                    transferId,
                    suffix: "; decision_reason=retry_dispatch_source_publication_mismatch"),
                $"event=runtime_unlock_filetransfer_owner_missing_before_path_lease; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; trigger=runtime_unlock; rejection_reason=missing_current_filetransfer_route_publication_authority; decision_reason=coordinator_filetransfer_owner_required",
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_rejected",
                    transferId,
                    dispatchAttemptGeneration: 4,
                    suffix: "; decision_reason=retry_dispatch_successor_attempt_mismatch")),
            ["-LiveRouteProofMode", "None"]);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_source_publication_mismatch", verdict["recovery_failure_class"]);
        Assert.Equal("runtime_unlock_retry_source_publication_mismatch", verdict["first_proof_chain_failure_class"]);
        Assert.Equal("runtime_unlock_retry_dispatch_authority_rejected", verdict["first_proof_chain_failure_event"]);
        Assert.Equal("retry_dispatch_source_publication_mismatch", verdict["first_proof_chain_failure_reason"]);
        Assert.Equal("2", verdict["first_proof_chain_failure_sequence"]);
        Assert.Equal("2", verdict["first_proof_chain_failure_line_number"]);
        Assert.Equal("nlink.log", Path.GetFileName(verdict["first_proof_chain_failure_source_path"]));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FirstProofChainFailure_UsesEarlierAttemptMismatchBeforeLaterOwnerAndSourceFailures()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_first_attempt_mismatch";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_rejected",
                    transferId,
                    dispatchAttemptGeneration: 4,
                    suffix: "; decision_reason=retry_dispatch_successor_attempt_mismatch"),
                $"event=runtime_unlock_filetransfer_owner_missing_before_path_lease; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; trigger=runtime_unlock; rejection_reason=missing_current_filetransfer_route_publication_authority; decision_reason=coordinator_filetransfer_owner_required",
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_rejected",
                    transferId,
                    suffix: "; decision_reason=retry_dispatch_source_publication_mismatch")),
            ["-LiveRouteProofMode", "None"]);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_dispatch_successor_attempt_mismatch", verdict["recovery_failure_class"]);
        Assert.Equal("runtime_unlock_retry_dispatch_successor_attempt_mismatch", verdict["first_proof_chain_failure_class"]);
        Assert.Equal("runtime_unlock_retry_dispatch_authority_rejected", verdict["first_proof_chain_failure_event"]);
        Assert.Equal("retry_dispatch_successor_attempt_mismatch", verdict["first_proof_chain_failure_reason"]);
        Assert.Equal("2", verdict["first_proof_chain_failure_sequence"]);
        Assert.Equal("2", verdict["first_proof_chain_failure_line_number"]);
        Assert.Equal("nlink.log", Path.GetFileName(verdict["first_proof_chain_failure_source_path"]));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FirstProofChainFailure_UsesEarlierTerminalLifecycleViolationBeforeLaterRetryMismatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_terminal_lifecycle_before_retry_mismatch";
        const string sessionId = "sess_runtime_unlock_dispatch_proof";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; selection_reason=regular_nkn_default_v4", secondsOffset: 0),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=CanceledLocal; error_code=session_end; status_message=session_end", secondsOffset: 10),
            LogLine($"event=filetransfer_data_frame_ignored; transport=nkn; transfer_id={transferId}; session_id={sessionId}; frame_type=filetransfer.chunk_batch.v4; chunk_index=0; reason=post_terminal_late_sender_frame", secondsOffset: 20),
            LogLine(
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_rejected",
                    transferId,
                    dispatchAttemptGeneration: 4,
                    suffix: "; decision_reason=retry_dispatch_successor_attempt_mismatch"),
                secondsOffset: 30),
        };

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "None"]);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("terminal_lifecycle", verdict["recovery_failure_class"]);
        Assert.Equal("terminal_lifecycle", verdict["first_proof_chain_failure_class"]);
        Assert.Equal("filetransfer_data_frame_ignored", verdict["first_proof_chain_failure_event"]);
        Assert.Equal("terminal_lifecycle", verdict["first_proof_chain_failure_reason"]);
        Assert.Equal("3", verdict["first_proof_chain_failure_sequence"]);
        Assert.Equal("3", verdict["first_proof_chain_failure_line_number"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FirstProofChainFailure_UsesEarlierRetryMismatchBeforeLaterTerminalLifecycleViolation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_retry_mismatch_before_terminal_lifecycle";
        const string sessionId = "sess_runtime_unlock_dispatch_proof";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; selection_reason=regular_nkn_default_v4", secondsOffset: 0),
            LogLine(
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_rejected",
                    transferId,
                    dispatchAttemptGeneration: 4,
                    suffix: "; decision_reason=retry_dispatch_successor_attempt_mismatch"),
                secondsOffset: 10),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=CanceledLocal; error_code=session_end; status_message=session_end", secondsOffset: 20),
            LogLine($"event=filetransfer_data_frame_ignored; transport=nkn; transfer_id={transferId}; session_id={sessionId}; frame_type=filetransfer.chunk_batch.v4; chunk_index=0; reason=post_terminal_late_sender_frame", secondsOffset: 30),
        };

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "None"]);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_dispatch_successor_attempt_mismatch", verdict["recovery_failure_class"]);
        Assert.Equal("runtime_unlock_retry_dispatch_successor_attempt_mismatch", verdict["first_proof_chain_failure_class"]);
        Assert.Equal("runtime_unlock_retry_dispatch_authority_rejected", verdict["first_proof_chain_failure_event"]);
        Assert.Equal("retry_dispatch_successor_attempt_mismatch", verdict["first_proof_chain_failure_reason"]);
        Assert.Equal("2", verdict["first_proof_chain_failure_sequence"]);
        Assert.Equal("2", verdict["first_proof_chain_failure_line_number"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RouteLessFallbackDiagnostic_DoesNotManufactureFallbackFirstEdge()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_route_less_fallback_diagnostic";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                $"event=filetransfer_fallback_tail_yield_local_authority_rejected; direction=outbound; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; route=(none); protocol_version=0; live_route_epoch=0; reason=authority_tuple_mismatch"),
            ["-LiveRouteProofMode", "None"]);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.False(
            verdict["recovery_failure_class"].StartsWith("fallback_", StringComparison.Ordinal),
            $"Route-less diagnostic manufactured fallback classification: {verdict["recovery_failure_class"]}");
        Assert.Equal("(none)", verdict["first_proof_chain_failure_class"]);
        Assert.Equal("(none)", verdict["first_proof_chain_failure_event"]);
        Assert.Equal("(none)", verdict["first_proof_chain_failure_reason"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryMissingDispatchWithValidSuccessor_PassesChain()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_valid_missing_successor";
        const string successorId = "fta2-runtime-unlock-test-2";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=MissingDispatch; dispatch_observed=0"),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_granted",
                    transferId,
                    authorityId: successorId,
                    predecessorAuthorityId: "fta2-runtime-unlock-test-1",
                    retiredGeneration: 12,
                    dispatchAttemptGeneration: 2,
                    missingDispatchSuccessorGeneration: 1),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_observed",
                    transferId,
                    authorityId: successorId,
                    predecessorAuthorityId: "fta2-runtime-unlock-test-1",
                    retiredGeneration: 12,
                    dispatchAttemptGeneration: 2,
                    missingDispatchSuccessorGeneration: 1),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=DispatchObserved; dispatch_observed=1",
                    authorityId: successorId,
                    predecessorAuthorityId: "fta2-runtime-unlock-test-1",
                    retiredGeneration: 12,
                    dispatchAttemptGeneration: 2,
                    missingDispatchSuccessorGeneration: 1)),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch_count"]);
        Assert.NotEqual("runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryMissingDispatchSuccessorWithAdvancedBridgeState_PassesChain()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_successor_advanced_bridge";
        const string successorId = "fta2-runtime-unlock-test-2";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=MissingDispatch; dispatch_observed=0"),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_granted",
                    transferId,
                    authorityId: successorId,
                    predecessorAuthorityId: "fta2-runtime-unlock-test-1",
                    retiredGeneration: 12,
                    dispatchAttemptGeneration: 2,
                    missingDispatchSuccessorGeneration: 1,
                    bridgeRecoveryGeneration: 4,
                    bridgeDecisionGeneration: 8)),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryMissingDispatchIgnoresLegacyRejectionAndOppositeTerminal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_unmatched_bounded_outcome";
        var fixture = BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=MissingDispatch; dispatch_observed=0"),
                $"event=runtime_unlock_retry_successor_intent_rejected; predecessor_authority_id=fta2-runtime-unlock-test-1; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}")
            .Where(line =>
                !line.Contains("event=file_transfer_outbound_terminal", StringComparison.Ordinal) &&
                !line.Contains("event=transfer_terminal", StringComparison.Ordinal))
            .ToArray();
        var result = await RunAnalyzeFixtureAsync(fixture, ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryMissingDispatchWithCorrelatedRejection_IsBounded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_correlated_successor_rejection";
        var fixture = BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=MissingDispatch; dispatch_observed=0"),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_successor_intent_rejected",
                    transferId,
                    suffix: "; requested_predecessor_authority_id=fta2-runtime-unlock-test-1; requested_retired_generation=12; requested_dispatch_attempt_generation=2; requested_missing_dispatch_successor_generation=1; intent_disposition=Rejected"))
            .Where(line =>
                !line.Contains("event=file_transfer_outbound_terminal", StringComparison.Ordinal) &&
                !line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal) &&
                !line.Contains("event=transfer_terminal", StringComparison.Ordinal))
            .ToArray();
        var result = await RunAnalyzeFixtureAsync(fixture, ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySchema2OrphanedSupersession_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_orphaned_supersession";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_released_after_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; successor_authority_id=(none); settlement_outcome=Superseded; dispatch_observed=0")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fail", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_orphaned_supersession_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySchema2SuccessorTupleMismatch_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_successor_tuple_mismatch";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_released_after_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; successor_authority_id=fta2-runtime-unlock-test-2; settlement_outcome=Superseded; dispatch_observed=0"),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_granted",
                    transferId,
                    runtimeGeneration: 5,
                    authorityId: "fta2-runtime-unlock-test-2",
                    predecessorAuthorityId: "fta2-runtime-unlock-test-1")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fail", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_orphaned_supersession_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySchema2DuplicateSuccessor_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_duplicate_successor";
        const string successorId = "fta2-runtime-unlock-test-2";
        var successorGrant = RuntimeUnlockRetrySchema2Line(
            "runtime_unlock_retry_dispatch_authority_granted",
            transferId,
            authorityId: successorId,
            predecessorAuthorityId: "fta2-runtime-unlock-test-1");
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: $"; successor_authority_id={successorId}; settlement_outcome=Superseded; dispatch_observed=0"),
                successorGrant,
                successorGrant),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fail", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_orphaned_supersession_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySchema2SuccessorBeforeSettlement_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_successor_before_settlement";
        const string successorId = "fta2-runtime-unlock-test-2";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_granted",
                    transferId,
                    authorityId: successorId,
                    predecessorAuthorityId: "fta2-runtime-unlock-test-1"),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: $"; successor_authority_id={successorId}; settlement_outcome=Superseded; dispatch_observed=0")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fail", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_orphaned_supersession_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySchema2CannotLinkLegacySuccessor_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_legacy_successor";
        const string successorId = "fta2-runtime-unlock-test-2";
        var legacySuccessor = RuntimeUnlockRetrySchema2Line(
                "runtime_unlock_retry_dispatch_authority_granted",
                transferId,
                authorityId: successorId,
                predecessorAuthorityId: "fta2-runtime-unlock-test-1")
            .Replace("authority_schema=2", "authority_schema=1", StringComparison.Ordinal);
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: $"; successor_authority_id={successorId}; settlement_outcome=Superseded; dispatch_observed=0"),
                legacySuccessor),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fail", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_orphaned_supersession_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySchema2ValidSuccessorChain_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_valid_successor_chain";
        const string successorId = "fta2-runtime-unlock-test-2";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: $"; successor_authority_id={successorId}; settlement_outcome=Superseded; dispatch_observed=0"),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_granted",
                    transferId,
                    authorityId: successorId,
                    predecessorAuthorityId: "fta2-runtime-unlock-test-1"),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_released_after_regular_v4_receive_recovery",
                    transferId,
                    authorityId: successorId,
                    predecessorAuthorityId: "fta2-runtime-unlock-test-1"),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_observed",
                    transferId,
                    authorityId: successorId,
                    predecessorAuthorityId: "fta2-runtime-unlock-test-1"),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; successor_authority_id=(none); settlement_outcome=DispatchObserved; dispatch_observed=1",
                    authorityId: successorId,
                    predecessorAuthorityId: "fta2-runtime-unlock-test-1")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("pass", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_orphaned_supersession_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySettlementWithoutCoreAcceptance_IsIncomplete()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_settlement_unaccepted";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_released_after_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_observed", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=DispatchObserved; dispatch_observed=1"),
                RuntimeUnlockRetrySettlementReportedLine(transferId, "DispatchObserved"),
                RuntimeUnlockRetrySettlementDecidedLine(
                    transferId,
                    "DispatchObserved",
                    decisionStatus: "Rejected"),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_settlement_decision_ignored",
                    transferId,
                    suffix: "; settlement_outcome=DispatchObserved; settlement_reason=accepted; decision_status=Rejected",
                    decisionReason: "retry_dispatch_settlement_late_or_mismatched")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_dispatch_settlement_unaccepted", verdict["recovery_failure_class"]);
        Assert.Equal("fail", verdict["runtime_unlock_retry_dispatch_chain_closure_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_settlement_reported_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_settlement_accepted_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_settlement_rejected_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_settlement_decision_ignored_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_settlement_acceptance_missing_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_unsettled_authority_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySettlementAuthorityGenerationMismatch_IsIncomplete()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_settlement_authority_generation_mismatch";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_released_after_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_observed", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=DispatchObserved; dispatch_observed=1"),
                RuntimeUnlockRetrySettlementReportedLine(
                    transferId,
                    "DispatchObserved",
                    authorityGeneration: 2),
                RuntimeUnlockRetrySettlementDecidedLine(
                    transferId,
                    "DispatchObserved",
                    authorityGeneration: 2)),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_dispatch_settlement_unaccepted", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_settlement_acceptance_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySettlementDecisionTargetMismatch_IsIncomplete()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_settlement_target_mismatch";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_released_after_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_observed", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=DispatchObserved; dispatch_observed=1"),
                RuntimeUnlockRetrySettlementReportedLine(transferId, "DispatchObserved"),
                RuntimeUnlockRetrySettlementDecidedLine(
                    transferId,
                    "DispatchObserved",
                    targetTransport: "regular_nkn")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_dispatch_settlement_unaccepted", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_settlement_acceptance_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySettlementNestedPublicationOwnerTamper_IsIncomplete()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_nested_publication_owner_tamper";
        var tamperedDecision = RuntimeUnlockRetrySettlementDecidedLine(
                transferId,
                "DispatchObserved")
            .Replace(
                "source_publication_runtime_owner_terminal_generation=0",
                "source_publication_runtime_owner_terminal_generation=9",
                StringComparison.Ordinal);
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_released_after_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_observed", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=DispatchObserved; dispatch_observed=1"),
                RuntimeUnlockRetrySettlementReportedLine(transferId, "DispatchObserved"),
                tamperedDecision),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_dispatch_settlement_unaccepted", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_settlement_acceptance_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryBoundedRejectionWithoutChainClosure_IsIncomplete()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_bounded_closure_missing";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_released_after_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=MissingDispatch; dispatch_observed=0"),
                RuntimeUnlockRetrySettlementReportedLine(transferId, "MissingDispatch"),
                RuntimeUnlockRetrySettlementDecidedLine(transferId, "MissingDispatch"),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_successor_intent_rejected",
                    transferId,
                    suffix: "; requested_predecessor_authority_id=fta2-runtime-unlock-test-1; requested_retired_generation=12; requested_dispatch_attempt_generation=2; requested_missing_dispatch_successor_generation=1; intent_disposition=Rejected",
                    decisionReason: "retry_dispatch_missing_successor_limit_exhausted"),
                RuntimeUnlockRetryMissingDispatchLimitRejectedLine(transferId),
                RuntimeUnlockRetryChainClosedLine(
                    transferId,
                    targetTransport: "regular_nkn")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_dispatch_chain_closure_incomplete", verdict["recovery_failure_class"]);
        Assert.Equal("fail", verdict["runtime_unlock_retry_dispatch_chain_closure_proof_verdict"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_complete_bounded_closure_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_incomplete_bounded_closure_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryAcceptedMissingDispatchWithBoundedClosure_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_bounded_closure_complete";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_released_after_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=MissingDispatch; dispatch_observed=0"),
                RuntimeUnlockRetrySettlementReportedLine(transferId, "MissingDispatch"),
                RuntimeUnlockRetrySettlementDecidedLine(transferId, "MissingDispatch"),
                RuntimeUnlockRetryMissingDispatchLimitRejectedLine(transferId),
                RuntimeUnlockRetryChainClosedLine(transferId)),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("pass", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("pass", verdict["runtime_unlock_retry_dispatch_chain_closure_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_settlement_accepted_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_complete_bounded_closure_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_incomplete_bounded_closure_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryFreshChainAfterBoundedClosure_StartsIndependently()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_fresh_after_closure";
        const string freshAuthorityId = "fta2-runtime-unlock-test-2";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_released_after_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=MissingDispatch; dispatch_observed=0"),
                RuntimeUnlockRetrySettlementReportedLine(transferId, "MissingDispatch"),
                RuntimeUnlockRetrySettlementDecidedLine(transferId, "MissingDispatch"),
                RuntimeUnlockRetryMissingDispatchLimitRejectedLine(transferId),
                RuntimeUnlockRetryChainClosedLine(transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_granted",
                    transferId,
                    authorityId: freshAuthorityId,
                    retiredGeneration: 13),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_released_after_regular_v4_receive_recovery",
                    transferId,
                    authorityId: freshAuthorityId,
                    retiredGeneration: 13),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_observed",
                    transferId,
                    authorityId: freshAuthorityId,
                    retiredGeneration: 13),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=DispatchObserved; dispatch_observed=1",
                    authorityId: freshAuthorityId,
                    retiredGeneration: 13),
                RuntimeUnlockRetrySettlementReportedLine(
                    transferId,
                    "DispatchObserved",
                    authorityId: freshAuthorityId,
                    retiredGeneration: 13),
                RuntimeUnlockRetrySettlementDecidedLine(
                    transferId,
                    "DispatchObserved",
                    authorityId: freshAuthorityId,
                    retiredGeneration: 13)),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("pass", verdict["runtime_unlock_retry_dispatch_chain_closure_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_fresh_independent_grant_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_invalid_fresh_grant_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySupersededPredecessorIdempotentWithSuccessor_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_superseded_idempotent";
        const string successorAuthorityId = "fta2-runtime-unlock-test-2";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_released_after_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: $"; successor_authority_id={successorAuthorityId}; settlement_outcome=Superseded; dispatch_observed=0",
                    decisionReason: "coordinator_successor_authorized"),
                RuntimeUnlockRetrySettlementReportedLine(
                    transferId,
                    "Superseded",
                    settlementReason: "coordinator_successor_authorized"),
                RuntimeUnlockRetrySettlementDecidedLine(
                    transferId,
                    "Superseded",
                    decisionStatus: "Idempotent",
                    settlementReason: "coordinator_successor_authorized"),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_granted",
                    transferId,
                    authorityId: successorAuthorityId,
                    predecessorAuthorityId: "fta2-runtime-unlock-test-1",
                    dispatchAttemptGeneration: 2,
                    authorityGeneration: 2),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_released_after_regular_v4_receive_recovery",
                    transferId,
                    authorityId: successorAuthorityId,
                    predecessorAuthorityId: "fta2-runtime-unlock-test-1",
                    dispatchAttemptGeneration: 2,
                    authorityGeneration: 2),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_observed",
                    transferId,
                    authorityId: successorAuthorityId,
                    predecessorAuthorityId: "fta2-runtime-unlock-test-1",
                    dispatchAttemptGeneration: 2,
                    authorityGeneration: 2),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=DispatchObserved; dispatch_observed=1",
                    authorityId: successorAuthorityId,
                    predecessorAuthorityId: "fta2-runtime-unlock-test-1",
                    dispatchAttemptGeneration: 2,
                    authorityGeneration: 2),
                RuntimeUnlockRetrySettlementReportedLine(
                    transferId,
                    "DispatchObserved",
                    authorityId: successorAuthorityId,
                    predecessorAuthorityId: "fta2-runtime-unlock-test-1",
                    dispatchAttemptGeneration: 2,
                    authorityGeneration: 2),
                RuntimeUnlockRetrySettlementDecidedLine(
                    transferId,
                    "DispatchObserved",
                    authorityId: successorAuthorityId,
                    predecessorAuthorityId: "fta2-runtime-unlock-test-1",
                    dispatchAttemptGeneration: 2,
                    authorityGeneration: 2)),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("pass", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_settlement_accepted_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_settlement_idempotent_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_settlement_acceptance_missing_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_orphaned_supersession_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryTerminalSettlementFirstVisibleIdempotent_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_terminal_idempotent";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_released_after_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=TerminalRejected; dispatch_observed=0",
                    decisionReason: "transfer_terminal"),
                RuntimeUnlockRetrySettlementReportedLine(
                    transferId,
                    "TerminalRejected",
                    settlementReason: "transfer_terminal"),
                RuntimeUnlockRetrySettlementDecidedLine(
                    transferId,
                    "TerminalRejected",
                    decisionStatus: "Idempotent",
                    settlementReason: "transfer_terminal")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("pass", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_settlement_idempotent_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_settlement_acceptance_missing_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryDuplicateAndLateSettlementDecisions_AreDiagnosticOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_duplicate_late_decisions";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_released_after_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_observed", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=DispatchObserved; dispatch_observed=1"),
                RuntimeUnlockRetrySettlementReportedLine(transferId, "DispatchObserved"),
                RuntimeUnlockRetrySettlementDecidedLine(transferId, "DispatchObserved"),
                RuntimeUnlockRetrySettlementDecidedLine(transferId, "DispatchObserved", "Idempotent"),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_settlement_decision_ignored",
                    transferId,
                    suffix: "; settlement_outcome=MissingDispatch; settlement_reason=late_predecessor; decision_status=Rejected",
                    decisionReason: "retry_dispatch_settlement_late_or_mismatched")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("pass", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("pass", verdict["runtime_unlock_retry_dispatch_chain_closure_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_settlement_accepted_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_settlement_idempotent_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_settlement_decision_ignored_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySchema2MatchingHoldAfterGrant_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_schema2_post_grant_hold";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_released_after_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_held_by_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_observed", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=DispatchObserved; dispatch_observed=1")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_post_grant_hold_count"]);
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("runtime_unlock_retry_dispatch_held_after_authority_granted", verdict["recovery_failure_class"]);
        Assert.Equal("fail", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySchema2MatchingHoldBeforeGrant_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_schema2_pre_grant_hold";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_held_by_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_released_after_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_observed", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_contract_settled",
                    transferId,
                    suffix: "; settlement_outcome=DispatchObserved; dispatch_observed=1")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("pass", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_dispatch_post_grant_hold_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySchema2DuplicateObserved_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_schema2_duplicate";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_released_after_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_observed", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_observed", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_contract_settled", transferId)),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fail", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_duplicate_observed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySchema2GenericRecoveryFailureDoesNotSettleGrant()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_unsettled_grant";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_granted",
                    transferId),
                $"event=session_recovery_contract_failed; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; state=failed; authority_failure_reason=runtime_unlock_failure"),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_dispatch_contract_unsettled", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_unsettled_authority_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySchema2BrokenStateBudgetBypassClassifiesFirstEdge()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_budget_bypass";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_granted",
                    transferId,
                    dispatchAttemptGeneration: 3,
                    missingDispatchSuccessorGeneration: 1),
                $"event=runtime_unlock_broken_state_retry_suppressed; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; reason=runtime_unlock_offer_send_not_observed; final_reason=runtime_unlock_broken_state_retry_exhausted_offer_send_not_observed",
                $"event=session_recovery_contract_failed; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; state=failed; authority_failure_reason=runtime_unlock_broken_state_retry_exhausted_offer_send_not_observed"),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_dispatch_grant_bypassed_by_retry_budget", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_unsettled_authority_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_grant_bypassed_by_retry_budget_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetrySchema2TupleMismatchDoesNotCrossMatch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_schema2_mismatch";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_dispatch_authority_granted", transferId),
                RuntimeUnlockRetrySchema2Line("runtime_unlock_retry_released_after_regular_v4_receive_recovery", transferId),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_observed",
                    transferId,
                    runtimeGeneration: 5),
                RuntimeUnlockRetryDispatchObservedLine(transferId)),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("fail", verdict["runtime_unlock_retry_dispatch_proof_verdict"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_missing_count"]);
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
    public async Task AnalyzeRetained_RuntimeUnlockRetryBlockedByRegularV4RecoveryWithoutSettlement_ClassifiesMissingSettlement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_regular_v4_settlement_missing";
        const string sessionId = "sess_runtime_unlock_dispatch_proof";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine("event=tuna_acceleration_activation_offer_not_observed; session_id=sess_runtime_unlock_dispatch_proof; transfer_id=transfer_runtime_unlock_regular_v4_settlement_missing; trigger=runtime_unlock; generation=11; recovery_reason=session_liveness_timeout_pending; retry_reason=runtime_unlock_offer_send_not_observed; retry_after_recovery_armed=1; recovery_requested=1; interruption_reason=offer_interrupted_by_bridge_recovery", secondsOffset: 10),
            LogLine("event=runtime_unlock_retry_blocked_by_regular_v4_receive_recovery; session_id=sess_runtime_unlock_dispatch_proof; transfer_id=transfer_runtime_unlock_regular_v4_settlement_missing; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; retired_generation=11; contract_generation=7; offer_generation=11; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; regular_v4_liveness_generation=3; bridge_decision_generation=5; blocker_reason=regular_v4_bridge_recovery_started_awaiting_filetransfer_proof; settlement_trigger=recovery_already_settled; decision_reason=awaiting_regular_v4_recovery_settlement", secondsOffset: 20),
            LogLine("event=runtime_unlock_retry_dispatch_held_by_regular_v4_receive_recovery; session_id=sess_runtime_unlock_dispatch_proof; transfer_id=transfer_runtime_unlock_regular_v4_settlement_missing; trigger=regular_v4_receive_recovery_pending; blocker_reason=regular_v4_bridge_recovery_started_awaiting_filetransfer_proof; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; regular_v4_liveness_generation=3; bridge_decision_generation=5; decision_reason=awaiting_regular_v4_recovery_settlement", secondsOffset: 30),
            LogLine("event=session_liveness_timeout; session_id=sess_runtime_unlock_dispatch_proof; reason=runtime_unlock_liveness_timeout", secondsOffset: 80),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; route=regular_nkn_v4_fast; protocol_version=4", secondsOffset: 89),
            LogLine($"event=file_transfer_inbound_terminal; role=Helper; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; route=regular_nkn_v4_fast; protocol_version=4", secondsOffset: 90),
        };

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("runtime_unlock_retry_regular_v4_recovery_settlement_missing", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_blocked_by_regular_v4_recovery_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_held_by_regular_v4_recovery_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_rejected_after_regular_v4_recovery_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_regular_v4_recovery_settlement_missing_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_retry_regular_v4_recovery_settlement_missing", stability["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryBlockedByRegularV4RecoveryWithAcceptedSettlementButNoOutcome_ClassifiesMissingSettlement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_regular_v4_accepted_settlement_no_outcome";
        const string sessionId = "sess_runtime_unlock_dispatch_proof";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; session_id={sessionId}; transfer_id={transferId}; trigger=runtime_unlock; generation=11; recovery_reason=session_liveness_timeout_pending; retry_reason=runtime_unlock_offer_send_not_observed; retry_after_recovery_armed=1; recovery_requested=1; interruption_reason=offer_interrupted_by_bridge_recovery", secondsOffset: 10),
            LogLine($"event=runtime_unlock_retry_blocked_by_regular_v4_receive_recovery; session_id={sessionId}; transfer_id={transferId}; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; retired_generation=11; contract_generation=7; offer_generation=11; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; regular_v4_liveness_generation=3; bridge_decision_generation=5; blocker_reason=receive_stall_recovery_in_progress; settlement_trigger=recovery_already_settled; decision_reason=awaiting_regular_v4_recovery_settlement", secondsOffset: 20),
            LogLine($"event=runtime_unlock_retry_dispatch_held_by_regular_v4_receive_recovery; session_id={sessionId}; transfer_id={transferId}; trigger=regular_v4_receive_recovery_pending; blocker_reason=receive_stall_recovery_in_progress; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; regular_v4_liveness_generation=3; bridge_decision_generation=5; decision_reason=awaiting_regular_v4_recovery_settlement", secondsOffset: 30),
            LogLine($"event=filetransfer_regular_v4_frontier_repair_settlement_accepted; source=nkn_bridge_pressure; stage=settlement; session_id={sessionId}; transfer_id={transferId}; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=5; route_publication_generation=3; route_commit_generation=0; target_transport=regular_nkn; repair_request_key=regular_v4_peer_silence_safety_replay:3:3:64:1; frontier_chunk_index=3; durable_received_highest_chunk_index=2; requested_chunk_count=64; decision_generation=13; outcome=missing_dispatch; settlement_reason=repair_dispatch_not_observed; pressure_pending_repair_count=64; pressure_reason=regular_v4_peer_silence_safety_replay; cleared_pressure=1; rejection_reason=none; decision_reason=accepted", secondsOffset: 35),
            LogLine($"event=session_recovery_contract_failed; session_id={sessionId}; transfer_id={transferId}; contract_generation=7; offer_generation=11; retired_offer_generation=11; kind=runtime_unlock_activation; state=failed; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=0; retry_observed=0; queued_behind_active_negotiation=0; retry_authority_pending=0; retry_authority_granted=0; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=(none); authority_failure_reason=regular_v4_recovery_no_current_settlement_proof", secondsOffset: 36),
            LogLine("event=session_liveness_timeout; session_id=sess_runtime_unlock_dispatch_proof; reason=runtime_unlock_liveness_timeout", secondsOffset: 80),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; route=regular_nkn_v4_fast; protocol_version=4", secondsOffset: 89),
            LogLine($"event=file_transfer_inbound_terminal; role=Helper; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; route=regular_nkn_v4_fast; protocol_version=4", secondsOffset: 90),
        };

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("runtime_unlock_retry_regular_v4_recovery_settlement_missing", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_regular_v4_recovery_settlement_missing_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_rejected_after_regular_v4_recovery_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryTemporaryHoldWithAcceptedConsumedHandoff_DoesNotClassifyFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_regular_v4_handoff_consumed";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetryBlockedByRegularV4RecoveryLine(transferId),
                RuntimeUnlockRetryHeldByRegularV4RecoveryLine(transferId),
                RuntimeUnlockRegularV4RecoveryLifecycleReportedLine(transferId),
                RuntimeUnlockRegularV4RecoveryLifecycleDecidedLine(transferId),
                RuntimeUnlockRegularV4RecoverySettlementReportedLine(transferId),
                RuntimeUnlockRegularV4RecoverySettlementDecidedLine(transferId)),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_regular_v4_recovery_settlement_missing_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_regular_v4_settlement_handoff_bypassed_count"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_recovery_settlement_reported_count"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_recovery_settlement_accepted_count"]);
        Assert.Equal("(none)", verdict["first_proof_chain_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryPhase2ReportAndLegacyReleaseWithoutDecision_RemainsMissingSettlement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_phase2_report_legacy_release_missing";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetryBlockedByRegularV4RecoveryLine(transferId),
                RuntimeUnlockRetryHeldByRegularV4RecoveryLine(transferId),
                RuntimeUnlockRegularV4RecoverySettlementReportedLine(transferId),
                RuntimeUnlockRetryReleasedLine(transferId)),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_regular_v4_recovery_settlement_missing", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_regular_v4_recovery_settlement_missing_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_regular_v4_settlement_handoff_bypassed_count"]);
        Assert.Equal("file_transfer_outbound_terminal", verdict["first_proof_chain_failure_event"]);
        Assert.Equal("7", verdict["first_proof_chain_failure_sequence"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryPhase2ReportAndLegacyReleaseBeforeStaleGrant_ClassifiesHandoffBypass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_phase2_report_legacy_release_bypass";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetryBlockedByRegularV4RecoveryLine(transferId),
                RuntimeUnlockRetryHeldByRegularV4RecoveryLine(transferId),
                RuntimeUnlockRegularV4RecoverySettlementReportedLine(transferId),
                RuntimeUnlockRetryReleasedLine(transferId),
                RuntimeUnlockRetryDispatchPendingRequestRecheckedLine(transferId, requestLivenessGeneration: 0, requestBridgeDecisionGeneration: 0),
                RuntimeUnlockRetryDispatchGrantedLine(transferId, queuedBehindActiveNegotiation: false)),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_regular_v4_settlement_handoff_bypassed", verdict["recovery_failure_class"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_regular_v4_recovery_settlement_missing_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_regular_v4_settlement_handoff_bypassed_count"]);
        Assert.Equal("runtime_unlock_retry_dispatch_pending_request_rechecked", verdict["first_proof_chain_failure_event"]);
        Assert.Equal("6", verdict["first_proof_chain_failure_sequence"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryHeldSettlementThenStalePendingRequestGranted_ClassifiesHandoffBypassAtRecheck()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_regular_v4_handoff_bypassed";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetryBlockedByRegularV4RecoveryLine(transferId),
                RuntimeUnlockRetryHeldByRegularV4RecoveryLine(transferId),
                $"event=filetransfer_regular_v4_frontier_repair_settlement_accepted; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; bridge_decision_generation=5; repair_request_key=regular_v4_peer_silence_safety_replay:3:3:64:1; outcome=missing_dispatch; settlement_reason=repair_dispatch_not_observed; decision_reason=accepted",
                $"event=runtime_unlock_regular_v4_frontier_repair_settlement_handoff_adopted; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; bridge_decision_generation=5; repair_request_key=regular_v4_peer_silence_safety_replay:3:3:64:1; adopted_recovery_liveness_generation=3; adopted_bridge_decision_generation=5; handoff_disposition=Adopted; decision_reason=adopted_by_receive_recovery",
                $"event=runtime_unlock_regular_v4_frontier_repair_settlement_held; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; bridge_decision_generation=5; repair_request_key=regular_v4_peer_silence_safety_replay:3:3:64:1; handoff_disposition=Held; decision_reason=awaiting_receive_recovery_lifecycle_boundary",
                RuntimeUnlockRetryDispatchPendingRequestRecheckedLine(transferId, requestLivenessGeneration: 0, requestBridgeDecisionGeneration: 0),
                RuntimeUnlockRetryDispatchGrantedLine(transferId, queuedBehindActiveNegotiation: false),
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_rejected",
                    transferId,
                    dispatchAttemptGeneration: 5,
                    missingDispatchSuccessorGeneration: 2,
                    suffix: "; decision_reason=retry_dispatch_successor_attempt_mismatch"),
                $"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_runtime_unlock_dispatch_proof; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; target_transport=regular_nkn; live_route_epoch=2"),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_regular_v4_settlement_handoff_bypassed", verdict["recovery_failure_class"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_regular_v4_recovery_settlement_missing_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_regular_v4_settlement_handoff_bypassed_count"]);
        Assert.Equal("runtime_unlock_retry_regular_v4_settlement_handoff_bypassed", verdict["first_proof_chain_failure_class"]);
        Assert.Equal("runtime_unlock_retry_dispatch_pending_request_rechecked", verdict["first_proof_chain_failure_event"]);
        Assert.Equal("regular_v4_recovery_settlement_handoff_bypassed", verdict["first_proof_chain_failure_reason"]);
        Assert.Equal("7", verdict["first_proof_chain_failure_sequence"]);
        Assert.Equal("7", verdict["first_proof_chain_failure_line_number"]);
        Assert.NotEqual("fallback_authority", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_successor_attempt_mismatch_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal(verdict["first_proof_chain_failure_class"], stability["first_proof_chain_failure_class"]);
        Assert.Equal(verdict["first_proof_chain_failure_event"], stability["first_proof_chain_failure_event"]);
        Assert.Equal(verdict["first_proof_chain_failure_sequence"], stability["first_proof_chain_failure_sequence"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryMalformedConsumedDecision_RemainsMissingSettlement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_regular_v4_malformed_decision";
        var malformedDecision = RuntimeUnlockRegularV4RecoverySettlementDecidedLine(transferId)
            .Replace("authority_id=fta2-regular-v4-release-1", "authority_id=(none)", StringComparison.Ordinal);
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetryBlockedByRegularV4RecoveryLine(transferId),
                RuntimeUnlockRetryHeldByRegularV4RecoveryLine(transferId),
                RuntimeUnlockRegularV4RecoverySettlementReportedLine(transferId),
                malformedDecision),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_regular_v4_recovery_settlement_missing", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_regular_v4_recovery_settlement_missing_count"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_regular_v4_settlement_handoff_bypassed_count"]);
        Assert.Equal("file_transfer_outbound_terminal", verdict["first_proof_chain_failure_event"]);
        Assert.Equal("7", verdict["first_proof_chain_failure_sequence"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryHeldSettlementThenGrantWithoutRecheck_ClassifiesHandoffBypassAtGrant()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_regular_v4_grant_only_bypass";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                RuntimeUnlockRetryBlockedByRegularV4RecoveryLine(transferId),
                RuntimeUnlockRetryHeldByRegularV4RecoveryLine(transferId),
                $"event=filetransfer_regular_v4_frontier_repair_settlement_accepted; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; bridge_decision_generation=5; repair_request_key=regular_v4_peer_silence_safety_replay:3:3:64:1; outcome=missing_dispatch; settlement_reason=repair_dispatch_not_observed; decision_reason=accepted",
                $"event=runtime_unlock_regular_v4_frontier_repair_settlement_handoff_adopted; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; bridge_decision_generation=5; repair_request_key=regular_v4_peer_silence_safety_replay:3:3:64:1; adopted_recovery_liveness_generation=3; adopted_bridge_decision_generation=5; handoff_disposition=Adopted; decision_reason=adopted_by_receive_recovery",
                $"event=runtime_unlock_regular_v4_frontier_repair_settlement_held; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; bridge_decision_generation=5; repair_request_key=regular_v4_peer_silence_safety_replay:3:3:64:1; handoff_disposition=Held; decision_reason=awaiting_receive_recovery_lifecycle_boundary",
                RuntimeUnlockRetrySchema2Line(
                    "runtime_unlock_retry_dispatch_authority_granted",
                    transferId,
                    suffix: "; request_regular_v4_liveness_generation=0; request_bridge_decision_generation=0")),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_regular_v4_settlement_handoff_bypassed", verdict["recovery_failure_class"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_regular_v4_recovery_settlement_missing_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_regular_v4_settlement_handoff_bypassed_count"]);
        Assert.Equal("runtime_unlock_retry_dispatch_authority_granted", verdict["first_proof_chain_failure_event"]);
        Assert.Equal("7", verdict["first_proof_chain_failure_sequence"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryResolvedBlockedChainAndIndependentHeldChain_RemainsPendingWithoutBoundary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string resolvedTransferId = "transfer_runtime_unlock_regular_v4_resolved_chain";
        const string heldTransferId = "transfer_runtime_unlock_regular_v4_held_only_chain";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                resolvedTransferId,
                RuntimeUnlockRetryBlockedByRegularV4RecoveryLine(resolvedTransferId),
                RuntimeUnlockRetryHeldByRegularV4RecoveryLine(resolvedTransferId),
                RuntimeUnlockRegularV4RecoveryLifecycleReportedLine(resolvedTransferId),
                RuntimeUnlockRegularV4RecoveryLifecycleDecidedLine(resolvedTransferId),
                RuntimeUnlockRegularV4RecoverySettlementReportedLine(resolvedTransferId),
                RuntimeUnlockRegularV4RecoverySettlementDecidedLine(resolvedTransferId),
                $"event=runtime_unlock_retry_dispatch_held_by_regular_v4_receive_recovery; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={heldTransferId}; trigger=regular_v4_receive_recovery_pending; blocker_reason=receive_stall_recovery_in_progress; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=2; regular_v4_liveness_generation=7; bridge_decision_generation=9; decision_reason=awaiting_regular_v4_recovery_settlement"),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", verdict["recovery_failure_class"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_regular_v4_recovery_settlement_missing_count"]);
        Assert.Equal("(none)", verdict["first_proof_chain_failure_event"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockFileTransferOwnerMissingBeforePathLease_ClassifiesFirstEdge()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_owner_missing";
        const string sessionId = "sess_runtime_unlock_owner_missing";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=runtime_unlock_filetransfer_owner_missing_before_path_lease; session_id={sessionId}; transfer_id=(none); trigger=runtime_unlock; rejection_reason=missing_current_filetransfer_route_publication_authority; decision_reason=coordinator_filetransfer_owner_required", secondsOffset: 10),
            LogLine($"event=runtime_unlock_filetransfer_path_lease_rejected_no_current_owner; session_id={sessionId}; transfer_id=(none); trigger=runtime_unlock; rejection_reason=missing_current_filetransfer_route_publication_authority; decision_reason=no_listener_or_path_lease_effect", secondsOffset: 11),
            LogLine($"event=filetransfer_regular_v4_frontier_repair_settled_stalled; session_id={sessionId}; transfer_id={transferId}; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; decision_reason=repair_stalled", secondsOffset: 20),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; route=regular_nkn_v4_fast; protocol_version=4", secondsOffset: 89),
        };

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("runtime_unlock_filetransfer_owner_missing_before_path_lease", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_filetransfer_owner_missing_before_path_lease_count"]);
        Assert.Equal("0", verdict["runtime_unlock_tuna_path_lease_started_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_filetransfer_owner_missing_before_path_lease", stability["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockSourcePublicationMismatchPrecedesHistoricalRepairEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_source_publication_mismatch";
        const string sessionId = "sess_runtime_unlock_source_publication_mismatch";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=runtime_unlock_retry_dispatch_authority_rejected; session_id={sessionId}; transfer_id={transferId}; contract_generation=1; retired_generation=3; offer_generation=3; route=(none); protocol_version=0; live_route_epoch=0; runtime_generation=0; terminal_generation=0; leg_generation=0; decision_reason=retry_dispatch_source_publication_mismatch; attempted_source_publication_route_publication_generation=2; current_source_publication_route_publication_generation=3", secondsOffset: 20),
            LogLine($"event=filetransfer_regular_v4_frontier_repair_settled_stalled; session_id={sessionId}; transfer_id={transferId}; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; decision_reason=repair_stalled", secondsOffset: 25),
            LogLine($"event=session_liveness_timeout; session_id={sessionId}; reason=runtime_unlock_liveness_timeout", secondsOffset: 80),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; route=regular_nkn_v4_fast; protocol_version=4", secondsOffset: 89),
        };

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("runtime_unlock_retry_source_publication_mismatch", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_authority_rejected_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_retry_source_publication_mismatch", stability["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockAuthorityTupleMismatchPrecedesHistoricalRepairEvidence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_authority_tuple_mismatch";
        const string sessionId = "sess_runtime_unlock_authority_tuple_mismatch";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=runtime_unlock_retry_dispatch_decision_tuple_rejected; authority_schema=2; authority_id=fta2-runtime_unlock_retry_dispatch-1234567890abcdef12345678; predecessor_authority_id=(none); direction=outbound; session_id={sessionId}; transfer_id={transferId}; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=1; route_publication_generation=1; route_commit_generation=0; contract_generation=1; retired_generation=1; dispatch_attempt_generation=1; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; release_kind=RegularV4RepairSettlement; release_authority_id=fta2-repair_replay-1234567890abcdef12345678; release_generation=1; release_outcome=MissingDispatch; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; request_regular_v4_liveness_generation=1; request_bridge_decision_generation=0; mismatch_field=bridge_decision_generation; decision_status=Granted; decision_reason=runtime_unlock_retry_dispatch_authority_tuple_mismatch", secondsOffset: 20),
            LogLine($"event=filetransfer_regular_v4_frontier_repair_settled_stalled; session_id={sessionId}; transfer_id={transferId}; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; decision_reason=repair_stalled", secondsOffset: 25),
            LogLine($"event=session_liveness_timeout; session_id={sessionId}; reason=runtime_unlock_liveness_timeout", secondsOffset: 80),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; route=regular_nkn_v4_fast; protocol_version=4", secondsOffset: 89),
        };

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("runtime_unlock_retry_dispatch_authority_tuple_mismatch", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_dispatch_decision_tuple_rejected_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("runtime_unlock_retry_dispatch_authority_tuple_mismatch", stability["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockFileTransferOwnerMissingThenAccepted_ClassifiesNextUnresolvedEdge()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_owner_recovered";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                $"event=runtime_unlock_filetransfer_owner_missing_before_path_lease; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; direction=outbound; trigger=runtime_unlock; rejection_reason=missing_current_filetransfer_route_publication_authority; decision_reason=coordinator_filetransfer_owner_required",
                $"event=runtime_unlock_filetransfer_owner_accepted_before_path_lease; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=4; terminal_generation=0; leg_generation=9; route_publication_generation=3; route_commit_generation=0; target_route=regular_nkn_v4_fast; target_protocol_version=4; target_transport=regular_nkn; trigger=runtime_unlock; decision_reason=current_coordinator_route_publication_authority",
                RuntimeUnlockRetryReleasedLine(transferId),
                RuntimeUnlockRetryDispatchGrantedLine(transferId, queuedBehindActiveNegotiation: false),
                RuntimeUnlockRetryDispatchMissingAfterAuthorityGrantedLine(transferId),
                RuntimeUnlockRetryContractFailedDispatchMissingAfterAuthorityGrantedLine(transferId)),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_dispatch_missing_after_authority_granted", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_filetransfer_owner_missing_before_path_lease_count"]);
        Assert.Equal("1", verdict["runtime_unlock_filetransfer_owner_accepted_before_path_lease_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryBlockedByRegularV4RecoveryWithExplicitNoProofRejection_ClassifiesRejectedBeforeRuntimeUnlock()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_regular_v4_rejected_before_unlock";
        const string sessionId = "sess_runtime_unlock_dispatch_proof";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=tuna_acceleration_activation_offer_not_observed; session_id={sessionId}; transfer_id={transferId}; trigger=runtime_unlock; generation=11; recovery_reason=session_liveness_timeout_pending; retry_reason=runtime_unlock_offer_send_not_observed; retry_after_recovery_armed=1; recovery_requested=1; interruption_reason=offer_interrupted_by_bridge_recovery", secondsOffset: 10),
            LogLine($"event=runtime_unlock_retry_blocked_by_regular_v4_receive_recovery; session_id={sessionId}; transfer_id={transferId}; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; retired_generation=11; contract_generation=7; offer_generation=11; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; regular_v4_liveness_generation=3; bridge_decision_generation=5; blocker_reason=receive_stall_recovery_in_progress; settlement_trigger=recovery_already_settled; decision_reason=awaiting_regular_v4_recovery_settlement", secondsOffset: 20),
            LogLine($"event=runtime_unlock_retry_dispatch_held_by_regular_v4_receive_recovery; session_id={sessionId}; transfer_id={transferId}; trigger=regular_v4_receive_recovery_pending; blocker_reason=receive_stall_recovery_in_progress; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; regular_v4_liveness_generation=3; bridge_decision_generation=5; decision_reason=awaiting_regular_v4_recovery_settlement", secondsOffset: 30),
            LogLine($"event=filetransfer_regular_v4_frontier_repair_settlement_accepted; source=nkn_bridge_pressure; stage=settlement; session_id={sessionId}; transfer_id={transferId}; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=5; route_publication_generation=3; route_commit_generation=0; target_transport=regular_nkn; repair_request_key=regular_v4_peer_silence_safety_replay:3:3:64:1; frontier_chunk_index=3; durable_received_highest_chunk_index=2; requested_chunk_count=64; decision_generation=13; outcome=missing_dispatch; settlement_reason=repair_dispatch_not_observed; pressure_pending_repair_count=64; pressure_reason=regular_v4_peer_silence_safety_replay; cleared_pressure=1; rejection_reason=none; decision_reason=accepted", secondsOffset: 35),
            LogLine($"event=runtime_unlock_retry_rejected_after_regular_v4_receive_recovery; session_id={sessionId}; transfer_id={transferId}; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; retired_generation=11; current_retired_generation=11; contract_generation=7; current_contract_generation=7; offer_generation=11; current_offer_generation=11; route=regular_nkn_v4_fast; current_route=regular_nkn_v4_fast; protocol_version=4; current_protocol_version=4; live_route_epoch=0; current_live_route_epoch=0; regular_v4_liveness_generation=3; current_regular_v4_liveness_generation=3; bridge_decision_generation=5; current_bridge_decision_generation=5; blocker_reason=receive_stall_recovery_in_progress; settlement_trigger=regular_v4_frontier_repair_precontract_settlement; decision_reason=regular_v4_recovery_no_current_settlement_proof", secondsOffset: 36),
            LogLine($"event=session_recovery_contract_failed; session_id={sessionId}; transfer_id={transferId}; contract_generation=7; offer_generation=11; retired_offer_generation=11; kind=runtime_unlock_activation; state=failed; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=0; retry_observed=0; queued_behind_active_negotiation=0; retry_authority_pending=0; retry_authority_granted=0; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=(none); authority_failure_reason=regular_v4_recovery_no_current_settlement_proof", secondsOffset: 37),
            LogLine($"event=regular_v4_receive_recovery_contract_closed_no_deferral; session_id={sessionId}; transfer_id={transferId}; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; regular_v4_liveness_generation=3; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=5; receive_proof_observed=0; recovery_exhausted=0; settlement_trigger=regular_v4_frontier_repair_precontract_settlement; decision_reason=regular_v4_recovery_no_current_settlement_proof", secondsOffset: 38),
            LogLine("event=session_liveness_timeout; session_id=sess_runtime_unlock_dispatch_proof; reason=runtime_unlock_liveness_timeout", secondsOffset: 80),
            LogLine($"event=file_transfer_outbound_terminal; role=Helpee; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; route=regular_nkn_v4_fast; protocol_version=4", secondsOffset: 89),
            LogLine($"event=file_transfer_inbound_terminal; role=Helper; session_id={sessionId}; transfer_id={transferId}; state=Failed; error_code=peer_disconnected; route=regular_nkn_v4_fast; protocol_version=4", secondsOffset: 90),
        };

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("regular_v4_receive_recovery_rejected_before_runtime_unlock", verdict["recovery_failure_class"]);
        Assert.Equal("0", verdict["runtime_unlock_retry_regular_v4_recovery_settlement_missing_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_rejected_after_regular_v4_recovery_count"]);

        var stability = ReadArtifactReport(result.ArtifactDir, "stability-gates-summary.txt");
        Assert.Equal("regular_v4_receive_recovery_rejected_before_runtime_unlock", stability["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockRetryMissingSettlementThenLateRejection_RetainsFirstFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_regular_v4_settlement_rejected";
        var result = await RunAnalyzeFixtureAsync(
            BuildRuntimeUnlockRetryDispatchProofFixture(
                transferId,
                $"event=runtime_unlock_retry_blocked_by_regular_v4_receive_recovery; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; retired_generation=11; contract_generation=7; offer_generation=11; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; regular_v4_liveness_generation=3; bridge_decision_generation=5; blocker_reason=regular_v4_bridge_recovery_started_awaiting_filetransfer_proof; settlement_trigger=recovery_already_settled; decision_reason=awaiting_regular_v4_recovery_settlement",
                $"event=runtime_unlock_retry_regular_v4_recovery_settlement_missing; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; retired_generation=11; contract_generation=7; offer_generation=11; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; regular_v4_liveness_generation=3; bridge_decision_generation=5; current_route=regular_nkn_v4_fast; current_protocol_version=4; current_live_route_epoch=1; current_regular_v4_liveness_generation=3; current_bridge_decision_generation=5; blocker_reason=regular_v4_bridge_recovery_started_awaiting_filetransfer_proof; settlement_trigger=transfer_terminal_peer_disconnected; decision_reason=transfer_terminal_before_recovery_settlement",
                $"event=runtime_unlock_retry_rejected_after_regular_v4_receive_recovery; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; retired_generation=11; current_retired_generation=11; contract_generation=7; current_contract_generation=7; offer_generation=11; current_offer_generation=11; route=regular_nkn_v4_fast; current_route=regular_nkn_v4_fast; protocol_version=4; current_protocol_version=4; live_route_epoch=1; current_live_route_epoch=1; regular_v4_liveness_generation=3; current_regular_v4_liveness_generation=3; bridge_decision_generation=5; current_bridge_decision_generation=5; blocker_reason=regular_v4_bridge_recovery_started_awaiting_filetransfer_proof; settlement_trigger=transfer_terminal_peer_disconnected; decision_reason=transfer_terminal_before_recovery_settlement",
                $"event=session_recovery_contract_failed; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; offer_generation=11; retired_offer_generation=11; kind=runtime_unlock_activation; state=failed; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=0; retry_observed=0; queued_behind_active_negotiation=0; retry_authority_pending=0; retry_authority_granted=0; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=(none); authority_failure_reason=transfer_terminal_before_recovery_settlement"),
            ["-LiveRouteProofMode", "None"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_retry_regular_v4_recovery_settlement_missing", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_regular_v4_recovery_settlement_missing_count"]);
        Assert.Equal("1", verdict["runtime_unlock_retry_rejected_after_regular_v4_recovery_count"]);
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
    public async Task AnalyzeRetained_RuntimeUnlockPreCommitProbeSentThenTimeoutWithoutAck_ClassifiesAckMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_precommit_ack_missing";
        const string sessionId = "sess_runtime_unlock_precommit_ack_missing";
        const string firstProbeId = "runtime-unlock-precommit:1:7:first";
        const string secondProbeId = "runtime-unlock-precommit:2:9:second";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup", secondsOffset: 0),
            LogLine($"event=runtime_unlock_transaction_answer_received; session_id={sessionId}; transaction_generation=1; offer_generation=7; state=answerreceived; peer_visible_proof=1; peer_received=1; answer_received=1; route_commit_pending=0; route_committed=0; tuna_path_lease_generation=3; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; reason=transport_acceleration_answer_received", secondsOffset: 1),
            LogLine($"event=runtime_unlock_precommit_probe_started; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=1; offer_generation=7; tuna_path_lease_generation=3; probe_id={firstProbeId}; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation", secondsOffset: 2),
            LogLine($"event=runtime_unlock_precommit_probe_sent; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=1; offer_generation=7; tuna_path_lease_generation=3; probe_id={firstProbeId}; target_transport=tuna; reason=precommit_probe_sent", secondsOffset: 3),
            LogLine($"event=runtime_unlock_tuna_probe_ack_received; transport=tuna; direction=inbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=1; offer_generation=7; tuna_path_lease_generation=3; probe_id={firstProbeId}; accepted=1; reason=precommit_probe_ack", secondsOffset: 4),
            LogLine($"event=runtime_unlock_precommit_probe_acked; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=1; offer_generation=7; tuna_path_lease_generation=3; probe_id={firstProbeId}; received_transport=tuna", secondsOffset: 5),
            LogLine($"event=runtime_unlock_precommit_probe_paired_commit_confirmed; session_id={sessionId}; transfer_id={transferId}; transaction_generation=1; offer_generation=7; probe_id={firstProbeId}; reason=precommit_probe_ack", secondsOffset: 6),
            LogLine($"event=filetransfer_runtime_unlock_route_commit_accepted; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=1; offer_generation=7; route_commit_generation=1; reason=normal_to_tuna_activation", secondsOffset: 7),
            LogLine($"event=filetransfer_live_route_epoch_recovered; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=file_tuna_v4; protocol_version=4; live_route_epoch=3; handoff_kind=normal_to_tuna_activation; target_transport=tuna; reason=precommit_probe_ack", secondsOffset: 8),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=conservative_nkn_startup; live_route_epoch=4", secondsOffset: 9),
            LogLine($"event=runtime_unlock_transaction_answer_received; session_id={sessionId}; transaction_generation=2; offer_generation=9; state=answerreceived; peer_visible_proof=1; peer_received=1; answer_received=1; route_commit_pending=0; route_committed=0; tuna_path_lease_generation=5; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; reason=transport_acceleration_answer_received", secondsOffset: 10),
            LogLine($"event=filetransfer_runtime_unlock_activation_window_granted; direction=outbound; transfer_id={transferId}; session_id={sessionId}; reason=fallback_tail_quiescent; leg_id=leg:4; leg_generation=4; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=4; transport_epoch=9; bridge_recovery_generation=1; checkpoint_request_id=(none); state=recovery_active; can_send_data=1", secondsOffset: 10),
            LogLine($"event=runtime_unlock_precommit_probe_started; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=2; offer_generation=9; tuna_path_lease_generation=5; probe_id={secondProbeId}; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation", secondsOffset: 11),
            LogLine($"event=runtime_unlock_precommit_probe_sent; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=2; offer_generation=9; tuna_path_lease_generation=5; probe_id={secondProbeId}; target_transport=tuna; reason=precommit_probe_sent", secondsOffset: 12),
            LogLine($"event=runtime_unlock_tuna_probe_sent; transport=tuna; session_id={sessionId}; transfer_id={transferId}; transaction_generation=2; offer_generation=9; tuna_path_lease_generation=5; probe_id={secondProbeId}; target_transport=tuna", secondsOffset: 13),
            LogLine($"event=runtime_unlock_precommit_probe_failed; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=2; offer_generation=9; tuna_path_lease_generation=5; probe_id={secondProbeId}; reason=timeout", secondsOffset: 43),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=268435456; bytes_transferred=134217728; chunks_transferred=6241; chunk_count=12481; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none", secondsOffset: 90)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_precommit_probe_ack_missing", verdict["recovery_failure_class"]);
        Assert.Equal("2", verdict["runtime_unlock_precommit_probe_sent_count"]);
        Assert.Equal("0", verdict["runtime_unlock_precommit_probe_delivery_result_count"]);
        Assert.Equal("1", verdict["runtime_unlock_precommit_probe_ack_missing_count"]);
        Assert.Equal("fail", verdict["runtime_unlock_probe_commit_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockPreCommitAckAuthorityRejected_ClassifiesAckMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_precommit_ack_authority_rejected";
        const string sessionId = "sess_runtime_unlock_precommit_ack_authority_rejected";
        const string probeId = "runtime-unlock-precommit:3:5:ack-authority-rejected";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; selection_reason=regular_nkn_default_v4", secondsOffset: 0),
            LogLine($"event=runtime_unlock_transaction_answer_received; session_id={sessionId}; transaction_generation=3; offer_generation=5; state=answerreceived; peer_visible_proof=1; peer_received=1; answer_received=1; route_commit_pending=0; route_committed=0; tuna_path_lease_generation=7; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; reason=transport_acceleration_answer_received", secondsOffset: 1),
            LogLine($"event=filetransfer_runtime_unlock_admission_granted; direction=inbound; transfer_id={transferId}; session_id={sessionId}; source=precommit_probe_responder; admission_generation=4; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=9; runtime_generation=11; terminal_generation=2; leg_generation=6; reason=current_regular_route", secondsOffset: 2),
            LogLine($"event=runtime_unlock_precommit_probe_started; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation", secondsOffset: 3),
            LogLine($"event=runtime_unlock_precommit_probe_sent; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; target_transport=tuna; reason=runtime_unlock_answer_ack_probe_ready", secondsOffset: 4),
            LogLine($"event=runtime_unlock_tuna_probe_received; transport=tuna; direction=inbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; target_transport=tuna", secondsOffset: 5),
            LogLine($"event=runtime_unlock_precommit_probe_ack_authority_rejected; direction=inbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; attempted_admission_generation=3; current_admission_generation=4; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; decision_reason=runtime_unlock_admission_generation_mismatch", secondsOffset: 6),
            LogLine($"event=filetransfer_runtime_unlock_callback_rejected; direction=inbound; transfer_id={transferId}; session_id={sessionId}; action_kind=RuntimeUnlockProbeAck; source=runtime_unlock_precommit_probe_received; attempted_action=probe_received; rejection_reason=runtime_unlock_admission_generation_mismatch; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; admission_generation=3", secondsOffset: 7),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=268435456; bytes_transferred=134217728; chunks_transferred=6241; chunk_count=12481; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none", secondsOffset: 90)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_precommit_probe_ack_missing", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_precommit_probe_sent_count"]);
        Assert.Equal("1", verdict["runtime_unlock_tuna_probe_received_count"]);
        Assert.Equal("1", verdict["runtime_unlock_precommit_probe_ack_authority_rejected_count"]);
        Assert.Equal("0", verdict["runtime_unlock_tuna_probe_ack_sent_count"]);
        Assert.Equal("0", verdict["runtime_unlock_precommit_probe_ack_delivery_result_count"]);
        Assert.Equal("0", verdict["runtime_unlock_tuna_probe_ack_received_count"]);
        Assert.Equal("1", verdict["runtime_unlock_precommit_probe_ack_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockPreCommitAckSentWithoutDeliveryOrReceive_ClassifiesAckMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_precommit_ack_delivery_missing";
        const string sessionId = "sess_runtime_unlock_precommit_ack_delivery_missing";
        const string probeId = "runtime-unlock-precommit:3:5:ack-delivery-missing";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; selection_reason=regular_nkn_default_v4", secondsOffset: 0),
            LogLine($"event=runtime_unlock_transaction_answer_received; session_id={sessionId}; transaction_generation=3; offer_generation=5; state=answerreceived; peer_visible_proof=1; peer_received=1; answer_received=1; route_commit_pending=0; route_committed=0; tuna_path_lease_generation=7; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; reason=transport_acceleration_answer_received", secondsOffset: 1),
            LogLine($"event=runtime_unlock_precommit_probe_started; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation", secondsOffset: 2),
            LogLine($"event=runtime_unlock_precommit_probe_sent; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; target_transport=tuna; reason=runtime_unlock_answer_ack_probe_ready", secondsOffset: 3),
            LogLine($"event=runtime_unlock_tuna_probe_received; transport=tuna; direction=inbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; target_transport=tuna", secondsOffset: 4),
            LogLine($"event=runtime_unlock_tuna_probe_ack_sent; transport=tuna; session_id={sessionId}; transfer_id={transferId}; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; reason=precommit_probe_received; accepted=1", secondsOffset: 5),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=268435456; bytes_transferred=134217728; chunks_transferred=6241; chunk_count=12481; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none", secondsOffset: 90)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_precommit_probe_ack_missing", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_tuna_probe_ack_sent_count"]);
        Assert.Equal("0", verdict["runtime_unlock_precommit_probe_delivery_result_count"]);
        Assert.Equal("0", verdict["runtime_unlock_precommit_probe_ack_delivery_result_count"]);
        Assert.Equal("0", verdict["runtime_unlock_tuna_probe_ack_received_count"]);
        Assert.Equal("1", verdict["runtime_unlock_precommit_probe_ack_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockPreCommitAckDeliveryWithoutReceiveOrFailure_ClassifiesAckMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_precommit_ack_delivery_before_sent";
        const string sessionId = "sess_runtime_unlock_precommit_ack_delivery_before_sent";
        const string probeId = "runtime-unlock-precommit:3:5:ack-delivery-before-sent";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; selection_reason=regular_nkn_default_v4", secondsOffset: 0),
            LogLine($"event=runtime_unlock_transaction_answer_received; session_id={sessionId}; transaction_generation=3; offer_generation=5; state=answerreceived; peer_visible_proof=1; peer_received=1; answer_received=1; route_commit_pending=0; route_committed=0; tuna_path_lease_generation=7; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; reason=transport_acceleration_answer_received", secondsOffset: 1),
            LogLine($"event=runtime_unlock_precommit_probe_started; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation", secondsOffset: 2),
            LogLine($"event=runtime_unlock_precommit_probe_sent; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; target_transport=tuna; reason=runtime_unlock_answer_ack_probe_ready", secondsOffset: 3),
            LogLine($"event=runtime_unlock_tuna_probe_received; transport=tuna; direction=inbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; target_transport=tuna", secondsOffset: 4),
            LogLine($"event=runtime_unlock_precommit_probe_ack_delivery_result; session_id={sessionId}; transfer_id={transferId}; frame_type=filetransfer.runtime_unlock.precommit_probe_ack; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; ack_authority_source=runtime_unlock_precommit_ack_authority; ack_authority_generation=5; intended_transport=tuna; actual_transport=tuna; lane=bulk; payload_bytes=512; delivery_result=sent; rejection_reason=(none)", secondsOffset: 5),
            LogLine($"event=runtime_unlock_tuna_probe_ack_sent; transport=tuna; session_id={sessionId}; transfer_id={transferId}; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; reason=precommit_probe_received; accepted=1", secondsOffset: 6),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=268435456; bytes_transferred=134217728; chunks_transferred=6241; chunk_count=12481; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none", secondsOffset: 90)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("1", verdict["runtime_unlock_tuna_probe_ack_sent_count"]);
        Assert.Equal("0", verdict["runtime_unlock_precommit_probe_delivery_result_count"]);
        Assert.Equal("1", verdict["runtime_unlock_precommit_probe_ack_delivery_result_count"]);
        Assert.Equal("0", verdict["runtime_unlock_tuna_probe_ack_received_count"]);
        Assert.Equal("1", verdict["runtime_unlock_precommit_probe_ack_missing_count"]);
        Assert.Equal("runtime_unlock_precommit_probe_ack_missing", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockPreCommitAckReceiveResultWithoutCoreObservation_ClassifiesAckMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_precommit_ack_receive_dispatch_missing";
        const string sessionId = "sess_runtime_unlock_precommit_ack_receive_dispatch_missing";
        const string probeId = "runtime-unlock-precommit:3:5:ack-receive-dispatch-missing";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; selection_reason=regular_nkn_default_v4", secondsOffset: 0),
            LogLine($"event=runtime_unlock_transaction_answer_received; session_id={sessionId}; transaction_generation=3; offer_generation=5; state=answerreceived; peer_visible_proof=1; peer_received=1; answer_received=1; route_commit_pending=0; route_committed=0; tuna_path_lease_generation=7; tuna_path_lease_state=listenerready; tuna_path_lease_current=1; reason=transport_acceleration_answer_received", secondsOffset: 1),
            LogLine($"event=filetransfer_runtime_unlock_admission_granted; direction=outbound; transfer_id={transferId}; session_id={sessionId}; source=fallback_quiescent; admission_generation=4; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=2; runtime_generation=3; terminal_generation=1; leg_generation=3; reason=fallback_quiescent", secondsOffset: 2),
            LogLine($"event=runtime_unlock_precommit_probe_started; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation", secondsOffset: 3),
            LogLine($"event=runtime_unlock_precommit_probe_sent; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; target_transport=tuna; reason=runtime_unlock_answer_ack_probe_ready", secondsOffset: 4),
            LogLine($"event=runtime_unlock_tuna_probe_received; transport=tuna; direction=inbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; target_transport=tuna", secondsOffset: 5),
            LogLine($"event=runtime_unlock_precommit_probe_ack_delivery_result; session_id={sessionId}; transfer_id={transferId}; frame_type=filetransfer.runtime_unlock.precommit_probe_ack; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; ack_authority_source=runtime_unlock_precommit_ack_authority; ack_authority_generation=5; intended_transport=tuna; actual_transport=tuna; lane=bulk; payload_bytes=512; delivery_result=sent; rejection_reason=(none)", secondsOffset: 6),
            LogLine($"event=runtime_unlock_tuna_probe_ack_sent; transport=tuna; session_id={sessionId}; transfer_id={transferId}; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; reason=precommit_probe_received; accepted=1", secondsOffset: 7),
            LogLine($"event=filetransfer_data_path_receive_result; transfer_id={transferId}; session_id={sessionId}; frame_type=filetransfer.runtime_unlock.precommit_probe_ack; lane=bulk; actual_transport=tuna; delivery_transport=tuna; chunk_index=(none); payload_bytes=512; raw_payload_bytes=512; transport_payload_bytes=0; batch_chunk_count=0; batch_profile=(none); delivery_result=received; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=2; runtime_generation=3; terminal_generation=1; leg_generation=3; route_publication_generation=11; route_commit_generation=0; target_route=regular_nkn_v4_fast; target_protocol_version=4; target_transport=regular_nkn; intended_target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 8),
            LogLine($"event=runtime_unlock_precommit_probe_ack_receive_queued; transfer_id={transferId}; session_id={sessionId}; frame_type=filetransfer.runtime_unlock.precommit_probe_ack; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; actual_transport=tuna; delivery_transport=tuna; lane=bulk; priority=1; queued_frames=88; queued_bytes=65536; decision_reason=queued_for_core_authority_handler", secondsOffset: 9),
            LogLine($"event=runtime_unlock_precommit_probe_failed; direction=outbound; transfer_id={transferId}; session_id={sessionId}; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; reason=timeout", secondsOffset: 38),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=268435456; bytes_transferred=134217728; chunks_transferred=6241; chunk_count=12481; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none", secondsOffset: 90)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_precommit_probe_ack_missing", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_precommit_probe_ack_delivery_result_count"]);
        Assert.Equal("1", verdict["runtime_unlock_precommit_probe_ack_receive_result_count"]);
        Assert.Equal("1", verdict["runtime_unlock_precommit_probe_ack_receive_queued_count"]);
        Assert.Equal("0", verdict["runtime_unlock_precommit_probe_ack_receive_dispatched_count"]);
        Assert.Equal("0", verdict["runtime_unlock_tuna_probe_ack_received_count"]);
        Assert.Equal("1", verdict["runtime_unlock_precommit_probe_ack_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeUnlockPreCommitAckReceiveDispatchWithoutCoreObservation_ClassifiesAckMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_precommit_ack_dispatch_only";
        const string sessionId = "sess_runtime_unlock_precommit_ack_dispatch_only";
        const string probeId = "runtime-unlock-precommit:3:5:ack-dispatch-only";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id={sessionId}; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; selection_reason=regular_nkn_default_v4", secondsOffset: 0),
            LogLine($"event=filetransfer_data_path_receive_result; transfer_id={transferId}; session_id={sessionId}; frame_type=filetransfer.runtime_unlock.precommit_probe_ack; lane=bulk; actual_transport=tuna; delivery_transport=tuna; chunk_index=(none); payload_bytes=512; raw_payload_bytes=512; transport_payload_bytes=0; batch_chunk_count=0; batch_profile=(none); delivery_result=received; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; direction=outbound; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=2; runtime_generation=3; terminal_generation=1; leg_generation=3; route_publication_generation=11; route_commit_generation=0; target_route=regular_nkn_v4_fast; target_protocol_version=4; target_transport=regular_nkn; intended_target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 1),
            LogLine($"event=runtime_unlock_precommit_probe_ack_receive_dispatched; transfer_id={transferId}; session_id={sessionId}; frame_type=filetransfer.runtime_unlock.precommit_probe_ack; transaction_generation=3; offer_generation=5; tuna_path_lease_generation=7; probe_id={probeId}; actual_transport=tuna; delivery_transport=tuna; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; decision_reason=core_handler_invoked", secondsOffset: 2),
            LogLine($"event=transfer_terminal; direction=outbound; transfer_id={transferId}; session_id={sessionId}; file_name_len=33; file_size_bytes=268435456; bytes_transferred=134217728; chunks_transferred=6241; chunk_count=12481; error_code=peer_disconnected; reason=Peer disconnected.; saved_path=(none); route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none", secondsOffset: 90)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("runtime_unlock_precommit_probe_ack_missing", verdict["recovery_failure_class"]);
        Assert.Equal("1", verdict["runtime_unlock_precommit_probe_ack_receive_result_count"]);
        Assert.Equal("0", verdict["runtime_unlock_precommit_probe_ack_receive_queued_count"]);
        Assert.Equal("1", verdict["runtime_unlock_precommit_probe_ack_receive_dispatched_count"]);
        Assert.Equal("0", verdict["runtime_unlock_tuna_probe_ack_received_count"]);
        Assert.Equal("1", verdict["runtime_unlock_precommit_probe_ack_missing_count"]);
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

    private static string RuntimeUnlockRetryBlockedByRegularV4RecoveryLine(string transferId)
        => $"event=runtime_unlock_retry_blocked_by_regular_v4_receive_recovery; direction=outbound; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; retired_generation=11; contract_generation=7; offer_generation=11; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; regular_v4_liveness_generation=3; bridge_decision_generation=5; blocker_reason=receive_stall_recovery_in_progress; settlement_trigger=dispatch_deferred; decision_reason=awaiting_regular_v4_recovery_settlement";

    private static string RuntimeUnlockRetryHeldByRegularV4RecoveryLine(string transferId)
        => $"event=runtime_unlock_retry_dispatch_held_by_regular_v4_receive_recovery; direction=outbound; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; trigger=regular_v4_receive_recovery_pending; blocker_reason=receive_stall_recovery_in_progress; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; regular_v4_liveness_generation=3; bridge_decision_generation=5; decision_reason=awaiting_regular_v4_recovery_settlement";

    private static string RuntimeUnlockRegularV4RecoverySettlementReportedLine(string transferId)
        => $"event=runtime_unlock_regular_v4_recovery_settlement_reported; authority_schema=2; authority_id=fta2-regular-v4-release-1; successor_authority_id=(none); direction=outbound; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; runtime_generation=4; terminal_generation=0; leg_generation=9; transport_epoch=2; bridge_recovery_generation=3; settlement_bridge_decision_generation=4; route_publication_generation=6; route_commit_generation=0; target_transport=regular_nkn; repair_request_key=regular_v4_peer_silence_safety_replay:3:3:64:1; frontier_chunk_index=3; durable_received_highest_chunk_index=2; requested_chunk_count=64; settlement_decision_generation=3; settlement_outcome=MissingDispatch; regular_v4_liveness_generation=3; bridge_decision_generation=5; disposition=Consumed; settlement_reason=repair_dispatch_not_observed; subscriber_present=1; decision_reason=reported_to_core";

    private static string RuntimeUnlockRegularV4RecoverySettlementDecidedLine(string transferId)
        => $"event=runtime_unlock_regular_v4_recovery_settlement_decided; authority_schema=2; authority_id=fta2-regular-v4-release-1; successor_authority_id=(none); direction=outbound; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; runtime_generation=4; terminal_generation=0; leg_generation=9; transport_epoch=2; bridge_recovery_generation=3; settlement_bridge_decision_generation=4; route_publication_generation=6; route_commit_generation=0; target_transport=regular_nkn; repair_request_key=regular_v4_peer_silence_safety_replay:3:3:64:1; frontier_chunk_index=3; durable_received_highest_chunk_index=2; requested_chunk_count=64; settlement_decision_generation=3; settlement_outcome=MissingDispatch; regular_v4_liveness_generation=3; bridge_decision_generation=5; disposition=Consumed; settlement_reason=repair_dispatch_not_observed; decision_status=Accepted; decision_reason=regular_v4_recovery_settlement_consumed";

    private static string RuntimeUnlockRegularV4RecoveryLifecycleReportedLine(string transferId)
        => $"event=runtime_unlock_regular_v4_recovery_lifecycle_reported; direction=outbound; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; runtime_generation=4; terminal_generation=0; leg_generation=9; transport_epoch=2; bridge_recovery_generation=3; bridge_decision_generation=5; regular_v4_liveness_generation=3; lifecycle_outcome=Completed; lifecycle_reason=bridge_lifecycle_completed; subscriber_present=1; decision_reason=reported_to_core";

    private static string RuntimeUnlockRegularV4RecoveryLifecycleDecidedLine(string transferId)
        => $"event=runtime_unlock_regular_v4_recovery_lifecycle_decided; direction=outbound; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; runtime_generation=4; terminal_generation=0; leg_generation=9; transport_epoch=2; bridge_recovery_generation=3; bridge_decision_generation=5; regular_v4_liveness_generation=3; lifecycle_outcome=Completed; lifecycle_reason=bridge_lifecycle_completed; decision_status=Accepted; decision_reason=regular_v4_recovery_lifecycle_accepted";

    private static string RuntimeUnlockRetryDispatchPendingRequestRecheckedLine(
        string transferId,
        int requestLivenessGeneration,
        int requestBridgeDecisionGeneration)
        => $"event=runtime_unlock_retry_dispatch_pending_request_rechecked; direction=outbound; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; release_authority_id=fta2-regular-v4-release-1; release_generation=3; release_outcome=MissingDispatch; request_regular_v4_liveness_generation={requestLivenessGeneration}; request_bridge_decision_generation={requestBridgeDecisionGeneration}; granted_authority_id=fta2-runtime-unlock-test-1; granted_bridge_decision_generation=5; decision_status=Granted; decision_reason=accepted; trigger=regular_v4_repair_settlement";

    private static string RuntimeUnlockRetryScheduledLine(string transferId)
        => $"event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; direction=outbound; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; retired_generation=11; contract_generation=7; offer_generation=11; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; regular_v4_liveness_generation=3; bridge_decision_generation=5; trigger=regular_v4_receive_proof_observed; queued_behind_active_negotiation=1";

    private static string RuntimeUnlockRetryRouteLessScheduledLine(string transferId)
        => $"event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; direction=outbound; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; retired_generation=11; contract_generation=7; offer_generation=11; route=(none); protocol_version=0; live_route_epoch=0; regular_v4_liveness_generation=0; bridge_decision_generation=0; trigger=regular_v4_receive_proof_observed; queued_behind_active_negotiation=1";

    private static string RuntimeUnlockRetryOfferGenerationLine()
        => "event=runtime_unlock_transaction_offer_generation_created; session_id=sess_runtime_unlock_dispatch_proof; transaction_generation=4; offer_generation=12; state=offerpreparing; peer_visible_proof=0; peer_received=0; answer_received=0; route_commit_pending=0; route_committed=0; failure_reason=(none); retired_reason=(none); reason=runtime_unlock";

    private static string RuntimeUnlockRetryDispatchGrantedLine(
        string transferId,
        bool queuedBehindActiveNegotiation = true)
        => $"event=runtime_unlock_retry_dispatch_authority_granted; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; retired_generation=11; offer_generation=12; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; runtime_generation=4; terminal_generation=0; leg_generation=9; regular_v4_liveness_generation=3; bridge_decision_generation=5; queued_behind_active_negotiation={(queuedBehindActiveNegotiation ? 1 : 0)}; decision_reason=regular_v4_recovery_released";

    private static string RuntimeUnlockRetryDispatchObservedLine(string transferId)
        => $"event=runtime_unlock_retry_dispatch_observed; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; retired_generation=11; offer_generation=12; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; runtime_generation=4; terminal_generation=0; leg_generation=9; regular_v4_liveness_generation=3; bridge_decision_generation=5; queued_behind_active_negotiation=1; decision_reason=retry_observed_send";

    private static string RuntimeUnlockRetrySchema2Line(
        string eventName,
        string transferId,
        int runtimeGeneration = 4,
        string suffix = "",
        string authorityId = "fta2-runtime-unlock-test-1",
        string predecessorAuthorityId = "(none)",
        long retiredGeneration = 11,
        int dispatchAttemptGeneration = 1,
        int missingDispatchSuccessorGeneration = 0,
        int bridgeRecoveryGeneration = 3,
        int bridgeDecisionGeneration = 5,
        string decisionReason = "accepted",
        int authorityGeneration = 1,
        string targetRoute = "file_tuna_v4",
        string targetRouteKind = "FileTunaV4",
        int targetProtocolVersion = 4,
        string targetTransport = "tuna",
        string runtimeOwnerFamily = "test_runtime",
        string runtimeOwnerCreatedReason = "test_coordinator_fixture",
        string sourcePublicationActionKind = "RoutePublication",
        string sourcePublicationReason = "test_regular_route",
        string sourcePublicationTargetRoute = "regular_nkn_v4_fast",
        string sourcePublicationTargetRouteKind = "RegularNknV4Fast",
        int sourcePublicationTargetProtocolVersion = 4,
        string sourcePublicationTargetTransport = "regular_nkn")
        => $"event={eventName}; authority_schema=2; authority_id={authorityId}; authority_generation={authorityGeneration}; predecessor_authority_id={predecessorAuthorityId}; " +
           $"direction=outbound; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; route=regular_nkn_v4_fast; route_kind=RegularNknV4Fast; protocol_version=4; " +
           $"live_route_epoch=1; runtime_generation={runtimeGeneration}; terminal_generation=0; leg_generation=9; transport_epoch=2; bridge_recovery_generation={bridgeRecoveryGeneration}; " +
           $"bridge_decision_generation={bridgeDecisionGeneration}; route_publication_generation=6; route_commit_generation=0; contract_generation=7; retired_generation={retiredGeneration}; " +
           $"dispatch_attempt_generation={dispatchAttemptGeneration}; missing_dispatch_successor_generation={missingDispatchSuccessorGeneration}; missing_dispatch_successor_limit=1; " +
           $"retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; release_kind=RegularV4RepairSettlement; release_authority_id=fta2-regular-v4-release-1; " +
           $"release_generation=3; release_outcome=ProgressObserved; target_route={targetRoute}; target_route_kind={targetRouteKind}; target_protocol_version={targetProtocolVersion}; target_transport={targetTransport}; " +
           $"runtime_owner_session_id=sess_runtime_unlock_dispatch_proof; runtime_owner_transfer_id={transferId}; runtime_owner_direction=outbound; runtime_owner_route=regular_nkn_v4_fast; " +
           $"runtime_owner_route_kind=RegularNknV4Fast; runtime_owner_protocol_version=4; runtime_owner_live_route_epoch=1; runtime_owner_runtime_generation={runtimeGeneration}; runtime_owner_terminal_generation=0; " +
           $"runtime_owner_family={runtimeOwnerFamily}; runtime_owner_created_reason={runtimeOwnerCreatedReason}; source_publication_session_id=sess_runtime_unlock_dispatch_proof; " +
           $"source_publication_transfer_id={transferId}; source_publication_direction=outbound; source_publication_route=regular_nkn_v4_fast; source_publication_route_kind=RegularNknV4Fast; " +
           $"source_publication_protocol_version=4; source_publication_live_route_epoch=1; source_publication_runtime_generation={runtimeGeneration}; source_publication_terminal_generation=0; " +
           $"source_publication_leg_generation=9; source_publication_route_commit_generation=0; source_publication_target_route={sourcePublicationTargetRoute}; source_publication_target_route_kind={sourcePublicationTargetRouteKind}; " +
           $"source_publication_target_protocol_version={sourcePublicationTargetProtocolVersion}; source_publication_target_transport={sourcePublicationTargetTransport}; source_publication_generation=6; " +
           $"source_publication_action_kind={sourcePublicationActionKind}; source_publication_reason={sourcePublicationReason}; source_publication_runtime_owner_session_id=sess_runtime_unlock_dispatch_proof; " +
           $"source_publication_runtime_owner_transfer_id={transferId}; source_publication_runtime_owner_direction=outbound; source_publication_runtime_owner_route=regular_nkn_v4_fast; " +
           $"source_publication_runtime_owner_route_kind=RegularNknV4Fast; source_publication_runtime_owner_protocol_version=4; source_publication_runtime_owner_live_route_epoch=1; " +
           $"source_publication_runtime_owner_runtime_generation={runtimeGeneration}; source_publication_runtime_owner_terminal_generation=0; source_publication_runtime_owner_family={runtimeOwnerFamily}; " +
           $"source_publication_runtime_owner_created_reason={runtimeOwnerCreatedReason}; offer_generation=12; offer_nonce=nonce-1; decision_reason={decisionReason}{suffix}";

    private static string RuntimeUnlockRetrySettlementReportedLine(
        string transferId,
        string outcome,
        string authorityId = "fta2-runtime-unlock-test-1",
        string predecessorAuthorityId = "(none)",
        long retiredGeneration = 11,
        int dispatchAttemptGeneration = 1,
        int missingDispatchSuccessorGeneration = 0,
        int authorityGeneration = 1,
        string targetRoute = "file_tuna_v4",
        int targetProtocolVersion = 4,
        string targetTransport = "tuna",
        string settlementReason = "accepted")
        => RuntimeUnlockRetrySchema2Line(
            "runtime_unlock_retry_dispatch_settlement_reported",
            transferId,
            suffix: $"; settlement_outcome={outcome}; settlement_reason={settlementReason}; subscriber_present=1",
            authorityId: authorityId,
            predecessorAuthorityId: predecessorAuthorityId,
            retiredGeneration: retiredGeneration,
            dispatchAttemptGeneration: dispatchAttemptGeneration,
            missingDispatchSuccessorGeneration: missingDispatchSuccessorGeneration,
            authorityGeneration: authorityGeneration,
            targetRoute: targetRoute,
            targetProtocolVersion: targetProtocolVersion,
            targetTransport: targetTransport);

    private static string RuntimeUnlockRetrySettlementDecidedLine(
        string transferId,
        string outcome,
        string decisionStatus = "Accepted",
        string authorityId = "fta2-runtime-unlock-test-1",
        string predecessorAuthorityId = "(none)",
        long retiredGeneration = 11,
        int dispatchAttemptGeneration = 1,
        int missingDispatchSuccessorGeneration = 0,
        int authorityGeneration = 1,
        string targetRoute = "file_tuna_v4",
        int targetProtocolVersion = 4,
        string targetTransport = "tuna",
        string settlementReason = "accepted")
        => RuntimeUnlockRetrySchema2Line(
            "runtime_unlock_retry_dispatch_settlement_decided",
            transferId,
            suffix: $"; settlement_outcome={outcome}; settlement_reason={settlementReason}; decision_status={decisionStatus}",
            authorityId: authorityId,
            predecessorAuthorityId: predecessorAuthorityId,
            retiredGeneration: retiredGeneration,
            dispatchAttemptGeneration: dispatchAttemptGeneration,
            missingDispatchSuccessorGeneration: missingDispatchSuccessorGeneration,
            decisionReason: decisionStatus == "Accepted" ? "retry_dispatch_settlement_accepted" : "retry_dispatch_settlement_duplicate",
            authorityGeneration: authorityGeneration,
            targetRoute: targetRoute,
            targetProtocolVersion: targetProtocolVersion,
            targetTransport: targetTransport);

    private static string RuntimeUnlockRetryMissingDispatchLimitRejectedLine(string transferId)
        => RuntimeUnlockRetrySchema2Line(
            "runtime_unlock_retry_missing_dispatch_successor_limit_rejected",
            transferId,
            suffix: "; requested_predecessor_authority_id=fta2-runtime-unlock-test-1; requested_retired_generation=12; requested_dispatch_attempt_generation=2; requested_missing_dispatch_successor_generation=1",
            decisionReason: "retry_dispatch_missing_successor_limit_exhausted");

    private static string RuntimeUnlockRetryChainClosedLine(
        string transferId,
        int authorityGeneration = 1,
        string targetRoute = "file_tuna_v4",
        int targetProtocolVersion = 4,
        string targetTransport = "tuna")
        => RuntimeUnlockRetrySchema2Line(
            "runtime_unlock_retry_dispatch_chain_closed",
            transferId,
            suffix: "; settlement_outcome=MissingDispatch; successor_decision_status=Rejected; requested_dispatch_attempt_generation=2; requested_missing_dispatch_successor_generation=1; current_authority_cleared=1; retry_deadline_cleared=1; liveness_deferral_cleared=1",
            decisionReason: "retry_dispatch_missing_successor_limit_exhausted",
            authorityGeneration: authorityGeneration,
            targetRoute: targetRoute,
            targetProtocolVersion: targetProtocolVersion,
            targetTransport: targetTransport);

    private static string RuntimeUnlockRetryDispatchMissingAfterAuthorityGrantedLine(string transferId)
        => $"event=runtime_unlock_retry_dispatch_missing_after_authority_granted; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; retired_generation=11; offer_generation=12; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; runtime_generation=4; terminal_generation=0; leg_generation=9; regular_v4_liveness_generation=3; bridge_decision_generation=5; queued_behind_active_negotiation=0; decision_reason=runtime_unlock_retry_dispatch_missing_after_authority_granted";

    private static string RuntimeUnlockRetryDispatchRejectedLine(string transferId)
        => $"event=runtime_unlock_retry_dispatch_authority_rejected; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; retired_generation=11; offer_generation=12; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; runtime_generation=4; terminal_generation=0; leg_generation=9; regular_v4_liveness_generation=3; bridge_decision_generation=5; queued_behind_active_negotiation=1; decision_reason=terminal_retry_contract";

    private static string RuntimeUnlockRetryDispatchMissingAuthorityRejectedLine(string transferId)
        => $"event=runtime_unlock_retry_dispatch_authority_rejected; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; retired_generation=11; offer_generation=12; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; runtime_generation=4; terminal_generation=0; leg_generation=9; regular_v4_liveness_generation=3; bridge_decision_generation=5; queued_behind_active_negotiation=1; decision_reason=missing_runtime_unlock_retry_authority";

    private static string RuntimeUnlockRetryContractFailedMissingAuthorityLine(string transferId)
        => $"event=session_recovery_contract_failed; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; offer_generation=12; retired_offer_generation=11; kind=runtime_unlock_activation; state=failed; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=0; retry_observed=0; queued_behind_active_negotiation=1; retry_authority_pending=0; retry_authority_granted=0; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=(none); authority_failure_reason=missing_runtime_unlock_retry_authority";

    private static string RuntimeUnlockRetryContractFailedDispatchMissingAfterAuthorityGrantedLine(string transferId)
        => $"event=session_recovery_contract_failed; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; offer_generation=12; retired_offer_generation=11; kind=runtime_unlock_activation; state=failed; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=0; retry_observed=0; queued_behind_active_negotiation=0; retry_authority_pending=0; retry_authority_granted=0; observed_send_pending=0; authority_attempt=1; authorized_observed_lane=(none); authority_failure_reason=runtime_unlock_retry_dispatch_missing_after_authority_granted";

    private static string RuntimeUnlockRetryMissingAuthorityLine(string transferId)
        => $"event=session_recovery_contract_retry_authority_observed; session_id=sess_runtime_unlock_dispatch_proof; transfer_id={transferId}; contract_generation=7; offer_generation=12; retired_offer_generation=11; kind=runtime_unlock_activation; state=retrydispatched; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; recovery_pending=0; recovery_settled=1; retry_required=0; retry_dispatching=0; retry_dispatched=1; retry_observed=0; queued_behind_active_negotiation=1; retry_authority_pending=0; retry_authority_granted=0; observed_send_pending=1; authority_attempt=1; authorized_observed_lane=(none); authority_failure_reason=missing_runtime_unlock_retry_authority";
}
