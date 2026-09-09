using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Fact]
    [Trait("FileTransferCoordinatorSuite", "CheckpointRepairContinuity")]
    [Trait("FileTransferCoordinatorSuite", "FileTransferRouteTransition")]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    public void CheckpointContinuity_RealCommittedRouteTransitionRetiresExactOriginOnce()
        => CreateCheckpointRepairContinuityCommittedRetirementFixture();

    internal static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope>
        CreateCheckpointRepairContinuityCommittedRetirementFixture()
    {
        var fixture = CreateCheckpointRepairContinuityFixture(5, 5);
        var state = fixture.State;
        var origin = Assert.IsType<CheckpointRepairContinuityOrigin>(
            state.PersistentState.CheckpointRepairContinuityOrigin);
        var envelopes = fixture.Envelopes.ToList();
        var priorEvidence = envelopes.Select(envelope => envelope.ToCanonicalJson()).ToArray();
        void Capture(FileTransferCoordinatorTransitionEnvelope envelope)
        {
            Assert.NotNull(envelope);
            Assert.Equal(envelopes[^1].Aggregate.AfterStateGeneration, envelope.Aggregate.BeforeStateGeneration);
            Assert.Equal(envelopes[^1].Aggregate.AfterStateFingerprint, envelope.Aggregate.BeforeStateFingerprint);
            envelopes.Add(envelope);
        }

        // The shared source helper republishes the route. Keep the already-current
        // publication here so the real target commit, not setup, retires the origin.
        var leg = state.CurrentLeg!;
        var yielded = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.FallbackTailQuiescent,
                state.RouteSelection, reason: "fallback_tail_quiescent", state: FileTransferLegState.Active,
                canSendData: true, committedChunk: 5, highestObservedChunk: 5,
                transportEpoch: leg.TransportEpochId, bridgeRecoveryGeneration: leg.BridgeRecoveryGeneration), state);
        Assert.Null(yielded.MutationRejectedReason);
        Capture(yielded.SnapshotEnvelope!);
        var tailYield = Assert.IsType<FallbackTailYieldDecision>(yielded.FallbackTailYieldDecision);
        var localReceipt = FileTransferCoordinator.CreateFallbackTailLocalProofReceipt(
            yielded.State, tailYield, "continuity-retirement-local-proof", "continuity-retirement-local-frame", 20_000);
        var localApplied = FileTransferCoordinator.ApplyFallbackTailLocalProofReceipt(yielded.State, localReceipt);
        Assert.True(localApplied.Accepted, localApplied.Reason);
        Capture(localApplied.SnapshotEnvelope!);
        var peerReceipt = FileTransferCoordinator.CreateFallbackTailPeerProofReceipt(
            localApplied.State, CreateCoordinatorFallbackTailPeerProof(localApplied.State, tailYield),
            "continuity-retirement-peer-proof", 20_001);
        var paired = FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt(localApplied.State, peerReceipt);
        Assert.True(paired.Accepted, paired.Reason);
        Capture(paired.SnapshotEnvelope!);
        var release = Assert.IsType<FileTransferFallbackTailPairedReleaseReceipt>(paired.PairedRelease);
        var admitted = RequestRuntimeUnlockAdmission(paired.State, paired.State.CurrentRuntimeOwner);
        Capture(admitted.SnapshotEnvelope!);
        var admission = Assert.IsType<RuntimeUnlockAdmissionToken>(
            admitted.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken);
        var pendingDecision = RecordPendingPreCommitProbe(admitted.State, admission);
        Capture(pendingDecision.SnapshotEnvelope!);
        var pending = Assert.IsType<RuntimeUnlockPendingPreCommitProbeRequest>(
            pendingDecision.State.CurrentRuntimeUnlockPendingPreCommitProbe);
        var pendingState = pendingDecision.State;
        var pendingLeg = pendingState.CurrentLeg!;
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(pendingState,
            pending.TransactionGeneration, pending.OfferGeneration, "probe-continuity-retirement-responder");
        var finalProof = WithCurrentTunaLease(RuntimeUnlockTransaction.CreateRouteCommitProof(
            transaction, "runtime_unlock_transaction_peer_proof", admission),
            pending.TunaPathLeaseGeneration, pending.TunaPathLeaseListenerRunId!);
        var receiveScope = new RuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
            pendingState.CurrentRuntimeOwner!.Value, pendingState.CurrentRoutePublicationToken!.Value,
            pendingState.TerminalGeneration, pendingLeg.Generation, pendingLeg.TransportEpochId,
            pendingLeg.BridgeRecoveryGeneration, pendingLeg.CheckpointRequestId ?? "(none)",
            pendingLeg.CheckpointGeneration, pendingState.CurrentDataSessionIdentity!,
            pendingState.CurrentDataSessionGeneration, admission,
            RuntimeUnlockResponderPeerProbeTransitionMode.PostFallbackV6Paired);
        var observation = new RuntimeUnlockResponderPeerProbeRouteTransitionObservation(
            pending.SessionId, pending.TransferId, pending.TransactionGeneration, pending.OfferGeneration,
            PendingAnswerAckGeneration: 5, PayerDecisionId: 7, LaunchGeneration: 31,
            Lanes: "file", AnswerAcceptanceSource: "SemanticAnswerAck",
            AckMessageId: "continuity-retirement-answer-ack", AckSourceFingerprint: new string('b', 64),
            PeerProbeFrameFingerprint: new string('c', 64), pending.TunaPathLeaseGeneration,
            pending.TunaPathLeaseListenerRunId, finalProof.PathProbeId!, RuntimeUnlockPathProbeState.Acked,
            FileTransferTransportKind.Tuna, finalProof.PathProbeAckedUtcMs, FileTransferRoute.FileTunaV4,
            FileTransferProtocol.ProtocolVersionV4, FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna, receiveScope);
        var bound = FileTransferCoordinator.BindRuntimeUnlockResponderPeerProbeRouteTransitionHandoff(
            pendingState, observation, admission, pending);
        Assert.True(bound.Status == RuntimeUnlockResponderPeerProbeRouteTransitionHandoffStatus.Accepted, bound.Reason);
        Capture(bound.SnapshotEnvelope!);
        var responder = Assert.IsType<RuntimeUnlockResponderPeerProbeRouteTransitionHandoffAuthority>(bound.Authority);
        var request = new FileTransferRouteTransitionPrepareRequest(
            FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4,
            bound.State.CurrentRuntimeOwner!.Value, bound.State.CurrentRoutePublicationToken!.Value,
            SourceRouteCommit: null, FileTransferRoute.PostTunaFallbackV6, FileTransferProtocol.ProtocolVersionV6,
            FileTransferTransportKind.RegularNkn,
            new FileTransferTunaActivationTransitionProofBoundary(admission, pending,
                finalProof with { Reason = "runtime_unlock_responder_peer_probe_acked" }, observation.LaunchGeneration,
                EffectiveRetryInstallReceipt: null, release, responder),
            new FileTransferRouteTransitionDurableCheckpoint(bound.State.CurrentRuntimeOwner.Value,
                bound.State.CurrentRoutePublicationToken.Value, bound.State.TerminalGeneration,
                pendingLeg.Generation, pendingLeg.TransportEpochId, pendingLeg.BridgeRecoveryGeneration,
                bound.State.CurrentDataSessionGeneration, StateEpoch: 5, ContiguousCommittedChunkIndex: 6,
                DurableHighestChunkIndex: 7, ChunkCount: 128, CheckpointGeneration: 3),
            FileTransferRoute.FileTunaV4, FileTransferProtocol.ProtocolVersionV4, FileTransferTransportKind.Tuna,
            FileTransferTransportHandoffKind.NormalToTunaActivation, PredecessorTransitionAuthorityId: null,
            "continuity_retirement_responder_transition");
        Assert.Same(origin, bound.State.PersistentState.CheckpointRepairContinuityOrigin);
        Assert.Equal(state.CurrentRuntimeOwner, bound.State.CurrentRuntimeOwner);
        Assert.Equal(state.CurrentRoutePublicationToken, bound.State.CurrentRoutePublicationToken);
        var preparationProbe = FileTransferCoordinator.PrepareFileTransferRouteTransition(bound.State, request);
        Assert.True(preparationProbe.Status == FileTransferRouteTransitionDecisionStatus.Prepared, preparationProbe.Reason);
        var committed = CommitEmptyFileTransferRouteTransitionForTest(
            bound.State, request, finalProof, invalidateRuntimeUnlockRetryLifecycle: false, capture: Capture);

        var acceptance = Assert.Single(envelopes, envelope => envelope.DecisionKind == "FallbackCheckpointAccepted");
        var entering = Assert.Single(acceptance.Aggregate.Operations, operation =>
            operation.OperationRole == "checkpoint_acceptance_retained");
        var commit = Assert.Single(envelopes, envelope => envelope.DecisionKind == "CommitFileTransferRouteTransition");
        Assert.Equal("Committed", commit.DecisionStatus);
        Assert.Equal("post_tuna_fallback_v6", commit.Aggregate.BeforeBoundary.Route);
        Assert.Equal(6, commit.Aggregate.BeforeBoundary.ProtocolVersion);
        Assert.Equal("file_tuna_v4", commit.Aggregate.AfterBoundary.Route);
        Assert.Equal(4, commit.Aggregate.AfterBoundary.ProtocolVersion);
        Assert.Equal(commit.Aggregate.BeforeStateGeneration + 1, commit.Aggregate.AfterStateGeneration);
        Assert.NotEqual(commit.Aggregate.BeforeStateFingerprint, commit.Aggregate.AfterStateFingerprint);
        Assert.Equal(commit.Aggregate.BeforeBoundary.DataSessionIdentity, commit.Aggregate.AfterBoundary.DataSessionIdentity);
        Assert.Equal(commit.Aggregate.BeforeBoundary.DataSessionGeneration, commit.Aggregate.AfterBoundary.DataSessionGeneration);
        var removed = Assert.Single(envelopes.SelectMany(envelope => envelope.Aggregate.Operations), operation =>
            operation.OperationRole == "checkpoint_acceptance_retained" && operation.AfterState == "None");
        Assert.Contains(removed, commit.Aggregate.Operations);
        Assert.Equal("Retained", removed.BeforeState);
        Assert.True(removed.StateApplied);
        Assert.Same(origin.Snapshot, removed.CheckpointRepairContinuity);
        Assert.Equal(entering.CheckpointRepairContinuity, removed.CheckpointRepairContinuity);
        Assert.Equal(entering.OperationKey, removed.OperationKey);
        Assert.Equal(entering.ReceiptIdentity, removed.ReceiptIdentity);
        Assert.Equal(entering.ReceiptFingerprint, removed.ReceiptFingerprint);
        Assert.Equal(origin.Snapshot.AcceptedStateGeneration, removed.ReceiptGeneration);
        Assert.Contains(envelopes, envelope => envelope.DecisionKind == "PrepareFileTransferRouteTransition" &&
            envelope.DecisionStatus == "Prepared");
        Assert.Contains(envelopes, envelope => envelope.DecisionKind == "BeginFileTransferRouteTransitionPhysicalDrain" &&
            envelope.DecisionStatus == "Draining");
        Assert.Contains(commit.Aggregate.Operations, operation => operation.OperationRole == "route_publication" &&
            operation.StateApplied && operation.ReceiptValidated);
        Assert.Equal("ApplyFileTransferRouteTransitionDispositionReceipt", envelopes[^1].DecisionKind);
        Assert.Equal(FileTransferRoute.FileTunaV4, committed.State.RouteSelection.Route);
        Assert.NotNull(committed.State.CurrentRoutePublicationToken);
        Assert.Null(committed.State.PersistentState.CheckpointRepairContinuityOrigin);

        var retired = FileTransferCoordinator.ValidateCheckpointRepairContinuity(committed.State, fixture.Authority);
        Assert.Same(committed.State, retired.State);
        Assert.Equal("NotApplicable", retired.Status);
        Assert.Null(retired.Stamp);
        Assert.Null(retired.SnapshotEnvelope);
        var restoredScope = state with { PersistentState = committed.State.PersistentState };
        var aba = FileTransferCoordinator.ValidateCheckpointRepairContinuity(restoredScope, fixture.Authority);
        Assert.Same(restoredScope, aba.State);
        Assert.Equal("NotApplicable", aba.Status);
        Assert.Null(aba.Stamp);
        Assert.Same(origin, state.PersistentState.CheckpointRepairContinuityOrigin);
        Assert.Equal(priorEvidence, envelopes.Take(priorEvidence.Length).Select(envelope => envelope.ToCanonicalJson()));
        return envelopes.AsReadOnly();
    }
}
