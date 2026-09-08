using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Fact]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    public void ReceiverBarrier_RealRoutePreparationAtomicallyInvalidatesWithoutApplyReceipt()
        => CreateReceiverBarrierRoutePreparationFixture();

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    public void ReceiverBarrier_RoutePreparationInvalidationSurvivesRealRouteAndTerminalCarries()
        => CreateReceiverBarrierRoutePreparationFixture(completeLifecycle: true);

    internal static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope>
        CreateReceiverBarrierRoutePreparationFixture(bool completeLifecycle = false)
    {
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        void Capture(FileTransferCoordinatorTransitionEnvelope envelope)
        {
            Assert.NotNull(envelope);
            if (envelopes.Count > 0)
            {
                Assert.Equal(envelopes[^1].Aggregate.AfterStateGeneration, envelope.Aggregate.BeforeStateGeneration);
                Assert.Equal(envelopes[^1].Aggregate.AfterStateFingerprint, envelope.Aggregate.BeforeStateFingerprint);
            }
            envelopes.Add(envelope);
        }

        var fixture = CreateReceiverBarrierFixture();
        // These are the retained transaction/offer and payer observations used by
        // the existing real pending-probe/initial-responder fixture below.
        var negotiation = fixture.Negotiation with
        {
            TransactionGeneration = 21,
            OfferGeneration = 22,
            PayerDecisionId = 11,
        };
        var bound = FileTransferCoordinator.BindInitialActivationReceiverBarrier(
            fixture.State, fixture.Source, 1, negotiation);
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, bound.Status);
        Assert.Null(bound.State.CurrentRuntimeUnlockAdmission);
        Capture(bound.SnapshotEnvelope!);

        var setup = CreateRegularV4ToTunaRouteTransitionSetup(bound.State, Capture);
        var activation = Assert.IsType<FileTransferTunaActivationTransitionProofBoundary>(setup.Request.ProofBoundary);
        Assert.Equal(negotiation.TransactionGeneration, activation.PendingPreCommitProbe.TransactionGeneration);
        Assert.Equal(negotiation.OfferGeneration, activation.PendingPreCommitProbe.OfferGeneration);
        negotiation = negotiation with
        {
            PendingAnswerAckGeneration = 7,
            LaunchGeneration = activation.LaunchGeneration,
            TunaPathLeaseGeneration = activation.PendingPreCommitProbe.TunaPathLeaseGeneration,
            TunaPathLeaseListenerRunId = activation.PendingPreCommitProbe.TunaPathLeaseListenerRunId!,
            Ack = new("receiver-barrier-preparation-answer-ack", new string('a', 64), new string('b', 64), 1000),
        };
        var attached = FileTransferCoordinator.AttachInitialActivationReceiverBarrierAdmission(
            setup.State, bound.Authority!, fixture.Source, activation.RuntimeUnlockAdmission, negotiation);
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, attached.Status);
        Capture(attached.SnapshotEnvelope!);

        var leg = attached.State.CurrentLeg!;
        var receiveScope = new RuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
            fixture.Source.RuntimeOwner, fixture.Source.RoutePublication, attached.State.TerminalGeneration,
            leg.Generation, leg.TransportEpochId, leg.BridgeRecoveryGeneration,
            leg.CheckpointRequestId ?? "(none)", leg.CheckpointGeneration,
            attached.State.CurrentDataSessionIdentity!, attached.State.CurrentDataSessionGeneration,
            activation.RuntimeUnlockAdmission, RuntimeUnlockResponderPeerProbeTransitionMode.InitialRegularV4);
        Assert.False(receiveScope.IsDefault);
        var observation = new RuntimeUnlockResponderPeerProbeRouteTransitionObservation(
            attached.State.SessionId, attached.State.TransferId,
            activation.PendingPreCommitProbe.TransactionGeneration,
            activation.PendingPreCommitProbe.OfferGeneration,
            negotiation.PendingAnswerAckGeneration, negotiation.PayerDecisionId, activation.LaunchGeneration,
            Lanes: "file", AnswerAcceptanceSource: "SemanticAnswerAck",
            AckMessageId: negotiation.Ack!.MessageId, AckSourceFingerprint: negotiation.Ack.SourceFingerprint,
            PeerProbeFrameFingerprint: negotiation.Ack.FrameFingerprint,
            activation.PendingPreCommitProbe.TunaPathLeaseGeneration,
            activation.PendingPreCommitProbe.TunaPathLeaseListenerRunId!,
            ProbeId: setup.FinalProof.PathProbeId!, RuntimeUnlockPathProbeState.Acked,
            FileTransferTransportKind.Tuna, ProbeAckedUtcMs: setup.FinalProof.PathProbeAckedUtcMs,
            FileTransferRoute.FileTunaV4, FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportHandoffKind.NormalToTunaActivation, FileTransferTransportKind.Tuna, receiveScope);
        var request = setup.Request with
        {
            DurableCheckpoint = setup.Request.DurableCheckpoint with
            {
                DataSessionGeneration = fixture.Source.DataSessionGeneration,
            },
            ProofBoundary = activation with
            {
                AcceptedLaunchBoundary = setup.FinalProof,
                InitialResponderPeerProbeObservation = observation,
            },
        };
        var beforePreparation = attached.State;
        var beforeFingerprint = FileTransferCoordinator.CalculateSnapshotFingerprint(beforePreparation);
        var priorEvidence = envelopes.Select(envelope => envelope.ToCanonicalJson()).ToArray();
        FileTransferCoordinatorState after;
        if (completeLifecycle)
        {
            var committed = CommitEmptyFileTransferRouteTransitionForTest(
                beforePreparation, request, setup.FinalProof, capture: Capture);
            Assert.Equal(FileTransferRoute.FileTunaV4, committed.State.RouteSelection.Route);
            var fallback = FallBack(committed.State,
                FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6),
                committedChunk: 8, transportEpoch: 1, capture: Capture);
            var checkpoint = RequestFallbackCheckpoint(fallback.State, fallback.State.RouteSelection,
                "receiver-barrier-carry-checkpoint", transportEpoch: 1);
            Assert.Null(checkpoint.MutationRejectedReason);
            Capture(checkpoint.SnapshotEnvelope!);
            var checkpointAccepted = AcceptFallbackCheckpoint(checkpoint.State, fallback.State.RouteSelection,
                "receiver-barrier-carry-checkpoint", transportEpoch: 1, committedChunk: 8, highestObservedChunk: 8);
            Assert.Null(checkpointAccepted.MutationRejectedReason);
            Capture(checkpointAccepted.SnapshotEnvelope!);
            var terminal = FileTransferCoordinator.Apply(
                CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, fallback.State.RouteSelection,
                    reason: "receiver_barrier_preparation_fixture_complete"), checkpointAccepted.State);
            Capture(terminal.SnapshotEnvelope!);
            Assert.True(terminal.State.IsTerminal);
            after = terminal.State;
        }
        else
        {
            var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(beforePreparation, request);
            Assert.Equal(FileTransferRouteTransitionDecisionStatus.Prepared, prepared.Status);
            Assert.NotNull(prepared.PauseBindCommand);
            Assert.Null(prepared.DrainCommand);
            Capture(prepared.SnapshotEnvelope!);
            Assert.Equal(beforePreparation.CurrentRuntimeOwner, prepared.State.CurrentRuntimeOwner);
            Assert.Equal(beforePreparation.CurrentRoutePublicationToken, prepared.State.CurrentRoutePublicationToken);
            Assert.Null(prepared.State.CurrentRouteCommit);
            after = prepared.State;
        }

        var preparation = envelopes.First(envelope => envelope.DecisionKind == "PrepareFileTransferRouteTransition");
        Assert.Equal("Prepared", preparation.DecisionStatus);
        Assert.Equal(preparation.Aggregate.BeforeStateGeneration + 1, preparation.Aggregate.AfterStateGeneration);
        Assert.Equal(preparation.Aggregate.BeforeBoundary, preparation.Aggregate.AfterBoundary);
        var operations = preparation.Aggregate.Operations;
        var removed = Assert.Single(operations.Where(operation => operation.OperationRole == "receiver_barrier_current"));
        var invalidated = Assert.Single(operations.Where(operation => operation.OperationRole == "receiver_barrier_terminal"));
        Assert.Equal("AdmissionAttached", removed.BeforeState);
        Assert.Equal("None", removed.AfterState);
        Assert.Equal("None", invalidated.BeforeState);
        Assert.Equal("Invalidated", invalidated.AfterState);
        Assert.True(removed.StateApplied);
        Assert.True(invalidated.StateApplied);
        Assert.Equal(removed.AuthorityId, invalidated.AuthorityId);
        Assert.Equal(removed.OperationGeneration, invalidated.OperationGeneration);
        Assert.Equal(removed.ReceiverBarrier!.PauseGeneration, invalidated.ReceiverBarrier!.PauseGeneration);
        Assert.Equal(removed.ReceiverBarrier.SourceBoundary, invalidated.ReceiverBarrier.SourceBoundary);
        Assert.Equal(removed.ReceiverBarrier.NegotiationFingerprint, invalidated.ReceiverBarrier.NegotiationFingerprint);
        Assert.Equal(removed.ReceiverBarrier.AdmissionFingerprint, invalidated.ReceiverBarrier.AdmissionFingerprint);
        Assert.Equal("(none)", invalidated.ReceiverBarrier.FailureOriginFingerprint);
        Assert.Equal("receiver_barrier_source_boundary_invalidated", Assert.Single(invalidated.ReceiverBarrier.Receipts).ReceiptReason);
        var current = Assert.Single(operations.Where(operation => operation.Family == "route_transition" &&
            operation.OperationRole == "current"));
        Assert.Equal("RegularV4ToTunaV4", current.OperationKind);
        Assert.Equal("None", current.BeforeState);
        Assert.Equal("Prepared", current.AfterState);
        Assert.True(current.StateApplied);
        Assert.Equal(current.AuthorityId, current.CommandIdentity);
        Assert.Equal(current.OperationGeneration, current.CommandGeneration);
        Assert.NotEqual("(none)", current.CommandFingerprint);
        Assert.Equal("(none)", current.ReceiptIdentity);
        Assert.Equal(0, current.ReceiptGeneration);
        Assert.Equal("(none)", current.ReceiptFingerprint);
        Assert.Equal("(none)", current.ReceiptStatus);
        Assert.False(current.ReceiptValidated);
        Assert.Empty(current.RouteTransition!.Receipts);
        Assert.Equal(1, current.RouteTransition.SourceBoundary.CheckpointGeneration);
        Assert.NotEqual("(none)", current.RouteTransition.SourceBoundary.CheckpointRequestId);
        Assert.Equal(0, invalidated.ReceiverBarrier.SourceBoundary.CheckpointGeneration);
        Assert.Equal("(none)", invalidated.ReceiverBarrier.SourceBoundary.CheckpointRequestId);
        Assert.Null(after.PersistentState.InitialActivationReceiverBarrier.Current);
        Assert.Equal(InitialActivationReceiverBarrierState.Invalidated,
            after.PersistentState.InitialActivationReceiverBarrier.LastTerminal!.State);
        Assert.Same(bound.Authority, after.PersistentState.InitialActivationReceiverBarrier.LastTerminal.Authority);
        Assert.Equal(beforeFingerprint, FileTransferCoordinator.CalculateSnapshotFingerprint(beforePreparation));
        Assert.Equal(InitialActivationReceiverBarrierState.AdmissionAttached,
            beforePreparation.PersistentState.InitialActivationReceiverBarrier.Current!.State);
        Assert.Equal(priorEvidence, envelopes.Take(priorEvidence.Length).Select(envelope => envelope.ToCanonicalJson()));
        Assert.DoesNotContain(envelopes.SelectMany(envelope => envelope.Aggregate.Operations), operation =>
            operation.ReceiverBarrier is not null && operation.AfterState == "Closed");
        return envelopes;
    }
}
