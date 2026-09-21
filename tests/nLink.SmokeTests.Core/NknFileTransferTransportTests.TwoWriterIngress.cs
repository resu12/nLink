using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknFileTransferTransportTests
{
    // Two real test hosts/loggers, connected only by an offline packet pipe.
    // The pipe carries original encoded bytes, never log records or authority.
    internal static async Task ProduceTwoWriterIngressFixture()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var ct = deadline.Token;
        var sender = Environment.GetEnvironmentVariable("NLINK_TWO_WRITER_PEER") == "sender";
        var directory = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR")!;
        var pipeName = Environment.GetEnvironmentVariable("NLINK_TWO_WRITER_PIPE") ?? "nlink-ingress-" + Guid.NewGuid().ToString("N");
        using var pipe = sender
            ? (PipeStream)new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)
            : new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var peer = sender ? null : new Process { StartInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        } };
        Task<string>? stdout = null, stderr = null;
        try
        {
            if (peer is not null)
            {
                peer.StartInfo.ArgumentList.Add("vstest");
                peer.StartInfo.ArgumentList.Add(typeof(FileTransferPublicationUseConsumerTests).Assembly.Location);
                peer.StartInfo.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName~PublicationUseConsumer_GeneratedRecordsAreBoundAndFailClosed");
                peer.StartInfo.Environment["NLINK_TWO_WRITER_PEER"] = "sender";
                peer.StartInfo.Environment["NLINK_TWO_WRITER_PIPE"] = pipeName;
                peer.StartInfo.Environment["NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH"] = Path.Combine(directory, "sender.log");
                peer.Start();
                stdout = peer.StandardOutput.ReadToEndAsync();
                stderr = peer.StandardError.ReadToEndAsync();
                await ((NamedPipeServerStream)pipe).WaitForConnectionAsync(ct);
            }
            else { await ((NamedPipeClientStream)pipe).ConnectAsync(ct); }

            await RunTwoWriterIngressPeerAsync(sender, pipe, directory, ct);
            if (peer is not null)
            {
                await peer.WaitForExitAsync(ct);
                Assert.True(peer.ExitCode == 0, await stdout! + Environment.NewLine + await stderr!);
            }
        }
        finally
        {
            if (peer is not null && !peer.HasExited) { peer.Kill(entireProcessTree: true); }
        }
    }

    private static async Task RunTwoWriterIngressPeerAsync(bool sender, PipeStream pipe, string directory, CancellationToken ct)
    {
        FakeNknClient.ResetNetwork();
        const string senderAddress = "sender.two-writer.address", receiverAddress = "receiver.two-writer.address";
        const string transferId = "transfer_two_writer_ingress";
        var finalFallbackFixture = Environment.GetEnvironmentVariable("NLINK_PUBLICATION_FINAL_FALLBACK_FIXTURE") == "1";
        var serviceFixture = Environment.GetEnvironmentVariable("NLINK_PUBLICATION_SERVICE_INGRESS_FIXTURE") == "1";
        var serviceFallback = finalFallbackFixture && serviceFixture;
        var startChunk = serviceFallback ? 100 : 12;
        var chunkSize = serviceFallback ? 21 * 1024 : 1024;
        var tunaPayloadMode = Environment.GetEnvironmentVariable("NLINK_PUBLICATION_TUNA_PAYLOAD_MODE") ?? "none";
        using var tunaLane = tunaPayloadMode == "none" ? null : new FakeNknAccelerationLane();
        var client = new FakeNknClient(sender ? senderAddress : receiverAddress);
        using var transport = new NknSignalingTransport(client, NknTransportOptions.Load(),
            new NknIdentity(sender ? "two-writer-sender" : "two-writer-receiver", client.Address),
            NknTunaAccelerationOptions.Disabled, tunaLane);
        await using var link = new IngressPacketPipe(pipe, client, ct, tunaLane);
        var approved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var join = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Approved += (_, _) => approved.TrySetResult();
        transport.IncomingJoinRequest += (_, e) => join.TrySetResult(e);
        var offered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.FileTransferOfferReceived += (_, _) => offered.TrySetResult();
        transport.FileTransferAcceptReceived += (_, _) => accepted.TrySetResult();
        transport.FileTransferSessionOpenReceived += (_, _) => opened.TrySetResult();

        if (sender)
        {
            var raw = await link.WaitAsync("invite");
            var validation = InviteTokenServiceFactory.CreateInviteTokenValidator().Validate(raw, DateTimeOffset.UtcNow);
            Assert.True(validation.IsSuccess, validation.Message);
            await transport.JoinByInviteAsync(raw, validation.Invite!, ct);
        }
        else
        {
            await transport.HostByAddressAsync(ct);
            _ = CreateValidatedInviteForTarget(new PeerAddress(receiverAddress), out var raw,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer, boundHelperAddress: new PeerAddress(senderAddress));
            await link.SignalAsync("invite", raw);
            var pending = await join.Task.WaitAsync(ct);
            await pending.ApproveAsync(pending.CreateApprovalDecision(), ct);
        }
        await approved.Task.WaitAsync(ct);
        var sessionId = transport.CurrentSessionSecurityState.SessionId!.Value.Value;
        if (sender)
        {
            await transport.SendFileTransferOfferAsync(new FileTransferOfferV2
            {
                SessionId = sessionId, TransferId = transferId, FileName = "two-writer.bin", FileSizeBytes = serviceFallback ? 256L * chunkSize : 131072,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            }, ct);
            await accepted.Task.WaitAsync(ct);
            await transport.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2
            {
                SessionId = sessionId, TransferId = transferId, ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                SessionRole = FileTransferProtocol.SessionRoleSender, ChunkSizeBytes = chunkSize, InitialPipelineDepth = 8,
            }, ct);
        }
        else
        {
            await offered.Task.WaitAsync(ct);
            await transport.SendFileTransferAcceptAsync(new FileTransferAcceptV1
            {
                SessionId = sessionId, TransferId = transferId, AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            }, ct);
            await opened.Task.WaitAsync(ct);
        }
        using var dataSession = await transport.OpenFileTransferDataSessionAsync(sessionId, transferId, ct);
        if (serviceFixture && !sender)
        {
            await link.SignalAsync("regular-ready");
            await link.WaitAsync("held");
            await SessionFileTransferV4SenderTests.CreateSecondTunaPublicationEvidenceFixtureAsync(
                replayPostCommitCallbacks: false, sessionId: sessionId, suppliedTransferId: transferId,
                receivePeerYield: async hints =>
                {
                    foreach (var hint in hints)
                    {
                        Assert.IsAssignableFrom<IFileTransferRouteHintObserver>(transport).ObserveFileTransferRouteHint(hint);
                    }
                    await link.SignalAsync("fallback-ready");
                    return Assert.IsType<FileTransferFallbackTailYieldFrameV6>(await dataSession.ReceiveAsync(ct));
                },
                afterSecondTuna: async (publication, hints) =>
                {
                    var hint = Assert.Single(hints.Where(hint => hint.PublicationAuthority?.LiveRouteEpoch == 3));
                    Assert.IsAssignableFrom<IFileTransferRouteHintObserver>(transport).ObserveFileTransferRouteHint(hint);
                    var receivedTunaStart = -1;
                    if (tunaLane is not null)
                    {
                        transport.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
                        await link.SignalAsync("tuna-receiver-ready");
                        await link.WaitAsync("tuna-payload-sent");
                        if (tunaPayloadMode != "missing_receive")
                        {
                            var received = Assert.IsType<FileTransferChunkBatchFrameV4>(await dataSession.ReceiveAsync(ct));
                            receivedTunaStart = tunaPayloadMode == "wrong_range" ? 13 : 12;
                            Assert.Equal(receivedTunaStart, received.StartChunkIndex);
                            Assert.Equal(2, received.ChunkCount);
                            Assert.Equal(Enumerable.Repeat((byte)17, 1024), received.DataSegments[0]);
                            Assert.Equal(Enumerable.Repeat((byte)34, 1024), received.DataSegments[1]);
                        }
                        await link.SignalAsync("tuna-payload-checked");
                    }
                    await CheckDelayedIngressAsync(transport, dataSession, publication, link, peerYieldReceived: true, receivedTunaStart,
                        startChunk, chunkSize);
                },
                sendLocalYield: tunaLane is null && !serviceFallback ? null : frame => dataSession.SendAsync(frame, ct),
                prepareFinalFallbackReceive: !serviceFallback ? null : async (hints, repairId) =>
                {
                    foreach (var hint in hints)
                    {
                        Assert.IsAssignableFrom<IFileTransferRouteHintObserver>(transport).ObserveFileTransferRouteHint(hint);
                    }
                    await link.SignalAsync("final-fallback-ready", repairId ?? "");
                    await link.WaitAsync("final-fallback-ready");
                },
                receiveData: !serviceFallback ? null : dataSession.ReceiveWithMetadataAsync);
            if (serviceFallback) { await link.SignalAsync("final-fallback-delivered"); }
            await WriteProcessAsync();
            return;
        }
        using var history = new FileTransferPublicationIngressHistory(sessionId, transferId,
            sender ? FileTransferDirection.Outbound : FileTransferDirection.Inbound, stopAtFallbackYield: serviceFixture);
        var evidence = history.Publish(0);
        PublishDataPathFixtureAuthority(transport, history.Tokens[0], evidence);
        await link.SignalAsync("regular-ready");
        await link.WaitAsync("regular-ready");
        if (sender)
        {
            link.HoldDataFrame = true;
            var frame = new FileTransferChunkBatchFrameV4
            {
                SessionId = sessionId, TransferId = transferId, StartChunkIndex = startChunk, ChunkCount = 2,
                DataSegments = [Enumerable.Repeat((byte)17, chunkSize).ToArray(), Enumerable.Repeat((byte)34, chunkSize).ToArray()],
                BatchProfile = "v4_default_21k",
            };
            var authority = Assert.IsType<FileTransferDataPathSendAuthority>(typeof(SessionFileTransferService)
                .GetMethod("CreateDataPathSendAuthority", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [history.Tokens[0], "two_writer_ingress_fixture"]));
            FileTransferPublicationUseEvidence.CaptureSend(authority, evidence, FileTransferPublicationUseKind.V4PhysicalSubmission);
            await Assert.IsAssignableFrom<IFileTransferDataPathAuthoritySession>(dataSession).SendAsync(frame, authority, ct);
            Assert.NotNull(link.HeldFrame);
            await link.SignalAsync("held");
        }
        else { await link.WaitAsync("held"); }
        for (var i = 1; i < history.Tokens.Count; i++) { PublishDataPathFixtureAuthority(transport, history.Tokens[i], history.Publish(i)); }
        if (serviceFixture)
        {
            history.PublishFallbackYield();
            await link.WaitAsync("fallback-ready");
            link.HoldDataFrame = false;
            await dataSession.SendAsync(history.FallbackYieldFrame!, ct);
            if (tunaLane is not null || serviceFallback)
            {
                var peerFrame = Assert.IsType<FileTransferFallbackTailYieldFrameV6>(await dataSession.ReceiveAsync(ct));
                evidence = history.ActivateFromPeerYield(peerFrame);
                var publication = history.Tokens[3];
                PublishDataPathFixtureAuthority(transport, publication, evidence);
            }
            if (tunaLane is not null)
            {
                var publication = history.Tokens[3];
                transport.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
                await link.WaitAsync("tuna-receiver-ready");
                var authority = Assert.IsType<FileTransferDataPathSendAuthority>(typeof(SessionFileTransferService)
                    .GetMethod("CreateDataPathSendAuthority", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [publication, "two_writer_exact_tuna_payload_fixture"]));
                FileTransferPublicationUseEvidence.CaptureSend(authority, evidence, FileTransferPublicationUseKind.V4PhysicalSubmission);
                await Assert.IsAssignableFrom<IFileTransferDataPathAuthoritySession>(dataSession).SendAsync(new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId, TransferId = transferId, StartChunkIndex = tunaPayloadMode == "wrong_range" ? 13 : 12,
                    ChunkCount = 2, BatchProfile = "v4_default_21k",
                    DataSegments = [Enumerable.Repeat((byte)17, 1024).ToArray(), Enumerable.Repeat((byte)34, 1024).ToArray()],
                }, authority, ct);
                var sent = Assert.Single(tunaLane.Sent);
                Assert.Equal(NknBridgeChannel.Bulk, sent.Lane);
                if (tunaPayloadMode != "missing_receive")
                {
                    await link.SendAsync(new IngressPacketPipe.Packet(null, null, null, null, null, sent.Lane, sent.Payload, Tuna: true));
                }
                await link.SignalAsync("tuna-payload-sent");
                await link.WaitAsync("tuna-payload-checked");
            }
        }
        if (sender)
        {
            await link.SignalAsync("second-tuna-ready");
            await link.WaitAsync("second-tuna-ready");
            // Replay the same original packet twice; the second must deduplicate.
            await link.SendAsync(link.HeldFrame!);
            await link.SendAsync(link.HeldFrame!);
            await link.SignalAsync("replayed");
            await link.WaitAsync("rejection-checked");
        }
        else { await CheckDelayedIngressAsync(transport, dataSession, history.Tokens[3], link); }
        if (finalFallbackFixture)
        {
            var beforeFallback = history.Tokens[3];
            var fallbackEvidence = history.PublishFinalFallback(serviceFallback ? 1 : 2);
            var fallback = history.Tokens[4];
            PublishDataPathFixtureAuthority(transport, fallback, fallbackEvidence);
            var states = Assert.IsAssignableFrom<IDictionary>(GetPrivateField(transport, "fileTransferStates"));
            var fallbackState = states[transferId];
            Assert.NotNull(fallbackState);
            // A stale publication cannot roll the adapter back to second Tuna.
            PublishDataPathFixtureAuthority(transport, beforeFallback);
            Assert.Equal(fallbackState, states[transferId]);
            await link.SignalAsync("final-fallback-ready");
            var repairId = await link.WaitAsync("final-fallback-ready");
            if (sender)
            {
                link.HoldDataFrame = false;
                var authority = Assert.IsType<FileTransferDataPathSendAuthority>(typeof(SessionFileTransferService)
                    .GetMethod("CreateDataPathSendAuthority", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [fallback, "independent_final_fallback_fixture"]));
                await Assert.IsAssignableFrom<IFileTransferDataPathAuthoritySession>(dataSession).SendAsync(new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId, TransferId = transferId, TransportEpoch = serviceFallback ? 1 : 2,
                    StartChunkIndex = startChunk, ChunkCount = 2, BatchProfile = "v4_default_21k",
                    RepairRequestId = string.IsNullOrEmpty(repairId) ? null : repairId,
                    DataSegments = [Enumerable.Repeat((byte)17, chunkSize).ToArray(), Enumerable.Repeat((byte)34, chunkSize).ToArray()],
                }, authority, ct);
                await link.WaitAsync("final-fallback-delivered");
            }
            else
            {
                var received = Assert.IsType<FileTransferChunkBatchFrameV6>(await dataSession.ReceiveAsync(ct));
                Assert.Equal(12, received.StartChunkIndex);
                Assert.Equal(2, received.ChunkCount);
                Assert.Equal(2, received.TransportEpoch);
                Assert.Equal(Enumerable.Repeat((byte)17, 1024), received.DataSegments[0]);
                Assert.Equal(Enumerable.Repeat((byte)34, 1024), received.DataSegments[1]);
                var receive = Assert.Single(FindOperationalLogLines(line => line.Contains("event=filetransfer_data_path_receive_result;", StringComparison.Ordinal) &&
                    line.Contains("frame_type=filetransfer.chunk_batch.v6;", StringComparison.Ordinal)));
                AssertDataPathPublication(receive, fallback);
                var dispatched = Assert.Single(FindOperationalLogLines(line => line.Contains("event=filetransfer_data_frame_dispatched;", StringComparison.Ordinal)));
                Assert.Contains("frame_type=filetransfer.chunk_batch.v6;", dispatched, StringComparison.Ordinal);
                await link.SignalAsync("final-fallback-delivered");
            }
        }
        await WriteProcessAsync();

        async Task WriteProcessAsync()
        {
            await File.WriteAllTextAsync(Path.Combine(directory, sender ? "sender-process.json" : "receiver-process.json"),
                JsonSerializer.Serialize(new { ProcessId = Environment.ProcessId, LogPath = LocalOperationalLog.LogFilePath, Sender = sender }), ct);
            link.ThrowIfFaulted();
        }
    }

    private static async Task CheckDelayedIngressAsync(NknSignalingTransport transport, IFileTransferDataSession dataSession,
        RoutePublicationToken publication, IngressPacketPipe link, bool peerYieldReceived = false, int receivedTunaStart = -1,
        int startChunk = 12, int chunkSize = 1024)
    {
        var states = Assert.IsAssignableFrom<IDictionary>(GetPrivateField(transport, "fileTransferStates"));
        var before = states[publication.TransferId];
        Assert.NotNull(before);
        var dispatchedBefore = FindOperationalLogLines(line => line.Contains("event=filetransfer_data_frame_dispatched;", StringComparison.Ordinal));
        if (peerYieldReceived)
        {
            Assert.Contains("frame_type=filetransfer.fallback_tail.yield.v6;", dispatchedBefore[0], StringComparison.Ordinal);
            Assert.Equal(receivedTunaStart < 0 ? 1 : 2, dispatchedBefore.Count);
            if (receivedTunaStart >= 0)
            {
                Assert.Contains("frame_type=filetransfer.chunk_batch.v4;", dispatchedBefore[1], StringComparison.Ordinal);
                var tunaReceive = Assert.Single(FindOperationalLogLines(line => line.Contains("event=filetransfer_data_path_receive_result;", StringComparison.Ordinal) &&
                    line.Contains("actual_transport=tuna;", StringComparison.Ordinal)));
                Assert.Contains($"start_chunk_index={receivedTunaStart};", tunaReceive, StringComparison.Ordinal);
            }
        }
        else { Assert.Empty(dispatchedBefore); }
        await link.SignalAsync("second-tuna-ready");
        await link.WaitAsync("second-tuna-ready");
        await link.WaitAsync("replayed");
        await WaitUntilAsync(() => FindOperationalLogLines(line => line.Contains("event=filetransfer_data_frame_ignored;", StringComparison.Ordinal)).Count == 1,
            TimeSpan.FromSeconds(3));
        var rows = FindOperationalLogLines(line => line.Contains("receive_disposition=rejected_before_dispatch;", StringComparison.Ordinal));
        Assert.Equal(3, rows.Count);
        Assert.All(rows, line =>
        {
            AssertDataPathPublication(line, publication);
            Assert.Contains("authority_effect=none;", line, StringComparison.Ordinal);
            AssertDataPathFrame(line, startChunk, chunkSize);
        });
        Assert.Equal(before, states[publication.TransferId]);
        Assert.Equal(dispatchedBefore, FindOperationalLogLines(line => line.Contains("event=filetransfer_data_frame_dispatched;", StringComparison.Ordinal)));
        using var noDelivery = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dataSession.ReceiveAsync(noDelivery.Token));
        await link.SignalAsync("rejection-checked");
    }

    private sealed class IngressPacketPipe : IAsyncDisposable
    {
        internal sealed record Packet(string? Signal, string? Value, string? Source, string? Destination, string? Topic, NknBridgeChannel Channel, byte[]? Payload, bool Tuna = false);
        private readonly StreamReader reader;
        private readonly StreamWriter writer;
        private readonly SemaphoreSlim writes = new(1);
        private readonly CancellationTokenSource stop;
        private readonly CancellationToken ct;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> signals = new();
        private readonly Task pump;
        internal bool HoldDataFrame { get; set; }
        internal Packet? HeldFrame { get; private set; }

        internal IngressPacketPipe(PipeStream pipe, FakeNknClient client, CancellationToken ct, FakeNknAccelerationLane? tunaLane = null)
        {
            stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            this.ct = ct = stop.Token;
            reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            client.ShouldDeliverSendAsync = (_, _, _) => Task.FromResult(false);
            client.BeforeSendCoreAsync = async (destination, payload, channel, _) =>
            {
                var source = channel == NknBridgeChannel.Bulk ? client.BulkAddress : channel == NknBridgeChannel.Media ? client.MediaAddress : client.Address;
                var packet = new Packet(null, null, source, destination, null, channel, payload.ToArray());
                if (HoldDataFrame && EnvelopeCodec.TryDeserialize(payload, out var envelope) && envelope.Type == MsgType.FileTransferDataFrame)
                { Assert.Null(HeldFrame); HeldFrame = packet; return; }
                await SendAsync(packet);
            };
            client.BeforePublishAsync = (topic, payload, _) => SendAsync(new Packet(null, null, client.Address, null, topic, NknBridgeChannel.Control, payload.ToArray()));
            pump = Task.Run(async () =>
            {
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    var packet = JsonSerializer.Deserialize<Packet>(line)!;
                    if (packet.Signal is not null) { Signal(packet.Signal).TrySetResult(packet.Value ?? ""); }
                    else if (packet.Tuna) { Assert.NotNull(tunaLane); tunaLane.InjectInbound(packet.Channel, packet.Payload!); }
                    else
                    {
                        if (packet.Topic is null) { Assert.Contains(packet.Destination, new[] { client.Address, client.MediaAddress, client.BulkAddress }); }
                        typeof(FakeNknClient).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(client, [new NknIncomingMessage(packet.Source!, packet.Payload!, packet.Topic is not null, packet.Topic, packet.Channel)]);
                    }
                }
            }, ct);
        }

        private TaskCompletionSource<string> Signal(string name) => signals.GetOrAdd(name, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        internal async Task<string> WaitAsync(string name)
        {
            var signal = Signal(name).Task;
            if (await Task.WhenAny(signal, pump).WaitAsync(ct) == pump)
            {
                await pump;
                throw new InvalidOperationException("Peer pipe closed before " + name);
            }
            return await signal.WaitAsync(ct);
        }
        internal Task SignalAsync(string name, string value = "") => SendAsync(new Packet(name, value, null, null, null, NknBridgeChannel.Control, null));
        internal async Task SendAsync(Packet packet)
        {
            await writes.WaitAsync(ct);
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(packet).AsMemory(), ct); }
            finally { writes.Release(); }
        }
        internal void ThrowIfFaulted() { if (pump.IsFaulted) { pump.GetAwaiter().GetResult(); } }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            try { await pump; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { reader.Dispose(); writer.Dispose(); writes.Dispose(); stop.Dispose(); }
        }
    }
}
