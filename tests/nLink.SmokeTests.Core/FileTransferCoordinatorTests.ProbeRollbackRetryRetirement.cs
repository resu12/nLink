using System.Runtime.CompilerServices;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Fact]
    [Trait("FileTransferCoordinatorSuite", "PostFallbackRetryInstallRetirement")]
    public void ProbeRollbackRetryRetirement_RequiresExactAppliedReceiptAndPreservesConsumedPair()
    {
        var (state, _) = CreateProbeRollbackRetryRetirementFixture();
        AssertProbeRollbackRetirementReceipts(state);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertProbeRollbackRetirementReceipts(FileTransferCoordinatorState state)
    {
        var prepared = FileTransferCoordinator.PrepareRuntimeUnlockRetryLifecycleScopeInvalidation(state,
            FileTransferCoordinator.PostFallbackProbeRollbackRetryRetirementReason);
        Assert.True(prepared.Accepted, prepared.Reason);
        Assert.Equal(state.PersistentState.RuntimeUnlockRetryLifecycle.LastTerminalAttempt, prepared.Command!.InstalledAttempt);
        var receipt = new RuntimeUnlockRetryDispatchScopeInvalidationReceipt(prepared.Command,
            RuntimeUnlockRetryDispatchScopeInvalidationStatus.Applied, "retry_dispatch_scope_invalidation_applied");
        foreach (var invalid in new[] { receipt with { Status = RuntimeUnlockRetryDispatchScopeInvalidationStatus.Rejected },
            receipt with { Command = receipt.Command with { InvalidationGeneration = receipt.Command.InvalidationGeneration + 1 } } })
        {
            var rejected = FileTransferCoordinator.CommitRuntimeUnlockRetryLifecycleScopeInvalidation(prepared.State, invalid);
            Assert.False(rejected.Accepted);
            Assert.Same(prepared.State, rejected.State);
        }
        var committed = FileTransferCoordinator.CommitRuntimeUnlockRetryLifecycleScopeInvalidation(prepared.State, receipt);
        Assert.True(committed.Accepted, committed.Reason);
        Assert.Equal(receipt, committed.State.PersistentState.RuntimeUnlockRetryLifecycle.LastScopeInvalidationReceipt);
        Assert.Null(committed.State.PersistentState.RuntimeUnlockRetryLifecycle.PendingScopeInvalidationCommand);
        Assert.Equal(state.PersistentState.FallbackTailYield, committed.State.PersistentState.FallbackTailYield);
        Assert.Equal(state.CurrentRoutePublicationToken, committed.State.CurrentRoutePublicationToken);
        var duplicate = FileTransferCoordinator.CommitRuntimeUnlockRetryLifecycleScopeInvalidation(committed.State, receipt);
        Assert.Equal(RuntimeUnlockRetryLifecycleTransitionStatus.Idempotent, duplicate.Status);
        Assert.Same(committed.State, duplicate.State);
    }

    [Theory]
    [InlineData("direction")]
    [InlineData("terminal")]
    [InlineData("publication")]
    [InlineData("pending_probe")]
    [InlineData("rollback_receipt")]
    [InlineData("rollback_reason")]
    [InlineData("consumed_install")]
    [InlineData("consumed_authority")]
    [InlineData("pair_state")]
    [InlineData("pending_attempt")]
    [InlineData("settlement")]
    [InlineData("install_receipt")]
    [Trait("FileTransferCoordinatorSuite", "PostFallbackRetryInstallRetirement")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ProbeRollbackRetryRetirement_RejectsChangedProofAtPrepareAndCommit(string mutation)
    {
        var (state, _) = CreateProbeRollbackRetryRetirementFixture();
        var prepared = FileTransferCoordinator.PrepareRuntimeUnlockRetryLifecycleScopeInvalidation(state,
            FileTransferCoordinator.PostFallbackProbeRollbackRetryRetirementReason);
        Assert.True(prepared.Accepted, prepared.Reason);
        var changed = MutateProbeRollbackRetirementState(state, mutation);
        var rejected = FileTransferCoordinator.PrepareRuntimeUnlockRetryLifecycleScopeInvalidation(changed,
            FileTransferCoordinator.PostFallbackProbeRollbackRetryRetirementReason);
        Assert.False(rejected.Accepted);
        Assert.Same(changed, rejected.State);
        var changedPrepared = MutateProbeRollbackRetirementState(prepared.State, mutation);
        var commit = FileTransferCoordinator.CommitRuntimeUnlockRetryLifecycleScopeInvalidation(changedPrepared,
            new RuntimeUnlockRetryDispatchScopeInvalidationReceipt(prepared.Command!,
                RuntimeUnlockRetryDispatchScopeInvalidationStatus.Applied, "retry_dispatch_scope_invalidation_applied"));
        Assert.False(commit.Accepted);
        Assert.Same(changedPrepared, commit.State);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorState MutateProbeRollbackRetirementState(FileTransferCoordinatorState state, string mutation)
    {
        var persistent = state.PersistentState;
        var tail = persistent.FallbackTailYield;
        var lifecycle = persistent.RuntimeUnlockRetryLifecycle;
        var terminal = persistent.FileTransferRouteTransition.LastTerminalOperation!.Value;
        var activation = (FileTransferTunaActivationTransitionProofBoundary)terminal.Authority.ProofBoundary;
        return mutation switch
        {
            "direction" => state with { Direction = FileTransferDirection.Inbound },
            "terminal" => state with { IsTerminal = true },
            "publication" => state with { CurrentRoutePublicationToken = null },
            "pending_probe" => state with { CurrentRuntimeUnlockPendingPreCommitProbe = activation.PendingPreCommitProbe },
            "rollback_receipt" => state with { PersistentState = persistent with { FileTransferRouteTransition = persistent.FileTransferRouteTransition with
                { LastTerminalOperation = terminal with { DispositionReceipt = null } } } },
            "rollback_reason" => state with { PersistentState = persistent with { FileTransferRouteTransition = persistent.FileTransferRouteTransition with
                { LastTerminalOperation = terminal with { DispositionCommand = terminal.DispositionCommand!.Value with { Reason = "unrelated" } } } } },
            "consumed_install" => state with { PersistentState = persistent with { FallbackTailYield = tail with { LastConsumingInstallReceipt = null } } },
            "consumed_authority" => state with { PersistentState = persistent with { FallbackTailYield = tail with { LastConsumedReservation = null } } },
            "pair_state" => state with { PersistentState = persistent with { FallbackTailYield = tail with
                { CurrentOperation = tail.CurrentOperation!.Value with { PairingState = FileTransferFallbackTailYieldPairingState.Invalidated } } } },
            "pending_attempt" => state with { PersistentState = persistent with { RuntimeUnlockRetryLifecycle = lifecycle with { PendingAttempt = lifecycle.LastTerminalAttempt } } },
            "settlement" => state with { PersistentState = persistent with { RuntimeUnlockRetryLifecycle = lifecycle with
                { LastTerminalAttempt = lifecycle.LastTerminalAttempt!.Value with { DispatchObserved = false } } } },
            "install_receipt" => state with { PersistentState = persistent with { RuntimeUnlockRetryLifecycle = lifecycle with
                { LastTerminalAttempt = lifecycle.LastTerminalAttempt!.Value with { InstallReceipt = null } } } },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static (FileTransferCoordinatorState State, List<FileTransferCoordinatorTransitionEnvelope> Envelopes)
        CreateProbeRollbackRetryRetirementFixture()
    {
        var fixture = CreateRetryConfirmationFixture(false, clearInitialCheckpoint: true);
        var envelopes = fixture.Envelopes;
        var setup = CreateFallbackV6ToTunaRouteTransitionSetup(fixture.Commit.State, envelopes.Add);
        var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(setup.State, setup.Request);
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Prepared, prepared.Status);
        envelopes.Add(prepared.SnapshotEnvelope!);
        return FinishProbeRollbackRetryRetirementFixture(prepared.State, envelopes);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (FileTransferCoordinatorState, List<FileTransferCoordinatorTransitionEnvelope>) FinishProbeRollbackRetryRetirementFixture(
        FileTransferCoordinatorState state, List<FileTransferCoordinatorTransitionEnvelope> envelopes)
    {
        var receipt = state.PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt!.Value.InstallReceipt!.Value;
        var alias = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(state, receipt,
            RuntimeUnlockRetryDispatchAttemptPhase.AliasBound, 31, "probe-rollback-retirement", false);
        Assert.True(alias.Accepted, alias.Reason); envelopes.Add(alias.SnapshotEnvelope!);
        var sending = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(alias.State, receipt,
            RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight, 31, "probe-rollback-retirement", false);
        Assert.True(sending.Accepted, sending.Reason); envelopes.Add(sending.SnapshotEnvelope!);
        var settled = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(sending.State,
            new RuntimeUnlockRetryDispatchSettlementReport(receipt.Authority,
                RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved, "peer_visible_offer_send_observed")
            { OfferGeneration = 31, OfferNonce = "probe-rollback-retirement", DispatchObserved = true });
        Assert.True(settled.Accepted, settled.Reason); envelopes.Add(settled.SnapshotEnvelope!);
        var invalidated = FileTransferCoordinator.InvalidateFileTransferRouteTransition(settled.State,
            settled.State.PersistentState.FileTransferRouteTransition.CurrentOperation!.Value.Authority, false,
            "precommit_probe_failed_runtime_unlock_tuna_probe_sidecar_closed");
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Invalidated, invalidated.Status);
        envelopes.Add(invalidated.SnapshotEnvelope!);
        var applied = FileTransferCoordinator.ApplyFileTransferRouteTransitionDispositionReceipt(invalidated.State,
            new FileTransferRouteTransitionDispositionReceipt(invalidated.DispositionCommand!.Value,
                FileTransferRouteTransitionDispositionApplyStatus.Applied, "filetransfer_route_transition_rolled_back"));
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Invalidated, applied.Status);
        envelopes.Add(applied.SnapshotEnvelope!);
        var retired = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbeRetired,
            applied.State.RouteSelection, reason: "runtime_unlock_tuna_probe_sidecar_closed",
            runtimeUnlockPendingPreCommitProbeRequest: applied.State.CurrentRuntimeUnlockPendingPreCommitProbe), applied.State);
        Assert.True(retired.RuntimeUnlockPendingPreCommitProbeDecision!.Value.Accepted);
        envelopes.Add(retired.SnapshotEnvelope!);
        return (retired.State, envelopes);
    }
}
