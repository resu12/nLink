using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    [Trait("FileTransferCoordinatorSuite", "InitialTunaFallbackTransition")]
    public void InitialTunaFallbackTransition_RejectsMissingMixedAndStaleProof(FileTransferDirection direction)
    {
        var state = CreateCompletedInitialFallbackSource(direction);
        AssertInitialFallbackPrepareRejections(state, InitialFallbackTransitionRequest(state));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertInitialFallbackPrepareRejections(FileTransferCoordinatorState state,
        in FileTransferRouteTransitionPrepareRequest request)
    {
        var proof = Assert.IsType<FileTransferFallbackTransitionProofBoundary>(request.ProofBoundary);
        var source = proof.InitialSource!;
        foreach (var invalid in new[]
        {
            proof with { InitialSource = null },
            proof with { SourceRouteCommit = default(RouteCommitToken) },
            proof with { PredecessorTransitionAuthorityId = new("foreign-predecessor") },
            proof with { InitialSource = source with { DataSessionIdentity = "replacement" } },
            proof with { InitialSource = source with { DataSessionGeneration = source.DataSessionGeneration + 1 } },
            proof with { InitialSource = source with { Publication = source.Publication with { PublicationGeneration = 2 } } },
            proof with { PeerProofEvidence = proof.PeerProofEvidence with { ProofGeneration = proof.PeerProofEvidence.ProofGeneration + 1 } },
            proof with { TargetTransportEpoch = proof.TargetTransportEpoch + 1 },
        })
        {
            AssertInitialFallbackPrepareRejected(state, request with { ProofBoundary = invalid });
        }
        AssertInitialFallbackPrepareRejected(state, request with
        {
            DurableCheckpoint = request.DurableCheckpoint with { CheckpointGeneration = request.DurableCheckpoint.CheckpointGeneration + 1 },
        });
        for (var mutation = 0; mutation < 12; mutation++)
        {
            AssertInitialFallbackPrepareRejected(MutateInitialFallbackTransitionSource(state, mutation), request);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertInitialFallbackPrepareRejected(FileTransferCoordinatorState state,
        in FileTransferRouteTransitionPrepareRequest request)
        => AssertInitialFallbackRejectedWithoutAuthority(state, FileTransferCoordinator.PrepareFileTransferRouteTransition(state, request));

    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    [Trait("FileTransferCoordinatorSuite", "InitialTunaFallbackTransition")]
    public void InitialTunaFallbackTransition_LateDrainAndInvalidatedSourceCannotPublish(FileTransferDirection direction)
    {
        var state = CreateCompletedInitialFallbackSource(direction);
        var draining = PrepareInitialFallbackTransition(state);
        AssertInitialFallbackDrainRejections(draining);
        AssertInitialFallbackInvalidationRetiresProof(draining);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertInitialFallbackDrainRejections(in FileTransferRouteTransitionDecision draining)
    {
        var authority = draining.Authority!.Value;
        var receipt = InitialFallbackDrainReceipt(draining);
        for (var mutation = 0; mutation < 12; mutation++)
        {
            var stale = MutateInitialFallbackTransitionSource(draining.State, mutation);
            Assert.False(FileTransferCoordinator.TryValidateFileTransferRouteTransitionCommitProof(stale, authority, null, out _));
            AssertInitialFallbackCommitRejected(stale, authority, receipt);
        }
        AssertInitialFallbackCommitRejected(draining.State, authority,
            receipt with { CompletedPhysicalSendFingerprint = "wrong" });
        var retired = ReplaceInitialFallbackCurrentOperation(draining.State, null);
        AssertInitialFallbackCommitRejected(retired, authority, receipt);
        var operation = draining.State.PersistentState.FileTransferRouteTransition.CurrentOperation!.Value;
        var unrelated = ReplaceInitialFallbackCurrentOperation(draining.State,
            operation with { Authority = authority with { Reason = "unrelated_transition" } });
        AssertInitialFallbackCommitRejected(unrelated, authority, receipt);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertInitialFallbackInvalidationRetiresProof(in FileTransferRouteTransitionDecision draining)
    {
        var invalidated = FileTransferCoordinator.InvalidateFileTransferRouteTransition(draining.State,
            draining.Authority!.Value, terminalInvalidation: false, "initial_source_retired");
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Invalidated, invalidated.Status);
        AssertInitialFallbackCommitRejected(invalidated.State, draining.Authority.Value, InitialFallbackDrainReceipt(draining));
        Assert.False(FileTransferCoordinator.CanStartFallbackBootstrapExchange(invalidated.State));
        Assert.False(FileTransferCoordinator.IsFallbackBootstrapExchangeCurrent(invalidated.State,
            draining.State.PersistentState.FileTransferRouteTransition.BootstrapExchange!));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertInitialFallbackCommitRejected(FileTransferCoordinatorState state,
        in FileTransferRouteTransitionAuthority authority, in FileTransferRouteTransitionPhysicalDrainReceipt receipt)
        => AssertInitialFallbackRejectedWithoutAuthority(state,
            FileTransferCoordinator.CommitFileTransferRouteTransition(state, authority, receipt, null));

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertInitialFallbackRejectedWithoutAuthority(FileTransferCoordinatorState state,
        in FileTransferRouteTransitionDecision rejected)
    {
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Rejected, rejected.Status);
        Assert.Null(rejected.RouteDecision);
        Assert.Null(rejected.DispositionCommand);
        Assert.Null(rejected.RetainedFrameApplyCommand);
        Assert.Equal(state.CurrentRoutePublicationToken, rejected.State.CurrentRoutePublicationToken);
        Assert.Equal(state.PersistentState.FileTransferRouteTransition, rejected.State.PersistentState.FileTransferRouteTransition);
        Assert.All(rejected.SnapshotEnvelope!.Aggregate.Operations, operation => Assert.False(operation.StateApplied));
    }

    private static FileTransferCoordinatorState MutateInitialFallbackTransitionSource(FileTransferCoordinatorState state, int mutation)
        => mutation switch
        {
            0 => state with { CurrentDataSessionIdentity = "replacement" },
            1 => state with { CurrentDataSessionGeneration = state.CurrentDataSessionGeneration + 1 },
            2 => state with { LastRouteCommitGeneration = 1 },
            3 => state with { LastLiveRouteEpochId = 1 },
            4 => state with { IsTerminal = true },
            5 => state with { CurrentRoutePublicationToken = state.CurrentRoutePublicationToken!.Value with { PublicationGeneration = 2 } },
            _ => MutateInitialFallbackExchangeProof(state, mutation),
        };

    private static FileTransferCoordinatorState MutateInitialFallbackExchangeProof(FileTransferCoordinatorState state, int mutation)
    {
        var transition = state.PersistentState.FileTransferRouteTransition;
        transition = mutation switch
        {
            6 => transition with { BootstrapExchange = null },
            7 => transition with { BootstrapExchange = transition.BootstrapExchange! with { Completed = false } },
            8 => transition with { BootstrapExchange = transition.BootstrapExchange! with { Invalidated = true } },
            9 => transition with { LastFallbackV6ProofBootstrapReceipt = null },
            10 => transition with { BootstrapSourceHold = null },
            11 => transition with { BootstrapAgreement = null },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        return state with { PersistentState = state.PersistentState with { FileTransferRouteTransition = transition } };
    }

    private static FileTransferCoordinatorState ReplaceInitialFallbackCurrentOperation(FileTransferCoordinatorState state,
        FileTransferRouteTransitionOperation? operation)
        => state with { PersistentState = state.PersistentState with { FileTransferRouteTransition =
            state.PersistentState.FileTransferRouteTransition with { CurrentOperation = operation } } };
}
