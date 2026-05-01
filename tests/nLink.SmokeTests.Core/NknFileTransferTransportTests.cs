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
            FileTransferChunkBudgetRequest request = new FileTransferChunkBudgetRequest("transfer_nkn_payload_budget", 88100000L, 49152, FileTransferProtocol.ProtocolVersionV4);
            int safeChunkSize = budgetProvider.ResolveSafeOutboundChunkSize(request);
            int chunkCount = (int)((request.FileSizeBytes + safeChunkSize - 1) / safeChunkSize);
            Envelope envelope = CoreSmokeTestsBase.BuildSecureFileTransferDataFrameEnvelope(helper, new FileTransferChunkBatchFrameV4 { SessionId = sessionId, TransferId = request.TransferId, StartChunkIndex = chunkCount - 1, ChunkCount = 1, DataSegments = new[] { new byte[safeChunkSize] }, BatchProfile = "v4_default_21k" }, 1L);
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
                FileTransferChunkBatchFrameV4 batch = new FileTransferChunkBatchFrameV4
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
                FileTransferChunkBatchFrameV4 receivedBatch = Assert.IsType<FileTransferChunkBatchFrameV4>(frame);
                Assert.Equal(9, receivedBatch.StartChunkIndex);
                Assert.Equal(3, receivedBatch.ChunkCount);
                Assert.Equal(3, receivedBatch.DataSegments.Count);
                Assert.All(receivedBatch.DataSegments, segment => Assert.Equal(21 * 1024, segment.Length));

                string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
                Assert.Contains("event=filetransfer_chunk_batch_sent_as_batch", logTail, StringComparison.Ordinal);
                Assert.Contains("frame_type=filetransfer.chunk_batch.v4", logTail, StringComparison.Ordinal);
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
                FileTransferChunkBatchFrameV4 batch = new FileTransferChunkBatchFrameV4
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
                    FileTransferChunkBatchFrameV4 receivedBatch = Assert.IsType<FileTransferChunkBatchFrameV4>(frame);
                    Assert.Equal(9, receivedBatch.StartChunkIndex);
                    Assert.Equal(3, receivedBatch.ChunkCount);
                    Assert.Equal(3, receivedBatch.DataSegments.Count);
                    Assert.All(receivedBatch.DataSegments, segment => Assert.Equal(21 * 1024, segment.Length));
                }

                string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
                Assert.Contains("event=filetransfer_chunk_batch_sent_as_batch", logTail, StringComparison.Ordinal);
                Assert.Contains("frame_type=filetransfer.chunk_batch.v4", logTail, StringComparison.Ordinal);
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
                FileTransferChunkBatchFrameV4 batch = new FileTransferChunkBatchFrameV4
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
                FileTransferChunkBatchFrameV4 receivedBatch = Assert.IsType<FileTransferChunkBatchFrameV4>(frame);
                Assert.Equal(3, receivedBatch.ChunkCount);
                Assert.Equal(3, receivedBatch.DataSegments.Count);
                Assert.All(receivedBatch.DataSegments, segment => Assert.Equal(21 * 1024, segment.Length));
                Assert.Equal(NknBridgeChannel.Bulk, rawBulkMessage.Channel);

                string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
                Assert.Contains("event=filetransfer_chunk_batch_sent_as_batch", logTail, StringComparison.Ordinal);
                Assert.Contains("frame_type=filetransfer.chunk_batch.v4", logTail, StringComparison.Ordinal);
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
                FileTransferChunkBatchFrameV4 oversizedBatch = new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_batchfallback_v4",
                    StartChunkIndex = 0,
                    ChunkCount = 4,
                    DataSegments = new byte[4][] { new byte[21 * 1024], new byte[21 * 1024], new byte[21 * 1024], new byte[21 * 1024] },
                    BatchProfile = "v4_default_21k"
                };

                await outboundSession.SendAsync(oversizedBatch, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => rawBulkMessages.Count >= 2, TimeSpan.FromSeconds(2.0));

                var frames = rawBulkMessages.Select(message => DecodeNknDataFrame(host, helper, message, sessionId)).ToArray();
                Assert.All(frames, frame => Assert.IsType<FileTransferChunkBatchFrameV4>(frame));
                var batches = frames.Cast<FileTransferChunkBatchFrameV4>().OrderBy(static frame => frame.StartChunkIndex).ToArray();
                Assert.Equal(new[] { 0, 3 }, batches.Select(static frame => frame.StartChunkIndex).ToArray());
                Assert.Equal(4, batches.Sum(static frame => frame.DataSegments.Count));
                string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
                Assert.Contains("event=filetransfer_chunk_batch_split_for_transport", logTail, StringComparison.Ordinal);
                Assert.Contains("original_frame_type=filetransfer.chunk_batch.v4", logTail, StringComparison.Ordinal);
                Assert.Contains("frame_type=filetransfer.chunk_batch.v4", logTail, StringComparison.Ordinal);
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

            var state = new FileTransferStateFrameV4
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
                var receivedState = Assert.IsType<FileTransferStateFrameV4>(DecodeNknDataFrame(helper, host, message, sessionId));
                Assert.Equal(1, receivedState.Epoch);
                Assert.Equal(64, receivedState.CreditUntilChunkIndexExclusive);
            }

            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            Assert.Contains("event=filetransfer_v4_feedback_first_success", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v4_feedback_secondary_completed", logTail, StringComparison.Ordinal);
            Assert.Contains("frame_type=filetransfer.state.v4", logTail, StringComparison.Ordinal);
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
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                },
                cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                },
                cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await helper.SendFileTransferSessionOpenAsync(
                new FileTransferSessionOpenV2
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                    SessionRole = FileTransferProtocol.SessionRoleSender,
                    ChunkSizeBytes = 1024,
                    InitialPipelineDepth = 8,
                },
                cts.Token);
            await sessionOpenReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);

            IFileTransferDataSession inboundSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var pauseState = new FileTransferStateFrameV4
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
            FileTransferStateFrameV4 receivedState = Assert.IsType<FileTransferStateFrameV4>(receivedFrame);
            Assert.True(receivedState.TransferPaused);
            Assert.Equal("ui_pause", receivedState.TransferPauseReason);
            Assert.Contains("event=filetransfer_data_frame_dispatched; transport=nkn; transfer_id=transfer_nkn_v4_sender_pause_state; session_id=", logTail, StringComparison.Ordinal);
            Assert.Contains("frame_type=filetransfer.state.v4; chunk_index=(none); lane=control", logTail, StringComparison.Ordinal);
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
                new FileTransferStateFrameV4
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
            Assert.IsType<FileTransferStateFrameV4>(DecodeNknDataFrame(helper, host, rawControlMessages.Single(), sessionId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V4_FEEDBACK_BULK_REDUNDANCY", previous);
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
            await helper.SendFileTransferOfferAsync(new FileTransferOfferV2 { SessionId = sessionId, TransferId = "transfer_nkn_inbound_chunk_batch", FileName = "inbound-batch.bin", FileSizeBytes = 2048L, PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4 }, cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = sessionId, TransferId = "transfer_nkn_inbound_chunk_batch", AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4 }, cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await helper.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2 { SessionId = sessionId, TransferId = "transfer_nkn_inbound_chunk_batch", ProtocolVersion = FileTransferProtocol.ProtocolVersionV4, SessionRole = FileTransferProtocol.SessionRoleSender, ChunkSizeBytes = 1024, InitialPipelineDepth = 8 }, cts.Token);
            await sessionOpenReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            IFileTransferDataSession inboundSession = await host.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_inbound_chunk_batch", cts.Token);
            FileTransferChunkBatchFrameV4 batchFrame = new FileTransferChunkBatchFrameV4
            {
                SessionId = sessionId,
                TransferId = "transfer_nkn_inbound_chunk_batch",
                StartChunkIndex = 12,
                ChunkCount = 2,
                DataSegments = new byte[2][] { Enumerable.Repeat((byte)17, 1024).ToArray(), Enumerable.Repeat((byte)34, 1024).ToArray() }
            };
            CoreSmokeTestsBase.InvokeNknIncomingMessage(host, helperClient, new NknIncomingMessage(payload: EnvelopeCodec.Serialize(CoreSmokeTestsBase.BuildSecureFileTransferDataFrameEnvelope(helper, batchFrame, CoreSmokeTestsBase.GetNextFileTransferSecureSequence(helper), useBulkSenderIdentity: true)), source: helperClient.ConnectedBulkAddress, isTopic: false, topic: null, channel: NknBridgeChannel.Bulk, bridgeIngressObservedUtcMs: 0L, bridgeMessageObservedUtcMs: 0L, binaryFrameDecodedUtcMs: 0L, socketDataEventEmittedUtcMs: 0L, wsReceiverWriteEnteredUtcMs: 0L, wsMessageEmittedUtcMs: 0L, sdkHandleMsgEnteredUtcMs: 0L, clientMessageDispatchUtcMs: 0L, multiClientMessageDispatchUtcMs: 0L));
            FileTransferDataFrame receivedFrame = await inboundSession.ReceiveAsync(cts.Token);
            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            FileTransferChunkBatchFrameV4 receivedBatch = Assert.IsType<FileTransferChunkBatchFrameV4>(receivedFrame);
            Assert.Equal(batchFrame.TransferId, receivedBatch.TransferId);
            Assert.Equal(batchFrame.SessionId, receivedBatch.SessionId);
            Assert.Equal(batchFrame.StartChunkIndex, receivedBatch.StartChunkIndex);
            Assert.Equal(batchFrame.ChunkCount, receivedBatch.ChunkCount);
            Assert.Equal(batchFrame.DataSegments.Count, receivedBatch.DataSegments.Count);
            Assert.Contains("event=filetransfer_data_frame_dispatched; transport=nkn; transfer_id=transfer_nkn_inbound_chunk_batch; session_id=", logTail, StringComparison.Ordinal);
            Assert.Contains("frame_type=filetransfer.chunk_batch.v4; chunk_index=12-13; lane=bulk", logTail, StringComparison.Ordinal);
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
    public async Task NknTransport_V4DataSessionComplete_ClearsSameDirectionBusyGuard()
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

            await helper.SendFileTransferOfferAsync(
                new FileTransferOfferV2
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_v4_repeat_1",
                    FileName = "first.bin",
                    FileSizeBytes = 1024L,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                },
                cts.Token);
            await firstOfferReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_v4_repeat_1",
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                },
                cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);

            await helper.SendFileTransferSessionOpenAsync(
                new FileTransferSessionOpenV2
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_v4_repeat_1",
                    ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                    SessionRole = FileTransferProtocol.SessionRoleSender,
                    ChunkSizeBytes = 21 * 1024,
                    InitialPipelineDepth = 8,
                },
                cts.Token);
            await sessionOpenReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);

            IFileTransferDataSession hostDataSession = await host.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_v4_repeat_1", cts.Token);
            IFileTransferDataSession helperDataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_v4_repeat_1", cts.Token);
            await hostDataSession.SendAsync(
                new FileTransferCompleteFrameV4
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_v4_repeat_1",
                    FileSizeBytes = 1024L,
                    Sha256Base64 = Convert.ToBase64String(new byte[32]),
                },
                cts.Token);
            FileTransferDataFrame terminalFrame = await helperDataSession.ReceiveAsync(cts.Token);
            Assert.IsType<FileTransferCompleteFrameV4>(terminalFrame);
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
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                },
                cts.Token);
            FileTransferOfferV2 secondOffer = await secondOfferReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            Assert.Equal("transfer_nkn_v4_repeat_2", secondOffer.TransferId);

            var lateBatch = new FileTransferChunkBatchFrameV4
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
            Assert.Contains("event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=unknown_transfer_id", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_message_rejected; message_type=file_transfer_data_frame; reason=transfer_already_terminal", logTail, StringComparison.Ordinal);
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

            await helper.SendFileTransferOfferAsync(new FileTransferOfferV2 { SessionId = sessionId, TransferId = transferId, FileName = "disposed-session.bin", FileSizeBytes = 2048L, PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4 }, cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = sessionId, TransferId = transferId, AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4 }, cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await helper.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2 { SessionId = sessionId, TransferId = transferId, ProtocolVersion = FileTransferProtocol.ProtocolVersionV4, SessionRole = FileTransferProtocol.SessionRoleSender, ChunkSizeBytes = 1024, InitialPipelineDepth = 8 }, cts.Token);
            await sessionOpenReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);

            IFileTransferDataSession staleSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            staleSession.Dispose();

            FileTransferChunkBatchFrameV4 batchFrame = new FileTransferChunkBatchFrameV4
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
            FileTransferChunkBatchFrameV4 receivedBatch = Assert.IsType<FileTransferChunkBatchFrameV4>(receivedFrame);
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
            FileTransferManifestFrameV4 frame = new FileTransferManifestFrameV4
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
            Assert.Equal(0, dataSessions.Count);
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
