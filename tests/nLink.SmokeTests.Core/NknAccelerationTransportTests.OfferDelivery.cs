using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;
using System.Threading.Channels;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData("current", 4)]
    [InlineData("current", 6)]
    [InlineData("disposed", 4)]
    [InlineData("replacement", 6)]
    [InlineData("queue-full", 4)]
    [InlineData("writer-closed", 6)]
    [InlineData("control-first", 6)]
    [InlineData("dispose-during-write", 6)]
    public Task OfferDelivery_PreparedBinaryCompletionStaysWithItsOriginalChannel(string dispatchAt, int version)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            // No service pump: hold the prepared work and observe exactly which channel receives it.
            pair.PayerService.DetachTransport();
            pair.DialerService.DetachTransport();
            var offer = ReplacementTestOffer(pair.Permit.Scope.SessionId);
            async Task<Guid> Start(string name)
            {
                var offered = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
                var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnOffer(object? _, FileTransferOfferReceivedEventArgs e) => offered.TrySetResult(e.Message);
                void OnAccept(object? _, FileTransferAcceptReceivedEventArgs e) => accepted.TrySetResult();
                void OnOpen(object? _, FileTransferSessionOpenReceivedEventArgs e) => opened.TrySetResult();
                pair.Dialer.FileTransferOfferReceived += OnOffer;
                pair.Payer.FileTransferAcceptReceived += OnAccept;
                pair.Dialer.FileTransferSessionOpenReceived += OnOpen;
                try
                {
                    await pair.Payer.SendFileTransferOfferAsync(offer with { FileName = name }, pair.Token);
                    var wireId = Assert.IsType<Guid>((await offered.Task.WaitAsync(pair.Token)).OfferInstanceId);
                    await pair.Dialer.SendFileTransferAcceptAsync(new() { SessionId = offer.SessionId, TransferId = offer.TransferId,
                        OfferInstanceId = wireId, AcceptedDataProtocolVersion = 4, FileTransferRoute = offer.FileTransferRoute }, pair.Token);
                    await accepted.Task.WaitAsync(pair.Token);
                    await pair.Payer.SendFileTransferSessionOpenAsync(new() { SessionId = offer.SessionId, TransferId = offer.TransferId,
                        SessionRole = FileTransferProtocol.SessionRoleSender, ProtocolVersion = 4, FileTransferRoute = offer.FileTransferRoute,
                        ChunkSizeBytes = 1024, InitialPipelineDepth = 8 }, pair.Token);
                    await opened.Task.WaitAsync(pair.Token);
                    return wireId;
                }
                finally
                {
                    pair.Dialer.FileTransferOfferReceived -= OnOffer;
                    pair.Payer.FileTransferAcceptReceived -= OnAccept;
                    pair.Dialer.FileTransferSessionOpenReceived -= OnOpen;
                }
            }
            var first = await Start("first.bin");
            using var original = await pair.Payer.OpenFileTransferDataSessionAsync(offer.SessionId, offer.TransferId, pair.Token);
            FileTransferCompleteFrameV4 frame = version == 6 ? new FileTransferCompleteFrameV6() : new FileTransferCompleteFrameV4();
            frame = frame with { SessionId = offer.SessionId, TransferId = offer.TransferId, FileSizeBytes = offer.FileSizeBytes,
                Sha256Base64 = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(new byte[4])) };
            var payload = Assert.IsType<byte[]>(InvokePrivateMethod(pair.Dialer, "CreateSecureFileTransferPayload", MsgType.FileTransferDataFrame,
                offer.TransferId, version == 4 ? FileTransferDataFrameCodec.SerializeLegacyV4(frame) : FileTransferDataFrameCodec.Serialize(frame), true));
            var envelope = new Envelope(1, (string)GetPrivateField(pair.Dialer, "currentEnvelopeCode")!, Guid.NewGuid().ToString("N"),
                MsgType.FileTransferDataFrame, payload, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);
            object?[] args = [pair.DialerClient.BulkAddress, NknBridgeChannel.Bulk, envelope, null];
            var prepare = typeof(NknSignalingTransport).GetMethod("TryPrepareFileTransferDataFrameDispatch",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            async Task<FileTransferCompleteV1> SendTaggedReceipt(Guid owner)
            {
                var received = new TaskCompletionSource<FileTransferCompleteV1>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnComplete(object? _, FileTransferCompleteReceivedEventArgs e) => received.TrySetResult(e.Message);
                pair.Payer.FileTransferCompleteReceived += OnComplete;
                try
                {
                    await pair.Dialer.SendFileTransferCompleteAsync(new() { SessionId = frame.SessionId, TransferId = frame.TransferId,
                        OfferInstanceId = owner, FileSizeBytes = frame.FileSizeBytes, Sha256Base64 = frame.Sha256Base64 }, pair.Token);
                    return await received.Task.WaitAsync(pair.Token);
                }
                finally { pair.Payer.FileTransferCompleteReceived -= OnComplete; }
            }
            if (dispatchAt == "control-first")
            {
                Assert.Equal(frame.Sha256Base64, (await SendTaggedReceipt(first)).Sha256Base64);
                original.Dispose();
                var previous = NknRuntimeDiagnostics.Snapshot();
                try
                {
                    NknRuntimeDiagnostics.SetLastError("previous_diagnostic");
                    NknRuntimeDiagnostics.SetLastEnvelopeDropReason("previous_drop");
                    Assert.False(Assert.IsType<bool>(prepare.Invoke(pair.Payer, args)));
                    var after = NknRuntimeDiagnostics.Snapshot();
                    Assert.Equal("previous_diagnostic", after.LastError);
                    Assert.Equal("previous_drop", after.LastEnvelopeDropReason);
                }
                finally
                {
                    NknRuntimeDiagnostics.SetLastError(previous.LastError);
                    NknRuntimeDiagnostics.SetLastEnvelopeDropReason(previous.LastEnvelopeDropReason);
                }
                return;
            }
            Assert.True(Assert.IsType<bool>(prepare.Invoke(pair.Payer, args)));
            var dispatch = Assert.IsType<Action>(args[3]!.GetType().GetProperty("Dispatch")!.GetValue(args[3]));
            if (dispatchAt == "dispose-during-write")
            {
                // Interpose at the real priority writer, after the completion reserved its queue slot.
                using var entered = new ManualResetEventSlim();
                var retirement = new Thread(() => { entered.Set(); original.Dispose(); }) { IsBackground = true };
                bool retiredBeforeWrite = false;
                Action beforeWrite = () =>
                {
                    retirement.Start();
                    entered.Wait(pair.Token);
                    Assert.True(SpinWait.SpinUntil(() => !retirement.IsAlive ||
                        (retirement.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(3)));
                    retiredBeforeWrite = (bool)original.GetType().GetProperty("IsDisposed")!.GetValue(original)!;
                };
                var queue = original.GetType().GetField("priorityFrames", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!;
                var wrapper = typeof(ReceiptWriteGate<>).MakeGenericType(queue.FieldType.GetGenericArguments());
                queue.SetValue(original, Activator.CreateInstance(wrapper, queue.GetValue(original), beforeWrite));
                dispatch();
                Assert.True(retirement.Join(TimeSpan.FromSeconds(3)));
                Assert.False(retiredBeforeWrite);
                Assert.Equal(frame, await original.ReceiveAsync(pair.Token));
                return;
            }
            if (dispatchAt == "current")
            {
                dispatch();
                Assert.Equal(frame, await original.ReceiveAsync(pair.Token));
                return;
            }
            if (dispatchAt is "queue-full" or "writer-closed")
            {
                if (dispatchAt == "queue-full")
                {
                    var deliver = original.GetType().GetMethod("Deliver")!;
                    var feedback = new FileTransferStateFrameV4 { SessionId = offer.SessionId, TransferId = offer.TransferId };
                    for (var i = 0; i < NknSignalingTransport.FileTransferDataSessionMaxQueuedFrames; i++)
                        deliver.Invoke(original, [feedback, NknBridgeChannel.Bulk, FileTransferTransportKind.RegularNkn, null, null]);
                }
                else
                {
                    var queue = original.GetType().GetField("priorityFrames", System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic)!.GetValue(original)!;
                    var writer = queue.GetType().GetProperty("Writer")!.GetValue(queue)!;
                    Assert.True(Assert.IsType<bool>(writer.GetType().GetMethod("TryComplete")!.Invoke(writer, [null])));
                }
                dispatch();
                Assert.True(Assert.IsType<bool>(original.GetType().GetProperty("IsDisposed")!.GetValue(original)));
                Assert.True(Assert.IsType<bool>(InvokePrivateMethod(pair.Payer, "TryValidateAndTrackFileTransferMessage",
                    MsgType.FileTransferComplete, offer.TransferId, true, false, null, null)));
                Assert.Equal(frame.Sha256Base64, (await SendTaggedReceipt(first)).Sha256Base64);
                return;
            }

            await pair.Dialer.SendFileTransferCancelAsync(new() { SessionId = offer.SessionId, TransferId = offer.TransferId,
                OfferInstanceId = first }, pair.Token);
            original.Dispose();
            if (dispatchAt == "disposed")
            {
                dispatch();
                var channels = (System.Collections.IDictionary)GetPrivateField(pair.Payer, "fileTransferDataSessions")!;
                Assert.False(channels.Contains(offer.TransferId));
                return;
            }

            var second = await Start("second.bin");
            Assert.NotEqual(first, second);
            using var replacement = await pair.Payer.OpenFileTransferDataSessionAsync(offer.SessionId, offer.TransferId, pair.Token);
            dispatch();
            foreach (var count in new[] { "queuedFrameCount", "queuedPriorityControlFrameCount" })
                Assert.Equal(0, Assert.IsType<int>(replacement.GetType().GetField(count,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(replacement)));
            Assert.Equal(frame.Sha256Base64, (await SendTaggedReceipt(second)).Sha256Base64);
        });

    private sealed class ReceiptWriteGate<T> : Channel<T>
    {
        public ReceiptWriteGate(Channel<T> queue, Action beforeWrite)
        {
            Reader = queue.Reader;
            Writer = new WriterGate(queue.Writer, beforeWrite);
        }
        private sealed class WriterGate(ChannelWriter<T> inner, Action beforeWrite) : ChannelWriter<T>
        {
            public override bool TryWrite(T item) { beforeWrite(); return inner.TryWrite(item); }
            public override bool TryComplete(Exception? error = null) => inner.TryComplete(error);
            public override ValueTask<bool> WaitToWriteAsync(CancellationToken ct = default) => inner.WaitToWriteAsync(ct);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OfferDelivery_DelayedCompletionCannotRetireReusedId(bool identicalContents)
        => WithPreparationHandshakePairAsync(false, async pair =>
        {
            const string id = "reused-completion";
            var oldBulk = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            var originalReceipt = new TaskCompletionSource<FileTransferCompleteV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacementReceipt = new TaskCompletionSource<FileTransferCompleteV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sendingReplacement = new PreparationHandshakeBarrier();
            var generation = 1;
            pair.Payer.FileTransferCompleteReceived += (_, e) =>
                (Volatile.Read(ref generation) == 1 ? originalReceipt : replacementReceipt).TrySetResult(e.Message);
            pair.PayerClient.ShouldDeliverSendAsync = async (_, bytes, _) =>
            {
                if (Volatile.Read(ref generation) == 2 && EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.FileTransferDataFrame)
                {
                    sendingReplacement.Entered.TrySetResult();
                    await sendingReplacement.Release.Task.WaitAsync(pair.Token);
                }
                return true;
            };
            pair.DialerClient.ShouldDeliverSendAsync = (destination, bytes, _) =>
            {
                if (!EnvelopeCodec.TryDeserialize(bytes, out var e) || e.Type != MsgType.FileTransferComplete)
                    return Task.FromResult(true);
                if (Volatile.Read(ref generation) == 1)
                {
                    if (destination != pair.PayerClient.BulkAddress) return Task.FromResult(true);
                    oldBulk.TrySetResult(e);
                    return Task.FromResult(false);
                }
                return Task.FromResult(true);
            };

            async Task Receive(string name, byte[] contents)
            {
                await pair.PayerService.TryStartSendAsync(new(name, contents.Length, id),
                    _ => Task.FromResult<Stream>(new MemoryStream(contents)), pair.Token);
                await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound is { State: FileTransferTransferState.PendingDecision } offer &&
                    offer.FileName == name, TimeSpan.FromSeconds(3));
                using var output = new MemoryStream();
                await pair.DialerService.AcceptIncomingTransferAsync(id,
                    (FileTransferWriteStreamFactory)((_, _) => Task.FromResult<Stream>(output)), pair.Token);
                await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.Completed, TimeSpan.FromSeconds(3));
                Assert.Equal(contents, output.ToArray());
            }

            byte[] first = [1, 2, 3, 4];
            await Receive("first.bin", first);
            var stale = await oldBulk.Task.WaitAsync(pair.Token);
            var queued = await originalReceipt.Task.WaitAsync(pair.Token);
            Assert.NotNull(queued.OfferInstanceId);
            await WaitUntilAsync(() => pair.PayerService.Snapshot.Outbound?.State == FileTransferTransferState.Completed, TimeSpan.FromSeconds(3));
            Volatile.Write(ref generation, 2);
            var receiving = Receive("second.bin", identicalContents ? first : [5, 6, 7, 8]);
            try
            {
                await sendingReplacement.Entered.Task.WaitAsync(pair.Token);
                await WaitUntilAsync(() => Assert.IsType<bool>(InvokePrivateMethod(pair.Payer, "TryValidateAndTrackFileTransferMessage",
                    MsgType.FileTransferComplete, id, true, false, null, null)), TimeSpan.FromSeconds(3));
                Assert.False(Assert.IsType<bool>(InvokePrivateMethod(pair.Payer, "TryPrepareFileTransferCompleteDispatch",
                    pair.DialerClient.BulkAddress, NknBridgeChannel.Bulk, stale, null)));
                foreach (var frame in new FileTransferCompleteFrameV4[] { new(), new FileTransferCompleteFrameV6() })
                {
                    var staleFrame = frame with { SessionId = queued.SessionId, TransferId = id,
                        FileSizeBytes = queued.FileSizeBytes, Sha256Base64 = queued.Sha256Base64 };
                    Assert.False(Assert.IsType<bool>(InvokePrivateMethod(pair.Payer, "TryValidateAndTrackFileTransferDataFrame",
                        staleFrame, true, true, null, null)));
                }
                await (Task)InvokePrivateMethod(pair.PayerService, "HandleIncomingCompleteAsync", queued)!;
                Assert.NotEqual(FileTransferTransferState.Completed, pair.PayerService.Snapshot.Outbound?.State);
            }
            finally { sendingReplacement.Release.TrySetResult(); }
            await receiving;
            var receipt = await replacementReceipt.Task.WaitAsync(pair.Token);
            Assert.NotNull(receipt.OfferInstanceId);
            Assert.NotEqual(queued.OfferInstanceId, receipt.OfferInstanceId);
            await WaitUntilAsync(() => pair.PayerService.Snapshot.Outbound?.State == FileTransferTransferState.Completed, TimeSpan.FromSeconds(3));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OfferDelivery_ReverseRegularOfferAfterCancelHonorsEstablishedFallbackEpoch(bool hasEpoch)
        => WithPreparationHandshakePairAsync(true, async pair =>
        {
            var session = pair.Permit.Scope.SessionId;
            var first = ReplacementTestOffer(session) with { TransferId = "first-transfer" };
            var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Dialer.FileTransferAcceptReceived += (_, e) =>
            {
                if (e.Message.TransferId == first.TransferId) accepted.TrySetResult(true);
            };
            await pair.Dialer.SendFileTransferOfferAsync(first, pair.Token);
            await WaitUntilAsync(() => pair.PayerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            await pair.PayerService.AcceptIncomingTransferAsync(first.TransferId,
                (FileTransferWriteStreamFactory)((_, _) => Task.FromResult<Stream>(new MemoryStream())), pair.Token);
            await accepted.Task.WaitAsync(pair.Token);
            var oldData = await pair.Dialer.OpenFileTransferDataSessionAsync(session, first.TransferId, pair.Token);
            using (oldData)
            {
                Assert.True(Assert.IsType<bool>(InvokePrivateMethod(pair.Dialer, "StartTunaFallbackProofIfNeeded",
                    "sidecar_remote_closed", session, NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen)));
                InvokePrivateMethod(pair.Dialer, "RecordFileTransferTunaFallbackEvidence",
                    "sidecar_remote_closed", session, NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, false);
                Assert.True(pair.Dialer.IsPostTunaFileFallbackActiveForRouteSelection);
                Assert.True(((IFileTransferRouteStatus)pair.Dialer).IsPostTunaFileFallbackActiveForNewOffer);
                if (hasEpoch)
                    pair.Dialer.ObserveFileTransferV6TransportEpoch(new(session, first.TransferId, FileTransferDirection.Outbound,
                        1, FileTransferTransportHandoffKind.TunaToNormalFallback, FileTransferTransportKind.Tuna,
                        FileTransferTransportKind.RegularNkn, V6TransportEpochState.TargetProofPending, "sidecar_remote_closed", IsUnresolved: true));
                await pair.Dialer.SendFileTransferCancelAsync(new() { SessionId = session, TransferId = first.TransferId }, pair.Token);
            }
            await WaitUntilAsync(() => pair.PayerService.Snapshot.Inbound?.State == FileTransferTransferState.Canceled, TimeSpan.FromSeconds(3));
            Assert.True(pair.Dialer.IsPostTunaFileFallbackActiveForRouteSelection);
            Assert.Equal(hasEpoch, ((IFileTransferRouteStatus)pair.Dialer).IsPostTunaFileFallbackActiveForNewOffer);
            byte[] payload = [0x17, 0xa2, 0x39, 0xfe];
            await pair.PayerService.TryStartSendAsync(new("reverse.bin", payload.Length, "reverse-transfer"),
                _ => Task.FromResult<Stream>(new MemoryStream(payload)), pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision ||
                pair.PayerService.Snapshot.Outbound?.State == FileTransferTransferState.Declined, TimeSpan.FromSeconds(3));
            if (hasEpoch)
            {
                Assert.Equal(FileTransferTransferState.Declined, pair.PayerService.Snapshot.Outbound?.State);
                Assert.Null(pair.DialerService.Snapshot.Inbound);
            }
            else
            {
                Assert.Equal(FileTransferTransferState.AwaitingAcceptance, pair.PayerService.Snapshot.Outbound?.State);
                Assert.Equal(FileTransferTransferState.PendingDecision, pair.DialerService.Snapshot.Inbound?.State);
                Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, pair.DialerService.Snapshot.Inbound?.RouteToken);
                using var destination = new MemoryStream();
                await pair.DialerService.AcceptIncomingTransferAsync("reverse-transfer",
                    (FileTransferWriteStreamFactory)((_, _) => Task.FromResult<Stream>(destination)), pair.Token);
                await WaitUntilAsync(() => pair.PayerService.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                    pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.Completed, TimeSpan.FromSeconds(5));
                Assert.Equal(payload, destination.ToArray());
            }
        });

    [Fact]
    public Task OfferDelivery_SwitchOffThenCancelLetsReverseRegularTransferReceiveCreditAndFirstChunk()
        => WithPreparationHandshakePairAsync(true, async pair =>
        {
            var session = pair.Permit.Scope.SessionId;
            var old = ReplacementTestOffer(session) with { TransferId = "old-before-switch-off" };
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Payer.FileTransferAcceptReceived += (_, e) =>
            {
                if (e.Message.TransferId == old.TransferId) accepted.TrySetResult();
            };
            await pair.Payer.SendFileTransferOfferAsync(old, pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision,
                TimeSpan.FromSeconds(3));
            await pair.DialerService.AcceptIncomingTransferAsync(old.TransferId,
                (FileTransferWriteStreamFactory)((_, _) => Task.FromResult<Stream>(new MemoryStream())), pair.Token);
            await accepted.Task.WaitAsync(pair.Token);
            // Keep the old channel alive: disposing the last channel also drops the session's cached intent.
            using var oldData = await pair.Payer.OpenFileTransferDataSessionAsync(session, old.TransferId, pair.Token);
            Assert.True(Assert.IsType<bool>(InvokePrivateMethod(pair.Payer, "PauseFileTransferDataSessionsForTunaActivationNegotiation",
                "tuna_activation_negotiating", session, "test_offer")));
            var start = GetOperationalLogLength();
            pair.Payer.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, session);
            pair.Dialer.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, session);
            Assert.Contains("event=filetransfer_v6_pending_handoff_recorded;", ReadOperationalLogTail(start));
            await pair.Payer.StopAccelerationAsync("header_switch_off", pair.Token);
            await WaitUntilAsync(() => pair.Dialer.AccelerationNegotiatedLanesForTests == NknAccelerationLaneKind.None,
                TimeSpan.FromSeconds(3));
            Assert.True(pair.Payer.IsPostTunaFileFallbackActiveForRouteSelection);
            await pair.Payer.SendFileTransferCancelAsync(new() { SessionId = session, TransferId = old.TransferId }, pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
                TimeSpan.FromSeconds(3));

            const string reverse = "reverse-after-switch-off";
            byte[] payload = [0x36, 0xa2, 0x71, 0xfd];
            using var destination = new MemoryStream();
            await pair.DialerService.TryStartSendAsync(new("reverse.bin", payload.Length, reverse),
                _ => Task.FromResult<Stream>(new MemoryStream(payload)), pair.Token);
            await WaitUntilAsync(() => pair.PayerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision,
                TimeSpan.FromSeconds(3));
            Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, pair.PayerService.Snapshot.Inbound?.RouteToken);
            await pair.PayerService.AcceptIncomingTransferAsync(reverse,
                (FileTransferWriteStreamFactory)((_, _) => Task.FromResult<Stream>(destination)), pair.Token);
            await WaitUntilAsync(() => pair.PayerService.Snapshot.Inbound?.State == FileTransferTransferState.Completed &&
                pair.DialerService.Snapshot.Outbound?.State == FileTransferTransferState.Completed, TimeSpan.FromSeconds(5));
            Assert.Equal(payload, destination.ToArray());
            var lines = ReadOperationalLogTail(start).Split('\n').Where(line => line.Contains($"transfer_id={reverse};")).ToArray();
            Assert.Contains(lines, line => line.Contains("event=filetransfer_v4_state_received;") &&
                line.Contains("route=regular_nkn_v4_fast;") && line.Contains("available_credit_chunks=1;"));
            Assert.Contains(lines, line => line.Contains("event=filetransfer_data_path_receive_result;") &&
                line.Contains("actual_transport=regular_nkn;") && line.Contains("start_chunk_index=0;"));
            Assert.DoesNotContain(lines, line => line.Contains("event=filetransfer_v6_pending_handoff_replayed;"));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task OfferDelivery_ReverseFileGetsCreditWhileOriginalOwnsPause(bool rollbackPending)
        => WithPreparationHandshakePairAsync(true, async pair =>
        {
            var session = pair.Permit.Scope.SessionId;
            using var a = await pair.Payer.OpenFileTransferDataSessionAsync(session, "paused-original", pair.Token);
            Assert.True(Assert.IsType<bool>(InvokePrivateMethod(pair.Payer, "PauseFileTransferDataSessionsForTunaActivationNegotiation",
                "tuna_activation_negotiating", session, "test_offer")));
            var generation = ((IFileTransferDataSessionGenerationProvider)a).Generation;
            var authority = FileTransferRouteTransitionInfraTests.CreateAuthorityForRoute("owner-a", generation,
                sessionId: session, transferId: a.TransferId);
            var bridge = (IFileTransferRouteTransitionBridge)pair.Payer;
            Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Installed, bridge.BindFileTransferRouteTransitionPause(new(authority)).Status);
            Assert.False(a.IsAvailable);
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            void Rollback() => Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied,
                bridge.ApplyFileTransferRouteTransitionDisposition(new(authority, FileTransferRouteTransitionDispositionKind.RolledBack,
                    null, null, "test_rollback_a")).Status);
            try
            {
                if (rollbackPending)
                {
                    pair.Payer.BeforeTransitionAvailabilityForTests = () => { pending.TrySetResult(); release.Wait(pair.Token); };
                    Rollback();
                    await pending.Task.WaitAsync(pair.Token);
                }
                const string b = "reverse-scoped-credit";
                byte[] bytes = [0x5a, 0x91, 0x7f];
                using var destination = new MemoryStream();
                var start = GetOperationalLogLength();
                await pair.DialerService.TryStartSendAsync(new("reverse.bin", bytes.Length, b),
                    _ => Task.FromResult<Stream>(new MemoryStream(bytes)), pair.Token);
                await WaitUntilAsync(() => pair.PayerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
                Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, pair.PayerService.Snapshot.Inbound?.RouteToken);
                await pair.PayerService.AcceptIncomingTransferAsync(b, (FileTransferWriteStreamFactory)((_, _) => Task.FromResult<Stream>(destination)), pair.Token);
                await WaitUntilAsync(() => pair.PayerService.Snapshot.Inbound?.State == FileTransferTransferState.Completed &&
                    pair.DialerService.Snapshot.Outbound?.State == FileTransferTransferState.Completed, TimeSpan.FromSeconds(5));
                Assert.Equal(bytes, destination.ToArray());
                var lines = ReadOperationalLogTail(start).Split('\n').Where(line => line.Contains($"transfer_id={b};")).ToArray();
                Assert.Contains(lines, line => line.Contains("event=filetransfer_v4_state_received;") && line.Contains("available_credit_chunks=1;"));
                Assert.Contains(lines, line => line.Contains("event=filetransfer_data_path_receive_result;") &&
                    line.Contains("actual_transport=regular_nkn;") && line.Contains("start_chunk_index=0;"));
                Assert.False(a.IsAvailable);
                if (!rollbackPending) Rollback();
            }
            finally { pair.Payer.BeforeTransitionAvailabilityForTests = null; release.Set(); }
            await WaitUntilAsync(() => a.IsAvailable, TimeSpan.FromSeconds(3));
        });

    [Fact]
    public Task OfferDelivery_ResetBetweenNewChannelPauseCaptureAndWriteKeepsChannelAvailable()
        => WithPreparationHandshakePairAsync(true, async pair =>
        {
            var transport = pair.Payer;
            var session = pair.Permit.Scope.SessionId;
            Assert.True(Assert.IsType<bool>(InvokePrivateMethod(transport, "PauseFileTransferDataSessionsForTunaActivationNegotiation",
                "tuna_activation_negotiating", session, "test_offer")));
            transport.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, session);
            var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            transport.NewChannelPauseCapturedForTests = () => { captured.TrySetResult(); release.Wait(pair.Token); };
            var opening = Task.Run(() => transport.OpenFileTransferDataSessionAsync(session, "reverse-pause-write", pair.Token));
            try
            {
                await captured.Task.WaitAsync(pair.Token);
                await transport.StopAccelerationAsync("header_switch_off", pair.Token);
            }
            finally { transport.NewChannelPauseCapturedForTests = null; release.Set(); }
            using var channel = await opening.WaitAsync(pair.Token);
            Assert.True(channel.IsAvailable);
        });

    [Fact]
    public Task OfferDelivery_ResetDuringOrdinaryReplayRejectsOldDispatchAndLateCacheWrite()
        => WithPreparationHandshakePairAsync(true, async pair =>
        {
            var transport = pair.Payer;
            var session = pair.Permit.Scope.SessionId;
            transport.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, session);
            var original = Assert.IsType<long>(GetPrivateField(transport, "fileActivationHandoffResetGeneration"));
            using var channel = await transport.OpenFileTransferDataSessionAsync(session, "reverse-pending-replay", pair.Token);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            transport.OrdinaryFileHandoffBeforeDispatchForTests = () => { entered.TrySetResult(); release.Wait(pair.Token); };
            var notifications = new System.Collections.Concurrent.ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            var subscribe = Task.Run(() => { channel.AvailabilityChanged += (_, args) =>
            {
                if (args.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation) notifications.Enqueue(args);
            }; });
            try
            {
                await entered.Task.WaitAsync(pair.Token);
                await transport.StopAccelerationAsync("sidecar_remote_closed", pair.Token);
            }
            finally { transport.OrdinaryFileHandoffBeforeDispatchForTests = null; release.Set(); }
            await subscribe.WaitAsync(pair.Token);
            Assert.Empty(notifications);

            transport.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, session);
            var current = Assert.Single(notifications);
            Assert.True(current.OrdinaryTunaHandoffIsCurrent!());
            Assert.False(Assert.IsType<bool>(InvokePrivateMethod(transport, "TryRecordPendingFileTransferV6Handoff",
                session, "old_delayed_answer", FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna, "no_subscribers", null, original)));

            // Admit while current, reset before the priority worker can consume the notification.
            var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var consumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var dispatch = new ManualResetEventSlim();
            var firstRead = 1;
            var queued = new FileTransferDataSessionAvailabilityChangedEventArgs(current.IsAvailable, current.Reason,
                current.RequiresResumeRequest, current.HandoffKind, current.TargetTransport)
            {
                OrdinaryTunaHandoffIsCurrent = () =>
                {
                    var valid = current.OrdinaryTunaHandoffIsCurrent();
                    if (Interlocked.Exchange(ref firstRead, 0) == 1) { admitted.TrySetResult(); dispatch.Wait(pair.Token); }
                    else consumed.TrySetResult(valid);
                    return valid;
                }
            };
            var delivery = Task.Run(() => InvokePrivateMethod(pair.PayerService, "OnDataSessionAvailabilityChanged", channel, queued));
            try
            {
                await admitted.Task.WaitAsync(pair.Token);
                await transport.StopAccelerationAsync("header_switch_off", pair.Token);
            }
            finally { dispatch.Set(); }
            await delivery.WaitAsync(pair.Token);
            Assert.False(await consumed.Task.WaitAsync(pair.Token));
        });

    [Theory]
    [InlineData("unanswered")]
    [InlineData("canceled-before-capture")]
    [InlineData("reused-before-capture")]
    [InlineData("cancel-and-failure")]
    [InlineData("both-failed")]
    public Task OfferDelivery_FallbackCaptureKeepsOnlyParticipatingIncarnations(string ordering)
        => WithPreparationHandshakePairAsync(true, async pair =>
        {
            var session = pair.Permit.Scope.SessionId;
            var first = ReplacementTestOffer(session);
            var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pair.Dialer.FileTransferAcceptReceived += (_, e) => accepted.TrySetResult(e.Message.TransferId == first.TransferId);
            await pair.Dialer.SendFileTransferOfferAsync(first, pair.Token);
            await WaitUntilAsync(() => pair.PayerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            await pair.PayerService.AcceptIncomingTransferAsync(first.TransferId,
                (FileTransferWriteStreamFactory)((_, _) => Task.FromResult<Stream>(new MemoryStream())), pair.Token);
            Assert.True(await accepted.Task.WaitAsync(pair.Token));
            using var oldChannel = await pair.Dialer.OpenFileTransferDataSessionAsync(session, first.TransferId, pair.Token);
            async Task CancelFirst()
            {
                await pair.Dialer.SendFileTransferCancelAsync(new() { SessionId = session, TransferId = first.TransferId }, pair.Token);
                await WaitUntilAsync(() => pair.PayerService.Snapshot.Inbound?.State == FileTransferTransferState.Canceled, TimeSpan.FromSeconds(3));
            }
            var twoAdmitted = ordering is "cancel-and-failure" or "both-failed";
            if (ordering == "unanswered" || twoAdmitted)
            {
                var other = twoAdmitted ? "other-active" : "unanswered";
                await pair.Payer.SendFileTransferOfferAsync(first with { TransferId = other }, pair.Token);
                await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.TransferId == other, TimeSpan.FromSeconds(3));
                if (twoAdmitted)
                    await pair.DialerService.AcceptIncomingTransferAsync(other,
                        (FileTransferWriteStreamFactory)((_, _) => Task.FromResult<Stream>(new MemoryStream())), pair.Token);
            }
            else
            {
                await CancelFirst();
                if (ordering == "reused-before-capture")
                {
                    await pair.Dialer.SendFileTransferOfferAsync(first with { FileName = "replacement.bin" }, pair.Token);
                    await WaitUntilAsync(() => pair.PayerService.Snapshot.Inbound?.FileName == "replacement.bin", TimeSpan.FromSeconds(3));
                }
            }
            Assert.True(Assert.IsType<bool>(InvokePrivateMethod(pair.Dialer, "StartTunaFallbackProofIfNeeded",
                "sidecar_remote_closed", session, NknAccelerationLaneKind.File)));
            InvokePrivateMethod(pair.Dialer, "RecordFileTransferTunaFallbackEvidence",
                "sidecar_remote_closed", session, NknAccelerationLaneKind.File, false);
            var status = (IFileTransferRouteStatus)pair.Dialer;
            if (twoAdmitted)
            {
                if (ordering == "both-failed")
                {
                    await pair.Dialer.SendFileTransferErrorAsync(new() { SessionId = session, TransferId = first.TransferId, ErrorCode = "send_failed" }, pair.Token);
                    await WaitUntilAsync(() => pair.PayerService.Snapshot.Inbound?.State == FileTransferTransferState.Failed, TimeSpan.FromSeconds(3));
                }
                else await CancelFirst();
                Assert.True(status.IsPostTunaFileFallbackActiveForNewOffer);
                await pair.Payer.SendFileTransferErrorAsync(new() { SessionId = session, TransferId = "other-active", ErrorCode = "send_failed" }, pair.Token);
                await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.Failed, TimeSpan.FromSeconds(3));
                if (ordering == "both-failed")
                {
                    Assert.True(status.IsPostTunaFileFallbackActiveForNewOffer);
                    return;
                }
            }
            if (ordering == "unanswered")
            {
                Assert.True(status.IsPostTunaFileFallbackActiveForNewOffer);
                await CancelFirst();
                Assert.Equal(FileTransferTransferState.PendingDecision, pair.DialerService.Snapshot.Inbound?.State);
                var declined = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                pair.Payer.FileTransferDeclineReceived += (_, e) => declined.TrySetResult(e.Message.TransferId == "unanswered");
                await pair.DialerService.DeclineIncomingTransferAsync("unanswered", "not_now", pair.Token);
                Assert.True(await declined.Task.WaitAsync(pair.Token));
            }
            Assert.True(status.IsPostTunaFileFallbackActiveForRouteSelection);
            Assert.False(status.IsPostTunaFileFallbackActiveForNewOffer);
            await pair.PayerService.TryStartSendAsync(new("reverse.bin", 4, "reverse-check"),
                _ => Task.FromResult<Stream>(new MemoryStream(new byte[4])), pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.TransferId == "reverse-check" ||
                pair.PayerService.Snapshot.Outbound?.State == FileTransferTransferState.Declined, TimeSpan.FromSeconds(3));
            Assert.Equal(FileTransferTransferState.PendingDecision, pair.DialerService.Snapshot.Inbound?.State);
            Assert.Equal("reverse-check", pair.DialerService.Snapshot.Inbound?.TransferId);
            if (ordering == "reused-before-capture")
                Assert.Equal(FileTransferTransferState.PendingDecision, pair.PayerService.Snapshot.Inbound?.State);
        });

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
            var writer = new PreparationHandshakeBarrier();
            pair.DialerClient.ShouldDeliverSendAsync = async (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var e) && e.Type == MsgType.FileTransferAccept)
                {
                    await Task.Yield();
                    writer.Entered.TrySetResult();
                    await writer.Release.Task.WaitAsync(pair.Token);
                    throw new IOException("accept write failed before delivery");
                }
                return true;
            };
            await pair.Payer.SendFileTransferOfferAsync(offer, pair.Token);
            await WaitUntilAsync(() => pair.DialerService.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, TimeSpan.FromSeconds(3));
            var accepting = pair.DialerService.AcceptIncomingTransferAsync(offer.TransferId,
                (FileTransferWriteStreamFactory)((_, _) => Task.FromResult<Stream>(new MemoryStream())), pair.Token);
            try
            {
                await writer.Entered.Task.WaitAsync(pair.Token);
                Assert.True(Assert.IsType<bool>(InvokePrivateMethod(pair.Dialer, "StartTunaFallbackProofIfNeeded",
                    "sidecar_remote_closed", offer.SessionId, NknAccelerationLaneKind.File)));
                InvokePrivateMethod(pair.Dialer, "RecordFileTransferTunaFallbackEvidence",
                    "sidecar_remote_closed", offer.SessionId, NknAccelerationLaneKind.File, false);
                Assert.True(((IFileTransferRouteStatus)pair.Dialer).IsPostTunaFileFallbackActiveForNewOffer);
            }
            finally { writer.Release.TrySetResult(); }
            var result = await accepting.WaitAsync(pair.Token);
            Assert.Equal(FileTransferTransferState.Failed, result!.State);
            Assert.Equal(offer.OfferInstanceId, await error.Task.WaitAsync(pair.Token));
            Assert.True(pair.Dialer.IsPostTunaFileFallbackActiveForRouteSelection);
            Assert.False(((IFileTransferRouteStatus)pair.Dialer).IsPostTunaFileFallbackActiveForNewOffer);
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
                Assert.True(Assert.IsType<bool>(InvokePrivateMethod(pair.Dialer, "StartTunaFallbackProofIfNeeded",
                    "sidecar_remote_closed", offer.SessionId, NknAccelerationLaneKind.File)));
                InvokePrivateMethod(pair.Dialer, "RecordFileTransferTunaFallbackEvidence",
                    "sidecar_remote_closed", offer.SessionId, NknAccelerationLaneKind.File, false);
                await pair.Payer.SendFileTransferSessionOpenAsync(new() { SessionId = offer.SessionId, TransferId = offer.TransferId,
                    ProtocolVersion = 4, SessionRole = FileTransferProtocol.SessionRoleSender,
                    FileTransferRoute = offer.FileTransferRoute, ChunkSizeBytes = 1024, InitialPipelineDepth = 8 }, pair.Token);
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(3), pair.Token);
                Assert.False(accept.IsCompleted);
            }
            finally { writer.Release.TrySetResult(); }
            Assert.NotEqual(FileTransferTransferState.Failed, (await accept.WaitAsync(pair.Token))!.State);
            Assert.True(((IFileTransferRouteStatus)pair.Dialer).IsPostTunaFileFallbackActiveForNewOffer);
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
