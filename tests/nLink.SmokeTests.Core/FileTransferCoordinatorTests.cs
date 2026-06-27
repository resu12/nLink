using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class FileTransferCoordinatorTests
{
    [Fact]
    public void StartTransfer_RegularV4CreatesActiveV4Leg()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = EmptyState(regular);

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                regular,
                state: FileTransferLegState.Active,
                canSendData: true,
                committedChunk: 0,
                highestObservedChunk: -1),
            state);

        Assert.False(decision.TerminalMutationRejected);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, decision.State.RouteSelection.Route);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, decision.State.RouteSelection.ProtocolVersion);
        Assert.True(decision.State.RouteRuntime.UsesV4SparsePump);
        Assert.NotNull(decision.StartedLeg);
        Assert.Equal(FileTransferLegState.Active, decision.State.CurrentLeg!.State);
        Assert.True(decision.State.CurrentLeg.CanSendData);
        Assert.Equal(1, decision.State.LastTransferLegGeneration);
        Assert.Equal(1, decision.State.LastRuntimeOwnerGeneration);
        Assert.NotNull(decision.State.CurrentRuntimeOwner);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, decision.State.CurrentRuntimeOwner!.Value.Route);
        Assert.Equal("regular_v4", decision.State.CurrentRuntimeOwner.Value.RuntimeFamily);
    }

    [Fact]
    public void RegularV4ToTunaV4_CreatesRecoveredLiveEpochAndActiveTunaLeg()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaActivated,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock",
                FileTransferLegState.Active,
                canSendData: true,
                committedChunk: 4,
                highestObservedChunk: 7),
            started.State);

        Assert.True(decision.RouteChanged);
        Assert.NotNull(decision.StartedLiveRouteEpoch);
        Assert.NotNull(decision.RecoveredLiveRouteEpoch);
        Assert.Equal(1, decision.StartedLiveRouteEpoch!.EpochId);
        Assert.Equal("recovered", decision.StartedLiveRouteEpoch.State);
        Assert.Equal(FileTransferRoute.FileTunaV4, decision.State.RouteSelection.Route);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, decision.State.CurrentLeg!.ProtocolVersion);
        Assert.True(decision.State.CurrentLeg.CanSendData);
        Assert.Equal(FileTransferLegState.Frozen, decision.FrozenLeg!.State);
        Assert.Equal(2, decision.State.LastRuntimeOwnerGeneration);
        Assert.NotEqual(started.State.CurrentRuntimeOwner, decision.State.CurrentRuntimeOwner);
        Assert.Equal(FileTransferRoute.FileTunaV4, decision.State.CurrentRuntimeOwner!.Value.Route);
    }

    [Fact]
    public void RuntimeOwnerToken_OldOwnerInvalidAfterRouteTransition()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var oldOwner = started.State.CurrentRuntimeOwner!.Value;

        var activated = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaActivated,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock",
                FileTransferLegState.Active,
                canSendData: true),
            started.State);

        Assert.False(FileTransferCoordinator.IsRuntimeOwnerCurrent(activated.State, oldOwner, out var reason));
        Assert.Equal("runtime_generation_mismatch", reason);
        Assert.True(FileTransferCoordinator.IsRuntimeOwnerCurrent(activated.State, activated.State.CurrentRuntimeOwner!.Value, out reason));
        Assert.Equal("current", reason);
    }

    [Fact]
    public void RuntimeOwnerToken_TerminalGenerationInvalidatesNonTerminalOwner()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var owner = started.State.CurrentRuntimeOwner!.Value;

        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                regular,
                reason: "completed"),
            started.State);

        Assert.True(terminal.State.IsTerminal);
        Assert.Equal(1, terminal.State.TerminalGeneration);
        Assert.False(FileTransferCoordinator.IsRuntimeOwnerCurrent(terminal.State, owner, out var reason));
        Assert.Equal("terminal_generation_mismatch", reason);
    }

    [Fact]
    public void AuthorizeAction_AcceptsCurrentRuntimeOwner()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var owner = started.State.CurrentRuntimeOwner!.Value;

        var decision = FileTransferCoordinator.AuthorizeAction(
            started.State,
            ActionRequest(
                FileTransferCoordinatorActionKind.RuntimeOwnerValidation,
                owner,
                route: owner.Route,
                protocolVersion: owner.ProtocolVersion,
                liveRouteEpoch: owner.LiveRouteEpoch,
                legGeneration: started.State.CurrentLeg!.Generation));

        Assert.True(decision.Accepted);
        Assert.False(decision.TerminalMutationRejected);
        Assert.Equal("accepted", decision.RejectionReason);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, decision.CurrentRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, decision.CurrentProtocolVersion);
        Assert.Equal(owner.RuntimeGeneration, decision.CurrentRuntimeGeneration);
        Assert.Equal(started.State.CurrentLeg.Generation, decision.CurrentLegGeneration);
    }

    [Fact]
    public void AuthorizeAction_RejectsMissingRuntimeOwner()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);

        var decision = FileTransferCoordinator.AuthorizeAction(
            EmptyState(regular),
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RuntimeOwnerValidation,
                Source: "test",
                Reason: "missing_owner"));

        Assert.False(decision.Accepted);
        Assert.False(decision.TerminalMutationRejected);
        Assert.Equal("runtime_owner_missing", decision.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RejectsOldOwnerAfterRouteTransition()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var oldOwner = started.State.CurrentRuntimeOwner!.Value;
        var activated = ActivateTuna(started.State, tuna, committedChunk: 4);

        var decision = FileTransferCoordinator.AuthorizeAction(
            activated.State,
            ActionRequest(FileTransferCoordinatorActionKind.LivenessDeferral, oldOwner));

        Assert.False(decision.Accepted);
        Assert.False(decision.TerminalMutationRejected);
        Assert.Equal("runtime_generation_mismatch", decision.RejectionReason);
        Assert.Equal(activated.State.CurrentRuntimeOwner!.Value.RuntimeGeneration, decision.CurrentRuntimeGeneration);
    }

    [Fact]
    public void AuthorizeAction_RejectsOwnerAfterTerminalGenerationChanges()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var owner = started.State.CurrentRuntimeOwner!.Value;
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                regular,
                reason: "completed"),
            started.State);

        var decision = FileTransferCoordinator.AuthorizeAction(
            terminal.State,
            ActionRequest(FileTransferCoordinatorActionKind.RepairReplay, owner));

        Assert.False(decision.Accepted);
        Assert.True(decision.TerminalMutationRejected);
        Assert.Equal("terminal_state", decision.RejectionReason);
        Assert.Equal(terminal.State.TerminalGeneration, decision.CurrentTerminalGeneration);
    }

    [Fact]
    public void AuthorizeAction_RejectsRouteProtocolAndLiveEpochMismatch()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var owner = started.State.CurrentRuntimeOwner!.Value;

        var routeMismatch = FileTransferCoordinator.AuthorizeAction(
            started.State,
            ActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                owner,
                route: FileTransferRoute.FileTunaV4));
        Assert.False(routeMismatch.Accepted);
        Assert.Equal("route_or_protocol_mismatch", routeMismatch.RejectionReason);

        var protocolMismatch = FileTransferCoordinator.AuthorizeAction(
            started.State,
            ActionRequest(
                FileTransferCoordinatorActionKind.ControlPlaneSend,
                owner,
                protocolVersion: FileTransferProtocol.ProtocolVersionV6));
        Assert.False(protocolMismatch.Accepted);
        Assert.Equal("route_or_protocol_mismatch", protocolMismatch.RejectionReason);

        var liveEpochMismatch = FileTransferCoordinator.AuthorizeAction(
            started.State,
            ActionRequest(
                FileTransferCoordinatorActionKind.ProgressMutation,
                owner,
                liveRouteEpoch: owner.LiveRouteEpoch + 1));
        Assert.False(liveEpochMismatch.Accepted);
        Assert.Equal("live_route_epoch_mismatch", liveEpochMismatch.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RejectsLegGenerationMismatch()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var owner = started.State.CurrentRuntimeOwner!.Value;

        var decision = FileTransferCoordinator.AuthorizeAction(
            started.State,
            ActionRequest(
                FileTransferCoordinatorActionKind.FallbackCheckpointBegin,
                owner,
                legGeneration: started.State.CurrentLeg!.Generation + 1));

        Assert.False(decision.Accepted);
        Assert.Equal("leg_generation_mismatch", decision.RejectionReason);
        Assert.Equal(started.State.CurrentLeg.Generation, decision.CurrentLegGeneration);
    }

    [Fact]
    public void FallbackCheckpointAccepted_RemainsNonYieldableUntilTailQuiescent()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var started = StartRegular(regular);
        var fallbackDecision = FallBack(started.State, fallback, committedChunk: 32, transportEpoch: 7);
        var requested = RequestFallbackCheckpoint(fallbackDecision.State, fallback, "checkpoint:1", 7);
        var accepted = AcceptFallbackCheckpoint(requested.State, fallback, "checkpoint:1", 7, 64, 96);
        var leg = accepted.State.CurrentLeg!;

        Assert.Equal(FileTransferLegState.RecoveryActive, leg.State);
        Assert.False(FileTransferCoordinator.CanAdmitRuntimeUnlockWhilePostTunaFallback(
            routeUsesPostTunaFallbackRuntime: true,
            leg,
            checkpointDeliveryRecoveryPending: false,
            stateRefreshSendInFlight: false,
            deferredStateRefreshPending: false,
            proofReplayPending: false,
            out var rejectionReason));
        Assert.Equal("recovery_active", rejectionReason);
    }

    [Fact]
    public void FallbackTailQuiescent_MarksLegYieldableForRuntimeUnlock()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var started = StartRegular(regular);
        var fallbackDecision = FallBack(started.State, fallback, committedChunk: 32, transportEpoch: 7);
        var requested = RequestFallbackCheckpoint(fallbackDecision.State, fallback, "checkpoint:1", 7);
        var accepted = AcceptFallbackCheckpoint(requested.State, fallback, "checkpoint:1", 7, 64, 96);
        var quiescent = MarkFallbackTailQuiescent(accepted.State, fallback, 7, 64, 96);
        var leg = quiescent.State.CurrentLeg!;

        Assert.Equal(FileTransferLegState.Active, leg.State);
        Assert.True(leg.CanSendData);
        Assert.False(FileTransferCoordinator.CanAdmitRuntimeUnlockWhilePostTunaFallback(
            routeUsesPostTunaFallbackRuntime: true,
            leg,
            checkpointDeliveryRecoveryPending: false,
            stateRefreshSendInFlight: false,
            deferredStateRefreshPending: false,
            proofReplayPending: false,
            out var rejectionReason));
        Assert.Equal("paired_fallback_tail_yield_missing", rejectionReason);
    }

    [Fact]
    public void RuntimeUnlockTransaction_LocalObservedSendCannotCommitRoute()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.OfferGenerationCreated,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 1,
                OfferGeneration: 10,
                Reason: "runtime_unlock"),
            RuntimeUnlockTransactionSnapshot.Idle).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.ObservedSend,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 1,
                OfferGeneration: 10,
                Reason: "control_priority",
                ObservedLane: "control_priority"),
            transaction).State;

        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "local_only", admissionToken));
        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal("peer_visible_proof_missing", decision.RuntimeUnlockCommitRejectedReason);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, decision.State.RouteSelection.Route);
    }

    [Fact]
    public void RuntimeUnlockTransaction_PeerProofWithoutPathProbeRejectsRouteCommit()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.OfferGenerationCreated,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 2,
                OfferGeneration: 22,
                Reason: "runtime_unlock"),
            RuntimeUnlockTransactionSnapshot.Idle).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.PeerReceived,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 2,
                OfferGeneration: 22,
                Reason: "tuna_acceleration_offer_received_raw"),
            transaction).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.AnswerReceived,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 2,
                OfferGeneration: 22,
                Reason: "transport_acceleration_answer"),
            transaction).State;

        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken));
        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                committedChunk: 3,
                highestObservedChunk: 3,
                runtimeUnlockCommitProof: proof),
            admission.State);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal("runtime_unlock_probe_missing", decision.RuntimeUnlockCommitRejectedReason);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, decision.State.RouteSelection.Route);
    }

    [Fact]
    public void RuntimeUnlockTransaction_PeerProofAndTunaPathProbePermitRouteCommit()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.OfferGenerationCreated,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 2,
                OfferGeneration: 22,
                Reason: "runtime_unlock"),
            RuntimeUnlockTransactionSnapshot.Idle).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.PeerReceived,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 2,
                OfferGeneration: 22,
                Reason: "tuna_acceleration_offer_received_raw"),
            transaction).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.AnswerReceived,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 2,
                OfferGeneration: 22,
                Reason: "transport_acceleration_answer"),
            transaction).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.PathProbeStarted,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 2,
                OfferGeneration: 22,
                Reason: "transport_probe_sent",
                PathProbeId: "probe-22",
                PathProbeTransport: FileTransferTransportKind.Tuna),
            transaction).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.PathProbeAcked,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 2,
                OfferGeneration: 22,
                Reason: "transport_probe_ack",
                PathProbeId: "probe-22",
                PathProbeTransport: FileTransferTransportKind.Tuna),
            transaction).State;

        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken));
        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                committedChunk: 3,
                highestObservedChunk: 3,
                runtimeUnlockCommitProof: proof),
            admission.State);

        Assert.True(decision.RuntimeUnlockCommitAccepted);
        Assert.True(decision.RouteChanged);
        Assert.Equal(FileTransferRoute.FileTunaV4, decision.State.RouteSelection.Route);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, decision.State.RouteSelection.ProtocolVersion);
        Assert.NotNull(decision.RecoveredLiveRouteEpoch);
        Assert.Equal(1, decision.RecoveredLiveRouteEpoch!.EpochId);
        Assert.NotNull(decision.RouteCommitToken);
        Assert.True(FileTransferCoordinator.IsRouteCommitTokenCurrent(
            decision.State,
            decision.RouteCommitToken!.Value,
            out var routeCommitReason));
        Assert.Equal("current", routeCommitReason);
    }

    [Fact]
    public void RuntimeUnlockTransaction_FailedTunaPathLeaseRejectsRouteCommit()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.OfferGenerationCreated,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 3,
                OfferGeneration: 33,
                Reason: "runtime_unlock"),
            RuntimeUnlockTransactionSnapshot.Idle).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.AnswerReceived,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 3,
                OfferGeneration: 33,
                Reason: "transport_acceleration_answer"),
            transaction).State;

        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var proof = RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken) with
        {
            TunaPathLeaseRequired = true,
            TunaPathLeaseGeneration = 7,
            TunaPathLeaseState = RuntimeUnlockTunaPathLeaseState.Failed,
            TunaPathLeaseListenerRunId = "listener-start-7",
            TunaPathLeaseCurrent = false,
            TunaPathLeaseFailureReason = "runtime_unlock_answer_rejected_tuna_path_lease_unavailable",
        };
        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal("tuna_path_lease_failed", decision.RuntimeUnlockCommitRejectedReason);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, decision.State.RouteSelection.Route);
    }

    [Fact]
    public void RuntimeUnlockTunaPathLease_CurrentListenerProofAllowsCommit()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.OfferGenerationCreated,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 4,
                OfferGeneration: 44,
                Reason: "runtime_unlock"),
            RuntimeUnlockTransactionSnapshot.Idle).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.AnswerReceived,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 4,
                OfferGeneration: 44,
                Reason: "transport_acceleration_answer"),
            transaction).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.PathProbeAcked,
                started.State.SessionId,
                started.State.TransferId,
                TransactionGeneration: 4,
                OfferGeneration: 44,
                Reason: "transport_probe_ack",
                PathProbeId: "probe-44",
                PathProbeTransport: FileTransferTransportKind.Tuna),
            transaction).State;
        var lease = RuntimeUnlockTunaPathLease.Start(
            started.State.SessionId,
            started.State.TransferId,
            leaseGeneration: 9,
            listenerRunId: "listener-start-9",
            payerDecisionId: 123,
            nowUtcMs: 100);
        lease = RuntimeUnlockTunaPathLease.BindOffer(
            lease,
            transaction.TransactionGeneration,
            transaction.OfferGeneration,
            payerDecisionId: 123,
            nowUtcMs: 101);
        Assert.True(RuntimeUnlockTunaPathLease.TryMarkListenerReady(
            lease,
            started.State.SessionId,
            leaseGeneration: 9,
            listenerRunId: "listener-start-9",
            nowUtcMs: 102,
            out lease));

        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var proof = RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken) with
        {
            TunaPathLeaseRequired = true,
            TunaPathLeaseGeneration = lease.LeaseGeneration,
            TunaPathLeaseState = lease.State,
            TunaPathLeaseListenerRunId = lease.ListenerRunId,
            TunaPathLeaseCurrent = lease.IsCurrent,
            TunaPathLeaseFailureReason = lease.FailureReason,
        };
        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);

        Assert.True(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal(FileTransferRoute.FileTunaV4, decision.State.RouteSelection.Route);
        Assert.NotNull(decision.RouteCommitToken);
    }

    [Fact]
    public void RuntimeUnlockRouteCommit_RejectsMissingAdmissionToken()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(started.State);
        var proof = WithCurrentTunaLease(RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received"));

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            started.State);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Null(decision.RouteCommitToken);
        Assert.Equal("runtime_unlock_admission_missing", decision.RuntimeUnlockCommitRejectedReason);
    }

    [Fact]
    public void RuntimeUnlockRouteCommit_RejectsStaleAdmissionTokenAfterRouteChange()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var changed = ActivateTuna(admission.State, tuna, committedChunk: 1);
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(admission.State);
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken));

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            changed.State);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal("runtime_unlock_admission_generation_mismatch", decision.RuntimeUnlockCommitRejectedReason);
    }

    [Fact]
    public void RuntimeUnlockRouteCommit_RejectsMissingTunaLease()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(admission.State);
        var proof = RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken);

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal("tuna_path_lease_generation_missing", decision.RuntimeUnlockCommitRejectedReason);
    }

    [Fact]
    public void RuntimeUnlockRouteCommit_RejectsMismatchedSamePathAck()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(admission.State, probeId: "probe-current");
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken) with
            {
                SamePathTunaProbeAckId = "probe-old",
            });

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal("same_path_tuna_probe_ack_mismatch", decision.RuntimeUnlockCommitRejectedReason);
    }

    [Fact]
    public void RuntimeUnlockRouteCommit_RejectsInvalidTargetRoute()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(admission.State);
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken) with
            {
                TargetRoute = fallback.Route,
                ProtocolVersion = fallback.ProtocolVersion,
                TargetTransport = FileTransferTransportKind.RegularNkn,
            });

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                fallback,
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal("route_commit_metadata_invalid", decision.RuntimeUnlockCommitRejectedReason);
    }

    [Fact]
    public void RuntimeUnlockRouteCommit_RejectsFallbackActiveWithoutYield()
    {
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallbackState = ActiveFallbackStateWithoutCheckpoint();
        var owner = fallbackState.CurrentRuntimeOwner!.Value;
        var survivalState = new RuntimeUnlockFallbackSurvivalState(
            FallbackActive: true,
            FallbackCurrent: true,
            FallbackTerminalized: false,
            CheckpointDeliveryRecoveryPending: false,
            StateRefreshSendInFlight: false,
            DeferredStateRefreshPending: false,
            ProofReplayPending: false,
            ControlPlanePressurePending: false,
            BridgeRecoveryPending: false,
            FallbackTailRepairPending: false,
            PairedFallbackYieldGranted: false);
        var forcedAdmissionToken = new RuntimeUnlockAdmissionToken(
            fallbackState.SessionId,
            fallbackState.TransferId,
            fallbackState.Direction,
            owner,
            fallbackState.RouteSelection.Route,
            fallbackState.RouteSelection.TelemetryToken,
            fallbackState.RouteSelection.ProtocolVersion,
            fallbackState.CurrentLiveRouteEpoch?.EpochId ?? 0,
            fallbackState.LastRuntimeOwnerGeneration,
            fallbackState.TerminalGeneration,
            fallbackState.CurrentLeg?.Generation ?? 0,
            AdmissionGeneration: 1,
            Reason: "forced_test_admission",
            survivalState);
        var forcedState = fallbackState with
        {
            RuntimeUnlockFallbackSurvivalState = survivalState,
            CurrentRuntimeUnlockAdmission = forcedAdmissionToken,
            LastRuntimeUnlockAdmissionGeneration = 1,
        };
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(forcedState);
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", forcedAdmissionToken));

        var decision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            forcedState);

        Assert.False(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal("paired_fallback_tail_yield_missing", decision.RuntimeUnlockCommitRejectedReason);
    }

    [Fact]
    public void RuntimeUnlockRouteCommit_TokenBecomesStaleAfterOwnerChange()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(admission.State);
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken));
        var committed = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);
        var token = committed.RouteCommitToken!.Value;

        var nextStart = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                tuna,
                reason: "new_owner",
                state: FileTransferLegState.Active,
                canSendData: true),
            committed.State);

        Assert.False(FileTransferCoordinator.IsRouteCommitTokenCurrent(nextStart.State, token, out var rejectionReason));
        Assert.Equal("route_commit_generation_mismatch", rejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RuntimeUnlockProbeStart_AcceptsCurrentAdmissionToken()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var token = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;

        var decision = FileTransferCoordinator.AuthorizeAction(
            admission.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RuntimeUnlockProbeStart,
                Source: "test",
                Reason: "start_probe",
                RuntimeOwner: token.RuntimeOwner,
                RuntimeUnlockAdmissionToken: token,
                RuntimeUnlockTransactionGeneration: 7,
                RuntimeUnlockOfferGeneration: 8,
                RuntimeUnlockTunaPathLeaseGeneration: 9,
                RuntimeUnlockProbeId: "probe-8",
                TargetRoute: FileTransferRoute.FileTunaV4,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                TargetTransport: FileTransferTransportKind.Tuna));

        Assert.True(decision.Accepted);
        Assert.Equal("accepted", decision.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RuntimeUnlockCallback_RejectsStaleAdmissionTokenAfterOwnerChange()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var token = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var changed = ActivateTuna(admission.State, tuna, committedChunk: 1);

        var decision = FileTransferCoordinator.AuthorizeAction(
            changed.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RuntimeUnlockProbeStart,
                Source: "test",
                Reason: "start_probe",
                RuntimeOwner: token.RuntimeOwner,
                RuntimeUnlockAdmissionToken: token,
                RuntimeUnlockTransactionGeneration: 7,
                RuntimeUnlockOfferGeneration: 8,
                RuntimeUnlockProbeId: "probe-8",
                TargetRoute: FileTransferRoute.FileTunaV4,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                TargetTransport: FileTransferTransportKind.Tuna));

        Assert.False(decision.Accepted);
        Assert.Equal("runtime_generation_mismatch", decision.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RouteCommitPublication_RejectsMissingRouteCommitToken()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);

        var decision = FileTransferCoordinator.AuthorizeAction(
            started.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RouteCommitPublication,
                Source: "test",
                Reason: "route_publication",
                TargetRoute: FileTransferRoute.FileTunaV4,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                TargetTransport: FileTransferTransportKind.Tuna));

        Assert.False(decision.Accepted);
        Assert.Equal("route_commit_token_missing", decision.RejectionReason);
    }

    [Fact]
    public void AuthorizeAction_RouteCommitPublication_AcceptsOnlyFileTunaV4Protocol4Token()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var admission = RequestRuntimeUnlockAdmission(started.State, started.State.CurrentRuntimeOwner);
        var admissionToken = admission.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(admission.State);
        var proof = WithCurrentTunaLease(
            RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received", admissionToken));
        var committed = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            admission.State);
        var token = committed.RouteCommitToken!.Value;

        var accepted = FileTransferCoordinator.AuthorizeAction(
            committed.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RouteCommitPublication,
                Source: "test",
                Reason: "route_publication",
                Route: token.Route,
                ProtocolVersion: token.ProtocolVersion,
                LiveRouteEpoch: token.LiveRouteEpoch,
                LegGeneration: token.LegGeneration,
                RouteCommitToken: token,
                RuntimeUnlockTransactionGeneration: token.TransactionGeneration,
                RuntimeUnlockOfferGeneration: token.OfferGeneration,
                RuntimeUnlockTunaPathLeaseGeneration: token.TunaPathLeaseGeneration,
                RuntimeUnlockProbeId: token.SamePathTunaProbeAckId,
                RuntimeUnlockSamePathTunaProbeAckId: token.SamePathTunaProbeAckId,
                RouteCommitGeneration: token.CommitGeneration,
                TargetRoute: FileTransferRoute.FileTunaV4,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                TargetTransport: FileTransferTransportKind.Tuna));
        var rejectedTarget = FileTransferCoordinator.AuthorizeAction(
            committed.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RouteCommitPublication,
                Source: "test",
                Reason: "route_publication",
                Route: token.Route,
                ProtocolVersion: token.ProtocolVersion,
                LiveRouteEpoch: token.LiveRouteEpoch,
                LegGeneration: token.LegGeneration,
                RouteCommitToken: token,
                TargetRoute: FileTransferRoute.RegularNknV4Fast,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                TargetTransport: FileTransferTransportKind.Tuna));

        Assert.True(accepted.Accepted);
        Assert.False(rejectedTarget.Accepted);
        Assert.Equal("route_commit_metadata_invalid", rejectedTarget.RejectionReason);
    }

    [Fact]
    public void RuntimeUnlockTunaPathLease_StaleListenerReadyProofIsIgnored()
    {
        var lease = RuntimeUnlockTunaPathLease.Start(
            "session_a",
            transferId: "transfer_a",
            leaseGeneration: 5,
            listenerRunId: "listener-start-5",
            payerDecisionId: 1,
            nowUtcMs: 100);
        lease = RuntimeUnlockTunaPathLease.Fail(lease, "listener_sidecar_unavailable", nowUtcMs: 101);

        var marked = RuntimeUnlockTunaPathLease.TryMarkListenerReady(
            lease,
            "session_a",
            leaseGeneration: 4,
            listenerRunId: "listener-start-4",
            nowUtcMs: 102,
            out var updated);

        Assert.False(marked);
        Assert.Equal(RuntimeUnlockTunaPathLeaseState.Failed, updated.State);
        Assert.False(updated.IsCurrent);
    }

    [Fact]
    public void RuntimeUnlockTransaction_StaleSessionOrTerminalTransferRejectsCommit()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var started = StartRegular(regular);
        var staleProof = new RuntimeUnlockRouteCommitProof(
            "different_session",
            started.State.TransferId,
            TransactionGeneration: 1,
            OfferGeneration: 1,
            PeerVisibleProof: true,
            PeerReceived: true,
            AnswerReceived: false,
            FileTransferRoute.FileTunaV4,
            FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            RuntimeUnlockTransactionState.PeerReceived,
            "test");

        var stale = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: staleProof),
            started.State);

        Assert.False(stale.RuntimeUnlockCommitAccepted);
        Assert.Equal("session_mismatch", stale.RuntimeUnlockCommitRejectedReason);

        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, regular, reason: "completed"),
            started.State);
        var proof = staleProof with { SessionId = started.State.SessionId };
        var terminalRejected = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock_commit",
                FileTransferLegState.Active,
                canSendData: true,
                runtimeUnlockCommitProof: proof),
            terminal.State);

        Assert.False(terminalRejected.TerminalMutationRejected);
        Assert.False(terminalRejected.RuntimeUnlockCommitAccepted);
        Assert.Equal("transfer_terminal", terminalRejected.RuntimeUnlockCommitRejectedReason);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, terminalRejected.State.RouteSelection.Route);
    }

    [Fact]
    public void FallbackV6Checkpoint_AcceptanceEnablesCurrentGenerationOnly()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var activated = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaActivated,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock",
                FileTransferLegState.Active,
                canSendData: true,
                committedChunk: 9,
                highestObservedChunk: 11),
            StartRegular(regular).State);

        var fallbackDecision = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaLost,
                fallback,
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn,
                "tuna_disabled",
                FileTransferLegState.CheckpointPending,
                canSendData: false,
                committedChunk: 9,
                highestObservedChunk: 11,
                transportEpoch: 3,
                bridgeRecoveryGeneration: 3),
            activated.State);

        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, fallbackDecision.State.RouteSelection.Route);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, fallbackDecision.State.CurrentLeg!.ProtocolVersion);
        Assert.False(fallbackDecision.State.CurrentLeg.CanSendData);
        Assert.Equal(FileTransferLegState.CheckpointPending, fallbackDecision.State.CurrentLeg.State);
        Assert.True(fallbackDecision.FallbackCheckpointRequired);

        var requested = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackCheckpointRequested,
                fallback,
                reason: "checkpoint",
                state: FileTransferLegState.CheckpointPending,
                canSendData: false,
                transportEpoch: 3,
                checkpointRequestId: "checkpoint:1",
                checkpointPriority: "state_refresh"),
            fallbackDecision.State);
        Assert.True(FileTransferCoordinator.IsCurrentPostTunaFallbackLegCheckpointPending(requested.State.CurrentLeg));

        var accepted = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackCheckpointAccepted,
                fallback,
                reason: "receiver_state",
                state: FileTransferLegState.RecoveryActive,
                canSendData: true,
                committedChunk: 12,
                highestObservedChunk: 18,
                transportEpoch: 3),
            requested.State);

        Assert.NotNull(accepted.AcceptedCheckpointLeg);
        Assert.True(accepted.State.CurrentLeg!.CanSendData);
        Assert.Equal(FileTransferLegState.RecoveryActive, accepted.State.CurrentLeg.State);
        Assert.Equal(12, accepted.State.CurrentLeg.ProvenCommittedChunkIndex);
        Assert.Equal(18, accepted.State.CurrentLeg.ProvenHighestObservedChunkIndex);
        Assert.Null(accepted.State.CurrentLeg.CheckpointRequestId);
    }

    [Fact]
    public void FallbackV6Checkpoint_RequiresCurrentRequestAndTransportEpoch()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var activated = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaActivated,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock",
                FileTransferLegState.Active,
                canSendData: true,
                committedChunk: 9,
                highestObservedChunk: 11),
            StartRegular(regular).State);
        var fallbackDecision = FallBack(activated.State, fallback, committedChunk: 9, transportEpoch: 4);
        var requested = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackCheckpointRequested,
                fallback,
                reason: "checkpoint",
                state: FileTransferLegState.CheckpointPending,
                canSendData: false,
                transportEpoch: 4,
                checkpointRequestId: "checkpoint:4",
                checkpointPriority: "state_refresh"),
            fallbackDecision.State);
        var leg = requested.State.CurrentLeg;

        Assert.False(FileTransferCoordinator.TryValidateCurrentFallbackCheckpointProof(
            leg,
            FileTransferRoute.PostTunaFallbackV6,
            FileTransferProtocol.ProtocolVersionV6,
            requested.State.CurrentLiveRouteEpoch?.EpochId ?? 0,
            proofTransportEpochId: 4,
            proofCheckpointRequestId: null,
            out var missingRequestReason));
        Assert.Equal("checkpoint_request_missing_proof", missingRequestReason);

        Assert.False(FileTransferCoordinator.TryValidateCurrentFallbackCheckpointProof(
            leg,
            FileTransferRoute.PostTunaFallbackV6,
            FileTransferProtocol.ProtocolVersionV6,
            requested.State.CurrentLiveRouteEpoch?.EpochId ?? 0,
            proofTransportEpochId: 5,
            proofCheckpointRequestId: "checkpoint:4",
            out var transportMismatchReason));
        Assert.Equal("transport_epoch_mismatch", transportMismatchReason);

        Assert.False(FileTransferCoordinator.TryValidateCurrentFallbackCheckpointProof(
            leg,
            FileTransferRoute.PostTunaFallbackV6,
            FileTransferProtocol.ProtocolVersionV6,
            requested.State.CurrentLiveRouteEpoch?.EpochId ?? 0,
            proofTransportEpochId: 4,
            proofCheckpointRequestId: "checkpoint:old",
            out var checkpointMismatchReason));
        Assert.Equal("checkpoint_request_mismatch", checkpointMismatchReason);

        Assert.True(FileTransferCoordinator.TryValidateCurrentFallbackCheckpointProof(
            leg,
            FileTransferRoute.PostTunaFallbackV6,
            FileTransferProtocol.ProtocolVersionV6,
            requested.State.CurrentLiveRouteEpoch?.EpochId ?? 0,
            proofTransportEpochId: 4,
            proofCheckpointRequestId: "checkpoint:4",
            out var acceptedReason));
        Assert.Equal("ok", acceptedReason);
    }

    [Fact]
    public void OffOnOffRouteCycle_UsesStrictlyIncreasingEpochsAndNeverFileTunaV6()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);

        var state = StartRegular(regular).State;
        var firstOn = ActivateTuna(state, tuna, committedChunk: 5);
        var firstOff = FallBack(firstOn.State, fallback, committedChunk: 5, transportEpoch: 2);
        var secondOn = ActivateTuna(firstOff.State, tuna, committedChunk: 6);
        var secondOff = FallBack(secondOn.State, fallback, committedChunk: 6, transportEpoch: 3);

        Assert.Equal(1, firstOn.StartedLiveRouteEpoch!.EpochId);
        Assert.Equal(2, firstOff.StartedLiveRouteEpoch!.EpochId);
        Assert.Equal(3, secondOn.StartedLiveRouteEpoch!.EpochId);
        Assert.Equal(4, secondOff.StartedLiveRouteEpoch!.EpochId);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, secondOff.State.RouteSelection.Route);
        Assert.All(
            secondOff.State.LegHistory,
            leg => Assert.NotEqual("file_tuna_v6", leg.RouteSelection.TelemetryToken));
    }

    [Fact]
    public void PostTunaFallbackRecoveryActive_DeniesRuntimeUnlockAdmission()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);

        var state = FallBack(
            ActivateTuna(StartRegular(regular).State, tuna, committedChunk: 5).State,
            fallback,
            committedChunk: 5,
            transportEpoch: 2).State;
        state.CurrentLeg!.State = FileTransferLegState.RecoveryActive;
        state.CurrentLeg.CanSendData = true;
        state.CurrentLeg.CheckpointRequestId = null;

        var admitted = FileTransferCoordinator.CanAdmitRuntimeUnlockWhilePostTunaFallback(
            routeUsesPostTunaFallbackRuntime: true,
            state.CurrentLeg,
            checkpointDeliveryRecoveryPending: false,
            stateRefreshSendInFlight: false,
            deferredStateRefreshPending: false,
            proofReplayPending: false,
            out var rejectionReason);

        Assert.False(admitted);
        Assert.Equal("recovery_active", rejectionReason);
    }

    [Fact]
    public void RuntimeUnlockAdmissionDecision_ReportsFallbackOwnerAndTerminalReasons()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);

        var state = FallBack(
            ActivateTuna(StartRegular(regular).State, tuna, committedChunk: 5).State,
            fallback,
            committedChunk: 5,
            transportEpoch: 2).State;
        state.CurrentLeg!.State = FileTransferLegState.Active;
        state.CurrentLeg.CanSendData = true;

        var blocked = FileTransferCoordinator.EvaluateRuntimeUnlockAdmission(
            isTerminal: false,
            runtimeOwnerCurrent: true,
            routeUsesPostTunaFallbackRuntime: true,
            state.CurrentLeg,
            checkpointDeliveryRecoveryPending: false,
            stateRefreshSendInFlight: false,
            deferredStateRefreshPending: false,
            proofReplayPending: false,
            bridgeRecoveryPending: false,
            controlPlanePressurePending: false,
            fallbackTailRepairPending: true,
            transactionFailed: false);

        Assert.Equal(RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner, blocked.Status);
        Assert.Equal("fallback_tail_repair_pending", blocked.Reason);

        var missingPairedYield = FileTransferCoordinator.EvaluateRuntimeUnlockAdmission(
            isTerminal: false,
            runtimeOwnerCurrent: true,
            routeUsesPostTunaFallbackRuntime: true,
            state.CurrentLeg,
            checkpointDeliveryRecoveryPending: false,
            stateRefreshSendInFlight: false,
            deferredStateRefreshPending: false,
            proofReplayPending: false,
            bridgeRecoveryPending: false,
            controlPlanePressurePending: false,
            fallbackTailRepairPending: false,
            transactionFailed: false);

        Assert.Equal(RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner, missingPairedYield.Status);
        Assert.Equal("paired_fallback_tail_yield_missing", missingPairedYield.Reason);

        var pairedYield = FileTransferCoordinator.EvaluateRuntimeUnlockAdmission(
            isTerminal: false,
            runtimeOwnerCurrent: true,
            routeUsesPostTunaFallbackRuntime: true,
            state.CurrentLeg,
            checkpointDeliveryRecoveryPending: false,
            stateRefreshSendInFlight: false,
            deferredStateRefreshPending: false,
            proofReplayPending: false,
            bridgeRecoveryPending: false,
            controlPlanePressurePending: false,
            fallbackTailRepairPending: false,
            transactionFailed: false,
            pairedFallbackYieldGranted: true);

        Assert.Equal(RuntimeUnlockAdmissionStatus.Granted, pairedYield.Status);
        Assert.Equal("fallback_quiescent", pairedYield.Reason);

        var terminal = FileTransferCoordinator.EvaluateRuntimeUnlockAdmission(
            isTerminal: true,
            runtimeOwnerCurrent: true,
            routeUsesPostTunaFallbackRuntime: true,
            state.CurrentLeg,
            checkpointDeliveryRecoveryPending: false,
            stateRefreshSendInFlight: false,
            deferredStateRefreshPending: false,
            proofReplayPending: false,
            bridgeRecoveryPending: false,
            controlPlanePressurePending: false,
            fallbackTailRepairPending: false,
            transactionFailed: false);

        Assert.Equal(RuntimeUnlockAdmissionStatus.RejectedTerminal, terminal.Status);
        Assert.Equal("transfer_terminal", terminal.Reason);
    }

    [Fact]
    public void RuntimeUnlockAdmission_AcceptsCurrentOwnerWithoutFallbackBlocker()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = StartRegular(regular).State;

        var decision = RequestRuntimeUnlockAdmission(state, state.CurrentRuntimeOwner!);

        var admission = decision.RuntimeUnlockAdmissionDecision!.Value;
        Assert.True(admission.Granted);
        Assert.Equal("not_post_tuna_fallback", admission.Reason);
        Assert.NotNull(admission.AdmissionToken);
        Assert.Equal(state.CurrentRuntimeOwner!.Value.RuntimeGeneration, admission.AdmissionToken!.Value.RuntimeGeneration);
    }

    [Fact]
    public void RuntimeUnlockAdmission_RejectsTerminalState()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = StartRegular(regular).State;
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, regular, reason: "complete"),
            state).State;

        var decision = RequestRuntimeUnlockAdmission(terminal, terminal.CurrentRuntimeOwner);

        Assert.False(decision.RuntimeUnlockAdmissionDecision!.Value.Granted);
        Assert.Equal(RuntimeUnlockAdmissionStatus.RejectedTerminal, decision.RuntimeUnlockAdmissionDecision!.Value.Status);
        Assert.Equal("transfer_terminal", decision.RuntimeUnlockAdmissionDecision!.Value.Reason);
    }

    [Fact]
    public void RuntimeUnlockAdmission_RejectsStaleOwner()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var regularState = StartRegular(regular).State;
        var oldOwner = regularState.CurrentRuntimeOwner!.Value;
        var tunaState = ActivateTuna(regularState, tuna, committedChunk: 4).State;

        var decision = RequestRuntimeUnlockAdmission(tunaState, oldOwner);

        Assert.False(decision.RuntimeUnlockAdmissionDecision!.Value.Granted);
        Assert.Equal(RuntimeUnlockAdmissionStatus.RejectedStaleOwner, decision.RuntimeUnlockAdmissionDecision!.Value.Status);
        Assert.Equal("runtime_generation_mismatch", decision.RuntimeUnlockAdmissionDecision!.Value.Reason);
    }

    [Fact]
    public void RuntimeUnlockAdmission_RejectsActiveFallbackWithoutYield()
    {
        var fallbackState = ActiveFallbackStateWithoutCheckpoint();

        var decision = RequestRuntimeUnlockAdmission(
            fallbackState,
            fallbackState.CurrentRuntimeOwner!,
            new RuntimeUnlockFallbackSurvivalState(
                FallbackActive: true,
                FallbackCurrent: true,
                FallbackTerminalized: false,
                CheckpointDeliveryRecoveryPending: false,
                StateRefreshSendInFlight: false,
                DeferredStateRefreshPending: false,
                ProofReplayPending: false,
                ControlPlanePressurePending: false,
                BridgeRecoveryPending: false,
                FallbackTailRepairPending: false,
                PairedFallbackYieldGranted: false));

        Assert.False(decision.RuntimeUnlockAdmissionDecision!.Value.Granted);
        Assert.Equal("paired_fallback_tail_yield_missing", decision.RuntimeUnlockAdmissionDecision!.Value.Reason);
    }

    [Fact]
    public void RuntimeUnlockAdmission_AcceptsAfterFallbackTerminalized()
    {
        var fallbackState = ActiveFallbackStateWithoutCheckpoint();
        fallbackState.CurrentLeg!.State = FileTransferLegState.Terminal;
        fallbackState.CurrentLeg.CanSendData = false;

        var decision = RequestRuntimeUnlockAdmission(
            fallbackState,
            fallbackState.CurrentRuntimeOwner!,
            new RuntimeUnlockFallbackSurvivalState(
                FallbackActive: true,
                FallbackCurrent: true,
                FallbackTerminalized: true,
                CheckpointDeliveryRecoveryPending: false,
                StateRefreshSendInFlight: false,
                DeferredStateRefreshPending: false,
                ProofReplayPending: false,
                ControlPlanePressurePending: false,
                BridgeRecoveryPending: false,
                FallbackTailRepairPending: false,
                PairedFallbackYieldGranted: false));

        Assert.True(decision.RuntimeUnlockAdmissionDecision!.Value.Granted);
        Assert.Equal("fallback_terminalized", decision.RuntimeUnlockAdmissionDecision!.Value.Reason);
    }

    [Fact]
    public void RuntimeUnlockAdmission_AcceptsAfterPairedYield()
    {
        var fallbackState = ActiveFallbackStateWithoutCheckpoint();

        var decision = RequestRuntimeUnlockAdmission(
            fallbackState,
            fallbackState.CurrentRuntimeOwner!,
            new RuntimeUnlockFallbackSurvivalState(
                FallbackActive: true,
                FallbackCurrent: true,
                FallbackTerminalized: false,
                CheckpointDeliveryRecoveryPending: false,
                StateRefreshSendInFlight: false,
                DeferredStateRefreshPending: false,
                ProofReplayPending: false,
                ControlPlanePressurePending: false,
                BridgeRecoveryPending: false,
                FallbackTailRepairPending: false,
                PairedFallbackYieldGranted: true));

        Assert.True(decision.RuntimeUnlockAdmissionDecision!.Value.Granted);
        Assert.Equal("fallback_quiescent", decision.RuntimeUnlockAdmissionDecision!.Value.Reason);
    }

    [Fact]
    public void RuntimeUnlockAdmission_TokenCarriesCoordinatorAuthorityFields()
    {
        var state = ActiveFallbackStateWithoutCheckpoint();
        var survival = new RuntimeUnlockFallbackSurvivalState(
            FallbackActive: true,
            FallbackCurrent: true,
            FallbackTerminalized: false,
            CheckpointDeliveryRecoveryPending: false,
            StateRefreshSendInFlight: false,
            DeferredStateRefreshPending: false,
            ProofReplayPending: false,
            ControlPlanePressurePending: false,
            BridgeRecoveryPending: false,
            FallbackTailRepairPending: false,
            PairedFallbackYieldGranted: true);

        var decision = RequestRuntimeUnlockAdmission(state, state.CurrentRuntimeOwner!, survival);
        var token = decision.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken!.Value;

        Assert.Equal(state.CurrentRuntimeOwner, token.RuntimeOwner);
        Assert.Equal(state.TerminalGeneration, token.TerminalGeneration);
        Assert.Equal(state.CurrentLiveRouteEpoch!.EpochId, token.LiveRouteEpoch);
        Assert.Equal(state.CurrentLeg!.Generation, token.LegGeneration);
        Assert.Equal(state.CurrentRuntimeOwner!.Value.RuntimeGeneration, token.RuntimeGeneration);
        Assert.Equal(survival, token.FallbackSurvivalState);
    }

    [Fact]
    public void RuntimeUnlockAdmission_TokenBecomesStaleAfterRouteOwnerOrTerminalChange()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var state = StartRegular(regular).State;
        var admitted = RequestRuntimeUnlockAdmission(state, state.CurrentRuntimeOwner!).State;
        var token = admitted.CurrentRuntimeUnlockAdmission!.Value;

        var tunaState = ActivateTuna(admitted, tuna, committedChunk: 3).State;
        Assert.False(FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(tunaState, token, out var routeReason));
        Assert.Equal("runtime_unlock_admission_generation_mismatch", routeReason);

        var terminalState = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, regular, reason: "complete"),
            admitted).State;
        Assert.False(FileTransferCoordinator.IsRuntimeUnlockAdmissionTokenCurrent(terminalState, token, out var terminalReason));
        Assert.Equal("runtime_unlock_admission_generation_mismatch", terminalReason);
    }

    [Fact]
    public void TerminalStateRejectsLaterRouteMutation()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, regular, reason: "completed"),
            StartRegular(regular).State);

        var rejected = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaActivated,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "late_activation",
                FileTransferLegState.Active,
                canSendData: true),
            terminal.State);

        Assert.True(rejected.TerminalMutationRejected);
        Assert.Equal("terminal_state", rejected.MutationRejectedReason);
        Assert.True(rejected.State.IsTerminal);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, rejected.State.RouteSelection.Route);
        Assert.Equal(FileTransferLegState.Terminal, rejected.State.CurrentLeg!.State);
    }

    [Fact]
    public void Apply_Terminalized_IsFirstValidTerminalWins()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);

        var first = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                regular,
                reason: "completed"),
            started.State);
        var second = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                regular,
                reason: "late_failed"),
            first.State);

        Assert.False(first.TerminalMutationRejected);
        Assert.True(first.State.IsTerminal);
        Assert.Equal(1, first.State.TerminalGeneration);
        Assert.Equal(FileTransferLegState.Terminal, first.State.CurrentLeg!.State);
        Assert.True(second.TerminalMutationRejected);
        Assert.Equal("terminal_state", second.MutationRejectedReason);
        Assert.Equal(1, second.State.TerminalGeneration);
        Assert.Equal(FileTransferLegState.Terminal, second.State.CurrentLeg!.State);
    }

    [Fact]
    public void Apply_StartTransferAfterTerminal_DoesNotStartLeg()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                regular,
                reason: "completed"),
            StartRegular(regular).State);
        var previousLegGeneration = terminal.State.LastTransferLegGeneration;
        var previousHistoryCount = terminal.State.LegHistory.Count;

        var rejected = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                regular,
                reason: "late_start",
                state: FileTransferLegState.Active,
                canSendData: true),
            terminal.State);

        Assert.True(rejected.TerminalMutationRejected);
        Assert.Equal("terminal_state", rejected.MutationRejectedReason);
        Assert.Null(rejected.StartedLeg);
        Assert.Equal(previousLegGeneration, rejected.State.LastTransferLegGeneration);
        Assert.Equal(previousHistoryCount, rejected.State.LegHistory.Count);
    }

    [Fact]
    public void Apply_SecondTerminalizedAfterTerminal_DoesNotIncrementTerminalGeneration()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var first = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                regular,
                reason: "completed"),
            StartRegular(regular).State);

        var second = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.Terminalized,
                regular,
                reason: "late_failed"),
            first.State);

        Assert.True(second.TerminalMutationRejected);
        Assert.Equal("terminal_state", second.MutationRejectedReason);
        Assert.Equal(first.State.TerminalGeneration, second.State.TerminalGeneration);
        Assert.Equal(1, second.State.TerminalGeneration);
    }

    [Fact]
    public void Apply_StartTransfer_FreezesPriorLegOnlyWhenAccepted()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);
        var previousLeg = started.State.CurrentLeg!;

        var next = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                regular,
                reason: "restart_leg",
                state: FileTransferLegState.Active,
                canSendData: true),
            started.State);

        Assert.NotNull(next.StartedLeg);
        Assert.Same(previousLeg, next.FrozenLeg);
        Assert.Equal(FileTransferLegState.Frozen, previousLeg.State);
        Assert.False(previousLeg.CanSendData);
        Assert.Equal(2, next.State.LegHistory.Count);
        Assert.Equal(2, next.State.LastTransferLegGeneration);
    }

    [Fact]
    public void Apply_FallbackCheckpointRequested_RejectedWhenLegNotCurrent()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = StartRegular(regular);

        var rejected = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackCheckpointRequested,
                regular,
                reason: "late_checkpoint",
                state: FileTransferLegState.CheckpointPending,
                canSendData: false,
                transportEpoch: 1,
                checkpointRequestId: "checkpoint:late"),
            started.State);

        Assert.False(rejected.TerminalMutationRejected);
        Assert.Equal("fallback_leg_not_current", rejected.MutationRejectedReason);
        Assert.Null(rejected.MutatedLeg);
        Assert.Equal(FileTransferLegState.Active, rejected.State.CurrentLeg!.State);
    }

    [Fact]
    public void Apply_FallbackCheckpointAccepted_RejectedWhenCheckpointMismatches()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var fallbackDecision = FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4);
        var requested = RequestFallbackCheckpoint(fallbackDecision.State, fallback, "checkpoint:4", 4);

        var rejected = AcceptFallbackCheckpoint(requested.State, fallback, "checkpoint:old", 4, 12, 18);

        Assert.False(rejected.TerminalMutationRejected);
        Assert.Equal("checkpoint_request_mismatch", rejected.MutationRejectedReason);
        Assert.Null(rejected.AcceptedCheckpointLeg);
        Assert.Equal("checkpoint:4", rejected.State.CurrentLeg!.CheckpointRequestId);
        Assert.False(rejected.State.CurrentLeg.CanSendData);
    }

    [Fact]
    public void Apply_FallbackCheckpointRetired_MutatesOnlyCurrentFallbackLeg()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var fallbackDecision = FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4);
        var requested = RequestFallbackCheckpoint(fallbackDecision.State, fallback, "checkpoint:4", 4);

        var retired = RetireFallbackCheckpoint(requested.State, fallback, "checkpoint:4", 4);

        Assert.Null(retired.MutationRejectedReason);
        Assert.Same(retired.State.CurrentLeg, retired.MutatedLeg);
        Assert.Null(retired.State.CurrentLeg!.CheckpointRequestId);
        Assert.True(retired.State.CurrentLeg.CanSendData);
        Assert.Equal(FileTransferLegState.RecoveryActive, retired.State.CurrentLeg.State);
    }

    [Fact]
    public void Apply_FallbackTailFailed_MutatesOnlyCurrentFallbackLeg()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var fallbackDecision = FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4);
        var requested = RequestFallbackCheckpoint(fallbackDecision.State, fallback, "checkpoint:4", 4);
        var accepted = AcceptFallbackCheckpoint(requested.State, fallback, "checkpoint:4", 4, 12, 18);

        var failed = MarkFallbackTailFailed(accepted.State, fallback, 4);

        Assert.Null(failed.MutationRejectedReason);
        Assert.Same(failed.State.CurrentLeg, failed.MutatedLeg);
        Assert.Equal(FileTransferLegState.RecoveryActive, failed.State.CurrentLeg!.State);
        Assert.True(failed.State.CurrentLeg.CanSendData);
    }

    [Fact]
    public void Apply_TerminalStateRejectsFallbackLegMutations()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var fallbackDecision = FallBack(StartRegular(regular).State, fallback, committedChunk: 8, transportEpoch: 4);
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, fallback, reason: "completed"),
            fallbackDecision.State);

        var rejected = FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackTailQuiescent,
                fallback,
                reason: "late_tail",
                state: FileTransferLegState.Active,
                canSendData: true,
                committedChunk: 12,
                highestObservedChunk: 18,
                transportEpoch: 4),
            terminal.State);

        Assert.True(rejected.TerminalMutationRejected);
        Assert.Equal("terminal_state", rejected.MutationRejectedReason);
        Assert.Equal(terminal.State.TerminalGeneration, rejected.State.TerminalGeneration);
        Assert.Equal(FileTransferLegState.Terminal, rejected.State.CurrentLeg!.State);
    }

    private static FileTransferCoordinatorDecision StartRegular(FileTransferRouteSelection regular)
        => FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.StartTransfer,
                regular,
                state: FileTransferLegState.Active,
                canSendData: true),
            EmptyState(regular));

    private static FileTransferCoordinatorDecision ActivateTuna(
        FileTransferCoordinatorState state,
        FileTransferRouteSelection tuna,
        int committedChunk)
        => FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaActivated,
                tuna,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "runtime_unlock",
                FileTransferLegState.Active,
                canSendData: true,
                committedChunk: committedChunk,
                highestObservedChunk: committedChunk),
            state);

    private static FileTransferCoordinatorDecision FallBack(
        FileTransferCoordinatorState state,
        FileTransferRouteSelection fallback,
        int committedChunk,
        long transportEpoch)
        => FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.TunaLost,
                fallback,
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn,
                "tuna_disabled",
                FileTransferLegState.CheckpointPending,
                canSendData: false,
                committedChunk: committedChunk,
                highestObservedChunk: committedChunk,
                transportEpoch: transportEpoch,
                bridgeRecoveryGeneration: (int)transportEpoch),
            state);

    private static FileTransferCoordinatorDecision RequestFallbackCheckpoint(
        FileTransferCoordinatorState state,
        FileTransferRouteSelection fallback,
        string checkpointRequestId,
        long transportEpoch)
        => FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackCheckpointRequested,
                fallback,
                reason: "checkpoint_requested",
                state: FileTransferLegState.CheckpointPending,
                canSendData: false,
                transportEpoch: transportEpoch,
                checkpointRequestId: checkpointRequestId,
                checkpointPriority: "state_refresh_tail_reconciliation"),
            state);

    private static FileTransferCoordinatorDecision AcceptFallbackCheckpoint(
        FileTransferCoordinatorState state,
        FileTransferRouteSelection fallback,
        string checkpointRequestId,
        long transportEpoch,
        int committedChunk,
        int highestObservedChunk)
        => FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackCheckpointAccepted,
                fallback,
                reason: "checkpoint_accepted",
                state: FileTransferLegState.RecoveryActive,
                canSendData: true,
                committedChunk: committedChunk,
                highestObservedChunk: highestObservedChunk,
                transportEpoch: transportEpoch,
                checkpointRequestId: checkpointRequestId),
            state);

    private static FileTransferCoordinatorDecision MarkFallbackTailQuiescent(
        FileTransferCoordinatorState state,
        FileTransferRouteSelection fallback,
        long transportEpoch,
        int committedChunk,
        int highestObservedChunk)
        => FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackTailQuiescent,
                fallback,
                reason: "fallback_tail_quiescent",
                state: FileTransferLegState.Active,
                canSendData: true,
                committedChunk: committedChunk,
                highestObservedChunk: highestObservedChunk,
                transportEpoch: transportEpoch),
            state);

    private static FileTransferCoordinatorDecision RetireFallbackCheckpoint(
        FileTransferCoordinatorState state,
        FileTransferRouteSelection fallback,
        string checkpointRequestId,
        long transportEpoch)
        => FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackCheckpointRetired,
                fallback,
                reason: "checkpoint_retired",
                state: FileTransferLegState.RecoveryActive,
                canSendData: true,
                transportEpoch: transportEpoch,
                checkpointRequestId: checkpointRequestId),
            state);

    private static FileTransferCoordinatorDecision MarkFallbackTailFailed(
        FileTransferCoordinatorState state,
        FileTransferRouteSelection fallback,
        long transportEpoch)
        => FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.FallbackTailFailed,
                fallback,
                reason: "fallback_tail_failed",
                state: FileTransferLegState.RecoveryActive,
                canSendData: false,
                transportEpoch: transportEpoch),
            state);

    private static FileTransferCoordinatorState ActiveFallbackStateWithoutCheckpoint()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var tuna = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var state = FallBack(
            ActivateTuna(StartRegular(regular).State, tuna, committedChunk: 5).State,
            fallback,
            committedChunk: 5,
            transportEpoch: 2).State;
        state.CurrentLeg!.State = FileTransferLegState.Active;
        state.CurrentLeg.CanSendData = true;
        state.CurrentLeg.CheckpointRequestId = null;
        return state;
    }

    private static FileTransferCoordinatorDecision RequestRuntimeUnlockAdmission(
        FileTransferCoordinatorState state,
        RuntimeOwnerToken? owner,
        RuntimeUnlockFallbackSurvivalState? survivalState = null)
    {
        var nextState = state;
        if (survivalState is { } survival)
        {
            nextState = FileTransferCoordinator.Apply(
                CoordinatorEvent(
                    FileTransferCoordinatorEventKind.RuntimeUnlockFallbackSurvivalUpdated,
                    state.RouteSelection,
                    reason: "fallback_survival_updated",
                    state: state.CurrentLeg?.State ?? FileTransferLegState.Active,
                    canSendData: state.CurrentLeg?.CanSendData ?? true,
                    transportEpoch: state.CurrentLeg?.TransportEpochId ?? 0,
                    bridgeRecoveryGeneration: state.CurrentLeg?.BridgeRecoveryGeneration ?? 0,
                    runtimeUnlockFallbackSurvivalState: survival),
                state).State;
        }

        return FileTransferCoordinator.Apply(
            CoordinatorEvent(
                FileTransferCoordinatorEventKind.RuntimeUnlockAdmission,
                nextState.RouteSelection,
                reason: "runtime_unlock_admission",
                state: nextState.CurrentLeg?.State ?? FileTransferLegState.Active,
                canSendData: nextState.CurrentLeg?.CanSendData ?? true,
                transportEpoch: nextState.CurrentLeg?.TransportEpochId ?? 0,
                bridgeRecoveryGeneration: nextState.CurrentLeg?.BridgeRecoveryGeneration ?? 0,
                runtimeUnlockAdmissionRequest: new RuntimeUnlockAdmissionRequest(
                    "test",
                    "runtime_unlock_admission",
                    owner)),
            nextState);
    }

    private static RuntimeUnlockRouteCommitProof WithCurrentTunaLease(
        RuntimeUnlockRouteCommitProof proof,
        long leaseGeneration = 11,
        string listenerRunId = "listener-start-11")
        => proof with
        {
            TunaPathLeaseRequired = true,
            TunaPathLeaseGeneration = leaseGeneration,
            TunaPathLeaseState = RuntimeUnlockTunaPathLeaseState.ListenerReady,
            TunaPathLeaseListenerRunId = listenerRunId,
            TunaPathLeaseCurrent = true,
            TunaPathLeaseFailureReason = null,
        };

    private static RuntimeUnlockTransactionSnapshot BuildCommitReadyRuntimeUnlockTransaction(
        FileTransferCoordinatorState state,
        long transactionGeneration = 21,
        long offerGeneration = 22,
        string probeId = "probe-22")
    {
        var transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.OfferGenerationCreated,
                state.SessionId,
                state.TransferId,
                transactionGeneration,
                offerGeneration,
                Reason: "runtime_unlock"),
            RuntimeUnlockTransactionSnapshot.Idle).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.PeerReceived,
                state.SessionId,
                state.TransferId,
                transactionGeneration,
                offerGeneration,
                Reason: "tuna_acceleration_offer_received_raw"),
            transaction).State;
        transaction = RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.AnswerReceived,
                state.SessionId,
                state.TransferId,
                transactionGeneration,
                offerGeneration,
                Reason: "transport_acceleration_answer"),
            transaction).State;
        return RuntimeUnlockTransaction.Apply(
            new RuntimeUnlockTransactionEvent(
                RuntimeUnlockTransactionEventKind.PathProbeAcked,
                state.SessionId,
                state.TransferId,
                transactionGeneration,
                offerGeneration,
                Reason: "transport_probe_ack",
                PathProbeId: probeId,
                PathProbeTransport: FileTransferTransportKind.Tuna),
            transaction).State;
    }

    private static FileTransferCoordinatorState EmptyState(FileTransferRouteSelection routeSelection)
        => new(
            SessionId: "session_coordinator",
            TransferId: "transfer_coordinator",
            Direction: FileTransferDirection.Outbound,
            IsTerminal: false,
            RouteSelection: routeSelection,
            CurrentLiveRouteEpoch: null,
            CurrentLeg: null,
            LastLiveRouteEpochId: 0,
            LastTransferLegGeneration: 0,
            LastRuntimeOwnerGeneration: 0,
            TerminalGeneration: 0,
            CurrentRuntimeOwner: null,
            LegHistory: []);

    private static FileTransferCoordinatorActionRequest ActionRequest(
        FileTransferCoordinatorActionKind kind,
        RuntimeOwnerToken owner,
        FileTransferRoute? route = null,
        int? protocolVersion = null,
        int? liveRouteEpoch = null,
        int? legGeneration = null,
        long? transportEpoch = null,
        int? bridgeRecoveryGeneration = null,
        string? checkpointRequestId = null)
        => new(
            kind,
            Source: "test",
            Reason: "test",
            RuntimeOwner: owner,
            Route: route,
            ProtocolVersion: protocolVersion,
            LiveRouteEpoch: liveRouteEpoch,
            LegGeneration: legGeneration,
            TransportEpochId: transportEpoch,
            BridgeRecoveryGeneration: bridgeRecoveryGeneration,
            CheckpointRequestId: checkpointRequestId);

    private static FileTransferCoordinatorEvent CoordinatorEvent(
        FileTransferCoordinatorEventKind kind,
        FileTransferRouteSelection routeSelection,
        FileTransferTransportHandoffKind handoffKind = FileTransferTransportHandoffKind.None,
        FileTransferTransportKind targetTransport = FileTransferTransportKind.Unknown,
        string reason = "test",
        FileTransferLegState state = FileTransferLegState.Active,
        bool canSendData = true,
        int committedChunk = 0,
        int highestObservedChunk = -1,
        long transportEpoch = 0,
        int bridgeRecoveryGeneration = 0,
        string? checkpointRequestId = null,
        string? checkpointPriority = null,
        RuntimeUnlockRouteCommitProof? runtimeUnlockCommitProof = null,
        RuntimeUnlockAdmissionRequest? runtimeUnlockAdmissionRequest = null,
        RuntimeUnlockFallbackSurvivalState? runtimeUnlockFallbackSurvivalState = null)
        => new(
            kind,
            routeSelection,
            handoffKind,
            targetTransport,
            reason,
            state,
            canSendData,
            committedChunk,
            highestObservedChunk,
            transportEpoch,
            bridgeRecoveryGeneration,
            checkpointRequestId,
            checkpointPriority,
            RuntimeUnlockCommitProof: runtimeUnlockCommitProof,
            RuntimeUnlockAdmissionRequest: runtimeUnlockAdmissionRequest,
            RuntimeUnlockFallbackSurvivalState: runtimeUnlockFallbackSurvivalState);
}
