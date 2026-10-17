using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferCoordinatorSuite", "ResponderProbeHandoff")]
    public void ConsumedResponderHandoff_ExactInstallCommitsWithoutReconsumption(bool settled)
    {
        var state = CreateConsumedResponderState(settled);
        var receipt = state.PersistentState.FallbackTailYield.LastConsumingInstallReceipt!.Value;
        var release = state.PersistentState.FallbackTailYield.CurrentOperation!.Value.PairedRelease!.Value;
        var setup = BindResponderHandoffForTest(state, release, listenerReceiver: true, receipt);
        Assert.Equal(state.PersistentState.FallbackTailYield.LastConsumedReservation,
            setup.Bound.State.PersistentState.FallbackTailYield.LastConsumedReservation);
        Assert.Equal(state.PersistentState.RuntimeUnlockRetryLifecycle,
            setup.Bound.State.PersistentState.RuntimeUnlockRetryLifecycle);
        Assert.Equal(receipt, setup.Bound.Authority!.Value.Observation.ConsumingRetryInstallReceipt);
        CompletePairedResponderHandoffForCheckpoint(setup.Bound, setup.PairedRelease, setup.Observation,
            setup.Admission, setup.Pending, setup.PeerReceiveScope, ownerWorkHeld: true);
    }

    [Theory]
    [InlineData("missing_receipt")]
    [InlineData("replaced_receipt")]
    [InlineData("wrong_answer_source")]
    [InlineData("different_release")]
    [InlineData("different_consuming_install")]
    [InlineData("different_offer")]
    [InlineData("failed_dispatch")]
    [InlineData("new_pending_retry")]
    [InlineData("scope_invalidated")]
    [InlineData("terminal")]
    [InlineData("session")]
    [InlineData("data_session")]
    [InlineData("repair_generation")]
    [Trait("FileTransferCoordinatorSuite", "ResponderProbeHandoff")]
    public void ConsumedResponderHandoff_RejectsStaleEvidenceBeforeBindAndAfterHold(string control)
    {
        var state = CreateConsumedResponderState(settled: true);
        var tail = state.PersistentState.FallbackTailYield;
        var receipt = tail.LastConsumingInstallReceipt!.Value;
        var setup = BindResponderHandoffForTest(state, tail.CurrentOperation!.Value.PairedRelease!.Value, true, receipt);
        var held = FileTransferCoordinator.HoldRuntimeUnlockResponderPeerProbeRouteTransitionHandoffForOwnerWork(
            setup.Bound.State, setup.Bound.Authority!.Value, "owner_work_pending");
        Assert.Equal(RuntimeUnlockResponderPeerProbeRouteTransitionHandoffStatus.Held, held.Status);
        var observation = setup.Observation;
        var changed = held.State;
        var lifecycle = changed.PersistentState.RuntimeUnlockRetryLifecycle;
        switch (control)
        {
            case "missing_receipt": observation = observation with { ConsumingRetryInstallReceipt = null }; break;
            case "replaced_receipt": observation = observation with
                { ConsumingRetryInstallReceipt = receipt with { InstallGeneration = receipt.InstallGeneration + 1 } }; break;
            case "wrong_answer_source": observation = observation with { AnswerAcceptanceSource = "SemanticAnswerAck" }; break;
            case "different_release":
                tail = tail with { LastConsumedReservation = tail.LastConsumedReservation!.Value with
                    { Release = tail.CurrentOperation!.Value.PairedRelease!.Value with { PairingGeneration = 999 } } }; break;
            case "different_consuming_install":
                tail = tail with { LastConsumingInstallReceipt = receipt with { InstallGeneration = receipt.InstallGeneration + 1 } }; break;
            case "different_offer":
                lifecycle = lifecycle with { LastTerminalAttempt = lifecycle.LastTerminalAttempt!.Value with { OfferGeneration = 999 } }; break;
            case "failed_dispatch":
                lifecycle = lifecycle with { LastTerminalAttempt = lifecycle.LastTerminalAttempt!.Value with
                    { DispatchObserved = false, Settlement = null } }; break;
            case "new_pending_retry": lifecycle = lifecycle with { PendingAttempt = lifecycle.LastTerminalAttempt }; break;
            case "terminal": changed = changed with { IsTerminal = true }; break;
            case "session": changed = changed with { SessionId = "replacement-session" }; break;
            case "data_session": changed = changed with { CurrentDataSessionGeneration = changed.CurrentDataSessionGeneration + 1 }; break;
            case "repair_generation": tail = tail with { CurrentOperation = tail.CurrentOperation!.Value with
                { Scope = tail.CurrentOperation!.Value.Scope with
                    { BridgeRecoveryGeneration = tail.CurrentOperation!.Value.Scope.BridgeRecoveryGeneration + 1 } } }; break;
        }
        changed = changed with { PersistentState = changed.PersistentState with
            { FallbackTailYield = tail, RuntimeUnlockRetryLifecycle = lifecycle } };
        if (control == "scope_invalidated")
        {
            changed = InvalidateRuntimeUnlockRetryLifecycleForRouteTransitionCommitForTest(changed);
            lifecycle = changed.PersistentState.RuntimeUnlockRetryLifecycle;
            tail = changed.PersistentState.FallbackTailYield;
        }
        var authority = setup.Bound.Authority!.Value with { Observation = observation };
        Assert.False(FileTransferCoordinator.TryValidateRuntimeUnlockResponderPeerProbeRouteTransitionHandoffAuthorityCurrent(
            changed, authority, out _));
        // Remove the retained binding to exercise the same proof checks before
        // initial binding, rather than merely the retained-tuple mutation guard.
        var unbound = changed with { PersistentState = changed.PersistentState with
            { RuntimeUnlockResponderPeerProbeRouteTransitionHandoff = default } };
        Assert.False(FileTransferCoordinator.TryPrevalidateRuntimeUnlockResponderPeerProbeRouteTransitionObservation(
            unbound, observation, setup.Admission, out _, out _));
        var rejected = FileTransferCoordinator.BindRuntimeUnlockResponderPeerProbeRouteTransitionHandoff(
            unbound, observation, setup.Admission, setup.Pending);
        Assert.NotEqual(RuntimeUnlockResponderPeerProbeRouteTransitionHandoffStatus.Accepted, rejected.Status);
        Assert.Null(rejected.Authority);
        Assert.Equal(unbound.RouteSelection, rejected.State.RouteSelection);
        Assert.Equal(unbound.CurrentRoutePublicationToken, rejected.State.CurrentRoutePublicationToken);
        Assert.Equal(lifecycle, rejected.State.PersistentState.RuntimeUnlockRetryLifecycle);
        Assert.Equal(tail, rejected.State.PersistentState.FallbackTailYield);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertConsumedResponderRetirementScope(FileTransferCoordinatorState state,
        RuntimeUnlockResponderPeerProbeRouteTransitionHandoffAuthority handoff)
    {
        Assert.True(FileTransferCoordinator.TryValidateRuntimeUnlockResponderPeerProbeRouteTransitionHandoffAuthorityCurrent(
            state, handoff, out var reason), reason);
        Assert.False(FileTransferCoordinator.TryGetCurrentRuntimeUnlockRetryDispatchInstallReceipt(state, out _));
        Assert.False(FileTransferCoordinator.IsRuntimeUnlockRetryDispatchAuthorityCurrent(state,
            handoff.Observation.ConsumingRetryInstallReceipt!.Value.Authority, requireInstalled: true, out _));
        var transition = state.PersistentState.FileTransferRouteTransition;
        var operation = transition.CurrentOperation!.Value;
        var activation = Assert.IsType<FileTransferTunaActivationTransitionProofBoundary>(operation.Authority.ProofBoundary);
        var invalidTransitions = new[]
        {
            transition with { CurrentOperation = null },
            transition with { CurrentOperation = operation with { State = FileTransferRouteTransitionState.Prepared } },
            transition with { CurrentOperation = operation with { Authority = operation.Authority with
                { ProofBoundary = activation with { ResponderPeerProbeHandoffAuthority = handoff with
                    { Observation = handoff.Observation with { OfferGeneration = handoff.Observation.OfferGeneration + 1 } } } } } },
        };
        foreach (var changed in invalidTransitions)
            Assert.False(FileTransferCoordinator.TryValidateRuntimeUnlockResponderPeerProbeRouteTransitionHandoffAuthorityCurrent(
                state with { PersistentState = state.PersistentState with { FileTransferRouteTransition = changed } }, handoff, out _));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorState CreateConsumedResponderState(bool settled)
    {
        var fixture = CreateRetryConfirmationFixture(false, FileTransferDirection.Inbound);
        var receipt = fixture.Commit.CompensationContext!.Receipt;
        var alias = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(fixture.Commit.State,
            receipt, RuntimeUnlockRetryDispatchAttemptPhase.AliasBound, 22, "consumed-responder", false);
        Assert.True(alias.Accepted, alias.Reason);
        if (!settled)
            return alias.State;
        var sending = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(alias.State,
            receipt, RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight, 22, "consumed-responder", false);
        Assert.True(sending.Accepted, sending.Reason);
        var result = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(sending.State,
            new RuntimeUnlockRetryDispatchSettlementReport(receipt.Authority,
                RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved, "dispatch_observed")
            { OfferGeneration = 22, OfferNonce = "consumed-responder", DispatchObserved = true });
        Assert.True(result.Status == RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, result.Reason);
        Assert.True(FileTransferCoordinator.TryGetCurrentRuntimeUnlockRetryDispatchInstallReceipt(result.State, out var current));
        Assert.Equal(receipt, current);
        return result.State;
    }
}
