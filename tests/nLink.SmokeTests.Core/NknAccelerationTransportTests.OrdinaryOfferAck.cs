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
    [Fact]
    [Trait("Category", "Smoke")]
    [Trait("FileTransferInfraSuite", "RuntimeUnlockResponderPeerProbe")]
    public async Task OrdinaryAccelerationOffer_AnswerAckDoesNotRequireRuntimeUnlockOrPairedAuthority()
    {
        var previousPriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        var ackQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var hostClient = new FakeNknClient("host.ordinary-offer-ack.address");
            var helperClient = new FakeNknClient("helper.ordinary-offer-ack.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var offers = new ConcurrentQueue<Envelope>();
            var answers = new ConcurrentQueue<Envelope>();
            var acknowledgements = new ConcurrentQueue<Envelope>();
            hostClient.BeforeSendAsync = async (_, payload, ct) =>
            {
                if (!EnvelopeCodec.TryDeserialize(payload, out var envelope)) { return; }
                if (envelope.Type == MsgType.TransportAccelerationOffer) { offers.Enqueue(envelope); }
                if (envelope.Type == MsgType.TransportAccelerationAnswerAck)
                {
                    acknowledgements.Enqueue(envelope);
                    ackQueued.TrySetResult();
                    await releaseAck.Task.WaitAsync(ct);
                }
            };
            helperClient.BeforeSendAsync = (_, payload, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) && envelope.Type == MsgType.TransportAccelerationAnswer)
                { answers.Enqueue(envelope); }
                return Task.CompletedTask;
            };
            var options = NknTransportOptions.Load();
            using var host = new NknSignalingTransport(hostClient, options,
                new NknIdentity("host-ordinary-offer-ack", hostClient.Address), NknTunaAccelerationOptions.Disabled, hostLane);
            using var helper = new NknSignalingTransport(helperClient, options,
                new NknIdentity("helper-ordinary-offer-ack", helperClient.Address), NknTunaAccelerationOptions.Disabled, helperLane);
            var sessionId = await ApproveNknSessionAsync(host, helper, cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
                suppressAutomaticAccelerationNegotiation: true);
            const string transferId = "transfer_ordinary_offer_ack";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            _ = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            TrackAuthorizedRegularV4RouteForTests(host, transferId);
            TrackAuthorizedRegularV4RouteForTests(helper, transferId);
            var hostTransaction = GetPrivateField(host, "runtimeUnlockTransactionState");
            var helperTransaction = GetPrivateField(helper, "runtimeUnlockTransactionState");
            var hostLease = GetPrivateField(host, "runtimeUnlockTunaPathLeaseState");
            var helperLease = GetPrivateField(helper, "runtimeUnlockTunaPathLeaseState");
            var logStart = GetOperationalLogLength();

            // Suppressing approval's automatic negotiation makes this ordinary
            // offer deterministic. Both answer and ACK are production messages;
            // no runtime transaction, paired receipt or probe state is supplied.
            await host.RequestAccelerationNegotiationAsync("session_security_state_ready", cts.Token);
            await ackQueued.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            Assert.False(helper.IsAccelerationAvailableForTests);
            Assert.Null(GetPrivateField(helper, "pendingAccelerationAnswerAckAuthorityMode"));
            Assert.Null(GetPrivateField(helper, "pendingAccelerationAnswerAckPairedRetryBindingReceipt"));
            Assert.Equal(helperTransaction, GetPrivateField(helper, "runtimeUnlockTransactionState"));
            Assert.Equal(helperLease, GetPrivateField(helper, "runtimeUnlockTunaPathLeaseState"));
            var offerEnvelope = ExactMessage(offers);
            var answerEnvelope = ExactMessage(answers);
            var ackEnvelope = ExactMessage(acknowledgements);
            var secureOffer = Decode(host, offerEnvelope, "transport_acceleration_offer");
            var secureAnswer = Decode(helper, answerEnvelope, "transport_acceleration_answer");
            var secureAck = Decode(host, ackEnvelope, "transport_acceleration_answer_ack");
            var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var offer = JsonSerializer.Deserialize<TransportAccelerationOfferPayload>(secureOffer.Plaintext, json)!;
            var answer = JsonSerializer.Deserialize<TransportAccelerationAnswerPayload>(secureAnswer.Plaintext, json)!;
            var ack = JsonSerializer.Deserialize<TransportAccelerationAnswerAckPayload>(secureAck.Plaintext, json)!;
            Assert.Equal("session_security_state_ready", offer.Trigger);
            Assert.True(answer.Accepted);
            Assert.True(ack.Accepted);
            Assert.Equal((sessionId, offer.Nonce, offer.PayerDecisionId), (answer.SessionId, answer.Nonce, answer.PayerDecisionId));
            Assert.Equal((sessionId, offer.Nonce, offer.PayerDecisionId), (ack.SessionId, ack.Nonce, ack.PayerDecisionId));
            Assert.Equal(offer.Nonce, secureOffer.Metadata.RequestId);
            Assert.Equal(offer.Nonce, secureAnswer.Metadata.RequestId);
            Assert.Equal(offer.Nonce, secureAck.Metadata.RequestId);
            Assert.NotEqual(offerEnvelope.MessageId, answerEnvelope.MessageId);
            Assert.NotEqual(answerEnvelope.MessageId, ackEnvelope.MessageId);
            Assert.Equal(offer.Nonce, GetPrivateField(helper, "pendingAccelerationAnswerAckNonce"));
            Assert.Equal(offer.PayerDecisionId, GetPrivateField(helper, "pendingAccelerationAnswerAckPayerDecisionId"));

            releaseAck.TrySetResult();
            await WaitUntilAsync(() => helper.IsAccelerationAvailableForTests, TimeSpan.FromSeconds(3));
            Assert.Equal(hostTransaction, GetPrivateField(host, "runtimeUnlockTransactionState"));
            Assert.Equal(helperTransaction, GetPrivateField(helper, "runtimeUnlockTransactionState"));
            Assert.Equal(hostLease, GetPrivateField(host, "runtimeUnlockTunaPathLeaseState"));
            Assert.Equal(helperLease, GetPrivateField(helper, "runtimeUnlockTunaPathLeaseState"));
            var log = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_offer_queued; reason=session_security_state_ready;", log, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_answer_ack_pending;", log, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_answer_ack_received_raw;", log, StringComparison.Ordinal);
            Assert.Contains(log.Split(Environment.NewLine), line =>
                line.Contains("event=tuna_acceleration_answer_ack_received;", StringComparison.Ordinal) &&
                line.Contains("authority_mode=none;", StringComparison.Ordinal));
            Assert.DoesNotContain("post_fallback_paired_retry_offer_binding_stale", log, StringComparison.Ordinal);
            Assert.DoesNotContain("event=runtime_unlock_precommit_probe_launch_requested;", log, StringComparison.Ordinal);
        }
        finally
        {
            releaseAck.TrySetResult();
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousPriorityDelay;
        }

        static Envelope ExactMessage(ConcurrentQueue<Envelope> messages)
        {
            var copies = Assert.Single(messages.GroupBy(envelope => envelope.MessageId)).ToArray();
            Assert.All(copies, copy => Assert.Equal(copies[0].Payload, copy.Payload));
            return copies[0];
        }

        static SessionSecureEnvelopePayload Decode(NknSignalingTransport sender, Envelope envelope, string type)
            => SessionSecureEnvelopeCodec.Decrypt(
                Assert.IsType<byte[]>(GetPrivateField(sender, "controlSessionSharedKey")), envelope.Payload,
                new SessionSecureEnvelopeExpectation(SessionSecureMessageFamily.RemoteControl, type,
                    sender.CurrentSessionSecurityState.SessionId, new PeerAddress(sender.LocalPeerAddress)));
    }
}
