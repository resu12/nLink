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
    public async Task NknTransport_FileTransfer_RoundTrip_UsesTypedEventsAndSecureLane()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.roundtrip.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.roundtrip.address");
            NknIdentity hostIdentity = new NknIdentity("host-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            TaskCompletionSource<FileTransferOfferV1> offerReceived = new TaskCompletionSource<FileTransferOfferV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferAcceptV1> acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferStartV1> startReceived = new TaskCompletionSource<FileTransferStartV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferChunkV1> chunkReceived = new TaskCompletionSource<FileTransferChunkV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<NknIncomingMessage> rawChunkEnvelopeReceived = new TaskCompletionSource<NknIncomingMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferCompleteV1> completeReceived = new TaskCompletionSource<FileTransferCompleteV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                offerReceived.TrySetResult(e.Message);
            };
            helper.FileTransferAcceptReceived += delegate (object? _, FileTransferAcceptReceivedEventArgs e)
            {
                acceptReceived.TrySetResult(e.Message);
            };
            host.FileTransferStartReceived += delegate (object? _, FileTransferStartReceivedEventArgs e)
            {
                startReceived.TrySetResult(e.Message);
            };
            host.FileTransferChunkReceived += delegate (object? _, FileTransferChunkReceivedEventArgs e)
            {
                chunkReceived.TrySetResult(e.Message);
            };
            helper.FileTransferCompleteReceived += delegate (object? _, FileTransferCompleteReceivedEventArgs e)
            {
                completeReceived.TrySetResult(e.Message);
            };
            hostClient.MessageReceived += delegate (object? _, NknIncomingMessage e)
            {
                if (!e.IsTopic && e.Channel == NknBridgeChannel.Bulk && EnvelopeCodec.TryDeserialize(e.Payload, out Envelope env) && env.Type == MsgType.FileTransferChunk)
                {
                    rawChunkEnvelopeReceived.TrySetResult(e);
                }
            };
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            string expectedHash = Convert.ToBase64String(SHA256.HashData(new byte[3] { 1, 2, 3 }));
            Assert.Equal(hostClient.ConnectedBulkAddress, CoreSmokeTestsBase.GetPrivateField(helper, "remoteBulkEndpoint"));
            Assert.Equal(helperClient.ConnectedBulkAddress, CoreSmokeTestsBase.GetPrivateField(host, "remoteBulkEndpoint"));
            await helper.SendFileTransferOfferAsync(new FileTransferOfferV1 { SessionId = sessionId, TransferId = "transfer_nkn_roundtrip", FileName = "nkn.bin", FileSizeBytes = 3L, Sha256Base64 = expectedHash }, cts.Token);
            Assert.Equal("transfer_nkn_roundtrip", (await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token)).TransferId);
            await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = sessionId, TransferId = "transfer_nkn_roundtrip" }, cts.Token);
            Assert.Equal("transfer_nkn_roundtrip", (await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token)).TransferId);
            await helper.SendFileTransferStartAsync(new FileTransferStartV1 { SessionId = sessionId, TransferId = "transfer_nkn_roundtrip", FileName = "nkn.bin", FileSizeBytes = 3L, Sha256Base64 = expectedHash, ChunkCount = 1, ChunkSizeBytes = 3 }, cts.Token);
            Assert.Equal(1, (await startReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token)).ChunkCount);
            await helper.SendFileTransferChunkAsync(new FileTransferChunkV1 { SessionId = sessionId, TransferId = "transfer_nkn_roundtrip", ChunkIndex = 0, ChunkCount = 1, DataBase64 = Convert.ToBase64String(new byte[3] { 1, 2, 3 }) }, cts.Token);
            NknIncomingMessage rawChunkEnvelope = await rawChunkEnvelopeReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            FileTransferChunkV1 chunk;
            try
            {
                chunk = await chunkReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            }
            catch (TimeoutException ex)
            {
                TimeoutException ex2 = ex;
                throw new TimeoutException("Chunk dispatch timed out after raw bulk delivery. LastError=" + NknRuntimeDiagnostics.Snapshot().LastError, ex2);
            }

            Assert.Equal("transfer_nkn_roundtrip", chunk.TransferId);
            Assert.Equal(0, chunk.ChunkIndex);
            Assert.Equal(helperClient.ConnectedBulkAddress, rawChunkEnvelope.Source);
            Assert.Equal(NknBridgeChannel.Bulk, rawChunkEnvelope.Channel);
            await host.SendFileTransferCompleteAsync(new FileTransferCompleteV1 { SessionId = sessionId, TransferId = "transfer_nkn_roundtrip", FileSizeBytes = 3L, Sha256Base64 = expectedHash }, cts.Token);
            Assert.Equal(expectedHash, (await completeReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token)).Sha256Base64);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferPressureState_UsesSerializedControlDispatchPath()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.pressure.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.pressure.address");
            NknIdentity hostIdentity = new NknIdentity("host-pressure-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-pressure-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            TaskCompletionSource<FileTransferOfferV1> offerReceived = new TaskCompletionSource<FileTransferOfferV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferAcceptV1> acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferStartV1> startReceived = new TaskCompletionSource<FileTransferStartV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferPressureStateV1> pressureReceived = new TaskCompletionSource<FileTransferPressureStateV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<NknIncomingMessage> rawPressureEnvelopeReceived = new TaskCompletionSource<NknIncomingMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                offerReceived.TrySetResult(e.Message);
            };
            helper.FileTransferAcceptReceived += delegate (object? _, FileTransferAcceptReceivedEventArgs e)
            {
                acceptReceived.TrySetResult(e.Message);
            };
            host.FileTransferStartReceived += delegate (object? _, FileTransferStartReceivedEventArgs e)
            {
                startReceived.TrySetResult(e.Message);
            };
            helper.FileTransferPressureStateReceived += delegate (object? _, FileTransferPressureStateReceivedEventArgs e)
            {
                pressureReceived.TrySetResult(e.Message);
            };
            helperClient.MessageReceived += delegate (object? _, NknIncomingMessage e)
            {
                if (!e.IsTopic && e.Channel == NknBridgeChannel.Control && EnvelopeCodec.TryDeserialize(e.Payload, out Envelope env) && env.Type == MsgType.FileTransferPressureState)
                {
                    rawPressureEnvelopeReceived.TrySetResult(e);
                }
            };
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            string expectedHash = Convert.ToBase64String(new byte[32]);
            await helper.SendFileTransferOfferAsync(new FileTransferOfferV1 { SessionId = sessionId, TransferId = "transfer_nkn_pressure", FileName = "pressure.bin", FileSizeBytes = 2L, Sha256Base64 = expectedHash }, cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = sessionId, TransferId = "transfer_nkn_pressure" }, cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await helper.SendFileTransferStartAsync(new FileTransferStartV1 { SessionId = sessionId, TransferId = "transfer_nkn_pressure", FileName = "pressure.bin", FileSizeBytes = 2L, Sha256Base64 = expectedHash, ChunkCount = 1, ChunkSizeBytes = 2 }, cts.Token);
            await startReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferPressureStateAsync(new FileTransferPressureStateV1 { SessionId = sessionId, TransferId = "transfer_nkn_pressure", Revision = 1, Mode = "CatchUpOnly", SuggestedSendAheadChunks = 1, ReceiverNextExpectedChunkIndex = 0, Reason = "BulkBacklog" }, cts.Token);
            FileTransferPressureStateV1 pressure = await pressureReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            NknIncomingMessage rawPressureEnvelope = await rawPressureEnvelopeReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            Assert.Equal("transfer_nkn_pressure", pressure.TransferId);
            Assert.Equal("CatchUpOnly", pressure.Mode);
            Assert.Equal(1, pressure.Revision);
            Assert.Equal(hostClient.ConnectedAddress, rawPressureEnvelope.Source);
            Assert.Equal(NknBridgeChannel.Control, rawPressureEnvelope.Channel);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferChunk_FromControlAddress_IsRejected_WhenBulkAddressIsExpected()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.bulkguard.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.bulkguard.address");
            NknIdentity hostIdentity = new NknIdentity("host-bulkguard-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-bulkguard-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            int chunkCount = 0;
            host.FileTransferChunkReceived += delegate
            {
                Interlocked.Increment(ref chunkCount);
            };
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            string expectedHash = Convert.ToBase64String(SHA256.HashData(new byte[3] { 1, 2, 3 }));
            TaskCompletionSource<FileTransferOfferV1> offerReceived = new TaskCompletionSource<FileTransferOfferV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferStartV1> startReceived = new TaskCompletionSource<FileTransferStartV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                offerReceived.TrySetResult(e.Message);
            };
            host.FileTransferStartReceived += delegate (object? _, FileTransferStartReceivedEventArgs e)
            {
                startReceived.TrySetResult(e.Message);
            };
            await helper.SendFileTransferOfferAsync(new FileTransferOfferV1 { SessionId = sessionId, TransferId = "transfer_nkn_bulkguard", FileName = "nkn.bin", FileSizeBytes = 3L, Sha256Base64 = expectedHash }, cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = sessionId, TransferId = "transfer_nkn_bulkguard" }, cts.Token);
            await helper.SendFileTransferStartAsync(new FileTransferStartV1 { SessionId = sessionId, TransferId = "transfer_nkn_bulkguard", FileName = "nkn.bin", FileSizeBytes = 3L, Sha256Base64 = expectedHash, ChunkCount = 1, ChunkSizeBytes = 3 }, cts.Token);
            await startReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            CoreSmokeTestsBase.InvokeNknIncomingMessage(host, helperClient, new NknIncomingMessage(payload: EnvelopeCodec.Serialize(CoreSmokeTestsBase.BuildSecureFileTransferEnvelope(helper, MsgType.FileTransferChunk, new FileTransferChunkV1 { SessionId = sessionId, TransferId = "transfer_nkn_bulkguard", ChunkIndex = 0, ChunkCount = 1, DataBase64 = Convert.ToBase64String(new byte[3] { 1, 2, 3 }) }, "transfer_nkn_bulkguard", 1L)), source: helperClient.ConnectedAddress, isTopic: false, topic: null, channel: NknBridgeChannel.Control, bridgeIngressObservedUtcMs: 0L, bridgeMessageObservedUtcMs: 0L, binaryFrameDecodedUtcMs: 0L, socketDataEventEmittedUtcMs: 0L, wsReceiverWriteEnteredUtcMs: 0L, wsMessageEmittedUtcMs: 0L, sdkHandleMsgEnteredUtcMs: 0L, clientMessageDispatchUtcMs: 0L, multiClientMessageDispatchUtcMs: 0L));
            await Task.Delay(150, cts.Token);
            Assert.Equal(0, Volatile.Read(in chunkCount));
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
            FileTransferChunkBudgetRequest request = new FileTransferChunkBudgetRequest("transfer_nkn_payload_budget", 88100000L, 49152, 2);
            int safeChunkSize = budgetProvider.ResolveSafeOutboundChunkSize(request);
            int chunkCount = (int)((request.FileSizeBytes + safeChunkSize - 1) / safeChunkSize);
            Envelope envelope = CoreSmokeTestsBase.BuildSecureFileTransferDataFrameEnvelope(helper, new FileTransferChunkDataFrameV2 { SessionId = sessionId, TransferId = request.TransferId, ChunkIndex = chunkCount - 1, ChunkCount = chunkCount, Data = new byte[safeChunkSize] }, 1L);
            Assert.InRange(safeChunkSize, 1, 49152);
            Assert.InRange(EnvelopeCodec.Serialize(envelope).Length, 1, 65536);
            FileTransferChunkBudgetRequest v3Request = request with
            {
                TransferId = "transfer_nkn_payload_budget_v3",
                RequestedChunkSizeBytes = 40960,
                NegotiatedDataProtocolVersion = 3
            };
            int safeV3ChunkSize = budgetProvider.ResolveSafeOutboundChunkSize(v3Request);
            int v3ChunkCount = (int)((v3Request.FileSizeBytes + safeV3ChunkSize - 1) / safeV3ChunkSize);
            Envelope v3Envelope = CoreSmokeTestsBase.BuildSecureFileTransferDataFrameEnvelope(helper, new FileTransferChunkDataFrameV3 { SessionId = sessionId, TransferId = v3Request.TransferId, ChunkIndex = v3ChunkCount - 1, ChunkCount = v3ChunkCount, Data = new byte[safeV3ChunkSize] }, 2L);
            Assert.InRange(safeV3ChunkSize, 40960, 49152);
            Assert.InRange(EnvelopeCodec.Serialize(v3Envelope).Length, 1, 65536);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_SplitsOutboundChunkBatchFramesIntoBulkChunkDataFrames()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.batchlimit.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.batchlimit.address");
            NknIdentity hostIdentity = new NknIdentity("host-batchlimit-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-batchlimit-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            try
            {
                string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
                IFileTransferDataSession outboundSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_batchlimit", cts.Token);
                ConcurrentQueue<NknIncomingMessage> rawBulkMessages = new ConcurrentQueue<NknIncomingMessage>();
                hostClient.MessageReceived += delegate (object? _, NknIncomingMessage e)
                {
                    if (!e.IsTopic && e.Channel == NknBridgeChannel.Bulk && EnvelopeCodec.TryDeserialize(e.Payload, out Envelope env) && env.Type == MsgType.FileTransferDataFrame)
                    {
                        rawBulkMessages.Enqueue(e);
                    }
                };
                FileTransferChunkBatchFrameV2 oversizedBatch = new FileTransferChunkBatchFrameV2
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_batchlimit",
                    StartChunkIndex = 0,
                    ChunkCount = 3,
                    DataSegments = new byte[3][] { new byte[16384], new byte[16384], new byte[16384] }
                };
                await outboundSession.SendAsync(oversizedBatch, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => rawBulkMessages.Count >= 3, TimeSpan.FromSeconds(2.0));
                string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
                Assert.Equal(3, rawBulkMessages.Count);
                Assert.Contains("original_frame_type=filetransfer.chunk_batch.v2", logTail, StringComparison.Ordinal);
                Assert.Contains("split_chunk_range=0-2", logTail, StringComparison.Ordinal);
                Assert.Contains("chunk_frame_count=3", logTail, StringComparison.Ordinal);
                Assert.Contains("reason=legacy_v2", logTail, StringComparison.Ordinal);
                Assert.Contains("event=filetransfer_transport_payload_budget", logTail, StringComparison.Ordinal);
                Assert.Contains("frame_type=filetransfer.chunk_data.v2", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("event=filetransfer_transport_payload_budget; transport=nkn; transfer_id=transfer_nkn_batchlimit; message_type=file_transfer_data_frame; frame_type=filetransfer.chunk_batch.v2", logTail, StringComparison.Ordinal);
                Assert.Contains("max_allowed_bytes=65536", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("event=filetransfer_transport_payload_rejected", logTail, StringComparison.Ordinal);
                byte[] decryptKey = Assert.IsType<byte[]>(CoreSmokeTestsBase.GetPrivateField(host, "fileTransferSessionSharedKey")).AsSpan().ToArray();
                FileTransferDataFrameV2[] receivedFrames = rawBulkMessages.Select(delegate (NknIncomingMessage message)
                {
                    Assert.True(EnvelopeCodec.TryDeserialize(message.Payload, out Envelope env));
                    SessionSecureEnvelopePayload sessionSecureEnvelopePayload = SessionSecureEnvelopeCodec.Decrypt(decryptKey, env.Payload, new SessionSecureEnvelopeExpectation(SessionSecureMessageFamily.FileTransfer, "file_transfer_data_frame", new SessionId(sessionId), new PeerAddress(helper.LocalPeerAddress)));
                    Assert.True(FileTransferDataFrameCodec.TryDeserialize(sessionSecureEnvelopePayload.Plaintext, out FileTransferDataFrameV2 frame));
                    return frame;
                }).ToArray();
                Assert.Equal(actual: (
                    from frame in receivedFrames.Select((FileTransferDataFrameV2 frame) => Assert.IsType<FileTransferChunkDataFrameV2>(frame)).ToArray()select frame.ChunkIndex into index
                        orderby index
                        select index).ToArray(), expected: new int[3] { 0, 1, 2 });
                Assert.All(rawBulkMessages, delegate (NknIncomingMessage message)
                {
                    Assert.Equal(NknBridgeChannel.Bulk, message.Channel);
                });
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

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_SendsOutboundV3ChunkBatchFrameAsSingleBulkFrame_WhenWithinBudget()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.batchlimit.v3.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.batchlimit.v3.address");
            NknIdentity hostIdentity = new NknIdentity("host-batchlimit-v3-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-batchlimit-v3-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            try
            {
                string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
                IFileTransferDataSession outboundSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_batchlimit_v3", cts.Token);
                ConcurrentQueue<NknIncomingMessage> rawBulkMessages = new ConcurrentQueue<NknIncomingMessage>();
                hostClient.MessageReceived += delegate (object? _, NknIncomingMessage e)
                {
                    if (!e.IsTopic && e.Channel == NknBridgeChannel.Bulk && EnvelopeCodec.TryDeserialize(e.Payload, out Envelope env) && env.Type == MsgType.FileTransferDataFrame)
                    {
                        rawBulkMessages.Enqueue(e);
                    }
                };
                FileTransferChunkBatchFrameV3 oversizedBatch = new FileTransferChunkBatchFrameV3
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_batchlimit_v3",
                    StartChunkIndex = 0,
                    ChunkCount = 3,
                    DataSegments = new byte[3][] { new byte[21 * 1024], new byte[21 * 1024], new byte[21 * 1024] }
                };
                await outboundSession.SendAsync(oversizedBatch, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => rawBulkMessages.Count >= 1, TimeSpan.FromSeconds(2.0));
                Assert.Equal(1, rawBulkMessages.Count);
                string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
                Assert.Contains("event=filetransfer_chunk_batch_sent_as_batch", logTail, StringComparison.Ordinal);
                Assert.Contains("transfer_id=transfer_nkn_batchlimit_v3", logTail, StringComparison.Ordinal);
                Assert.Contains("chunk_range=0-2", logTail, StringComparison.Ordinal);
                Assert.Contains("chunk_frame_count=3", logTail, StringComparison.Ordinal);
                Assert.Contains("raw_bytes=64512", logTail, StringComparison.Ordinal);
                Assert.Contains("batch_chunk_count=3", logTail, StringComparison.Ordinal);
                Assert.Contains("bridge_payload_fill_percent=", logTail, StringComparison.Ordinal);
                Assert.Contains("event=filetransfer_transport_payload_budget", logTail, StringComparison.Ordinal);
                Assert.Contains("frame_type=filetransfer.chunk_batch.v3", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("event=filetransfer_chunk_batch_split_for_transport; transport=nkn; transfer_id=transfer_nkn_batchlimit_v3", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("event=filetransfer_transport_payload_rejected", logTail, StringComparison.Ordinal);
                byte[] decryptKey = Assert.IsType<byte[]>(CoreSmokeTestsBase.GetPrivateField(host, "fileTransferSessionSharedKey")).AsSpan().ToArray();
                NknIncomingMessage rawBulkMessage = Assert.Single(rawBulkMessages);
                Assert.True(EnvelopeCodec.TryDeserialize(rawBulkMessage.Payload, out Envelope env));
                SessionSecureEnvelopePayload sessionSecureEnvelopePayload = SessionSecureEnvelopeCodec.Decrypt(decryptKey, env.Payload, new SessionSecureEnvelopeExpectation(SessionSecureMessageFamily.FileTransfer, "file_transfer_data_frame", new SessionId(sessionId), new PeerAddress(helper.LocalPeerAddress)));
                Assert.True(FileTransferDataFrameCodec.TryDeserialize(sessionSecureEnvelopePayload.Plaintext, out FileTransferDataFrameV2 frame));
                FileTransferChunkBatchFrameV3 receivedBatch = Assert.IsType<FileTransferChunkBatchFrameV3>(frame);
                Assert.Equal(oversizedBatch.TransferId, receivedBatch.TransferId);
                Assert.Equal(oversizedBatch.SessionId, receivedBatch.SessionId);
                Assert.Equal(0, receivedBatch.StartChunkIndex);
                Assert.Equal(3, receivedBatch.ChunkCount);
                Assert.Equal(3, receivedBatch.DataSegments.Count);
                Assert.All(receivedBatch.DataSegments, segment => Assert.Equal(21 * 1024, segment.Length));
                Assert.All(rawBulkMessages, delegate (NknIncomingMessage message)
                {
                    Assert.Equal(NknBridgeChannel.Bulk, message.Channel);
                });
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

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_SendsV3ReceiverControlOnControlAndBulkLanes()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.v3-control-redundancy.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.v3-control-redundancy.address");
            NknIdentity hostIdentity = new NknIdentity("host-v3-control-redundancy-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-v3-control-redundancy-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);

            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            IFileTransferDataSession receiverControlSession = await host.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_v3_control_redundancy", cts.Token);
            ConcurrentQueue<NknIncomingMessage> rawControlMessages = new ConcurrentQueue<NknIncomingMessage>();
            ConcurrentQueue<NknIncomingMessage> rawBulkMessages = new ConcurrentQueue<NknIncomingMessage>();
            helperClient.MessageReceived += delegate (object? _, NknIncomingMessage e)
            {
                if (e.IsTopic ||
                    !EnvelopeCodec.TryDeserialize(e.Payload, out Envelope env) ||
                    env.Type != MsgType.FileTransferDataFrame)
                {
                    return;
                }

                if (e.Channel == NknBridgeChannel.Control)
                {
                    rawControlMessages.Enqueue(e);
                }
                else if (e.Channel == NknBridgeChannel.Bulk)
                {
                    rawBulkMessages.Enqueue(e);
                }
            };

            var grant = new FileTransferGrantWindowFrameV3
            {
                SessionId = sessionId,
                TransferId = "transfer_nkn_v3_control_redundancy",
                NextExpectedChunkIndex = 25,
                GrantedUntilChunkIndexExclusive = 64,
                BytesCommitted = 512000
            };

            await receiverControlSession.SendAsync(grant, cts.Token);
            await CoreSmokeTestsBase.WaitUntilAsync(
                () => rawControlMessages.Count >= 1 && rawBulkMessages.Count >= 1,
                TimeSpan.FromSeconds(2.0));

            Assert.Single(rawControlMessages);
            Assert.Single(rawBulkMessages);
            Assert.Equal(NknBridgeChannel.Control, rawControlMessages.Single().Channel);
            Assert.Equal(NknBridgeChannel.Bulk, rawBulkMessages.Single().Channel);
            Assert.Equal(hostClient.ConnectedAddress, rawControlMessages.Single().Source);
            Assert.Equal(hostClient.ConnectedBulkAddress, rawBulkMessages.Single().Source);

            byte[] decryptKey = Assert.IsType<byte[]>(CoreSmokeTestsBase.GetPrivateField(helper, "fileTransferSessionSharedKey")).AsSpan().ToArray();
            foreach (var message in rawControlMessages.Concat(rawBulkMessages))
            {
                Assert.True(EnvelopeCodec.TryDeserialize(message.Payload, out Envelope env));
                SessionSecureEnvelopePayload payload = SessionSecureEnvelopeCodec.Decrypt(
                    decryptKey,
                    env.Payload,
                    new SessionSecureEnvelopeExpectation(
                        SessionSecureMessageFamily.FileTransfer,
                        "file_transfer_data_frame",
                        new SessionId(sessionId),
                        new PeerAddress(host.LocalPeerAddress)));
                Assert.True(FileTransferDataFrameCodec.TryDeserialize(payload.Plaintext, out FileTransferDataFrameV2? frame));
                FileTransferGrantWindowFrameV3 receivedGrant = Assert.IsType<FileTransferGrantWindowFrameV3>(frame);
                Assert.Equal(25, receivedGrant.NextExpectedChunkIndex);
                Assert.Equal(64, receivedGrant.GrantedUntilChunkIndexExclusive);
            }

            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            Assert.Contains("event=filetransfer_v3_control_redundant_bulk_sent", logTail, StringComparison.Ordinal);
            Assert.Contains("frame_type=filetransfer.grant_window.v3", logTail, StringComparison.Ordinal);
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

                FileTransferDataFrameV2 frame = DecodeNknDataFrame(host, helper, rawBulkMessages.Single(), sessionId);
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
                    FileTransferDataFrameV2 frame = DecodeNknDataFrame(host, helper, message, sessionId);
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
                FileTransferDataFrameV2 frame = DecodeNknDataFrame(host, helper, rawBulkMessage, sessionId);
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
                Assert.DoesNotContain(frames, static frame => frame is FileTransferChunkDataFrameV2 or FileTransferChunkDataFrameV3);

                string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
                Assert.Contains("event=filetransfer_chunk_batch_split_for_transport", logTail, StringComparison.Ordinal);
                Assert.Contains("original_frame_type=filetransfer.chunk_batch.v4", logTail, StringComparison.Ordinal);
                Assert.Contains("frame_type=filetransfer.chunk_batch.v4", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("frame_type=filetransfer.chunk_data.v2", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("frame_type=filetransfer.chunk_data.v3", logTail, StringComparison.Ordinal);
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

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferDataSession_SplitsOutboundV3ChunkBatchFrame_WhenWrappedPayloadExceedsBudget()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.batchfallback.v3.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.batchfallback.v3.address");
            NknIdentity hostIdentity = new NknIdentity("host-batchfallback-v3-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-batchfallback-v3-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            try
            {
                string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
                IFileTransferDataSession outboundSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_batchfallback_v3", cts.Token);
                ConcurrentQueue<NknIncomingMessage> rawBulkMessages = new ConcurrentQueue<NknIncomingMessage>();
                hostClient.MessageReceived += delegate (object? _, NknIncomingMessage e)
                {
                    if (!e.IsTopic && e.Channel == NknBridgeChannel.Bulk && EnvelopeCodec.TryDeserialize(e.Payload, out Envelope env) && env.Type == MsgType.FileTransferDataFrame)
                    {
                        rawBulkMessages.Enqueue(e);
                    }
                };
                FileTransferChunkBatchFrameV3 nearLimitBatch = new FileTransferChunkBatchFrameV3
                {
                    SessionId = sessionId,
                    TransferId = "transfer_nkn_batchfallback_v3",
                    StartChunkIndex = 0,
                    ChunkCount = 3,
                    DataSegments = new byte[3][] { new byte[22000], new byte[22000], new byte[21300] }
                };

                await outboundSession.SendAsync(nearLimitBatch, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => rawBulkMessages.Count >= 3, TimeSpan.FromSeconds(2.0));

                Assert.Equal(3, rawBulkMessages.Count);
                string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
                Assert.Contains("event=filetransfer_chunk_batch_split_for_transport", logTail, StringComparison.Ordinal);
                Assert.Contains("transfer_id=transfer_nkn_batchfallback_v3", logTail, StringComparison.Ordinal);
                Assert.Contains("reason=payload_budget_fallback", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("event=filetransfer_transport_payload_rejected", logTail, StringComparison.Ordinal);

                byte[] decryptKey = Assert.IsType<byte[]>(CoreSmokeTestsBase.GetPrivateField(host, "fileTransferSessionSharedKey")).AsSpan().ToArray();
                FileTransferDataFrameV2[] frames = rawBulkMessages.Select(message =>
                {
                    Assert.True(EnvelopeCodec.TryDeserialize(message.Payload, out Envelope env));
                    SessionSecureEnvelopePayload sessionSecureEnvelopePayload = SessionSecureEnvelopeCodec.Decrypt(decryptKey, env.Payload, new SessionSecureEnvelopeExpectation(SessionSecureMessageFamily.FileTransfer, "file_transfer_data_frame", new SessionId(sessionId), new PeerAddress(helper.LocalPeerAddress)));
                    Assert.True(FileTransferDataFrameCodec.TryDeserialize(sessionSecureEnvelopePayload.Plaintext, out FileTransferDataFrameV2 frame));
                    return frame;
                }).ToArray();
                Assert.All(frames, frame => Assert.IsType<FileTransferChunkDataFrameV3>(frame));
                Assert.Equal(new[] { 0, 1, 2 }, frames.Cast<FileTransferChunkDataFrameV3>().Select(static frame => frame.ChunkIndex).OrderBy(static index => index).ToArray());
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

    [Trait("Category", "LegacySmoke")]
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
            TaskCompletionSource<FileTransferOfferV1> offerReceived = new TaskCompletionSource<FileTransferOfferV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferAcceptV1> acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferStartV1> startReceived = new TaskCompletionSource<FileTransferStartV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                offerReceived.TrySetResult(e.Message);
            };
            helper.FileTransferAcceptReceived += delegate (object? _, FileTransferAcceptReceivedEventArgs e)
            {
                acceptReceived.TrySetResult(e.Message);
            };
            host.FileTransferStartReceived += delegate (object? _, FileTransferStartReceivedEventArgs e)
            {
                startReceived.TrySetResult(e.Message);
            };
            await helper.SendFileTransferOfferAsync(new FileTransferOfferV1 { SessionId = sessionId, TransferId = "transfer_nkn_inbound_chunk_batch", FileName = "inbound-batch.bin", FileSizeBytes = 2048L, Sha256Base64 = Convert.ToBase64String(new byte[32]) }, cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = sessionId, TransferId = "transfer_nkn_inbound_chunk_batch" }, cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await helper.SendFileTransferStartAsync(new FileTransferStartV1 { SessionId = sessionId, TransferId = "transfer_nkn_inbound_chunk_batch", FileName = "inbound-batch.bin", FileSizeBytes = 2048L, Sha256Base64 = Convert.ToBase64String(new byte[32]), ChunkCount = 20, ChunkSizeBytes = 1024 }, cts.Token);
            await startReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            IFileTransferDataSession inboundSession = await host.OpenFileTransferDataSessionAsync(sessionId, "transfer_nkn_inbound_chunk_batch", cts.Token);
            FileTransferChunkBatchFrameV2 batchFrame = new FileTransferChunkBatchFrameV2
            {
                SessionId = sessionId,
                TransferId = "transfer_nkn_inbound_chunk_batch",
                StartChunkIndex = 12,
                ChunkCount = 20,
                DataSegments = new byte[2][] { Enumerable.Repeat((byte)17, 1024).ToArray(), Enumerable.Repeat((byte)34, 1024).ToArray() }
            };
            CoreSmokeTestsBase.InvokeNknIncomingMessage(host, helperClient, new NknIncomingMessage(payload: EnvelopeCodec.Serialize(CoreSmokeTestsBase.BuildSecureFileTransferDataFrameEnvelope(helper, batchFrame, CoreSmokeTestsBase.GetNextFileTransferSecureSequence(helper))), source: helperClient.ConnectedAddress, isTopic: false, topic: null, channel: NknBridgeChannel.Bulk, bridgeIngressObservedUtcMs: 0L, bridgeMessageObservedUtcMs: 0L, binaryFrameDecodedUtcMs: 0L, socketDataEventEmittedUtcMs: 0L, wsReceiverWriteEnteredUtcMs: 0L, wsMessageEmittedUtcMs: 0L, sdkHandleMsgEnteredUtcMs: 0L, clientMessageDispatchUtcMs: 0L, multiClientMessageDispatchUtcMs: 0L));
            FileTransferDataFrameV2 receivedFrame = await inboundSession.ReceiveAsync(cts.Token);
            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            FileTransferChunkBatchFrameV2 receivedBatch = Assert.IsType<FileTransferChunkBatchFrameV2>(receivedFrame);
            Assert.Equal(batchFrame.TransferId, receivedBatch.TransferId);
            Assert.Equal(batchFrame.SessionId, receivedBatch.SessionId);
            Assert.Equal(batchFrame.StartChunkIndex, receivedBatch.StartChunkIndex);
            Assert.Equal(batchFrame.ChunkCount, receivedBatch.ChunkCount);
            Assert.Equal(batchFrame.DataSegments.Count, receivedBatch.DataSegments.Count);
            Assert.Contains("event=filetransfer_data_frame_dispatched; transport=nkn; transfer_id=transfer_nkn_inbound_chunk_batch; session_id=", logTail, StringComparison.Ordinal);
            Assert.Contains("frame_type=filetransfer.chunk_batch.v2; chunk_index=12-13; lane=bulk", logTail, StringComparison.Ordinal);
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

            TaskCompletionSource<FileTransferOfferV1> firstOfferReceived = new TaskCompletionSource<FileTransferOfferV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferOfferV1> secondOfferReceived = new TaskCompletionSource<FileTransferOfferV1>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            FileTransferDataFrameV2 terminalFrame = await helperDataSession.ReceiveAsync(cts.Token);
            Assert.IsType<FileTransferCompleteFrameV4>(terminalFrame);

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
            FileTransferOfferV1 secondOffer = await secondOfferReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
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
                    payload: EnvelopeCodec.Serialize(CoreSmokeTestsBase.BuildSecureFileTransferDataFrameEnvelope(helper, lateBatch, CoreSmokeTestsBase.GetNextFileTransferSecureSequence(helper))),
                    source: helperClient.ConnectedAddress,
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
            Assert.Contains("reason=transfer_already_terminal", logTail, StringComparison.Ordinal);
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
            TaskCompletionSource<FileTransferOfferV1> offerReceived = new TaskCompletionSource<FileTransferOfferV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferAcceptV1> acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferStartV1> startReceived = new TaskCompletionSource<FileTransferStartV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                offerReceived.TrySetResult(e.Message);
            };
            helper.FileTransferAcceptReceived += delegate (object? _, FileTransferAcceptReceivedEventArgs e)
            {
                acceptReceived.TrySetResult(e.Message);
            };
            host.FileTransferStartReceived += delegate (object? _, FileTransferStartReceivedEventArgs e)
            {
                startReceived.TrySetResult(e.Message);
            };

            await helper.SendFileTransferOfferAsync(new FileTransferOfferV1 { SessionId = sessionId, TransferId = transferId, FileName = "disposed-session.bin", FileSizeBytes = 2048L, Sha256Base64 = Convert.ToBase64String(new byte[32]) }, cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = sessionId, TransferId = transferId }, cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await helper.SendFileTransferStartAsync(new FileTransferStartV1 { SessionId = sessionId, TransferId = transferId, FileName = "disposed-session.bin", FileSizeBytes = 2048L, Sha256Base64 = Convert.ToBase64String(new byte[32]), ChunkCount = 20, ChunkSizeBytes = 1024 }, cts.Token);
            await startReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);

            IFileTransferDataSession staleSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            staleSession.Dispose();

            FileTransferChunkBatchFrameV2 batchFrame = new FileTransferChunkBatchFrameV2
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 4,
                ChunkCount = 20,
                DataSegments = new byte[2][] { Enumerable.Repeat((byte)17, 1024).ToArray(), Enumerable.Repeat((byte)34, 1024).ToArray() }
            };
            CoreSmokeTestsBase.InvokeNknIncomingMessage(host, helperClient, new NknIncomingMessage(payload: EnvelopeCodec.Serialize(CoreSmokeTestsBase.BuildSecureFileTransferDataFrameEnvelope(helper, batchFrame, CoreSmokeTestsBase.GetNextFileTransferSecureSequence(helper))), source: helperClient.ConnectedAddress, isTopic: false, topic: null, channel: NknBridgeChannel.Bulk, bridgeIngressObservedUtcMs: 0L, bridgeMessageObservedUtcMs: 0L, binaryFrameDecodedUtcMs: 0L, socketDataEventEmittedUtcMs: 0L, wsReceiverWriteEnteredUtcMs: 0L, wsMessageEmittedUtcMs: 0L, sdkHandleMsgEnteredUtcMs: 0L, clientMessageDispatchUtcMs: 0L, multiClientMessageDispatchUtcMs: 0L));

            IFileTransferDataSession replacementSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            FileTransferDataFrameV2 receivedFrame = await replacementSession.ReceiveAsync(cts.Token);
            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            FileTransferChunkBatchFrameV2 receivedBatch = Assert.IsType<FileTransferChunkBatchFrameV2>(receivedFrame);
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
            FileTransferManifestFrameV2 frame = new FileTransferManifestFrameV2
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

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferChunks_UseDedicatedDispatchPath_WhenInboundCallbacksOverlap()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.concurrent.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.concurrent.address");
            NknIdentity hostIdentity = new NknIdentity("host-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            try
            {
                using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
                string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
                string expectedHash = Convert.ToBase64String(SHA256.HashData(new byte[4] { 1, 2, 3, 4 }));
                await helper.SendFileTransferOfferAsync(new FileTransferOfferV1 { SessionId = sessionId, TransferId = "transfer_nkn_concurrent_dispatch", FileName = "concurrent.bin", FileSizeBytes = 4L, Sha256Base64 = expectedHash }, cts.Token);
                await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = sessionId, TransferId = "transfer_nkn_concurrent_dispatch" }, cts.Token);
                TaskCompletionSource<bool> startReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> firstChunkEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> secondChunkEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> releaseFirstChunk = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                int activeChunkHandlers = 0;
                int chunkHandlerOverlapDetected = 0;
                ConcurrentQueue<int> receivedChunkIndexes = new ConcurrentQueue<int>();
                host.FileTransferStartReceived += delegate
                {
                    startReceived.TrySetResult(result: true);
                };
                host.FileTransferChunkReceived += delegate (object? _, FileTransferChunkReceivedEventArgs e)
                {
                    if (Interlocked.Increment(ref activeChunkHandlers) > 1)
                    {
                        Interlocked.Exchange(ref chunkHandlerOverlapDetected, 1);
                    }

                    try
                    {
                        receivedChunkIndexes.Enqueue(e.Message.ChunkIndex);
                        if (e.Message.ChunkIndex == 0)
                        {
                            firstChunkEntered.TrySetResult(result: true);
                            releaseFirstChunk.Task.GetAwaiter().GetResult();
                        }
                        else if (e.Message.ChunkIndex == 1)
                        {
                            secondChunkEntered.TrySetResult(result: true);
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeChunkHandlers);
                    }
                };
                Envelope startEnvelope = CoreSmokeTestsBase.BuildSecureFileTransferEnvelope(helper, MsgType.FileTransferStart, new FileTransferStartV1 { SessionId = sessionId, TransferId = "transfer_nkn_concurrent_dispatch", FileName = "concurrent.bin", FileSizeBytes = 4L, Sha256Base64 = expectedHash, ChunkCount = 2, ChunkSizeBytes = 2 }, "transfer_nkn_concurrent_dispatch", 2L);
                CoreSmokeTestsBase.InvokeNknIncomingMessage(host, helperClient, new NknIncomingMessage(helperClient.Address, EnvelopeCodec.Serialize(startEnvelope), isTopic: false, null, NknBridgeChannel.Control, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L));
                await startReceived.Task.WaitAsync(TimeSpan.FromSeconds(2.0), cts.Token);
                Envelope chunkZeroEnvelope = CoreSmokeTestsBase.BuildSecureFileTransferEnvelope(helper, MsgType.FileTransferChunk, new FileTransferChunkV1 { SessionId = sessionId, TransferId = "transfer_nkn_concurrent_dispatch", ChunkIndex = 0, ChunkCount = 2, DataBase64 = Convert.ToBase64String(new byte[2] { 1, 2 }) }, "transfer_nkn_concurrent_dispatch", 3L);
                Envelope chunkOneEnvelope = CoreSmokeTestsBase.BuildSecureFileTransferEnvelope(helper, MsgType.FileTransferChunk, new FileTransferChunkV1 { SessionId = sessionId, TransferId = "transfer_nkn_concurrent_dispatch", ChunkIndex = 1, ChunkCount = 2, DataBase64 = Convert.ToBase64String(new byte[2] { 3, 4 }) }, "transfer_nkn_concurrent_dispatch", 4L);
                Task chunkZeroTask = Task.Run(delegate
                {
                    CoreSmokeTestsBase.InvokeNknIncomingMessage(host, helperClient, new NknIncomingMessage(helperClient.ConnectedBulkAddress, EnvelopeCodec.Serialize(chunkZeroEnvelope), isTopic: false, null, NknBridgeChannel.Bulk, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L));
                }, cts.Token);
                await firstChunkEntered.Task.WaitAsync(TimeSpan.FromSeconds(2.0), cts.Token);
                Task chunkOneTask = Task.Run(delegate
                {
                    CoreSmokeTestsBase.InvokeNknIncomingMessage(host, helperClient, new NknIncomingMessage(helperClient.ConnectedBulkAddress, EnvelopeCodec.Serialize(chunkOneEnvelope), isTopic: false, null, NknBridgeChannel.Bulk, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L));
                }, cts.Token);
                await Task.Delay(150, cts.Token);
                Assert.True(secondChunkEntered.Task.IsCompleted);
                releaseFirstChunk.TrySetResult(result: true);
                await Task.WhenAll(chunkZeroTask, chunkOneTask);
                await secondChunkEntered.Task.WaitAsync(TimeSpan.FromSeconds(2.0), cts.Token);
                Assert.Equal(1, Volatile.Read(in chunkHandlerOverlapDetected));
                Assert.Equal(new int[2] { 0, 1 }, receivedChunkIndexes.ToArray());
                string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
                Assert.Contains("event=filetransfer_chunk_ingress", logTail, StringComparison.Ordinal);
                Assert.Contains("event=filetransfer_chunk_validated", logTail, StringComparison.Ordinal);
                Assert.Contains("event=filetransfer_chunk_dispatched", logTail, StringComparison.Ordinal);
            }
            finally
            {
                if (host != null)
                {
                    ((IDisposable)host).Dispose();
                }
            }
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    private static FileTransferDataFrameV2 DecodeNknDataFrame(
        NknSignalingTransport recipient,
        NknSignalingTransport sender,
        NknIncomingMessage message,
        string sessionId)
    {
        Assert.True(EnvelopeCodec.TryDeserialize(message.Payload, out Envelope env));
        byte[] decryptKey = Assert.IsType<byte[]>(CoreSmokeTestsBase.GetPrivateField(recipient, "fileTransferSessionSharedKey")).AsSpan().ToArray();
        SessionSecureEnvelopePayload payload = SessionSecureEnvelopeCodec.Decrypt(
            decryptKey,
            env.Payload,
            new SessionSecureEnvelopeExpectation(
                SessionSecureMessageFamily.FileTransfer,
                "file_transfer_data_frame",
                new SessionId(sessionId),
                new PeerAddress(sender.LocalPeerAddress)));
        Assert.True(FileTransferDataFrameCodec.TryDeserialize(payload.Plaintext, out FileTransferDataFrameV2? frame));
        return Assert.IsAssignableFrom<FileTransferDataFrameV2>(frame);
    }

}
