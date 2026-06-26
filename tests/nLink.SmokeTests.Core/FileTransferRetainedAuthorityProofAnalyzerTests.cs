using System.Collections.Generic;
using System.Linq;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Trait("OpsSuite", "RetainedAuthorityProof")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class FileTransferRetainedAuthorityProofAnalyzerTests : FileTransferOpsScriptsTestBase
{
    [Theory]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    [InlineData("liveness")]
    [InlineData("repair")]
    [InlineData("progress")]
    [InlineData("checkpoint")]
    [InlineData("bridge_recovery")]
    [InlineData("control_plane")]
    [InlineData("route_publication")]
    public async Task AnalyzeRetained_StaleRejectedActionWithoutMatchingEffect_Passes(string family)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = $"transfer_stale_rejected_{family}_pass";
        var lines = BuildAuthorityProofFixture(
            transferId,
            RuntimeOwnerStaleRejectedMessage(family, transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("0", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("0", route[FamilyCounterName(family)]);
        Assert.Equal("0", route["stale_rejected_unknown_action_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
    }

    [Theory]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    [InlineData("liveness")]
    [InlineData("repair")]
    [InlineData("progress")]
    [InlineData("checkpoint")]
    [InlineData("bridge_recovery")]
    [InlineData("control_plane")]
    [InlineData("route_publication")]
    public async Task AnalyzeRetained_StaleRejectedActionFollowedByMatchingEffect_Fails(string family)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = $"transfer_stale_rejected_{family}_fail";
        var lines = BuildAuthorityProofFixture(
            transferId,
            RuntimeOwnerStaleRejectedMessage(family, transferId),
            ForbiddenEffectMessage(family, transferId, runtimeGeneration: 1));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("1", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("1", route[FamilyCounterName(family)]);
        Assert.Equal("0", route["stale_rejected_unknown_action_count"]);

        var ledger = ReadArtifactReport(result.ArtifactDir, "phase0-invariant-ledger.txt");
        Assert.Equal("fail", ledger["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", ledger["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("stale_rejected_action_effect", ledger["invariant_failure_class"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stale_rejected_action_effect", verdict["acceptance_failure_class"]);
        Assert.Equal("stale_rejected_action_effect", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_StaleRejectedActionFollowedByCurrentAuthorityEffect_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_stale_rejected_current_authority_pass";
        var lines = BuildAuthorityProofFixture(
            transferId,
            RuntimeOwnerStaleRejectedMessage("repair", transferId),
            ForbiddenEffectMessage("repair", transferId, runtimeGeneration: 2));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("0", route["stale_rejected_action_forbidden_effect_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_CoordinatorLegMutationRejected_IsIndexedAndDiagnosticWhenNoEffectFollows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_coordinator_leg_mutation_rejected_indexed";
        var lines = BuildAuthorityProofFixture(
            transferId,
            $"event=filetransfer_coordinator_leg_mutation_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; action_kind=FallbackCheckpointAccept; attempted_action=fallback_checkpoint_accept; rejection_reason=leg_generation_mismatch; current_route=regular_nkn_v4_fast; current_protocol_version=4; current_live_route_epoch=0; current_runtime_generation=2; current_terminal_generation=0; current_leg_generation=3; attempted_route=post_tuna_fallback_v6; attempted_protocol_version=6; attempted_live_route_epoch=1; attempted_runtime_generation=1; attempted_leg_generation=2; attempted_transport_epoch=7; attempted_checkpoint_request_id=checkpoint-a; reason=test_fixture");

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["route_consistency_verdict"]);
        Assert.Equal("pass", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("0", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("0", route["stale_rejected_unknown_action_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_UnknownStaleRejectedActionKind_FailsCoverageProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_unknown_stale_rejected_action_kind";
        var lines = BuildAuthorityProofFixture(
            transferId,
            $"event=filetransfer_coordinator_leg_mutation_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; action_kind=FutureAuthorityAction; attempted_action=future_authority_action; rejection_reason=runtime_generation_mismatch; current_route=regular_nkn_v4_fast; current_protocol_version=4; current_live_route_epoch=0; current_runtime_generation=2; current_terminal_generation=0; current_leg_generation=3; attempted_route=regular_nkn_v4_fast; attempted_protocol_version=4; attempted_live_route_epoch=0; attempted_runtime_generation=1; attempted_leg_generation=2; reason=test_fixture");

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("0", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("1", route["stale_rejected_unknown_action_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stale_rejected_action_effect", verdict["acceptance_failure_class"]);
        Assert.Equal("stale_rejected_action_effect", verdict["recovery_failure_class"]);
    }

    private static List<string> BuildAuthorityProofFixture(string transferId, params string[] middleMessages)
    {
        var lines = BuildRouteAwareCompletedFixture(
                transferId,
                route: "regular_nkn_v4_fast",
                protocolVersion: 4,
                runtimeProfile: "regular_nkn_v4_fast",
                bridgeRecoveryPolicy: "regular_nkn_v4_fast",
                runtimeEventName: "filetransfer_v4_sender_started")
            .ToList();

        var insertAt = lines.Count - 2;
        for (var index = 0; index < middleMessages.Length; index++)
        {
            lines.Insert(insertAt + index, LogLine(middleMessages[index], secondsOffset: 10 + index));
        }

        return lines;
    }

    private static string RuntimeOwnerStaleRejectedMessage(string family, string transferId)
        => $"event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=phase1d_fixture; attempted_action={AttemptedAction(family)}; reason=runtime_generation_mismatch; runtime_generation=1; current_runtime_generation=2; terminal_generation=0; current_terminal_generation=0; route=regular_nkn_v4_fast; current_route=regular_nkn_v4_fast; protocol_version=4; current_protocol_version=4; live_route_epoch=0; current_live_route_epoch=0; runtime_family=regular_v4; current_runtime_family=regular_v4";

    private static string AttemptedAction(string family)
        => family switch
        {
            "liveness" => "defer_liveness",
            "repair" => "repair_replay",
            "progress" => "progress_mutation",
            "checkpoint" => "fallback_checkpoint_accept",
            "bridge_recovery" => "bridge_recovery",
            "control_plane" => "control_plane_send",
            "route_publication" => "route_publication",
            _ => family
        };

    private static string ForbiddenEffectMessage(string family, string transferId, int runtimeGeneration)
        => family switch
        {
            "liveness" => $"event=session_liveness_timeout_deferred_for_current_filetransfer_recovery; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation={runtimeGeneration}; reason=test_fixture",
            "repair" => $"event=filetransfer_v4_repair_scheduled; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation={runtimeGeneration}; repair_request_key=repair-a",
            "progress" => $"event=filetransfer_v6_post_tuna_fallback_frontier_progress_state_forced; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation={runtimeGeneration}; previous_committed_chunk_index=1; current_committed_chunk_index=2",
            "checkpoint" => $"event=filetransfer_fallback_checkpoint_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation={runtimeGeneration}; leg_generation=2; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=checkpoint-a; reason=test_fixture",
            "bridge_recovery" => $"event=filetransfer_fallback_leg_authority_bridge_recovery_requested; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation={runtimeGeneration}; leg_generation=2; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=checkpoint-a; authority_reason=test_fixture",
            "control_plane" => $"event=filetransfer_control_plane_delivery_result; kind=fallback_checkpoint_request; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation={runtimeGeneration}; leg_generation=2; bridge_recovery_generation=1; transport_epoch=7; checkpoint_request_id=checkpoint-a; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy=1; bulk_copy=0; peer_visible_any=1; accepted_any=1; local_only_rejected=0",
            "route_publication" => $"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; runtime_profile=regular_nkn_v4_fast; frame_family=v4; handoff_kind=none; bridge_recovery_policy=regular_nkn_v4_fast; liveness_terminal_policy=regular_nkn_v4_fast; selection_reason=stale_worker; file_tuna_active=0; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=nkn; live_route_epoch=0; runtime_generation={runtimeGeneration}",
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown action family.")
        };

    private static string FamilyCounterName(string family)
        => family switch
        {
            "liveness" => "stale_rejected_liveness_effect_count",
            "repair" => "stale_rejected_repair_effect_count",
            "progress" => "stale_rejected_progress_effect_count",
            "checkpoint" => "stale_rejected_checkpoint_effect_count",
            "bridge_recovery" => "stale_rejected_bridge_recovery_effect_count",
            "control_plane" => "stale_rejected_control_plane_effect_count",
            "route_publication" => "stale_rejected_route_publication_effect_count",
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown action family.")
        };
}
