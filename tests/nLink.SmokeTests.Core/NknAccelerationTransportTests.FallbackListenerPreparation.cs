using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Fact]
    [Trait("FileTransferInfraSuite", "FallbackListenerPreparation")]
    public Task FallbackListenerPreparation_PublicUnlockStartsBeforeYieldWithoutNegotiation()
        => WithPreparationExecutionPairAsync(true, async pair =>
        {
            var state = await InstallFallbackPreparationSource(pair);
            var publication = state.CurrentRoutePublicationToken!.Value;
            await pair.Host.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            await pair.Lane.Entered.Task.WaitAsync(pair.Token);
            Assert.Equal(NknTunaListenerLifetimePolicy.CoordinatorScopedInitialActivation, pair.Lane.Request!.ListenerLifetimePolicy);
            await pair.Host.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            Assert.Equal(1, pair.Lane.Starts);
            var waiting = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(pair.Host,
                "AwaitFallbackListenerPreparationAsync", FileTransferCoordinator.CreateRoutePublicationAuthority(publication)!));
            Assert.False(waiting.IsCompleted);
            AssertFallbackPreparationNoAuthority(pair, publication);
            pair.Lane.Release.TrySetResult();
            await waiting.WaitAsync(pair.Token);
            AssertFallbackPreparationNoAuthority(pair, publication);
        });

    [Theory]
    [InlineData("switch_off")]
    [InlineData("reset")]
    [InlineData("key_reset")]
    [InlineData("terminal")]
    [InlineData("publication")]
    [InlineData("listener_loss")]
    [InlineData("cancel")]
    [Trait("FileTransferInfraSuite", "FallbackListenerPreparation")]
    public Task FallbackListenerPreparation_LateStartupCannotSurviveRetiredScope(string change)
        => WithPreparationExecutionPairAsync(true, async pair =>
        {
            var state = await InstallFallbackPreparationSource(pair);
            using var request = new CancellationTokenSource();
            await pair.Host.RequestAccelerationNegotiationAsync("runtime_unlock", request.Token);
            await pair.Lane.Entered.Task.WaitAsync(pair.Token);
            var work = GetPrivateField(pair.Host, "fallbackListenerPreparation")!;
            var completion = (TaskCompletionSource<NknTunaLaneLease?>)work.GetType().GetField("Completion")!.GetValue(work)!;
            switch (change)
            {
                case "switch_off": await pair.Host.StopAccelerationAsync("header_switch_off", pair.Token); break;
                case "reset": pair.Service.ResetSessionState(); break;
                case "key_reset": InvokePrivateMethod(pair.Host, "ResetControlSecureState"); break;
                case "terminal": await pair.Service.CancelTransferAsync("fallback-preparation", "test", pair.Token); break;
                case "publication":
                    var successor = state.CurrentRoutePublicationToken!.Value with
                    { PublicationGeneration = state.CurrentRoutePublicationToken.Value.PublicationGeneration + 1 };
                    InvokePrivateMethod(pair.Host, "TrackAuthorizedFileTransferRouteHint", new FileTransferRouteHintNotification(
                        successor.SessionId, successor.TransferId, successor.RouteToken, 6, "test",
                        PublicationAuthority: FileTransferCoordinator.CreateRoutePublicationAuthority(successor)));
                    break;
                case "cancel": request.Cancel(); break;
                case "listener_loss":
                    var identity = (string)work.GetType().GetField("Identity")!.GetValue(work)!;
                    InvokePrivateMethod(pair.Host, "OnAccelerationStateChanged", pair.Lane,
                        new AccelerationStateChangedEventArgs(false, "remote_closed", sessionPreparationExecutionIdentity: identity));
                    break;
            }
            pair.Lane.Release.TrySetResult(); // Simulate a callback ignoring cancellation.
            Assert.Null(await completion.Task.WaitAsync(pair.Token));
            await pair.Lane.Stopped.Task.WaitAsync(pair.Token);
            Assert.Equal(1, pair.Lane.ExactStops);
            Assert.Null(GetPrivateField(pair.Host, "fallbackListenerPreparation"));
            Assert.Null(GetPrivateField(pair.Host, "outboundAccelerationOfferNonce"));
            Assert.Null(GetPrivateField(pair.Host, "runtimeUnlockRecoveryRetryState"));
            Assert.False(pair.Host.IsAccelerationAvailableForTests);
        });

    [Fact]
    [Trait("FileTransferInfraSuite", "FallbackListenerPreparation")]
    public Task FallbackListenerPreparation_NoCurrentPublicationCannotStartPhysicalWork()
        => WithPreparationExecutionPairAsync(true, async pair =>
        {
            await InstallFallbackPreparationSource(pair);
            var context = GetPrivateField(pair.Service, "outboundTransfer")!;
            SetPrivateProperty(context, "CurrentRoutePublicationToken", null);
            await pair.Host.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            Assert.Equal(0, pair.Lane.Starts);
            Assert.Null(GetPrivateField(pair.Host, "fallbackListenerPreparation"));
        });

    private static async Task<FileTransferCoordinatorState> InstallFallbackPreparationSource(PreparationExecutionPair pair)
    {
        await pair.Service.TryStartSendAsync(new("fallback.bin", 4, "fallback-preparation"),
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[4])), pair.Token);
        var context = GetPrivateField(pair.Service, "outboundTransfer")!;
        var session = ((IFileTransferSessionPreparationTransport)pair.Host).SessionPreparationLifecycle.Current!.SessionId;
        var data = await pair.Host.OpenFileTransferDataSessionAsync(session, "fallback-preparation", pair.Token);
        SetPrivateProperty(context, "DataSession", data);
        SetPrivateProperty(context, "DataSessionGeneration", 1L);
        var state = FileTransferCoordinatorTests.CreateFallbackPreparationStateForTests(session, "fallback-preparation");
        var decision = new FileTransferCoordinatorDecision(state, false, false, null, null, null, null, null, false, "test");
        typeof(SessionFileTransferService).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name == "ApplyCoordinatorDecisionToContextLocked" && method.GetParameters().Length == 4 &&
                method.GetParameters()[0].ParameterType == context.GetType()).Invoke(null, [context, decision, false, false]);
        InvokePrivateMethod(pair.Host, "TrackAuthorizedFileTransferRouteHint", new FileTransferRouteHintNotification(
            session, "fallback-preparation", "post_tuna_fallback_v6", 6, "test",
            PublicationAuthority: FileTransferCoordinator.CreateRoutePublicationAuthority(state.CurrentRoutePublicationToken)));
        return state;
    }

    private static void AssertFallbackPreparationNoAuthority(PreparationExecutionPair pair, RoutePublicationToken publication)
    {
        Assert.False(pair.Host.IsAccelerationAvailableForTests);
        Assert.Null(GetPrivateField(pair.Host, "outboundAccelerationOfferNonce"));
        Assert.Null(GetPrivateField(pair.Host, "pendingAccelerationAnswerAckNonce"));
        Assert.Null(GetPrivateField(pair.Host, "runtimeUnlockRecoveryRetryState"));
        var context = GetPrivateField(pair.Service, "outboundTransfer")!;
        Assert.Equal(publication, ContextProperty(context, "CurrentRoutePublicationToken"));
        Assert.Equal("post_tuna_fallback_v6", pair.Service.Snapshot.Outbound!.RouteToken);
    }
}
