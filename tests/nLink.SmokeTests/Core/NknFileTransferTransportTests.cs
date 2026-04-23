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
    public async Task NknTransport_FileTransferDataSession_SplitsOutboundChunkBatchFramesIntoBulkChunkDataFrames_PreservingV3FrameType()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            CoreSmokeTestsBase.GetOperationalLogLength();
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
                    DataSegments = new byte[3][] { new byte[16384], new byte[16384], new byte[16384] }
                };
                await outboundSession.SendAsync(oversizedBatch, cts.Token);
                await CoreSmokeTestsBase.WaitUntilAsync(() => rawBulkMessages.Count >= 3, TimeSpan.FromSeconds(2.0));
                Assert.Equal(3, rawBulkMessages.Count);
                byte[] decryptKey = Assert.IsType<byte[]>(CoreSmokeTestsBase.GetPrivateField(host, "fileTransferSessionSharedKey")).AsSpan().ToArray();
                FileTransferDataFrameV2[] receivedFrames = rawBulkMessages.Select(delegate (NknIncomingMessage message)
                {
                    Assert.True(EnvelopeCodec.TryDeserialize(message.Payload, out Envelope env));
                    SessionSecureEnvelopePayload sessionSecureEnvelopePayload = SessionSecureEnvelopeCodec.Decrypt(decryptKey, env.Payload, new SessionSecureEnvelopeExpectation(SessionSecureMessageFamily.FileTransfer, "file_transfer_data_frame", new SessionId(sessionId), new PeerAddress(helper.LocalPeerAddress)));
                    Assert.True(FileTransferDataFrameCodec.TryDeserialize(sessionSecureEnvelopePayload.Plaintext, out FileTransferDataFrameV2 frame));
                    return frame;
                }).ToArray();
                Assert.Equal(actual: (
                    from frame in receivedFrames.Select((FileTransferDataFrameV2 frame) => Assert.IsType<FileTransferChunkDataFrameV3>(frame)).ToArray()select frame.ChunkIndex into index
                        orderby index
                        select index).ToArray(), expected: new int[3] { 0, 1, 2 });
                Assert.DoesNotContain(receivedFrames, (FileTransferDataFrameV2 frame) => frame is FileTransferChunkDataFrameV2 fileTransferChunkDataFrameV && !(fileTransferChunkDataFrameV is FileTransferChunkDataFrameV3));
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

}
