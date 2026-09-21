using System.Collections.Concurrent;
using System.Text.Json;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Fact]
    [Trait("FileTransferInfraSuite", "SessionPreparationWire")]
    public void PreparationWire_AddsTypesWithoutRenumberingExistingProtocol()
    {
        Assert.Equal(new[] { 41, 42, 43, 44, 50, 51 }, new[]
        {
            (int)MsgType.TransportAccelerationOffer, (int)MsgType.TransportAccelerationAnswer,
            (int)MsgType.TransportAccelerationDown, (int)MsgType.TransportAccelerationPayerIntent,
            (int)MsgType.TransportAccelerationAnswerAck, (int)MsgType.SessionHeartbeat,
        });
        Assert.Equal(new[] { 52, 53, 54 }, PreparationWireTypes.Select(type => (int)type));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationWire")]
    public Task PreparationWire_AuthenticatesBothPayerRolesWithoutCreatingReadiness(bool helperPays)
        => WithPreparationWirePairAsync(helperPays, async pair =>
        {
            foreach (var type in PreparationWireTypes)
            {
                var (sender, receiver, source) = pair.Endpoints(type);
                var envelope = pair.Envelope(sender, type);
                Assert.True(receiver.TryReadSessionPreparationEnvelope(source, envelope, out var message));
                Assert.Equal(type, message!.Type);
                Assert.Equal(pair.Nonce, message.Nonce);
                Assert.Equal(pair.Deadline, message.DeadlineUtc);
                Assert.Equal(type != MsgType.SessionPreparationOffer,
                    receiver.IsSessionPreparationReplyCurrent(message, pair.Permit(receiver)));
                Assert.False(receiver.TryReadSessionPreparationEnvelope(source, envelope, out _));
            }
            pair.AssertNoEffects();
            await Task.CompletedTask;
        });

    [Theory]
    [InlineData("source")]
    [InlineData("session")]
    [InlineData("nonce")]
    [InlineData("payer")]
    [InlineData("protocol")]
    [InlineData("expired")]
    [InlineData("unbounded")]
    [InlineData("sent_future")]
    [InlineData("sent_missing")]
    [InlineData("trigger")]
    [InlineData("role")]
    [InlineData("address")]
    [InlineData("screen")]
    [InlineData("unknown_lane")]
    [InlineData("null_lanes")]
    [InlineData("malformed")]
    [InlineData("ciphertext")]
    [Trait("FileTransferInfraSuite", "SessionPreparationWire")]
    public Task PreparationWire_RejectsInvalidAuthenticatedOfferWithoutEffects(string fault)
        => WithPreparationWirePairAsync(false, async pair =>
        {
            var type = MsgType.SessionPreparationOffer;
            var (sender, receiver, source) = pair.Endpoints(type);
            var offer = (TransportAccelerationOfferPayload)pair.Payload(type);
            switch (fault)
            {
                case "source": source = "unapproved.preparation.peer"; break;
                case "session": offer.SessionId = "other-session"; break;
                case "nonce": offer.Nonce = "other-nonce"; break;
                case "payer": offer.PayerDecisionId = 0; break;
                case "protocol": offer.SidecarProtocolVersion++; break;
                case "expired": offer.ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds(); break;
                case "unbounded": offer.ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(); break;
                case "sent_future": offer.SentAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(); break;
                case "sent_missing": offer.SentAtUnixMs = 0; break;
                case "trigger": offer.Trigger = "runtime_unlock"; break;
                case "role": offer.SenderRole = "helper"; break;
                case "address": offer.TunaAddress = ""; break;
                case "screen": offer.SupportedLanes = ["file", "screen"]; break;
                case "unknown_lane": offer.SupportedLanes = ["file", "other"]; break;
                case "null_lanes": offer.SupportedLanes = null!; break;
            }
            var envelope = pair.Envelope(sender, type, fault == "malformed" ? "{"u8.ToArray() : JsonSerializer.SerializeToUtf8Bytes(offer));
            if (fault == "ciphertext") envelope.Payload[^1] ^= 1;
            Assert.False(receiver.TryReadSessionPreparationEnvelope(source, envelope, out _));
            pair.AssertNoEffects();
            await Task.CompletedTask;
        });

    [Theory]
    [InlineData(false, "nonce")]
    [InlineData(true, "nonce")]
    [InlineData(false, "payer")]
    [InlineData(true, "payer")]
    [InlineData(false, "deadline")]
    [InlineData(true, "deadline")]
    [InlineData(false, "copied_permit")]
    [InlineData(true, "copied_permit")]
    [InlineData(false, "reset")]
    [InlineData(true, "reset")]
    [InlineData(false, "detach")]
    [InlineData(true, "detach")]
    [InlineData(false, "key_reset")]
    [InlineData(true, "key_reset")]
    [InlineData(false, "retire")]
    [InlineData(true, "retire")]
    [InlineData(false, "admission")]
    [InlineData(true, "admission")]
    [Trait("FileTransferInfraSuite", "SessionPreparationWire")]
    public Task PreparationWire_ReplyRequiresExactCurrentCorePermit(bool localIsPayer, string change)
        => WithPreparationWirePairAsync(false, async pair =>
        {
            var type = localIsPayer ? MsgType.SessionPreparationAnswer : MsgType.SessionPreparationAnswerAck;
            var (sender, receiver, source) = pair.Endpoints(type);
            Assert.True(receiver.TryReadSessionPreparationEnvelope(source, pair.Envelope(sender, type), out var message));
            var permit = pair.Permit(receiver);
            Assert.True(receiver.IsSessionPreparationReplyCurrent(message!, permit));
            var service = ReferenceEquals(receiver, pair.Host) ? pair.HostService : pair.HelperService;
            switch (change)
            {
                case "nonce": message = message! with { Nonce = "other" }; break;
                case "payer": message = message! with { PayerDecisionId = 2 }; break;
                case "deadline": message = message! with { DeadlineUtc = pair.Deadline.AddMilliseconds(-1) }; break;
                case "copied_permit": permit = permit with { }; break;
                case "reset": service.ResetSessionState(); break;
                case "detach": service.DetachTransport(); break;
                case "key_reset": InvokePrivateMethod(receiver, "ResetControlSecureState"); break;
                case "retire":
                    message!.Attachment.Authority.Retire(message.Attachment, message.Epoch, permit);
                    break;
                case "admission":
                    await service.TryStartSendAsync(new("first.bin", 8, "first-preparation-wire-transfer"),
                        _ => Task.FromResult<Stream>(new MemoryStream(new byte[8])), pair.Token);
                    break;
            }
            Assert.False(receiver.IsSessionPreparationReplyCurrent(message!, permit));
            if (change != "admission") pair.AssertNoEffects();
            else
            {
                Assert.False(pair.Host.IsAccelerationAvailableForTests);
                Assert.False(pair.Helper.IsAccelerationAvailableForTests);
                Assert.Equal(0, pair.HostLane.StartDialerCalls + pair.HelperLane.StartDialerCalls);
                Assert.Equal(0, pair.HostLane.EnsureListenerCalls + pair.HelperLane.EnsureListenerCalls);
            }
        });

    [Theory]
    [InlineData(52, 41)]
    [InlineData(53, 42)]
    [InlineData(54, 50)]
    [Trait("FileTransferInfraSuite", "SessionPreparationWire")]
    public Task PreparationWire_AuthenticatedPurposeCannotBeRetaggedAsLegacy(int newType, int oldType)
        => WithPreparationWirePairAsync(false, async pair =>
        {
            var type = (MsgType)newType;
            var (sender, receiver, source) = pair.Endpoints(type);
            var legacy = pair.Envelope(sender, (MsgType)oldType, JsonSerializer.SerializeToUtf8Bytes(pair.Payload(type)));
            Assert.False(receiver.TryReadSessionPreparationEnvelope(source, legacy with { Type = type }, out _));
            var preparation = pair.Envelope(sender, type);
            receiver.RouteControlEnvelope(source, preparation with { Type = (MsgType)oldType });
            Assert.False(EnvelopeCodec.TryDeserialize(EnvelopeCodec.Serialize(preparation with { Type = (MsgType)255 }), out _));
            pair.AssertNoEffects();
            await Task.CompletedTask;
        });

    [Theory]
    [InlineData(52, 53)]
    [InlineData(52, 54)]
    [InlineData(53, 52)]
    [InlineData(53, 54)]
    [InlineData(54, 52)]
    [InlineData(54, 53)]
    [Trait("FileTransferInfraSuite", "SessionPreparationWire")]
    public Task PreparationWire_NewMessageKindsAreAlsoAuthenticated(int originalType, int substitutedType)
        => WithPreparationWirePairAsync(false, async pair =>
        {
            var (sender, receiver, source) = pair.Endpoints((MsgType)originalType);
            var envelope = pair.Envelope(sender, (MsgType)originalType);
            Assert.False(receiver.TryReadSessionPreparationEnvelope(source,
                envelope with { Type = (MsgType)substitutedType }, out _));
            pair.AssertNoEffects();
            await Task.CompletedTask;
        });

    [Theory]
    [InlineData(53)]
    [InlineData(54)]
    [Trait("FileTransferInfraSuite", "SessionPreparationWire")]
    public Task PreparationWire_NegativeReplyCannotCompletePermit(int messageType)
        => WithPreparationWirePairAsync(false, async pair =>
        {
            var type = (MsgType)messageType;
            var (sender, receiver, source) = pair.Endpoints(type);
            var payload = pair.Payload(type);
            if (payload is TransportAccelerationAnswerPayload answer) answer.Accepted = false;
            else ((TransportAccelerationAnswerAckPayload)payload).Accepted = false;
            var read = receiver.TryReadSessionPreparationEnvelope(source,
                pair.Envelope(sender, type, JsonSerializer.SerializeToUtf8Bytes(payload)), out var message);
            Assert.Equal(type == MsgType.SessionPreparationAnswer, read);
            if (read) Assert.False(receiver.IsSessionPreparationReplyCurrent(message!, pair.Permit(receiver)));
            pair.AssertNoEffects();
            await Task.CompletedTask;
        });

    [Fact]
    [Trait("FileTransferInfraSuite", "SessionPreparationWire")]
    public Task PreparationWire_DoesNotChangeLegacyControlLiveness()
        => WithPreparationWirePairAsync(false, async pair =>
        {
            var type = MsgType.SessionPreparationAnswer;
            var (sender, receiver, source) = pair.Endpoints(type);
            Assert.True(receiver.TryReadSessionPreparationEnvelope(source, pair.Envelope(sender, type), out _));
            receiver.RouteControlEnvelope(source, pair.Envelope(sender, MsgType.TransportAccelerationAnswer,
                JsonSerializer.SerializeToUtf8Bytes(pair.Payload(type))));
            Assert.Contains("transport_acceleration_answer", pair.Liveness);
            pair.AssertNoEffects();
            await Task.CompletedTask;
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "SessionPreparationWire")]
    public Task PreparationWire_ProductionRouterFailsClosedWithoutLaneExecution(bool helperPays)
        => WithPreparationWirePairAsync(helperPays, async pair =>
        {
            var logStart = GetOperationalLogLength();
            foreach (var type in PreparationWireTypes)
            {
                var (sender, receiver, _) = pair.Endpoints(type);
                var client = ReferenceEquals(sender, pair.Host) ? pair.HostClient : pair.HelperClient;
                await client.SendAsync(receiver.LocalPeerAddress!, EnvelopeCodec.Serialize(pair.Envelope(sender, type)), pair.Token);
            }
            await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Split('\n').Count(line =>
                line.Contains("event=session_preparation_message_rejected;", StringComparison.Ordinal) &&
                line.Contains("reason=execution_unavailable; authority_effect=none", StringComparison.Ordinal)) == 3,
                TimeSpan.FromSeconds(3));
            pair.AssertNoEffects();
        });

    private static readonly MsgType[] PreparationWireTypes =
        [MsgType.SessionPreparationOffer, MsgType.SessionPreparationAnswer, MsgType.SessionPreparationAnswerAck];

    private static async Task WithPreparationWirePairAsync(bool helperPays, Func<PreparationWirePair, Task> test)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var pair = new PreparationWirePair(helperPays);
            await pair.ApproveAsync();
            await test(pair);
        }
        finally { FakeNknClient.ResetNetwork(); }
    }

    private sealed class PreparationWirePair : IDisposable
    {
        private readonly bool helperPays;
        private readonly CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(15));
        private readonly ConcurrentQueue<string> liveness = new();
        public IEnumerable<string> Liveness => liveness;
        private FileTransferSessionPreparationPermit hostPermit = null!, helperPermit = null!;
        public readonly FakeNknClient HostClient, HelperClient;
        public readonly RetryableTunaAccelerationSession HostLane, HelperLane;
        public readonly NknSignalingTransport Host, Helper;
        public readonly SessionFileTransferService HostService = new(), HelperService = new();
        public readonly string Nonce = Guid.NewGuid().ToString("N");
        public string SessionId = "";
        public DateTimeOffset Deadline;
        public CancellationToken Token => cancellation.Token;

        public PreparationWirePair(bool helperPays)
        {
            this.helperPays = helperPays;
            var identity = Guid.NewGuid().ToString("N");
            HostClient = new($"host.preparation-wire.{identity}.address");
            HelperClient = new($"helper.preparation-wire.{identity}.address");
            HostLane = new(!helperPays, 0, NknAccelerationLaneKind.File);
            HelperLane = new(helperPays, 0, NknAccelerationLaneKind.File);
            var options = NknTransportOptions.Load();
            Host = new(HostClient, options, new NknIdentity("prep-host", HostClient.Address), NknTunaAccelerationOptions.Disabled, HostLane);
            Helper = new(HelperClient, options, new NknIdentity("prep-helper", HelperClient.Address), NknTunaAccelerationOptions.Disabled, HelperLane);
            HostService.AttachTransport(Host);
            HelperService.AttachTransport(Helper);
        }

        public async Task ApproveAsync()
        {
            SessionId = await ApproveNknSessionAsync(Host, Helper, Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
            Deadline = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds());
            hostPermit = Begin(Host, !helperPays);
            helperPermit = Begin(Helper, helperPays);
            Host.SessionLivenessProofReceived += (_, e) => liveness.Enqueue(e.ProofKind);
            Helper.SessionLivenessProofReceived += (_, e) => liveness.Enqueue(e.ProofKind);
        }

        private FileTransferSessionPreparationPermit Begin(NknSignalingTransport transport, bool payer)
        {
            var lifetime = ((IFileTransferSessionPreparationTransport)transport).SessionPreparationLifecycle;
            var decision = lifetime.Attachment!.Authority.Begin(lifetime.Attachment, lifetime.Current!, Nonce, 1, payer, Deadline);
            Assert.Equal(FileTransferSessionPreparationStatus.Accepted, decision.Status);
            return decision.Permit!;
        }

        public FileTransferSessionPreparationPermit Permit(NknSignalingTransport transport)
            => ReferenceEquals(transport, Host) ? hostPermit : helperPermit;

        public (NknSignalingTransport Sender, NknSignalingTransport Receiver, string Source) Endpoints(MsgType type)
        {
            var fromHelper = type == MsgType.SessionPreparationAnswer ? !helperPays : helperPays;
            var sender = fromHelper ? Helper : Host;
            return (sender, fromHelper ? Host : Helper, sender.LocalPeerAddress!);
        }

        public object Payload(MsgType type)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var expiry = Deadline.ToUnixTimeMilliseconds();
            return type switch
            {
                MsgType.SessionPreparationOffer => new TransportAccelerationOfferPayload
                {
                    SessionId = SessionId, Nonce = Nonce, PayerDecisionId = 1, SentAtUnixMs = now,
                    ExpiresAtUnixMs = expiry, SidecarProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion,
                    SupportedLanes = ["file"], SenderRole = helperPays ? "helper" : "helpee",
                    Trigger = "session_preparation", TunaAddress = "test-listener-address",
                },
                MsgType.SessionPreparationAnswer => new TransportAccelerationAnswerPayload
                {
                    SessionId = SessionId, Nonce = Nonce, PayerDecisionId = 1, ExpiresAtUnixMs = expiry,
                    SidecarProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion,
                    SupportedLanes = ["file"], Accepted = true,
                },
                _ => new TransportAccelerationAnswerAckPayload
                {
                    SessionId = SessionId, Nonce = Nonce, PayerDecisionId = 1, ExpiresAtUnixMs = expiry,
                    SentAtUnixMs = now, SidecarProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion,
                    SupportedLanes = ["file"], Accepted = true,
                },
            };
        }

        public Envelope Envelope(NknSignalingTransport sender, MsgType type, byte[]? body = null)
        {
            var encrypted = Assert.IsType<byte[]>(InvokePrivateMethod(sender, "CreateSecureControlPayload",
                type, Nonce, body ?? JsonSerializer.SerializeToUtf8Bytes(Payload(type))));
            return new(1, Assert.IsType<string>(GetPrivateField(sender, "currentEnvelopeCode")),
                Guid.NewGuid().ToString("N"), type, encrypted, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);
        }

        public void AssertNoEffects()
        {
            foreach (var transport in new[] { Host, Helper })
            {
                Assert.False(transport.IsAccelerationAvailableForTests);
                Assert.Null(GetPrivateField(transport, "initialTunaReadinessState"));
                Assert.Equal(NknAccelerationLaneKind.None, GetPrivateField(transport, "accelerationNegotiatedLanes"));
                Assert.Null(GetPrivateField(transport, "outboundAccelerationOfferNonce"));
                Assert.Null(GetPrivateField(transport, "pendingAccelerationAnswerAckNonce"));
                Assert.Null(GetPrivateField(transport, "runtimeUnlockRecoveryRetryState"));
            }
            foreach (var service in new[] { HostService, HelperService })
            {
                Assert.Null(service.Snapshot.Inbound);
                Assert.Null(service.Snapshot.Outbound);
                Assert.Null(Assert.IsType<FileTransferSessionPreparationState>(GetPrivateField(service, "sessionPreparation")).Readiness);
            }
            Assert.Equal(0, HostLane.EnsureListenerCalls + HelperLane.EnsureListenerCalls);
            Assert.Equal(0, HostLane.StartDialerCalls + HelperLane.StartDialerCalls);
            Assert.Equal(0, HostLane.StopCalls + HelperLane.StopCalls);
            Assert.DoesNotContain(liveness, value => value.StartsWith("session_preparation", StringComparison.Ordinal));
        }

        public void Dispose()
        {
            cancellation.Cancel();
            HostService.Dispose();
            HelperService.Dispose();
            Host.Dispose();
            Helper.Dispose();
            cancellation.Dispose();
        }
    }
}
