using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("FileTransferInfraSuite", "TunaDeliveryTraceApplication")]
    public async Task TunaDeliveryTrace_RealTransportBindsSendAndDeferredApplicationReceive(bool forwardBeforeStop)
    {
        var traces = new ConcurrentQueue<string>();
        using var traceOverride = NknTunaDeliveryTrace.OverrideForTests(
            new NknTunaDeliveryTrace.Producer(Guid.NewGuid().ToString("D"), traces.Enqueue));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        using var sender = new NknTunaSidecarClient(NknAccelerationLaneKind.File, 16);
        using var receiver = new NknTunaSidecarClient(NknAccelerationLaneKind.File, 16);
        var hostClient = new FakeNknClient("host.tuna-delivery-trace.address");
        var helperClient = new FakeNknClient("helper.tuna-delivery-trace.address");
        var options = NknTransportOptions.Load();
        using var host = new NknSignalingTransport(hostClient, options,
            new NknIdentity("host-tuna-delivery-trace-id", hostClient.Address), NknTunaAccelerationOptions.Disabled, receiver);
        using var helper = new NknSignalingTransport(helperClient, options,
            new NknIdentity("helper-tuna-delivery-trace-id", helperClient.Address), NknTunaAccelerationOptions.Disabled, sender);
        var dispatchBarrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Start();
        try
        {
            var sessionId = await ApproveNknSessionAsync(host, helper, cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_delivery_trace_application";
            var offerReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sessionOpened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += (_, _) => offerReceived.TrySetResult(true);
            helper.FileTransferAcceptReceived += (_, _) => acceptReceived.TrySetResult(true);
            host.FileTransferSessionOpenReceived += (_, _) => sessionOpened.TrySetResult(true);
            await helper.SendFileTransferOfferAsync(new FileTransferOfferV2
            {
                SessionId = sessionId, TransferId = transferId, FileName = "trace.bin", FileSizeBytes = 2048,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            }, cts.Token);
            await offerReceived.Task.WaitAsync(cts.Token);
            await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1
            {
                SessionId = sessionId, TransferId = transferId, AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            }, cts.Token);
            await acceptReceived.Task.WaitAsync(cts.Token);
            await helper.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2
            {
                SessionId = sessionId, TransferId = transferId, ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                SessionRole = FileTransferProtocol.SessionRoleSender, ChunkSizeBytes = 1024, InitialPipelineDepth = 8,
            }, cts.Token);
            await sessionOpened.Task.WaitAsync(cts.Token);
            var inbound = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var outbound = Assert.IsAssignableFrom<IFileTransferDataPathAuthoritySession>(
                await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token));

            using var senderRelay = await ConnectDeliveryTraceRelayAsync(listener, sender, cts.Token);
            using var receiverRelay = await ConnectDeliveryTraceRelayAsync(listener, receiver, cts.Token);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            // Controlled transport-authority fixture, not coordinator/route-acceptance proof.
            var authority = new FileTransferDataPathSendAuthority(sessionId, transferId, FileTransferDirection.Outbound,
                FileTransferRouteResolver.FileTunaV4Token, FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch: 7, RuntimeGeneration: 11, TerminalGeneration: 0, LegGeneration: 13,
                RoutePublicationGeneration: 17, RouteCommitGeneration: 0,
                TargetRouteToken: FileTransferRouteResolver.FileTunaV4Token, TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
                TargetTransport: FileTransferTransportKind.Tuna, RoutePublicationActionKind: "RoutePublication", CoordinatorDecisionReason: "accepted");
            var frames = Enumerable.Range(0, 2).Select(index => new FileTransferChunkBatchFrameV4
            {
                SessionId = sessionId, TransferId = transferId, StartChunkIndex = index, ChunkCount = 1,
                DataSegments = new[] { Enumerable.Repeat((byte)(index + 1), 1024).ToArray() }, BatchProfile = "v4_default_21k",
            }).ToArray();
            var wireFrames = new List<NknTunaSidecarFrame>();
            var logStart = GetOperationalLogLength();
            // No BeginSend/BindSend/Result calls: the production transport must create the binding.
            foreach (var frame in frames)
            {
                await outbound.SendAsync(frame, authority, cts.Token);
                NknTunaSidecarFrame wire;
                do { wire = await NknTunaSidecarFrameProtocol.ReadFrameAsync(senderRelay.GetStream(), cts.Token); }
                while (wire.Type == NknTunaSidecarFrameType.Ping);
                Assert.Equal(NknTunaSidecarFrameType.Data, wire.Type);
                wireFrames.Add(wire);
            }

            var callbackCount = 0;
            var callbacksReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiver.MessageReceived += (_, _) =>
            {
                if (Interlocked.Increment(ref callbackCount) == frames.Length) callbacksReturned.TrySetResult(true);
            };
            // Hold the existing scheduling barrier only; no authority/lifecycle rule is changed.
            SetPrivateField(host, "inboundFileTransferLifecycleDispatchTail", dispatchBarrier.Task);
            if (forwardBeforeStop)
            {
                foreach (var wire in wireFrames)
                    await NknTunaSidecarFrameProtocol.WriteFrameAsync(receiverRelay.GetStream(), wire.Type,
                        wire.Lane, wire.Sequence, wire.TimestampUtcMs, wire.Payload, cts.Token);
                await callbacksReturned.Task.WaitAsync(cts.Token);
                Assert.Equal(2, traces.Count(line => line.Contains("trace_stage=app_raw_ingress;", StringComparison.Ordinal)));
                Assert.DoesNotContain(traces, line => line.Contains("trace_stage=app_receive_disposition;", StringComparison.Ordinal));
                dispatchBarrier.SetResult(true);
                var received = new List<FileTransferChunkBatchFrameV4>();
                for (var index = 0; index < frames.Length; index++)
                {
                    var message = await inbound.ReceiveWithMetadataAsync(cts.Token);
                    Assert.Equal(FileTransferTransportKind.Tuna, message.TransportKind);
                    received.Add(Assert.IsType<FileTransferChunkBatchFrameV4>(message.Frame));
                }
                foreach (var frame in frames)
                    Assert.Equal(FileTransferDataFrameCodec.SerializeLegacyV4(frame),
                        FileTransferDataFrameCodec.SerializeLegacyV4(Assert.Single(received.Where(value => value.StartChunkIndex == frame.StartChunkIndex))));
            }
            receiver.Dispose();
            await Task.WhenAll(Assert.IsAssignableFrom<Task>(GetPrivateField(receiver, "readerTask")),
                Assert.IsAssignableFrom<Task>(GetPrivateField(receiver, "writerTask"))).WaitAsync(cts.Token);
            Assert.Equal(forwardBeforeStop ? 2 : 0, callbackCount);

            var observations = traces.Select(ParseDeliveryTraceFields).ToArray();
            var log = ReadOperationalLogTail(logStart);
            Assert.Equal(2, wireFrames.Select(wire => NknTunaDeliveryTrace.WireHash(wire.Payload)).Distinct().Count());
            for (var index = 0; index < wireFrames.Count; index++)
            {
                var wire = wireFrames[index];
                var hash = NknTunaDeliveryTrace.WireHash(wire.Payload);
                var send = Assert.Single(observations.Where(value => value.GetValueOrDefault("trace_stage") == "app_send_result" && value.GetValueOrDefault("trace_wire") == hash));
                Assert.Equal(wire.Sequence.ToString(CultureInfo.InvariantCulture), send["trace_seq"]);
                Assert.Equal(index.ToString(CultureInfo.InvariantCulture), send["start_chunk_index"]);
                Assert.Equal("17", send["route_publication_generation"]);
                Assert.Equal("tuna", send["actual_transport"]);
                var fingerprint = "data_path_envelope_fingerprint=" + send["data_path_envelope_fingerprint"];
                var primarySend = ParseDeliveryTraceFields(Assert.Single(log.Split('\n').Where(line =>
                    line.Contains("event=filetransfer_data_path_send_result;", StringComparison.Ordinal) && line.Contains(fingerprint, StringComparison.Ordinal))));
                Assert.Equal(send["start_chunk_index"], primarySend["start_chunk_index"]);
                Assert.Equal(send["route_publication_generation"], primarySend["route_publication_generation"]);
                var ingress = observations.Where(value => value.GetValueOrDefault("trace_stage") == "app_raw_ingress" && value.GetValueOrDefault("trace_wire") == hash).ToArray();
                var dispositions = observations.Where(value => value.GetValueOrDefault("trace_stage") == "app_receive_disposition" && value.GetValueOrDefault("trace_wire") == hash).ToArray();
                if (forwardBeforeStop)
                {
                    Assert.Single(ingress);
                    var disposition = Assert.Single(dispositions);
                    Assert.Equal(send["data_path_envelope_fingerprint"], disposition["data_path_envelope_fingerprint"]);
                    Assert.Equal(send["start_chunk_index"], disposition["start_chunk_index"]);
                    Assert.Equal(send["trace_seq"], disposition["trace_seq"]);
                    Assert.NotEqual(send["trace_connection"], disposition["trace_connection"]);
                    Assert.Equal("dispatch_entered", disposition["receive_disposition"]);
                    var primaryReceive = ParseDeliveryTraceFields(Assert.Single(log.Split('\n').Where(line =>
                        line.Contains("event=filetransfer_data_path_receive_result;", StringComparison.Ordinal) && line.Contains(fingerprint, StringComparison.Ordinal))));
                    Assert.Equal(disposition["start_chunk_index"], primaryReceive["start_chunk_index"]);
                    Assert.Equal(disposition["receive_disposition"], primaryReceive["receive_disposition"]);
                    Assert.Equal(disposition["route_publication_generation"], primaryReceive["route_publication_generation"]);
                }
                else { Assert.Empty(ingress); Assert.Empty(dispositions); }
            }
        }
        finally
        {
            dispatchBarrier.TrySetResult(true);
            sender.Dispose();
            receiver.Dispose();
            listener.Stop();
            await Task.WhenAll(new[] { sender, receiver }.SelectMany(client => new[]
            {
                GetPrivateField(client, "readerTask"), GetPrivateField(client, "writerTask"),
            }).OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static Dictionary<string, string> ParseDeliveryTraceFields(string line)
        => line.Split(';').Select(part => part.Trim().Split('=', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
}
