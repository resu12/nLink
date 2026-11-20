using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Fact]
    public Task OfferDelivery_TimeoutBeforeWriterAndFailedFirstWriteLeaveNextRetryUsable()
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var oldTimeout = SessionFileTransferService.OfferSendTimeoutOverrideForTests;
            var gate = (SemaphoreSlim)GetPrivateField(pair.Payer, "outboundSendGate")!;
            int writes = 0;
            pair.PayerClient.ShouldDeliverSendAsync = (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var env) && env.Type == MsgType.FileTransferOffer &&
                    Interlocked.Increment(ref writes) == 1) throw new IOException("first admitted write failed");
                return Task.FromResult(true);
            };
            SessionFileTransferService.OfferSendTimeoutOverrideForTests = TimeSpan.FromMilliseconds(250);
            try
            {
                await gate.WaitAsync(pair.Token);
                try
                {
                    Assert.Equal(FileTransferTransferState.AwaitingAcceptance, (await StartLifetimeOffer(pair, "first.bin"))!.State);
                    Assert.Equal(0, Volatile.Read(ref writes));
                }
                finally { gate.Release(); }
                await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(7));
                Assert.Equal(2, Volatile.Read(ref writes));
                await pair.DialerService.DeclineIncomingTransferAsync("reused-id", null, pair.Token);
                await WaitUntilAsync(() => pair.PayerService.Snapshot.Outbound?.State == FileTransferTransferState.Declined, TimeSpan.FromSeconds(3));
            }
            finally { SessionFileTransferService.OfferSendTimeoutOverrideForTests = oldTimeout; }
        });

    [Fact]
    public Task OfferDelivery_LostAcceptReplaysOriginalConsentOnceWithoutRedisplay()
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            int offers = 0, accepts = 0;
            pair.Dialer.FileTransferOfferReceived += (_, _) => Interlocked.Increment(ref offers);
            pair.DialerClient.ShouldDeliverSendAsync = (_, bytes, _) => Task.FromResult(
                !EnvelopeCodec.TryDeserialize(bytes, out var env) || env.Type != MsgType.FileTransferAccept ||
                Interlocked.Increment(ref accepts) != 1);
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Dialer.FileTransferSessionOpenReceived += (_, _) => opened.TrySetResult();
            await StartLifetimeOffer(pair, "first.bin");
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            var token = pair.DialerService.Snapshot.Inbound!.InboundOfferToken;
            await pair.DialerService.AcceptIncomingTransferAsync("reused-id",
                (FileTransferWriteStreamFactory)((_, _) => Task.FromResult<Stream>(new MemoryStream())), pair.Token);
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(5), pair.Token);
            Assert.Equal(2, Volatile.Read(ref accepts));
            Assert.Equal(1, Volatile.Read(ref offers));
            Assert.Equal(token, pair.DialerService.Snapshot.Inbound!.InboundOfferToken);
        });

    [Fact]
    public Task OfferDelivery_ReplayRequiresExactOfferAndRechecksReplacementAtWriter()
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var client = Assert.IsType<WriterGatePeer>(pair.DialerClient);
            var original = ReplacementTestOffer(pair.Permit.Scope.SessionId);
            var receipt = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Dialer.FileTransferOfferReceived += (_, e) => receipt.TrySetResult(e.Message);
            Envelope? sent = null;
            pair.PayerClient.BeforeSendCoreAsync = (_, bytes, _, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.FileTransferOffer) sent = e;
                return Task.CompletedTask;
            };
            int acceptWrites = 0;
            client.ShouldDeliverSendAsync = (_, bytes, _) => Task.FromResult(
                !EnvelopeCodec.TryDeserialize(bytes, out var e) || e.Type != MsgType.FileTransferAccept ||
                Interlocked.Increment(ref acceptWrites) != 1);
            await pair.Payer.SendFileTransferOfferAsync(original, pair.Token);
            var wire = await receipt.Task.WaitAsync(pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            FileTransferWriteStreamFactory destination = (_, _) => Task.FromResult<Stream>(new MemoryStream());
            await pair.DialerService.AcceptIncomingTransferAsync(original.TransferId, destination, pair.Token);
            foreach (var mismatch in new[] { wire with { FileName = "changed.bin" }, wire with { OfferInstanceId = Guid.NewGuid() }, wire with { OfferInstanceId = null } })
            {
                var payload = Assert.IsType<byte[]>(InvokePrivateMethod(pair.Payer, "CreateSecureFileTransferPayload", MsgType.FileTransferOffer,
                    original.TransferId, FileTransferPayloadCodec.Serialize(mismatch), false));
                var packet = sent! with { MessageId = Guid.NewGuid().ToString("N"), Payload = payload };
                Assert.False(Assert.IsType<bool>(InvokePrivateMethod(pair.Dialer, "TryPrepareFileTransferOfferDispatch", pair.PayerClient.Address, packet, null)));
            }
            var writer = new PreparationHandshakeBarrier();
            client.BeforeOwnedWrite = async (type, _) =>
            {
                if (type != MsgType.FileTransferAccept) return;
                writer.Entered.TrySetResult();
                await writer.Release.Task.WaitAsync(pair.Token);
            };
            await pair.Payer.SendFileTransferOfferAsync(original, pair.Token);
            try
            {
                await writer.Entered.Task.WaitAsync(pair.Token);
                await pair.Payer.SendFileTransferCancelAsync(new() { SessionId = wire.SessionId, TransferId = wire.TransferId }, pair.Token);
                await pair.Payer.SendFileTransferOfferAsync(original with { FileName = "replacement.bin" }, pair.Token);
                await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound is { State: FileTransferTransferState.PendingDecision, FileName: "replacement.bin" }, TimeSpan.FromSeconds(3));
                Assert.False(await pair.Payer.TrySendFileTransferOfferAsync(original, pair.Token));
            }
            finally { writer.Release.TrySetResult(); }
            var gate = (SemaphoreSlim)GetPrivateField(pair.Dialer, "outboundSendGate")!;
            await gate.WaitAsync(pair.Token);
            gate.Release();
            Assert.Equal(1, Volatile.Read(ref acceptWrites));
            await pair.DialerService.AcceptIncomingTransferAsync(original.TransferId, destination, pair.Token);
            Assert.Equal(2, Volatile.Read(ref acceptWrites));
        }, useWriterGatePeer: true);

    [Fact]
    public Task OfferDelivery_LocalCancelRevokesAcceptReplayBeforeCancelIsSent()
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var writer = new PreparationHandshakeBarrier();
            var canceled = new PreparationHandshakeBarrier();
            var client = Assert.IsType<WriterGatePeer>(pair.DialerClient);
            var original = ReplacementTestOffer(pair.Permit.Scope.SessionId);
            int accepts = 0;
            client.ShouldDeliverSendAsync = (_, bytes, _) => Task.FromResult(
                !EnvelopeCodec.TryDeserialize(bytes, out var env) || env.Type != MsgType.FileTransferAccept ||
                Interlocked.Increment(ref accepts) != 1);
            await pair.Payer.SendFileTransferOfferAsync(original, pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            await pair.DialerService.AcceptIncomingTransferAsync(original.TransferId,
                (FileTransferWriteStreamFactory)((_, _) => Task.FromResult<Stream>(new MemoryStream())), pair.Token);
            client.BeforeOwnedWrite = async (type, _) =>
            {
                if (type != MsgType.FileTransferAccept) return;
                writer.Entered.TrySetResult();
                await writer.Release.Task.WaitAsync(pair.Token);
            };
            pair.DialerService.TransferChanged += (_, e) =>
            {
                if (e.Snapshot.Inbound?.State != FileTransferTransferState.Canceled || !canceled.Entered.TrySetResult()) return;
                canceled.Release.Task.Wait(pair.Token); // Local cancellation is visible, but no Cancel has been sent yet.
            };
            await pair.Payer.SendFileTransferOfferAsync(original, pair.Token);
            await writer.Entered.Task.WaitAsync(pair.Token);
            var cancel = Task.Run(() => pair.DialerService.CancelTransferAsync(original.TransferId, "user_cancel", pair.Token));
            try
            {
                await canceled.Entered.Task.WaitAsync(pair.Token);
                writer.Release.TrySetResult();
                var gate = (SemaphoreSlim)GetPrivateField(pair.Dialer, "outboundSendGate")!;
                await gate.WaitAsync(pair.Token);
                gate.Release();
                Assert.Equal(1, Volatile.Read(ref accepts)); // Only the original lost Accept.
            }
            finally { writer.Release.TrySetResult(); canceled.Release.TrySetResult(); await cancel.WaitAsync(pair.Token); }
        }, useWriterGatePeer: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OfferDelivery_LostDeclineReplaysExactDecisionWithoutRedisplay(bool firstWriteThrows)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            int offers = 0, declines = 0;
            pair.Dialer.FileTransferOfferReceived += (_, _) => Interlocked.Increment(ref offers);
            pair.DialerClient.ShouldDeliverSendAsync = (_, bytes, _) =>
            {
                if (!EnvelopeCodec.TryDeserialize(bytes, out var env) || env.Type != MsgType.FileTransferDecline ||
                    Interlocked.Increment(ref declines) != 1) return Task.FromResult(true);
                if (firstWriteThrows) throw new IOException("first decline write failed");
                return Task.FromResult(false); // The write succeeded locally, but delivery was lost.
            };
            var reply = new TaskCompletionSource<FileTransferDeclineV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Payer.FileTransferDeclineReceived += (_, e) => reply.TrySetResult(e.Message);
            await StartLifetimeOffer(pair, "first.bin");
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            var decision = await pair.DialerService.DeclineIncomingTransferAsync("reused-id", "not_now", pair.Token);
            Assert.Equal(FileTransferTransferState.Declined, decision!.State);
            var delivered = await reply.Task.WaitAsync(TimeSpan.FromSeconds(5), pair.Token);
            Assert.Equal("not_now", delivered.Reason);
            Assert.Equal((pair.Permit.Scope.SessionId, "reused-id"), (delivered.SessionId, delivered.TransferId));
            Assert.NotNull(delivered.OfferInstanceId); // The sender translates the wire nonce back to its own offer identity.
            await WaitUntilAsync(() => pair.PayerService.Snapshot.Outbound?.State == FileTransferTransferState.Declined, TimeSpan.FromSeconds(3));
            Assert.Equal(FileTransferTransferState.Declined, pair.DialerService.Snapshot.Inbound!.State);
            Assert.Equal(1, Volatile.Read(ref offers));
            Assert.Equal(2, Volatile.Read(ref declines));
        });

    [Fact]
    public Task OfferDelivery_LegacyDeclineWriteFailureFreesSlotWithoutReplayingConsent()
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var old = ReplacementTestOffer(pair.Permit.Scope.SessionId);
            Envelope LegacyOffer() => SecureOfferDeliveryPacket(pair, MsgType.FileTransferOffer, old.TransferId,
                FileTransferPayloadCodec.Serialize(old with { OfferInstanceId = null }));
            int declines = 0, offers = 0;
            pair.Dialer.FileTransferOfferReceived += (_, _) => Interlocked.Increment(ref offers);
            pair.DialerClient.ShouldDeliverSendAsync = (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var env) && env.Type == MsgType.FileTransferDecline &&
                    Interlocked.Increment(ref declines) == 1) throw new IOException("legacy decline write failed");
                return Task.FromResult(true);
            };
            await pair.PayerClient.SendAsync(pair.DialerClient.Address, EnvelopeCodec.Serialize(LegacyOffer()), pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            Assert.Equal(FileTransferTransferState.Declined, (await pair.DialerService.DeclineIncomingTransferAsync(old.TransferId, "not_now", pair.Token))!.State);
            Assert.False(Assert.IsType<bool>(InvokePrivateMethod(pair.Dialer, "TryPrepareFileTransferOfferDispatch", pair.PayerClient.Address, LegacyOffer(), null)));
            var next = old with { TransferId = "different-id", FileName = "next.bin" };
            await pair.Payer.SendFileTransferOfferAsync(next, pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound is { TransferId: "different-id", State: FileTransferTransferState.PendingDecision }, TimeSpan.FromSeconds(3));
            Assert.Equal(2, Volatile.Read(ref offers));
            Assert.Equal(1, Volatile.Read(ref declines));
        });

    [Theory]
    [InlineData(false, false, "matching", 0)]
    [InlineData(true, false, "matching", 0)]
    [InlineData(true, false, "matching_bulk", 0)]
    [InlineData(false, true, "matching", 0)]
    [InlineData(false, false, "different", 1)]
    [InlineData(false, false, "missing", 1)]
    [InlineData(false, false, "wrong_session", 1)]
    public Task OfferDelivery_CancelBeforeDeclineWriteRequiresExactOwner(bool replay, bool legacy, string cancelOwner, int additionalWrites)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var original = ReplacementTestOffer(pair.Permit.Scope.SessionId);
            Guid? wireId = legacy ? null : Guid.NewGuid();
            Envelope Offer() => SecureOfferDeliveryPacket(pair, MsgType.FileTransferOffer, original.TransferId,
                FileTransferPayloadCodec.Serialize(original with { OfferInstanceId = wireId }));
            await pair.PayerClient.SendAsync(pair.DialerClient.Address, EnvelopeCodec.Serialize(Offer()), pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            var client = Assert.IsType<WriterGatePeer>(pair.DialerClient);
            int writes = 0, cancelEvents = 0;
            client.ShouldDeliverSendAsync = (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var env) && env.Type == MsgType.FileTransferDecline)
                    Interlocked.Increment(ref writes);
                return Task.FromResult(false); // Count physical writes, without delivering to the synthetic sender.
            };
            pair.Dialer.FileTransferCancelReceived += (_, _) => Interlocked.Increment(ref cancelEvents);
            if (replay) await pair.DialerService.DeclineIncomingTransferAsync(original.TransferId, "not_now", pair.Token);
            var writer = new PreparationHandshakeBarrier();
            client.BeforeOwnedWrite = async (type, _) =>
            {
                if (type != MsgType.FileTransferDecline) return;
                writer.Entered.TrySetResult();
                await writer.Release.Task.WaitAsync(pair.Token);
            };
            Task pending = replay ? pair.PayerClient.SendAsync(pair.DialerClient.Address, EnvelopeCodec.Serialize(Offer()), pair.Token)
                : pair.DialerService.DeclineIncomingTransferAsync(original.TransferId, "not_now", pair.Token);
            try
            {
                await writer.Entered.Task.WaitAsync(pair.Token);
                var bulk = cancelOwner == "matching_bulk";
                var cancel = new FileTransferCancelV1 { SessionId = cancelOwner == "wrong_session" ? "other-session" : original.SessionId,
                    TransferId = original.TransferId, OfferInstanceId = cancelOwner switch { "different" => Guid.NewGuid(), "missing" => null, _ => wireId } };
                var packet = SecureOfferDeliveryPacket(pair, MsgType.FileTransferCancel, original.TransferId, FileTransferPayloadCodec.Serialize(cancel), bulk);
                // Complete authenticated receive admission before releasing the actual writer. No replacement is installed.
                Assert.False(Assert.IsType<bool>(InvokePrivateMethod(pair.Dialer, "TryPrepareFileTransferCancelDispatch",
                    bulk ? pair.PayerClient.BulkAddress : pair.PayerClient.Address, bulk ? NknBridgeChannel.Bulk : NknBridgeChannel.Control, packet, null)));
            }
            finally { writer.Release.TrySetResult(); }
            await pending.WaitAsync(pair.Token);
            var gate = (SemaphoreSlim)GetPrivateField(pair.Dialer, "outboundSendGate")!;
            await gate.WaitAsync(pair.Token);
            gate.Release();
            Assert.Equal((replay ? 1 : 0) + additionalWrites, Volatile.Read(ref writes));
            Assert.Equal(0, Volatile.Read(ref cancelEvents));
            Assert.Equal(FileTransferTransferState.Declined, pair.DialerService.Snapshot.Inbound!.State);
            Assert.Equal("not_now", pair.DialerService.Snapshot.Inbound.StatusMessage);
            if (!legacy && (cancelOwner == "matching" || cancelOwner == "matching_bulk"))
                Assert.False(Assert.IsType<bool>(InvokePrivateMethod(pair.Dialer, "TryPrepareFileTransferOfferDispatch",
                    pair.PayerClient.Address, Offer(), null)));
        }, useWriterGatePeer: true);

    private static Envelope SecureOfferDeliveryPacket(PreparationHandshakePair pair, MsgType type, string transferId, byte[] body, bool bulk = false)
        => new(1, Assert.IsType<string>(GetPrivateField(pair.Payer, "currentEnvelopeCode")), Guid.NewGuid().ToString("N"), type,
            Assert.IsType<byte[]>(InvokePrivateMethod(pair.Payer, "CreateSecureFileTransferPayload", type, transferId, body, bulk)),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);

    [Fact]
    public Task OfferDelivery_QueuedDeclineReplayCannotAnswerReplacement()
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var writer = new PreparationHandshakeBarrier();
            var client = Assert.IsType<WriterGatePeer>(pair.DialerClient);
            var original = ReplacementTestOffer(pair.Permit.Scope.SessionId);
            int declines = 0;
            client.ShouldDeliverSendAsync = (_, bytes, _) => Task.FromResult(
                !EnvelopeCodec.TryDeserialize(bytes, out var env) || env.Type != MsgType.FileTransferDecline ||
                Interlocked.Increment(ref declines) != 1);
            await pair.Payer.SendFileTransferOfferAsync(original, pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            await pair.DialerService.DeclineIncomingTransferAsync(original.TransferId, "not_now", pair.Token);
            client.BeforeOwnedWrite = async (type, _) =>
            {
                if (type != MsgType.FileTransferDecline) return;
                writer.Entered.TrySetResult();
                await writer.Release.Task.WaitAsync(pair.Token);
            };
            await pair.Payer.SendFileTransferOfferAsync(original, pair.Token);
            try
            {
                await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3), pair.Token);
                await pair.Payer.SendFileTransferCancelAsync(new() { SessionId = original.SessionId, TransferId = original.TransferId }, pair.Token);
                await pair.Payer.SendFileTransferOfferAsync(original with { FileName = "replacement.bin" }, pair.Token);
                await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound is { State: FileTransferTransferState.PendingDecision, FileName: "replacement.bin" }, TimeSpan.FromSeconds(3));
            }
            finally { writer.Release.TrySetResult(); }
            var gate = (SemaphoreSlim)GetPrivateField(pair.Dialer, "outboundSendGate")!;
            await gate.WaitAsync(pair.Token);
            gate.Release();
            Assert.Equal(1, Volatile.Read(ref declines));
            await pair.DialerService.DeclineIncomingTransferAsync(original.TransferId, "different_decision", pair.Token);
            Assert.Equal(2, Volatile.Read(ref declines));
        }, useWriterGatePeer: true);

    [Fact]
    public Task OfferDelivery_FailedAcceptWriteWithoutPeerProgressReportsFailureForThatOffer()
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var offer = ReplacementTestOffer(pair.Permit.Scope.SessionId) with { OfferInstanceId = Guid.NewGuid() };
            var error = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Payer.FileTransferErrorReceived += (_, e) => error.TrySetResult(e.Message.OfferInstanceId);
            pair.DialerClient.ShouldDeliverSendAsync = (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.FileTransferAccept)
                    throw new IOException("accept write failed before delivery");
                return Task.FromResult(true);
            };
            await pair.Payer.SendFileTransferOfferAsync(offer, pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            var result = await pair.DialerService.AcceptIncomingTransferAsync(offer.TransferId,
                (FileTransferWriteStreamFactory)((_, _) => Task.FromResult<Stream>(new MemoryStream())), pair.Token);
            Assert.Equal(FileTransferTransferState.Failed, result!.State);
            Assert.Equal(offer.OfferInstanceId, await error.Task.WaitAsync(pair.Token));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OfferDelivery_SessionOpenDuringAcceptWriteSurvivesCompletion(bool lateFailure)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var writer = new PreparationHandshakeBarrier();
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Dialer.FileTransferSessionOpenReceived += (_, _) => opened.TrySetResult();
            pair.DialerClient.ShouldDeliverSendAsync = async (_, bytes, _) =>
            {
                if (!EnvelopeCodec.TryDeserialize(bytes, out var env) || env.Type != MsgType.FileTransferAccept) return true;
                await Task.Yield();
                InvokePrivateMethod(pair.PayerClient, "Receive", new NknIncomingMessage(pair.DialerClient.Address, bytes, false, null));
                writer.Entered.TrySetResult();
                await writer.Release.Task.WaitAsync(pair.Token);
                if (lateFailure) throw new IOException("accept completion lost");
                return false;
            };
            var offer = ReplacementTestOffer(pair.Permit.Scope.SessionId);
            await pair.Payer.SendFileTransferOfferAsync(offer, pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            var accept = pair.DialerService.AcceptIncomingTransferAsync(offer.TransferId,
                (FileTransferWriteStreamFactory)((_, _) => Task.FromResult<Stream>(new MemoryStream())), pair.Token);
            try
            {
                await writer.Entered.Task.WaitAsync(pair.Token);
                await pair.Payer.SendFileTransferSessionOpenAsync(new() { SessionId = offer.SessionId, TransferId = offer.TransferId,
                    ProtocolVersion = 4, SessionRole = FileTransferProtocol.SessionRoleSender,
                    FileTransferRoute = offer.FileTransferRoute, ChunkSizeBytes = 1024, InitialPipelineDepth = 8 }, pair.Token);
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(3), pair.Token);
                Assert.False(accept.IsCompleted);
            }
            finally { writer.Release.TrySetResult(); }
            Assert.NotEqual(FileTransferTransferState.Failed, (await accept.WaitAsync(pair.Token))!.State);
        });

    [Fact]
    public Task OfferDelivery_DelayedOutboundCancelDoesNotCloseSuccessorRetry()
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var old = new PreparationHandshakeBarrier();
            var type = typeof(NknSignalingTransport).GetNestedType("RuntimeUnlockRecoveryRetryState", System.Reflection.BindingFlags.NonPublic)!;
            object InstallRetry()
            {
                var state = Activator.CreateInstance(type)!;
                type.GetProperty("SessionId")!.SetValue(state, pair.Permit.Scope.SessionId);
                type.GetProperty("TransferId")!.SetValue(state, "reused-id");
                type.GetProperty("PendingDispatchAuthorityRequest")!.SetValue(state,
                    default(RuntimeUnlockRetryDispatchAuthorityRequest) with { Direction = FileTransferDirection.Outbound });
                SetPrivateField(pair.Payer, "runtimeUnlockRecoveryRetryState", state);
                return state;
            }
            string? Status(object state) => type.GetProperty("ContractState")!.GetValue(state)?.ToString();
            await StartLifetimeOffer(pair, "first.bin");
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            InstallRetry();
            pair.PayerService.TransferChanged += (_, e) =>
            {
                if (e.Snapshot.Outbound is not { FileName: "first.bin", State: FileTransferTransferState.Canceled } || !old.Entered.TrySetResult()) return;
                old.Release.Task.Wait(pair.Token);
            };
            var cancel = Task.Run(() => pair.PayerService.CancelTransferAsync("reused-id", "user_cancel", pair.Token));
            object successor;
            try
            {
                await old.Entered.Task.WaitAsync(pair.Token);
                await pair.DialerService.CancelTransferAsync("reused-id", "user_cancel", pair.Token);
                await StartLifetimeOffer(pair, "replacement.bin");
                await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound is { FileName: "replacement.bin", State: FileTransferTransferState.PendingDecision }, TimeSpan.FromSeconds(3));
                successor = InstallRetry();
            }
            finally { old.Release.TrySetResult(); }
            await cancel.WaitAsync(pair.Token);
            Assert.Equal("RecoveryPending", Status(successor));
            await pair.PayerService.CancelTransferAsync("reused-id", "user_cancel", pair.Token);
            Assert.Equal("Completed", Status(successor));
        });
}
