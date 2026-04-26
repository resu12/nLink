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
public sealed class NknFileTransferSecurityBoundaryTests : CoreSmokeTestsBase
{
    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferOffer_WithSessionIdMismatch_IsRejected()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.sessionmismatch.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.sessionmismatch.address");
            NknIdentity hostIdentity = new NknIdentity("host-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            int offerCount = 0;
            host.FileTransferOfferReceived += delegate
            {
                Interlocked.Increment(ref offerCount);
            };
            await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason(null);
            await helperClient.SendAsync(payload: EnvelopeCodec.Serialize(CoreSmokeTestsBase.BuildSecureFileTransferEnvelope(helper, MsgType.FileTransferOffer, new FileTransferOfferV1 { SessionId = "sess_filetransfer_wrong", TransferId = "transfer_nkn_wrong_session", FileName = "wrong.bin", FileSizeBytes = 1L, Sha256Base64 = Convert.ToBase64String(new byte[32]) }, "transfer_nkn_wrong_session", 1L)), destination: host.LocalPeerAddress, ct: cts.Token);
            await Task.Delay(300, cts.Token);
            Assert.Equal(0, Volatile.Read(in offerCount));
            Assert.Equal("file_transfer_offer_session_id_mismatch", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferOffer_WithSourceIdentityMismatch_IsRejected()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.source.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.source.address");
            FakeNknClient attackerClient = new FakeNknClient("attacker.filetransfer.source.address");
            NknIdentity hostIdentity = new NknIdentity("host-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            int offerCount = 0;
            host.FileTransferOfferReceived += delegate
            {
                Interlocked.Increment(ref offerCount);
            };
            await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            await attackerClient.ConnectAsync(cts.Token);
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason(null);
            await attackerClient.SendAsync(payload: EnvelopeCodec.Serialize(CoreSmokeTestsBase.BuildSecureFileTransferEnvelope(helper, MsgType.FileTransferOffer, new FileTransferOfferV1 { SessionId = host.CurrentSessionSecurityState.SessionId.Value.Value, TransferId = "transfer_nkn_wrong_source", FileName = "source.bin", FileSizeBytes = 1L, Sha256Base64 = Convert.ToBase64String(new byte[32]) }, "transfer_nkn_wrong_source", 1L)), destination: host.LocalPeerAddress, ct: cts.Token);
            await Task.Delay(300, cts.Token);
            Assert.Equal(0, Volatile.Read(in offerCount));
            Assert.Equal("file_transfer_offer_source_identity_mismatch", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferOffer_WithTamperedSecureEnvelope_IsRejected()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.tamper.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.tamper.address");
            NknIdentity hostIdentity = new NknIdentity("host-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            int offerCount = 0;
            host.FileTransferOfferReceived += delegate
            {
                Interlocked.Increment(ref offerCount);
            };
            await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            Envelope envelope = CoreSmokeTestsBase.BuildSecureFileTransferEnvelope(helper, MsgType.FileTransferOffer, new FileTransferOfferV1 { SessionId = host.CurrentSessionSecurityState.SessionId.Value.Value, TransferId = "transfer_nkn_tamper", FileName = "tamper.bin", FileSizeBytes = 1L, Sha256Base64 = Convert.ToBase64String(new byte[32]) }, "transfer_nkn_tamper", 1L);
            byte[] tamperedPayload = envelope.Payload.AsSpan().ToArray();
            tamperedPayload[^1] ^= 1;
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason(null);
            await helperClient.SendAsync(host.LocalPeerAddress, EnvelopeCodec.Serialize(envelope with { MessageId = Guid.NewGuid().ToString("N"), Payload = tamperedPayload }), cts.Token);
            await Task.Delay(300, cts.Token);
            Assert.Equal(0, Volatile.Read(in offerCount));
            Assert.Equal("file_transfer_offer_secure_envelope_invalid", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferChunk_ReplayedSecureEnvelope_IsRejected()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.replay.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.replay.address");
            NknIdentity hostIdentity = new NknIdentity("host-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            TaskCompletionSource offerReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource acceptReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource startReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int chunkCount = 0;
            byte[] replayEnvelopeBytes = null;
            host.FileTransferOfferReceived += delegate
            {
                offerReceived.TrySetResult();
            };
            helper.FileTransferAcceptReceived += delegate
            {
                acceptReceived.TrySetResult();
            };
            host.FileTransferStartReceived += delegate
            {
                startReceived.TrySetResult();
            };
            host.FileTransferChunkReceived += delegate
            {
                Interlocked.Increment(ref chunkCount);
            };
            helperClient.BeforeSendAsync = delegate (string _, byte[] payload, CancellationToken _)
            {
                if (replayEnvelopeBytes != null || !EnvelopeCodec.TryDeserialize(payload, out Envelope env) || env.Type != MsgType.FileTransferChunk)
                {
                    return Task.CompletedTask;
                }

                replayEnvelopeBytes = EnvelopeCodec.Serialize(env with { MessageId = Guid.NewGuid().ToString("N") });
                return Task.CompletedTask;
            };
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            string expectedHash = Convert.ToBase64String(new byte[32]);
            await helper.SendFileTransferOfferAsync(new FileTransferOfferV1 { SessionId = sessionId, TransferId = "transfer_nkn_replay", FileName = "replay.bin", FileSizeBytes = 1L, Sha256Base64 = expectedHash }, cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = sessionId, TransferId = "transfer_nkn_replay" }, cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await helper.SendFileTransferStartAsync(new FileTransferStartV1 { SessionId = sessionId, TransferId = "transfer_nkn_replay", FileName = "replay.bin", FileSizeBytes = 1L, Sha256Base64 = expectedHash, ChunkCount = 1, ChunkSizeBytes = 1 }, cts.Token);
            await startReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await helper.SendFileTransferChunkAsync(new FileTransferChunkV1 { SessionId = sessionId, TransferId = "transfer_nkn_replay", ChunkIndex = 0, ChunkCount = 1, DataBase64 = Convert.ToBase64String(new byte[1] { 9 }) }, cts.Token);
            await Task.Delay(150, cts.Token);
            Assert.NotNull(replayEnvelopeBytes);
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason(null);
            await helperClient.SendAsync(host.LocalPeerAddress, replayEnvelopeBytes, cts.Token);
            await Task.Delay(300, cts.Token);
            Assert.Equal(1, Volatile.Read(in chunkCount));
            Assert.Equal("file_transfer_chunk_replay_duplicate", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferChunk_LateButWithinLargeReplayWindow_IsAccepted()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.largewindow.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.largewindow.address");
            NknIdentity hostIdentity = new NknIdentity("host-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            TaskCompletionSource offerReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource acceptReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource startReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferChunkV1> firstChunkReceived = new TaskCompletionSource<FileTransferChunkV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<FileTransferChunkV1> secondChunkReceived = new TaskCompletionSource<FileTransferChunkV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            ConcurrentQueue<int> receivedChunkIndexes = new ConcurrentQueue<int>();
            host.FileTransferOfferReceived += delegate
            {
                offerReceived.TrySetResult();
            };
            helper.FileTransferAcceptReceived += delegate
            {
                acceptReceived.TrySetResult();
            };
            host.FileTransferStartReceived += delegate
            {
                startReceived.TrySetResult();
            };
            host.FileTransferChunkReceived += delegate (object? _, FileTransferChunkReceivedEventArgs e)
            {
                receivedChunkIndexes.Enqueue(e.Message.ChunkIndex);
                if (!firstChunkReceived.Task.IsCompleted)
                {
                    firstChunkReceived.TrySetResult(e.Message);
                }
                else
                {
                    secondChunkReceived.TrySetResult(e.Message);
                }
            };
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            string expectedHash = Convert.ToBase64String(new byte[32]);
            await helper.SendFileTransferOfferAsync(new FileTransferOfferV1 { SessionId = sessionId, TransferId = "transfer_nkn_large_replay_window", FileName = "large-window.bin", FileSizeBytes = 2L, Sha256Base64 = expectedHash }, cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1 { SessionId = sessionId, TransferId = "transfer_nkn_large_replay_window" }, cts.Token);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            CoreSmokeTestsBase.InvokeNknIncomingMessage(host, helperClient, new NknIncomingMessage(payload: EnvelopeCodec.Serialize(CoreSmokeTestsBase.BuildSecureFileTransferEnvelope(helper, MsgType.FileTransferStart, new FileTransferStartV1 { SessionId = sessionId, TransferId = "transfer_nkn_large_replay_window", FileName = "large-window.bin", FileSizeBytes = 2L, Sha256Base64 = expectedHash, ChunkCount = 2, ChunkSizeBytes = 1 }, "transfer_nkn_large_replay_window", 2L)), source: helperClient.Address, isTopic: false, topic: null, channel: NknBridgeChannel.Control, bridgeIngressObservedUtcMs: 0L, bridgeMessageObservedUtcMs: 0L, binaryFrameDecodedUtcMs: 0L, socketDataEventEmittedUtcMs: 0L, wsReceiverWriteEnteredUtcMs: 0L, wsMessageEmittedUtcMs: 0L, sdkHandleMsgEnteredUtcMs: 0L, clientMessageDispatchUtcMs: 0L, multiClientMessageDispatchUtcMs: 0L));
            await startReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            Envelope farAheadChunkEnvelope = CoreSmokeTestsBase.BuildSecureFileTransferEnvelope(helper, MsgType.FileTransferChunk, new FileTransferChunkV1 { SessionId = sessionId, TransferId = "transfer_nkn_large_replay_window", ChunkIndex = 0, ChunkCount = 2, DataBase64 = Convert.ToBase64String(new byte[1] { 1 }) }, "transfer_nkn_large_replay_window", 300L);
            Envelope lateChunkEnvelope = CoreSmokeTestsBase.BuildSecureFileTransferEnvelope(helper, MsgType.FileTransferChunk, new FileTransferChunkV1 { SessionId = sessionId, TransferId = "transfer_nkn_large_replay_window", ChunkIndex = 1, ChunkCount = 2, DataBase64 = Convert.ToBase64String(new byte[1] { 2 }) }, "transfer_nkn_large_replay_window", 3L);
            CoreSmokeTestsBase.InvokeNknIncomingMessage(host, helperClient, new NknIncomingMessage(helperClient.ConnectedBulkAddress, EnvelopeCodec.Serialize(farAheadChunkEnvelope), isTopic: false, null, NknBridgeChannel.Bulk, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L));
            await firstChunkReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason(null);
            CoreSmokeTestsBase.InvokeNknIncomingMessage(host, helperClient, new NknIncomingMessage(helperClient.ConnectedBulkAddress, EnvelopeCodec.Serialize(lateChunkEnvelope), isTopic: false, null, NknBridgeChannel.Bulk, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L));
            Assert.Equal(1, (await secondChunkReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token)).ChunkIndex);
            Assert.Equal(new int[2] { 0, 1 }, receivedChunkIndexes.ToArray());
            Assert.NotEqual("file_transfer_chunk_replay_stale", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferChunk_BeforeStart_IsRejected()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            int logStartIndex = CoreSmokeTestsBase.GetOperationalLogLength();
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.beforestart.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.beforestart.address");
            NknIdentity hostIdentity = new NknIdentity("host-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            TaskCompletionSource offerReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int chunkCount = 0;
            host.FileTransferOfferReceived += delegate
            {
                offerReceived.TrySetResult();
            };
            host.FileTransferChunkReceived += delegate
            {
                Interlocked.Increment(ref chunkCount);
            };
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            await helper.SendFileTransferOfferAsync(new FileTransferOfferV1 { SessionId = sessionId, TransferId = "transfer_nkn_before_start", FileName = "state.bin", FileSizeBytes = 1L, Sha256Base64 = Convert.ToBase64String(new byte[32]) }, cts.Token);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
            Envelope chunkEnvelope = CoreSmokeTestsBase.BuildSecureFileTransferEnvelope(helper, MsgType.FileTransferChunk, new FileTransferChunkV1 { SessionId = sessionId, TransferId = "transfer_nkn_before_start", ChunkIndex = 0, ChunkCount = 1, DataBase64 = Convert.ToBase64String(new byte[1] { 122 }) }, "transfer_nkn_before_start", 2L);
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason(null);
            await helperClient.SendBulkAsync(hostClient.ConnectedBulkAddress, EnvelopeCodec.Serialize(chunkEnvelope), cts.Token);
            await Task.Delay(300, cts.Token);
            Assert.Equal(0, Volatile.Read(in chunkCount));
            Assert.Equal("file_transfer_chunk_chunk_requires_start", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason);
            string logTail = CoreSmokeTestsBase.ReadOperationalLogTail(logStartIndex);
            Assert.Contains("event=filetransfer_chunk_ingress", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_chunk_rejected", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=dispatch_state", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferOffer_WhileSameDirectionTransferIsActive_IsRejectedAsBusy()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.busy.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.busy.address");
            NknIdentity hostIdentity = new NknIdentity("host-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            int offerCount = 0;
            TaskCompletionSource<FileTransferOfferV1> firstOfferReceived = new TaskCompletionSource<FileTransferOfferV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                Interlocked.Increment(ref offerCount);
                firstOfferReceived.TrySetResult(e.Message);
            };
            string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            string expectedHash = Convert.ToBase64String(new byte[32]);
            await helper.SendFileTransferOfferAsync(new FileTransferOfferV1 { SessionId = sessionId, TransferId = "transfer_nkn_busy_first", FileName = "first.bin", FileSizeBytes = 1L, Sha256Base64 = expectedHash }, cts.Token);
            Assert.Equal("transfer_nkn_busy_first", (await firstOfferReceived.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token)).TransferId);
            Envelope secondOfferEnvelope = CoreSmokeTestsBase.BuildSecureFileTransferEnvelope(helper, MsgType.FileTransferOffer, new FileTransferOfferV1 { SessionId = sessionId, TransferId = "transfer_nkn_busy_second", FileName = "second.bin", FileSizeBytes = 1L, Sha256Base64 = expectedHash }, "transfer_nkn_busy_second", 2L);
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason(null);
            await helperClient.SendAsync(host.LocalPeerAddress, EnvelopeCodec.Serialize(secondOfferEnvelope), cts.Token);
            await Task.Delay(300, cts.Token);
            Assert.Equal(1, Volatile.Read(in offerCount));
            Assert.Equal("file_transfer_offer_concurrent_transfer_busy", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Fact]
    public async Task NknTransport_FileTransferOffer_UsesFileTransferSecureFamily_AndDedicatedKey()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(8.0));
            NknTransportOptions options = NknTransportOptions.Load();
            FakeNknClient hostClient = new FakeNknClient("host.filetransfer.family.address");
            FakeNknClient helperClient = new FakeNknClient("helper.filetransfer.family.address");
            NknIdentity hostIdentity = new NknIdentity("host-id", hostClient.Address);
            NknIdentity helperIdentity = new NknIdentity("helper-id", helperClient.Address);
            using NknSignalingTransport host = new NknSignalingTransport(hostClient, options, hostIdentity);
            NknSignalingTransport helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            try
            {
                TaskCompletionSource<Envelope> capturedEnvelope = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
                helperClient.BeforeSendAsync = delegate (string _, byte[] payload, CancellationToken _)
                {
                    if (!EnvelopeCodec.TryDeserialize(payload, out Envelope env) || env.Type != MsgType.FileTransferOffer)
                    {
                        return Task.CompletedTask;
                    }

                    capturedEnvelope.TrySetResult(env);
                    return Task.CompletedTask;
                };
                string sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token, InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
                await helper.SendFileTransferOfferAsync(new FileTransferOfferV1 { SessionId = sessionId, TransferId = "transfer_nkn_family", FileName = "family.bin", FileSizeBytes = 1L, Sha256Base64 = Convert.ToBase64String(new byte[32]) }, cts.Token);
                Envelope envelope = await capturedEnvelope.Task.WaitAsync(TimeSpan.FromSeconds(3.0), cts.Token);
                byte[] fileTransferKey = Assert.IsType<byte[]>(CoreSmokeTestsBase.GetPrivateField(helper, "fileTransferSessionSharedKey")).AsSpan().ToArray();
                byte[] controlKey = Assert.IsType<byte[]>(CoreSmokeTestsBase.GetPrivateField(helper, "controlSessionSharedKey")).AsSpan().ToArray();
                try
                {
                    SessionSecureEnvelopePayload securePayload = SessionSecureEnvelopeCodec.Decrypt(fileTransferKey, envelope.Payload, new SessionSecureEnvelopeExpectation(SessionSecureMessageFamily.FileTransfer, "file_transfer_offer", new SessionId(sessionId), new PeerAddress(helper.LocalPeerAddress)));
                    Assert.Equal(SessionSecureMessageFamily.FileTransfer, securePayload.Metadata.Family);
                    Assert.Equal("file_transfer_offer", securePayload.Metadata.MessageType);
                    Assert.NotEqual(Convert.ToBase64String(controlKey), Convert.ToBase64String(fileTransferKey));
                    Assert.ThrowsAny<Exception>(() => SessionSecureEnvelopeCodec.Decrypt(controlKey, envelope.Payload, new SessionSecureEnvelopeExpectation(SessionSecureMessageFamily.FileTransfer, "file_transfer_offer", new SessionId(sessionId), new PeerAddress(helper.LocalPeerAddress))));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(fileTransferKey);
                    CryptographicOperations.ZeroMemory(controlKey);
                }
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

    [Fact]
    public void NknFileTransferDispatchState_TreatsOnlyLateControlUnknownTransferAsBenign()
    {
        MethodInfo method = typeof(NknSignalingTransport).GetMethod("IsBenignLateFileTransferControlRejection", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.True((bool)(method.Invoke(null, new object[2] { MsgType.FileTransferWindowUpdate, "unknown_transfer_id" }) ?? ((object)false)));
        Assert.True((bool)(method.Invoke(null, new object[2] { MsgType.FileTransferPressureState, "unknown_transfer_id" }) ?? ((object)false)));
        Assert.False((bool)(method.Invoke(null, new object[2] { MsgType.FileTransferChunk, "unknown_transfer_id" }) ?? ((object)true)));
        Assert.False((bool)(method.Invoke(null, new object[2] { MsgType.FileTransferWindowUpdate, "window_update_requires_start" }) ?? ((object)true)));
    }

}
