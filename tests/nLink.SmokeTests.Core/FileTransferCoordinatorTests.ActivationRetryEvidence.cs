using System.Runtime.CompilerServices;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Fact]
    [Trait("FileTransferCoordinatorSuite", "FileTransferRouteTransition")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void FallbackDescendantCommit_InvalidationReceiptAndTerminalMustRemainExact()
    {
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        var (state, request, tailYield) = AuthorizeFallbackRuntimeUnlockRetryDispatchSource(snapshotCapture: envelopes);
        var (root, _) = InstallFailedYieldConsumedReservationFixture(envelopes, state, request, tailYield);
        var rootAttempt = FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(root.State).InstalledAttempt;
        var descendant = InstallActivationRetryDescendant(root.State, envelopes);
        var setup = CreateFallbackV6ToTunaRouteTransitionSetup(descendant);
        var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(setup.State, setup.Request);
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Prepared, prepared.Status);
        var invalidated = InvalidateRuntimeUnlockRetryLifecycleForRouteTransitionCommitForTest(prepared.State);
        var lifecycle = FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(invalidated);
        var receipt = lifecycle.LastScopeInvalidationReceipt!.Value;
        Assert.True(FileTransferCoordinator.TryValidateFileTransferRouteTransitionCommitProof(
            invalidated, prepared.Authority!.Value, setup.FinalProof, out var reason), reason);
        foreach (var altered in new[]
        {
            lifecycle with { LastScopeInvalidationReceipt = null },
            lifecycle with { LastTerminalAttempt = rootAttempt },
            lifecycle with { PendingScopeInvalidationCommand = receipt.Command },
            lifecycle with { LastScopeInvalidationReceipt = receipt with
                { Command = receipt.Command with { InstalledAttempt = rootAttempt } } },
        })
        {
            var rejectedState = invalidated with { PersistentState = invalidated.PersistentState with
                { RuntimeUnlockRetryLifecycle = altered } };
            Assert.False(FileTransferCoordinator.TryValidateFileTransferRouteTransitionCommitProof(
                rejectedState, prepared.Authority.Value, setup.FinalProof, out _));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static FileTransferCoordinatorTransitionEnvelope[] CreateActivationRetryEvidenceFixture(bool descendant)
    {
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        var (state, request, tailYield) = AuthorizeFallbackRuntimeUnlockRetryDispatchSource(snapshotCapture: envelopes);
        var (installed, _) = InstallFailedYieldConsumedReservationFixture(envelopes, state, request, tailYield);
        var effectiveState = descendant ? InstallActivationRetryDescendant(installed.State, envelopes) : installed.State;
        var setup = CreateFallbackV6ToTunaRouteTransitionSetup(effectiveState, envelopes.Add);
        var activation = Assert.IsType<FileTransferTunaActivationTransitionProofBoundary>(setup.Request.ProofBoundary);
        var install = activation.EffectiveRetryInstallReceipt!.Value;
        var release = activation.FallbackTailPairedReleaseReceipt!.Value;
        var committed = CommitEmptyFileTransferRouteTransitionForTest(
            setup.State, setup.Request, setup.FinalProof, capture: envelopes.Add);
        Assert.Equal(FileTransferRoute.FileTunaV4, committed.State.RouteSelection.Route);
        var bindings = envelopes.SelectMany(e => e.Aggregate.Operations)
            .Where(o => o.RouteTransition?.ActivationRetryBinding is not null).ToArray();
        Assert.NotEmpty(bindings);
        var expected = bindings[0].RouteTransition!.ActivationRetryBinding!;
        Assert.Equal(install.Authority.AuthorityId.ToString(), expected.RetryAuthorityId);
        Assert.Equal(install.Authority.AuthorityGeneration, expected.RetryAuthorityGeneration);
        Assert.Equal(install.InstallGeneration, expected.InstallGeneration);
        Assert.Equal(release.ReleaseAuthorityId.ToString(), expected.PairedReleaseAuthorityId);
        Assert.Equal(release.ReleaseGeneration, expected.PairedReleaseGeneration);
        var issued = Assert.Single(envelopes.SelectMany(e => e.Aggregate.Operations),
            o => o.OperationRole == "attempt_installed" && o.BeforeState == "None" && o.AfterState == "Installed" &&
                o.AuthorityId == expected.RetryAuthorityId);
        Assert.Equal(issued.ReceiptFingerprint, expected.InstallReceiptFingerprint);
        Assert.All(bindings, o => Assert.Equal(expected, o.RouteTransition!.ActivationRetryBinding));
        Assert.Contains(bindings, o => o.OperationRole == "current" && o.AfterState == "Prepared");
        Assert.Contains(bindings, o => o.OperationRole == "terminal" && o.AfterState == "Committed");
        return envelopes.ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorState InstallActivationRetryDescendant(
        FileTransferCoordinatorState state, List<FileTransferCoordinatorTransitionEnvelope> envelopes)
    {
        var install = FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(state).InstalledAttempt!.Value.InstallReceipt!.Value;
        var settled = FileTransferCoordinator.ObserveRuntimeUnlockRetryDispatchSettlement(state,
            new RuntimeUnlockRetryDispatchSettlementReport(install.Authority,
                RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch, "pre_offer_dispatch_missing"));
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, settled.Status);
        envelopes.Add(settled.SnapshotEnvelope!);
        var successor = FileTransferCoordinator.PrepareRuntimeUnlockRetryDispatchSuccessor(settled.State,
            new RuntimeUnlockRetryDispatchSuccessorEvaluation(install.Authority, RetiredOfferGeneration: 0)
            { PredecessorOfferGeneration = 0, PredecessorOfferNonce = null, PredecessorDispatchObserved = false });
        Assert.True(successor.Requested, successor.Reason);
        envelopes.Add(successor.SnapshotEnvelope!);
        return StageAndInstallPersistentRetryLifecycle(successor.State, successor.AuthorizationDecision!.Value,
            successor.Request!.Value, stagedUtcMs: 3_001_000, capture: envelopes.Add).State;
    }
}
