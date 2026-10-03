using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(58)]
    [InlineData(119)]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_ColdStartupGetsOneSeparateHandshakeDeadline(int startupSeconds)
    {
        var f = CreateSessionPreparationFixture();
        var eligible = FileTransferCoordinator.RetireSessionPreparationEpisode(f.State, f.Permit).State;
        var startup = FileTransferCoordinator.BeginSessionPreparation(eligible, f.Permit.Scope, "cold", 2, true,
            f.Now.AddSeconds(120), f.Now, listenerStartup: true);
        var permit = startup.Permit!;
        Assert.Equal(FileTransferSessionPreparationPhase.Starting, startup.State.Phase);
        Assert.Null(startup.State.Handshake);
        Assert.Null(FileTransferCoordinator.CompleteSessionPreparation(startup.State, permit, f.Proof, f.Now).State.Readiness);
        var startupDuplicate = FileTransferCoordinator.BeginSessionPreparation(startup.State, permit.Scope,
            permit.Nonce, permit.PayerDecisionId, true, permit.DeadlineUtc, f.Now.AddSeconds(1), listenerStartup: true);
        Assert.Equal(FileTransferSessionPreparationStatus.Idempotent, startupDuplicate.Status);
        Assert.Same(startup.State, startupDuplicate.State);
        var now = f.Now.AddSeconds(startupSeconds);
        var lease = f.Lease with { SnapshotGeneration = permit.Generation, ObservedUtc = now };
        var handshake = FileTransferCoordinator.BeginSessionPreparationHandshake(startup.State, permit, lease, now);
        Assert.Equal(FileTransferSessionPreparationStatus.Accepted, handshake.Status);
        Assert.Same(permit, handshake.State.Handshake!.Permit);
        Assert.InRange((handshake.State.Handshake.DeadlineUtc - now).TotalSeconds, 59.999, 60);
        Assert.Null(handshake.State.Readiness);
        Assert.Null(handshake.State.ClaimedEvidence);
        var duplicate = FileTransferCoordinator.BeginSessionPreparationHandshake(handshake.State, permit, lease, now.AddSeconds(1));
        Assert.Equal(FileTransferSessionPreparationStatus.Idempotent, duplicate.Status);
        Assert.Same(handshake.State, duplicate.State);
        var proof = f.Proof with { Nonce = permit.Nonce, PayerDecisionId = permit.PayerDecisionId,
            SnapshotGeneration = permit.Generation, ObservedUtc = now.AddSeconds(20) };
        var ready = FileTransferCoordinator.CompleteSessionPreparation(handshake.State, permit, proof, proof.ObservedUtc);
        Assert.Equal(FileTransferSessionPreparationStatus.Accepted, ready.Status);
        Assert.Null(ready.State.ClaimedEvidence);
        Assert.False(FileTransferCoordinator.IsSessionPreparationPermitCurrent(handshake.State, permit, handshake.State.Handshake.DeadlineUtc));
        Assert.NotEqual(FileTransferSessionPreparationStatus.Accepted,
            FileTransferCoordinator.BeginSessionPreparationHandshake(handshake.State, permit, lease, handshake.State.Handshake.DeadlineUtc).Status);
    }

    [Theory]
    [InlineData("copied_permit")]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("admission")]
    [InlineData("missing_lease")]
    [InlineData("scope")]
    [InlineData("snapshot")]
    [InlineData("generation")]
    [InlineData("run")]
    [InlineData("lease_lost")]
    [InlineData("listener_unready")]
    [InlineData("no_file_lane")]
    [InlineData("old_capture")]
    [InlineData("future_capture")]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_StartupCannotBorrowOrRenewHandshake(string mutation)
    {
        var f = CreateSessionPreparationFixture();
        var eligible = FileTransferCoordinator.RetireSessionPreparationEpisode(f.State, f.Permit).State;
        var startup = FileTransferCoordinator.BeginSessionPreparation(eligible, f.Permit.Scope, "cold", 2, true,
            f.Now.AddSeconds(120), f.Now, listenerStartup: true);
        var permit = startup.Permit!;
        var now = f.Now.AddSeconds(58);
        var lease = f.Lease with { SnapshotGeneration = permit.Generation, ObservedUtc = now };
        lease = mutation switch
        {
            "scope" => lease with { Scope = lease.Scope with { SessionId = "wrong" } },
            "snapshot" => lease with { SnapshotGeneration = 0 },
            "generation" => lease with { LeaseGeneration = 0 },
            "run" => lease with { ListenerRunId = "" },
            "lease_lost" => lease with { LeaseCurrent = false },
            "listener_unready" => lease with { ListenerReady = false },
            "no_file_lane" => lease with { FileLaneNegotiated = false },
            "old_capture" => lease with { ObservedUtc = f.Now.AddTicks(-1) },
            "future_capture" => lease with { ObservedUtc = now.AddTicks(1) },
            _ => lease,
        };
        var state = mutation switch
        {
            "revoked" => FileTransferCoordinator.RevokeSessionPreparationScope(startup.State, permit.Scope).State,
            "admission" => FileTransferCoordinator.ReserveSessionPreparationAdmission(startup.State, permit.Scope,
                "real-transfer", FileTransferDirection.Outbound, now).State,
            _ => startup.State,
        };
        var rejected = FileTransferCoordinator.BeginSessionPreparationHandshake(state,
            mutation == "copied_permit" ? permit with { } : permit, mutation == "missing_lease" ? null : lease,
            mutation == "expired" ? permit.DeadlineUtc : now);
        Assert.NotEqual(FileTransferSessionPreparationStatus.Accepted, rejected.Status);
        Assert.Same(state, rejected.State);
        Assert.Null(rejected.State.Handshake);
        Assert.Null(rejected.State.Readiness);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_StartupAndHandshakeRemainApprovalBoundAndLeaseBound()
    {
        var f = CreateSessionPreparationFixture();
        var eligible = FileTransferCoordinator.RetireSessionPreparationEpisode(f.State, f.Permit).State;
        var scope = eligible.Scope! with { ApprovalExpiresUtc = f.Now.AddSeconds(130) };
        eligible = eligible with { Scope = scope };
        Assert.Null(FileTransferCoordinator.BeginSessionPreparation(eligible, scope, "cold", 2, false,
            f.Now.AddSeconds(120), f.Now, listenerStartup: true).Permit);
        Assert.Null(FileTransferCoordinator.BeginSessionPreparation(eligible, scope, "cold", 2, true,
            f.Now.AddSeconds(121), f.Now, listenerStartup: true).Permit);
        var startup = FileTransferCoordinator.BeginSessionPreparation(eligible, scope, "cold", 2, true,
            f.Now.AddSeconds(120), f.Now, listenerStartup: true);
        var permit = startup.Permit!;
        var now = f.Now.AddSeconds(119);
        var handshake = FileTransferCoordinator.BeginSessionPreparationHandshake(startup.State, permit,
            f.Lease with { Scope = scope, SnapshotGeneration = permit.Generation, ObservedUtc = now }, now);
        Assert.Equal(scope.ApprovalExpiresUtc.ToUnixTimeMilliseconds(), handshake.State.Handshake!.DeadlineUtc.ToUnixTimeMilliseconds());
        var completedProof = f.Proof with { ObservedUtc = now.AddSeconds(1) };
        foreach (var proof in new[]
        {
            completedProof with { ListenerRunId = "replacement" },
            completedProof with { LeaseGeneration = 2 },
            completedProof with { ObservedUtc = now.AddTicks(-1) },
        })
        {
            Assert.Null(FileTransferCoordinator.CompleteSessionPreparation(handshake.State, permit,
                proof with { Nonce = permit.Nonce, PayerDecisionId = permit.PayerDecisionId, SnapshotGeneration = permit.Generation },
                now.AddSeconds(1)).State.Readiness);
        }
    }

    [Theory]
    [InlineData("eligible", true)]
    [InlineData("pending", true)]
    [InlineData("ready", true)]
    [InlineData("expired", false)]
    [InlineData("admission", false)]
    [InlineData("revoked", false)]
    [InlineData("wrong_session", false)]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparationRequest")]
    public void SessionPreparation_RequestSelectionPreservesEpisodeAndClosedFence(string phase, bool allowed)
    {
        var f = CreateSessionPreparationFixture(true, ready: phase == "ready");
        var state = f.State;
        if (phase == "eligible") state = FileTransferCoordinator.RetireSessionPreparationEpisode(state, f.Permit).State;
        if (phase == "admission") state = FileTransferCoordinator.ReserveSessionPreparationAdmission(state, f.Permit.Scope,
            "first", FileTransferDirection.Outbound, f.Now).State;
        if (phase == "revoked") state = FileTransferCoordinator.RevokeSessionPreparationScope(state, f.Permit.Scope).State;
        var selection = FileTransferCoordinator.SelectSessionPreparationRequest(state,
            phase == "wrong_session" ? "other" : f.Permit.Scope.SessionId, null,
            phase == "expired" ? f.Permit.DeadlineUtc.AddSeconds(1) : f.Now);
        Assert.Equal(allowed ? FileTransferAccelerationRequestPurpose.SessionPreparation : FileTransferAccelerationRequestPurpose.Unavailable,
            selection.Purpose);
        if (phase is "pending" or "ready") Assert.Same(f.Permit, selection.Permit);
        else Assert.Null(selection.Permit);
    }

    [Theory]
    [InlineData(true, FileTransferDirection.Outbound)]
    [InlineData(true, FileTransferDirection.Inbound)]
    [InlineData(false, FileTransferDirection.Outbound)]
    [InlineData(false, FileTransferDirection.Inbound)]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_CompletedEvidenceNeedsRealTransferAdmission(bool localIsPayer, FileTransferDirection direction)
    {
        var f = CreateSessionPreparationFixture(localIsPayer, ready: true);
        var before = f.State;
        foreach (var observedDirection in new[] { FileTransferDirection.Outbound, FileTransferDirection.Inbound })
        {
            var view = FileTransferCoordinator.ObserveSessionPreparation(f.State, f.Permit.Scope, observedDirection, f.Lease, f.Now, f.Now);
            Assert.True(view.Ready);
            Assert.False(view.Consumed);
            Assert.Equal("none", view.AuthorityEffect);
        }
        Assert.Same(before, f.State);
        Assert.Null(f.State.Admission);
        Assert.Null(f.State.ClaimedEvidence);

        var reserved = FileTransferCoordinator.ReserveSessionPreparationAdmission(f.State, f.Permit.Scope,
            "transfer_coordinator", direction, f.Now);
        Assert.Equal(FileTransferSessionPreparationStatus.Accepted, reserved.Status);
        Assert.Null(reserved.State.Permit);
        Assert.Same(f.State.Readiness, reserved.Admission!.Readiness);
        var claimed = FileTransferCoordinator.ClaimSessionPreparationReadiness(reserved.State, reserved.Admission, f.Lease, f.Now);
        Assert.Equal(FileTransferSessionPreparationStatus.Accepted, claimed.Status);
        Assert.Equal(f.Proof.ObservedUtc, claimed.Evidence!.SourceCapturedUtc);
        Assert.Equal(direction, claimed.Evidence.Direction);
        Assert.Equal(FileTransferInitialTunaReadinessScopes.Transfer, claimed.Evidence.ReadinessScope);
        Assert.Null(claimed.Evidence.SourceTransferId);
        var duplicate = FileTransferCoordinator.ClaimSessionPreparationReadiness(claimed.State, reserved.Admission,
            f.Lease with { ObservedUtc = f.Now.AddSeconds(1) }, f.Now.AddSeconds(1));
        Assert.Equal(FileTransferSessionPreparationStatus.Idempotent, duplicate.Status);
        Assert.Same(claimed.State, duplicate.State);
        Assert.Same(claimed.Evidence, duplicate.Evidence); // No refreshed capture time.
        Assert.False(FileTransferCoordinator.ObserveSessionPreparation(claimed.State, f.Permit.Scope,
            direction, f.Lease, f.Now, f.Now).Ready);

        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var transfer = EmptyState(regular, direction);
        var missing = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer,
            tuna, targetTransport: FileTransferTransportKind.Tuna), transfer);
        Assert.Null(missing.StartedLeg);
        Assert.Equal("initial_tuna_readiness_missing", missing.MutationRejectedReason);
        var admitted = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer,
            tuna, targetTransport: FileTransferTransportKind.Tuna, initialTunaReadinessEvidence: claimed.Evidence), transfer);
        Assert.NotNull(admitted.StartedLeg);
        Assert.True(admitted.InitialTunaRouteSelectionDecision?.Accepted);
        Assert.Equal(FileTransferRoute.FileTunaV4, admitted.State.RouteSelection.Route);
        Assert.Equal(4, admitted.State.RouteSelection.ProtocolVersion);
    }

    [Theory]
    [InlineData("detached")]
    [InlineData("empty_service")]
    [InlineData("attachment")]
    [InlineData("security_generation")]
    [InlineData("incarnation")]
    [InlineData("unverified")]
    [InlineData("failed_handshake")]
    [InlineData("unapproved")]
    [InlineData("capability")]
    [InlineData("expired")]
    [InlineData("session")]
    [InlineData("helpee")]
    [InlineData("helper")]
    [InlineData("same_peer")]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_UnknownOrInvalidRegistrationCannotIssueAuthority(string mutation)
    {
        var now = DateTimeOffset.UtcNow;
        var state = new FileTransferSessionPreparationState(mutation == "empty_service" ? Guid.Empty : Guid.NewGuid());
        if (mutation != "detached") state = FileTransferCoordinator.AttachSessionPreparationTransport(state);
        var security = PreparationSecurity(now);
        security = mutation switch
        {
            "unverified" => security with { HandshakeCompleted = false },
            "failed_handshake" => security with { HandshakeState = SessionHandshakeState.Failed },
            "unapproved" => security with { ApprovalGranted = false },
            "capability" => security with { ApprovedCapabilities = CapabilityGrant.Chat },
            "expired" => security with { ApprovalExpiresAt = now },
            "session" => security with { SessionId = null },
            "helpee" => security with { HelpeeAddress = null },
            "helper" => security with { HelperAddress = null },
            "same_peer" => security with { HelperAddress = security.HelpeeAddress },
            _ => security,
        };
        var result = FileTransferCoordinator.RegisterSessionPreparationScope(state,
            mutation == "attachment" ? 9 : state.AttachmentGeneration,
            mutation == "security_generation" ? 0 : 1,
            mutation == "incarnation" ? Guid.Empty : Guid.NewGuid(), security, now);
        Assert.Equal(FileTransferSessionPreparationStatus.Rejected, result.Status);
        Assert.Same(state, result.State);
        Assert.Null(result.Permit);
    }

    [Theory]
    [InlineData(true, "answer")]
    [InlineData(true, "ack_dispatch")]
    [InlineData(false, "ack_received")]
    [InlineData(true, "nonce")]
    [InlineData(true, "payer")]
    [InlineData(true, "offer_generation")]
    [InlineData(true, "snapshot_generation")]
    [InlineData(true, "lease_generation")]
    [InlineData(true, "listener")]
    [InlineData(true, "lease_lost")]
    [InlineData(true, "listener_unready")]
    [InlineData(true, "no_file_lane")]
    [InlineData(true, "future")]
    [InlineData(true, "before_permit")]
    [InlineData(true, "deadline")]
    [InlineData(true, "copied_permit")]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_IncompleteOrUnboundHandshakeCannotBecomeReady(bool localIsPayer, string mutation)
    {
        var f = CreateSessionPreparationFixture(localIsPayer);
        var proof = mutation switch
        {
            "answer" => f.Proof with { AnswerReceived = false },
            "ack_dispatch" => f.Proof with { AnswerAckDispatched = false },
            "ack_received" => f.Proof with { AnswerAckReceived = false },
            "nonce" => f.Proof with { Nonce = "another-episode" },
            "payer" => f.Proof with { PayerDecisionId = 8 },
            "offer_generation" => f.Proof with { OfferGeneration = 0 },
            "snapshot_generation" => f.Proof with { SnapshotGeneration = 0 },
            "lease_generation" => f.Proof with { LeaseGeneration = 0 },
            "listener" => f.Proof with { ListenerRunId = "" },
            "lease_lost" => f.Proof with { LeaseCurrent = false },
            "listener_unready" => f.Proof with { ListenerReady = false },
            "no_file_lane" => f.Proof with { FileLaneNegotiated = false },
            "future" => f.Proof with { ObservedUtc = f.Now.AddTicks(1) },
            "before_permit" => f.Proof with { ObservedUtc = f.Now.AddTicks(-1) },
            _ => f.Proof,
        };
        var result = FileTransferCoordinator.CompleteSessionPreparation(f.State,
            mutation == "copied_permit" ? f.Permit with { } : f.Permit, proof,
            mutation == "deadline" ? f.Permit.DeadlineUtc : f.Now);
        Assert.NotEqual(FileTransferSessionPreparationStatus.Accepted, result.Status);
        Assert.Same(f.State, result.State);
        Assert.Null(result.State.Readiness);
        Assert.Null(result.Evidence);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_DuplicatesDoNotRenewOrReplaceEpisode()
    {
        var f = CreateSessionPreparationFixture();
        var duplicate = FileTransferCoordinator.BeginSessionPreparation(f.State, f.Permit.Scope,
            f.Permit.Nonce, f.Permit.PayerDecisionId, true, f.Permit.DeadlineUtc, f.Now.AddSeconds(1));
        Assert.Equal(FileTransferSessionPreparationStatus.Idempotent, duplicate.Status);
        Assert.Same(f.State, duplicate.State);
        Assert.Same(f.Permit, duplicate.Permit);
        var replacement = FileTransferCoordinator.BeginSessionPreparation(f.State, f.Permit.Scope,
            "other", 1, true, f.Permit.DeadlineUtc, f.Now.AddSeconds(1));
        Assert.Equal(FileTransferSessionPreparationStatus.Rejected, replacement.Status);
        Assert.Null(replacement.Permit);
        var completed = FileTransferCoordinator.CompleteSessionPreparation(f.State, f.Permit, f.Proof, f.Now);
        var completedAgain = FileTransferCoordinator.CompleteSessionPreparation(completed.State, f.Permit, f.Proof, f.Now.AddSeconds(1));
        Assert.Equal(FileTransferSessionPreparationStatus.Idempotent, completedAgain.Status);
        Assert.Same(completed.State, completedAgain.State);
        var changed = FileTransferCoordinator.CompleteSessionPreparation(completed.State, f.Permit,
            f.Proof with { SnapshotGeneration = 99 }, f.Now.AddSeconds(1));
        Assert.Equal(FileTransferSessionPreparationStatus.Stale, changed.Status);
        Assert.Same(completed.State, changed.State);
        var expired = FileTransferCoordinator.BeginSessionPreparation(completed.State, f.Permit.Scope,
            f.Permit.Nonce, 1, true, f.Permit.DeadlineUtc, completed.State.Readiness!.ExpiresUtc);
        Assert.Equal(FileTransferSessionPreparationStatus.Rejected, expired.Status);
        Assert.Same(completed.State, expired.State);
    }

    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_AdmissionBeforeAnswerClosesCallbackFence(FileTransferDirection direction)
    {
        var f = CreateSessionPreparationFixture();
        var reserved = FileTransferCoordinator.ReserveSessionPreparationAdmission(f.State, f.Permit.Scope, "actual", direction, f.Now);
        Assert.Null(reserved.Admission!.Readiness);
        Assert.False(FileTransferCoordinator.IsSessionPreparationPermitCurrent(reserved.State, f.Permit, f.Now));
        var late = FileTransferCoordinator.CompleteSessionPreparation(reserved.State, f.Permit, f.Proof, f.Now);
        Assert.Equal(FileTransferSessionPreparationStatus.Stale, late.Status);
        Assert.Same(reserved.State, late.State);
        Assert.Null(FileTransferCoordinator.ClaimSessionPreparationReadiness(reserved.State, reserved.Admission, f.Lease, f.Now).Evidence);
        var begin = FileTransferCoordinator.BeginSessionPreparation(reserved.State, f.Permit.Scope,
            "new", 2, true, f.Now.AddSeconds(60), f.Now);
        Assert.Equal(FileTransferSessionPreparationStatus.Rejected, begin.Status);
        var sameScope = FileTransferCoordinator.RegisterSessionPreparationScope(reserved.State,
            f.Permit.Scope.AttachmentGeneration, f.Permit.Scope.SecurityGeneration, f.Permit.Scope.SecurityIncarnation,
            PreparationSecurity(f.Now), f.Now);
        Assert.Equal(FileTransferSessionPreparationStatus.Stale, sameScope.Status);
        Assert.Same(reserved.State, sameScope.State);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("snapshot")]
    [InlineData("lease")]
    [InlineData("listener")]
    [InlineData("lease_lost")]
    [InlineData("listener_unready")]
    [InlineData("no_file_lane")]
    [InlineData("old_observation")]
    [InlineData("future")]
    [InlineData("expired")]
    [InlineData("copied_admission")]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_ClaimRechecksExactLeaseAndReservation(string mutation)
    {
        var f = CreateSessionPreparationFixture(ready: true);
        var now = f.Now.AddSeconds(1);
        var reserved = FileTransferCoordinator.ReserveSessionPreparationAdmission(f.State, f.Permit.Scope,
            "actual", FileTransferDirection.Outbound, now);
        var lease = f.Lease with { ObservedUtc = now };
        lease = mutation switch
        {
            "scope" => lease with { Scope = f.Permit.Scope with { HelperIdentity = "wrong-peer" } },
            "snapshot" => lease with { SnapshotGeneration = 2 },
            "lease" => lease with { LeaseGeneration = 2 },
            "listener" => lease with { ListenerRunId = "replaced" },
            "lease_lost" => lease with { LeaseCurrent = false },
            "listener_unready" => lease with { ListenerReady = false },
            "no_file_lane" => lease with { FileLaneNegotiated = false },
            "old_observation" => lease with { ObservedUtc = f.Now },
            "future" => lease with { ObservedUtc = now.AddTicks(1) },
            _ => lease,
        };
        var result = FileTransferCoordinator.ClaimSessionPreparationReadiness(reserved.State,
            mutation == "copied_admission" ? reserved.Admission! with { } : reserved.Admission!, lease,
            mutation == "expired" ? reserved.Admission!.Readiness!.ExpiresUtc : now);
        Assert.Equal(FileTransferSessionPreparationStatus.Stale, result.Status);
        Assert.Same(reserved.State, result.State);
        Assert.Null(result.Evidence);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_ClaimCannotMoveToAnotherTransferOrDirection()
    {
        var f = CreateSessionPreparationFixture(ready: true);
        var first = FileTransferCoordinator.ReserveSessionPreparationAdmission(f.State, f.Permit.Scope,
            "actual", FileTransferDirection.Outbound, f.Now);
        var duplicate = FileTransferCoordinator.ReserveSessionPreparationAdmission(first.State, f.Permit.Scope,
            "actual", FileTransferDirection.Outbound, f.Now.AddSeconds(1));
        Assert.Equal(FileTransferSessionPreparationStatus.Idempotent, duplicate.Status);
        Assert.Same(first.Admission, duplicate.Admission);
        foreach (var (id, direction) in new[] { ("other", FileTransferDirection.Outbound), ("actual", FileTransferDirection.Inbound) })
        {
            var rejected = FileTransferCoordinator.ReserveSessionPreparationAdmission(first.State, f.Permit.Scope, id, direction, f.Now);
            Assert.Equal(FileTransferSessionPreparationStatus.Rejected, rejected.Status);
            Assert.Same(first.State, rejected.State);
            Assert.Null(rejected.Admission);
        }
        Assert.False(FileTransferCoordinator.ObserveSessionPreparation(first.State, f.Permit.Scope,
            FileTransferDirection.Inbound, f.Lease, f.Now, f.Now).Ready);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("detach_reattach")]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_LifecycleRevocationCannotReopenSameIncarnation(string lifecycle)
    {
        var f = CreateSessionPreparationFixture(ready: true);
        var state = lifecycle == "detach_reattach"
            ? FileTransferCoordinator.AttachSessionPreparationTransport(FileTransferCoordinator.DetachSessionPreparationTransport(f.State))
            : FileTransferCoordinator.RevokeSessionPreparationScope(f.State, f.Permit.Scope).State;
        var oldScope = FileTransferCoordinator.RegisterSessionPreparationScope(state, state.AttachmentGeneration,
            f.Permit.Scope.SecurityGeneration, f.Permit.Scope.SecurityIncarnation, PreparationSecurity(f.Now), f.Now);
        Assert.Equal(FileTransferSessionPreparationStatus.Stale, oldScope.Status);
        var madeUpGeneration = FileTransferCoordinator.RegisterSessionPreparationScope(state, state.AttachmentGeneration,
            2, f.Permit.Scope.SecurityIncarnation, PreparationSecurity(f.Now), f.Now);
        Assert.Equal(FileTransferSessionPreparationStatus.Stale, madeUpGeneration.Status);
        Assert.Equal(FileTransferSessionPreparationStatus.Stale,
            FileTransferCoordinator.CompleteSessionPreparation(state, f.Permit, f.Proof, f.Now).Status);
        var next = FileTransferCoordinator.RegisterSessionPreparationScope(state, state.AttachmentGeneration,
            2, Guid.NewGuid(), PreparationSecurity(f.Now), f.Now);
        Assert.Equal(FileTransferSessionPreparationStatus.Accepted, next.Status);
        var staleRevoke = FileTransferCoordinator.RevokeSessionPreparationScope(next.State, f.Permit.Scope);
        Assert.Equal(FileTransferSessionPreparationStatus.Stale, staleRevoke.Status);
        Assert.Same(next.State, staleRevoke.State);
        Assert.Equal(FileTransferSessionPreparationStatus.Stale,
            FileTransferCoordinator.CompleteSessionPreparation(next.State, f.Permit, f.Proof, f.Now).Status);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_RetiredEpisodeCannotCompleteOrRetireItsSuccessor()
    {
        var f = CreateSessionPreparationFixture();
        var retired = FileTransferCoordinator.RetireSessionPreparationEpisode(f.State, f.Permit);
        var next = FileTransferCoordinator.BeginSessionPreparation(retired.State, f.Permit.Scope,
            "next-nonce", 2, true, f.Permit.DeadlineUtc, f.Now);
        Assert.Equal(FileTransferSessionPreparationStatus.Accepted, next.Status);
        Assert.True(next.Permit!.Generation > f.Permit.Generation);
        Assert.Equal(FileTransferSessionPreparationStatus.Stale,
            FileTransferCoordinator.CompleteSessionPreparation(next.State, f.Permit, f.Proof, f.Now).Status);
        var staleRetire = FileTransferCoordinator.RetireSessionPreparationEpisode(next.State, f.Permit);
        Assert.Equal(FileTransferSessionPreparationStatus.Stale, staleRetire.Status);
        Assert.Same(next.State, staleRetire.State);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_ApprovalAndReadinessExpiryDoNotRenewThroughObservation()
    {
        var f = CreateSessionPreparationFixture(ready: true);
        var before = f.State;
        foreach (var now in new[] { f.Now, f.Now.AddMinutes(4), f.Now.AddMinutes(5), f.Permit.Scope.ApprovalExpiresUtc })
        {
            var view = FileTransferCoordinator.ObserveSessionPreparation(f.State, f.Permit.Scope,
                FileTransferDirection.Outbound, f.Lease with { ObservedUtc = now }, now, now);
            Assert.Equal(now < f.State.Readiness!.ExpiresUtc, view.Ready);
            Assert.Equal(f.State.Readiness.ExpiresUtc, view.ExpiresUtc);
            Assert.False(view.Consumed);
        }
        Assert.Same(before, f.State);
        var expired = FileTransferCoordinator.ReserveSessionPreparationAdmission(f.State, f.Permit.Scope,
            "actual", FileTransferDirection.Outbound, f.State.Readiness!.ExpiresUtc);
        Assert.Equal(FileTransferSessionPreparationStatus.Accepted, expired.Status);
        Assert.Null(expired.Admission!.Readiness);

        var initial = FileTransferCoordinator.AttachSessionPreparationTransport(new FileTransferSessionPreparationState(Guid.NewGuid()));
        var security = PreparationSecurity(f.Now) with { ApprovalExpiresAt = f.Now.AddMinutes(2) };
        var registered = FileTransferCoordinator.RegisterSessionPreparationScope(initial, initial.AttachmentGeneration, 1, Guid.NewGuid(), security, f.Now);
        var pending = FileTransferCoordinator.BeginSessionPreparation(registered.State, registered.State.Scope!, "nonce", 1, true, f.Now.AddSeconds(60), f.Now);
        var complete = FileTransferCoordinator.CompleteSessionPreparation(pending.State, pending.Permit!, f.Proof, f.Now);
        Assert.Equal(security.ApprovalExpiresAt, complete.State.Readiness!.ExpiresUtc);
    }

    [Theory]
    [InlineData("service")]
    [InlineData("attachment")]
    [InlineData("incarnation")]
    [InlineData("peer")]
    [InlineData("session")]
    [InlineData("direction")]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_ObservationRejectsWrongScopeWithoutConsuming(string mutation)
    {
        var f = CreateSessionPreparationFixture(ready: true);
        var scope = mutation switch
        {
            "service" => f.Permit.Scope with { ServiceInstanceId = Guid.NewGuid() },
            "attachment" => f.Permit.Scope with { AttachmentGeneration = 2 },
            "incarnation" => f.Permit.Scope with { SecurityIncarnation = Guid.NewGuid() },
            "peer" => f.Permit.Scope with { HelperIdentity = "wrong-peer" },
            "session" => f.Permit.Scope with { SessionId = "other" },
            _ => f.Permit.Scope,
        };
        var view = FileTransferCoordinator.ObserveSessionPreparation(f.State, scope,
            mutation == "direction" ? (FileTransferDirection)9 : FileTransferDirection.Outbound, f.Lease, f.Now, f.Now);
        Assert.False(view.Ready);
        Assert.Null(f.State.Admission);
        Assert.Null(f.State.ClaimedEvidence);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_ObservationRequiresLeaseReadAfterRequest()
    {
        var f = CreateSessionPreparationFixture(ready: true);
        var requested = f.Now.AddSeconds(1);
        var stale = FileTransferCoordinator.ObserveSessionPreparation(f.State, f.Permit.Scope,
            FileTransferDirection.Outbound, f.Lease, requested, requested);
        Assert.False(stale.Ready);
        var fresh = FileTransferCoordinator.ObserveSessionPreparation(f.State, f.Permit.Scope,
            FileTransferDirection.Outbound, f.Lease with { ObservedUtc = requested }, requested, requested);
        Assert.True(fresh.Ready);
        Assert.Null(f.State.Admission);
        Assert.Null(f.State.ClaimedEvidence);
    }

    [Theory]
    [InlineData("before_issue")]
    [InlineData("deadline")]
    [InlineData("retired")]
    [InlineData("revoked")]
    [InlineData("detached")]
    [InlineData("copied")]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_QueuedWorkRequiresCurrentExactPermit(string mutation)
    {
        var f = CreateSessionPreparationFixture();
        Assert.True(FileTransferCoordinator.IsSessionPreparationPermitCurrent(f.State, f.Permit, f.Now));
        var state = mutation switch
        {
            "retired" => FileTransferCoordinator.RetireSessionPreparationEpisode(f.State, f.Permit).State,
            "revoked" => FileTransferCoordinator.RevokeSessionPreparationScope(f.State, f.Permit.Scope).State,
            "detached" => FileTransferCoordinator.DetachSessionPreparationTransport(f.State),
            _ => f.State,
        };
        var now = mutation switch
        {
            "before_issue" => f.Now.AddTicks(-1),
            "deadline" => f.Permit.DeadlineUtc,
            _ => f.Now,
        };
        Assert.False(FileTransferCoordinator.IsSessionPreparationPermitCurrent(state,
            mutation == "copied" ? f.Permit with { } : f.Permit, now));
    }

    private sealed record SessionPreparationFixture(FileTransferSessionPreparationState State,
        FileTransferSessionPreparationPermit Permit, FileTransferSessionPreparationProof Proof,
        FileTransferSessionPreparationLeaseObservation Lease, DateTimeOffset Now);

    [Theory]
    [InlineData("nonce")]
    [InlineData("payer")]
    [InlineData("scope")]
    [InlineData("deadline_past")]
    [InlineData("deadline_unbounded")]
    [InlineData("approval_expired")]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_InvalidRequestPreservesEligibility(string mutation)
    {
        var f = CreateSessionPreparationFixture();
        var eligible = FileTransferCoordinator.RetireSessionPreparationEpisode(f.State, f.Permit).State;
        var now = mutation == "approval_expired" ? f.Permit.Scope.ApprovalExpiresUtc : f.Now;
        var deadline = mutation switch
        {
            "deadline_past" => now,
            "deadline_unbounded" => now.AddSeconds(61),
            _ => now.AddSeconds(60),
        };
        var result = FileTransferCoordinator.BeginSessionPreparation(eligible,
            mutation == "scope" ? f.Permit.Scope with { HelperIdentity = "wrong" } : f.Permit.Scope,
            mutation == "nonce" ? "" : "new", mutation == "payer" ? 0 : 2, true, deadline, now);
        Assert.Equal(FileTransferSessionPreparationStatus.Rejected, result.Status);
        Assert.Same(eligible, result.State);
        Assert.Null(result.Permit);
    }

    [Theory]
    [InlineData("transfer")]
    [InlineData("direction")]
    [InlineData("scope")]
    [Trait("FileTransferCoordinatorSuite", "SessionPreparation")]
    public void SessionPreparation_InvalidAdmissionDoesNotConsumeReadiness(string mutation)
    {
        var f = CreateSessionPreparationFixture(ready: true);
        var result = FileTransferCoordinator.ReserveSessionPreparationAdmission(f.State,
            mutation == "scope" ? f.Permit.Scope with { SessionId = "wrong" } : f.Permit.Scope,
            mutation == "transfer" ? "" : "actual",
            mutation == "direction" ? (FileTransferDirection)99 : FileTransferDirection.Outbound, f.Now);
        Assert.Equal(FileTransferSessionPreparationStatus.Rejected, result.Status);
        Assert.Same(f.State, result.State);
        Assert.Null(result.Admission);
        Assert.Same(f.State.Readiness, result.State.Readiness);
    }

    private static SessionPreparationFixture CreateSessionPreparationFixture(bool localIsPayer = true, bool ready = false)
    {
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var state = FileTransferCoordinator.AttachSessionPreparationTransport(new FileTransferSessionPreparationState(Guid.NewGuid()));
        var registered = FileTransferCoordinator.RegisterSessionPreparationScope(state, state.AttachmentGeneration,
            1, Guid.NewGuid(), PreparationSecurity(now), now);
        Assert.Equal(FileTransferSessionPreparationStatus.Accepted, registered.Status);
        var pending = FileTransferCoordinator.BeginSessionPreparation(registered.State, registered.State.Scope!,
            "nonce", 1, localIsPayer, now.AddSeconds(60), now);
        Assert.Equal(FileTransferSessionPreparationStatus.Accepted, pending.Status);
        var proof = new FileTransferSessionPreparationProof("nonce", 1, 1, 1, 1, "listener",
            LeaseCurrent: true, ListenerReady: true, FileLaneNegotiated: true,
            AnswerReceived: localIsPayer, AnswerAckDispatched: localIsPayer,
            AnswerAckReceived: !localIsPayer, now);
        var lease = new FileTransferSessionPreparationLeaseObservation(pending.Permit!.Scope,
            1, 1, "listener", LeaseCurrent: true, ListenerReady: true, FileLaneNegotiated: true, now);
        if (!ready) return new(pending.State, pending.Permit, proof, lease, now);
        var complete = FileTransferCoordinator.CompleteSessionPreparation(pending.State, pending.Permit, proof, now);
        Assert.Equal(FileTransferSessionPreparationStatus.Accepted, complete.Status);
        return new(complete.State, pending.Permit, proof, lease, now);
    }

    private static SessionSecurityState PreparationSecurity(DateTimeOffset now)
        => new()
        {
            SessionId = new SessionId("session_coordinator"),
            HelpeeAddress = new PeerAddress("helpee"),
            HelperAddress = new PeerAddress("helper"),
            HandshakeCompleted = true,
            HandshakeState = SessionHandshakeState.Verified,
            ApprovalGranted = true,
            ApprovedCapabilities = CapabilityGrant.FileTransfer,
            ApprovalExpiresAt = now.AddMinutes(10),
        };
}
