using System.Runtime.CompilerServices;
using System.Text.Json;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Inbound, true)]
    [InlineData(FileTransferDirection.Outbound, true)]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    [Trait("FileTransferCoordinatorSuite", "FallbackTailYield")]
    public void FallbackTailYield_CheckpointFailedPeerFirstInvalidationPreservesExactTerminalRollover(
        FileTransferDirection direction, bool priorTerminal)
        => CreateCheckpointFailedYieldSnapshotFixture(direction, priorTerminal, out _, out _, out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static FileTransferCoordinatorTransitionEnvelope[] CreateCheckpointFailedYieldSnapshotFixture(
        FileTransferDirection direction,
        bool priorTerminal,
        out FileTransferCoordinatorTransitionEnvelope checkpointEnvelope,
        out FileTransferCoordinatorTransitionEnvelope failedEnvelope,
        out FileTransferCoordinatorTransitionEnvelope? priorTerminalEnvelope)
    {
        // Keep the route setup and the large value-type receipt graph in separate
        // stack frames, as in the retained-recheck ownership fixture.
        var fixture = CreateCheckpointFailedYieldSource(direction, priorTerminal);
        RequestCheckpointFailedYieldInvalidation(fixture);
        AssertCheckpointFailedYieldInvalidation(fixture, priorTerminal);
        AssertCheckpointPriorEnvelopesImmutable(fixture);
        checkpointEnvelope = fixture.CheckpointEnvelope;
        failedEnvelope = fixture.FailedEnvelope;
        priorTerminalEnvelope = fixture.PriorTerminalEnvelope;
        return fixture.Snapshots.ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CheckpointFailedYieldFixture CreateCheckpointFailedYieldSource(
        FileTransferDirection direction, bool priorTerminal, bool includePeerProof = true)
    {
        var fixture = new CheckpointFailedYieldFixture();
        if (priorTerminal)
        {
            if (direction == FileTransferDirection.Outbound)
            {
                fixture.Retained = CreateRetainedRecheckSource();
                AssertOwnedQueuedRecheckSnapshot(fixture.Retained);
                InstallRetainedRecheckSource(fixture.Retained);
                RetireRetainedRecheckRoute(fixture.Retained);
            }
            else
            {
                InitializeCheckpointWithoutPriorTerminal(fixture, direction);
                IssueInboundCheckpointPriorYield(fixture);
                RetireInboundCheckpointPriorYield(fixture);
            }
            CaptureCheckpointPriorTerminal(fixture);
        }
        else
        {
            InitializeCheckpointWithoutPriorTerminal(fixture, direction);
        }
        SettleCheckpointBeforeFailedYield(fixture);
        if (includePeerProof) { RetainCheckpointFailedPeerProof(fixture); }
        IssueCheckpointFailedYield(fixture, includePeerProof);
        return fixture;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CaptureCheckpointPriorTerminal(CheckpointFailedYieldFixture fixture)
    {
        if (fixture.Retained is not null)
        {
            fixture.State = fixture.Retained.State;
            foreach (var envelope in fixture.Retained.Snapshots)
            {
                fixture.Capture(envelope);
            }
        }
        var terminal = fixture.State.PersistentState.FallbackTailYield.LastTerminalOperation!.Value;
        Assert.Equal(FallbackTailYieldKind.Quiescent, terminal.Decision.YieldKind);
        Assert.Equal("runtime_scope_replaced", terminal.InvalidationReason);
        var authority = terminal.Decision.AuthorityId.ToString();
        fixture.PriorTerminalEnvelope = Assert.Single(fixture.Snapshots, envelope =>
            envelope.Aggregate.Operations.Any(operation => operation.Family == "fallback_tail_yield" &&
                operation.OperationRole == "yield_terminal" && operation.AuthorityId == authority &&
                operation.BeforeState == "None" && operation.AfterState == "Invalidated"));
        fixture.PriorTerminal = Assert.Single(fixture.PriorTerminalEnvelope.Aggregate.Operations,
            operation => operation.Family == "fallback_tail_yield" &&
                operation.OperationRole == "yield_terminal" && operation.AuthorityId == authority);
        Assert.Contains(fixture.Snapshots, envelope => envelope.Aggregate.Operations.Any(operation =>
            operation.OperationRole == "yield_current" && operation.AuthorityId == authority &&
            operation.BeforeState == "None"));
        Assert.True(fixture.PriorTerminalEnvelope.Aggregate.BeforeStateGeneration > 0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InitializeCheckpointWithoutPriorTerminal(
        CheckpointFailedYieldFixture fixture, FileTransferDirection direction)
    {
        var source = ActiveFallbackStateWithoutCheckpoint(direction) with
        {
            CurrentDataSessionIdentity = $"checkpoint-failed-peer-first-{direction}",
            CurrentDataSessionGeneration = 1,
        };
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var requested = RequestFallbackCheckpoint(source, fallback, "checkpoint-failed-initial",
            source.CurrentLeg!.TransportEpochId);
        Assert.Null(requested.MutationRejectedReason);
        var accepted = AcceptFallbackCheckpoint(requested.State, fallback, "checkpoint-failed-initial",
            requested.State.CurrentLeg!.TransportEpochId, 5, 5);
        Assert.Null(accepted.MutationRejectedReason);
        fixture.State = accepted.State with
        {
            PersistentState = accepted.State.PersistentState with
            {
                CoordinatorInstanceId = $"checkpoint-failed-peer-first-{direction}",
                SnapshotStateGeneration = 0,
                SnapshotStateFingerprint = null,
                // The fixture starts on an established fallback route.
                FileTransferRouteTransition = default,
            },
        };
        Assert.Null(fixture.State.PersistentState.FallbackTailYield.CurrentOperation);
        Assert.Null(fixture.State.PersistentState.FallbackTailYield.LastTerminalOperation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IssueInboundCheckpointPriorYield(CheckpointFailedYieldFixture fixture)
    {
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var yielded = MarkFallbackTailQuiescent(fixture.State, fallback,
            fixture.State.CurrentLeg!.TransportEpochId, 5, 5);
        Assert.Null(yielded.MutationRejectedReason);
        fixture.Capture(Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(yielded.SnapshotEnvelope));
        var decision = Assert.IsType<FallbackTailYieldDecision>(yielded.FallbackTailYieldDecision);
        var proof = FileTransferCoordinator.CreateFallbackTailLocalProofReceipt(yielded.State, decision,
            "checkpoint-inbound-prior-local-proof", "checkpoint-inbound-prior-local-frame", 30_000);
        var observed = FileTransferCoordinator.ApplyFallbackTailLocalProofReceipt(yielded.State, proof);
        Assert.True(observed.Accepted, observed.Reason);
        fixture.Capture(Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(observed.SnapshotEnvelope));
        fixture.State = observed.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RetireInboundCheckpointPriorYield(CheckpointFailedYieldFixture fixture)
    {
        // Retry-dispatch holds are sender-owned. The receiver instead replaces
        // its fallback runtime through the Coordinator's normal restart path.
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var restarted = FileTransferCoordinator.Apply(CoordinatorEvent(
            FileTransferCoordinatorEventKind.StartTransfer, fallback,
            reason: "checkpoint_inbound_runtime_replaced", state: FileTransferLegState.RecoveryActive,
            canSendData: true, committedChunk: 5, highestObservedChunk: 5, transportEpoch: 4), fixture.State);
        Assert.Null(restarted.MutationRejectedReason);
        fixture.Capture(Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(restarted.SnapshotEnvelope));
        var state = restarted.State;
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;
        var published = FileTransferCoordinator.AuthorizeAction(state,
            new FileTransferCoordinatorActionRequest(FileTransferCoordinatorActionKind.RoutePublication,
                Source: "test", Reason: "checkpoint_inbound_replacement_publication", RuntimeOwner: owner,
                Route: fallback.Route, ProtocolVersion: fallback.ProtocolVersion, LiveRouteEpoch: leg.LiveRouteEpochId,
                LegGeneration: leg.Generation, RuntimeGeneration: owner.RuntimeGeneration,
                TerminalGeneration: state.TerminalGeneration, TargetRoute: fallback.Route,
                TargetProtocolVersion: fallback.ProtocolVersion, TargetTransport: FileTransferTransportKind.RegularNkn));
        Assert.True(published.Accepted, published.RejectionReason);
        fixture.Capture(Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(published.SnapshotEnvelope));
        fixture.State = published.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RetainCheckpointFailedPeerProof(CheckpointFailedYieldFixture fixture)
    {
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        // The preview supplies a valid remote wire proof, not local authority.
        var preview = MarkFallbackTailFailed(fixture.State, fallback, fixture.State.CurrentLeg!.TransportEpochId);
        var decision = Assert.IsType<FallbackTailYieldDecision>(preview.FallbackTailYieldDecision);
        var proof = CreateCoordinatorFallbackTailPeerProof(preview.State, decision) with
        {
            AuthorityId = new FileTransferAuthorityId("fta2-fallback_tail_yield-fedcba9876543210fedcba98"),
            YieldGeneration = 7,
        };
        fixture.PeerProof = FileTransferCoordinator.CreateFallbackTailPeerProofReceipt(
            fixture.State, proof, "checkpoint-failed-peer-first-proof", 40_000);
        var retained = FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt(fixture.State, fixture.PeerProof);
        Assert.True(retained.Accepted, retained.Reason);
        Assert.Null(retained.State.PersistentState.FallbackTailYield.CurrentOperation);
        fixture.Capture(Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(retained.SnapshotEnvelope));
        fixture.State = retained.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SettleCheckpointBeforeFailedYield(CheckpointFailedYieldFixture fixture)
    {
        if (fixture.State.CurrentFallbackCheckpointLease is not null)
        {
            var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
            var leg = fixture.State.CurrentLeg!;
            var accepted = AcceptFallbackCheckpoint(fixture.State, fallback, leg.CheckpointRequestId!,
                leg.TransportEpochId, 5, 5);
            Assert.Null(accepted.MutationRejectedReason);
            fixture.Capture(Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(accepted.SnapshotEnvelope));
            fixture.State = accepted.State;
        }
        Assert.Null(fixture.State.CurrentFallbackCheckpointLease);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IssueCheckpointFailedYield(CheckpointFailedYieldFixture fixture, bool includePeerProof = true)
    {
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var failed = MarkFallbackTailFailed(fixture.State, fallback, fixture.State.CurrentLeg!.TransportEpochId);
        Assert.Null(failed.MutationRejectedReason);
        fixture.FailedEnvelope = Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(failed.SnapshotEnvelope);
        fixture.Capture(fixture.FailedEnvelope);
        var operation = failed.State.PersistentState.FallbackTailYield.CurrentOperation!.Value;
        Assert.Equal(FallbackTailYieldKind.Failed, operation.Decision.YieldKind);
        Assert.Equal(includePeerProof ? FileTransferFallbackTailYieldPairingState.PeerProofObserved :
            FileTransferFallbackTailYieldPairingState.YieldIssued, operation.PairingState);
        if (includePeerProof) { Assert.Equal(fixture.PeerProof, operation.PeerProof); }
        else { Assert.Null(operation.PeerProof); }
        Assert.Null(operation.LocalProof);
        Assert.Null(operation.PairedRelease);
        Assert.Null(operation.Decision.PredecessorAuthorityId);
        var operations = fixture.FailedEnvelope.Aggregate.Operations
            .Where(value => value.Family == "fallback_tail_yield").ToArray();
        Assert.Equal(includePeerProof ? 2 : 1, operations.Length);
        if (includePeerProof)
        {
            var pending = Assert.Single(operations, value => value.OperationRole == "peer_proof_pending_initial");
            Assert.Equal("Pending", pending.BeforeState);
            Assert.Equal("None", pending.AfterState);
        }
        fixture.FailedCurrent = Assert.Single(operations, value => value.OperationRole == "yield_current");
        Assert.Equal("None", fixture.FailedCurrent.BeforeState);
        Assert.Equal(includePeerProof ? "PeerProofObserved" : "YieldIssued", fixture.FailedCurrent.AfterState);
        fixture.State = failed.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RequestCheckpointFailedYieldInvalidation(CheckpointFailedYieldFixture fixture)
    {
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        fixture.PreviousCheckpointGeneration = fixture.State.CurrentLeg!.CheckpointGeneration;
        var requested = RequestFallbackCheckpoint(fixture.State, fallback, "checkpoint-failed-successor",
            fixture.State.CurrentLeg!.TransportEpochId);
        CompleteCheckpointFailedYieldInvalidation(fixture, requested);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CompleteCheckpointFailedYieldInvalidation(CheckpointFailedYieldFixture fixture,
        FileTransferCoordinatorDecision requested)
    {
        Assert.Null(requested.MutationRejectedReason);
        fixture.CheckpointEnvelope = Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(requested.SnapshotEnvelope);
        Assert.Null(requested.State.PersistentState.FallbackTailYield.CurrentOperation);
        Assert.Null(requested.State.PersistentState.FallbackTailYield.PendingPeerProofBeforeLocalYield);
        Assert.Null(requested.State.PersistentState.FallbackTailYield.PendingRetryDispatchRecheckCommand);
        Assert.Null(requested.State.PersistentState.FallbackTailYield.CurrentRetryDispatchHold);
        Assert.Null(requested.State.PersistentState.FallbackTailYield.CurrentReservation);
        var terminal = requested.State.PersistentState.FallbackTailYield.LastTerminalOperation!.Value;
        Assert.Equal(fixture.FailedCurrent.AuthorityId, terminal.Decision.AuthorityId.ToString());
        Assert.Equal("fallback_checkpoint_requested", terminal.InvalidationReason);
        Assert.Equal(fixture.PeerProof, terminal.PeerProof);
        if (fixture.Retained is not null)
        {
            Assert.Equal(fixture.Retained.Receipt,
                requested.State.PersistentState.FallbackTailYield.LastRetryDispatchRecheckReceipt);
        }
        fixture.Capture(fixture.CheckpointEnvelope);
        fixture.State = requested.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertCheckpointFailedYieldInvalidation(CheckpointFailedYieldFixture fixture, bool priorTerminal)
    {
        var envelope = fixture.CheckpointEnvelope;
        Assert.Equal("FallbackCheckpointRequested", envelope.DecisionKind);
        Assert.Equal("Accepted", envelope.DecisionStatus);
        Assert.Equal(envelope.Aggregate.BeforeStateGeneration + 1, envelope.Aggregate.AfterStateGeneration);
        Assert.NotEqual(envelope.Aggregate.BeforeStateFingerprint, envelope.Aggregate.AfterStateFingerprint);
        Assert.Equal(0, envelope.Aggregate.BeforeBoundary.CheckpointGeneration);
        Assert.Equal("(none)", envelope.Aggregate.BeforeBoundary.CheckpointRequestId);
        Assert.Equal(envelope.Aggregate.BeforeBoundary with
        {
            CheckpointGeneration = envelope.Aggregate.AfterBoundary.CheckpointGeneration,
            CheckpointRequestId = envelope.Aggregate.AfterBoundary.CheckpointRequestId,
        }, envelope.Aggregate.AfterBoundary);
        Assert.Equal(fixture.PreviousCheckpointGeneration + 1,
            envelope.Aggregate.AfterBoundary.CheckpointGeneration);
        Assert.Equal("checkpoint-failed-successor", envelope.Aggregate.AfterBoundary.CheckpointRequestId);
        var operations = envelope.Aggregate.Operations.Where(value => value.Family == "fallback_tail_yield").ToArray();
        Assert.Equal(priorTerminal ? 3 : 2, operations.Length);
        var current = Assert.Single(operations, value => value.OperationRole == "yield_current");
        var terminal = Assert.Single(operations, value => value.OperationRole == "yield_terminal" &&
            value.AuthorityId == current.AuthorityId);
        Assert.Equal(JsonSerializer.Serialize(fixture.FailedCurrent with
        {
            BeforeState = "PeerProofObserved", AfterState = "None",
        }), JsonSerializer.Serialize(current));
        Assert.Equal("Failed", current.OperationKind);
        Assert.Equal("None", terminal.BeforeState);
        Assert.Equal("Invalidated", terminal.AfterState);
        Assert.Equal(current.OperationGeneration, terminal.OperationGeneration);
        Assert.Equal(current.PredecessorAuthorityId, terminal.PredecessorAuthorityId);
        Assert.Equal("(none)", current.PredecessorAuthorityId);
        Assert.True(current.StateApplied);
        Assert.True(terminal.StateApplied);
        Assert.Equal(current.FallbackTailYield! with
        {
            Role = "yield_terminal",
            PairingState = "Invalidated",
            InvalidationReason = "fallback_checkpoint_requested",
            Receipts = terminal.FallbackTailYield!.Receipts,
        }, terminal.FallbackTailYield);
        Assert.Equal(2, current.FallbackTailYield.Receipts.Length);
        Assert.Equal(3, terminal.FallbackTailYield.Receipts.Length);
        foreach (var receipt in current.FallbackTailYield.Receipts)
        {
            Assert.Equal(receipt, Assert.Single(terminal.FallbackTailYield.Receipts, value => value.Stage == receipt.Stage));
            Assert.True(receipt.ReceiptValidated);
        }
        var peer = Assert.Single(current.FallbackTailYield.Receipts, value => value.Stage == "peer_proof");
        Assert.Equal(7, peer.ReceiptGeneration);
        Assert.NotEqual(current.OperationGeneration, peer.ReceiptGeneration);
        AssertCheckpointTopLevelReceipt(current, peer);
        var invalidation = Assert.Single(terminal.FallbackTailYield.Receipts, value => value.Stage == "invalidation");
        Assert.Equal("fallback_checkpoint_requested", invalidation.ReceiptReason);
        Assert.Equal(current.OperationGeneration, invalidation.CommandGeneration);
        Assert.Equal(current.OperationGeneration, invalidation.ReceiptGeneration);
        Assert.True(invalidation.ReceiptValidated);
        AssertCheckpointTopLevelReceipt(terminal, invalidation);
        foreach (var value in new[] { current, terminal })
        {
            var typed = value.FallbackTailYield!;
            Assert.Equal("(none)", typed.LocalProofIdentity);
            Assert.Equal("(none)", typed.PairedReleaseAuthorityId);
            Assert.Equal(0, typed.PairingGeneration);
            Assert.Equal(0, typed.ReservationGeneration);
            Assert.Equal(0, typed.ConsumingInstallGeneration);
            Assert.Equal(0, typed.ReauthorizationGeneration);
            Assert.Equal(0, typed.RetryHoldGeneration);
            Assert.Equal(0, typed.RetryRecheckGeneration);
            Assert.Equal(envelope.Aggregate.BeforeBoundary.DataSessionIdentity, typed.SourceBoundary.DataSessionIdentity);
            Assert.Equal(envelope.Aggregate.BeforeBoundary.DataSessionGeneration, typed.SourceBoundary.DataSessionGeneration);
        }
        Assert.DoesNotContain(operations, value => value.OperationRole.StartsWith("retry_", StringComparison.Ordinal));
        Assert.Null(SessionFileTransferService.CreateFallbackTailYieldAuthorityEffectBindingForTesting(
            envelope, "peer_proof", "peer_proof_observed"));
        Assert.Null(SessionFileTransferService.CreateFallbackTailYieldAuthorityEffectBindingForTesting(
            envelope, "RetryDispatchRecheck", "retry_dispatch_recheck_observed"));
        if (priorTerminal)
        {
            var prior = Assert.Single(operations, value => value.OperationRole == "yield_terminal" &&
                value.AuthorityId != current.AuthorityId);
            Assert.Equal(JsonSerializer.Serialize(fixture.PriorTerminal! with
            {
                BeforeState = "Invalidated", AfterState = "None",
            }), JsonSerializer.Serialize(prior));
            Assert.Equal("Quiescent", prior.OperationKind);
            Assert.True(prior.OperationGeneration < current.OperationGeneration);
            Assert.True(prior.FallbackTailYield!.SourceBoundary.RuntimeGeneration < current.FallbackTailYield.SourceBoundary.RuntimeGeneration);
        }
    }

    private static void AssertCheckpointTopLevelReceipt(FileTransferCoordinatorOperationTransitionSnapshot operation,
        FileTransferCoordinatorReceiptTransitionSnapshot receipt)
    {
        Assert.Equal(receipt.CommandIdentity, operation.CommandIdentity);
        Assert.Equal(receipt.CommandGeneration, operation.CommandGeneration);
        Assert.Equal(receipt.CommandFingerprint, operation.CommandFingerprint);
        Assert.Equal(receipt.ReceiptIdentity, operation.ReceiptIdentity);
        Assert.Equal(receipt.ReceiptGeneration, operation.ReceiptGeneration);
        Assert.Equal(receipt.ReceiptStatus, operation.ReceiptStatus);
        Assert.Equal(receipt.ReceiptFingerprint, operation.ReceiptFingerprint);
        Assert.Equal(receipt.ReceiptValidated, operation.ReceiptValidated);
    }

    private static void AssertCheckpointPriorEnvelopesImmutable(CheckpointFailedYieldFixture fixture)
    {
        for (var index = 0; index < fixture.Snapshots.Count; index++)
        {
            Assert.Equal(fixture.ImmutableSnapshots[index], fixture.Snapshots[index].ToCanonicalJson());
        }
    }

    private sealed class CheckpointFailedYieldFixture
    {
        public FileTransferCoordinatorState State = null!;
        public RetainedRecheckFixture? Retained;
        public FileTransferFallbackTailPeerProofReceipt PeerProof;
        public int PreviousCheckpointGeneration;
        public List<FileTransferCoordinatorTransitionEnvelope> Snapshots { get; } = [];
        public List<string> ImmutableSnapshots { get; } = [];
        public FileTransferCoordinatorTransitionEnvelope FailedEnvelope = null!;
        public FileTransferCoordinatorTransitionEnvelope CheckpointEnvelope = null!;
        public FileTransferCoordinatorTransitionEnvelope? PriorTerminalEnvelope;
        public FileTransferCoordinatorOperationTransitionSnapshot FailedCurrent = null!;
        public FileTransferCoordinatorOperationTransitionSnapshot? PriorTerminal;

        public void Capture(FileTransferCoordinatorTransitionEnvelope envelope)
        {
            Snapshots.Add(envelope);
            ImmutableSnapshots.Add(envelope.ToCanonicalJson());
        }
    }
}
