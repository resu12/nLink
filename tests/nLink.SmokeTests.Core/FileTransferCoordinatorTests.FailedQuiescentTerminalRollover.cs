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
    public void FallbackTailYield_FailedQuiescentSupersessionPreservesExactTerminalRollover(
        FileTransferDirection direction, bool priorTerminal)
        => CreateFailedQuiescentTerminalRolloverSnapshotFixture(direction, priorTerminal, out _, out _, out _);

    [Theory]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Inbound, true)]
    [InlineData(FileTransferDirection.Outbound, true)]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    public void FallbackTailYield_NoPeerFailedQuiescentSupersessionPreservesExactTerminalRollover(
        FileTransferDirection direction, bool priorTerminal)
        => CreateFailedQuiescentTerminalRolloverSnapshotFixture(direction, priorTerminal, out _, out _, out _,
            includeSuccessorHistory: true, includePeerProof: false);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static FileTransferCoordinatorTransitionEnvelope[] CreateFailedQuiescentTerminalRolloverSnapshotFixture(
        FileTransferDirection direction,
        bool priorTerminal,
        out FileTransferCoordinatorTransitionEnvelope supersessionEnvelope,
        out FileTransferCoordinatorTransitionEnvelope failedEnvelope,
        out FileTransferCoordinatorTransitionEnvelope? priorTerminalEnvelope,
        bool includeSuccessorHistory = false,
        bool includePeerProof = true)
    {
        // The prior terminal is born, paired/installed where applicable and retired
        // by the real Coordinator before this branch issues the Failed successor.
        var fixture = CreateCheckpointFailedYieldSource(direction, priorTerminal, includePeerProof);
        var envelope = SupersedeCheckpointFailedYieldWithQuiescence(fixture);
        AssertFailedQuiescentRolloverShape(fixture, envelope, priorTerminal);
        AssertFailedQuiescentRolloverReceipts(fixture, envelope);
        if (priorTerminal)
        {
            AssertFailedQuiescentDisplacedTerminal(fixture, envelope, direction);
        }
        AssertCheckpointPriorEnvelopesImmutable(fixture);
        supersessionEnvelope = envelope;
        failedEnvelope = fixture.FailedEnvelope;
        priorTerminalEnvelope = fixture.PriorTerminalEnvelope;
        if (includeSuccessorHistory)
        {
            if (!includePeerProof) { ObserveNoPeerQuiescentSuccessorPeerProof(fixture); }
            PairAndRetireFailedQuiescentSuccessor(fixture);
            AssertCheckpointPriorEnvelopesImmutable(fixture);
        }
        return fixture.Snapshots.ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ObserveNoPeerQuiescentSuccessorPeerProof(CheckpointFailedYieldFixture fixture)
    {
        var successor = fixture.State.PersistentState.FallbackTailYield.CurrentOperation!.Value;
        Assert.Null(successor.PeerProof);
        var proof = CreateCoordinatorFallbackTailPeerProof(fixture.State, successor.Decision) with
        {
            AuthorityId = new FileTransferAuthorityId("fta2-fallback_tail_yield-fedcba9876543210fedcba98"),
            YieldGeneration = 7,
        };
        fixture.PeerProof = FileTransferCoordinator.CreateFallbackTailPeerProofReceipt(
            fixture.State, proof, "no-peer-successor-later-proof", 45_000);
        var observed = FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt(fixture.State, fixture.PeerProof);
        Assert.True(observed.Accepted, observed.Reason);
        Assert.Equal(FileTransferFallbackTailYieldPairingState.PeerProofObserved,
            observed.State.PersistentState.FallbackTailYield.CurrentOperation!.Value.PairingState);
        Assert.False(observed.State.PersistentState.FallbackTailYield.CurrentOperation!.Value
            .FailedYieldSupersession!.Value.CarriedPeerProof);
        fixture.Capture(Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(observed.SnapshotEnvelope));
        fixture.State = observed.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PairAndRetireFailedQuiescentSuccessor(CheckpointFailedYieldFixture fixture)
    {
        var successor = fixture.State.PersistentState.FallbackTailYield.CurrentOperation!.Value;
        var local = FileTransferCoordinator.CreateFallbackTailLocalProofReceipt(fixture.State, successor.Decision,
            "failed-quiescent-successor-local-proof", "failed-quiescent-successor-local-frame", 50_000);
        var paired = FileTransferCoordinator.ApplyFallbackTailLocalProofReceipt(fixture.State, local);
        Assert.True(paired.Accepted, paired.Reason);
        Assert.NotNull(paired.PairedRelease);
        Assert.Equal(FileTransferFallbackTailYieldPairingState.Paired,
            paired.State.PersistentState.FallbackTailYield.CurrentOperation!.Value.PairingState);
        Assert.Equal(fixture.PeerProof, paired.State.PersistentState.FallbackTailYield.CurrentOperation!.Value.PeerProof);
        fixture.Capture(Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(paired.SnapshotEnvelope));
        fixture.State = paired.State;
        RetirePairedFailedQuiescentSuccessor(fixture, successor.Decision.AuthorityId);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RetirePairedFailedQuiescentSuccessor(
        CheckpointFailedYieldFixture fixture, FileTransferAuthorityId successorAuthority)
    {
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var retired = FileTransferCoordinator.Apply(CoordinatorEvent(
            FileTransferCoordinatorEventKind.StartTransfer, fallback,
            reason: "failed_quiescent_successor_runtime_replaced", state: FileTransferLegState.RecoveryActive,
            canSendData: true, committedChunk: 5, highestObservedChunk: 5,
            transportEpoch: fixture.State.CurrentLeg!.TransportEpochId + 1), fixture.State);
        Assert.Null(retired.MutationRejectedReason);
        var terminal = retired.State.PersistentState.FallbackTailYield.LastTerminalOperation!.Value;
        Assert.Equal(successorAuthority, terminal.Decision.AuthorityId);
        Assert.Equal(fixture.PeerProof, terminal.PeerProof);
        Assert.Equal("runtime_scope_replaced", terminal.InvalidationReason);
        fixture.Capture(Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(retired.SnapshotEnvelope));
        fixture.State = retired.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorTransitionEnvelope SupersedeCheckpointFailedYieldWithQuiescence(
        CheckpointFailedYieldFixture fixture)
    {
        var before = fixture.State;
        var immutableBefore = FileTransferCoordinator.CalculateSnapshotFingerprint(before);
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var successor = MarkFallbackTailQuiescent(before, fallback,
            before.CurrentLeg!.TransportEpochId, committedChunk: 5, highestObservedChunk: 5);
        Assert.Null(successor.MutationRejectedReason);
        var envelope = Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(successor.SnapshotEnvelope);
        fixture.Capture(envelope);
        Assert.Equal(immutableBefore, FileTransferCoordinator.CalculateSnapshotFingerprint(before));
        AssertFailedQuiescentRolloverState(before, successor.State,
            before.PersistentState.FallbackTailYield.CurrentOperation!.Value.PeerProof);
        fixture.State = successor.State;
        return envelope;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertFailedQuiescentRolloverState(
        FileTransferCoordinatorState before,
        FileTransferCoordinatorState after,
        FileTransferFallbackTailPeerProofReceipt? peerProof)
    {
        var failed = before.PersistentState.FallbackTailYield.CurrentOperation!.Value;
        var substate = after.PersistentState.FallbackTailYield;
        var successor = substate.CurrentOperation!.Value;
        var terminal = substate.LastTerminalOperation!.Value;
        Assert.Equal(FallbackTailYieldKind.Quiescent, successor.Decision.YieldKind);
        Assert.Equal(failed.Decision.YieldGeneration + 1, successor.Decision.YieldGeneration);
        Assert.NotEqual(failed.Decision.AuthorityId, successor.Decision.AuthorityId);
        Assert.Null(successor.Decision.PredecessorAuthorityId);
        Assert.Equal(failed.Scope, successor.Scope);
        Assert.Equal(peerProof, successor.PeerProof);
        Assert.Equal(peerProof is null ? FileTransferFallbackTailYieldPairingState.YieldIssued :
            FileTransferFallbackTailYieldPairingState.PeerProofObserved, successor.PairingState);
        Assert.Null(successor.LocalProof);
        Assert.Null(successor.PairedRelease);
        var binding = successor.FailedYieldSupersession!.Value;
        Assert.Equal(failed.Decision, binding.FailedDecision);
        Assert.Equal(successor.Decision, binding.SuccessorDecision);
        Assert.Equal(failed.Scope, binding.Scope);
        Assert.Equal(peerProof is not null, binding.CarriedPeerProof);
        Assert.Equal(failed with
        {
            PairingState = FileTransferFallbackTailYieldPairingState.Invalidated,
            InvalidationReason = "failed_fallback_tail_yield_superseded_by_quiescence",
        }, terminal);
        Assert.Null(substate.RetainedPredecessor);
        Assert.Null(substate.CurrentReservation);
        Assert.Equal(before.PersistentState.FallbackTailYield.LastRetryDispatchRecheckReceipt,
            substate.LastRetryDispatchRecheckReceipt);
    }

    private static void AssertFailedQuiescentRolloverShape(
        CheckpointFailedYieldFixture fixture,
        FileTransferCoordinatorTransitionEnvelope envelope,
        bool priorTerminal)
    {
        Assert.Equal("FallbackTailQuiescent", envelope.DecisionKind);
        Assert.Equal("Accepted", envelope.DecisionStatus);
        Assert.Equal(fixture.FailedEnvelope.Aggregate.AfterStateGeneration, envelope.Aggregate.BeforeStateGeneration);
        Assert.Equal(fixture.FailedEnvelope.Aggregate.AfterStateFingerprint, envelope.Aggregate.BeforeStateFingerprint);
        Assert.Equal(envelope.Aggregate.BeforeStateGeneration + 1, envelope.Aggregate.AfterStateGeneration);
        Assert.NotEqual(envelope.Aggregate.BeforeStateFingerprint, envelope.Aggregate.AfterStateFingerprint);
        Assert.Equal(envelope.Aggregate.BeforeBoundary, envelope.Aggregate.AfterBoundary);
        Assert.Collection(envelope.Aggregate.ChangedFamilies, family => Assert.Equal("fallback_tail_yield", family));
        var operations = envelope.Aggregate.Operations;
        Assert.Equal(priorTerminal ? 4 : 3, operations.Length);
        Assert.All(operations, operation =>
        {
            Assert.Equal("fallback_tail_yield", operation.Family);
            Assert.True(operation.StateApplied);
            Assert.Equal($"fallback_tail_yield|{operation.OperationRole}|{operation.AuthorityId}|{operation.OperationGeneration}|{operation.OperationKind}",
                operation.OperationKey);
        });
        var failed = Assert.Single(operations, operation => operation.OperationRole == "yield_current" &&
            operation.OperationKind == "Failed");
        var current = Assert.Single(operations, operation => operation.OperationRole == "yield_current" &&
            operation.OperationKind == "Quiescent");
        var terminal = Assert.Single(operations, operation => operation.OperationRole == "yield_terminal" &&
            operation.OperationKind == "Failed");
        Assert.Equal(JsonSerializer.Serialize(fixture.FailedCurrent with
        {
            BeforeState = fixture.FailedCurrent.AfterState, AfterState = "None",
        }), JsonSerializer.Serialize(failed));
        Assert.Equal("None", current.BeforeState);
        Assert.Equal(fixture.FailedCurrent.AfterState, current.AfterState);
        Assert.Equal("None", terminal.BeforeState);
        Assert.Equal("Invalidated", terminal.AfterState);
        Assert.Equal(failed.AuthorityId, terminal.AuthorityId);
        Assert.Equal(failed.OperationGeneration, terminal.OperationGeneration);
        Assert.NotEqual(failed.AuthorityId, current.AuthorityId);
        Assert.Equal(failed.OperationGeneration + 1, current.OperationGeneration);
        Assert.Equal("(none)", current.PredecessorAuthorityId);
        Assert.Equal(failed.AuthorityId, current.FallbackTailYield!.FailedPredecessorAuthorityId);
        Assert.Equal(fixture.FailedCurrent.AfterState == "PeerProofObserved", current.FallbackTailYield.FailedSupersessionCarriedPeerProof);
        foreach (var value in new[] { current, terminal })
        {
            var typed = value.FallbackTailYield!;
            Assert.Equal(failed.FallbackTailYield!.SourceBoundary, typed.SourceBoundary);
            Assert.Equal(failed.FallbackTailYield.EffectiveBoundary, typed.EffectiveBoundary);
            Assert.Equal(failed.FallbackTailYield.TargetBoundary, typed.TargetBoundary);
            Assert.Equal(envelope.Aggregate.BeforeBoundary.DataSessionIdentity, typed.SourceBoundary.DataSessionIdentity);
            Assert.Equal(envelope.Aggregate.BeforeBoundary.DataSessionGeneration, typed.SourceBoundary.DataSessionGeneration);
            Assert.Equal("(none)", typed.LocalProofIdentity);
            Assert.Equal("(none)", typed.PairedReleaseAuthorityId);
            Assert.Equal(0, typed.PairingGeneration);
            Assert.Equal(0, typed.ReservationGeneration);
            Assert.Equal(0, typed.ConsumingInstallGeneration);
            Assert.Equal(0, typed.ReauthorizationGeneration);
            Assert.Equal(0, typed.RetryHoldGeneration);
            Assert.Equal(0, typed.RetryRecheckGeneration);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertFailedQuiescentRolloverReceipts(
        CheckpointFailedYieldFixture fixture, FileTransferCoordinatorTransitionEnvelope envelope)
    {
        var operations = envelope.Aggregate.Operations;
        var failed = Assert.Single(operations, operation => operation.OperationRole == "yield_current" &&
            operation.OperationKind == "Failed");
        var current = Assert.Single(operations, operation => operation.OperationRole == "yield_current" &&
            operation.OperationKind == "Quiescent");
        var terminal = Assert.Single(operations, operation => operation.OperationRole == "yield_terminal" &&
            operation.OperationKind == "Failed");
        var hasPeerProof = failed.BeforeState == "PeerProofObserved";
        if (hasPeerProof)
        {
            var pending = Assert.Single(fixture.Snapshots.SelectMany(value => value.Aggregate.Operations),
                operation => operation.OperationRole == "peer_proof_pending_initial" && operation.BeforeState == "None");
            var pendingPeer = Assert.Single(pending.FallbackTailYield!.Receipts, receipt => receipt.Stage == "peer_proof");
            var peer = Assert.Single(failed.FallbackTailYield!.Receipts, receipt => receipt.Stage == "peer_proof");
            Assert.Equal("fallback_tail_peer_proof_retained", pendingPeer.ReceiptReason);
            Assert.Equal(pendingPeer with { ReceiptReason = "fallback_tail_peer_proof_observed" }, peer);
            foreach (var operation in new[] { failed, current, terminal })
            {
                Assert.Equal(peer, Assert.Single(operation.FallbackTailYield!.Receipts, receipt => receipt.Stage == "peer_proof"));
            }
            Assert.Equal(7, peer.ReceiptGeneration);
        }
        else
        {
            foreach (var operation in new[] { failed, current, terminal })
            {
                var typed = operation.FallbackTailYield!;
                Assert.Equal("(none)", typed.PeerProofAuthorityId);
                Assert.Equal("(none)", typed.PeerProofIdentity);
                Assert.Equal("(none)", typed.PeerProofFingerprint);
                Assert.Equal(0, typed.PeerProofYieldGeneration);
                Assert.False(typed.FailedSupersessionCarriedPeerProof);
                Assert.DoesNotContain(typed.Receipts, receipt => receipt.Stage == "peer_proof");
            }
            AssertCheckpointTopLevelReceipt(failed, Assert.Single(failed.FallbackTailYield!.Receipts));
        }
        foreach (var operation in new[] { failed, current, terminal })
        {
            Assert.All(operation.FallbackTailYield!.Receipts, receipt => Assert.True(receipt.ReceiptValidated));
        }
        Assert.Equal(hasPeerProof ? 2 : 1, failed.FallbackTailYield!.Receipts.Length);
        Assert.Equal(hasPeerProof ? 3 : 2, terminal.FallbackTailYield!.Receipts.Length);
        Assert.Equal(hasPeerProof ? 3 : 2, current.FallbackTailYield!.Receipts.Length);
        foreach (var receipt in failed.FallbackTailYield.Receipts)
        {
            Assert.Equal(receipt, Assert.Single(terminal.FallbackTailYield.Receipts, value => value.Stage == receipt.Stage));
        }
        var supersession = Assert.Single(current.FallbackTailYield.Receipts, receipt => receipt.Stage == "failed_supersession");
        var binding = fixture.State.PersistentState.FallbackTailYield.CurrentOperation!.Value.FailedYieldSupersession!.Value;
        Assert.Equal($"{failed.AuthorityId}:supersede", supersession.CommandIdentity);
        Assert.Equal(failed.OperationGeneration, supersession.CommandGeneration);
        Assert.Equal(FileTransferCoordinator.CalculateSnapshotFingerprint(binding.FailedDecision), supersession.CommandFingerprint);
        Assert.Equal($"{current.AuthorityId}:supersession:receipt", supersession.ReceiptIdentity);
        Assert.Equal(current.OperationGeneration, supersession.ReceiptGeneration);
        Assert.Equal("Superseded", supersession.ReceiptStatus);
        Assert.Equal(binding.Reason, supersession.ReceiptReason);
        Assert.Equal(FileTransferCoordinator.CalculateSnapshotFingerprint(binding), supersession.ReceiptFingerprint);
        AssertCheckpointTopLevelReceipt(current, supersession);
        var invalidation = Assert.Single(terminal.FallbackTailYield.Receipts, receipt => receipt.Stage == "invalidation");
        Assert.Equal("failed_fallback_tail_yield_superseded_by_quiescence", invalidation.ReceiptReason);
        AssertCheckpointTopLevelReceipt(terminal, invalidation);
        var yieldReceipt = Assert.Single(current.FallbackTailYield.Receipts, receipt => receipt.Stage == "yield");
        var effect = Assert.IsType<FileTransferCoordinatorAuthorityEffectBinding>(
            SessionFileTransferService.CreateFallbackTailYieldAuthorityEffectBindingForTesting(
                envelope, "yield", "recovery_owner_retired_after_yield"));
        Assert.Equal(current.OperationKey, effect.OperationKey);
        Assert.Equal(current.AuthorityId, effect.AuthorityId);
        Assert.Equal(current.OperationGeneration, effect.OperationGeneration);
        Assert.Equal(yieldReceipt.ReceiptIdentity, effect.ReceiptIdentity);
        Assert.Equal(yieldReceipt.ReceiptFingerprint, effect.ReceiptFingerprint);
    }

    private static void AssertFailedQuiescentDisplacedTerminal(
        CheckpointFailedYieldFixture fixture,
        FileTransferCoordinatorTransitionEnvelope envelope,
        FileTransferDirection direction)
    {
        var prior = Assert.Single(envelope.Aggregate.Operations, operation =>
            operation.OperationRole == "yield_terminal" && operation.OperationKind == "Quiescent");
        Assert.Equal(JsonSerializer.Serialize(fixture.PriorTerminal! with
        {
            BeforeState = "Invalidated", AfterState = "None",
        }), JsonSerializer.Serialize(prior));
        Assert.NotEqual(fixture.FailedCurrent.AuthorityId, prior.AuthorityId);
        Assert.True(prior.OperationGeneration < fixture.FailedCurrent.OperationGeneration);
        Assert.NotEqual(prior.AuthorityId, Assert.Single(envelope.Aggregate.Operations, operation =>
            operation.OperationRole == "yield_current" && operation.OperationKind == "Quiescent").AuthorityId);
        var historical = prior.FallbackTailYield!;
        var current = fixture.FailedCurrent.FallbackTailYield!;
        Assert.True(historical.SourceBoundary.RuntimeGeneration < current.SourceBoundary.RuntimeGeneration);
        Assert.True(historical.SourceBoundary.LegGeneration < current.SourceBoundary.LegGeneration);
        Assert.NotEqual(envelope.Aggregate.BeforeBoundary, historical.SourceBoundary);
        Assert.NotEqual("(none)", historical.LocalProofIdentity);
        Assert.Equal("runtime_scope_replaced", historical.InvalidationReason);
        Assert.All(historical.Receipts, receipt => Assert.True(receipt.ReceiptValidated));
        if (direction == FileTransferDirection.Outbound)
        {
            Assert.NotEqual("(none)", historical.PairedReleaseAuthorityId);
            Assert.True(historical.ReservationGeneration > 0);
            Assert.True(historical.ConsumingInstallGeneration > 0);
            Assert.True(historical.RetryRecheckGeneration > 0);
            Assert.True(historical.SourceBoundary.LiveRouteEpoch < current.SourceBoundary.LiveRouteEpoch);
            Assert.True(historical.SourceBoundary.RoutePublicationGeneration < current.SourceBoundary.RoutePublicationGeneration);
        }
        var birthIndex = fixture.Snapshots.IndexOf(fixture.PriorTerminalEnvelope!);
        Assert.True(birthIndex > 0);
        var retirement = Assert.Single(fixture.PriorTerminalEnvelope!.Aggregate.Operations, operation =>
            operation.OperationRole == "yield_current" && operation.AuthorityId == prior.AuthorityId);
        Assert.Equal("None", retirement.AfterState);
        var priorCurrent = fixture.Snapshots.Take(birthIndex).SelectMany(value => value.Aggregate.Operations)
            .Last(operation => operation.OperationRole == "yield_current" && operation.AuthorityId == prior.AuthorityId &&
                operation.AfterState != "None");
        Assert.Equal(JsonSerializer.Serialize(priorCurrent with
        {
            BeforeState = priorCurrent.AfterState, AfterState = "None",
        }), JsonSerializer.Serialize(retirement));
        foreach (var carried in fixture.Snapshots.Skip(birthIndex + 1).Take(fixture.Snapshots.Count - birthIndex - 2))
        {
            Assert.DoesNotContain(carried.Aggregate.Operations, operation => operation.OperationRole == "yield_terminal");
        }
        for (var index = birthIndex + 1; index < fixture.Snapshots.Count; index++)
        {
            Assert.Equal(fixture.Snapshots[index - 1].Aggregate.AfterStateGeneration,
                fixture.Snapshots[index].Aggregate.BeforeStateGeneration);
            Assert.Equal(fixture.Snapshots[index - 1].Aggregate.AfterStateFingerprint,
                fixture.Snapshots[index].Aggregate.BeforeStateFingerprint);
        }
    }
}
