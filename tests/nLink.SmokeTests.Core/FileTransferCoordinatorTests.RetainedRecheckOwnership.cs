using System.Reflection;
using System.Runtime.CompilerServices;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    [Trait("FileTransferCoordinatorSuite", "FallbackTailYield")]
    public void FallbackTailYield_FreshFailedYieldDoesNotInheritRetainedRecheck(bool peerFirst)
        => CreateFailedYieldAfterRetainedRecheckSnapshotFixture(peerFirst);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static FileTransferCoordinatorTransitionEnvelope[]
        CreateFailedYieldAfterRetainedRecheckSnapshotFixture(bool peerFirst)
        => CreateFailedYieldAfterRetainedRecheckSnapshotFixture(peerFirst, out _, out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static FileTransferCoordinatorTransitionEnvelope[]
        CreateFailedYieldAfterRetainedRecheckSnapshotFixture(
            bool peerFirst,
            out FileTransferCoordinatorTransitionEnvelope freshFailedEnvelope,
            out FileTransferCoordinatorTransitionEnvelope queuedRecheckEnvelope)
    {
        var fixture = CreateRetainedRecheckSource();
        AssertOwnedQueuedRecheckSnapshot(fixture);
        InstallRetainedRecheckSource(fixture);
        RetireRetainedRecheckRoute(fixture);
        if (peerFirst)
        {
            RetainInitialProofAfterRecheckRetirement(fixture);
        }
        IssueFreshFailedYieldAfterRecheckRetirement(fixture, peerFirst);
        AssertRetainedReceiptDuplicateAfterFreshYield(fixture);
        AssertFreshFailedYieldAttempts(fixture);
        Assert.Equal(fixture.ImmutableQueued, fixture.QueuedEnvelope.ToCanonicalJson());
        Assert.Equal(fixture.ImmutableFailed, fixture.FailedEnvelope.ToCanonicalJson());
        freshFailedEnvelope = fixture.FailedEnvelope;
        queuedRecheckEnvelope = fixture.QueuedEnvelope;
        return fixture.Snapshots.ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertOwnedQueuedRecheckSnapshot(RetainedRecheckFixture fixture)
    {
        fixture.QueuedEnvelope = fixture.Snapshots[^1];
        fixture.ImmutableQueued = fixture.QueuedEnvelope.ToCanonicalJson();
        var terminal = Assert.Single(fixture.QueuedEnvelope.Aggregate.Operations,
            operation => operation.OperationRole == "retry_recheck_terminal");
        Assert.Equal(fixture.State.PersistentState.FallbackTailYield.CurrentOperation!.Value.Decision.AuthorityId.ToString(),
            terminal.AuthorityId);
        Assert.Equal(fixture.Receipt.Command.RecheckGeneration, terminal.OperationGeneration);
        Assert.Equal("None", terminal.BeforeState);
        Assert.Equal("Queued", terminal.AfterState);
        Assert.True(terminal.ReceiptValidated);
        Assert.Equal(fixture.Receipt.Command.RecheckGeneration, terminal.FallbackTailYield!.RetryRecheckGeneration);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InstallRetainedRecheckSource(RetainedRecheckFixture fixture)
    {
        var duplicateReceipt = FileTransferCoordinator.ApplyFallbackTailYieldRetryDispatchRecheckReceipt(
            fixture.State, fixture.Receipt);
        Assert.Equal(FileTransferFallbackTailYieldTransitionStatus.Idempotent, duplicateReceipt.Status);
        Assert.Equal(fixture.Receipt, duplicateReceipt.State.PersistentState.FallbackTailYield.LastRetryDispatchRecheckReceipt);
        fixture.Capture(duplicateReceipt.SnapshotEnvelope!);
        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            duplicateReceipt.State, fixture.Request, RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield,
            fixture.Yield.AuthorityId, fixture.Yield.YieldGeneration, fixture.Yield.YieldKind.ToString());
        Assert.True(granted.Granted, granted.Reason);
        fixture.Capture(granted.SnapshotEnvelope!);
        var installed = StageAndInstallPersistentRetryLifecycle(
            granted.State, granted, fixture.Request, stagedUtcMs: 30_000, capture: fixture.Capture);
        Assert.Equal(fixture.Receipt, installed.State.PersistentState.FallbackTailYield.LastRetryDispatchRecheckReceipt);
        Assert.Null(installed.State.PersistentState.FallbackTailYield.PendingRetryDispatchRecheckCommand);
        fixture.State = installed.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RetireRetainedRecheckRoute(RetainedRecheckFixture fixture)
    {
        var setup = CreateFallbackV6ToTunaRouteTransitionSetup(fixture.State, fixture.Capture);
        CompleteRetainedRecheckRouteRetirement(fixture, setup);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CompleteRetainedRecheckRouteRetirement(RetainedRecheckFixture fixture,
        (FileTransferCoordinatorState State, FileTransferRouteTransitionPrepareRequest Request,
            RuntimeUnlockRouteCommitProof FinalProof) setup)
    {
        fixture.State = CommitEmptyFileTransferRouteTransitionForTest(
            setup.State, setup.Request, setup.FinalProof, capture: fixture.Capture).State;
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        fixture.State = FallBack(fixture.State, fallback, 4, 4, fixture.Capture).State;
        AssertRetainedRecheckRouteRetired(fixture);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertRetainedRecheckRouteRetired(RetainedRecheckFixture fixture)
    {
        Assert.Null(fixture.State.PersistentState.FallbackTailYield.CurrentOperation);
        Assert.Equal(fixture.Receipt, fixture.State.PersistentState.FallbackTailYield.LastRetryDispatchRecheckReceipt);
        Assert.Equal(fixture.Receipt.Command.RecheckGeneration,
            fixture.State.PersistentState.FallbackTailYield.LastRetryDispatchRecheckGeneration);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RetainInitialProofAfterRecheckRetirement(RetainedRecheckFixture fixture)
    {
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var preview = MarkFallbackTailFailed(fixture.State, fallback, 4);
        var previewDecision = Assert.IsType<FallbackTailYieldDecision>(preview.FallbackTailYieldDecision);
        fixture.InitialProof = FileTransferCoordinator.CreateFallbackTailPeerProofReceipt(
            fixture.State, CreateCoordinatorFallbackTailPeerProof(preview.State, previewDecision),
            "retained-recheck-peer-first-proof", 40_000);
        var retained = FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt(fixture.State, fixture.InitialProof.Value);
        Assert.True(retained.Accepted, retained.Reason);
        fixture.Capture(retained.SnapshotEnvelope!);
        fixture.State = retained.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IssueFreshFailedYieldAfterRecheckRetirement(RetainedRecheckFixture fixture, bool peerFirst)
    {
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var lastYieldGeneration = fixture.State.PersistentState.FallbackTailYield.LastYieldGeneration;
        var failed = MarkFallbackTailFailed(fixture.State, fallback, 4);
        Assert.Null(failed.MutationRejectedReason);
        var failedEnvelope = Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(failed.SnapshotEnvelope);
        fixture.Capture(failedEnvelope);
        Assert.Equal("Accepted", failedEnvelope.DecisionStatus);
        Assert.Equal(failedEnvelope.Aggregate.BeforeStateGeneration + 1,
            failedEnvelope.Aggregate.AfterStateGeneration);
        Assert.NotEqual(failedEnvelope.Aggregate.BeforeStateFingerprint,
            failedEnvelope.Aggregate.AfterStateFingerprint);
        var operations = failedEnvelope.Aggregate.Operations
            .Where(operation => operation.Family == "fallback_tail_yield").ToArray();
        Assert.Equal(peerFirst ? 2 : 1, operations.Length);
        var fresh = Assert.Single(operations, operation => operation.OperationRole == "yield_current");
        Assert.Equal("Failed", fresh.OperationKind);
        Assert.Equal("None", fresh.BeforeState);
        Assert.Equal(peerFirst ? "PeerProofObserved" : "YieldIssued", fresh.AfterState);
        Assert.Equal(lastYieldGeneration + 1, fresh.OperationGeneration);
        Assert.NotEqual(fixture.Yield.AuthorityId.ToString(), fresh.AuthorityId);
        Assert.Equal(0, fresh.FallbackTailYield!.RetryRecheckGeneration);
        Assert.Equal(0, fresh.FallbackTailYield.RetryHoldGeneration);
        Assert.Equal(0, fresh.FallbackTailYield.ReservationGeneration);
        Assert.Equal(0, fresh.FallbackTailYield.ConsumingInstallGeneration);
        Assert.Equal("(none)", fresh.FallbackTailYield.PairedReleaseAuthorityId);
        Assert.DoesNotContain(operations, operation => operation.OperationRole.StartsWith("retry_", StringComparison.Ordinal));
        var yieldReceipt = Assert.Single(fresh.FallbackTailYield.Receipts, value => value.Stage == "yield");
        Assert.Equal("Accepted", yieldReceipt.ReceiptStatus);
        Assert.Equal(fresh.OperationGeneration, yieldReceipt.ReceiptGeneration);
        var freshOperation = failed.State.PersistentState.FallbackTailYield.CurrentOperation!.Value;
        Assert.Equal(fixture.InitialProof, freshOperation.PeerProof);
        Assert.Null(freshOperation.LocalProof);
        Assert.Null(freshOperation.PairedRelease);
        Assert.Equal(fixture.Receipt, failed.State.PersistentState.FallbackTailYield.LastRetryDispatchRecheckReceipt);
        if (peerFirst)
        {
            var retired = Assert.Single(operations, operation => operation.OperationRole == "peer_proof_pending_initial");
            Assert.Equal("Pending", retired.BeforeState);
            Assert.Equal("None", retired.AfterState);
            Assert.Equal(fixture.InitialProof!.Value.ObservationIdentity, fresh.FallbackTailYield.PeerProofIdentity);
        }
        fixture.State = failed.State;
        fixture.FailedEnvelope = failedEnvelope;
        fixture.ImmutableFailed = failedEnvelope.ToCanonicalJson();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertRetainedReceiptDuplicateAfterFreshYield(RetainedRecheckFixture fixture)
    {
        var retainedDuplicate = FileTransferCoordinator.ApplyFallbackTailYieldRetryDispatchRecheckReceipt(
            fixture.State, fixture.Receipt);
        Assert.Equal(FileTransferFallbackTailYieldTransitionStatus.Idempotent, retainedDuplicate.Status);
        Assert.Equal(fixture.State.PersistentState.FallbackTailYield.CurrentOperation,
            retainedDuplicate.State.PersistentState.FallbackTailYield.CurrentOperation);
        Assert.Equal(fixture.Receipt, retainedDuplicate.State.PersistentState.FallbackTailYield.LastRetryDispatchRecheckReceipt);
        var retainedDuplicateEnvelope = Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(
            retainedDuplicate.SnapshotEnvelope);
        Assert.Equal(retainedDuplicateEnvelope.Aggregate.BeforeStateGeneration,
            retainedDuplicateEnvelope.Aggregate.AfterStateGeneration);
        Assert.Equal(retainedDuplicateEnvelope.Aggregate.BeforeStateFingerprint,
            retainedDuplicateEnvelope.Aggregate.AfterStateFingerprint);
        Assert.DoesNotContain(retainedDuplicateEnvelope.Aggregate.Operations,
            operation => operation.OperationRole == "retry_recheck_terminal" || operation.StateApplied);
        Assert.Null(SessionFileTransferService.CreateFallbackTailYieldAuthorityEffectBindingForTesting(
            retainedDuplicateEnvelope, "RetryDispatchRecheck", "retry_dispatch_recheck_observed"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertFreshFailedYieldAttempts(RetainedRecheckFixture fixture)
    {
        var fallback = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var freshOperation = fixture.State.PersistentState.FallbackTailYield.CurrentOperation!.Value;
        var duplicate = MarkFallbackTailFailed(fixture.State, fallback, 4);
        fixture.Capture(duplicate.SnapshotEnvelope!);
        Assert.Equal("Idempotent", duplicate.SnapshotEnvelope!.DecisionStatus);
        Assert.Equal(duplicate.SnapshotEnvelope.Aggregate.BeforeStateGeneration,
            duplicate.SnapshotEnvelope.Aggregate.AfterStateGeneration);
        Assert.Equal(freshOperation, duplicate.State.PersistentState.FallbackTailYield.CurrentOperation);
        Assert.Equal(fixture.Receipt, duplicate.State.PersistentState.FallbackTailYield.LastRetryDispatchRecheckReceipt);
        var terminalView = duplicate.State with
        {
            IsTerminal = true,
            PersistentState = duplicate.State.PersistentState with { SnapshotStateFingerprint = null },
        };
        var rejected = MarkFallbackTailFailed(terminalView, fallback, 4);
        fixture.Capture(rejected.SnapshotEnvelope!);
        Assert.True(rejected.TerminalMutationRejected);
        Assert.Equal("Rejected", rejected.SnapshotEnvelope!.DecisionStatus);
        Assert.Equal(rejected.SnapshotEnvelope.Aggregate.BeforeStateGeneration,
            rejected.SnapshotEnvelope.Aggregate.AfterStateGeneration);
        foreach (var envelope in new[] { duplicate.SnapshotEnvelope, rejected.SnapshotEnvelope })
        {
            Assert.Equal(envelope.Aggregate.BeforeStateFingerprint, envelope.Aggregate.AfterStateFingerprint);
            var attempt = Assert.Single(envelope.Aggregate.Operations,
                operation => operation.Family == "fallback_tail_yield");
            Assert.Equal("attempted_decision", attempt.OperationRole);
            Assert.Equal(freshOperation.Decision.AuthorityId.ToString(), attempt.AuthorityId);
            Assert.Equal(freshOperation.Decision.YieldGeneration, attempt.OperationGeneration);
            Assert.False(attempt.StateApplied);
            Assert.Equal(0, attempt.FallbackTailYield!.RetryRecheckGeneration);
            Assert.Equal(envelope.DecisionStatus, Assert.Single(attempt.FallbackTailYield.Receipts).ReceiptStatus);
        }
        Assert.Equal(fixture.Receipt, rejected.State.PersistentState.FallbackTailYield.LastRetryDispatchRecheckReceipt);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    [Trait("FileTransferCoordinatorSuite", "FallbackTailYield")]
    public void FallbackTailYield_RetainedRecheckSnapshotOwnershipUsesImmutableReleaseScope()
        => AssertRetainedRecheckSnapshotOwnership(CreateRetainedRecheckSource());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertRetainedRecheckSnapshotOwnership(RetainedRecheckFixture fixture)
    {
        var operation = fixture.State.PersistentState.FallbackTailYield.CurrentOperation!.Value;
        var command = fixture.Receipt.Command;
        var ownership = typeof(FileTransferCoordinator).GetMethod(
            "FallbackTailRetryRecheckSnapshotBelongsToOperation", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool Belongs(FileTransferFallbackTailYieldOperation candidate,
            FileTransferFallbackTailYieldRetryDispatchRecheckCommand value)
            => (bool)ownership.Invoke(null, [candidate, value])!;
        Assert.True(Belongs(operation, command));
        Assert.False(Belongs(operation, default));
        foreach (var mutation in new[]
        {
            operation with { Decision = operation.Decision with { YieldGeneration = operation.Decision.YieldGeneration + 1 } },
            operation with { Scope = operation.Scope with { DataSessionIdentity = "replacement" } },
            operation with { Scope = operation.Scope with { DataSessionGeneration = operation.Scope.DataSessionGeneration + 1 } },
            operation with { Scope = operation.Scope with { LiveRouteEpoch = operation.Scope.LiveRouteEpoch + 1 } },
            operation with { Scope = operation.Scope with { LegGeneration = operation.Scope.LegGeneration + 1 } },
            operation with { Scope = operation.Scope with { TransportEpoch = operation.Scope.TransportEpoch + 1 } },
            operation with { Scope = operation.Scope with { BridgeRecoveryGeneration = operation.Scope.BridgeRecoveryGeneration + 1 } },
            operation with { Scope = operation.Scope with { CheckpointRequestIdSnapshot = "replacement-checkpoint" } },
            operation with { Scope = operation.Scope with { CheckpointGeneration = operation.Scope.CheckpointGeneration + 1 } },
            operation with { Scope = operation.Scope with { PairKey = "replacement-pair" } },
            operation with { LocalProof = null },
            operation with { PeerProof = null },
            operation with { PairedRelease = null },
        })
        {
            Assert.False(Belongs(mutation, command));
        }
        foreach (var hold in new[]
        {
            command.Hold with { DataSessionIdentity = "replacement" },
            command.Hold with { DataSessionGeneration = command.Hold.DataSessionGeneration + 1 },
            command.Hold with { Release = command.Hold.Release with { PairingGeneration = command.Hold.Release.PairingGeneration + 1 } },
        })
        {
            Assert.False(Belongs(operation, command with
            {
                Hold = hold,
                Observation = command.Observation with { Hold = hold },
            }));
        }
        foreach (var pairing in new[]
        {
            FileTransferFallbackTailYieldPairingState.ReleaseReserved,
            FileTransferFallbackTailYieldPairingState.ReleaseConsumed,
            FileTransferFallbackTailYieldPairingState.Invalidated,
        })
        {
            Assert.True(Belongs(operation with { PairingState = pairing }, command));
        }
        var carried = operation with
        {
            PairedReleaseCarryForward = new(operation.Decision, command.Hold.Release, operation.Scope,
                operation.Scope with { TransportEpoch = operation.Scope.TransportEpoch + 1 }, "transport_reauthorized"),
        };
        Assert.True(Belongs(carried, command));
        Assert.Equal(fixture.Receipt, fixture.State.PersistentState.FallbackTailYield.LastRetryDispatchRecheckReceipt);
    }

    private sealed class RetainedRecheckFixture
    {
        public FileTransferCoordinatorState State = null!;
        public RuntimeUnlockRetryDispatchAuthorityRequest Request;
        public FallbackTailYieldDecision Yield;
        public FallbackFrontierRepairAuthority Repair;
        public FileTransferFallbackTailYieldRetryDispatchRecheckReceipt Receipt;
        public FileTransferFallbackTailPeerProofReceipt? InitialProof;
        public List<FileTransferCoordinatorTransitionEnvelope> Snapshots { get; } = [];
        public FileTransferCoordinatorTransitionEnvelope QueuedEnvelope = null!;
        public FileTransferCoordinatorTransitionEnvelope FailedEnvelope = null!;
        public string ImmutableQueued = string.Empty;
        public string ImmutableFailed = string.Empty;

        public void Capture(FileTransferCoordinatorTransitionEnvelope envelope) => Snapshots.Add(envelope);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RetainedRecheckFixture CreateRetainedRecheckSource()
    {
        // Build the deep route setup before entering methods that copy the large
        // value-type request/command/receipt graph onto their stack frames.
        var fixture = new RetainedRecheckFixture { State = ActiveFallbackStateWithoutCheckpoint() };
        InitializeRetainedRecheckPair(fixture);
        InitializeRetainedRecheckRepair(fixture);
        BindRetainedRecheckHold(fixture);
        QueueRetainedRecheck(fixture);
        return fixture;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InitializeRetainedRecheckPair(RetainedRecheckFixture fixture)
    {
        (fixture.State, fixture.Request, fixture.Yield) = AuthorizeFallbackRuntimeUnlockRetryDispatchSource(
            fixture.State, snapshotCapture: fixture.Snapshots);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InitializeRetainedRecheckRepair(RetainedRecheckFixture fixture)
    {
        var repair = FileTransferCoordinator.AuthorizeAction(fixture.State,
            RecoveryActionRequest(FileTransferCoordinatorActionKind.RepairReplay,
                fixture.State.CurrentRuntimeOwner!.Value, fixture.State.CurrentLeg!,
                repairReplayKind: RepairReplayKind.FrontierStallRepair));
        Assert.True(repair.Accepted, repair.RejectionReason);
        fixture.Capture(repair.SnapshotEnvelope!);
        Assert.True(FileTransferCoordinator.TryIssueFallbackFrontierRepairAuthority(
            repair.RepairReplayDecision!.Value,
            new FallbackFrontierRepairAuthorityRequest(
                "fallback-frontier:12:1:frng-retained-recheck", "frng-retained-recheck",
                FrontierChunkIndex: 12, DurableReceivedHighestChunkIndex: 12,
                RangeCount: 1, RequestedChunkCount: 1, FallbackFrontierRepairSourceKind.PeerRequest,
                ProtocolRepairRequestId: "v6-frontier:12:1", RequestCorrelationId: "retained-recheck-request",
                CheckpointRequestId: "(none)", Priority: "frontier",
                RecoveryMode: "regular_nkn_frontier_stall_control_bulk",
                DecisionReason: "fallback_frontier_request_received"), out var authority, out var reason), reason);
        fixture.State = repair.State;
        fixture.Repair = authority!.Value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BindRetainedRecheckHold(RetainedRecheckFixture fixture)
    {
        var held = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            fixture.State, fixture.Request, RuntimeUnlockRetryReleaseKind.Unknown, null, 0, "pending");
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Held, held.Status);
        fixture.Capture(held.SnapshotEnvelope!);
        var bound = FileTransferCoordinator.BindFallbackTailYieldRetryDispatchHold(
            held.State, fixture.Request, fixture.Repair);
        Assert.True(bound.Accepted, bound.Reason);
        fixture.Capture(bound.SnapshotEnvelope!);
        fixture.State = bound.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void QueueRetainedRecheck(RetainedRecheckFixture fixture)
    {
        var issued = FileTransferCoordinator.IssueFallbackTailYieldRetryDispatchRecheck(fixture.State,
            new(fixture.State.PersistentState.FallbackTailYield.CurrentRetryDispatchHold!.Value,
                fixture.Repair, "ProgressObserved", SuccessorAuthorityId: null, ObservedUtcMs: 25_000));
        Assert.True(issued.Accepted, issued.Reason);
        fixture.Capture(issued.SnapshotEnvelope!);
        fixture.Receipt = new FileTransferFallbackTailYieldRetryDispatchRecheckReceipt(
            issued.State.PersistentState.FallbackTailYield.PendingRetryDispatchRecheckCommand!.Value,
            FileTransferFallbackTailYieldRetryDispatchRecheckReceiptStatus.Queued, "queued");
        var queued = FileTransferCoordinator.ApplyFallbackTailYieldRetryDispatchRecheckReceipt(issued.State, fixture.Receipt);
        Assert.True(queued.Accepted, queued.Reason);
        fixture.Capture(queued.SnapshotEnvelope!);
        Assert.Equal(fixture.Receipt, queued.State.PersistentState.FallbackTailYield.LastRetryDispatchRecheckReceipt);
        fixture.State = queued.State;
    }
}
