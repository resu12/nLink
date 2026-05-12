using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Configuration;
using NLink.App.Services;
using NLink.App.ViewModels;
using NLink.Core;
using NLink.Core.Chat;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using Xunit;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class NknFileTransferTransportTests : CoreSmokeTestsBase
{
    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_InboundBulkParseFailure_LogsStructuredEnvelopeDrop()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.filetransfer.drop.address");
            var helperClient = new FakeNknClient("helper.filetransfer.drop.address");
            var hostIdentity = new NknIdentity("host-drop-id", hostClient.Address);
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);

            await hostClient.ConnectAsync(cts.Token);
            await helperClient.ConnectAsync(cts.Token);
            await helperClient.SendBulkAsync(hostClient.ConnectedBulkAddress, new byte[] { 1, 2, 3, 4 }, cts.Token);

            await Task.Delay(50, cts.Token);
            var logText = LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=nkn_inbound_envelope_drop", logText, StringComparison.Ordinal);
            Assert.Contains("channel=bulk", logText, StringComparison.Ordinal);
            Assert.Contains("reason=parse_failed", logText, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_InboundBulkFileTransferEnvelope_LogsStructuredEnvelopeReceived()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.filetransfer.received.address");
            var helperClient = new FakeNknClient("helper.filetransfer.received.address");
            var hostIdentity = new NknIdentity("host-received-id", hostClient.Address);
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            var envelope = new Envelope(
                Version: 1,
                Code: "test-code",
                MessageId: Guid.NewGuid().ToString("N"),
                Type: MsgType.FileTransferDataFrame,
                Payload: new byte[] { 1, 2, 3 },
                UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ReplyTo: null);

            await hostClient.ConnectAsync(cts.Token);
            await helperClient.ConnectAsync(cts.Token);
            await helperClient.SendBulkAsync(hostClient.ConnectedBulkAddress, EnvelopeCodec.Serialize(envelope), cts.Token);

            await Task.Delay(50, cts.Token);
            var logText = LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=nkn_inbound_envelope_received", logText, StringComparison.Ordinal);
            Assert.Contains("channel=bulk", logText, StringComparison.Ordinal);
            Assert.Contains("envelope_type=file_transfer_data_frame", logText, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferChunkBudgetProvider_KeepsWrappedEnvelopeWithinNknLimit()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.budget.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.budget.address");
            NknIdentity hostIdentity = new NknIdentity("host-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            IFileTransferChunkBudgetProvider budgetProvider = Assert.IsAssignableFrom<IFileTransferChunkBudgetProvider>(helper);
            FileTransferChunkBudgetRequest request = new FileTransferChunkBudgetRequest("transfer_nkn_payload_budget", 88100000L, 49152, FileTransferProtocol.ProtocolVersionV6);
            int safeChunkSize = budgetProvider.ResolveSafeOutboundChunkSize(request);
            int chunkCount = (int)((request.FileSizeBytes + safeChunkSize - 1) / safeChunkSize);
            Envelope envelope = CoreSmokeTestsBase.BuildSecureFileTransferDataFrameEnvelope(helper, new FileTransferChunkBatchFrameV6 { SessionId = sessionId, TransferId = request.TransferId, StartChunkIndex = chunkCount - 1, ChunkCount = 1, DataSegments = new[] { new byte[safeChunkSize] }, BatchProfile = "v4_default_21k" }, 1L);
            Assert.InRange(safeChunkSize, 1, 49152);
            Assert.InRange(EnvelopeCodec.Serialize(envelope).Length, 1, 65536);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_LegacyFileTransferChunk_RejectedBeforeDispatch_WhenSecureEnvelopeInvalid()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.legacy.chunk.reject.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.legacy.chunk.reject.address");
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-legacy-chunk-reject-id", hostClient.Address));
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-legacy-chunk-reject-id", helperClient.Address));

            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_legacy_chunk_rejected_before_dispatch";
            Envelope envelope = CoreSmokeTestsBase.BuildSecureFileTransferEnvelope(
                helper,
                MsgType.FileTransferChunk,
                new FileTransferCancelV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = "legacy_probe",
                },
                transferId,
                CoreSmokeTestsBase.GetNextFileTransferSecureSequence(helper));
            byte[] tamperedPayload = envelope.Payload.ToArray();
            tamperedPayload[^1] ^= 0x01;

            CoreSmokeTestsBase.InvokeNknIncomingMessage(
                host,
                helperClient,
                new NknIncomingMessage(
                    payload: EnvelopeCodec.Serialize(envelope with { Payload = tamperedPayload }),
                    source: helperClient.ConnectedBulkAddress,
                    isTopic: false,
                    topic: null,
                    channel: NknBridgeChannel.Bulk,
                    bridgeIngressObservedUtcMs: 0L,
                    bridgeMessageObservedUtcMs: 0L,
                    binaryFrameDecodedUtcMs: 0L,
                    socketDataEventEmittedUtcMs: 0L,
                    wsReceiverWriteEnteredUtcMs: 0L,
                    wsMessageEmittedUtcMs: 0L,
                    sdkHandleMsgEnteredUtcMs: 0L,
                    clientMessageDispatchUtcMs: 0L,
                    multiClientMessageDispatchUtcMs: 0L));

            await Task.Delay(100, cts.Token);
            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            Assert.Contains("event=filetransfer_message_rejected; message_type=file_transfer_chunk; reason=secure_envelope_invalid", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_legacy_message_ignored", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_LegacyFileTransferStart_RejectedBeforeDispatch_WhenOutOfPhase()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.legacy.start.reject.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.legacy.start.reject.address");
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-legacy-start-reject-id", hostClient.Address));
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-legacy-start-reject-id", helperClient.Address));

            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_legacy_start_rejected_before_dispatch";
            Envelope envelope = BuildSecureLegacyFileTransferEnvelope(
                helper,
                MsgType.FileTransferStart,
                sessionId,
                transferId,
                CoreSmokeTestsBase.GetNextFileTransferSecureSequence(helper));

            CoreSmokeTestsBase.InvokeNknIncomingMessage(
                host,
                helperClient,
                new NknIncomingMessage(
                    payload: EnvelopeCodec.Serialize(envelope),
                    source: helperClient.ConnectedAddress,
                    isTopic: false,
                    topic: null,
                    channel: NknBridgeChannel.Control,
                    bridgeIngressObservedUtcMs: 0L,
                    bridgeMessageObservedUtcMs: 0L,
                    binaryFrameDecodedUtcMs: 0L,
                    socketDataEventEmittedUtcMs: 0L,
                    wsReceiverWriteEnteredUtcMs: 0L,
                    wsMessageEmittedUtcMs: 0L,
                    sdkHandleMsgEnteredUtcMs: 0L,
                    clientMessageDispatchUtcMs: 0L,
                    multiClientMessageDispatchUtcMs: 0L));

            await Task.Delay(100, cts.Token);
            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            Assert.Contains("event=filetransfer_message_rejected; message_type=file_transfer_start; reason=unknown_transfer_id", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_legacy_message_ignored", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_SendsOutboundV4RepairChunkBatchOnBulkOnlyByDefault()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.repairbatch.v4.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.repairbatch.v4.address");
            NknIdentity hostIdentity = new NknIdentity("host-repairbatch-v4-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-repairbatch-v4-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            try
            {
                string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
                IFileTransferDataSession outboundSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_repairbatch_v4", cts.Token);
                ConcurrentQueue<NknIncomingMessage> rawControlMessages = new ConcurrentQueue<NknIncomingMessage>();
                ConcurrentQueue<NknIncomingMessage> rawBulkMessages = new ConcurrentQueue<NknIncomingMessage>();
                hostClient.MessageReceived += delegate (object? _, NknIncomingMessage e)
                {
                    if (!e.IsTopic && EnvelopeCodec.TryDeserialize(e.Payload, out Envelope env) && env.Type == MsgType.FileTransferDataFrame)
                    {
                        if (e.Channel == NknBridgeChannel.Control)
                        {
                            rawControlMessages.Enqueue(e);
                        }
                        else if (e.Channel == NknBridgeChannel.Bulk)
                        {
                            rawBulkMessages.Enqueue(e);
                        }
                    }
                };
                FileTransferChunkBatchFrameV6 batch = new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_repairbatch_v4",
                    StartChunkIndex = 9,
                    ChunkCount = 3,
                    DataSegments = new byte[3][] { new byte[21 * 1024], new byte[21 * 1024], new byte[21 * 1024] },
                    BatchProfile = "v4_repair_21k"
                };

                await outboundSession.SendAsync(batch, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => rawBulkMessages.Count >= 1, TimeSpan.FromSeconds(2.0));
                await Task.Delay(150, cts.Token);

                Assert.Empty(rawControlMessages);
                Assert.Single(rawBulkMessages);
                Assert.Equal(NknBridgeChannel.Bulk, rawBulkMessages.Single().Channel);

                FileTransferDataFrame frame = DecodeNknDataFrame(host, helper, rawBulkMessages.Single(), sessionId);
                FileTransferChunkBatchFrameV6 receivedBatch = Assert.IsType<FileTransferChunkBatchFrameV6>(frame);
                Assert.Equal(9, receivedBatch.StartChunkIndex);
                Assert.Equal(3, receivedBatch.ChunkCount);
                Assert.Equal(3, receivedBatch.DataSegments.Count);
                Assert.All(receivedBatch.DataSegments, segment => Assert.Equal(21 * 1024, segment.Length));

                string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
                Assert.Contains("event=filetransfer_chunk_batch_sent_as_batch", logTail, StringComparison.Ordinal);
                Assert.Contains("frame_type=filetransfer.chunk_batch.v6", logTail, StringComparison.Ordinal);
                Assert.Contains("batch_profile=v4_repair_21k", logTail, StringComparison.Ordinal);
                Assert.Contains("repair_delivery_mode=bulk_only", logTail, StringComparison.Ordinal);
                Assert.Contains("lane=bulk", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("lane=control_bulk", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("event=filetransfer_v4_repair_delivery_first_success", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("event=filetransfer_v4_repair_delivery_secondary_completed", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("event=filetransfer_v4_feedback_first_success; transport=nkn; transfer_id=transfer_nkn_repairbatch_v4", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("event=filetransfer_transport_payload_rejected", logTail, StringComparison.Ordinal);
            }
            finally
            {
                if (helper != null)
                {
                    ((IDisposable)helper).Dispose();
                }
            }
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_SendsV6TransportProbeOnBulkLane()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.probe.v6.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.probe.v6.address");
            NknIdentity hostIdentity = new NknIdentity("host-probe-v6-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-probe-v6-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            try
            {
                string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
                IFileTransferDataSession outboundSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_probe_v6", cts.Token);
                ConcurrentQueue<NknIncomingMessage> rawControlMessages = new ConcurrentQueue<NknIncomingMessage>();
                ConcurrentQueue<NknIncomingMessage> rawBulkMessages = new ConcurrentQueue<NknIncomingMessage>();
                hostClient.MessageReceived += delegate (object? _, NknIncomingMessage e)
                {
                    if (!e.IsTopic && EnvelopeCodec.TryDeserialize(e.Payload, out Envelope env) && env.Type == MsgType.FileTransferDataFrame)
                    {
                        if (e.Channel == NknBridgeChannel.Control)
                        {
                            rawControlMessages.Enqueue(e);
                        }
                        else if (e.Channel == NknBridgeChannel.Bulk)
                        {
                            rawBulkMessages.Enqueue(e);
                        }
                    }
                };
                FileTransferTransportProbeFrameV6 probe = new FileTransferTransportProbeFrameV6
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_probe_v6",
                    TransportEpoch = 17,
                    ProbeId = "probe-17",
                    TargetTransport = "tuna"
                };

                await outboundSession.SendAsync(probe, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => rawBulkMessages.Count >= 1, TimeSpan.FromSeconds(2.0));
                await Task.Delay(150, cts.Token);

                Assert.Empty(rawControlMessages);
                NknIncomingMessage rawBulkMessage = Assert.Single(rawBulkMessages);
                Assert.Equal(NknBridgeChannel.Bulk, rawBulkMessage.Channel);
                FileTransferDataFrame frame = DecodeNknDataFrame(host, helper, rawBulkMessage, sessionId);
                FileTransferTransportProbeFrameV6 receivedProbe = Assert.IsType<FileTransferTransportProbeFrameV6>(frame);
                Assert.Equal(17, receivedProbe.TransportEpoch);
                Assert.Equal("probe-17", receivedProbe.ProbeId);
                Assert.Equal("tuna", receivedProbe.TargetTransport);
            }
            finally
            {
                if (helper != null)
                {
                    ((IDisposable)helper).Dispose();
                }
            }
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_ForcesRegularNknForRegularTargetV6TransportProbe()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.probe.v6.regular.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.probe.v6.regular.address");
            FakeNknAccelerationLane helperLane = new FakeNknAccelerationLane(isAvailable: true);
            NknIdentity hostIdentity = new NknIdentity("host-probe-v6-regular-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-probe-v6-regular-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            NknSignalingTransport helper = new NknSignalingTransport(
                helperClient,
                options,
                helperIdentity,
                NknTunaAccelerationOptions.Disabled,
                helperLane);
            try
            {
                string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
                helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
                IFileTransferDataSession outboundSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_probe_v6_regular", cts.Token);
                ConcurrentQueue<NknIncomingMessage> rawBulkMessages = new ConcurrentQueue<NknIncomingMessage>();
                hostClient.MessageReceived += delegate (object? _, NknIncomingMessage e)
                {
                    if (!e.IsTopic && EnvelopeCodec.TryDeserialize(e.Payload, out Envelope env) && env.Type == MsgType.FileTransferDataFrame && e.Channel == NknBridgeChannel.Bulk)
                    {
                        rawBulkMessages.Enqueue(e);
                    }
                };
                FileTransferTransportProbeFrameV6 probe = new FileTransferTransportProbeFrameV6
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_probe_v6_regular",
                    TransportEpoch = 23,
                    ProbeId = "probe-regular-23",
                    TargetTransport = "regular_nkn"
                };

                await outboundSession.SendAsync(probe, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => rawBulkMessages.Count >= 1, TimeSpan.FromSeconds(2.0));
                await Task.Delay(150, cts.Token);

                Assert.Empty(helperLane.Sent);
                NknIncomingMessage rawBulkMessage = Assert.Single(rawBulkMessages);
                Assert.Equal(NknBridgeChannel.Bulk, rawBulkMessage.Channel);
                FileTransferDataFrame frame = DecodeNknDataFrame(host, helper, rawBulkMessage, sessionId);
                FileTransferTransportProbeFrameV6 receivedProbe = Assert.IsType<FileTransferTransportProbeFrameV6>(frame);
                Assert.Equal(23, receivedProbe.TransportEpoch);
                Assert.Equal("probe-regular-23", receivedProbe.ProbeId);
                Assert.Equal("regular_nkn", receivedProbe.TargetTransport);
                string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
                Assert.Contains("frame_type=filetransfer.transport_probe.v6", logTail, StringComparison.Ordinal);
                Assert.Contains("effective_transport=nkn", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("effective_transport=tuna", logTail, StringComparison.Ordinal);
            }
            finally
            {
                if (helper != null)
                {
                    ((IDisposable)helper).Dispose();
                }
            }
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_SendsEscalatedV4RepairChunkBatchOnControlAndBulk()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.repairbatch.escalated.v4.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.repairbatch.escalated.v4.address");
            NknIdentity hostIdentity = new NknIdentity("host-repairbatch-escalated-v4-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-repairbatch-escalated-v4-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            try
            {
                string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
                IFileTransferDataSession outboundSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_repairbatch_escalated_v4", cts.Token);
                ConcurrentQueue<NknIncomingMessage> rawControlMessages = new ConcurrentQueue<NknIncomingMessage>();
                ConcurrentQueue<NknIncomingMessage> rawBulkMessages = new ConcurrentQueue<NknIncomingMessage>();
                hostClient.MessageReceived += delegate (object? _, NknIncomingMessage e)
                {
                    if (!e.IsTopic && EnvelopeCodec.TryDeserialize(e.Payload, out Envelope env) && env.Type == MsgType.FileTransferDataFrame)
                    {
                        if (e.Channel == NknBridgeChannel.Control)
                        {
                            rawControlMessages.Enqueue(e);
                        }
                        else if (e.Channel == NknBridgeChannel.Bulk)
                        {
                            rawBulkMessages.Enqueue(e);
                        }
                    }
                };
                FileTransferChunkBatchFrameV6 batch = new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_repairbatch_escalated_v4",
                    StartChunkIndex = 9,
                    ChunkCount = 3,
                    DataSegments = new byte[3][] { new byte[21 * 1024], new byte[21 * 1024], new byte[21 * 1024] },
                    BatchProfile = "v4_repair_21k",
                    RepairDeliveryMode = FileTransferV4RepairDeliveryMode.ControlBulkRedundant
                };

                await outboundSession.SendAsync(batch, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(
                    () => rawControlMessages.Count >= 1 && rawBulkMessages.Count >= 1,
                    TimeSpan.FromSeconds(2.0));

                Assert.Single(rawControlMessages);
                Assert.Single(rawBulkMessages);
                Assert.Equal(NknBridgeChannel.Control, rawControlMessages.Single().Channel);
                Assert.Equal(NknBridgeChannel.Bulk, rawBulkMessages.Single().Channel);

                foreach (var message in rawControlMessages.Concat(rawBulkMessages))
                {
                    FileTransferDataFrame frame = DecodeNknDataFrame(host, helper, message, sessionId);
                    FileTransferChunkBatchFrameV6 receivedBatch = Assert.IsType<FileTransferChunkBatchFrameV6>(frame);
                    Assert.Equal(9, receivedBatch.StartChunkIndex);
                    Assert.Equal(3, receivedBatch.ChunkCount);
                    Assert.Equal(3, receivedBatch.DataSegments.Count);
                    Assert.All(receivedBatch.DataSegments, segment => Assert.Equal(21 * 1024, segment.Length));
                }

                string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
                Assert.Contains("event=filetransfer_chunk_batch_sent_as_batch", logTail, StringComparison.Ordinal);
                Assert.Contains("frame_type=filetransfer.chunk_batch.v6", logTail, StringComparison.Ordinal);
                Assert.Contains("batch_profile=v4_repair_21k", logTail, StringComparison.Ordinal);
                Assert.Contains("repair_delivery_mode=control_bulk_escalated", logTail, StringComparison.Ordinal);
                Assert.Contains("lane=control_bulk", logTail, StringComparison.Ordinal);
                Assert.Contains("event=filetransfer_v4_repair_delivery_first_success", logTail, StringComparison.Ordinal);
                Assert.Contains("event=filetransfer_v4_repair_delivery_secondary_completed", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("event=filetransfer_v4_feedback_first_success; transport=nkn; transfer_id=transfer_nkn_repairbatch_escalated_v4", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("event=filetransfer_transport_payload_rejected", logTail, StringComparison.Ordinal);
            }
            finally
            {
                if (helper != null)
                {
                    ((IDisposable)helper).Dispose();
                }
            }
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_SendsV6PriorityChunkBatchOnControlAndBulk()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.prioritybatch.v6.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.prioritybatch.v6.address");
            NknIdentity hostIdentity = new NknIdentity("host-prioritybatch-v6-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-prioritybatch-v6-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            try
            {
                string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
                IFileTransferDataSession outboundSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_prioritybatch_v6", cts.Token);
                ConcurrentQueue<NknIncomingMessage> rawControlMessages = new ConcurrentQueue<NknIncomingMessage>();
                ConcurrentQueue<NknIncomingMessage> rawBulkMessages = new ConcurrentQueue<NknIncomingMessage>();
                hostClient.MessageReceived += delegate (object? _, NknIncomingMessage e)
                {
                    if (!e.IsTopic && EnvelopeCodec.TryDeserialize(e.Payload, out Envelope env) && env.Type == MsgType.FileTransferDataFrame)
                    {
                        if (e.Channel == NknBridgeChannel.Control)
                        {
                            rawControlMessages.Enqueue(e);
                        }
                        else if (e.Channel == NknBridgeChannel.Bulk)
                        {
                            rawBulkMessages.Enqueue(e);
                        }
                    }
                };
                FileTransferChunkBatchFrameV6 batch = new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_prioritybatch_v6",
                    StartChunkIndex = 9,
                    ChunkCount = 1,
                    DataSegments = new byte[1][] { new byte[21 * 1024] },
                    BatchProfile = "v6_request_window",
                    RepairDeliveryMode = FileTransferV4RepairDeliveryMode.ControlBulkRedundant,
                    RepairRequestId = "v6-frontier:9:1",
                    Priority = "frontier",
                };

                await outboundSession.SendAsync(batch, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(
                    () => rawControlMessages.Count >= 1 && rawBulkMessages.Count >= 1,
                    TimeSpan.FromSeconds(2.0));

                Assert.Single(rawControlMessages);
                Assert.Single(rawBulkMessages);

                foreach (var message in rawControlMessages.Concat(rawBulkMessages))
                {
                    FileTransferDataFrame frame = DecodeNknDataFrame(host, helper, message, sessionId);
                    FileTransferChunkBatchFrameV6 receivedBatch = Assert.IsType<FileTransferChunkBatchFrameV6>(frame);
                    Assert.Equal(9, receivedBatch.StartChunkIndex);
                    Assert.Equal("frontier", receivedBatch.Priority);
                    Assert.Equal("v6-frontier:9:1", receivedBatch.RepairRequestId);
                }

                string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
                Assert.Contains("batch_profile=v6_request_window", logTail, StringComparison.Ordinal);
                Assert.Contains("repair_delivery_mode=control_bulk_escalated", logTail, StringComparison.Ordinal);
                Assert.Contains("lane=control_bulk", logTail, StringComparison.Ordinal);
                Assert.Contains("event=filetransfer_v4_repair_delivery_first_success", logTail, StringComparison.Ordinal);
            }
            finally
            {
                if (helper != null)
                {
                    ((IDisposable)helper).Dispose();
                }
            }
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_SendsOutboundV4ChunkBatchFrameAsSingleBulkFrame_WhenWithinBudget()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.batchlimit.v4.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.batchlimit.v4.address");
            NknIdentity hostIdentity = new NknIdentity("host-batchlimit-v4-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-batchlimit-v4-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            try
            {
                string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
                IFileTransferDataSession outboundSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_batchlimit_v4", cts.Token);
                ConcurrentQueue<NknIncomingMessage> rawBulkMessages = new ConcurrentQueue<NknIncomingMessage>();
                hostClient.MessageReceived += delegate (object? _, NknIncomingMessage e)
                {
                    if (!e.IsTopic && e.Channel == NknBridgeChannel.Bulk && EnvelopeCodec.TryDeserialize(e.Payload, out Envelope env) && env.Type == MsgType.FileTransferDataFrame)
                    {
                        rawBulkMessages.Enqueue(e);
                    }
                };
                FileTransferChunkBatchFrameV6 batch = new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_batchlimit_v4",
                    StartChunkIndex = 0,
                    ChunkCount = 3,
                    DataSegments = new byte[3][] { new byte[21 * 1024], new byte[21 * 1024], new byte[21 * 1024] },
                    BatchProfile = "v4_default_21k"
                };

                await outboundSession.SendAsync(batch, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => rawBulkMessages.Count >= 1, TimeSpan.FromSeconds(2.0));

                NknIncomingMessage rawBulkMessage = Assert.Single(rawBulkMessages);
                FileTransferDataFrame frame = DecodeNknDataFrame(host, helper, rawBulkMessage, sessionId);
                FileTransferChunkBatchFrameV6 receivedBatch = Assert.IsType<FileTransferChunkBatchFrameV6>(frame);
                Assert.Equal(3, receivedBatch.ChunkCount);
                Assert.Equal(3, receivedBatch.DataSegments.Count);
                Assert.All(receivedBatch.DataSegments, segment => Assert.Equal(21 * 1024, segment.Length));
                Assert.Equal(NknBridgeChannel.Bulk, rawBulkMessage.Channel);

                string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
                Assert.Contains("event=filetransfer_chunk_batch_sent_as_batch", logTail, StringComparison.Ordinal);
                Assert.Contains("frame_type=filetransfer.chunk_batch.v6", logTail, StringComparison.Ordinal);
                Assert.Contains("batch_profile=v4_default_21k", logTail, StringComparison.Ordinal);
                Assert.Contains("bridge_payload_fill_percent=", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("event=filetransfer_chunk_batch_split_for_transport; transport=nkn; transfer_id=transfer_nkn_batchlimit_v4", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("event=filetransfer_transport_payload_rejected", logTail, StringComparison.Ordinal);
            }
            finally
            {
                if (helper != null)
                {
                    ((IDisposable)helper).Dispose();
                }
            }
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_SplitsOutboundV4ChunkBatchFrameIntoV4SubBatches_WhenWrappedPayloadExceedsBudget()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.batchfallback.v4.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.batchfallback.v4.address");
            NknIdentity hostIdentity = new NknIdentity("host-batchfallback-v4-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-batchfallback-v4-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            try
            {
                string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
                IFileTransferDataSession outboundSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_batchfallback_v4", cts.Token);
                ConcurrentQueue<NknIncomingMessage> rawBulkMessages = new ConcurrentQueue<NknIncomingMessage>();
                hostClient.MessageReceived += delegate (object? _, NknIncomingMessage e)
                {
                    if (!e.IsTopic && e.Channel == NknBridgeChannel.Bulk && EnvelopeCodec.TryDeserialize(e.Payload, out Envelope env) && env.Type == MsgType.FileTransferDataFrame)
                    {
                        rawBulkMessages.Enqueue(e);
                    }
                };
                FileTransferChunkBatchFrameV6 oversizedBatch = new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_batchfallback_v4",
                    StartChunkIndex = 0,
                    ChunkCount = 4,
                    DataSegments = new byte[4][] { new byte[21 * 1024], new byte[21 * 1024], new byte[21 * 1024], new byte[21 * 1024] },
                    BatchProfile = "v4_default_21k",
                    TransportEpoch = 7,
                    BatchId = "batch-7",
                    RepairRequestId = "repair-7",
                    Priority = "frontier",
                    RecoveryMode = "frontier_repair_only"
                };

                await outboundSession.SendAsync(oversizedBatch, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => rawBulkMessages.Count >= 2, TimeSpan.FromSeconds(2.0));

                var frames = rawBulkMessages.Select(message => DecodeNknDataFrame(host, helper, message, sessionId)).ToArray();
                Assert.All(frames, frame => Assert.IsType<FileTransferChunkBatchFrameV6>(frame));
                var batches = frames.Cast<FileTransferChunkBatchFrameV6>().OrderBy(static frame => frame.StartChunkIndex).ToArray();
                Assert.Equal(new[] { 0, 3 }, batches.Select(static frame => frame.StartChunkIndex).ToArray());
                Assert.Equal(4, batches.Sum(static frame => frame.DataSegments.Count));
                Assert.All(batches, batch =>
                {
                    Assert.Equal(7, batch.TransportEpoch);
                    Assert.Equal("batch-7", batch.BatchId);
                    Assert.Equal("repair-7", batch.RepairRequestId);
                    Assert.Equal("frontier", batch.Priority);
                    Assert.Equal("frontier_repair_only", batch.RecoveryMode);
                });
                await CoreSmokeTestsBase.WaitUntilAsync(
                    () => CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex)
                        .Contains("event=filetransfer_chunk_batch_split_for_transport", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(2.0));
                string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
                Assert.Contains("event=filetransfer_chunk_batch_split_for_transport", logTail, StringComparison.Ordinal);
                Assert.Contains("original_frame_type=filetransfer.chunk_batch.v6", logTail, StringComparison.Ordinal);
                Assert.Contains("frame_type=filetransfer.chunk_batch.v6", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("event=filetransfer_transport_payload_rejected", logTail, StringComparison.Ordinal);
            }
            finally
            {
                if (helper != null)
                {
                    ((IDisposable)helper).Dispose();
                }
            }
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_SendsV4StateFeedbackOnControlAndBulk_ByDefault()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.v4.feedback.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.v4.feedback.address");
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-v4-feedback-id", hostClient.Address));
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-v4-feedback-id", helperClient.Address));

            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            IFileTransferDataSession receiverFeedbackSession = await host.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_v4_feedback", cts.Token);
            ConcurrentQueue<NknIncomingMessage> rawControlMessages = new ConcurrentQueue<NknIncomingMessage>();
            ConcurrentQueue<NknIncomingMessage> rawBulkMessages = new ConcurrentQueue<NknIncomingMessage>();
            helperClient.MessageReceived += delegate (object? _, NknIncomingMessage e)
            {
                if (!e.IsTopic && EnvelopeCodec.TryDeserialize(e.Payload, out Envelope env) && env.Type == MsgType.FileTransferDataFrame)
                {
                    if (e.Channel == NknBridgeChannel.Control)
                    {
                        rawControlMessages.Enqueue(e);
                    }
                    else if (e.Channel == NknBridgeChannel.Bulk)
                    {
                        rawBulkMessages.Enqueue(e);
                    }
                }
            };

            var state = new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = "transfer_nkn_v4_feedback",
                Epoch = 1,
                ContiguousCommittedChunkIndex = 10,
                DurableReceivedHighestChunkIndex = 12,
                CreditUntilChunkIndexExclusive = 64,
                BytesCommitted = 21_504L * 10
            };

            await receiverFeedbackSession.SendAsync(state, cts.Token);
            await CoreSmokeTestsBase.WaitUntilAsync(
                () => rawControlMessages.Count >= 1 && rawBulkMessages.Count >= 1,
                TimeSpan.FromSeconds(2.0));

            Assert.Single(rawControlMessages);
            Assert.Single(rawBulkMessages);
            Assert.Equal(NknBridgeChannel.Control, rawControlMessages.Single().Channel);
            Assert.Equal(NknBridgeChannel.Bulk, rawBulkMessages.Single().Channel);

            foreach (var message in rawControlMessages.Concat(rawBulkMessages))
            {
                var receivedState = Assert.IsType<FileTransferReceiverStateFrameV6>(DecodeNknDataFrame(helper, host, message, sessionId));
                Assert.Equal(1, receivedState.Epoch);
                Assert.Equal(64, receivedState.CreditUntilChunkIndexExclusive);
            }

            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            Assert.Contains("event=filetransfer_v4_feedback_first_success", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v4_feedback_secondary_completed", logTail, StringComparison.Ordinal);
            Assert.Contains("frame_type=filetransfer.receiver_state.v6", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_RoutesV6RecoveryFramesOnRegularNkn()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.v6.recovery.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.v6.recovery.address");
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-v6-recovery-id", hostClient.Address));
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-v6-recovery-id", helperClient.Address));

            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            TaskCompletionSource<FileTransferOfferV2> offerReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferAcceptV1> acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferSessionOpenV2> sessionOpenReceived = new TaskCompletionSource<FileTransferSessionOpenV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                offerReceived.TrySetResult(e.Message);
            };
            helper.FileTransferAcceptReceived += delegate (object? _, FileTransferAcceptReceivedEventArgs e)
            {
                acceptReceived.TrySetResult(e.Message);
            };
            host.FileTransferSessionOpenReceived += delegate (object? _, FileTransferSessionOpenReceivedEventArgs e)
            {
                sessionOpenReceived.TrySetResult(e.Message);
            };

            const string transferId = "transfer_nkn_v6_recovery_frames";
            await helper.SendFileTransferOfferAsync(
                new FileTransferOfferV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = "recovery.bin",
                    FileSizeBytes = 8 * 1024L,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await helper.SendFileTransferSessionOpenAsync(
                new FileTransferSessionOpenV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                    SessionRole = FileTransferProtocol.SessionRoleSender,
                    ChunkSizeBytes = 1024,
                    InitialPipelineDepth = 8,
                },
                cts.Token);
            await sessionOpenReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);

            IFileTransferDataSession receiverRecoverySession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            IFileTransferDataSession senderRecoverySession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            ConcurrentQueue<NknIncomingMessage> rawControlMessages = new ConcurrentQueue<NknIncomingMessage>();
            ConcurrentQueue<NknIncomingMessage> rawBulkMessages = new ConcurrentQueue<NknIncomingMessage>();
            helperClient.MessageReceived += delegate (object? _, NknIncomingMessage e)
            {
                if (!e.IsTopic && EnvelopeCodec.TryDeserialize(e.Payload, out Envelope env) && env.Type == MsgType.FileTransferDataFrame)
                {
                    if (e.Channel == NknBridgeChannel.Control)
                    {
                        rawControlMessages.Enqueue(e);
                    }
                    else if (e.Channel == NknBridgeChannel.Bulk)
                    {
                        rawBulkMessages.Enqueue(e);
                    }
                }
            };

            await receiverRecoverySession.SendAsync(
                new FileTransferTransportEpochFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = 7,
                    RecoveryMode = "nkn_proof_pending",
                },
                cts.Token);
            await receiverRecoverySession.SendAsync(
                new FileTransferFrontierRequestFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = 7,
                    RepairRequestId = "v6:7:42:1",
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 42, ChunkCount = 1 }],
                    Priority = "frontier",
                    RecoveryMode = "frontier_repair_only",
                },
                cts.Token);
            await receiverRecoverySession.SendAsync(
                new FileTransferRepairProofFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = 7,
                    RepairRequestId = "v6:7:42:1",
                    AppliedChunkCount = 1,
                    CommittedChunkIndex = 43,
                    RecoveryMode = "backfill_repair",
                },
                cts.Token);

            await CoreSmokeTestsBase.WaitUntilAsync(
                () => rawControlMessages.Count >= 3 && rawBulkMessages.Count >= 3,
                TimeSpan.FromSeconds(3.0));

            HashSet<string> deliveredTypes = [];
            for (var i = 0; i < 6 && deliveredTypes.Count < 3; i++)
            {
                var deliveredFrame = await senderRecoverySession.ReceiveAsync(cts.Token);
                deliveredTypes.Add(deliveredFrame.Type);
            }

            Assert.Contains(FileTransferProtocol.TransportEpochFrameTypeV6, deliveredTypes);
            Assert.Contains(FileTransferProtocol.FrontierRequestFrameTypeV6, deliveredTypes);
            Assert.Contains(FileTransferProtocol.RepairProofFrameTypeV6, deliveredTypes);
            Assert.True(rawControlMessages.Count >= 3);
            Assert.True(rawBulkMessages.Count >= 3);

            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            Assert.Contains($"frame_type={FileTransferProtocol.TransportEpochFrameTypeV6}", logTail, StringComparison.Ordinal);
            Assert.Contains($"frame_type={FileTransferProtocol.FrontierRequestFrameTypeV6}", logTail, StringComparison.Ordinal);
            Assert.Contains($"frame_type={FileTransferProtocol.RepairProofFrameTypeV6}", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("reason=unsupported_data_frame_type", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_AcceptsSenderV4PauseStateFrame()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("nlink-host-v4-senderpause.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            FakeNknClient helperClient = new FakeNknClient("nlink-helper-v4-senderpause.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-v4-senderpause-id", hostClient.Address));
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-v4-senderpause-id", helperClient.Address));

            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            TaskCompletionSource<FileTransferOfferV2> offerReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferAcceptV1> acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferSessionOpenV2> sessionOpenReceived = new TaskCompletionSource<FileTransferSessionOpenV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                offerReceived.TrySetResult(e.Message);
            };
            helper.FileTransferAcceptReceived += delegate (object? _, FileTransferAcceptReceivedEventArgs e)
            {
                acceptReceived.TrySetResult(e.Message);
            };
            host.FileTransferSessionOpenReceived += delegate (object? _, FileTransferSessionOpenReceivedEventArgs e)
            {
                sessionOpenReceived.TrySetResult(e.Message);
            };

            const string transferId = "transfer_nkn_v4_sender_pause_state";
            await helper.SendFileTransferOfferAsync(
                new FileTransferOfferV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = "sender-pause.bin",
                    FileSizeBytes = 2048L,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await helper.SendFileTransferSessionOpenAsync(
                new FileTransferSessionOpenV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                    SessionRole = FileTransferProtocol.SessionRoleSender,
                    ChunkSizeBytes = 1024,
                    InitialPipelineDepth = 8,
                },
                cts.Token);
            await sessionOpenReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);

            IFileTransferDataSession inboundSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var pauseState = new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 0,
                BytesCommitted = 0,
                TransferPaused = true,
                TransferPauseReason = "ui_pause",
            };

            CoreSmokeTestsBase.InvokeNknIncomingMessage(
                host,
                helperClient,
                new NknIncomingMessage(
                    payload: EnvelopeCodec.Serialize(CoreSmokeTestsBase.BuildSecureFileTransferDataFrameEnvelope(helper, pauseState, CoreSmokeTestsBase.GetNextFileTransferSecureSequence(helper))),
                    source: helperClient.ConnectedAddress,
                    isTopic: false,
                    topic: null,
                    channel: NknBridgeChannel.Control,
                    bridgeIngressObservedUtcMs: 0L,
                    bridgeMessageObservedUtcMs: 0L,
                    binaryFrameDecodedUtcMs: 0L,
                    socketDataEventEmittedUtcMs: 0L,
                    wsReceiverWriteEnteredUtcMs: 0L,
                    wsMessageEmittedUtcMs: 0L,
                    sdkHandleMsgEnteredUtcMs: 0L,
                    clientMessageDispatchUtcMs: 0L,
                    multiClientMessageDispatchUtcMs: 0L));

            FileTransferDataFrame receivedFrame = await inboundSession.ReceiveAsync(cts.Token);
            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            FileTransferReceiverStateFrameV6 receivedState = Assert.IsType<FileTransferReceiverStateFrameV6>(receivedFrame);
            Assert.True(receivedState.TransferPaused);
            Assert.Equal("ui_pause", receivedState.TransferPauseReason);
            Assert.Contains("event=filetransfer_data_frame_dispatched; transport=nkn; transfer_id=transfer_nkn_v4_sender_pause_state; session_id=", logTail, StringComparison.Ordinal);
            Assert.Contains("frame_type=filetransfer.receiver_state.v6; chunk_index=(none); lane=control", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_DisablesV4FeedbackBulkRedundancy_WhenRollbackEnvIsOff()
    {
        var previous = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V4_FEEDBACK_BULK_REDUNDANCY");
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V4_FEEDBACK_BULK_REDUNDANCY", "0");
        FakeNknClient.ResetNetwork();
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.v4.feedback.rollback.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.v4.feedback.rollback.address");
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-v4-feedback-rollback-id", hostClient.Address));
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-v4-feedback-rollback-id", helperClient.Address));

            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            IFileTransferDataSession receiverFeedbackSession = await host.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_v4_feedback_rollback", cts.Token);
            ConcurrentQueue<NknIncomingMessage> rawControlMessages = new ConcurrentQueue<NknIncomingMessage>();
            ConcurrentQueue<NknIncomingMessage> rawBulkMessages = new ConcurrentQueue<NknIncomingMessage>();
            helperClient.MessageReceived += delegate (object? _, NknIncomingMessage e)
            {
                if (!e.IsTopic && EnvelopeCodec.TryDeserialize(e.Payload, out Envelope env) && env.Type == MsgType.FileTransferDataFrame)
                {
                    if (e.Channel == NknBridgeChannel.Control)
                    {
                        rawControlMessages.Enqueue(e);
                    }
                    else if (e.Channel == NknBridgeChannel.Bulk)
                    {
                        rawBulkMessages.Enqueue(e);
                    }
                }
            };

            await receiverFeedbackSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_v4_feedback_rollback",
                    Epoch = 1,
                    CreditUntilChunkIndexExclusive = 16
                },
                cts.Token);
            await CoreSmokeTestsBase.WaitUntilAsync(() => rawControlMessages.Count >= 1, TimeSpan.FromSeconds(2.0));
            await Task.Delay(150, cts.Token);

            Assert.Single(rawControlMessages);
            Assert.Empty(rawBulkMessages);
            Assert.IsType<FileTransferReceiverStateFrameV6>(DecodeNknDataFrame(helper, host, rawControlMessages.Single(), sessionId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V4_FEEDBACK_BULK_REDUNDANCY", previous);
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_PreservesAcceleratedTransportMetadataAcrossDeferredDispatch()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.tuna-metadata.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.tuna-metadata.address");
            FakeNknAccelerationLane hostLane = new FakeNknAccelerationLane(isAvailable: true);
            NknIdentity hostIdentity = new NknIdentity("host-tuna-metadata-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-tuna-metadata-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(
                hostClient,
                options,
                hostIdentity,
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            string sessionId = await ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            TaskCompletionSource<FileTransferOfferV2> offerReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferAcceptV1> acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferSessionOpenV2> sessionOpenReceived = new TaskCompletionSource<FileTransferSessionOpenV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                offerReceived.TrySetResult(e.Message);
            };
            helper.FileTransferAcceptReceived += delegate (object? _, FileTransferAcceptReceivedEventArgs e)
            {
                acceptReceived.TrySetResult(e.Message);
            };
            host.FileTransferSessionOpenReceived += delegate (object? _, FileTransferSessionOpenReceivedEventArgs e)
            {
                sessionOpenReceived.TrySetResult(e.Message);
            };

            const string transferId = "transfer_nkn_tuna_metadata";
            await helper.SendFileTransferOfferAsync(new FileTransferOfferV2 { SessionId = sessionId, TransferId = transferId, FileName = "tuna-metadata.bin", FileSizeBytes = 2048L, PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6 }, cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = sessionId, TransferId = transferId, AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6 }, cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await helper.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2 { SessionId = sessionId, TransferId = transferId, ProtocolVersion = FileTransferProtocol.ProtocolVersionV6, SessionRole = FileTransferProtocol.SessionRoleSender, ChunkSizeBytes = 1024, InitialPipelineDepth = 8 }, cts.Token);
            await sessionOpenReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            IFileTransferDataSession inboundSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            FileTransferTransportProbeFrameV6 probeFrame = new FileTransferTransportProbeFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 1,
                ProbeId = "probe-tuna-metadata",
                TargetTransport = "tuna"
            };
            byte[] payload = EnvelopeCodec.Serialize(BuildSecureFileTransferDataFrameEnvelope(
                helper,
                probeFrame,
                GetNextFileTransferSecureSequence(helper),
                useBulkSenderIdentity: true));

            hostLane.InjectInbound(NknBridgeChannel.Bulk, payload);

            FileTransferReceivedDataFrame receivedMetadata = await inboundSession.ReceiveWithMetadataAsync(cts.Token);
            FileTransferTransportProbeFrameV6 receivedProbe = Assert.IsType<FileTransferTransportProbeFrameV6>(receivedMetadata.Frame);
            Assert.Equal(probeFrame.ProbeId, receivedProbe.ProbeId);
            Assert.Equal(FileTransferTransportKind.Tuna, receivedMetadata.TransportKind);
            Assert.Equal("bulk", receivedMetadata.Lane);
            string logTail = ReadOperationalLogTail(logStartIndex);
            Assert.Contains("effective_transport=tuna; accelerated=1; message_type=file_transfer_data_frame; transfer_id=transfer_nkn_tuna_metadata", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_data_frame_decode_failed", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_AcceptsInboundChunkBatchFrames()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.inboundbatch.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.inboundbatch.address");
            NknIdentity hostIdentity = new NknIdentity("host-inboundbatch-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-inboundbatch-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            TaskCompletionSource<FileTransferOfferV2> offerReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferAcceptV1> acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferSessionOpenV2> sessionOpenReceived = new TaskCompletionSource<FileTransferSessionOpenV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                offerReceived.TrySetResult(e.Message);
            };
            helper.FileTransferAcceptReceived += delegate (object? _, FileTransferAcceptReceivedEventArgs e)
            {
                acceptReceived.TrySetResult(e.Message);
            };
            host.FileTransferSessionOpenReceived += delegate (object? _, FileTransferSessionOpenReceivedEventArgs e)
            {
                sessionOpenReceived.TrySetResult(e.Message);
            };
            await helper.SendFileTransferOfferAsync(new FileTransferOfferV2 { SessionId = sessionId, TransferId = "transfer_nkn_inbound_chunk_batch", FileName = "inbound-batch.bin", FileSizeBytes = 2048L, PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6 }, cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = sessionId, TransferId = "transfer_nkn_inbound_chunk_batch", AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6 }, cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await helper.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2 { SessionId = sessionId, TransferId = "transfer_nkn_inbound_chunk_batch", ProtocolVersion = FileTransferProtocol.ProtocolVersionV6, SessionRole = FileTransferProtocol.SessionRoleSender, ChunkSizeBytes = 1024, InitialPipelineDepth = 8 }, cts.Token);
            await sessionOpenReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            IFileTransferDataSession inboundSession = await host.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_inbound_chunk_batch", cts.Token);
            FileTransferChunkBatchFrameV6 batchFrame = new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = "transfer_nkn_inbound_chunk_batch",
                StartChunkIndex = 12,
                ChunkCount = 2,
                DataSegments = new byte[2][] { Enumerable.Repeat((byte)17, 1024).ToArray(), Enumerable.Repeat((byte)34, 1024).ToArray() }
            };
            CoreSmokeTestsBase.InvokeNknIncomingMessage(host, helperClient, new NknIncomingMessage(payload: EnvelopeCodec.Serialize(CoreSmokeTestsBase.BuildSecureFileTransferDataFrameEnvelope(helper, batchFrame, CoreSmokeTestsBase.GetNextFileTransferSecureSequence(helper), useBulkSenderIdentity: true)), source: helperClient.ConnectedBulkAddress, isTopic: false, topic: null, channel: NknBridgeChannel.Bulk, bridgeIngressObservedUtcMs: 0L, bridgeMessageObservedUtcMs: 0L, binaryFrameDecodedUtcMs: 0L, socketDataEventEmittedUtcMs: 0L, wsReceiverWriteEnteredUtcMs: 0L, wsMessageEmittedUtcMs: 0L, sdkHandleMsgEnteredUtcMs: 0L, clientMessageDispatchUtcMs: 0L, multiClientMessageDispatchUtcMs: 0L));
            FileTransferReceivedDataFrame receivedMetadata = await inboundSession.ReceiveWithMetadataAsync(cts.Token);
            FileTransferDataFrame receivedFrame = receivedMetadata.Frame;
            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            FileTransferChunkBatchFrameV6 receivedBatch = Assert.IsType<FileTransferChunkBatchFrameV6>(receivedFrame);
            Assert.Equal(batchFrame.TransferId, receivedBatch.TransferId);
            Assert.Equal(batchFrame.SessionId, receivedBatch.SessionId);
            Assert.Equal(batchFrame.StartChunkIndex, receivedBatch.StartChunkIndex);
            Assert.Equal(batchFrame.ChunkCount, receivedBatch.ChunkCount);
            Assert.Equal(batchFrame.DataSegments.Count, receivedBatch.DataSegments.Count);
            Assert.Equal(FileTransferTransportKind.RegularNkn, receivedMetadata.TransportKind);
            Assert.Equal("bulk", receivedMetadata.Lane);
            Assert.Contains("event=filetransfer_data_frame_dispatched; transport=nkn; transfer_id=transfer_nkn_inbound_chunk_batch; session_id=", logTail, StringComparison.Ordinal);
            Assert.Contains("frame_type=filetransfer.chunk_batch.v6; chunk_index=12-13; lane=bulk", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_data_frame_decode_failed", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_data_frame_ignored", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_RejectsBulkDataFramesWithSpoofedBridgeSource()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.spoofed-bulk.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.spoofed-bulk.address");
            FakeNknClient spoofClient = new FakeNknClient("spoof.filetransfer.spoofed-bulk.address");
            NknIdentity hostIdentity = new NknIdentity("host-spoofed-bulk-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-spoofed-bulk-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            const string transferId = "transfer_nkn_spoofed_bulk_source";
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            TaskCompletionSource<FileTransferOfferV2> offerReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferAcceptV1> acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferSessionOpenV2> sessionOpenReceived = new TaskCompletionSource<FileTransferSessionOpenV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                offerReceived.TrySetResult(e.Message);
            };
            helper.FileTransferAcceptReceived += delegate (object? _, FileTransferAcceptReceivedEventArgs e)
            {
                acceptReceived.TrySetResult(e.Message);
            };
            host.FileTransferSessionOpenReceived += delegate (object? _, FileTransferSessionOpenReceivedEventArgs e)
            {
                sessionOpenReceived.TrySetResult(e.Message);
            };

            await helper.SendFileTransferOfferAsync(new FileTransferOfferV2 { SessionId = sessionId, TransferId = transferId, FileName = "spoofed-bulk.bin", FileSizeBytes = 2048L, PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6 }, cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = sessionId, TransferId = transferId, AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6 }, cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await helper.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2 { SessionId = sessionId, TransferId = transferId, ProtocolVersion = FileTransferProtocol.ProtocolVersionV6, SessionRole = FileTransferProtocol.SessionRoleSender, ChunkSizeBytes = 1024, InitialPipelineDepth = 8 }, cts.Token);
            await sessionOpenReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);

            IFileTransferDataSession inboundSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            string spoofSource = spoofClient.ConnectedBulkAddress;
            Assert.NotEqual(helperClient.ConnectedBulkAddress, spoofSource);

            long nextSequence = CoreSmokeTestsBase.GetNextFileTransferSecureSequence(helper);
            FileTransferChunkBatchFrameV6 spoofSignedFrame = new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 21,
                ChunkCount = 1,
                DataSegments = new[] { Enumerable.Repeat((byte)51, 1024).ToArray() },
            };
            Envelope spoofSignedEnvelope = BuildSecureFileTransferDataFrameEnvelopeForSenderIdentity(helper, spoofSignedFrame, nextSequence++, spoofSource);
            InjectSecureFileTransferDataFrameEnvelope(host, spoofClient, spoofSignedEnvelope, spoofSource, NknBridgeChannel.Bulk);
            await AssertNoFileTransferFrameAvailableAsync(inboundSession, TimeSpan.FromMilliseconds(150));

            FileTransferChunkBatchFrameV6 sourceMismatchFrame = new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 22,
                ChunkCount = 1,
                DataSegments = new[] { Enumerable.Repeat((byte)68, 1024).ToArray() },
            };
            Envelope sourceMismatchEnvelope = CoreSmokeTestsBase.BuildSecureFileTransferDataFrameEnvelope(helper, sourceMismatchFrame, nextSequence++, useBulkSenderIdentity: true);
            InjectSecureFileTransferDataFrameEnvelope(host, spoofClient, sourceMismatchEnvelope, spoofSource, NknBridgeChannel.Bulk);
            await AssertNoFileTransferFrameAvailableAsync(inboundSession, TimeSpan.FromMilliseconds(150));

            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            if (string.IsNullOrWhiteSpace(logTail))
            {
                logTail = LocalOperationalLog.GetRecentLogText();
            }

            Assert.Contains("event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=secure_envelope_invalid", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=source_identity_mismatch", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_data_frame_dispatched; transport=nkn; transfer_id=transfer_nkn_spoofed_bulk_source", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_OverflowFailsClosedAndRequiresLocalReopen()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(12.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.queue-overflow.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.queue-overflow.address");
            NknIdentity hostIdentity = new NknIdentity("host-queue-overflow-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-queue-overflow-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            const string transferId = "transfer_nkn_data_session_queue_overflow";
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            TaskCompletionSource<FileTransferOfferV2> offerReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferAcceptV1> acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferSessionOpenV2> sessionOpenReceived = new TaskCompletionSource<FileTransferSessionOpenV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                offerReceived.TrySetResult(e.Message);
            };
            helper.FileTransferAcceptReceived += delegate (object? _, FileTransferAcceptReceivedEventArgs e)
            {
                acceptReceived.TrySetResult(e.Message);
            };
            host.FileTransferSessionOpenReceived += delegate (object? _, FileTransferSessionOpenReceivedEventArgs e)
            {
                sessionOpenReceived.TrySetResult(e.Message);
            };

            await helper.SendFileTransferOfferAsync(
                new FileTransferOfferV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = "queue-overflow.bin",
                    FileSizeBytes = 4096L,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await helper.SendFileTransferSessionOpenAsync(
                new FileTransferSessionOpenV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                    SessionRole = FileTransferProtocol.SessionRoleSender,
                    ChunkSizeBytes = 1024,
                    InitialPipelineDepth = 8,
                },
                cts.Token);
            await sessionOpenReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);

            IFileTransferDataSession inboundSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            TaskCompletionSource<FileTransferDataSessionAvailabilityChangedEventArgs> availabilityChanged = new TaskCompletionSource<FileTransferDataSessionAvailabilityChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            inboundSession.AvailabilityChanged += delegate (object? _, FileTransferDataSessionAvailabilityChangedEventArgs e)
            {
                availabilityChanged.TrySetResult(e);
            };

            var nextFileTransferSequence = CoreSmokeTestsBase.GetNextFileTransferSecureSequence(helper);
            for (var i = 0; i <= NknSignalingTransport.FileTransferDataSessionMaxQueuedFrames; i++)
            {
                var batchFrame = new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = i,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                };
                InjectSecureFileTransferDataFrame(host, helper, helperClient, batchFrame, NknBridgeChannel.Bulk, nextFileTransferSequence++);
            }

            await CoreSmokeTestsBase.WaitUntilAsync(() => !inboundSession.IsAvailable, TimeSpan.FromSeconds(2.0));

            Assert.False(inboundSession.IsAvailable);
            FileTransferDataSessionAvailabilityChangedEventArgs availabilityChange = await availabilityChanged.Task.WaitAsync(TimeSpan.FromSeconds(2.0), cts.Token);
            Assert.False(availabilityChange!.IsAvailable);
            Assert.Equal(FileTransferResultCodes.ReceiverBufferExhausted, availabilityChange.Reason);
            Assert.True(availabilityChange.RequiresResumeRequest);

            var postOverflowFrame = new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = NknSignalingTransport.FileTransferDataSessionMaxQueuedFrames + 1,
                ChunkCount = 1,
                DataSegments = new[] { new byte[1024] },
            };
            InjectSecureFileTransferDataFrame(host, helper, helperClient, postOverflowFrame, NknBridgeChannel.Bulk, nextFileTransferSequence++);

            await Task.Delay(100, cts.Token);
            IDictionary dataSessionsAfterOverflow = Assert.IsAssignableFrom<IDictionary>(CoreSmokeTestsBase.GetPrivateField(host, "fileTransferDataSessions"));
            string overflowLogText = LocalOperationalLog.GetRecentLogText();
            Assert.Empty(dataSessionsAfterOverflow);
            Assert.Contains("event=filetransfer_data_session_overflow; transport=nkn;", overflowLogText, StringComparison.Ordinal);
            Assert.Contains("reason=receiver_buffer_exhausted", overflowLogText, StringComparison.Ordinal);

            IFileTransferDataSession replacementSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            Assert.NotSame(inboundSession, replacementSession);
            Assert.True(replacementSession.IsAvailable);

            var resumedFrame = new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = NknSignalingTransport.FileTransferDataSessionMaxQueuedFrames + 2,
                ChunkCount = 1,
                DataSegments = new[] { new byte[1024] },
            };
            InjectSecureFileTransferDataFrame(host, helper, helperClient, resumedFrame, NknBridgeChannel.Bulk, nextFileTransferSequence++);
            FileTransferDataFrame receivedFrame = await replacementSession.ReceiveAsync(cts.Token);
            FileTransferChunkBatchFrameV6 receivedBatch = Assert.IsType<FileTransferChunkBatchFrameV6>(receivedFrame);
            Assert.Equal(resumedFrame.StartChunkIndex, receivedBatch.StartChunkIndex);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void NknTransport_FileTransferSecureStateReset_ClearsRemoteOpenSuppression()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.filetransfer.suppression-reset.address");
            var hostIdentity = new NknIdentity("host-suppression-reset-id", hostClient.Address);
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            var suppressed = Assert.IsAssignableFrom<ISet<string>>(
                CoreSmokeTestsBase.GetPrivateField(host, "fileTransferDataSessionRemoteOpenSuppressed"));

            suppressed.Add("transfer_reused_after_overflow");
            CoreSmokeTestsBase.InvokePrivateMethod(host, "SetControlSessionSharedKey", RandomNumberGenerator.GetBytes(32));
            Assert.Empty(suppressed);

            suppressed.Add("transfer_reused_after_disconnect");
            CoreSmokeTestsBase.InvokePrivateMethod(host, "ResetControlSecureState");
            Assert.Empty(suppressed);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_V6CancelDataFrame_IsRejectedInsteadOfPriorityCancel()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.v4-priority-cancel.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.v4-priority-cancel.address");
            NknIdentity hostIdentity = new NknIdentity("host-v4-priority-cancel-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-v4-priority-cancel-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_nkn_v4_priority_cancel";

            TaskCompletionSource<FileTransferOfferV2> offerReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferAcceptV1> acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferSessionOpenV2> sessionOpenReceived = new TaskCompletionSource<FileTransferSessionOpenV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferCancelV1> cancelReceived = new TaskCompletionSource<FileTransferCancelV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal))
                {
                    offerReceived.TrySetResult(e.Message);
                }
            };
            helper.FileTransferAcceptReceived += delegate (object? _, FileTransferAcceptReceivedEventArgs e)
            {
                if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal))
                {
                    acceptReceived.TrySetResult(e.Message);
                }
            };
            host.FileTransferSessionOpenReceived += delegate (object? _, FileTransferSessionOpenReceivedEventArgs e)
            {
                if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal))
                {
                    sessionOpenReceived.TrySetResult(e.Message);
                }
            };
            host.FileTransferCancelReceived += delegate (object? _, FileTransferCancelReceivedEventArgs e)
            {
                if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal))
                {
                    cancelReceived.TrySetResult(e.Message);
                }
            };

            await helper.SendFileTransferOfferAsync(
                new FileTransferOfferV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = "priority-cancel.bin",
                    FileSizeBytes = 4096L,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await helper.SendFileTransferSessionOpenAsync(
                new FileTransferSessionOpenV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                    SessionRole = FileTransferProtocol.SessionRoleSender,
                    ChunkSizeBytes = 1024,
                    InitialPipelineDepth = 8,
                },
                cts.Token);
            await sessionOpenReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);

            IFileTransferDataSession hostDataSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var nextSequence = CoreSmokeTestsBase.GetNextFileTransferSecureSequence(helper);
            InjectSecureFileTransferDataFrame(
                host,
                helper,
                helperClient,
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                },
                NknBridgeChannel.Bulk,
                nextSequence++);

            InjectSecureFileTransferDataFrame(
                host,
                helper,
                helperClient,
                new FileTransferCancelFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = "sender_canceled",
                },
                NknBridgeChannel.Bulk,
                nextSequence++);

            FileTransferDataFrame queuedBeforeCancel = await hostDataSession.ReceiveAsync(cts.Token);
            Assert.IsType<FileTransferChunkBatchFrameV6>(queuedBeforeCancel);
            await Task.Delay(150, cts.Token);
            Assert.False(cancelReceived.Task.IsCompleted);

            InjectSecureFileTransferDataFrame(
                host,
                helper,
                helperClient,
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 1,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                },
                NknBridgeChannel.Bulk,
                nextSequence++);

            FileTransferDataFrame queuedAfterRejectedCancel = await hostDataSession.ReceiveAsync(cts.Token);
            var batchAfterRejectedCancel = Assert.IsType<FileTransferChunkBatchFrameV6>(queuedAfterRejectedCancel);
            Assert.Equal(1, batchAfterRejectedCancel.StartChunkIndex);

            await Task.Delay(100, cts.Token);
            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            Assert.Contains("event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=lifecycle_data_frame_unsupported", logTail, StringComparison.Ordinal);
            Assert.Contains("file_transfer_data_frame_lifecycle_data_frame_unsupported", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v4_cancel_frame_received", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("post_terminal_late_frame_canceled", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FileTransferCancelBulkCopy_AcceptsBulkSenderIdentity()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.bulk-cancel.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.bulk-cancel.address");
            NknIdentity hostIdentity = new NknIdentity("host-bulk-cancel-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-bulk-cancel-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_nkn_bulk_cancel_copy";

            TaskCompletionSource<FileTransferOfferV2> offerReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferCancelV1> cancelReceived = new TaskCompletionSource<FileTransferCancelV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal))
                {
                    offerReceived.TrySetResult(e.Message);
                }
            };
            host.FileTransferCancelReceived += delegate (object? _, FileTransferCancelReceivedEventArgs e)
            {
                if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal))
                {
                    cancelReceived.TrySetResult(e.Message);
                }
            };

            await helper.SendFileTransferOfferAsync(
                new FileTransferOfferV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = "bulk-cancel.bin",
                    FileSizeBytes = 4096L,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);

            var cancelEnvelope = BuildSecureFileTransferCancelEnvelopeForSenderIdentity(
                helper,
                new FileTransferCancelV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Reason = "bulk_cancel_copy",
                },
                CoreSmokeTestsBase.GetNextFileTransferSecureSequence(helper),
                helperClient.BulkAddress);
            CoreSmokeTestsBase.InvokeNknIncomingMessage(
                host,
                helperClient,
                new NknIncomingMessage(
                    payload: EnvelopeCodec.Serialize(cancelEnvelope),
                    source: helperClient.ConnectedBulkAddress,
                    isTopic: false,
                    topic: null,
                    channel: NknBridgeChannel.Bulk,
                    bridgeIngressObservedUtcMs: 0L,
                    bridgeMessageObservedUtcMs: 0L,
                    binaryFrameDecodedUtcMs: 0L,
                    socketDataEventEmittedUtcMs: 0L,
                    wsReceiverWriteEnteredUtcMs: 0L,
                    wsMessageEmittedUtcMs: 0L,
                    sdkHandleMsgEnteredUtcMs: 0L,
                    clientMessageDispatchUtcMs: 0L,
                    multiClientMessageDispatchUtcMs: 0L));

            FileTransferCancelV1 cancel = await cancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            Assert.Equal("bulk_cancel_copy", cancel.Reason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_FileTransferPauseControlAndHeartbeat_DispatchSecureControlAndRejectReplay()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.lifecycle-control.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.lifecycle-control.address");
            NknIdentity hostIdentity = new NknIdentity("host-lifecycle-control-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-lifecycle-control-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_nkn_lifecycle_control";

            TaskCompletionSource<FileTransferOfferV2> offerReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferAcceptV1> acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferPauseControlV6> pauseReceived = new TaskCompletionSource<FileTransferPauseControlV6>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferHeartbeatV6> heartbeatReceived = new TaskCompletionSource<FileTransferHeartbeatV6>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pauseDispatchCount = 0;
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal))
                {
                    offerReceived.TrySetResult(e.Message);
                }
            };
            helper.FileTransferAcceptReceived += delegate (object? _, FileTransferAcceptReceivedEventArgs e)
            {
                if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal))
                {
                    acceptReceived.TrySetResult(e.Message);
                }
            };
            host.FileTransferPauseControlReceived += delegate (object? _, FileTransferPauseControlReceivedEventArgs e)
            {
                if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref pauseDispatchCount);
                    pauseReceived.TrySetResult(e.Message);
                }
            };
            host.FileTransferHeartbeatReceived += delegate (object? _, FileTransferHeartbeatReceivedEventArgs e)
            {
                if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal))
                {
                    heartbeatReceived.TrySetResult(e.Message);
                }
            };

            await helper.SendFileTransferOfferAsync(
                new FileTransferOfferV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = "lifecycle-control.bin",
                    FileSizeBytes = 4096L,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);

            var nextSequence = CoreSmokeTestsBase.GetNextFileTransferSecureSequence(helper);
            Envelope pauseEnvelope = CoreSmokeTestsBase.BuildSecureFileTransferEnvelope(
                helper,
                MsgType.FileTransferPauseControl,
                new FileTransferPauseControlV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 2,
                    Paused = true,
                    Reason = "user_pause",
                    TransportEpoch = 4,
                    Priority = "frontier",
                },
                transferId,
                nextSequence++);
            InjectSecureFileTransferDataFrameEnvelope(host, helperClient, pauseEnvelope, helper.LocalPeerAddress, NknBridgeChannel.Control);

            FileTransferPauseControlV6 pause = await pauseReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            Assert.True(pause.Paused);
            Assert.Equal("user_pause", pause.Reason);
            Assert.Equal(4, pause.TransportEpoch);

            InjectSecureFileTransferDataFrameEnvelope(host, helperClient, pauseEnvelope, helper.LocalPeerAddress, NknBridgeChannel.Control);
            await Task.Delay(150, cts.Token);
            Assert.Equal(1, Volatile.Read(ref pauseDispatchCount));
            Assert.Contains("duplicate", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason, StringComparison.Ordinal);

            Envelope heartbeatEnvelope = CoreSmokeTestsBase.BuildSecureFileTransferEnvelope(
                helper,
                MsgType.FileTransferHeartbeat,
                new FileTransferHeartbeatV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = 4,
                    Sequence = 12,
                    SentUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                },
                transferId,
                nextSequence);
            InjectSecureFileTransferDataFrameEnvelope(host, helperClient, heartbeatEnvelope, helper.LocalPeerAddress, NknBridgeChannel.Control);

            FileTransferHeartbeatV6 heartbeat = await heartbeatReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            Assert.Equal(12, heartbeat.Sequence);
            Assert.Equal(4, heartbeat.TransportEpoch);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_V6ControlComplete_ClearsSameDirectionBusyGuard()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.v4-repeat.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.v4-repeat.address");
            NknIdentity hostIdentity = new NknIdentity("host-v4-repeat-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-v4-repeat-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);

            TaskCompletionSource<FileTransferOfferV2> firstOfferReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferOfferV2> secondOfferReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferAcceptV1> acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferSessionOpenV2> sessionOpenReceived = new TaskCompletionSource<FileTransferSessionOpenV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferCompleteV1> completeReceived = new TaskCompletionSource<FileTransferCompleteV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                if (string.Equals(e.Message.TransferId, "transfer_nkn_v4_repeat_1", StringComparison.Ordinal))
                {
                    firstOfferReceived.TrySetResult(e.Message);
                }
                else if (string.Equals(e.Message.TransferId, "transfer_nkn_v4_repeat_2", StringComparison.Ordinal))
                {
                    secondOfferReceived.TrySetResult(e.Message);
                }
            };
            helper.FileTransferAcceptReceived += delegate (object? _, FileTransferAcceptReceivedEventArgs e)
            {
                acceptReceived.TrySetResult(e.Message);
            };
            host.FileTransferSessionOpenReceived += delegate (object? _, FileTransferSessionOpenReceivedEventArgs e)
            {
                sessionOpenReceived.TrySetResult(e.Message);
            };
            helper.FileTransferCompleteReceived += delegate (object? _, FileTransferCompleteReceivedEventArgs e)
            {
                if (string.Equals(e.Message.TransferId, "transfer_nkn_v4_repeat_1", StringComparison.Ordinal))
                {
                    completeReceived.TrySetResult(e.Message);
                }
            };

            await helper.SendFileTransferOfferAsync(
                new FileTransferOfferV2
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_v4_repeat_1",
                    FileName = "first.bin",
                    FileSizeBytes = 1024L,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                cts.Token);
            await firstOfferReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_v4_repeat_1",
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);

            await helper.SendFileTransferSessionOpenAsync(
                new FileTransferSessionOpenV2
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_v4_repeat_1",
                    ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                    SessionRole = FileTransferProtocol.SessionRoleSender,
                    ChunkSizeBytes = 21 * 1024,
                    InitialPipelineDepth = 8,
                },
                cts.Token);
            await sessionOpenReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);

            await host.SendFileTransferCompleteAsync(
                new FileTransferCompleteV1
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_v4_repeat_1",
                    FileSizeBytes = 1024L,
                    Sha256Base64 = Convert.ToBase64String(new byte[32]),
                },
                cts.Token);
            FileTransferCompleteV1 complete = await completeReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            Assert.Equal("transfer_nkn_v4_repeat_1", complete.TransferId);
            IDictionary hostTransferStates = Assert.IsAssignableFrom<IDictionary>(CoreSmokeTestsBase.GetPrivateField(host, "fileTransferStates"));
            IDictionary helperTransferStates = Assert.IsAssignableFrom<IDictionary>(CoreSmokeTestsBase.GetPrivateField(helper, "fileTransferStates"));
            Assert.False(hostTransferStates.Contains("transfer_nkn_v4_repeat_1"));
            Assert.False(helperTransferStates.Contains("transfer_nkn_v4_repeat_1"));

            await helper.SendFileTransferOfferAsync(
                new FileTransferOfferV2
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_v4_repeat_2",
                    FileName = "second.bin",
                    FileSizeBytes = 1024L,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                cts.Token);
            FileTransferOfferV2 secondOffer = await secondOfferReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            Assert.Equal("transfer_nkn_v4_repeat_2", secondOffer.TransferId);

            var lateBatch = new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = "transfer_nkn_v4_repeat_1",
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = new[] { new byte[1024] },
            };
            CoreSmokeTestsBase.InvokeNknIncomingMessage(
                host,
                helperClient,
                new NknIncomingMessage(
                    payload: EnvelopeCodec.Serialize(CoreSmokeTestsBase.BuildSecureFileTransferDataFrameEnvelope(helper, lateBatch, CoreSmokeTestsBase.GetNextFileTransferSecureSequence(helper), useBulkSenderIdentity: true)),
                    source: helperClient.ConnectedBulkAddress,
                    isTopic: false,
                    topic: null,
                    channel: NknBridgeChannel.Bulk,
                    bridgeIngressObservedUtcMs: 0L,
                    bridgeMessageObservedUtcMs: 0L,
                    binaryFrameDecodedUtcMs: 0L,
                    socketDataEventEmittedUtcMs: 0L,
                    wsReceiverWriteEnteredUtcMs: 0L,
                    wsMessageEmittedUtcMs: 0L,
                    sdkHandleMsgEnteredUtcMs: 0L,
                    clientMessageDispatchUtcMs: 0L,
                    multiClientMessageDispatchUtcMs: 0L));
            await Task.Delay(100, cts.Token);
            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            Assert.Contains("event=filetransfer_data_frame_ignored; transport=nkn; transfer_id=transfer_nkn_v4_repeat_1", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=post_completion_late_sender_frame", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=unknown_transfer_id", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=transfer_already_terminal", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Theory]
    [InlineData("declined", "post_terminal_late_sender_frame_declined")]
    [InlineData("canceled", "post_terminal_late_sender_frame_canceled")]
    [InlineData("failed", "post_terminal_late_sender_frame_failed")]
    public async Task NknTransport_FileTransfer_LateSenderFrameAfterNonCompletedTerminal_IsRejectedAsPostTerminal(
        string terminalKind,
        string expectedReason)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.declined-late.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.declined-late.address");
            NknIdentity hostIdentity = new NknIdentity("host-declined-late-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-declined-late-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            string transferId = $"transfer_nkn_v4_{terminalKind}_late_sender";

            TaskCompletionSource<FileTransferOfferV2> offerReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<string> terminalReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal))
                {
                    offerReceived.TrySetResult(e.Message);
                }
            };
            helper.FileTransferDeclineReceived += delegate (object? _, FileTransferDeclineReceivedEventArgs e)
            {
                if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal) &&
                    string.Equals(terminalKind, "declined", StringComparison.Ordinal))
                {
                    terminalReceived.TrySetResult(e.Message.TransferId);
                }
            };
            host.FileTransferCancelReceived += delegate (object? _, FileTransferCancelReceivedEventArgs e)
            {
                if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal) &&
                    string.Equals(terminalKind, "canceled", StringComparison.Ordinal))
                {
                    terminalReceived.TrySetResult(e.Message.TransferId);
                }
            };
            host.FileTransferErrorReceived += delegate (object? _, FileTransferErrorReceivedEventArgs e)
            {
                if (string.Equals(e.Message.TransferId, transferId, StringComparison.Ordinal) &&
                    string.Equals(terminalKind, "failed", StringComparison.Ordinal))
                {
                    terminalReceived.TrySetResult(e.Message.TransferId);
                }
            };

            await helper.SendFileTransferOfferAsync(
                new FileTransferOfferV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileName = "declined.bin",
                    FileSizeBytes = 1024L,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            switch (terminalKind)
            {
                case "declined":
                    await host.SendFileTransferDeclineAsync(
                        new FileTransferDeclineV1
                        {
                            SessionId = sessionId,
                            TransferId = transferId,
                            Reason = "test_decline",
                        },
                        cts.Token);
                    break;
                case "canceled":
                    await helper.SendFileTransferCancelAsync(
                        new FileTransferCancelV1
                        {
                            SessionId = sessionId,
                            TransferId = transferId,
                            Reason = "test_cancel",
                        },
                        cts.Token);
                    break;
                case "failed":
                    await helper.SendFileTransferErrorAsync(
                        new FileTransferErrorV1
                        {
                            SessionId = sessionId,
                            TransferId = transferId,
                            ErrorCode = "test_failure",
                            Message = "test failure",
                        },
                        cts.Token);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported terminal kind.");
            }

            await terminalReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);

            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            var lateBatch = new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = new[] { new byte[1024] },
            };
            InjectSecureFileTransferDataFrame(host, helper, helperClient, lateBatch, NknBridgeChannel.Bulk, CoreSmokeTestsBase.GetNextFileTransferSecureSequence(helper));
            await Task.Delay(100, cts.Token);

            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            Assert.Contains(transferId, logTail, StringComparison.Ordinal);
            if (string.Equals(terminalKind, "canceled", StringComparison.Ordinal))
            {
                Assert.Contains("event=filetransfer_data_frame_ignored; transport=nkn", logTail, StringComparison.Ordinal);
                Assert.Contains("post_terminal_late_frame_canceled", logTail, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("event=filetransfer_message_rejected; message_type=file_transfer_data_frame", logTail, StringComparison.Ordinal);
                Assert.Contains($"file_transfer_data_frame_{expectedReason}", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason, StringComparison.Ordinal);
                Assert.DoesNotContain($"event=filetransfer_data_frame_ignored; transport=nkn; transfer_id={transferId}", logTail, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("reason=post_completion_late_sender_frame", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataFrame_DisposedDataSessionDoesNotBlackHoleFrames()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.disposed-data-session.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.disposed-data-session.address");
            NknIdentity hostIdentity = new NknIdentity("host-disposed-data-session-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-disposed-data-session-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            const string transferId = "transfer_nkn_disposed_data_session";
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            TaskCompletionSource<FileTransferOfferV2> offerReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferAcceptV1> acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferSessionOpenV2> sessionOpenReceived = new TaskCompletionSource<FileTransferSessionOpenV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                offerReceived.TrySetResult(e.Message);
            };
            helper.FileTransferAcceptReceived += delegate (object? _, FileTransferAcceptReceivedEventArgs e)
            {
                acceptReceived.TrySetResult(e.Message);
            };
            host.FileTransferSessionOpenReceived += delegate (object? _, FileTransferSessionOpenReceivedEventArgs e)
            {
                sessionOpenReceived.TrySetResult(e.Message);
            };

            await helper.SendFileTransferOfferAsync(new FileTransferOfferV2 { SessionId = sessionId, TransferId = transferId, FileName = "disposed-session.bin", FileSizeBytes = 2048L, PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6 }, cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = sessionId, TransferId = transferId, AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6 }, cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await helper.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2 { SessionId = sessionId, TransferId = transferId, ProtocolVersion = FileTransferProtocol.ProtocolVersionV6, SessionRole = FileTransferProtocol.SessionRoleSender, ChunkSizeBytes = 1024, InitialPipelineDepth = 8 }, cts.Token);
            await sessionOpenReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);

            IFileTransferDataSession staleSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            staleSession.Dispose();

            FileTransferChunkBatchFrameV6 batchFrame = new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 4,
                ChunkCount = 2,
                DataSegments = new byte[2][] { Enumerable.Repeat((byte)17, 1024).ToArray(), Enumerable.Repeat((byte)34, 1024).ToArray() }
            };
            CoreSmokeTestsBase.InvokeNknIncomingMessage(host, helperClient, new NknIncomingMessage(payload: EnvelopeCodec.Serialize(CoreSmokeTestsBase.BuildSecureFileTransferDataFrameEnvelope(helper, batchFrame, CoreSmokeTestsBase.GetNextFileTransferSecureSequence(helper), useBulkSenderIdentity: true)), source: helperClient.ConnectedBulkAddress, isTopic: false, topic: null, channel: NknBridgeChannel.Bulk, bridgeIngressObservedUtcMs: 0L, bridgeMessageObservedUtcMs: 0L, binaryFrameDecodedUtcMs: 0L, socketDataEventEmittedUtcMs: 0L, wsReceiverWriteEnteredUtcMs: 0L, wsMessageEmittedUtcMs: 0L, sdkHandleMsgEnteredUtcMs: 0L, clientMessageDispatchUtcMs: 0L, multiClientMessageDispatchUtcMs: 0L));

            IFileTransferDataSession replacementSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            FileTransferDataFrame receivedFrame = await replacementSession.ReceiveAsync(cts.Token);
            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            FileTransferChunkBatchFrameV6 receivedBatch = Assert.IsType<FileTransferChunkBatchFrameV6>(receivedFrame);
            Assert.Equal(batchFrame.StartChunkIndex, receivedBatch.StartChunkIndex);
            Assert.Equal(batchFrame.DataSegments.Count, receivedBatch.DataSegments.Count);
            Assert.Contains("event=filetransfer_data_session_removed; transport=nkn; transfer_id=transfer_nkn_disposed_data_session", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_data_frame_dispatched; transport=nkn; transfer_id=transfer_nkn_disposed_data_session", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_data_frame_ignored", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataFrame_UnknownTransferId_IsRejectedWithoutAllocatingSession()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.unknown-dataframe.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.unknown-dataframe.address");
            NknIdentity hostIdentity = new NknIdentity("host-unknown-dataframe-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-unknown-dataframe-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            FileTransferManifestFrameV6 frame = new FileTransferManifestFrameV6
            {
                SessionId = sessionId,
                TransferId = "transfer_nkn_unknown_data_frame",
                FileName = "unknown.bin",
                FileSizeBytes = 16L,
                ChunkSizeBytes = 16,
                ChunkCount = 1,
                Sha256Base64 = Convert.ToBase64String(new byte[32])
            };
            CoreSmokeTestsBase.InvokeNknIncomingMessage(host, helperClient, new NknIncomingMessage(payload: EnvelopeCodec.Serialize(CoreSmokeTestsBase.BuildSecureFileTransferDataFrameEnvelope(helper, frame, CoreSmokeTestsBase.GetNextFileTransferSecureSequence(helper))), source: helper.LocalPeerAddress, isTopic: false, topic: null, channel: NknBridgeChannel.Bulk, bridgeIngressObservedUtcMs: 0L, bridgeMessageObservedUtcMs: 0L, binaryFrameDecodedUtcMs: 0L, socketDataEventEmittedUtcMs: 0L, wsReceiverWriteEnteredUtcMs: 0L, wsMessageEmittedUtcMs: 0L, sdkHandleMsgEnteredUtcMs: 0L, clientMessageDispatchUtcMs: 0L, multiClientMessageDispatchUtcMs: 0L));
            await Task.Delay(200, cts.Token);
            IDictionary dataSessions = Assert.IsAssignableFrom<IDictionary>(CoreSmokeTestsBase.GetPrivateField(host, "fileTransferDataSessions"));
            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            Assert.Empty(dataSessions);
            Assert.DoesNotContain("event=filetransfer_data_frame_dispatched", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_data_frame_ignored", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    private static FileTransferDataFrame DecodeNknDataFrame(
        NknSignalingTransport recipient,
        NknSignalingTransport sender,
        NknIncomingMessage message,
        string sessionId)
    {
        Assert.True(EnvelopeCodec.TryDeserialize(message.Payload, out Envelope env));
        byte[] decryptKey = Assert.IsType<byte[]>(CoreSmokeTestsBase.GetPrivateField(recipient, "fileTransferSessionSharedKey")).AsSpan().ToArray();
        var senderClient = Assert.IsAssignableFrom<INknClient>(CoreSmokeTestsBase.GetPrivateField(sender, "client"));
        var expectedSenderIdentity = message.Channel == NknBridgeChannel.Bulk
            ? senderClient.BulkAddress
            : sender.LocalPeerAddress;
        SessionSecureEnvelopePayload payload = SessionSecureEnvelopeCodec.Decrypt(
            decryptKey,
            env.Payload,
            new SessionSecureEnvelopeExpectation(
                SessionSecureMessageFamily.FileTransfer,
                "file_transfer_data_frame",
                new SessionId(sessionId),
                new PeerAddress(expectedSenderIdentity)));
        Assert.True(FileTransferDataFrameCodec.TryDeserialize(payload.Plaintext, out FileTransferDataFrame? frame));
        return Assert.IsAssignableFrom<FileTransferDataFrame>(frame);
    }

    private static void InjectSecureFileTransferDataFrame(
        NknSignalingTransport recipient,
        NknSignalingTransport sender,
        FakeNknClient senderClient,
        FileTransferDataFrame frame,
        NknBridgeChannel channel,
        long sequence)
    {
        var source = channel == NknBridgeChannel.Bulk
            ? senderClient.ConnectedBulkAddress
            : sender.LocalPeerAddress;
        CoreSmokeTestsBase.InvokeNknIncomingMessage(
            recipient,
            senderClient,
            new NknIncomingMessage(
                payload: EnvelopeCodec.Serialize(CoreSmokeTestsBase.BuildSecureFileTransferDataFrameEnvelope(
                    sender,
                    frame,
                    sequence,
                    useBulkSenderIdentity: channel == NknBridgeChannel.Bulk)),
                source: source,
                isTopic: false,
                topic: null,
                channel: channel,
                bridgeIngressObservedUtcMs: 0L,
                bridgeMessageObservedUtcMs: 0L,
                binaryFrameDecodedUtcMs: 0L,
                socketDataEventEmittedUtcMs: 0L,
                wsReceiverWriteEnteredUtcMs: 0L,
                wsMessageEmittedUtcMs: 0L,
                sdkHandleMsgEnteredUtcMs: 0L,
                clientMessageDispatchUtcMs: 0L,
                multiClientMessageDispatchUtcMs: 0L));
    }

    private static void InjectSecureFileTransferDataFrameEnvelope(
        NknSignalingTransport recipient,
        FakeNknClient senderClient,
        Envelope envelope,
        string source,
        NknBridgeChannel channel)
    {
        CoreSmokeTestsBase.InvokeNknIncomingMessage(
            recipient,
            senderClient,
            new NknIncomingMessage(
                payload: EnvelopeCodec.Serialize(envelope),
                source: source,
                isTopic: false,
                topic: null,
                channel: channel,
                bridgeIngressObservedUtcMs: 0L,
                bridgeMessageObservedUtcMs: 0L,
                binaryFrameDecodedUtcMs: 0L,
                socketDataEventEmittedUtcMs: 0L,
                wsReceiverWriteEnteredUtcMs: 0L,
                wsMessageEmittedUtcMs: 0L,
                sdkHandleMsgEnteredUtcMs: 0L,
                clientMessageDispatchUtcMs: 0L,
                multiClientMessageDispatchUtcMs: 0L));
    }

    private static Envelope BuildSecureFileTransferDataFrameEnvelopeForSenderIdentity(
        NknSignalingTransport senderTransport,
        FileTransferDataFrame frame,
        long sequence,
        string senderIdentity)
    {
        var key = Assert.IsType<byte[]>(CoreSmokeTestsBase.GetPrivateField(senderTransport, "fileTransferSessionSharedKey")).AsSpan().ToArray();
        var envelopeCode = Assert.IsType<string>(CoreSmokeTestsBase.GetPrivateField(senderTransport, "currentEnvelopeCode"));
        var sessionId = Assert.IsType<SessionId>(senderTransport.CurrentSessionSecurityState.SessionId);
        var plaintext = FileTransferDataFrameCodec.Serialize(frame);
        var securePayload = SessionSecureEnvelopeCodec.Encrypt(
            key,
            new SessionSecureEnvelopeMetadata(
                Family: SessionSecureMessageFamily.FileTransfer,
                MessageType: "file_transfer_data_frame",
                SessionId: sessionId,
                SenderIdentity: new PeerAddress(senderIdentity),
                Sequence: sequence,
                RequestId: string.IsNullOrWhiteSpace(frame.TransferId) ? null : frame.TransferId),
            plaintext);

        return new Envelope(
            Version: 1,
            Code: envelopeCode,
            MessageId: Guid.NewGuid().ToString("N"),
            Type: MsgType.FileTransferDataFrame,
            Payload: securePayload,
            UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyTo: null);
    }

    private static Envelope BuildSecureFileTransferCancelEnvelopeForSenderIdentity(
        NknSignalingTransport senderTransport,
        FileTransferCancelV1 cancel,
        long sequence,
        string senderIdentity)
    {
        var key = Assert.IsType<byte[]>(CoreSmokeTestsBase.GetPrivateField(senderTransport, "fileTransferSessionSharedKey")).AsSpan().ToArray();
        var envelopeCode = Assert.IsType<string>(CoreSmokeTestsBase.GetPrivateField(senderTransport, "currentEnvelopeCode"));
        var sessionId = Assert.IsType<SessionId>(senderTransport.CurrentSessionSecurityState.SessionId);
        var plaintext = FileTransferPayloadCodec.Serialize(cancel);
        var securePayload = SessionSecureEnvelopeCodec.Encrypt(
            key,
            new SessionSecureEnvelopeMetadata(
                Family: SessionSecureMessageFamily.FileTransfer,
                MessageType: "file_transfer_cancel",
                SessionId: sessionId,
                SenderIdentity: new PeerAddress(senderIdentity),
                Sequence: sequence,
                RequestId: cancel.TransferId),
            plaintext);

        return new Envelope(
            Version: 1,
            Code: envelopeCode,
            MessageId: Guid.NewGuid().ToString("N"),
            Type: MsgType.FileTransferCancel,
            Payload: securePayload,
            UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyTo: null);
    }

    private static async Task AssertNoFileTransferFrameAvailableAsync(IFileTransferDataSession session, TimeSpan timeout)
    {
        using CancellationTokenSource receiveCts = new CancellationTokenSource(timeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await session.ReceiveAsync(receiveCts.Token));
    }

    private static Envelope BuildSecureLegacyFileTransferEnvelope(
        NknSignalingTransport senderTransport,
        MsgType messageType,
        string sessionId,
        string transferId,
        long sequence)
    {
        var key = Assert.IsType<byte[]>(CoreSmokeTestsBase.GetPrivateField(senderTransport, "fileTransferSessionSharedKey")).AsSpan().ToArray();
        var envelopeCode = Assert.IsType<string>(CoreSmokeTestsBase.GetPrivateField(senderTransport, "currentEnvelopeCode"));
        var senderClient = Assert.IsAssignableFrom<INknClient>(CoreSmokeTestsBase.GetPrivateField(senderTransport, "client"));
        var senderIdentity = messageType == MsgType.FileTransferChunk
            ? senderClient.BulkAddress
            : senderTransport.LocalPeerAddress;
        try
        {
            var securePayload = SessionSecureEnvelopeCodec.Encrypt(
                key,
                new SessionSecureEnvelopeMetadata(
                    SessionSecureMessageFamily.FileTransfer,
                    messageType switch
                    {
                        MsgType.FileTransferStart => "file_transfer_start",
                        MsgType.FileTransferChunk => "file_transfer_chunk",
                        MsgType.FileTransferWindowUpdate => "file_transfer_window_update",
                        MsgType.FileTransferMissingRange => "file_transfer_missing_range",
                        MsgType.FileTransferPressureState => "file_transfer_pressure_state",
                        _ => throw new ArgumentOutOfRangeException(nameof(messageType), messageType, "Unsupported legacy file-transfer message type."),
                    },
                    new SessionId(sessionId),
                    new PeerAddress(senderIdentity),
                    sequence,
                    transferId),
                "legacy ignored payload"u8);

            return new Envelope(
                Version: 1,
                Code: envelopeCode,
                MessageId: Guid.NewGuid().ToString("N"),
                Type: messageType,
                Payload: securePayload,
                UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ReplyTo: null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

}
