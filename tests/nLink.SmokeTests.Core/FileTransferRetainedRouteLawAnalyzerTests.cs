using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NLink.App.Views;
using NLink.Core.Configuration;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "RetainedRouteLaw")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferRetainedRouteLawAnalyzerTests : FileTransferOpsScriptsTestBase
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RouteConsistentRegularNknV4_ReturnsPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_regular_v4";
        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareCompletedFixture(
            transferId,
            route: "regular_nkn_v4_fast",
            protocolVersion: 4,
            runtimeProfile: "regular_nkn_v4_fast",
            bridgeRecoveryPolicy: "regular_nkn_v4_fast",
            runtimeEventName: "filetransfer_v4_sender_started"));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal("regular_nkn_v4_fast", route["selected_routes"]);

        var ledger = ReadArtifactReport(result.ArtifactDir, "phase0-invariant-ledger.txt");
        var invariant = ReadArtifactReport(result.ArtifactDir, "filetransfer-invariant-summary.txt");
        Assert.Equal(transferId, ledger["transfer_id"]);
        Assert.Equal("regular_nkn_v4_fast", ledger["final_route"]);
        Assert.Equal("4", ledger["protocol_version"]);
        Assert.Equal("regular_v4", ledger["runtime_family"]);
        Assert.Equal("v4", ledger["frame_family"]);
        Assert.Equal("regular_nkn_v4_fast", ledger["selected_route_changes"]);
        Assert.Equal("regular_v4", ledger["current_runtime_owner"]);
        Assert.Equal("none", ledger["fallback_owner_state"]);
        Assert.Equal("none", ledger["runtime_unlock_transaction_owner_state"]);
        Assert.Equal("completed", ledger["terminal_winner"]);
        Assert.Equal("Completed", ledger["sender_terminal_state"]);
        Assert.Equal("Completed", ledger["receiver_terminal_state"]);
        Assert.Equal("1", ledger["sha_ok"]);
        Assert.Equal("none", ledger["late_frame_classification"]);
        Assert.Equal("0", ledger["stale_route_mismatch_count"]);
        Assert.Equal("pass", invariant["release_route_law_verdict"]);
        Assert.Equal("pass", invariant["terminal_lifecycle_invariant_verdict"]);
        Assert.Equal("pass", invariant["control_priority_invariant_verdict"]);
        Assert.Equal("none", invariant["invariant_failure_class"]);
        Assert.Equal("0", invariant["fallback_consumed"]);
        Assert.Equal("0", invariant["stale_route_worker_activity_count"]);
        Assert.Equal("0", invariant["control_plane_peer_visible_proof_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RouteConsistentFileTunaV4_ReturnPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_file_tuna_v4";
        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareCompletedFixture(
            transferId,
            route: "file_tuna_v4",
            protocolVersion: 4,
            runtimeProfile: "file_tuna_v4_fast",
            bridgeRecoveryPolicy: "tuna_strict",
            runtimeEventName: "filetransfer_v4_sender_started"));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal("file_tuna_v4", route["selected_routes"]);
        var invariant = ReadArtifactReport(result.ArtifactDir, "filetransfer-invariant-summary.txt");
        Assert.Equal("file_tuna_v4", invariant["final_route"]);
        Assert.Equal("4", invariant["protocol_version"]);
        Assert.Equal("file_tuna_v4", invariant["runtime_family"]);
        Assert.Equal("v4", invariant["frame_family"]);
        Assert.Equal("pass", invariant["release_route_law_verdict"]);
    }

    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("post_tuna_fallback_v6", "default_v6", "post_tuna_fallback_strict")]
    [InlineData("diagnostic_regular_nkn_v6", "primary_regular_nkn_bulk_v6", "primary_regular_nkn_quiet")]
    public async Task AnalyzeRetained_RouteConsistentV6Routes_ReturnPass(
        string routeToken,
        string runtimeProfile,
        string bridgeRecoveryPolicy)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = "transfer_route_pass_" + routeToken;
        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareCompletedFixture(
            transferId,
            route: routeToken,
            protocolVersion: 6,
            runtimeProfile: runtimeProfile,
            bridgeRecoveryPolicy: bridgeRecoveryPolicy,
            runtimeEventName: "filetransfer_v6_sender_started"));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal(routeToken, route["selected_routes"]);
        var invariant = ReadArtifactReport(result.ArtifactDir, "filetransfer-invariant-summary.txt");
        Assert.Equal(routeToken, invariant["final_route"]);
        Assert.Equal("6", invariant["protocol_version"]);
        Assert.Equal(routeToken == "post_tuna_fallback_v6" ? "post_tuna_fallback_v6" : "diagnostic_v6", invariant["runtime_family"]);
        Assert.Equal("v6", invariant["frame_family"]);
        Assert.Equal("pass", invariant["release_route_law_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV6Route_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_obsolete_file_tuna_v6";
        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareCompletedFixture(
            transferId,
            route: "file_tuna_v6",
            protocolVersion: 6,
            runtimeProfile: "default_v6",
            bridgeRecoveryPolicy: "tuna_strict",
            runtimeEventName: "filetransfer_v6_sender_started"));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("route_consistency_verdict=fail", routeText, StringComparison.Ordinal);
        Assert.Contains("unknown route selected", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ObsoleteV5Evidence_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_obsolete_v5";
        var lines = BuildRouteAwareCompletedFixture(
                transferId,
                route: "regular_nkn_v4_fast",
                protocolVersion: 4,
                runtimeProfile: "regular_nkn_v4_fast",
                bridgeRecoveryPolicy: "regular_nkn_v4_fast",
                runtimeEventName: "filetransfer_v4_sender_started")
            .Append(LogLine($"event=filetransfer_v5_sender_started; transfer_id={transferId}; session_id=sess_a; protocol_version=5"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("route_consistency_verdict=fail", routeText, StringComparison.Ordinal);
        Assert.Contains("obsolete_protocol_v5", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_DiagnosticRouteWithoutMarker_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_diagnostic_marker_missing";
        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareCompletedFixture(
            transferId,
            route: "diagnostic_regular_nkn_v6",
            protocolVersion: 6,
            runtimeProfile: "primary_regular_nkn_bulk_v6",
            bridgeRecoveryPolicy: "primary_regular_nkn_quiet",
            runtimeEventName: "filetransfer_v6_sender_started",
            diagnosticMarkerOverride: "0"));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("route_consistency_verdict=fail", routeText, StringComparison.Ordinal);
        Assert.Contains("diagnostic route selected without diagnostic marker", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RouteSelectedMissingProtocol_FailsInvariantRouteLaw()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_missing_protocol";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; diagnostic_regular_nkn_v6=0"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; bridge_recovery_policy=regular_nkn_v4_fast"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_a; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("route_runtime", verdict["acceptance_failure_class"]);
        var invariant = ReadArtifactReport(result.ArtifactDir, "filetransfer-invariant-summary.txt");
        Assert.Equal("fail", invariant["release_route_law_verdict"]);
        Assert.Equal("route_runtime", invariant["invariant_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularRouteWithV6Runtime_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_regular_v6_runtime";
        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareCompletedFixture(
            transferId,
            route: "regular_nkn_v4_fast",
            protocolVersion: 4,
            runtimeProfile: "regular_nkn_v4_fast",
            bridgeRecoveryPolicy: "regular_nkn_v4_fast",
            runtimeEventName: "filetransfer_v6_sender_started",
            runtimeProtocolVersion: 6));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("filetransfer-route-consistency-summary.txt", verdict["next_artifact"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["route_consistency_verdict"]);
        Assert.NotEqual("0", route["route_mismatch_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4RouteWithV6Runtime_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_tuna_v4_v6_runtime";
        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareCompletedFixture(
            transferId,
            route: "file_tuna_v4",
            protocolVersion: 4,
            runtimeProfile: "file_tuna_v4_fast",
            bridgeRecoveryPolicy: "tuna_strict",
            runtimeEventName: "filetransfer_v6_sender_started",
            runtimeProtocolVersion: 6));

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["route_consistency_verdict"]);
        var invariant = ReadArtifactReport(result.ArtifactDir, "filetransfer-invariant-summary.txt");
        Assert.Equal("fail", invariant["release_route_law_verdict"]);
        Assert.Equal("route_runtime", invariant["invariant_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RouteBridgePolicyMismatch_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_bridge_mismatch";
        var lines = BuildRouteAwareCompletedFixture(
                transferId,
                route: "post_tuna_fallback_v6",
                protocolVersion: 6,
                runtimeProfile: "default_v6",
                bridgeRecoveryPolicy: "post_tuna_fallback_strict",
                runtimeEventName: "filetransfer_v6_sender_started")
            .Append(LogLine($"event=filetransfer_bridge_recovery_policy_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; bridge_recovery_policy=tuna_strict"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["route_consistency_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ControlledRestartTerminalSeparatedRoutes_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareControlledRestartFixture(includeSetupTerminal: true));

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
    public async Task AnalyzeRetained_LiveTunaLegStartBeforeRouteSelected_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_active; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=0"),
            LogLine($"event=filetransfer_leg_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:2; leg_generation=2; route=file_tuna_v4; protocol_version=4; live_route_epoch=1; transport_epoch=0; state=active; reason=live_route_tuna_activated; start_committed_chunk=32; bridge_recovery_generation=0; can_send_data=1"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_runtime_started; direction=outbound; role=sender; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; bridge_recovery_policy=tuna_strict; live_route_epoch=1"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal("regular_nkn_v4_fast,file_tuna_v4", route["selected_route_changes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LegHistoryWithUnknownRoute_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = BuildRouteAwareControlledRestartFixture(includeSetupTerminal: false)
            .Take(2)
            .Append(LogLine($"event=filetransfer_leg_frozen; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:1; leg_generation=1; route=file_tuna_v6; protocol_version=6; live_route_epoch=0; transport_epoch=0; reason=header_switch_off"))
            .Concat(BuildRouteAwareControlledRestartFixture(includeSetupTerminal: false).Skip(2))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("route_consistency_verdict=fail", routeText, StringComparison.Ordinal);
        Assert.Contains("transfer leg history unknown route", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_CurrentLegStartedRouteMismatch_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1"),
            LogLine($"event=filetransfer_leg_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:2; leg_generation=2; route=file_tuna_v4; protocol_version=4; live_route_epoch=1; transport_epoch=0; state=active; reason=header_switch_off; start_committed_chunk=128; bridge_recovery_generation=0; can_send_data=1"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("route_consistency_verdict=fail", routeText, StringComparison.Ordinal);
        Assert.Contains("route token mismatch", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveFileTunaReenableRouteChangeBeforeTerminal_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareLiveReenableFixture());

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Contains("file_tuna_v4", route["selected_routes"], StringComparison.Ordinal);
        Assert.Contains("post_tuna_fallback_v6", route["selected_routes"], StringComparison.Ordinal);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4", route["selected_route_changes"]);
        Assert.Equal("post_tuna_fallback_v6,file_tuna_v4", route["live_route_epoch_route_changes"]);
        Assert.Equal("0", route["live_route_epoch_metadata_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SwitchOffFallbackSetupBeforeSelection_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=0; route_publication_generation=1; runtime_generation=1; terminal_generation=0; leg_generation=1; route_commit_generation=0; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=0; route_publication_generation=2; runtime_generation=1; terminal_generation=0; leg_generation=1; route_commit_generation=0; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted"),
            LogLine($"event=filetransfer_route_hint_observed_without_publication_authority; transfer_id={transferId}; route=post_tuna_fallback_v6; protocol_version=6; source=availability_broadcast; reason=header_switch_off"),
            LogLine($"event=filetransfer_leg_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:2; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=1; state=checkpoint_pending; reason=header_switch_off; start_committed_chunk=1440; bridge_recovery_generation=1; can_send_data=0"),
            LogLine($"event=filetransfer_clean_fallback_leg_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:2; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=1; state=checkpoint_pending; reason=header_switch_off; start_committed_chunk=1440; bridge_recovery_generation=1; can_send_data=0"),
            LogLine($"event=filetransfer_fallback_leg_recovery_generation_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; reason=header_switch_off; leg_generation=2; recovery_generation=1; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=1; frontier_chunk_index=1440; committed_chunk=1440"),
            LogLine($"event=filetransfer_route_transitioned; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; previous_route=file_tuna_v4; new_route=post_tuna_fallback_v6; previous_protocol_version=4; new_protocol_version=6; previous_runtime_profile=file_tuna_v4_fast; new_runtime_profile=default_v6; previous_frame_family=v4; new_frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; selection_reason=post_tuna_file_fallback_active; reason=header_switch_off; live_route_epoch=1; route_publication_generation=4; runtime_generation=2; terminal_generation=0; leg_generation=2; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1; route_publication_generation=5; runtime_generation=2; terminal_generation=0; leg_generation=2; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted"),
            LogLine($"event=filetransfer_data_path_receive_result; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.chunk_batch.v4; lane=bulk; actual_transport=tuna; delivery_transport=tuna; payload_bytes=64512; raw_payload_bytes=64512; transport_payload_bytes=0; batch_chunk_count=3; batch_profile=(none); delivery_result=received; chunk_index=1866-1868; start_chunk_index=1866; last_end_chunk_exclusive=1869; frame_chunk_count=3; direction=inbound; route=file_tuna_v4; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; route_publication_generation=2; route_commit_generation=0; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; intended_target_transport=tuna; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted"),
            LogLine($"event=filetransfer_tuna_data_path_peer_visible; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.chunk_batch.v4; lane=bulk; actual_transport=tuna; delivery_transport=tuna; payload_bytes=64512; raw_payload_bytes=64512; transport_payload_bytes=0; batch_chunk_count=3; batch_profile=(none); proof_direction=receive; chunk_index=1866-1868; start_chunk_index=1866; last_end_chunk_exclusive=1869; frame_chunk_count=3; direction=inbound; route=file_tuna_v4; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; route_publication_generation=2; route_commit_generation=0; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; intended_target_transport=tuna; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted"),
            LogLine($"event=filetransfer_leg_started; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:2; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=1; state=checkpoint_pending; reason=sidecar_remote_closed; start_committed_chunk=1566; bridge_recovery_generation=1; can_send_data=0"),
            LogLine($"event=filetransfer_clean_fallback_leg_started; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; leg_id=leg:2; leg_generation=2; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=1; state=checkpoint_pending; reason=sidecar_remote_closed; start_committed_chunk=1566; bridge_recovery_generation=1; can_send_data=0"),
            LogLine($"event=filetransfer_fallback_leg_recovery_generation_started; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; reason=sidecar_remote_closed; leg_generation=2; recovery_generation=1; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; transport_epoch=1; frontier_chunk_index=1566; committed_chunk=1566; highest_received_chunk_index=1565; old_frontier_discarded=1"),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1; route_publication_generation=5; runtime_generation=2; terminal_generation=0; leg_generation=2; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted"),
            LogLine($"event=filetransfer_live_route_epoch_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; live_route_epoch=1; previous_route=file_tuna_v4; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=started; reason=header_switch_off; route_publication_generation=7; runtime_generation=2; terminal_generation=0; leg_generation=2; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted"),
            LogLine($"event=runtime_unlock_retry_dispatch_authority_rejected; session_id=sess_redacted; transfer_id={transferId}; contract_generation=1; retired_generation=0; offer_generation=0; route=(none); protocol_version=0; live_route_epoch=0; runtime_generation=0; terminal_generation=0; leg_generation=0; regular_v4_liveness_generation=0; bridge_decision_generation=0; queued_behind_active_negotiation=0; decision_reason=missing_route_publication_authority"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "SwitchOff"]);

        Assert.Equal(0, result.Script.ExitCode);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6", route["selected_route_changes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_InboundRouteEventDoesNotUseOutboundPublication_ReturnsRouteConsistencyFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_direction_window";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=0; route_publication_generation=5; runtime_generation=1; terminal_generation=0; leg_generation=1; route_commit_generation=0; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted"),
            LogLine($"event=filetransfer_data_path_receive_result; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; frame_type=filetransfer.chunk_batch.v4; lane=bulk; actual_transport=tuna; delivery_transport=tuna; payload_bytes=64512; raw_payload_bytes=64512; transport_payload_bytes=0; batch_chunk_count=3; batch_profile=(none); delivery_result=received; chunk_index=12-14; start_chunk_index=12; last_end_chunk_exclusive=15; frame_chunk_count=3; route=file_tuna_v4; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; route_publication_generation=5; route_commit_generation=0; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; intended_target_transport=tuna; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("route_consistency_verdict=fail", routeText, StringComparison.Ordinal);
        Assert.Contains("route-aware event has no selected route", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeEffectBeforeRouteSelection_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "[redacted]";
        var lines = new[]
        {
            LogLine($"event=filetransfer_v6_sender_started; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; live_route_epoch=1; runtime_profile=default_v6; frame_family=v6"),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=post_tuna_fallback_v6; protocol_version=6; runtime_profile=default_v6; frame_family=v6; handoff_kind=tuna_to_normal_fallback; bridge_recovery_policy=post_tuna_fallback_strict; liveness_terminal_policy=post_tuna_fallback_v6_repair; selection_reason=post_tuna_file_fallback_active; file_tuna_active=0; post_tuna_fallback_active=1; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1; route_publication_generation=5; runtime_generation=2; terminal_generation=0; leg_generation=2; route_commit_generation=0; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted"),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1"),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1")
        };

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "SwitchOff"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("route_consistency_verdict=fail", routeText, StringComparison.Ordinal);
        Assert.Contains("route-aware event has no selected route", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveFileTunaMultiCycleRouteChangesBeforeTerminal_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareLiveMultiCycleFixture(), ["-LiveRouteProofMode", "MultiToggle"]);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("MultiToggle", verdict["live_route_epoch_proof_mode"]);
        Assert.Equal("pass", verdict["live_route_epoch_proof_verdict"]);
        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("0", protocol["legacy_data_protocol_started_count"]);
        Assert.Equal("0", protocol["unexpected_legacy_data_frame_during_v4_count"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Contains("file_tuna_v4", route["selected_routes"], StringComparison.Ordinal);
        Assert.Contains("post_tuna_fallback_v6", route["selected_routes"], StringComparison.Ordinal);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["selected_route_changes"]);
        Assert.Equal("post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["live_route_epoch_route_changes"]);
        Assert.Equal("pass", route["live_route_epoch_proof_verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveRegularActivationCycleRouteChangesBeforeTerminal_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareLiveRegularActivationCycleFixture(), ["-LiveRouteProofMode", "RegularActivationCycle"]);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("RegularActivationCycle", verdict["live_route_epoch_proof_mode"]);
        Assert.Equal("pass", verdict["live_route_epoch_proof_verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["selected_route_changes"]);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["live_route_epoch_route_changes"]);
        Assert.Equal("pass", route["live_route_epoch_proof_verdict"]);
        Assert.Equal("8", route["live_route_epoch_event_count"]);
        Assert.Equal("8", route["live_route_epoch_explicit_event_count"]);
        Assert.Equal("0", route["live_route_epoch_metadata_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveRegularActivationCycleEpochStartedBeforeRouteSelected_ReturnsRouteConsistencyPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareLiveRegularActivationCycleFixture().ToList();
        for (var epoch = 4; epoch >= 1; epoch--)
        {
            var selectedIndex = lines.FindIndex(line =>
                line.Contains("event=filetransfer_route_selected", StringComparison.Ordinal) &&
                line.Contains($"live_route_epoch={epoch}", StringComparison.Ordinal));
            var startedIndex = lines.FindIndex(line =>
                line.Contains("event=filetransfer_live_route_epoch_started", StringComparison.Ordinal) &&
                line.Contains($"live_route_epoch={epoch}", StringComparison.Ordinal));

            Assert.True(selectedIndex >= 0);
            Assert.True(startedIndex >= 0);

            var started = lines[startedIndex];
            lines.RemoveAt(startedIndex);
            if (startedIndex < selectedIndex)
            {
                selectedIndex--;
            }

            lines.Insert(selectedIndex, started);
        }

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("pass", verdict["live_route_epoch_proof_verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
        Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["selected_route_changes"]);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["live_route_epoch_route_changes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveRegularActivationCycleLateDuplicateRecovered_DedupesByEpoch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareLiveRegularActivationCycleFixture().ToList();
        var firstFallbackStartedIndex = lines.FindIndex(line =>
            line.Contains("event=filetransfer_live_route_epoch_started", StringComparison.Ordinal) &&
            line.Contains("route=post_tuna_fallback_v6", StringComparison.Ordinal) &&
            line.Contains("live_route_epoch=2", StringComparison.Ordinal));
        Assert.True(firstFallbackStartedIndex >= 0);

        lines.Insert(
            firstFallbackStartedIndex + 1,
            LogLine("event=filetransfer_live_route_epoch_recovered; direction=inbound; transfer_id=[redacted]; session_id=sess_redacted; live_route_epoch=1; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; target_transport=tuna; reason=late_peer_ack"));

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        Assert.Equal(0, result.Script.ExitCode);
        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("pass", verdict["live_route_epoch_proof_verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["selected_route_changes"]);
        Assert.Equal("file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["live_route_epoch_route_changes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveSwitchOffProofModeWithStrictLiveRouteEvents_ReturnsPass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await RunAnalyzeFixtureAsync(BuildRouteAwareLiveSwitchOffFixture(), ["-LiveRouteProofMode", "SwitchOff"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        Assert.Equal("SwitchOff", verdict["live_route_epoch_proof_mode"]);
        Assert.Equal("pass", verdict["live_route_epoch_proof_verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("post_tuna_fallback_v6", route["live_route_epoch_route_changes"]);
        Assert.Equal("0", route["live_route_epoch_metadata_missing_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveSwitchOffMetadataLessNonPayloadReceive_IsDiagnostic()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareLiveSwitchOffFixture()
            .Append(LogLine("event=filetransfer_data_path_receive_result; transfer_id=[redacted]; session_id=sess_redacted; frame_type=filetransfer.complete.v4; lane=bulk; actual_transport=regular_nkn; delivery_transport=regular_nkn; payload_bytes=0; raw_payload_bytes=0; transport_payload_bytes=0; batch_chunk_count=0; batch_profile=(none); delivery_result=received; transaction_generation=0; offer_generation=0; tuna_path_lease_generation=0; probe_id=(none); direction=(none); route=(none); protocol_version=0; live_route_epoch=0; runtime_generation=0; terminal_generation=0; leg_generation=0; route_publication_generation=0; route_commit_generation=0; target_route=(none); target_protocol_version=0; target_transport=unknown; intended_target_transport=unknown; route_publication_action_kind=(none); coordinator_decision_reason=(none)", secondsOffset: 30))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "SwitchOff"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveSwitchOffStaleV4LivenessSummary_IsDiagnostic()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareLiveSwitchOffFixture()
            .Append(LogLine("event=filetransfer_v4_receive_liveness_summary; reason=sample; active_file_transfer_sessions=1; active_file_transfer_runtime_sessions=1; ready_emitted=1; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=38; control_messages_received_since_last=7; bulk_messages_received_since_last=8; total_messages_received_since_last=15; control_last_received_age_ms=786; bulk_last_received_age_ms=236; all_zero_receive_consecutive_windows=0; bulk_zero_receive_consecutive_windows=0; control_zero_receive_consecutive_windows=0; filetransfer_fast_recovery_enabled=1; regular_v4_control_feedback_pressure=1; regular_v4_control_feedback_pressure_remaining_ms=18139; post_tuna_fallback_control_plane_pressure=0; post_tuna_fallback_control_plane_pressure_remaining_ms=0; sample_window_ms=2000; session_id=sess_redacted; transfer_id=[redacted]; direction=inbound; route=file_tuna_v4; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=0; route_publication_generation=3; route_commit_generation=0; target_transport=tuna; repair_request_key=0:12:0:12; frontier_chunk_index=0; durable_received_highest_chunk_index=-1; pending_repair_count=12; decision_generation=3; pressure_reason=regular_v4_frontier_repair; pressure_recovery_attempted=0", secondsOffset: 30))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "SwitchOff"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_MetadataLessPayloadReceive_RemainsRouteFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareLiveSwitchOffFixture()
            .Append(LogLine("event=filetransfer_data_path_receive_result; transfer_id=[redacted]; session_id=sess_redacted; frame_type=filetransfer.chunk_batch.v4; lane=bulk; actual_transport=regular_nkn; delivery_transport=regular_nkn; payload_bytes=65536; raw_payload_bytes=65536; transport_payload_bytes=65536; batch_chunk_count=3; batch_profile=v4_bulk_chunk_batch; delivery_result=received; transaction_generation=0; offer_generation=0; tuna_path_lease_generation=0; probe_id=(none); direction=(none); route=(none); protocol_version=0; live_route_epoch=0; runtime_generation=0; terminal_generation=0; leg_generation=0; route_publication_generation=0; route_commit_generation=0; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; intended_target_transport=tuna; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted", secondsOffset: 30))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "SwitchOff"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["route_consistency_verdict"]);
        Assert.NotEqual("0", route["route_mismatch_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveProofModeRejectsV6TransportOnlyProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareControlledRestartFixture(includeSetupTerminal: false)
            .ToList();
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.Insert(
            firstTerminalIndex,
            LogLine("event=filetransfer_v6_epoch_recovered; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; transport_epoch=2; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=recovered"));

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "SwitchOff"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("SwitchOff", verdict["live_route_epoch_proof_mode"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);
        Assert.Equal("1", verdict["live_route_epoch_transport_only_count"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("(none)", route["live_route_epoch_route_changes"]);
        Assert.Contains("missing live route epoch started proof", File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt")), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularActivationCycleRejectsTransportOnlyV6Proof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareLiveRegularActivationCycleFixture()
            .Where(line => !line.Contains("event=filetransfer_live_route_epoch_started", StringComparison.Ordinal) &&
                           !line.Contains("event=filetransfer_live_route_epoch_recovered", StringComparison.Ordinal))
            .ToList();
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.Insert(
            firstTerminalIndex,
            LogLine("event=filetransfer_v6_epoch_recovered; direction=outbound; transfer_id=[redacted]; session_id=sess_redacted; transport_epoch=4; handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn; state=recovered"));

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("RegularActivationCycle", verdict["live_route_epoch_proof_mode"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);
        Assert.Equal("1", verdict["live_route_epoch_transport_only_count"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("(none)", route["live_route_epoch_route_changes"]);
        Assert.Contains("missing live route epoch started proof", File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt")), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularActivationCycleRejectsRouteSelectedOnlyProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareLiveRegularActivationCycleFixture()
            .Where(line => !line.Contains("event=filetransfer_live_route_epoch_started", StringComparison.Ordinal) &&
                           !line.Contains("event=filetransfer_live_route_epoch_recovered", StringComparison.Ordinal))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);
        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6", route["selected_route_changes"]);
        Assert.Equal("(none)", route["live_route_epoch_route_changes"]);
    }

    [Theory]
    [InlineData("route")]
    [InlineData("protocol_version")]
    [InlineData("handoff_kind")]
    [InlineData("target_transport")]
    [InlineData("live_route_epoch")]
    [InlineData("transfer_id")]
    [InlineData("session_id")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularActivationCycleMissingLiveRouteMetadata_ReturnsProtocolFailure(string fieldName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var removed = false;
        var lines = BuildRouteAwareLiveRegularActivationCycleFixture()
            .Select(line =>
            {
                if (!removed &&
                    line.Contains("event=filetransfer_live_route_epoch_started", StringComparison.Ordinal) &&
                    line.Contains("live_route_epoch=1", StringComparison.Ordinal))
                {
                    removed = true;
                    return RemoveSemicolonLogField(line, fieldName);
                }

                return line;
            })
            .ToArray();
        Assert.True(removed);

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);
        Assert.NotEqual("0", verdict["live_route_epoch_metadata_missing_count"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("live route epoch metadata missing", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularActivationCycleWrongProtocol_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var changed = false;
        var lines = BuildRouteAwareLiveRegularActivationCycleFixture()
            .Select(line =>
            {
                if (!changed &&
                    line.Contains("event=filetransfer_live_route_epoch_started", StringComparison.Ordinal) &&
                    line.Contains("route=file_tuna_v4", StringComparison.Ordinal) &&
                    line.Contains("protocol_version=4", StringComparison.Ordinal))
                {
                    changed = true;
                    return line.Replace("protocol_version=4", "protocol_version=6", StringComparison.Ordinal);
                }

                return line;
            })
            .ToArray();
        Assert.True(changed);

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("missing live route epoch started proof: route=file_tuna_v4", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularActivationCycleRejectsReusedLiveRouteEpoch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareLiveRegularActivationCycleFixture()
            .Select(line => line.Contains("live_route_epoch=3", StringComparison.Ordinal)
                ? line.Replace("live_route_epoch=3", "live_route_epoch=2", StringComparison.Ordinal)
                : line)
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("missing live route epoch started proof: route=file_tuna_v4; mode=RegularActivationCycle; after_epoch=2", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularActivationCycleRejectsStaleSessionProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareLiveRegularActivationCycleFixture()
            .Select(line => line.Contains("event=filetransfer_live_route_epoch_", StringComparison.Ordinal)
                ? line.Replace("session_id=sess_redacted", "session_id=sess_stale", StringComparison.Ordinal)
                : line)
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "RegularActivationCycle"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("live route epoch session scope mismatch", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LiveRouteEpochMissingMetadata_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareLiveSwitchOffFixture()
            .Select(line => line.Contains("event=filetransfer_live_route_epoch_started", StringComparison.Ordinal)
                ? line.Replace("route=post_tuna_fallback_v6; ", "", StringComparison.Ordinal)
                : line)
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines, ["-LiveRouteProofMode", "SwitchOff"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("fail", verdict["live_route_epoch_proof_verdict"]);
        Assert.NotEqual("0", verdict["live_route_epoch_metadata_missing_count"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("live route epoch metadata missing", routeText, StringComparison.Ordinal);
        Assert.Contains("live_route_epoch_metadata_missing_count=", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_UnmarkedRouteChangeBeforeTerminal_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareControlledRestartFixture(includeSetupTerminal: false)
            .Select(line => line.Replace("post_tuna_fallback_active=1", "post_tuna_fallback_active=0", StringComparison.Ordinal))
            .ToArray();
        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var routeText = File.ReadAllText(Path.Combine(result.ArtifactDir, "filetransfer-route-consistency-summary.txt"));
        Assert.Contains("route_consistency_verdict=fail", routeText, StringComparison.Ordinal);
        Assert.Contains("route changed before prior terminal", routeText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_InitialRegularRouteBridgeQueueClearBeforeActivation_ReturnsWarningNotFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_regular_then_tuna_queue_clear";
        var lines = new[]
        {
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn", secondsOffset: 0),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=regular_nkn_default_v4; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn", secondsOffset: 1),
            LogLine("event=nkn_bridge_bulk_queue_state; congested=0; severe=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; in_flight_bytes=0; configured_concurrency=4; effective_concurrency=4; cleared_since_last=4", secondsOffset: 30),
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=17; frames_enqueued=20; payload_bytes_sent=847447; payload_bytes_per_second=423724; payload_bytes_enqueued=848728; payload_bytes_enqueued_per_second=424364; send_failures=0; queue_clears=4; queue_depth=1; queued_bytes=427; oldest_queued_age_ms=299; in_flight=0; configured_concurrency=4; effective_concurrency=4; sample_window_ms=2000", secondsOffset: 45),
            LogLine($"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1", secondsOffset: 60),
            LogLine($"event=filetransfer_route_selected; direction=inbound; transfer_id={transferId}; session_id=sess_redacted; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4_fast; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=tuna_strict; liveness_terminal_policy=file_tuna_v4_fast; selection_reason=file_tuna_active; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=1", secondsOffset: 61),
            LogLine($"event=file_transfer_inbound_terminal; role=helper; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); saved_path=(none); integrity_ok=1", secondsOffset: 180),
            LogLine($"event=file_transfer_outbound_terminal; role=helpee; session_id=sess_redacted; transfer_id={transferId}; state=Completed; error_code=(none); integrity_ok=1", secondsOffset: 180)
        };

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("0", route["route_mismatch_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("WARN_EXTERNAL_TRANSPORT", verdict["verdict"]);
        Assert.Equal("0", verdict["hard_failure_count"]);
        Assert.Equal("recovered_regular_v4_bridge_clear", verdict["warning_kinds"]);
        Assert.Equal("(none)", verdict["warning_cap_exceeded_kinds"]);

        var stabilityText = File.ReadAllText(Path.Combine(result.ArtifactDir, "stability-gates-summary.txt"));
        Assert.Contains("recovered regular NKN V4 bridge queue clear", stabilityText, StringComparison.Ordinal);
        Assert.DoesNotContain("bridge bulk send failure/clear", stabilityText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularRouteBridgeSendFailure_RemainsHardFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildRouteAwareCompletedFixture(
            "transfer_regular_send_failure",
            route: "regular_nkn_v4_fast",
            protocolVersion: 4,
            runtimeProfile: "regular_nkn_v4_fast",
            bridgeRecoveryPolicy: "regular_nkn_v4_fast",
            runtimeEventName: "filetransfer_v4_sender_started").ToList();
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.Insert(
            firstTerminalIndex,
            LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=17; payload_bytes_sent=847447; payload_bytes_per_second=423724; send_failures=1; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=0; configured_concurrency=4; effective_concurrency=4"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Contains("bridge bulk send failure/clear", File.ReadAllText(Path.Combine(result.ArtifactDir, "stability-gates-summary.txt")), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_BridgeConfigSummary_ClassifiesDiagnosticOverrideSeparately()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_bridge_diagnostic_override")
            .Append(LogLine("event=screenshare_bridge_transport_health_summary; selected_rpc=fake; selected_rpc_key=fake; selected_rpc_stage=initial; connect_id=fake; connect_key=fake; ready_emitted=1; client_ready_age_ms=100; disconnect_count_since_last=0; connect_failed_count_since_last=0; ws_error_count_since_last=0; rpc_fallback_attempt_count_since_last=0; control_ready=1; media_ready=1; bulk_ready=1; frames_sent_since_last=0; latest_disconnect_reason=(none); sample_window_ms=2000; control_subclients=4; media_subclients=8; bulk_subclients=8; bulk_send_concurrency=6; bulk_send_mode=round_robin; control_messages_received_since_last=0; media_messages_received_since_last=0; bulk_messages_received_since_last=0; total_messages_received_since_last=0; control_bytes_received_since_last=0; media_bytes_received_since_last=0; bulk_bytes_received_since_last=0; total_bytes_received_since_last=0; control_last_received_age_ms=-1; media_last_received_age_ms=-1; bulk_last_received_age_ms=-1"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: new Dictionary<string, string>
            {
                ["NLINK_FILETRANSFER_EXTERNAL_TOPOLOGY_PROFILE"] = "BulkFanout8",
            });

        Assert.Equal(0, result.Script.ExitCode);
        var bridgeConfig = ReadArtifactReport(result.ArtifactDir, "bridge-config-summary.txt");
        Assert.Equal("diagnostic_override", bridgeConfig["bridge_config_status"]);
        Assert.Equal("1", bridgeConfig["diagnostic_profile"]);
        Assert.Equal("0", bridgeConfig["settings_match_expected"]);
        Assert.Equal("BulkFanout8", bridgeConfig["external_topology_profile"]);
        Assert.Equal("4/8/8", bridgeConfig["observed_topology"]);
        Assert.Equal("6", bridgeConfig["observed_bulk_send_concurrency"]);
        Assert.Equal("round_robin", bridgeConfig["observed_bulk_send_mode"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.NotEqual("INVALID_SETUP", verdict["verdict"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FixedFileOnlyWindowSummary_ReportsDiagnosticMode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_fixed_file_only_window")
            .Append(LogLine("event=filetransfer_v4_grant_window_summary; transfer_id=transfer_fixed_file_only_window; session_id=sess_a; reason=target_changed; file_only_sparse_cadence=1; profile=healthy_expanded; target_window_bytes=16777216; effective_granted_window_bytes=16795648; current_credit_chunks=200; desired_credit_chunks=780; low_watermark_credit_chunks=702; credit_remaining_chunks=200; credit_desired_chunks=780; credit_remaining_bytes=4300800; credit_desired_bytes=16773120; granted_until_chunk_index_exclusive=1780; target_granted_until_chunk_index_exclusive=1780; target_base_chunk_index=1000; target_base_reason=sparse_ahead; grant_base_chunk_index=1000; grant_base_reason=sparse_ahead; sparse_ahead_bytes=4300800; credit_base_chunk_index=1000; credit_base_reason=sparse_base; sparse_credit_mode=Dominant; sparse_credit_hold_active=0; sparse_credit_eligible=1; sparse_credit_advance_bytes=262144; sparse_credit_topup_bytes=131072; sparse_credit_block_reason=(none); limited_recovery_clean_ms=0; limited_recovery_block_reason=(none); fixed_file_only_window_active=1; fixed_file_only_window_bytes=16777216; next_chunk_index=800; highest_received_chunk_index=999; late_arrival_distance=199; pending_chunk_count=0; pending_bytes=0"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("1", decomposition["fixed_file_only_window_active_count"]);
        Assert.Equal("16777216", decomposition["max_fixed_file_only_window_bytes"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PayloadReject_ReturnsProtocolOrIntegrityFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_reject")
            .Append(LogLine("event=filetransfer_transport_payload_rejected; transport=nkn; transfer_id=transfer_reject; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v6; lane=bulk; bridge_command_bytes=70000; max_allowed_bytes=65536"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stability-gates-summary.txt", verdict["next_artifact"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RuntimeOwnerStaleUpdateRejected_IsDiagnostic()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_owner_stale_rejected";
        var lines = BuildRouteAwareCompletedFixture(
                transferId,
                route: "regular_nkn_v4_fast",
                protocolVersion: 4,
                runtimeProfile: "regular_nkn_v4_fast",
                bridgeRecoveryPolicy: "regular_nkn_v4_fast",
                runtimeEventName: "filetransfer_v4_sender_started")
            .Append(LogLine($"event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=v4_peer_silence_safety_replay; attempted_action=defer_liveness; reason=runtime_generation_mismatch; runtime_generation=1; current_runtime_generation=2; terminal_generation=0; current_terminal_generation=0; route=regular_nkn_v4_fast; current_route=file_tuna_v4; protocol_version=4; current_protocol_version=4; live_route_epoch=0; current_live_route_epoch=1; runtime_family=regular_v4; current_runtime_family=file_tuna_v4"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var invariant = ReadArtifactReport(result.ArtifactDir, "filetransfer-invariant-summary.txt");
        Assert.Equal("pass", invariant["runtime_owner_invariant_verdict"]);
        Assert.Equal("2", invariant["runtime_owner_generation"]);
        Assert.Equal("1", invariant["runtime_owner_stale_update_rejected_count"]);
        Assert.Equal("0", invariant["runtime_owner_forbidden_stale_effect_count"]);
        Assert.Equal("none", invariant["invariant_failure_class"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTerminalUnknownTransferDataFrameReject_RemainsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_post_terminal_unknown_reject";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Append(LogLine($"event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=unknown_transfer_id; session_id=sess_a; transfer_id={transferId}; source=nlink-helper-bulk.test; msg_id=late_frame_after_terminal"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stability-gates-summary.txt", verdict["next_artifact"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PostTerminalLateSenderReject_RemainsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_post_terminal_declined_reject";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Append(LogLine($"event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=post_terminal_late_sender_frame_declined; session_id=sess_a; transfer_id={transferId}; source=nlink-helper-bulk.test; msg_id=late_frame_after_decline"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stability-gates-summary.txt", verdict["next_artifact"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PreTerminalUnknownTransferDataFrameReject_RemainsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_pre_terminal_unknown_reject";
        var lines = BuildCleanCompletedV4TransferFixture(transferId).ToList();
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.Insert(
            firstTerminalIndex,
            LogLine($"event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=unknown_transfer_id; session_id=sess_a; transfer_id={transferId}; source=nlink-helper-bulk.test; msg_id=early_frame_before_terminal"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stability-gates-summary.txt", verdict["next_artifact"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SenderCacheExhausted_ReturnsProtocolOrIntegrityFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_sender_cache_exhausted")
            .Append(LogLine("event=filetransfer_sender_cache_exhausted; transfer_id=transfer_sender_cache_exhausted; session_id=sess_a; reason=non_seekable_cache_limit; chunk_index=1700; source_can_seek=0; cache_chunk_count=1700; cache_bytes=69632000; cache_hard_limit_bytes=67108864; remote_next_expected_chunk_index=0; chunks_accepted_for_transport=1700; error_code=sender_cache_exhausted"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["sender_cache_exhausted_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_SenderRepairUnavailable_ReturnsProtocolOrIntegrityFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_sender_repair_unavailable")
            .Append(LogLine("event=filetransfer_sender_repair_unavailable; transfer_id=transfer_sender_repair_unavailable; session_id=sess_a; reason=non_seekable_cache_miss; chunk_index=12; source_can_seek=0; cache_chunk_count=4; cache_bytes=163840; cache_hard_limit_bytes=67108864; remote_next_expected_chunk_index=0; chunks_accepted_for_transport=64; error_code=sender_repair_unavailable"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["sender_repair_unavailable_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ReceiverBufferExhausted_ReturnsProtocolOrIntegrityFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_receiver_exhausted")
            .Append(LogLine("event=filetransfer_receiver_buffer_exhausted; transfer_id=transfer_receiver_exhausted; session_id=sess_a; pending_chunk_count=17000; pending_bytes=69632000; emergency_limit_bytes=67108864; next_chunk_index=0; highest_received_chunk_index=17000; late_arrival_distance=17000; granted_window_bytes=0"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stability-gates-summary.txt", verdict["next_artifact"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_ReceiverFeedbackFailure_ReturnsProtocolOrIntegrityFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_receiver_feedback_failed")
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_failed; transfer_id=transfer_receiver_feedback_failed; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; reason=queue_exhausted; mode=pump; queue_depth=64; error_code=receiver_feedback_queue_exhausted"))
            .Append(LogLine("event=filetransfer_v4_receiver_feedback_summary; transfer_id=transfer_receiver_feedback_failed; session_id=sess_a; mode=pump; queue_depth=64; queue_limit=64; enqueued=64; sent=0; coalesced=0; failed=1; max_queue_depth=64; max_enqueue_to_send_age_ms=0; max_send_duration_ms=0"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stability-gates-summary.txt", verdict["next_artifact"]);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("1", throughput["receiver_feedback_failed_count"]);
        Assert.Equal("64", throughput["max_receiver_feedback_queue_depth"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LegacyNegotiationRejected_IsTrackedInProtocolShape()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_legacy_rejected")
            .Append(LogLine("event=filetransfer_legacy_negotiation_rejected; transfer_id=transfer_legacy_rejected; session_id=sess_a; direction=Inbound; offered_version=2; accepted_version=(none); reason=offer_protocol_not_v4"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["legacy_negotiation_rejected_count"]);
        Assert.Equal("0", protocol["legacy_data_protocol_started_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4Negotiation_IsTrackedInProtocolShape()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedV4TransferFixture("transfer_v4_negotiated");

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["v6_negotiated_count"]);
        Assert.Equal("0", protocol["legacy_data_protocol_started_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4FeedbackBothFailed_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedV4TransferFixture("transfer_v4_feedback_failed")
            .Append(LogLine("event=filetransfer_v4_feedback_both_failed; transfer_id=transfer_v4_feedback_failed; session_id=sess_a; frame_type=filetransfer.receiver_state.v6; reason=both_lanes_failed"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["v4_feedback_both_failed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_OperationCanceledFeedbackWithoutPrimaryQuietPolicy_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_checkpoint_feedback_canceled_strict";
        var lines = BuildCleanCompletedV4TransferFixture(transferId).ToList();
        var firstTerminalIndex = lines.FindIndex(line => line.Contains("event=file_transfer_inbound_terminal", StringComparison.Ordinal));
        Assert.True(firstTerminalIndex > 0);
        lines.InsertRange(
            firstTerminalIndex,
            [
                LogLine($"event=filetransfer_v4_feedback_both_failed; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.frontier_request.v6; first_lane=control; second_lane=bulk; first_error=OperationCanceledException; second_error=OperationCanceledException"),
                LogLine($"event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_send_timeout; direction=outbound; transfer_id={transferId}; session_id=sess_a; request_id=v6-regular-nkn-checkpoint-sync:7; timeout_ms=7500"),
            ]);

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);

        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["v4_feedback_both_failed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4ReceiverFailure_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedV4TransferFixture("transfer_v4_receiver_failed")
            .Append(LogLine("event=filetransfer_v4_receiver_failed; transfer_id=transfer_v4_receiver_failed; session_id=sess_a; error_code=v4_sparse_destination_required; reason=destination_not_seekable"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["v4_receiver_failed_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LegacyDataFrameDuringV4_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedV4TransferFixture("transfer_v4_legacy_frame")
            .Append(LogLine("event=filetransfer_binary_frame_sent; transfer_id=transfer_v4_legacy_frame; session_id=sess_a; frame_type=filetransfer.chunk_batch.legacy; chunk_index=10-11; raw_chunk_bytes=49152; chunk_count=2"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["unexpected_legacy_data_frame_during_v4_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_V4RepairDeliveryModes_AreReported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_v4_repair_delivery_modes";
        var lines = BuildCleanCompletedV4TransferFixture(transferId)
            .Append(LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=4:3:4:12:4:3; range_count=1; requested_chunk_count=3; sent_chunk_count=3; repair_delivery_mode=bulk_only; repair_delivery_escalation_reason=first_send"))
            .Append(LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=4:3:4:12:4:3; range_count=1; requested_chunk_count=3; sent_chunk_count=3; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=frontier_not_advanced"))
            .Append(LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=8:3:8:18:8:3; range_count=1; requested_chunk_count=3; sent_chunk_count=3; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=credit_stall"))
            .Append(LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=12:3:12:24:12:3; range_count=1; requested_chunk_count=3; sent_chunk_count=3; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=retry"))
            .Append(LogLine($"event=filetransfer_v4_repair_sent; transfer_id={transferId}; session_id=sess_a; repair_request_key=16:3:16:30:16:3; range_count=1; requested_chunk_count=3; sent_chunk_count=3; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=primary_regular_nkn_frontier_first_send"))
            .Append(LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_repair_21k; batch_chunk_count=3; chunk_range=4-6; raw_bytes=64512; lane=bulk; repair_delivery_mode=bulk_only; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"))
            .Append(LogLine($"event=filetransfer_chunk_batch_sent_as_batch; transport=nkn; transfer_id={transferId}; session_id=sess_a; frame_type=filetransfer.chunk_batch.v6; batch_profile=v4_repair_21k; batch_chunk_count=3; chunk_range=7-9; raw_bytes=64512; lane=control_bulk; repair_delivery_mode=control_bulk_escalated; raw_to_bridge_payload_ratio=0.975; bridge_payload_fill_percent=96.000"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var repair = ReadArtifactReport(result.ArtifactDir, "repair-reorder-summary.txt");
        Assert.Equal("1", repair["v4_repair_delivery_bulk_only_count"]);
        Assert.Equal("4", repair["v4_repair_delivery_control_bulk_escalated_count"]);
        Assert.Equal("1", repair["v4_repair_delivery_retry_escalated_count"]);
        Assert.Equal("1", repair["v4_repair_delivery_credit_stall_escalated_count"]);
        Assert.Equal("1", repair["v4_repair_delivery_frontier_not_advanced_escalated_count"]);
        Assert.Equal("1", repair["v4_repair_delivery_primary_regular_nkn_frontier_first_send_count"]);
        Assert.Equal("1", repair["v4_repair_batch_bulk_only_count"]);
        Assert.Equal("1", repair["v4_repair_batch_control_bulk_count"]);

        var throughput = ReadArtifactReport(result.ArtifactDir, "throughput-summary.txt");
        Assert.Equal("1", throughput["v4_repair_delivery_bulk_only_count"]);
        Assert.Equal("4", throughput["v4_repair_delivery_control_bulk_escalated_count"]);
        Assert.Equal("1", throughput["v4_repair_delivery_primary_regular_nkn_frontier_first_send_count"]);

        var decomposition = ReadArtifactReport(result.ArtifactDir, "throughput-decomposition-summary.txt");
        Assert.Equal("1", decomposition["v4_repair_delivery_credit_stall_escalated_count"]);
        Assert.Equal("1", decomposition["v4_repair_delivery_primary_regular_nkn_frontier_first_send_count"]);
        Assert.Equal("1", decomposition["v4_repair_batch_control_bulk_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LegacyDataProtocolStarted_ReturnsProtocolFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_legacy_started")
            .Append(LogLine("event=filetransfer_session_opened; direction=inbound; transfer_id=transfer_legacy_started; session_id=sess_a; protocol_version=2; reason=role=Sender"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        var protocol = ReadArtifactReport(result.ArtifactDir, "protocol-shape-summary.txt");
        Assert.Equal("1", protocol["legacy_data_protocol_started_count"]);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_BridgeBulkSendFailure_ReturnsProtocolOrIntegrityFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var lines = BuildCleanCompletedTransferFixture("transfer_bridge_failure")
            .Append(LogLine("event=nkn_bridge_bulk_send_summary; frames_sent=3; send_failures=1; queue_clears=0; queue_depth=0; queued_bytes=0; oldest_queued_age_ms=0; in_flight=2; in_flight_bytes=98304; configured_concurrency=4; effective_concurrency=4; in_flight_max=3; in_flight_bytes_max=147456; worker_utilization_percent=75"))
            .ToArray();

        var result = await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);

        var bridge = ReadArtifactReport(result.ArtifactDir, "bridge-bulk-summary.txt");
        Assert.Equal("1", bridge["bulk_send_failures"]);
        Assert.Equal("3", bridge["max_bulk_in_flight_summary"]);
        Assert.Equal("4", bridge["max_bulk_effective_concurrency"]);
    }
}
