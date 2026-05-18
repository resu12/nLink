using System.Reflection;
using System.Security.Cryptography;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferV4SenderTests : SessionFileTransferServiceTestBase
{
    private const string RetiredV4CreditRepairRuntimeSkip =
        "Retired: Phase 3 V6 receiver-driven runtime no longer uses V4 credit/repair scheduling; covered by SessionFileTransferV6RuntimeTests.";
    private const string DeferredV6TransportEpochRuntimeSkip =
        "Deferred: Phase 4/5 will replace these transport epoch and Tuna handoff expectations with proof-based V6 tests.";

    [Fact]
    public async Task PrimaryRegularNknBulkV6_CompletesEndToEnd_WithSparseReceiverIntegrity()
    {
        const string transferId = "transfer_v4_sender_e2e";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_e2e");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_e2e");
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
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
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, sessionOpen.ProtocolVersion);
        Assert.All(senderTransport.SentDataFrames, static frame =>
            Assert.True(FileTransferProtocol.IsV6DataFrame(frame), $"Expected only V6 sender data frames, got {frame.Type}."));
        Assert.All(receiverTransport.SentDataFrames, static frame =>
            Assert.True(FileTransferProtocol.IsV6DataFrame(frame), $"Expected only V6 receiver data frames, got {frame.Type}."));
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferManifestFrameV6);
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferChunkBatchFrameV6);
        Assert.Contains(receiverTransport.SentDataFrames, static frame => frame is FileTransferReceiverStateFrameV6);
        Assert.Contains(receiverTransport.SentCompletes, complete =>
            string.Equals(complete.TransferId, transferId, StringComparison.Ordinal) &&
            complete.FileSizeBytes == payload.Length);
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferCompleteFrameV6);
        Assert.DoesNotContain(
            receiverTransport.SentDataFrames,
            static frame => frame is not FileTransferReceiverStateFrameV6 and not FileTransferFrontierRequestFrameV6);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.Contains("runtime_profile=PrimaryRegularNknBulkV6", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_regular_nkn_sparse_runtime_selected", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_state; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("state=manifest_exchange", logTail, StringComparison.Ordinal);
        Assert.Contains("state=awaiting_receiver_state", logTail, StringComparison.Ordinal);
        Assert.Contains("state=credit_granted", logTail, StringComparison.Ordinal);
        Assert.Contains("state=sending_bulk", logTail, StringComparison.Ordinal);
        Assert.Contains("state=completed", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_state; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.Contains("state=awaiting_manifest", logTail, StringComparison.Ordinal);
        Assert.Contains("state=receiving_bulk", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_TunaActivationProbeOverSparseReceiverAcksAndRecovers()
    {
        const string transferId = "transfer_primary_regular_nkn_bulk_v6_tuna_probe_ack";
        const string sessionId = "session_primary_regular_nkn_bulk_v6_tuna_probe_ack";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 1_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.Connect(receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideWithLaneAsync = (target, frame, _, _) =>
        {
            if (frame is FileTransferTransportProbeFrameV6)
            {
                target.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
            }

            return Task.FromResult(false);
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new DelayedWriteMemoryStream(delayMilliseconds: 1);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("primary-regular-nkn-bulk-v6-tuna-probe.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(),
            timeoutMs: 5000);
        await sender.PauseTransferAsync(transferId, "test_tuna_activation_probe", CancellationToken.None);

        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);

        await WaitUntilAsync(
            () => receiverTransport.SentTransportProbes.Any(
                probe => string.Equals(probe.TransferId, transferId, StringComparison.Ordinal) &&
                         string.Equals(probe.TargetTransport, "tuna", StringComparison.OrdinalIgnoreCase)),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_transport_probe_ack_sent; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.Contains("target_transport=tuna", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("reason=proof_timeout", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_ResumeDuringTunaHandoffKeepsRepairEligible()
    {
        const string transferId = "transfer_primary_regular_nkn_bulk_v6_resume_handoff";
        const string sessionId = "session_primary_regular_nkn_bulk_v6_resume_handoff";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("primary-regular-nkn-bulk-v6-resume-handoff.bin", payload.Length, transferId),
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

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex >= 48),
            timeoutMs: 5000);

        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);

        Assert.NotNull(await sender.PauseTransferAsync(transferId, "test_tuna_activation_pause", CancellationToken.None));
        Assert.NotNull(await sender.ResumeTransferAsync(transferId, "test_tuna_activation_resume", CancellationToken.None));

        var repairBatchCountBefore = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 24,
                DurableReceivedHighestChunkIndex = 63,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 24, ChunkCount = 12 }],
                BytesCommitted = 24 * 21 * 1024,
                TransportEpoch = 1,
                RepairRequestId = "v6-state-frontier:1:resume-handoff",
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(repairBatchCountBefore)
                .Any(static batch =>
                    batch.StartChunkIndex == 24 &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain("reason=not_yet_sent; chunk_index=24", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v4_repair_sent; transfer_id=transfer_primary_regular_nkn_bulk_v6_resume_handoff; repair_request_key=24:12:24:12; range_count=1; requested_chunk_count=12; sent_chunk_count=0", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_TunaHandoffFrontierAtAcceptedTailResumesNormalSend()
    {
        const string transferId = "transfer_primary_regular_nkn_bulk_v6_handoff_frontier_accepted";
        const string sessionId = "session_primary_regular_nkn_bulk_v6_handoff_frontier_accepted";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("primary-regular-nkn-bulk-v6-handoff-frontier-accepted.bin", payload.Length, transferId),
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

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Any(static batch => batch.StartChunkIndex >= 48),
            timeoutMs: 5000);
        Assert.NotNull(await sender.PauseTransferAsync(transferId, "test_tuna_activation_pause", CancellationToken.None));
        var acceptedTail = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .Where(static batch => batch.RepairRequestId is null)
            .Select(static batch => batch.StartChunkIndex + batch.ChunkCount)
            .DefaultIfEmpty(0)
            .Max();
        Assert.InRange(acceptedTail, 1, 800);

        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);
        Assert.NotNull(await sender.ResumeTransferAsync(transferId, "test_tuna_activation_resume", CancellationToken.None));

        var logAfterHandoffState = GetOperationalLogLength();
        var batchCountBeforeHandoffProof = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = acceptedTail,
                DurableReceivedHighestChunkIndex = acceptedTail - 1,
                CreditUntilChunkIndexExclusive = acceptedTail + 64,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = acceptedTail, ChunkCount = 12 }],
                BytesCommitted = acceptedTail * 21 * 1024,
                TransportEpoch = 1,
                RepairRequestId = "v6-state-frontier:1:caught-up",
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logAfterHandoffState).Contains("event=filetransfer_v6_epoch_tail_caught_up_to_accepted;", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforeHandoffProof)
                .Any(batch =>
                    batch.StartChunkIndex >= acceptedTail &&
                    batch.RepairRequestId is null &&
                    batch.RecoveryMode is null),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        var proofLog = ReadOperationalLogTail(logAfterHandoffState);
        Assert.Contains("event=filetransfer_v6_epoch_tail_unblocked; direction=outbound;", logTail, StringComparison.Ordinal);
        Assert.Contains("reason=frontier_caught_up_to_accepted_tail", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_tail_blocked_until_frontier_proof; direction=outbound;", proofLog, StringComparison.Ordinal);
        Assert.DoesNotContain($"reason=not_yet_sent; chunk_index={acceptedTail}", proofLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_TunaSafetyReplayProofRecoversAndCurrentReceiverRangeWins()
    {
        const string transferId = "transfer_primary_regular_nkn_bulk_v6_safety_replay_proof";
        const string sessionId = "session_primary_regular_nkn_bulk_v6_safety_replay_proof";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 4_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("primary-regular-nkn-bulk-v6-safety-replay.bin", payload.Length, transferId),
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

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 128,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Any(static batch => batch.RepairRequestId is null && batch.StartChunkIndex >= 96),
            timeoutMs: 5000);

        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);
        var handoffEpoch = senderTransport.SentTransportEpochs.Last().TransportEpoch;

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Any(batch =>
                    batch.TransportEpoch == handoffEpoch &&
                    batch.RepairRequestId is not null &&
                    batch.RepairRequestId.StartsWith("transport_rebind_safety_replay:", StringComparison.Ordinal)),
            timeoutMs: 5000);
        var safetyReplayBatch = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .First(batch =>
                batch.TransportEpoch == handoffEpoch &&
                batch.RepairRequestId is not null &&
                batch.RepairRequestId.StartsWith("transport_rebind_safety_replay:", StringComparison.Ordinal));
        var safetyReplayRepairId = safetyReplayBatch.RepairRequestId!;
        var proofCommittedChunk = safetyReplayBatch.StartChunkIndex + safetyReplayBatch.ChunkCount;

        await receiverTransport.SendFileTransferRepairProofAsync(
            new FileTransferRepairProofV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                TransportEpoch = handoffEpoch,
                RepairRequestId = safetyReplayRepairId,
                AppliedChunkCount = safetyReplayBatch.ChunkCount,
                CommittedChunkIndex = proofCommittedChunk,
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);

        var repairBatchCountAfterProof = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        var currentRepairId = $"v6-state-frontier:{handoffEpoch}:current-gap";
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = proofCommittedChunk,
                DurableReceivedHighestChunkIndex = proofCommittedChunk + 31,
                CreditUntilChunkIndexExclusive = 128,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = proofCommittedChunk, ChunkCount = 12 }],
                BytesCommitted = proofCommittedChunk * 21 * 1024,
                TransportEpoch = handoffEpoch,
                RepairRequestId = currentRepairId,
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(repairBatchCountAfterProof)
                .Any(batch =>
                    batch.StartChunkIndex == proofCommittedChunk &&
                    string.Equals(batch.RepairRequestId, currentRepairId, StringComparison.Ordinal)),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_epoch_repair_request_registered; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("source=transport_rebind_safety_replay", logTail, StringComparison.Ordinal);
        Assert.Contains("reason=frontier_repair_proof", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain($"repair_request_id={safetyReplayRepairId}; current_transport_epoch={handoffEpoch}; last_repair_request_id=(none); reason=repair_request_mismatch", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6RegularNknSparseRuntimeFlag_UsesSparseCreditEngineUnderV6Envelope()
    {
        const string transferId = "transfer_v6_sparse_runtime_flag";
        const string envName = "NLINK_FILETRANSFER_V6_REGULAR_NKN_SPARSE_RUNTIME";
        var previous = Environment.GetEnvironmentVariable(envName);
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_flag");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_flag");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            Environment.SetEnvironmentVariable(envName, "1");
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 15000);

            Assert.Equal(payload, destination.ToArray()[..payload.Length]);
            Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferChunkBatchFrameV6);
            Assert.Contains(receiverTransport.SentDataFrames, static frame => frame is FileTransferReceiverStateFrameV6);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_regular_nkn_sparse_runtime_selected; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_regular_nkn_sparse_runtime_selected; direction=inbound", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_primary_regular_nkn_bulk_v6_selected", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_primary_regular_nkn_bulk_v6_state", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v4_chunk_batch_sent;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v4_finalize_started;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previous);
        }
    }

    [Fact]
    public async Task V6RegularNknSparseRuntimeFlag_RequestsV4FileOnlyFrontierRepairsUnderV6Envelope()
    {
        const string transferId = "transfer_v6_sparse_runtime_file_only_repair";
        const string envName = "NLINK_FILETRANSFER_V6_REGULAR_NKN_SPARSE_RUNTIME";
        const int expectedFrontierRepairChunks = 12;
        var previous = Environment.GetEnvironmentVariable(envName);
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_file_only_repair");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_file_only_repair");
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(
                frame is FileTransferChunkBatchFrameV6 { RepairRequestId: null } batch &&
                batch.StartChunkIndex < FileTransferProtocol.MaxStateMissingChunksV4);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            Environment.SetEnvironmentVariable(envName, "1");
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-file-only-repair.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

            await WaitUntilAsync(
                () => receiverTransport.SentDataFrames
                    .OfType<FileTransferReceiverStateFrameV6>()
                    .Any(state => state.MissingRanges.Any(range =>
                        range.StartChunkIndex == 0 &&
                        range.ChunkCount == expectedFrontierRepairChunks)),
                timeoutMs: 15000);

            var repairState = receiverTransport.SentDataFrames
                .OfType<FileTransferReceiverStateFrameV6>()
                .First(state => state.MissingRanges.Any(range => range.StartChunkIndex == 0));
            Assert.Equal(expectedFrontierRepairChunks, repairState.MissingRanges[0].ChunkCount);
            Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame.GetType() == typeof(FileTransferStateFrameV4));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previous);
        }
    }

    [Fact]
    public async Task V6RegularNknSparseRuntimeLegacyFramesFlag_UsesV4DataFramesUnderV6Envelope()
    {
        const string transferId = "transfer_v6_sparse_runtime_legacy_frames";
        const string sparseEnvName = "NLINK_FILETRANSFER_V6_REGULAR_NKN_SPARSE_RUNTIME";
        const string legacyEnvName = "NLINK_FILETRANSFER_V6_REGULAR_NKN_SPARSE_RUNTIME_LEGACY_FRAMES";
        var previousSparse = Environment.GetEnvironmentVariable(sparseEnvName);
        var previousLegacy = Environment.GetEnvironmentVariable(legacyEnvName);
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_legacy_frames");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_legacy_frames");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            Environment.SetEnvironmentVariable(sparseEnvName, "1");
            Environment.SetEnvironmentVariable(legacyEnvName, "1");
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-legacy-frames.bin", payload.Length, transferId),
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
            Assert.Equal(FileTransferProtocol.ProtocolVersionV6, sessionOpen.ProtocolVersion);
            Assert.Contains(senderTransport.SentDataFrames, static frame => frame.GetType() == typeof(FileTransferManifestFrameV4));
            Assert.Contains(senderTransport.SentDataFrames, static frame => frame.GetType() == typeof(FileTransferChunkBatchFrameV4));
            Assert.Contains(receiverTransport.SentDataFrames, static frame => frame.GetType() == typeof(FileTransferStateFrameV4));
            Assert.DoesNotContain(senderTransport.SentDataFrames, static frame => frame is FileTransferChunkBatchFrameV6);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sparseEnvName, previousSparse);
            Environment.SetEnvironmentVariable(legacyEnvName, previousLegacy);
        }
    }

    [Fact]
    public async Task V6RegularNknSparseRuntimeFlag_DefersPrimaryRegularNknPeerSilence()
    {
        const string transferId = "transfer_v6_sparse_runtime_peer_silence";
        const string envName = "NLINK_FILETRANSFER_V6_REGULAR_NKN_SPARSE_RUNTIME";
        var previous = Environment.GetEnvironmentVariable(envName);
        var previousTimeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropReceiverFeedback = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_peer_silence");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_peer_silence");
        senderTransport.DataSessionSendDelayMs = 20;
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            Environment.SetEnvironmentVariable(envName, "1");
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromMilliseconds(300);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-peer-silence.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                      senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
                timeoutMs: 5000);

            Volatile.Write(ref dropReceiverFeedback, 1);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v4_peer_feedback_timeout_deferred_for_v6_regular_nkn_sparse_runtime",
                    StringComparison.Ordinal),
                timeoutMs: 5000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain("event=filetransfer_v4_peer_feedback_timeout; transfer_id=transfer_v6_sparse_runtime_peer_silence", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousTimeout;
            Environment.SetEnvironmentVariable(envName, previous);
        }
    }

    [Fact]
    public async Task V6RegularNknSparseRuntimeFlag_DefersReceiveRecoveryWhenFeedbackStallsWithUsableCredit()
    {
        const string transferId = "transfer_v6_sparse_runtime_feedback_recovery";
        const string envName = "NLINK_FILETRANSFER_V6_REGULAR_NKN_SPARSE_RUNTIME";
        var previous = Environment.GetEnvironmentVariable(envName);
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousFeedbackDelay = SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests;
        var previousStaleCreditDelay = SessionFileTransferService.V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelayOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropReceiverFeedback = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_feedback_recovery");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_feedback_recovery");
        senderTransport.DataSessionSendDelayMs = 100;
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            Environment.SetEnvironmentVariable(envName, "1");
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromSeconds(2);
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelayOverrideForTests = TimeSpan.FromSeconds(30);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-feedback-recovery.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                      senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
                timeoutMs: 5000);

            Volatile.Write(ref dropReceiverFeedback, 1);
            var sawFeedbackDeferral = false;
            await WaitUntilAsync(
                () =>
                {
                    sawFeedbackDeferral = ReadOperationalLogTail(logStart).Contains(
                        "event=filetransfer_v4_sparse_runtime_sender_feedback_stale_receive_recovery_deferred",
                        StringComparison.Ordinal);
                    return sawFeedbackDeferral;
                },
                timeoutMs: 5000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v4_sparse_runtime_sender_feedback_stale_receive_recovery_deferred", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                senderTransport.ReceiveRecoveryRequests,
                request => request.Direction == FileTransferDirection.Outbound &&
                           string.Equals(request.TransferId, transferId, StringComparison.Ordinal) &&
                           string.Equals(request.Reason, "sender_request_feedback_stalled", StringComparison.Ordinal));
            Assert.DoesNotContain("event=filetransfer_v4_sparse_runtime_sender_feedback_stale_receive_recovery_requested", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain($"event=filetransfer_v4_peer_feedback_timeout; transfer_id={transferId}", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_epoch_started; direction=outbound", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = previousFeedbackDelay;
            SessionFileTransferService.V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelayOverrideForTests = previousStaleCreditDelay;
            Environment.SetEnvironmentVariable(envName, previous);
        }
    }

    [Fact]
    public async Task V6RegularNknSparseRuntimeFlag_UsesStateRefreshWhenUsableCreditTurnsStale()
    {
        const string transferId = "transfer_v6_sparse_runtime_stale_credit_quiet";
        const string envName = "NLINK_FILETRANSFER_V6_REGULAR_NKN_SPARSE_RUNTIME";
        var previous = Environment.GetEnvironmentVariable(envName);
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousFeedbackDelay = SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests;
        var previousStaleCreditDelay = SessionFileTransferService.V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelayOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropReceiverFeedback = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_stale_credit_quiet");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_stale_credit_quiet");
        senderTransport.DataSessionSendDelayMs = 100;
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            Environment.SetEnvironmentVariable(envName, "1");
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromSeconds(5);
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelayOverrideForTests = TimeSpan.FromSeconds(30);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-stale-credit-quiet.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                      senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
                timeoutMs: 5000);

            Volatile.Write(ref dropReceiverFeedback, 1);
            var sawFeedbackDeferral = false;
            await WaitUntilAsync(
                () =>
                {
                    sawFeedbackDeferral = ReadOperationalLogTail(logStart).Contains(
                        "event=filetransfer_v4_sparse_runtime_sender_feedback_stale_receive_recovery_deferred",
                        StringComparison.Ordinal);
                    return sawFeedbackDeferral;
                },
                timeoutMs: 5000);

            SessionFileTransferService.V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(150);
            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(static frame =>
                    string.Equals(frame.RecoveryMode, "regular_nkn_state_refresh", StringComparison.Ordinal)),
                timeoutMs: 10000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.True(sawFeedbackDeferral);
            Assert.DoesNotContain("event=filetransfer_v4_sparse_runtime_sender_feedback_stale_receive_recovery_requested", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_regular_nkn_state_refresh_requested", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=feedback_stalled_with_credit", logTail, StringComparison.Ordinal);
            var refresh = senderTransport.SentDataFrames
                .OfType<FileTransferFrontierRequestFrameV6>()
                .First(static frame => string.Equals(frame.RecoveryMode, "regular_nkn_state_refresh", StringComparison.Ordinal));
            var refreshHint = Assert.Single(refresh.MissingRanges);
            Assert.Equal(1, refreshHint.ChunkCount);
            Assert.NotEmpty(FileTransferDataFrameCodec.Serialize(refresh));
            Assert.DoesNotContain(
                senderTransport.ReceiveRecoveryRequests,
                request => request.Direction == FileTransferDirection.Outbound &&
                           string.Equals(request.TransferId, transferId, StringComparison.Ordinal) &&
                           string.Equals(request.Reason, "sender_request_feedback_stalled", StringComparison.Ordinal));
            Assert.DoesNotContain($"event=filetransfer_v4_peer_feedback_timeout; transfer_id={transferId}", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = previousFeedbackDelay;
            SessionFileTransferService.V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelayOverrideForTests = previousStaleCreditDelay;
            Environment.SetEnvironmentVariable(envName, previous);
        }
    }

    [Fact]
    public async Task V6RegularNknSparseRuntimeFlag_SendsStateRefreshWhenFeedbackStallsWithoutCredit()
    {
        const string transferId = "transfer_v6_sparse_runtime_state_refresh";
        const string sessionId = "session_v6_sparse_runtime_state_refresh";
        const string envName = "NLINK_FILETRANSFER_V6_REGULAR_NKN_SPARSE_RUNTIME";
        var previous = Environment.GetEnvironmentVariable(envName);
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousFeedbackDelay = SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests;
        var previousRefreshCooldown = SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropReceiverFeedback = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.DataSessionSendDelayMs = 40;
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            Environment.SetEnvironmentVariable(envName, "1");
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromSeconds(5);
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = TimeSpan.FromMilliseconds(100);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-state-refresh.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                      senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
                timeoutMs: 5000);

            senderTransport.ReceiveDeliveredDataFrame(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 10_000,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 0,
                    MissingRanges = [],
                    BytesCommitted = 0,
                });
            Volatile.Write(ref dropReceiverFeedback, 1);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(static frame =>
                    string.Equals(frame.RecoveryMode, "regular_nkn_state_refresh", StringComparison.Ordinal)),
                timeoutMs: 10000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            var refresh = senderTransport.SentDataFrames
                .OfType<FileTransferFrontierRequestFrameV6>()
                .First(frame => string.Equals(frame.RecoveryMode, "regular_nkn_state_refresh", StringComparison.Ordinal));
            Assert.Equal("state_refresh", refresh.Priority);
            Assert.StartsWith("v6-regular-nkn-state-refresh:", refresh.RepairRequestId, StringComparison.Ordinal);
            var refreshHint = Assert.Single(refresh.MissingRanges);
            Assert.Equal(1, refreshHint.ChunkCount);
            Assert.NotEmpty(FileTransferDataFrameCodec.Serialize(refresh));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_regular_nkn_state_refresh_requested", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                senderTransport.ReceiveRecoveryRequests,
                request => request.Direction == FileTransferDirection.Outbound &&
                           string.Equals(request.TransferId, transferId, StringComparison.Ordinal) &&
                           string.Equals(request.Reason, "sender_request_feedback_stalled", StringComparison.Ordinal));
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = previousFeedbackDelay;
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = previousRefreshCooldown;
            Environment.SetEnvironmentVariable(envName, previous);
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_UsesCheckpointSyncWhenFeedbackStallsWithoutCredit()
    {
        const string transferId = "transfer_v6_bulk_checkpoint_no_credit";
        const string sessionId = "session_v6_bulk_checkpoint_no_credit";
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousFeedbackDelay = SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests;
        var previousRefreshCooldown = SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropReceiverFeedback = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.DataSessionSendDelayMs = 40;
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromSeconds(5);
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = TimeSpan.FromMilliseconds(100);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-bulk-checkpoint-no-credit.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                      senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
                timeoutMs: 5000);

            senderTransport.ReceiveDeliveredDataFrame(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 10_000,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 0,
                    MissingRanges = [],
                    BytesCommitted = 0,
                });
            Volatile.Write(ref dropReceiverFeedback, 1);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(static frame =>
                    string.Equals(frame.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal)),
                timeoutMs: 10000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            var checkpoint = senderTransport.SentDataFrames
                .OfType<FileTransferFrontierRequestFrameV6>()
                .First(frame => string.Equals(frame.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal));
            Assert.Equal("checkpoint_sync", checkpoint.Priority);
            Assert.StartsWith("v6-regular-nkn-checkpoint-sync:", checkpoint.RepairRequestId, StringComparison.Ordinal);
            var refreshHint = Assert.Single(checkpoint.MissingRanges);
            Assert.Equal(1, refreshHint.ChunkCount);
            Assert.NotEmpty(FileTransferDataFrameCodec.Serialize(checkpoint));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_prepared", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_sent", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_bridge_recovery_policy_selected; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.Contains("bridge_recovery_policy=primary_regular_nkn_quiet", logTail, StringComparison.Ordinal);
            Assert.Contains("state=checkpoint_sync_requested", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_regular_nkn_state_refresh_requested", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                senderTransport.ReceiveRecoveryRequests,
                request => request.Direction == FileTransferDirection.Outbound &&
                           string.Equals(request.TransferId, transferId, StringComparison.Ordinal));
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = previousFeedbackDelay;
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = previousRefreshCooldown;
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_CheckpointProofAfterTunaEpochRecovers()
    {
        const string transferId = "transfer_v6_bulk_checkpoint_epoch_proof";
        const string sessionId = "session_v6_bulk_checkpoint_epoch_proof";
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousFeedbackDelay = SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests;
        var previousRefreshCooldown = SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropReceiverFeedback = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.DataSessionSendDelayMs = 40;
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromSeconds(5);
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = TimeSpan.FromMilliseconds(100);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-bulk-checkpoint-epoch-proof.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                      senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
                timeoutMs: 5000);

            senderTransport.ReceiveDeliveredDataFrame(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 10_000,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 0,
                    MissingRanges = [],
                    BytesCommitted = 0,
                });
            Volatile.Write(ref dropReceiverFeedback, 1);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(static frame =>
                    string.Equals(frame.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal)),
                timeoutMs: 10000);

            var checkpoint = senderTransport.SentDataFrames
                .OfType<FileTransferFrontierRequestFrameV6>()
                .First(frame => string.Equals(frame.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal));
            Assert.StartsWith("v6-regular-nkn-checkpoint-sync:", checkpoint.RepairRequestId, StringComparison.Ordinal);

            senderTransport.RequestAllDataSessionHandoffs(
                "normal_to_tuna_activation",
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna);
            await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);
            var handoffEpoch = senderTransport.SentTransportEpochs.Last().TransportEpoch;
            var checkpointHint = checkpoint.MissingRanges.Single().StartChunkIndex;
            var committed = Math.Min(payload.Length / (21 * 1024), checkpointHint + 3);

            await receiverTransport.SendFileTransferRepairProofAsync(
                new FileTransferRepairProofV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = handoffEpoch,
                    RepairRequestId = checkpoint.RepairRequestId,
                    AppliedChunkCount = 3,
                    CommittedChunkIndex = committed,
                    RecoveryMode = "target_proof_pending",
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered; direction=outbound", StringComparison.Ordinal),
                timeoutMs: 5000);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_repair_proof_unmatched_frontier_accepted; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.Contains($"repair_request_id={checkpoint.RepairRequestId}", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=frontier_repair_proof", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain($"repair_request_id={checkpoint.RepairRequestId}; current_transport_epoch={handoffEpoch}; last_repair_request_id=(none); reason=repair_request_mismatch", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = previousFeedbackDelay;
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = previousRefreshCooldown;
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_CheckpointSyncSingleSendFailureDefersBridgeRecovery()
    {
        const string transferId = "transfer_v6_bulk_checkpoint_send_failure_defer";
        const string sessionId = "session_v6_bulk_checkpoint_send_failure_defer";
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousFeedbackDelay = SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests;
        var previousRefreshCooldown = SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropReceiverFeedback = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferFrontierRequestFrameV6 frontier &&
                string.Equals(frontier.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected checkpoint sync send failure.");
            }

            return Task.FromResult(false);
        };
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromSeconds(5);
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = TimeSpan.FromSeconds(10);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-bulk-checkpoint-send-failure-defer.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                      senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
                timeoutMs: 5000);

            senderTransport.ReceiveDeliveredDataFrame(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 10_000,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 0,
                    MissingRanges = [],
                    BytesCommitted = 0,
                });
            Volatile.Write(ref dropReceiverFeedback, 1);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("suppression_reason=quiet_policy_first_failure", StringComparison.Ordinal),
                timeoutMs: 10000);
            await Task.Delay(300);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            Assert.Contains(senderTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>(), static frame =>
                string.Equals(frame.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_send_failed", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_primary_regular_nkn_frontier_feedback_failed_recoverable", logTail, StringComparison.Ordinal);
            Assert.Contains("recovery_action=defer_bridge_recovery", logTail, StringComparison.Ordinal);
            Assert.Contains("bridge_recovery_policy=primary_regular_nkn_quiet", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_receive_recovery_requested", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                senderTransport.ReceiveRecoveryRequests,
                request => request.Direction == FileTransferDirection.Outbound &&
                           string.Equals(request.TransferId, transferId, StringComparison.Ordinal));
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = previousFeedbackDelay;
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = previousRefreshCooldown;
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_CheckpointSyncSendFailureRequestsReceiveRecovery()
    {
        const string transferId = "transfer_v6_bulk_checkpoint_send_failure";
        const string sessionId = "session_v6_bulk_checkpoint_send_failure";
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousFeedbackDelay = SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests;
        var previousRefreshCooldown = SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropReceiverFeedback = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferFrontierRequestFrameV6 frontier &&
                string.Equals(frontier.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected checkpoint sync send failure.");
            }

            return Task.FromResult(false);
        };
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromSeconds(5);
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = TimeSpan.FromMilliseconds(100);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-bulk-checkpoint-send-failure.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                      senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
                timeoutMs: 5000);

            senderTransport.ReceiveDeliveredDataFrame(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 10_000,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 0,
                    MissingRanges = [],
                    BytesCommitted = 0,
                });
            Volatile.Write(ref dropReceiverFeedback, 1);

            await WaitUntilAsync(
                () => senderTransport.ReceiveRecoveryRequests.Any(request =>
                    request.Direction == FileTransferDirection.Outbound &&
                    string.Equals(request.TransferId, transferId, StringComparison.Ordinal) &&
                    string.Equals(request.Reason, "primary_regular_nkn_bulk_v6_checkpoint_sync_failed", StringComparison.Ordinal)),
                timeoutMs: 10000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            Assert.Contains(senderTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>(), static frame =>
                string.Equals(frame.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_send_failed", logTail, StringComparison.Ordinal);
            Assert.Contains("suppression_reason=quiet_policy_first_failure", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_primary_regular_nkn_frontier_feedback_failed_recoverable", logTail, StringComparison.Ordinal);
            Assert.Contains("recovery_action=request_bridge_recovery", logTail, StringComparison.Ordinal);
            Assert.Contains("bridge_recovery_policy=primary_regular_nkn_quiet", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_receive_recovery_requested", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_epoch_started; direction=outbound", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = previousFeedbackDelay;
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = previousRefreshCooldown;
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_RebindUsesCheckpointSyncWithoutTransportEpoch()
    {
        const string transferId = "transfer_v6_bulk_checkpoint_rebind";
        const string sessionId = "session_v6_bulk_checkpoint_rebind";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            DataSessionSendDelayMs = 25,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-bulk-checkpoint-rebind.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
            timeoutMs: 5000);

        var transportEpochCountBeforeRebind = senderTransport.SentTransportEpochs.Count + receiverTransport.SentTransportEpochs.Count;
        senderTransport.SetLocalDataSessionsUnavailableForTests("receive_stall_recovery");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);
        senderTransport.SetLocalDataSessionsAvailableForTests("receive_stall_recovery");

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(static frame =>
                string.Equals(frame.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal)),
            timeoutMs: 10000);

        Assert.Equal(transportEpochCountBeforeRebind, senderTransport.SentTransportEpochs.Count + receiverTransport.SentTransportEpochs.Count);
        Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_rebind_started; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_sent; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_epoch_started; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_transport_rebind_generation_started; direction=outbound", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6RegularNknSparseRuntimeFlag_DefersOutboundV6PeerLivenessTerminal()
    {
        const string transferId = "transfer_v6_sparse_runtime_v6_liveness";
        const string envName = "NLINK_FILETRANSFER_V6_REGULAR_NKN_SPARSE_RUNTIME";
        var previous = Environment.GetEnvironmentVariable(envName);
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropReceiverFeedback = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_v6_liveness");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_v6_liveness");
        senderTransport.DataSessionSendDelayMs = 20;
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        receiverTransport.OutboundHeartbeatDeliveryOverrideAsync = (_, _, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            Environment.SetEnvironmentVariable(envName, "1");
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-v6-liveness.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                      senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
                timeoutMs: 5000);

            Volatile.Write(ref dropReceiverFeedback, 1);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v6_heartbeat_timeout_deferred_for_v6_regular_nkn_sparse_runtime",
                    StringComparison.Ordinal),
                timeoutMs: 10000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain($"event=filetransfer_v6_heartbeat_timeout; direction=outbound; transfer_id={transferId}", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = previousHeartbeatInterval;
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = previousPeerLivenessTimeout;
            Environment.SetEnvironmentVariable(envName, previous);
        }
    }

    [Fact]
    public async Task V6RegularNknSparseRuntimeFlag_DefersInboundV6PeerLivenessTerminal()
    {
        const string transferId = "transfer_v6_sparse_runtime_inbound_v6_liveness";
        const string envName = "NLINK_FILETRANSFER_V6_REGULAR_NKN_SPARSE_RUNTIME";
        var previous = Environment.GetEnvironmentVariable(envName);
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropSenderTraffic = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_inbound_v6_liveness");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_inbound_v6_liveness");
        senderTransport.DataSessionSendDelayMs = 20;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropSenderTraffic) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        senderTransport.OutboundHeartbeatDeliveryOverrideAsync = (_, _, _) =>
            Task.FromResult(Volatile.Read(ref dropSenderTraffic) == 1);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            Environment.SetEnvironmentVariable(envName, "1");
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-inbound-v6-liveness.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Receiving &&
                      destination.Length > 0,
                timeoutMs: 5000);

            Volatile.Write(ref dropSenderTraffic, 1);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v6_heartbeat_timeout_deferred_for_v6_regular_nkn_sparse_runtime; direction=inbound",
                    StringComparison.Ordinal),
                timeoutMs: 10000);

            Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound?.State);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain($"event=filetransfer_v6_heartbeat_timeout; direction=inbound; transfer_id={transferId}", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = previousHeartbeatInterval;
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = previousPeerLivenessTimeout;
            Environment.SetEnvironmentVariable(envName, previous);
        }
    }

    [Fact]
    public async Task V6RegularNknSparseRuntimeFlag_PausedTransportBlocksV4SenderPump()
    {
        const string transferId = "transfer_v6_sparse_runtime_transport_pause";
        const string sessionId = "session_v6_sparse_runtime_transport_pause";
        const string envName = "NLINK_FILETRANSFER_V6_REGULAR_NKN_SPARSE_RUNTIME";
        var previous = Environment.GetEnvironmentVariable(envName);
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        try
        {
            Environment.SetEnvironmentVariable(envName, "1");
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-transport-pause.bin", payload.Length, transferId),
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

            var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 1,
                    MissingRanges = [],
                    BytesCommitted = 0,
                },
                CancellationToken.None);
            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count() == 1,
                timeoutMs: 5000);

            var chunkBatchesBeforePause = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
            senderTransport.SetLocalDataSessionsUnavailableForTests("receive_stall_recovery");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal),
                timeoutMs: 5000);

            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = 16,
                    CreditUntilChunkIndexExclusive = 32,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 8 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v4_sender_pump_transport_paused_for_v6_regular_nkn_sparse_runtime",
                    StringComparison.Ordinal),
                timeoutMs: 5000);
            await Task.Delay(300);

            Assert.Equal(chunkBatchesBeforePause, senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count());
            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previous);
        }
    }

    [Fact]
    public async Task V6RegularNknSparseRuntimeFlag_AbandonsPendingSendsAfterPausedTransportFailure()
    {
        const string transferId = "transfer_v6_sparse_runtime_abandon_pending";
        const string sessionId = "session_v6_sparse_runtime_abandon_pending";
        const string envName = "NLINK_FILETRANSFER_V6_REGULAR_NKN_SPARSE_RUNTIME";
        var previous = Environment.GetEnvironmentVariable(envName);
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 4_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var firstSendGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldSendGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunkSendAttempts = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame is not FileTransferChunkBatchFrameV6)
            {
                return false;
            }

            var attempt = Interlocked.Increment(ref chunkSendAttempts);
            if (attempt == 1)
            {
                await firstSendGate.Task.WaitAsync(ct);
                throw new ObjectDisposedException("System.Threading.SemaphoreSlim");
            }

            await heldSendGate.Task.WaitAsync(ct);
            return true;
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        try
        {
            Environment.SetEnvironmentVariable(envName, "1");
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-abandon-pending.bin", payload.Length, transferId),
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

            var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 64,
                    MissingRanges = [],
                    BytesCommitted = 0,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.ActiveDataSessionSends >= 4, timeoutMs: 5000);

            senderTransport.SetLocalDataSessionsUnavailableForTests("receive_stall_recovery");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal),
                timeoutMs: 5000);
            firstSendGate.SetResult(true);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v4_pending_transport_sends_abandoned_for_v6_regular_nkn_sparse_runtime",
                    StringComparison.Ordinal),
                timeoutMs: 5000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
        }
        finally
        {
            heldSendGate.TrySetResult(true);
            firstSendGate.TrySetResult(true);
            Environment.SetEnvironmentVariable(envName, previous);
        }
    }

    [Fact]
    public async Task V6RegularNknSparseRuntimeFlag_BoundsHungNormalTransportSends()
    {
        const string transferId = "transfer_v6_sparse_runtime_bound_normal_send";
        const string sessionId = "session_v6_sparse_runtime_bound_normal_send";
        const string envName = "NLINK_FILETRANSFER_V6_REGULAR_NKN_SPARSE_RUNTIME";
        var previous = Environment.GetEnvironmentVariable(envName);
        var previousSendTimeout = SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame is not FileTransferChunkBatchFrameV6)
            {
                return false;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return true;
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        try
        {
            Environment.SetEnvironmentVariable(envName, "1");
            SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests = TimeSpan.FromMilliseconds(100);
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-bound-normal-send.bin", payload.Length, transferId),
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

            var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 64,
                    MissingRanges = [],
                    BytesCommitted = 0,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.ActiveDataSessionSends > 0, timeoutMs: 5000);
            await WaitUntilAsync(
                () =>
                {
                    var log = ReadOperationalLogTail(logStart);
                    return log.Contains(
                               "event=filetransfer_v4_transport_send_timeout_deferred_for_v6_regular_nkn_sparse_runtime",
                               StringComparison.Ordinal) &&
                           log.Contains("repair_send=0", StringComparison.Ordinal);
                },
                timeoutMs: 5000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
        }
        finally
        {
            SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests = previousSendTimeout;
            Environment.SetEnvironmentVariable(envName, previous);
        }
    }

    [Fact]
    public async Task V4Sender_LocalCancel_RetriesControlCancelWhenFirstSignalIsLost()
    {
        const string transferId = "transfer_v4_sender_cancel_retry";
        var payload = Enumerable.Range(0, 768_000).Select(static index => (byte)(index % 251)).ToArray();
        var cancelControlAttempts = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_cancel_retry");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_cancel_retry");
        senderTransport.DataSessionSendDelayMs = 10;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(frame is FileTransferCancelFrameV6);
        senderTransport.OutboundCancelDeliveryOverrideAsync = (_, _, _) =>
        {
            var attempt = Interlocked.Increment(ref cancelControlAttempts);
            return Task.FromResult(attempt == 1);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-cancel-retry.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
            timeoutMs: 5000);

        var canceled = await sender.CancelTransferAsync(transferId, "user_canceled", CancellationToken.None);

        Assert.NotNull(canceled);
        Assert.Equal(FileTransferTransferState.Canceled, canceled!.State);
        Assert.Equal(FileTransferResultCodes.CanceledLocal, canceled.ErrorCode);
        Assert.Contains(senderTransport.SentCancels, cancel => string.Equals(cancel.TransferId, transferId, StringComparison.Ordinal));
        Assert.DoesNotContain(senderTransport.SentDataFrames, static frame => frame is FileTransferCancelFrameV6);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 6000);
        Assert.True(cancelControlAttempts >= 2);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);
    }

    [Fact]
    public async Task V6Sender_LocalCancel_DoesNotFallbackToDataCancelWhenControlPathIsLost()
    {
        const string transferId = "transfer_v5_sender_cancel_data_retry";
        var payload = Enumerable.Range(0, 1_500_000).Select(static index => (byte)(index % 251)).ToArray();
        var cancelDataAttempts = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v5_sender_cancel_data_retry");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v5_sender_cancel_data_retry");
        senderTransport.DataSessionSendDelayMs = 10;
        senderTransport.OutboundCancelDeliveryOverrideAsync = (_, _, _) => Task.FromResult(true);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is not FileTransferCancelFrameV6)
            {
                return Task.FromResult(false);
            }

            var attempt = Interlocked.Increment(ref cancelDataAttempts);
            return Task.FromResult(attempt == 1);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v5-cancel-data-retry.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
            timeoutMs: 5000);

        var canceled = await sender.CancelTransferAsync(transferId, "user_canceled", CancellationToken.None);

        Assert.NotNull(canceled);
        Assert.Equal(FileTransferTransferState.Canceled, canceled!.State);
        Assert.Contains(senderTransport.SentCancels, cancel => string.Equals(cancel.TransferId, transferId, StringComparison.Ordinal));
        Assert.Equal(0, cancelDataAttempts);
        Assert.DoesNotContain(senderTransport.SentDataFrames, static frame => frame is FileTransferCancelFrameV6);
        await Task.Delay(200);
        Assert.NotEqual(FileTransferTransferState.Canceled, receiver.Snapshot.Inbound?.State);
    }

    [Fact]
    public async Task V5Sender_LocalCancel_IgnoresCanceledCallerTokenAndUsesPriorityControl()
    {
        const string transferId = "transfer_v5_sender_cancel_priority_token";
        var payload = Enumerable.Range(0, 1_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v5_sender_cancel_priority_token");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v5_sender_cancel_priority_token");
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(frame is FileTransferCancelFrameV6);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v5-cancel-priority-token.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
            timeoutMs: 5000);

        using var canceledToken = new CancellationTokenSource();
        canceledToken.Cancel();
        var canceled = await sender.CancelTransferAsync(transferId, "user_canceled", canceledToken.Token);

        Assert.NotNull(canceled);
        Assert.Equal(FileTransferTransferState.Canceled, canceled!.State);
        Assert.Equal(FileTransferResultCodes.CanceledLocal, canceled.ErrorCode);
        Assert.Contains(senderTransport.SentCancels, cancel => string.Equals(cancel.TransferId, transferId, StringComparison.Ordinal));
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 6000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);

        var logText = ReadOperationalLogText();
        Assert.Contains("event=filetransfer_lifecycle_priority_sent; kind=cancel", logText, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_lifecycle_priority_received; kind=cancel", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V5Receiver_RemoteCancelBypassesBlockedLifecycleTail()
    {
        const string transferId = "transfer_v5_cancel_bypasses_lifecycle";
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v5_cancel_bypasses_lifecycle");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v5_cancel_bypasses_lifecycle");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v5-cancel-bypasses-lifecycle.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Receiving,
            timeoutMs: 5000);

        var releaseLifecycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.InboundDispatchBeforeWorkAsyncForTests = (lane, operation) =>
            lane == "lifecycle" && operation == "complete"
                ? releaseLifecycle.Task
                : Task.CompletedTask;

        await senderTransport.SendFileTransferCompleteAsync(
            new FileTransferCompleteV1
            {
                SessionId = "session_v5_cancel_bypasses_lifecycle",
                TransferId = transferId,
                FileSizeBytes = payload.Length,
                Sha256Base64 = Convert.ToBase64String(SHA256.HashData(payload)),
            },
            CancellationToken.None);

        var canceled = await sender.CancelTransferAsync(transferId, "user_canceled", CancellationToken.None);

        Assert.NotNull(canceled);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 3000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);

        releaseLifecycle.SetResult();
    }

    [Fact]
    public async Task V4Sender_SessionEndCancel_NotifiesReceiverWhileTransferIsActive()
    {
        const string transferId = "transfer_v4_sender_session_end_cancel";
        var payload = Enumerable.Range(0, 5_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_session_end_cancel");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_session_end_cancel");
        senderTransport.DataSessionSendDelayMs = 20;
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-session-end-cancel.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
            timeoutMs: 5000);

        var canceledCount = await sender.CancelActiveTransfersForSessionEndAsync("session_end", CancellationToken.None);

        Assert.Equal(1, canceledCount);
        Assert.Equal(FileTransferTransferState.Canceled, sender.Snapshot.Outbound?.State);
        Assert.Equal(FileTransferResultCodes.CanceledLocal, sender.Snapshot.Outbound?.ErrorCode);
        Assert.Contains(senderTransport.SentCancels, cancel => string.Equals(cancel.Reason, "session_end", StringComparison.Ordinal));
        Assert.DoesNotContain(senderTransport.SentDataFrames, static frame => frame is FileTransferCancelFrameV6);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 6000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_PeerSilence_TerminalsInsteadOfSendingForever()
    {
        const string transferId = "transfer_v4_sender_peer_silence";
        var previousTimeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromMilliseconds(300);
        SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = TimeSpan.FromMilliseconds(50);
        SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromMilliseconds(300);
        try
        {
            var payload = Enumerable.Range(0, 5_000_000).Select(static index => (byte)(index % 251)).ToArray();
            var dropPeerLiveness = 0;
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_peer_silence");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_peer_silence");
            senderTransport.DataSessionSendDelayMs = 100;
            receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
                Task.FromResult(Volatile.Read(ref dropPeerLiveness) == 1 && frame is FileTransferReceiverStateFrameV6);
            receiverTransport.OutboundHeartbeatDeliveryOverrideAsync = (_, _, _) =>
                Task.FromResult(Volatile.Read(ref dropPeerLiveness) == 1);
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            using var destination = new NonDisposingMemoryStream();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-sender-peer-silence.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                      senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
                timeoutMs: 5000);
            Volatile.Write(ref dropPeerLiveness, 1);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Failed, timeoutMs: 5000);

            Assert.Equal(FileTransferResultCodes.PeerDisconnected, sender.Snapshot.Outbound!.ErrorCode);
            Assert.Contains("Peer disconnected", sender.Snapshot.Outbound.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousTimeout;
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = previousHeartbeatInterval;
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = previousPeerLivenessTimeout;
        }
    }

    [Fact]
    public async Task V4Sender_ControlReceiveStallExhausted_StartsV6RegularNknRecoveryWithoutSessionDisconnect()
    {
        const string transferId = "transfer_v4_sender_control_stall_exhausted";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 5_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_control_stall_exhausted");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_control_stall_exhausted");
        senderTransport.DataSessionSendDelayMs = 5;
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-control-stall-exhausted.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
            timeoutMs: 5000);

        senderTransport.SetLocalDataSessionsUnavailableForTests("control_receive_stalled_max_restarts");

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_control_channel_stalled_recovery; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentTransportEpochs.Any(static epoch =>
                epoch.HandoffKind == "regular_nkn_recovery" &&
                epoch.TargetTransport == "regular_nkn"),
            timeoutMs: 5000);

        var outbound = sender.Snapshot.Outbound;
        Assert.NotNull(outbound);
        Assert.NotEqual(FileTransferTransferState.Failed, outbound!.State);
        Assert.NotEqual(FileTransferResultCodes.ControlChannelStalled, outbound.ErrorCode);
        Assert.DoesNotContain(senderTransport.SentErrors, static error => error.ErrorCode == FileTransferResultCodes.ControlChannelStalled);
        Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound?.State);
    }

    [Fact]
    public async Task V4Sender_WithScreenshare_FailsCleanlyWhenMixedDisabledByEnvironment()
    {
        const string transferId = "transfer_v4_sender_mixed_disabled";
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "0");
        try
        {
            var payload = Enumerable.Range(0, 64_000).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_disabled");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_disabled");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            sender.SetSessionScreenShareActive(true);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-mixed-disabled.bin", payload.Length, transferId),
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

            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Failed, timeoutMs: 5000);
            Assert.Equal(FileTransferResultCodes.V4FileOnlyRequired, sender.Snapshot.Outbound?.ErrorCode);
            Assert.DoesNotContain(senderTransport.SentDataFrames, static frame => frame is FileTransferManifestFrameV6);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_WithScreenshareAndMixedFlag_CompletesWithTwoChunkNormalBatches()
    {
        const string transferId = "transfer_v4_sender_mixed_flag_on";
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "1");
        try
        {
            var logStart = ReadOperationalLogText().Length;
            var payload = Enumerable.Range(0, 192_000).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_flag_on");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_flag_on");
            senderTransport.DataSessionSendDelayMs = 25;
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            sender.SetSessionScreenShareActive(true);
            receiver.SetSessionScreenShareActive(true);
            using var destination = new NonDisposingMemoryStream();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-mixed-flag-on.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.IsV4MixedScreenShareTransferActive &&
                      receiver.IsV4MixedScreenShareTransferActive,
                timeoutMs: 5000);

            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 15000);

            Assert.Equal(payload, destination.ToArray()[..payload.Length]);
            var normalBatches = senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Where(static batch => batch.BatchProfile != "v4_repair_21k")
                .ToArray();
            Assert.NotEmpty(normalBatches);
            Assert.All(normalBatches, static batch =>
            {
                Assert.InRange(batch.DataSegments.Count, 1, 2);
                Assert.Equal("v4_default_21k_2x", batch.BatchProfile);
            });
            Assert.Contains(normalBatches, static batch => batch.DataSegments.Count == 2);
            Assert.Contains(
                normalBatches,
                static batch => batch.DataSegments.Count == 2 &&
                    batch.DataSegments[0].Length == 21 * 1024 &&
                    batch.DataSegments[1].Length == 21 * 1024);

            var log = ReadOperationalLogTail(logStart);
            Assert.Contains($"event=filetransfer_v6_mixed_enabled; transfer_id={transferId}", log, StringComparison.Ordinal);
            Assert.Contains("normal_batch_segments=2", log, StringComparison.Ordinal);
            Assert.Contains("mixed_screenshare=1", log, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_WithMixedFlag_LatchesMixedPacingAfterScreenshareWasObserved()
    {
        const string transferId = "transfer_v4_sender_mixed_latched";
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "1");
        try
        {
            var logStart = ReadOperationalLogText().Length;
            var payload = Enumerable.Range(0, 128_000).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_latched");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_latched");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            sender.SetSessionScreenShareActive(true);
            receiver.SetSessionScreenShareActive(true);
            sender.SetSessionScreenShareActive(false);
            receiver.SetSessionScreenShareActive(false);
            using var destination = new NonDisposingMemoryStream();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-mixed-latched.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 15000);

            Assert.Equal(payload, destination.ToArray()[..payload.Length]);
            var normalBatches = senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Where(static batch => batch.BatchProfile != "v4_repair_21k")
                .ToArray();
            Assert.NotEmpty(normalBatches);
            Assert.All(normalBatches, static batch =>
            {
                Assert.InRange(batch.DataSegments.Count, 1, 2);
                Assert.Equal("v4_default_21k_2x", batch.BatchProfile);
            });
            Assert.Contains(normalBatches, static batch => batch.DataSegments.Count == 2);

            var log = ReadOperationalLogTail(logStart);
            Assert.Contains($"event=filetransfer_v6_mixed_enabled; transfer_id={transferId}", log, StringComparison.Ordinal);
            Assert.Contains("screen_share_active=0", log, StringComparison.Ordinal);
            Assert.Contains("screen_share_observed=1", log, StringComparison.Ordinal);
            Assert.Contains("normal_batch_segments=2", log, StringComparison.Ordinal);
            Assert.Contains("mixed_screenshare=1", log, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_WithDegradedScreenshareAndMixedFlag_UsesTwoChunkNormalBatches()
    {
        const string transferId = "transfer_v4_sender_mixed_degraded";
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "1");
        try
        {
            var logStart = ReadOperationalLogText().Length;
            var payload = Enumerable.Range(0, 128_000).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_degraded");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_degraded");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            sender.SetSessionScreenShareActive(true);
            receiver.SetSessionScreenShareActive(true);
            sender.SetSessionScreenShareDegraded(true);
            receiver.SetSessionScreenShareDegraded(true);
            using var destination = new NonDisposingMemoryStream();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-mixed-degraded.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 15000);

            Assert.Equal(payload, destination.ToArray()[..payload.Length]);
            var normalBatches = senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Where(static batch => batch.BatchProfile != "v4_repair_21k")
                .ToArray();
            Assert.NotEmpty(normalBatches);
            Assert.All(normalBatches, static batch =>
            {
                Assert.InRange(batch.DataSegments.Count, 1, 2);
                Assert.Equal("v4_default_21k_2x", batch.BatchProfile);
            });
            Assert.Contains(normalBatches, static batch => batch.DataSegments.Count == 2);

            var log = ReadOperationalLogTail(logStart);
            Assert.Contains($"event=filetransfer_v6_mixed_enabled; transfer_id={transferId}", log, StringComparison.Ordinal);
            Assert.Contains("screen_share_degraded=1", log, StringComparison.Ordinal);
            Assert.Contains("normal_batch_segments=2", log, StringComparison.Ordinal);
            Assert.Contains("mixed_screenshare=1", log, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_MissingRangesSchedulePriorityRepair_AndComplete()
    {
        const string transferId = "transfer_v4_sender_repair_priority";
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 239)).ToArray();
        var droppedFirstBatch = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_priority");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_priority");
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, _) =>
        {
            if (frame is FileTransferChunkBatchFrameV6 { StartChunkIndex: 0 } &&
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
        Assert.True(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count(static batch => batch.StartChunkIndex == 0) >= 2);
        Assert.Contains(
            senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>(),
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

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V4Sender_TransportRebind_QueuesBoundedSafetyReplayFromPeerFrontier()
    {
        const string transferId = "transfer_v4_sender_transport_rebind_replay";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_transport_rebind_replay");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_transport_rebind_replay");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-transport-rebind-replay.bin", payload.Length, transferId),
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

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
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
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count(static batch => batch.StartChunkIndex == 0) == 1,
            timeoutMs: 5000);

        var batchCountBeforeRebind = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        senderTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_transport_rebind_safety_replay_started;",
                StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforeRebind)
                .Any(static batch =>
                    batch.StartChunkIndex == 0 &&
                    batch.BatchProfile == "v4_repair_21k" &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_transport_rebind_generation_started; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_safety_replay_started;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_tail_blocked_until_frontier_proof; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_mode=control_bulk_escalated", log, StringComparison.Ordinal);
        Assert.Contains("requested_chunk_count=1", log, StringComparison.Ordinal);
        Assert.Contains("replay_chunk_cap=64", log, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V4Sender_TransportRebind_RearmsSafetyReplayWhenNoFeedbackArrives()
    {
        const string transferId = "transfer_v4_sender_transport_rebind_rearm";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_transport_rebind_rearm");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_transport_rebind_rearm");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-transport-rebind-rearm.bin", payload.Length, transferId),
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

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
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
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count(static batch => batch.StartChunkIndex == 0) == 1,
            timeoutMs: 5000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await WaitUntilAsync(
            () => CountOccurrences(
                ReadOperationalLogTail(logStart),
                "event=filetransfer_transport_rebind_safety_replay_started;") >= 2,
            timeoutMs: 9000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_rebind_safety_replay_rearmed;", log, StringComparison.Ordinal);
        Assert.Contains("reason=post_fallback_sender_wait", log, StringComparison.Ordinal);
        Assert.True(
            senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Count(static batch =>
                    batch.StartChunkIndex == 0 &&
                    batch.BatchProfile == "v4_repair_21k" &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant) >= 2);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V4Sender_PostRebindZeroCredit_EmergencyReplaysExactFrontier()
    {
        const string transferId = "transfer_v4_sender_rebind_zero_credit_emergency";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_rebind_zero_credit_emergency");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_rebind_zero_credit_emergency");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-rebind-zero-credit-emergency.bin", payload.Length, transferId),
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

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 0,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        senderTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Any(static batch =>
                    batch.StartChunkIndex == 0 &&
                    batch.ChunkCount == 1 &&
                    batch.BatchProfile == "v4_repair_21k" &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_transport_rebind_emergency_frontier_replay;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_safety_replay_started;", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_escalation_reason=transport_rebind_emergency_frontier", log, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V4Sender_PostRebindDuplicateFrontierState_ReplaysOnlyExactFrontier()
    {
        const string transferId = "transfer_v4_sender_rebind_frontier_only";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_rebind_frontier_only");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_rebind_frontier_only");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-rebind-frontier-only.bin", payload.Length, transferId),
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

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
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
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0), timeoutMs: 5000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_rebind_generation_started; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);

        var missingFrontierState = new FileTransferReceiverStateFrameV6
        {
            SessionId = offer.SessionId,
            TransferId = transferId,
            Epoch = 2,
                ContiguousCommittedChunkIndex = 4,
                DurableReceivedHighestChunkIndex = 16,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 4, ChunkCount = 32 }],
                BytesCommitted = 4 * 21 * 1024,
            };
        await receiverSession.SendAsync(missingFrontierState, CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Any(static batch =>
                    batch.StartChunkIndex == 4 &&
                    batch.ChunkCount == 1 &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var batchCountBeforeDuplicate = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        var duplicateStart = ReadOperationalLogText().Length;
        await receiverSession.SendAsync(missingFrontierState, CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(duplicateStart).Contains("event=filetransfer_transport_rebind_frontier_only_replay;", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforeDuplicate)
                .Any(static batch =>
                    batch.StartChunkIndex == 4 &&
                    batch.ChunkCount == 1 &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var fullLog = ReadOperationalLogTail(logStart);
        var duplicateLog = ReadOperationalLogTail(duplicateStart);
        Assert.Contains("event=filetransfer_transport_rebind_frontier_only_started;", fullLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_frontier_only_replay;", duplicateLog, StringComparison.Ordinal);
        Assert.Contains("replay_start_chunk_index=4; replay_end_chunk_exclusive=5; requested_chunk_count=1", duplicateLog, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_escalation_reason=transport_rebind_frontier_only", duplicateLog, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V5Sender_HandoffRecoveryUnblocksTailAndIgnoresStaleRecoveryFrames()
    {
        const string transferId = "transfer_v5_handoff_tail_unblock";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 1_024_000).Select(static index => (byte)(index % 239)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v5_handoff_tail_unblock");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v5_handoff_tail_unblock");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v5-handoff-tail-unblock.bin", payload.Length, transferId),
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

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0), timeoutMs: 5000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_rebind_generation_started; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 8,
                DurableReceivedHighestChunkIndex = 7,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 8 * 21 * 1024,
                TransportEpoch = 1,
            },
            CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 3,
                ContiguousCommittedChunkIndex = 16,
                DurableReceivedHighestChunkIndex = 15,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 16 * 21 * 1024,
                TransportEpoch = 1,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_handoff_tail_unblocked; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Any(static batch =>
                    batch.StartChunkIndex >= 16 &&
                    batch.TransportEpoch == 0 &&
                    batch.RepairRequestId is null &&
                    batch.RecoveryMode is null),
            timeoutMs: 5000);

        var staleStart = ReadOperationalLogText().Length;
        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                TransportEpoch = 1,
                RepairRequestId = "stale-after-recovered",
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 8, ChunkCount = 1 }],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(staleStart).Contains("reason=recovered_epoch", StringComparison.Ordinal),
            timeoutMs: 5000);

        var staleLog = ReadOperationalLogTail(staleStart);
        Assert.Contains("event=filetransfer_v6_recovery_frame_ignored; direction=outbound;", staleLog, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_tail_blocked_until_frontier_proof; direction=outbound;", staleLog, StringComparison.Ordinal);

        var reopenedStart = ReadOperationalLogText().Length;
        var batchCountBeforeReopen = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                TransportEpoch = 1,
                RepairRequestId = "frontier-after-recovered",
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 16, ChunkCount = 1 }],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(reopenedStart).Contains("event=filetransfer_v6_handoff_epoch_reopened; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforeReopen)
                .Any(static batch =>
                    batch.StartChunkIndex == 16 &&
                    batch.TransportEpoch == 1 &&
                    string.Equals(batch.RecoveryMode, "frontier_repair_only", StringComparison.Ordinal)),
            timeoutMs: 5000);

        var reopenedLog = ReadOperationalLogTail(reopenedStart);
        Assert.Contains("event=filetransfer_v6_frontier_repair_requested; direction=outbound;", reopenedLog, StringComparison.Ordinal);
        Assert.DoesNotContain("reason=recovered_epoch", reopenedLog, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V5Sender_HandoffSparseBackfillKeepsTailBlockedAndSendsRepairWindow()
    {
        const string transferId = "transfer_v5_sparse_backfill_window";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 1_024_000).Select(static index => (byte)(index % 237)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v5_sparse_backfill_window");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v5_sparse_backfill_window");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v5-sparse-backfill-window.bin", payload.Length, transferId),
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

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0), timeoutMs: 5000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_rebind_generation_started; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);

        var batchCountBeforeSparseProof = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 8,
                DurableReceivedHighestChunkIndex = 31,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 8 * 21 * 1024,
                TransportEpoch = 1,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("reason=frontier_proof_with_sparse_backfill", StringComparison.Ordinal),
            timeoutMs: 5000);
        var sparseProofLog = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain("event=filetransfer_v6_handoff_tail_unblocked; direction=outbound;", sparseProofLog, StringComparison.Ordinal);

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                TransportEpoch = 1,
                RepairRequestId = "backfill-window",
                Priority = "backfill",
                RecoveryMode = "backfill_repair",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 8, ChunkCount = 12 }],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforeSparseProof)
                .Any(static batch =>
                    batch.StartChunkIndex == 8 &&
                    batch.ChunkCount > 1 &&
                    batch.TransportEpoch == 1 &&
                    string.Equals(batch.RepairRequestId, "backfill-window", StringComparison.Ordinal) &&
                    string.Equals(batch.RecoveryMode, "backfill_repair", StringComparison.Ordinal)),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_frontier_repair_requested; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("priority=backfill", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_handoff_tail_unblocked; direction=outbound;", log, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V5Sender_NormalToTunaActivationStartsSymmetricHandoffAndBlocksTail()
    {
        const string transferId = "transfer_v5_tuna_activation_handoff";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 1_024_000).Select(static index => (byte)(index % 241)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v5_tuna_activation_handoff");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v5_tuna_activation_handoff");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v5-tuna-activation-handoff.bin", payload.Length, transferId),
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

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 8,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0), timeoutMs: 5000);

        var batchCountBeforeHandoff = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_activation_negotiated",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_handoff_epoch_started; direction=outbound;", StringComparison.Ordinal) &&
                  ReadOperationalLogTail(logStart).Contains("handoff_kind=normal_to_tuna_activation", StringComparison.Ordinal) &&
                  ReadOperationalLogTail(logStart).Contains("target_transport=tuna", StringComparison.Ordinal),
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 4,
                DurableReceivedHighestChunkIndex = 16,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 4, ChunkCount = 12 }],
                BytesCommitted = 4 * 21 * 1024,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforeHandoff)
                .Any(static batch =>
                    batch.StartChunkIndex == 4 &&
                    batch.ChunkCount == 1 &&
                    batch.TransportEpoch > 0 &&
                    string.Equals(batch.RecoveryMode, "frontier_repair_only", StringComparison.Ordinal)),
            timeoutMs: 5000);

        var newBatches = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .Skip(batchCountBeforeHandoff)
            .ToArray();
        Assert.DoesNotContain(
            newBatches,
            static batch => batch.StartChunkIndex > 4 &&
                            batch.RepairRequestId is null &&
                            string.IsNullOrWhiteSpace(batch.RecoveryMode));

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_tail_blocked_until_frontier_proof; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("target_transport=tuna", log, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V5Sender_DuplicateTargetHandoffReusesEpochAndAcceptsPeerRepair()
    {
        const string transferId = "transfer_v5_duplicate_handoff_epoch";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 1_024_000).Select(static index => (byte)(index % 233)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v5_duplicate_handoff_epoch");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v5_duplicate_handoff_epoch");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v5-duplicate-handoff-epoch.bin", payload.Length, transferId),
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

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 8,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0), timeoutMs: 5000);

        senderTransport.RequestAllDataSessionHandoffs(
            "header_switch_off",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.TransportEpoch > 0),
            timeoutMs: 5000);
        var handoffEpoch = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .First(static batch => batch.TransportEpoch > 0)
            .TransportEpoch;

        senderTransport.RequestAllDataSessionHandoffs(
            "header_switch_off_duplicate_ready",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_handoff_epoch_reused; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                TransportEpoch = handoffEpoch,
                RepairRequestId = "duplicate-target-frontier",
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains($"event=filetransfer_v6_frontier_repair_requested; direction=outbound; transfer_id={transferId};", StringComparison.Ordinal) &&
                  ReadOperationalLogTail(logStart).Contains($"transport_epoch={handoffEpoch}", StringComparison.Ordinal),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_handoff_epoch_reused; direction=outbound;", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"reason=stale_or_mismatched_epoch; frame_transport_epoch={handoffEpoch}", log, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V4FileOnlyTransfer_TransportRebindSafetyReplay_FillsDroppedFrontierAndCompletes()
    {
        const string transferId = "transfer_v4_transport_rebind_e2e";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 768_000).Select(static index => (byte)(index % 229)).ToArray();
        var allowFrontierChunk = 0;
        var droppedFrontierBatchCount = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_transport_rebind_e2e");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_transport_rebind_e2e");
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferChunkBatchFrameV6 { StartChunkIndex: 0 } &&
                Volatile.Read(ref allowFrontierChunk) == 0)
            {
                Interlocked.Increment(ref droppedFrontierBatchCount);
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
            new FileTransferSendDescriptor("v4-transport-rebind-e2e.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static state =>
                state.MissingRanges.Any(static range =>
                    range.StartChunkIndex == 0 &&
                    range.ChunkCount > 0)),
            timeoutMs: 10000);
        Assert.True(Volatile.Read(ref droppedFrontierBatchCount) > 0);

        Volatile.Write(ref allowFrontierChunk, 1);
        senderTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_transport_rebind_generation_started; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_generation_started; direction=inbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_state_forced; direction=inbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_safety_replay_started;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_recovered; direction=inbound;", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
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
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
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
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        var repairState = new FileTransferReceiverStateFrameV6
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
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_state_received; transfer_id=transfer_v4_sender_repair_dedupe; session_id=session_v4_sender_repair_dedupe; epoch=2; previous_epoch=2; duplicate=1; applied=0", StringComparison.Ordinal), timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Equal(1, CountOccurrences(log, "event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_repair_dedupe"));
        Assert.Contains("duplicate=1; applied=0", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v4_repair_suppressed; transfer_id=transfer_v4_sender_repair_dedupe", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
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
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
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
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        var repairState = new FileTransferReceiverStateFrameV6
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
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                batch.BatchProfile == "v4_repair_21k" &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.BulkOnly),
            timeoutMs: 5000);

        await Task.Delay(900);
        await receiverSession.SendAsync(repairState with { Epoch = 3 }, CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                batch.BatchProfile == "v4_repair_21k" &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("repair_delivery_mode=control_bulk_escalated", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_escalation_reason=frontier_not_advanced", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_MaxBatchSegmentsEnvironment_CapsNormalBatches()
    {
        const string transferId = "transfer_v4_sender_batch_cap";
        const string envName = "NLINK_FILETRANSFER_V4_MAX_BATCH_SEGMENTS";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
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
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
            await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

            var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
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
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Sum(static batch => batch.DataSegments.Count) >= 12,
                timeoutMs: 5000);

            var batches = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().ToArray();
            Assert.NotEmpty(batches);
            Assert.All(batches, static batch => Assert.InRange(batch.DataSegments.Count, 1, 2));
            Assert.Contains(batches, static batch => batch.DataSegments.Count == 2 && batch.BatchProfile == "v4_default_21k_2x");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
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
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
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
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
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
            new FileTransferReceiverStateFrameV6
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

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_SuppressesRepair_FromStaleSameFrontierMissingRanges()
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
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
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
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
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
        Assert.Contains("reason=stale_epoch", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_stale_state_missing_ranges_applied; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_TreatsSameEpochReceiverStateAsDuplicate()
    {
        const string transferId = "v4_duplicate_epoch";
        const string sessionId = "session_v4_duplicate_epoch";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 211)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-duplicate-epoch.bin", payload.Length, transferId),
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

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
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
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 10,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 1,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
                TransferPaused = true,
                TransferPauseReason = "duplicate_pause",
            },
            CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id={sessionId}; epoch=10; previous_epoch=10; duplicate=1; applied=0", StringComparison.Ordinal), timeoutMs: 5000);
        await Task.Delay(250);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id={sessionId}; epoch=10; previous_epoch=10; duplicate=1; applied=0", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.False(sender.Snapshot.Outbound!.IsPeerPaused);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
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
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
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
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
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

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V4Sender_PostRebind_SuppressesStaleRepairStateBehindCurrentFrontier()
    {
        const string transferId = "v4_post_rebind_stale_behind";
        const string sessionId = "session_v4_post_rebind_stale_behind";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 211)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-post-rebind-stale-behind.bin", payload.Length, transferId),
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

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
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
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_rebind_generation_started; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 4,
                DurableReceivedHighestChunkIndex = 16,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges = [],
                BytesCommitted = 4 * 21 * 1024,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains($"contiguous_committed_chunk_index=4", StringComparison.Ordinal), timeoutMs: 5000);
        var staleStart = ReadOperationalLogText().Length;

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 16,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(staleStart).Contains($"event=filetransfer_v4_stale_state_missing_ranges_suppressed; transfer_id={transferId}", StringComparison.Ordinal),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(staleStart);
        Assert.Contains($"event=filetransfer_v4_stale_state_missing_ranges_suppressed; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("reason=frontier_moved", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_rebind_duplicate_state_repair_enqueued; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
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
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
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
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static frame =>
                frame.StartChunkIndex <= 10 &&
                frame.StartChunkIndex + frame.ChunkCount > 10),
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
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

    [Fact]
    public async Task V4Sender_PostRebindMissingRanges_SchedulesExactFrontierRepairOnly()
    {
        await Task.CompletedTask;
        var method = typeof(SessionFileTransferService).GetMethod(
            "SelectV4RepairRangesForSend",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var ranges = new[]
        {
            new FileTransferRangeV4 { StartChunkIndex = 16, ChunkCount = 3 },
            new FileTransferRangeV4 { StartChunkIndex = 30, ChunkCount = 4 },
        };

        var selected = Assert.IsAssignableFrom<IReadOnlyList<FileTransferRangeV4>>(
            method!.Invoke(null, [ranges, 16, FileTransferProtocol.MaxStateMissingChunksV4, true]));

        var range = Assert.Single(selected);
        Assert.Equal(16, range.StartChunkIndex);
        Assert.Equal(3, range.ChunkCount);
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
