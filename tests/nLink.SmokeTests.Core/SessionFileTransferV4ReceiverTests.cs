using System.Security.Cryptography;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferV4ReceiverTests : SessionFileTransferServiceTestBase
{
    [Fact]
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
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.ContiguousCommittedChunkIndex == 0));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV4
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
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame =>
            frame.ContiguousCommittedChunkIndex == 0 &&
            frame.DurableReceivedHighestChunkIndex == 2 &&
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 1)));

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

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 5000);
        Assert.Equal(payload.Length, receiver.Snapshot.Inbound!.BytesTransferred);
        Assert.Equal(3, receiver.Snapshot.Inbound.ChunksTransferred);
        Assert.Contains(receiverTransport.SentDataFrames, static frame => frame is FileTransferCompleteFrameV4);
        Assert.Contains(receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>(), static frame => frame.TerminalReady);
        destination.Seek(0, SeekOrigin.Begin);
        var received = destination.ToArray()[..payload.Length];
        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task V4SparseReceiver_TransportRebind_RepeatsMissingFrontierRequest()
    {
        const string transferId = "transfer_v4_sparse_receiver_rebind_missing";
        const string sessionId = "session_v4_sparse_receiver_rebind_missing";
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
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV4
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
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame =>
            frame.ContiguousCommittedChunkIndex == 0 &&
            frame.DurableReceivedHighestChunkIndex == 2 &&
            frame.MissingRanges.Any(static range => range.StartChunkIndex == 0 && range.ChunkCount == 1)));
        var stateCountBeforeRebind = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Count();

        receiverTransport.RaiseDisconnected();
        receiverTransport.RaiseReconnected();

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Skip(stateCountBeforeRebind).Any(static frame =>
                frame.ContiguousCommittedChunkIndex == 0 &&
                frame.DurableReceivedHighestChunkIndex == 2 &&
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 1)),
            timeoutMs: 5000);
    }

    [Fact]
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
            if (frame is FileTransferStateFrameV4 { TerminalReady: true })
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
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.ContiguousCommittedChunkIndex == 0));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV4
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = [payload],
            },
            CancellationToken.None);

        await WaitUntilAsync(() => terminalStateSendStarted.Task.IsCompleted, timeoutMs: 5000);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferCompleteFrameV4>().Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 5000);

        Assert.Contains(receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>(), static frame => frame.TerminalReady);
        Assert.Contains(receiverTransport.SentDataFrames, static frame => frame is FileTransferCompleteFrameV4);
        var log = ReadOperationalLogText();
        Assert.Contains("event=filetransfer_v4_receiver_completed_chunks;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_finalize_started;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_sparse_hash_started;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_sparse_hash_completed;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_complete_send_started;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_complete_sent;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_terminal_ready_state_send_deferred;", log, StringComparison.Ordinal);
    }

    [Fact]
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

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

        var initialState = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>()
            .First(frame => frame.CreditUntilChunkIndexExclusive > 0);
        Assert.Equal(expectedCreditChunkCount, initialState.CreditUntilChunkIndexExclusive);
        Assert.Equal(0, initialState.ContiguousCommittedChunkIndex);
        Assert.Equal(-1, initialState.DurableReceivedHighestChunkIndex);
    }

    [Fact]
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

            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

            var initialState = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>()
                .First(frame => frame.CreditUntilChunkIndexExclusive > 0);
            Assert.Equal(96, initialState.CreditUntilChunkIndexExclusive);

            var log = ReadOperationalLogText();
            Assert.Contains("event=filetransfer_v4_mixed_enabled;", log, StringComparison.Ordinal);
            Assert.Contains("credit_window_chunks=96", log, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact]
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

            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

            var initialState = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>()
                .First(frame => frame.CreditUntilChunkIndexExclusive > 0);
            Assert.Equal(24, initialState.CreditUntilChunkIndexExclusive);

            var log = ReadOperationalLogText();
            Assert.Contains("event=filetransfer_v4_mixed_enabled;", log, StringComparison.Ordinal);
            Assert.Contains("credit_window_chunks=24", log, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact]
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
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV4
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

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>()
            .Any(frame =>
                frame.ContiguousCommittedChunkIndex == 0 &&
                frame.DurableReceivedHighestChunkIndex >= expectedCreditChunkCount + 1));

        var durableAheadState = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>()
            .Last(frame =>
                frame.ContiguousCommittedChunkIndex == 0 &&
                frame.DurableReceivedHighestChunkIndex >= expectedCreditChunkCount + 1);
        Assert.True(
            durableAheadState.CreditUntilChunkIndexExclusive > expectedCreditChunkCount,
            $"Expected sparse durable progress to advance credit beyond {expectedCreditChunkCount}, got {durableAheadState.CreditUntilChunkIndexExclusive}.");

        var log = ReadOperationalLogText();
        Assert.Contains("frontier_window_credit_capped=0", log, StringComparison.Ordinal);
    }

    [Fact]
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
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.ContiguousCommittedChunkIndex == 0));

        var outOfOrderBatch = new FileTransferChunkBatchFrameV4
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
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>()
            .Any(static frame => frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 1)));

        var stateCountBeforeDuplicate = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Count();
        await senderSession.SendAsync(outOfOrderBatch, CancellationToken.None);
        await Task.Delay(150);
        Assert.Equal(stateCountBeforeDuplicate, receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Count());

        var missingRangeStateCountBeforeRetry = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>()
            .Count(static frame => frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 1));
        await Task.Delay(850);
        await senderSession.SendAsync(outOfOrderBatch, CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>()
            .Count(static frame => frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 1)) > missingRangeStateCountBeforeRetry);

        var log = ReadOperationalLogText();
        Assert.Contains("event=filetransfer_v4_repair_requested", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_repair_suppressed; direction=receiver", log, StringComparison.Ordinal);
    }

    [Fact]
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
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.MissingRanges.Count == 0));
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV4
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
            new FileTransferChunkBatchFrameV4
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 64,
                ChunkCount = 1,
                DataSegments = [payload.Skip(64 * chunkSize).Take(chunkSize).ToArray()],
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12)));

        var stateCountBeforeRepairFill = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Count();
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV4
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

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Count() > stateCountBeforeRepairFill);
        var stateAfterPartialFill = receiverTransport.SentDataFrames
            .OfType<FileTransferStateFrameV4>()
            .Skip(stateCountBeforeRepairFill)
            .First();
        Assert.Equal(3, stateAfterPartialFill.ContiguousCommittedChunkIndex);

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 3 && range.ChunkCount == 12)), timeoutMs: 4000);

        var log = ReadOperationalLogText();
        Assert.Contains("overlap_repair_request_key=", log, StringComparison.Ordinal);
    }

    [Fact]
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
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame =>
            frame.ContiguousCommittedChunkIndex == 0 &&
            frame.DurableReceivedHighestChunkIndex == -1 &&
            frame.MissingRanges.Count == 0));

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 3)), timeoutMs: 4000);

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

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame =>
            frame.ContiguousCommittedChunkIndex == 1), timeoutMs: 4000);

        var log = ReadOperationalLogText();
        Assert.Contains("event=filetransfer_v4_repair_chunk_observed", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_frontier_stall_missing_range_due", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_frontier_stall_missing_range_filled", log, StringComparison.Ordinal);
    }

    [Fact]
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
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.MissingRanges.Count == 0));
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12)), timeoutMs: 5000);

        var log = ReadOperationalLogText();
        Assert.Contains("event=filetransfer_v4_frontier_stall_missing_range_due", log, StringComparison.Ordinal);
        Assert.Contains("requested_chunk_count=12", log, StringComparison.Ordinal);
    }

    [Fact]
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
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any());
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV4
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

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12)), timeoutMs: 4000);
        var state = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().First(frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12));

        var log = ReadOperationalLogText();
        Assert.Contains("event=filetransfer_v4_repair_requested", log, StringComparison.Ordinal);
        Assert.Equal(12, state.MissingRanges.Sum(static range => range.ChunkCount));
    }

    [Fact]
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
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any());
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV4
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
            new FileTransferChunkBatchFrameV4
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

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame =>
            frame.MissingRanges.Count == 1 &&
            frame.MissingRanges[0].StartChunkIndex == 0 &&
            frame.MissingRanges[0].ChunkCount == 12), timeoutMs: 5000);
        var initialRepairStateCount = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Count(frame =>
            frame.MissingRanges.Count == 1 &&
            frame.MissingRanges[0].StartChunkIndex == 0 &&
            frame.MissingRanges[0].ChunkCount == 12);

        await Task.Delay(850);
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV4
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 80,
                ChunkCount = 1,
                DataSegments = [payload.Skip(80 * chunkSize).Take(chunkSize).ToArray()],
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Count(static frame =>
            frame.MissingRanges.Count == 1 &&
            frame.MissingRanges[0].StartChunkIndex == 0 &&
            frame.MissingRanges[0].ChunkCount == 12) > initialRepairStateCount, timeoutMs: 5000);

        var state = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().First(frame =>
            frame.MissingRanges.Count == 1 &&
            frame.MissingRanges[0].StartChunkIndex == 0 &&
            frame.MissingRanges[0].ChunkCount == 12);
        Assert.Single(state.MissingRanges);
        Assert.Equal(12, state.MissingRanges.Sum(static range => range.ChunkCount));
    }

    [Fact]
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
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any());
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12)));
        var state = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().First(frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12));
        Assert.Equal(12, state.MissingRanges.Sum(static range => range.ChunkCount));
    }

    [Fact]
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
            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any());
            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame =>
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 3)));

            var state = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().First(frame =>
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

    [Fact]
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
                new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 12,
                    ChunkCount = 1,
                    DataSegments = [payload.Skip(12 * chunkSize).Take(chunkSize).ToArray()],
                },
                CancellationToken.None);

            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame =>
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12)), timeoutMs: 5000);

            await Task.Delay(850);
            await senderSession.SendAsync(
                new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 13,
                    ChunkCount = 1,
                    DataSegments = [payload.Skip(13 * chunkSize).Take(chunkSize).ToArray()],
                },
                CancellationToken.None);

            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame =>
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 3)), timeoutMs: 5000);

            var log = ReadOperationalLogText();
            Assert.Contains("event=filetransfer_v4_repair_retry_narrowed", log, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact]
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

            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame =>
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12)), timeoutMs: 5000);

            var state = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().First(frame =>
                frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 12));
            Assert.Equal(12, state.MissingRanges.Sum(static range => range.ChunkCount));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact]
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
        var error = Assert.Single(receiverTransport.SentDataFrames.OfType<FileTransferErrorFrameV4>());
        Assert.Equal(FileTransferResultCodes.V4SparseDestinationRequired, error.ErrorCode);
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
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any());
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV4
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
        Assert.Contains(receiverTransport.SentDataFrames.OfType<FileTransferErrorFrameV4>(), static frame => frame.ErrorCode == FileTransferResultCodes.InvalidState);
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
        Assert.Contains(receiverTransport.SentDataFrames.OfType<FileTransferErrorFrameV4>(), static frame => frame.ErrorCode == FileTransferResultCodes.InvalidState);
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
            new FileTransferManifestFrameV4
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
        Assert.Contains(receiverTransport.SentDataFrames.OfType<FileTransferErrorFrameV4>(), static frame => frame.ErrorCode == FileTransferResultCodes.InvalidState);
        Assert.Empty(receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>());
    }

    [Fact]
    public async Task V4SparseReceiver_CancelFrameTerminatesInbound()
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

        await senderSession.SendAsync(
            new FileTransferCancelFrameV4
            {
                SessionId = sessionId,
                TransferId = transferId,
                Reason = "sender_canceled",
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled, timeoutMs: 5000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);
    }

    private static FileTransferManifestFrameV4 CreateManifest(
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
        await senderTransport.SendFileTransferOfferAsync(
            new FileTransferOfferV2
            {
                SessionId = sessionId,
                TransferId = transferId,
                FileName = fileName,
                FileSizeBytes = fileSizeBytes,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
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
                SessionRole = FileTransferProtocol.SessionRoleSender,
                ChunkSizeBytes = 4,
                InitialPipelineDepth = 1,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_receiver_started;", StringComparison.Ordinal), timeoutMs: 5000);
        return await senderTransport.OpenFileTransferDataSessionAsync(sessionId, transferId, CancellationToken.None).ConfigureAwait(false);
    }

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
