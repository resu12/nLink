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
        Assert.Equal("6", route["fallback_stale_generation_treadmill_score"]);
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
        Assert.Equal("1", route["fallback_tail_quiescent_yield_granted_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("pass", verdict["fallback_tail_reconciliation_verdict"]);
        Assert.Equal("pass", verdict["fallback_tail_quiescence_verdict"]);
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
}
