using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    private async Task AssertCallbackRecoveryHandoffAsync(SessionFileTransferService service, object context,
        SourceFailureDataSession session, object callback, TaskCompletionSource release, Func<int> sourceOpens,
        bool recoveryBeforeCallback, int logStart)
    {
        var originalCapture = GetPrivatePropertyValue(callback, "Capture");
        if (!recoveryBeforeCallback) { await ConsumeCallbackAsync(2); }
        RestartCallbackFallbackForTest(service, context, GetPrivateProperty<FileTransferBootstrapSourceCallback>(callback, "Authority").Source);
        await CoreSmokeTestsBase.WaitUntilAsync(() => sourceOpens() == 3 && CallbackRecoveryInstalledForTest(service, context),
            TimeSpan.FromSeconds(3));
        if (recoveryBeforeCallback) { await ConsumeCallbackAsync(3); }
        Assert.Same(originalCapture, GetPrivatePropertyValue(callback, "Capture"));
        var beforeDuplicate = SourceFailureEffectsForTest(context);
        lock (GetServiceGate(service))
        {
            typeof(SessionFileTransferService).GetMethod("RetireBootstrapSourceCallbackLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, callback, "none"]);
        }
        Assert.Equal(beforeDuplicate, SourceFailureEffectsForTest(context));
        var lease = GetPrivateProperty<FileTransferBootstrapSourceCallback>(callback, "Authority").LeaseGeneration;
        await CoreSmokeTestsBase.WaitUntilAsync(() => ReadCallbackPayloadsForTest(logStart).Any(p =>
            p.GetProperty("stage").GetString() == "Consumed" && p.GetProperty("leaseGeneration").GetInt64() == lease), TimeSpan.FromSeconds(3));
        var payloads = ReadCallbackPayloadsForTest(logStart);
        var consumed = Assert.Single(payloads.Where(p => p.GetProperty("stage").GetString() == "Consumed" &&
            p.GetProperty("leaseGeneration").GetInt64() == lease));
        Assert.Equal("Stale", consumed.GetProperty("disposition").GetString());
        Assert.Equal("none", consumed.GetProperty("allowedEffect").GetString());
        var captures = payloads.Where(p => p.GetProperty("stage").GetString() == "Capture" &&
            p.GetProperty("capturedCallbacks").EnumerateArray().Any(id => id.GetString() == consumed.GetProperty("callbackFingerprint").GetString()));
        Assert.NotEmpty(captures);
        Assert.All(captures, p =>
        {
            Assert.Equal(p.GetProperty("captureFingerprint").GetString(), consumed.GetProperty("captureFingerprint").GetString());
            Assert.Equal(p.GetProperty("holdCommandFingerprint").GetString(), consumed.GetProperty("holdCommandFingerprint").GetString());
        });

        async Task ConsumeCallbackAsync(int expectedOpens)
        {
            var before = SourceFailureEffectsForTest(context);
            release.SetResult();
            await CoreSmokeTestsBase.WaitUntilAsync(() =>
                ((System.Collections.IDictionary)GetPrivatePropertyValue(context, "ActiveBootstrapSourceCallbacks")!).Count == 0, TimeSpan.FromSeconds(3));
            Assert.Equal(before, SourceFailureEffectsForTest(context));
            Assert.Equal(expectedOpens, sourceOpens());
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
            Assert.DoesNotContain(session.SentFrames, frame => frame is FileTransferErrorFrameV4);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RestartCallbackFallbackForTest(SessionFileTransferService service, object context, FileTransferBootstrapLocalStopScope source)
    {
        lock (GetServiceGate(service))
        {
            var before = CreateCoordinatorStateForTest(context);
            var leg = typeof(SessionFileTransferService).GetMethod("StartOutboundTransferLegLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, before.RouteSelection, "callback_retirement_recovery", FileTransferLegState.RecoveryActive,
                    true, true, before.CurrentLeg!.TransportEpochId + 1, before.CurrentLeg.BridgeRecoveryGeneration, null, false, null, null, null, false]);
            Assert.NotNull(leg);
            var after = CreateCoordinatorStateForTest(context);
            Assert.Equal(before.CurrentLiveRouteEpoch!.EpochId, after.CurrentLiveRouteEpoch!.EpochId);
            Assert.True(after.CurrentRuntimeOwner!.Value.RuntimeGeneration > before.CurrentRuntimeOwner!.Value.RuntimeGeneration);
            Assert.NotEqual(before.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
            Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(after, after.CurrentRoutePublicationToken!.Value, out _));
            // Exercise the active-failure wait marker as well as the historical capture and pending-callback predicates.
            // This is execution bookkeeping only; all recovery authority above came from Core.
            SetPrivateProperty(context, "BootstrapCallbackAwaitingSuccessor", source);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool CallbackRecoveryInstalledForTest(SessionFileTransferService service, object context)
    {
        lock (GetServiceGate(service))
        {
            var install = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump");
            return install is not null && GetPrivateProperty<RuntimeOwnerToken>(install, "RuntimeOwner") == CreateCoordinatorStateForTest(context).CurrentRuntimeOwner;
        }
    }
}
