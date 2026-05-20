using System.Security.Cryptography;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferV6RuntimeTests : SessionFileTransferServiceTestBase
{
    [Theory]
    [InlineData(64_000)]
    [InlineData(1_000_000)]
    [InlineData(3_000_000)]
    public async Task V6ReceiverDrivenTransfer_CompletesWithIntegrity(int payloadSize)
    {
        var transferId = $"transfer_v6_runtime_{payloadSize}";
        var sessionId = $"session_v6_runtime_{payloadSize}";
        var payload = Enumerable.Range(0, payloadSize).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-runtime.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20_000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        Assert.All(senderTransport.SentDataFrames, static frame =>
            Assert.True(FileTransferProtocol.IsV6DataFrame(frame), $"Expected V6 data frame, got {frame.Type}."));
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferManifestFrameV6);
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferChunkBatchFrameV6);
        Assert.Contains(receiverTransport.SentDataFrames, static frame => frame is FileTransferReceiverStateFrameV6);
        Assert.Contains(receiverTransport.SentCompletes, complete =>
            complete.TransferId == transferId &&
            complete.FileSizeBytes == payload.Length);
    }

    [Fact]
    public async Task V6DefaultTransportProfile_DoesNotSelectPrimaryRegularNknBulkSparseRuntime()
    {
        const string transferId = "transfer_v6_default_profile_runtime";
        const string sessionId = "session_v6_default_profile_runtime";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 128_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-default-profile.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 15_000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        Assert.All(senderTransport.SentDataFrames, static frame =>
            Assert.True(FileTransferProtocol.IsV6DataFrame(frame), $"Expected V6 data frame, got {frame.Type}."));
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferChunkBatchFrameV6);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_bridge_recovery_policy_selected; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("runtime_profile=Default", logTail, StringComparison.Ordinal);
        Assert.Contains("selection_reason=transport_profile_not_conservative", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_primary_regular_nkn_bulk_v6_selected", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Sender_IgnoresCreditWithoutExplicitRequestedRanges()
    {
        const string transferId = "transfer_v6_sender_explicit_ranges";
        const string sessionId = "session_v6_sender_explicit_ranges";
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(
            sender,
            senderTransport,
            receiverTransport,
            transferId,
            payload);
        var sentBeforeCreditOnly = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 8,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await Task.Delay(300);

        Assert.Equal(sentBeforeCreditOnly, senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count());

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 8,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 2, ChunkCount = 2 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                batch.StartChunkIndex == 2 &&
                batch.ChunkCount == 2),
            timeoutMs: 5000);
        Assert.DoesNotContain(
            senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>(),
            static batch => batch.StartChunkIndex < 2);
    }

    [Fact]
    public async Task V6Sender_FrontierRequestAdvancesRemoteFrontierWhenReceiverStateLags()
    {
        const string transferId = "transfer_v6_sender_frontier_request_no_ack";
        const string sessionId = "session_v6_sender_frontier_request_no_ack";
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(
            sender,
            senderTransport,
            receiverTransport,
            transferId,
            payload);

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 0,
                RepairRequestId = "frontier:ahead:not-ack",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 12, ChunkCount = 1 }],
                Priority = "frontier",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                batch.StartChunkIndex == 12 &&
                batch.ChunkCount == 1 &&
                batch.Priority == "frontier"),
            timeoutMs: 5000);

        var snapshot = sender.Snapshot.Outbound;
        Assert.NotNull(snapshot);
        Assert.True(snapshot.ChunksTransferred >= 12);
        Assert.True(snapshot.BytesTransferred >= 12L * snapshot.ChunkSizeBytes);
        Assert.Equal(snapshot.BytesTransferred, snapshot.BytesAcknowledgedByReceiver);
    }

    [Fact]
    public async Task V6Sender_DuplicateReceiverStateCanAdvanceVisibleReceiverProgress()
    {
        const string transferId = "transfer_v6_sender_duplicate_state_progress";
        const string sessionId = "session_v6_sender_duplicate_state_progress";
        const int chunkSizeBytes = 4096;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(
            sender,
            senderTransport,
            receiverTransport,
            transferId,
            payload,
            chunkSizeBytes);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 8,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.BytesAcknowledgedByReceiver == 0,
            timeoutMs: 5000);

        var receiverWrittenBytes = 16L * chunkSizeBytes;
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 15,
                CreditUntilChunkIndexExclusive = 8,
                MissingRanges = [],
                BytesCommitted = receiverWrittenBytes,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.ProgressBytes >= receiverWrittenBytes,
            timeoutMs: 5000);
        var snapshot = sender.Snapshot.Outbound;
        Assert.NotNull(snapshot);
        Assert.True(snapshot!.BytesAcknowledgedByReceiver >= receiverWrittenBytes);
        Assert.True(snapshot.ProgressBytes >= receiverWrittenBytes);
    }

    [Fact]
    public async Task V6Sender_FrontierStalledReceiverStateKeepsNormalWindowDuringHealthyStreaming()
    {
        const string transferId = "transfer_v6_sender_frontier_state_healthy_normal";
        const string sessionId = "session_v6_sender_frontier_state_healthy_normal";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-frontier-state-preempts-normal.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        Assert.NotNull(await sender.PauseTransferAsync(transferId, "stage_requests", CancellationToken.None));

        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);
        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        var baselineBatchCount = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 60,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 40, ChunkCount = 10 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 80,
                CreditUntilChunkIndexExclusive = 60,
                MissingRanges =
                [
                    new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 },
                    new FileTransferRangeV4 { StartChunkIndex = 40, ChunkCount = 10 },
                ],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await Task.Delay(300);
        Assert.Equal(baselineBatchCount, senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count());
        Assert.DoesNotContain(
            "event=filetransfer_v6_receiver_state_frontier_preempted_normal_pipeline",
            ReadOperationalLogTail(logStart),
            StringComparison.Ordinal);

        Assert.NotNull(await sender.ResumeTransferAsync(transferId, "send_normal_requests", CancellationToken.None));
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>()
                .Skip(baselineBatchCount)
                .Any(static batch =>
                    batch.StartChunkIndex == 0 &&
                    batch.ChunkCount == 1),
            timeoutMs: 5000);

        await Task.Delay(500);

        var batchesAfterReceiverState = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .Skip(baselineBatchCount)
            .ToList();
        Assert.Contains(batchesAfterReceiverState, static batch =>
            batch.StartChunkIndex == 0 &&
            batch.ChunkCount == 1 &&
            batch.Priority is null);
        Assert.DoesNotContain(batchesAfterReceiverState, static batch => batch.Priority == "frontier");
    }

    [Fact]
    public async Task V6Sender_NormalReceiverStateDoesNotChaseFarTailBeyondRegularNknSendAheadLimit()
    {
        const string transferId = "transfer_v6_sender_normal_send_ahead_limited";
        const string sessionId = "session_v6_sender_normal_send_ahead_limited";
        const int regularNknSendAheadLimitChunks = 512;
        var payload = new byte[20 * 1024 * 1024];
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(
            sender,
            senderTransport,
            receiverTransport,
            transferId,
            payload,
            chunkSizeBytes: 8192);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 1800,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1800 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>()
                .Any(static batch => batch.Priority is null),
            timeoutMs: 5000);
        await Task.Delay(1000);

        var normalBatches = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .Where(static batch => batch.Priority is null)
            .ToList();
        Assert.NotEmpty(normalBatches);
        Assert.DoesNotContain(normalBatches, batch =>
            batch.StartChunkIndex + batch.ChunkCount > regularNknSendAheadLimitChunks);
    }

    [Fact]
    public async Task V6Sender_RegularNknNormalReceiverStateWaitsForBacklogLowWatermarkBeforeTailRefill()
    {
        const string transferId = "transfer_v6_sender_normal_refill_low_watermark";
        const string sessionId = "session_v6_sender_normal_refill_low_watermark";
        const int regularNknSendAheadLimitChunks = 512;
        const int regularNknObservedBacklogChunks = 384;
        var payload = new byte[20 * 1024 * 1024];
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(
            sender,
            senderTransport,
            receiverTransport,
            transferId,
            payload,
            chunkSizeBytes: 8192);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 1800,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1800 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>()
                .Where(static batch => batch.Priority is null)
                .Sum(static batch => batch.ChunkCount) >= regularNknObservedBacklogChunks,
            timeoutMs: 5000);
        await Task.Delay(300);

        var baselineNormalBatches = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .Where(static batch => batch.Priority is null)
            .ToList();
        Assert.DoesNotContain(baselineNormalBatches, batch =>
            batch.StartChunkIndex + batch.ChunkCount > regularNknSendAheadLimitChunks);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 9,
                DurableReceivedHighestChunkIndex = regularNknSendAheadLimitChunks - 1,
                CreditUntilChunkIndexExclusive = 1809,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 9, ChunkCount = 1800 }],
                BytesCommitted = 9L * FileTransferProtocol.MaxChunkRawBytes,
            },
            CancellationToken.None);
        await Task.Delay(750);

        var batchesAfterRefillState = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .Where(static batch => batch.Priority is null)
            .ToList();
        Assert.DoesNotContain(batchesAfterRefillState, batch =>
            batch.StartChunkIndex >= regularNknSendAheadLimitChunks);
    }

    [Fact]
    public async Task V6Sender_RecoveredRegularNknProgressClearsFrontierPressure()
    {
        const string transferId = "transfer_v6_sender_frontier_pressure_stable_refill";
        const string sessionId = "session_v6_sender_frontier_pressure_stable_refill";
        const int pressureSendAheadLimitChunks = 128;
        var logStart = GetOperationalLogLength();
        var payload = new byte[16 * 1024 * 1024];
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(
            sender,
            senderTransport,
            receiverTransport,
            transferId,
            payload);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 3,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 3 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>()
                .Where(static batch => batch.Priority is null)
                .Sum(static batch => batch.ChunkCount) >= 3,
            timeoutMs: 5000);

        senderTransport.RequestAllDataSessionHandoffs(
            "receive_stall_recovery",
            FileTransferTransportHandoffKind.RegularNknRecovery,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);

        var probe = await ReceiveProbeAsync(receiverSession);
        var probeFrame = Assert.IsType<FileTransferTransportProbeFrameV6>(probe.Frame);
        await receiverTransport.SendFileTransferTransportProbeAsync(
            new FileTransferTransportProbeV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = probeFrame.TransferId,
                TransportEpoch = probeFrame.TransportEpoch,
                ProbeId = probeFrame.ProbeId,
                TargetTransport = probeFrame.TargetTransport,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered", StringComparison.Ordinal),
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 5,
                CreditUntilChunkIndexExclusive = 4,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
                TransportEpoch = probeFrame.TransportEpoch,
                Priority = "frontier",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>()
                .Any(static batch => batch.StartChunkIndex == 0 && batch.Priority == "frontier"),
            timeoutMs: 5000);

        var batchCountBeforePressureNormal = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .Count();
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 3,
                ContiguousCommittedChunkIndex = 6,
                DurableReceivedHighestChunkIndex = 5,
                CreditUntilChunkIndexExclusive = 700,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 6, ChunkCount = 650 }],
                BytesCommitted = 6L * FileTransferProtocol.MaxChunkRawBytes,
                TransportEpoch = probeFrame.TransportEpoch,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforePressureNormal)
                .Any(static batch => batch.Priority is null),
            timeoutMs: 5000);
        await Task.Delay(750);

        var pressureNormalBatches = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .Skip(batchCountBeforePressureNormal)
            .Where(static batch => batch.Priority is null)
            .ToList();
        Assert.NotEmpty(pressureNormalBatches);
        Assert.Contains(pressureNormalBatches, batch =>
            batch.StartChunkIndex + batch.ChunkCount > 6 + pressureSendAheadLimitChunks);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_regular_nkn_frontier_pressure_entered", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_regular_nkn_frontier_pressure_cleared", logTail, StringComparison.Ordinal);
        Assert.Contains("reason=receiver_state_progress", logTail, StringComparison.Ordinal);
        Assert.Contains("send_ahead_limit_chunks=128", logTail, StringComparison.Ordinal);
        Assert.Contains("refill_low_watermark_chunks=96", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Sender_NormalRegularNknProgressClearsFrontierPressure()
    {
        const string transferId = "transfer_v6_sender_frontier_pressure_normal_progress";
        const string sessionId = "session_v6_sender_frontier_pressure_normal_progress";
        const int pressureSendAheadLimitChunks = 128;
        var logStart = GetOperationalLogLength();
        var payload = new byte[20 * 1024 * 1024];
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(
            sender,
            senderTransport,
            receiverTransport,
            transferId,
            payload);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 5,
                CreditUntilChunkIndexExclusive = 12,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
                BytesCommitted = 0,
                RecoveryMode = "regular_nkn_frontier_stall_control_bulk",
                Priority = "frontier",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_regular_nkn_frontier_pressure_entered", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>()
                .Any(static batch => batch.StartChunkIndex == 0 && batch.Priority == "frontier"),
            timeoutMs: 5000);

        var batchCountBeforeProgress = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .Count();
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 12,
                DurableReceivedHighestChunkIndex = 11,
                CreditUntilChunkIndexExclusive = 780,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 12, ChunkCount = 768 }],
                BytesCommitted = 12L * FileTransferProtocol.MaxChunkRawBytes,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_regular_nkn_frontier_pressure_cleared", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforeProgress)
                .Any(batch => batch.Priority is null &&
                              batch.StartChunkIndex + batch.ChunkCount > 12 + pressureSendAheadLimitChunks),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_regular_nkn_frontier_pressure_cleared", logTail, StringComparison.Ordinal);
        Assert.Contains("reason=receiver_state_progress", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Sender_RegularNknDegradedProfileKeepsStableTailRefillAndClearsOnProgress()
    {
        const string transferId = "transfer_v6_sender_regular_nkn_degraded_profile";
        const string sessionId = "session_v6_sender_regular_nkn_degraded_profile";
        var logStart = GetOperationalLogLength();
        var payload = new byte[12 * 1024 * 1024];
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(
            sender,
            senderTransport,
            receiverTransport,
            transferId,
            payload);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 64 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>()
                .Where(static batch => batch.Priority is null)
                .Sum(static batch => batch.ChunkCount) >= 64,
            timeoutMs: 5000);

        await Task.Delay(3600);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 64 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 3,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 64 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        var batchCountBeforeDegradedState = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 4,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 328,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 128, ChunkCount = 200 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_v6_regular_nkn_degraded_profile_entered",
                StringComparison.Ordinal),
            timeoutMs: 5000);
        await Task.Delay(500);

        Assert.Contains(
            senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforeDegradedState),
            static batch => batch.Priority is null && batch.StartChunkIndex >= 128);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 5,
                ContiguousCommittedChunkIndex = 16,
                DurableReceivedHighestChunkIndex = 127,
                CreditUntilChunkIndexExclusive = 400,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 200, ChunkCount = 80 }],
                BytesCommitted = 16L * FileTransferProtocol.MaxChunkRawBytes,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                batch.Priority is null &&
                batch.StartChunkIndex >= 200),
            timeoutMs: 5000);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_v6_regular_nkn_degraded_profile_cleared",
                StringComparison.Ordinal),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("send_ahead_limit_chunks=512", logTail, StringComparison.Ordinal);
        Assert.Contains("refill_low_watermark_chunks=384", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_regular_nkn_degraded_profile_cleared", logTail, StringComparison.Ordinal);
        Assert.Contains("reason=receiver_state_progress", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Sender_RegularNknFrontierStallUsesBulkOnlyBeforeEscalation()
    {
        const string transferId = "transfer_v6_sender_frontier_stall_bulk_only";
        const string sessionId = "session_v6_sender_frontier_stall_bulk_only";
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-frontier-state-bulk-only.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);

        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);
        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 0,
                RepairRequestId = "frontier:bulk-only",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "regular_nkn_frontier_stall",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                batch.StartChunkIndex == 0 &&
                batch.ChunkCount == 1 &&
                batch.Priority == "frontier"),
            timeoutMs: 5000);

        var frontierBatch = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .First(static batch => batch.StartChunkIndex == 0 && batch.Priority == "frontier");
        Assert.False(frontierBatch.ForceRegularNknBulk);
    }

    [Fact]
    public void V6PeerLiveness_AllowsAuthenticatedFeedbackAndDataAcrossTransportOrigins()
    {
        var method = typeof(SessionFileTransferService).GetMethod(
            "IsAuthoritativeV6PeerLivenessFrame",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        static FileTransferReceivedDataFrame Received(FileTransferDataFrame frame, FileTransferTransportKind transportKind, string lane)
            => new(frame, transportKind, lane, DateTimeOffset.UtcNow);

        bool IsAuthoritative(FileTransferReceivedDataFrame received)
            => Assert.IsType<bool>(method!.Invoke(null, [received]));

        Assert.True(IsAuthoritative(Received(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = "session_v6_liveness_bulk",
                TransferId = "transfer_v6_liveness_bulk",
                Epoch = 1,
            },
            FileTransferTransportKind.RegularNkn,
            "bulk")));
        Assert.True(IsAuthoritative(Received(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = "session_v6_liveness_bulk",
                TransferId = "transfer_v6_liveness_bulk",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
            },
            FileTransferTransportKind.RegularNkn,
            "control_bulk")));
        Assert.True(IsAuthoritative(Received(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = "session_v6_liveness_bulk",
                TransferId = "transfer_v6_liveness_bulk",
                Epoch = 2,
            },
            FileTransferTransportKind.Tuna,
            "tuna")));
        Assert.True(IsAuthoritative(Received(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = "session_v6_liveness_bulk",
                TransferId = "transfer_v6_liveness_bulk",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
            },
            FileTransferTransportKind.Tuna,
            "tuna")));
        Assert.True(IsAuthoritative(Received(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = "session_v6_liveness_bulk",
                TransferId = "transfer_v6_liveness_bulk",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
            },
            FileTransferTransportKind.Unknown,
            "unknown")));
        Assert.True(IsAuthoritative(Received(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = "session_v6_liveness_bulk",
                TransferId = "transfer_v6_liveness_bulk",
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = [new byte[] { 1 }],
            },
            FileTransferTransportKind.RegularNkn,
            "bulk")));
        Assert.True(IsAuthoritative(Received(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = "session_v6_liveness_bulk",
                TransferId = "transfer_v6_liveness_bulk",
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = [new byte[] { 1 }],
            },
            FileTransferTransportKind.Tuna,
            "tuna")));
    }

    [Fact]
    public async Task V6SparseReceiver_AdvertisesBoundedExplicitRequestWindowAfterManifest()
    {
        const string transferId = "transfer_v6_receiver_full_window";
        const string sessionId = "session_v6_receiver_full_window";
        const int chunkSize = 4;
        const int expectedRuntimeWindowChunks = 512;
        var fileSizeBytes = (FileTransferProtocol.MaxStateMissingChunksV6 + 16) * chunkSize;
        var sha256 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v6-full-window.bin",
            fileSizeBytes,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v6-full-window.bin", fileSizeBytes, chunkSize, sha256),
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());
        var state = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Last();
        var requestedChunks = state.MissingRanges.Sum(static range => range.ChunkCount);

        Assert.Equal(expectedRuntimeWindowChunks, requestedChunks);
        Assert.Equal(expectedRuntimeWindowChunks, state.CreditUntilChunkIndexExclusive);
    }

    [Fact]
    public async Task V6Receiver_RegularNknRecoveryEpochUsesProofableFrontierBurst()
    {
        const string transferId = "transfer_v6_receiver_epoch_frontier_only";
        const string sessionId = "session_v6_receiver_epoch_frontier_only";
        const int chunkSize = 4;
        var payload = Enumerable.Range(0, 256).Select(static index => (byte)(index % 251)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v6-frontier-only.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));
        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v6-frontier-only.bin", payload.Length, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());

        receiverTransport.RequestAllDataSessionHandoffs(
            "receive_stall_recovery",
            FileTransferTransportHandoffKind.RegularNknRecovery,
            FileTransferTransportKind.RegularNkn);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(frame =>
                frame.TransportEpoch > 0 &&
                !string.IsNullOrWhiteSpace(frame.RepairRequestId)),
            timeoutMs: 5000);
        var epochState = receiverTransport.SentDataFrames
            .OfType<FileTransferReceiverStateFrameV6>()
            .Last(frame => frame.TransportEpoch > 0 && frame.RecoveryMode is not null);
        var frontierRequest = receiverTransport.SentDataFrames
            .OfType<FileTransferFrontierRequestFrameV6>()
            .Last(frame => frame.TransportEpoch == epochState.TransportEpoch);

        var epochStateRange = Assert.Single(epochState.MissingRanges);
        Assert.Equal(epochState.ContiguousCommittedChunkIndex, epochStateRange.StartChunkIndex);
        Assert.Equal(12, epochStateRange.ChunkCount);
        Assert.Equal(epochState.ContiguousCommittedChunkIndex + 12, epochState.CreditUntilChunkIndexExclusive);
        Assert.Equal(epochState.ContiguousCommittedChunkIndex, frontierRequest.MissingRanges[0].StartChunkIndex);
        Assert.Equal(12, frontierRequest.MissingRanges[0].ChunkCount);
        Assert.NotNull(frontierRequest.RepairRequestId);
    }

    [Fact]
    public async Task V6Receiver_ExactFrontierChunkWithoutBatchRepairIdStillSendsRepairProof()
    {
        const string transferId = "transfer_v6_receiver_infers_frontier_proof";
        const string sessionId = "session_v6_receiver_infers_frontier_proof";
        const int chunkSize = 4;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 16).Select(static index => (byte)(index + 1)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v6-inferred-frontier-proof.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));
        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v6-inferred-frontier-proof.bin", payload.Length, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());

        receiverTransport.RequestAllDataSessionHandoffs(
            "receive_stall_recovery",
            FileTransferTransportHandoffKind.RegularNknRecovery,
            FileTransferTransportKind.RegularNkn);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(frame =>
                frame.TransportEpoch > 0 &&
                !string.IsNullOrWhiteSpace(frame.RepairRequestId)),
            timeoutMs: 5000);
        var frontierRequest = receiverTransport.SentDataFrames
            .OfType<FileTransferFrontierRequestFrameV6>()
            .Last(frame => frame.TransportEpoch > 0);
        var frontierChunk = frontierRequest.MissingRanges[0].StartChunkIndex;

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = frontierRequest.TransportEpoch,
                StartChunkIndex = frontierChunk,
                ChunkCount = 1,
                DataSegments = [payload.Skip(frontierChunk * chunkSize).Take(chunkSize).ToArray()],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentRepairProofs.Any(proof =>
                proof.TransportEpoch == frontierRequest.TransportEpoch &&
                proof.RepairRequestId == frontierRequest.RepairRequestId &&
                proof.CommittedChunkIndex > frontierChunk),
            timeoutMs: 5000);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_repair_proof_inferred", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_epoch_recovered; direction=inbound", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Receiver_RecoveredTunaFallbackKeepsStalledFrontierWindowNarrow()
    {
        const string transferId = "transfer_v6_receiver_recovered_frontier_narrow";
        const string sessionId = "session_v6_receiver_recovered_frontier_narrow";
        const int chunkSize = 4;
        const int farAheadChunkIndex = 10;
        var payload = Enumerable.Range(0, 512).Select(static index => (byte)(index % 251)).ToArray();
        var sha256 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v6-recovered-frontier-narrow.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));
        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v6-recovered-frontier-narrow.bin", payload.Length, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());

        receiverTransport.RequestAllDataSessionHandoffs(
            "tuna_to_normal_fallback",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(frame =>
                frame.TransportEpoch > 0 &&
                !string.IsNullOrWhiteSpace(frame.RepairRequestId)),
            timeoutMs: 5000);
        var frontierRequest = receiverTransport.SentDataFrames
            .OfType<FileTransferFrontierRequestFrameV6>()
            .Last(frame => frame.TransportEpoch > 0);
        var frontierChunk = frontierRequest.MissingRanges[0].StartChunkIndex;

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = frontierRequest.TransportEpoch,
                StartChunkIndex = frontierChunk,
                ChunkCount = 1,
                DataSegments = [payload.Skip(frontierChunk * chunkSize).Take(chunkSize).ToArray()],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentRepairProofs.Any(proof =>
                proof.TransportEpoch == frontierRequest.TransportEpoch &&
                proof.CommittedChunkIndex > frontierChunk),
            timeoutMs: 5000);

        var stateCountBeforeFarAhead = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count();
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = frontierRequest.TransportEpoch,
                StartChunkIndex = farAheadChunkIndex,
                ChunkCount = 1,
                DataSegments = [payload.Skip(farAheadChunkIndex * chunkSize).Take(chunkSize).ToArray()],
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count() > stateCountBeforeFarAhead,
            timeoutMs: 5000);

        var recoveredState = receiverTransport.SentDataFrames
            .OfType<FileTransferReceiverStateFrameV6>()
            .Last(frame => frame.TransportEpoch == frontierRequest.TransportEpoch);
        var requestedChunks = recoveredState.MissingRanges.Sum(static range => range.ChunkCount);

        Assert.Equal(1, recoveredState.ContiguousCommittedChunkIndex);
        Assert.True(requestedChunks <= 512);
        Assert.True(recoveredState.CreditUntilChunkIndexExclusive <= farAheadChunkIndex + 1 + 256);
        Assert.Contains(recoveredState.MissingRanges, static range => range.StartChunkIndex == 1);
    }

    [Fact]
    public async Task V6Receiver_RecoveredTunaFallbackPausedRepairDefersLivenessTerminal()
    {
        const string transferId = "transfer_v6_receiver_post_fallback_paused_liveness";
        const string sessionId = "session_v6_receiver_post_fallback_paused_liveness";
        const int chunkSize = 4;
        const int farAheadChunkIndex = 10;
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        var previousProofTimeout = SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 128).Select(static index => (byte)(index % 251)).ToArray();
        var sha256 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        try
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromSeconds(10);
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromSeconds(5);
            SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests = TimeSpan.FromMilliseconds(250);

            var senderSession = await StartManualInboundV6ReceiverAsync(
                senderTransport,
                receiver,
                transferId,
                sessionId,
                "v6-post-fallback-paused-liveness.bin",
                payload.Length,
                sha256,
                (_, _) => Task.FromResult<Stream>(destination));
            await senderSession.SendAsync(
                CreateManifest(sessionId, transferId, "v6-post-fallback-paused-liveness.bin", payload.Length, chunkSize, sha256),
                CancellationToken.None);
            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());

            await senderSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = farAheadChunkIndex,
                    ChunkCount = 1,
                    DataSegments = [payload.Skip(farAheadChunkIndex * chunkSize).Take(chunkSize).ToArray()],
                },
                CancellationToken.None);
            await WaitUntilAsync(
                () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(frame =>
                    frame.DurableReceivedHighestChunkIndex >= farAheadChunkIndex),
                timeoutMs: 5000);

            senderTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
            receiverTransport.RequestAllDataSessionHandoffs(
                "tuna_activation_negotiated",
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna);
            var activationProbe = await ReceiveProbeAsync(senderSession);
            Assert.Equal(FileTransferTransportKind.Tuna, activationProbe.TransportKind);
            var activationProbeFrame = Assert.IsType<FileTransferTransportProbeFrameV6>(activationProbe.Frame);
            var activationTransportEpoch = activationProbeFrame.TransportEpoch;
            await senderTransport.SendFileTransferTransportProbeAsync(
                new FileTransferTransportProbeV6
                {
                    SessionId = activationProbeFrame.SessionId,
                    TransferId = activationProbeFrame.TransferId,
                    TransportEpoch = activationProbeFrame.TransportEpoch,
                    ProbeId = activationProbeFrame.ProbeId,
                    TargetTransport = activationProbeFrame.TargetTransport,
                },
                CancellationToken.None);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered; direction=inbound", StringComparison.Ordinal) &&
                      ReadOperationalLogTail(logStart).Contains("handoff_kind=normal_to_tuna_activation", StringComparison.Ordinal),
                timeoutMs: 5000);

            receiverTransport.RequestAllDataSessionHandoffs(
                "sidecar_read_failed",
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn);
            await WaitUntilAsync(
                () => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(frame =>
                    frame.TransportEpoch > activationTransportEpoch &&
                    !string.IsNullOrWhiteSpace(frame.RepairRequestId)),
                timeoutMs: 5000);
            var frontierRequest = receiverTransport.SentDataFrames
                .OfType<FileTransferFrontierRequestFrameV6>()
                .Last(frame => frame.TransportEpoch > activationTransportEpoch);
            var frontierChunk = frontierRequest.MissingRanges[0].StartChunkIndex;

            await senderSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = frontierRequest.TransportEpoch,
                    StartChunkIndex = frontierChunk,
                    ChunkCount = 1,
                    DataSegments = [payload.Skip(frontierChunk * chunkSize).Take(chunkSize).ToArray()],
                    Priority = "frontier",
                    RecoveryMode = "frontier_repair_only",
                },
                CancellationToken.None);
            await WaitUntilAsync(
                () => receiverTransport.SentRepairProofs.Any(proof =>
                    proof.TransportEpoch == frontierRequest.TransportEpoch &&
                    proof.RepairRequestId == frontierRequest.RepairRequestId &&
                    proof.CommittedChunkIndex > frontierChunk),
                timeoutMs: 5000);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered; direction=inbound", StringComparison.Ordinal) &&
                      ReadOperationalLogTail(logStart).Contains("handoff_kind=tuna_to_normal_fallback", StringComparison.Ordinal),
                timeoutMs: 5000);

            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
            receiverTransport.SetLocalDataSessionsUnavailableForTests("receive_stall_recovery");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_paused; direction=inbound", StringComparison.Ordinal),
                timeoutMs: 5000);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v6_heartbeat_timeout_deferred_for_post_tuna_fallback_repair; direction=inbound",
                    StringComparison.Ordinal),
                timeoutMs: 10000);

            Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound?.State);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain($"event=filetransfer_v6_heartbeat_timeout; direction=inbound; transfer_id={transferId}", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain($"event=filetransfer_lifecycle_priority_sent; kind=error; transfer_id={transferId};", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = previousHeartbeatInterval;
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = previousPeerLivenessTimeout;
            SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests = previousProofTimeout;
        }
    }

    [Fact]
    public async Task V6Receiver_UnresolvedEpochReissuesFrontierRequestWhenFrontierWriteIsPending()
    {
        const string transferId = "transfer_v6_receiver_epoch_pending_frontier";
        const string sessionId = "session_v6_receiver_epoch_pending_frontier";
        const int chunkSize = 4;
        var previousProofTimeout = SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests;
        SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests = TimeSpan.FromMilliseconds(100);
        var payload = Enumerable.Range(0, 8).Select(static index => (byte)(index + 1)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new BlockingSparseStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        Task? blockedSend = null;
        try
        {
            var senderSession = await StartManualInboundV6ReceiverAsync(
                senderTransport,
                receiver,
                transferId,
                sessionId,
                "v6-pending-frontier.bin",
                payload.Length,
                sha256,
                (_, _) => Task.FromResult<Stream>(destination));

            await senderSession.SendAsync(
                CreateManifest(sessionId, transferId, "v6-pending-frontier.bin", payload.Length, chunkSize, sha256),
                CancellationToken.None);
            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());

            blockedSend = senderSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = [payload.Take(chunkSize).ToArray()],
                },
                CancellationToken.None);
            await destination.WaitForWriteStartedAsync();

            receiverTransport.RequestAllDataSessionHandoffs(
                "receive_stall_recovery",
                FileTransferTransportHandoffKind.RegularNknRecovery,
                FileTransferTransportKind.RegularNkn);

            await WaitUntilAsync(
                () => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(frame =>
                    frame.TransportEpoch > 0 &&
                    frame.MissingRanges.Count == 1 &&
                    frame.MissingRanges[0].StartChunkIndex == 0 &&
                    frame.MissingRanges[0].ChunkCount == 1),
                timeoutMs: 5000);

            await WaitUntilAsync(
                () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(frame =>
                    frame.TransportEpoch > 0 &&
                    frame.MissingRanges.Count == 1 &&
                    frame.MissingRanges[0].StartChunkIndex == 0 &&
                    frame.MissingRanges[0].ChunkCount == 1),
                timeoutMs: 5000);

            var frontierRequest = receiverTransport.SentDataFrames
                .OfType<FileTransferFrontierRequestFrameV6>()
                .Last(frame => frame.TransportEpoch > 0);
            var receiverState = receiverTransport.SentDataFrames
                .OfType<FileTransferReceiverStateFrameV6>()
                .Last(frame => frame.TransportEpoch == frontierRequest.TransportEpoch && frame.MissingRanges.Count == 1);

            Assert.Equal(frontierRequest.TransportEpoch, receiverState.TransportEpoch);
            Assert.Equal(0, receiverState.MissingRanges[0].StartChunkIndex);
            Assert.Equal(1, receiverState.MissingRanges[0].ChunkCount);
            Assert.Equal(0, frontierRequest.MissingRanges[0].StartChunkIndex);
            Assert.Equal(1, frontierRequest.MissingRanges[0].ChunkCount);
        }
        finally
        {
            SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests = previousProofTimeout;
            destination.ReleaseWrites();
            if (blockedSend is not null)
            {
                await blockedSend.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task V6SparseReceiver_ClampsExplicitWindowWhenRegularNknFrontierIsMissing()
    {
        const string transferId = "transfer_v6_receiver_frontier_window";
        const string sessionId = "session_v6_receiver_frontier_window";
        const int chunkSize = 4;
        const int expectedRuntimeWindowChunks = 512;
        const int expectedFrontierStalledWindowChunks = 256;
        const int farAheadChunkIndex = expectedRuntimeWindowChunks + 100;
        var fileSizeBytes = (farAheadChunkIndex + 16) * chunkSize;
        var sha256 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v6-frontier-window.bin",
            fileSizeBytes,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v6-frontier-window.bin", fileSizeBytes, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());
        var stateCountBeforeFarAhead = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count();

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 1,
                ChunkCount = 2,
                DataSegments =
                [
                    new byte[] { 1, 2, 3, 4 },
                    new byte[] { 5, 6, 7, 8 },
                ],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count() > stateCountBeforeFarAhead,
            timeoutMs: 5000);
        var retryState = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Last();
        var requestedChunks = retryState.MissingRanges.Sum(static range => range.ChunkCount);

        Assert.True(requestedChunks > 1);
        Assert.True(requestedChunks <= expectedFrontierStalledWindowChunks + 1);
        Assert.True(retryState.CreditUntilChunkIndexExclusive <= expectedFrontierStalledWindowChunks + 3);
        Assert.Contains(retryState.MissingRanges, static range => range.StartChunkIndex == 0 && range.ChunkCount == 1);
        Assert.Contains(retryState.MissingRanges, static range => range.StartChunkIndex == 3);

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = farAheadChunkIndex,
                ChunkCount = 1,
                DataSegments = [new byte[] { 9, 10, 11, 12 }],
            },
            CancellationToken.None);
        await Task.Delay(200);
        Assert.True(destination.Length < farAheadChunkIndex * chunkSize);
    }

    [Fact]
    public async Task V6SparseReceiver_DoesNotPromoteInitialNormalRequestToRepairBeforeGrace()
    {
        const string transferId = "transfer_v6_receiver_initial_grace";
        const string sessionId = "session_v6_receiver_initial_grace";
        const int chunkSize = 4;
        const int expectedFrontierStalledWindowChunks = 256;
        var payload = Enumerable.Range(0, 2048 * chunkSize).Select(static index => (byte)(index % 251)).ToArray();
        var sha256 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v6-initial-grace.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v6-initial-grace.bin", payload.Length, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());

        await Task.Delay(1600);

        var receiverStates = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().ToArray();
        Assert.NotEmpty(receiverStates);
        Assert.Empty(receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>());
        Assert.All(receiverStates, state =>
            Assert.True(
                state.CreditUntilChunkIndexExclusive > expectedFrontierStalledWindowChunks,
                $"Initial regular NKN request was prematurely clamped to {state.CreditUntilChunkIndexExclusive} chunks."));
    }

    [Fact]
    public async Task V6SparseReceiver_DoesNotChaseFarAheadTailWhileFrontierIsMissing()
    {
        const string transferId = "transfer_v6_receiver_frontier_far_tail";
        const string sessionId = "session_v6_receiver_frontier_far_tail";
        const int chunkSize = 4;
        const int farAheadChunkIndex = 400;
        const int expectedFrontierStalledWindowChunks = 256;
        var payload = Enumerable.Range(0, 1024 * chunkSize).Select(static index => (byte)(index % 251)).ToArray();
        var sha256 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v6-frontier-far-tail.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v6-frontier-far-tail.bin", payload.Length, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static state =>
            state.CreditUntilChunkIndexExclusive > farAheadChunkIndex));

        var stateCountBeforeFarAhead = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count();
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = farAheadChunkIndex,
                ChunkCount = 1,
                DataSegments = [payload.Skip(farAheadChunkIndex * chunkSize).Take(chunkSize).ToArray()],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count() > stateCountBeforeFarAhead,
            timeoutMs: 5000);
        var stalledState = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Last();
        var requestedChunks = stalledState.MissingRanges.Sum(static range => range.ChunkCount);

        Assert.Equal(0, stalledState.ContiguousCommittedChunkIndex);
        Assert.Equal(farAheadChunkIndex, stalledState.DurableReceivedHighestChunkIndex);
        Assert.True(requestedChunks <= expectedFrontierStalledWindowChunks);
        Assert.True(stalledState.CreditUntilChunkIndexExclusive <= expectedFrontierStalledWindowChunks);
        Assert.DoesNotContain(stalledState.MissingRanges, static range =>
            range.StartChunkIndex >= farAheadChunkIndex ||
            range.StartChunkIndex + range.ChunkCount > farAheadChunkIndex);
    }

    [Fact]
    public async Task V6SparseReceiver_CoalescesFrontierRequestsWhenFrontierAdvancesQuickly()
    {
        const string transferId = "transfer_v6_receiver_frontier_coalesce";
        const string sessionId = "session_v6_receiver_frontier_coalesce";
        const int chunkSize = 4;
        var fileSizeBytes = 16 * chunkSize;
        var sha256 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v6-frontier-coalesce.bin",
            fileSizeBytes,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v6-frontier-coalesce.bin", fileSizeBytes, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 1,
                ChunkCount = 2,
                DataSegments =
                [
                    new byte[] { 1, 2, 3, 4 },
                    new byte[] { 5, 6, 7, 8 },
                ],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(frame =>
                frame.MissingRanges.Any(static range => range.StartChunkIndex == 0)),
            timeoutMs: 5000);
        var firstFrontierRequest = receiverTransport.SentDataFrames
            .OfType<FileTransferFrontierRequestFrameV6>()
            .Last(frame =>
                frame.MissingRanges.Any(static range => range.StartChunkIndex == 0));
        Assert.Equal("regular_nkn_frontier_stall_control_bulk", firstFrontierRequest.RecoveryMode);
        var frontierRequestCount = receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Count();

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = [new byte[] { 9, 10, 11, 12 }],
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.ChunksTransferred >= 3, timeoutMs: 5000);

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 4,
                ChunkCount = 1,
                DataSegments = [new byte[] { 13, 14, 15, 16 }],
            },
            CancellationToken.None);
        await Task.Delay(300);

        Assert.Equal(frontierRequestCount, receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Count());
    }

    [Fact]
    public async Task V6SparseReceiver_RegularNknFrontierRequestIncludesNearFrontierHoles()
    {
        const string transferId = "transfer_v6_receiver_frontier_multirange";
        const string sessionId = "session_v6_receiver_frontier_multirange";
        const int chunkSize = 4;
        var fileSizeBytes = 64 * chunkSize;
        var sha256 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v6-frontier-multirange.bin",
            fileSizeBytes,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v6-frontier-multirange.bin", fileSizeBytes, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());

        foreach (var startChunk in new[] { 1, 5, 9 })
        {
            await senderSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = startChunk,
                    ChunkCount = 2,
                    DataSegments =
                    [
                        new byte[] { 1, 2, 3, 4 },
                        new byte[] { 5, 6, 7, 8 },
                    ],
                },
                CancellationToken.None);
        }

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(frame =>
                frame.MissingRanges.Count >= 3 &&
                frame.MissingRanges[0].StartChunkIndex == 0),
            timeoutMs: 5000);
        var frontierRequest = receiverTransport.SentDataFrames
            .OfType<FileTransferFrontierRequestFrameV6>()
            .Last(frame => frame.MissingRanges.Count >= 3);

        Assert.Equal("regular_nkn_frontier_stall_control_bulk", frontierRequest.RecoveryMode);
        Assert.Contains(frontierRequest.MissingRanges, static range => range.StartChunkIndex == 0 && range.ChunkCount == 1);
        Assert.Contains(frontierRequest.MissingRanges, static range => range.StartChunkIndex == 3 && range.ChunkCount == 2);
        Assert.Contains(frontierRequest.MissingRanges, static range => range.StartChunkIndex == 7 && range.ChunkCount == 2);
        Assert.Contains(frontierRequest.MissingRanges, static range => range.StartChunkIndex == 11 && range.ChunkCount == 1);
        Assert.DoesNotContain(frontierRequest.MissingRanges, static range => range.StartChunkIndex > 11);
    }

    [Fact]
    public async Task V6SparseReceiver_RegularNknFrontierRequestBurstsWhenNoAheadChunksArrived()
    {
        const string transferId = "transfer_v6_receiver_frontier_burst_no_ahead";
        const string sessionId = "session_v6_receiver_frontier_burst_no_ahead";
        const int chunkSize = 4;
        var fileSizeBytes = 64 * chunkSize;
        var sha256 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v6-frontier-burst-no-ahead.bin",
            fileSizeBytes,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v6-frontier-burst-no-ahead.bin", fileSizeBytes, chunkSize, sha256),
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(frame =>
                frame.MissingRanges.Count == 1 &&
                frame.MissingRanges[0].StartChunkIndex == 0),
            timeoutMs: 5000);

        var frontierRequest = receiverTransport.SentDataFrames
            .OfType<FileTransferFrontierRequestFrameV6>()
            .Last(frame => frame.MissingRanges.Count == 1 && frame.MissingRanges[0].StartChunkIndex == 0);

        Assert.Contains(receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>(), static frame =>
            frame.RecoveryMode is "regular_nkn_frontier_stall" or "regular_nkn_frontier_stall_control_bulk" &&
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0));
        Assert.Equal("regular_nkn_frontier_stall_control_bulk", frontierRequest.RecoveryMode);
        Assert.Equal(12, frontierRequest.MissingRanges[0].ChunkCount);
    }

    [Fact]
    public async Task V6SparseReceiver_DefersRegularNknFrontierRequestWhileFrontierRecentlyAdvanced()
    {
        const string transferId = "transfer_v6_receiver_frontier_recent_progress";
        const string sessionId = "session_v6_receiver_frontier_recent_progress";
        const int chunkSize = 4;
        var fileSizeBytes = 8 * chunkSize;
        var sha256 = Convert.ToBase64String(new byte[FileTransferProtocol.Sha256LengthBytes]);
        var previousProgressGrace = SessionFileTransferService.V6RegularNknFrontierRequestProgressGraceOverrideForTests;
        SessionFileTransferService.V6RegularNknFrontierRequestProgressGraceOverrideForTests = TimeSpan.FromMilliseconds(2000);
        try
        {
            using var destination = new NonDisposingMemoryStream();
            using var senderTransport = new LoopbackFileTransferTransport(sessionId);
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
            senderTransport.Connect(receiverTransport);
            using var receiver = new SessionFileTransferService();
            receiver.AttachTransport(receiverTransport);

            var senderSession = await StartManualInboundV6ReceiverAsync(
                senderTransport,
                receiver,
                transferId,
                sessionId,
                "v6-frontier-recent-progress.bin",
                fileSizeBytes,
                sha256,
                (_, _) => Task.FromResult<Stream>(destination));

            await senderSession.SendAsync(
                CreateManifest(sessionId, transferId, "v6-frontier-recent-progress.bin", fileSizeBytes, chunkSize, sha256),
                CancellationToken.None);
            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());

            await senderSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = [new byte[] { 1, 2, 3, 4 }],
                },
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.ChunksTransferred >= 1, timeoutMs: 5000);

            var frontierRequestCountAfterProgress = receiverTransport.SentDataFrames
                .OfType<FileTransferFrontierRequestFrameV6>()
                .Count(static frame => frame.MissingRanges.Count > 0 && frame.MissingRanges[0].StartChunkIndex == 1);

            await senderSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 2,
                    ChunkCount = 1,
                    DataSegments = [new byte[] { 5, 6, 7, 8 }],
                },
                CancellationToken.None);

            await Task.Delay(1000);
            Assert.Equal(
                frontierRequestCountAfterProgress,
                receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Count(static frame =>
                    frame.MissingRanges.Count > 0 && frame.MissingRanges[0].StartChunkIndex == 1));

            await WaitUntilAsync(
                () => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(static frame =>
                    frame.MissingRanges.Count > 0 && frame.MissingRanges[0].StartChunkIndex == 1),
                timeoutMs: 5000);
        }
        finally
        {
            SessionFileTransferService.V6RegularNknFrontierRequestProgressGraceOverrideForTests = previousProgressGrace;
        }
    }

    [Fact]
    public async Task V6Sender_ResendsNormalReceiverStateRangesAfterResendGate()
    {
        const string transferId = "transfer_v6_sender_normal_resend_gate";
        const string sessionId = "session_v6_sender_normal_resend_gate";
        var previousGate = SessionFileTransferService.V6RegularNknNormalReceiverStateResendGateOverrideForTests;
        SessionFileTransferService.V6RegularNknNormalReceiverStateResendGateOverrideForTests = TimeSpan.FromMilliseconds(1000);
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        try
        {
            using var senderTransport = new LoopbackFileTransferTransport(sessionId);
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);

            var receiverSession = await StartManualOutboundV6SenderAsync(
                sender,
                senderTransport,
                receiverTransport,
                transferId,
                payload);

            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 12,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                    batch.StartChunkIndex <= 9 && batch.StartChunkIndex + batch.ChunkCount > 9),
                timeoutMs: 5000);
            await Task.Delay(200);
            var sentAfterInitialWindow = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();

            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 2,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 12,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);
            await Task.Delay(500);

            var batchesAfterStateFrontierRepair = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().ToList();
            var stateRepairBatches = batchesAfterStateFrontierRepair.Skip(sentAfterInitialWindow).ToList();
            Assert.Empty(stateRepairBatches);

            await Task.Delay(1100);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 3,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 12,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);
            await Task.Delay(500);

            var batchesAfterNormalResendGate = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().ToList();
            var normalResendBatches = batchesAfterNormalResendGate.Skip(batchesAfterStateFrontierRepair.Count).ToList();
            Assert.NotEmpty(normalResendBatches);
            Assert.Contains(
                normalResendBatches,
                static batch => batch.StartChunkIndex <= 9 &&
                                batch.StartChunkIndex + batch.ChunkCount > 9 &&
                                string.IsNullOrWhiteSpace(batch.Priority));
        }
        finally
        {
            SessionFileTransferService.V6RegularNknNormalReceiverStateResendGateOverrideForTests = previousGate;
        }
    }

    [Fact]
    public async Task V6Sender_RegularNknNormalReceiverStateDoesNotResendAtLegacyGate()
    {
        const string transferId = "transfer_v6_sender_normal_resend_legacy_gate";
        const string sessionId = "session_v6_sender_normal_resend_legacy_gate";
        var previousGate = SessionFileTransferService.V6RegularNknNormalReceiverStateResendGateOverrideForTests;
        SessionFileTransferService.V6RegularNknNormalReceiverStateResendGateOverrideForTests = null;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        try
        {
            using var senderTransport = new LoopbackFileTransferTransport(sessionId);
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);

            var receiverSession = await StartManualOutboundV6SenderAsync(
                sender,
                senderTransport,
                receiverTransport,
                transferId,
                payload);

            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 12,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                    batch.StartChunkIndex <= 9 && batch.StartChunkIndex + batch.ChunkCount > 9),
                timeoutMs: 5000);
            await Task.Delay(200);
            var sentAfterInitialWindow = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();

            await Task.Delay(3900);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 2,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 12,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);
            await Task.Delay(500);

            var sentAfterLegacyGate = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
            Assert.Equal(sentAfterInitialWindow, sentAfterLegacyGate);
        }
        finally
        {
            SessionFileTransferService.V6RegularNknNormalReceiverStateResendGateOverrideForTests = previousGate;
        }
    }

    [Fact]
    public async Task V6Sender_ResendsNearFrontierNormalRangeWhenRefillWouldOtherwiseDefer()
    {
        const string transferId = "transfer_v6_sender_normal_refill_bypass";
        const string sessionId = "session_v6_sender_normal_refill_bypass";
        var previousGate = SessionFileTransferService.V6RegularNknNormalReceiverStateResendGateOverrideForTests;
        SessionFileTransferService.V6RegularNknNormalReceiverStateResendGateOverrideForTests = TimeSpan.FromMilliseconds(1000);
        var payload = Enumerable.Range(0, 20 * 1024 * 1024).Select(static index => (byte)(index % 251)).ToArray();
        try
        {
            using var senderTransport = new LoopbackFileTransferTransport(sessionId);
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);

            var receiverSession = await StartManualOutboundV6SenderAsync(
                sender,
                senderTransport,
                receiverTransport,
                transferId,
                payload);

            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 768,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 768 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                    batch.StartChunkIndex <= 510 && batch.StartChunkIndex + batch.ChunkCount > 510),
                timeoutMs: 5000);
            await Task.Delay(200);
            var sentAfterInitialWindow = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();

            await Task.Delay(1100);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 2,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 768,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 768 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count() > sentAfterInitialWindow,
                timeoutMs: 5000);

            var bypassBatches = senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(sentAfterInitialWindow)
                .ToList();
            Assert.Contains(
                bypassBatches,
                static batch => batch.StartChunkIndex <= 9 &&
                                batch.StartChunkIndex + batch.ChunkCount > 9 &&
                                string.IsNullOrWhiteSpace(batch.Priority));
        }
        finally
        {
            SessionFileTransferService.V6RegularNknNormalReceiverStateResendGateOverrideForTests = previousGate;
        }
    }

    [Fact]
    public async Task V6Sender_DoesNotBypassNormalRefillDeferralWhenReceiverFrontierAdvances()
    {
        const string transferId = "transfer_v6_sender_normal_refill_bypass_progress";
        const string sessionId = "session_v6_sender_normal_refill_bypass_progress";
        var payload = Enumerable.Range(0, 20 * 1024 * 1024).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(
            sender,
            senderTransport,
            receiverTransport,
            transferId,
            payload);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 1536,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1536 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                batch.StartChunkIndex <= 510 && batch.StartChunkIndex + batch.ChunkCount > 510),
            timeoutMs: 5000);
        await Task.Delay(200);
        var sentAfterInitialWindow = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();

        await Task.Delay(3700);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 18,
                DurableReceivedHighestChunkIndex = 17,
                CreditUntilChunkIndexExclusive = 1554,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 18, ChunkCount = 1536 }],
                BytesCommitted = 18 * 21_504,
            },
            CancellationToken.None);
        await Task.Delay(500);

        var sentAfterAdvancedState = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        Assert.Equal(sentAfterInitialWindow, sentAfterAdvancedState);
    }

    [Fact]
    public async Task V6Sender_SuppressesFrontierRetryWhileChunkSendIsInFlight()
    {
        const string transferId = "transfer_v6_sender_inflight_dedupe";
        const string sessionId = "session_v6_sender_inflight_dedupe";
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(
            sender,
            senderTransport,
            receiverTransport,
            transferId,
            payload);
        senderTransport.DataSessionSendDelayMs = 750;

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 4,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 4 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => senderTransport.ActiveDataSessionSends > 0, timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 0,
                RepairRequestId = "frontier:0:inflight",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                batch.StartChunkIndex == 0),
            timeoutMs: 5000);
        await Task.Delay(500);

        Assert.Equal(
            1,
            senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count(static batch =>
                batch.StartChunkIndex <= 0 && batch.StartChunkIndex + batch.ChunkCount > 0));
    }

    [Fact]
    public async Task V6Sender_RequeuesRegularNknNormalBatchAfterSendTimeout()
    {
        const string transferId = "transfer_v6_sender_regular_nkn_timeout_requeue";
        const string sessionId = "session_v6_sender_regular_nkn_timeout_requeue";
        var logStart = GetOperationalLogLength();
        var previousTimeout = SessionFileTransferService.V6SenderTransportSendTimeoutOverrideForTests;
        SessionFileTransferService.V6SenderTransportSendTimeoutOverrideForTests = TimeSpan.FromMilliseconds(100);
        var injectedTimeoutCount = 0;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();

        try
        {
            using var senderTransport = new LoopbackFileTransferTransport(sessionId);
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
            senderTransport.Connect(receiverTransport);
            senderTransport.OutboundDataFrameDeliveryOverrideWithLaneAsync = async (_, frame, _, ct) =>
            {
                if (frame is FileTransferChunkBatchFrameV6 batch &&
                    batch.StartChunkIndex == 0 &&
                    string.IsNullOrWhiteSpace(batch.Priority) &&
                    Interlocked.CompareExchange(ref injectedTimeoutCount, 1, 0) == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    return true;
                }

                return false;
            };

            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);

            var receiverSession = await StartManualOutboundV6SenderAsync(
                sender,
                senderTransport,
                receiverTransport,
                transferId,
                payload);

            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 12,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v6_chunk_batch_send_timeout_requeue",
                    StringComparison.Ordinal),
                timeoutMs: 5000);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count(static batch =>
                    batch.StartChunkIndex == 0 &&
                    string.IsNullOrWhiteSpace(batch.Priority)) >= 2,
                timeoutMs: 5000);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_chunk_batch_send_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_chunk_batch_send_timeout_requeue", logTail, StringComparison.Ordinal);
            Assert.Contains("requeued_chunk_count=", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=requeued", logTail, StringComparison.Ordinal);
            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            Assert.Empty(senderTransport.SentErrors);
        }
        finally
        {
            SessionFileTransferService.V6SenderTransportSendTimeoutOverrideForTests = previousTimeout;
        }
    }

    [Fact]
    public async Task V6Sender_RequiresFrontierRequestToRetransmitReceiverStateFrontierGap()
    {
        const string transferId = "transfer_v6_sender_state_frontier_repair";
        const string sessionId = "session_v6_sender_state_frontier_repair";
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(
            sender,
            senderTransport,
            receiverTransport,
            transferId,
            payload);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 6,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 6 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                batch.StartChunkIndex == 0),
            timeoutMs: 5000);
        await Task.Delay(5200);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 5,
                CreditUntilChunkIndexExclusive = 1,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
                Priority = "frontier",
            },
            CancellationToken.None);

        await Task.Delay(500);
        Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>(), static batch => batch.StartChunkIndex == 0);

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 0,
                RepairRequestId = "frontier:normal-nkn:0",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "regular_nkn_frontier_stall",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count(static batch =>
                batch.StartChunkIndex == 0) >= 2,
            timeoutMs: 5000);

        var repairBatch = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>()
            .Where(static batch => batch.StartChunkIndex == 0)
            .Last();
        Assert.Equal("frontier", repairBatch.Priority);
        Assert.Equal("frontier:normal-nkn:0", repairBatch.RepairRequestId);
    }

    [Fact]
    public async Task V6Sender_RegularNknFrontierStallReceiverStateUsesPriorityRepair()
    {
        const string transferId = "transfer_v6_sender_state_frontier_stall_repair";
        const string sessionId = "session_v6_sender_state_frontier_stall_repair";
        const string repairRequestId = "state-frontier:normal-nkn:0";
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(
            sender,
            senderTransport,
            receiverTransport,
            transferId,
            payload);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 12,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>()
                .Where(static batch => batch.Priority is null)
                .Sum(static batch => batch.ChunkCount) >= 12,
            timeoutMs: 5000);
        var sentAfterInitialWindow = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 12,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
                BytesCommitted = 0,
                Priority = "frontier",
                RepairRequestId = repairRequestId,
                RecoveryMode = "regular_nkn_frontier_stall_control_bulk",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>()
                .Skip(sentAfterInitialWindow)
                .Any(batch => batch.RepairRequestId == repairRequestId),
            timeoutMs: 5000);

        var repairBatches = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .Skip(sentAfterInitialWindow)
            .Where(batch => batch.RepairRequestId == repairRequestId)
            .ToList();
        Assert.NotEmpty(repairBatches);
        Assert.All(repairBatches, static batch =>
        {
            Assert.Equal("frontier", batch.Priority);
            Assert.True(batch.ForceRegularNknBulk);
        });
    }

    [Fact]
    public async Task V6Sender_InferredRegularNknStateFrontierRepairIsLatchedAfterStableStall()
    {
        const string transferId = "transfer_v6_sender_inferred_regular_nkn_frontier_repair";
        const string sessionId = "session_v6_sender_inferred_regular_nkn_frontier_repair";
        var logStart = GetOperationalLogLength();
        var previousStall = SessionFileTransferService.V6RegularNknInferredFrontierRepairStallOverrideForTests;
        var previousCooldown = SessionFileTransferService.V6RegularNknInferredFrontierRepairCooldownOverrideForTests;
        SessionFileTransferService.V6RegularNknInferredFrontierRepairStallOverrideForTests = TimeSpan.FromMilliseconds(1000);
        SessionFileTransferService.V6RegularNknInferredFrontierRepairCooldownOverrideForTests = TimeSpan.FromMilliseconds(1500);
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        try
        {
            var receiverSession = await StartManualOutboundV6SenderAsync(
                sender,
                senderTransport,
                receiverTransport,
                transferId,
                payload);

            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = 5,
                    CreditUntilChunkIndexExclusive = 1,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 4 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                    batch.StartChunkIndex == 0),
                timeoutMs: 5000);
            await ReceiveChunkBatchAsync(receiverSession, 0);
            await Task.Delay(500);
            Assert.DoesNotContain(
                senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>(),
                static batch => batch.Priority == "frontier" && batch.RepairRequestId == "v6-inferred-frontier:0");

            await Task.Delay(3600);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 2,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = 5,
                    CreditUntilChunkIndexExclusive = 1,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 4 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                    batch.StartChunkIndex == 0 &&
                    batch.Priority == "frontier" &&
                    batch.RepairRequestId == "v6-inferred-frontier:0"),
                timeoutMs: 5000);

            var inferredRepairBatches = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>()
                .Where(static batch => batch.Priority == "frontier" && batch.RepairRequestId == "v6-inferred-frontier:0")
                .ToList();
            Assert.NotEmpty(inferredRepairBatches);
            Assert.Equal(4, inferredRepairBatches.Sum(static batch => batch.ChunkCount));
            Assert.Contains(
                inferredRepairBatches,
                static batch => batch.StartChunkIndex == 0);

            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 3,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = 5,
                    CreditUntilChunkIndexExclusive = 1,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 4 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);

            await Task.Delay(500);
            Assert.Equal(
                inferredRepairBatches.Count,
                senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>()
                    .Count(static batch => batch.Priority == "frontier" && batch.RepairRequestId == "v6-inferred-frontier:0"));

            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 4,
                    ContiguousCommittedChunkIndex = 1,
                    DurableReceivedHighestChunkIndex = 5,
                    CreditUntilChunkIndexExclusive = 2,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 1, ChunkCount = 4 }],
                    BytesCommitted = FileTransferProtocol.MaxChunkRawBytes,
                },
                CancellationToken.None);

            await Task.Delay(500);
            Assert.DoesNotContain(
                senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>(),
                static batch => batch.StartChunkIndex == 1 &&
                    batch.Priority == "frontier" &&
                    batch.RepairRequestId == "v6-inferred-frontier:1");
            Assert.Contains(
                "event=filetransfer_v6_regular_nkn_state_frontier_repair_request_inferred",
                ReadOperationalLogTail(logStart),
                StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V6RegularNknInferredFrontierRepairStallOverrideForTests = previousStall;
            SessionFileTransferService.V6RegularNknInferredFrontierRepairCooldownOverrideForTests = previousCooldown;
        }
    }

    [Fact]
    public async Task V6Sender_ExplicitRegularNknFrontierRequestBypassesRecentSentGate()
    {
        const string transferId = "transfer_v6_sender_regular_nkn_frontier_recent";
        const string sessionId = "session_v6_sender_regular_nkn_frontier_recent";
        const string repairRequestId = "frontier:normal-nkn:recent";
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(
            sender,
            senderTransport,
            receiverTransport,
            transferId,
            payload);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 12,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>()
                .Where(static batch => batch.Priority is null)
                .Sum(static batch => batch.ChunkCount) >= 12,
            timeoutMs: 5000);
        await Task.Delay(200);
        var sentAfterInitialWindow = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 0,
                RepairRequestId = repairRequestId,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
                Priority = "frontier",
                RecoveryMode = "regular_nkn_frontier_stall_control_bulk",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>()
                .Skip(sentAfterInitialWindow)
                .Any(batch => batch.RepairRequestId == repairRequestId),
            timeoutMs: 5000);

        var repairBatches = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .Skip(sentAfterInitialWindow)
            .Where(batch => batch.RepairRequestId == repairRequestId)
            .ToList();
        Assert.NotEmpty(repairBatches);
        Assert.Contains(repairBatches, static batch =>
            batch.StartChunkIndex == 0 &&
            batch.Priority == "frontier");
    }

    [Fact]
    public async Task V6Sender_SuppressesEpochFrontierRetryUntilV6ResendGate()
    {
        const string transferId = "transfer_v6_sender_epoch_frontier_resend_gate";
        const string sessionId = "session_v6_sender_epoch_frontier_resend_gate";
        const long transportEpoch = 7;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(
            sender,
            senderTransport,
            receiverTransport,
            transferId,
            payload);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 1,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
                TransportEpoch = transportEpoch,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                batch.StartChunkIndex == 0),
            timeoutMs: 5000);

        var firstSendCount = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>()
            .Count(static batch => batch.StartChunkIndex == 0);
        await Task.Delay(1000);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 5,
                CreditUntilChunkIndexExclusive = 1,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
                TransportEpoch = transportEpoch,
                Priority = "frontier",
            },
            CancellationToken.None);

        await Task.Delay(750);

        Assert.Equal(
            firstSendCount,
            senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count(static batch =>
                batch.StartChunkIndex == 0));
    }

    [Fact]
    public async Task V6Sender_IgnoresDuplicateFrontierRequestId()
    {
        const string transferId = "transfer_v6_sender_frontier_dedupe";
        const string sessionId = "session_v6_sender_frontier_dedupe";
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(
            sender,
            senderTransport,
            receiverTransport,
            transferId,
            payload);

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 0,
                RepairRequestId = "frontier:dedupe",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                batch.StartChunkIndex == 0 &&
                batch.ChunkCount == 1 &&
                batch.Priority == "frontier"),
            timeoutMs: 5000);
        var sentAfterFirst = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 0,
                RepairRequestId = "frontier:dedupe",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
            },
            CancellationToken.None);
        await Task.Delay(250);

        Assert.Equal(sentAfterFirst, senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count());
    }

    [Fact]
    public async Task V6Sender_ReceiverStateFrontierGapUsesNormalRequestWindow()
    {
        const string transferId = "transfer_v6_sender_state_frontier_only";
        const string sessionId = "session_v6_sender_state_frontier_only";
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-frontier-only.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        Assert.NotNull(await sender.PauseTransferAsync(transferId, "queue_state_frontier_gap", CancellationToken.None));

        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);
        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 7,
                CreditUntilChunkIndexExclusive = 8,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 8 }],
                BytesCommitted = 0,
                Priority = "frontier",
            },
            CancellationToken.None);
        await Task.Delay(300);
        Assert.Empty(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>());

        Assert.NotNull(await sender.ResumeTransferAsync(transferId, "send_state_frontier_gap", CancellationToken.None));
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
            timeoutMs: 5000);

        var batches = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().ToList();
        Assert.Equal(0, batches[0].StartChunkIndex);
        Assert.True(batches[0].ChunkCount > 1);
        Assert.True(batches[0].ChunkCount <= FileTransferProtocol.MaxChunkBatchSegmentsV6);
        Assert.Null(batches[0].Priority);
        Assert.DoesNotContain(batches, static batch => batch.Priority == "frontier");
    }

    [Fact]
    public async Task V6Sender_ReceiverStateNonContiguousRangesDoNotCoalesceAcrossGaps()
    {
        const string transferId = "transfer_v6_sender_state_frontier_noncontiguous";
        const string sessionId = "session_v6_sender_state_frontier_noncontiguous";
        const int chunkSizeBytes = 4096;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-frontier-noncontiguous.bin", payload.Length, transferId, chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        Assert.NotNull(await sender.PauseTransferAsync(transferId, "queue_noncontiguous_state_frontier", CancellationToken.None));

        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);
        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 3,
                DurableReceivedHighestChunkIndex = 32,
                CreditUntilChunkIndexExclusive = 40,
                MissingRanges =
                [
                    new FileTransferRangeV4 { StartChunkIndex = 3, ChunkCount = 1 },
                    new FileTransferRangeV4 { StartChunkIndex = 10, ChunkCount = 1 },
                    new FileTransferRangeV4 { StartChunkIndex = 18, ChunkCount = 3 },
                ],
                BytesCommitted = 3 * chunkSizeBytes,
                Priority = "frontier",
            },
            CancellationToken.None);
        await Task.Delay(300);
        Assert.Empty(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>());

        Assert.NotNull(await sender.ResumeTransferAsync(transferId, "send_noncontiguous_state_frontier", CancellationToken.None));
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
            timeoutMs: 5000);

        var batches = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().ToList();
        var firstBatch = batches.First();
        Assert.Equal(3, firstBatch.StartChunkIndex);
        Assert.Equal(1, firstBatch.ChunkCount);
        Assert.Null(firstBatch.Priority);
        Assert.All(batches, static batch => Assert.Null(batch.Priority));
    }

    [Fact]
    public async Task V6Sender_PrioritizesFrontierRequestBeforeNormalRanges()
    {
        const string transferId = "transfer_v6_sender_frontier_priority";
        const string sessionId = "session_v6_sender_frontier_priority";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-frontier-priority.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        Assert.NotNull(await sender.PauseTransferAsync(transferId, "queue_requests", CancellationToken.None));

        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);
        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 8,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 5, ChunkCount = 2 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 0,
                RepairRequestId = "frontier-0",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("repair_request_id=frontier-0", StringComparison.Ordinal),
            timeoutMs: 5000);
        Assert.Empty(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>());

        Assert.NotNull(await sender.ResumeTransferAsync(transferId, "send_requests", CancellationToken.None));
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(), timeoutMs: 5000);

        var firstBatch = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().First();
        Assert.Equal(0, firstBatch.StartChunkIndex);
        Assert.Equal(1, firstBatch.ChunkCount);
        Assert.Equal("frontier", firstBatch.Priority);
        Assert.Equal("frontier-0", firstBatch.RepairRequestId);

        await Task.Delay(500);
        Assert.DoesNotContain(
            senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>(),
            static batch => batch.StartChunkIndex == 5 && batch.Priority is null);
    }

    [Fact]
    public async Task V6Sender_ReplacesStaleNormalReceiverStateRanges()
    {
        const string transferId = "transfer_v6_sender_replaces_stale_normal_ranges";
        const string sessionId = "session_v6_sender_replaces_stale_normal_ranges";
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-stale-normal-ranges.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        Assert.NotNull(await sender.PauseTransferAsync(transferId, "queue_requests", CancellationToken.None));

        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);
        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 8,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 2, ChunkCount = 2 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 12,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 8, ChunkCount = 2 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await Task.Delay(300);
        Assert.Empty(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>());

        Assert.NotNull(await sender.ResumeTransferAsync(transferId, "send_latest_request", CancellationToken.None));
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(), timeoutMs: 5000);

        var firstBatch = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().First();
        Assert.Equal(8, firstBatch.StartChunkIndex);
        Assert.Equal(2, firstBatch.ChunkCount);
        Assert.DoesNotContain(
            senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>(),
            static batch => batch.StartChunkIndex is 2 or 3);
    }

    [Fact]
    public async Task V6Sender_ObsoleteFrontierRequestDoesNotPreemptNormalWindow()
    {
        const string transferId = "transfer_v6_sender_ignores_obsolete_frontier";
        const string sessionId = "session_v6_sender_ignores_obsolete_frontier";
        const int chunkSizeBytes = 4096;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-obsolete-frontier.bin", payload.Length, transferId, chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        Assert.NotNull(await sender.PauseTransferAsync(transferId, "queue_normal_then_obsolete_frontier", CancellationToken.None));

        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);
        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 4,
                DurableReceivedHighestChunkIndex = 7,
                CreditUntilChunkIndexExclusive = 10,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 8, ChunkCount = 2 }],
                BytesCommitted = 4L * chunkSizeBytes,
            },
            CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 0,
                RepairRequestId = "frontier-obsolete",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "regular_nkn_frontier_stall_control_bulk",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_frontier_request_ignored_obsolete", StringComparison.Ordinal),
            timeoutMs: 5000);
        Assert.NotNull(await sender.ResumeTransferAsync(transferId, "send_normal_after_obsolete_frontier", CancellationToken.None));
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(), timeoutMs: 5000);

        var firstBatch = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().First();
        Assert.Equal(8, firstBatch.StartChunkIndex);
        Assert.Null(firstBatch.Priority);
        Assert.DoesNotContain(
            senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>(),
            static batch => batch.RepairRequestId == "frontier-obsolete");
    }

    [Fact]
    public async Task V6Sender_NormalReceiverStateDoesNotOverwriteQueuedFrontierMetadata()
    {
        const string transferId = "transfer_v6_sender_keeps_frontier_metadata";
        const string sessionId = "session_v6_sender_keeps_frontier_metadata";
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-frontier-metadata.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        Assert.NotNull(await sender.PauseTransferAsync(transferId, "queue_requests", CancellationToken.None));

        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);
        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 0,
                RepairRequestId = "frontier-keep",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
            },
            CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 4,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 4 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await Task.Delay(300);
        Assert.Empty(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>());

        Assert.NotNull(await sender.ResumeTransferAsync(transferId, "send_frontier", CancellationToken.None));
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(), timeoutMs: 5000);

        var firstBatch = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().First();
        Assert.Equal(0, firstBatch.StartChunkIndex);
        Assert.Equal(1, firstBatch.ChunkCount);
        Assert.Equal("frontier", firstBatch.Priority);
        Assert.Equal("frontier-keep", firstBatch.RepairRequestId);
    }

    [Fact]
    public async Task V6SparseReceiver_CachesFarAheadRequestedChunksAndCommitsOnlyContiguousFrontier()
    {
        const string transferId = "transfer_v6_sparse_far_ahead";
        const string sessionId = "session_v6_sparse_far_ahead";
        var payload = Enumerable.Range(1, 12).Select(static value => (byte)value).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v6-sparse.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v6-sparse.bin", payload.Length, 4, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());
        var receiverStateCountBeforeFarAhead = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count();

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

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(static frame =>
            frame.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount == 1)));
        Assert.True(receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count() >= receiverStateCountBeforeFarAhead);
        Assert.Equal(0, receiver.Snapshot.Inbound!.BytesTransferred);

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
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
    }

    [Fact]
    public async Task V6SparseReceiver_FrontierStallKeepsRequestWindowFlowing()
    {
        const string transferId = "transfer_v6_sparse_frontier_stall_window";
        const string sessionId = "session_v6_sparse_frontier_stall_window";
        const int chunkSize = 4;
        var payload = Enumerable.Range(1, 2048 * chunkSize).Select(static value => (byte)(value % 251)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v6-sparse-frontier-stall-window.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "v6-sparse-frontier-stall-window.bin", payload.Length, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any());

        await Task.Delay(1100);
        var stateCountBeforeStallChunk = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count();
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 1,
                ChunkCount = 1,
                DataSegments = [payload.Skip(chunkSize).Take(chunkSize).ToArray()],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count() > stateCountBeforeStallChunk,
            timeoutMs: 5000);
        var stalledState = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Last();

        Assert.Equal(0, receiver.Snapshot.Inbound!.BytesTransferred);
        Assert.Contains(stalledState.MissingRanges, static range => range.StartChunkIndex == 0);
        Assert.True(stalledState.CreditUntilChunkIndexExclusive > 1);
    }

    [Fact]
    public async Task V6SparseReceiver_AcceptsLateChunkFromPreviouslyAdvertisedWindowAfterFrontierStall()
    {
        const string transferId = "transfer_v6_sparse_late_requested_window";
        const string sessionId = "session_v6_sparse_late_requested_window";
        const int chunkSize = 4;
        const int lateChunkIndex = 40;
        var payload = Enumerable.Range(1, 256 * chunkSize).Select(static value => (byte)(value % 251)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v6-sparse-late-window.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v6-sparse-late-window.bin", payload.Length, chunkSize, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static state =>
            state.CreditUntilChunkIndexExclusive > lateChunkIndex));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 10,
                ChunkCount = 1,
                DataSegments = [payload.Skip(10 * chunkSize).Take(chunkSize).ToArray()],
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(static frame =>
            frame.MissingRanges.Any(static range => range.StartChunkIndex == 0)));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = lateChunkIndex,
                ChunkCount = 1,
                DataSegments = [payload.Skip(lateChunkIndex * chunkSize).Take(chunkSize).ToArray()],
            },
            CancellationToken.None);

        await WaitUntilAsync(() => destination.Length >= (lateChunkIndex + 1) * chunkSize, timeoutMs: 5000);
        Assert.Equal(payload.Skip(lateChunkIndex * chunkSize).Take(chunkSize), destination.ToArray().Skip(lateChunkIndex * chunkSize).Take(chunkSize));
    }

    [Fact]
    public async Task V6ContiguousReceiver_RequestsFrontierAndDropsUnsolicitedFarAheadChunks()
    {
        const string transferId = "transfer_v6_contiguous_destination";
        const string sessionId = "session_v6_contiguous_destination";
        var payload = Enumerable.Range(1, 12).Select(static value => (byte)value).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new WriteOnlyNonSeekableMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "v6-contiguous.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "v6-contiguous.bin", payload.Length, 4, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
            frame.MissingRanges.Count == 1 &&
            frame.MissingRanges[0].StartChunkIndex == 0 &&
            frame.MissingRanges[0].ChunkCount == 1));

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 1,
                ChunkCount = 1,
                DataSegments = [payload.Skip(4).Take(4).ToArray()],
            },
            CancellationToken.None);
        await Task.Delay(200);

        Assert.Equal(0, receiver.Snapshot.Inbound!.BytesTransferred);
        Assert.Empty(destination.ToArray());

        for (var chunkIndex = 0; chunkIndex < 3; chunkIndex++)
        {
            await senderSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = chunkIndex,
                    ChunkCount = 1,
                    DataSegments = [payload.Skip(chunkIndex * 4).Take(4).ToArray()],
                },
                CancellationToken.None);
        }

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 5000);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
    }

    private static async Task<IFileTransferDataSession> StartManualOutboundV6SenderAsync(
        SessionFileTransferService sender,
        LoopbackFileTransferTransport senderTransport,
        LoopbackFileTransferTransport receiverTransport,
        string transferId,
        byte[] payload,
        int? chunkSizeBytes = null)
    {
        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-manual-sender.bin", payload.Length, transferId, chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);
        return await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
    }

    private static async Task<FileTransferReceivedDataFrame> ReceiveProbeAsync(IFileTransferDataSession session)
    {
        using var cts = new CancellationTokenSource(5000);
        while (true)
        {
            var received = await session.ReceiveWithMetadataAsync(cts.Token);
            if (received.Frame is FileTransferTransportProbeFrameV6)
            {
                return received;
            }
        }
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

    private static async Task<FileTransferChunkBatchFrameV6> ReceiveChunkBatchAsync(
        IFileTransferDataSession session,
        int startChunkIndex)
    {
        using var cts = new CancellationTokenSource(5000);
        while (true)
        {
            var received = await session.ReceiveWithMetadataAsync(cts.Token);
            if (received.Frame is FileTransferChunkBatchFrameV6 batch &&
                batch.StartChunkIndex == startChunkIndex)
            {
                return batch;
            }
        }
    }

    private static async Task<IFileTransferDataSession> StartManualInboundV6ReceiverAsync(
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
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, openWriteStreamAsync, CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.AwaitingMetadata);
        await senderTransport.SendFileTransferSessionOpenAsync(
            new FileTransferSessionOpenV2
            {
                SessionId = sessionId,
                TransferId = transferId,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                SessionRole = FileTransferProtocol.SessionRoleSender,
                ChunkSizeBytes = 4,
                InitialPipelineDepth = 1,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State is FileTransferTransferState.AwaitingMetadata or FileTransferTransferState.Receiving);
        return await senderTransport.OpenFileTransferDataSessionAsync(sessionId, transferId, CancellationToken.None);
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

    private sealed class WriteOnlyNonSeekableMemoryStream : Stream
    {
        private readonly MemoryStream inner = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => inner.ToArray();

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

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

    private sealed class BlockingSparseStream : Stream
    {
        private readonly MemoryStream inner = new();
        private readonly TaskCompletionSource writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseWrites = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public Task WaitForWriteStartedAsync()
            => writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void ReleaseWrites()
            => releaseWrites.TrySetResult();

        public override void Flush()
            => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
            => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin)
            => inner.Seek(offset, origin);

        public override void SetLength(long value)
            => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
            => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => new(WriteAsyncCore(buffer, cancellationToken));

        private async Task WriteAsyncCore(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            writeStarted.TrySetResult();
            await releaseWrites.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            inner.Write(buffer.Span);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                releaseWrites.TrySetResult();
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
