using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(false, "offer", false)]
    [InlineData(true, "offer", false)]
    [InlineData(false, "offer", true)]
    [InlineData(true, "offer", true)]
    [InlineData(false, "ack", false)]
    [InlineData(true, "ack", false)]
    [InlineData(false, "ack", true)]
    [InlineData(true, "ack", true)]
    [InlineData(false, "slot", false)]
    [InlineData(true, "slot", false)]
    [InlineData(false, "slot", true)]
    [InlineData(true, "slot", true)]
    [Trait("FileTransferInfraSuite", "AutomaticFileHandoff")]
    public Task StartupBinding_QueuedWorkRetainsExactTransfer(bool helperPays, string blocker, bool successor)
        => WithPublicPreparationPairAsync(helperPays, async pair =>
        {
            var binding = await PrepareStartupBinding(pair);
            long slot = 0;
            object? owner = null;
            if (blocker == "slot")
            {
                object?[] args = [0L];
                Assert.True((bool)typeof(NknSignalingTransport).GetMethod("TryAcquireGenericAccelerationNegotiationSlot",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pair.Payer, args)!);
                slot = (long)args[0]!;
                owner = GetPrivateField(pair.Payer, "accelerationNegotiationSlotOwner");
            }
            else
                SetPrivateField(pair.Payer, blocker == "offer" ? "outboundAccelerationOfferNonce" :
                    "pendingAccelerationAnswerAckNonce", "held-startup-generation");
            if (blocker == "ack") SetPrivateField(pair.Payer, "pendingAccelerationAnswerAckPayerDecisionId", 1L);

            InvokePrivateMethod(pair.Payer, "ScheduleAccelerationNegotiationIfEligible",
                "runtime_unlock_automatic_transfer", null, false, true, 0L, null, binding);
            Assert.Same(binding, GetPrivateField(pair.Payer, "pendingRuntimeUnlockStartupBinding"));
            Assert.Equal(1, GetPrivateField(pair.Payer, "pendingRuntimeUnlockAccelerationNegotiation"));
            Assert.Equal(0, pair.PayerLane.OrdinaryStarts);

            if (successor) await RetireStartupBinding(pair, binding, "successor");
            if (blocker == "slot")
                InvokePrivateMethod(pair.Payer, "ReleaseAccelerationNegotiationSlot", slot, owner, "startup_binding_test");
            else if (blocker == "ack")
                InvokePrivateMethod(pair.Payer, "ClearPendingAccelerationAnswerAckLocked");
            else
            {
                SetPrivateField(pair.Payer, "outboundAccelerationOfferNonce", null);
                InvokePrivateMethod(pair.Payer, "SchedulePendingRuntimeUnlockAccelerationNegotiationAfterCurrentOperation",
                    "startup_binding_test");
            }
            Assert.NotNull(pair.Payer.StartupTransferActivationWakeForTests);
            await pair.Payer.StartupTransferActivationWakeForTests!.WaitAsync(pair.Token);
            await AssertStartupBindingDispatch(pair, !successor);
            Assert.Null(GetPrivateField(pair.Payer, "pendingRuntimeUnlockStartupBinding"));
        });

    [Theory]
    [InlineData(false, "current")]
    [InlineData(true, "current")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "successor")]
    [InlineData(true, "successor")]
    [InlineData(false, "key_reset")]
    [InlineData(true, "key_reset")]
    [InlineData(false, "stop")]
    [InlineData(true, "stop")]
    [Trait("FileTransferInfraSuite", "AutomaticFileHandoff")]
    public Task StartupBinding_RetryDelayCannotRebindToCurrentTransfer(bool helperPays, string change)
        => RunStartupBindingRetryAsync(helperPays, change, preflight: false);

    [Theory]
    [InlineData(false, "current")]
    [InlineData(true, "current")]
    [InlineData(false, "successor")]
    [InlineData(true, "successor")]
    [Trait("FileTransferInfraSuite", "AutomaticFileHandoff")]
    public Task StartupBinding_PreflightRetryRetainsExactTransfer(bool helperPays, string change)
        => RunStartupBindingRetryAsync(helperPays, change, preflight: true);

    private static Task RunStartupBindingRetryAsync(bool helperPays, string change, bool preflight)
        => WithPublicPreparationPairAsync(helperPays, async pair =>
        {
            var binding = await PrepareStartupBinding(pair);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Payer.StartupTransferActivationBeforeRetryForTests = async () =>
            {
                entered.TrySetResult();
                await release.Task;
            };
            try
            {
                if (preflight)
                {
                    var mode = Enum.Parse(typeof(NknSignalingTransport).GetNestedType("RuntimeUnlockOfferAuthorityMode",
                        BindingFlags.NonPublic)!, "InitialActivation");
                    InvokePrivateMethod(pair.Payer, "RejectAccelerationOfferPreflight", "runtime_unlock_automatic_transfer",
                        "listener_unavailable", true, NknAccelerationLaneKind.File, null, mode, binding);
                }
                else
                    Assert.True((bool)InvokePrivateMethod(pair.Payer,
                        "ScheduleInitialRuntimeUnlockAccelerationNegotiationRetry", "listener_sidecar_unavailable", binding)!);
                await entered.Task.WaitAsync(pair.Token);
                if (change != "current") await RetireStartupBinding(pair, binding, change);
                release.TrySetResult();
                await pair.Payer.StartupTransferActivationRetryForTests!.WaitAsync(pair.Token);
                await AssertStartupBindingDispatch(pair, change == "current");
            }
            finally { release.TrySetResult(); }
        });

    private static async Task<object> PrepareStartupBinding(PreparationHandshakePair pair)
    {
        pair.PayerLane.Configuration = NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen;
        Assert.Equal("regular_nkn_v4_fast", (await StartPreparedAdmission(pair.PayerService, pair.Token))!.RouteToken);
        return await CaptureStartupBinding(pair, "prepared-admission");
    }

    private static async Task<object> CaptureStartupBinding(PreparationHandshakePair pair, string transferId)
    {
        var lifecycle = ((IFileTransferSessionPreparationTransport)pair.Payer).SessionPreparationLifecycle;
        Assert.Equal(FileTransferAccelerationRequestPurpose.TransferActivation,
            lifecycle.Attachment!.Authority.SelectRequest(lifecycle.Attachment, lifecycle.Current!).Purpose);
        object?[] args = [lifecycle.Current!.SessionId, null, null];
        Assert.True((bool)typeof(NknSignalingTransport).GetMethod("TryCaptureCurrentRuntimeUnlockFileTransferOwnerAuthority",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(pair.Payer, args)!);
        var publication = Assert.IsType<FileTransferRoutePublicationAuthority>(args[1]);
        Assert.Equal(transferId, publication.TransferId);
        await pair.Payer.OpenFileTransferDataSessionAsync(publication.SessionId, transferId, pair.Token);
        var type = typeof(NknSignalingTransport).GetNestedType("StartupTransferActivationBinding", BindingFlags.NonPublic)!;
        var binding = Activator.CreateInstance(type, lifecycle.Attachment, lifecycle.Current, publication)!;
        Assert.True((bool)InvokePrivateMethod(pair.Payer, "IsStartupTransferActivationBindingCurrent", binding)!);
        return binding;
    }

    private static async Task RetireStartupBinding(PreparationHandshakePair pair, object binding, string change)
    {
        if (change == "key_reset") InvokePrivateMethod(pair.Payer, "ResetControlSecureState");
        else if (change == "stop")
        {
            // Model the existing mixed-lane negotiation whose retry is pending.
            SetPrivateField(pair.Payer, "accelerationSessionId",
                ((IFileTransferSessionPreparationTransport)pair.Payer).SessionPreparationLifecycle.Current!.SessionId);
            SetPrivateField(pair.Payer, "accelerationNegotiatedLanes", NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen);
            await pair.Payer.StopAccelerationAsync("user_stopped_tuna", pair.Token);
        }
        else
        {
            await pair.PayerService.CancelTransferAsync("prepared-admission", "startup_binding_test", pair.Token);
            // This fixture, not the service, opened the pending transfer's data session.
            var sessionId = ((IFileTransferSessionPreparationTransport)pair.Payer).SessionPreparationLifecycle.Current!.SessionId;
            (await pair.Payer.OpenFileTransferDataSessionAsync(sessionId, "prepared-admission", pair.Token)).Dispose();
            if (change == "successor")
            {
                Assert.Equal("regular_nkn_v4_fast", (await pair.PayerService.TryStartSendAsync(
                    new("successor.bin", 4, "startup-successor"),
                    _ => Task.FromResult<Stream>(new MemoryStream(new byte[4])), pair.Token))!.RouteToken);
                Assert.NotSame(binding, await CaptureStartupBinding(pair, "startup-successor"));
            }
        }
        Assert.False((bool)InvokePrivateMethod(pair.Payer, "IsStartupTransferActivationBindingCurrent", binding)!);
    }

    private static async Task AssertStartupBindingDispatch(PreparationHandshakePair pair, bool expected)
    {
        // The preparation fake deliberately throws at ordinary listener startup:
        // this proves exact-scope dispatch without admitting any synthetic route.
        if (expected) await WaitUntilAsync(() => pair.PayerLane.OrdinaryStarts == 1, TimeSpan.FromSeconds(3));
        await WaitPreparationRequestIdle(pair);
        Assert.Equal(expected ? 1 : 0, pair.PayerLane.OrdinaryStarts);
        Assert.Null(GetPrivateField(pair.Payer, "outboundRuntimeUnlockOfferAuthorityBoundary"));
        Assert.False(pair.Payer.IsFileTunaActiveForRouteSelection);
        Assert.False(pair.Dialer.IsFileTunaActiveForRouteSelection);
        Assert.DoesNotContain(MsgType.TransportAccelerationOffer, pair.Sent);
        Assert.Equal(0, GetPrivateField(pair.Payer, "pendingRuntimeUnlockAccelerationNegotiation"));
    }
}
