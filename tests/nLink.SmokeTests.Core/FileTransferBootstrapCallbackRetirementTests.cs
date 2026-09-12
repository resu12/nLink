using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData("exact")] [InlineData("no_successor")] [InlineData("publication")]
    [InlineData("owner")] [InlineData("agreement")] [InlineData("terminal")]
    [InlineData("missing_capture")] [InlineData("wrong_capture")] [InlineData("wrong_callback")]
    [InlineData("missing_receipt")] [InlineData("invalidated_exchange")]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void BootstrapCallbackRetirement_RequiresAcceptedCaptureAndExactSuccessor(string mutation)
    {
        var history = CreateBootstrapCallbackRetirementHistory();
        var state = MutateBootstrapCallbackRetirementState(history, mutation);
        var callback = mutation == "wrong_callback" ? history.Callback with { LeaseGeneration = 72 } : history.Callback;
        var capture = mutation == "missing_capture" ? null : mutation == "wrong_capture"
            ? history.Capture with { OutboundCallbacks = [] } : history.Capture;
        var retirement = FileTransferCoordinator.IssueFallbackBootstrapSourceCallbackRetirement(state, callback, capture);
        if (mutation != "exact") { Assert.Null(retirement); return; }
        Assert.NotNull(retirement);
        Assert.True(FileTransferCoordinator.IsFallbackBootstrapSourceCallbackRetired(state, callback, capture, retirement));
        var next = StartCorrelated(state).State;
        // The committed source proof survives the next exchange replacing its original exchange record.
        Assert.True(FileTransferCoordinator.HasCommittedBootstrapSourceCallbackSuccessor(next, callback.Source));
        Assert.True(FileTransferCoordinator.IsFallbackBootstrapSourceCallbackRetired(next, callback, capture, retirement));
        Assert.False(FileTransferCoordinator.IsFallbackBootstrapSourceCallbackRetired(next, callback, capture,
            retirement with { Successor = retirement.Successor with { PublicationGeneration = retirement.Successor.PublicationGeneration + 1 } }));
        Assert.False(FileTransferCoordinator.IsFallbackBootstrapSourceCallbackRetired(next, callback with { LeaseGeneration = 72 }, capture, retirement));
        Assert.False(FileTransferCoordinator.IsFallbackBootstrapSourceCallbackRetired(next, callback, capture! with { PauseInstance = capture.PauseInstance + 1 }, retirement));
        Assert.False(FileTransferCoordinator.IsFallbackBootstrapSourceCallbackRetired(MutateBootstrapCallbackStateForTest(next, "publication"), callback, capture, retirement));
        Assert.False(FileTransferCoordinator.IsFallbackBootstrapSourceCallbackRetired(history.Initial, callback, capture, retirement));
    }

    private sealed record BootstrapCallbackRetirementHistory(FileTransferCoordinatorState Initial, FileTransferCoordinatorState Completed,
        FileTransferCoordinatorState Reactivated, FileTransferBootstrapSourceCallback Callback, FileTransferBootstrapSourceHoldCapture Capture);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void BootstrapCallbackRetirement_SameEpochSuccessorUsesOnlyCorePublicationLineage(bool restart)
    {
        var history = CreateBootstrapCallbackRetirementHistory();
        var before = CommitCorrelatedFallback(history.Completed);
        var retirement = FileTransferCoordinator.IssueFallbackBootstrapSourceCallbackRetirement(before, history.Callback, history.Capture)!;
        Assert.NotNull(retirement);
        var after = PublishCallbackRecoveryForTest(before, restart);
        Assert.Equal(before.CurrentLiveRouteEpoch!.EpochId, after.CurrentLiveRouteEpoch!.EpochId);
        Assert.NotEqual(before.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
        Assert.True(FileTransferCoordinator.HasCommittedBootstrapSourceCallbackSuccessor(after, history.Callback.Source));
        Assert.True(FileTransferCoordinator.IsFallbackBootstrapSourceCallbackRetired(after, history.Callback, history.Capture, retirement));
        // Issuance after recovery must not depend on a Service call before publication changed.
        Assert.Equal(retirement, FileTransferCoordinator.IssueFallbackBootstrapSourceCallbackRetirement(after, history.Callback, history.Capture));
        foreach (var mutation in new[] { "missing", "publication", "identity", "coordinator" })
        {
            var changed = MutateCallbackLineageForTest(after, mutation);
            Assert.False(FileTransferCoordinator.HasCommittedBootstrapSourceCallbackSuccessor(changed, history.Callback.Source));
            Assert.False(FileTransferCoordinator.IsFallbackBootstrapSourceCallbackRetired(changed, history.Callback, history.Capture, retirement));
            Assert.Null(FileTransferCoordinator.IssueFallbackBootstrapSourceCallbackRetirement(changed, history.Callback, history.Capture));
        }
        Assert.False(FileTransferCoordinator.HasCommittedBootstrapSourceCallbackSuccessor(after,
            history.Callback.Source with { Source = history.Callback.Source.Source with { LiveRouteEpoch = history.Callback.Source.Source.LiveRouteEpoch + 1 } }));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorState PublishCallbackRecoveryForTest(FileTransferCoordinatorState state, bool restart)
    {
        if (restart)
        {
            var decision = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer, state.RouteSelection,
                reason: "callback_retirement_recovery", state: FileTransferLegState.RecoveryActive, canSendData: true,
                transportEpoch: state.CurrentLeg!.TransportEpochId + 1, bridgeRecoveryGeneration: state.CurrentLeg.BridgeRecoveryGeneration), state);
            Assert.Null(decision.MutationRejectedReason);
            Assert.True(decision.State.CurrentRuntimeOwner!.Value.RuntimeGeneration > state.CurrentRuntimeOwner!.Value.RuntimeGeneration);
            Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(decision.State, decision.State.CurrentRoutePublicationToken!.Value, out _));
            return decision.State;
        }
        var published = FileTransferCoordinator.AuthorizeAction(state, new FileTransferCoordinatorActionRequest(
            FileTransferCoordinatorActionKind.RoutePublication, "test", "callback_retirement_publication", RuntimeOwner: state.CurrentRuntimeOwner,
            Route: state.RouteSelection.Route, ProtocolVersion: state.RouteSelection.ProtocolVersion, LiveRouteEpoch: state.CurrentLiveRouteEpoch!.EpochId,
            LegGeneration: state.CurrentLeg!.Generation, RuntimeGeneration: state.CurrentRuntimeOwner!.Value.RuntimeGeneration,
            TerminalGeneration: state.TerminalGeneration, TargetRoute: state.RouteSelection.Route, TargetProtocolVersion: state.RouteSelection.ProtocolVersion,
            TargetTransport: FileTransferTransportKind.RegularNkn));
        Assert.True(published.Accepted, published.RejectionReason);
        return published.State;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorState MutateCallbackLineageForTest(FileTransferCoordinatorState state, string mutation)
    {
        var lineage = state.PersistentState.BootstrapSourceCallbackLineage!;
        return mutation switch
        {
            "missing" => state with { PersistentState = state.PersistentState with { BootstrapSourceCallbackLineage = null } },
            "identity" => state with { PersistentState = state.PersistentState with { BootstrapSourceCallbackLineage = lineage with { Identity = "unissued" } } },
            "coordinator" => state with { PersistentState = state.PersistentState with { CoordinatorInstanceId = "other" } },
            _ => MutateBootstrapCallbackStateForTest(state, "publication"),
        };
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorState MutateBootstrapCallbackRetirementState(BootstrapCallbackRetirementHistory history, string mutation)
    {
        var state = mutation == "no_successor" ? history.Completed : MutateBootstrapCallbackStateForTest(history.Reactivated, mutation);
        if (mutation == "missing_receipt")
        { return state with { PersistentState = state.PersistentState with { FileTransferRouteTransition = state.PersistentState.FileTransferRouteTransition with
            { BootstrapSourceHold = state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold! with { Receipt = null } } } }; }
        if (mutation == "invalidated_exchange")
        { return state with { PersistentState = state.PersistentState with { FileTransferRouteTransition = state.PersistentState.FileTransferRouteTransition with
            { BootstrapExchange = state.PersistentState.FileTransferRouteTransition.BootstrapExchange! with { Invalidated = true } } } }; }
        return state;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static BootstrapCallbackRetirementHistory CreateBootstrapCallbackRetirementHistory(Action<FileTransferCoordinatorTransitionEnvelope>? captureEnvelope = null)
    {
        void CaptureEnvelope(FileTransferCoordinatorTransitionEnvelope envelope)
        { if (envelope.Direction == "outbound") { captureEnvelope?.Invoke(envelope); } }
        var pair = CreateCorrelatedBootstrapPair(capture: CaptureEnvelope);
        var initial = pair.Sender;
        var callback = FileTransferCoordinator.IssueFallbackBootstrapSourceCallback(initial, 71, 7, false, 0)!;
        Assert.NotNull(callback);
        var admitted = FileTransferCoordinator.CreateBootstrapSourceCallbackSnapshot(initial, callback, "Admitted",
            FileTransferBootstrapSourceCallbackDisposition.Current, "none");
        CaptureEnvelope(admitted.Envelope);
        var observation = StartCorrelated(admitted.State).Command!.Bootstrap.Observation;
        var prepared = FileTransferCoordinator.PrepareFallbackBootstrapSourceHold(admitted.State, observation, null);
        Assert.True(prepared.Accepted); CaptureEnvelope(prepared.SnapshotEnvelope!);
        var capture = new FileTransferBootstrapSourceHoldCapture(prepared.Command!, 99, [], [], []) { OutboundCallbacks = [callback] };
        var applied = FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldCapture(prepared.State, capture);
        Assert.True(applied.Accepted); CaptureEnvelope(applied.SnapshotEnvelope!);
        var drained = FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldReceipt(applied.State, new(capture, [], [], [], observation.DurableCheckpoint));
        Assert.True(drained.Accepted); CaptureEnvelope(drained.SnapshotEnvelope!);
        var completed = CompleteCorrelatedExchange(drained.State, pair.Receiver, CaptureEnvelope).Sender;
        var committed = CommitCorrelatedFallback(completed, CaptureEnvelope);
        var reactivated = ReactivateCorrelatedFallback(committed, CaptureEnvelope).State;
        return new(initial, completed, reactivated, callback, capture);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> CreateBootstrapCallbackNextCycleEvidence()
    {
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        var history = CreateBootstrapCallbackRetirementHistory(envelopes.Add);
        var retirement = FileTransferCoordinator.IssueFallbackBootstrapSourceCallbackRetirement(history.Reactivated, history.Callback, history.Capture);
        Assert.NotNull(retirement);
        var next = StartCorrelated(history.Reactivated, capture: envelopes.Add);
        envelopes.Add(next.SnapshotEnvelope!);
        Assert.True(FileTransferCoordinator.IsFallbackBootstrapSourceCallbackRetired(next.State, history.Callback, history.Capture, retirement));
        Assert.Empty(next.State.PersistentState.FileTransferRouteTransition.BootstrapSourceHold!.Capture!.OutboundCallbacks);
        var consumed = FileTransferCoordinator.CreateBootstrapSourceCallbackSnapshot(next.State, history.Callback, "Consumed",
            FileTransferBootstrapSourceCallbackDisposition.Stale, "none", history.Capture);
        envelopes.Add(consumed.Envelope);
        return envelopes;
    }
}
