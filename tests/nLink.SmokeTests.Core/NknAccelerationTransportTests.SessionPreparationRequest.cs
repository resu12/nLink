using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationRequest")]
    public Task PreparationRequest_AutomaticFileOnlySchedulingUsesCoordinatorBeforePublicUnlock(bool helperPays)
        => WithPublicPreparationPairAsync(helperPays, async pair =>
        {
            InvokePrivateMethod(pair.Payer, "ScheduleAccelerationNegotiationIfEligible",
                "session_security_state_ready", null, false, false, 0L);
            await pair.WaitReady();
            await WaitPreparationRequestIdle(pair);
            var payer = pair.State(pair.PayerService);
            var dialer = pair.State(pair.DialerService);
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            await WaitPreparationRequestIdle(pair);
            Assert.Same(payer, pair.State(pair.PayerService));
            Assert.Same(dialer, pair.State(pair.DialerService));
            Assert.Equal(1, pair.PayerLane.Starts);
            Assert.Equal(1, pair.DialerLane.Starts);
            Assert.Equal(3, pair.Sent.Count);
            pair.AssertNoFileEffects();
            Assert.Equal("file_tuna_v4", (await StartPreparedAdmission(pair.PayerService, pair.Token))!.RouteToken);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.RouteToken == "file_tuna_v4", TimeSpan.FromSeconds(3));
        });

    [Fact]
    [Trait("FileTransferInfraSuite", "SessionPreparationRequest")]
    public Task PreparationRequest_AutomaticSchedulingHonorsExistingScheduledWork()
        => WithPublicPreparationPairAsync(false, pair =>
        {
            var before = pair.State(pair.PayerService);
            SetPrivateField(pair.Payer, "accelerationNegotiationScheduled", 1);
            InvokePrivateMethod(pair.Payer, "ScheduleAccelerationNegotiationIfEligible",
                "session_security_state_ready", null, false, false, 0L);
            Assert.Same(before, pair.State(pair.PayerService));
            Assert.Equal(0, pair.PayerLane.Starts);
            Assert.Equal(0, pair.PayerLane.OrdinaryStarts);
            SetPrivateField(pair.Payer, "accelerationNegotiationScheduled", 0);
            return Task.CompletedTask;
        });

    [Fact]
    [Trait("FileTransferInfraSuite", "SessionPreparationRequest")]
    public Task PreparationRequest_AutomaticMixedLaneSchedulingKeepsGenericPath()
        => WithPublicPreparationPairAsync(false, async pair =>
        {
            pair.PayerLane.Configuration = NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen;
            InvokePrivateMethod(pair.Payer, "ScheduleAccelerationNegotiationIfEligible",
                "session_security_state_ready", null, false, false, 0L);
            await WaitUntilAsync(() => pair.PayerLane.OrdinaryStarts > 0, TimeSpan.FromSeconds(3));
            await WaitPreparationRequestIdle(pair);
            Assert.Equal(0, pair.PayerLane.Starts);
            Assert.Null(pair.State(pair.PayerService).Permit);
            Assert.Null(pair.State(pair.PayerService).Readiness);
            pair.AssertNoFileEffects();
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationRequest")]
    public Task PreparationRequest_ColdListenerStartupDoesNotSpendHandshakeLifetime(bool helperPays)
        => WithPublicPreparationPairAsync(helperPays, async pair =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Payer.SessionPreparationBeforeLaneStartForTests = () => { entered.TrySetResult(); return release.Task; };
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            try
            {
                await entered.Task.WaitAsync(pair.Token);
                var state = pair.State(pair.PayerService);
                Assert.Equal("Starting", state.Phase.ToString());
                Assert.InRange((state.Permit!.DeadlineUtc - state.Permit.IssuedUtc).TotalSeconds, 119, 120);
                Assert.Null(state.Readiness);
                Assert.Empty(pair.Sent);
                var lifecycle = ((IFileTransferSessionPreparationTransport)pair.Payer).SessionPreparationLifecycle;
                Assert.False(pair.Payer.IsSessionPreparationReplyCurrent(new(MsgType.SessionPreparationAnswer,
                    lifecycle.Attachment!, lifecycle.Current!, state.Permit.Nonce, state.Permit.PayerDecisionId,
                    state.Permit.DeadlineUtc, true, null, null), state.Permit));
                pair.AssertNoFileEffects();
            }
            finally { release.TrySetResult(); }
            await pair.WaitReady();
            await WaitPreparationRequestIdle(pair);
            var ready = pair.State(pair.PayerService);
            var current = ((IFileTransferSessionPreparationTransport)pair.Payer).SessionPreparationLifecycle;
            var reply = new SessionPreparationInbound(MsgType.SessionPreparationAnswer, current.Attachment!, current.Current!,
                ready.Permit!.Nonce, ready.Permit.PayerDecisionId, ready.Handshake!.DeadlineUtc, true, null, null);
            Assert.True(pair.Payer.IsSessionPreparationReplyCurrent(reply, ready.Permit));
            Assert.False(pair.Payer.IsSessionPreparationReplyCurrent(reply with { DeadlineUtc = ready.Permit.DeadlineUtc }, ready.Permit));
            Assert.False(pair.Payer.IsSessionPreparationReplyCurrent(reply, ready.Permit with { }));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationRequest")]
    public Task PreparationRequest_PublicUnlockCompletesReadinessThenActualAdmission(bool helperPays)
        => WithPublicPreparationPairAsync(helperPays, async pair =>
        {
            Assert.Null(pair.State(pair.PayerService).Permit);
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            await pair.WaitReady();
            await WaitPreparationRequestIdle(pair);
            pair.AssertNoFileEffects();
            var before = pair.State(pair.PayerService);
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            await WaitPreparationRequestIdle(pair);
            Assert.Same(before, pair.State(pair.PayerService));
            Assert.Equal(1, pair.PayerLane.Starts);
            Assert.Equal(3, pair.Sent.Count);
            Assert.Equal("file_tuna_v4", (await StartPreparedAdmission(pair.PayerService, pair.Token))!.RouteToken);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.RouteToken == "file_tuna_v4", TimeSpan.FromSeconds(3));
            Assert.True(AdmissionLease(pair.PayerService).IsAdopted);
            Assert.True(AdmissionLease(pair.DialerService).IsAdopted);
        });

    [Theory]
    [InlineData("missing_attachment")]
    [InlineData("late_attachment")]
    [InlineData("reset")]
    [InlineData("key_reset")]
    [InlineData("canceled")]
    [InlineData("pending_offer")]
    [InlineData("pending_ack")]
    [InlineData("busy_slot")]
    [InlineData("dialer_only")]
    [InlineData("unowned_data_session")]
    [Trait("FileTransferInfraSuite", "SessionPreparationRequest")]
    public Task PreparationRequest_UnavailableCannotFallThroughToSourceLessActivation(string change)
        => WithPublicPreparationPairAsync(false, async pair =>
        {
            if (change is "missing_attachment" or "late_attachment" or "unowned_data_session") pair.PayerService.DetachTransport();
            if (change == "unowned_data_session")
                await pair.Payer.OpenFileTransferDataSessionAsync(
                    ((IFileTransferSessionPreparationTransport)pair.Payer).SessionPreparationLifecycle.Current!.SessionId, "unowned", pair.Token);
            if (change == "late_attachment") pair.PayerService.AttachTransport(pair.Payer);
            if (change == "reset") pair.PayerService.ResetSessionState();
            if (change == "key_reset") InvokePrivateMethod(pair.Payer, "ResetControlSecureState");
            if (change == "pending_offer") SetPrivateField(pair.Payer, "outboundAccelerationOfferNonce", "ordinary-pending");
            if (change == "pending_ack") SetPrivateField(pair.Payer, "pendingAccelerationAnswerAckNonce", "ordinary-pending");
            if (change == "busy_slot") SetPrivateField(pair.Payer, "accelerationNegotiationScheduled", 1);
            if (change == "dialer_only") pair.PayerLane.CanListen = false;
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock",
                change == "canceled" ? new CancellationToken(true) : pair.Token);
            Assert.Equal(0, pair.PayerLane.Starts);
            Assert.Null(pair.State(pair.PayerService).Permit);
            Assert.Empty(pair.Sent);
            Assert.Null(GetPrivateField(pair.Payer, "runtimeUnlockRecoveryRetryState"));
            Assert.Equal(0, GetPrivateField(pair.Payer, "pendingRuntimeUnlockAccelerationNegotiation"));
        });

    [Theory]
    [InlineData("reset")]
    [InlineData("key_reset")]
    [InlineData("admission")]
    [InlineData("cancel")]
    [Trait("FileTransferInfraSuite", "SessionPreparationRequest")]
    public Task PreparationRequest_QueuedStartupRechecksCoreLifetime(string change)
        => WithPublicPreparationPairAsync(false, async pair =>
        {
            using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Payer.SessionPreparationBeforeLaneStartForTests = () => { entered.TrySetResult(); return release.Task; };
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", cancellation.Token);
            try
            {
                await entered.Task.WaitAsync(pair.Token);
                if (change == "reset") pair.PayerService.ResetSessionState();
                if (change == "key_reset") InvokePrivateMethod(pair.Payer, "ResetControlSecureState");
                if (change == "admission") Assert.Equal("regular_nkn_v4_fast", (await StartPreparedAdmission(pair.PayerService, pair.Token))!.RouteToken);
                if (change == "cancel") cancellation.Cancel();
            }
            finally { release.TrySetResult(); }
            await WaitPreparationRequestIdle(pair);
            Assert.Equal(0, pair.PayerLane.Starts);
            Assert.Empty(pair.Sent);
            Assert.Null(GetPrivateField(pair.Payer, "runtimeUnlockRecoveryRetryState"));
        });

    [Fact]
    [Trait("FileTransferInfraSuite", "SessionPreparationRequest")]
    public Task PreparationRequest_HeldAnswerCoalescesAndBlocksOrdinarySlot()
        => WithPublicPreparationPairAsync(false, async pair =>
        {
            var held = pair.Hold(MsgType.SessionPreparationAnswer);
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            await held.Entered.Task.WaitAsync(pair.Token);
            var permit = pair.State(pair.PayerService).Permit;
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            await pair.Payer.RequestAccelerationNegotiationAsync("session_security_state_ready", pair.Token);
            Assert.Same(permit, pair.State(pair.PayerService).Permit);
            Assert.Equal(1, pair.PayerLane.Starts);
            Assert.Null(GetPrivateField(pair.Payer, "outboundAccelerationOfferNonce"));
            Assert.Null(pair.State(pair.PayerService).Readiness);
            held.Release.TrySetResult();
            await pair.WaitReady();
            await WaitPreparationRequestIdle(pair);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationRequest")]
    public Task PreparationRequest_CoreSelectsActivePublicationButCannotReopenTerminalFence(bool lateAttachment)
        => WithPublicPreparationPairAsync(false, async pair =>
        {
            if (lateAttachment)
            {
                pair.PayerService.DetachTransport();
                pair.PayerService.AttachTransport(pair.Payer);
            }
            var lifecycle = ((IFileTransferSessionPreparationTransport)pair.Payer).SessionPreparationLifecycle;
            FileTransferAccelerationRequestSelection Select() => lifecycle.Attachment!.Authority.SelectRequest(lifecycle.Attachment, lifecycle.Current!);
            Assert.Equal(lateAttachment ? FileTransferAccelerationRequestPurpose.Unavailable : FileTransferAccelerationRequestPurpose.SessionPreparation, Select().Purpose);
            await StartPreparedAdmission(pair.PayerService, pair.Token);
            Assert.Equal(FileTransferAccelerationRequestPurpose.TransferActivation, Select().Purpose);
            SetPrivateField(pair.Payer, "outboundAccelerationOfferNonce", "ordinary-pending");
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            Assert.Equal(1, GetPrivateField(pair.Payer, "pendingRuntimeUnlockAccelerationNegotiation"));
            InvokePrivateMethod(pair.Payer, "ClearPendingRuntimeUnlockAccelerationNegotiation");
            SetPrivateField(pair.Payer, "outboundAccelerationOfferNonce", null);
            var context = GetPrivateField(pair.PayerService, "outboundTransfer")!;
            var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(pair.PayerService, context);
            Assert.Equal(FileTransferAccelerationRequestPurpose.Unavailable,
                FileTransferCoordinator.SelectSessionPreparationRequest(pair.State(pair.PayerService), lifecycle.Current!.SessionId,
                    state with { CurrentRoutePublicationToken = null }, DateTimeOffset.UtcNow).Purpose);
            await pair.PayerService.CancelTransferAsync("prepared-admission", "test", pair.Token);
            Assert.Equal(FileTransferAccelerationRequestPurpose.Unavailable, Select().Purpose);
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            Assert.Equal(0, pair.PayerLane.Starts);
            Assert.Null(pair.State(pair.PayerService).Permit);
        });

    [Theory]
    [InlineData("new_key")]
    [InlineData("admission")]
    [InlineData("reset")]
    [Trait("FileTransferInfraSuite", "SessionPreparationRequest")]
    public Task PreparationRequest_BeforePermitWaitCannotSwitchSessionOrBorrowNewAdmission(string change)
        => WithPublicPreparationPairAsync(false, async pair =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Payer.SessionPreparationBeforeRequestPermitForTests = async () =>
            {
                entered.TrySetResult();
                await release.Task;
                resumed.TrySetResult();
            };
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            try
            {
                await entered.Task.WaitAsync(pair.Token);
                if (change == "new_key")
                {
                    var lifecycle = ((IFileTransferSessionPreparationTransport)pair.Payer).SessionPreparationLifecycle;
                    var old = lifecycle.Current!;
                    lifecycle.InstallKey(old.SessionId);
                    lifecycle.ObserveSecurity(old.Security!);
                    Assert.NotSame(old, lifecycle.Current);
                }
                else if (change == "admission") await StartPreparedAdmission(pair.PayerService, pair.Token);
                else pair.PayerService.ResetSessionState();
            }
            finally { release.TrySetResult(); }
            await resumed.Task.WaitAsync(pair.Token);
            await WaitPreparationRequestIdle(pair);
            Assert.Equal(0, pair.PayerLane.Starts);
            Assert.Null(pair.State(pair.PayerService).Permit);
            Assert.Empty(pair.Sent);
        });

    [Fact]
    [Trait("FileTransferInfraSuite", "SessionPreparationRequest")]
    public Task PreparationRequest_HelperWaitCoalescesWithHelpeePreparation()
        => WithPublicPreparationPairAsync(false, async pair =>
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.FromSeconds(1);
            var helperResumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Dialer.SessionPreparationBeforeRequestPermitForTests = () => { helperResumed.TrySetResult(); return Task.CompletedTask; };
            await pair.Dialer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            await WaitUntilAsync(() => (int)GetPrivateField(pair.Dialer, "helperPaidOfferPriorityDelayConsumed")! != 0, TimeSpan.FromSeconds(2));
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            await pair.WaitReady();
            await helperResumed.Task.WaitAsync(pair.Token);
            await WaitPreparationRequestIdle(pair);
            Assert.True(pair.State(pair.PayerService).Permit!.LocalIsPayer);
            Assert.False(pair.State(pair.DialerService).Permit!.LocalIsPayer);
            Assert.Equal(1, pair.PayerLane.Starts);
            Assert.Equal(1, pair.DialerLane.Starts);
            Assert.Equal(3, pair.Sent.Count);
            pair.AssertNoFileEffects();
        });

    private static Task WaitPreparationRequestIdle(PreparationHandshakePair pair)
        => WaitUntilAsync(() => (long)GetPrivateField(pair.Payer, "accelerationNegotiationSlotGeneration")! == 0 &&
            (long)GetPrivateField(pair.Dialer, "accelerationNegotiationSlotGeneration")! == 0, TimeSpan.FromSeconds(3));

    private static async Task WithPublicPreparationPairAsync(bool helperPays, Func<PreparationHandshakePair, Task> test)
    {
        FakeNknClient.ResetNetwork();
        var prior = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var pair = new PreparationHandshakePair(helperPays);
            await pair.Approve(begin: false);
            await test(pair);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = prior;
            FakeNknClient.ResetNetwork();
        }
    }
}
