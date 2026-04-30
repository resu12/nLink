using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferV4SenderTests : SessionFileTransferServiceTestBase
{
    [Fact]
    public async Task V4FileOnlyTransfer_CompletesEndToEnd_WithSparseReceiverIntegrity()
    {
        const string transferId = "transfer_v4_sender_e2e";
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_e2e");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_e2e");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-e2e.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 15000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var sessionOpen = Assert.Single(senderTransport.SentSessionOpens);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, sessionOpen.ProtocolVersion);
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferManifestFrameV4);
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferChunkBatchFrameV4);
        Assert.Contains(receiverTransport.SentDataFrames, static frame => frame is FileTransferStateFrameV4);
        Assert.Contains(receiverTransport.SentDataFrames, static frame => frame is FileTransferCompleteFrameV4);
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferRequestChunksFrameV2);
    }

    [Fact]
    public async Task V4Sender_MissingRangesSchedulePriorityRepair_AndComplete()
    {
        const string transferId = "transfer_v4_sender_repair_priority";
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 239)).ToArray();
        var droppedFirstBatch = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_priority");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_priority");
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, _) =>
        {
            if (frame is FileTransferChunkBatchFrameV4 { StartChunkIndex: 0 } &&
                Interlocked.Exchange(ref droppedFirstBatch, 1) == 0)
            {
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-repair-priority.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(1, droppedFirstBatch);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        Assert.True(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Count(static batch => batch.StartChunkIndex == 0) >= 2);
        Assert.Contains(
            senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>(),
            static batch => batch.StartChunkIndex == 0 &&
                batch.BatchProfile == "v4_repair_21k" &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.BulkOnly);
        await WaitUntilAsync(
            () => LocalOperationalLog.GetRecentLogText().Contains($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", StringComparison.Ordinal),
            timeoutMs: 5000);
        var log = LocalOperationalLog.GetRecentLogText();
        Assert.Contains($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_v4_repair_sent; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_v4_chunk_batch_sent; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_mode=bulk_only", log, StringComparison.Ordinal);
        Assert.Contains("repair_request_key=", log, StringComparison.Ordinal);
        Assert.Contains("repair_send=1", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_SuppressesDuplicateMissingRangeRepairKeys()
    {
        const string transferId = "transfer_v4_sender_repair_dedupe";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_dedupe");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_dedupe");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-repair-dedupe.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV4>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV4
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        var repairState = new FileTransferStateFrameV4
        {
            SessionId = offer.SessionId,
            TransferId = transferId,
            Epoch = 2,
            ContiguousCommittedChunkIndex = 0,
            DurableReceivedHighestChunkIndex = 8,
            CreditUntilChunkIndexExclusive = 16,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
            BytesCommitted = 0,
        };
        await receiverSession.SendAsync(repairState, CancellationToken.None);
        await receiverSession.SendAsync(repairState, CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_repair_dedupe", StringComparison.Ordinal), timeoutMs: 5000);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_repair_suppressed; transfer_id=transfer_v4_sender_repair_dedupe", StringComparison.Ordinal), timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Equal(1, CountOccurrences(log, "event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_repair_dedupe"));
        Assert.Contains("reason=", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_RetriedMissingRange_EscalatesRepairDelivery()
    {
        const string transferId = "transfer_v4_sender_repair_escalates";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_escalates");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_escalates");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-repair-escalates.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV4>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV4
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        var repairState = new FileTransferStateFrameV4
        {
            SessionId = offer.SessionId,
            TransferId = transferId,
            Epoch = 2,
            ContiguousCommittedChunkIndex = 0,
            DurableReceivedHighestChunkIndex = 8,
            CreditUntilChunkIndexExclusive = 16,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
            BytesCommitted = 0,
        };

        await receiverSession.SendAsync(repairState, CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(static batch =>
                batch.BatchProfile == "v4_repair_21k" &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.BulkOnly),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("repair_delivery_mode=bulk_only", StringComparison.Ordinal),
            timeoutMs: 5000);

        await Task.Delay(900);
        await receiverSession.SendAsync(repairState with { Epoch = 3 }, CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(static batch =>
                batch.BatchProfile == "v4_repair_21k" &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("repair_delivery_mode=bulk_only", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_mode=control_bulk_escalated", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_escalation_reason=frontier_not_advanced", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_MaxBatchSegmentsEnvironment_CapsNormalBatches()
    {
        const string transferId = "transfer_v4_sender_batch_cap";
        const string envName = "NLINK_FILETRANSFER_V4_MAX_BATCH_SEGMENTS";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        Environment.SetEnvironmentVariable(envName, "2");
        try
        {
            var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 241)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_batch_cap");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_batch_cap");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-batch-cap.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
            var offer = senderTransport.SentOffers.Single();
            await receiverTransport.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
            await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV4>().Any(), timeoutMs: 5000);

            var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
            await receiverSession.SendAsync(
                new FileTransferStateFrameV4
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 16,
                    MissingRanges = [],
                    BytesCommitted = 0,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Sum(static batch => batch.DataSegments.Count) >= 12,
                timeoutMs: 5000);

            var batches = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().ToArray();
            Assert.NotEmpty(batches);
            Assert.All(batches, static batch => Assert.InRange(batch.DataSegments.Count, 1, 2));
            Assert.Contains(batches, static batch => batch.DataSegments.Count == 2 && batch.BatchProfile == "v4_default_21k_2x");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact]
    public async Task V4Sender_SuppressesSameMissingRange_WhenDurableHighestAdvances()
    {
        const string transferId = "transfer_v4_sender_repair_stable_key";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 229)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_stable_key");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_stable_key");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-repair-stable-key.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV4>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV4
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferStateFrameV4
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_repair_stable_key", StringComparison.Ordinal), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferStateFrameV4
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 3,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 12,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_repair_suppressed; transfer_id=transfer_v4_sender_repair_stable_key", StringComparison.Ordinal), timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Equal(1, CountOccurrences(log, "event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_repair_stable_key"));
        Assert.Contains("event=filetransfer_v4_repair_suppressed; transfer_id=transfer_v4_sender_repair_stable_key", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_AppliesRepair_FromStaleSameFrontierMissingRanges()
    {
        const string transferId = "v4_stale_same";
        const string sessionId = "session_v4_stale_same";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 211)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-stale-missing-range.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV4>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV4
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 10,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferStateFrameV4
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 9,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains($"event=filetransfer_v4_stale_state_missing_ranges_applied; transfer_id={transferId}", StringComparison.Ordinal), timeoutMs: 5000);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", StringComparison.Ordinal), timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains($"event=filetransfer_v4_state_received; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("stale=1; applied=0", log, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_v4_stale_state_missing_ranges_applied; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("reason=same_frontier", log, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_SuppressesRepair_FromStaleMovedFrontierMissingRanges()
    {
        const string transferId = "v4_stale_moved";
        const string sessionId = "session_v4_stale_moved";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 211)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-stale-moved-frontier-missing-range.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV4>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV4
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 10,
                ContiguousCommittedChunkIndex = 1,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 21 * 1024,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferStateFrameV4
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 9,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains($"event=filetransfer_v4_stale_state_missing_ranges_suppressed; transfer_id={transferId}", StringComparison.Ordinal), timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains($"event=filetransfer_v4_state_received; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("stale=1; applied=0", log, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_v4_stale_state_missing_ranges_suppressed; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("reason=frontier_moved", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_stale_state_missing_ranges_applied; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_PrioritizesFrontierMissingRange_AndKeepsFarHoles()
    {
        const string transferId = "transfer_v4_sender_frontier_first";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 223)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_frontier_first");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_frontier_first");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-frontier-first.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV4>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV4
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(static frame =>
                frame.StartChunkIndex <= 10 &&
                frame.StartChunkIndex + frame.ChunkCount > 10),
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferStateFrameV4
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 20,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges =
                [
                    new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 3 },
                    new FileTransferRangeV4 { StartChunkIndex = 10, ChunkCount = 4 },
                ],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_frontier_first", StringComparison.Ordinal), timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_frontier_first", log, StringComparison.Ordinal);
        Assert.Contains("range_count=2; requested_chunk_count=7; scheduled_chunk_count=7", log, StringComparison.Ordinal);
        Assert.Contains("first_start_chunk_index=0; last_end_chunk_exclusive=14", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
