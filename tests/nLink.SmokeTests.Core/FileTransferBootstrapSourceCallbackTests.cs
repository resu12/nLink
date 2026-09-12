using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Fact]
    public void BootstrapSourceCallback_OnlyExactCoordinatorCommittedSuccessorCanRestartSource()
    {
        var pair = CreateCorrelatedBootstrapPair();
        var source = BootstrapStopScopeForTests(pair.Sender);
        Assert.False(FileTransferCoordinator.HasCommittedBootstrapSourceCallbackSuccessor(pair.Sender, source));
        var exchange = CompleteCorrelatedExchange(pair.Sender, pair.Receiver);
        Assert.False(FileTransferCoordinator.HasCommittedBootstrapSourceCallbackSuccessor(exchange.Sender, source));
        var committed = CommitCorrelatedFallback(exchange.Sender);
        Assert.True(FileTransferCoordinator.HasCommittedBootstrapSourceCallbackSuccessor(committed, source));
        Assert.False(FileTransferCoordinator.HasCommittedBootstrapSourceCallbackSuccessor(
            MutateBootstrapCallbackStateForTest(committed, "publication"), source));
        var reactivated = ReactivateCorrelatedFallback(committed).State;
        Assert.True(FileTransferCoordinator.HasCommittedBootstrapSourceCallbackSuccessor(reactivated, source));
    }

    [Theory]
    [InlineData("current")] [InlineData("owner")] [InlineData("publication")] [InlineData("terminal")]
    [InlineData("session")] [InlineData("provider")] [InlineData("pause")] [InlineData("agreement")]
    public void BootstrapSourceCallback_CorePinsSourceAndSeparatesCaptureFromPhysicalDrain(string mutation)
    {
        var state = CreateCorrelatedBootstrapPair().Sender;
        var callback = FileTransferCoordinator.IssueFallbackBootstrapSourceCallback(state, 71, 7, false, 0)!;
        Assert.NotNull(callback);
        Assert.Equal(FileTransferBootstrapSourceCallbackDisposition.Current,
            FileTransferCoordinator.ResolveFallbackBootstrapSourceCallback(state, callback, 7, false, 0));
        var observation = StartCorrelated(state).Command!.Bootstrap.Observation;
        var prepared = FileTransferCoordinator.PrepareFallbackBootstrapSourceHold(state, observation, null);
        Assert.True(prepared.Accepted);
        var capture = new FileTransferBootstrapSourceHoldCapture(prepared.Command!, 99, [], [], []) { OutboundCallbacks = [callback] };
        Assert.False(FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldCapture(prepared.State,
            capture with { OutboundCallbacks = [callback, callback] }).Accepted);
        Assert.False(FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldCapture(prepared.State,
            capture with { OutboundCallbacks = [callback with { CoordinatorInstanceId = "other" }] }).Accepted);
        Assert.False(FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldCapture(prepared.State,
            capture with { OutboundCallbacks = [callback with { ProviderGeneration = 8 }] }).Accepted);
        var applied = FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldCapture(prepared.State, capture);
        Assert.True(applied.Accepted);
        Assert.Null(FileTransferCoordinator.IssueFallbackBootstrapSourceCallback(applied.State, 72, 7, true, 99));
        // A pending callback does not delay physical drain or manufacture physical settlement.
        var drained = FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldReceipt(applied.State,
            new(capture, [], [], [], observation.DurableCheckpoint));
        Assert.True(drained.Accepted);
        var changed = MutateBootstrapCallbackStateForTest(drained.State, mutation);
        Assert.Equal(mutation == "current" ? FileTransferBootstrapSourceCallbackDisposition.Held : FileTransferBootstrapSourceCallbackDisposition.Stale,
            FileTransferCoordinator.ResolveFallbackBootstrapSourceCallback(changed, callback, mutation == "provider" ? 8 : 7, true,
                mutation == "pause" ? 100 : 99));
        var invalidated = FileTransferCoordinator.InvalidateFallbackBootstrapSourceHold(drained.State, "test");
        Assert.Equal(FileTransferBootstrapSourceCallbackDisposition.Stale,
            FileTransferCoordinator.ResolveFallbackBootstrapSourceCallback(invalidated.State, callback, 7, true, 99));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BootstrapSourceCallback_RetainedCaptureProjectsOnlyItsExactCallback(bool wrongCapture)
    {
        var state = CreateCorrelatedBootstrapPair().Sender;
        var callback = FileTransferCoordinator.IssueFallbackBootstrapSourceCallback(state, 71, 7, false, 0)!;
        var prepared = FileTransferCoordinator.PrepareFallbackBootstrapSourceHold(state, StartCorrelated(state).Command!.Bootstrap.Observation, null);
        Assert.True(prepared.Accepted);
        var capture = new FileTransferBootstrapSourceHoldCapture(prepared.Command!, 99, [], [], []) { OutboundCallbacks = [callback] };
        var applied = FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldCapture(prepared.State, capture);
        Assert.True(applied.Accepted);
        var original = FileTransferCoordinator.CreateBootstrapSourceCallbackSnapshot(applied.State, callback, "Consumed",
            FileTransferBootstrapSourceCallbackDisposition.Held, "none").Envelope.Aggregate.Operations.Single().RouteTransition!.BootstrapCallbacks!;
        // Projection-only replacement fixture: absence of current hold must not erase retained diagnostic linkage.
        var replaced = applied.State with { PersistentState = applied.State.PersistentState with
        { FileTransferRouteTransition = applied.State.PersistentState.FileTransferRouteTransition with { BootstrapSourceHold = null } } };
        var retained = wrongCapture ? capture with { OutboundCallbacks = [] } : capture;
        var snapshot = FileTransferCoordinator.CreateBootstrapSourceCallbackSnapshot(replaced, callback, "Consumed",
            FileTransferBootstrapSourceCallbackDisposition.Stale, "none", retained).Envelope.Aggregate.Operations.Single().RouteTransition!.BootstrapCallbacks!;
        Assert.Equal(wrongCapture ? "(none)" : original.HoldCommandFingerprint, snapshot.HoldCommandFingerprint);
        Assert.Equal(wrongCapture ? "(none)" : original.CaptureFingerprint, snapshot.CaptureFingerprint);
        Assert.Equal("Stale", snapshot.Disposition);
        Assert.Equal("none", snapshot.AllowedEffect);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorState MutateBootstrapCallbackStateForTest(FileTransferCoordinatorState state, string mutation)
        => mutation switch
        {
            "owner" => state with { CurrentRuntimeOwner = state.CurrentRuntimeOwner!.Value with { RuntimeGeneration = state.CurrentRuntimeOwner.Value.RuntimeGeneration + 1 } },
            "publication" => state with { CurrentRoutePublicationToken = state.CurrentRoutePublicationToken!.Value with { PublicationGeneration = state.CurrentRoutePublicationToken.Value.PublicationGeneration + 1 } },
            "terminal" => state with { IsTerminal = true },
            "session" => state with { CurrentDataSessionGeneration = state.CurrentDataSessionGeneration + 1 },
            "agreement" => state with { PersistentState = state.PersistentState with { FileTransferRouteTransition = state.PersistentState.FileTransferRouteTransition with { BootstrapAgreement = null } } },
            _ => state,
        };
}
