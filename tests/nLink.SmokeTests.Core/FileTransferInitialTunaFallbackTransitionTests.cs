using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    [Trait("FileTransferCoordinatorSuite", "InitialTunaFallbackTransition")]
    public void InitialTunaFallbackTransition_ExactProofDrainsAndPublishesFallback(FileTransferDirection direction)
    {
        var state = CreateCompletedInitialFallbackSource(direction);
        var draining = PrepareInitialFallbackTransition(state);
        var committed = CommitInitialFallbackTransition(draining);
        AssertInitialFallbackTransitionApplied(state, committed);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorState CreateCompletedInitialFallbackSource(FileTransferDirection direction,
        List<FileTransferCoordinatorTransitionEnvelope>? envelopes = null)
    {
        var sender = CreateInitialFallbackExchangeFixture(FileTransferDirection.Outbound, true, envelopes, pairedIssuers: true);
        var receiver = CreateInitialFallbackExchangeFixture(FileTransferDirection.Inbound, false, envelopes, pairedIssuers: true);
        var completed = CompleteInitialFallbackPeerExchange(sender, receiver, envelopes is null ? null : envelopes.Add);
        return direction == FileTransferDirection.Outbound ? completed.Sender : completed.Receiver;
    }

    internal static FileTransferCoordinatorTransitionEnvelope[] CreateInitialFallbackTransitionSnapshotFixture(FileTransferDirection direction)
    {
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        var state = CreateCompletedInitialFallbackSource(direction, envelopes);
        var committed = CommitEmptyFileTransferRouteTransitionForTest(state, InitialFallbackTransitionRequest(state),
            finalProof: null, capture: envelopes.Add);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, committed.State.RouteSelection.Route);
        return envelopes.Where(envelope => string.Equals(envelope.Direction, direction.ToString(), StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static FileTransferRouteTransitionPrepareRequest InitialFallbackTransitionRequest(FileTransferCoordinatorState state)
    {
        var exchange = state.PersistentState.FileTransferRouteTransition.BootstrapExchange!;
        Assert.True(FileTransferCoordinator.TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(
            state, exchange.RetainedPeerFrame!, out var boundary, out var reason), reason);
        var command = boundary.Command;
        var proof = new FileTransferFallbackTransitionProofBoundary(null, boundary.PeerProofEvidence,
            boundary.PeerFrameIdentity, command.TargetTransportEpoch, command.SourceBridgeRecoveryGeneration + 1)
        {
            InitialSource = command.InitialSource,
        };
        return new(FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6,
            command.SourceRuntimeOwner, command.SourceRoutePublication, null, FileTransferRoute.FileTunaV4, 4,
            FileTransferTransportKind.Tuna, proof, command.Observation.DurableCheckpoint,
            FileTransferRoute.PostTunaFallbackV6, 6, FileTransferTransportKind.RegularNkn,
            FileTransferTransportHandoffKind.TunaToNormalFallback, null, "initial_tuna_disabled");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferRouteTransitionDecision PrepareInitialFallbackTransition(FileTransferCoordinatorState state)
    {
        var request = InitialFallbackTransitionRequest(state);
        Assert.False(request.ProofBoundary.IsDefault);
        var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(state, request);
        Assert.True(prepared.Status == FileTransferRouteTransitionDecisionStatus.Prepared, prepared.Reason);
        Assert.Equal(state.CurrentRoutePublicationToken, prepared.State.CurrentRoutePublicationToken);
        Assert.Null(prepared.State.CurrentRouteCommit);
        Assert.Null(prepared.State.CurrentLiveRouteEpoch);
        AssertInitialFallbackPreparationIdempotent(prepared.State, request, prepared.Authority!.Value);
        var draining = BeginInitialFallbackTransitionDrain(prepared);
        AssertInitialFallbackPreparationIdempotent(draining.State, request, prepared.Authority!.Value);
        return draining;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertInitialFallbackPreparationIdempotent(FileTransferCoordinatorState state,
        in FileTransferRouteTransitionPrepareRequest request, in FileTransferRouteTransitionAuthority authority)
    {
        var duplicate = FileTransferCoordinator.PrepareFileTransferRouteTransition(state, request);
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Idempotent, duplicate.Status);
        Assert.Equal(authority, duplicate.Authority);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferRouteTransitionDecision CommitInitialFallbackTransition(in FileTransferRouteTransitionDecision draining)
    {
        var authority = draining.Authority!.Value;
        Assert.True(FileTransferCoordinator.TryValidateFileTransferRouteTransitionCommitProof(
            draining.State, authority, null, out var reason), reason);
        var committed = FileTransferCoordinator.CommitFileTransferRouteTransition(draining.State,
            authority, InitialFallbackDrainReceipt(draining), null);
        Assert.True(committed.Status == FileTransferRouteTransitionDecisionStatus.Committed, committed.Reason);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, committed.State.RouteSelection.Route);
        Assert.Equal(6, committed.State.RouteSelection.ProtocolVersion);
        Assert.Equal(1, committed.State.CurrentLiveRouteEpoch!.EpochId);
        Assert.Null(committed.State.CurrentRouteCommit);
        Assert.Equal(2, committed.State.CurrentRoutePublicationToken!.Value.PublicationGeneration);
        Assert.NotNull(committed.RouteDecision);
        Assert.NotNull(committed.RetainedFrameApplyCommand);
        Assert.NotNull(committed.DispositionCommand);
        return committed;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertInitialFallbackTransitionApplied(FileTransferCoordinatorState state,
        in FileTransferRouteTransitionDecision committed)
    {
        Assert.False(FileTransferCoordinator.IsFallbackBootstrapExchangeCurrent(committed.State,
            state.PersistentState.FileTransferRouteTransition.BootstrapExchange!));
        Assert.False(FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(committed.State,
            CorrelatedReceipt(state.PersistentState.FileTransferRouteTransition.BootstrapExchange!.First.Command,
                FileTransferBootstrapPhaseReceiptStatus.Delivered)).Accepted);
        var appliedFrame = FileTransferCoordinator.ApplyFileTransferRouteTransitionRetainedFrameReceipt(committed.State,
            new(committed.RetainedFrameApplyCommand!.Value, FileTransferRouteTransitionRetainedFrameApplyStatus.Applied,
                "filetransfer_route_transition_retained_frame_applied"));
        var applied = FileTransferCoordinator.ApplyFileTransferRouteTransitionDispositionReceipt(appliedFrame.State,
            new(committed.DispositionCommand!.Value, FileTransferRouteTransitionDispositionApplyStatus.Applied,
                "filetransfer_route_transition_committed"));
        Assert.True(FileTransferCoordinator.IsCommittedFileTransferRouteTransitionTerminalEvidenceValid(
            applied.State.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value));
        Assert.Equal(FileTransferRouteTransitionDecisionStatus.Idempotent,
            FileTransferCoordinator.PrepareFileTransferRouteTransition(applied.State, InitialFallbackTransitionRequest(state)).Status);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferRouteTransitionDecision BeginInitialFallbackTransitionDrain(in FileTransferRouteTransitionDecision prepared)
    {
        var draining = FileTransferCoordinator.BeginFileTransferRouteTransitionPhysicalDrain(prepared.State,
            prepared.Authority!.Value,
            new(prepared.PauseBindCommand!.Value, FileTransferRouteTransitionPauseBindStatus.Installed,
                "filetransfer_route_transition_pause_bound"), 0, [],
            FileTransferCoordinator.CreateFileTransferRouteTransitionPhysicalDrainFingerprint([]));
        Assert.True(draining.Status == FileTransferRouteTransitionDecisionStatus.Draining, draining.Reason);
        return draining;
    }

    private static FileTransferRouteTransitionPhysicalDrainReceipt InitialFallbackDrainReceipt(in FileTransferRouteTransitionDecision draining)
    {
        var command = draining.DrainCommand!.Value;
        return new(command, command.CapturedPhysicalSendLeaseIds, command.CapturedPhysicalSendCount,
            command.CapturedPhysicalSendFingerprint, FileTransferRouteTransitionPhysicalDrainOutcome.Completed,
            "physical_drain_completed");
    }
}
