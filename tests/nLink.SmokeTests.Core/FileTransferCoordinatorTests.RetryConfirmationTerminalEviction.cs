using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Fact]
    [Trait("FileTransferCoordinatorSuite", "RuntimeUnlockRetryLifecycle")]
    public void RuntimeUnlockRetryConfirmation_IndependentInstallEvictsOnlyOldDispatchObservedTerminal()
        => CreateRejectedIndependentRetryConfirmationSnapshotFixture();

    internal static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> CreateRejectedIndependentRetryConfirmationSnapshotFixture()
    {
        var fixture = CreateRetryConfirmationFixture(false, clearInitialCheckpoint: true);
        var envelopes = fixture.Envelopes;
        var receipt = fixture.Commit.CompensationContext!.Receipt;
        var alias = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(fixture.Commit.State,
            receipt, RuntimeUnlockRetryDispatchAttemptPhase.AliasBound, 31, "completed-probe-attempt", false);
        Assert.True(alias.Accepted, alias.Reason); Capture(alias.SnapshotEnvelope!);
        var sending = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchExecutionState(alias.State,
            receipt, RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight, 31, "completed-probe-attempt", false);
        Assert.True(sending.Accepted, sending.Reason); Capture(sending.SnapshotEnvelope!);
        var settled = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(sending.State,
            new RuntimeUnlockRetryDispatchSettlementReport(receipt.Authority,
                RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved, "peer_visible_offer_send_observed")
            { OfferGeneration = 31, OfferNonce = "completed-probe-attempt", DispatchObserved = true });
        Assert.True(settled.Accepted, settled.Reason); Capture(settled.SnapshotEnvelope!);
        var oldTerminal = settled.State.PersistentState.RuntimeUnlockRetryLifecycle.LastTerminalAttempt!.Value;
        var pair = settled.State.PersistentState.FallbackTailYield.CurrentOperation!.Value;
        var retired = FileTransferCoordinator.SupersedeFallbackTailPairedYieldAfterRejectedProbe(settled.State,
            FileTransferFallbackTailRetryCycleSupersessionRole.Initiator, pair.Decision, pair.PairedRelease!.Value,
            receipt, "sidecar_failed_precommit_probe_rollback");
        Assert.True(retired.Accepted, retired.Reason); Capture(retired.SnapshotEnvelope!);
        var source = PairIndependentRetryAfterTerminal(retired.State, envelopes);
        var grant = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(source.State, source.Request,
            RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield, source.TailYield.AuthorityId,
            source.TailYield.YieldGeneration, source.TailYield.YieldKind.ToString());
        Assert.True(grant.Granted, grant.Reason); Capture(grant.SnapshotEnvelope!);
        Assert.Null(grant.Contract!.Authority.PredecessorAuthorityId);
        var stage = StagePersistentRetryLifecycle(grant.State, grant, 40_000);
        Assert.True(stage.Accepted, stage.Reason); Capture(stage.SnapshotEnvelope!);
        var issue = FileTransferCoordinator.IssueRuntimeUnlockRetryDispatchInstallReceipt(stage.State, grant, grant.Request, 40_001);
        Assert.True(issue.Accepted, issue.Reason); Capture(issue.SnapshotEnvelope!);
        var commit = FileTransferCoordinator.CommitRuntimeUnlockRetryDispatchInstall(issue.State,
            new RuntimeUnlockRetryDispatchInstallResult(RuntimeUnlockRetryDispatchInstallStatus.Installed,
                "independent-confirmation", issue.Receipt!.Value), 40_002);
        Assert.True(commit.Accepted, commit.Reason); Capture(commit.SnapshotEnvelope!);
        Assert.Null(commit.CompensationContext!.PredecessorBeforeInstall);
        Assert.Equal(oldTerminal, commit.State.PersistentState.RuntimeUnlockRetryLifecycle.LastTerminalAttempt);
        var compensation = FileTransferCoordinator.CompensateRejectedRuntimeUnlockRetryDispatchConfirmation(
            commit.State, commit.CompensationContext);
        Assert.True(compensation.Accepted, compensation.Reason); Capture(compensation.SnapshotEnvelope!);
        Assert.Null(compensation.State.PersistentState.RuntimeUnlockRetryLifecycle.InstalledAttempt);
        var operations = compensation.SnapshotEnvelope!.Aggregate.Operations;
        Assert.Equal(3, operations.Length);
        var displaced = Assert.Single(operations.Where(op => op.OperationRole == "attempt_terminal" && op.AfterState == "None"));
        Assert.Equal("Settled", displaced.BeforeState);
        Assert.Equal("DispatchObserved", displaced.ReceiptStatus);
        Assert.Equal(oldTerminal.Authority.AuthorityGeneration, displaced.OperationGeneration);
        var invalidated = Assert.Single(operations.Where(op => op.AfterState == "Invalidated"));
        Assert.NotEqual(displaced.AuthorityId, invalidated.AuthorityId);
        Assert.Equal(issue.Receipt.Value.Authority.AuthorityGeneration, invalidated.OperationGeneration);
        return envelopes;

        void Capture(FileTransferCoordinatorTransitionEnvelope envelope)
        {
            Assert.Equal(envelopes[^1].Aggregate.AfterStateGeneration, envelope.Aggregate.BeforeStateGeneration);
            Assert.Equal(envelopes[^1].Aggregate.AfterStateFingerprint, envelope.Aggregate.BeforeStateFingerprint);
            envelopes.Add(envelope);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (FileTransferCoordinatorState State, RuntimeUnlockRetryDispatchAuthorityRequest Request,
        FallbackTailYieldDecision TailYield) PairIndependentRetryAfterTerminal(
        FileTransferCoordinatorState state, List<FileTransferCoordinatorTransitionEnvelope> envelopes)
    {
        var leg = state.CurrentLeg!;
        var yielded = FileTransferCoordinator.Apply(CoordinatorEvent(
            FileTransferCoordinatorEventKind.FallbackTailQuiescent, state.RouteSelection,
            reason: "sidecar_failed_precommit_probe_rollback", state: FileTransferLegState.Active,
            canSendData: true, committedChunk: leg.ProvenCommittedChunkIndex,
            highestObservedChunk: leg.ProvenHighestObservedChunkIndex,
            transportEpoch: leg.TransportEpochId, bridgeRecoveryGeneration: leg.BridgeRecoveryGeneration), state);
        Assert.Null(yielded.MutationRejectedReason);
        var tail = yielded.FallbackTailYieldDecision!.Value;
        var local = FileTransferCoordinator.ApplyFallbackTailLocalProofReceipt(yielded.State,
            FileTransferCoordinator.CreateFallbackTailLocalProofReceipt(yielded.State, tail,
                "second-local-proof", "second-local-frame", 39_000));
        Assert.True(local.Accepted, local.Reason);
        var peerProof = CreateCoordinatorFallbackTailPeerProof(local.State, tail) with
        {
            AuthorityId = new FileTransferAuthorityId("fta2-fallback_tail_yield-0123456789abcdef01234567"),
            PredecessorAuthorityId = new FileTransferAuthorityId("fta2-fallback_tail_yield-89abcdef0123456789abcdef"),
            YieldGeneration = 2,
        };
        var paired = FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt(local.State,
            FileTransferCoordinator.CreateFallbackTailPeerProofReceipt(local.State, peerProof, "second-peer-proof", 39_001));
        Assert.True(paired.Accepted, paired.Reason);
        envelopes.Add(yielded.SnapshotEnvelope!);
        envelopes.Add(local.SnapshotEnvelope!);
        envelopes.Add(paired.SnapshotEnvelope!);
        Assert.True(FileTransferCoordinator.TryCreateFallbackPairedTailYieldRuntimeUnlockRetryDispatchRequest(
            paired.State, paired.PairedRelease!.Value, "runtime_unlock_offer_send_not_observed",
            "post_tuna_fallback_tail_unblocked", out var request, out var reason), reason);
        return (paired.State, request, tail);
    }
}
