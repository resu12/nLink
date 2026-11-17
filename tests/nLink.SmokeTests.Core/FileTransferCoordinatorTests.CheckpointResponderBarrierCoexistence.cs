using System.Runtime.CompilerServices;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Fact]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    [Trait("FileTransferCoordinatorSuite", "ResponderProbeHandoff")]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    public void Checkpoint_IndependentlyRetainsInvalidatedResponderHandoffAndReceiverBarrier()
        => CreateCheckpointResponderBarrierCoexistenceSnapshotFixture();

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope>
        CreateCheckpointResponderBarrierCoexistenceSnapshotFixture()
    {
        FileTransferCoordinatorState? committedInitial = null;
        var envelopes = CreateReceiverBarrierRoutePreparationFixture(
            completeLifecycle: true, observeInitialCommit: state => committedInitial = state).ToList();
        Assert.NotNull(committedInitial);
        var initialEvidence = envelopes.Select(envelope => envelope.ToCanonicalJson()).ToArray();
        void Capture(FileTransferCoordinatorTransitionEnvelope envelope)
        {
            Assert.NotNull(envelope);
            Assert.Equal(envelopes[^1].Aggregate.AfterStateGeneration, envelope.Aggregate.BeforeStateGeneration);
            Assert.Equal(envelopes[^1].Aggregate.AfterStateFingerprint, envelope.Aggregate.BeforeStateFingerprint);
            envelopes.Add(envelope);
        }

        var state = EnterCheckpointResponderFallback(committedInitial!, 1, Capture);
        var paired = CreateCheckpointResponderCoexistencePairedHandoff(state, Capture);
        var committedPaired = CommitEmptyFileTransferRouteTransitionForTest(
            paired.State, paired.Request, paired.FinalProof,
            invalidateRuntimeUnlockRetryLifecycle: false, capture: Capture);
        Assert.Equal(FileTransferRoute.FileTunaV4, committedPaired.State.RouteSelection.Route);
        state = EnterCheckpointResponderFallback(committedPaired.State, 2, Capture);
        var fallback = state.RouteSelection;
        var request = RequestFallbackCheckpoint(state, fallback, "coexisting-retained-authorities-checkpoint",
            state.CurrentLeg!.TransportEpochId);
        Assert.Null(request.MutationRejectedReason);
        Capture(request.SnapshotEnvelope!);
        var accepted = AcceptFallbackCheckpoint(request.State, fallback, "coexisting-retained-authorities-checkpoint",
            request.State.CurrentLeg!.TransportEpochId, committedChunk: 8, highestObservedChunk: 8);
        Assert.Null(accepted.MutationRejectedReason);
        Capture(accepted.SnapshotEnvelope!);

        var barrierBirth = Assert.Single(envelopes.SelectMany(envelope => envelope.Aggregate.Operations), operation =>
            operation.OperationRole == "receiver_barrier_terminal" && operation.BeforeState == "None" &&
            operation.AfterState == "Invalidated");
        var handoffBirth = Assert.Single(envelopes.SelectMany(envelope => envelope.Aggregate.Operations), operation =>
            operation.OperationRole == "handoff_terminal" && operation.BeforeState == "None" &&
            operation.AfterState == "Invalidated");
        Assert.NotEqual(barrierBirth.AuthorityId, handoffBirth.AuthorityId);
        Assert.NotEqual(barrierBirth.OperationKey, handoffBirth.OperationKey);
        Assert.True(barrierBirth.ReceiptValidated);
        Assert.True(handoffBirth.ReceiptValidated);
        foreach (var envelope in new[] { request.SnapshotEnvelope!, accepted.SnapshotEnvelope! })
        {
            var sameFamily = envelope.Aggregate.Operations.Where(operation => operation.Family == "responder_probe_handoff").ToArray();
            Assert.Equal(2, sameFamily.Length);
            var barrier = Assert.Single(sameFamily, operation => operation.OperationRole == "receiver_barrier_terminal" &&
                operation.OperationKind == "InitialActivationReceiverBarrier");
            var handoff = Assert.Single(sameFamily, operation => operation.OperationRole == "handoff_terminal" &&
                operation.OperationKind == "ResponderPeerProbeRouteTransitionHandoff");
            foreach (var (birth, carry) in new[] { (barrierBirth, barrier), (handoffBirth, handoff) })
            {
                Assert.Equal("Invalidated", carry.BeforeState);
                Assert.Equal("Invalidated", carry.AfterState);
                Assert.Equal(birth.AuthorityId, carry.AuthorityId);
                Assert.Equal(birth.OperationKey, carry.OperationKey);
                Assert.Equal(birth.OperationGeneration, carry.OperationGeneration);
                Assert.Equal(birth.CommandIdentity, carry.CommandIdentity);
                Assert.Equal(birth.CommandGeneration, carry.CommandGeneration);
                Assert.Equal(birth.CommandFingerprint, carry.CommandFingerprint);
                Assert.Equal(birth.ReceiptIdentity, carry.ReceiptIdentity);
                Assert.Equal(birth.ReceiptGeneration, carry.ReceiptGeneration);
                Assert.Equal(birth.ReceiptFingerprint, carry.ReceiptFingerprint);
                Assert.True(carry.ReceiptValidated);
            }
            Assert.Equal(barrierBirth.ReceiverBarrier!.SourceBoundary, barrier.ReceiverBarrier!.SourceBoundary);
            Assert.Equal(handoffBirth.ResponderProbeHandoff!.SourceBoundary, handoff.ResponderProbeHandoff!.SourceBoundary);
            Assert.Equal(envelope.Aggregate.AfterBoundary, barrier.ReceiverBarrier.EffectiveBoundary);
            Assert.Equal(envelope.Aggregate.AfterBoundary, handoff.ResponderProbeHandoff.EffectiveBoundary);
            Assert.Equal(envelope.Aggregate.BeforeStateGeneration + 1, envelope.Aggregate.AfterStateGeneration);
            Assert.NotEqual(envelope.Aggregate.BeforeStateFingerprint, envelope.Aggregate.AfterStateFingerprint);
        }
        Assert.Equal(initialEvidence, envelopes.Take(initialEvidence.Length).Select(envelope => envelope.ToCanonicalJson()));
        Assert.Equal(InitialActivationReceiverBarrierState.Invalidated,
            committedInitial!.PersistentState.InitialActivationReceiverBarrier.LastTerminal!.State);
        return envelopes;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorState EnterCheckpointResponderFallback(
        FileTransferCoordinatorState state, long transportEpoch,
        Action<FileTransferCoordinatorTransitionEnvelope> capture)
    {
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        state = FallBack(state, fallback, committedChunk: 8, transportEpoch, capture).State;
        if (state.CurrentFallbackCheckpointLease is { } bootstrap)
        {
            var accepted = AcceptFallbackCheckpoint(state, fallback, bootstrap.CheckpointRequestId,
                state.CurrentLeg!.TransportEpochId, committedChunk: 8, highestObservedChunk: 8);
            Assert.Null(accepted.MutationRejectedReason);
            capture(accepted.SnapshotEnvelope!);
            state = accepted.State;
        }
        return state;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (FileTransferCoordinatorState State, FileTransferRouteTransitionPrepareRequest Request,
        RuntimeUnlockRouteCommitProof FinalProof) CreateCheckpointResponderCoexistencePairedHandoff(
        FileTransferCoordinatorState state, Action<FileTransferCoordinatorTransitionEnvelope> capture,
        bool pairYield = true)
    {
        if (pairYield) { state = PairCheckpointResponderCoexistenceYield(state, capture); }
        var release = Assert.IsType<FileTransferFallbackTailPairedReleaseReceipt>(
            FileTransferCoordinator.GetFallbackTailYieldSubstate(state).CurrentOperation!.Value.PairedRelease);
        var admitted = RequestRuntimeUnlockAdmission(state, state.CurrentRuntimeOwner);
        capture(admitted.SnapshotEnvelope!);
        var admission = Assert.IsType<RuntimeUnlockAdmissionToken>(admitted.RuntimeUnlockAdmissionDecision!.Value.AdmissionToken);
        var pendingDecision = RecordPendingPreCommitProbe(admitted.State, admission);
        capture(pendingDecision.SnapshotEnvelope!);
        var pending = Assert.IsType<RuntimeUnlockPendingPreCommitProbeRequest>(pendingDecision.State.CurrentRuntimeUnlockPendingPreCommitProbe);
        state = pendingDecision.State;
        var leg = state.CurrentLeg!;
        var scope = new RuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
            state.CurrentRuntimeOwner!.Value, state.CurrentRoutePublicationToken!.Value, state.TerminalGeneration,
            leg.Generation, leg.TransportEpochId, leg.BridgeRecoveryGeneration,
            leg.CheckpointRequestId ?? "(none)", leg.CheckpointGeneration,
            state.CurrentDataSessionIdentity!, state.CurrentDataSessionGeneration, admission);
        var observation = new RuntimeUnlockResponderPeerProbeRouteTransitionObservation(
            pending.SessionId, pending.TransferId, pending.TransactionGeneration, pending.OfferGeneration,
            PendingAnswerAckGeneration: 5, PayerDecisionId: 7, LaunchGeneration: 31,
            Lanes: "file", AnswerAcceptanceSource: "SemanticAnswerAck", AckMessageId: "coexistence-answer-ack",
            AckSourceFingerprint: new string('b', 64), PeerProbeFrameFingerprint: new string('c', 64),
            pending.TunaPathLeaseGeneration, pending.TunaPathLeaseListenerRunId,
            ProbeId: "coexistence-paired-responder-probe", RuntimeUnlockPathProbeState.Acked,
            FileTransferTransportKind.Tuna, ProbeAckedUtcMs: 40_100,
            FileTransferRoute.FileTunaV4, FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportHandoffKind.NormalToTunaActivation, FileTransferTransportKind.Tuna, scope);
        var bound = FileTransferCoordinator.BindRuntimeUnlockResponderPeerProbeRouteTransitionHandoff(
            state, observation, admission, pending);
        Assert.Equal(RuntimeUnlockResponderPeerProbeRouteTransitionHandoffStatus.Accepted, bound.Status);
        capture(bound.SnapshotEnvelope!);
        var authority = Assert.IsType<RuntimeUnlockResponderPeerProbeRouteTransitionHandoffAuthority>(bound.Authority);
        var transaction = BuildCommitReadyRuntimeUnlockTransaction(bound.State, pending.TransactionGeneration,
            pending.OfferGeneration, observation.ProbeId);
        var proof = WithCurrentTunaLease(RuntimeUnlockTransaction.CreateRouteCommitProof(
            transaction, "runtime_unlock_transaction_peer_proof", admission),
            pending.TunaPathLeaseGeneration, pending.TunaPathLeaseListenerRunId!);
        var owner = bound.State.CurrentRuntimeOwner!.Value;
        var publication = bound.State.CurrentRoutePublicationToken!.Value;
        var checkpoint = new FileTransferRouteTransitionDurableCheckpoint(owner, publication,
            bound.State.TerminalGeneration, leg.Generation, leg.TransportEpochId, leg.BridgeRecoveryGeneration,
            bound.State.CurrentDataSessionGeneration, StateEpoch: 5, ContiguousCommittedChunkIndex: 8,
            DurableHighestChunkIndex: 8, ChunkCount: 128, CheckpointGeneration: 3);
        var request = new FileTransferRouteTransitionPrepareRequest(
            FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4, owner, publication, SourceRouteCommit: null,
            FileTransferRoute.PostTunaFallbackV6, FileTransferProtocol.ProtocolVersionV6, FileTransferTransportKind.RegularNkn,
            new FileTransferTunaActivationTransitionProofBoundary(admission, pending,
                proof with { Reason = "runtime_unlock_responder_peer_probe_acked" }, observation.LaunchGeneration,
                EffectiveRetryInstallReceipt: null, release, authority), checkpoint,
            FileTransferRoute.FileTunaV4, FileTransferProtocol.ProtocolVersionV4, FileTransferTransportKind.Tuna,
            FileTransferTransportHandoffKind.NormalToTunaActivation, PredecessorTransitionAuthorityId: null,
            "checkpoint_coexistence_paired_responder_transition");
        return (bound.State, request, proof);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorState PairCheckpointResponderCoexistenceYield(
        FileTransferCoordinatorState state, Action<FileTransferCoordinatorTransitionEnvelope> capture)
    {
        var yielded = MarkFallbackTailQuiescent(state, state.RouteSelection, state.CurrentLeg!.TransportEpochId,
            committedChunk: 8, highestObservedChunk: 8);
        Assert.Null(yielded.MutationRejectedReason);
        capture(yielded.SnapshotEnvelope!);
        var yield = Assert.IsType<FallbackTailYieldDecision>(yielded.FallbackTailYieldDecision);
        var local = FileTransferCoordinator.CreateFallbackTailLocalProofReceipt(yielded.State, yield,
            "coexistence-local-proof", new string('a', 64), 40_000);
        var localApplied = FileTransferCoordinator.ApplyFallbackTailLocalProofReceipt(yielded.State, local);
        Assert.True(localApplied.Accepted, localApplied.Reason);
        capture(localApplied.SnapshotEnvelope!);
        var peer = FileTransferCoordinator.CreateFallbackTailPeerProofReceipt(localApplied.State,
            CreateCoordinatorFallbackTailPeerProof(localApplied.State, yield), "coexistence-peer-proof", 40_001);
        var paired = FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt(localApplied.State, peer);
        Assert.True(paired.Accepted, paired.Reason);
        capture(paired.SnapshotEnvelope!);
        Assert.NotNull(paired.PairedRelease);
        Assert.Null(FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(paired.State).InstalledAttempt);
        return paired.State;
    }
}
