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

    [Theory]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    [InlineData("fallback_send", "ControlPlaneSend", "control_plane")]
    [InlineData("fallback_checkpoint", "FallbackCheckpointAccept", "checkpoint")]
    [InlineData("fallback_reissue", "FallbackCheckpointReissue", "checkpoint")]
    [InlineData("fallback_pressure", "FallbackControlPlanePressure", "control_plane")]
    [InlineData("fallback_tail_yield", "FallbackTailQuiescent", "checkpoint")]
    public async Task AnalyzeRetained_FallbackAuthorityRejectedActionWithoutMatchingEffect_Passes(
        string subfamily,
        string actionKind,
        string family)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = $"transfer_phase3c_{subfamily}_pass";
        var lines = BuildAuthorityProofFixture(
            transferId,
            FallbackAuthorityRejectedMessage(subfamily, actionKind, transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("0", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("0", route[FamilyCounterName(family)]);
        Assert.Equal("0", route[FallbackSubfamilyCounterName(subfamily)]);
        Assert.Equal("0", route["stale_rejected_unknown_action_count"]);
    }

    [Theory]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    [InlineData("fallback_send", "ControlPlaneSend", "control_plane")]
    [InlineData("fallback_checkpoint", "FallbackCheckpointAccept", "checkpoint")]
    [InlineData("fallback_reissue", "FallbackCheckpointReissue", "checkpoint")]
    [InlineData("fallback_pressure", "FallbackControlPlanePressure", "control_plane")]
    [InlineData("fallback_tail_yield", "FallbackTailQuiescent", "checkpoint")]
    public async Task AnalyzeRetained_FallbackAuthorityRejectedActionFollowedByMatchingEffect_Fails(
        string subfamily,
        string actionKind,
        string family)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = $"transfer_phase3c_{subfamily}_fail";
        var lines = BuildAuthorityProofFixture(
            transferId,
            FallbackAuthorityRejectedMessage(subfamily, actionKind, transferId),
            FallbackForbiddenEffectMessage(subfamily, transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("1", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("1", route[FamilyCounterName(family)]);
        Assert.Equal("1", route[FallbackSubfamilyCounterName(subfamily)]);
        Assert.Equal("0", route["stale_rejected_unknown_action_count"]);

        var ledger = ReadArtifactReport(result.ArtifactDir, "phase0-invariant-ledger.txt");
        Assert.Equal("fail", ledger["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", ledger[FallbackSubfamilyCounterName(subfamily)]);
        Assert.Equal("stale_rejected_action_effect", ledger["invariant_failure_class"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stale_rejected_action_effect", verdict["acceptance_failure_class"]);
        Assert.Equal("stale_rejected_action_effect", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_DiagnosticOnlyFallbackEventsAfterRejection_Pass()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_phase3c_diagnostic_only_pass";
        var lines = BuildAuthorityProofFixture(
            transferId,
            FallbackAuthorityRejectedMessage("fallback_tail_yield", "FallbackTailQuiescent", transferId),
            $"event=filetransfer_fallback_tail_yield_proof_stale_ignored; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; leg_generation=2; transport_epoch=7; bridge_recovery_generation=1; checkpoint_request_id=checkpoint-a; reason=stale_owner",
            $"event=filetransfer_control_plane_send_retired_stale_generation; direction=outbound; kind=fallback_checkpoint_request; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; leg_generation=2; current_leg_generation=3; bridge_recovery_generation=1; current_bridge_recovery_generation=2; transport_epoch=7; current_transport_epoch=8; checkpoint_request_id=checkpoint-a; current_checkpoint_request_id=checkpoint-b; reason=stale_leg_generation; frame_type=filetransfer.frontier_request.v6",
            $"event=filetransfer_fallback_checkpoint_reissue_suppressed; direction=outbound; transfer_id={transferId}; session_id=sess_a; reason=current_checkpoint_send_in_flight; source=phase3c_fixture; leg_generation=2; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; transport_epoch=7; checkpoint_request_id=checkpoint-a; proof_transport_epoch=7; proof_checkpoint_request_id=checkpoint-a",
            $"event=filetransfer_post_tuna_fallback_control_plane_pressure_stale_ignored; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; leg_generation=2; transport_epoch=7; checkpoint_request_id=checkpoint-a; reason=stale_pressure",
            $"event=filetransfer_fallback_tail_recovery_owner_retired_after_yield; direction=outbound; transfer_id={transferId}; session_id=sess_a; leg_id=leg:2; leg_generation=2; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; transport_epoch=7; previous_handoff_epoch=0; previous_handoff_state=none; discarded_repair_frame_count=1; discarded_repair_chunk_count=1; reason=fallback_tail_quiescent");

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("0", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("0", route["stale_rejected_fallback_tail_yield_effect_count"]);
        Assert.Equal("0", route["stale_rejected_unknown_action_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackCurrentAuthorityEffectAfterStaleRejection_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_phase3c_current_authority_pass";
        var lines = BuildAuthorityProofFixture(
            transferId,
            FallbackAuthorityRejectedMessage("fallback_reissue", "FallbackCheckpointReissue", transferId),
            FallbackForbiddenEffectMessage("fallback_reissue", transferId, liveRouteEpoch: 2));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("0", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("0", route["stale_rejected_fallback_reissue_effect_count"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LocalAcceptedAnyDeliveryBeforeUnlockAdmissionWithoutPeerVisibleCompletion_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_phase3c_accepted_any_unlock_fail";
        var lines = BuildAuthorityProofFixture(
            transferId,
            FallbackLocalDeliveryMessage(transferId, peerVisible: false),
            RuntimeUnlockAdmissionGrantedMessage(transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("0", route["stale_rejected_action_count"]);
        Assert.Equal("1", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("1", route["stale_rejected_control_plane_effect_count"]);
        Assert.Equal("1", route["stale_rejected_fallback_send_effect_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stale_rejected_action_effect", verdict["acceptance_failure_class"]);
        Assert.Equal("stale_rejected_action_effect", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_LocalAcceptedAnyDeliveryBeforeUnlockAdmissionWithPeerVisibleCompletion_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_phase3c_accepted_any_unlock_pass";
        var lines = BuildAuthorityProofFixture(
            transferId,
            FallbackLocalDeliveryMessage(transferId, peerVisible: false),
            FallbackLocalDeliveryMessage(transferId, peerVisible: true),
            RuntimeUnlockAdmissionGrantedMessage(transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("0", route["stale_rejected_action_count"]);
        Assert.Equal("0", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("0", route["stale_rejected_fallback_send_effect_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RecoveryAuthorityRejectedPeerEpochPromotionFollowedByMatchingRoutePromotion_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_phase4_recovery_peer_epoch_promotion_fail";
        var lines = BuildAuthorityProofFixture(
            transferId,
            RecoveryAuthorityRejectedMessage("PeerEpochPromotion", transferId),
            $"event=filetransfer_v6_peer_epoch_promoted_live_route; direction=inbound; transfer_id={transferId}; session_id=sess_a; transport_epoch=7; route=post_tuna_fallback_v6; protocol_version=6; handoff_kind=regular_nkn_to_regular_nkn; target_transport=regular_nkn; reason=phase4_fixture");

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("1", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("1", route["stale_rejected_route_publication_effect_count"]);
        Assert.Equal("0", route["stale_rejected_unknown_action_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stale_rejected_action_effect", verdict["acceptance_failure_class"]);
        Assert.Equal("stale_rejected_action_effect", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RecoveryAuthorityRejectedPeerEpochPromotionFollowedByCurrentRoutePromotion_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_phase4_recovery_peer_epoch_promotion_current_pass";
        var lines = BuildAuthorityProofFixture(
            transferId,
            RecoveryAuthorityRejectedMessage("PeerEpochPromotion", transferId),
            $"event=filetransfer_v6_peer_epoch_promoted_live_route; direction=inbound; transfer_id={transferId}; session_id=sess_a; transport_epoch=8; route=regular_nkn_v4_fast; protocol_version=4; handoff_kind=regular_nkn_to_regular_nkn; target_transport=regular_nkn; reason=phase4_fixture");

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("0", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("0", route["stale_rejected_route_publication_effect_count"]);
        Assert.Equal("0", route["stale_rejected_unknown_action_count"]);
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

    [Theory]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    [InlineData("RuntimeUnlockOfferCallback", "control_plane")]
    [InlineData("RuntimeUnlockAnswerCallback", "control_plane")]
    [InlineData("RuntimeUnlockProbeStart", "control_plane")]
    [InlineData("RuntimeUnlockProbeAck", "control_plane")]
    [InlineData("RuntimeUnlockTransactionCallback", "control_plane")]
    [InlineData("RouteCommitPublication", "route_publication")]
    public async Task AnalyzeRetained_RuntimeUnlockCallbackRejectedActionWithoutMatchingEffect_Passes(
        string actionKind,
        string family)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = $"transfer_runtime_unlock_callback_{actionKind}_pass";
        var lines = BuildAuthorityProofFixture(
            transferId,
            RuntimeUnlockCallbackRejectedMessage(actionKind, transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("0", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("0", route["stale_rejected_unknown_action_count"]);
        Assert.Equal("0", route[FamilyCounterName(family)]);
    }

    [Theory]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    [InlineData("RuntimeUnlockOfferCallback", "control_plane")]
    [InlineData("RuntimeUnlockAnswerCallback", "control_plane")]
    [InlineData("RuntimeUnlockProbeStart", "control_plane")]
    [InlineData("RuntimeUnlockProbeAck", "control_plane")]
    [InlineData("RuntimeUnlockTransactionCallback", "control_plane")]
    [InlineData("RouteCommitPublication", "route_publication")]
    public async Task AnalyzeRetained_RuntimeUnlockCallbackRejectedActionFollowedByMatchingEffect_Fails(
        string actionKind,
        string family)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = $"transfer_runtime_unlock_callback_{actionKind}_fail";
        var lines = BuildAuthorityProofFixture(
            transferId,
            RuntimeUnlockCallbackRejectedMessage(actionKind, transferId),
            RuntimeUnlockForbiddenEffectMessage(family, transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("1", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("0", route["stale_rejected_unknown_action_count"]);
        Assert.Equal("1", route[FamilyCounterName(family)]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stale_rejected_action_effect", verdict["acceptance_failure_class"]);
        Assert.Equal("stale_rejected_action_effect", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PendingPreCommitProbeRejectedWithoutMatchingEffect_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_pending_precommit_rejected_pass";
        var lines = BuildAuthorityProofFixture(
            transferId,
            PendingPreCommitProbeRejectedMessage(transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("0", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("0", route["stale_rejected_control_plane_effect_count"]);
        Assert.Equal("0", route["stale_rejected_unknown_action_count"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PendingPreCommitProbeRejectedFollowedByMatchingProbeEffect_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_pending_precommit_rejected_fail";
        var lines = BuildAuthorityProofFixture(
            transferId,
            PendingPreCommitProbeRejectedMessage(transferId),
            RuntimeUnlockForbiddenEffectMessage("control_plane", transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("1", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("1", route["stale_rejected_control_plane_effect_count"]);
        Assert.Equal("0", route["stale_rejected_unknown_action_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stale_rejected_action_effect", verdict["acceptance_failure_class"]);
        Assert.Equal("stale_rejected_action_effect", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularV4RecoveryRetryRejectedWithoutMatchingEffect_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_regular_v4_recovery_retry_rejected_pass";
        var lines = BuildAuthorityProofFixture(
            transferId,
            RegularV4RecoveryRetryRejectedMessage(transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("0", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("0", route["stale_rejected_control_plane_effect_count"]);
        Assert.Equal("0", route["stale_rejected_unknown_action_count"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "StaleRejectedActionEffectIsolation")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularV4RecoveryRetryRejectedFollowedByMatchingRetrySchedule_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_regular_v4_recovery_retry_rejected_fail";
        var lines = BuildAuthorityProofFixture(
            transferId,
            RegularV4RecoveryRetryRejectedMessage(transferId),
            RegularV4RecoveryRetryScheduledEffectMessage(transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("1", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("1", route["stale_rejected_control_plane_effect_count"]);
        Assert.Equal("0", route["stale_rejected_unknown_action_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stale_rejected_action_effect", verdict["acceptance_failure_class"]);
        Assert.Equal("stale_rejected_action_effect", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "RuntimeUnlockFallbackUnavailableCoordinatorDenial")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackUnavailableWithPriorCoordinatorDenial_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_fallback_unavailable_with_denial";
        var lines = BuildAuthorityProofFixture(
            transferId,
            $"event=filetransfer_runtime_unlock_admission_denied; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=test; reason=runtime_unlock; admission_generation=0; admission_reason=paired_fallback_tail_yield_missing; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=2; terminal_generation=0; leg_generation=1",
            $"event=runtime_unlock_fallback_unavailable_suppressed; session_id=sess_a; transfer_id={transferId}; reason=runtime_unlock; fallback_owner_reason=paired_fallback_tail_yield_missing; source=test");

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["fallback_runtime_unlock_separation_verdict"]);
        Assert.Equal("0", route["runtime_unlock_fallback_unavailable_without_denial_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "RuntimeUnlockFallbackUnavailableCoordinatorDenial")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FallbackUnavailableWithoutPriorCoordinatorDenial_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_runtime_unlock_fallback_unavailable_without_denial";
        var lines = BuildAuthorityProofFixture(
            transferId,
            $"event=runtime_unlock_fallback_unavailable_suppressed; session_id=sess_a; transfer_id={transferId}; reason=runtime_unlock; fallback_owner_reason=transport_observed_fallback_owner; source=test");

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["fallback_runtime_unlock_separation_verdict"]);
        Assert.Equal("1", route["runtime_unlock_fallback_unavailable_without_denial_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stale_rejected_action_effect", verdict["acceptance_failure_class"]);
        Assert.Equal("stale_rejected_action_effect", verdict["recovery_failure_class"]);
    }

    [Theory]
    [Trait("RetainedAuthorityProofSuite", "PublicationAuthority")]
    [Trait("Category", "Smoke")]
    [InlineData("filetransfer_route_publication_rejected")]
    [InlineData("filetransfer_route_hint_ignored_missing_publication_authority")]
    [InlineData("filetransfer_route_hint_ignored_stale_publication_authority")]
    [InlineData("filetransfer_route_hint_ignored_invalid_publication_authority")]
    public async Task AnalyzeRetained_PublicationRejectedWithoutMatchingEffect_Passes(string rejectionEventName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = $"transfer_phase5d_{rejectionEventName}_pass";
        var lines = BuildAuthorityProofFixture(
            transferId,
            PublicationRejectedMessage(rejectionEventName, transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("0", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("0", route["stale_rejected_route_publication_effect_count"]);
        Assert.Equal("0", route["stale_rejected_unknown_action_count"]);
        Assert.Equal("pass", route["publication_authority_field_coverage_verdict"]);
    }

    [Theory]
    [Trait("RetainedAuthorityProofSuite", "PublicationAuthority")]
    [Trait("Category", "Smoke")]
    [InlineData("filetransfer_route_publication_rejected", "filetransfer_route_selected")]
    [InlineData("filetransfer_route_publication_rejected", "filetransfer_live_route_epoch_started")]
    [InlineData("filetransfer_route_publication_rejected", "filetransfer_live_route_epoch_recovered")]
    [InlineData("filetransfer_route_publication_rejected", "filetransfer_runtime_started")]
    [InlineData("filetransfer_route_publication_rejected", "filetransfer_route_transitioned")]
    [InlineData("filetransfer_route_hint_ignored_stale_publication_authority", "filetransfer_route_hint_authorized_publication_accepted")]
    public async Task AnalyzeRetained_PublicationRejectedFollowedByMatchingEffect_Fails(
        string rejectionEventName,
        string effectEventName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = $"transfer_phase5d_{effectEventName}_fail";
        var lines = BuildAuthorityProofFixture(
            transferId,
            PublicationRejectedMessage(rejectionEventName, transferId),
            PublicationEffectMessage(effectEventName, transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("1", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("1", route["stale_rejected_route_publication_effect_count"]);
        Assert.Equal("0", route["stale_rejected_unknown_action_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("stale_rejected_action_effect", verdict["acceptance_failure_class"]);
        Assert.Equal("stale_rejected_action_effect", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "PublicationAuthority")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RejectedTunaActivePublicationFollowedByMatchingEffect_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_phase5d_tuna_active_publication_fail";
        var lines = BuildAuthorityProofFixture(
            transferId,
            PublicationRejectedMessage("filetransfer_route_publication_rejected", transferId, route: "file_tuna_v4", protocolVersion: 4, targetTransport: "tuna"),
            PublicationEffectMessage("filetransfer_route_selected", transferId, route: "file_tuna_v4", protocolVersion: 4, targetTransport: "tuna"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_route_publication_effect_count"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "PublicationAuthority")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PublicationRejectedFollowedByCurrentAuthorityEffect_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_phase5d_publication_current_authority_pass";
        var lines = BuildAuthorityProofFixture(
            transferId,
            PublicationRejectedMessage("filetransfer_route_publication_rejected", transferId),
            PublicationEffectMessage("filetransfer_route_selected", transferId, runtimeGeneration: 2, legGeneration: 2, routePublicationGeneration: 2));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("0", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("0", route["stale_rejected_route_publication_effect_count"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "PublicationAuthority")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_MissingAuthorityRejectionFollowedByAuthorizedPublicationBeforeRouteEffect_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_phase5d_missing_authority_then_authorized_pass";
        var lines = BuildAuthorityProofFixture(
            transferId,
            PublicationRejectedMessage("filetransfer_route_hint_ignored_missing_publication_authority", transferId),
            PublicationEffectMessage("filetransfer_route_hint_authorized_publication_accepted", transferId),
            PublicationEffectMessage("filetransfer_route_selected", transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("0", route["stale_rejected_action_forbidden_effect_count"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "PublicationAuthority")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_MissingAuthorityRejectionFollowedDirectlyByRouteEffect_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_phase5d_missing_authority_direct_effect_fail";
        var lines = BuildAuthorityProofFixture(
            transferId,
            PublicationRejectedMessage("filetransfer_route_hint_ignored_missing_publication_authority", transferId),
            PublicationEffectMessage("filetransfer_route_selected", transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_route_publication_effect_count"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "PublicationAuthorityFieldCoverage")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_PublicationAuthorityFieldCoverageComplete_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_phase5d_publication_field_coverage_pass";
        var lines = BuildAuthorityProofFixture(
            transferId,
            PublicationEffectMessage("filetransfer_route_selected", transferId),
            PublicationEffectMessage("filetransfer_route_hint_authorized_publication_accepted", transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["publication_authority_field_coverage_verdict"]);
        Assert.Equal("0", route["publication_authority_missing_field_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
    }

    [Theory]
    [Trait("RetainedAuthorityProofSuite", "PublicationAuthorityFieldCoverage")]
    [Trait("Category", "Smoke")]
    [InlineData("route_publication_generation")]
    [InlineData("runtime_generation")]
    [InlineData("terminal_generation")]
    [InlineData("leg_generation")]
    [InlineData("live_route_epoch")]
    [InlineData("route_commit_generation")]
    [InlineData("target_route")]
    [InlineData("target_protocol_version")]
    [InlineData("target_transport")]
    [InlineData("route_publication_action_kind")]
    [InlineData("coordinator_decision_reason")]
    public async Task AnalyzeRetained_PublicationAuthorityFieldCoverageMissingRequiredField_Fails(string omittedFieldName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = $"transfer_phase5d_publication_missing_{omittedFieldName}";
        var lines = BuildAuthorityProofFixture(
            transferId,
            PublicationEffectMissingFieldMessage("filetransfer_route_selected", transferId, omittedFieldName));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["publication_authority_field_coverage_verdict"]);
        Assert.NotEqual("0", route["publication_authority_missing_field_count"]);

        var ledger = ReadArtifactReport(result.ArtifactDir, "phase0-invariant-ledger.txt");
        Assert.Equal("fail", ledger["publication_authority_field_coverage_verdict"]);
        Assert.Equal("publication_authority_missing", ledger["invariant_failure_class"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("publication_authority_missing", verdict["acceptance_failure_class"]);
        Assert.Equal("publication_authority_missing", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "PublicationAuthorityFieldCoverage")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4PublicationMissingCommitTargetFields_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_phase5d_tuna_publication_missing_commit_fields";
        var lines = BuildAuthorityProofFixture(
            transferId,
            PublicationEffectMissingFieldMessage("filetransfer_route_selected", transferId, "route_commit_generation", route: "file_tuna_v4", protocolVersion: 4, targetTransport: "tuna"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["publication_authority_field_coverage_verdict"]);
        Assert.NotEqual("0", route["publication_authority_missing_field_count"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "PublicationAuthorityFieldCoverage")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4NormalPublicationWithZeroCommitGeneration_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_phase5d_tuna_normal_publication_zero_commit_generation";
        var lines = BuildAuthorityProofFixture(
            transferId,
            PublicationEffectMessage(
                "filetransfer_route_selected",
                transferId,
                route: "file_tuna_v4",
                protocolVersion: 4,
                targetTransport: "tuna",
                routeCommitGeneration: 0,
                routePublicationActionKind: "RoutePublication"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["publication_authority_field_coverage_verdict"]);
        Assert.Equal("0", route["publication_authority_missing_field_count"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "PublicationAuthorityFieldCoverage")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RouteCommitPublicationZeroCommitGeneration_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_phase5d_route_commit_publication_zero_commit_generation";
        var lines = BuildAuthorityProofFixture(
            transferId,
            PublicationEffectMessage(
                "filetransfer_route_selected",
                transferId,
                route: "file_tuna_v4",
                protocolVersion: 4,
                targetTransport: "tuna",
                routeCommitGeneration: 0,
                routePublicationActionKind: "RouteCommitPublication"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["publication_authority_field_coverage_verdict"]);
        Assert.NotEqual("0", route["publication_authority_missing_field_count"]);
    }

    [Theory]
    [Trait("RetainedAuthorityProofSuite", "PublicationAuthorityFieldCoverage")]
    [Trait("Category", "Smoke")]
    [InlineData("route")]
    [InlineData("protocol_version")]
    public async Task AnalyzeRetained_RouteTransitionedMissingCanonicalRouteOrProtocol_Fails(string omittedFieldName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = $"transfer_phase5d_route_transitioned_missing_{omittedFieldName}";
        var lines = BuildAuthorityProofFixture(
            transferId,
            PublicationEffectMissingFieldMessage("filetransfer_route_transitioned", transferId, omittedFieldName));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["publication_authority_field_coverage_verdict"]);
        Assert.NotEqual("0", route["publication_authority_missing_field_count"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4DataPathMatchingTunaProof_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_data_path_tuna_pass";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTunaV4DataPathIntentMessage(transferId),
            FileTunaV4DataPathActualMessage(transferId, actualTransport: "tuna", eventName: "filetransfer_tuna_data_path_peer_visible"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("1", route["file_tuna_v4_intended_send_count"]);
        Assert.Equal("1", route["file_tuna_v4_tuna_send_result_count"]);
        Assert.Equal("0", route["file_tuna_v4_regular_nkn_send_result_count"]);
        Assert.Equal("0", route["file_tuna_v4_data_path_mismatch_count"]);
        Assert.Equal("0", route["file_tuna_v4_data_path_missing_actual_proof_count"]);
        Assert.Equal("65536", route["file_tuna_v4_peer_visible_tuna_payload_bytes"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4DataPathRegularNknActualTransport_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_data_path_regular_nkn_fail";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTunaV4DataPathIntentMessage(transferId),
            FileTunaV4DataPathActualMessage(transferId, actualTransport: "regular_nkn"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("1", route["file_tuna_v4_regular_nkn_send_result_count"]);
        Assert.Equal("1", route["file_tuna_v4_data_path_mismatch_count"]);

        var ledger = ReadArtifactReport(result.ArtifactDir, "phase0-invariant-ledger.txt");
        Assert.Equal("fail", ledger["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("file_tuna_data_path_mismatch", ledger["invariant_failure_class"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("file_tuna_data_path_mismatch", verdict["acceptance_failure_class"]);
        Assert.Equal("file_tuna_data_path_mismatch", verdict["recovery_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4DataPathSenderSidePeerVisibleProof_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_data_path_sender_side_peer_visible_fail";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTunaV4DataPathIntentMessage(transferId),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("0", route["file_tuna_v4_data_path_missing_actual_proof_count"]);
        Assert.Equal("65536", route["file_tuna_v4_peer_visible_tuna_payload_bytes"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4ReceiveSideRegularNknObservation_FailsEvenWhenSendProofExists()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_receive_regular_observation_fail";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTunaV4DataPathIntentMessage(transferId),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "regular_nkn",
                eventName: "filetransfer_data_path_receive_result",
                proofDirection: "receive"),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("1", route["file_tuna_v4_regular_nkn_send_result_count"]);
        Assert.Equal("1", route["file_tuna_v4_data_path_mismatch_count"]);
        Assert.Equal("0", route["file_tuna_v4_data_path_missing_actual_proof_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("file_tuna_data_path_mismatch", verdict["acceptance_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4ControlFramesOverRegularNkn_DoNotFailDataPathProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_control_regular_nkn_pass";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTunaV4DataPathIntentMessage(transferId),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "regular_nkn",
                frameType: "filetransfer.manifest.v4",
                payloadBytes: 162,
                rawPayloadBytes: 0,
                batchChunkCount: 0,
                batchProfile: "Current"),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("0", route["file_tuna_v4_regular_nkn_send_result_count"]);
        Assert.Equal("0", route["file_tuna_v4_data_path_mismatch_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4DataPathEarlierReceiveProofDoesNotSatisfyLaterIntent_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_data_path_earlier_receive_proof_fail";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible"),
            FileTunaV4DataPathIntentMessage(transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("1", route["file_tuna_v4_data_path_missing_actual_proof_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("file_tuna_data_path_mismatch", verdict["acceptance_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4DataPathRangeMatchedProofBeforeIntent_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_data_path_prior_range_proof_pass";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                startChunkIndex: 1122,
                batchChunkCount: 3,
                payloadBytes: 64512,
                rawPayloadBytes: 64512,
                batchProfile: "v4_repair_21k"),
            FileTunaV4DataPathIntentMessage(transferId, startChunkIndex: 1122, batchChunkCount: 3, rawBytes: 64512));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("0", route["file_tuna_v4_data_path_missing_actual_proof_count"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4RepairAggregateAndSubBatchesShareConcreteProofSet_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_repair_sub_batch_proof_pass";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                startChunkIndex: 0,
                batchChunkCount: 3,
                payloadBytes: 64512,
                rawPayloadBytes: 64512,
                batchProfile: "v4_repair_21k"),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                startChunkIndex: 3,
                batchChunkCount: 3,
                payloadBytes: 64512,
                rawPayloadBytes: 64512,
                batchProfile: "v4_repair_21k"),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                startChunkIndex: 6,
                batchChunkCount: 3,
                payloadBytes: 64512,
                rawPayloadBytes: 64512,
                batchProfile: "v4_repair_21k"),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                startChunkIndex: 9,
                batchChunkCount: 3,
                payloadBytes: 64512,
                rawPayloadBytes: 64512,
                batchProfile: "v4_repair_21k"),
            FileTunaV4DataPathRepairChunkIntentMessage(transferId, startChunkIndex: 0),
            FileTunaV4DataPathRepairChunkIntentMessage(transferId, startChunkIndex: 3),
            FileTunaV4DataPathRepairChunkIntentMessage(transferId, startChunkIndex: 6),
            FileTunaV4DataPathRepairChunkIntentMessage(transferId, startChunkIndex: 9),
            FileTunaV4DataPathRepairIntentMessage(transferId, sentChunkCount: 12));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("5", route["file_tuna_v4_intended_send_count"]);
        Assert.Equal("4", route["file_tuna_v4_tuna_send_result_count"]);
        Assert.Equal("0", route["file_tuna_v4_data_path_missing_actual_proof_count"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4RepairAggregateDuplicateRangesDoNotSatisfyCoverage_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_repair_duplicate_range_proof_fail";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                startChunkIndex: 0,
                batchChunkCount: 3,
                payloadBytes: 64512,
                rawPayloadBytes: 64512,
                batchProfile: "v4_repair_21k"),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                startChunkIndex: 0,
                batchChunkCount: 3,
                payloadBytes: 64512,
                rawPayloadBytes: 64512,
                batchProfile: "v4_repair_21k"),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                startChunkIndex: 0,
                batchChunkCount: 3,
                payloadBytes: 64512,
                rawPayloadBytes: 64512,
                batchProfile: "v4_repair_21k"),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                startChunkIndex: 0,
                batchChunkCount: 3,
                payloadBytes: 64512,
                rawPayloadBytes: 64512,
                batchProfile: "v4_repair_21k"),
            FileTunaV4DataPathRepairIntentMessage(transferId, sentChunkCount: 12));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("1", route["file_tuna_v4_data_path_missing_actual_proof_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("file_tuna_data_path_mismatch", verdict["acceptance_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4DataPathIntentOnly_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_data_path_intent_only_fail";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTunaV4DataPathIntentMessage(transferId));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("1", route["file_tuna_v4_intended_send_count"]);
        Assert.Equal("0", route["file_tuna_v4_tuna_send_result_count"]);
        Assert.Equal("1", route["file_tuna_v4_data_path_missing_actual_proof_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("file_tuna_data_path_mismatch", verdict["acceptance_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4DataPathSinglePeerVisibleProofCannotSatisfyMultipleIntents_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_data_path_reused_proof_fail";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTunaV4DataPathIntentMessage(transferId, startChunkIndex: 0),
            FileTunaV4DataPathIntentMessage(transferId, startChunkIndex: 2),
            FileTunaV4DataPathActualMessage(transferId, actualTransport: "tuna", eventName: "filetransfer_tuna_data_path_peer_visible"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("2", route["file_tuna_v4_intended_send_count"]);
        Assert.Equal("1", route["file_tuna_v4_tuna_send_result_count"]);
        Assert.Equal("1", route["file_tuna_v4_data_path_missing_actual_proof_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("file_tuna_data_path_mismatch", verdict["acceptance_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4RepairSentRequiresFullPeerVisibleChunkCoverage_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_repair_full_coverage_pass";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTunaV4DataPathRepairIntentMessage(transferId, sentChunkCount: 10),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                payloadBytes: 163840,
                rawPayloadBytes: 163840,
                batchChunkCount: 5),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                payloadBytes: 163840,
                rawPayloadBytes: 163840,
                batchChunkCount: 5));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("0", route["file_tuna_v4_data_path_missing_actual_proof_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4RepairSentPartialPeerVisibleChunkCoverage_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_repair_partial_coverage_fail";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTunaV4DataPathRepairIntentMessage(transferId, sentChunkCount: 10),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                payloadBytes: 32768,
                rawPayloadBytes: 32768,
                batchChunkCount: 1));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("1", route["file_tuna_v4_data_path_missing_actual_proof_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("file_tuna_data_path_mismatch", verdict["acceptance_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4DataPathGenericTunaSendResultWithoutPeerVisibleProof_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_data_path_generic_tuna_result_fail";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTunaV4DataPathIntentMessage(transferId),
            FileTunaV4DataPathActualMessage(transferId, actualTransport: "tuna"));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("1", route["file_tuna_v4_tuna_send_result_count"]);
        Assert.Equal("1", route["file_tuna_v4_data_path_missing_actual_proof_count"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("file_tuna_data_path_mismatch", verdict["acceptance_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_RegularRouteRegularNknDataPath_DoesNotRequireTunaProof()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_regular_route_regular_nkn_data_path_pass";
        var lines = BuildAuthorityProofFixture(
            transferId,
            $"event=filetransfer_v4_chunk_batch_sent; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; route_publication_generation=1; route_commit_generation=0; target_route=regular_nkn_v4_fast; target_protocol_version=4; target_transport=regular_nkn; intended_target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted; start_chunk_index=0; batch_chunk_count=2; raw_bytes=65536; repair_send=0",
            $"event=filetransfer_data_path_send_result; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; route_publication_generation=1; route_commit_generation=0; target_route=regular_nkn_v4_fast; target_protocol_version=4; target_transport=regular_nkn; intended_target_transport=regular_nkn; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted; actual_transport=regular_nkn; delivery_transport=regular_nkn; lane=bulk; sent_via_acceleration=0; force_regular_nkn_bulk=0; payload_bytes=65536; batch_chunk_count=2; batch_profile=v4_batch; delivery_result=accepted");

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("none", route["file_tuna_v4_data_path_proof_verdict"]);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("PASS", verdict["verdict"]);
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

    private static List<string> BuildFileTunaV4DataPathProofFixture(string transferId, params string[] middleMessages)
    {
        var lines = BuildRouteAwareCompletedFixture(
                transferId,
                route: "file_tuna_v4",
                protocolVersion: 4,
                runtimeProfile: "file_tuna_v4_fast",
                bridgeRecoveryPolicy: "tuna_strict",
                runtimeEventName: "filetransfer_v4_sender_started")
            .ToList();

        var insertAt = lines.Count - 2;
        for (var index = 0; index < middleMessages.Length; index++)
        {
            lines.Insert(insertAt + index, LogLine(middleMessages[index], secondsOffset: 10 + index));
        }

        return lines;
    }

    private static string FileTunaV4DataPathAuthoritySuffix(string transferId)
        => $"direction=outbound; transfer_id={transferId}; session_id=sess_a; route=file_tuna_v4; protocol_version=4; live_route_epoch=3; runtime_generation=7; terminal_generation=0; leg_generation=4; route_publication_generation=9; route_commit_generation=0; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; intended_target_transport=tuna; route_publication_action_kind=RoutePublication; coordinator_decision_reason=accepted";

    private static string FileTunaV4DataPathIntentMessage(
        string transferId,
        int startChunkIndex = 0,
        int batchChunkCount = 2,
        int rawBytes = 65536)
        => $"event=filetransfer_v4_chunk_batch_sent; {FileTunaV4DataPathAuthoritySuffix(transferId)}; repair_request_key=none; start_chunk_index={startChunkIndex}; batch_chunk_count={batchChunkCount}; raw_bytes={rawBytes}; repair_send=0; mixed_screenshare=0; screen_share_active=0; screen_share_degraded=0; screen_share_observed=0; repair_delivery_mode=none; repair_delivery_escalation_reason=none; repair_batch_send_attempt=1; repair_batch_send_attempt_count=1";

    private static string FileTunaV4DataPathRepairChunkIntentMessage(string transferId, int startChunkIndex)
        => $"event=filetransfer_v4_chunk_batch_sent; {FileTunaV4DataPathAuthoritySuffix(transferId)}; repair_request_key=repair-1; start_chunk_index={startChunkIndex}; batch_chunk_count=3; raw_bytes=64512; repair_send=1; mixed_screenshare=0; screen_share_active=0; screen_share_degraded=0; screen_share_observed=0; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=credit_stall; repair_batch_send_attempt=1; repair_batch_send_attempt_count=1";

    private static string FileTunaV4DataPathRepairIntentMessage(string transferId, int sentChunkCount)
        => $"event=filetransfer_v4_repair_sent; {FileTunaV4DataPathAuthoritySuffix(transferId)}; repair_request_key=repair-1; protocol_repair_request_id=repair-protocol-1; range_count=1; requested_chunk_count={sentChunkCount}; sent_chunk_count={sentChunkCount}; sent_chunk_indices=0-9; transport_sent_chunk_count={sentChunkCount}; repair_batch_send_attempt_count=1; repair_delivery_mode=bulk_only; repair_delivery_escalation_reason=none; first_start_chunk_index=0; last_end_chunk_exclusive={sentChunkCount}; frontier_tail_repair=0; credit_exhausted_time_ms_at_repair=0; remote_next_expected_chunk_index=0; chunks_accepted_for_transport={sentChunkCount}; skipped_obsolete_count=0; skipped_future_count=0; skipped_out_of_bounds_count=0; receiver_proof_status=pending";

    private static string FileTunaV4DataPathActualMessage(
        string transferId,
        string actualTransport,
        string eventName = "filetransfer_data_path_send_result",
        string? proofDirection = null,
        string frameType = "filetransfer.chunk_batch.v4",
        int payloadBytes = 65536,
        int rawPayloadBytes = 65536,
        int batchChunkCount = 2,
        string batchProfile = "v4_batch",
        int? startChunkIndex = null)
    {
        var rangeSuffix = startChunkIndex is { } start
            ? $"; chunk_index={start}-{start + batchChunkCount - 1}; start_chunk_index={start}; last_end_chunk_exclusive={start + batchChunkCount}; frame_chunk_count={batchChunkCount}"
            : string.Empty;
        return $"event={eventName}; {FileTunaV4DataPathAuthoritySuffix(transferId)}; actual_transport={actualTransport}; delivery_transport={actualTransport}; lane=bulk; sent_via_acceleration={(actualTransport == "tuna" ? 1 : 0)}; force_regular_nkn_bulk=0; payload_bytes={payloadBytes}; raw_payload_bytes={rawPayloadBytes}; batch_count=1; batch_chunk_count={batchChunkCount}; batch_profile={batchProfile}; frame_type={frameType}; delivery_result=accepted; proof_direction={proofDirection ?? (eventName == "filetransfer_tuna_data_path_peer_visible" ? "receive" : "none")}{rangeSuffix}";
    }

    private static string RuntimeOwnerStaleRejectedMessage(string family, string transferId)
        => $"event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=phase1d_fixture; attempted_action={AttemptedAction(family)}; reason=runtime_generation_mismatch; runtime_generation=1; current_runtime_generation=2; terminal_generation=0; current_terminal_generation=0; route=regular_nkn_v4_fast; current_route=regular_nkn_v4_fast; protocol_version=4; current_protocol_version=4; live_route_epoch=0; current_live_route_epoch=0; runtime_family=regular_v4; current_runtime_family=regular_v4";

    private static string RuntimeUnlockCallbackRejectedMessage(string actionKind, string transferId)
        => $"event=filetransfer_runtime_unlock_callback_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; action_kind={actionKind}; source=phase2c_fixture; attempted_action=runtime_unlock_callback; rejection_reason=runtime_generation_mismatch; reason=runtime_unlock_callback; current_route=regular_nkn_v4_fast; current_protocol_version=4; current_live_route_epoch=2; current_runtime_generation=2; current_terminal_generation=0; current_leg_generation=3; attempted_route=file_tuna_v4; attempted_protocol_version=4; attempted_live_route_epoch=1; attempted_runtime_generation=1; attempted_terminal_generation=0; attempted_leg_generation=2; transaction_generation=11; offer_generation=12; tuna_path_lease_generation=13; probe_id=probe-12; same_path_tuna_probe_ack_id=probe-12; route_commit_generation=4; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; admission_generation=5; route_commit_token_generation=4";

    private static string PendingPreCommitProbeRejectedMessage(string transferId)
        => $"event=filetransfer_runtime_unlock_precommit_probe_pending_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=runtime_unlock_precommit_probe_pending; reason=runtime_unlock_pending_precommit_probe_offer_generation_mismatch; rejection_reason=runtime_unlock_pending_precommit_probe_offer_generation_mismatch; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=1; runtime_generation=1; terminal_generation=0; leg_generation=2; admission_generation=5; pending_probe_generation=6; transaction_generation=11; offer_generation=12; tuna_path_lease_generation=13; tuna_path_lease_listener_run_id=listener-start-13; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation";

    private static string RegularV4RecoveryRetryRejectedMessage(string transferId)
        => $"event=runtime_unlock_retry_rejected_after_regular_v4_receive_recovery; direction=outbound; transfer_id={transferId}; session_id=sess_a; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; retired_generation=11; current_retired_generation=11; contract_generation=7; current_contract_generation=7; offer_generation=11; current_offer_generation=11; route=regular_nkn_v4_fast; current_route=file_tuna_v4; protocol_version=4; current_protocol_version=4; live_route_epoch=0; current_live_route_epoch=2; regular_v4_liveness_generation=1; current_regular_v4_liveness_generation=1; bridge_decision_generation=3; current_bridge_decision_generation=3; blocker_reason=regular_v4_bridge_recovery_completed_awaiting_filetransfer_proof; settlement_trigger=regular_v4_recovery_superseded_by_route_hint; decision_reason=regular_v4_route_superseded";

    private static string RegularV4RecoveryRetryScheduledEffectMessage(string transferId)
        => $"event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled; direction=outbound; transfer_id={transferId}; session_id=sess_a; retry_reason=runtime_unlock_offer_send_not_observed; recovery_reason=session_liveness_timeout_pending; retired_generation=11; contract_generation=7; offer_generation=11; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; regular_v4_liveness_generation=1; bridge_decision_generation=3; trigger=stale_regular_v4_recovery_settlement; queued_behind_active_negotiation=0";

    private static string FallbackAuthorityRejectedMessage(string subfamily, string actionKind, string transferId)
        => $"event=filetransfer_coordinator_leg_mutation_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; action_kind={actionKind}; attempted_action={FallbackAttemptedAction(subfamily)}; rejection_reason=runtime_generation_mismatch; current_route=file_tuna_v4; current_protocol_version=4; current_live_route_epoch=2; current_runtime_generation=2; current_terminal_generation=0; current_leg_generation=3; current_transport_epoch=8; current_bridge_recovery_generation=2; current_checkpoint_request_id=checkpoint-b; current_checkpoint_generation=12; current_control_plane_token_generation=5; current_checkpoint_lease_generation=6; current_pressure_generation=7; attempted_route=regular_nkn_v4_fast; attempted_protocol_version=4; attempted_live_route_epoch=0; attempted_runtime_generation=1; attempted_terminal_generation=0; attempted_leg_generation=2; attempted_transport_epoch=7; attempted_bridge_recovery_generation=1; attempted_checkpoint_request_id=checkpoint-a; attempted_checkpoint_generation=11; attempted_control_plane_token_generation=4; attempted_checkpoint_lease_generation=5; attempted_pressure_generation=6; reason=phase3c_fixture";

    private static string RecoveryAuthorityRejectedMessage(string actionKind, string transferId)
        => $"event=filetransfer_recovery_authority_rejected; direction=inbound; transfer_id={transferId}; session_id=sess_a; action_kind={actionKind}; source=phase4_fixture; attempted_action=peer_epoch_promotion; rejection_reason=runtime_generation_mismatch; reason=runtime_generation_mismatch; current_route=regular_nkn_v4_fast; current_protocol_version=4; current_live_route_epoch=2; current_runtime_generation=2; current_terminal_generation=0; attempted_route=post_tuna_fallback_v6; attempted_protocol_version=6; attempted_live_route_epoch=1; attempted_runtime_generation=1; attempted_terminal_generation=0; attempted_leg_generation=2; attempted_transport_epoch=7; attempted_bridge_recovery_generation=1; recovery_decision_generation=3; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn";

    private static string PublicationRejectedMessage(
        string eventName,
        string transferId,
        string route = "regular_nkn_v4_fast",
        int protocolVersion = 4,
        string targetTransport = "regular_nkn")
    {
        if (eventName == "filetransfer_route_hint_ignored_missing_publication_authority")
        {
            return $"event={eventName}; direction=outbound; transfer_id={transferId}; session_id=sess_a; route={route}; protocol_version={protocolVersion}; source=phase5d_fixture; reason=missing_publication_authority";
        }

        return $"event={eventName}; direction=outbound; transfer_id={transferId}; session_id=sess_a; action_kind=RoutePublication; source=phase5d_fixture; attempted_action=route_publication; rejection_reason=route_publication_generation_mismatch; reason=phase5d_fixture; current_route={route}; current_protocol_version={protocolVersion}; current_live_route_epoch=0; current_runtime_generation=2; current_terminal_generation=0; current_leg_generation=2; current_route_publication_generation=2; current_route_commit_generation=0; current_target_route={route}; current_target_protocol_version={protocolVersion}; current_target_transport={targetTransport}; attempted_route={route}; attempted_protocol_version={protocolVersion}; attempted_live_route_epoch=0; attempted_runtime_generation=1; attempted_terminal_generation=0; attempted_leg_generation=1; attempted_route_publication_generation=1; attempted_route_commit_generation=0; attempted_target_route={route}; attempted_target_protocol_version={protocolVersion}; attempted_target_transport={targetTransport}; route={route}; protocol_version={protocolVersion}; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; route_publication_generation=1; route_commit_generation=0; target_route={route}; target_protocol_version={protocolVersion}; target_transport={targetTransport}; route_publication_action_kind=RoutePublication";
    }

    private static string PublicationEffectMessage(
        string eventName,
        string transferId,
        string route = "regular_nkn_v4_fast",
        int protocolVersion = 4,
        int liveRouteEpoch = 0,
        int runtimeGeneration = 1,
        int terminalGeneration = 0,
        int legGeneration = 1,
        int routePublicationGeneration = 1,
        string targetTransport = "regular_nkn",
        int routeCommitGeneration = 0,
        string routePublicationActionKind = "RoutePublication")
    {
        var actualEventName = eventName;
        var coordinatorReason = "phase5d_fixture";
        var effectiveTargetTransport = targetTransport == "regular_nkn" && route == "file_tuna_v4"
            ? "tuna"
            : targetTransport;
        var common = $"event={actualEventName}; direction=outbound; transfer_id={transferId}; session_id=sess_a; route={route}; protocol_version={protocolVersion}; live_route_epoch={liveRouteEpoch}; runtime_generation={runtimeGeneration}; terminal_generation={terminalGeneration}; leg_generation={legGeneration}; route_publication_generation={routePublicationGeneration}; route_commit_generation={routeCommitGeneration}; target_route={route}; target_protocol_version={protocolVersion}; target_transport={effectiveTargetTransport}; route_publication_action_kind={routePublicationActionKind}; coordinator_decision_reason={coordinatorReason}";
        return actualEventName switch
        {
            "filetransfer_route_selected" => $"{common}; runtime_profile={route}; frame_family=v{protocolVersion}; handoff_kind={(route == "file_tuna_v4" ? "normal_to_tuna_activation" : "none")}; bridge_recovery_policy={route}; liveness_terminal_policy={route}; selection_reason={(route == "file_tuna_v4" ? "file_tuna_active" : "phase5d_fixture")}; file_tuna_active={(route == "file_tuna_v4" ? 1 : 0)}; post_tuna_fallback_active={(route == "post_tuna_fallback_v6" ? 1 : 0)}; diagnostic_regular_nkn_v6=0; transport_profile=nkn",
            "filetransfer_live_route_epoch_started" => $"{common}; previous_route=regular_nkn_v4_fast; state=started; reason=phase5d_fixture",
            "filetransfer_live_route_epoch_recovered" => $"{common}; reason=phase5d_fixture",
            "filetransfer_runtime_started" => $"{common}; role=sender; runtime_profile={route}; frame_family=v{protocolVersion}; bridge_recovery_policy={route}",
            "filetransfer_route_transitioned" => $"{common}; previous_route=regular_nkn_v4_fast; new_route={route}; previous_protocol_version=4; new_protocol_version={protocolVersion}; reason=phase5d_fixture",
            "filetransfer_route_hint_authorized_publication_accepted" => $"{common}; source=phase5d_fixture; reason=authorized_publication",
            _ => throw new ArgumentOutOfRangeException(nameof(eventName), eventName, "Unknown publication effect event.")
        };
    }

    private static string PublicationEffectMissingFieldMessage(
        string eventName,
        string transferId,
        string omittedFieldName,
        string route = "regular_nkn_v4_fast",
        int protocolVersion = 4,
        string targetTransport = "regular_nkn")
    {
        var message = PublicationEffectMessage(
            eventName,
            transferId,
            route: route,
            protocolVersion: protocolVersion,
            targetTransport: targetTransport);
        message = RemoveSemicolonLogField(message, omittedFieldName);
        return $"{message}; fixture_skip_publication_authority_fields=1";
    }

    private static string FallbackAttemptedAction(string subfamily)
        => subfamily switch
        {
            "fallback_send" => "fallback_control_plane_send",
            "fallback_checkpoint" => "fallback_checkpoint_accept",
            "fallback_reissue" => "fallback_checkpoint_reissue",
            "fallback_pressure" => "fallback_control_plane_pressure",
            "fallback_tail_yield" => "fallback_tail_quiescent",
            _ => subfamily
        };

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

    private static string FallbackForbiddenEffectMessage(
        string subfamily,
        string transferId,
        int liveRouteEpoch = 0,
        int runtimeGeneration = 1,
        int legGeneration = 2,
        int transportEpoch = 7,
        int bridgeRecoveryGeneration = 1,
        string checkpointRequestId = "checkpoint-a")
        => subfamily switch
        {
            "fallback_send" => $"event=filetransfer_control_plane_delivery_observed; kind=fallback_checkpoint_request; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch={liveRouteEpoch}; runtime_generation={runtimeGeneration}; leg_generation={legGeneration}; bridge_recovery_generation={bridgeRecoveryGeneration}; transport_epoch={transportEpoch}; checkpoint_request_id={checkpointRequestId}; checkpoint_generation=11; control_plane_token_generation=4; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy=1; bulk_copy=0; peer_visible_any=1; accepted_any=1; local_only_rejected=0",
            "fallback_checkpoint" => $"event=filetransfer_fallback_checkpoint_accepted; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch={liveRouteEpoch}; runtime_generation={runtimeGeneration}; leg_generation={legGeneration}; transport_epoch={transportEpoch}; bridge_recovery_generation={bridgeRecoveryGeneration}; checkpoint_request_id={checkpointRequestId}; checkpoint_generation=11; checkpoint_lease_generation=5; reason=phase3c_fixture",
            "fallback_reissue" => $"event=filetransfer_fallback_checkpoint_reissue_requested; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch={liveRouteEpoch}; runtime_generation={runtimeGeneration}; leg_generation={legGeneration}; transport_epoch={transportEpoch}; bridge_recovery_generation={bridgeRecoveryGeneration}; checkpoint_request_id={checkpointRequestId}; checkpoint_generation=11; checkpoint_lease_generation=5; reason=phase3c_fixture",
            "fallback_pressure" => $"event=filetransfer_post_tuna_fallback_control_plane_pressure_marked; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch={liveRouteEpoch}; runtime_generation={runtimeGeneration}; leg_generation={legGeneration}; transport_epoch={transportEpoch}; bridge_recovery_generation={bridgeRecoveryGeneration}; checkpoint_request_id={checkpointRequestId}; pressure_generation=6; reason=phase3c_fixture",
            "fallback_tail_yield" => $"event=filetransfer_fallback_tail_yield_proof_sent; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch={liveRouteEpoch}; runtime_generation={runtimeGeneration}; leg_generation={legGeneration}; transport_epoch={transportEpoch}; bridge_recovery_generation={bridgeRecoveryGeneration}; checkpoint_request_id={checkpointRequestId}; checkpoint_generation=11; checkpoint_lease_generation=5; yield_generation=3; reason=phase3c_fixture",
            _ => throw new ArgumentOutOfRangeException(nameof(subfamily), subfamily, "Unknown fallback subfamily.")
        };

    private static string FallbackLocalDeliveryMessage(string transferId, bool peerVisible)
    {
        var peerVisibleValue = peerVisible ? 1 : 0;
        var reason = peerVisible ? "peer_visible_checkpoint_completion" : "local_queue_only_checkpoint_completion";
        return $"event=filetransfer_control_plane_delivery_observed; kind=fallback_checkpoint_request; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; leg_generation=2; bridge_recovery_generation=1; transport_epoch=7; checkpoint_request_id=checkpoint-a; checkpoint_generation=11; reason={reason}; frame_type=filetransfer.frontier_request.v6; control_queue=1; control_copy={peerVisibleValue}; bulk_copy=0; peer_visible_any={peerVisibleValue}; accepted_any=1; local_only_rejected=0";
    }

    private static string RuntimeUnlockAdmissionGrantedMessage(string transferId)
        => $"event=filetransfer_runtime_unlock_admission_granted; direction=outbound; transfer_id={transferId}; session_id=sess_a; source=phase3c_fixture; reason=fallback_survival; admission_generation=9; admission_reason=paired_fallback_tail_yield_granted; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=2; fallback_active=1; fallback_current=1; fallback_terminalized=0; checkpoint_delivery_recovery_pending=0; state_refresh_send_in_flight=0; deferred_state_refresh_pending=0; proof_replay_pending=0; control_plane_pressure_pending=0; bridge_recovery_pending=0; fallback_tail_repair_pending=0; paired_fallback_yield_granted=1";

    private static string RuntimeUnlockForbiddenEffectMessage(string family, string transferId)
        => family switch
        {
            "control_plane" => $"event=runtime_unlock_precommit_probe_sent; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=file_tuna_v4; protocol_version=4; live_route_epoch=1; runtime_generation=1; transaction_generation=11; offer_generation=12; tuna_path_lease_generation=13; probe_id=probe-12; target_transport=tuna; reason=test_fixture",
            "route_publication" => $"event=filetransfer_route_selected; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=file_tuna_v4; protocol_version=4; runtime_profile=file_tuna_v4; frame_family=v4; handoff_kind=normal_to_tuna_activation; bridge_recovery_policy=file_tuna_v4; liveness_terminal_policy=file_tuna_v4; selection_reason=runtime_unlock_callback; file_tuna_active=1; post_tuna_fallback_active=0; diagnostic_regular_nkn_v6=0; transport_profile=tuna; live_route_epoch=1; runtime_generation=1; terminal_generation=0; leg_generation=2; route_publication_generation=1; coordinator_decision_reason=runtime_unlock_callback; route_commit_generation=4; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; transaction_generation=11; offer_generation=12; probe_id=probe-12",
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown runtime unlock action family.")
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

    private static string FallbackSubfamilyCounterName(string subfamily)
        => subfamily switch
        {
            "fallback_send" => "stale_rejected_fallback_send_effect_count",
            "fallback_checkpoint" => "stale_rejected_fallback_checkpoint_effect_count",
            "fallback_reissue" => "stale_rejected_fallback_reissue_effect_count",
            "fallback_pressure" => "stale_rejected_fallback_pressure_effect_count",
            "fallback_tail_yield" => "stale_rejected_fallback_tail_yield_effect_count",
            _ => throw new ArgumentOutOfRangeException(nameof(subfamily), subfamily, "Unknown fallback subfamily.")
        };
}
