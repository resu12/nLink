using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    internal static FileTransferCoordinatorState CreateRetryConfirmationFingerprintFixture()
        => CreateRetryConfirmationFixture(false).Commit.State;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferCoordinatorSuite", "RuntimeUnlockRetryLifecycle")]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void RuntimeUnlockRetryConfirmation_ContextUsesFinalizedInstallSnapshot(bool activePredecessor)
    {
        var fixture = activePredecessor
            ? CreateActiveRetryConfirmationFixture()
            : CreateRetryConfirmationFixture(false);
        AssertRetryConfirmationContextProvenance(fixture, activePredecessor);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertRetryConfirmationContextProvenance(RetryConfirmationFixture fixture, bool activePredecessor)
    {
        var commit = fixture.Commit;
        var state = commit.State;
        var context = Assert.IsType<RuntimeUnlockRetryConfirmationCompensationContext>(commit.CompensationContext);
        var envelope = Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(commit.SnapshotEnvelope);
        Assert.Equal("CommitRuntimeUnlockRetryDispatchInstall", envelope.DecisionKind);
        Assert.Equal(envelope.CoordinatorInstanceId, context.CoordinatorInstanceId);
        Assert.Equal(envelope.Aggregate.AfterStateGeneration, context.CommittedStateGeneration);
        Assert.Equal(envelope.Aggregate.AfterStateFingerprint, context.CommittedSnapshotFingerprint);
        Assert.Equal(envelope.Aggregate.AfterBoundary, context.CommittedBoundary);
        Assert.Equal(FileTransferCoordinator.CalculateSnapshotStateFingerprint(state), context.CommittedStateFingerprint);
        Assert.Equal(state.PersistentState.SnapshotStateGeneration, context.CommittedStateGeneration);
        Assert.Equal(state.PersistentState.SnapshotStateFingerprint, context.CommittedSnapshotFingerprint);
        Assert.Equal(state.PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt!.Value.InstallReceipt, context.Receipt);
        if (activePredecessor)
        {
            var predecessor = Assert.IsType<RuntimeUnlockRetryLifecycleAttemptSnapshot>(context.PredecessorBeforeInstall);
            Assert.Equal(fixture.PredecessorContract!.Authority, predecessor.Authority);
            Assert.Equal(RuntimeUnlockRetryLifecycleAttemptState.AliasBound, predecessor.State);
            Assert.Null(predecessor.Settlement);
            var settlement = commit.PredecessorSettlementDecision!.Value;
            Assert.Equal(predecessor.Authority, settlement.Report.Authority);
            Assert.Equal(RuntimeUnlockRetryDispatchSettlementOutcome.Superseded, settlement.Report.Outcome);
        }
        else
        {
            Assert.Null(context.PredecessorBeforeInstall);
            Assert.Null(commit.PredecessorSettlementDecision);
        }
    }

    [Theory]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Outbound, true)]
    [InlineData(FileTransferDirection.Inbound, true)]
    [Trait("FileTransferCoordinatorSuite", "RuntimeUnlockRetryLifecycle")]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void RuntimeUnlockRetryConfirmation_CompensatesCurrentInstallWithoutReusingConsumedRelease(
        FileTransferDirection direction, bool predecessor)
    {
        var fixture = CreateRetryConfirmationFixture(predecessor, direction);
        AssertCurrentRetryConfirmationCompensation(fixture, predecessor);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertCurrentRetryConfirmationCompensation(
        RetryConfirmationFixture fixture, bool predecessor)
    {
        var before = fixture.Commit.State;
        var context = Assert.IsType<RuntimeUnlockRetryConfirmationCompensationContext>(fixture.Commit.CompensationContext);
        var installed = before.PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt!.Value;
        var result = FileTransferCoordinator.CompensateRejectedRuntimeUnlockRetryDispatchConfirmation(before, context);
        AssertCurrentRetryConfirmationCompensated(before, context, installed, result);
        var duplicate = FileTransferCoordinator.CompensateRejectedRuntimeUnlockRetryDispatchConfirmation(result.State, context);
        AssertCurrentRetryConfirmationDuplicate(before, installed, result, duplicate);
        if (!predecessor)
        {
            AssertConsumedRetryConfirmationReleaseCannotBeReused(result, context);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertCurrentRetryConfirmationCompensated(
        FileTransferCoordinatorState before,
        RuntimeUnlockRetryConfirmationCompensationContext context,
        in RuntimeUnlockRetryLifecycleAttemptSnapshot installed,
        in RuntimeUnlockRetryLifecycleTransitionDecision result)
    {
        Assert.Equal(RuntimeUnlockRetryLifecycleTransitionStatus.Accepted, result.Status);
        Assert.Equal(context.PredecessorBeforeInstall, result.State.PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt);
        Assert.Null(result.State.PersistentState.RuntimeUnlockRetryLifecycle.PendingAttempt);
        Assert.Null(result.State.PersistentState.RuntimeUnlockRetryLifecycle.RetainedPredecessor);
        var invalidated = installed with { State = RuntimeUnlockRetryLifecycleAttemptState.Invalidated };
        Assert.Equal(invalidated, result.State.PersistentState.RuntimeUnlockRetryLifecycle.LastTerminalAttempt);
        Assert.Contains(invalidated, result.State.PersistentState.RuntimeUnlockRetryLifecycle.AttemptHistory);
        Assert.Same(before.PersistentState.FallbackTailYield, result.State.PersistentState.FallbackTailYield);
        Assert.Equal(FileTransferFallbackTailYieldPairingState.ReleaseConsumed,
            result.State.PersistentState.FallbackTailYield.CurrentOperation!.Value.PairingState);
        Assert.Equal(before.PersistentState.FallbackTailYield.LastConsumedReservation,
            result.State.PersistentState.FallbackTailYield.LastConsumedReservation);
        Assert.Equal(before.PersistentState.FallbackTailYield.LastConsumingInstallReceipt,
            result.State.PersistentState.FallbackTailYield.LastConsumingInstallReceipt);
        Assert.Equal(before.RouteSelection, result.State.RouteSelection);
        Assert.Equal(before.CurrentRuntimeOwner, result.State.CurrentRuntimeOwner);
        Assert.Equal(before.CurrentRoutePublicationToken, result.State.CurrentRoutePublicationToken);
        Assert.Equal(before.CurrentRouteCommit, result.State.CurrentRouteCommit);
        Assert.Equal(before.CurrentDataSessionIdentity, result.State.CurrentDataSessionIdentity);
        Assert.Equal(before.CurrentDataSessionGeneration, result.State.CurrentDataSessionGeneration);
        Assert.Same(before.CurrentLeg, result.State.CurrentLeg);
        var envelope = Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(result.SnapshotEnvelope);
        Assert.Equal(before.PersistentState.SnapshotStateGeneration, envelope.Aggregate.BeforeStateGeneration);
        Assert.Equal(before.PersistentState.SnapshotStateGeneration + 1, envelope.Aggregate.AfterStateGeneration);
        Assert.Equal(before.PersistentState.SnapshotStateFingerprint, envelope.Aggregate.BeforeStateFingerprint);
        Assert.Equal("CompensateRejectedRuntimeUnlockRetryDispatchConfirmation", envelope.DecisionKind);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertCurrentRetryConfirmationDuplicate(
        FileTransferCoordinatorState before,
        in RuntimeUnlockRetryLifecycleAttemptSnapshot installed,
        in RuntimeUnlockRetryLifecycleTransitionDecision result,
        in RuntimeUnlockRetryLifecycleTransitionDecision duplicate)
    {
        Assert.Equal(RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent, duplicate.Status);
        Assert.Same(result.State, duplicate.State);
        Assert.Equal(result.State.PersistentState, duplicate.State.PersistentState);
        Assert.Equal(installed, before.PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertConsumedRetryConfirmationReleaseCannotBeReused(
        in RuntimeUnlockRetryLifecycleTransitionDecision result,
        RuntimeUnlockRetryConfirmationCompensationContext context)
    {
        var replay = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(result.State,
            context.Receipt.Request, context.Receipt.Authority.ReleaseKind,
            context.Receipt.Authority.ReleaseAuthorityId, context.Receipt.Authority.ReleaseGeneration,
            context.Receipt.Authority.ReleaseOutcome);
        Assert.False(replay.Granted);
        Assert.Equal(result.State.PersistentState.RuntimeUnlockRetryLifecycle.LastAuthorityGeneration,
            replay.State.PersistentState.RuntimeUnlockRetryLifecycle.LastAuthorityGeneration);
        Assert.Equal(result.State.PersistentState.FallbackTailYield.LastConsumingInstallReceipt,
            replay.State.PersistentState.FallbackTailYield.LastConsumingInstallReceipt);
        Assert.Null(replay.State.PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "RuntimeUnlockRetryLifecycle")]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void RuntimeUnlockRetryConfirmation_RestoresOnlyExactActivePredecessor()
    {
        var fixture = CreateActiveRetryConfirmationFixture();
        AssertRetryConfirmationRestoresOnlyExactActivePredecessor(fixture);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertRetryConfirmationRestoresOnlyExactActivePredecessor(RetryConfirmationFixture fixture)
    {
        var commit = fixture.Commit;
        var context = commit.CompensationContext!;
        var original = context.PredecessorBeforeInstall!.Value;
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementOutcome.Superseded,
            commit.State.PersistentState.RuntimeUnlockRetryLifecycle.RetainedPredecessor!.Value.Settlement!.Value.Outcome);

        foreach (var invalidPredecessor in new RuntimeUnlockRetryLifecycleAttemptSnapshot?[]
                 { null, original with { OfferNonce = "different-predecessor" } })
        {
            var malformed = new RuntimeUnlockRetryConfirmationCompensationContext(context.Receipt,
                invalidPredecessor, context.CoordinatorInstanceId, context.CommittedStateGeneration,
                context.CommittedSnapshotFingerprint, context.CommittedStateFingerprint,
                context.CommittedBoundary, context.CompensatedStateFingerprint);
            var rejected = FileTransferCoordinator.CompensateRejectedRuntimeUnlockRetryDispatchConfirmation(
                commit.State, malformed);
            Assert.Equal(RuntimeUnlockRetryLifecycleTransitionStatus.Rejected, rejected.Status);
            Assert.Same(commit.State, rejected.State);
        }

        var compensated = FileTransferCoordinator.CompensateRejectedRuntimeUnlockRetryDispatchConfirmation(
            commit.State, context);
        Assert.True(compensated.Accepted, compensated.Reason);
        Assert.Equal(original, compensated.State.PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt);
        Assert.Equal(original.Authority, compensated.State.CurrentRuntimeUnlockRetryDispatchAuthority);
        Assert.Null(compensated.State.PersistentState.RuntimeUnlockRetryLifecycle.RetainedPredecessor);
        Assert.Equal(RuntimeUnlockRetryLifecycleAttemptState.Invalidated,
            compensated.State.PersistentState.RuntimeUnlockRetryLifecycle.LastTerminalAttempt!.Value.State);
        Assert.Equal(commit.State.PersistentState.RuntimeUnlockRetryLifecycle.RepairSettlementRelease,
            compensated.State.PersistentState.RuntimeUnlockRetryLifecycle.RepairSettlementRelease);
        Assert.False(fixture.PredecessorContract!.Lifecycle.IsSettled);
        Assert.Null(fixture.PredecessorContract.Lifecycle.SuccessorAuthorityId);
        var duplicate = FileTransferCoordinator.CompensateRejectedRuntimeUnlockRetryDispatchConfirmation(
            compensated.State, context);
        Assert.Equal(RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent, duplicate.Status);
        Assert.Same(compensated.State, duplicate.State);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "RuntimeUnlockRetryLifecycle")]
    public void RuntimeUnlockRetryConfirmation_SnapshotDisabledCompensationDuplicateIsNeutral()
    {
        var fixture = CreateRetryConfirmationFixture(false, snapshots: false);
        var context = fixture.Commit.CompensationContext;
        var result = FileTransferCoordinator.CompensateRejectedRuntimeUnlockRetryDispatchConfirmation(fixture.Commit.State, context);
        Assert.Equal(RuntimeUnlockRetryLifecycleTransitionStatus.Accepted, result.Status);
        Assert.Null(result.SnapshotEnvelope);
        var duplicate = FileTransferCoordinator.CompensateRejectedRuntimeUnlockRetryDispatchConfirmation(result.State, context);
        Assert.Equal(RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent, duplicate.Status);
        Assert.Same(result.State, duplicate.State);
        Assert.Null(duplicate.SnapshotEnvelope);
    }

    [Theory]
    [InlineData("missing_context")]
    [InlineData("receipt")]
    [InlineData("terminal")]
    [InlineData("data_identity")]
    [InlineData("data_generation")]
    [InlineData("checkpoint_aba")]
    [InlineData("generation_aba")]
    [InlineData("pending_conflict")]
    [InlineData("alias")]
    [InlineData("dispatch")]
    [Trait("FileTransferCoordinatorSuite", "RuntimeUnlockRetryLifecycle")]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void RuntimeUnlockRetryConfirmation_RejectsStaleOrAdvancedInstall(string mutation)
    {
        var fixture = CreateRetryConfirmationFixture(false);
        AssertRetryConfirmationRejectsStaleOrAdvancedInstall(fixture, mutation);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertRetryConfirmationRejectsStaleOrAdvancedInstall(
        RetryConfirmationFixture fixture, string mutation)
    {
        var state = fixture.Commit.State;
        var context = fixture.Commit.CompensationContext!;
        ApplyRetryConfirmationRejectionMutation(ref state, ref context, mutation);
        AssertRejectedRetryConfirmationStateNeutral(state, context);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ApplyRetryConfirmationRejectionMutation(
        ref FileTransferCoordinatorState state,
        ref RuntimeUnlockRetryConfirmationCompensationContext context,
        string mutation)
    {
        switch (mutation)
        {
            case "missing_context": context = null!; break;
            case "receipt":
                context = new RuntimeUnlockRetryConfirmationCompensationContext(
                    context.Receipt with { InstallGeneration = context.Receipt.InstallGeneration + 1 },
                    context.PredecessorBeforeInstall, context.CoordinatorInstanceId, context.CommittedStateGeneration,
                    context.CommittedSnapshotFingerprint, context.CommittedStateFingerprint,
                    context.CommittedBoundary, context.CompensatedStateFingerprint);
                break;
            case "terminal": state = state with { IsTerminal = true }; break;
            case "data_identity": state = state with { CurrentDataSessionIdentity = "replaced" }; break;
            case "data_generation": state = state with { CurrentDataSessionGeneration = state.CurrentDataSessionGeneration + 1 }; break;
            case "checkpoint_aba":
                var leg = state.CurrentLeg!;
                state = state with { CurrentLeg = new FileTransferLeg
                {
                    LegId = leg.LegId, Generation = leg.Generation, RouteSelection = leg.RouteSelection,
                    ProtocolVersion = leg.ProtocolVersion, LiveRouteEpochId = leg.LiveRouteEpochId,
                    TransportEpochId = leg.TransportEpochId, StartedUtc = leg.StartedUtc,
                    FrozenUtc = leg.FrozenUtc, State = leg.State,
                    StartCommittedChunkIndex = leg.StartCommittedChunkIndex,
                    ProvenCommittedChunkIndex = leg.ProvenCommittedChunkIndex,
                    ProvenHighestObservedChunkIndex = leg.ProvenHighestObservedChunkIndex,
                    CheckpointRequestId = leg.CheckpointRequestId, CheckpointPriority = leg.CheckpointPriority,
                    CheckpointRequestedUtc = leg.CheckpointRequestedUtc,
                    CheckpointGeneration = leg.CheckpointGeneration + 1,
                    BridgeRecoveryGeneration = leg.BridgeRecoveryGeneration, CanSendData = leg.CanSendData,
                    RuntimeUnlockProbeAllowedAfterTransportEpochAdoption = leg.RuntimeUnlockProbeAllowedAfterTransportEpochAdoption,
                } };
                break;
            case "generation_aba": state = state with { PersistentState = state.PersistentState with { SnapshotStateGeneration = state.PersistentState.SnapshotStateGeneration + 2 } }; break;
            case "pending_conflict": state = state with { PersistentState = state.PersistentState with
                { RuntimeUnlockRetryLifecycle = state.PersistentState.RuntimeUnlockRetryLifecycle with
                    { PendingAttempt = state.PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt } } }; break;
            case "alias":
            case "dispatch":
                var alias = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(state,
                    context.Receipt, RuntimeUnlockRetryDispatchAttemptPhase.AliasBound, 44, "confirmation-alias", false);
                Assert.True(alias.Accepted);
                state = alias.State;
                if (mutation == "dispatch")
                {
                    var sent = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(state,
                        context.Receipt, RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight, 44, "confirmation-alias", false);
                    Assert.True(sent.Accepted, sent.Reason);
                    var dispatched = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(sent.State,
                        new RuntimeUnlockRetryDispatchSettlementReport(context.Receipt.Authority,
                            RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved, "dispatch_observed")
                        { OfferGeneration = 44, OfferNonce = "confirmation-alias", DispatchObserved = true });
                    Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, dispatched.Status);
                    state = dispatched.State;
                }
                break;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertRejectedRetryConfirmationStateNeutral(
        FileTransferCoordinatorState state,
        RuntimeUnlockRetryConfirmationCompensationContext context)
    {
        var result = FileTransferCoordinator.CompensateRejectedRuntimeUnlockRetryDispatchConfirmation(state, context);
        Assert.Equal(RuntimeUnlockRetryLifecycleTransitionStatus.Rejected, result.Status);
        Assert.Same(state, result.State);
        Assert.Equal(state.PersistentState, result.State.PersistentState);
    }

    internal static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> CreateRejectedRetryConfirmationSnapshotFixture(bool withPredecessor)
    {
        var fixture = CreateRetryConfirmationFixture(withPredecessor);
        var result = FileTransferCoordinator.CompensateRejectedRuntimeUnlockRetryDispatchConfirmation(
            fixture.Commit.State, fixture.Commit.CompensationContext);
        Assert.True(result.Accepted, result.Reason);
        fixture.Envelopes.Add(result.SnapshotEnvelope!);
        return fixture.Envelopes;
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "RuntimeUnlockRetryLifecycle")]
    public void RuntimeUnlockRetryConfirmation_ActivePredecessorSnapshotRetainsExactReceipts()
        => CreateRejectedActiveRetryConfirmationSnapshotFixture();

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "RuntimeUnlockRetryLifecycle")]
    public void RuntimeUnlockRetryConfirmation_RestoredPredecessorClosesOnlyOnOwnDispatchSettlement()
        => CreateRejectedActiveRetryConfirmationSnapshotFixture(settleRestoredPredecessor: true);

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    public void RuntimeUnlockRetryConfirmation_SuccessfulConfirmationRetiresOnlyExactPredecessor()
        => CreateSuccessfulActiveRetryConfirmationSnapshotFixture();

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    public void RuntimeUnlockRetryConfirmation_SuccessfulRetirementLateReportsAreStateNeutral()
        => CreateSuccessfulActiveRetryConfirmationSnapshotFixture(includeLateAttempts: true);

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    public void RuntimeUnlockRetryConfirmation_SuccessfulSuccessorSettlementAtomicallyReplacesTerminalPredecessor()
        => CreateSuccessfulActiveRetryConfirmationSnapshotFixture(settleSuccessor: true);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope>
        CreateSuccessfulActiveRetryConfirmationSnapshotFixture(bool includeLateAttempts = false, bool settleSuccessor = false)
    {
        var fixture = CreateActiveRetryConfirmationFixture();
        return CompleteSuccessfulActiveRetryConfirmationSnapshotFixture(fixture, includeLateAttempts, settleSuccessor);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope>
        CompleteSuccessfulActiveRetryConfirmationSnapshotFixture(
            RetryConfirmationFixture fixture, bool includeLateAttempts, bool settleSuccessor)
    {
        var commit = fixture.Commit;
        var before = commit.State.PersistentState.RuntimeUnlockRetryLifecycle;
        var predecessor = before.RetainedPredecessor!.Value;
        var successor = before.InstalledAttempt!.Value;
        var report = commit.PredecessorSettlementDecision!.Value.Report;
        var retirement = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(commit.State, report);
        Assert.True(retirement.Accepted, retirement.Reason);
        var envelope = retirement.SnapshotEnvelope!;
        Assert.Equal(commit.SnapshotEnvelope!.Aggregate.AfterStateGeneration, envelope.Aggregate.BeforeStateGeneration);
        Assert.Equal(commit.SnapshotEnvelope.Aggregate.AfterStateFingerprint, envelope.Aggregate.BeforeStateFingerprint);
        Assert.Equal(envelope.Aggregate.BeforeStateGeneration + 1, envelope.Aggregate.AfterStateGeneration);
        Assert.NotEqual(envelope.Aggregate.BeforeStateFingerprint, envelope.Aggregate.AfterStateFingerprint);
        var operations = envelope.Aggregate.Operations.Where(operation =>
            operation.Family == "runtime_unlock_retry_lifecycle").ToArray();
        Assert.Equal(2, operations.Length);
        var removed = Assert.Single(operations.Where(operation => operation.OperationRole == "attempt_retained_predecessor"));
        var terminal = Assert.Single(operations.Where(operation => operation.OperationRole == "attempt_terminal"));
        Assert.Equal("Settled", removed.BeforeState);
        Assert.Equal("None", removed.AfterState);
        Assert.Equal("None", terminal.BeforeState);
        Assert.Equal("Settled", terminal.AfterState);
        Assert.Equal(predecessor.Authority.AuthorityId.ToString(), terminal.AuthorityId);
        Assert.Equal(removed.AuthorityId, terminal.AuthorityId);
        Assert.Equal(removed.OperationGeneration, terminal.OperationGeneration);
        Assert.Equal(successor.Authority.AuthorityId.ToString(), terminal.RuntimeUnlockRetry!.SuccessorAuthorityId);
        Assert.Equal(removed.RuntimeUnlockRetry!.SourceBoundary, terminal.RuntimeUnlockRetry.SourceBoundary);
        Assert.Equal(removed.RuntimeUnlockRetry.Receipts.ToArray(), terminal.RuntimeUnlockRetry.Receipts.ToArray());
        var receipt = Assert.Single(terminal.RuntimeUnlockRetry.Receipts.Where(value => value.Stage == "settlement"));
        Assert.Equal("Superseded", receipt.ReceiptStatus);
        Assert.True(receipt.ReceiptValidated);
        Assert.Equal(report.OfferGeneration, receipt.ReceiptGeneration);
        Assert.Equal(before, commit.State.PersistentState.RuntimeUnlockRetryLifecycle);
        var after = retirement.State.PersistentState.RuntimeUnlockRetryLifecycle;
        Assert.Null(after.RetainedPredecessor);
        Assert.Equal(successor, after.InstalledAttempt);
        Assert.Equal(successor.Authority, retirement.State.CurrentRuntimeUnlockRetryDispatchAuthority);
        Assert.Equal(predecessor.Authority, after.LastTerminalAttempt!.Value.Authority);
        Assert.Equal(report, after.LastTerminalAttempt.Value.Settlement);
        Assert.Equal(report, after.LastSettlement);
        fixture.Envelopes.Add(envelope);
        if (includeLateAttempts)
        {
            var duplicate = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(retirement.State, report);
            Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent, duplicate.Status);
            var rejected = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(duplicate.State,
                report with { Reason = "late_predecessor_mismatch" });
            Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected, rejected.Status);
            foreach (var decision in new[] { duplicate, rejected })
            {
                var attempt = decision.SnapshotEnvelope!;
                Assert.Equal(envelope.Aggregate.AfterStateGeneration, attempt.Aggregate.BeforeStateGeneration);
                Assert.Equal(attempt.Aggregate.BeforeStateGeneration, attempt.Aggregate.AfterStateGeneration);
                Assert.Equal(envelope.Aggregate.AfterStateFingerprint, attempt.Aggregate.BeforeStateFingerprint);
                Assert.Equal(attempt.Aggregate.BeforeStateFingerprint, attempt.Aggregate.AfterStateFingerprint);
                Assert.Equal(after, decision.State.PersistentState.RuntimeUnlockRetryLifecycle);
                var operation = Assert.Single(attempt.Aggregate.Operations);
                Assert.Equal(terminal.OperationKey, operation.OperationKey);
                Assert.Equal("Settled", operation.BeforeState);
                Assert.Equal("Settled", operation.AfterState);
                Assert.False(operation.StateApplied);
                Assert.Equal(terminal.RuntimeUnlockRetry.Receipts.ToArray(), operation.RuntimeUnlockRetry!.Receipts.ToArray());
                fixture.Envelopes.Add(attempt);
            }
        }
        if (settleSuccessor)
        {
            var a = predecessor.Authority.AuthorityId.ToString();
            var b = successor.Authority.AuthorityId.ToString();
            var successorReceipt = successor.InstallReceipt!.Value;
            const long successorOfferGeneration = 73;
            const string successorOfferNonce = "confirmation-successor-dispatch";
            var predecessorReceipts = terminal.RuntimeUnlockRetry.Receipts.ToArray();
            var installedB = Operation(commit.SnapshotEnvelope!, "attempt_installed", b, "None", "Installed");
            Assert.Equal(a, installedB.PredecessorAuthorityId);
            Assert.NotEqual(successorOfferGeneration, successorReceipt.InstallGeneration);
            Assert.NotEqual(successorOfferGeneration, successor.Authority.DispatchAttemptGeneration);

            var alias = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(
                retirement.State, successorReceipt, RuntimeUnlockRetryDispatchAttemptPhase.AliasBound,
                successorOfferGeneration, successorOfferNonce, dispatchObserved: false);
            Assert.True(alias.Accepted, alias.Reason);
            AppendContinuation(alias.SnapshotEnvelope!);
            Assert.Single(alias.SnapshotEnvelope!.Aggregate.Operations.Where(operation => operation.Family == "runtime_unlock_retry_lifecycle"));
            var aliasB = Operation(alias.SnapshotEnvelope, "attempt_installed", b, "Installed", "AliasBound");
            Assert.Equal(Receipt(installedB, "stage"), Receipt(aliasB, "stage"));
            Assert.Equal(Receipt(installedB, "install") with { ReceiptStatus = "AliasBound" }, Receipt(aliasB, "install"));
            Assert.Equal(after.LastTerminalAttempt, alias.State.PersistentState.RuntimeUnlockRetryLifecycle.LastTerminalAttempt);

            var sending = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(
                alias.State, successorReceipt, RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight,
                successorOfferGeneration, successorOfferNonce, dispatchObserved: false);
            Assert.True(sending.Accepted, sending.Reason);
            AppendContinuation(sending.SnapshotEnvelope!);
            Assert.Single(sending.SnapshotEnvelope!.Aggregate.Operations.Where(operation => operation.Family == "runtime_unlock_retry_lifecycle"));
            var sendingB = Operation(sending.SnapshotEnvelope, "attempt_installed", b, "AliasBound", "SendInFlight");
            Assert.Equal(Receipt(installedB, "stage"), Receipt(sendingB, "stage"));
            Assert.Equal(Receipt(installedB, "install") with { ReceiptStatus = "SendInFlight" }, Receipt(sendingB, "install"));
            Assert.Equal(after.LastTerminalAttempt, sending.State.PersistentState.RuntimeUnlockRetryLifecycle.LastTerminalAttempt);

            var successorReport = new RuntimeUnlockRetryDispatchSettlementReport(successor.Authority,
                RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved, "dispatch_observed")
            {
                OfferGeneration = successorOfferGeneration, OfferNonce = successorOfferNonce,
                DispatchObserved = true,
            };
            var settled = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(sending.State, successorReport);
            Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, settled.Status);
            var settlementEnvelope = settled.SnapshotEnvelope!;
            AppendContinuation(settlementEnvelope);
            Assert.Equal(3, settlementEnvelope.Aggregate.Operations.Count(operation => operation.Family == "runtime_unlock_retry_lifecycle"));
            var displacedA = Operation(settlementEnvelope, "attempt_terminal", a, "Settled", "None");
            var removedB = Operation(settlementEnvelope, "attempt_installed", b, "SendInFlight", "None");
            var settledB = Operation(settlementEnvelope, "attempt_terminal", b, "None", "Settled");
            Assert.Equal(terminal.OperationKey, displacedA.OperationKey);
            Assert.Equal(terminal.OperationGeneration, displacedA.OperationGeneration);
            Assert.Equal(terminal.RuntimeUnlockRetry with { Receipts = displacedA.RuntimeUnlockRetry!.Receipts }, displacedA.RuntimeUnlockRetry);
            Assert.Equal(predecessorReceipts, displacedA.RuntimeUnlockRetry!.Receipts.ToArray());
            Assert.Equal(receipt, Receipt(displacedA, "settlement"));
            Assert.Equal(sendingB.OperationKey, removedB.OperationKey);
            Assert.Equal(sendingB.RuntimeUnlockRetry! with { Receipts = removedB.RuntimeUnlockRetry!.Receipts }, removedB.RuntimeUnlockRetry);
            Assert.Equal(sendingB.RuntimeUnlockRetry.Receipts.ToArray(), removedB.RuntimeUnlockRetry.Receipts.ToArray());
            Assert.Equal(removedB.OperationGeneration, settledB.OperationGeneration);
            Assert.Equal(a, settledB.PredecessorAuthorityId);
            Assert.Equal(removedB.RuntimeUnlockRetry!.SourceBoundary, settledB.RuntimeUnlockRetry!.SourceBoundary);
            Assert.Equal(Receipt(installedB, "stage"), Receipt(settledB, "stage"));
            Assert.Equal(Receipt(installedB, "install") with { ReceiptStatus = "Settled" }, Receipt(settledB, "install"));
            Assert.Equal(successor.Authority.DispatchAttemptGeneration, Receipt(settledB, "stage").ReceiptGeneration);
            Assert.Equal(successorReceipt.InstallGeneration, Receipt(settledB, "install").CommandGeneration);
            Assert.Equal(successorReceipt.InstallGeneration, Receipt(settledB, "install").ReceiptGeneration);
            Assert.Equal("DispatchObserved", Receipt(settledB, "settlement").ReceiptStatus);
            Assert.Equal(successorOfferGeneration, Receipt(settledB, "execution").CommandGeneration);
            Assert.Equal(successorOfferGeneration, Receipt(settledB, "execution").ReceiptGeneration);
            Assert.Equal(successorOfferGeneration, Receipt(settledB, "settlement").CommandGeneration);
            Assert.Equal(successorOfferGeneration, Receipt(settledB, "settlement").ReceiptGeneration);
            Assert.All(settledB.RuntimeUnlockRetry.Receipts, value => Assert.True(value.ReceiptValidated));
            Assert.True(settledB.RuntimeUnlockRetry.DispatchObserved);
            var settledLifecycle = settled.State.PersistentState.RuntimeUnlockRetryLifecycle;
            Assert.Null(settledLifecycle.PendingAttempt);
            Assert.Null(settledLifecycle.InstalledAttempt);
            Assert.Null(settledLifecycle.RetainedPredecessor);
            Assert.Null(settled.State.CurrentRuntimeUnlockRetryDispatchAuthority);
            Assert.Equal(successor.Authority, settledLifecycle.LastTerminalAttempt!.Value.Authority);
            Assert.Equal(successorReport, settledLifecycle.LastTerminalAttempt.Value.Settlement);
            Assert.Equal(successorReport, settledLifecycle.LastSettlement);
            Assert.Contains(after.LastTerminalAttempt!.Value, settledLifecycle.AttemptHistory);
            Assert.Equal(after.RepairSettlementRelease, settledLifecycle.RepairSettlementRelease);
            Assert.Equal(before, commit.State.PersistentState.RuntimeUnlockRetryLifecycle);
            Assert.Equal(after, retirement.State.PersistentState.RuntimeUnlockRetryLifecycle);
            Assert.Equal(predecessorReceipts, terminal.RuntimeUnlockRetry.Receipts.ToArray());
            Assert.Equal(report, retirement.State.PersistentState.RuntimeUnlockRetryLifecycle.LastTerminalAttempt!.Value.Settlement);
            Assert.Equal(RuntimeUnlockRetryLifecycleAttemptState.SendInFlight,
                sending.State.PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt!.Value.State);
        }
        return fixture.Envelopes;

        void AppendContinuation(FileTransferCoordinatorTransitionEnvelope continuation)
        {
            Assert.Equal(fixture.Envelopes[^1].Aggregate.AfterStateGeneration, continuation.Aggregate.BeforeStateGeneration);
            Assert.Equal(fixture.Envelopes[^1].Aggregate.AfterStateFingerprint, continuation.Aggregate.BeforeStateFingerprint);
            Assert.Equal(continuation.Aggregate.BeforeStateGeneration + 1, continuation.Aggregate.AfterStateGeneration);
            Assert.NotEqual(continuation.Aggregate.BeforeStateFingerprint, continuation.Aggregate.AfterStateFingerprint);
            fixture.Envelopes.Add(continuation);
        }
        static FileTransferCoordinatorOperationTransitionSnapshot Operation(FileTransferCoordinatorTransitionEnvelope transition,
            string role, string authorityId, string beforeState, string afterState)
        {
            var operation = Assert.Single(transition.Aggregate.Operations.Where(operation =>
                operation.Family == "runtime_unlock_retry_lifecycle" && operation.OperationRole == role && operation.AuthorityId == authorityId));
            Assert.Equal(beforeState, operation.BeforeState);
            Assert.Equal(afterState, operation.AfterState);
            Assert.True(operation.StateApplied);
            return operation;
        }
        static FileTransferCoordinatorReceiptTransitionSnapshot Receipt(FileTransferCoordinatorOperationTransitionSnapshot operation,
            string stage) => Assert.Single(operation.RuntimeUnlockRetry!.Receipts.Where(value => value.Stage == stage));
    }

    // The established RegularV4 source helper is outbound-only. Fallback's
    // canonical paired request does not authorize active listener rearming.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope>
        CreateRejectedActiveRetryConfirmationSnapshotFixture(bool settleRestoredPredecessor = false)
    {
        var fixture = CreateActiveRetryConfirmationFixture();
        return CompleteRejectedActiveRetryConfirmationSnapshotFixture(fixture, settleRestoredPredecessor);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope>
        CompleteRejectedActiveRetryConfirmationSnapshotFixture(
            RetryConfirmationFixture fixture, bool settleRestoredPredecessor)
    {
        var commit = fixture.Commit;
        var context = commit.CompensationContext!;
        var predecessor = context.PredecessorBeforeInstall!.Value;
        var a = predecessor.Authority.AuthorityId.ToString();
        var b = context.Receipt.Authority.AuthorityId.ToString();
        Assert.NotEqual(a, b);
        var committed = commit.SnapshotEnvelope!;
        Assert.Equal(4, committed.Aggregate.Operations.Count(operation => operation.Family == "runtime_unlock_retry_lifecycle"));
        var original = Operation(committed, "attempt_installed", a, "AliasBound", "None");
        var retained = Operation(committed, "attempt_retained_predecessor", a, "None", "Settled");
        var pending = Operation(committed, "attempt_pending", b, "Staged", "None");
        var installed = Operation(committed, "attempt_installed", b, "None", "Installed");
        Assert.Equal(a, installed.PredecessorAuthorityId);
        Assert.Equal(b, retained.RuntimeUnlockRetry!.SuccessorAuthorityId);
        Assert.True(installed.RuntimeUnlockRetry!.RequestActivePredecessorSupersession);
        Assert.Equal(original.OperationGeneration + 1, installed.OperationGeneration);
        Assert.Equal(3, original.RuntimeUnlockRetry!.Receipts.Length);
        Assert.Equal(4, retained.RuntimeUnlockRetry.Receipts.Length);
        Assert.Equal(2, pending.RuntimeUnlockRetry!.Receipts.Length);
        Assert.Equal(2, installed.RuntimeUnlockRetry.Receipts.Length);
        Assert.Equal(original.RuntimeUnlockRetry!.SourceBoundary, retained.RuntimeUnlockRetry.SourceBoundary);
        Assert.Equal(original.RuntimeUnlockRetry.SourceBoundary, installed.RuntimeUnlockRetry.SourceBoundary);
        Assert.Equal(original.RuntimeUnlockRetry.ReleaseAuthorityId, installed.RuntimeUnlockRetry.ReleaseAuthorityId);
        Assert.Equal(original.RuntimeUnlockRetry.ReleaseGeneration, installed.RuntimeUnlockRetry.ReleaseGeneration);
        Assert.Equal(original.RuntimeUnlockRetry.ReleaseOutcome, installed.RuntimeUnlockRetry.ReleaseOutcome);
        Assert.Equal(original.RuntimeUnlockRetry.ReleaseKind, installed.RuntimeUnlockRetry.ReleaseKind);
        Assert.Equal(original.RuntimeUnlockRetry.OfferGeneration, retained.RuntimeUnlockRetry.OfferGeneration);
        Assert.Equal(original.RuntimeUnlockRetry.OfferNonceFingerprint, retained.RuntimeUnlockRetry.OfferNonceFingerprint);
        Assert.False(retained.RuntimeUnlockRetry.DispatchObserved);
        Assert.Equal(Receipt(original, "stage"), Receipt(retained, "stage"));
        Assert.Equal(Receipt(original, "install") with { ReceiptStatus = "Settled" }, Receipt(retained, "install"));
        var originalExecution = Receipt(original, "execution");
        var retainedExecution = Receipt(retained, "execution");
        Assert.Equal(originalExecution.CommandIdentity, retainedExecution.CommandIdentity);
        Assert.Equal(originalExecution.CommandGeneration, retainedExecution.CommandGeneration);
        Assert.Equal(originalExecution.CommandFingerprint, retainedExecution.CommandFingerprint);
        Assert.Equal(originalExecution.ReceiptIdentity, retainedExecution.ReceiptIdentity);
        Assert.Equal(originalExecution.ReceiptGeneration, retainedExecution.ReceiptGeneration);
        Assert.Equal("Settled", retainedExecution.ReceiptStatus);
        Assert.NotEqual(originalExecution.ReceiptFingerprint, retainedExecution.ReceiptFingerprint);
        Assert.Equal(Receipt(pending, "stage"), Receipt(installed, "stage"));
        Assert.Equal(Receipt(pending, "install") with { ReceiptStatus = "Installed" }, Receipt(installed, "install"));
        Assert.Equal(Receipt(original, "install").ReceiptGeneration + 1, Receipt(installed, "install").ReceiptGeneration);
        var settlement = Receipt(retained, "settlement");
        Assert.Equal("Superseded", settlement.ReceiptStatus);
        Assert.Equal("coordinator_successor_installed", settlement.ReceiptReason);
        Assert.True(settlement.ReceiptValidated);
        Assert.Equal(original.RuntimeUnlockRetry.OfferGeneration, settlement.ReceiptGeneration);
        Assert.Equal(settlement.ReceiptGeneration, settlement.CommandGeneration);
        Assert.Equal(a + ":settlement", settlement.ReceiptIdentity);
        Assert.Equal(settlement.ReceiptIdentity, settlement.CommandIdentity);
        Assert.NotEqual(original.OperationGeneration, settlement.ReceiptGeneration);
        Assert.NotEqual(original.OperationGeneration, Receipt(original, "install").ReceiptGeneration);
        Assert.NotEqual(settlement.ReceiptGeneration, Receipt(original, "install").ReceiptGeneration);

        var compensation = FileTransferCoordinator.CompensateRejectedRuntimeUnlockRetryDispatchConfirmation(commit.State, context);
        Assert.True(compensation.Accepted, compensation.Reason);
        var inverse = compensation.SnapshotEnvelope!;
        Assert.Equal(committed.Aggregate.AfterStateGeneration, inverse.Aggregate.BeforeStateGeneration);
        Assert.Equal(committed.Aggregate.AfterStateFingerprint, inverse.Aggregate.BeforeStateFingerprint);
        Assert.Equal(4, inverse.Aggregate.Operations.Count(operation => operation.Family == "runtime_unlock_retry_lifecycle"));
        var restored = Operation(inverse, "attempt_installed", a, "None", "AliasBound");
        var removed = Operation(inverse, "attempt_retained_predecessor", a, "Settled", "None");
        var removedInstall = Operation(inverse, "attempt_installed", b, "Installed", "None");
        var terminal = Operation(inverse, "attempt_terminal", b, "None", "Invalidated");
        Assert.Equal(2, terminal.RuntimeUnlockRetry!.Receipts.Length);
        Assert.Equal(original.RuntimeUnlockRetry.Receipts.ToArray(), restored.RuntimeUnlockRetry!.Receipts.ToArray());
        Assert.Equal(retained.RuntimeUnlockRetry.Receipts.ToArray(), removed.RuntimeUnlockRetry!.Receipts.ToArray());
        Assert.Equal(installed.RuntimeUnlockRetry.Receipts.ToArray(), removedInstall.RuntimeUnlockRetry!.Receipts.ToArray());
        Assert.DoesNotContain(restored.RuntimeUnlockRetry.Receipts, receipt => receipt.Stage == "settlement");
        Assert.Equal(Receipt(installed, "install") with { ReceiptStatus = "Invalidated" }, Receipt(terminal, "install"));
        Assert.Equal(predecessor, compensation.State.PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt);
        Assert.Equal(commit.State.PersistentState.RuntimeUnlockRetryLifecycle.RepairSettlementRelease,
            compensation.State.PersistentState.RuntimeUnlockRetryLifecycle.RepairSettlementRelease);
        Assert.Equal(RuntimeUnlockRetryLifecycleRepairSettlementReleaseState.Consumed,
            compensation.State.PersistentState.RuntimeUnlockRetryLifecycle.RepairSettlementRelease!.Value.State);
        fixture.Envelopes.Add(inverse);
        if (settleRestoredPredecessor)
        {
            var originalReceipt = predecessor.InstallReceipt!.Value;
            var sending = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(
                compensation.State, originalReceipt, RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight,
                predecessor.OfferGeneration, predecessor.OfferNonce!, dispatchObserved: false);
            Assert.True(sending.Accepted, sending.Reason);
            var sendEnvelope = sending.SnapshotEnvelope!;
            AppendContinuation(sendEnvelope);
            Assert.Single(sendEnvelope.Aggregate.Operations.Where(operation => operation.Family == "runtime_unlock_retry_lifecycle"));
            var sendingA = Operation(sendEnvelope, "attempt_installed", a, "AliasBound", "SendInFlight");
            Assert.Equal(Receipt(restored, "stage"), Receipt(sendingA, "stage"));
            Assert.Equal(Receipt(restored, "install") with { ReceiptStatus = "SendInFlight" }, Receipt(sendingA, "install"));
            Assert.Equal(predecessor.Authority, sending.State.CurrentRuntimeUnlockRetryDispatchAuthority);
            Assert.Equal(context.Receipt.Authority,
                sending.State.PersistentState.RuntimeUnlockRetryLifecycle.LastTerminalAttempt!.Value.Authority);

            var report = new RuntimeUnlockRetryDispatchSettlementReport(predecessor.Authority,
                RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved, "dispatch_observed")
            {
                OfferGeneration = predecessor.OfferGeneration, OfferNonce = predecessor.OfferNonce,
                DispatchObserved = true,
            };
            var settled = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(sending.State, report);
            Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, settled.Status);
            var settlementEnvelope = settled.SnapshotEnvelope!;
            AppendContinuation(settlementEnvelope);
            Assert.Equal(3, settlementEnvelope.Aggregate.Operations.Count(operation => operation.Family == "runtime_unlock_retry_lifecycle"));
            var retiredA = Operation(settlementEnvelope, "attempt_installed", a, "SendInFlight", "None");
            var settledA = Operation(settlementEnvelope, "attempt_terminal", a, "None", "Settled");
            var displacedB = Operation(settlementEnvelope, "attempt_terminal", b, "Invalidated", "None");
            Assert.Equal(sendingA.RuntimeUnlockRetry!.Receipts.ToArray(), retiredA.RuntimeUnlockRetry!.Receipts.ToArray());
            Assert.Equal(terminal.RuntimeUnlockRetry!.Receipts.ToArray(), displacedB.RuntimeUnlockRetry!.Receipts.ToArray());
            Assert.Equal(Receipt(restored, "install") with { ReceiptStatus = "Settled" }, Receipt(settledA, "install"));
            Assert.Equal("DispatchObserved", Receipt(settledA, "settlement").ReceiptStatus);
            Assert.True(settledA.RuntimeUnlockRetry!.DispatchObserved);
            Assert.Equal(predecessor.OfferGeneration, Receipt(settledA, "settlement").ReceiptGeneration);
            Assert.Null(settled.State.PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt);
            Assert.Null(settled.State.CurrentRuntimeUnlockRetryDispatchAuthority);
            Assert.Equal(predecessor.Authority, settled.State.PersistentState.RuntimeUnlockRetryLifecycle.LastTerminalAttempt!.Value.Authority);
            Assert.Equal(report, settled.State.PersistentState.RuntimeUnlockRetryLifecycle.LastSettlement);
            Assert.Contains(settled.State.PersistentState.RuntimeUnlockRetryLifecycle.AttemptHistory, attempt =>
                attempt.Authority == context.Receipt.Authority && attempt.State == RuntimeUnlockRetryLifecycleAttemptState.Invalidated &&
                !attempt.DispatchObserved && attempt.Settlement is null);
            Assert.Equal(compensation.State.PersistentState.RuntimeUnlockRetryLifecycle.RepairSettlementRelease,
                settled.State.PersistentState.RuntimeUnlockRetryLifecycle.RepairSettlementRelease);
            Assert.DoesNotContain(new[] { sendEnvelope, settlementEnvelope }.SelectMany(envelope => envelope.Aggregate.Operations)
                .Where(operation => operation.RuntimeUnlockRetry is not null)
                .SelectMany(operation => operation.RuntimeUnlockRetry!.Receipts), receipt => receipt.ReceiptStatus == "Superseded");
        }
        return fixture.Envelopes;

        void AppendContinuation(FileTransferCoordinatorTransitionEnvelope envelope)
        {
            Assert.Equal(fixture.Envelopes[^1].Aggregate.AfterStateGeneration, envelope.Aggregate.BeforeStateGeneration);
            Assert.Equal(fixture.Envelopes[^1].Aggregate.AfterStateFingerprint, envelope.Aggregate.BeforeStateFingerprint);
            fixture.Envelopes.Add(envelope);
        }

        static FileTransferCoordinatorOperationTransitionSnapshot Operation(FileTransferCoordinatorTransitionEnvelope envelope,
            string role, string authorityId, string before, string after)
        {
            var operation = Assert.Single(envelope.Aggregate.Operations.Where(operation =>
                operation.Family == "runtime_unlock_retry_lifecycle" && operation.OperationRole == role && operation.AuthorityId == authorityId));
            Assert.Equal(before, operation.BeforeState);
            Assert.Equal(after, operation.AfterState);
            Assert.True(operation.StateApplied);
            return operation;
        }
        static FileTransferCoordinatorReceiptTransitionSnapshot Receipt(FileTransferCoordinatorOperationTransitionSnapshot operation,
            string stage) => Assert.Single(operation.RuntimeUnlockRetry!.Receipts.Where(receipt => receipt.Stage == stage));
    }

    private sealed record RetryConfirmationFixture(RuntimeUnlockRetryLifecycleTransitionDecision Commit,
        List<FileTransferCoordinatorTransitionEnvelope> Envelopes,
        RuntimeUnlockRetryDispatchContract? PredecessorContract = null);

    private static RetryConfirmationFixture CreateActiveRetryConfirmationFixture()
    {
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        var source = AuthorizeRegularRuntimeUnlockRetryDispatchSource();
        var initial = source.State with { PersistentState = source.State.PersistentState with
        {
            CoordinatorInstanceId = "confirmation-active-predecessor", SnapshotStateGeneration = 0,
            SnapshotStateFingerprint = null,
            RuntimeUnlockRetryLifecycle = source.State.PersistentState.RuntimeUnlockRetryLifecycle with
                { LastInstallGeneration = 40 },
        } };
        var request = source.Request with
        {
            ListenerRearmRequirement = RuntimeUnlockRetryDispatchListenerRearmRequirement.Required,
        };
        var first = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(initial, request,
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement, source.Release.AuthorityId,
            source.Release.RepairGeneration,
            FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved.ToString());
        Assert.True(first.Granted, first.Reason); Capture(first.SnapshotEnvelope!);
        var installedFirst = StageAndInstallPersistentRetryLifecycle(first.State, first, request,
            9_400_000, capture: Capture);
        var alias = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(
            installedFirst.State, installedFirst.Receipt, RuntimeUnlockRetryDispatchAttemptPhase.AliasBound,
            request.RetiredOfferGeneration, "confirmation-active-predecessor", false);
        Assert.True(alias.Accepted, alias.Reason); Capture(alias.SnapshotEnvelope!);
        var successor = FileTransferCoordinator.PrepareRuntimeUnlockRetryDispatchSuccessor(
            alias.State, installedFirst.Receipt.Authority, request.RetiredOfferGeneration);
        Assert.Equal(RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Requested, successor.Status);
        Capture(successor.SnapshotEnvelope!);
        var authorization = successor.AuthorizationDecision!.Value;
        var staged = StagePersistentRetryLifecycle(successor.State, authorization, 9_500_000);
        Assert.True(staged.Accepted, staged.Reason); Capture(staged.SnapshotEnvelope!);
        var issued = FileTransferCoordinator.IssueRuntimeUnlockRetryDispatchInstallReceipt(
            staged.State, authorization, authorization.Request, 9_500_001);
        Assert.True(issued.Accepted, issued.Reason); Capture(issued.SnapshotEnvelope!);
        var commit = FileTransferCoordinator.CommitRuntimeUnlockRetryDispatchInstall(issued.State,
            new RuntimeUnlockRetryDispatchInstallResult(RuntimeUnlockRetryDispatchInstallStatus.Installed,
                "confirmation-active-successor", issued.Receipt!.Value), 9_500_002);
        Assert.True(commit.Accepted, commit.Reason); Capture(commit.SnapshotEnvelope!);
        return new RetryConfirmationFixture(commit, envelopes, first.Contract);

        void Capture(FileTransferCoordinatorTransitionEnvelope envelope)
        {
            if (envelopes.Count > 0)
            {
                Assert.Equal(envelopes[^1].Aggregate.AfterStateGeneration, envelope.Aggregate.BeforeStateGeneration);
                Assert.Equal(envelopes[^1].Aggregate.AfterStateFingerprint, envelope.Aggregate.BeforeStateFingerprint);
            }
            envelopes.Add(envelope);
        }
    }

    private static RetryConfirmationFixture CreateRetryConfirmationFixture(bool predecessor,
        FileTransferDirection direction = FileTransferDirection.Outbound, bool snapshots = true)
    {
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        var initial = ActiveFallbackStateWithoutCheckpoint(direction);
        initial = initial with
        {
            CurrentDataSessionIdentity = "retry-confirmation-data", CurrentDataSessionGeneration = 1,
            PersistentState = initial.PersistentState with { CoordinatorInstanceId = snapshots ? "retry-confirmation-" + direction : null,
                SnapshotStateGeneration = 0, SnapshotStateFingerprint = null },
        };
        var source = AuthorizeFallbackRuntimeUnlockRetryDispatchSource(initial,
            snapshots ? envelopes : null, preserveSnapshotStream: true);
        var granted = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(source.State, source.Request,
            RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield, source.TailYield.AuthorityId,
            source.TailYield.YieldGeneration, source.TailYield.YieldKind.ToString());
        Assert.True(granted.Granted, granted.Reason);
        Capture(granted.SnapshotEnvelope);
        var commit = Install(granted.State, granted, 20_000);
        if (predecessor)
        {
            var receipt = commit.CompensationContext!.Receipt;
            var alias = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(commit.State, receipt,
                RuntimeUnlockRetryDispatchAttemptPhase.AliasBound, 31, "confirmation-predecessor", false);
            Assert.True(alias.Accepted); Capture(alias.SnapshotEnvelope);
            var sending = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(alias.State, receipt,
                RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight, 31, "confirmation-predecessor", false);
            Assert.True(sending.Accepted); Capture(sending.SnapshotEnvelope);
            var stalled = FileTransferCoordinator.EvaluateRuntimeUnlockRetryDispatchDeadline(sending.State,
                receipt.Authority.AuthorityId, receipt.DispatchDeadlineUtcMs);
            Assert.True(stalled.SettlementIssued); Capture(stalled.SnapshotEnvelope);
            var successor = FileTransferCoordinator.PrepareRuntimeUnlockRetryDispatchSuccessor(stalled.State, receipt.Authority, 31);
            Assert.Equal(RuntimeUnlockRetryDispatchSuccessorDecisionStatus.Requested, successor.Status);
            Capture(successor.SnapshotEnvelope);
            commit = Install(successor.State, successor.AuthorizationDecision!.Value, 40_000);
        }
        return new RetryConfirmationFixture(commit, envelopes);

        void Capture(FileTransferCoordinatorTransitionEnvelope? envelope)
        {
            if (envelope is null) { return; }
            if (envelopes.Count > 0)
            {
                Assert.Equal(envelopes[^1].Aggregate.AfterStateGeneration, envelope.Aggregate.BeforeStateGeneration);
                Assert.Equal(envelopes[^1].Aggregate.AfterStateFingerprint, envelope.Aggregate.BeforeStateFingerprint);
            }
            envelopes.Add(envelope);
        }
        RuntimeUnlockRetryLifecycleTransitionDecision Install(FileTransferCoordinatorState currentState,
            RuntimeUnlockRetryDispatchDecision authorization, long time)
        {
            var staged = StagePersistentRetryLifecycle(currentState, authorization, time);
            Assert.True(staged.Accepted, staged.Reason); Capture(staged.SnapshotEnvelope);
            var issued = FileTransferCoordinator.IssueRuntimeUnlockRetryDispatchInstallReceipt(staged.State,
                authorization, authorization.Request, time + 1);
            Assert.True(issued.Accepted, issued.Reason); Capture(issued.SnapshotEnvelope);
            var installed = FileTransferCoordinator.CommitRuntimeUnlockRetryDispatchInstall(issued.State,
                new RuntimeUnlockRetryDispatchInstallResult(RuntimeUnlockRetryDispatchInstallStatus.Installed,
                    "confirmation-fixture", issued.Receipt!.Value), time + 2);
            Assert.True(installed.Accepted, installed.Reason); Capture(installed.SnapshotEnvelope);
            Assert.NotNull(installed.CompensationContext);
            return installed;
        }
    }
}
