using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Fact]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    public void ReceiverBarrier_PreAdmissionBindExactClosePreservesRouteAndCannotRevive()
    {
        var fixture = CreateReceiverBarrierFixture();
        var bound = FileTransferCoordinator.BindInitialActivationReceiverBarrier(fixture.State, fixture.Source, 1, fixture.Negotiation);
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, bound.Status);
        Assert.Equal(InitialActivationReceiverBarrierState.Bound, bound.State.PersistentState.InitialActivationReceiverBarrier.Current!.State);
        Assert.Null(bound.State.CurrentRuntimeUnlockAdmission);
        var json = bound.SnapshotEnvelope!.ToCanonicalJson();
        var admitted = AdmitReceiverBarrier(bound.State);
        var negotiation = fixture.Negotiation with { PendingAnswerAckGeneration = 6 };
        var attached = FileTransferCoordinator.AttachInitialActivationReceiverBarrierAdmission(admitted,
            bound.Authority!, fixture.Source, admitted.CurrentRuntimeUnlockAdmission!.Value, negotiation);
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, attached.Status);
        var prepared = FileTransferCoordinator.PrepareInitialActivationReceiverBarrierClose(attached.State,
            bound.Authority!, fixture.Source, 1, negotiation, ReceiverBarrierOrigin());
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, prepared.Status);
        var receipt = new InitialActivationReceiverBarrierCloseReceipt(prepared.Command!,
            InitialActivationReceiverBarrierCloseApplyStatus.Applied, "exact_bridge_release");
        var committed = FileTransferCoordinator.CommitInitialActivationReceiverBarrierClose(prepared.State, fixture.Source, receipt);
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, committed.Status);
        Assert.Null(committed.State.PersistentState.InitialActivationReceiverBarrier.Current);
        Assert.Equal(InitialActivationReceiverBarrierState.Closed, committed.State.PersistentState.InitialActivationReceiverBarrier.LastTerminal!.State);
        Assert.Equal(fixture.State.CurrentRuntimeOwner, committed.State.CurrentRuntimeOwner);
        Assert.Equal(fixture.State.CurrentRoutePublicationToken, committed.State.CurrentRoutePublicationToken);
        Assert.Null(committed.State.CurrentRouteCommit);
        Assert.Equal(json, bound.SnapshotEnvelope.ToCanonicalJson());
        var duplicate = FileTransferCoordinator.CommitInitialActivationReceiverBarrierClose(committed.State, fixture.Source,
            receipt with { Status = InitialActivationReceiverBarrierCloseApplyStatus.Idempotent });
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Idempotent, duplicate.Status);
        Assert.Equal(committed.State.PersistentState.SnapshotStateGeneration, duplicate.State.PersistentState.SnapshotStateGeneration);
        Assert.All(duplicate.SnapshotEnvelope!.Aggregate.Operations, operation => Assert.False(operation.StateApplied));
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Stale,
            FileTransferCoordinator.BindInitialActivationReceiverBarrier(committed.State, fixture.Source, 1, fixture.Negotiation).Status);
        var successor = FileTransferCoordinator.BindInitialActivationReceiverBarrier(committed.State, fixture.Source, 2, fixture.Negotiation);
        Assert.True(successor.Authority!.BarrierGeneration > bound.Authority!.BarrierGeneration);
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Stale,
            FileTransferCoordinator.CommitInitialActivationReceiverBarrierClose(successor.State, fixture.Source, receipt).Status);
        Assert.Equal(successor.Authority, successor.State.PersistentState.InitialActivationReceiverBarrier.Current!.Authority);
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("identity")]
    [InlineData("generation")]
    [InlineData("pause")]
    [InlineData("nonce")]
    [InlineData("admission")]
    [InlineData("origin")]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    public void ReceiverBarrier_StaleCloseScopeDoesNotMutate(string mutation)
    {
        var fixture = CreateReceiverBarrierFixture();
        var bound = FileTransferCoordinator.BindInitialActivationReceiverBarrier(fixture.State, fixture.Source, 1, fixture.Negotiation);
        var state = AdmitReceiverBarrier(bound.State);
        var attached = FileTransferCoordinator.AttachInitialActivationReceiverBarrierAdmission(state, bound.Authority!,
            fixture.Source, state.CurrentRuntimeUnlockAdmission!.Value, fixture.Negotiation);
        state = attached.State;
        var source = fixture.Source;
        var negotiation = fixture.Negotiation;
        var origin = ReceiverBarrierOrigin();
        if (mutation == "reference") source = source with { DataSession = new ReceiverBarrierDataSession(state.SessionId, state.TransferId) };
        if (mutation == "identity") source = source with { DataSessionIdentity = "different" };
        if (mutation == "generation") source = source with { DataSessionGeneration = 2 };
        if (mutation == "nonce") negotiation = negotiation with { Nonce = "different" };
        if (mutation == "admission") state = state with { CurrentRuntimeUnlockAdmission = null };
        if (mutation == "origin") origin = origin with { ClientGeneration = 0 };
        var result = FileTransferCoordinator.PrepareInitialActivationReceiverBarrierClose(state, bound.Authority!, source,
            mutation == "pause" ? 2 : 1, negotiation, origin);
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Stale, result.Status);
        Assert.Null(result.Command);
        Assert.Equal(state.PersistentState.InitialActivationReceiverBarrier, result.State.PersistentState.InitialActivationReceiverBarrier);
    }

    [Theory]
    [InlineData("Rejected")]
    [InlineData("Stale")]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    public void ReceiverBarrier_RejectedBridgeReceiptRetainsPreparedOperation(string statusName)
    {
        var status = Enum.Parse<InitialActivationReceiverBarrierCloseApplyStatus>(statusName);
        var fixture = CreateReceiverBarrierFixture();
        var bound = FileTransferCoordinator.BindInitialActivationReceiverBarrier(fixture.State, fixture.Source, 1, fixture.Negotiation);
        var admitted = AdmitReceiverBarrier(bound.State);
        var attached = FileTransferCoordinator.AttachInitialActivationReceiverBarrierAdmission(admitted,
            bound.Authority!, fixture.Source, admitted.CurrentRuntimeUnlockAdmission!.Value, fixture.Negotiation);
        var prepared = FileTransferCoordinator.PrepareInitialActivationReceiverBarrierClose(attached.State,
            bound.Authority!, fixture.Source, 1, fixture.Negotiation, ReceiverBarrierOrigin());
        var result = FileTransferCoordinator.CommitInitialActivationReceiverBarrierClose(prepared.State, fixture.Source,
            new(prepared.Command!, status, "not_applied"));
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Stale, result.Status);
        Assert.Equal(prepared.State.PersistentState.InitialActivationReceiverBarrier, result.State.PersistentState.InitialActivationReceiverBarrier);
        Assert.All(result.SnapshotEnvelope!.Aggregate.Operations, operation => Assert.False(operation.StateApplied));
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    public void ReceiverBarrier_CommonSnapshotFinalizerInvalidatesChangedDataSession()
    {
        var fixture = CreateReceiverBarrierFixture();
        var bound = FileTransferCoordinator.BindInitialActivationReceiverBarrier(fixture.State, fixture.Source, 1, fixture.Negotiation);
        var changed = bound.State with { CurrentDataSessionIdentity = "successor", CurrentDataSessionGeneration = 2 };
        var snapshot = FileTransferCoordinator.CreateTransitionSnapshot(bound.State, changed,
            bound.State.PersistentState.CoordinatorInstanceId!, bound.State.PersistentState.SnapshotStateGeneration,
            "ReceiverDataSessionReplaced", "Accepted", "source_replaced");
        Assert.Null(snapshot.State.PersistentState.InitialActivationReceiverBarrier.Current);
        Assert.Equal(InitialActivationReceiverBarrierState.Invalidated,
            snapshot.State.PersistentState.InitialActivationReceiverBarrier.LastTerminal!.State);
        Assert.Contains(snapshot.Envelope.Aggregate.Operations, operation => operation.ReceiverBarrier is not null &&
            operation.OperationRole == "receiver_barrier_terminal" && operation.AfterState == "Invalidated");
    }

    private static (FileTransferCoordinatorState State, InitialActivationReceiverBarrierSource Source,
        InitialActivationReceiverBarrierNegotiation Negotiation) CreateReceiverBarrierFixture()
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = StartRegular(regular, FileTransferDirection.Inbound).State with
        {
            CurrentDataSessionIdentity = "receiver-barrier-data:1", CurrentDataSessionGeneration = 1,
        };
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;
        var published = FileTransferCoordinator.AuthorizeAction(state,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test",
                Reason: "receiver_barrier_source_publication",
                RuntimeOwner: owner,
                Route: regular.Route,
                ProtocolVersion: regular.ProtocolVersion,
                LiveRouteEpoch: leg.LiveRouteEpochId,
                LegGeneration: leg.Generation,
                RuntimeGeneration: owner.RuntimeGeneration,
                TerminalGeneration: state.TerminalGeneration,
                TargetRoute: regular.Route,
                TargetProtocolVersion: regular.ProtocolVersion,
                TargetTransport: FileTransferTransportKind.RegularNkn));
        Assert.True(published.Accepted, published.RejectionReason);
        state = published.State;
        state = state with { PersistentState = state.PersistentState with { CoordinatorInstanceId = "receiver-barrier-coordinator" } };
        var session = new ReceiverBarrierDataSession(state.SessionId, state.TransferId);
        var source = FileTransferCoordinator.CreateInitialActivationReceiverBarrierSource(state, session);
        Assert.NotNull(source);
        return (state, source!, new(state.SessionId, state.TransferId, 1, 6, 0, "offer-nonce", 4, 0, 0, ""));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    public void ReceiverBarrier_AdmissionInvalidationRetainsExactReplacementFingerprint(bool replacementPresent)
    {
        var fixture = CreateReceiverBarrierFixture();
        var bound = FileTransferCoordinator.BindInitialActivationReceiverBarrier(fixture.State, fixture.Source, 1, fixture.Negotiation);
        var admitted = AdmitReceiverBarrier(bound.State);
        var attached = FileTransferCoordinator.AttachInitialActivationReceiverBarrierAdmission(admitted, bound.Authority!,
            fixture.Source, admitted.CurrentRuntimeUnlockAdmission!.Value, fixture.Negotiation);
        var originalJson = attached.SnapshotEnvelope!.ToCanonicalJson();
        RuntimeUnlockAdmissionToken? replacement = replacementPresent
            ? admitted.CurrentRuntimeUnlockAdmission.Value with { AdmissionGeneration = admitted.CurrentRuntimeUnlockAdmission.Value.AdmissionGeneration + 1 }
            : null;
        var after = attached.State with { CurrentRuntimeUnlockAdmission = replacement };
        var snapshot = FileTransferCoordinator.CreateTransitionSnapshot(attached.State, after,
            attached.State.PersistentState.CoordinatorInstanceId!, attached.State.PersistentState.SnapshotStateGeneration,
            "RuntimeUnlockAdmission", "Accepted", "admission_replaced");
        var terminal = snapshot.State.PersistentState.InitialActivationReceiverBarrier.LastTerminal!;
        Assert.Equal("receiver_barrier_admission_replaced", terminal.Reason);
        Assert.Equal(0, terminal.ReplacementPauseGeneration);
        var expected = replacement is { } token
            ? FileTransferCoordinator.CalculateSnapshotFingerprint(token)
            : "(none)";
        Assert.Equal(expected, terminal.InvalidatingAdmissionFingerprint);
        var payload = Assert.Single(snapshot.Envelope.Aggregate.Operations.Where(operation =>
            operation.OperationRole == "receiver_barrier_terminal")).ReceiverBarrier!;
        Assert.Equal(expected, payload.InvalidatingAdmissionFingerprint);
        Assert.Equal(0, payload.ReplacementPauseGeneration);
        Assert.Equal(originalJson, attached.SnapshotEnvelope.ToCanonicalJson());
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    public void ReceiverBarrier_PauseInvalidationRequiresAndRetainsExactNewGeneration()
    {
        var fixture = CreateReceiverBarrierFixture();
        var bound = FileTransferCoordinator.BindInitialActivationReceiverBarrier(fixture.State, fixture.Source, 1, fixture.Negotiation);
        foreach (var generation in new long[] { 0, 1 })
        {
            var rejected = FileTransferCoordinator.InvalidateInitialActivationReceiverBarrier(bound.State,
                bound.Authority!, "receiver_barrier_pause_replaced", generation);
            Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Stale, rejected.Status);
            Assert.Equal(bound.State.PersistentState.InitialActivationReceiverBarrier,
                rejected.State.PersistentState.InitialActivationReceiverBarrier);
        }
        var invalidated = FileTransferCoordinator.InvalidateInitialActivationReceiverBarrier(bound.State,
            bound.Authority!, "receiver_barrier_pause_replaced", 2);
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Invalidated, invalidated.Status);
        Assert.Equal(2, invalidated.State.PersistentState.InitialActivationReceiverBarrier.LastTerminal!.ReplacementPauseGeneration);
        var payload = Assert.Single(invalidated.SnapshotEnvelope!.Aggregate.Operations.Where(operation =>
            operation.OperationRole == "receiver_barrier_terminal")).ReceiverBarrier!;
        Assert.Equal(2, payload.ReplacementPauseGeneration);
        Assert.Equal("(none)", payload.InvalidatingAdmissionFingerprint);
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Stale,
            FileTransferCoordinator.InvalidateInitialActivationReceiverBarrier(invalidated.State,
                bound.Authority!, "receiver_barrier_pause_replaced", 3).Status);
    }

    private static FileTransferCoordinatorState AdmitReceiverBarrier(FileTransferCoordinatorState state)
    {
        var admitted = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.RuntimeUnlockAdmission,
            state.RouteSelection, runtimeUnlockAdmissionRequest: new("receiver_barrier_test", "precommit_probe_received",
                state.CurrentRuntimeOwner)), state).State;
        Assert.NotNull(admitted.CurrentRuntimeUnlockAdmission);
        return admitted;
    }

    private static InitialActivationReceiverBarrierFailureOrigin ReceiverBarrierOrigin()
        => new(Guid.NewGuid(), 1, "exact-process-run", 1);

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    public void ReceiverBarrier_LaterSemanticAckWithoutPeerProbeAttachesAndCloses()
    {
        var fixture = CreateReceiverBarrierFixture();
        var bound = FileTransferCoordinator.BindInitialActivationReceiverBarrier(fixture.State, fixture.Source, 1, fixture.Negotiation);
        var admitted = AdmitReceiverBarrier(bound.State);
        var attached = FileTransferCoordinator.AttachInitialActivationReceiverBarrierAdmission(admitted,
            bound.Authority!, fixture.Source, admitted.CurrentRuntimeUnlockAdmission!.Value, fixture.Negotiation);
        var beforeAck = attached.SnapshotEnvelope!.ToCanonicalJson();
        var acknowledged = fixture.Negotiation with
        {
            PendingAnswerAckGeneration = 6,
            AnswerMessageId = "receiver-answer",
            AnswerDeadlineUtcMs = 1000,
            Ack = new("receiver-answer-ack", new string('a', 64), new string('b', 64), 900),
        };
        var ackAttached = FileTransferCoordinator.AttachInitialActivationReceiverBarrierAdmission(attached.State,
            bound.Authority!, fixture.Source, admitted.CurrentRuntimeUnlockAdmission.Value, acknowledged);
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, ackAttached.Status);
        Assert.Null(acknowledged.PeerProbe);
        Assert.Equal(acknowledged, ackAttached.State.PersistentState.InitialActivationReceiverBarrier.Current!.Negotiation);
        Assert.Same(bound.Authority, ackAttached.Authority);
        Assert.Equal(beforeAck, attached.SnapshotEnvelope.ToCanonicalJson());
        var duplicate = FileTransferCoordinator.AttachInitialActivationReceiverBarrierAdmission(ackAttached.State,
            bound.Authority!, fixture.Source, admitted.CurrentRuntimeUnlockAdmission.Value, acknowledged);
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Idempotent, duplicate.Status);
        Assert.All(duplicate.SnapshotEnvelope!.Aggregate.Operations, operation => Assert.False(operation.StateApplied));
        var prepared = FileTransferCoordinator.PrepareInitialActivationReceiverBarrierClose(ackAttached.State,
            bound.Authority!, fixture.Source, 1, acknowledged, ReceiverBarrierOrigin());
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, prepared.Status);
        var closed = FileTransferCoordinator.CommitInitialActivationReceiverBarrierClose(prepared.State, fixture.Source,
            new(prepared.Command!, InitialActivationReceiverBarrierCloseApplyStatus.Applied, "exact_ack_without_probe_close"));
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, closed.Status);
        Assert.Equal(InitialActivationReceiverBarrierState.Closed,
            closed.State.PersistentState.InitialActivationReceiverBarrier.LastTerminal!.State);
        Assert.Equal(fixture.Source.RuntimeOwner, closed.State.CurrentRuntimeOwner);
        Assert.Equal(fixture.Source.RoutePublication, closed.State.CurrentRoutePublicationToken);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    public void ReceiverBarrier_EstablishedSemanticAckCannotBeChangedOrRemoved()
    {
        var fixture = CreateReceiverBarrierFixture();
        var bound = FileTransferCoordinator.BindInitialActivationReceiverBarrier(fixture.State, fixture.Source, 1, fixture.Negotiation);
        var admitted = AdmitReceiverBarrier(bound.State);
        var acknowledged = fixture.Negotiation with
        {
            PendingAnswerAckGeneration = 6,
            AnswerMessageId = "receiver-answer",
            AnswerDeadlineUtcMs = 1000,
            Ack = new("receiver-answer-ack", new string('a', 64), new string('b', 64), 900),
        };
        var attached = FileTransferCoordinator.AttachInitialActivationReceiverBarrierAdmission(admitted,
            bound.Authority!, fixture.Source, admitted.CurrentRuntimeUnlockAdmission!.Value, acknowledged);
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, attached.Status);
        var evidence = attached.SnapshotEnvelope!.ToCanonicalJson();
        foreach (var changed in new[]
        {
            acknowledged with { Ack = null },
            acknowledged with { Ack = acknowledged.Ack! with { MessageId = "successor-ack" } },
            acknowledged with { Ack = acknowledged.Ack! with { SourceFingerprint = new string('c', 64) } },
            acknowledged with { PendingAnswerAckGeneration = 7 },
            acknowledged with { AnswerMessageId = "successor-answer" },
            acknowledged with { AnswerDeadlineUtcMs = 1001 },
            acknowledged with { AnswerMessageId = null, AnswerDeadlineUtcMs = 0 },
        })
        {
            var rejected = FileTransferCoordinator.AttachInitialActivationReceiverBarrierAdmission(attached.State,
                bound.Authority!, fixture.Source, admitted.CurrentRuntimeUnlockAdmission.Value, changed);
            Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Stale, rejected.Status);
            Assert.Equal(attached.State.PersistentState.InitialActivationReceiverBarrier,
                rejected.State.PersistentState.InitialActivationReceiverBarrier);
            Assert.Null(rejected.Command);
            Assert.All(rejected.SnapshotEnvelope!.Aggregate.Operations, operation => Assert.False(operation.StateApplied));
        }
        Assert.Equal(evidence, attached.SnapshotEnvelope.ToCanonicalJson());
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    public void ReceiverBarrier_RealTerminalDecisionInvalidatesPreparedCloseAndRejectsLateReceipt()
    {
        var fixture = CreateReceiverBarrierFixture();
        var bound = FileTransferCoordinator.BindInitialActivationReceiverBarrier(fixture.State, fixture.Source, 1, fixture.Negotiation);
        var admitted = AdmitReceiverBarrier(bound.State);
        var attached = FileTransferCoordinator.AttachInitialActivationReceiverBarrierAdmission(admitted,
            bound.Authority!, fixture.Source, admitted.CurrentRuntimeUnlockAdmission!.Value, fixture.Negotiation);
        var prepared = FileTransferCoordinator.PrepareInitialActivationReceiverBarrierClose(attached.State,
            bound.Authority!, fixture.Source, 1, fixture.Negotiation, ReceiverBarrierOrigin());
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, prepared.Status);
        var evidence = prepared.SnapshotEnvelope!.ToCanonicalJson();
        var terminal = FileTransferCoordinator.Apply(
            CoordinatorEvent(FileTransferCoordinatorEventKind.Terminalized, prepared.State.RouteSelection,
                reason: "receiver_barrier_transfer_terminal"), prepared.State);
        Assert.True(terminal.State.IsTerminal);
        Assert.Null(terminal.State.PersistentState.InitialActivationReceiverBarrier.Current);
        Assert.Equal(InitialActivationReceiverBarrierState.Invalidated,
            terminal.State.PersistentState.InitialActivationReceiverBarrier.LastTerminal!.State);
        Assert.Contains(terminal.SnapshotEnvelope!.Aggregate.Operations, operation =>
            operation.OperationRole == "receiver_barrier_terminal" && operation.AfterState == "Invalidated" && operation.StateApplied);
        Assert.Null(FileTransferCoordinator.CreateInitialActivationReceiverBarrierSource(terminal.State, fixture.Source.DataSession));
        var late = FileTransferCoordinator.CommitInitialActivationReceiverBarrierClose(terminal.State, fixture.Source,
            new(prepared.Command!, InitialActivationReceiverBarrierCloseApplyStatus.Applied, "late_after_terminal"));
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Stale, late.Status);
        Assert.Equal(terminal.State.PersistentState.InitialActivationReceiverBarrier,
            late.State.PersistentState.InitialActivationReceiverBarrier);
        Assert.All(late.SnapshotEnvelope!.Aggregate.Operations, operation => Assert.False(operation.StateApplied));
        Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Rejected,
            FileTransferCoordinator.BindInitialActivationReceiverBarrier(terminal.State, fixture.Source, 2, fixture.Negotiation).Status);
        Assert.Equal(evidence, prepared.SnapshotEnvelope.ToCanonicalJson());
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "InitialActivationReceiverBarrier")]
    public void ReceiverBarrierFence_AuthorityCopiesShareRevocationWithoutChangingEvidence()
    {
        var fixture = CreateReceiverBarrierFixture();
        var bound = FileTransferCoordinator.BindInitialActivationReceiverBarrier(fixture.State, fixture.Source, 1, fixture.Negotiation);
        var authority = bound.Authority!;
        var copy = authority with { };
        var stateCopy = bound.State with { };
        Assert.Same(authority.ExecutionFence, copy.ExecutionFence);
        Assert.Same(authority.ExecutionFence,
            stateCopy.PersistentState.InitialActivationReceiverBarrier.Current!.Authority.ExecutionFence);
        var fingerprint = FileTransferCoordinator.CalculateSnapshotFingerprint(authority);
        var evidence = bound.SnapshotEnvelope!.ToCanonicalJson();
        authority.ExecutionFence.Invalidate();
        Assert.False(copy.ExecutionFence.TryApply(() => true));
        Assert.False(copy.ExecutionFence.TryFinalize(() => throw new InvalidOperationException("must not execute")));
        Assert.Equal(fingerprint, FileTransferCoordinator.CalculateSnapshotFingerprint(authority));
        Assert.Equal(evidence, bound.SnapshotEnvelope.ToCanonicalJson());
        Assert.DoesNotContain("executionFence", evidence, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ReceiverBarrierDataSession(string sessionId, string transferId) : IFileTransferDataSession
    {
        public string SessionId => sessionId;
        public string TransferId => transferId;
        public bool IsAvailable => false;
        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged { add { } remove { } }
        public void Dispose() { }
        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct) => throw new NotSupportedException();
    }
}
