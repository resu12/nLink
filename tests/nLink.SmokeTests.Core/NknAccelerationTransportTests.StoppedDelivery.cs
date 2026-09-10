using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("FileTransferInfraSuite", "TunaStoppedDelivery")]
    public async Task TunaSidecarClient_WrittenBatchIsReceivedOnlyWhenRelayForwardsBeforeStop(bool forwardBeforeStop)
    {
        var traceLines = new ConcurrentQueue<string>();
        using var traceOverride = NknTunaDeliveryTrace.OverrideForTests(
            new NknTunaDeliveryTrace.Producer(Guid.NewGuid().ToString("D"), traceLines.Enqueue));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        using var sender = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
        using var receiver = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
        using var receiverLane = new NknTunaAccelerationLane(NknTunaAccelerationOptions.Disabled);
        var received = new ConcurrentQueue<NknIncomingMessage>();
        var delivered = new TaskCompletionSource<NknIncomingMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.MessageReceived += (_, message) =>
        {
            received.Enqueue(message);
            delivered.TrySetResult(message);
        };
        listener.Start();
        try
        {
            using var senderRelay = await ConnectDeliveryTraceRelayAsync(listener, sender, cts.Token);
            using var receiverRelay = await ConnectDeliveryTraceRelayAsync(listener, receiver, cts.Token);
            // Install the real connected client for the production StopAsync path.
            // This fixture does not negotiate a Tuna route or start a sidecar process.
            SetPrivateField(receiverLane, "client", receiver);
            Assert.True(receiverLane.IsAvailable);

            var batch = new FileTransferChunkBatchFrameV4
            {
                SessionId = "session_tuna_stopped_delivery",
                TransferId = "transfer_tuna_stopped_delivery",
                StartChunkIndex = 4602,
                ChunkCount = 3,
                DataSegments = Enumerable.Range(1, 3)
                    .Select(value => Enumerable.Repeat((byte)value, 21504).ToArray()).ToArray(),
                BatchProfile = "v4_default_21k",
            };
            var plaintext = FileTransferDataFrameCodec.SerializeLegacyV4(batch);
            var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(); // Fixture key only.
            var metadata = new SessionSecureEnvelopeMetadata(SessionSecureMessageFamily.FileTransfer,
                "file_transfer_data_frame", new SessionId(batch.SessionId),
                new PeerAddress("sender.tuna.stopped.delivery"), 1, batch.TransferId);
            var envelope = new Envelope(1, "loopback", Guid.NewGuid().ToString("N"),
                MsgType.FileTransferDataFrame, SessionSecureEnvelopeCodec.Encrypt(key, metadata, plaintext),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);
            var envelopeBytes = EnvelopeCodec.Serialize(envelope);

            using (var sendTrace = NknTunaDeliveryTrace.BeginSend(envelopeBytes))
            {
                Assert.True(await sender.TrySendAsync(NknBridgeChannel.Bulk, envelopeBytes, cts.Token));
                sendTrace!.Result("; start_chunk_index=4602; frame_chunk_count=3");
            }
            var warmup = await NknTunaSidecarFrameProtocol.ReadFrameAsync(senderRelay.GetStream(), cts.Token);
            Assert.Equal(NknTunaSidecarFrameType.Ping, warmup.Type);
            var held = await NknTunaSidecarFrameProtocol.ReadFrameAsync(senderRelay.GetStream(), cts.Token);
            Assert.Equal(NknTunaSidecarFrameType.Data, held.Type);
            Assert.Equal(NknTunaSidecarLane.Bulk, held.Lane);
            Assert.Equal(1UL, held.Sequence);
            Assert.Equal(envelopeBytes, held.Payload);
            await WaitUntilAsync(() => sender.GetDiagnosticsSnapshot().BulkFramesWritten == 1, TimeSpan.FromSeconds(2));
            Assert.Equal(1, sender.GetDiagnosticsSnapshot().BulkFramesAccepted);
            Assert.Equal(1, sender.GetDiagnosticsSnapshot().BulkFramesWritten);
            Assert.Equal(0, receiver.GetDiagnosticsSnapshot().BulkFramesReceived);
            Assert.Empty(received);

            // The relay has read the exact local IPC frame but has not forwarded it.
            // Local acceptance AND a completed socket write are not peer receipt.
            if (forwardBeforeStop)
            {
                await NknTunaSidecarFrameProtocol.WriteFrameAsync(receiverRelay.GetStream(), held.Type,
                    held.Lane, held.Sequence, held.TimestampUtcMs, held.Payload, cts.Token);
                var message = await delivered.Task.WaitAsync(cts.Token);
                Assert.Equal(NknBridgeChannel.Bulk, message.Channel);
                Assert.Equal(envelopeBytes, message.Payload);
                Assert.True(EnvelopeCodec.TryDeserialize(message.Payload, out var receivedEnvelope));
                Assert.Equal(envelope.MessageId, receivedEnvelope.MessageId);
                Assert.Equal(MsgType.FileTransferDataFrame, receivedEnvelope.Type);
                var decoded = SessionSecureEnvelopeCodec.Decrypt(key, receivedEnvelope.Payload,
                    new SessionSecureEnvelopeExpectation(metadata.Family, metadata.MessageType,
                        metadata.SessionId, metadata.SenderIdentity, metadata.RequestId));
                Assert.Equal(metadata, decoded.Metadata);
                Assert.Equal(plaintext, decoded.Plaintext);
                Assert.True(FileTransferDataFrameCodec.TryDeserializeLegacyV4(decoded.Plaintext, out var receivedFrame));
                var receivedBatch = Assert.IsType<FileTransferChunkBatchFrameV4>(receivedFrame);
                Assert.Equal(4602, receivedBatch.StartChunkIndex);
                Assert.Equal(3, receivedBatch.ChunkCount);
                Assert.Equal(plaintext, FileTransferDataFrameCodec.SerializeLegacyV4(receivedBatch));
            }

            await receiverLane.StopAsync("test_stop_before_remaining_relay_delivery", cts.Token);
            Assert.False(receiverLane.IsAvailable);
            Assert.False(receiver.IsAvailable);
            // Join the actual I/O loops before checking absence; no timed negative assertion.
            await Task.WhenAll(
                Assert.IsAssignableFrom<Task>(GetPrivateField(receiver, "readerTask")),
                Assert.IsAssignableFrom<Task>(GetPrivateField(receiver, "writerTask"))).WaitAsync(cts.Token);
            Assert.Equal(forwardBeforeStop ? 1 : 0, receiver.GetDiagnosticsSnapshot().BulkFramesReceived);
            Assert.Equal(forwardBeforeStop ? 1 : 0, received.Count);
            Assert.Equal(forwardBeforeStop, delivered.Task.IsCompletedSuccessfully);
            Assert.Equal(1, sender.GetDiagnosticsSnapshot().BulkFramesAccepted);
            Assert.Equal(1, sender.GetDiagnosticsSnapshot().BulkFramesWritten);
            var wire = NknTunaDeliveryTrace.WireHash(envelopeBytes);
            foreach (var stage in new[] { "queue_accepted", "ipc_write_entered", "ipc_write_completed", "app_send_result" })
                Assert.Single(traceLines.Where(line => line.Contains($"trace_stage={stage};", StringComparison.Ordinal) &&
                    line.Contains($"trace_wire={wire};", StringComparison.Ordinal) && line.Contains("trace_seq=1;", StringComparison.Ordinal)));
            Assert.Equal(forwardBeforeStop ? 1 : 0, traceLines.Count(line => line.Contains("trace_stage=ipc_read_completed;", StringComparison.Ordinal)));
            Assert.Equal(forwardBeforeStop ? 1 : 0, traceLines.Count(line => line.Contains("trace_stage=callback_entered;", StringComparison.Ordinal)));
            Assert.DoesNotContain(traceLines, line => line.Contains("trace_tail=complete", StringComparison.Ordinal));
        }
        finally
        {
            sender.Dispose();
            receiver.Dispose();
            listener.Stop();
            var ioTasks = new[] { sender, receiver }.SelectMany(client => new[]
            {
                GetPrivateField(client, "readerTask"), GetPrivateField(client, "writerTask"),
            }).OfType<Task>();
            await Task.WhenAll(ioTasks).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<TcpClient> ConnectDeliveryTraceRelayAsync(
        TcpListener listener, NknTunaSidecarClient client, CancellationToken ct)
    {
        var connect = client.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).ToString(),
            TimeSpan.FromSeconds(5), ct);
        var relay = await listener.AcceptTcpClientAsync(ct);
        try
        {
            var status = JsonSerializer.SerializeToUtf8Bytes(new
            {
                @event = "status",
                address = "nlink-tuna-sidecar.loopback-stopped-delivery",
                appProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion,
                frameProtocolVersion = NknTunaSidecarFrameProtocol.ProtocolVersion,
                sidecarVersion = NknTunaSidecarCompatibility.ExpectedSidecarVersion,
                lanes = new[] { "file" },
            });
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(relay.GetStream(),
                NknTunaSidecarFrameType.Status, NknTunaSidecarLane.Control, 0,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), status, ct);
            await connect;
            return relay;
        }
        catch
        {
            relay.Dispose();
            throw;
        }
    }
}
