using System.Reflection;
using System.Security.Cryptography;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferV4ReceiverTests : SessionFileTransferServiceTestBase
{
    private const string RetiredV4CreditRepairRuntimeSkip =
        "Retired: Phase 3 V6 receiver-driven runtime no longer uses V4 credit/repair scheduling; covered by SessionFileTransferV6RuntimeTests.";
    private const string DeferredV6TransportEpochRuntimeSkip =
        "Deferred: Phase 4/5 will replace these transport epoch and Tuna handoff expectations with proof-based V6 tests.";

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_WritesOutOfOrderBatches_AndCompletesWithIntegrity()
    {
        const string transferId = "transfer_v4_sparse_receiver_complete";
        const string sessionId = "session_v4_sparse_receiver_complete";
        var payload = Enumerable.Range(1, 10).Select(static value => (byte)value).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-complete.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-complete.bin", payload.Length, chunkSizeBytes: 4, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.ContiguousCommittedChunkIndex == 0));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 1,
                ChunkCount = 2,
                DataSegments =
                [
                    payload.Skip(4).Take(4).ToArray(),
                    payload.Skip(8).Take(2).ToArray(),
                ],
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
            frame.ContiguousCommittedChunkIndex == 0 &&
            frame.DurableReceivedHighestChunkIndex == 2 &&
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 1)));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = [payload.Take(4).ToArray()],
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 5000);
        Assert.Equal(payload.Length, receiver.Snapshot.Inbound!.BytesTransferred);
        Assert.Equal(3, receiver.Snapshot.Inbound.ChunksTransferred);
        Assert.Contains(receiverTransport.SentCompletes, complete =>
            string.Equals(complete.TransferId, transferId, StringComparison.Ordinal) &&
            complete.FileSizeBytes == payload.Length);
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferCompleteFrameV6);
        Assert.Contains(receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>(), static frame => frame.TerminalReady);
        destination.Seek(0, SeekOrigin.Begin);
        var received = destination.ToArray()[..payload.Length];
        Assert.Equal(payload, received);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_FrontierRepairBatch_LogsReceivedAppliedAndAdvanced()
    {
        const string transferId = "transfer_v4_repair_batch_proof";
        const string sessionId = "session_v4_repair_batch_proof";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(1, 12).Select(static value => (byte)value).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-repair-batch-proof.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-repair-batch-proof.bin", payload.Length, chunkSizeBytes: 4, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.ContiguousCommittedChunkIndex == 0));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 1,
                ChunkCount = 2,
                DataSegments =
                [
                    payload.Skip(4).Take(4).ToArray(),
                    payload.Skip(8).Take(4).ToArray(),
                ],
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 1)));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = [payload.Take(4).ToArray()],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains($"event=filetransfer_v4_frontier_repair_frontier_advanced; transfer_id={transferId}", StringComparison.Ordinal),
            timeoutMs: 5000);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains($"event=filetransfer_v4_frontier_repair_batch_received; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_v4_frontier_repair_batch_applied; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_v4_frontier_repair_frontier_advanced; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("requested_missing_range_start=0; requested_missing_range_count=1", log, StringComparison.Ordinal);
        Assert.Contains("committed_frontier_before=0; committed_frontier_after=3", log, StringComparison.Ordinal);
        Assert.Contains("accepted_chunk_count=1", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Receiver_PeerSilence_TerminalsInsteadOfReceivingForever()
    {
        const string transferId = "transfer_v4_receiver_peer_silence";
        const string sessionId = "session_v4_receiver_peer_silence";
        var previousTimeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromMilliseconds(300);
        try
        {
            var payload = Enumerable.Range(1, 12).Select(static value => (byte)value).ToArray();
            var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
            using var destination = new NonDisposingMemoryStream();
            using var senderTransport = new LoopbackFileTransferTransport(sessionId);
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
            senderTransport.Connect(receiverTransport);
            using var receiver = new SessionFileTransferService();
            receiver.AttachTransport(receiverTransport);

            var senderSession = await StartInboundV4ReceiverAsync(
                senderTransport,
                receiver,
                transferId,
                sessionId,
                "v4-peer-silence.bin",
                payload.Length,
                sha256,
                (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

            await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-peer-silence.bin", payload.Length, chunkSizeBytes: 4, sha256), CancellationToken.None);
            await senderSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = [payload.Take(4).ToArray()],
                },
                CancellationToken.None);

            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.BytesTransferred == 4, timeoutMs: 5000);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed, timeoutMs: 5000);

            Assert.Equal(FileTransferResultCodes.PeerDisconnected, receiver.Snapshot.Inbound!.ErrorCode);
            Assert.Contains("Sender stopped responding", receiver.Snapshot.Inbound.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousTimeout;
        }
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V4Receiver_PeerSilenceDuringTunaRecoveryPause_TerminalsInsteadOfReceivingForever()
    {
        const string transferId = "transfer_v4_receiver_peer_silence_tuna_recovery";
        const string sessionId = "session_v4_receiver_peer_silence_tuna_recovery";
        var previousTimeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromMilliseconds(300);
        try
        {
            var payload = Enumerable.Range(1, 12).Select(static value => (byte)value).ToArray();
            var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
            using var destination = new NonDisposingMemoryStream();
            using var senderTransport = new LoopbackFileTransferTransport(sessionId);
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
            senderTransport.Connect(receiverTransport);
            using var receiver = new SessionFileTransferService();
            receiver.AttachTransport(receiverTransport);

            var senderSession = await StartInboundV4ReceiverAsync(
                senderTransport,
                receiver,
                transferId,
                sessionId,
                "v4-peer-silence-tuna-recovery.bin",
                payload.Length,
                sha256,
                (_, _) => Task.FromResult<Stream>(destination));

            await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-peer-silence-tuna-recovery.bin", payload.Length, chunkSizeBytes: 4, sha256), CancellationToken.None);
            await senderSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = [payload.Take(4).ToArray()],
                },
                CancellationToken.None);

            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.BytesTransferred == 4, timeoutMs: 5000);

            receiverTransport.SetLocalDataSessionsUnavailableForTests("receive_stall_recovery");

            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed, timeoutMs: 5000);

            Assert.Equal(FileTransferResultCodes.PeerDisconnected, receiver.Snapshot.Inbound!.ErrorCode);
            Assert.Contains("Sender stopped responding", receiver.Snapshot.Inbound.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousTimeout;
        }
    }

    [Fact]
    public async Task V4SparseReceiver_TunaActivationPauseSuppressesStateSends()
    {
        const string transferId = "transfer_v4_receiver_tuna_activation_pause";
        const string sessionId = "session_v4_receiver_tuna_activation_pause";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(1, 80).Select(static value => (byte)value).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.TransportAccelerationStatusReason = "test_tuna_pending";
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.TransportAccelerationStatusReason = "test_tuna_pending";
        var receiverStateSendCount = 0;
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferStateFrameV4 and not FileTransferReceiverStateFrameV6)
            {
                Interlocked.Increment(ref receiverStateSendCount);
            }

            return Task.FromResult(false);
        };
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundRegularNknV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-receiver-tuna-activation-pause.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(CreateRegularNknV4Manifest(sessionId, transferId, "v4-receiver-tuna-activation-pause.bin", payload.Length, chunkSizeBytes: 4, sha256), CancellationToken.None);
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV4
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = [payload.Take(4).ToArray()],
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.BytesTransferred == 4, timeoutMs: 5000);

        receiverTransport.SetLocalDataSessionsUnavailableForTests("tuna_activation_negotiating");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_paused; direction=inbound", StringComparison.Ordinal),
            timeoutMs: 5000);
        var stateSendCountBeforePause = Volatile.Read(ref receiverStateSendCount);

        receiverTransport.ReceiveDeliveredDataFrame(
            new FileTransferChunkBatchFrameV4
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 1,
                ChunkCount = 16,
                DataSegments = Enumerable.Range(1, 16)
                    .Select(index => payload.Skip(index * 4).Take(4).ToArray())
                    .ToList(),
            });

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_v4_state_send_suppressed_for_tuna_activation_barrier;",
                StringComparison.Ordinal),
            timeoutMs: 5000);

        Assert.Equal(stateSendCountBeforePause, Volatile.Read(ref receiverStateSendCount));
        Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound?.State);
    }

    [Fact]
    public async Task V4SparseReceiver_RegularNknFrontierRepairDueMarksControlFeedbackPressure()
    {
        const string transferId = "transfer_regular_v4_receiver_frontier_pressure";
        const string sessionId = "session_regular_v4_receiver_frontier_pressure";
        var payload = Enumerable.Range(1, 96).Select(static value => (byte)value).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundRegularNknV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "regular-v4-frontier-pressure.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateRegularNknV4Manifest(sessionId, transferId, "regular-v4-frontier-pressure.bin", payload.Length, chunkSizeBytes: 4, sha256),
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame =>
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount > 0)),
            timeoutMs: 5000);
        await WaitUntilAsync(() => !receiverTransport.RegularV4ControlFeedbackPressures.IsEmpty, timeoutMs: 5000);

        var pressure = Assert.Single(receiverTransport.RegularV4ControlFeedbackPressures.Take(1));
        Assert.Equal(sessionId, pressure.SessionId);
        Assert.Equal(transferId, pressure.TransferId);
        Assert.Equal("regular_v4_receiver_frontier_repair_due", pressure.Reason);
        Assert.Equal(0, pressure.CreditExhaustedTimeMs);
        Assert.True(pressure.FrontierLagChunks > 0);
        Assert.True(pressure.PendingRepairCount > 0);
    }

    [Fact]
    public async Task V4SparseReceiver_FrontierAdvance_ClearsObsoleteRepairStateBeforeStaleBatch()
    {
        const string transferId = "transfer_v4_repair_obsolete_after_frontier";
        const string sessionId = "session_v4_repair_obsolete_after_frontier";
        var payload = Enumerable.Range(1, 16).Select(static value => (byte)value).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-obsolete-repair.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-obsolete-repair.bin", payload.Length, chunkSizeBytes: 4, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.ContiguousCommittedChunkIndex == 0));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 1,
                ChunkCount = 2,
                DataSegments =
                [
                    payload.Skip(4).Take(4).ToArray(),
                    payload.Skip(8).Take(4).ToArray(),
                ],
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 1)));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = [payload.Take(4).ToArray()],
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
                frame.ContiguousCommittedChunkIndex == 3),
            timeoutMs: 5000);
        var afterAdvanceLogStart = ReadOperationalLogText().Length;

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = [payload.Take(4).ToArray()],
            },
            CancellationToken.None);
        await Task.Delay(250);

        Assert.DoesNotContain(
            $"event=filetransfer_v4_frontier_repair_batch_ignored; transfer_id={transferId}; session_id={sessionId}; reason=stale_chunk",
            ReadOperationalLogTail(afterAdvanceLogStart),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_UsesV4FileOnlyPrimaryRegularNknFrontierRepair()
    {
        const string transferId = "transfer_v6_sparse_runtime_file_only_frontier_repair";
        const string sessionId = "session_v6_sparse_runtime_file_only_frontier_repair";
        const int chunkSize = 4;
        const int chunkCount = 130;
        const int expectedFrontierRepairChunks = 12;
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, chunkSize * chunkCount).Select(static value => (byte)(value % 251)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        try
        {

            var senderSession = await StartInboundV4ReceiverAsync(
                senderTransport,
                receiver,
                transferId,
                sessionId,
                "v6-sparse-runtime-file-only-frontier-repair.bin",
                payload.Length,
                sha256,
                (_, _) => Task.FromResult<Stream>(destination));

            await senderSession.SendAsync(
                CreateManifest(sessionId, transferId, "v6-sparse-runtime-file-only-frontier-repair.bin", payload.Length, chunkSize, sha256),
                CancellationToken.None);
            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.MissingRanges.Count == 0));

            await senderSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 64,
                    ChunkCount = chunkCount - 64,
                    DataSegments = Enumerable.Range(64, chunkCount - 64)
                        .Select(index => payload.Skip(index * chunkSize).Take(chunkSize).ToArray())
                        .ToArray(),
                },
                CancellationToken.None);

            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(frame =>
                frame.MissingRanges.Count == 1 &&
                frame.MissingRanges[0].StartChunkIndex == 0 &&
                frame.MissingRanges[0].ChunkCount == expectedFrontierRepairChunks), timeoutMs: 5000);

            var state = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().First(frame =>
                frame.MissingRanges.Count == 1 &&
                frame.MissingRanges[0].StartChunkIndex == 0 &&
                frame.MissingRanges[0].ChunkCount == expectedFrontierRepairChunks);
            Assert.Equal(expectedFrontierRepairChunks, state.MissingRanges.Sum(static range => range.ChunkCount));
            Assert.StartsWith("regular-nkn-frontier:0:12:", state.RepairRequestId, StringComparison.Ordinal);
            Assert.Equal("frontier", state.Priority);
            Assert.Equal("regular_nkn_frontier_stall_control_bulk", state.RecoveryMode);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_primary_regular_nkn_frontier_repair_transaction_started", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_primary_regular_nkn_frontier_repair_transaction_state_sent", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v4_repair_requested", logTail, StringComparison.Ordinal);
            Assert.Contains("requested_chunk_count=12", logTail, StringComparison.Ordinal);
            Assert.Contains("repair_interval_ms=250", logTail, StringComparison.Ordinal);
            Assert.Contains("initial_frontier_repair_chunks=12", logTail, StringComparison.Ordinal);
        }
        finally
        {
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_RetainsFrontierRepairTransactionAcrossNoMissingRefresh()
    {
        const string transferId = "transfer_v6_sparse_runtime_frontier_transaction_retained";
        const string sessionId = "session_v6_sparse_runtime_frontier_transaction_retained";
        const int chunkSize = 4;
        const int chunkCount = 130;
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, chunkSize * chunkCount).Select(static value => (byte)(value % 251)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v6-sparse-runtime-frontier-transaction-retained.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v6-sparse-runtime-frontier-transaction-retained.bin", payload.Length, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.MissingRanges.Count == 0));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 64,
                ChunkCount = chunkCount - 64,
                DataSegments = Enumerable.Range(64, chunkCount - 64)
                    .Select(index => payload.Skip(index * chunkSize).Take(chunkSize).ToArray())
                    .ToArray(),
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(frame =>
                frame.MissingRanges.Count == 1 &&
                frame.MissingRanges[0].StartChunkIndex == 0 &&
                frame.MissingRanges[0].ChunkCount == 12 &&
                frame.RepairRequestId?.StartsWith("regular-nkn-frontier:0:12:", StringComparison.Ordinal) == true),
            timeoutMs: 5000);

        var firstRepairState = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().First(frame =>
            frame.MissingRanges.Count == 1 &&
            frame.MissingRanges[0].StartChunkIndex == 0 &&
            frame.MissingRanges[0].ChunkCount == 12 &&
            frame.RepairRequestId?.StartsWith("regular-nkn-frontier:0:12:", StringComparison.Ordinal) == true);
        var firstRepairRequestId = firstRepairState.RepairRequestId;
        Assert.NotNull(firstRepairRequestId);
        var stateCountBeforeRefresh = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count();

        await senderSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 0,
                RepairRequestId = "v6-regular-nkn-state-refresh:retain-before-due",
                MissingRanges =
                [
                    new FileTransferRangeV4
                    {
                        StartChunkIndex = 0,
                        ChunkCount = 1,
                    },
                ],
                Priority = "state_refresh",
                RecoveryMode = "regular_nkn_state_refresh",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count() > stateCountBeforeRefresh,
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_primary_regular_nkn_frontier_repair_transaction_retained",
                StringComparison.Ordinal),
            timeoutMs: 5000);

        await Task.Delay(300);
        stateCountBeforeRefresh = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count();
        await senderSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 0,
                RepairRequestId = "v6-regular-nkn-state-refresh:retain-after-due",
                MissingRanges =
                [
                    new FileTransferRangeV4
                    {
                        StartChunkIndex = 0,
                        ChunkCount = 1,
                    },
                ],
                Priority = "state_refresh",
                RecoveryMode = "regular_nkn_state_refresh",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count() > stateCountBeforeRefresh,
            timeoutMs: 5000);

        var repeatedRepairState = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Last(frame =>
            frame.MissingRanges.Count == 1 &&
            frame.MissingRanges[0].StartChunkIndex == 0 &&
            frame.MissingRanges[0].ChunkCount == 12);
        Assert.Equal(firstRepairRequestId, repeatedRepairState.RepairRequestId);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_primary_regular_nkn_frontier_repair_transaction_retained", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("reason=no_missing_ranges; committed_frontier_chunk_index=0", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_StateRefreshResendsSparseReceiverState()
    {
        const string transferId = "transfer_v6_sparse_runtime_state_refresh_receiver";
        const string sessionId = "session_v6_sparse_runtime_state_refresh_receiver";
        const int chunkSize = 4;
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 16).Select(static value => (byte)(value % 251)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        try
        {

            var senderSession = await StartInboundV4ReceiverAsync(
                senderTransport,
                receiver,
                transferId,
                sessionId,
                "v6-sparse-runtime-state-refresh-receiver.bin",
                payload.Length,
                sha256,
                (_, _) => Task.FromResult<Stream>(destination));

            await senderSession.SendAsync(
                CreateManifest(sessionId, transferId, "v6-sparse-runtime-state-refresh-receiver.bin", payload.Length, chunkSize, sha256),
                CancellationToken.None);
            await WaitUntilAsync(
                () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
                    frame.MissingRanges.Count == 0),
                timeoutMs: 5000);
            var stateCountBeforeRefresh = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count();

            await senderSession.SendAsync(
                new FileTransferFrontierRequestFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = 0,
                    RepairRequestId = "v6-regular-nkn-state-refresh:test",
                    MissingRanges =
                    [
                        new FileTransferRangeV4
                        {
                            StartChunkIndex = 0,
                            ChunkCount = 1,
                        },
                    ],
                    Priority = "state_refresh",
                    RecoveryMode = "regular_nkn_state_refresh",
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count() > stateCountBeforeRefresh,
                timeoutMs: 5000);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "reason=regular_nkn_state_refresh",
                    StringComparison.Ordinal),
                timeoutMs: 5000);

            var refreshedState = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Last();
            Assert.Equal(FileTransferProtocol.ReceiverStateFrameTypeV6, refreshedState.Type);
            Assert.True(refreshedState.CreditUntilChunkIndexExclusive > 0);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_regular_nkn_state_refresh_received", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_regular_nkn_state_refresh_state_resent", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=regular_nkn_state_refresh", logTail, StringComparison.Ordinal);
        }
        finally
        {
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_CheckpointSyncResendsSparseReceiverCheckpoint()
    {
        const string transferId = "transfer_v6_bulk_checkpoint_receiver";
        const string sessionId = "session_v6_bulk_checkpoint_receiver";
        const int chunkSize = 4;
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 16).Select(static value => (byte)(value % 251)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v6-bulk-checkpoint-receiver.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v6-bulk-checkpoint-receiver.bin", payload.Length, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
                frame.MissingRanges.Count == 0),
            timeoutMs: 5000);
        var stateCountBeforeRefresh = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count();

        await senderSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 3,
                RepairRequestId = "v6-regular-nkn-checkpoint-sync:test",
                MissingRanges =
                [
                    new FileTransferRangeV4
                    {
                        StartChunkIndex = 0,
                        ChunkCount = 1,
                    },
                ],
                Priority = "checkpoint_sync",
                RecoveryMode = "regular_nkn_checkpoint_sync",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count() > stateCountBeforeRefresh,
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => FilterV4ReceiverTransferLog(ReadV4ReceiverLogSnapshot(logStart), transferId).Contains(
                "event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_sent",
                StringComparison.Ordinal),
            timeoutMs: 5000);

        var checkpoint = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Last();
        Assert.Equal(FileTransferProtocol.ReceiverStateFrameTypeV6, checkpoint.Type);
        Assert.Equal("regular_nkn_checkpoint_sync", checkpoint.RecoveryMode);
        Assert.Equal("checkpoint_sync", checkpoint.Priority);
        Assert.Equal("v6-regular-nkn-checkpoint-sync:test", checkpoint.RepairRequestId);
        Assert.True(checkpoint.CreditUntilChunkIndexExclusive > 0);
        var logTail = FilterV4ReceiverTransferLog(ReadV4ReceiverLogSnapshot(logStart), transferId);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_received", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_sent", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_response_queued", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_regular_nkn_state_refresh_received", logTail, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V4SparseReceiver_TransportRebind_RepeatsMissingFrontierRequest()
    {
        const string transferId = "transfer_v4_sparse_receiver_rebind_missing";
        const string sessionId = "session_v4_sparse_receiver_rebind_missing";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(1, 12).Select(static value => (byte)value).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-rebind-missing.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-rebind-missing.bin", payload.Length, chunkSizeBytes: 4, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 1,
                ChunkCount = 2,
                DataSegments =
                [
                    payload.Skip(4).Take(4).ToArray(),
                    payload.Skip(8).Take(4).ToArray(),
                ],
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
            frame.ContiguousCommittedChunkIndex == 0 &&
            frame.DurableReceivedHighestChunkIndex == 2 &&
            frame.MissingRanges.Any(static range => range.StartChunkIndex == 0 && range.ChunkCount == 1)));
        var stateCountBeforeRebind = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count();

        receiverTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        receiverTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Skip(stateCountBeforeRebind).Any(static frame =>
                frame.ContiguousCommittedChunkIndex == 0 &&
                frame.DurableReceivedHighestChunkIndex == 2 &&
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 1)),
            timeoutMs: 5000);

        var rebindState = receiverTransport.SentDataFrames
            .OfType<FileTransferReceiverStateFrameV6>()
            .Skip(stateCountBeforeRebind)
            .Last(frame =>
                frame.ContiguousCommittedChunkIndex == 0 &&
                frame.DurableReceivedHighestChunkIndex == 2 &&
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 1));
        var frontierRange = Assert.Single(rebindState.MissingRanges);
        Assert.Equal(0, frontierRange.StartChunkIndex);
        Assert.Equal(1, frontierRange.ChunkCount);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v4_emergency_frontier_repair_requested;", log, StringComparison.Ordinal);
        Assert.Contains("requested_chunk_count=1", log, StringComparison.Ordinal);
        Assert.Contains("rebind_generation=1", log, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V4SparseReceiver_TransportRebind_TinyProgressWithRemainingFrontierGap_DoesNotDeclareRecovered()
    {
        const string transferId = "transfer_v4_sparse_receiver_rebind_tiny_progress";
        const string sessionId = "session_v4_sparse_receiver_rebind_tiny_progress";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(1, 12).Select(static value => (byte)value).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-rebind-tiny-progress.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-rebind-tiny-progress.bin", payload.Length, chunkSizeBytes: 4, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 2,
                ChunkCount = 1,
                DataSegments = [payload.Skip(8).Take(4).ToArray()],
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
            frame.ContiguousCommittedChunkIndex == 0 &&
            frame.DurableReceivedHighestChunkIndex == 2 &&
            frame.MissingRanges.Any(static range => range.StartChunkIndex == 0 && range.ChunkCount >= 1)));

        receiverTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        receiverTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = [payload.Take(4).ToArray()],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_rebind_recovery_still_stalled;", StringComparison.Ordinal),
            timeoutMs: 5000);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("committed_chunk=1", log, StringComparison.Ordinal);
        Assert.Contains("highest_received_chunk=2", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_transport_rebind_recovered; direction=inbound; transfer_id={transferId}", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_TerminalReadyStateSendStall_DoesNotBlockCompleteFrame()
    {
        const string transferId = "transfer_v4_terminal_state_stall";
        const string sessionId = "session_v4_terminal_state_stall";
        var payload = Enumerable.Range(1, 12).Select(static value => (byte)value).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        var terminalStateSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiverTransport.OutboundDataFrameDeliveryOverrideWithLaneAsync = async (_, frame, _, ct) =>
        {
            if (frame is FileTransferReceiverStateFrameV6 { TerminalReady: true })
            {
                terminalStateSendStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return true;
            }

            return false;
        };
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-terminal-state-stall.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-terminal-state-stall.bin", payload.Length, chunkSizeBytes: payload.Length, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.ContiguousCommittedChunkIndex == 0));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = [payload],
            },
            CancellationToken.None);

        await WaitUntilAsync(() => terminalStateSendStarted.Task.IsCompleted, timeoutMs: 5000);
        await WaitUntilAsync(() => receiverTransport.SentCompletes.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 5000);

        Assert.Contains(receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>(), static frame => frame.TerminalReady);
        Assert.Contains(receiverTransport.SentCompletes, complete =>
            string.Equals(complete.TransferId, transferId, StringComparison.Ordinal) &&
            complete.FileSizeBytes == payload.Length);
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferCompleteFrameV6);
        var log = ReadOperationalLogText();
        Assert.Contains("event=filetransfer_v4_receiver_completed_chunks;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_finalize_started;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_sparse_hash_started;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_sparse_hash_completed;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_lifecycle_priority_sent; kind=complete", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_terminal_ready_state_send_deferred;", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_InitialCreditUses64MiBFileOnlySparseWindow()
    {
        const string transferId = "transfer_v4_sparse_receiver_credit_64m";
        const string sessionId = "session_v4_sparse_receiver_credit_64m";
        const int fileSizeBytes = 128 * 1024 * 1024;
        const int expectedCreditBytes = 64 * 1024 * 1024;
        const int expectedCreditQuantumBytes = 1024 * 1024;
        const int chunkSizeBytes = 21 * 1024;
        var expectedWindowChunks = checked((int)((expectedCreditBytes + chunkSizeBytes - 1L) / chunkSizeBytes));
        var expectedQuantumChunks = checked((int)((expectedCreditQuantumBytes + chunkSizeBytes - 1L) / chunkSizeBytes));
        var expectedCreditChunkCount = ((expectedWindowChunks + expectedQuantumChunks - 1) / expectedQuantumChunks) * expectedQuantumChunks;
        var sha256 = Convert.ToBase64String(new byte[32]);
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-credit-64m.bin",
            fileSizeBytes,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v4-credit-64m.bin", fileSizeBytes, chunkSizeBytes, sha256),
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

        var initialState = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>()
            .First(frame => frame.CreditUntilChunkIndexExclusive > 0);
        Assert.Equal(expectedCreditChunkCount, initialState.CreditUntilChunkIndexExclusive);
        Assert.Equal(0, initialState.ContiguousCommittedChunkIndex);
        Assert.Equal(-1, initialState.DurableReceivedHighestChunkIndex);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6Profile_InitialCreditUsesV4Sized64MiBWindow()
    {
        const string transferId = "transfer_v6_bulk_sparse_receiver_credit_64m";
        const string sessionId = "session_v6_bulk_sparse_receiver_credit_64m";
        const int fileSizeBytes = 128 * 1024 * 1024;
        const int expectedCreditBytes = 64 * 1024 * 1024;
        const int expectedCreditQuantumBytes = 1024 * 1024;
        const int chunkSizeBytes = 21 * 1024;
        var expectedWindowChunks = checked((int)((expectedCreditBytes + chunkSizeBytes - 1L) / chunkSizeBytes));
        var expectedQuantumChunks = checked((int)((expectedCreditQuantumBytes + chunkSizeBytes - 1L) / chunkSizeBytes));
        var expectedCreditChunkCount = ((expectedWindowChunks + expectedQuantumChunks - 1) / expectedQuantumChunks) * expectedQuantumChunks;
        var sha256 = Convert.ToBase64String(new byte[32]);
        var logStart = ReadOperationalLogText().Length;
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v6-bulk-sparse-credit-64m.bin",
            fileSizeBytes,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v6-bulk-sparse-credit-64m.bin", fileSizeBytes, chunkSizeBytes, sha256),
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

        var initialState = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>()
            .First(frame => frame.CreditUntilChunkIndexExclusive > 0);
        Assert.Equal(expectedCreditChunkCount, initialState.CreditUntilChunkIndexExclusive);
        Assert.Equal(0, initialState.ContiguousCommittedChunkIndex);
        Assert.Equal(-1, initialState.DurableReceivedHighestChunkIndex);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected", logTail, StringComparison.Ordinal);
        Assert.Contains("runtime_profile=primary_regular_nkn_bulk_v6", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_state; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.Contains("state=awaiting_manifest", logTail, StringComparison.Ordinal);
        Assert.Contains("state=credit_granted", logTail, StringComparison.Ordinal);
        Assert.Contains($"credit_window_chunks={expectedWindowChunks}", logTail, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_WithScreenshareMixedFlag_InitialCreditUsesMixedWindow()
    {
        const string transferId = "transfer_v4_sparse_receiver_guard_credit";
        const string sessionId = "session_v4_sparse_receiver_guard_credit";
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        const int fileSizeBytes = 8 * 1024 * 1024;
        const int chunkSizeBytes = 21 * 1024;
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "1");
        try
        {
            var sha256 = Convert.ToBase64String(new byte[32]);
            using var destination = new NonDisposingMemoryStream();
            using var senderTransport = new LoopbackFileTransferTransport(sessionId);
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
            senderTransport.Connect(receiverTransport);
            using var receiver = new SessionFileTransferService();
            receiver.AttachTransport(receiverTransport);
            receiver.SetSessionScreenShareActive(true);

            var senderSession = await StartInboundV4ReceiverAsync(
                senderTransport,
                receiver,
                transferId,
                sessionId,
                "v4-guard-credit.bin",
                fileSizeBytes,
                sha256,
                (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

            await senderSession.SendAsync(
                CreateManifest(sessionId, transferId, "v4-guard-credit.bin", fileSizeBytes, chunkSizeBytes, sha256),
                CancellationToken.None);

            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

            var initialState = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>()
                .First(frame => frame.CreditUntilChunkIndexExclusive > 0);
            Assert.Equal(96, initialState.CreditUntilChunkIndexExclusive);

            var log = ReadOperationalLogText();
            Assert.Contains("event=filetransfer_v6_mixed_enabled;", log, StringComparison.Ordinal);
            Assert.Contains("credit_window_chunks=96", log, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_WithDegradedScreenshareMixedFlag_InitialCreditUsesDegradedMixedWindow()
    {
        const string transferId = "transfer_v4_sparse_receiver_guard_degraded";
        const string sessionId = "session_v4_sparse_receiver_guard_degraded";
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        const int fileSizeBytes = 8 * 1024 * 1024;
        const int chunkSizeBytes = 21 * 1024;
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "1");
        try
        {
            var sha256 = Convert.ToBase64String(new byte[32]);
            using var destination = new NonDisposingMemoryStream();
            using var senderTransport = new LoopbackFileTransferTransport(sessionId);
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
            senderTransport.Connect(receiverTransport);
            using var receiver = new SessionFileTransferService();
            receiver.AttachTransport(receiverTransport);
            receiver.SetSessionScreenShareActive(true);
            receiver.SetSessionScreenShareDegraded(true);

            var senderSession = await StartInboundV4ReceiverAsync(
                senderTransport,
                receiver,
                transferId,
                sessionId,
                "v4-guard-degraded.bin",
                fileSizeBytes,
                sha256,
                (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

            await senderSession.SendAsync(
                CreateManifest(sessionId, transferId, "v4-guard-degraded.bin", fileSizeBytes, chunkSizeBytes, sha256),
                CancellationToken.None);

            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

            var initialState = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>()
                .First(frame => frame.CreditUntilChunkIndexExclusive > 0);
            Assert.Equal(24, initialState.CreditUntilChunkIndexExclusive);

            var log = ReadOperationalLogText();
            Assert.Contains("event=filetransfer_v6_mixed_enabled;", log, StringComparison.Ordinal);
            Assert.Contains("credit_window_chunks=24", log, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_DurableAheadAdvancesSparseCreditBeyondFrontierWindow()
    {
        const string transferId = "transfer_v4_frontier_cap";
        const string sessionId = "session_v4_frontier_cap";
        const int fileSizeBytes = 128 * 1024 * 1024;
        const int expectedCreditBytes = 64 * 1024 * 1024;
        const int chunkSizeBytes = 1024;
        var expectedCreditChunkCount = expectedCreditBytes / chunkSizeBytes;
        var sha256 = Convert.ToBase64String(new byte[32]);
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-frontier-credit-cap.bin",
            fileSizeBytes,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v4-frontier-credit-cap.bin", fileSizeBytes, chunkSizeBytes, sha256),
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = expectedCreditChunkCount,
                ChunkCount = 2,
                DataSegments =
                [
                    Enumerable.Repeat((byte)1, chunkSizeBytes).ToArray(),
                    Enumerable.Repeat((byte)2, chunkSizeBytes).ToArray(),
                ],
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>()
            .Any(frame =>
                frame.ContiguousCommittedChunkIndex == 0 &&
                frame.DurableReceivedHighestChunkIndex >= expectedCreditChunkCount + 1));

        var durableAheadState = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>()
            .Last(frame =>
                frame.ContiguousCommittedChunkIndex == 0 &&
                frame.DurableReceivedHighestChunkIndex >= expectedCreditChunkCount + 1);
        Assert.True(
            durableAheadState.CreditUntilChunkIndexExclusive > expectedCreditChunkCount,
            $"Expected sparse durable progress to advance credit beyond {expectedCreditChunkCount}, got {durableAheadState.CreditUntilChunkIndexExclusive}.");

        var log = ReadOperationalLogText();
        Assert.Contains("frontier_window_credit_capped=0", log, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V4SparseReceiver_TransportRebindWithFrontierGap_ClampsAdvertisedCreditToFrontierWindow()
    {
        const string transferId = "transfer_v4_rebind_frontier_credit_cap";
        const string sessionId = "session_v4_rebind_frontier_credit_cap";
        const int fileSizeBytes = 128 * 1024 * 1024;
        const int expectedCreditBytes = 64 * 1024 * 1024;
        const int chunkSizeBytes = 1024;
        var expectedFrontierCreditChunkCount = expectedCreditBytes / chunkSizeBytes;
        var logStart = ReadOperationalLogText().Length;
        var sha256 = Convert.ToBase64String(new byte[32]);
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-rebind-frontier-credit-cap.bin",
            fileSizeBytes,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v4-rebind-frontier-credit-cap.bin", fileSizeBytes, chunkSizeBytes, sha256),
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = expectedFrontierCreditChunkCount,
                ChunkCount = 2,
                DataSegments =
                [
                    Enumerable.Repeat((byte)1, chunkSizeBytes).ToArray(),
                    Enumerable.Repeat((byte)2, chunkSizeBytes).ToArray(),
                ],
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>()
            .Any(frame =>
                frame.ContiguousCommittedChunkIndex == 0 &&
                frame.DurableReceivedHighestChunkIndex >= expectedFrontierCreditChunkCount + 1 &&
                frame.CreditUntilChunkIndexExclusive > expectedFrontierCreditChunkCount));
        var stateCountBeforeRebind = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count();

        receiverTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        receiverTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Skip(stateCountBeforeRebind).Any(static frame =>
                frame.ContiguousCommittedChunkIndex == 0 &&
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount > 0)),
            timeoutMs: 5000);

        var rebindState = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>()
            .Skip(stateCountBeforeRebind)
            .Last(frame =>
                frame.ContiguousCommittedChunkIndex == 0 &&
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount > 0));
        Assert.Equal(expectedFrontierCreditChunkCount, rebindState.CreditUntilChunkIndexExclusive);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v4_frontier_credit_clamped;", log, StringComparison.Ordinal);
        Assert.Contains("frontier_window_credit_capped=1", log, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V4SparseReceiver_TransportRebindWithFrontierGap_KeepsCreditClampedWhenMoreFarAheadDataArrives()
    {
        const string transferId = "transfer_v4_rebind_frontier_credit_cap_no_missing";
        const string sessionId = "session_v4_rebind_frontier_credit_cap_no_missing";
        const int fileSizeBytes = 128 * 1024 * 1024;
        const int expectedCreditBytes = 64 * 1024 * 1024;
        const int chunkSizeBytes = 1024;
        var expectedFrontierCreditChunkCount = expectedCreditBytes / chunkSizeBytes;
        var sha256 = Convert.ToBase64String(new byte[32]);
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-rebind-frontier-credit-cap-no-missing.bin",
            fileSizeBytes,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v4-rebind-frontier-credit-cap-no-missing.bin", fileSizeBytes, chunkSizeBytes, sha256),
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = expectedFrontierCreditChunkCount,
                ChunkCount = 2,
                DataSegments =
                [
                    Enumerable.Repeat((byte)1, chunkSizeBytes).ToArray(),
                    Enumerable.Repeat((byte)2, chunkSizeBytes).ToArray(),
                ],
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>()
            .Any(frame =>
                frame.ContiguousCommittedChunkIndex == 0 &&
                frame.DurableReceivedHighestChunkIndex >= expectedFrontierCreditChunkCount + 1 &&
                frame.CreditUntilChunkIndexExclusive > expectedFrontierCreditChunkCount));

        var stateCountBeforeRebind = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count();
        receiverTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        receiverTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = expectedFrontierCreditChunkCount + 2,
                ChunkCount = 1,
                DataSegments = [Enumerable.Repeat((byte)3, chunkSizeBytes).ToArray()],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Skip(stateCountBeforeRebind).Any(frame =>
                frame.ContiguousCommittedChunkIndex == 0 &&
                frame.DurableReceivedHighestChunkIndex >= expectedFrontierCreditChunkCount + 2),
            timeoutMs: 5000);

        var postRebindLagStates = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>()
            .Skip(stateCountBeforeRebind)
            .Where(frame =>
                frame.ContiguousCommittedChunkIndex == 0 &&
                frame.DurableReceivedHighestChunkIndex >= expectedFrontierCreditChunkCount + 1)
            .ToArray();
        Assert.NotEmpty(postRebindLagStates);
        Assert.All(
            postRebindLagStates,
            frame => Assert.Equal(expectedFrontierCreditChunkCount, frame.CreditUntilChunkIndexExclusive));
    }

    [Fact]
    public async Task V4SparseReceiver_PostFallbackFrontierRepairWidensAfterFirstV6Proof()
    {
        const string transferId = "transfer_v4_post_fallback_backfill_widens";
        const string sessionId = "session_v4_post_fallback_backfill_widens";
        const int chunkSize = 4;
        const int chunkCount = 32;
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, chunkSize * chunkCount).Select(static value => (byte)(value % 251)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        FileTransferChunkBatchFrameV6 CreateBatch(int startChunkIndex, int chunkCountToSend)
            => new()
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = startChunkIndex,
                ChunkCount = chunkCountToSend,
                DataSegments = Enumerable.Range(startChunkIndex, chunkCountToSend)
                    .Select(index => payload.Skip(index * chunkSize).Take(chunkSize).ToArray())
                    .ToArray(),
            };

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-post-fallback-backfill-widens.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-post-fallback-backfill-widens.bin", payload.Length, chunkSize, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

        var stateCountBeforeRebind = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count();
        receiverTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        receiverTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await senderSession.SendAsync(CreateBatch(1, 2), CancellationToken.None);
        await senderSession.SendAsync(CreateBatch(6, 1), CancellationToken.None);
        await senderSession.SendAsync(CreateBatch(19, 1), CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Skip(stateCountBeforeRebind).Any(static frame =>
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 1)),
            timeoutMs: 5000);
        Assert.Contains(
            receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Skip(stateCountBeforeRebind),
            static frame => frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 1));

        await senderSession.SendAsync(CreateBatch(0, 1), CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Skip(stateCountBeforeRebind).Any(static frame =>
                frame.ContiguousCommittedChunkIndex == 3 &&
                frame.MissingRanges.Any(range => range.StartChunkIndex == 3 && range.ChunkCount == 3)),
            timeoutMs: 5000);
        Assert.Contains(
            receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Skip(stateCountBeforeRebind),
            static frame =>
                frame.ContiguousCommittedChunkIndex == 3 &&
                frame.MissingRanges.Any(range => range.StartChunkIndex == 3 && range.ChunkCount == 3));

        await senderSession.SendAsync(CreateBatch(3, 3), CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Skip(stateCountBeforeRebind).Any(static frame =>
                frame.ContiguousCommittedChunkIndex == 7 &&
                frame.MissingRanges.Any(range => range.StartChunkIndex == 7 && range.ChunkCount >= 3)),
            timeoutMs: 5000);
        Assert.Contains(
            receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Skip(stateCountBeforeRebind),
            static frame =>
                frame.ContiguousCommittedChunkIndex == 7 &&
                frame.MissingRanges.Any(range => range.StartChunkIndex == 7 && range.ChunkCount >= 3));
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_ReportsFirstMissingRangeImmediately_ThenSuppressesUntilRetryInterval()
    {
        const string transferId = "transfer_v4_repair_lifecycle";
        const string sessionId = "session_v4_repair_lifecycle";
        var payload = Enumerable.Range(0, 12).Select(static value => (byte)(value + 1)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-repair-lifecycle.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-repair-lifecycle.bin", payload.Length, chunkSizeBytes: 4, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.ContiguousCommittedChunkIndex == 0));

        var outOfOrderBatch = new FileTransferChunkBatchFrameV6
        {
            SessionId = sessionId,
            TransferId = transferId,
            StartChunkIndex = 1,
            ChunkCount = 2,
            DataSegments =
            [
                payload.Skip(4).Take(4).ToArray(),
                payload.Skip(8).Take(4).ToArray(),
            ],
        };

        await senderSession.SendAsync(outOfOrderBatch, CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>()
            .Any(static frame => frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 1)));

        var stateCountBeforeDuplicate = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count();
        await senderSession.SendAsync(outOfOrderBatch, CancellationToken.None);
        await Task.Delay(150);
        Assert.Equal(stateCountBeforeDuplicate, receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count());

        var missingRangeStateCountBeforeRetry = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>()
            .Count(static frame => frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 1));
        await Task.Delay(850);
        await senderSession.SendAsync(outOfOrderBatch, CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>()
            .Count(static frame => frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 1)) > missingRangeStateCountBeforeRetry);

        var log = ReadOperationalLogText();
        Assert.Contains("event=filetransfer_v4_repair_requested", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_repair_suppressed; direction=receiver", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_PartialRepairProgressRequestsRemainingNextFrontierRange()
    {
        const string transferId = "transfer_v4_overlap_repair_suppression";
        const string sessionId = "session_v4_overlap_repair_suppression";
        const int chunkSize = 4;
        const int chunkCount = 80;
        var payload = Enumerable.Range(0, chunkSize * chunkCount).Select(static value => (byte)(value % 251)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-overlap-repair-suppression.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-overlap-repair-suppression.bin", payload.Length, chunkSize, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.MissingRanges.Count == 0));
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 64,
                ChunkCount = chunkCount - 64,
                DataSegments = Enumerable.Range(64, chunkCount - 64)
                    .Select(index => payload.Skip(index * chunkSize).Take(chunkSize).ToArray())
                    .ToArray(),
            },
            CancellationToken.None);

        await Task.Delay(850);
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 64,
                ChunkCount = 1,
                DataSegments = [payload.Skip(64 * chunkSize).Take(chunkSize).ToArray()],
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12)));

        var stateCountBeforeRepairFill = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count();
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 0,
                ChunkCount = 3,
                DataSegments =
                [
                    payload.Take(chunkSize).ToArray(),
                    payload.Skip(chunkSize).Take(chunkSize).ToArray(),
                    payload.Skip(2 * chunkSize).Take(chunkSize).ToArray(),
                ],
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count() > stateCountBeforeRepairFill);
        var stateAfterPartialFill = receiverTransport.SentDataFrames
            .OfType<FileTransferReceiverStateFrameV6>()
            .Skip(stateCountBeforeRepairFill)
            .First();
        Assert.Equal(3, stateAfterPartialFill.ContiguousCommittedChunkIndex);

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 3 && range.ChunkCount == 12)), timeoutMs: 4000);

        var log = ReadOperationalLogText();
        Assert.Contains("overlap_repair_request_key=", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_StalledFrontierWithoutDurableAhead_EmitsTailRepairRange()
    {
        const string transferId = "transfer_v4_frontier_tail_repair";
        const string sessionId = "session_v4_frontier_tail_repair";
        var payload = Enumerable.Range(0, 12).Select(static value => (byte)(value + 1)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-frontier-tail-repair.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-frontier-tail-repair.bin", payload.Length, chunkSizeBytes: 4, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
            frame.ContiguousCommittedChunkIndex == 0 &&
            frame.DurableReceivedHighestChunkIndex == -1 &&
            frame.MissingRanges.Count == 0));

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 3)), timeoutMs: 4000);

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = [payload.Take(4).ToArray()],
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
            frame.ContiguousCommittedChunkIndex == 1), timeoutMs: 4000);

        var log = ReadOperationalLogText();
        Assert.Contains("event=filetransfer_v4_repair_chunk_observed", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_frontier_stall_missing_range_due", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_frontier_stall_missing_range_filled", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_StalledFrontierUsesSmallRepairBurstForLargeTail()
    {
        const string transferId = "transfer_v4_frontier_tail_retry_narrow";
        const string sessionId = "session_v4_frontier_tail_retry_narrow";
        const int chunkSize = 4;
        const int chunkCount = 130;
        var payload = Enumerable.Range(0, chunkSize * chunkCount).Select(static value => (byte)(value % 251)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-frontier-tail-retry-narrow.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-frontier-tail-retry-narrow.bin", payload.Length, chunkSize, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.MissingRanges.Count == 0));
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12)), timeoutMs: 5000);

        var log = ReadOperationalLogText();
        Assert.Contains("event=filetransfer_v4_frontier_stall_missing_range_due", log, StringComparison.Ordinal);
        Assert.Contains("requested_chunk_count=12", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_DurableAheadInitialRepairUsesSmallFrontierBurst()
    {
        const string transferId = "transfer_v4_durable_ahead_retry_narrow";
        const string sessionId = "session_v4_durable_ahead_retry_narrow";
        const int chunkSize = 4;
        const int chunkCount = 130;
        var payload = Enumerable.Range(0, chunkSize * chunkCount).Select(static value => (byte)(value % 251)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-durable-ahead-retry-narrow.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-durable-ahead-retry-narrow.bin", payload.Length, chunkSize, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 64,
                ChunkCount = chunkCount - 64,
                DataSegments = Enumerable.Range(64, chunkCount - 64)
                    .Select(index => payload.Skip(index * chunkSize).Take(chunkSize).ToArray())
                    .ToArray(),
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12)), timeoutMs: 4000);
        var state = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().First(frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12));

        var log = ReadOperationalLogText();
        Assert.Contains("event=filetransfer_v4_repair_requested", log, StringComparison.Ordinal);
        Assert.Equal(12, state.MissingRanges.Sum(static range => range.ChunkCount));
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_SameFrontierRetryKeepsFrontierBurstAndDefersLaterRanges()
    {
        const string transferId = "transfer_v4_retry_keeps_far_ranges";
        const string sessionId = "session_v4_retry_keeps_far_ranges";
        const int chunkSize = 4;
        const int chunkCount = 130;
        var payload = Enumerable.Range(0, chunkSize * chunkCount).Select(static value => (byte)(value % 251)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-retry-keeps-far-ranges.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-retry-keeps-far-ranges.bin", payload.Length, chunkSize, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 64,
                ChunkCount = 6,
                DataSegments = Enumerable.Range(64, 6)
                    .Select(index => payload.Skip(index * chunkSize).Take(chunkSize).ToArray())
                    .ToArray(),
            },
            CancellationToken.None);
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 80,
                ChunkCount = 20,
                DataSegments = Enumerable.Range(80, 20)
                    .Select(index => payload.Skip(index * chunkSize).Take(chunkSize).ToArray())
                    .ToArray(),
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
            frame.MissingRanges.Count == 1 &&
            frame.MissingRanges[0].StartChunkIndex == 0 &&
            frame.MissingRanges[0].ChunkCount == 12), timeoutMs: 5000);
        var initialRepairStateCount = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count(frame =>
            frame.MissingRanges.Count == 1 &&
            frame.MissingRanges[0].StartChunkIndex == 0 &&
            frame.MissingRanges[0].ChunkCount == 12);

        await Task.Delay(850);
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 80,
                ChunkCount = 1,
                DataSegments = [payload.Skip(80 * chunkSize).Take(chunkSize).ToArray()],
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count(static frame =>
            frame.MissingRanges.Count == 1 &&
            frame.MissingRanges[0].StartChunkIndex == 0 &&
            frame.MissingRanges[0].ChunkCount == 12) > initialRepairStateCount, timeoutMs: 5000);

        var state = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().First(frame =>
            frame.MissingRanges.Count == 1 &&
            frame.MissingRanges[0].StartChunkIndex == 0 &&
            frame.MissingRanges[0].ChunkCount == 12);
        Assert.Single(state.MissingRanges);
        Assert.Equal(12, state.MissingRanges.Sum(static range => range.ChunkCount));
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_CapsFrontierMissingRangeBurstAtSmallRepairBatch()
    {
        const string transferId = "transfer_v4_repair_cap";
        const string sessionId = "session_v4_repair_cap";
        const int chunkSize = 4;
        const int chunkCount = 130;
        var payload = Enumerable.Range(0, chunkSize * chunkCount).Select(static value => (byte)(value % 251)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-repair-cap.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-repair-cap.bin", payload.Length, chunkSize, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12)));
        var state = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().First(frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12));
        Assert.Equal(12, state.MissingRanges.Sum(static range => range.ChunkCount));
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_FileOnlyFastRepairKillSwitchUsesLegacySmallBurst()
    {
        const string transferId = "transfer_v4_fast_repair_disabled";
        const string sessionId = "session_v4_fast_repair_disabled";
        const string envName = "NLINK_FILETRANSFER_V4_FILE_ONLY_FAST_REPAIR";
        const int chunkSize = 4;
        const int chunkCount = 130;
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "0");
        try
        {
            var payload = Enumerable.Range(0, chunkSize * chunkCount).Select(static value => (byte)(value % 251)).ToArray();
            var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
            using var destination = new NonDisposingMemoryStream();
            using var senderTransport = new LoopbackFileTransferTransport(sessionId);
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
            senderTransport.Connect(receiverTransport);
            using var receiver = new SessionFileTransferService();
            receiver.AttachTransport(receiverTransport);

            var senderSession = await StartInboundV4ReceiverAsync(
                senderTransport,
                receiver,
                transferId,
                sessionId,
                "v4-fast-repair-disabled.bin",
                payload.Length,
                sha256,
                (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

            await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-fast-repair-disabled.bin", payload.Length, chunkSize, sha256), CancellationToken.None);
            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());
            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 3)));

            var state = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().First(frame =>
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 3));
            Assert.Equal(3, state.MissingRanges.Sum(static range => range.ChunkCount));

            var log = ReadOperationalLogText();
            Assert.Contains("repair_interval_ms=750", log, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_WithMixedTransfer_FirstKnownFrontierRepairUsesWiderBatchThenRetryNarrows()
    {
        const string transferId = "transfer_v4_mixed_repair_wide_then_narrow";
        const string sessionId = "session_v4_mixed_repair_wide_then_narrow";
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        const int chunkSize = 4;
        const int chunkCount = 80;
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "1");
        try
        {
            var payload = Enumerable.Range(0, chunkSize * chunkCount).Select(static value => (byte)(value % 251)).ToArray();
            var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
            using var destination = new NonDisposingMemoryStream();
            using var senderTransport = new LoopbackFileTransferTransport(sessionId);
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
            senderTransport.Connect(receiverTransport);
            using var receiver = new SessionFileTransferService();
            receiver.AttachTransport(receiverTransport);
            receiver.SetSessionScreenShareActive(true);

            var senderSession = await StartInboundV4ReceiverAsync(
                senderTransport,
                receiver,
                transferId,
                sessionId,
                "v4-mixed-repair-wide-then-narrow.bin",
                payload.Length,
                sha256,
                (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

            await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-mixed-repair-wide-then-narrow.bin", payload.Length, chunkSize, sha256), CancellationToken.None);
            await senderSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 12,
                    ChunkCount = 1,
                    DataSegments = [payload.Skip(12 * chunkSize).Take(chunkSize).ToArray()],
                },
                CancellationToken.None);

            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12)), timeoutMs: 5000);

            await Task.Delay(850);
            await senderSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 13,
                    ChunkCount = 1,
                    DataSegments = [payload.Skip(13 * chunkSize).Take(chunkSize).ToArray()],
                },
                CancellationToken.None);

            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 3)), timeoutMs: 5000);

            var log = ReadOperationalLogText();
            Assert.Contains("event=filetransfer_v4_repair_retry_narrowed", log, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_WithMixedTransfer_FirstFrontierTailRepairUsesWiderBatch()
    {
        const string transferId = "transfer_v4_mixed_tail_repair_wide";
        const string sessionId = "session_v4_mixed_tail_repair_wide";
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        const int chunkSize = 4;
        const int chunkCount = 80;
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "1");
        try
        {
            var payload = Enumerable.Range(0, chunkSize * chunkCount).Select(static value => (byte)(value % 251)).ToArray();
            var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
            using var destination = new NonDisposingMemoryStream();
            using var senderTransport = new LoopbackFileTransferTransport(sessionId);
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
            senderTransport.Connect(receiverTransport);
            using var receiver = new SessionFileTransferService();
            receiver.AttachTransport(receiverTransport);
            receiver.SetSessionScreenShareActive(true);

            var senderSession = await StartInboundV4ReceiverAsync(
                senderTransport,
                receiver,
                transferId,
                sessionId,
                "v4-mixed-tail-repair-wide.bin",
                payload.Length,
                sha256,
                (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

            await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-mixed-tail-repair-wide.bin", payload.Length, chunkSize, sha256), CancellationToken.None);

            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12)), timeoutMs: 5000);

            var state = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().First(frame =>
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12));
            Assert.Equal(12, state.MissingRanges.Sum(static range => range.ChunkCount));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4SparseReceiver_RejectsNonSparseDestination()
    {
        const string transferId = "transfer_v4_sparse_required";
        const string sessionId = "session_v4_sparse_required";
        var payload = new byte[] { 1, 2, 3, 4 };
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-write-only.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(new WriteOnlySeekableMemoryStream())).ConfigureAwait(false);

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-write-only.bin", payload.Length, chunkSizeBytes: 4, sha256), CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed, timeoutMs: 5000);
        Assert.Equal(FileTransferResultCodes.V4SparseDestinationRequired, receiver.Snapshot.Inbound!.ErrorCode);
        var error = Assert.Single(receiverTransport.SentErrors);
        Assert.Equal(FileTransferResultCodes.V4SparseDestinationRequired, error.ErrorCode);
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferErrorFrameV6);
    }

    [Fact]
    public async Task V4SparseReceiver_InvalidBatchFailsWithV4Error()
    {
        const string transferId = "transfer_v4_invalid_batch";
        const string sessionId = "session_v4_invalid_batch";
        var payload = new byte[] { 1, 2, 3, 4, 5, 6 };
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-invalid-batch.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v4-invalid-batch.bin", payload.Length, chunkSizeBytes: 4, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 1,
                ChunkCount = 1,
                DataSegments = [new byte[] { 5 }],
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed, timeoutMs: 5000);
        Assert.Equal(FileTransferResultCodes.InvalidState, receiver.Snapshot.Inbound!.ErrorCode);
        Assert.Contains(receiverTransport.SentErrors, static error => error.ErrorCode == FileTransferResultCodes.InvalidState);
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferErrorFrameV6);
    }

    [Fact]
    public async Task V4SparseReceiver_RejectsManifestChunkCountAboveProtocolLimit()
    {
        const string transferId = "transfer_v4_manifest_chunk_count_cap";
        const string sessionId = "session_v4_manifest_chunk_count_cap";
        const long fileSizeBytes = FileTransferProtocol.MaxChunkCountV4 + 1L;
        var sha256 = Convert.ToBase64String(SHA256.HashData(Array.Empty<byte>()));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);
        var openWriteCalled = false;

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-manifest-chunk-count-cap.bin",
            fileSizeBytes,
            sha256,
            (_, _) =>
            {
                openWriteCalled = true;
                return Task.FromResult<Stream>(destination);
            });

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v4-manifest-chunk-count-cap.bin", fileSizeBytes, chunkSizeBytes: 1, sha256),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed, timeoutMs: 5000);
        Assert.False(openWriteCalled);
        Assert.Equal(FileTransferResultCodes.InvalidState, receiver.Snapshot.Inbound!.ErrorCode);
        Assert.Equal(0, receiver.Snapshot.Inbound.ChunkCount);
        Assert.Contains(receiverTransport.SentErrors, static error => error.ErrorCode == FileTransferResultCodes.InvalidState);
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferErrorFrameV6);
    }

    [Fact]
    public async Task V4SparseReceiver_RejectsManifestTupleMismatchBeforeOpeningDestination()
    {
        const string transferId = "transfer_v4_manifest_tuple_mismatch";
        const string sessionId = "session_v4_manifest_tuple_mismatch";
        var payload = Enumerable.Range(1, 10).Select(static value => (byte)value).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);
        var openWriteCalled = false;

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-manifest-tuple-mismatch.bin",
            payload.Length,
            sha256,
            (_, _) =>
            {
                openWriteCalled = true;
                return Task.FromResult<Stream>(destination);
            });

        await senderSession.SendAsync(
            new FileTransferManifestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                FileName = "v4-manifest-tuple-mismatch.bin",
                FileSizeBytes = payload.Length,
                ChunkSizeBytes = 4,
                ChunkCount = 2,
                Sha256Base64 = sha256,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed, timeoutMs: 5000);
        Assert.False(openWriteCalled);
        Assert.Equal(FileTransferResultCodes.InvalidState, receiver.Snapshot.Inbound!.ErrorCode);
        Assert.Equal(0, receiver.Snapshot.Inbound.ChunkCount);
        Assert.Contains(receiverTransport.SentErrors, static error => error.ErrorCode == FileTransferResultCodes.InvalidState);
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferErrorFrameV6);
        Assert.Empty(receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>());
    }

    [Fact]
    public async Task V4SparseReceiver_ControlCancelTerminatesInbound()
    {
        const string transferId = "transfer_v4_cancel";
        const string sessionId = "session_v4_cancel";
        var payload = new byte[] { 1, 2, 3, 4 };
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v4-cancel.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination)).ConfigureAwait(false);

        await senderTransport.SendFileTransferCancelAsync(
            new FileTransferCancelV1
            {
                SessionId = sessionId,
                TransferId = transferId,
                Reason = "sender_canceled",
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled, timeoutMs: 5000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);
    }

    private static FileTransferManifestFrameV6 CreateManifest(
        string sessionId,
        string transferId,
        string fileName,
        long fileSizeBytes,
        int chunkSizeBytes,
        string sha256)
        => new()
        {
            SessionId = sessionId,
            TransferId = transferId,
            FileName = fileName,
            FileSizeBytes = fileSizeBytes,
            ChunkSizeBytes = chunkSizeBytes,
            ChunkCount = checked((int)((fileSizeBytes + chunkSizeBytes - 1) / chunkSizeBytes)),
            Sha256Base64 = sha256,
        };

    private static FileTransferManifestFrameV4 CreateRegularNknV4Manifest(
        string sessionId,
        string transferId,
        string fileName,
        long fileSizeBytes,
        int chunkSizeBytes,
        string sha256)
        => new()
        {
            SessionId = sessionId,
            TransferId = transferId,
            FileName = fileName,
            FileSizeBytes = fileSizeBytes,
            ChunkSizeBytes = chunkSizeBytes,
            ChunkCount = checked((int)((fileSizeBytes + chunkSizeBytes - 1) / chunkSizeBytes)),
            Sha256Base64 = sha256,
        };

    private static async Task<IFileTransferDataSession> StartInboundRegularNknV4ReceiverAsync(
        LoopbackFileTransferTransport senderTransport,
        SessionFileTransferService receiver,
        string transferId,
        string sessionId,
        string fileName,
        long fileSizeBytes,
        string sha256,
        FileTransferWriteStreamFactory openWriteStreamAsync)
    {
        const string routeToken = FileTransferRouteResolver.RegularNknV4FastToken;
        await senderTransport.SendFileTransferOfferAsync(
            new FileTransferOfferV2
            {
                SessionId = sessionId,
                TransferId = transferId,
                FileName = fileName,
                FileSizeBytes = fileSizeBytes,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                FileTransferRoute = routeToken,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        await receiver.AcceptIncomingTransferAsync(transferId, openWriteStreamAsync, CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.AwaitingMetadata);

        var logStart = ReadOperationalLogText().Length;
        await senderTransport.SendFileTransferSessionOpenAsync(
            new FileTransferSessionOpenV2
            {
                SessionId = sessionId,
                TransferId = transferId,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                FileTransferRoute = routeToken,
                SessionRole = FileTransferProtocol.SessionRoleSender,
                ChunkSizeBytes = 4,
                InitialPipelineDepth = 1,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_session_opened; direction=inbound", StringComparison.Ordinal) &&
                  ReadOperationalLogTail(logStart).Contains("protocol_version=4", StringComparison.Ordinal),
            timeoutMs: 5000);
        return await senderTransport.OpenFileTransferDataSessionAsync(sessionId, transferId, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<IFileTransferDataSession> StartInboundV4ReceiverAsync(
        LoopbackFileTransferTransport senderTransport,
        SessionFileTransferService receiver,
        string transferId,
        string sessionId,
        string fileName,
        long fileSizeBytes,
        string sha256,
        FileTransferWriteStreamFactory openWriteStreamAsync)
    {
        EnsureAttachedReceiverV6RouteForTest(receiver, senderTransport);
        var routeToken = ResolveRouteToken(senderTransport);
        await senderTransport.SendFileTransferOfferAsync(
            new FileTransferOfferV2
            {
                SessionId = sessionId,
                TransferId = transferId,
                FileName = fileName,
                FileSizeBytes = fileSizeBytes,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                FileTransferRoute = routeToken,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        await receiver.AcceptIncomingTransferAsync(transferId, openWriteStreamAsync, CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.AwaitingMetadata);

        var logStart = ReadOperationalLogText().Length;
        await senderTransport.SendFileTransferSessionOpenAsync(
            new FileTransferSessionOpenV2
            {
                SessionId = sessionId,
                TransferId = transferId,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                FileTransferRoute = routeToken,
                SessionRole = FileTransferProtocol.SessionRoleSender,
                ChunkSizeBytes = 4,
                InitialPipelineDepth = 1,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_receiver_started;", StringComparison.Ordinal), timeoutMs: 5000);
        return await senderTransport.OpenFileTransferDataSessionAsync(sessionId, transferId, CancellationToken.None).ConfigureAwait(false);
    }

    private static void EnsureAttachedReceiverV6RouteForTest(
        SessionFileTransferService receiver,
        LoopbackFileTransferTransport senderTransport)
    {
        var field = typeof(SessionFileTransferService).GetField("transport", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        if (field!.GetValue(receiver) is LoopbackFileTransferTransport transport)
        {
            EnsureMatchingV6RouteForTest(senderTransport, transport);
        }
    }

    private static void EnsureMatchingV6RouteForTest(
        LoopbackFileTransferTransport senderTransport,
        LoopbackFileTransferTransport receiverTransport)
    {
        var route = ResolveV6RouteForTest(senderTransport, receiverTransport);
        ApplyV6RouteForTest(senderTransport, route);
        ApplyV6RouteForTest(receiverTransport, route);
    }

    private static FileTransferRoute ResolveV6RouteForTest(
        LoopbackFileTransferTransport senderTransport,
        LoopbackFileTransferTransport receiverTransport)
    {
        if (senderTransport.IsPostTunaFileFallbackActiveForRouteSelection ||
            receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection)
        {
            return FileTransferRoute.PostTunaFallbackV6;
        }

        if (senderTransport.IsDiagnosticRegularNknV6RouteEnabled ||
            receiverTransport.IsDiagnosticRegularNknV6RouteEnabled)
        {
            return FileTransferRoute.DiagnosticRegularNknV6;
        }

        return FileTransferRoute.DiagnosticRegularNknV6;
    }

    private static void ApplyV6RouteForTest(LoopbackFileTransferTransport transport, FileTransferRoute route)
    {
        transport.IsFileTunaActiveForRouteSelection = false;
        transport.IsPostTunaFileFallbackActiveForRouteSelection = route == FileTransferRoute.PostTunaFallbackV6;
        transport.IsDiagnosticRegularNknV6RouteEnabled = route == FileTransferRoute.DiagnosticRegularNknV6;
    }

    private static string ResolveRouteToken(LoopbackFileTransferTransport transport)
        => FileTransferRouteResolver.Resolve(FileTransferRouteResolverInput.FromTransport(transport)).TelemetryToken;

    private static string ReadV4ReceiverLogSnapshot(int logStart)
        => ReadOperationalLogTail(logStart) + Environment.NewLine + LocalOperationalLog.GetRecentLogText();

    private static string FilterV4ReceiverTransferLog(string logText, string transferId)
        => string.Join(
            Environment.NewLine,
            logText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("transfer_id=" + transferId, StringComparison.Ordinal)));

    private sealed class WriteOnlySeekableMemoryStream : Stream
    {
        private readonly MemoryStream inner = new();

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => inner.Seek(offset, origin);

        public override void SetLength(long value)
            => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
            => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
