using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("FileTransferInfraSuite", "PreparationActivationHandoff")]
    public async Task PreparationAdmission_PendingStartupContinuesThroughPublishedTransferAndDeliversPayload(bool helperPays, bool earlyRelease)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var identity = Guid.NewGuid().ToString("N");
        var payerClient = new FakeNknClient($"payer.preparation-handoff.{identity}.address");
        var dialerClient = new FakeNknClient($"dialer.preparation-handoff.{identity}.address");
        var options = NknTransportOptions.Load();
        var payerIdentity = new NknIdentity("preparation-handoff-payer", payerClient.Address);
        var dialerIdentity = new NknIdentity("preparation-handoff-dialer", dialerClient.Address);
        using var payerPressure = new RealNknClientAdapter(payerIdentity, options);
        using var dialerPressure = new RealNknClientAdapter(dialerIdentity, options);
        using var payerLane = new RetryableTunaAccelerationSession(true, 0, supportedLanes: NknAccelerationLaneKind.File);
        using var dialerLane = new RetryableTunaAccelerationSession(false, 0, supportedLanes: NknAccelerationLaneKind.File);
        // Keep joined byte transport offline, but use the production pressure
        // install/settlement backend so an actual repair can retire at completion.
        using var payer = new NknSignalingTransport(payerClient, options, payerIdentity,
            NknTunaAccelerationOptions.Disabled, payerLane, payerPressure);
        using var dialer = new NknSignalingTransport(dialerClient, options, dialerIdentity,
            NknTunaAccelerationOptions.Disabled, dialerLane, dialerPressure);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(payer);
        receiver.AttachTransport(dialer);
        await ApproveNknSessionAsync(helperPays ? dialer : payer, helperPays ? payer : dialer, timeout.Token,
            InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePayload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        payer.SessionPreparationBeforeLaneStartForTests = () => { entered.TrySetResult(); return releasePreparation.Task; };
        var payloadFrames = 0;
        payerLane.ForwardBytesForTests = (lane, bytes, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            if (bytes.Length > 1024) Interlocked.Increment(ref payloadFrames);
            dialerLane.ReceivePeerBytesForTests(lane, bytes);
            return Task.FromResult(true);
        };
        dialerLane.ForwardBytesForTests = (lane, bytes, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            payerLane.ReceivePeerBytesForTests(lane, bytes);
            return Task.FromResult(true);
        };
        var previousPriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            await payer.RequestAccelerationNegotiationAsync("runtime_unlock", timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            var preparation = Assert.IsType<FileTransferSessionPreparationExecution>(GetPrivateField(sender, "preparationExecution"));
            Assert.False(sender.ObserveSessionPreparationForDiagnostics(preparation.Permit.Scope.SessionId,
                FileTransferDirection.Outbound, DateTimeOffset.UtcNow).Ready);
            Assert.Null(GetPrivateField(payer, "initialTunaReadinessState"));
            var payload = Enumerable.Range(0, 256 * 1024).Select(i => (byte)(i % 251)).ToArray();
            var opens = 0;
            const string transferId = "pending-preparation-handoff";
            Assert.Equal("regular_nkn_v4_fast", (await sender.TryStartSendAsync(new("handoff.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(Interlocked.Increment(ref opens) == 1
                    ? new MemoryStream(payload, writable: false)
                    : new InitialActivationPayloadReadGate(payload, releasePayload.Task)), timeout.Token))!.RouteToken);
            Assert.True(preparation.Cancellation.IsCancellationRequested);
            if (earlyRelease)
            {
                releasePreparation.TrySetResult();
                var handoff = GetPrivateField(payer, "sessionPreparationActivationHandoff")!;
                await Assert.IsAssignableFrom<Task>(handoff.GetType().GetField("Activation")!.GetValue(handoff)).WaitAsync(timeout.Token);
            }
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            using var destination = new MemoryStream();
            Assert.NotNull(await receiver.AcceptIncomingTransferAsync(transferId,
                (_, _) => Task.FromResult<Stream>(destination), timeout.Token));
            await WaitUntilAsync(() => Volatile.Read(ref opens) >= 2, TimeSpan.FromSeconds(3));
            releasePreparation.TrySetResult();
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.RouteToken == "file_tuna_v4" &&
                receiver.Snapshot.Inbound?.RouteToken == "file_tuna_v4", TimeSpan.FromSeconds(8));
            foreach (var (service, name) in new[] { (sender, "outboundTransfer"), (receiver, "inboundTransfer") })
            {
                var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, GetPrivateField(service, name)!);
                var publication = Assert.IsType<RoutePublicationToken>(state.CurrentRoutePublicationToken);
                Assert.Equal((FileTransferRoute.FileTunaV4, 4), (publication.Route, publication.ProtocolVersion));
                Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(state, publication, out _));
            }
            releasePayload.TrySetResult();
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, TimeSpan.FromSeconds(8));
            Assert.True(Volatile.Read(ref payloadFrames) > 0);
            Assert.Equal(1, payerLane.EnsureListenerCalls);
            Assert.Equal(1, dialerLane.StartDialerCalls);
            Assert.Equal(payload, destination.ToArray());
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(payload), System.Security.Cryptography.SHA256.HashData(destination.ToArray()));
        }
        finally
        {
            releasePreparation.TrySetResult();
            releasePayload.TrySetResult();
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousPriorityDelay;
        }
    }

    [Fact]
    [Trait("FileTransferInfraSuite", "PreparationActivationHandoff")]
    public Task PreparationAdmission_ActivationLifetimeRequiresExactCoreAdmissionAndPublication()
        => WithPublicPreparationPairAsync(false, async pair =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Payer.SessionPreparationBeforeLaneStartForTests = () => { entered.TrySetResult(); return release.Task; };
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            try
            {
                await entered.Task.WaitAsync(pair.Token);
                Assert.NotNull(await StartPreparedAdmission(pair.PayerService, pair.Token));
                var attachment = Assert.IsType<FileTransferSessionPreparationAttachment>(
                    GetPrivateField(pair.PayerService, "preparationAttachment"));
                var epoch = attachment.Lifecycle.Current!;
                var admission = pair.State(pair.PayerService).Admission!;
                var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(pair.PayerService,
                    GetPrivateField(pair.PayerService, "outboundTransfer")!);
                var publication = FileTransferCoordinator.CreateRoutePublicationAuthority(state.CurrentRoutePublicationToken)!;
                var authority = (IFileTransferSessionPreparationAuthority)pair.PayerService;
                foreach (var wrong in new[]
                {
                    publication with { TransferId = "successor" },
                    publication with { Direction = FileTransferDirection.Inbound },
                    publication with { RuntimeGeneration = publication.RuntimeGeneration + 1 },
                    publication with { RoutePublicationGeneration = publication.RoutePublicationGeneration + 1 },
                    publication with { RouteToken = "file_tuna_v4" },
                    publication with { ProtocolVersion = 6 },
                })
                    Assert.Null(authority.BindAdmissionActivationLifetime(attachment, epoch, admission, wrong));
                Assert.Null(authority.BindAdmissionActivationLifetime(attachment, epoch, admission with { }, publication));
                var lifetime = authority.BindAdmissionActivationLifetime(attachment, epoch, admission, publication);
                Assert.True(lifetime.HasValue);
                Assert.False(lifetime.Value.IsCancellationRequested);
                await pair.PayerService.CancelTransferAsync(admission.TransferId, "test_complete", pair.Token);
                Assert.True(lifetime.Value.IsCancellationRequested);
                Assert.Null(authority.BindAdmissionActivationLifetime(attachment, epoch, admission, publication));
            }
            finally { release.TrySetResult(); await WaitPreparationRequestIdle(pair); }
        });

    [Theory]
    [InlineData("caller_cancel", false)]
    [InlineData("user_stop", false)]
    [InlineData("key_reset", false)]
    [InlineData("service_reset", false)]
    [InlineData("detach", false)]
    [InlineData("terminal", false)]
    [InlineData("caller_cancel", true)]
    [InlineData("user_stop", true)]
    [InlineData("key_reset", true)]
    [InlineData("service_reset", true)]
    [InlineData("detach", true)]
    [InlineData("terminal", true)]
    [InlineData("terminal_successor", true)]
    [Trait("FileTransferInfraSuite", "PreparationActivationHandoff")]
    public Task PreparationAdmission_HandoffCannotReviveRevokedRequest(string change, bool queuedOffer)
        => WithPublicPreparationPairAsync(false, async pair =>
        {
            using var request = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var offerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOffer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Payer.SessionPreparationBeforeLaneStartForTests = () => { entered.TrySetResult(); return release.Task; };
            if (queuedOffer)
                pair.Payer.SessionPreparationBeforeTransferActivationOfferForTests = () => { offerEntered.TrySetResult(); return releaseOffer.Task; };
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", request.Token);
            try
            {
                await entered.Task.WaitAsync(pair.Token);
                Assert.Equal("regular_nkn_v4_fast", (await StartPreparedAdmission(pair.PayerService, pair.Token))!.RouteToken);
                var handoff = GetPrivateField(pair.Payer, "sessionPreparationActivationHandoff")!;
                var activation = Assert.IsAssignableFrom<Task>(handoff.GetType().GetField("Activation")!.GetValue(handoff));
                if (queuedOffer)
                {
                    await pair.Payer.OpenFileTransferDataSessionAsync(pair.State(pair.PayerService).Admission!.Scope.SessionId,
                        "prepared-admission", pair.Token);
                    release.TrySetResult();
                    await offerEntered.Task.WaitAsync(pair.Token);
                }
                switch (change)
                {
                    case "caller_cancel": request.Cancel(); break;
                    case "user_stop": await pair.Payer.StopAccelerationAsync("header_switch_off", pair.Token); break;
                    case "key_reset": InvokePrivateMethod(pair.Payer, "ResetControlSecureState"); break;
                    case "service_reset": pair.PayerService.ResetSessionState(); break;
                    case "detach": pair.PayerService.DetachTransport(); break;
                    case "terminal": await pair.PayerService.CancelTransferAsync("prepared-admission", "test_complete", pair.Token); break;
                    case "terminal_successor":
                        await pair.PayerService.CancelTransferAsync("prepared-admission", "test_complete", pair.Token);
                        Assert.NotNull(await pair.PayerService.TryStartSendAsync(new("successor.bin", 4, "successor"),
                            _ => Task.FromResult<Stream>(new MemoryStream(new byte[4])), pair.Token));
                        break;
                }
                release.TrySetResult();
                releaseOffer.TrySetResult();
                await activation.WaitAsync(pair.Token);
                await WaitPreparationRequestIdle(pair);
                Assert.Equal(0, pair.PayerLane.OrdinaryStarts);
                Assert.False(pair.Payer.IsAccelerationAvailableForTests);
                Assert.Null(GetPrivateField(pair.Payer, "runtimeUnlockRecoveryRetryState"));
            }
            finally { release.TrySetResult(); releaseOffer.TrySetResult(); }
        });
}
