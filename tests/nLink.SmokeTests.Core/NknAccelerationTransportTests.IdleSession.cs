using System.Collections.Concurrent;
using System.Text.Json;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    [Trait("FileTransferInfraSuite", "IdleSessionReestablishment")]
    public Task IdleSession_ReestablishesAfterTerminalTransfer(
        bool completed, bool helperPays, bool switchPayer, bool stopBeforeAnswer)
        => RunIdleSessionReestablishmentAsync(completed, helperPays, switchPayer, stopBeforeAnswer);

    [Theory]
    [InlineData(false, false, "delayed_stop")]
    [InlineData(false, true, "delayed_stop")]
    [InlineData(true, false, "delayed_stop")]
    [InlineData(true, true, "delayed_stop")]
    [InlineData(false, false, "first_send_failure")]
    [InlineData(false, true, "first_send_failure")]
    [InlineData(true, false, "first_send_failure")]
    [InlineData(true, true, "first_send_failure")]
    [InlineData(false, false, "delayed_stop_new_stop")]
    [InlineData(true, true, "delayed_stop_key_reset")]
    [InlineData(false, true, "delayed_stop_new_transfer")]
    [InlineData(true, false, "first_send_failure_new_stop")]
    [InlineData(false, false, "first_send_failure_peer_stop")]
    [InlineData(false, false, "delayed_stop_late_marker")]
    [InlineData(true, true, "delayed_stop_late_marker")]
    [Trait("FileTransferInfraSuite", "IdleUnlockRaces")]
    public Task IdleSession_UnlockSurvivesOnlyItsOwnStopAndSendRetry(bool completed, bool helperPays, string change)
        => RunIdleSessionReestablishmentAsync(completed, helperPays, switchPayer: true, stopBeforeAnswer: false, change);

    [Theory]
    [InlineData(false, false, "before_publication_stop")]
    [InlineData(true, true, "before_publication_stop")]
    [InlineData(false, false, "offer_failure")]
    [InlineData(true, true, "offer_failure")]
    [InlineData(false, false, "offer_failure_cancel")]
    [InlineData(true, true, "offer_failure_cancel")]
    [InlineData(false, true, "offer_failure_new_transfer")]
    [InlineData(false, false, "answer_timeout")]
    [InlineData(true, true, "answer_timeout")]
    [InlineData(false, false, "answer_timeout_cancel")]
    [InlineData(true, true, "answer_timeout_cancel")]
    [InlineData(true, false, "answer_timeout_key_reset")]
    [Trait("FileTransferInfraSuite", "IdleUnlockRemainingRaces")]
    public Task IdleSession_UnlockPublicationAndOfferRetriesStayBound(bool completed, bool helperPays, string change)
        => RunIdleSessionReestablishmentAsync(completed, helperPays, switchPayer: true, stopBeforeAnswer: false, change);

    [Theory]
    [InlineData(false, false, "answer_commit_stop")]
    [InlineData(true, true, "answer_commit_stop")]
    [InlineData(false, false, "answer_commit_cancel")]
    [InlineData(true, true, "answer_commit_cancel")]
    [InlineData(false, false, "answer_ack_stop")]
    [InlineData(true, true, "answer_ack_stop")]
    [InlineData(false, false, "answer_ack_cancel")]
    [InlineData(true, true, "answer_ack_cancel")]
    [InlineData(false, true, "answer_ack_key_reset")]
    [InlineData(false, false, "retired_answer_current")]
    [InlineData(true, true, "retired_answer_current")]
    [InlineData(false, false, "retired_answer_cancel")]
    [InlineData(true, true, "retired_answer_cancel")]
    [InlineData(false, true, "retired_answer_new_transfer")]
    [InlineData(true, false, "retired_answer_key_reset")]
    [InlineData(false, false, "retired_answer_rejected_cancel")]
    [Trait("FileTransferInfraSuite", "IdleAnswerRaces")]
    public Task IdleSession_AnswersCannotOutliveTheirUnlock(bool completed, bool helperPays, string change)
        => RunIdleSessionReestablishmentAsync(completed, helperPays, switchPayer: true, stopBeforeAnswer: false, change);

    [Theory]
    [InlineData(false, false, "answer_ack_new_transfer")]
    [InlineData(false, true, "answer_ack_new_transfer")]
    [InlineData(true, false, "answer_ack_new_transfer")]
    [InlineData(true, true, "answer_ack_new_transfer")]
    [InlineData(false, false, "answer_physical_stop")]
    [InlineData(false, true, "answer_physical_stop")]
    [InlineData(false, false, "answer_physical_cancel")]
    [InlineData(false, true, "answer_physical_cancel")]
    [InlineData(false, false, "answer_physical_key_reset")]
    [InlineData(false, true, "answer_physical_replace")]
    [InlineData(false, false, "answer_physical_expire")]
    [InlineData(false, true, "answer_physical_current")]
    [Trait("FileTransferInfraSuite", "IdleAckCompletionRaces")]
    public Task IdleSession_AcceptedAckCompletesOnlyWithinItsHandshake(bool completed, bool helperPays, string change)
        => RunIdleSessionReestablishmentAsync(completed, helperPays, switchPayer: true, stopBeforeAnswer: false, change);

    private async Task RunIdleSessionReestablishmentAsync(
        bool completed, bool helperPays, bool switchPayer, bool stopBeforeAnswer, string? change = null)
    {
        FakeNknClient.ResetNetwork();
        var previousPriority = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var releaseAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publicationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePublication = new ManualResetEventSlim();
        var retryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerCommitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseAnswerCommit = new ManualResetEventSlim();
        var answerAckEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAnswerAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var unlockCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var delayStop = change?.StartsWith("delayed_stop", StringComparison.Ordinal) == true;
        var failSend = change?.StartsWith("first_send_failure", StringComparison.Ordinal) == true;
        var failOffer = change?.StartsWith("offer_failure", StringComparison.Ordinal) == true;
        var answerTimeout = change?.StartsWith("answer_timeout", StringComparison.Ordinal) == true;
        var offerRetry = failOffer || answerTimeout;
        try
        {
            var scope = Guid.NewGuid().ToString("N");
            var hostClient = new FakeNknClient($"host.idle-restart-{scope}.address");
            var helperClient = new FakeNknClient($"helper.idle-restart-{scope}.address");
            using var hostLane = new RetryableTunaAccelerationSession(!helperPays, 0) { StrictPayerHandoffForTests = true };
            using var helperLane = new RetryableTunaAccelerationSession(helperPays, 0) { StrictPayerHandoffForTests = true };
            hostLane.ForwardBytesForTests = async (lane, bytes, _) =>
            {
                await Task.Yield();
                helperLane.ReceivePeerBytesForTests(lane, bytes);
                return true;
            };
            helperLane.ForwardBytesForTests = async (lane, bytes, _) =>
            {
                await Task.Yield();
                hostLane.ReceivePeerBytesForTests(lane, bytes);
                return true;
            };
            var options = NknTransportOptions.Load();
            using var host = new NknSignalingTransport(hostClient, options,
                new NknIdentity("host-idle-restart", hostClient.Address), NknTunaAccelerationOptions.Disabled, hostLane);
            using var helper = new NknSignalingTransport(helperClient, options,
                new NknIdentity("helper-idle-restart", helperClient.Address), NknTunaAccelerationOptions.Disabled, helperLane);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(host);
            receiver.AttachTransport(helper);
            await ApproveNknSessionAsync(host, helper, timeout.Token,
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare | InviteCapabilities.FileTransfer,
                suppressAutomaticAccelerationNegotiation: true);
            var originalPayer = helperPays ? helper : host;
            await originalPayer.RequestAccelerationNegotiationAsync("runtime_unlock", timeout.Token);
            await WaitUntilAsync(() => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(5));

            const string transferId = "idle-restart-transfer";
            var payload = Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i % 251)).ToArray();
            using var destination = new MemoryStream();
            await sender.TryStartSendAsync(new("memory.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), timeout.Token);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision,
                TimeSpan.FromSeconds(3));
            if (completed)
                await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), timeout.Token);
            else
                await sender.CancelTransferAsync(transferId, "test_cancel", timeout.Token);
            var terminalState = completed ? FileTransferTransferState.Completed : FileTransferTransferState.Canceled;
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == terminalState && receiver.Snapshot.Inbound?.State == terminalState,
                TimeSpan.FromSeconds(5));
            if (completed) Assert.Equal(payload, destination.ToArray());

            var remoteStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (delayStop)
                (helperPays ? hostLane : helperLane).BeforeStopForTests = async reason =>
                {
                    if (reason != "remote_header_switch_off") return;
                    stopEntered.TrySetResult();
                    await releaseStop.Task.WaitAsync(timeout.Token);
                };
            // Observe completion of the peer's asynchronous stop callback, not just
            // the earlier clearing of its negotiated/available state.
            (helperPays ? hostLane : helperLane).StateChanged += (_, e) =>
            {
                if (!e.IsAvailable && e.Reason == "remote_header_switch_off") remoteStopped.TrySetResult();
            };
            await originalPayer.StopAccelerationAsync("header_switch_off", timeout.Token);
            await (delayStop ? stopEntered.Task : remoteStopped.Task).WaitAsync(TimeSpan.FromSeconds(3), timeout.Token);
            await WaitUntilAsync(() => !host.IsAccelerationAvailableForTests && !helper.IsAccelerationAvailableForTests &&
                (int)GetPrivateField(host, "accelerationNegotiationScheduled")! == 0 &&
                (int)GetPrivateField(helper, "accelerationNegotiationScheduled")! == 0, TimeSpan.FromSeconds(3));
            var hostPreparation = GetPrivateField(sender, "sessionPreparation");
            var helperPreparation = GetPrivateField(receiver, "sessionPreparation");
            Assert.Equal(FileTransferSessionPreparationPhase.AdmissionReserved,
                Assert.IsType<FileTransferSessionPreparationState>(hostPreparation).Phase);
            var retainedPublications = new[] { host, helper }.Select(transport =>
            {
                var map = (System.Collections.IDictionary)GetPrivateField(transport, "fileTransferRoutePublicationAuthorities")!;
                return map.Keys.Cast<object>().ToDictionary(key => key, key => map[key]);
            }).ToArray();
            var restartHelperPays = helperPays != switchPayer;
            hostLane.SetCanListen(!restartHelperPays);
            helperLane.SetCanListen(restartHelperPays);
            var payer = restartHelperPays ? helper : host;
            var payerClient = restartHelperPays ? helperClient : hostClient;
            string? rejectedIntentId = null;
            string? firstOfferId = null;
            var sentIntents = new ConcurrentDictionary<string, TransportAccelerationPayerIntentPayload>();
            var sentOffers = new ConcurrentDictionary<string, TransportAccelerationOfferPayload>();
            if (failSend || offerRetry)
                payerClient.BeforeSendCoreAsync = (_, bytes, _, _) =>
                {
                    if (EnvelopeCodec.TryDeserialize(bytes, out var envelope) && envelope.Type == MsgType.TransportAccelerationPayerIntent)
                    {
                        var decoded = SessionSecureEnvelopeCodec.Decrypt(
                            (byte[])InvokePrivateMethod(payer, "GetControlSessionSharedKeyOrThrow")!, envelope.Payload);
                        sentIntents[envelope.MessageId] = JsonSerializer.Deserialize<TransportAccelerationPayerIntentPayload>(decoded.Plaintext)!;
                        Interlocked.CompareExchange(ref rejectedIntentId, envelope.MessageId, null);
                        if (failSend && envelope.MessageId == rejectedIntentId)
                        {
                            failedSend.TrySetResult();
                            throw new InvalidOperationException("test_first_intent_rejected");
                        }
                    }
                    if (offerRetry && EnvelopeCodec.TryDeserialize(bytes, out var offerEnvelope) &&
                        offerEnvelope.Type == MsgType.TransportAccelerationOffer)
                    {
                        var decoded = SessionSecureEnvelopeCodec.Decrypt(
                            (byte[])InvokePrivateMethod(payer, "GetControlSessionSharedKeyOrThrow")!, offerEnvelope.Payload);
                        sentOffers[offerEnvelope.MessageId] = JsonSerializer.Deserialize<TransportAccelerationOfferPayload>(decoded.Plaintext)!;
                        Interlocked.CompareExchange(ref firstOfferId, offerEnvelope.MessageId, null);
                        if (failOffer && offerEnvelope.MessageId == firstOfferId)
                            throw new InvalidOperationException("test_first_offer_rejected");
                    }
                    return Task.CompletedTask;
                };
            if (answerTimeout)
            {
                NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromMilliseconds(200);
                payerClient.ShouldDeliverSendAsync = (_, bytes, _) => Task.FromResult(
                    !EnvelopeCodec.TryDeserialize(bytes, out var envelope) ||
                    envelope.Type != MsgType.TransportAccelerationOffer || envelope.MessageId != firstOfferId);
            }
            if (offerRetry)
                payer.IdleSessionUnlockBeforeRetryForTests = async () =>
                {
                    retryEntered.TrySetResult();
                    await releaseRetry.Task.WaitAsync(timeout.Token);
                };
            var messages = new ConcurrentQueue<MsgType>();
            Envelope? heldAnswer = null;
            foreach (var client in new[] { hostClient, helperClient })
                client.BeforeSendAsync = async (_, bytes, ct) =>
                {
                    if (!EnvelopeCodec.TryDeserialize(bytes, out var envelope)) return;
                    messages.Enqueue(envelope.Type);
                    if (envelope.Type != MsgType.TransportAccelerationAnswer) return;
                    heldAnswer = envelope;
                    answerHeld.TrySetResult();
                    await releaseAnswer.Task.WaitAsync(ct);
                };
            if (change == "before_publication_stop")
            {
                payer.IdleSessionUnlockBeforePublicationForTests = () =>
                {
                    publicationEntered.TrySetResult();
                    releasePublication.Wait(timeout.Token);
                };
                var unlock = Task.Run(() => payer.RequestAccelerationNegotiationAsync("runtime_unlock", timeout.Token));
                try
                {
                    await publicationEntered.Task.WaitAsync(timeout.Token);
                    var stopGeneration = (long)GetPrivateField(payer, "accelerationExplicitStopGeneration")!;
                    await payer.StopAccelerationAsync("header_switch_off", timeout.Token);
                    Assert.Equal(stopGeneration + 1, GetPrivateField(payer, "accelerationExplicitStopGeneration"));
                }
                finally { releasePublication.Set(); }
                await unlock.WaitAsync(timeout.Token);
                Assert.Null(GetPrivateField(payer, "idleSessionUnlock"));
                Assert.False((bool)GetPrivateField(payer, "idleSessionUnlockWaiting")!);
                Assert.Equal(0, GetPrivateField(payer, "accelerationNegotiationScheduled"));
                Assert.Empty(messages);
                Assert.False(host.IsAccelerationAvailableForTests);
                Assert.False(helper.IsAccelerationAvailableForTests);
                return;
            }
            await payer.RequestAccelerationNegotiationAsync("runtime_unlock", unlockCancellation.Token);
            var request = GetPrivateField(payer, "idleSessionUnlock")!;
            var requestedAt = (long)request.GetType().GetProperty("RequestedAtUnixMs")!.GetValue(request)!;
            if (offerRetry)
            {
                await retryEntered.Task.WaitAsync(TimeSpan.FromSeconds(8), timeout.Token);
                var retry = Assert.IsAssignableFrom<Task>(payer.IdleSessionUnlockRetryForTests);
                Assert.Single(sentOffers);
                Assert.Single(sentIntents);
                Assert.Null(GetPrivateField(payer, "outboundAccelerationOfferNonce"));
                Assert.Null(GetPrivateField(payer, "outboundAccelerationOfferIdleUnlock"));
                if (change!.EndsWith("_cancel", StringComparison.Ordinal)) unlockCancellation.Cancel();
                if (change.EndsWith("_new_transfer", StringComparison.Ordinal))
                    Assert.Equal("regular_nkn_v4_fast", (await StartPreparedAdmission(sender, timeout.Token))!.RouteToken);
                if (change.EndsWith("_key_reset", StringComparison.Ordinal)) InvokePrivateMethod(payer, "ResetControlSecureState");
                NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousAnswerTimeout;
                releaseRetry.TrySetResult();
                await retry.WaitAsync(timeout.Token);
                if (change.EndsWith("_cancel", StringComparison.Ordinal) ||
                    change.EndsWith("_new_transfer", StringComparison.Ordinal) ||
                    change.EndsWith("_key_reset", StringComparison.Ordinal))
                {
                    Assert.Single(sentOffers);
                    Assert.Single(sentIntents);
                    Assert.DoesNotContain(MsgType.TransportAccelerationAnswerAck, messages);
                    Assert.False(host.IsAccelerationAvailableForTests);
                    Assert.False(helper.IsAccelerationAvailableForTests);
                    Assert.Null(GetPrivateField(payer, "outboundRuntimeUnlockOfferAuthorityBoundary"));
                    return;
                }
            }
            if (delayStop) Assert.DoesNotContain(MsgType.TransportAccelerationPayerIntent, messages);
            if (failSend) await failedSend.Task.WaitAsync(TimeSpan.FromSeconds(3), timeout.Token);
            if (change?.EndsWith("new_stop", StringComparison.Ordinal) == true)
                await payer.StopAccelerationAsync("header_switch_off", timeout.Token);
            if (change == "delayed_stop_key_reset") InvokePrivateMethod(payer, "ResetControlSecureState");
            if (change == "delayed_stop_new_transfer")
                Assert.Equal("regular_nkn_v4_fast", (await StartPreparedAdmission(sender, timeout.Token))!.RouteToken);
            if (change == "delayed_stop_late_marker")
                InvokePrivateMethod(payer, "MarkAccelerationPeerUserStoppedForCurrentSession", new object?[] { null });
            if (change == "first_send_failure_peer_stop")
            {
                await WaitUntilAsync(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > requestedAt, TimeSpan.FromSeconds(1));
                InvokePrivateMethod(restartHelperPays ? host : helper,
                    "MarkAccelerationUserStoppedForCurrentSession", new object?[] { null });
            }
            releaseStop.TrySetResult();
            if (delayStop)
                await WaitUntilAsync(() => (int)GetPrivateField(payer, "accelerationLaneStopDrains")! == 0,
                    TimeSpan.FromSeconds(3));
            if (change is "delayed_stop_new_stop" or "delayed_stop_key_reset" or "delayed_stop_new_transfer" or "first_send_failure_new_stop")
            {
                await WaitUntilAsync(() => (int)GetPrivateField(payer, "accelerationNegotiationScheduled")! == 0,
                    TimeSpan.FromSeconds(3));
                Assert.False(host.IsAccelerationAvailableForTests);
                Assert.False(helper.IsAccelerationAvailableForTests);
                Assert.DoesNotContain(MsgType.TransportAccelerationOffer, messages);
                Assert.DoesNotContain(MsgType.TransportAccelerationAnswerAck, messages);
                Assert.Null(GetPrivateField(payer, "outboundRuntimeUnlockOfferAuthorityBoundary"));
                return;
            }
            await answerHeld.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
            if (failSend || offerRetry)
            {
                Assert.True(sentIntents.Count >= 2);
                foreach (var intent in sentIntents.Values)
                {
                    Assert.Equal("session_ready_after_user_unlock", intent.Trigger);
                    Assert.Equal(requestedAt, intent.SentAtUnixMs);
                    Assert.Equal(sentIntents.Values.First().ExpiresAtUnixMs, intent.ExpiresAtUnixMs);
                }
            }
            if (offerRetry)
            {
                Assert.Equal(2, sentOffers.Count);
                var binding = GetPrivateField(payer, "outboundAccelerationOfferIdleUnlock")!;
                Assert.Same(request, binding.GetType().GetProperty("Request")!.GetValue(binding));
                foreach (var offer in sentOffers.Values)
                {
                    Assert.Equal("session_ready_after_user_unlock", offer.Trigger);
                    Assert.Equal(requestedAt, offer.SentAtUnixMs);
                    Assert.Equal(sentOffers.Values.First().ExpiresAtUnixMs, offer.ExpiresAtUnixMs);
                }
            }
            var commitRace = change?.StartsWith("answer_commit_", StringComparison.Ordinal) == true;
            var physicalAckRace = change?.StartsWith("answer_physical_", StringComparison.Ordinal) == true;
            var ackRace = physicalAckRace || change?.StartsWith("answer_ack_", StringComparison.Ordinal) == true;
            var retiredAnswer = change?.StartsWith("retired_answer_", StringComparison.Ordinal) == true;
            if (commitRace || ackRace || retiredAnswer)
            {
                var binding = GetPrivateField(payer, "outboundAccelerationOfferIdleUnlock")!;
                var peerClient = restartHelperPays ? hostClient : helperClient;
                if (retiredAnswer)
                {
                    // Exercise the existing retirement transition without starting a
                    // second handshake: its delayed answer must retain the exact intent.
                    lock (GetPrivateField(payer, "accelerationGate")!)
                        InvokePrivateMethod(payer, "RetireOutboundAccelerationOfferLocked",
                            ((FileTransferSessionPreparationSecurityEpoch)request.GetType().GetProperty("Epoch")!.GetValue(request)!).SessionId,
                            "accepted_peer_offer", 0L);
                    Assert.Null(GetPrivateField(payer, "outboundAccelerationOfferIdleUnlock"));
                    Assert.Same(binding, GetPrivateField(payer, "retiredAccelerationOfferIdleUnlock"));
                }
                if (commitRace)
                    payer.IdleSessionBeforeAnswerCommitForTests = () =>
                    {
                        answerCommitEntered.TrySetResult();
                        releaseAnswerCommit.Wait(timeout.Token);
                    };
                if (ackRace)
                    payer.IdleSessionBeforeAnswerAckForTests = async () =>
                    {
                        answerAckEntered.TrySetResult();
                        await releaseAnswerAck.Task.WaitAsync(timeout.Token);
                    };

                async Task InvalidateAnswerAsync()
                {
                    if (change!.EndsWith("_stop", StringComparison.Ordinal))
                        await payer.StopAccelerationAsync("header_switch_off", timeout.Token);
                    if (change.EndsWith("_cancel", StringComparison.Ordinal)) unlockCancellation.Cancel();
                    if (change.EndsWith("_key_reset", StringComparison.Ordinal)) InvokePrivateMethod(payer, "ResetControlSecureState");
                    if (change.EndsWith("_new_transfer", StringComparison.Ordinal))
                        Assert.Equal("regular_nkn_v4_fast", (await StartPreparedAdmission(sender, timeout.Token))!.RouteToken);
                }

                if (retiredAnswer) await InvalidateAnswerAsync();
                var handling = Task.Run(() =>
                {
                    if (change == "retired_answer_rejected_cancel")
                    {
                        var decoded = SessionSecureEnvelopeCodec.Decrypt(
                            (byte[])InvokePrivateMethod(payer, "GetControlSessionSharedKeyOrThrow")!, heldAnswer!.Payload);
                        var answer = JsonSerializer.Deserialize<TransportAccelerationAnswerPayload>(decoded.Plaintext)!;
                        answer.Accepted = false;
                        answer.RejectReason = "sidecar_unavailable";
                        InvokePrivateMethod(payer, "HandleValidatedTransportAccelerationAnswer",
                            peerClient.Address, heldAnswer, answer, null);
                    }
                    else InvokePrivateMethod(payer, "HandleTransportAccelerationAnswer", peerClient.Address, heldAnswer!);
                });
                if (physicalAckRace)
                {
                    await answerAckEntered.Task.WaitAsync(timeout.Token);
                    await handling.WaitAsync(timeout.Token);
                    var sendGate = Assert.IsType<SemaphoreSlim>(GetPrivateField(payer, "outboundSendGate"));
                    await sendGate.WaitAsync(timeout.Token);
                    var directCopiesEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var releaseDirectCopies = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var directCopies = 0;
                    var bulkCopies = 0;
                    payerClient.BeforeSendCoreAsync = async (_, bytes, lane, ct) =>
                    {
                        if (!EnvelopeCodec.TryDeserialize(bytes, out var envelope) || envelope.Type != MsgType.TransportAccelerationAnswerAck) return;
                        if (lane == NknBridgeChannel.Bulk) Interlocked.Increment(ref bulkCopies);
                        if (lane == NknBridgeChannel.Control && Interlocked.Increment(ref directCopies) <= 2)
                        {
                            if (Volatile.Read(ref directCopies) == 2) directCopiesEntered.TrySetResult();
                            await releaseDirectCopies.Task.WaitAsync(ct);
                            throw new InvalidOperationException("test_direct_ack_copy_failed");
                        }
                    };
                    try
                    {
                        releaseAnswerAck.TrySetResult();
                        await directCopiesEntered.Task.WaitAsync(timeout.Token);
                        await Assert.IsAssignableFrom<Task>(payer.IdleSessionAnswerAckForTests).WaitAsync(timeout.Token);
                        Assert.Equal(2, messages.Count(type => type == MsgType.TransportAccelerationAnswerAck));
                        await InvalidateAnswerAsync();
                        if (change == "answer_physical_replace")
                            lock (GetPrivateField(payer, "accelerationGate")!) SetPrivateField(payer, "acceptedIdleSessionAnswer", null);
                        if (change == "answer_physical_expire")
                            request.GetType().GetProperty("RequestedAtUnixMs")!.SetValue(request, DateTimeOffset.UtcNow.AddMinutes(-3).ToUnixTimeMilliseconds());
                    }
                    finally
                    {
                        sendGate.Release();
                        releaseDirectCopies.TrySetResult();
                    }
                    var sends = Assert.IsType<Task[]>(payer.IdleSessionAnswerAckSendsForTests);
                    try { await Task.WhenAll(sends).WaitAsync(timeout.Token); }
                    catch (OperationCanceledException) when (change == "answer_physical_cancel" && !timeout.IsCancellationRequested) { }
                    Assert.All(sends, task => Assert.False(task.IsFaulted));
                    if (change == "answer_physical_current")
                    {
                        Assert.True(Volatile.Read(ref bulkCopies) > 0);
                        await WaitUntilAsync(() => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                            TimeSpan.FromSeconds(3));
                    }
                    else
                    {
                        Assert.Equal(2, messages.Count(type => type == MsgType.TransportAccelerationAnswerAck));
                        Assert.Equal(0, Volatile.Read(ref bulkCopies));
                        Assert.False((restartHelperPays ? host : helper).IsAccelerationAvailableForTests);
                    }
                    foreach (var (transport, retained) in new[] { host, helper }.Zip(retainedPublications))
                    {
                        var map = (System.Collections.IDictionary)GetPrivateField(transport, "fileTransferRoutePublicationAuthorities")!;
                        Assert.Equal(retained.Count, map.Count);
                        foreach (var (key, value) in retained) Assert.Equal(value, map[key]);
                    }
                    await handling.WaitAsync(timeout.Token);
                    return;
                }
                try
                {
                    if (commitRace)
                    {
                        await answerCommitEntered.Task.WaitAsync(timeout.Token);
                        await InvalidateAnswerAsync();
                    }
                    if (ackRace)
                    {
                        await answerAckEntered.Task.WaitAsync(timeout.Token);
                        if (change == "answer_ack_new_transfer")
                        {
                            // Admit on the already-ready payer before its ACK task can run.
                            Assert.True(payer.IsAccelerationAvailableForTests);
                            Assert.Equal("file_tuna_v4", (await StartPreparedAdmission(restartHelperPays ? receiver : sender, timeout.Token))!.RouteToken);
                            Assert.Equal(FileTransferAccelerationRequestPurpose.TransferActivation,
                                Assert.IsType<FileTransferAccelerationRequestSelection>(InvokePrivateMethod(payer, "SelectSessionPreparationRequest")).Purpose);
                        }
                        else await InvalidateAnswerAsync();
                    }
                }
                finally
                {
                    releaseAnswerCommit.Set();
                    releaseAnswerAck.TrySetResult();
                }
                await handling.WaitAsync(timeout.Token);
                if (ackRace) await Assert.IsAssignableFrom<Task>(payer.IdleSessionAnswerAckForTests).WaitAsync(timeout.Token);
                if (change == "answer_ack_new_transfer")
                {
                    await WaitUntilAsync(() => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                        TimeSpan.FromSeconds(3));
                    Assert.Contains(MsgType.TransportAccelerationAnswerAck, messages);
                    var admitted = (restartHelperPays ? receiver : sender).Snapshot.Outbound!;
                    Assert.Equal("prepared-admission", admitted.TransferId);
                    Assert.Equal("file_tuna_v4", admitted.RouteToken);
                    Assert.Null(GetPrivateField(payer, "outboundRuntimeUnlockOfferAuthorityBoundary"));
                    Assert.Null(GetPrivateField(payer, "runtimeUnlockAcceptedPreCommitSnapshot"));
                    return;
                }
                if (change != "retired_answer_current")
                {
                    Assert.DoesNotContain(MsgType.TransportAccelerationAnswerAck, messages);
                    Assert.Null(GetPrivateField(payer, "pendingAccelerationAnswerAckReplyShadow"));
                    Assert.Null(payer.IdleSessionUnlockRetryForTests);
                    if (!ackRace)
                    {
                        Assert.False(host.IsAccelerationAvailableForTests);
                        Assert.False(helper.IsAccelerationAvailableForTests);
                    }
                    if (change!.EndsWith("_stop", StringComparison.Ordinal))
                        Assert.False(payer.IsAccelerationAvailableForTests);
                    foreach (var (transport, retained) in new[] { host, helper }.Zip(retainedPublications))
                    {
                        Assert.Null(GetPrivateField(transport, "outboundRuntimeUnlockOfferAuthorityBoundary"));
                        Assert.Null(GetPrivateField(transport, "runtimeUnlockAcceptedPreCommitSnapshot"));
                        if (!change.EndsWith("_new_transfer", StringComparison.Ordinal))
                        {
                            var map = (System.Collections.IDictionary)GetPrivateField(transport, "fileTransferRoutePublicationAuthorities")!;
                            Assert.Equal(retained.Count, map.Count);
                            foreach (var (key, value) in retained) Assert.Equal(value, map[key]);
                        }
                    }
                    return;
                }
            }
            if (change == "first_send_failure_peer_stop")
            {
                Assert.NotNull(GetPrivateField(payer, "outboundAccelerationOfferNonce"));
                releaseAnswer.TrySetResult();
                await WaitUntilAsync(() => GetPrivateField(payer, "outboundAccelerationOfferNonce") is null,
                    TimeSpan.FromSeconds(3));
                Assert.False(host.IsAccelerationAvailableForTests);
                Assert.False(helper.IsAccelerationAvailableForTests);
                Assert.DoesNotContain(MsgType.TransportAccelerationAnswerAck, messages);
                return;
            }
            if (change != "retired_answer_current")
            {
                Assert.False(host.IsAccelerationAvailableForTests);
                Assert.False(helper.IsAccelerationAvailableForTests);
            }
            if (stopBeforeAnswer)
            {
                await payer.StopAccelerationAsync("header_switch_off", timeout.Token);
                InvokePrivateMethod(payer, "HandleTransportAccelerationAnswer",
                    (restartHelperPays ? hostClient : helperClient).Address, heldAnswer!);
                Assert.False(host.IsAccelerationAvailableForTests);
                Assert.False(helper.IsAccelerationAvailableForTests);
                Assert.DoesNotContain(MsgType.TransportAccelerationAnswerAck, messages);
            }
            else
            {
                releaseAnswer.TrySetResult();
                await WaitUntilAsync(() => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                    TimeSpan.FromSeconds(5));
                Assert.Contains(MsgType.TransportAccelerationAnswerAck, messages);
                Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, host.AccelerationNegotiatedLanesForTests);
                Assert.Equal(host.AccelerationNegotiatedLanesForTests, helper.AccelerationNegotiatedLanesForTests);
            }
            Assert.Contains(MsgType.TransportAccelerationOffer, messages);
            Assert.DoesNotContain(MsgType.SessionPreparationOffer, messages);
            Assert.Same(hostPreparation, GetPrivateField(sender, "sessionPreparation"));
            Assert.Same(helperPreparation, GetPrivateField(receiver, "sessionPreparation"));
            Assert.Equal(terminalState, sender.Snapshot.Outbound!.State);
            Assert.Equal(terminalState, receiver.Snapshot.Inbound!.State);
            foreach (var (transport, retained) in new[] { host, helper }.Zip(retainedPublications))
            {
                var map = (System.Collections.IDictionary)GetPrivateField(transport, "fileTransferRoutePublicationAuthorities")!;
                Assert.Equal(retained.Count, map.Count);
                foreach (var (key, value) in retained) Assert.Equal(value, map[key]);
                Assert.Null(GetPrivateField(transport, "outboundRuntimeUnlockOfferAuthorityBoundary"));
                Assert.Null(GetPrivateField(transport, "runtimeUnlockAcceptedPreCommitSnapshot"));
            }
            if (!stopBeforeAnswer)
            {
                // A new file still needs its own explicit Coordinator admission.
                Assert.Equal("file_tuna_v4", (await StartPreparedAdmission(sender, timeout.Token))!.RouteToken);
                await WaitUntilAsync(() => receiver.Snapshot.Inbound?.TransferId == "prepared-admission" &&
                    receiver.Snapshot.Inbound.RouteToken == "file_tuna_v4", TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            releaseAnswerCommit.Set();
            releaseAnswerAck.TrySetResult();
            releasePublication.Set();
            releaseRetry.TrySetResult();
            releaseStop.TrySetResult();
            releaseAnswer.TrySetResult();
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousPriority;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousAnswerTimeout;
            FakeNknClient.ResetNetwork();
        }
    }

    [Theory]
    [InlineData("session_ready_after_user_unlock", 0, true)]
    [InlineData("session_ready_after_user_unlock", 1, true)]
    [InlineData("session_ready_after_user_unlock", -1, false)]
    [InlineData("session_ready_after_user_unlock", long.MinValue, false)]
    [InlineData("session_security_state_ready", 1, false)]
    [InlineData("runtime_unlock", 1, true)]
    [Trait("FileTransferInfraSuite", "IdleSessionReestablishment")]
    public Task IdleSession_ExplicitPeerUnlockPreservesStopFreshness(string trigger, long offset, bool allowed)
        => WithPublicPreparationPairAsync(false, pair =>
        {
            InvokePrivateMethod(pair.Payer, "MarkAccelerationUserStoppedForCurrentSession", new object?[] { null });
            var stoppedAt = (long)GetPrivateField(pair.Payer, "accelerationUserStoppedUtcMs")!;
            var cleared = Assert.IsType<bool>(InvokePrivateMethod(pair.Payer,
                "ClearAccelerationUserStoppedForFreshPeerMessage", "offer", trigger,
                offset == long.MinValue ? 0L : stoppedAt + offset, null, null));
            Assert.Equal(allowed, cleared);
            Assert.Equal(!allowed, pair.Payer.IsAccelerationUserStoppedForCurrentSessionForTests);
            Assert.Empty(pair.Sent);
            Assert.Null(GetPrivateField(pair.Payer, "outboundRuntimeUnlockOfferAuthorityBoundary"));
            Assert.Null(GetPrivateField(pair.Payer, "runtimeUnlockAcceptedPreCommitSnapshot"));
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [Trait("FileTransferInfraSuite", "IdleSession")]
    public async Task IdleSession_UnlockNegotiatesBeforeFileOrScreenStarts(bool helperPays, bool allowFiles, bool stopBeforeAnswer)
    {
        FakeNknClient.ResetNetwork();
        var previousPriority = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var releaseAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Envelope? heldAnswer = null;
        try
        {
            var hostClient = new FakeNknClient("host.idle-tuna.address");
            var helperClient = new FakeNknClient("helper.idle-tuna.address");
            using var hostLane = new RetryableTunaAccelerationSession(!helperPays, 0) { StrictPayerHandoffForTests = true };
            using var helperLane = new RetryableTunaAccelerationSession(helperPays, 0) { StrictPayerHandoffForTests = true };
            var tunaSends = 0;
            foreach (var lane in new[] { hostLane, helperLane })
                lane.ForwardBytesForTests = (_, _, _) =>
                {
                    Interlocked.Increment(ref tunaSends);
                    return Task.FromResult(true);
                };
            var options = NknTransportOptions.Load();
            using var host = new NknSignalingTransport(hostClient, options,
                new NknIdentity("host-idle-tuna", hostClient.Address), NknTunaAccelerationOptions.Disabled, hostLane);
            using var helper = new NknSignalingTransport(helperClient, options,
                new NknIdentity("helper-idle-tuna", helperClient.Address), NknTunaAccelerationOptions.Disabled, helperLane);
            using var hostFiles = new SessionFileTransferService();
            using var helperFiles = new SessionFileTransferService();
            hostFiles.AttachTransport(host);
            helperFiles.AttachTransport(helper);
            await ApproveNknSessionAsync(host, helper, timeout.Token,
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare |
                (allowFiles ? InviteCapabilities.FileTransfer : InviteCapabilities.None),
                suppressAutomaticAccelerationNegotiation: true);
            var messages = new ConcurrentQueue<MsgType>();
            foreach (var client in new[] { hostClient, helperClient })
                client.BeforeSendAsync = async (_, bytes, ct) =>
                {
                    if (!EnvelopeCodec.TryDeserialize(bytes, out var envelope)) return;
                    messages.Enqueue(envelope.Type);
                    if (envelope.Type == MsgType.TransportAccelerationAnswer)
                    {
                        heldAnswer = envelope;
                        answerHeld.TrySetResult();
                        await releaseAnswer.Task.WaitAsync(ct);
                    }
                };

            await (helperPays ? helper : host).RequestAccelerationNegotiationAsync("runtime_unlock", timeout.Token);
            await answerHeld.Task.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
            Assert.False(host.IsAccelerationAvailableForTests);
            Assert.False(helper.IsAccelerationAvailableForTests);
            if (stopBeforeAnswer)
            {
                await (helperPays ? helper : host).StopAccelerationAsync("header_switch_off", timeout.Token);
                // Deliver the authenticated late answer directly at its handler
                // boundary; do not infer processing from a redacted log string.
                InvokePrivateMethod(helperPays ? helper : host, "HandleTransportAccelerationAnswer",
                    (helperPays ? hostClient : helperClient).Address, heldAnswer!);
            }
            if (stopBeforeAnswer)
            {
                Assert.False(host.IsAccelerationAvailableForTests);
                Assert.False(helper.IsAccelerationAvailableForTests);
                Assert.DoesNotContain(MsgType.TransportAccelerationAnswerAck, messages);
            }
            else
            {
                releaseAnswer.TrySetResult();
                await WaitUntilAsync(() => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                    TimeSpan.FromSeconds(5));
                var lanes = NknAccelerationLaneKind.Screen | (allowFiles ? NknAccelerationLaneKind.File : NknAccelerationLaneKind.None);
                Assert.Equal(lanes, host.AccelerationNegotiatedLanesForTests);
                Assert.Equal(lanes, helper.AccelerationNegotiatedLanesForTests);
                Assert.Contains(MsgType.TransportAccelerationAnswerAck, messages);
            }

            Assert.Contains(MsgType.TransportAccelerationOffer, messages);
            Assert.Contains(MsgType.TransportAccelerationAnswer, messages);
            Assert.DoesNotContain(MsgType.SessionPreparationOffer, messages);
            Assert.Equal(1, (helperPays ? helperLane : hostLane).EnsureListenerCalls);
            Assert.Equal(1, (helperPays ? hostLane : helperLane).StartDialerCalls);
            foreach (var service in new[] { hostFiles, helperFiles })
            {
                Assert.Null(service.Snapshot.Inbound);
                Assert.Null(service.Snapshot.Outbound);
            }
            foreach (var transport in new[] { host, helper })
            {
                Assert.Empty((System.Collections.IDictionary)GetPrivateField(transport, "fileTransferRoutePublicationAuthorities")!);
                Assert.Null(GetPrivateField(transport, "outboundRuntimeUnlockOfferAuthorityBoundary"));
                Assert.Null(GetPrivateField(transport, "runtimeUnlockAcceptedPreCommitSnapshot"));
            }
            Assert.Equal(0, Volatile.Read(ref tunaSends));
            if (allowFiles && !stopBeforeAnswer)
            {
                // Only a subsequent explicit admission may publish a file route.
                Assert.Equal("file_tuna_v4", (await StartPreparedAdmission(hostFiles, timeout.Token))!.RouteToken);
                await WaitUntilAsync(() => helperFiles.Snapshot.Inbound?.RouteToken == "file_tuna_v4", TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            releaseAnswer.TrySetResult();
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousPriority;
            FakeNknClient.ResetNetwork();
        }
    }
}
