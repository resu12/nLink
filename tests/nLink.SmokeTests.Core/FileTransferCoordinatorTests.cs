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

        var proof = RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "local_only");
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

        var proof = RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received");
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
            started.State);

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

        var proof = RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received");
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
            started.State);

        Assert.True(decision.RuntimeUnlockCommitAccepted);
        Assert.True(decision.RouteChanged);
        Assert.Equal(FileTransferRoute.FileTunaV4, decision.State.RouteSelection.Route);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, decision.State.RouteSelection.ProtocolVersion);
        Assert.NotNull(decision.RecoveredLiveRouteEpoch);
        Assert.Equal(1, decision.RecoveredLiveRouteEpoch!.EpochId);
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

        var proof = RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received") with
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
            started.State);

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

        var proof = RuntimeUnlockTransaction.CreateRouteCommitProof(transaction, "answer_received") with
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
            started.State);

        Assert.True(decision.RuntimeUnlockCommitAccepted);
        Assert.Equal(FileTransferRoute.FileTunaV4, decision.State.RouteSelection.Route);
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

        Assert.True(terminalRejected.TerminalMutationRejected);
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
        Assert.True(rejected.State.IsTerminal);
        Assert.Equal(FileTransferRoute.RegularNknV4Fast, rejected.State.RouteSelection.Route);
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
            LegHistory: []);

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
        RuntimeUnlockRouteCommitProof? runtimeUnlockCommitProof = null)
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
            RuntimeUnlockCommitProof: runtimeUnlockCommitProof);
}
