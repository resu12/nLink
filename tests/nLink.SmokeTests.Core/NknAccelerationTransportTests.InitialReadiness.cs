using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    private static FileTransferOfferV2 ReplacementTestOffer(string sessionId) => new()
    {
        SessionId = sessionId, TransferId = "reused-id", FileName = "old.bin", FileSizeBytes = 4,
        PreferredDataProtocolVersion = 4, FileTransferRoute = FileTransferRouteResolver.RegularNknV4FastToken
    };

    private static Task<FileTransferTransferSnapshot?> StartLifetimeOffer(PreparationHandshakePair pair, string name) =>
        pair.PayerService.TryStartSendAsync(new(name, 4, "reused-id"),
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[4])), pair.Token);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TransferLifetime_ServiceRetryKeepsOriginalOffer(bool legacyReply)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var retry = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            int sends = 0;
            pair.PayerClient.ShouldDeliverSendAsync = (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.FileTransferOffer &&
                    Interlocked.Increment(ref sends) == 2)
                {
                    retry.TrySetResult(e);
                    return Task.FromResult(legacyReply); // Hold one fresh retry packet until after the original decision.
                }
                return Task.FromResult(true);
            };
            await StartLifetimeOffer(pair, "first.bin");
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            var token = pair.DialerService.Snapshot.Inbound!.InboundOfferToken;
            var packet = await retry.Task.WaitAsync(pair.Token); // Actual timer retransmission, not a second service offer.
            if (legacyReply)
            {
                var payload = FileTransferPayloadCodec.Serialize(new FileTransferDeclineV1 { SessionId = pair.Permit.Scope.SessionId, TransferId = "reused-id" });
                var encrypted = Assert.IsType<byte[]>(InvokePrivateMethod(pair.Dialer, "CreateSecureFileTransferPayload",
                    MsgType.FileTransferDecline, "reused-id", payload, false));
                await pair.DialerClient.SendAsync(pair.PayerClient.Address, EnvelopeCodec.Serialize(packet with {
                    Type = MsgType.FileTransferDecline, MessageId = Guid.NewGuid().ToString("N"), Payload = encrypted }), pair.Token);
            }
            else await pair.DialerService.DeclineIncomingTransferAsync("reused-id", null, pair.Token);
            await WaitUntilAsync(() => pair.PayerService.Snapshot.Outbound?.State == FileTransferTransferState.Declined, TimeSpan.FromSeconds(3));
            Assert.Equal(token, pair.DialerService.Snapshot.Inbound!.InboundOfferToken);
            if (!legacyReply)
            {
                await pair.PayerClient.SendAsync(pair.DialerClient.Address, EnvelopeCodec.Serialize(packet), pair.Token);
                await StartLifetimeOffer(pair, "replacement.bin");
                await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound is { FileName: "replacement.bin", State: FileTransferTransferState.PendingDecision }, TimeSpan.FromSeconds(3));
            }
        });

    [Fact]
    public Task TransferLifetime_TimedOutOfferAndFailedRetryDoNotLoseNextRetry()
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var oldTimeout = SessionFileTransferService.OfferSendTimeoutOverrideForTests;
            SessionFileTransferService.OfferSendTimeoutOverrideForTests = TimeSpan.FromMilliseconds(350);
            try
            {
                int writes = 0;
                pair.PayerClient.ShouldDeliverSendAsync = async (_, bytes, ct) =>
                {
                    if (!EnvelopeCodec.TryDeserialize(bytes, out var e) || e.Type != MsgType.FileTransferOffer) return true;
                    switch (Interlocked.Increment(ref writes))
                    {
                        case 1: await Task.Delay(Timeout.Infinite, ct); break;
                        case 2: throw new IOException("fixture retry send failed");
                    }
                    return true;
                };
                Assert.Equal(FileTransferTransferState.AwaitingAcceptance, (await StartLifetimeOffer(pair, "first.bin"))!.State);
                await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(7));
                Assert.Equal(3, Volatile.Read(ref writes));
                await pair.DialerService.DeclineIncomingTransferAsync("reused-id", null, pair.Token);
                await WaitUntilAsync(() => pair.PayerService.Snapshot.Outbound?.State == FileTransferTransferState.Declined, TimeSpan.FromSeconds(3));
            }
            finally { SessionFileTransferService.OfferSendTimeoutOverrideForTests = oldTimeout; }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TransferLifetime_ResponseBeforeOfferSendCompletes(bool failWrite)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var writer = new PreparationHandshakeBarrier();
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Payer.FileTransferAcceptReceived += (_, _) => accepted.TrySetResult();
            pair.PayerClient.ShouldDeliverSendAsync = async (_, bytes, _) =>
            {
                if (!EnvelopeCodec.TryDeserialize(bytes, out var e) || e.Type != MsgType.FileTransferOffer) return true;
                await Task.Yield();
                InvokePrivateMethod(pair.DialerClient, "Receive", new NknIncomingMessage(pair.PayerClient.Address, bytes, false, null));
                writer.Entered.TrySetResult();
                await writer.Release.Task.WaitAsync(pair.Token);
                if (failWrite) throw new IOException("lost send completion");
                return false; // The real encrypted envelope was already delivered above.
            };
            var offer = StartLifetimeOffer(pair, "first.bin");
            try
            {
                await writer.Entered.Task.WaitAsync(pair.Token);
                await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
                await pair.DialerService.AcceptIncomingTransferAsync("reused-id",
                    (FileTransferWriteStreamFactory)((_, _) => Task.FromResult<Stream>(new MemoryStream())), pair.Token);
                await accepted.Task.WaitAsync(pair.Token);
                Assert.False(offer.IsCompleted);
            }
            finally { writer.Release.TrySetResult(); }
            Assert.NotEqual(FileTransferTransferState.Failed, (await offer.WaitAsync(pair.Token))!.State);
        });

    [Fact]
    public Task TransferLifetime_ServiceCancelRetriesDoNotCancelReplacement()
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            await StartLifetimeOffer(pair, "first.bin");
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            await pair.PayerService.CancelTransferAsync("reused-id", "user_cancel", pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.Canceled, TimeSpan.FromSeconds(3));
            var retries = Assert.IsAssignableFrom<Task>(pair.PayerService.LastCancelControlRetryForTests);
            await StartLifetimeOffer(pair, "replacement.bin");
            await retries.WaitAsync(pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound is { FileName: "replacement.bin", State: FileTransferTransferState.PendingDecision }, TimeSpan.FromSeconds(3));
            Assert.Equal(FileTransferTransferState.AwaitingAcceptance, pair.PayerService.Snapshot.Outbound!.State);
            await pair.DialerService.DeclineIncomingTransferAsync("reused-id", null, pair.Token);
            await WaitUntilAsync(() => pair.PayerService.Snapshot.Outbound?.State == FileTransferTransferState.Declined, TimeSpan.FromSeconds(3));
        });

    [Fact]
    public Task TransferLifetime_StaleErrorCannotFailAcceptedReplacement()
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var original = ReplacementTestOffer(pair.Permit.Scope.SessionId) with { OfferInstanceId = Guid.NewGuid() };
            var error = new FileTransferErrorV1 { SessionId = original.SessionId, TransferId = original.TransferId, ErrorCode = "old_error" };
            Envelope? offered = null;
            byte[]? lateBulk = null;
            pair.PayerClient.ShouldDeliverSendAsync = (to, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var e))
                {
                    if (e.Type == MsgType.FileTransferOffer) offered = e;
                    if (e.Type == MsgType.FileTransferError && to == pair.DialerClient.BulkAddress && lateBulk is null)
                    {
                        lateBulk = bytes;
                        return Task.FromResult(false);
                    }
                }
                return Task.FromResult(true);
            };
            await pair.Payer.SendFileTransferOfferAsync(original, pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            await pair.Payer.SendFileTransferErrorAsync(error, pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.Failed, TimeSpan.FromSeconds(3));
            await pair.Payer.SendFileTransferOfferAsync(original with { FileName = "replacement.bin", OfferInstanceId = Guid.NewGuid() }, pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.FileName == "replacement.bin", TimeSpan.FromSeconds(3));
            await pair.DialerService.AcceptIncomingTransferAsync(original.TransferId,
                (FileTransferWriteStreamFactory)((_, _) => Task.FromResult<Stream>(new MemoryStream())), pair.Token);
            var reported = new System.Collections.Concurrent.ConcurrentQueue<string>();
            pair.Dialer.FileTransferErrorReceived += (_, e) => reported.Enqueue(e.Message.ErrorCode);
            await pair.PayerClient.SendBulkAsync(pair.DialerClient.BulkAddress, Assert.IsType<byte[]>(lateBulk), pair.Token);
            var encrypted = Assert.IsType<byte[]>(InvokePrivateMethod(pair.Payer, "CreateSecureFileTransferPayload", MsgType.FileTransferError,
                original.TransferId, FileTransferPayloadCodec.Serialize(error), false));
            await pair.PayerClient.SendAsync(pair.DialerClient.Address, EnvelopeCodec.Serialize(offered! with {
                Type = MsgType.FileTransferError, Payload = encrypted, MessageId = Guid.NewGuid().ToString("N") }), pair.Token);
            foreach (var frame in new FileTransferDataFrame[] {
                new FileTransferCancelFrameV4 { SessionId = original.SessionId, TransferId = original.TransferId, Reason = "old_cancel" },
                new FileTransferErrorFrameV4 { SessionId = original.SessionId, TransferId = original.TransferId, ErrorCode = "old_error" } })
            {
                var data = Assert.IsType<byte[]>(InvokePrivateMethod(pair.Payer, "CreateSecureFileTransferPayload", MsgType.FileTransferDataFrame,
                    original.TransferId, FileTransferDataFrameCodec.SerializeLegacyV4(frame), true));
                await pair.PayerClient.SendBulkAsync(pair.DialerClient.BulkAddress, EnvelopeCodec.Serialize(offered! with {
                    Type = MsgType.FileTransferDataFrame, Payload = data, MessageId = Guid.NewGuid().ToString("N") }), pair.Token);
            }
            await pair.Payer.SendFileTransferErrorAsync(error with { ErrorCode = "current_error" }, pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.Failed, TimeSpan.FromSeconds(3));
            Assert.Equal("current_error", Assert.Single(reported));
            Assert.Equal("current_error", pair.DialerService.Snapshot.Inbound!.ErrorCode);
        });

    [Theory]
    [InlineData("accept", "click")]
    [InlineData("save-as", "click")]
    [InlineData("decline", "click")]
    [InlineData("accept", "queued")]
    [InlineData("decline", "queued")]
    [InlineData("accept", "submitted")]
    [InlineData("decline", "submitted")]
    public Task ManualDecision_BelongsToTheDisplayedOffer(string decision, string stage)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var service = pair.DialerService;
            var a = ReplacementTestOffer(pair.Permit.Scope.SessionId) with { OfferInstanceId = Guid.NewGuid() };
            var replacementId = Guid.NewGuid();
            var delivered = new System.Collections.Concurrent.ConcurrentQueue<Guid?>();
            pair.Payer.FileTransferAcceptReceived += (_, e) => delivered.Enqueue(e.Message.OfferInstanceId);
            pair.Payer.FileTransferDeclineReceived += (_, e) => delivered.Enqueue(e.Message.OfferInstanceId);
            await pair.Payer.SendFileTransferOfferAsync(a, pair.Token);
            await WaitUntilAsync(() => service.Snapshot.Inbound is { FileName: "old.bin", State: FileTransferTransferState.PendingDecision }, TimeSpan.FromSeconds(3));
            var old = service.Snapshot.Inbound!;
            var terminal = new PreparationHandshakeBarrier();
            var writing = new PreparationHandshakeBarrier();
            var receipt = new TaskCompletionSource<FileTransferOfferReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Dialer.FileTransferOfferReceived += (_, e) => receipt.TrySetResult(e);
            service.InboundDispatchBeforeWorkAsyncForTests = (lane, op) =>
            {
                if (lane != "lifecycle_priority" || op != "cancel" || stage != "click") return Task.CompletedTask;
                terminal.Entered.TrySetResult();
                return terminal.Release.Task;
            };
            int writes = 0;
            pair.DialerClient.ShouldDeliverSendAsync = async (_, bytes, _) =>
            {
                if (!EnvelopeCodec.TryDeserialize(bytes, out var e) || e.Type is not (MsgType.FileTransferAccept or MsgType.FileTransferDecline)) return true;
                if (Interlocked.Increment(ref writes) == 1 && stage == "submitted")
                {
                    writing.Entered.TrySetResult();
                    await writing.Release.Task;
                    return true; // Deliver A's actual authenticated response after B was offered.
                }
                return true;
            };
            FileTransferWriteDestinationFactory destination = (_, _) => throw new InvalidOperationException("No payload in this fixture.");
            Task<FileTransferTransferSnapshot?> Answer(FileTransferTransferSnapshot displayed) => decision switch
            {
                "accept" => service.AcceptIncomingTransferAsync(displayed.TransferId, destination, pair.Token),
                "save-as" => service.AcceptIncomingTransferForOfferAsync((displayed.SessionId, displayed.InboundOfferToken), displayed.TransferId, destination, pair.Token),
                _ => service.DeclineIncomingTransferAsync(displayed.TransferId, null, pair.Token)
            };
            var writer = (SemaphoreSlim)GetPrivateField(pair.Dialer, "outboundSendGate")!;
            if (stage == "queued") await writer.WaitAsync(pair.Token);
            Task<FileTransferTransferSnapshot?>? outstanding = null;
            try
            {
                if (stage != "click")
                {
                    outstanding = Answer(old);
                    if (stage == "queued") await WaitUntilAsync(() => GetPrivateField(writer, "m_asyncHead") is Task, TimeSpan.FromSeconds(3));
                    else await writing.Entered.Task.WaitAsync(pair.Token);
                }
                await pair.Payer.SendFileTransferCancelAsync(new() { SessionId = a.SessionId, TransferId = a.TransferId, Reason = "user_cancel" }, pair.Token);
                if (stage == "click") await terminal.Entered.Task.WaitAsync(pair.Token);
                await pair.Payer.SendFileTransferOfferAsync(a with { FileName = "replacement.bin", FileSizeBytes = 9, OfferInstanceId = replacementId }, pair.Token);
                Assert.True((await receipt.Task.WaitAsync(pair.Token)).IsCurrentOffer!(true));
                if (stage == "click") Assert.Null(await Answer(old));
            }
            finally
            {
                terminal.Release.TrySetResult();
                writing.Release.TrySetResult();
                if (stage == "queued") writer.Release();
            }
            if (outstanding is not null) await outstanding.WaitAsync(pair.Token);
            await ((Task)GetPrivateField(service, "inboundLifecycleTail")!).WaitAsync(pair.Token);
            int previouslySubmitted = stage == "submitted" ? 1 : 0;
            Assert.Equal(previouslySubmitted, Volatile.Read(ref writes));
            Assert.True((await receipt.Task).IsCurrentOffer!(true));
            var current = service.Snapshot.Inbound!;
            Assert.Equal(("replacement.bin", FileTransferTransferState.PendingDecision), (current.FileName, current.State));
            Assert.NotEqual(old.InboundOfferToken, current.InboundOfferToken);
            Assert.Empty(delivered);
            Assert.Null(await service.AcceptIncomingTransferForOfferAsync((old.SessionId, old.InboundOfferToken), old.TransferId, destination, pair.Token));
            Assert.Null(await service.DeclineIncomingTransferForOfferAsync((old.SessionId, old.InboundOfferToken), old.TransferId, null, pair.Token));
            Assert.Equal(previouslySubmitted, Volatile.Read(ref writes));
            var answered = await Answer(current);
            Assert.Equal(decision == "decline" ? FileTransferTransferState.Declined : FileTransferTransferState.AwaitingMetadata, answered!.State);
            Assert.Equal(previouslySubmitted + 1, Volatile.Read(ref writes));
            await WaitUntilAsync(() => !delivered.IsEmpty, TimeSpan.FromSeconds(3));
            Assert.Equal(replacementId, Assert.Single(delivered));
        });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task ManualDecision_RechecksAtBridgeWriter(bool decline, bool replace)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var service = pair.DialerService;
            var client = Assert.IsType<WriterGatePeer>(pair.DialerClient);
            var old = ReplacementTestOffer(pair.Permit.Scope.SessionId);
            await pair.Payer.SendFileTransferOfferAsync(old, pair.Token);
            await WaitUntilAsync(() => service.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            using var bytes = new MemoryStream();
            using var bridgeWriter = new BridgeStdioWriter(bytes, leaveOpen: true);
            client.Writer = bridgeWriter;
            var gate = (SemaphoreSlim)GetPrivateField(bridgeWriter, "gate")!;
            var receipt = new TaskCompletionSource<FileTransferOfferReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Dialer.FileTransferOfferReceived += (_, e) => receipt.TrySetResult(e);
            await gate.WaitAsync(pair.Token);
            Task reply;
            try
            {
                reply = decline ? service.DeclineIncomingTransferAsync(old.TransferId, null, pair.Token)
                    : service.AcceptIncomingTransferAsync(old.TransferId, (FileTransferWriteDestinationFactory)((_, _) =>
                        throw new InvalidOperationException("No payload expected.")), pair.Token);
                await client.Arrived.Task.WaitAsync(pair.Token);
                if (replace)
                {
                    await pair.Payer.SendFileTransferErrorAsync(new() { SessionId = old.SessionId, TransferId = old.TransferId, ErrorCode = "io_error" }, pair.Token);
                    await pair.Payer.SendFileTransferOfferAsync(old with { FileName = "replacement.bin" }, pair.Token);
                    await receipt.Task.WaitAsync(pair.Token);
                }
            }
            finally { gate.Release(); }
            await reply.WaitAsync(pair.Token);
            Assert.Equal(!replace, await client.WriteCompleted.Task.WaitAsync(pair.Token));
            Assert.Equal(!replace, bytes.Length > 0);
            if (replace)
            {
                await ((Task)GetPrivateField(service, "inboundLifecycleTail")!).WaitAsync(pair.Token);
                Assert.True((await receipt.Task).IsCurrentOffer!(true));
                Assert.True(service.Snapshot.Inbound is { FileName: "replacement.bin", State: FileTransferTransferState.PendingDecision });
            }
        }, useWriterGatePeer: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ManualDecision_LegacyReplyIsUnambiguousOnlyOnFirstUse(bool replace)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var a = ReplacementTestOffer(pair.Permit.Scope.SessionId) with { OfferInstanceId = Guid.NewGuid() };
            var delivered = new System.Collections.Concurrent.ConcurrentQueue<Guid?>();
            pair.Payer.FileTransferDeclineReceived += (_, e) => delivered.Enqueue(e.Message.OfferInstanceId);
            Envelope? offered = null;
            pair.PayerClient.ShouldDeliverSendAsync = (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var env) && env.Type == MsgType.FileTransferOffer) offered = env;
                return Task.FromResult(true);
            };
            await pair.Payer.SendFileTransferOfferAsync(a, pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.FileName == a.FileName, TimeSpan.FromSeconds(3));
            // The old peer's encrypted response contains session + transfer ID, but no offer instance.
            var payload = FileTransferPayloadCodec.Serialize(new FileTransferDeclineV1 { SessionId = a.SessionId, TransferId = a.TransferId });
            var encrypted = Assert.IsType<byte[]>(InvokePrivateMethod(pair.Dialer, "CreateSecureFileTransferPayload",
                MsgType.FileTransferDecline, a.TransferId, payload, false));
            var packet = EnvelopeCodec.Serialize(offered! with { Type = MsgType.FileTransferDecline, MessageId = Guid.NewGuid().ToString("N"), Payload = encrypted });
            var expectedId = a.OfferInstanceId;
            if (replace)
            {
                await pair.Payer.SendFileTransferCancelAsync(new() { SessionId = a.SessionId, TransferId = a.TransferId }, pair.Token);
                expectedId = Guid.NewGuid();
                await pair.Payer.SendFileTransferOfferAsync(a with { FileName = "replacement.bin", OfferInstanceId = expectedId }, pair.Token);
                await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.FileName == "replacement.bin", TimeSpan.FromSeconds(3));
            }
            await pair.DialerClient.SendAsync(pair.PayerClient.Address, packet, pair.Token);
            if (replace)
            {
                Assert.Empty(delivered);
                await pair.DialerService.DeclineIncomingTransferAsync(a.TransferId, null, pair.Token);
            }
            await WaitUntilAsync(() => !delivered.IsEmpty, TimeSpan.FromSeconds(3));
            Assert.Equal(expectedId, Assert.Single(delivered));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ManualDecision_FailedAcceptErrorCopiesBelongToOriginalOffer(bool replace)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var service = pair.DialerService;
            var client = Assert.IsType<WriterGatePeer>(pair.DialerClient);
            var a = ReplacementTestOffer(pair.Permit.Scope.SessionId) with { OfferInstanceId = Guid.NewGuid() };
            await pair.Payer.SendFileTransferOfferAsync(a, pair.Token);
            await WaitUntilAsync(() => service.Snapshot.Inbound?.FileName == a.FileName, TimeSpan.FromSeconds(3));
            var copies = new System.Collections.Concurrent.ConcurrentQueue<NknBridgeChannel>();
            var error = new PreparationHandshakeBarrier();
            int writes = 0;
            var delivered = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Payer.FileTransferErrorReceived += (_, e) => delivered.TrySetResult(e.Message.OfferInstanceId);
            client.BeforeOwnedWrite = async (type, channel) =>
            {
                if (type == MsgType.FileTransferAccept) throw new IOException("fixture accept write failed");
                if (type != MsgType.FileTransferError) return;
                copies.Enqueue(channel);
                if (copies.Count == 2) error.Entered.TrySetResult();
                await error.Release.Task.WaitAsync(pair.Token);
            };
            client.ShouldDeliverSendAsync = (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.FileTransferError) Interlocked.Increment(ref writes);
                return Task.FromResult(true);
            };
            var accept = service.AcceptIncomingTransferAsync(a.TransferId, (FileTransferWriteDestinationFactory)((_, _) =>
                throw new InvalidOperationException("no payload expected")), pair.Token);
            try
            {
                await error.Entered.Task.WaitAsync(pair.Token);
                Assert.Contains(NknBridgeChannel.Control, copies);
                Assert.Contains(NknBridgeChannel.Bulk, copies);
                Assert.Equal(FileTransferTransferState.Failed, service.Snapshot.Inbound!.State);
                if (replace)
                {
                    await pair.Payer.SendFileTransferCancelAsync(new() { SessionId = a.SessionId, TransferId = a.TransferId, Reason = "user_cancel" }, pair.Token);
                    await pair.Payer.SendFileTransferOfferAsync(a with { FileName = "replacement.bin", OfferInstanceId = Guid.NewGuid() }, pair.Token);
                    await WaitUntilAsync(() => service.Snapshot.Inbound?.FileName == "replacement.bin", TimeSpan.FromSeconds(3));
                }
            }
            finally { error.Release.TrySetResult(); }
            await accept.WaitAsync(pair.Token);
            Assert.Equal(replace ? 0 : 2, Volatile.Read(ref writes));
            if (!replace) Assert.Equal(a.OfferInstanceId, await delivered.Task.WaitAsync(pair.Token));
            else
            {
                Assert.False(delivered.Task.IsCompleted);
                Assert.Equal(FileTransferTransferState.PendingDecision, service.Snapshot.Inbound!.State);
                client.BeforeOwnedWrite = null;
                Assert.Equal(FileTransferTransferState.Declined, (await service.DeclineIncomingTransferAsync(a.TransferId, null, pair.Token))!.State);
            }
        }, useWriterGatePeer: true);

    [Fact]
    public Task ManualDecision_LateDeclineCleanupDoesNotRetireReplacementRetry()
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var service = pair.DialerService;
            var a = ReplacementTestOffer(pair.Permit.Scope.SessionId);
            await pair.Payer.SendFileTransferOfferAsync(a, pair.Token);
            await WaitUntilAsync(() => service.Snapshot.Inbound?.FileName == a.FileName, TimeSpan.FromSeconds(3));
            var type = typeof(NknSignalingTransport).GetNestedType("RuntimeUnlockRecoveryRetryState", System.Reflection.BindingFlags.NonPublic)!;
            object InstallRetry()
            {
                var state = Activator.CreateInstance(type)!;
                type.GetProperty("SessionId")!.SetValue(state, a.SessionId);
                type.GetProperty("TransferId")!.SetValue(state, a.TransferId);
                type.GetProperty("PendingDispatchAuthorityRequest")!.SetValue(state,
                    default(RuntimeUnlockRetryDispatchAuthorityRequest) with { Direction = FileTransferDirection.Inbound });
                SetPrivateField(pair.Dialer, "runtimeUnlockRecoveryRetryState", state);
                return state;
            }
            string? Status(object state) => type.GetProperty("ContractState")!.GetValue(state)?.ToString();
            var old = InstallRetry();
            var writer = new PreparationHandshakeBarrier();
            pair.DialerClient.ShouldDeliverSendAsync = async (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.FileTransferDecline)
                {
                    writer.Entered.TrySetResult();
                    await writer.Release.Task.WaitAsync(pair.Token);
                }
                return true;
            };
            var decline = service.DeclineIncomingTransferAsync(a.TransferId, null, pair.Token);
            object next;
            try
            {
                await writer.Entered.Task.WaitAsync(pair.Token);
                Assert.Equal("Completed", Status(old));
                await pair.Payer.SendFileTransferCancelAsync(new() { SessionId = a.SessionId, TransferId = a.TransferId, Reason = "user_cancel" }, pair.Token);
                await pair.Payer.SendFileTransferOfferAsync(a with { FileName = "replacement.bin" }, pair.Token);
                await WaitUntilAsync(() => service.Snapshot.Inbound?.FileName == "replacement.bin", TimeSpan.FromSeconds(3));
                next = InstallRetry();
            }
            finally { writer.Release.TrySetResult(); }
            await decline.WaitAsync(pair.Token);
            Assert.Equal("RecoveryPending", Status(next));
            await service.DeclineIncomingTransferAsync(a.TransferId, null, pair.Token);
            Assert.Equal("Completed", Status(next));
        });

    [Theory]
    [InlineData("cancel", true, false)]
    [InlineData("cancel", false, false)]
    [InlineData("error", true, false)]
    [InlineData("error", false, false)]
    [InlineData("cancel", true, true)]
    public Task AdmissionAfterDisplayedOffer_WaitsForItsTerminal(string operation, bool reuseId, bool withdrawReplacement)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var service = pair.DialerService;
            var old = ReplacementTestOffer(pair.Permit.Scope.SessionId);
            await pair.Payer.SendFileTransferOfferAsync(old, pair.Token);
            await WaitUntilAsync(() => service.Snapshot.Inbound is { FileName: "old.bin", State: FileTransferTransferState.PendingDecision },
                TimeSpan.FromSeconds(3));
            await ((Task)GetPrivateField(service, "inboundLifecycleTail")!).WaitAsync(pair.Token);

            var terminal = new PreparationHandshakeBarrier();
            var replacementAccountedFor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacementWithdrawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.InboundDispatchBeforeWorkAsyncForTests = (lane, op) =>
            {
                if (lane != "lifecycle_priority" || op != operation) return Task.CompletedTask;
                terminal.Entered.TrySetResult();
                return terminal.Release.Task;
            };
            // Do not release the old worker merely because the replacement reached the transport.
            // Wait until Core actually waits for it (fixed), or finishes processing B prematurely (old).
            service.InboundOfferWaitingForPeerTerminalForTests = () => replacementAccountedFor.TrySetResult();
            service.InboundDispatchAfterWorkForTests = (lane, op) =>
            {
                if (lane == "lifecycle" && op == "offer") replacementAccountedFor.TrySetResult();
                if (lane == "lifecycle_priority" && op == "error") replacementWithdrawn.TrySetResult();
            };
            var receipt = new TaskCompletionSource<FileTransferOfferReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Dialer.FileTransferOfferReceived += (_, e) =>
            {
                if (!withdrawReplacement || e.Message.FileName == "third.bin") receipt.TrySetResult(e);
            };
            var states = new System.Collections.Concurrent.ConcurrentQueue<(string, FileTransferTransferState)>();
            service.TransferChanged += (_, e) => { if (e.Snapshot.Inbound is { } file) states.Enqueue((file.FileName, file.State)); };
            var next = old with { TransferId = reuseId ? old.TransferId : "next-id", FileName = "replacement.bin", FileSizeBytes = 9 };
            try
            {
                if (operation == "cancel") await pair.Payer.SendFileTransferCancelAsync(
                    new() { SessionId = old.SessionId, TransferId = old.TransferId, Reason = "user_cancel" }, pair.Token);
                else await pair.Payer.SendFileTransferErrorAsync(
                    new() { SessionId = old.SessionId, TransferId = old.TransferId, ErrorCode = "io_error" }, pair.Token);
                await terminal.Entered.Task.WaitAsync(pair.Token);
                await pair.Payer.SendFileTransferOfferAsync(next, pair.Token);
                await replacementAccountedFor.Task.WaitAsync(pair.Token);
                if (withdrawReplacement)
                {
                    await pair.Payer.SendFileTransferErrorAsync(new() { SessionId = next.SessionId, TransferId = next.TransferId, ErrorCode = "io_error" }, pair.Token);
                    await replacementWithdrawn.Task.WaitAsync(pair.Token);
                    Assert.Equal((old.FileName, FileTransferTransferState.PendingDecision), (service.Snapshot.Inbound!.FileName, service.Snapshot.Inbound.State));
                    next = next with { FileName = "third.bin" };
                    await pair.Payer.SendFileTransferOfferAsync(next, pair.Token);
                }
                await receipt.Task.WaitAsync(pair.Token);
            }
            finally { terminal.Release.TrySetResult(); }
            await ((Task)GetPrivateField(service, "inboundLifecycleTail")!).WaitAsync(pair.Token);
            Assert.True((await receipt.Task.WaitAsync(pair.Token)).IsCurrentOffer!(true));
            Assert.Equal((next.FileName, FileTransferTransferState.PendingDecision), (service.Snapshot.Inbound!.FileName, service.Snapshot.Inbound.State));
            var published = states.ToList();
            if (withdrawReplacement) Assert.DoesNotContain(published, entry => entry.Item1 == "replacement.bin");
            int retired = published.IndexOf((old.FileName, operation == "cancel" ? FileTransferTransferState.Canceled : FileTransferTransferState.Failed));
            Assert.True(retired >= 0 && published.IndexOf((next.FileName, FileTransferTransferState.PendingDecision)) > retired);
        });

    [Theory]
    [InlineData("cancel")]
    [InlineData("error")]
    public async Task DevLocalTerminal_RetiresOnlyTheQueuedOffer(string operation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var transport = new NLink.Infra.DevLocal.DevLocalTransport();
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var gate = new PreparationHandshakeBarrier();
        var old = ReplacementTestOffer("devlocal-terminal");
        var terminalDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var displayed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        service.TransferChanged += (_, e) => { if (e.Snapshot.Inbound is { State: FileTransferTransferState.PendingDecision } file) displayed.Enqueue(file.FileName); };
        service.InboundDispatchBeforeWorkAsyncForTests = (lane, op) =>
        {
            if (lane != "lifecycle" || op != "offer") return Task.CompletedTask;
            gate.Entered.TrySetResult();
            return gate.Release.Task;
        };
        service.InboundDispatchAfterWorkForTests = (lane, op) => { if (lane == "lifecycle_priority" && op == operation) terminalDone.TrySetResult(); };
        // Exercise the real DevLocal event-to-Core boundary, without starting a loopback connection.
        try
        {
            InvokePrivateMethod(transport, "SafeRaiseFileTransferOfferReceived", old);
            await gate.Entered.Task.WaitAsync(timeout.Token);
            if (operation == "cancel") InvokePrivateMethod(transport, "SafeRaiseFileTransferCancelReceived",
                new FileTransferCancelV1 { SessionId = old.SessionId, TransferId = old.TransferId, Reason = "user_cancel" });
            else InvokePrivateMethod(transport, "SafeRaiseFileTransferErrorReceived",
                new FileTransferErrorV1 { SessionId = old.SessionId, TransferId = old.TransferId, ErrorCode = "io_error" });
            await terminalDone.Task.WaitAsync(timeout.Token);
            InvokePrivateMethod(transport, "SafeRaiseFileTransferOfferReceived", old with { FileName = "replacement.bin", FileSizeBytes = 9 });
        }
        finally { gate.Release.TrySetResult(); }
        await ((Task)GetPrivateField(service, "inboundLifecycleTail")!).WaitAsync(timeout.Token);
        Assert.DoesNotContain("old.bin", displayed);
        Assert.Contains("replacement.bin", displayed);
        Assert.True(service.Snapshot.Inbound is { FileName: "replacement.bin", FileSizeBytes: 9, State: FileTransferTransferState.PendingDecision });
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("error")]
    public Task PriorityTerminal_CannotRetireReplacementOffer(string operation)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var service = pair.DialerService;
            var offer = ReplacementTestOffer(pair.Permit.Scope.SessionId);
            var oldReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var offerGate = new PreparationHandshakeBarrier();
            var terminalGate = new PreparationHandshakeBarrier();
            var terminalDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Dialer.FileTransferOfferReceived += (_, e) => { if (e.Message.FileName == "old.bin") oldReceived.TrySetResult(); };
            service.InboundDispatchBeforeWorkAsyncForTests = (lane, op) =>
            {
                var barrier = lane == "lifecycle" && op == "offer" ? offerGate :
                    lane == "lifecycle_priority" && op == operation ? terminalGate : null;
                barrier?.Entered.TrySetResult();
                return barrier?.Release.Task ?? Task.CompletedTask;
            };
            service.InboundDispatchAfterWorkForTests = (lane, op) =>
            {
                if (lane == "lifecycle_priority" && op == operation) terminalDone.TrySetResult();
            };
            Task SendTerminal() => operation == "cancel"
                ? pair.Payer.SendFileTransferCancelAsync(new() { SessionId = offer.SessionId, TransferId = offer.TransferId, Reason = "user_cancel" }, pair.Token)
                : pair.Payer.SendFileTransferErrorAsync(new() { SessionId = offer.SessionId, TransferId = offer.TransferId, ErrorCode = "io_error" }, pair.Token);
            try
            {
                await pair.Payer.SendFileTransferOfferAsync(offer, pair.Token);
                await oldReceived.Task.WaitAsync(pair.Token);
                await offerGate.Entered.Task.WaitAsync(pair.Token);
                await SendTerminal();
                await terminalGate.Entered.Task.WaitAsync(pair.Token);
                await pair.Payer.SendFileTransferOfferAsync(offer with { FileName = "replacement.bin" }, pair.Token);
                offerGate.Release.TrySetResult();
                await WaitUntilAsync(() => service.Snapshot.Inbound is { FileName: "replacement.bin", State: FileTransferTransferState.PendingDecision }, TimeSpan.FromSeconds(3));
                terminalGate.Release.TrySetResult();
                await terminalDone.Task.WaitAsync(pair.Token);
                Assert.Equal(FileTransferTransferState.PendingDecision, service.Snapshot.Inbound!.State);
                await SendTerminal();
                var expected = operation == "cancel" ? FileTransferTransferState.Canceled : FileTransferTransferState.Failed;
                await WaitUntilAsync(() => service.Snapshot.Inbound?.State == expected, TimeSpan.FromSeconds(3));
            }
            finally { offerGate.Release.TrySetResult(); terminalGate.Release.TrySetResult(); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AdmissionDecline_CannotWriteOrCommitForReplacementOffer(bool writeAlreadyStarted)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var old = ReplacementTestOffer(pair.Permit.Scope.SessionId) with { FileTransferRoute = FileTransferRouteResolver.FileTunaV4Token };
            var replacement = old with { FileName = "replacement.bin", FileTransferRoute = FileTransferRouteResolver.RegularNknV4FastToken };
            var replacementReceipt = new TaskCompletionSource<FileTransferOfferReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Dialer.FileTransferOfferReceived += (_, e) => { if (e.Message.FileName == replacement.FileName) replacementReceipt.TrySetResult(e); };
            var write = new PreparationHandshakeBarrier();
            int declineWrites = 0;
            pair.DialerClient.ShouldDeliverSendAsync = async (_, bytes, _) =>
            {
                if (!EnvelopeCodec.TryDeserialize(bytes, out var envelope) || envelope.Type != MsgType.FileTransferDecline) return true;
                Interlocked.Increment(ref declineWrites);
                write.Entered.TrySetResult();
                await write.Release.Task;
                return false; // A write already submitted cannot be recalled. Isolate its local completion.
            };
            var writer = (SemaphoreSlim)GetPrivateField(pair.Dialer, "outboundSendGate")!;
            if (!writeAlreadyStarted) await writer.WaitAsync(pair.Token);
            FileTransferOfferReceivedEventArgs receipt;
            try
            {
                await pair.Payer.SendFileTransferOfferAsync(old, pair.Token); // No local Tuna proof: ordinary admission must decline.
                if (writeAlreadyStarted) await write.Entered.Task.WaitAsync(pair.Token);
                else await WaitUntilAsync(() => GetPrivateField(writer, "m_asyncHead") is Task, TimeSpan.FromSeconds(3));
                await pair.Payer.SendFileTransferErrorAsync(new() { SessionId = old.SessionId, TransferId = old.TransferId, ErrorCode = "io_error" }, pair.Token);
                await pair.Payer.SendFileTransferOfferAsync(replacement, pair.Token);
                receipt = await replacementReceipt.Task.WaitAsync(pair.Token);
                Assert.True(receipt.IsCurrentOffer!(true));
            }
            finally
            {
                write.Release.TrySetResult();
                if (!writeAlreadyStarted) writer.Release();
            }
            await ((Task)GetPrivateField(pair.DialerService, "inboundLifecycleTail")!).WaitAsync(pair.Token);
            Assert.Equal(writeAlreadyStarted ? 1 : 0, Volatile.Read(ref declineWrites));
            Assert.True(receipt.IsCurrentOffer!(true));
            Assert.Equal(FileTransferTransferState.PendingDecision, pair.DialerService.Snapshot.Inbound!.State);
            Assert.Equal(replacement.FileName, pair.DialerService.Snapshot.Inbound.FileName);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AdmissionDecline_RequiresOriginalOfferAtBridgeWrite(bool replace)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var client = Assert.IsType<WriterGatePeer>(pair.DialerClient);
            using var bytes = new MemoryStream();
            using var bridgeWriter = new BridgeStdioWriter(bytes, leaveOpen: true);
            client.Writer = bridgeWriter;
            var gate = (SemaphoreSlim)GetPrivateField(bridgeWriter, "gate")!;
            var old = ReplacementTestOffer(pair.Permit.Scope.SessionId) with { FileTransferRoute = FileTransferRouteResolver.FileTunaV4Token };
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Dialer.FileTransferOfferReceived += (_, e) => { if (e.Message.FileName == "replacement.bin") received.TrySetResult(); };
            await gate.WaitAsync(pair.Token);
            try
            {
                await pair.Payer.SendFileTransferOfferAsync(old, pair.Token);
                await client.Arrived.Task.WaitAsync(pair.Token);
                if (replace)
                {
                    await pair.Payer.SendFileTransferErrorAsync(new() { SessionId = old.SessionId, TransferId = old.TransferId, ErrorCode = "io_error" }, pair.Token);
                    await pair.Payer.SendFileTransferOfferAsync(old with { FileName = "replacement.bin", FileTransferRoute = FileTransferRouteResolver.RegularNknV4FastToken }, pair.Token);
                    await received.Task.WaitAsync(pair.Token);
                }
            }
            finally { gate.Release(); }
            Assert.Equal(!replace, await client.WriteCompleted.Task.WaitAsync(pair.Token));
            Assert.Equal(!replace, bytes.Length > 0);
            await ((Task)GetPrivateField(pair.DialerService, "inboundLifecycleTail")!).WaitAsync(pair.Token);
            if (replace) Assert.True(pair.DialerService.Snapshot.Inbound is { FileName: "replacement.bin", State: FileTransferTransferState.PendingDecision });
            else Assert.Null(pair.DialerService.Snapshot.Inbound);
        }, useWriterGatePeer: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "InitialTunaReadiness")]
    public Task QueuedRegularOffer_RequiresItsOwnOfferAndServiceAttachment(bool reattach)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            var service = pair.DialerService;
            var oldReceipt = new TaskCompletionSource<FileTransferOfferReceivedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var newReceipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var displayed = new System.Collections.Concurrent.ConcurrentQueue<string>();
            service.TransferChanged += (_, e) =>
            {
                if (e.Snapshot.Inbound is { State: FileTransferTransferState.PendingDecision } offer) displayed.Enqueue(offer.FileName);
            };
            pair.Dialer.FileTransferOfferReceived += (_, e) =>
            {
                if (e.Message.FileName == "old.bin") oldReceipt.TrySetResult(e); else newReceipt.TrySetResult();
            };
            pair.Dialer.FileTransferCancelReceived += (_, _) => terminal.TrySetResult();
            var old = new FileTransferOfferV2 { SessionId = pair.Permit.Scope.SessionId, TransferId = "reused-id",
                FileName = "old.bin", FileSizeBytes = 4, PreferredDataProtocolVersion = 4,
                FileTransferRoute = FileTransferRouteResolver.RegularNknV4FastToken };
            async Task CancelOld()
            {
                await pair.Payer.SendFileTransferCancelAsync(new() { SessionId = old.SessionId, TransferId = old.TransferId,
                    Reason = "user_cancel" }, pair.Token);
                await terminal.Task.WaitAsync(pair.Token);
            }
            var replacement = old with { FileName = "replacement.bin", FileSizeBytes = 9 };
            InvokePrivateMethod(service, "EnqueueInboundLifecycle", "hold_regular_offer", (Func<Task>)(() => release.Task));
            try
            {
                await pair.Payer.SendFileTransferOfferAsync(old, pair.Token);
                var receipt = await oldReceipt.Task.WaitAsync(pair.Token);
                Assert.Null(receipt.FinalAckReadiness);
                Assert.Null(GetPrivateField(service, "preparationAdmission"));
                if (reattach)
                {
                    service.DetachTransport();
                    service.AttachTransport(pair.Dialer); // Same authenticated session; no cancel to invalidate its tracker.
                }
                else
                {
                    await CancelOld();
                    await pair.Payer.SendFileTransferOfferAsync(replacement, pair.Token);
                    await newReceipt.Task.WaitAsync(pair.Token);
                }
            }
            finally { release.TrySetResult(); }
            await ((Task)GetPrivateField(service, "inboundLifecycleTail")!).WaitAsync(pair.Token);
            Assert.DoesNotContain("old.bin", displayed);
            if (reattach)
            {
                Assert.Null(service.Snapshot.Inbound);
                await CancelOld();
                await pair.Payer.SendFileTransferOfferAsync(replacement, pair.Token);
                await newReceipt.Task.WaitAsync(pair.Token);
            }
            await WaitUntilAsync(() => displayed.Contains("replacement.bin") && service.Snapshot.Inbound is
                { State: FileTransferTransferState.PendingDecision, FileName: "replacement.bin", FileSizeBytes: 9 }, TimeSpan.FromSeconds(3));
            Assert.DoesNotContain("old.bin", displayed);
        });

    [Theory]
    [InlineData(false, "ready")]
    [InlineData(true, "ready")]
    [InlineData(false, "runtime_no_transfer")]
    [InlineData(true, "runtime_no_transfer")]
    [InlineData(false, "ack_held")]
    [InlineData(true, "ack_held")]
    [InlineData(false, "answer_held")]
    [InlineData(true, "answer_held")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "expired")]
    [InlineData(true, "expired")]
    [InlineData(false, "consumed")]
    [InlineData(true, "consumed")]
    [InlineData(false, "wrong_direction")]
    [InlineData(true, "wrong_direction")]
    [Trait("FileTransferInfraSuite", "InitialTunaReadiness")]
    public Task InitialTunaReadiness_PairedServicesRequireLocalEvidenceAtOfferAdmission(
        bool helperPays, string control)
        => VerifyPairedInitialReadinessAsync(helperPays, control, runtimeLanes: false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "InitialTunaReadiness")]
    public Task InitialTunaReadiness_RuntimeLanesAdmitFirstFileOfferAfterFinalAck(bool helperPays)
        => VerifyPairedInitialReadinessAsync(helperPays, "ready", runtimeLanes: true);

    [Theory]
    [InlineData(true, "write_pending")]
    [InlineData(false, "write_pending")]
    [InlineData(true, "write_pending_expired")]
    [InlineData(true, "write_pending_failed")]
    [InlineData(true, "write_pending_replaced")]
    [InlineData(true, "write_pending_revoked")]
    [InlineData(true, "write_pending_cancel")]
    [InlineData(true, "write_pending_error")]
    [InlineData(true, "write_pending_detach")]
    [InlineData(true, "completed_replaced_while_queued")]
    [InlineData(true, "missing_while_queued")]
    [InlineData(true, "runtime_over_consumed")]
    [InlineData(true, "error_after_observation")]
    [InlineData(true, "busy_after_observation")]
    [InlineData(true, "busy_regular_keeps_proof")]
    [InlineData(true, "lease_replaced")]
    [InlineData(true, "screen_only")]
    [Trait("FileTransferInfraSuite", "InitialTunaReadiness")]
    public Task InitialTunaReadiness_FirstFileOfferRequiresItsOwnFinalAckAndFileLease(bool helperPays, string control)
        => VerifyPairedInitialReadinessAsync(helperPays, control, runtimeLanes: true);

    [Theory]
    [InlineData("fixed_original_lease")]
    [InlineData("fixed_replaced_at_record")]
    public Task InitialTunaReadiness_FixedListenerRecordsOnlyUnderItsOriginalLease(string control)
        => VerifyPairedInitialReadinessAsync(true, control, runtimeLanes: false);

    private async Task VerifyPairedInitialReadinessAsync(bool helperPays, string control, bool runtimeLanes)
    {
        var previousPriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        var heldMessage = new TaskCompletionSource<(string Destination, byte[] Bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var identity = Guid.NewGuid().ToString("N");
            using var profile = NknTransportOptions.OverrideLocalAppDataPathForTests(Path.Combine(Path.GetTempPath(), identity));
            var hostClient = new FakeNknClient($"host.initial-readiness.{identity}.address");
            var helperClient = new FakeNknClient($"helper.initial-readiness.{identity}.address");
            var runtimeNegotiation = control is "answer_held" or "runtime_no_transfer";
            // A file-only configured sidecar takes the separate session-preparation path.
            // Exercise the ordinary fixed-lane handshake with both lanes supported but only File granted.
            var lanes = runtimeNegotiation ? NknAccelerationLaneKind.File : NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen;
            var payerLanes = lanes;
            var pendingWrite = control.StartsWith("write_pending", StringComparison.Ordinal);
            var fixedLeaseControl = control.StartsWith("fixed_", StringComparison.Ordinal);
            var hostLane = new RetryableTunaAccelerationSession(!helperPays, 0, supportedLanes: lanes)
                { StrictPayerHandoffForTests = runtimeLanes || fixedLeaseControl };
            var helperLane = new RetryableTunaAccelerationSession(helperPays, 0, supportedLanes: lanes)
                { StrictPayerHandoffForTests = runtimeLanes || fixedLeaseControl };
            var payerClient = helperPays ? helperClient : hostClient;
            var heldClient = runtimeNegotiation ? (helperPays ? hostClient : helperClient) : payerClient;
            var heldType = runtimeNegotiation ? MsgType.TransportAccelerationAnswer : MsgType.TransportAccelerationAnswerAck;
            heldClient.ShouldDeliverSendAsync = async (destination, bytes, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var envelope) &&
                    envelope.Type == heldType && (!releaseDelivery.Task.IsCompleted || control == "write_pending_failed"))
                {
                    // Legacy controls withhold peer delivery. The race controls keep
                    // the writer pending after the authentic ACK reaches the peer.
                    heldMessage.TrySetResult((destination, bytes.ToArray()));
                    if (pendingWrite) await releaseDelivery.Task.WaitAsync(ct);
                    if (control == "write_pending_failed") throw new IOException("fake ACK write failed");
                    return false;
                }
                return true;
            };
            var options = NknTransportOptions.Load();
            NknTunaAccelerationOptions LaneOptions(bool pays) => runtimeLanes
                ? NknTunaAccelerationOptions.CreateRuntimePilot("fake-sidecar.exe", lanes, pays, () => pays ? payerLanes : lanes)
                : NknTunaAccelerationOptions.Disabled;
            using var host = new NknSignalingTransport(hostClient, options,
                new NknIdentity("host-initial-readiness", hostClient.Address), LaneOptions(!helperPays), hostLane);
            using var helper = new NknSignalingTransport(helperClient, options,
                new NknIdentity("helper-initial-readiness", helperClient.Address), LaneOptions(helperPays), helperLane);
            var transferId = $"initial_readiness_{helperPays}_{control}";
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            var offerReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            var payerTransport = helperPays ? helper : host;
            var payerLane = helperPays ? helperLane : hostLane;
            var receiveTransport = pendingWrite ? payerTransport : helper;
            var sendTransport = ReferenceEquals(receiveTransport, host) ? helper : host;
            var receivedPending = false;
            var observed = new TaskCompletionSource<FileTransferInitialTunaReadinessEvidence?>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiveTransport.FileTransferOfferReceived += (_, args) =>
            {
                if (control == "error_after_observation" && args.Message.TransferId == transferId && args.FinalAckReadiness is { } bound)
                {
                    // Pause this offer after its real observation returns; the authenticated peer error must retire the wait.
                    SetPrivateFieldDynamic(args, "<FinalAckReadiness>k__BackingField", bound with { ObserveAsync = async token =>
                    {
                        var evidence = await bound.ObserveAsync(token);
                        observed.TrySetResult(evidence);
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return evidence;
                    } });
                }
                receivedPending = args.FinalAckReadiness is not null;
                offerReceived.TrySetResult(args.Message);
            };
            void AttachFileServices()
            {
                sender.AttachTransport(sendTransport);
                receiver.AttachTransport(receiveTransport);
            }
            if (!runtimeNegotiation) AttachFileServices(); // Ordinary startup must observe approval, as the running app does.
            var sessionId = await ApproveNknSessionAsync(host, helper, cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | (runtimeLanes ? InviteCapabilities.ScreenShare : InviteCapabilities.None),
                suppressAutomaticAccelerationNegotiation: true);
            if (runtimeNegotiation) AttachFileServices(); // These controls specifically exercise late attachment.
            var logStart = GetOperationalLogLength();
            var fixedPublication = new TaskCompletionSource<(NknTunaLaneLease Original, object? PriorProof)>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (fixedLeaseControl)
                helperLane.BeforeListenerPublishForTests = lease =>
                {
                    // Ignore earlier guarded writes; intercept the accepted answer's publication.
                    if (GetPrivateField(helper, "finalAckPreferenceOffer") is null) return;
                    helperLane.BeforeListenerPublishForTests = null;
                    var prior = GetPrivateField(helper, "initialTunaReadinessState");
                    if (control == "fixed_replaced_at_record")
                        Assert.True(helperLane.EnsureListenerSidecarConnectedAsync(hostClient.Address, cts.Token).GetAwaiter().GetResult());
                    fixedPublication.TrySetResult((lease, prior));
                };

            // Real offer/answer/ACK messages, with no transfer, route
            // publication, readiness snapshot or runtime transaction preseeded.
            if (runtimeNegotiation)
            {
                // Keep the original transfer-scoped answer guard regression.
                // Public runtime_unlock now rejects this late/unknown preparation
                // attachment; its selection is tested in SessionPreparationRequest.
                InvokePrivateMethod(helperPays ? helper : host, "ScheduleAccelerationNegotiationIfEligible",
                    "runtime_unlock", null, false, true, 0L);
            }
            else await (helperPays ? helper : host).RequestAccelerationNegotiationAsync("session_security_state_ready", cts.Token);
            if (fixedLeaseControl)
            {
                var (lease, prior) = await fixedPublication.Task.WaitAsync(TimeSpan.FromSeconds(4), cts.Token);
                Assert.Null(prior);
                if (control == "fixed_replaced_at_record")
                {
                    await WaitUntilAsync(() => GetPrivateField(helper, "finalAckPreferenceOffer") is null, TimeSpan.FromSeconds(3));
                    Assert.NotEqual(lease, helperLane.CaptureCurrentLaneLease());
                    Assert.Null(GetPrivateField(helper, "initialTunaReadinessState"));
                    Assert.Equal(NknAccelerationLaneKind.None, helper.AccelerationNegotiatedLanesForTests);
                    Assert.False(((IFileTransferInitialTunaReadinessProvider)helper).TryGetInitialTunaReadinessEvidence(sessionId,
                        transferId, FileTransferDirection.Inbound, out _));
                }
                else
                {
                    await heldMessage.Task.WaitAsync(cts.Token);
                    Assert.Equal(lease, ContextValue(GetPrivateField(helper, "initialTunaReadinessState")!, "PhysicalLease"));
                    Assert.True(((IFileTransferInitialTunaReadinessProvider)helper).TryGetInitialTunaReadinessEvidence(sessionId,
                        transferId, FileTransferDirection.Inbound, out _));
                    Assert.False(releaseDelivery.Task.IsCompleted); // Fixed-lane readiness still precedes peer ACK delivery.
                }
                return;
            }
            var captured = await heldMessage.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            Assert.Equal(!helperPays, hostLane.IsLocalPaidListenerActive);
            Assert.Equal(helperPays, helperLane.IsLocalPaidListenerActive);
            if (pendingWrite)
            {
                // The peer gets the authentic bytes; the payer's actual SendAsync has not returned.
                var peer = helperPays ? hostClient : helperClient;
                Assert.Equal(peer.Address, captured.Destination);
                InvokePrivateMethod(peer, "Receive", new NknIncomingMessage(payerClient.Address, captured.Bytes, false, null));
                await WaitUntilAsync(() => GetPrivateField(sendTransport, "initialTunaReadinessState") is not null, TimeSpan.FromSeconds(3));
                await sender.TryStartSendAsync(new("first.bin", 8, transferId),
                    _ => Task.FromResult<Stream>(new MemoryStream(new byte[8], writable: false)), cts.Token);
                var earlyOffer = await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
                Assert.Equal(FileTransferRouteResolver.FileTunaV4Token, earlyOffer.FileTransferRoute);
                Assert.True(receivedPending);
                Assert.Null(receiver.Snapshot.Inbound);
                Assert.Null(GetPrivateField(payerTransport, "initialTunaReadinessState"));
                if (control is "write_pending_cancel" or "write_pending_error" or "write_pending_detach")
                {
                    await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains(
                        $"event=initial_tuna_offer_waiting_final_ack; transfer_id={transferId};", StringComparison.Ordinal), TimeSpan.FromSeconds(3));
                    if (control == "write_pending_cancel")
                        Assert.Equal(FileTransferTransferState.Canceled,
                            (await sender.CancelTransferAsync(transferId, "user_cancel", cts.Token))!.State);
                    else if (control == "write_pending_error")
                        await sendTransport.SendFileTransferErrorAsync(new() { SessionId = sessionId, TransferId = transferId,
                            ErrorCode = "peer_error" }, cts.Token);
                    else
                    {
                        receiver.DetachTransport();
                        receiver.AttachTransport(receiveTransport);
                    }
                    await ((Task)GetPrivateField(receiver, "inboundLifecycleTail")!).WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
                    releaseDelivery.TrySetResult();
                    await WaitUntilAsync(() => GetPrivateField(payerTransport, "initialTunaReadinessState") is not null,
                        TimeSpan.FromSeconds(3));
                    Assert.Null(receiver.Snapshot.Inbound);
                    Assert.DoesNotContain(ReadOperationalLogTail(logStart).Split('\n'), line =>
                        line.Contains($"transfer_id={transferId};") && line.Contains("event=filetransfer_route_selected; direction=inbound;"));
                    Assert.True(((IFileTransferInitialTunaReadinessProvider)payerTransport).TryGetInitialTunaReadinessEvidence(
                        sessionId, "successor-file", FileTransferDirection.Inbound, out _));
                    return;
                }
                if (control == "write_pending_expired")
                {
                    var shadow = GetPrivateField(payerTransport, "pendingAccelerationAnswerAckReplyShadow")!;
                    var deadline = Assert.IsType<long>(ContextValue(shadow, "DeadlineUtcMs"));
                    payerTransport.FinalAckUtcMs = () => deadline;
                }
                if (control == "write_pending_replaced")
                {
                    var original = Assert.IsType<NknTunaLaneLease>(payerLane.CaptureCurrentLaneLease());
                    Assert.True(await payerLane.EnsureListenerSidecarConnectedAsync(peer.Address, cts.Token));
                    Assert.NotEqual(original, payerLane.CaptureCurrentLaneLease());
                }
                if (control == "write_pending_revoked")
                {
                    payerLanes = NknAccelerationLaneKind.Screen;
                    payerTransport.RetireDeselectedAccelerationOffers();
                    payerLanes = lanes; // Reselecting cannot revive the original handshake.
                }
                releaseDelivery.TrySetResult();
                if (control == "write_pending")
                {
                    await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision,
                        TimeSpan.FromSeconds(3));
                    AssertPublication(receiver, "inboundTransfer", FileTransferDirection.Inbound, FileTransferRoute.FileTunaV4);
                }
                else
                {
                    await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Declined, TimeSpan.FromSeconds(3));
                    Assert.Null(receiver.Snapshot.Inbound);
                }
                return;
            }
            if (runtimeLanes)
            {
                var payer = helperPays ? helper : host;
                await WaitUntilAsync(() => payer.AccelerationNegotiatedLanesForTests == lanes, TimeSpan.FromSeconds(3));
                if (control is not ("completed_replaced_while_queued" or "missing_while_queued" or "runtime_over_consumed"
                    or "busy_after_observation" or "busy_regular_keeps_proof" or "error_after_observation"))
                {
                    Assert.True(((IFileTransferInitialTunaReadinessProvider)payer).TryGetInitialTunaReadinessEvidence(
                        sessionId, transferId, helperPays ? FileTransferDirection.Inbound : FileTransferDirection.Outbound, out var local));
                    Assert.False(local!.AnswerAckReceived); // Sent locally, not received from the peer.
                }
            }
            Assert.Contains(ReadOperationalLogTail(logStart).Split('\n'), line =>
                line.Contains("event=runtime_unlock_transaction_answer_received;", StringComparison.Ordinal) &&
                line.Contains("peer_visible_proof=1;", StringComparison.Ordinal) &&
                line.Contains("answer_received=1;", StringComparison.Ordinal) &&
                line.Contains("tuna_path_lease_current=1;", StringComparison.Ordinal) &&
                line.Contains("tuna_path_lease_state=listenerready;", StringComparison.Ordinal));
            if (runtimeNegotiation)
            {
                Assert.Contains("reason=transport_acceleration_answer_pending;", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
            }

            if (control is not ("ack_held" or "answer_held"))
            {
                releaseDelivery.TrySetResult();
                await heldClient.SendAsync(captured.Destination, captured.Bytes, cts.Token);
                if (control == "runtime_no_transfer")
                {
                    await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Split('\n').Any(line =>
                        line.Contains("event=runtime_unlock_transaction_peer_received;", StringComparison.Ordinal) &&
                        line.Contains("reason=transport_acceleration_answer;", StringComparison.Ordinal)), TimeSpan.FromSeconds(3));
                    var payer = helperPays ? helper : host;
                    var boundary = GetPrivateField(payer, "outboundRuntimeUnlockOfferAuthorityBoundary");
                    Assert.NotNull(boundary);
                    var sourceScope = ContextValue(boundary!, "InitialActivationExpectedScope");
                    Assert.NotNull(sourceScope);
                    Assert.Null(ContextValue(sourceScope!, "SourcePublicationAuthority"));
                    // A session-only unlock has no existing Coordinator source
                    // publication. Preserve this guard; do not invent one to
                    // make the pre-transfer GUI readiness condition pass.
                    Assert.False(Assert.IsType<bool>(InvokePrivateMethod(payer,
                        "IsRuntimeUnlockInitialActivationAnswerBoundarySourceCurrent", boundary)));
                    Assert.False(Assert.IsType<RuntimeUnlockTransactionSnapshot>(
                        GetPrivateField(payer, "runtimeUnlockTransactionState")).AnswerReceived);
                    Assert.Null(GetPrivateField(payer, "runtimeUnlockRetiredAnswerBarrier"));
                }
                else
                {
                    await WaitUntilAsync(() => GetPrivateField(host, "initialTunaReadinessState") is not null &&
                        GetPrivateField(helper, "initialTunaReadinessState") is not null, TimeSpan.FromSeconds(3));
                    if (!runtimeLanes)
                    {
                        Assert.Equal(NknAccelerationLaneKind.File, host.AccelerationNegotiatedLanesForTests);
                        Assert.Equal(NknAccelerationLaneKind.File, helper.AccelerationNegotiatedLanesForTests);
                    }
                    var snapshot = GetPrivateField(helper, "initialTunaReadinessState")!;
                    if (control is "completed_replaced_while_queued" or "missing_while_queued")
                    {
                        if (control == "missing_while_queued") SetPrivateField(helper, "initialTunaReadinessState", null);
                        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        InvokePrivateMethod(receiver, "EnqueueInboundLifecycle", "hold_dispatch", (Func<Task>)(() => release.Task));
                        try
                        {
                            await sender.TryStartSendAsync(new("queued.bin", 8, transferId),
                                _ => Task.FromResult<Stream>(new MemoryStream(new byte[8])), cts.Token);
                            Assert.Equal(FileTransferRouteResolver.FileTunaV4Token, (await offerReceived.Task.WaitAsync(cts.Token)).FileTransferRoute);
                            Assert.True(receivedPending); // The ACK completed before this offer was received.
                            Assert.Null(ContextValue(snapshot, "ConsumedTransferId"));
                            Assert.True(await helperLane.EnsureListenerSidecarConnectedAsync(hostClient.Address, cts.Token));
                            // Model B's completed local answer; no second live negotiation or route grant.
                            InvokePrivateMethod(helper, "RecordInitialTunaReadinessAnswerReceived", sessionId,
                                (long)ContextValue(snapshot, "OfferGeneration")! + 1, (long)ContextValue(snapshot, "PayerDecisionId")! + 1,
                                "replacement_fixture", false);
                            var successor = GetPrivateField(helper, "initialTunaReadinessState")!;
                            Assert.NotSame(snapshot, successor);
                            Assert.NotEqual(ContextValue(snapshot, "PhysicalLease"), ContextValue(successor, "PhysicalLease"));
                        }
                        finally { release.TrySetResult(); }
                        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Declined, TimeSpan.FromSeconds(3));
                        Assert.Null(receiver.Snapshot.Inbound);
                        var currentProof = GetPrivateField(helper, "initialTunaReadinessState")!;
                        Assert.Null(ContextValue(currentProof, "ConsumedTransferId"));
                        await host.SendFileTransferOfferAsync((await offerReceived.Task) with { TransferId = "successor-file" }, cts.Token);
                        await WaitUntilAsync(() => receiver.Snapshot.Inbound is { TransferId: "successor-file",
                            State: FileTransferTransferState.PendingDecision }, TimeSpan.FromSeconds(3));
                        Assert.Equal("successor-file", ContextValue(currentProof, "ConsumedTransferId"));
                        return;
                    }
                    if (control is "busy_after_observation" or "busy_regular_keeps_proof" or "error_after_observation" or "runtime_over_consumed")
                    {
                        // Model a receiver whose prior terminal notification has not reached Core. Seed only
                        // that service slot; the subject and successor offers use the unmodified secure transport.
                        var decline = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                        if (control is "busy_after_observation" or "busy_regular_keeps_proof")
                        {
                            await (Task)InvokePrivateMethod(receiver, "HandleIncomingOfferAsync", new FileTransferOfferV2
                            { SessionId = sessionId, TransferId = "occupied-slot", FileName = "occupied.bin", FileSizeBytes = 8,
                                FileTransferRoute = FileTransferRouteResolver.RegularNknV4FastToken, PreferredDataProtocolVersion = 4 })!;
                            Assert.Equal("occupied-slot", receiver.Snapshot.Inbound?.TransferId);
                            Assert.Same(snapshot, GetPrivateField(helper, "initialTunaReadinessState"));
                            Assert.Null(ContextValue(snapshot, "ConsumedTransferId"));
                            host.FileTransferDeclineReceived += (_, e) => { if (e.Message.TransferId == transferId) decline.TrySetResult(e.Message.Reason); };
                        }
                        if (control == "runtime_over_consumed")
                        {
                            Assert.True(((IFileTransferInitialTunaReadinessProvider)helper).TryGetInitialTunaReadinessEvidence(
                                sessionId, "prior-file", FileTransferDirection.Inbound, out _));
                            SetPrivateField(helper, "runtimeUnlockTransactionState", RuntimeUnlockTransactionSnapshot.Idle with
                            {
                                SessionId = sessionId, TransferId = transferId, TransactionGeneration = 201, OfferGeneration = 202,
                                State = RuntimeUnlockTransactionState.AnswerReceived, PeerReceived = true, AnswerReceived = true,
                                TunaPathLeaseCurrent = true, TunaPathLeaseState = RuntimeUnlockTunaPathLeaseState.ListenerReady,
                                TunaPathLeaseGeneration = 203, TunaPathLeaseListenerRunId = "runtime-fixture",
                            });
                        }
                        if (control == "busy_regular_keeps_proof")
                            await host.SendFileTransferOfferAsync(new() { SessionId = sessionId, TransferId = transferId,
                                FileName = "regular.bin", FileSizeBytes = 8, PreferredDataProtocolVersion = 4,
                                FileTransferRoute = FileTransferRouteResolver.RegularNknV4FastToken }, cts.Token);
                        else
                            await sender.TryStartSendAsync(new("first.bin", 8, transferId),
                                _ => Task.FromResult<Stream>(new MemoryStream(new byte[8])), cts.Token);
                        var first = await offerReceived.Task.WaitAsync(cts.Token);
                        Assert.Equal(control == "busy_regular_keeps_proof" ? FileTransferRouteResolver.RegularNknV4FastToken
                            : FileTransferRouteResolver.FileTunaV4Token, first.FileTransferRoute);
                        if (control == "error_after_observation")
                        {
                            Assert.NotNull(await observed.Task.WaitAsync(cts.Token));
                            Assert.Null(ContextValue(snapshot, "ConsumedTransferId"));
                            await host.SendFileTransferErrorAsync(new() { SessionId = sessionId, TransferId = transferId,
                                ErrorCode = "peer_error" }, cts.Token);
                            await ((Task)GetPrivateField(receiver, "inboundLifecycleTail")!).WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
                            Assert.Null(receiver.Snapshot.Inbound);
                        }
                        else if (control == "runtime_over_consumed")
                        {
                            await WaitUntilAsync(() => receiver.Snapshot.Inbound is { State: FileTransferTransferState.PendingDecision }
                                pending && pending.TransferId == transferId, TimeSpan.FromSeconds(3));
                            AssertPublication(receiver, "inboundTransfer", FileTransferDirection.Inbound, FileTransferRoute.FileTunaV4);
                            Assert.Equal("prior-file", ContextValue(snapshot, "ConsumedTransferId"));
                            return;
                        }
                        else
                        {
                            Assert.Equal("busy", await decline.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token));
                            Assert.Equal("occupied-slot", receiver.Snapshot.Inbound!.TransferId);
                            InvokePrivateMethod(receiver, "OnFileTransferCancelReceived", helper, new FileTransferCancelReceivedEventArgs(
                                new() { SessionId = sessionId, TransferId = "occupied-slot", Reason = "user_cancel" }, hostClient.Address));
                            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled, TimeSpan.FromSeconds(3));
                        }
                        Assert.Null(ContextValue(snapshot, "ConsumedTransferId"));
                        Assert.Same(snapshot, GetPrivateField(helper, "initialTunaReadinessState"));
                        await host.SendFileTransferOfferAsync(first with { TransferId = "next-file",
                            FileTransferRoute = FileTransferRouteResolver.FileTunaV4Token }, cts.Token);
                        await WaitUntilAsync(() => receiver.Snapshot.Inbound is { TransferId: "next-file",
                            State: FileTransferTransferState.PendingDecision }, TimeSpan.FromSeconds(3));
                        Assert.Equal("next-file", ContextValue(snapshot, "ConsumedTransferId"));
                        return;
                    }
                    switch (control)
                    {
                        case "lease_replaced":
                            var initialLease = Assert.IsType<NknTunaLaneLease>(helperLane.CaptureCurrentLaneLease());
                            Assert.True(await helperLane.EnsureListenerSidecarConnectedAsync(hostClient.Address, cts.Token));
                            Assert.NotEqual(initialLease, helperLane.CaptureCurrentLaneLease());
                            break;
                        case "screen_only":
                            payerLanes = NknAccelerationLaneKind.Screen;
                            break;
                        case "missing":
                            SetPrivateField(helper, "initialTunaReadinessState", null);
                            break;
                        case "expired":
                            SetPrivateFieldDynamic(snapshot, "<CapturedUtc>k__BackingField", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(6));
                            break;
                        case "consumed":
                            Assert.True(((IFileTransferInitialTunaReadinessProvider)helper).TryGetInitialTunaReadinessEvidence(
                                sessionId, "prior-transfer", FileTransferDirection.Inbound, out _));
                            break;
                        case "wrong_direction":
                            SetPrivateFieldDynamic(snapshot, "<Direction>k__BackingField", FileTransferDirection.Outbound);
                            break;
                    }
                }
            }

            // Capture the production providers with the exact transfer/direction
            // the services will use. A successful call consumes the snapshot for
            // this transfer, so it is not a general-purpose readiness poll.
            var senderReady = ((IFileTransferInitialTunaReadinessProvider)host).TryGetInitialTunaReadinessEvidence(
                sessionId, transferId, FileTransferDirection.Outbound, out var senderEvidence);
            var receiverReady = ((IFileTransferInitialTunaReadinessProvider)helper).TryGetInitialTunaReadinessEvidence(
                sessionId, transferId, FileTransferDirection.Inbound, out var receiverEvidence);
            Assert.Equal(control switch { "ack_held" => !helperPays, "answer_held" or "runtime_no_transfer" => helperPays, _ => true }, senderReady);
            Assert.Equal(control == "ready" || (control == "ack_held" && helperPays) ||
                (runtimeNegotiation && !helperPays), receiverReady);
            Assert.Equal(senderReady, senderEvidence is not null);
            Assert.Equal(receiverReady, receiverEvidence is not null);
            Assert.Null(sender.Snapshot.Outbound);
            Assert.Null(receiver.Snapshot.Inbound);
            if (senderEvidence is not null)
            {
                Assert.Equal((sessionId, transferId, FileTransferDirection.Outbound),
                    (senderEvidence.SessionId, senderEvidence.TransferId, senderEvidence.Direction));
            }
            if (receiverEvidence is not null)
            {
                Assert.Equal((sessionId, transferId, FileTransferDirection.Inbound),
                    (receiverEvidence.SessionId, receiverEvidence.TransferId, receiverEvidence.Direction));
            }

            await sender.TryStartSendAsync(new("initial-readiness.bin", 8, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(new byte[8], writable: false)), cts.Token);
            var offer = await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var expectedRoute = senderReady ? FileTransferRoute.FileTunaV4 : FileTransferRoute.RegularNknV4Fast;
            var expectedToken = senderReady ? FileTransferRouteResolver.FileTunaV4Token : FileTransferRouteResolver.RegularNknV4FastToken;
            Assert.Equal(expectedToken, offer.FileTransferRoute);
            Assert.Equal(FileTransferProtocol.ProtocolVersionV4, offer.PreferredDataProtocolVersion);
            if (senderReady && !receiverReady)
            {
                await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Declined,
                    TimeSpan.FromSeconds(3));
                Assert.Null(receiver.Snapshot.Inbound);
                var log = ReadOperationalLogTail(logStart);
                Assert.Contains(log.Split('\n'), line => line.Contains($"transfer_id={transferId};", StringComparison.Ordinal) &&
                    line.Contains("direction=inbound;", StringComparison.Ordinal) &&
                    line.Contains("initial_tuna_readiness_missing", StringComparison.Ordinal));
                Assert.DoesNotContain(log.Split('\n'), line => line.Contains($"transfer_id={transferId};", StringComparison.Ordinal) &&
                    line.Contains("event=filetransfer_route_selected; direction=inbound;", StringComparison.Ordinal));
                Assert.DoesNotContain(log.Split('\n'), line => line.Contains($"transfer_id={transferId};", StringComparison.Ordinal) &&
                    line.Contains("event=filetransfer_runtime_started;", StringComparison.Ordinal));
            }
            else
            {
                await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision,
                    TimeSpan.FromSeconds(3));
                AssertPublication(sender, "outboundTransfer", FileTransferDirection.Outbound, expectedRoute);
                AssertPublication(receiver, "inboundTransfer", FileTransferDirection.Inbound, expectedRoute);
            }

            void AssertPublication(SessionFileTransferService service, string contextField, FileTransferDirection direction, FileTransferRoute route)
            {
                lock (GetPrivateField(service, "gate")!)
                {
                    var context = GetPrivateField(service, contextField)!;
                    var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
                    var publication = Assert.IsType<RoutePublicationToken>(state.CurrentRoutePublicationToken);
                    Assert.Equal((sessionId, transferId, direction), (publication.SessionId, publication.TransferId, publication.Direction));
                    Assert.Equal(route, publication.Route);
                    Assert.Equal(FileTransferProtocol.ProtocolVersionV4, publication.ProtocolVersion);
                    Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(state, publication, out _));
                }
            }
        }
        finally
        {
            releaseDelivery.TrySetResult();
            cts.Cancel();
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousPriorityDelay;
        }
    }
}
