using NLink.Core.FileTransfer;
using System.Runtime.CompilerServices;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Fact]
    [Trait("FileTransferCoordinatorSuite", "PostFallbackDialFailureHandoff")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void PostFallbackDialFailure_RetiresExactSettledInstallWithoutInventingProbeEvidence()
    {
        var (state, observation) = CreatePostFallbackDialFailureFixture();
        var prepared = FileTransferCoordinator.PreparePostFallbackDialFailureRetirement(state, observation);
        Assert.True(prepared.Accepted, prepared.Reason);
        Assert.Null(prepared.State.CurrentRuntimeUnlockPendingPreCommitProbe);
        Assert.Null(prepared.State.PersistentState.FileTransferRouteTransition.CurrentOperation);
        var receipt = new RuntimeUnlockRetryDispatchScopeInvalidationReceipt(prepared.Command!,
            RuntimeUnlockRetryDispatchScopeInvalidationStatus.Applied, "applied");
        var committed = FileTransferCoordinator.CommitRuntimeUnlockRetryLifecycleScopeInvalidation(prepared.State, receipt);
        Assert.True(committed.Accepted, committed.Reason);
        Assert.Equal(observation, committed.State.PersistentState.RuntimeUnlockRetryLifecycle.LastPostFallbackDialFailure);
        Assert.Equal(state.CurrentRoutePublicationToken, committed.State.CurrentRoutePublicationToken);
        var pair = committed.State.PersistentState.FallbackTailYield.CurrentOperation!.Value;
        Assert.Equal(FileTransferFallbackTailYieldPairingState.ReleaseConsumed, pair.PairingState);
        var superseded = FileTransferCoordinator.SupersedeFallbackTailPairedYieldAfterRejectedProbe(committed.State,
            FileTransferFallbackTailRetryCycleSupersessionRole.Initiator, pair.Decision, pair.PairedRelease!.Value,
            observation.InstallReceipt, FileTransferCoordinator.PostFallbackDialFailureRetryRetirementReason);
        Assert.True(superseded.Accepted, superseded.Reason);
        Assert.Null(superseded.State.PersistentState.FallbackTailYield.CurrentOperation);
        Assert.True(FileTransferCoordinator.IsFallbackTailRetryCycleSupersessionPending(superseded.State));
        var replay = FileTransferCoordinator.PreparePostFallbackDialFailureRetirement(committed.State, observation);
        Assert.False(replay.Accepted);
        Assert.Same(committed.State, replay.State);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "PostFallbackDialFailureHandoff")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void PostFallbackDialFailure_SnapshotIncludesObservationAndRetirementInOneTransition()
    {
        var (state, observation) = CreatePostFallbackDialFailureFixture();
        state = state with { PersistentState = state.PersistentState with
            { CoordinatorInstanceId = "dial-failure-snapshot", SnapshotStateGeneration = 1,
                SnapshotStateFingerprint = "prior-authoritative-state" } };
        var prepared = FileTransferCoordinator.PreparePostFallbackDialFailureRetirement(state, observation);
        Assert.True(prepared.Accepted, prepared.Reason);
        var envelope = Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(prepared.SnapshotEnvelope);
        Assert.Equal(state.PersistentState.SnapshotStateFingerprint, envelope.Aggregate.BeforeStateFingerprint);
        Assert.Equal(1, envelope.Aggregate.BeforeStateGeneration);
        Assert.Equal(2, envelope.Aggregate.AfterStateGeneration);
        Assert.Contains("runtime_unlock_retry_lifecycle", envelope.Aggregate.ChangedFamilies);
        var committed = FileTransferCoordinator.CommitRuntimeUnlockRetryLifecycleScopeInvalidation(prepared.State,
            new(prepared.Command!, RuntimeUnlockRetryDispatchScopeInvalidationStatus.Applied, "applied"));
        Assert.True(committed.Accepted, committed.Reason);
        Assert.Equal(envelope.Aggregate.AfterStateFingerprint, committed.SnapshotEnvelope!.Aggregate.BeforeStateFingerprint);
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("publication")]
    [InlineData("terminal")]
    [Trait("FileTransferCoordinatorSuite", "PostFallbackDialFailureHandoff")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void PostFallbackDialFailure_RejectsChangedEvidenceAtRetirementCommit(string mutation)
    {
        var (state, observation) = CreatePostFallbackDialFailureFixture();
        var prepared = FileTransferCoordinator.PreparePostFallbackDialFailureRetirement(state, observation);
        Assert.True(prepared.Accepted, prepared.Reason);
        var changed = mutation switch
        {
            "terminal" => prepared.State with { IsTerminal = true },
            "publication" => prepared.State with { CurrentRoutePublicationToken = null },
            _ => prepared.State with { PersistentState = prepared.State.PersistentState with
                { RuntimeUnlockRetryLifecycle = prepared.State.PersistentState.RuntimeUnlockRetryLifecycle with
                    { LastPostFallbackDialFailure = observation with { OfferNonce = "wrong" } } } },
        };
        var rejected = FileTransferCoordinator.CommitRuntimeUnlockRetryLifecycleScopeInvalidation(changed,
            new(prepared.Command!, RuntimeUnlockRetryDispatchScopeInvalidationStatus.Applied, "applied"));
        Assert.False(rejected.Accepted);
        Assert.Same(changed, rejected.State);
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("offer")]
    [InlineData("install")]
    [InlineData("data")]
    [InlineData("epoch")]
    [InlineData("bridge")]
    [InlineData("checkpoint")]
    [InlineData("transaction_transfer")]
    [InlineData("transaction_offer")]
    [InlineData("probe")]
    [InlineData("terminal")]
    [InlineData("budget")]
    [InlineData("invalidation_generation")]
    [Trait("FileTransferCoordinatorSuite", "PostFallbackDialFailureHandoff")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void PostFallbackDialFailure_RejectsStaleOrExhaustedEvidenceWithoutEffects(string mutation)
    {
        var (state, observation) = CreatePostFallbackDialFailureFixture();
        observation = mutation switch
        {
            "nonce" => observation with { OfferNonce = "wrong" },
            "offer" => observation with { OfferGeneration = observation.OfferGeneration + 1 },
            "install" => observation with { InstallReceipt = observation.InstallReceipt!.Value with { InstallGeneration = 99 } },
            "data" => observation with { SourceScope = observation.SourceScope with { DataSessionGeneration = 99 } },
            "epoch" => observation with { SourceScope = observation.SourceScope with { TransportEpoch = 99 } },
            "bridge" => observation with { SourceScope = observation.SourceScope with { BridgeRecoveryGeneration = 99 } },
            "checkpoint" => observation with { SourceScope = observation.SourceScope with { CheckpointRequestId = "wrong" } },
            "transaction_transfer" => observation with { FailedTransaction = observation.FailedTransaction! with { TransferId = "wrong" } },
            "transaction_offer" => observation with { FailedTransaction = observation.FailedTransaction! with { OfferGeneration = 99 } },
            "probe" => observation with { FailedTransaction = observation.FailedTransaction! with { PathProbeState = RuntimeUnlockPathProbeState.Started } },
            _ => observation,
        };
        if (mutation == "terminal") { state = state with { IsTerminal = true }; }
        if (mutation == "budget")
        {
            state = state with { PersistentState = state.PersistentState with
                { RuntimeUnlockRetryLifecycle = state.PersistentState.RuntimeUnlockRetryLifecycle with { LastPostFallbackDialFailure = observation } } };
        }
        if (mutation == "invalidation_generation")
        {
            state = state with { PersistentState = state.PersistentState with
                { RuntimeUnlockRetryLifecycle = state.PersistentState.RuntimeUnlockRetryLifecycle with
                    { LastScopeInvalidationGeneration = long.MaxValue } } };
        }
        var rejected = FileTransferCoordinator.PreparePostFallbackDialFailureRetirement(state, observation);
        Assert.False(rejected.Accepted);
        Assert.Same(state, rejected.State);
        Assert.Null(rejected.Command);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (FileTransferCoordinatorState, RuntimeUnlockPostFallbackDialFailureObservation)
        CreatePostFallbackDialFailureFixture()
    {
        var fixture = CreateRetryConfirmationFixture(false, snapshots: false, clearInitialCheckpoint: true);
        var receipt = fixture.Commit.CompensationContext!.Receipt;
        var alias = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(fixture.Commit.State,
            receipt, RuntimeUnlockRetryDispatchAttemptPhase.AliasBound, 31, "dial-failure", false);
        Assert.True(alias.Accepted, alias.Reason);
        var sending = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(alias.State,
            receipt, RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight, 31, "dial-failure", false);
        Assert.True(sending.Accepted, sending.Reason);
        var settled = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(sending.State,
            new(receipt.Authority, RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved, "peer_visible_offer_send_observed")
            { OfferGeneration = 31, OfferNonce = "dial-failure", DispatchObserved = true });
        Assert.True(settled.Accepted, settled.Reason);
        var state = settled.State;
        var scope = FileTransferCoordinator.GetEffectiveFallbackTailYieldScope(state.PersistentState.FallbackTailYield.CurrentOperation!.Value);
        var observation = new RuntimeUnlockPostFallbackDialFailureObservation(
            new(receipt.Request.SourcePublicationAuthority, scope.TransportEpoch, scope.BridgeRecoveryGeneration,
                scope.CheckpointRequestIdSnapshot, scope.DataSessionIdentity!, scope.DataSessionGeneration),
            31, "dial-failure", 1, receipt, FailedTransaction: RuntimeUnlockTransactionSnapshot.Idle with
            {
                SessionId = state.SessionId, TransferId = state.TransferId, TransactionGeneration = 1, OfferGeneration = 31,
                State = RuntimeUnlockTransactionState.Failed, PeerReceived = true,
                FailureReason = "runtime_unlock_tuna_dial_failed_before_answer",
            });
        return (state, observation);
    }
}
