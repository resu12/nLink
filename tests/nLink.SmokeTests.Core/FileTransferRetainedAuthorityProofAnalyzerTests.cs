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
    public async Task AnalyzeRetained_ReceiveRecoveryOperationConflict_IsKnownDiagnosticOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_receive_recovery_operation_conflict";
        var lines = BuildAuthorityProofFixture(
            transferId,
            $"event=filetransfer_recovery_authority_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; action_kind=ReceiveRecoveryOperation; source=filetransfer_receive_recovery; attempted_action=session_liveness_timeout_pending; rejection_reason=filetransfer_receive_recovery_operation_conflict; reason=filetransfer_receive_recovery_operation_conflict; current_route=regular_nkn_v4_fast; current_protocol_version=4; current_live_route_epoch=0; current_runtime_generation=1; current_terminal_generation=0; attempted_route=regular_nkn_v4_fast; attempted_protocol_version=4; attempted_live_route_epoch=0; attempted_runtime_generation=1; attempted_terminal_generation=0; attempted_leg_generation=1; attempted_transport_epoch=0; attempted_bridge_recovery_generation=0; recovery_decision_generation=1; recovery_liveness_lease_generation=0",
            $"event=filetransfer_receive_recovery_requested; direction=outbound; transfer_id={transferId}; session_id=sess_a; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; bridge_decision_generation=1; reason=current_receive_recovery_operation");

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("0", route["stale_rejected_action_forbidden_effect_count"]);
        Assert.Equal("0", route["stale_rejected_bridge_recovery_effect_count"]);
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
    public async Task AnalyzeRetained_RejectedRetryInstallAfterRejectedRuntimeUnlockCallback_IsDiagnosticOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_rejected_retry_install_diagnostic";
        var lines = BuildAuthorityProofFixture(
            transferId,
            $"event=filetransfer_runtime_unlock_callback_rejected; direction=outbound; transfer_id={transferId}; session_id=sess_a; action_kind=RuntimeUnlockTransactionCallback; source=runtime_unlock_paired_commit_timeout; attempted_action=paired_commit_timeout; rejection_reason=transfer_not_current; current_route=(none); current_protocol_version=0; attempted_route=file_tuna_v4; attempted_protocol_version=4; transaction_generation=1; offer_generation=5; tuna_path_lease_generation=5; probe_id=probe-a; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna",
            $"event=runtime_unlock_retry_dispatch_install_rejected; authority_schema=2; authority_id=fta2-runtime_unlock_retry_dispatch-b; predecessor_authority_id=fta2-runtime_unlock_retry_dispatch-a; direction=outbound; session_id=sess_a; transfer_id={transferId}; route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; leg_generation=1; transport_epoch=0; bridge_recovery_generation=0; route_publication_generation=1; route_commit_generation=0; contract_generation=2; retired_generation=4; dispatch_attempt_generation=3; missing_dispatch_successor_generation=1; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; install_generation=4; install_status=Rejected; decision_reason=retry_dispatch_install_authority_no_longer_current_offer_answer_timeout");

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["stale_rejected_action_effect_isolation_verdict"]);
        Assert.Equal("1", route["stale_rejected_action_count"]);
        Assert.Equal("0", route["stale_rejected_action_forbidden_effect_count"]);
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
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                startChunkIndex: 0));

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

    [Theory]
    [InlineData("filetransfer_data_path_send_result")]
    [InlineData("filetransfer_tuna_data_path_peer_visible")]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    [Trait("Category", "Smoke")]
    public async Task AnalyzeRetained_FileTunaV4DataPathRegularNknActualTransport_Fails(string actualEventName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_data_path_regular_nkn_fail";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTunaV4DataPathIntentMessage(transferId),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "regular_nkn",
                eventName: actualEventName));

        var result = await RunAnalyzeFixtureAsync(lines);
        var compact = await RunAnalyzeFixtureAsync(
            lines,
            environment: new Dictionary<string, string>
            {
                ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
            });

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

        var compactVerdict = ReadArtifactReport(compact.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("file_tuna_data_path_mismatch", compactVerdict["acceptance_failure_class"]);
        Assert.Equal("file_tuna_data_path_mismatch", compactVerdict["recovery_failure_class"]);
        Assert.Equal("file_tuna_data_path_mismatch", compactVerdict["first_proof_chain_failure_class"]);
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
                proofDirection: "send",
                startChunkIndex: 0));

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
                proofDirection: "send",
                startChunkIndex: 0));

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
    public async Task AnalyzeRetained_FileTunaV4ReceiveSideRegularNknFrameRejectedFailClosed_DoesNotFail()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_receive_regular_rejected";
        var lines = BuildFileTunaV4RejectedReceiveFixture(transferId);

        var result = await RunAnalyzeFixtureAsync(lines);
        var compact = await RunAnalyzeFixtureAsync(
            lines,
            environment: new Dictionary<string, string>
            {
                ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
            });

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("pass", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("0", route["file_tuna_v4_regular_nkn_send_result_count"]);
        Assert.Equal("0", route["file_tuna_v4_data_path_mismatch_count"]);
        Assert.Equal("0", route["file_tuna_v4_data_path_missing_actual_proof_count"]);

        var fullVerdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        var compactVerdict = ReadArtifactReport(compact.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("(none)", fullVerdict["recovery_failure_class"]);
        Assert.Equal("none", compactVerdict["recovery_failure_class"]);
        Assert.NotEqual("file_tuna_data_path_mismatch", fullVerdict["first_proof_chain_failure_class"]);
        Assert.NotEqual("file_tuna_data_path_mismatch", compactVerdict["first_proof_chain_failure_class"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    public async Task AnalyzeRetained_FileTunaV4LateRegularDeliveryBeforeSenderTransition_IsFailClosed(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_late_regular_delivery";
        var lines = BuildFileTunaV4RejectedReceiveFixture(
            transferId,
            includeOutboundTransition: false);
        var result = await RunAnalyzeFixtureAsync(
            lines,
            environment: compact
                ? new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                }
                : null);

        Assert.Equal(0, result.Script.ExitCode);
        if (!compact)
        {
            var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
            Assert.Equal("pass", route["file_tuna_v4_data_path_proof_verdict"]);
            Assert.Equal("0", route["file_tuna_v4_data_path_mismatch_count"]);
        }

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("0", verdict["file_tuna_v4_data_path_mismatch_count"]);
        Assert.NotEqual("file_tuna_data_path_mismatch", verdict["recovery_failure_class"]);
        Assert.NotEqual("file_tuna_data_path_mismatch", verdict["first_proof_chain_failure_class"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_RouteCommitOldPhysicalWritesAfterOwnerInstall_AreOnePreciseFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_commit_old_physical_write";
        var lines = BuildRouteCommitSendDrainLegacyFailureFixture(transferId);

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
            Assert.Equal("6", route["runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count"]);
            Assert.Equal("1", route["runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count"]);
            Assert.Equal("0", route["runtime_unlock_regular_v4_route_commit_send_drain_missing_recovered_count"]);
            Assert.Equal("1", route["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
            Assert.Equal("fail", route["runtime_unlock_regular_v4_route_commit_send_drain_verdict"]);
            Assert.Equal("0", route["file_tuna_v4_data_path_mismatch_count"]);

            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("route_runtime", verdict["acceptance_failure_class"]);
            Assert.Equal("runtime_unlock_regular_v4_route_commit_send_drain_missing", verdict["recovery_failure_class"]);
            Assert.Equal("runtime_unlock_regular_v4_route_commit_send_drain_missing", verdict["first_proof_chain_failure_class"]);
            Assert.Equal("filetransfer_data_path_send_result", verdict["first_proof_chain_failure_event"]);
            Assert.Equal(
                "retired_regular_v4_send_completed_after_tuna_owner_install",
                verdict["first_proof_chain_failure_reason"]);
            Assert.Equal("102", verdict["first_proof_chain_failure_writer_sequence"]);
            Assert.Equal("route_runtime", verdict["semantic_failure_domain"]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_RegularV4WriteOwnerBoundary_BeginsAtTunaLegStart(
        bool sendAfterOwnerInstall)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = $"transfer_route_commit_owner_boundary_{sendAfterOwnerInstall}";
        const string writerId = "writer_route_commit_owner_boundary";
        var legStarted =
            $"event=filetransfer_leg_started; direction=outbound; transfer_id={transferId}; session_id=sess_a; " +
            "leg_id=leg:2; leg_generation=2; route=file_tuna_v4; protocol_version=4; live_route_epoch=1; " +
            "transport_epoch=0; state=active; reason=precommit_probe_commit_confirmed; can_send_data=1; " +
            $"log_writer_id={writerId}; log_writer_sequence=100";
        var physicalSend = LegacyRegularV4PhysicalSendMessage(
            transferId,
            writerId,
            writerSequence: sendAfterOwnerInstall ? 101 : 99,
            startChunkIndex: 42);
        var ownerRetired =
            $"event=filetransfer_runtime_owner_workers_retired; direction=outbound; transfer_id={transferId}; " +
            "session_id=sess_a; retired_runtime_generation=1; current_runtime_generation=2; " +
            "retired_route=regular_nkn_v4_fast; current_route=file_tuna_v4; " +
            $"reason=precommit_probe_commit_confirmed; log_writer_id={writerId}; log_writer_sequence=102";
        var orderedBoundary = sendAfterOwnerInstall
            ? new[] { legStarted, physicalSend, ownerRetired }
            : new[] { physicalSend, legStarted, ownerRetired };
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            orderedBoundary
                .Append(RouteCommitDrainTransitionMessage(transferId, completedDrainGeneration: 0))
                .Append(FileTunaV4DataPathActualMessage(
                    transferId,
                    actualTransport: "tuna",
                    eventName: "filetransfer_tuna_data_path_peer_visible",
                    proofDirection: "send",
                    startChunkIndex: 0))
                .ToArray());

        var reports = new List<IReadOnlyDictionary<string, string>>();
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
            reports.Add(ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt"));
        }

        foreach (var verdict in reports)
        {
            Assert.Equal(
                sendAfterOwnerInstall ? "1" : "0",
                verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count"]);
            Assert.Equal(
                sendAfterOwnerInstall ? "1" : "0",
                verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
            if (sendAfterOwnerInstall)
            {
                Assert.Equal(
                    "runtime_unlock_regular_v4_route_commit_send_drain_missing",
                    verdict["first_proof_chain_failure_class"]);
                Assert.Equal("filetransfer_data_path_send_result", verdict["first_proof_chain_failure_event"]);
                Assert.Equal(
                    "retired_regular_v4_send_completed_after_tuna_owner_install",
                    verdict["first_proof_chain_failure_reason"]);
                Assert.Equal("101", verdict["first_proof_chain_failure_writer_sequence"]);
            }
            else
            {
                Assert.NotEqual(
                    "runtime_unlock_regular_v4_route_commit_send_drain_missing",
                    verdict["first_proof_chain_failure_class"]);
            }
        }

        foreach (var field in new[]
                 {
                     "runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count",
                     "runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count",
                     "runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count",
                     "first_proof_chain_failure_class",
                     "first_proof_chain_failure_event",
                     "first_proof_chain_failure_reason",
                     "first_proof_chain_failure_sequence",
                     "first_proof_chain_failure_writer_sequence"
                 })
        {
            Assert.Equal(reports[0][field], reports[1][field]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_RouteCommitLateIgnoredDelivery_RequiresExactSourceWrite(
        bool mismatchRange)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = $"transfer_route_commit_safe_late_{mismatchRange}";
        var lines = BuildRouteCommitSendDrainSafeLateDeliveryFixture(transferId, mismatchRange);
        var reports = new List<IReadOnlyDictionary<string, string>>();
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
            reports.Add(ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt"));
        }

        foreach (var verdict in reports)
        {
            Assert.Equal("0", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count"]);
            Assert.Equal("0", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
            Assert.Equal("pass", verdict["runtime_unlock_regular_v4_route_commit_send_drain_verdict"]);
            if (mismatchRange)
            {
                Assert.NotEqual("0", verdict["file_tuna_v4_data_path_mismatch_count"]);
                Assert.Equal("file_tuna_data_path_mismatch", verdict["first_proof_chain_failure_class"]);
            }
            else
            {
                Assert.Equal("0", verdict["file_tuna_v4_data_path_mismatch_count"]);
                Assert.NotEqual("file_tuna_data_path_mismatch", verdict["first_proof_chain_failure_class"]);
            }
        }

        Assert.Equal(reports[0]["file_tuna_v4_data_path_mismatch_count"], reports[1]["file_tuna_v4_data_path_mismatch_count"]);
        Assert.Equal(reports[0]["first_proof_chain_failure_class"], reports[1]["first_proof_chain_failure_class"]);
        Assert.Equal(reports[0]["first_proof_chain_failure_event"], reports[1]["first_proof_chain_failure_event"]);
        Assert.Equal(reports[0]["first_proof_chain_failure_reason"], reports[1]["first_proof_chain_failure_reason"]);
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_RouteCommitDuplicateAndForeignScopeWrites_DeduplicateAndPreserveChronology()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_commit_duplicate";
        var lines = BuildRouteCommitSendDrainLegacyFailureFixture(transferId);
        var firstUnsafeIndex = lines.FindIndex(line =>
            line.Contains("log_writer_sequence=102", StringComparison.Ordinal));
        Assert.True(firstUnsafeIndex >= 0);
        lines.Insert(firstUnsafeIndex + 1, lines[firstUnsafeIndex]);
        lines.Insert(
            firstUnsafeIndex + 2,
            lines[firstUnsafeIndex]
                .Replace($"transfer_id={transferId}", "transfer_id=foreign_transfer", StringComparison.Ordinal)
                .Replace("log_writer_sequence=102", "log_writer_sequence=109", StringComparison.Ordinal));

        var reports = new List<IReadOnlyDictionary<string, string>>();
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
            reports.Add(ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt"));
        }

        foreach (var verdict in reports)
        {
            Assert.Equal("7", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count"]);
            Assert.Equal("1", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count"]);
            Assert.Equal("1", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
            Assert.Equal("runtime_unlock_regular_v4_route_commit_send_drain_missing", verdict["first_proof_chain_failure_class"]);
            Assert.Equal("filetransfer_data_path_send_result", verdict["first_proof_chain_failure_event"]);
            Assert.Equal(
                "retired_regular_v4_send_completed_after_tuna_owner_install",
                verdict["first_proof_chain_failure_reason"]);
        }

        foreach (var key in new[]
                 {
                     "runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count",
                     "runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count",
                     "runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count",
                     "first_proof_chain_failure_class",
                     "first_proof_chain_failure_event",
                     "first_proof_chain_failure_reason",
                     "first_proof_chain_failure_sequence"
                 })
        {
            Assert.Equal(reports[0][key], reports[1][key]);
        }
    }

    [Theory]
    [InlineData(true, "none")]
    [InlineData(false, "tuna_owner_installed_without_current_send_drain_completion")]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_RouteCommitOwnerInstallRequiresExactCompletedDrain(
        bool includeCompletion,
        string expectedReason)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = $"transfer_route_commit_drain_{includeCompletion}";
        var middle = new List<string>
        {
            RouteCommitSendDrainMessage(
                "runtime_unlock_regular_v4_route_commit_send_drain_staged",
                transferId)
        };
        if (includeCompletion)
        {
            middle.Add(RouteCommitSendDrainMessage(
                "runtime_unlock_regular_v4_route_commit_send_drain_completed",
                transferId));
        }
        middle.Add(
            $"event=filetransfer_runtime_owner_workers_retired; direction=outbound; transfer_id={transferId}; " +
            "session_id=sess_a; retired_runtime_generation=1; current_runtime_generation=7; " +
            "retired_route=regular_nkn_v4_fast; current_route=file_tuna_v4; reason=precommit_probe_commit_confirmed");
        middle.Add(RouteCommitDrainTransitionMessage(transferId, completedDrainGeneration: 1));
        middle.Add(FileTunaV4DataPathActualMessage(
            transferId,
            actualTransport: "tuna",
            eventName: "filetransfer_tuna_data_path_peer_visible",
            proofDirection: "send",
            startChunkIndex: 0));
        var lines = BuildFileTunaV4DataPathProofFixture(transferId, middle.ToArray());

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
            Assert.Equal(includeCompletion ? "0" : "1", route["runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count"]);
            Assert.Equal(includeCompletion ? "0" : "1", route["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
            Assert.Equal(includeCompletion ? "pass" : "fail", route["runtime_unlock_regular_v4_route_commit_send_drain_verdict"]);

            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            if (includeCompletion)
            {
                Assert.NotEqual("runtime_unlock_regular_v4_route_commit_send_drain_missing", verdict["first_proof_chain_failure_class"]);
            }
            else
            {
                Assert.Equal("runtime_unlock_regular_v4_route_commit_send_drain_missing", verdict["first_proof_chain_failure_class"]);
                Assert.Equal("filetransfer_runtime_owner_workers_retired", verdict["first_proof_chain_failure_event"]);
                Assert.Equal(expectedReason, verdict["first_proof_chain_failure_reason"]);
            }
        }
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_RouteCommitPublicationBeforeDrainCompletion_IsRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_commit_publication_before_drain";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            RouteCommitSendDrainMessage(
                "runtime_unlock_regular_v4_route_commit_send_drain_staged",
                transferId),
            RouteCommitDrainTransitionMessage(transferId, completedDrainGeneration: 1),
            RouteCommitSendDrainMessage(
                "runtime_unlock_regular_v4_route_commit_send_drain_completed",
                transferId),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                startChunkIndex: 0));

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
            Assert.Equal("1", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count"]);
            Assert.Equal("1", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count"]);
            Assert.Equal("runtime_unlock_regular_v4_route_commit_send_drain_missing", verdict["first_proof_chain_failure_class"]);
            Assert.Equal("filetransfer_route_transitioned", verdict["first_proof_chain_failure_event"]);
            Assert.Equal(
                "tuna_route_published_without_current_send_drain_completion",
                verdict["first_proof_chain_failure_reason"]);
        }
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_RouteCommitCompletedDrainDoesNotExcusePostOwnerPhysicalWrite()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_commit_completed_then_late_write";
        const string writerId = "writer_route_commit_completed_then_late_write";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            RouteCommitSendDrainMessage("runtime_unlock_regular_v4_route_commit_send_drain_staged", transferId),
            RouteCommitSendDrainMessage("runtime_unlock_regular_v4_route_commit_send_drain_completed", transferId),
            $"event=filetransfer_leg_started; direction=outbound; transfer_id={transferId}; session_id=sess_a; " +
            "leg_id=leg:2; leg_generation=2; route=file_tuna_v4; protocol_version=4; live_route_epoch=1; " +
            "transport_epoch=0; state=active; reason=precommit_probe_commit_confirmed; can_send_data=1; " +
            $"log_writer_id={writerId}; log_writer_sequence=100",
            $"event=filetransfer_runtime_owner_workers_retired; direction=outbound; transfer_id={transferId}; " +
            "session_id=sess_a; retired_runtime_generation=1; current_runtime_generation=2; " +
            "retired_route=regular_nkn_v4_fast; current_route=file_tuna_v4; " +
            $"reason=precommit_probe_commit_confirmed; log_writer_id={writerId}; log_writer_sequence=101",
            LegacyRegularV4PhysicalSendMessage(transferId, writerId, writerSequence: 102, startChunkIndex: 30),
            RouteCommitDrainTransitionMessage(transferId, completedDrainGeneration: 1),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                startChunkIndex: 0));

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
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("1", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count"]);
            Assert.Equal("1", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count"]);
            Assert.Equal("runtime_unlock_regular_v4_route_commit_send_drain_missing", verdict["first_proof_chain_failure_class"]);
            Assert.Equal("filetransfer_data_path_send_result", verdict["first_proof_chain_failure_event"]);
            Assert.Equal(
                "retired_regular_v4_send_completed_after_tuna_owner_install",
                verdict["first_proof_chain_failure_reason"]);
        }
    }

    [Fact]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_RouteCommitExplicitInvalidationDisposesStagedDrain()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_commit_invalidated";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            RouteCommitSendDrainMessage("runtime_unlock_regular_v4_route_commit_send_drain_staged", transferId),
            RouteCommitSendDrainMessage("runtime_unlock_regular_v4_route_commit_send_drain_invalidated", transferId),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                startChunkIndex: 0));

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
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal("0", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
            Assert.Equal("pass", verdict["runtime_unlock_regular_v4_route_commit_send_drain_verdict"]);
            Assert.NotEqual("runtime_unlock_regular_v4_route_commit_send_drain_missing", verdict["first_proof_chain_failure_class"]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_RouteCommitInvalidationWithCapturedWritesStillActive_FailsAtInvalidation(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_commit_invalidated_with_active_writes";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            RouteCommitSendDrainMessage(
                "runtime_unlock_regular_v4_route_commit_send_drain_staged",
                transferId,
                probeId: "(none)",
                probeState: "None",
                capturedSendCount: 45,
                matchingRetiredSendCount: 0,
                activeSendCount: 45,
                writerSequence: 2780),
            RouteCommitSendDrainMessage(
                "runtime_unlock_regular_v4_route_commit_send_drain_deferred",
                transferId,
                capturedSendCount: 45,
                matchingRetiredSendCount: 0,
                activeSendCount: 45,
                decisionReason: "regular_v4_physical_sends_in_flight",
                writerSequence: 2841),
            RouteCommitSendDrainMessage(
                "runtime_unlock_regular_v4_route_commit_send_drain_invalidated",
                transferId,
                capturedSendCount: 45,
                matchingRetiredSendCount: 0,
                activeSendCount: 45,
                decisionReason: "coordinator_source_scope_replaced",
                writerSequence: 3300));

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
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count"]);
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_recovered_count"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
        Assert.Equal("fail", verdict["runtime_unlock_regular_v4_route_commit_send_drain_verdict"]);
        Assert.Equal("runtime_unlock_regular_v4_route_commit_send_drain_missing", verdict["first_proof_chain_failure_class"]);
        Assert.Equal(
            "runtime_unlock_regular_v4_route_commit_send_drain_invalidated",
            verdict["first_proof_chain_failure_event"]);
        Assert.Equal(
            "captured_physical_sends_not_retired_before_scope_invalidation",
            verdict["first_proof_chain_failure_reason"]);
        Assert.Equal("3300", verdict["first_proof_chain_failure_writer_sequence"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_RouteCommitInvalidationAfterCapturedWritesRetire_DisposesDrain(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_commit_invalidated_after_writes_retire";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            RouteCommitSendDrainMessage(
                "runtime_unlock_regular_v4_route_commit_send_drain_staged",
                transferId,
                capturedSendCount: 2,
                matchingRetiredSendCount: 0,
                activeSendCount: 2),
            RouteCommitSendDrainMessage(
                "runtime_unlock_regular_v4_route_commit_send_drain_invalidated",
                transferId,
                capturedSendCount: 2,
                matchingRetiredSendCount: 2,
                activeSendCount: 0,
                decisionReason: "coordinator_source_scope_replaced"));

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
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
        Assert.Equal("pass", verdict["runtime_unlock_regular_v4_route_commit_send_drain_verdict"]);
        Assert.NotEqual(
            "runtime_unlock_regular_v4_route_commit_send_drain_missing",
            verdict["first_proof_chain_failure_class"]);
    }

    [Theory]
    [InlineData("(none)", "None", "probe-1", "Acked", false)]
    [InlineData("probe-1", "Started", "probe-2", "Acked", true)]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_RouteCommitProbeLifecycleAllowsOnlySameBoundaryProgression(
        string stagedProbeId,
        string stagedProbeState,
        string completedProbeId,
        string completedProbeState,
        bool expectFailure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = $"transfer_route_commit_probe_{expectFailure}";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            RouteCommitSendDrainMessage(
                "runtime_unlock_regular_v4_route_commit_send_drain_staged",
                transferId,
                stagedProbeId,
                stagedProbeState),
            RouteCommitSendDrainMessage(
                "runtime_unlock_regular_v4_route_commit_send_drain_completed",
                transferId,
                completedProbeId,
                completedProbeState),
            $"event=filetransfer_runtime_owner_workers_retired; direction=outbound; transfer_id={transferId}; " +
            "session_id=sess_a; retired_runtime_generation=1; current_runtime_generation=7; " +
            "retired_route=regular_nkn_v4_fast; current_route=file_tuna_v4; reason=precommit_probe_commit_confirmed",
            RouteCommitDrainTransitionMessage(transferId, completedDrainGeneration: 1),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                startChunkIndex: 0));

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
            var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
            Assert.Equal(expectFailure ? "1" : "0", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
            Assert.Equal(expectFailure ? "fail" : "pass", verdict["runtime_unlock_regular_v4_route_commit_send_drain_verdict"]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_CoordinatorRouteTransition_RequiresExactOrderedDrainAndAtomicCommit(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_coordinator_route_transition_valid";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_prepared",
                transferId),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_prepared",
                transferId),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_pause_bound",
                transferId,
                status: "Installed"),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_draining",
                transferId),
            RegularV4ToTunaPrecommitAckMessage(transferId),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_drain_receipt_decided",
                transferId,
                status: "Accepted"),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_drain_receipt_decided",
                transferId,
                status: "Idempotent"),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_committed",
                transferId,
                status: "Committed"),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_disposition_applied",
                transferId,
                status: "Committed")
                .Replace(
                    "status=Committed",
                    "disposition=committed; apply_status=applied",
                    StringComparison.Ordinal),
            RegularV4ToTunaAtomicRouteTransitionMessage(transferId),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                startChunkIndex: 0));

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
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count"]);
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
        Assert.Equal("pass", verdict["runtime_unlock_regular_v4_route_commit_send_drain_verdict"]);
        Assert.NotEqual(
            "runtime_unlock_regular_v4_route_commit_send_drain_missing",
            verdict["first_proof_chain_failure_class"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_CoordinatorRouteTransition_RejectedCommitThenExactCommitPasses(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_coordinator_route_transition_rejected_then_committed";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_prepared",
                transferId),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_pause_bound",
                transferId,
                status: "Installed"),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_draining",
                transferId),
            RegularV4ToTunaPrecommitAckMessage(transferId),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_drain_receipt_decided",
                transferId,
                status: "Accepted"),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_committed",
                transferId,
                status: "Rejected"),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_committed",
                transferId,
                status: "Committed"),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_disposition_applied",
                transferId,
                status: "Committed")
                .Replace(
                    "status=Committed",
                    "disposition=committed; apply_status=applied",
                    StringComparison.Ordinal));

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
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count"]);
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
        Assert.Equal("pass", verdict["runtime_unlock_regular_v4_route_commit_send_drain_verdict"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_CoordinatorRouteTransition_AckBeforeDrainingFailsAtCommit(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_coordinator_route_transition_ack_before_drain";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_prepared",
                transferId),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_pause_bound",
                transferId,
                status: "Installed"),
            RegularV4ToTunaPrecommitAckMessage(transferId),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_draining",
                transferId),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_drain_receipt_decided",
                transferId,
                status: "Accepted"),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_committed",
                transferId,
                status: "Committed",
                writerSequence: 46));

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
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
        Assert.Equal(
            "regular_v4_to_tuna_transition_committed_without_acked_precommit_proof",
            verdict["first_proof_chain_failure_reason"]);
        Assert.Equal("46", verdict["first_proof_chain_failure_writer_sequence"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_CoordinatorRouteTransition_MutatedCommitTupleFailsAtCommit(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_coordinator_route_transition_mutated_commit";
        var mutatedCommit = RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_committed",
                transferId,
                status: "Committed",
                writerSequence: 47)
            .Replace(
                "source_runtime_generation=4",
                "source_runtime_generation=99",
                StringComparison.Ordinal);
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_prepared",
                transferId),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_pause_bound",
                transferId,
                status: "Installed"),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_draining",
                transferId),
            RegularV4ToTunaPrecommitAckMessage(transferId),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_drain_receipt_decided",
                transferId,
                status: "Accepted"),
            mutatedCommit);

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
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
        Assert.Equal(
            "regular_v4_to_tuna_transition_authority_tuple_mutated",
            verdict["first_proof_chain_failure_reason"]);
        Assert.Equal("47", verdict["first_proof_chain_failure_writer_sequence"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_CoordinatorRouteTransition_CommitBeforeReceiptFailsAtCommit(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_coordinator_route_transition_early_commit";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_prepared",
                transferId),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_pause_bound",
                transferId,
                status: "Installed"),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_draining",
                transferId),
            RegularV4ToTunaPrecommitAckMessage(transferId),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_committed",
                transferId,
                status: "Committed",
                writerSequence: 44),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_drain_receipt_decided",
                transferId,
                status: "Accepted"),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_disposition_applied",
                transferId,
                status: "Committed"));

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
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count"]);
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
        Assert.Equal(
            "runtime_unlock_regular_v4_route_commit_send_drain_missing",
            verdict["first_proof_chain_failure_class"]);
        Assert.Equal(
            "filetransfer_regular_v4_to_tuna_route_transition_committed",
            verdict["first_proof_chain_failure_event"]);
        Assert.Equal(
            "regular_v4_to_tuna_transition_committed_without_exact_physical_drain_receipt",
            verdict["first_proof_chain_failure_reason"]);
        Assert.Equal("44", verdict["first_proof_chain_failure_writer_sequence"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_CoordinatorRouteTransition_PayloadLeaseAfterDrainingFailsAtRegistration(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_coordinator_route_transition_late_lease";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_prepared",
                transferId),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_pause_bound",
                transferId,
                status: "Installed"),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_draining",
                transferId),
            RegularV4ToTunaPhysicalSendLeaseMessage(transferId, writerSequence: 51),
            RegularV4ToTunaPrecommitAckMessage(transferId),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_drain_receipt_decided",
                transferId,
                status: "Accepted"),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_committed",
                transferId,
                status: "Committed"),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_disposition_applied",
                transferId,
                status: "Committed"));

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
        Assert.Equal("1", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
        Assert.Equal(
            "filetransfer_regular_v4_physical_send_lease_registered",
            verdict["first_proof_chain_failure_event"]);
        Assert.Equal(
            "regular_v4_payload_lease_registered_after_transition_draining",
            verdict["first_proof_chain_failure_reason"]);
        Assert.Equal("51", verdict["first_proof_chain_failure_writer_sequence"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedAuthorityProofSuite", "RouteCommitSendDrain")]
    public async Task AnalyzeRetained_CoordinatorRouteTransition_ExactInvalidationDispositionIsTerminal(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_coordinator_route_transition_invalidated";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_prepared",
                transferId),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_pause_bound",
                transferId,
                status: "Installed"),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_draining",
                transferId),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_invalidated",
                transferId,
                status: "Invalidated"),
            RegularV4ToTunaTransitionMessage(
                "filetransfer_regular_v4_to_tuna_route_transition_disposition_applied",
                transferId,
                status: "Invalidated")
                .Replace(
                    "status=Invalidated",
                    "disposition=rolledback; apply_status=applied",
                    StringComparison.Ordinal));

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
        Assert.Equal("0", verdict["runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count"]);
        Assert.Equal("pass", verdict["runtime_unlock_regular_v4_route_commit_send_drain_verdict"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedAuthorityProofSuite", "FileTransferRouteTransition")]
    public async Task AnalyzeRetained_FallbackRouteTransitions_ExactChainsPass(bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_generic_fallback_route_transition_valid";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTransferRouteTransitionProofMessage(
                transferId,
                operationGeneration: 11,
                authorityId: "transition-tuna-fallback-11"),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_prepared",
                transferId,
                operationGeneration: 11,
                authorityId: "transition-tuna-fallback-11"),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_pause_bound",
                transferId,
                operationGeneration: 11,
                authorityId: "transition-tuna-fallback-11",
                status: "Installed"),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_draining",
                transferId,
                operationGeneration: 11,
                authorityId: "transition-tuna-fallback-11",
                status: "Draining"),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_drain_receipt_decided",
                transferId,
                operationGeneration: 11,
                authorityId: "transition-tuna-fallback-11",
                status: "Accepted"),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_committed",
                transferId,
                operationGeneration: 11,
                authorityId: "transition-tuna-fallback-11",
                status: "Committed"),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_retained_frame_applied",
                transferId,
                operationGeneration: 11,
                authorityId: "transition-tuna-fallback-11",
                status: "Applied"),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_disposition_applied",
                transferId,
                operationGeneration: 11,
                authorityId: "transition-tuna-fallback-11",
                status: "Committed"),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_prepared",
                transferId,
                operationGeneration: 12,
                authorityId: "transition-fallback-tuna-12",
                sourceRoute: "post_tuna_fallback_v6",
                sourceProtocol: 6,
                targetRoute: "file_tuna_v4",
                targetProtocol: 4,
                targetTransport: "Tuna"),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_pause_bound",
                transferId,
                operationGeneration: 12,
                authorityId: "transition-fallback-tuna-12",
                status: "Installed",
                sourceRoute: "post_tuna_fallback_v6",
                sourceProtocol: 6,
                targetRoute: "file_tuna_v4",
                targetProtocol: 4,
                targetTransport: "Tuna"),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_draining",
                transferId,
                operationGeneration: 12,
                authorityId: "transition-fallback-tuna-12",
                status: "Draining",
                sourceRoute: "post_tuna_fallback_v6",
                sourceProtocol: 6,
                targetRoute: "file_tuna_v4",
                targetProtocol: 4,
                targetTransport: "Tuna"),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_drain_receipt_decided",
                transferId,
                operationGeneration: 12,
                authorityId: "transition-fallback-tuna-12",
                status: "Accepted",
                sourceRoute: "post_tuna_fallback_v6",
                sourceProtocol: 6,
                targetRoute: "file_tuna_v4",
                targetProtocol: 4,
                targetTransport: "Tuna"),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_committed",
                transferId,
                operationGeneration: 12,
                authorityId: "transition-fallback-tuna-12",
                status: "Committed",
                sourceRoute: "post_tuna_fallback_v6",
                sourceProtocol: 6,
                targetRoute: "file_tuna_v4",
                targetProtocol: 4,
                targetTransport: "Tuna"),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_disposition_applied",
                transferId,
                operationGeneration: 12,
                authorityId: "transition-fallback-tuna-12",
                status: "Committed",
                sourceRoute: "post_tuna_fallback_v6",
                sourceProtocol: 6,
                targetRoute: "file_tuna_v4",
                targetProtocol: 4,
                targetTransport: "Tuna"));

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
        Assert.Equal("0", verdict["filetransfer_route_transition_incomplete_unresolved_count"]);
        Assert.Equal("pass", verdict["filetransfer_route_transition_verdict"]);
        Assert.NotEqual("filetransfer_route_transition_incomplete", verdict["first_proof_chain_failure_class"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedAuthorityProofSuite", "FileTransferRouteTransition")]
    public async Task AnalyzeRetained_FallbackV6ProofBootstrapMissing_HasFullCompactParity(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_fallback_v6_proof_bootstrap_missing";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_committed",
                transferId,
                operationGeneration: 7,
                authorityId: "transition-tuna-7",
                status: "Committed",
                sourceRoute: "regular_nkn_v4_fast",
                sourceProtocol: 4,
                targetRoute: "file_tuna_v4",
                targetProtocol: 4,
                targetTransport: "Tuna",
                writerSequence: 100),
            $"event=tuna_fallback_started; session_id=sess_a; transfer_id={transferId}; direction=outbound; fallback_epoch=1; log_writer_id=writer_route_transition; log_writer_sequence=101",
            $"event=filetransfer_fallback_nkn_proof_pending; session_id=sess_a; transfer_id={transferId}; direction=outbound; fallback_epoch=1; log_writer_id=writer_route_transition; log_writer_sequence=102",
            $"event=filetransfer_fallback_nkn_proof_waiting_for_v6_epoch; session_id=sess_a; transfer_id={transferId}; direction=outbound; fallback_epoch=1; log_writer_id=writer_route_transition; log_writer_sequence=103",
            $"event=nkn_bridge_post_tuna_fallback_proof_send_window_skipped; session_id=sess_a; transfer_id={transferId}; direction=outbound; reason=no_active_post_tuna_fallback_runtime; log_writer_id=writer_route_transition; log_writer_sequence=104");

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
            "1",
            verdict["filetransfer_fallback_v6_proof_bootstrap_missing_distinct_count"]);
        Assert.Equal(
            "filetransfer_route_transition_tuna_v4_to_post_tuna_fallback_v6_proof_bootstrap_missing",
            verdict["first_proof_chain_failure_class"]);
        Assert.Equal(
            "post_tuna_fallback_v6_proof_bootstrap_gated_on_committed_target_runtime",
            verdict["first_proof_chain_failure_reason"]);
        Assert.Equal("route_runtime", verdict["semantic_failure_domain"]);
        Assert.Equal("fail", verdict["runtime_correctness_verdict"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedAuthorityProofSuite", "FileTransferRouteTransition")]
    public async Task AnalyzeRetained_FileTransferRouteTransitionLiveEpochProjection_MissingIsPrecise(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_transition_projection_missing";
        const int generation = 17;
        const string authorityId = "transition-projection-17";
        var commit = FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_committed",
                transferId,
                generation,
                authorityId,
                status: "Committed",
                sourceRoute: "regular_nkn_v4_fast",
                sourceProtocol: 4,
                targetRoute: "file_tuna_v4",
                targetProtocol: 4,
                targetTransport: "Tuna",
                writerSequence: 105)
            + "; projected_target_route=file_tuna_v4; target_runtime_generation=20; " +
              "target_terminal_generation=0; target_leg_generation=21; " +
              "target_route_publication_generation=22; target_route_commit_generation=23; " +
              "decision_status=Committed";
        var disposition = FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_disposition_applied",
                transferId,
                generation,
                authorityId,
                status: "Committed",
                sourceRoute: "regular_nkn_v4_fast",
                sourceProtocol: 4,
                targetRoute: "file_tuna_v4",
                targetProtocol: 4,
                targetTransport: "Tuna",
                writerSequence: 107)
            + "; disposition=committed; resulting_route_publication_generation=22; " +
              "resulting_route_commit_generation=23";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_prepared",
                transferId,
                generation,
                authorityId,
                sourceRoute: "regular_nkn_v4_fast",
                sourceProtocol: 4,
                targetRoute: "file_tuna_v4",
                targetProtocol: 4,
                targetTransport: "Tuna",
                writerSequence: 101),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_pause_bound",
                transferId,
                generation,
                authorityId,
                status: "Installed",
                sourceRoute: "regular_nkn_v4_fast",
                sourceProtocol: 4,
                targetRoute: "file_tuna_v4",
                targetProtocol: 4,
                targetTransport: "Tuna",
                writerSequence: 102),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_draining",
                transferId,
                generation,
                authorityId,
                status: "Draining",
                sourceRoute: "regular_nkn_v4_fast",
                sourceProtocol: 4,
                targetRoute: "file_tuna_v4",
                targetProtocol: 4,
                targetTransport: "Tuna",
                writerSequence: 103),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_drain_receipt_decided",
                transferId,
                generation,
                authorityId,
                status: "Accepted",
                sourceRoute: "regular_nkn_v4_fast",
                sourceProtocol: 4,
                targetRoute: "file_tuna_v4",
                targetProtocol: 4,
                targetTransport: "Tuna",
                writerSequence: 104),
            commit,
            $"event=filetransfer_route_hint_authorized_publication_accepted; session_id=sess_a; transfer_id={transferId}; direction=outbound; route=file_tuna_v4; protocol_version=4; live_route_epoch=5; runtime_generation=20; terminal_generation=0; leg_generation=21; route_publication_generation=22; route_commit_generation=23; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; log_writer_id=writer_route_transition; log_writer_sequence=106",
            disposition,
            $"event=filetransfer_data_path_send_result; session_id=sess_a; transfer_id={transferId}; direction=outbound; route=file_tuna_v4; protocol_version=4; live_route_epoch=5; runtime_generation=20; terminal_generation=0; leg_generation=21; route_publication_generation=22; route_commit_generation=23; target_route=file_tuna_v4; target_protocol_version=4; target_transport=tuna; delivery_result=sent; log_writer_id=writer_route_transition; log_writer_sequence=108");

        var result = compact
            ? await RunAnalyzeFixtureAsync(
                lines,
                environment: new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                })
            : await RunAnalyzeFixtureAsync(lines);

        var verdict = ReadArtifactReport(result.ArtifactDir, "filetransfer-operator-verdict.txt");
        Assert.Equal("1", verdict["filetransfer_live_route_epoch_projection_missing_raw_count"]);
        Assert.Equal("1", verdict["filetransfer_live_route_epoch_projection_missing_distinct_count"]);
        Assert.Equal("0", verdict["filetransfer_live_route_epoch_projection_missing_recovered_count"]);
        Assert.Equal("1", verdict["filetransfer_live_route_epoch_projection_missing_unresolved_count"]);
        Assert.Equal("filetransfer_live_route_epoch_projection_missing", verdict["first_proof_chain_failure_class"]);
        Assert.Equal("filetransfer_route_transition_committed", verdict["first_proof_chain_failure_event"]);
        Assert.Equal("incomplete_proof", verdict["acceptance_failure_class"]);
        Assert.Equal("incomplete_proof", verdict["runtime_correctness_verdict"]);
        Assert.Equal("route_evidence", verdict["semantic_failure_domain"]);
        Assert.Equal(
            "committed_route_transition_not_projected_to_live_route_epoch_evidence",
            verdict["first_proof_chain_failure_reason"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("RetainedAuthorityProofSuite", "FileTransferRouteTransition")]
    public async Task AnalyzeRetained_RouteTransitionDrainingTransportGrace_HasFullCompactParity(
        bool compact)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_route_transition_transport_grace";
        const string writerId = "writer_route_transition_grace";
        const int generation = 31;
        const string authorityId = "transition-grace-31";
        var prepared = FileTransferRouteTransitionMessage(
            "filetransfer_route_transition_prepared",
            transferId,
            generation,
            authorityId,
            sourceRoute: "regular_nkn_v4_fast",
            targetRoute: "file_tuna_v4",
            targetProtocol: 4,
            targetTransport: "Tuna",
            writerSequence: 100);
        var pause = FileTransferRouteTransitionMessage(
            "filetransfer_route_transition_pause_bound",
            transferId,
            generation,
            authorityId,
            status: "Installed",
            sourceRoute: "regular_nkn_v4_fast",
            targetRoute: "file_tuna_v4",
            targetProtocol: 4,
            targetTransport: "Tuna",
            writerSequence: 101);
        var draining = FileTransferRouteTransitionMessage(
            "filetransfer_route_transition_draining",
            transferId,
            generation,
            authorityId,
            status: "Draining",
            sourceRoute: "regular_nkn_v4_fast",
            targetRoute: "file_tuna_v4",
            targetProtocol: 4,
            targetTransport: "Tuna",
            writerSequence: 102);
        var rejectedDrain = FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_drain_receipt_decided",
                transferId,
                generation,
                authorityId,
                status: "Rejected",
                sourceRoute: "regular_nkn_v4_fast",
                targetRoute: "file_tuna_v4",
                targetProtocol: 4,
                targetTransport: "Tuna",
                writerSequence: 103)
            .Replace(
                "decision_reason=fixture",
                "decision_reason=activation_proof_mismatch",
                StringComparison.Ordinal);
        var invalidated = FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_invalidated",
                transferId,
                generation,
                authorityId,
                status: "Invalidated",
                sourceRoute: "regular_nkn_v4_fast",
                targetRoute: "file_tuna_v4",
                targetProtocol: 4,
                targetTransport: "Tuna",
                writerSequence: 105)
            .Replace(
                "decision_reason=fixture",
                "decision_reason=outbound_transfer_terminal",
                StringComparison.Ordinal);
        var disposition = FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_disposition_applied",
                transferId,
                generation,
                authorityId,
                status: "Applied",
                sourceRoute: "regular_nkn_v4_fast",
                targetRoute: "file_tuna_v4",
                targetProtocol: 4,
                targetTransport: "Tuna",
                writerSequence: 106)
            .Replace(
                "decision_reason=fixture",
                "disposition=terminalinvalidated; apply_status=applied; " +
                "decision_reason=filetransfer_route_transition_terminal_invalidated",
                StringComparison.Ordinal);
        var lines = BuildAuthorityProofFixture(
            transferId,
            $"event=filetransfer_regular_v4_frontier_repair_send_deferred; " +
            $"session_id=sess_a; transfer_id={transferId}; direction=outbound; stage=send; " +
            "repair_request_key=regular_v4_peer_silence_safety_replay:3:3:64:2; " +
            "route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=4; " +
            "runtime_generation=6; terminal_generation=0; leg_generation=5; " +
            "transport_epoch=2; bridge_recovery_generation=1; " +
            "route_publication_generation=8; route_commit_generation=0; " +
            "target_transport=regular_nkn; decision_generation=9; " +
            "scheduled_chunk_count=64; remote_next_expected_chunk_index=3; " +
            "chunks_accepted_for_transport=387; deferral_reason=repair_dispatch_not_observed; " +
            "repair_delivery_mode=control_bulk_escalated; " +
            "repair_delivery_escalation_reason=regular_v4_peer_silence_safety_replay; " +
            $"log_writer_id={writerId}; log_writer_sequence=99",
            prepared,
            pause,
            draining,
            rejectedDrain,
            $"event=filetransfer_transport_grace_exhausted; session_id=sess_a; " +
            $"transfer_id={transferId}; direction=outbound; " +
            "reason=filetransfer_route_transition_draining; " +
            $"log_writer_id={writerId}; log_writer_sequence=104",
            invalidated,
            disposition,
            $"event=file_transfer_outbound_terminal; session_id=sess_a; " +
            $"transfer_id={transferId}; state=Canceled; error_code=peer_disconnected; integrity_ok=0; " +
            $"log_writer_id={writerId}; log_writer_sequence=107");
        for (var index = 0; index < lines.Count; index++)
        {
            lines[index] = lines[index].Replace(
                "log_writer_id=writer_route_transition;",
                $"log_writer_id={writerId};",
                StringComparison.Ordinal);
        }

        var result = compact
            ? await RunAnalyzeFixtureAsync(
                lines,
                environment: new Dictionary<string, string>
                {
                    ["NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT"] = "1"
                })
            : await RunAnalyzeFixtureAsync(lines);
        Assert.True(
            result.Script.ExitCode == 0,
            result.Script.Stdout + Environment.NewLine + result.Script.Stderr);

        var verdict = ReadArtifactReport(
            result.ArtifactDir,
            "filetransfer-operator-verdict.txt");
        Assert.Equal("FAIL_PROTOCOL_OR_INTEGRITY", verdict["verdict"]);
        Assert.Equal("route_runtime", verdict["acceptance_failure_class"]);
        Assert.Equal("fail", verdict["runtime_correctness_verdict"]);
        Assert.Equal(
            "0",
            verdict["filetransfer_route_transition_incomplete_unresolved_count"]);
        Assert.Equal(
            "1",
            verdict[
                "filetransfer_route_transition_draining_transport_grace_terminalization_raw_count"]);
        Assert.Equal(
            "1",
            verdict[
                "filetransfer_route_transition_draining_transport_grace_terminalization_distinct_count"]);
        Assert.Equal(
            "0",
            verdict[
                "filetransfer_route_transition_draining_transport_grace_terminalization_recovered_count"]);
        Assert.Equal(
            "1",
            verdict[
                "filetransfer_route_transition_draining_transport_grace_terminalization_unresolved_count"]);
        Assert.Equal(
            "filetransfer_route_transition_draining_transport_grace_terminalization",
            verdict["first_proof_chain_failure_class"]);
        Assert.Equal(
            "filetransfer_transport_grace_exhausted",
            verdict["first_proof_chain_failure_event"]);
        Assert.Equal(
            "coordinator_route_transition_draining_treated_as_transport_disconnect",
            verdict["first_proof_chain_failure_reason"]);
        Assert.Equal("104", verdict["first_proof_chain_failure_writer_sequence"]);
    }

    [Theory]
    [InlineData(false, "missing_frame")]
    [InlineData(true, "missing_frame")]
    [InlineData(false, "local_proof_consumed")]
    [InlineData(true, "local_proof_consumed")]
    [InlineData(false, "payload_after_draining")]
    [InlineData(true, "payload_after_draining")]
    [InlineData(false, "owner_before_drain")]
    [InlineData(true, "owner_before_drain")]
    [InlineData(false, "old_scope_completion")]
    [InlineData(true, "old_scope_completion")]
    [InlineData(false, "frame_rejected")]
    [InlineData(true, "frame_rejected")]
    [InlineData(false, "duplicate_frame")]
    [InlineData(true, "duplicate_frame")]
    [Trait("RetainedAuthorityProofSuite", "FileTransferRouteTransition")]
    public async Task AnalyzeRetained_FallbackRouteTransition_ViolationsHaveFullCompactParity(
        bool compact,
        string violation)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = $"transfer_generic_fallback_route_transition_{violation}_{compact}";
        var messages = new List<string>
        {
            FileTransferRouteTransitionProofMessage(transferId, 21, "transition-fallback-21"),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_prepared",
                transferId,
                21,
                "transition-fallback-21"),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_pause_bound",
                transferId,
                21,
                "transition-fallback-21",
                status: "Installed"),
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_draining",
                transferId,
                21,
                "transition-fallback-21",
                status: "Draining")
        };
        if (violation == "payload_after_draining")
        {
            messages.Add(
                FileTransferRouteTransitionMessage(
                    "filetransfer_route_transition_physical_send_lease_registered",
                    transferId,
                    21,
                    "transition-fallback-21",
                    status: "Registered",
                    writerSequence: 71));
        }
        if (violation == "local_proof_consumed")
        {
            messages.Add(
                FileTransferRouteTransitionMessage(
                    "filetransfer_post_tuna_fallback_v6_route_consumed",
                    transferId,
                    21,
                    "transition-fallback-21",
                    status: "Consumed",
                    writerSequence: 72));
        }
        if (violation == "owner_before_drain")
        {
            messages.Add(
                $"event=filetransfer_route_transitioned; direction=outbound; transfer_id={transferId}; " +
                "session_id=sess_a; route=post_tuna_fallback_v6; new_route=post_tuna_fallback_v6; " +
                "protocol_version=6; runtime_generation=7; terminal_generation=0; leg_generation=6; " +
                "route_publication_generation=9; log_writer_id=writer_route_transition; " +
                "log_writer_sequence=72");
        }
        messages.Add(
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_drain_receipt_decided",
                transferId,
                21,
                "transition-fallback-21",
                status: "Accepted"));
        messages.Add(
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_committed",
                transferId,
                21,
                "transition-fallback-21",
                status: "Committed"));
        if (violation == "old_scope_completion")
        {
            messages.Add(
                FileTransferRouteTransitionMessage(
                    "filetransfer_route_transition_physical_send_lease_retired",
                    transferId,
                    21,
                    "transition-fallback-21",
                    status: "Completed",
                    writerSequence: 72));
        }
        if (violation == "frame_rejected")
        {
            messages.Add(
                FileTransferRouteTransitionMessage(
                    "filetransfer_route_transition_retained_frame_rejected",
                    transferId,
                    21,
                    "transition-fallback-21",
                    status: "Rejected",
                    writerSequence: 72));
        }
        if (violation != "missing_frame")
        {
            messages.Add(
                FileTransferRouteTransitionMessage(
                    "filetransfer_route_transition_retained_frame_applied",
                    transferId,
                    21,
                    "transition-fallback-21",
                    status: "Applied"));
        }
        if (violation == "duplicate_frame")
        {
            messages.Add(
                FileTransferRouteTransitionMessage(
                    "filetransfer_route_transition_retained_frame_applied",
                    transferId,
                    21,
                    "transition-fallback-21",
                    status: "Applied",
                    writerSequence: 72));
        }
        messages.Add(
            FileTransferRouteTransitionMessage(
                "filetransfer_route_transition_disposition_applied",
                transferId,
                21,
                "transition-fallback-21",
                status: "Committed",
                writerSequence: 73));

        var lines = BuildFileTunaV4DataPathProofFixture(transferId, messages.ToArray());
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
        Assert.Equal("1", verdict["filetransfer_route_transition_incomplete_raw_count"]);
        Assert.Equal("1", verdict["filetransfer_route_transition_incomplete_distinct_count"]);
        Assert.Equal("1", verdict["filetransfer_route_transition_incomplete_unresolved_count"]);
        Assert.Equal("fail", verdict["filetransfer_route_transition_verdict"]);
        Assert.Equal("filetransfer_route_transition_incomplete", verdict["first_proof_chain_failure_class"]);
        Assert.Equal(
            violation switch
            {
                "missing_frame" => "fallback_route_transition_retained_frame_not_applied",
                "local_proof_consumed" => "fallback_peer_proof_consumed_without_coordinator_transition_commit",
                "owner_before_drain" => "successor_owner_or_publication_installed_before_route_transition_drain",
                "old_scope_completion" => "old_scope_physical_send_completed_after_successor_owner_install",
                "frame_rejected" => "fallback_route_transition_retained_frame_rejected",
                "duplicate_frame" => "fallback_route_transition_retained_frame_applied_more_than_once",
                _ => "source_payload_lease_registered_after_route_transition_draining"
            },
            verdict["first_proof_chain_failure_reason"]);
    }

    [Theory]
    [InlineData("runtime_generation")]
    [InlineData("range")]
    [Trait("RetainedAuthorityProofSuite", "FileTunaV4DataPathProof")]
    public async Task AnalyzeRetained_FileTunaV4ReceiveSideFailClosedCompanionRequiresCompleteEvidence(
        string omittedCompanionEvidence)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var transferId = $"transfer_file_tuna_v4_receive_incomplete_{omittedCompanionEvidence}";
        var lines = BuildFileTunaV4RejectedReceiveFixture(transferId, omittedCompanionEvidence);

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
            Assert.Equal("file_tuna_data_path_mismatch", verdict["acceptance_failure_class"]);
            Assert.Equal("file_tuna_data_path_mismatch", verdict["recovery_failure_class"]);
            Assert.Equal("file_tuna_data_path_mismatch", verdict["first_proof_chain_failure_class"]);
        }
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
                proofDirection: "send",
                startChunkIndex: 0));

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
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                startChunkIndex: 0));

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
    public async Task AnalyzeRetained_FileTunaV4RepairPeerVisibleProofCannotSatisfyDuplicateRepairIntents_Fails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string transferId = "transfer_file_tuna_v4_repair_reused_proof_fail";
        var lines = BuildFileTunaV4DataPathProofFixture(
            transferId,
            FileTunaV4DataPathRepairIntentMessage(transferId, sentChunkCount: 10),
            FileTunaV4DataPathRepairIntentMessage(transferId, sentChunkCount: 10),
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                payloadBytes: 327680,
                rawPayloadBytes: 327680,
                batchChunkCount: 10,
                startChunkIndex: 0));

        var result = await RunAnalyzeFixtureAsync(lines);

        var route = ReadArtifactReport(result.ArtifactDir, "filetransfer-route-consistency-summary.txt");
        Assert.Equal("fail", route["file_tuna_v4_data_path_proof_verdict"]);
        Assert.Equal("2", route["file_tuna_v4_intended_send_count"]);
        Assert.Equal("1", route["file_tuna_v4_tuna_send_result_count"]);
        Assert.Equal("1", route["file_tuna_v4_data_path_missing_actual_proof_count"]);
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

    private static List<string> BuildFileTunaV4RejectedReceiveFixture(
        string transferId,
        string? omittedCompanionEvidence = null,
        bool includeOutboundTransition = true)
    {
        const string sourceWriterId = "writer_source_regular";
        const string writerId = "writer_receive_regular_rejected";
        var sourceSend =
            $"event=filetransfer_data_path_send_result; direction=outbound; transfer_id={transferId}; session_id=sess_a; " +
            "frame_type=filetransfer.chunk_batch.v4; lane=bulk; actual_transport=regular_nkn; delivery_transport=regular_nkn; " +
            "payload_bytes=65536; raw_payload_bytes=65536; batch_chunk_count=2; batch_profile=v4_batch; " +
            "delivery_result=sent; chunk_index=0-1; start_chunk_index=0; last_end_chunk_exclusive=2; frame_chunk_count=2; " +
            "route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; terminal_generation=0; " +
            "leg_generation=1; route_publication_generation=1; route_commit_generation=0; target_route=regular_nkn_v4_fast; " +
            "target_protocol_version=4; target_transport=regular_nkn; intended_target_transport=regular_nkn; " +
            $"log_writer_id={sourceWriterId}; log_writer_sequence=1";
        var outboundTransition =
            $"event=filetransfer_route_transitioned; direction=outbound; transfer_id={transferId}; session_id=sess_a; " +
            "route=file_tuna_v4; protocol_version=4; previous_route=regular_nkn_v4_fast; new_route=file_tuna_v4; " +
            "live_route_epoch=3; runtime_generation=7; terminal_generation=0; leg_generation=4; " +
            "route_publication_generation=9; route_commit_generation=0; target_route=file_tuna_v4; " +
            $"target_protocol_version=4; target_transport=tuna; log_writer_id={sourceWriterId}; log_writer_sequence=2";
        var inboundTransition =
            $"event=filetransfer_route_transitioned; direction=inbound; transfer_id={transferId}; session_id=sess_a; " +
            "route=file_tuna_v4; protocol_version=4; previous_route=regular_nkn_v4_fast; new_route=file_tuna_v4; " +
            "live_route_epoch=3; runtime_generation=7; terminal_generation=0; leg_generation=4; " +
            "route_publication_generation=9; route_commit_generation=0; target_route=file_tuna_v4; " +
            $"target_protocol_version=4; target_transport=tuna; log_writer_id={writerId}; log_writer_sequence=9";
        var receive = FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "regular_nkn",
                eventName: "filetransfer_data_path_receive_result",
                proofDirection: "receive",
                startChunkIndex: 0)
            + $"; log_writer_id={writerId}; log_writer_sequence=10";
        var mismatch = FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "regular_nkn",
                eventName: "filetransfer_tuna_data_path_mismatch",
                proofDirection: "receive",
                startChunkIndex: 0)
            + $"; mismatch_reason=file_tuna_v4_resolved_to_regular_nkn; log_writer_id={writerId}; log_writer_sequence=14";
        if (omittedCompanionEvidence == "runtime_generation")
        {
            receive = receive.Replace("runtime_generation=7; ", string.Empty, StringComparison.Ordinal);
            mismatch = mismatch.Replace("runtime_generation=7; ", string.Empty, StringComparison.Ordinal);
        }
        else if (omittedCompanionEvidence == "range")
        {
            const string range = "; chunk_index=0-1; start_chunk_index=0; last_end_chunk_exclusive=2; frame_chunk_count=2";
            receive = receive.Replace(range, string.Empty, StringComparison.Ordinal);
            mismatch = mismatch.Replace(range, string.Empty, StringComparison.Ordinal);
        }

        var ignored =
            $"event=filetransfer_data_frame_ignored; transport=nkn; transfer_id={transferId}; session_id=sess_a; " +
            "frame_type=filetransfer.chunk_batch.v4; chunk_index=0-1; lane=bulk; " +
            "reason=file_tuna_v4_payload_requires_tuna; actual_transport=regular_nkn; target_transport=tuna; " +
            $"route=file_tuna_v4; protocol_version=4; log_writer_id={writerId}; log_writer_sequence=21";
        var messages = new List<string>
        {
            sourceSend
        };
        if (includeOutboundTransition)
        {
            messages.Add(outboundTransition);
        }
        messages.AddRange(
        [
            inboundTransition,
            FileTunaV4DataPathIntentMessage(transferId),
            receive,
            mismatch,
            ignored,
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                startChunkIndex: 0)
        ]);
        var lines = BuildFileTunaV4DataPathProofFixture(transferId, messages.ToArray());
        var sourceLineIndex = lines.FindIndex(line => line.Contains(sourceSend, StringComparison.Ordinal));
        Assert.True(sourceLineIndex >= 0);
        lines[sourceLineIndex] = "\uFEFF" + lines[sourceLineIndex];
        return lines;
    }

    private static List<string> BuildRouteCommitSendDrainLegacyFailureFixture(string transferId)
    {
        const string sourceWriterId = "writer_route_commit_source";
        const string receiverWriterId = "writer_route_commit_receiver";
        var messages = new List<string>
        {
            $"event=filetransfer_leg_started; direction=outbound; transfer_id={transferId}; session_id=sess_a; " +
            "leg_id=leg:2; leg_generation=2; route=file_tuna_v4; protocol_version=4; live_route_epoch=1; " +
            "transport_epoch=0; state=active; reason=precommit_probe_commit_confirmed; can_send_data=1; " +
            $"log_writer_id={sourceWriterId}; log_writer_sequence=100",
            $"event=filetransfer_runtime_owner_workers_retired; direction=outbound; transfer_id={transferId}; " +
            "session_id=sess_a; retired_runtime_generation=1; current_runtime_generation=2; " +
            "retired_route=regular_nkn_v4_fast; current_route=file_tuna_v4; " +
            $"reason=precommit_probe_commit_confirmed; log_writer_id={sourceWriterId}; log_writer_sequence=101",
            LegacyRegularV4PhysicalSendMessage(transferId, sourceWriterId, writerSequence: 102, startChunkIndex: 1173),
            $"event=filetransfer_route_transitioned; direction=outbound; transfer_id={transferId}; session_id=sess_a; " +
            "route=file_tuna_v4; protocol_version=4; previous_route=regular_nkn_v4_fast; new_route=file_tuna_v4; " +
            "previous_protocol_version=4; new_protocol_version=4; handoff_kind=normal_to_tuna_activation; " +
            "live_route_epoch=1; runtime_generation=2; terminal_generation=0; leg_generation=2; " +
            "route_publication_generation=3; route_commit_generation=1; target_route=file_tuna_v4; " +
            "target_protocol_version=4; target_transport=tuna; route_publication_action_kind=RouteCommitPublication; " +
            $"coordinator_decision_reason=accepted; log_writer_id={sourceWriterId}; log_writer_sequence=103",
            LegacyRegularV4PhysicalSendMessage(transferId, sourceWriterId, writerSequence: 104, startChunkIndex: 1175),
            LegacyRegularV4PhysicalSendMessage(transferId, sourceWriterId, writerSequence: 105, startChunkIndex: 60),
            LegacyRegularV4PhysicalSendMessage(transferId, sourceWriterId, writerSequence: 106, startChunkIndex: 62),
            LegacyRegularV4PhysicalSendMessage(transferId, sourceWriterId, writerSequence: 107, startChunkIndex: 64),
            LegacyRegularV4PhysicalSendMessage(transferId, sourceWriterId, writerSequence: 108, startChunkIndex: 66),
            $"event=filetransfer_route_transitioned; direction=inbound; transfer_id={transferId}; session_id=sess_a; " +
            "route=file_tuna_v4; protocol_version=4; previous_route=regular_nkn_v4_fast; new_route=file_tuna_v4; " +
            "live_route_epoch=3; runtime_generation=7; terminal_generation=0; leg_generation=4; " +
            "route_publication_generation=9; route_commit_generation=0; target_route=file_tuna_v4; " +
            $"target_protocol_version=4; target_transport=tuna; log_writer_id={receiverWriterId}; log_writer_sequence=10",
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "regular_nkn",
                eventName: "filetransfer_data_path_receive_result",
                proofDirection: "receive",
                startChunkIndex: 1175).Replace("direction=outbound", "direction=inbound", StringComparison.Ordinal) +
            $"; log_writer_id={receiverWriterId}; log_writer_sequence=11",
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "regular_nkn",
                eventName: "filetransfer_tuna_data_path_mismatch",
                proofDirection: "receive",
                startChunkIndex: 1175).Replace("direction=outbound", "direction=inbound", StringComparison.Ordinal) +
            $"; mismatch_reason=file_tuna_v4_resolved_to_regular_nkn; log_writer_id={receiverWriterId}; log_writer_sequence=12",
            $"event=filetransfer_data_frame_ignored; transfer_id={transferId}; session_id=sess_a; " +
            "frame_type=filetransfer.chunk_batch.v4; chunk_index=1175-1176; start_chunk_index=1175; " +
            "last_end_chunk_exclusive=1177; lane=bulk; reason=file_tuna_v4_payload_requires_tuna; " +
            "actual_transport=regular_nkn; target_transport=tuna; route=file_tuna_v4; protocol_version=4; " +
            $"log_writer_id={receiverWriterId}; log_writer_sequence=13",
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                startChunkIndex: 0)
        };

        return BuildFileTunaV4DataPathProofFixture(transferId, messages.ToArray());
    }

    private static List<string> BuildRouteCommitSendDrainSafeLateDeliveryFixture(
        string transferId,
        bool mismatchRange)
    {
        const string sourceWriterId = "writer_route_commit_safe_source";
        const string receiverWriterId = "writer_route_commit_safe_receiver";
        const int sourceStart = 20;
        var mismatchStart = mismatchRange ? sourceStart + 2 : sourceStart;
        var messages = new List<string>
        {
            LegacyRegularV4PhysicalSendMessage(transferId, sourceWriterId, writerSequence: 99, startChunkIndex: sourceStart),
            $"event=filetransfer_leg_started; direction=outbound; transfer_id={transferId}; session_id=sess_a; " +
            "leg_id=leg:2; leg_generation=2; route=file_tuna_v4; protocol_version=4; live_route_epoch=1; " +
            "transport_epoch=0; state=active; reason=precommit_probe_commit_confirmed; can_send_data=1; " +
            $"log_writer_id={sourceWriterId}; log_writer_sequence=100",
            $"event=filetransfer_runtime_owner_workers_retired; direction=outbound; transfer_id={transferId}; " +
            "session_id=sess_a; retired_runtime_generation=1; current_runtime_generation=2; " +
            "retired_route=regular_nkn_v4_fast; current_route=file_tuna_v4; " +
            $"reason=precommit_probe_commit_confirmed; log_writer_id={sourceWriterId}; log_writer_sequence=101",
            $"event=filetransfer_route_transitioned; direction=outbound; transfer_id={transferId}; session_id=sess_a; " +
            "route=file_tuna_v4; protocol_version=4; previous_route=regular_nkn_v4_fast; new_route=file_tuna_v4; " +
            "previous_protocol_version=4; new_protocol_version=4; handoff_kind=normal_to_tuna_activation; " +
            "live_route_epoch=1; runtime_generation=2; terminal_generation=0; leg_generation=2; " +
            "route_publication_generation=3; route_commit_generation=1; target_route=file_tuna_v4; " +
            "target_protocol_version=4; target_transport=tuna; route_publication_action_kind=RouteCommitPublication; " +
            $"coordinator_decision_reason=accepted; log_writer_id={sourceWriterId}; log_writer_sequence=102",
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "regular_nkn",
                eventName: "filetransfer_data_path_receive_result",
                proofDirection: "receive",
                startChunkIndex: sourceStart).Replace("direction=outbound", "direction=inbound", StringComparison.Ordinal) +
            $"; log_writer_id={receiverWriterId}; log_writer_sequence=11",
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "regular_nkn",
                eventName: "filetransfer_tuna_data_path_mismatch",
                proofDirection: "receive",
                startChunkIndex: mismatchStart).Replace("direction=outbound", "direction=inbound", StringComparison.Ordinal) +
            $"; mismatch_reason=file_tuna_v4_resolved_to_regular_nkn; log_writer_id={receiverWriterId}; log_writer_sequence=12",
            $"event=filetransfer_data_frame_ignored; transfer_id={transferId}; session_id=sess_a; " +
            $"frame_type=filetransfer.chunk_batch.v4; chunk_index={mismatchStart}-{mismatchStart + 1}; " +
            $"start_chunk_index={mismatchStart}; last_end_chunk_exclusive={mismatchStart + 2}; lane=bulk; " +
            "reason=file_tuna_v4_payload_requires_tuna; actual_transport=regular_nkn; target_transport=tuna; " +
            $"route=file_tuna_v4; protocol_version=4; log_writer_id={receiverWriterId}; log_writer_sequence=13",
            FileTunaV4DataPathActualMessage(
                transferId,
                actualTransport: "tuna",
                eventName: "filetransfer_tuna_data_path_peer_visible",
                proofDirection: "send",
                startChunkIndex: 0)
        };

        return BuildFileTunaV4DataPathProofFixture(transferId, messages.ToArray());
    }

    private static string LegacyRegularV4PhysicalSendMessage(
        string transferId,
        string writerId,
        int writerSequence,
        int startChunkIndex)
        => $"event=filetransfer_data_path_send_result; direction=outbound; transfer_id={transferId}; session_id=sess_a; " +
           "frame_type=filetransfer.chunk_batch.v4; lane=bulk; actual_transport=regular_nkn; delivery_transport=regular_nkn; " +
           "payload_bytes=65536; raw_payload_bytes=65536; batch_chunk_count=2; batch_profile=v4_batch; " +
           $"delivery_result=sent; chunk_index={startChunkIndex}-{startChunkIndex + 1}; " +
           $"start_chunk_index={startChunkIndex}; last_end_chunk_exclusive={startChunkIndex + 2}; frame_chunk_count=2; " +
           "route=regular_nkn_v4_fast; protocol_version=4; live_route_epoch=0; runtime_generation=1; " +
           "terminal_generation=0; leg_generation=1; route_publication_generation=1; route_commit_generation=0; " +
           "target_route=regular_nkn_v4_fast; target_protocol_version=4; target_transport=regular_nkn; " +
           "intended_target_transport=regular_nkn; route_publication_action_kind=RoutePublication; " +
           $"coordinator_decision_reason=new_transfer; log_writer_id={writerId}; log_writer_sequence={writerSequence}";

    private static string RouteCommitSendDrainMessage(
        string eventName,
        string transferId,
        string probeId = "probe-1",
        string probeState = "Acked",
        int? capturedSendCount = null,
        int? matchingRetiredSendCount = null,
        int activeSendCount = 0,
        string decisionReason = "fixture",
        int? writerSequence = null)
        => $"event={eventName}; direction=outbound; transfer_id={transferId}; session_id=sess_a; drain_generation=1; " +
           "source_route=regular_nkn_v4_fast; source_protocol_version=4; source_live_route_epoch=0; " +
           "source_runtime_generation=1; source_terminal_generation=0; source_leg_generation=1; " +
           "source_transport_epoch=0; source_bridge_recovery_generation=0; source_route_publication_generation=1; " +
           "source_route_commit_generation=0; source_data_session_generation=1; transaction_generation=1; " +
           $"offer_generation=1; tuna_path_lease_generation=1; tuna_path_lease_listener_run_id=listener-1; probe_id={probeId}; probe_state={probeState}; target_route=file_tuna_v4; " +
           "target_protocol_version=4; target_transport=tuna; handoff_kind=normal_to_tuna_activation; registered_send_count=1; retired_send_count=1; " +
           $"active_send_count={activeSendCount}; decision_reason={decisionReason}" +
           (capturedSendCount.HasValue ? $"; captured_send_count={capturedSendCount.Value}" : string.Empty) +
           (matchingRetiredSendCount.HasValue ? $"; matching_retired_send_count={matchingRetiredSendCount.Value}" : string.Empty) +
           (writerSequence.HasValue ? $"; log_writer_sequence={writerSequence.Value}" : string.Empty);

    private static string RouteCommitDrainTransitionMessage(string transferId, int completedDrainGeneration)
        => $"event=filetransfer_route_transitioned; direction=outbound; transfer_id={transferId}; session_id=sess_a; " +
           "route=file_tuna_v4; protocol_version=4; previous_route=regular_nkn_v4_fast; new_route=file_tuna_v4; " +
           "previous_protocol_version=4; new_protocol_version=4; handoff_kind=normal_to_tuna_activation; " +
           "live_route_epoch=1; runtime_generation=2; terminal_generation=0; leg_generation=2; " +
           "route_publication_generation=3; route_commit_generation=1; target_route=file_tuna_v4; " +
           "target_protocol_version=4; target_transport=tuna; route_publication_action_kind=RouteCommitPublication; " +
           $"coordinator_decision_reason=accepted; completed_send_drain_generation={completedDrainGeneration}";

    private static string RegularV4ToTunaTransitionMessage(
        string eventName,
        string transferId,
        string? status = null,
        int? writerSequence = null)
    {
        status ??= eventName switch
        {
            "filetransfer_regular_v4_to_tuna_route_transition_draining" => "Draining",
            "filetransfer_regular_v4_to_tuna_route_transition_committed" => "Committed",
            "filetransfer_regular_v4_to_tuna_route_transition_invalidated" => "Invalidated",
            _ => "Prepared"
        };
        return $"event={eventName}; direction=outbound; transfer_id={transferId}; session_id=sess_a; " +
               "transition_generation=3; authority_id=transition-authority-3; " +
               "source_route=regular_nkn_v4_fast; source_protocol_version=4; source_live_route_epoch=1; " +
               "source_runtime_generation=4; source_terminal_generation=0; source_leg_generation=2; " +
               "source_transport_epoch=1; source_bridge_recovery_generation=1; " +
               "source_route_publication_generation=6; source_data_session_generation=3; " +
               "transaction_generation=7; offer_generation=8; launch_generation=9; " +
               "tuna_path_lease_generation=10; checkpoint_generation=3; checkpoint_state_epoch=12; " +
               "checkpoint_contiguous_frontier=24; checkpoint_durable_highest=31; " +
               "target_route=FileTunaV4; target_protocol_version=4; " +
               "target_transport=tuna; probe_state=Acked; captured_send_count=2; completed_send_count=2; " +
               "captured_send_fingerprint=lease-fingerprint-3; completed_send_fingerprint=lease-fingerprint-3; " +
               "target_runtime_generation=5; target_leg_generation=3; target_route_publication_generation=7; " +
               $"outcome=Completed; status={status}; decision_reason=fixture" +
               (writerSequence.HasValue ? $"; log_writer_sequence={writerSequence.Value}" : string.Empty);
    }

    private static string RegularV4ToTunaPrecommitAckMessage(string transferId)
        => $"event=runtime_unlock_precommit_probe_acked; direction=outbound; transfer_id={transferId}; " +
           "session_id=sess_a; transaction_generation=7; offer_generation=8; launch_generation=9; " +
           "tuna_path_lease_generation=10; probe_state=Acked; decision_reason=fixture";

    private static string RegularV4ToTunaPhysicalSendLeaseMessage(string transferId, int writerSequence)
        => $"event=filetransfer_regular_v4_physical_send_lease_registered; direction=outbound; " +
           $"transfer_id={transferId}; session_id=sess_a; transition_generation=3; send_lease_generation=11; " +
           "source_route=regular_nkn_v4_fast; source_protocol_version=4; source_live_route_epoch=1; " +
           "source_runtime_generation=4; source_terminal_generation=0; source_leg_generation=2; " +
           "source_transport_epoch=1; source_bridge_recovery_generation=1; " +
           "source_route_publication_generation=6; source_data_session_generation=3; " +
           $"start_chunk_index=24; chunk_count=4; repair_send=0; repair_request_key=(none); log_writer_sequence={writerSequence}";

    private static string RegularV4ToTunaAtomicRouteTransitionMessage(string transferId)
        => $"event=filetransfer_route_transitioned; direction=outbound; transfer_id={transferId}; session_id=sess_a; " +
           "transition_generation=3; authority_id=transition-authority-3; route=file_tuna_v4; protocol_version=4; " +
           "previous_route=regular_nkn_v4_fast; new_route=file_tuna_v4; previous_protocol_version=4; " +
           "new_protocol_version=4; handoff_kind=normal_to_tuna_activation; live_route_epoch=2; " +
           "runtime_generation=5; terminal_generation=0; leg_generation=3; route_publication_generation=7; " +
           "route_commit_generation=1; target_route=file_tuna_v4; target_protocol_version=4; " +
           "target_transport=tuna; route_publication_action_kind=RouteCommitPublication; " +
           "coordinator_decision_reason=regular_v4_to_tuna_transition_committed; completed_send_drain_generation=3";

    private static string FileTransferRouteTransitionProofMessage(
        string transferId,
        int operationGeneration,
        string authorityId)
        => FileTransferRouteTransitionMessage(
            "filetransfer_route_transition_peer_proof_observed",
            transferId,
            operationGeneration,
            authorityId,
            status: "Observed");

    private static string FileTransferRouteTransitionMessage(
        string eventName,
        string transferId,
        int operationGeneration,
        string authorityId,
        string? status = null,
        string sourceRoute = "file_tuna_v4",
        int sourceProtocol = 4,
        string targetRoute = "post_tuna_fallback_v6",
        int targetProtocol = 6,
        string targetTransport = "RegularNkn",
        int? writerSequence = null)
    {
        status ??= eventName switch
        {
            "filetransfer_route_transition_prepared" => "Prepared",
            "filetransfer_route_transition_pause_bound" => "Installed",
            "filetransfer_route_transition_draining" => "Draining",
            "filetransfer_route_transition_drain_receipt_decided" => "Accepted",
            "filetransfer_route_transition_committed" => "Committed",
            "filetransfer_route_transition_invalidated" => "Invalidated",
            "filetransfer_route_transition_retained_frame_applied" => "Applied",
            _ => "Accepted"
        };
        var transitionKind = sourceRoute switch
        {
            "post_tuna_fallback_v6" => "PostTunaFallbackToTuna",
            "regular_nkn_v4_fast" when targetRoute == "post_tuna_fallback_v6" => "UncommittedTunaToPostTunaFallback",
            "regular_nkn_v4_fast" => "RegularV4ToTuna",
            _ => "TunaToPostTunaFallback"
        };
        return $"event={eventName}; direction=outbound; transfer_id={transferId}; session_id=sess_a; " +
               $"operation_generation={operationGeneration}; transition_generation={operationGeneration}; " +
               $"authority_id={authorityId}; predecessor_authority_id=(none); transition_kind={transitionKind}; " +
               $"source_route={sourceRoute}; source_protocol_version={sourceProtocol}; " +
               $"source_transport={(sourceRoute == "file_tuna_v4" ? "tuna" : "regular_nkn")}; " +
               "source_live_route_epoch=4; " +
               "source_runtime_generation=6; source_route_publication_generation=8; source_route_commit_generation=0; " +
               "source_terminal_generation=0; " +
               "source_leg_generation=5; source_transport_epoch=2; source_bridge_recovery_generation=1; " +
               "source_data_session_generation=3; peer_proof_generation=4; peer_proof_transport_epoch=3; " +
               "peer_proof_kind=post_tuna_fallback_nkn; retained_frame_type=filetransfer.state.v6; " +
               "retained_frame_transport_epoch=3; retained_frame_fingerprint=frame-hash-1; " +
               "proof_generation=4; proof_transport_epoch=3; proof_kind=post_tuna_fallback_nkn; " +
               "frame_type=filetransfer.state.v6; frame_transport_epoch=3; frame_fingerprint=frame-hash-1; " +
               $"target_route={targetRoute}; target_protocol_version={targetProtocol}; target_transport={targetTransport}; " +
               "captured_send_count=0; completed_send_count=0; captured_lease_fingerprint=empty-set; " +
               "completed_lease_fingerprint=empty-set; outcome=Completed; " +
               $"status={status}; decision_reason=fixture" +
               (writerSequence.HasValue
                   ? $"; log_writer_id=writer_route_transition; log_writer_sequence={writerSequence.Value}"
                   : string.Empty);
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

    private static string FileTunaV4DataPathRepairIntentMessage(string transferId, int sentChunkCount, int startChunkIndex = 0)
        => $"event=filetransfer_v4_repair_sent; {FileTunaV4DataPathAuthoritySuffix(transferId)}; repair_request_key=repair-1; protocol_repair_request_id=repair-protocol-1; range_count=1; requested_chunk_count={sentChunkCount}; sent_chunk_count={sentChunkCount}; sent_chunk_indices={startChunkIndex}-{startChunkIndex + sentChunkCount - 1}; transport_sent_chunk_count={sentChunkCount}; repair_batch_send_attempt_count=1; repair_delivery_mode=bulk_only; repair_delivery_escalation_reason=none; first_start_chunk_index={startChunkIndex}; last_end_chunk_exclusive={startChunkIndex + sentChunkCount}; frontier_tail_repair=0; credit_exhausted_time_ms_at_repair=0; remote_next_expected_chunk_index=0; chunks_accepted_for_transport={sentChunkCount}; skipped_obsolete_count=0; skipped_future_count=0; skipped_out_of_bounds_count=0; receiver_proof_status=pending";

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
