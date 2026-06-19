using System.Security.Cryptography;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferV6TransportEpochTests : SessionFileTransferServiceTestBase
{
    [Theory]
    [InlineData("sender_request_feedback_stalled")]
    [InlineData("peer_liveness_stale_receive_recovery")]
    [InlineData("core_filetransfer_request")]
    public void V6ReceiveRecoveryPauseReasons_ExtendTransportRecoveryGrace(string reason)
    {
        var method = typeof(SessionFileTransferService).GetMethod(
            "IsTunaFallbackTransportPauseReason",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        Assert.True((bool)method.Invoke(null, [reason])!);
    }

    [Fact]
    public async Task V6Epoch_RegularNknRecoveryFrontierRequestOverTargetTransportRecoversOutboundEpoch()
    {
        const string transferId = "transfer_v6_epoch_regular_nkn_frontier_control_proof";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_regular_nkn_frontier_control_proof");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_regular_nkn_frontier_control_proof");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        senderTransport.RequestAllDataSessionHandoffs(
            "receive_stall_recovery",
            FileTransferTransportHandoffKind.RegularNknRecovery,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);

        var probe = await ReceiveProbeAsync(receiverSession, "regular_nkn");
        var probeFrame = Assert.IsType<FileTransferTransportProbeFrameV6>(probe.Frame);
        senderTransport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                TransportEpoch = probeFrame.TransportEpoch,
                RepairRequestId = $"v6-frontier:{probeFrame.TransportEpoch}:0:1",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("reason=frontier_request_control_proof", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(batch =>
                batch.TransportEpoch == probeFrame.TransportEpoch &&
                batch.StartChunkIndex == 0 &&
                string.Equals(batch.Priority, "frontier", StringComparison.OrdinalIgnoreCase) &&
                batch.ForceRegularNknBulk &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_epoch_recovered; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("handoff_kind=regular_nkn_recovery", logTail, StringComparison.Ordinal);
        Assert.Contains("target_transport=regular_nkn", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_TunaFallbackReceiverStateOverTargetTransportRecoversOutboundEpoch()
    {
        const string transferId = "transfer_v6_epoch_tuna_fallback_receiver_state_control_proof";
        const string sessionId = "session_v6_epoch_tuna_fallback_receiver_state_control_proof";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_to_normal_fallback",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);
        var epoch = senderTransport.SentTransportEpochs.Last();

        senderTransport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
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
                TransportEpoch = epoch.TransportEpoch,
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("reason=receiver_state_control_proof", StringComparison.Ordinal),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_epoch_recovered; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("handoff_kind=tuna_to_normal_fallback", logTail, StringComparison.Ordinal);
        Assert.Contains("target_transport=regular_nkn", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_TunaFallbackLegacyV4StateOverTargetTransportRecoversOutboundEpoch()
    {
        const string transferId = "transfer_v6_epoch_tuna_fallback_legacy_v4_state_proof";
        const string sessionId = "session_v6_epoch_tuna_fallback_legacy_v4_state_proof";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_to_normal_fallback",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);

        senderTransport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
        await receiverSession.SendAsync(
            new FileTransferStateFrameV4
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 1,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("reason=regular_nkn_legacy_v4_state_proof", StringComparison.Ordinal),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_regular_nkn_legacy_v4_state_proof_accepted; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_epoch_recovered; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("handoff_kind=tuna_to_normal_fallback", logTail, StringComparison.Ordinal);
        Assert.Contains("target_transport=regular_nkn", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_TunaFallbackRegularNknChunkProbeRecoversInboundEpochWithoutFrontierProgress()
    {
        const string transferId = "transfer_v6_epoch_tuna_fallback_stale_chunk_probe";
        const string sessionId = "session_v6_epoch_tuna_fallback_stale_chunk_probe";
        const int chunkSize = 4;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 16).Select(static index => (byte)(index + 1)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsPostTunaFileFallbackActiveForRouteSelection = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsPostTunaFileFallbackActiveForRouteSelection = true,
        };
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundPostTunaFallbackV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "post-fallback-stale-probe.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "post-fallback-stale-probe.bin", payload.Length, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(), timeoutMs: 5000);

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = [payload.Take(chunkSize).ToArray()],
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.ContiguousCommittedChunkIndex >= 1),
            timeoutMs: 5000);

        receiverTransport.RequestAllDataSessionHandoffs(
            "tuna_to_normal_fallback",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(frame => frame.TransportEpoch > 0),
            timeoutMs: 5000);
        var frontierRequest = receiverTransport.SentDataFrames
            .OfType<FileTransferFrontierRequestFrameV6>()
            .Last(frame => frame.TransportEpoch > 0);

        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = frontierRequest.TransportEpoch,
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = [payload.Take(chunkSize).ToArray()],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("reason=regular_nkn_chunk_probe", StringComparison.Ordinal),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_regular_nkn_chunk_probe_accepted; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_epoch_recovered; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.Contains("handoff_kind=tuna_to_normal_fallback", logTail, StringComparison.Ordinal);
        Assert.Contains("target_transport=regular_nkn", logTail, StringComparison.Ordinal);
        Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound?.State);
    }

    [Fact]
    public async Task V6Epoch_TunaFallbackLateLegacyV4ChunkOverTargetTransportRecoversInboundEpoch()
    {
        const string transferId = "transfer_v6_epoch_tuna_fallback_late_v4_chunk_probe";
        const string sessionId = "session_v6_epoch_tuna_fallback_late_v4_chunk_probe";
        const int chunkSize = 4;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 16).Select(static index => (byte)(index + 1)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsPostTunaFileFallbackActiveForRouteSelection = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsPostTunaFileFallbackActiveForRouteSelection = true,
        };
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundPostTunaFallbackV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "post-fallback-late-v4-proof.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "post-fallback-late-v4-proof.bin", payload.Length, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(), timeoutMs: 5000);

        receiverTransport.RequestAllDataSessionHandoffs(
            "tuna_to_normal_fallback",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(frame => frame.TransportEpoch > 0),
            timeoutMs: 5000);

        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV4
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = [payload.Take(chunkSize).ToArray()],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("reason=regular_nkn_legacy_v4_chunk_probe", StringComparison.Ordinal),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_regular_nkn_legacy_v4_chunk_probe_accepted; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_epoch_recovered; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.Contains("handoff_kind=tuna_to_normal_fallback", logTail, StringComparison.Ordinal);
        Assert.Contains("target_transport=regular_nkn", logTail, StringComparison.Ordinal);
        Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound?.State);
    }

    [Fact]
    public async Task V6Epoch_PeerTunaFallbackEpochPromotesInboundFileTunaV4AndAcceptsV6Chunk()
    {
        const string transferId = "transfer_v6_epoch_peer_promotes_inbound_file_tuna_v4";
        const string sessionId = "session_v6_epoch_peer_promotes_inbound_file_tuna_v4";
        const int chunkSize = 4;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 16).Select(static index => (byte)(index + 1)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsFileTunaActiveForRouteSelection = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsFileTunaActiveForRouteSelection = true,
        };
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundFileTunaV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "peer-promoted-file-tuna-v4.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateV4Manifest(sessionId, transferId, "peer-promoted-file-tuna-v4.bin", payload.Length, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(frame =>
                frame.TransferId == transferId &&
                frame.MissingRanges.Count > 0),
            timeoutMs: 5000);

        await senderTransport.SendFileTransferTransportEpochAsync(
            new FileTransferTransportEpochV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 1,
                State = "target_proof_pending",
                HandoffKind = "tuna_to_normal_fallback",
                SourceTransport = "tuna",
                TargetTransport = "regular_nkn",
                Reason = "tuna_to_normal_fallback",
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_peer_epoch_promoted_live_route; direction=inbound", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(frame =>
                frame.TransferId == transferId &&
                frame.TransportEpoch == 1 &&
                frame.MissingRanges.Count > 0),
            timeoutMs: 5000);
        var frontierRequest = receiverTransport.SentDataFrames
            .OfType<FileTransferFrontierRequestFrameV6>()
            .Last(frame => frame.TransferId == transferId && frame.TransportEpoch == 1 && frame.MissingRanges.Count > 0);
        var firstChunk = frontierRequest.MissingRanges[0].StartChunkIndex;

        senderTransport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = frontierRequest.TransportEpoch,
                RepairRequestId = frontierRequest.RepairRequestId,
                StartChunkIndex = firstChunk,
                ChunkCount = 1,
                DataSegments = [payload.Skip(firstChunk * chunkSize).Take(chunkSize).ToArray()],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(frame =>
                frame.TransferId == transferId &&
                frame.ContiguousCommittedChunkIndex > firstChunk),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_route_selected; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.Contains("route=post_tuna_fallback_v6; protocol_version=6", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_peer_epoch_promoted_live_route; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_chunk_batch_received", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"event=filetransfer_data_frame_ignored; transfer_id={transferId}; session_id={sessionId}; frame_type=filetransfer.chunk_batch.v6; reason=protocol_not_v4",
            logTail,
            StringComparison.Ordinal);
        Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound?.State);
    }

    [Fact]
    public async Task V6Epoch_CurrentPostTunaFallbackLiveRoutePromotesPlainV6Chunk()
    {
        const string transferId = "transfer_v6_epoch_live_route_plain_v6_chunk";
        const string sessionId = "session_v6_epoch_live_route_plain_v6_chunk";
        const int chunkSize = 4;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 16).Select(static index => (byte)(index + 1)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsFileTunaActiveForRouteSelection = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsFileTunaActiveForRouteSelection = true,
        };
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundFileTunaV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "live-route-plain-v6-chunk.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateV4Manifest(sessionId, transferId, "live-route-plain-v6-chunk.bin", payload.Length, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(frame =>
                frame.TransferId == transferId &&
                frame.MissingRanges.Count > 0),
            timeoutMs: 5000);

        var inboundContext = typeof(SessionFileTransferService)
            .GetField("inboundTransfer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(receiver)!;
        var postTunaFallbackRoute = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var liveRouteEpoch = typeof(SessionFileTransferService)
            .GetMethod("StartLiveRouteEpoch", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(
                null,
                [
                    0,
                    postTunaFallbackRoute,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.RegularNkn,
                    "remote_header_switch_off",
                ]);
        SetPrivateProperty(inboundContext, "LastLiveRouteEpochId", 1);
        SetPrivateProperty(inboundContext, "CurrentLiveRouteEpoch", liveRouteEpoch);

        senderTransport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 1,
                StartChunkIndex = 0,
                ChunkCount = 1,
                DataSegments = [payload.Take(chunkSize).ToArray()],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_chunk_batch_received", StringComparison.Ordinal),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_peer_post_tuna_fallback_v6_proof_promoted_route; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.Contains("reason=current_post_tuna_fallback_live_route_frame", logTail, StringComparison.Ordinal);
        Assert.Contains("route=post_tuna_fallback_v6; protocol_version=6", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"event=filetransfer_data_frame_ignored; transfer_id={transferId}; session_id={sessionId}; frame_type=filetransfer.chunk_batch.v6; reason=protocol_not_v4",
            logTail,
            StringComparison.Ordinal);
        Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound?.State);
    }

    [Fact]
    public async Task V6Epoch_PeerTunaFallbackEpochKeepsLegacyV4TailChunksAsRepairData()
    {
        const string transferId = "transfer_v6_epoch_peer_promoted_v4_tail_data";
        const string sessionId = "session_v6_epoch_peer_promoted_v4_tail_data";
        const int chunkSize = 4;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 16).Select(static index => (byte)(index + 1)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsFileTunaActiveForRouteSelection = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsFileTunaActiveForRouteSelection = true,
        };
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundFileTunaV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "peer-promoted-v4-tail.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateV4Manifest(sessionId, transferId, "peer-promoted-v4-tail.bin", payload.Length, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(frame =>
                frame.TransferId == transferId &&
                frame.MissingRanges.Count > 0),
            timeoutMs: 5000);

        await senderTransport.SendFileTransferTransportEpochAsync(
            new FileTransferTransportEpochV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 1,
                State = "target_proof_pending",
                HandoffKind = "tuna_to_normal_fallback",
                SourceTransport = "tuna",
                TargetTransport = "regular_nkn",
                Reason = "tuna_to_normal_fallback",
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_peer_epoch_promoted_live_route; direction=inbound", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(frame =>
                frame.TransferId == transferId &&
                frame.TransportEpoch == 1 &&
                frame.MissingRanges.Count > 0),
            timeoutMs: 5000);
        var frontierRequest = receiverTransport.SentDataFrames
            .OfType<FileTransferFrontierRequestFrameV6>()
            .Last(frame => frame.TransferId == transferId && frame.TransportEpoch == 1 && frame.MissingRanges.Count > 0);
        var firstChunk = frontierRequest.MissingRanges[0].StartChunkIndex;

        senderTransport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV4
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = firstChunk,
                ChunkCount = 1,
                DataSegments = [payload.Skip(firstChunk * chunkSize).Take(chunkSize).ToArray()],
                RepairDeliveryMode = FileTransferV4RepairDeliveryMode.ControlBulkRedundant,
                ForceRegularNknBulk = true,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(frame =>
                frame.TransferId == transferId &&
                frame.ContiguousCommittedChunkIndex > firstChunk),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("route=post_tuna_fallback_v6; protocol_version=6", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_chunk_batch_received", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"event=filetransfer_data_frame_ignored; transfer_id={transferId}; session_id={sessionId}; frame_type=filetransfer.chunk_batch.v4; reason=protocol_not_v4",
            logTail,
            StringComparison.Ordinal);
        Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound?.State);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_TunaActivationAndFallbackUseV6EpochPath()
    {
        const string transferId = "transfer_v6_primary_bulk_tuna_epoch_guard";
        const string sessionId = "session_v6_primary_bulk_tuna_epoch_guard";
        var logStart = GetOperationalLogLength();
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
            if (frame.TransferId == transferId &&
                frame is FileTransferTransportProbeFrameV6 probe)
            {
                receiverTransport.NextDataFrameTransportKind = string.Equals(probe.TargetTransport, "tuna", StringComparison.OrdinalIgnoreCase)
                    ? FileTransferTransportKind.Tuna
                    : FileTransferTransportKind.RegularNkn;
            }

            return Task.FromResult(false);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(
            sender,
            senderTransport,
            receiverTransport,
            transferId,
            payloadSize: 1_000_000);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=outbound",
                StringComparison.Ordinal),
            timeoutMs: 5000);

        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        var tunaProbe = await ReceiveProbeAsync(receiverSession, "tuna");
        Assert.Equal(FileTransferTransportKind.Tuna, tunaProbe.TransportKind);
        var tunaProbeFrame = Assert.IsType<FileTransferTransportProbeFrameV6>(tunaProbe.Frame);
        await receiverTransport.SendFileTransferTransportProbeAsync(
            new FileTransferTransportProbeV6
            {
                SessionId = tunaProbeFrame.SessionId,
                TransferId = tunaProbeFrame.TransferId,
                TransportEpoch = tunaProbeFrame.TransportEpoch,
                ProbeId = tunaProbeFrame.ProbeId,
                TargetTransport = tunaProbeFrame.TargetTransport,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("target_transport=tuna", StringComparison.Ordinal) &&
                  ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);

        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_to_normal_fallback",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        var fallbackProbe = await ReceiveProbeAsync(receiverSession, "regular_nkn");
        var fallbackProbeFrame = Assert.IsType<FileTransferTransportProbeFrameV6>(fallbackProbe.Frame);
        await receiverTransport.SendFileTransferTransportProbeAsync(
            new FileTransferTransportProbeV6
            {
                SessionId = fallbackProbeFrame.SessionId,
                TransferId = fallbackProbeFrame.TransferId,
                TransportEpoch = fallbackProbeFrame.TransportEpoch,
                ProbeId = fallbackProbeFrame.ProbeId,
                TargetTransport = fallbackProbeFrame.TargetTransport,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("handoff_kind=tuna_to_normal_fallback", StringComparison.Ordinal) &&
                  ReadOperationalLogTail(logStart).Contains("target_transport=regular_nkn", StringComparison.Ordinal),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_epoch_started; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("handoff_kind=normal_to_tuna_activation", logTail, StringComparison.Ordinal);
        Assert.Contains("handoff_kind=tuna_to_normal_fallback", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_transport_probe_sent; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_primary_regular_nkn_bulk_v6_rebind_started; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("recovery_mode=regular_nkn_checkpoint_sync", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_RecoveredRegularNknRecoveryFrontierPriorityPreemptsNormalRequestWindowWithoutRedundantBulk()
    {
        const string transferId = "transfer_v6_epoch_recovered_frontier_keeps_normal";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_recovered_frontier_keeps_normal");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_recovered_frontier_keeps_normal");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId, payloadSize: 1_000_000);
        Assert.NotNull(await sender.PauseTransferAsync(transferId, "queue_recovered_requests", CancellationToken.None));

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
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered", StringComparison.Ordinal), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 15,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 16 }],
                BytesCommitted = 0,
                TransportEpoch = probeFrame.TransportEpoch,
            },
            CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                TransportEpoch = probeFrame.TransportEpoch,
                RepairRequestId = $"frontier:{probeFrame.TransportEpoch}:0",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "recovered",
            },
            CancellationToken.None);
        await Task.Delay(250);
        Assert.Empty(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>());

        Assert.NotNull(await sender.ResumeTransferAsync(transferId, "send_recovered_requests", CancellationToken.None));
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
            timeoutMs: 5000);

        var batches = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().ToList();
        var recoveredStatePriorityBurst = batches.First(batch =>
            batch.StartChunkIndex == 0 &&
            batch.ChunkCount > 1 &&
            batch.Priority == "frontier");
        Assert.Equal(FileTransferV4RepairDeliveryMode.BulkOnly, recoveredStatePriorityBurst.RepairDeliveryMode);
        Assert.False(recoveredStatePriorityBurst.ForceRegularNknBulk);
        var firstNormalChunkIndex = recoveredStatePriorityBurst.StartChunkIndex + recoveredStatePriorityBurst.ChunkCount;
        var normalBatches = batches
            .Where(batch => batch.Priority is null &&
                            batch.TransportEpoch == probeFrame.TransportEpoch)
            .ToList();
        Assert.All(normalBatches, batch => Assert.True(batch.StartChunkIndex >= firstNormalChunkIndex));
    }

    [Fact]
    public async Task V6Epoch_RecoveredRegularNknFallbackUsesPrimaryNormalRequestWindowsBeyondRedundantLimit()
    {
        const string transferId = "transfer_v6_epoch_regular_nkn_primary_normal";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_regular_nkn_primary_normal");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_regular_nkn_primary_normal");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId, payloadSize: 1_000_000);
        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_to_normal_fallback",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
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
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered", StringComparison.Ordinal), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = probeFrame.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 12,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 8 }],
                    BytesCommitted = 0,
                    TransportEpoch = probeFrame.TransportEpoch,
                },
                CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Where(static batch =>
                batch.BatchProfile == "v6_request_window_regular_nkn_fallback" &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.BulkOnly &&
                batch.ForceRegularNknBulk).Sum(static batch => batch.ChunkCount) >= 5,
            timeoutMs: 5000);
        Assert.Contains(
            "reason=regular_nkn_recovered_after_tuna_fallback",
            ReadOperationalLogTail(logStart),
            StringComparison.Ordinal);
        Assert.Contains(
            "event=filetransfer_v6_regular_nkn_degraded_profile_entered",
            ReadOperationalLogTail(logStart),
            StringComparison.Ordinal);
        Assert.Contains(
            "reason=tuna_fallback_regular_nkn_recovery",
            ReadOperationalLogTail(logStart),
            StringComparison.Ordinal);
        Assert.DoesNotContain("reason=normal_batch_limit", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_RecoveredRegularNknRecoveryDoesNotEnableRedundantRegularNknStreaming()
    {
        const string transferId = "transfer_v6_epoch_regular_nkn_recovery_no_redundant";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_regular_nkn_recovery_no_redundant");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_regular_nkn_recovery_no_redundant");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId, payloadSize: 1_000_000);
        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
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
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered", StringComparison.Ordinal), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 12,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 8 }],
                BytesCommitted = 0,
                TransportEpoch = probeFrame.TransportEpoch,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0),
            timeoutMs: 5000);

        var normalBatches = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .Where(static batch => batch.StartChunkIndex == 0)
            .ToArray();
        Assert.NotEmpty(normalBatches);
        Assert.All(normalBatches, static batch =>
        {
            Assert.Equal("v6_request_window", batch.BatchProfile);
            Assert.False(batch.ForceRegularNknBulk);
        });
        var logTail = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain("event=filetransfer_v6_regular_nkn_redundant_data_enabled", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("reason=regular_nkn_recovered_after_tuna_fallback", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_UnresolvedRegularNknRecoveryDoesNotReusePreviousRecoveredRedundantMode()
    {
        const string transferId = "transfer_v6_epoch_regular_nkn_redundant_old_epoch";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_regular_nkn_redundant_old_epoch");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_regular_nkn_redundant_old_epoch");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_to_normal_fallback",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
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
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered", StringComparison.Ordinal), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 2,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => CountOccurrences(ReadOperationalLogTail(logStart), "event=filetransfer_v6_regular_nkn_redundant_data_enabled") == 1,
            timeoutMs: 5000);

        await receiverTransport.SendFileTransferTransportEpochAsync(
            new FileTransferTransportEpochV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                TransportEpoch = probeFrame.TransportEpoch + 1,
                State = "target_proof_pending",
                HandoffKind = "regular_nkn_recovery",
                SourceTransport = "tuna",
                TargetTransport = "regular_nkn",
                Reason = "receive_stall_recovery",
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => CountOccurrences(ReadOperationalLogTail(logStart), "event=filetransfer_v6_epoch_started") >= 2,
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 1,
                DurableReceivedHighestChunkIndex = 0,
                CreditUntilChunkIndexExclusive = 3,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 1, ChunkCount = 1 }],
                BytesCommitted = 64 * 1024,
            },
            CancellationToken.None);
        await Task.Delay(250);

        Assert.Equal(
            1,
            CountOccurrences(ReadOperationalLogTail(logStart), "event=filetransfer_v6_regular_nkn_redundant_data_enabled"));
    }

    [Fact]
    public async Task V6Epoch_RecoveredRegularNknRecoveryFrontierRepairUsesControlBulk()
    {
        const string transferId = "transfer_v6_epoch_regular_nkn_frontier_control_bulk";
        const string repairRequestId = "repair:regular-nkn-recovered:frontier";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_regular_nkn_frontier_control_bulk");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_regular_nkn_frontier_control_bulk");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
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
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                TransportEpoch = probeFrame.TransportEpoch,
                RepairRequestId = repairRequestId,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "regular_nkn_frontier_stall_control_bulk",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(batch =>
                batch.TransportEpoch == probeFrame.TransportEpoch &&
                batch.StartChunkIndex == 0 &&
                string.Equals(batch.Priority, "frontier", StringComparison.OrdinalIgnoreCase) &&
                batch.RepairRequestId == repairRequestId),
            timeoutMs: 5000);

        var frontierBatch = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .First(batch =>
                batch.TransportEpoch == probeFrame.TransportEpoch &&
                batch.StartChunkIndex == 0 &&
                batch.RepairRequestId == repairRequestId);
        Assert.True(frontierBatch.ForceRegularNknBulk);
        Assert.Equal(FileTransferV4RepairDeliveryMode.ControlBulkRedundant, frontierBatch.RepairDeliveryMode);
    }

    [Fact]
    public async Task V6Epoch_RegularNknFallbackNormalTimeoutKeepsPrimaryFallbackAndFrontierProof()
    {
        var previousSendTimeout = SessionFileTransferService.V6SenderTransportSendTimeoutOverrideForTests;
        SessionFileTransferService.V6SenderTransportSendTimeoutOverrideForTests = TimeSpan.FromMilliseconds(100);
        try
        {
            const string transferId = "xfer_v6_regular_timeout";
            var logStart = GetOperationalLogLength();
            using var senderTransport = new LoopbackFileTransferTransport("sess_v6_regular_timeout");
            using var receiverTransport = new LoopbackFileTransferTransport("sess_v6_regular_timeout");
            senderTransport.Connect(receiverTransport);
            senderTransport.OutboundDataFrameDeliveryOverrideWithLaneAsync = async (_, frame, _, ct) =>
            {
                if (frame is FileTransferChunkBatchFrameV6 batch &&
                    batch.BatchProfile == "v6_request_window_regular_nkn_fallback")
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    return true;
                }

                return false;
            };

            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);

            var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
            receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
            senderTransport.RequestAllDataSessionHandoffs(
                "tuna_to_normal_fallback",
                FileTransferTransportHandoffKind.TunaToNormalFallback,
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
            await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered", StringComparison.Ordinal), timeoutMs: 5000);

            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = probeFrame.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 16,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 16 }],
                    BytesCommitted = 0,
                    TransportEpoch = probeFrame.TransportEpoch,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_chunk_batch_send_timeout", StringComparison.Ordinal),
                timeoutMs: 5000);

            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = probeFrame.SessionId,
                    TransferId = transferId,
                    Epoch = 2,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 16,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 16 }],
                    BytesCommitted = 0,
                    TransportEpoch = probeFrame.TransportEpoch,
                },
                CancellationToken.None);
            await Task.Delay(250);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Equal(
                1,
                logTail.Split(Environment.NewLine).Count(line =>
                    line.Contains("event=filetransfer_v6_regular_nkn_redundant_data_enabled", StringComparison.Ordinal) &&
                    line.Contains($"transfer_id={transferId}", StringComparison.Ordinal)));
            Assert.Contains("event=filetransfer_v6_chunk_batch_send_timeout", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_regular_nkn_redundant_data_disabled", logTail, StringComparison.Ordinal);

            await receiverSession.SendAsync(
                new FileTransferFrontierRequestFrameV6
                {
                    SessionId = probeFrame.SessionId,
                    TransferId = transferId,
                    TransportEpoch = probeFrame.TransportEpoch,
                    RepairRequestId = $"repair:{probeFrame.TransportEpoch}:0",
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                    Priority = "frontier",
                    RecoveryMode = "frontier_repair_only",
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(batch =>
                    batch.TransportEpoch == probeFrame.TransportEpoch &&
                    batch.StartChunkIndex == 0 &&
                    string.Equals(batch.Priority, "frontier", StringComparison.OrdinalIgnoreCase) &&
                    batch.ForceRegularNknBulk &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
                timeoutMs: 5000);
        }
        finally
        {
            SessionFileTransferService.V6SenderTransportSendTimeoutOverrideForTests = previousSendTimeout;
        }
    }

    [Fact]
    public async Task V6Epoch_RegularNknFrontierRepairIsNotStarvedByStuckTunaSends()
    {
        var previousSendTimeout = SessionFileTransferService.V6SenderTransportSendTimeoutOverrideForTests;
        SessionFileTransferService.V6SenderTransportSendTimeoutOverrideForTests = TimeSpan.FromMilliseconds(100);
        try
        {
            const string transferId = "transfer_v6_epoch_regular_nkn_stuck_tuna_sends";
            var logStart = GetOperationalLogLength();
            using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_regular_nkn_stuck_tuna_sends");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_regular_nkn_stuck_tuna_sends");
            senderTransport.Connect(receiverTransport);
            senderTransport.OutboundDataFrameDeliveryOverrideWithLaneAsync = async (_, frame, _, ct) =>
            {
                if (frame is FileTransferChunkBatchFrameV6 batch &&
                    !string.Equals(batch.Priority, "frontier", StringComparison.OrdinalIgnoreCase))
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
                payloadSize: 2_000_000);

            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = "session_v6_epoch_regular_nkn_stuck_tuna_sends",
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 32,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 32 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.MaxConcurrentDataSessionSends >= 4, timeoutMs: 5000);

            receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
            senderTransport.RequestAllDataSessionHandoffs(
                "receive_stall_recovery",
                FileTransferTransportHandoffKind.RegularNknRecovery,
                FileTransferTransportKind.RegularNkn);
            await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);
            var epoch = senderTransport.SentTransportEpochs.Last();
            await ReceiveProbeAsync(receiverSession);

            await receiverSession.SendAsync(
                new FileTransferFrontierRequestFrameV6
                {
                    SessionId = epoch.SessionId,
                    TransferId = transferId,
                    TransportEpoch = epoch.TransportEpoch,
                    RepairRequestId = $"repair:{epoch.TransportEpoch}:0",
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                    Priority = "frontier",
                    RecoveryMode = "frontier_repair_only",
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(batch =>
                    batch.TransportEpoch == epoch.TransportEpoch &&
                    batch.StartChunkIndex == 0 &&
                    string.Equals(batch.Priority, "frontier", StringComparison.OrdinalIgnoreCase) &&
                    batch.ForceRegularNknBulk &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
                timeoutMs: 5000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            await WaitUntilAsync(
                () =>
                {
                    var logTail = ReadOperationalLogTail(logStart);
                    return logTail.Contains("event=filetransfer_v6_chunk_batch_send_timeout", StringComparison.Ordinal) ||
                           logTail.Contains("event=filetransfer_v6_chunk_batch_send_canceled_for_pipeline", StringComparison.Ordinal) ||
                           logTail.Contains("event=filetransfer_v6_stale_prepared_batch_canceled", StringComparison.Ordinal) ||
                           logTail.Contains("event=filetransfer_v6_stale_prepared_batch_dropped", StringComparison.Ordinal);
                },
                timeoutMs: 5000);
            var finalLogTail = ReadOperationalLogTail(logStart);
            Assert.True(
                finalLogTail.Contains("event=filetransfer_v6_chunk_batch_send_timeout", StringComparison.Ordinal) ||
                finalLogTail.Contains("event=filetransfer_v6_chunk_batch_send_canceled_for_pipeline", StringComparison.Ordinal) ||
                finalLogTail.Contains("event=filetransfer_v6_stale_prepared_batch_canceled", StringComparison.Ordinal) ||
                finalLogTail.Contains("event=filetransfer_v6_stale_prepared_batch_dropped", StringComparison.Ordinal));
        }
        finally
        {
            SessionFileTransferService.V6SenderTransportSendTimeoutOverrideForTests = previousSendTimeout;
        }
    }

    [Fact]
    public async Task PostTunaFallbackV6_SendTimeoutQueuesExactFrontierRepairAndFreezesNormalSendAhead()
    {
        var previousSendTimeout = SessionFileTransferService.V6SenderTransportSendTimeoutOverrideForTests;
        SessionFileTransferService.V6SenderTransportSendTimeoutOverrideForTests = TimeSpan.FromMilliseconds(100);
        try
        {
            const string transferId = "transfer_v6_post_fallback_send_timeout_frontier";
            const string sessionId = "session_v6_post_fallback_send_timeout_frontier";
            var logStart = GetOperationalLogLength();
            using var senderTransport = new LoopbackFileTransferTransport(sessionId);
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
            senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
            receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
            senderTransport.Connect(receiverTransport);
            senderTransport.OutboundDataFrameDeliveryOverrideWithLaneAsync = async (_, frame, _, ct) =>
            {
                if (frame is FileTransferChunkBatchFrameV6 batch &&
                    !string.Equals(batch.Priority, "frontier", StringComparison.OrdinalIgnoreCase))
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
                payloadSize: 1_000_000);

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
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v6_post_tuna_fallback_send_timeout_frontier_repair_queued",
                    StringComparison.Ordinal),
                timeoutMs: 5000);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                    batch.StartChunkIndex == 0 &&
                    batch.ChunkCount == 1 &&
                    string.Equals(batch.Priority, "frontier", StringComparison.OrdinalIgnoreCase)),
                timeoutMs: 5000);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_post_tuna_fallback_send_timeout_frontier_repair_queued", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_post_tuna_fallback_normal_send_ahead_freeze_started", logTail, StringComparison.Ordinal);
            Assert.Contains("exact_frontier_requeued_chunk_count=1", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("state=Failed", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V6SenderTransportSendTimeoutOverrideForTests = previousSendTimeout;
        }
    }

    [Theory]
    [InlineData("object_disposed")]
    [InlineData("bridge_not_running")]
    public async Task V6Epoch_InFlightSendFailureDuringRecoveryDoesNotTerminalizeSender(string failureKind)
    {
        var transferId = $"transfer_v6_epoch_send_failure_deferred_{failureKind}";
        var sessionId = $"session_v6_epoch_send_failure_deferred_{failureKind}";
        var logStart = GetOperationalLogLength();
        var normalSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNormalSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var injectedFailureCount = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideWithLaneAsync = async (_, frame, _, ct) =>
        {
            if (frame is FileTransferChunkBatchFrameV6 batch &&
                batch.TransportEpoch == 0 &&
                !string.Equals(batch.Priority, "frontier", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.CompareExchange(ref injectedFailureCount, 1, 0) == 0)
            {
                normalSendStarted.TrySetResult();
                await releaseNormalSend.Task.WaitAsync(TimeSpan.FromSeconds(5));
                throw failureKind switch
                {
                    "bridge_not_running" => new InvalidOperationException("NKN bridge is not running."),
                    _ => new ObjectDisposedException("LoopbackDataSession", "Bridge disconnected during recovery."),
                };
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
            payloadSize: 2_000_000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 16 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await normalSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        senderTransport.RequestAllDataSessionHandoffs(
            "receive_stall_recovery",
            FileTransferTransportHandoffKind.RegularNknRecovery,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);
        releaseNormalSend.TrySetResult();

        await WaitUntilAsync(
            () =>
            {
                var logTail = ReadOperationalLogTail(logStart);
                return logTail.Contains("event=filetransfer_v6_chunk_batch_send_deferred_for_recovery", StringComparison.Ordinal) ||
                       logTail.Contains("event=filetransfer_v6_chunk_batch_send_canceled_for_pipeline", StringComparison.Ordinal);
            },
            timeoutMs: 5000);
        var outboundAfterFailure = sender.Snapshot.Outbound;
        Assert.NotNull(outboundAfterFailure);
        Assert.NotEqual(FileTransferTransferState.Failed, outboundAfterFailure!.State);
        Assert.Null(outboundAfterFailure.ErrorCode);
        Assert.Empty(senderTransport.SentErrors);

        var epoch = senderTransport.SentTransportEpochs.Last();
        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = epoch.TransportEpoch,
                RepairRequestId = $"repair:{epoch.TransportEpoch}:0",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(batch =>
                batch.TransportEpoch == epoch.TransportEpoch &&
                batch.StartChunkIndex == 0 &&
                string.Equals(batch.Priority, "frontier", StringComparison.OrdinalIgnoreCase) &&
                batch.ForceRegularNknBulk),
            timeoutMs: 5000);
        Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
    }

    [Fact]
    public async Task V6Epoch_PausedTransportBlocksSenderPump()
    {
        const string transferId = "transfer_v6_epoch_transport_pause_blocks_pump";
        const string sessionId = "session_v6_epoch_transport_pause_blocks_pump";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        var chunkBatchesBeforePause = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();

        senderTransport.SetLocalDataSessionsUnavailableForTests("receive_stall_recovery");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);
        senderTransport.ReceiveDeliveredDataFrame(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 8,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 8 }],
                BytesCommitted = 0,
            });
        await Task.Delay(300);

        Assert.Equal(chunkBatchesBeforePause, senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count());
        Assert.Contains("reason=transport_paused", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_RegularNknRecoveryWhileUnavailableAllowsFrontierProofChunk()
    {
        const string transferId = "transfer_v6_epoch_unavailable_regular_nkn_frontier";
        const string sessionId = "session_v6_epoch_unavailable_regular_nkn_frontier";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);

        senderTransport.SetLocalDataSessionsUnavailableForTests("receive_stall_recovery");
        senderTransport.RequestAllDataSessionHandoffs(
            "receive_stall_recovery",
            FileTransferTransportHandoffKind.RegularNknRecovery,
            FileTransferTransportKind.RegularNkn);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal) &&
                  senderTransport.SentTransportEpochs.Any(epoch => epoch.HandoffKind == "regular_nkn_recovery"),
            timeoutMs: 5000);
        var epoch = senderTransport.SentTransportEpochs.Last(epoch => epoch.HandoffKind == "regular_nkn_recovery");
        await ReceiveProbeAsync(receiverSession);

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = epoch.TransportEpoch,
                RepairRequestId = $"repair:{epoch.TransportEpoch}:0",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(batch =>
                batch.TransportEpoch == epoch.TransportEpoch &&
                batch.StartChunkIndex == 0 &&
                string.Equals(batch.Priority, "frontier", StringComparison.OrdinalIgnoreCase) &&
                batch.ForceRegularNknBulk),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("reason=frontier_request_control_proof", logTail, StringComparison.Ordinal);
        Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
    }

    [Fact]
    public async Task V6Epoch_PostTunaFallbackSuppressesRepeatedRegularNknRecoveryAfterProof()
    {
        const string transferId = "transfer_v6_epoch_post_fallback_suppresses_repeated_regular_nkn";
        const string sessionId = "session_v6_epoch_post_fallback_suppresses_repeated_regular_nkn";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);

        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_to_normal_fallback",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);

        var probe = await ReceiveProbeAsync(receiverSession);
        var probeFrame = Assert.IsType<FileTransferTransportProbeFrameV6>(probe.Frame);
        await receiverTransport.SendFileTransferTransportProbeAsync(
            new FileTransferTransportProbeV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                TransportEpoch = probeFrame.TransportEpoch,
                ProbeId = probeFrame.ProbeId,
                TargetTransport = probeFrame.TargetTransport,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered", StringComparison.Ordinal),
            timeoutMs: 5000);
        var epochCountAfterProof = senderTransport.SentTransportEpochs.Count;
        var observedRecoveredCountAfterProof = senderTransport.ObservedV6TransportEpochs.Count(static snapshot =>
            snapshot.State == V6TransportEpochState.Recovered &&
            snapshot.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
            snapshot.TargetTransport == FileTransferTransportKind.RegularNkn);

        senderTransport.SetLocalDataSessionsUnavailableForTests("sender_request_feedback_stalled");
        senderTransport.RequestAllDataSessionHandoffs(
            "sender_request_feedback_stalled",
            FileTransferTransportHandoffKind.RegularNknRecovery,
            FileTransferTransportKind.RegularNkn);
        await Task.Delay(300);

        Assert.Equal(epochCountAfterProof, senderTransport.SentTransportEpochs.Count);
        Assert.True(
            senderTransport.ObservedV6TransportEpochs.Count(static snapshot =>
                snapshot.State == V6TransportEpochState.Recovered &&
                snapshot.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                snapshot.TargetTransport == FileTransferTransportKind.RegularNkn) > observedRecoveredCountAfterProof);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_epoch_recovered_restart_suppressed", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_epoch_recovered_restart_pause_cleared", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_epoch_recovered_restart_snapshot_published", logTail, StringComparison.Ordinal);
        Assert.Contains("route=post_tuna_fallback_v6", logTail, StringComparison.Ordinal);
        Assert.Contains("was_paused=1", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_PostTunaFallbackAllowsBridgeRecoveryRegularNknRefreshAfterProof()
    {
        const string transferId = "transfer_v6_epoch_post_fallback_allows_bridge_refresh";
        const string sessionId = "session_v6_epoch_post_fallback_allows_bridge_refresh";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);

        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_to_normal_fallback",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);

        var probe = await ReceiveProbeAsync(receiverSession);
        var probeFrame = Assert.IsType<FileTransferTransportProbeFrameV6>(probe.Frame);
        await receiverTransport.SendFileTransferTransportProbeAsync(
            new FileTransferTransportProbeV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                TransportEpoch = probeFrame.TransportEpoch,
                ProbeId = probeFrame.ProbeId,
                TargetTransport = probeFrame.TargetTransport,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered", StringComparison.Ordinal),
            timeoutMs: 5000);
        var epochCountAfterProof = senderTransport.SentTransportEpochs.Count;

        senderTransport.SetLocalDataSessionsUnavailableForTests("transport_recovered_unproven");
        senderTransport.RequestAllDataSessionHandoffs(
            "transport_recovered_unproven",
            FileTransferTransportHandoffKind.RegularNknRecovery,
            FileTransferTransportKind.RegularNkn);

        await WaitUntilAsync(
            () => senderTransport.SentTransportEpochs.Count > epochCountAfterProof,
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_epoch_recovered_restart_allowed", logTail, StringComparison.Ordinal);
        Assert.Contains("allowance=post_tuna_bridge_recovery_epoch_refresh", logTail, StringComparison.Ordinal);
        Assert.Contains("reason=transport_recovered_unproven", logTail, StringComparison.Ordinal);
        Assert.Contains(
            senderTransport.SentTransportEpochs.Skip(epochCountAfterProof),
            static epoch => string.Equals(epoch.HandoffKind, "regular_nkn_recovery", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("receiver_state", "bridge_not_running")]
    [InlineData("frontier_request", "operation_canceled")]
    [InlineData("frontier_request", "bridge_not_running")]
    [InlineData("frontier_request", "plain_bridge_not_running")]
    [InlineData("frontier_request", "wrapped_bridge_not_running")]
    [InlineData("frontier_request", "aggregate_bridge_not_running")]
    public async Task V6Epoch_PostTunaFallbackSurvivalControlSendFailureDoesNotTerminalizeInbound(
        string frameKind,
        string failureKind)
    {
        var transferId = $"transfer_v6_post_fallback_control_failure_{frameKind}_{failureKind}";
        var sessionId = $"session_v6_post_fallback_control_failure_{frameKind}_{failureKind}";
        const int chunkSize = 4;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 16).Select(static index => (byte)(index + 1)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        var injectControlFailure = 0;
        var injectedFailureCount = 0;
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsPostTunaFileFallbackActiveForRouteSelection = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsPostTunaFileFallbackActiveForRouteSelection = true,
        };
        receiverTransport.OutboundDataFrameDeliveryOverrideWithLaneAsync = (_, frame, _, _) =>
        {
            var shouldFail =
                Volatile.Read(ref injectControlFailure) != 0 &&
                string.Equals(frame.TransferId, transferId, StringComparison.Ordinal) &&
                ((string.Equals(frameKind, "receiver_state", StringComparison.Ordinal) &&
                  frame is FileTransferReceiverStateFrameV6) ||
                 (string.Equals(frameKind, "frontier_request", StringComparison.Ordinal) &&
                  frame is FileTransferFrontierRequestFrameV6));
            if (!shouldFail ||
                Interlocked.CompareExchange(ref injectedFailureCount, 1, 0) != 0)
            {
                return Task.FromResult(false);
            }

            Exception failure = failureKind switch
            {
                "operation_canceled" => new OperationCanceledException("Injected non-terminal V6 control send cancellation."),
                "plain_bridge_not_running" => new Exception("NKN bridge is not running."),
                "wrapped_bridge_not_running" => new Exception("Injected wrapped send failure.", new Exception("NKN bridge is not running.")),
                "aggregate_bridge_not_running" => new AggregateException(new Exception("NKN bridge is not running.")),
                _ => new InvalidOperationException("NKN bridge is not running."),
            };
            return Task.FromException<bool>(failure);
        };
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundPostTunaFallbackV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "post-fallback-control-failure.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "post-fallback-control-failure.bin", payload.Length, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(frame =>
                frame.TransferId == transferId &&
                frame.MissingRanges.Count > 0),
            timeoutMs: 5000);
        var firstRequest = receiverTransport.SentDataFrames
            .OfType<FileTransferFrontierRequestFrameV6>()
            .Last(frame => frame.TransferId == transferId && frame.MissingRanges.Count > 0);
        var firstChunk = firstRequest.MissingRanges[0].StartChunkIndex;
        Volatile.Write(ref injectControlFailure, 1);
        await Task.Delay(600);
        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = firstRequest.TransportEpoch,
                RepairRequestId = firstRequest.RepairRequestId,
                StartChunkIndex = firstChunk,
                ChunkCount = 1,
                DataSegments = [payload.Skip(firstChunk * chunkSize).Take(chunkSize).ToArray()],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        var expectedEvent = string.Equals(frameKind, "frontier_request", StringComparison.Ordinal)
            ? "event=filetransfer_v6_frontier_request_deferred_for_recovery"
            : "event=filetransfer_v6_receiver_state_deferred_for_recovery";
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(expectedEvent, StringComparison.Ordinal),
            timeoutMs: 5000);

        Assert.Equal(1, Volatile.Read(ref injectedFailureCount));
        Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound?.State);
        Assert.Empty(receiverTransport.SentErrors);
        var finalLogTail = ReadOperationalLogTail(logStart);
        Assert.Contains("post_tuna_fallback_survival=1", finalLogTail, StringComparison.Ordinal);
        Assert.Contains("route=post_tuna_fallback_v6", finalLogTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_PostTunaFallbackFrontierCancellationRemainsRecoverableUntilTerminalCompletion()
    {
        const string transferId = "transfer_v6_post_fallback_frontier_canceled_then_completed";
        const string sessionId = "session_v6_post_fallback_frontier_canceled_then_completed";
        const int chunkSize = 4;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 16).Select(static index => (byte)(index + 1)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        var injectFrontierFailure = 0;
        var injectedFailureCount = 0;
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsPostTunaFileFallbackActiveForRouteSelection = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsPostTunaFileFallbackActiveForRouteSelection = true,
        };
        receiverTransport.OutboundDataFrameDeliveryOverrideWithLaneAsync = (_, frame, _, _) =>
        {
            var shouldFail =
                Volatile.Read(ref injectFrontierFailure) != 0 &&
                string.Equals(frame.TransferId, transferId, StringComparison.Ordinal) &&
                frame is FileTransferFrontierRequestFrameV6;
            if (!shouldFail ||
                Interlocked.CompareExchange(ref injectedFailureCount, 1, 0) != 0)
            {
                return Task.FromResult(false);
            }

            return Task.FromException<bool>(
                new OperationCanceledException("Injected recoverable post-Tuna fallback frontier cancellation."));
        };
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundPostTunaFallbackV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "post-fallback-frontier-cancel-then-complete.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "post-fallback-frontier-cancel-then-complete.bin", payload.Length, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(frame =>
                frame.TransferId == transferId &&
                frame.MissingRanges.Count > 0),
            timeoutMs: 5000);
        var firstRequest = receiverTransport.SentDataFrames
            .OfType<FileTransferFrontierRequestFrameV6>()
            .Last(frame => frame.TransferId == transferId && frame.MissingRanges.Count > 0);
        var firstChunk = firstRequest.MissingRanges[0].StartChunkIndex;
        Volatile.Write(ref injectFrontierFailure, 1);

        await senderSession.SendAsync(
            new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = firstRequest.TransportEpoch,
                RepairRequestId = firstRequest.RepairRequestId,
                StartChunkIndex = firstChunk,
                ChunkCount = 1,
                DataSegments = [payload.Skip(firstChunk * chunkSize).Take(chunkSize).ToArray()],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_v6_frontier_request_deferred_for_recovery",
                StringComparison.Ordinal),
            timeoutMs: 5000);

        for (var chunkIndex = 0; chunkIndex < payload.Length / chunkSize; chunkIndex++)
        {
            if (chunkIndex == firstChunk)
            {
                continue;
            }

            await senderSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = firstRequest.TransportEpoch,
                    RepairRequestId = firstRequest.RepairRequestId,
                    StartChunkIndex = chunkIndex,
                    ChunkCount = 1,
                    DataSegments = [payload.Skip(chunkIndex * chunkSize).Take(chunkSize).ToArray()],
                    Priority = "frontier",
                    RecoveryMode = "frontier_repair_only",
                },
                CancellationToken.None);
        }

        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 5000);

        Assert.Equal(1, Volatile.Read(ref injectedFailureCount));
        Assert.Empty(receiverTransport.SentErrors);
        Assert.Equal(payload, destination.ToArray());
        var finalLogTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_frontier_request_deferred_for_recovery", finalLogTail, StringComparison.Ordinal);
        Assert.Contains("post_tuna_fallback_survival=1", finalLogTail, StringComparison.Ordinal);
        Assert.Contains("route=post_tuna_fallback_v6", finalLogTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_PostTunaFallbackForcedControlRetriesHonorSameFrontierCadence()
    {
        const string transferId = "transfer_v6_post_fallback_control_coalesces_forced_retry";
        const string sessionId = "session_v6_post_fallback_control_coalesces_forced_retry";
        const int chunkSize = 4;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 16).Select(static index => (byte)(index + 1)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsPostTunaFileFallbackActiveForRouteSelection = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsPostTunaFileFallbackActiveForRouteSelection = true,
        };
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundPostTunaFallbackV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "post-fallback-control-coalesces.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "post-fallback-control-coalesces.bin", payload.Length, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(frame =>
                frame.TransferId == transferId &&
                frame.MissingRanges.Count > 0),
            timeoutMs: 5000);

        var inboundContext = typeof(SessionFileTransferService)
            .GetField("inboundTransfer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(receiver);
        Assert.NotNull(inboundContext);
        var receiverStateCount = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count(frame => frame.TransferId == transferId);
        var frontierRequestCount = receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Count(frame => frame.TransferId == transferId);

        var stateRetryTask = Assert.IsAssignableFrom<Task<bool>>(InvokePrivateMethod(
            receiver,
            "SendInboundV6ReceiverStateAsync",
            inboundContext!,
            "unit_forced_retry",
            true));
        var frontierRetryTask = Assert.IsAssignableFrom<Task<bool>>(InvokePrivateMethod(
            receiver,
            "SendInboundV6FrontierRequestAsync",
            inboundContext!,
            "unit_forced_retry",
            true));

        Assert.False(await stateRetryTask);
        Assert.False(await frontierRetryTask);
        Assert.Equal(receiverStateCount, receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count(frame => frame.TransferId == transferId));
        Assert.Equal(frontierRequestCount, receiverTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Count(frame => frame.TransferId == transferId));

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_post_tuna_fallback_control_coalesced", logTail, StringComparison.Ordinal);
        Assert.Contains("frame_kind=receiver_state", logTail, StringComparison.Ordinal);
        Assert.Contains("frame_kind=frontier_request", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_PostTunaFallbackLegacyChunkProofBypassesSameFrontierCoalescing()
    {
        const string transferId = "transfer_v6_post_fallback_legacy_proof_bypasses_coalesce";
        const string sessionId = "session_v6_post_fallback_legacy_proof_bypasses_coalesce";
        const int chunkSize = 4;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 16).Select(static index => (byte)(index + 1)).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new NonDisposingMemoryStream();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsPostTunaFileFallbackActiveForRouteSelection = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            IsPostTunaFileFallbackActiveForRouteSelection = true,
        };
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        var senderSession = await StartManualInboundPostTunaFallbackV6ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "post-fallback-legacy-proof-bypasses-coalesce.bin",
            payload.Length,
            sha256,
            (_, _) => Task.FromResult<Stream>(destination));

        await senderSession.SendAsync(
            CreateManifest(sessionId, transferId, "post-fallback-legacy-proof-bypasses-coalesce.bin", payload.Length, chunkSize, sha256),
            CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(frame => frame.TransferId == transferId),
            timeoutMs: 5000);

        var inboundContext = typeof(SessionFileTransferService)
            .GetField("inboundTransfer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(receiver);
        Assert.NotNull(inboundContext);
        var receiverStateCount = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count(frame => frame.TransferId == transferId);

        var stateProofTask = Assert.IsAssignableFrom<Task<bool>>(InvokePrivateMethod(
            receiver,
            "SendInboundV6ReceiverStateAsync",
            inboundContext!,
            "regular_nkn_legacy_v4_chunk_probe",
            true));

        Assert.True(await stateProofTask);
        Assert.Equal(receiverStateCount + 1, receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count(frame => frame.TransferId == transferId));

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_receiver_state_sent", logTail, StringComparison.Ordinal);
        Assert.Contains("reason=regular_nkn_legacy_v4_chunk_probe", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_post_tuna_fallback_control_coalesced; direction=inbound; transfer_id=transfer_v6_post_fallback_legacy_proof_bypasses_coalesce", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public void V6Epoch_PostTunaFallbackControlSendFailureWithUnknownDestinationModeDefersForRecovery()
    {
        const string transferId = "transfer_v6_post_fallback_unknown_destination_control_failure";
        const string sessionId = "session_v6_post_fallback_unknown_destination_control_failure";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        var context = CreateInboundPostTunaFallbackV6Context(
            sessionId,
            transferId,
            "post-fallback-unknown-destination.bin",
            fileSizeBytes: 16);
        SetPrivateProperty(context, "State", FileTransferTransferState.Receiving);
        SetV6DestinationMode(context, "Unknown");
        SetPrivateProperty(context, "PullTransportPaused", false);
        SetPrivateProperty(context, "PullTransportResumeRequestPending", false);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(service, context);

        var deferred = Assert.IsType<bool>(InvokePrivateMethod(
            service,
            "TryDeferInboundV6ControlSendFailureForRecovery",
            context,
            "receiver_state",
            "bridge_restart",
            new InvalidOperationException("NKN bridge is not running.")));

        Assert.True(deferred);
        Assert.NotEqual(FileTransferTransferState.Failed, service.Snapshot.Inbound?.State);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_receiver_state_deferred_for_recovery", logTail, StringComparison.Ordinal);
        Assert.Contains("post_tuna_fallback_survival=1", logTail, StringComparison.Ordinal);
        Assert.Contains("recovery_mode=post_tuna_fallback_survival", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_PostTunaFallbackReceiverStateControlPlaneFailureDefersForRecovery()
    {
        const string transferId = "transfer_v6_post_fallback_receiver_state_control_plane_failure";
        const string sessionId = "session_v6_post_fallback_receiver_state_control_plane_failure";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var context = CreateInboundPostTunaFallbackV6Context(
            sessionId,
            transferId,
            "post-fallback-receiver-state-control-plane-failure.bin",
            fileSizeBytes: 256);
        SetPrivateProperty(context, "State", FileTransferTransferState.Receiving);
        SetPrivateProperty(context, "ChunkCount", 64);
        SetPrivateProperty(context, "NextChunkIndex", 10);
        SetPrivateProperty(context, "PullHighestReceivedChunkIndex", 9);
        SetPrivateProperty(context, "PullManifestReceived", true);
        SetPrivateProperty(context, "V6ReceiverTransportEpoch", 2L);
        SetPrivateProperty(context, "PullTransportRebindGeneration", 2);
        SetPrivateProperty(context, "DataSession", new ThrowingDataSession(
            sessionId,
            transferId,
            "File-transfer control-plane delivery failed for ReceiverState."));
        SetV6DestinationMode(context, "SparseSeekable");
        serviceType
            .GetField("inboundTransfer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(service, context);
        serviceType
            .GetMethod("StartInboundPostTunaRecoveryLocked", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [context, "unit_control_plane_failure"]);

        var state = new FileTransferReceiverStateFrameV6
        {
            SessionId = sessionId,
            TransferId = transferId,
            Epoch = 1,
            ContiguousCommittedChunkIndex = 10,
            DurableReceivedHighestChunkIndex = 9,
            CreditUntilChunkIndexExclusive = 11,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 10, ChunkCount = 1 }],
            BytesCommitted = 40,
            TransportEpoch = 2,
            Priority = "frontier",
            RecoveryMode = "regular_nkn_frontier_stall_control_bulk",
        };
        var request = Assert.IsType<FileTransferControlPlaneDeliveryRequest>(serviceType
            .GetMethod("CreateInboundControlPlaneDeliveryRequestLocked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    FileTransferControlPlaneKind.ReceiverState,
                    state,
                    "unit_control_plane_failure",
                    false,
                    1,
                ]));

        var sent = Assert.IsAssignableFrom<Task<bool>>(InvokePrivateMethod(
            service,
            "SendInboundFallbackControlPlaneOrDataSessionAsync",
            context,
            new ThrowingDataSession(
                sessionId,
                transferId,
                "File-transfer control-plane delivery failed for ReceiverState."),
            request,
            CancellationToken.None));

        Assert.False(await sent);
        Assert.NotEqual(FileTransferTransferState.Failed, service.Snapshot.Inbound?.State);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_receiver_state_deferred_for_recovery", logTail, StringComparison.Ordinal);
        Assert.Contains("post_tuna_fallback_survival=1", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_post_tuna_fallback_proof_replay_armed", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("Could not send V6 receiver state.", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public void V6Epoch_PostTunaFallbackSurvivalPathStaysLockedWhileRegularNknEpochWaiting()
    {
        const string transferId = "transfer_v6_post_fallback_waiting_epoch_survival";
        const string sessionId = "session_v6_post_fallback_waiting_epoch_survival";
        var context = CreateInboundPostTunaFallbackV6Context(
            sessionId,
            transferId,
            "post-fallback-waiting-epoch-survival.bin",
            fileSizeBytes: 16);
        SetPrivateProperty(context, "State", FileTransferTransferState.Receiving);
        SetV6DestinationMode(context, "SparseSeekable");

        var epochType = typeof(SessionFileTransferService).Assembly.GetType("NLink.Core.FileTransfer.V6TransportEpoch");
        Assert.NotNull(epochType);
        var epoch = Activator.CreateInstance(epochType!)!;
        SetPrivateProperty(epoch, "EpochId", 7L);
        SetPrivateProperty(epoch, "Kind", FileTransferTransportHandoffKind.RegularNknRecovery);
        SetPrivateProperty(epoch, "SourceTransport", FileTransferTransportKind.RegularNkn);
        SetPrivateProperty(epoch, "TargetTransport", FileTransferTransportKind.RegularNkn);
        SetPrivateProperty(epoch, "Direction", FileTransferDirection.Inbound);
        SetPrivateProperty(epoch, "Reason", "receive_stall_recovery");
        SetPrivateProperty(epoch, "State", V6TransportEpochState.WaitingForTargetTransport);
        SetPrivateProperty(context, "V6TransportEpoch", epoch);

        var method = typeof(SessionFileTransferService).GetMethod(
            "IsInboundV6PostTunaFallbackSurvivalPathLocked",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, [context])!);

        SetPrivateProperty(epoch, "TargetTransport", FileTransferTransportKind.Tuna);
        Assert.False((bool)method.Invoke(null, [context])!);
    }

    [Fact]
    public void V6Epoch_PostTunaFallbackFrontierProgressForcesReceiverStateDuringFrontierRepair()
    {
        const string transferId = "transfer_v6_post_fallback_frontier_progress_forces_state";
        const string sessionId = "session_v6_post_fallback_frontier_progress_forces_state";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        var context = CreateInboundPostTunaFallbackV6Context(
            sessionId,
            transferId,
            "post-fallback-frontier-progress-state.bin",
            fileSizeBytes: 4096);
        SetPrivateProperty(context, "State", FileTransferTransferState.Receiving);
        SetPrivateProperty(context, "ChunkCount", 256);
        SetPrivateProperty(context, "NextChunkIndex", 104);
        SetPrivateProperty(context, "PullHighestReceivedChunkIndex", 127);
        SetPrivateProperty(context, "PullTransportRebindGeneration", 2);
        SetPrivateProperty(context, "PullTransportRebindFrontierRepairCommittedChunks", 2);
        SetPrivateProperty(context, "V6LastReceiverStateSentUtc", DateTimeOffset.UtcNow);
        SetPrivateProperty(context, "V6LastReceiverStateCommittedChunkIndex", 100);
        SetV6DestinationMode(context, "SparseSeekable");
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(service, context);

        var forceState = Assert.IsType<bool>(InvokePrivateMethod(
            service,
            "ShouldForceInboundV6ReceiverStateAfterProgress",
            context,
            103));

        Assert.True(forceState);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_post_tuna_fallback_frontier_progress_state_forced", logTail, StringComparison.Ordinal);
        Assert.Contains("rebind_generation=2", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_PostTunaFallbackStrictEpochFrontierRequestKeepsSurvivalProof()
    {
        const string transferId = "transfer_v6_post_fallback_strict_epoch_survival_request";
        const string sessionId = "session_v6_post_fallback_strict_epoch_survival_request";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        receiverTransport.Connect(senderTransport);
        var dataSession = await receiverTransport.OpenFileTransferDataSessionAsync(sessionId, transferId, CancellationToken.None);
        var context = CreateInboundPostTunaFallbackV6Context(
            sessionId,
            transferId,
            "post-fallback-strict-epoch-survival.bin",
            fileSizeBytes: 256);
        SetPrivateProperty(context, "State", FileTransferTransferState.Receiving);
        SetPrivateProperty(context, "ChunkCount", 64);
        SetPrivateProperty(context, "NextChunkIndex", 10);
        SetPrivateProperty(context, "PullManifestReceived", true);
        SetPrivateProperty(context, "DataSession", dataSession);
        SetPrivateProperty(context, "V6ReceiverTransportEpoch", 2L);
        SetV6DestinationMode(context, "SparseSeekable");

        var epochType = typeof(SessionFileTransferService).Assembly.GetType("NLink.Core.FileTransfer.V6TransportEpoch");
        Assert.NotNull(epochType);
        var epoch = Activator.CreateInstance(epochType!)!;
        SetPrivateProperty(epoch, "EpochId", 2L);
        SetPrivateProperty(epoch, "Kind", FileTransferTransportHandoffKind.RegularNknRecovery);
        SetPrivateProperty(epoch, "SourceTransport", FileTransferTransportKind.RegularNkn);
        SetPrivateProperty(epoch, "TargetTransport", FileTransferTransportKind.RegularNkn);
        SetPrivateProperty(epoch, "Direction", FileTransferDirection.Inbound);
        SetPrivateProperty(epoch, "Reason", "receive_stall_recovery");
        SetPrivateProperty(epoch, "State", V6TransportEpochState.WaitingForTargetTransport);
        SetPrivateProperty(context, "V6TransportEpoch", epoch);

        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(service, context);

        var sendTask = Assert.IsAssignableFrom<Task<bool>>(InvokePrivateMethod(
            service,
            "SendInboundV6FrontierRequestAsync",
            context,
            "transport_epoch",
            true));

        Assert.True(await sendTask);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_frontier_request_sent", logTail, StringComparison.Ordinal);
        Assert.Contains("recovery_mode=waiting_for_target_transport", logTail, StringComparison.Ordinal);
        Assert.Contains("post_tuna_fallback_survival=1", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_post_tuna_fallback_frontier_rescue_requested", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_InboundPostTunaFallbackEpochStartUpdatesFallbackLegTransportEpoch()
    {
        const string transferId = "transfer_v6_post_fallback_epoch_adopts_leg_epoch";
        const string sessionId = "session_v6_post_fallback_epoch_adopts_leg_epoch";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        receiverTransport.Connect(senderTransport);
        var dataSession = await receiverTransport.OpenFileTransferDataSessionAsync(sessionId, transferId, CancellationToken.None);
        var context = CreateInboundPostTunaFallbackV6Context(
            sessionId,
            transferId,
            "post-fallback-epoch-adopts-leg.bin",
            fileSizeBytes: 256);
        SetPrivateProperty(context, "State", FileTransferTransferState.Receiving);
        SetPrivateProperty(context, "ChunkCount", 64);
        SetPrivateProperty(context, "NextChunkIndex", 10);
        SetPrivateProperty(context, "PullManifestReceived", true);
        SetPrivateProperty(context, "DataSession", dataSession);
        SetPrivateProperty(context, "V6ReceiverTransportEpoch", 1L);
        SetPrivateProperty(context, "PullTransportRebindGeneration", 1);
        SetV6DestinationMode(context, "SparseSeekable");

        var serviceType = typeof(SessionFileTransferService);
        serviceType
            .GetField("inboundTransfer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(service, context);
        var routeSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var leg = serviceType
            .GetMethod("StartInboundTransferLegLocked", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(
                null,
                [
                    context,
                    routeSelection,
                    "initial_fallback_leg",
                    FileTransferLegState.CheckpointPending,
                    false,
                    false,
                ])!;
        var legTransportEpochProperty = leg.GetType()
            .GetProperty("TransportEpochId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(1L, Assert.IsType<long>(legTransportEpochProperty.GetValue(leg)));

        SetPrivateProperty(context, "PullTransportRebindGeneration", 2);
        serviceType
            .GetMethod("StartInboundV6TransportEpochLocked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    "transport_epoch",
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.RegularNkn,
                ]);

        Assert.Equal(2L, Assert.IsType<long>(legTransportEpochProperty.GetValue(leg)));

        var sendTask = Assert.IsAssignableFrom<Task<bool>>(InvokePrivateMethod(
            service,
            "SendInboundV6FrontierRequestAsync",
            context,
            "transport_epoch",
            true));

        Assert.True(await sendTask);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_fallback_leg_transport_epoch_adopted", logTail, StringComparison.Ordinal);
        Assert.Contains("previous_transport_epoch=1", logTail, StringComparison.Ordinal);
        Assert.Contains("transport_epoch=2", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_frontier_request_sent", logTail, StringComparison.Ordinal);
        Assert.Contains("transport_epoch=2", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_control_plane_send_retired_stale_generation", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public void V6Epoch_TunaActivationUnavailableControlSendFailureDefersForRecovery()
    {
        const string transferId = "transfer_v6_tuna_activation_unavailable_control_failure";
        const string sessionId = "session_v6_tuna_activation_unavailable_control_failure";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        var context = CreateInboundPostTunaFallbackV6Context(
            sessionId,
            transferId,
            "tuna-activation-unavailable-control.bin",
            fileSizeBytes: 16);
        SetPrivateProperty(context, "State", FileTransferTransferState.Receiving);
        SetPrivateProperty(context, "PullTransportPaused", true);
        SetPrivateProperty(context, "PullTransportPauseReason", "tuna_activation_negotiating");
        SetV6DestinationMode(context, "Unknown");
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(service, context);

        var deferred = Assert.IsType<bool>(InvokePrivateMethod(
            service,
            "TryDeferInboundV6ControlSendFailureForRecovery",
            context,
            "receiver_state",
            "tuna_activation_negotiating",
            new InvalidOperationException("File-transfer data session is unavailable: tuna_activation_negotiating.")));

        Assert.True(deferred);
        Assert.NotEqual(FileTransferTransferState.Failed, service.Snapshot.Inbound?.State);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_receiver_state_deferred_for_recovery", logTail, StringComparison.Ordinal);
        Assert.Contains("pull_transport_paused=1", logTail, StringComparison.Ordinal);
        Assert.Contains("tuna_activation_negotiating", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public void LivePostTunaFallbackV6InfersDestinationModeFromV4SparseReceiveState()
    {
        const string transferId = "transfer_v6_post_fallback_infer_destination";
        const string sessionId = "session_v6_post_fallback_infer_destination";
        using var stream = new MemoryStream(new byte[16], writable: true);
        var context = CreateInboundPostTunaFallbackV6Context(
            sessionId,
            transferId,
            "post-fallback-infer-destination.bin",
            fileSizeBytes: 16);
        SetPrivateProperty(context, "ChunkCount", 4);
        SetPrivateProperty(context, "WriteStream", stream);
        SetPrivateProperty(context, "ReceiverSparseWriteActive", true);
        SetV6DestinationMode(context, "Unknown");

        typeof(SessionFileTransferService)
            .GetMethod(
                "EnsureInboundV6DestinationModeForLivePostTunaFallbackLocked",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [context, "unit_live_post_tuna_fallback"]);

        Assert.Equal("SparseSeekable", GetV6DestinationModeName(context));
        Assert.True(Assert.IsType<bool>(context.GetType()
            .GetProperty("ReceiverSparseWriteActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(context)));
        Assert.NotNull(context.GetType()
            .GetProperty("ReceiverSparseChunksWritten", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(context));
    }

    [Fact]
    public async Task V6Epoch_PeerTransportEpochAdoptionCancelsQueuedNormalBatchesBeforeFrontierRepair()
    {
        const string transferId = "transfer_v6_epoch_peer_adopt_clears_sender_queue";
        const string sessionId = "session_v6_epoch_peer_adopt_clears_sender_queue";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            DataSessionSendDelayMs = 1000,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(
            sender,
            senderTransport,
            receiverTransport,
            transferId,
            payloadSize: 2_000_000);

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
        await WaitUntilAsync(() => senderTransport.ActiveDataSessionSends > 0, timeoutMs: 5000);

        await receiverTransport.SendFileTransferTransportEpochAsync(
            new FileTransferTransportEpochV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 1,
                State = "target_proof_pending",
                HandoffKind = "regular_nkn_recovery",
                SourceTransport = "tuna",
                TargetTransport = "regular_nkn",
                Reason = "receive_stall_recovery",
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("reason=peer_transport_epoch_adopted", StringComparison.Ordinal),
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 1,
                RepairRequestId = "repair:peer-adopt:0",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(batch =>
                batch.TransportEpoch == 1 &&
                batch.StartChunkIndex == 0 &&
                string.Equals(batch.Priority, "frontier", StringComparison.OrdinalIgnoreCase)),
            timeoutMs: 5000);
        await Task.Delay(1200);

        var chunkBatches = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().ToList();
        Assert.All(
            chunkBatches,
            batch =>
            {
                Assert.Equal(1, batch.TransportEpoch);
                Assert.Equal("frontier", batch.Priority);
                Assert.Equal("repair:peer-adopt:0", batch.RepairRequestId);
            });
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_epoch_request_queues_cleared", logTail, StringComparison.Ordinal);
        Assert.True(
            logTail.Contains("event=filetransfer_v6_stale_prepared_batch_canceled", StringComparison.Ordinal) ||
            logTail.Contains("event=filetransfer_v6_chunk_batch_send_canceled_for_pipeline", StringComparison.Ordinal),
            logTail);
    }

    [Fact]
    public async Task V6Epoch_FrontierRequestAheadOfStaleSenderFrontierIsSent()
    {
        const string transferId = "transfer_v6_epoch_stale_sender_frontier";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_stale_sender_frontier");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_stale_sender_frontier");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        senderTransport.RequestAllDataSessionHandoffs(
            "receive_stall_recovery",
            FileTransferTransportHandoffKind.RegularNknRecovery,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);
        var epoch = senderTransport.SentTransportEpochs.Last();
        await ReceiveProbeAsync(receiverSession);

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = epoch.SessionId,
                TransferId = transferId,
                TransportEpoch = epoch.TransportEpoch,
                RepairRequestId = $"repair:{epoch.TransportEpoch}:8",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 8, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(batch =>
                batch.TransportEpoch == epoch.TransportEpoch &&
                batch.StartChunkIndex == 8 &&
                string.Equals(batch.Priority, "frontier", StringComparison.OrdinalIgnoreCase)),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_frontier_request_advanced_remote_frontier", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_epoch_request_blocked", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_NoProofTimeoutEntersWaitingForTargetTransport()
    {
        const string transferId = "transfer_v6_epoch_no_proof_waiting";
        var previousTimeout = SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests;
        SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
        try
        {
            var logStart = GetOperationalLogLength();
            using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_no_proof_waiting");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_no_proof_waiting");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);

            await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
            senderTransport.RequestAllDataSessionHandoffs(
                "no_proof_timeout",
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn);

            await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_waiting", StringComparison.Ordinal), timeoutMs: 5000);
            Assert.DoesNotContain("event=filetransfer_v6_epoch_recovered", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests = previousTimeout;
        }
    }

    [Fact]
    public async Task V6Epoch_TunaFallbackSupersedesUnresolvedRegularNknRecovery()
    {
        const string transferId = "transfer_v6_epoch_fallback_supersedes_recovery";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_fallback_supersedes_recovery");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_fallback_supersedes_recovery");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        senderTransport.RequestAllDataSessionHandoffs(
            "receive_stall_recovery",
            FileTransferTransportHandoffKind.RegularNknRecovery,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("handoff_kind=regular_nkn_recovery", StringComparison.Ordinal),
            timeoutMs: 5000);

        senderTransport.RequestAllDataSessionHandoffs(
            "sidecar_remote_closed",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);

        await WaitUntilAsync(
            () =>
            {
                var logTail = ReadOperationalLogTail(logStart);
                return logTail.Contains("event=filetransfer_v6_epoch_terminal", StringComparison.Ordinal) &&
                       logTail.Contains("reason=superseded", StringComparison.Ordinal) &&
                       logTail.Contains("handoff_kind=tuna_to_normal_fallback", StringComparison.Ordinal);
            },
            timeoutMs: 5000);
        Assert.DoesNotContain("event=filetransfer_v6_epoch_reused; direction=outbound", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6TransportDisconnected_DuringRegularNknRecoveryDefersToEpochAndLiveness()
    {
        const string transferId = "transfer_v6_epoch_transport_disconnect_deferred";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_transport_disconnect_deferred");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_transport_disconnect_deferred");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        senderTransport.RequestAllDataSessionHandoffs(
            "regular_nkn_recovery_after_stall",
            FileTransferTransportHandoffKind.RegularNknRecovery,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);

        senderTransport.RaiseDisconnected();

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_peer_disconnect_deferred_for_epoch; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);
        var outbound = sender.Snapshot.Outbound;
        Assert.NotNull(outbound);
        Assert.NotEqual(FileTransferTransferState.Failed, outbound!.State);
        Assert.Null(outbound.ErrorCode);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain("event=filetransfer_terminalized_by_peer_down; direction=outbound", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Heartbeat_UnresolvedRegularNknWaitingDefersPeerDisconnectAndReplaysEpoch()
    {
        const string transferId = "transfer_v6_epoch_liveness_defers_regular_nkn_waiting";
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        var previousProofTimeout = SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests;
        SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = TimeSpan.FromMilliseconds(25);
        SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromMilliseconds(75);
        SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
        try
        {
            var logStart = GetOperationalLogLength();
            using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_liveness_defers_regular_nkn_waiting");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_liveness_defers_regular_nkn_waiting");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);

            await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
            senderTransport.RequestAllDataSessionHandoffs(
                "regular_nkn_recovery_after_stall",
                FileTransferTransportHandoffKind.RegularNknRecovery,
                FileTransferTransportKind.RegularNkn);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_waiting", StringComparison.Ordinal),
                timeoutMs: 5000);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_heartbeat_timeout_deferred_for_epoch_waiting; direction=outbound", StringComparison.Ordinal),
                timeoutMs: 5000);

            var outbound = sender.Snapshot.Outbound;
            Assert.NotNull(outbound);
            Assert.NotEqual(FileTransferTransferState.Failed, outbound!.State);
            Assert.Null(outbound.ErrorCode);
            Assert.DoesNotContain(senderTransport.SentErrors, static error => error.ErrorCode == FileTransferResultCodes.PeerDisconnected);
        }
        finally
        {
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = previousHeartbeatInterval;
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = previousPeerLivenessTimeout;
            SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests = previousProofTimeout;
        }
    }

    [Fact]
    public async Task V6Heartbeat_InboundRegularNknWaitingDefersPeerDisconnectAndReplaysReceiverState()
    {
        const string transferId = "transfer_v6_epoch_inbound_liveness_defers_regular_nkn_waiting";
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        var previousProofTimeout = SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests;
        SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = TimeSpan.FromMilliseconds(25);
        SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromMilliseconds(75);
        SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
        try
        {
            var logStart = GetOperationalLogLength();
            var payload = Enumerable.Range(0, 5_000_000).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_inbound_liveness_defers_regular_nkn_waiting");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_inbound_liveness_defers_regular_nkn_waiting");
            EnsureV6RouteForTest(senderTransport);
            EnsureV6RouteForTest(receiverTransport);
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            using var destination = new NonDisposingMemoryStream();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-inbound-liveness.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Receiving, timeoutMs: 5000);

            senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, _, _) => Task.FromResult(true);
            senderTransport.OutboundHeartbeatDeliveryOverrideAsync = (_, _, _) => Task.FromResult(true);
            receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
                Task.FromResult(frame is FileTransferTransportProbeFrameV6);
            receiverTransport.RequestAllDataSessionHandoffs(
                "regular_nkn_recovery_after_stall",
                FileTransferTransportHandoffKind.RegularNknRecovery,
                FileTransferTransportKind.RegularNkn);

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=filetransfer_v6_epoch_waiting; direction=inbound", StringComparison.Ordinal) ||
                           tail.Contains("event=filetransfer_v6_heartbeat_timeout_deferred_for_post_tuna_fallback_repair; direction=inbound", StringComparison.Ordinal);
                },
                timeoutMs: 5000);
            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=filetransfer_v6_heartbeat_timeout_deferred_for_epoch_waiting; direction=inbound", StringComparison.Ordinal) ||
                           tail.Contains("event=filetransfer_v6_heartbeat_timeout_deferred_for_post_tuna_fallback_repair; direction=inbound", StringComparison.Ordinal);
                },
                timeoutMs: 5000);

            var inbound = receiver.Snapshot.Inbound;
            Assert.NotNull(inbound);
            Assert.NotEqual(FileTransferTransferState.Failed, inbound!.State);
            Assert.Null(inbound.ErrorCode);
            Assert.DoesNotContain(receiverTransport.SentErrors, static error => error.ErrorCode == FileTransferResultCodes.PeerDisconnected);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_receiver_state_sent", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = previousHeartbeatInterval;
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = previousPeerLivenessTimeout;
            SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests = previousProofTimeout;
        }
    }

    [Fact]
    public void V6Heartbeat_FallbackRecoveryUsesProofWindowLivenessTimeout()
    {
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        var previousProofTimeout = SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests;
        SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = TimeSpan.FromMilliseconds(25);
        SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromMilliseconds(75);
        SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
        try
        {
            var normalTimeout = SessionFileTransferService.ResolveV6PeerLivenessTimeoutForTests(
                unresolvedEpoch: false,
                fallbackRecoveryActive: false);
            var fallbackTimeout = SessionFileTransferService.ResolveV6PeerLivenessTimeoutForTests(
                unresolvedEpoch: false,
                fallbackRecoveryActive: true);
            var epochTimeout = SessionFileTransferService.ResolveV6PeerLivenessTimeoutForTests(
                unresolvedEpoch: true,
                fallbackRecoveryActive: false);

            Assert.Equal(TimeSpan.FromMilliseconds(75), normalTimeout);
            Assert.Equal(TimeSpan.FromMilliseconds(300), fallbackTimeout);
            Assert.Equal(epochTimeout, fallbackTimeout);
        }
        finally
        {
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = previousHeartbeatInterval;
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = previousPeerLivenessTimeout;
            SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests = previousProofTimeout;
        }
    }

    [Fact]
    public async Task V6Sender_RequestStarvationOnPostTunaFallbackRequestsBridgeRecovery()
    {
        const string transferId = "transfer_v6_sender_starved_regular_nkn_recovery";
        var previousFeedbackDelay = SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests;
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
        SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = TimeSpan.FromMilliseconds(250);
        SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromSeconds(30);
        try
        {
            var logStart = GetOperationalLogLength();
            using var senderTransport = new LoopbackFileTransferTransport("session_v6_sender_starved_regular_nkn_recovery");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sender_starved_regular_nkn_recovery");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);

            var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
            var offer = senderTransport.SentOffers.Single();
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 1,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);
            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0),
                timeoutMs: 5000);

            await WaitUntilAsync(
                () => senderTransport.ReceiveRecoveryRequests.Any(request =>
                    request.Direction == FileTransferDirection.Outbound &&
                    request.TransferId == transferId &&
                    request.Reason == "sender_request_feedback_stalled"),
                timeoutMs: 5000);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_sender_request_feedback_stalled_recovery_requested", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("reason=primary_regular_nkn_protocol_repair_only", logTail, StringComparison.Ordinal);
            Assert.Contains(
                senderTransport.ReceiveRecoveryRequests,
                request => request.Direction == FileTransferDirection.Outbound &&
                           request.TransferId == transferId &&
                           request.Reason == "sender_request_feedback_stalled");
            Assert.DoesNotContain("event=filetransfer_v6_heartbeat_timeout; direction=outbound", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = previousFeedbackDelay;
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = previousHeartbeatInterval;
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = previousPeerLivenessTimeout;
        }
    }

    [Fact]
    public async Task V6Sender_RequestStarvationAfterFallbackRegularNknRecoverySuppressesRepeatBridgeRecovery()
    {
        const string transferId = "transfer_v6_sender_starved_requests_receive_recovery";
        var previousFeedbackDelay = SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests;
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
        SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = TimeSpan.FromMilliseconds(250);
        SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromSeconds(30);
        try
        {
            var logStart = GetOperationalLogLength();
            using var senderTransport = new LoopbackFileTransferTransport("session_v6_sender_starved_requests_receive_recovery");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sender_starved_requests_receive_recovery");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);

            var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
            senderTransport.RequestAllDataSessionHandoffs(
                "receive_stall_recovery",
                FileTransferTransportHandoffKind.RegularNknRecovery,
                FileTransferTransportKind.RegularNkn);
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
                    SessionId = probeFrame.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 1,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                    BytesCommitted = 0,
                    TransportEpoch = probeFrame.TransportEpoch,
                },
                CancellationToken.None);
            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0),
                timeoutMs: 5000);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v6_post_tuna_fallback_sender_frontier_rescue_queued; direction=outbound",
                    StringComparison.Ordinal),
                timeoutMs: 5000);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_epoch_recovered_restart_suppressed; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_post_tuna_fallback_sender_frontier_rescue_queued; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.Contains("route=post_tuna_fallback_v6", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_sender_request_feedback_stalled_recovery_requested", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                senderTransport.ReceiveRecoveryRequests,
                request => request.Direction == FileTransferDirection.Outbound &&
                           request.TransferId == transferId &&
                           request.Reason == "sender_request_feedback_stalled");
        }
        finally
        {
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = previousFeedbackDelay;
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = previousHeartbeatInterval;
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = previousPeerLivenessTimeout;
        }
    }

    [Fact]
    public async Task V6Heartbeat_RecentOutboundDataSilenceUsesRegularNknFeedbackRepairBeforePeerDisconnect()
    {
        const string transferId = "transfer_v6_liveness_data_silence_defers_to_recovery";
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        var previousProofTimeout = SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests;
        SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = TimeSpan.FromMilliseconds(25);
        SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromMilliseconds(300);
        SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
        try
        {
            var logStart = GetOperationalLogLength();
            using var senderTransport = new LoopbackFileTransferTransport("session_v6_liveness_data_silence_defers_to_recovery");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v6_liveness_data_silence_defers_to_recovery");
            senderTransport.Connect(receiverTransport);
            ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);

            var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
            var offer = senderTransport.SentOffers.Single();
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 4,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);
            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0),
                timeoutMs: 5000);

            const string deferredMarker = "event=filetransfer_v6_heartbeat_timeout_deferred_for_regular_nkn_feedback_repair; direction=outbound";
            await WaitUntilAsync(
                () => CountOccurrences(ReadOperationalLogTail(logStart), deferredMarker) >= 4,
                timeoutMs: 10000);

            var outbound = sender.Snapshot.Outbound;
            Assert.NotNull(outbound);
            Assert.NotEqual(FileTransferTransferState.Failed, outbound!.State);
            Assert.Null(outbound.ErrorCode);
            var logTail = ReadOperationalLogTail(logStart);
            var deferredIndex = logTail.IndexOf(deferredMarker, StringComparison.Ordinal);
            Assert.True(deferredIndex >= 0, $"Expected deferred heartbeat recovery marker in log tail: {logTail}");
            Assert.True(
                CountOccurrences(logTail, deferredMarker) >= 4,
                "Primary regular-NKN feedback repair should not terminalize after the old fixed three-deferral window.");
            Assert.DoesNotContain("handoff_kind=regular_nkn_recovery", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_recovery_waiting_for_receiver_requests; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                senderTransport.ReceiveRecoveryRequests,
                request => request.Direction == FileTransferDirection.Outbound &&
                           request.TransferId == transferId &&
                           request.Reason == "peer_liveness_stale_receive_recovery");
            Assert.DoesNotContain("event=filetransfer_transport_rebind_safety_replay_started;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_heartbeat_timeout; direction=outbound", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = previousHeartbeatInterval;
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = previousPeerLivenessTimeout;
            SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests = previousProofTimeout;
        }
    }

    [Fact]
    public async Task V6Heartbeat_PeerTimeoutNotifiesPeerOverControl()
    {
        const string transferId = "transfer_v6_liveness_timeout_notifies_peer";
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = TimeSpan.FromMilliseconds(25);
        SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromMilliseconds(75);
        try
        {
            using var senderTransport = new LoopbackFileTransferTransport("session_v6_liveness_timeout_notifies_peer");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v6_liveness_timeout_notifies_peer");
            senderTransport.Connect(receiverTransport);
            ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);

            await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);

            await WaitUntilAsync(
                () => senderTransport.SentErrors.Any(static error => error.ErrorCode == FileTransferResultCodes.PeerDisconnected),
                timeoutMs: 5000);

            var outbound = sender.Snapshot.Outbound;
            Assert.NotNull(outbound);
            Assert.Equal(FileTransferTransferState.Failed, outbound!.State);
            Assert.Equal(FileTransferResultCodes.PeerDisconnected, outbound.ErrorCode);
        }
        finally
        {
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = previousHeartbeatInterval;
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = previousPeerLivenessTimeout;
        }
    }

    private static async Task<IFileTransferDataSession> StartManualOutboundV6SenderAsync(
        SessionFileTransferService sender,
        LoopbackFileTransferTransport senderTransport,
        LoopbackFileTransferTransport receiverTransport,
        string transferId,
        int payloadSize = 256_000)
    {
        EnsureV6RouteForTest(senderTransport);
        var payload = Enumerable.Range(0, payloadSize).Select(static index => (byte)(index % 251)).ToArray();
        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-epoch.bin", payload.Length, transferId),
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
                FileTransferRoute = offer.FileTransferRoute,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);
        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        var initialCheckpoint = senderTransport.SentDataFrames
            .OfType<FileTransferFrontierRequestFrameV6>()
            .LastOrDefault(frame =>
                frame.TransferId == transferId &&
                string.Equals(frame.RecoveryMode, "regular_nkn_state_refresh", StringComparison.Ordinal));
        if (string.Equals(offer.FileTransferRoute, FileTransferRouteResolver.PostTunaFallbackV6Token, StringComparison.Ordinal) &&
            initialCheckpoint is not null)
        {
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
                    TransportEpoch = initialCheckpoint.TransportEpoch,
                    RepairRequestId = initialCheckpoint.RepairRequestId,
                    Priority = initialCheckpoint.Priority,
                    RecoveryMode = initialCheckpoint.RecoveryMode,
                },
                CancellationToken.None);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(0).Contains(
                    "event=filetransfer_fallback_checkpoint_accepted; direction=outbound",
                    StringComparison.Ordinal),
                timeoutMs: 5000);
        }

        return receiverSession;
    }

    private static async Task<IFileTransferDataSession> StartManualInboundPostTunaFallbackV6ReceiverAsync(
        LoopbackFileTransferTransport senderTransport,
        SessionFileTransferService receiver,
        string transferId,
        string sessionId,
        string fileName,
        long fileSizeBytes,
        string sha256,
        FileTransferWriteStreamFactory openWriteStreamAsync)
    {
        const string routeToken = FileTransferRouteResolver.PostTunaFallbackV6Token;
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
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(transferId, openWriteStreamAsync, CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.AwaitingMetadata, timeoutMs: 5000);
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
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State is FileTransferTransferState.AwaitingMetadata or FileTransferTransferState.Receiving,
            timeoutMs: 5000);
        return await senderTransport.OpenFileTransferDataSessionAsync(sessionId, transferId, CancellationToken.None);
    }

    private static async Task<IFileTransferDataSession> StartManualInboundFileTunaV4ReceiverAsync(
        LoopbackFileTransferTransport senderTransport,
        SessionFileTransferService receiver,
        string transferId,
        string sessionId,
        string fileName,
        long fileSizeBytes,
        string sha256,
        FileTransferWriteStreamFactory openWriteStreamAsync)
    {
        const string routeToken = FileTransferRouteResolver.FileTunaV4Token;
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
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(transferId, openWriteStreamAsync, CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.AwaitingMetadata, timeoutMs: 5000);
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
            () => receiver.Snapshot.Inbound?.State is FileTransferTransferState.AwaitingMetadata or FileTransferTransferState.Receiving,
            timeoutMs: 5000);
        return await senderTransport.OpenFileTransferDataSessionAsync(sessionId, transferId, CancellationToken.None);
    }

    private static FileTransferManifestFrameV4 CreateV4Manifest(
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

    private static object CreateInboundPostTunaFallbackV6Context(
        string sessionId,
        string transferId,
        string fileName,
        long fileSizeBytes)
    {
        var routeSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var offer = new FileTransferOfferV2
        {
            SessionId = sessionId,
            TransferId = transferId,
            FileName = fileName,
            FileSizeBytes = fileSizeBytes,
            PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            FileTransferRoute = FileTransferRouteResolver.PostTunaFallbackV6Token,
        };
        var contextType = typeof(SessionFileTransferService)
            .GetNestedType("InboundTransferContext", System.Reflection.BindingFlags.NonPublic)!;
        var context = Activator.CreateInstance(
            contextType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [offer, routeSelection],
            culture: null)!;
        SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV6);
        SetPrivateProperty(context, "RouteSelection", routeSelection);
        SetPrivateProperty(context, "RouteRuntime", routeSelection.RuntimeDescriptor);
        return context;
    }

    private static void SetV6DestinationMode(object context, string modeName)
    {
        var property = context.GetType()
            .GetProperty("V6DestinationMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!;
        property.SetValue(context, Enum.Parse(property.PropertyType, modeName));
    }

    private static string GetV6DestinationModeName(object context)
    {
        var property = context.GetType()
            .GetProperty("V6DestinationMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!;
        return property.GetValue(context)?.ToString() ?? string.Empty;
    }

    private static void EnsureV6RouteForTest(LoopbackFileTransferTransport transport)
    {
        if (transport.IsPostTunaFileFallbackActiveForRouteSelection ||
            transport.IsDiagnosticRegularNknV6RouteEnabled)
        {
            return;
        }

        transport.IsFileTunaActiveForRouteSelection = false;
        if (transport.FileTransferTransportProfileKind == FileTransferTransportProfileKind.ConservativeNknStartup)
        {
            transport.IsDiagnosticRegularNknV6RouteEnabled = true;
        }
        else
        {
            transport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        }
    }

    private static void ConfigureDiagnosticRegularNknV6RouteForTest(
        LoopbackFileTransferTransport senderTransport,
        LoopbackFileTransferTransport receiverTransport)
    {
        senderTransport.IsFileTunaActiveForRouteSelection = false;
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = false;
        senderTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
        receiverTransport.IsFileTunaActiveForRouteSelection = false;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = false;
        receiverTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
    }

    private static async Task<FileTransferReceivedDataFrame> ReceiveProbeAsync(
        IFileTransferDataSession session,
        string? targetTransport = null)
    {
        using var cts = new CancellationTokenSource(5000);
        while (true)
        {
            var received = await session.ReceiveWithMetadataAsync(cts.Token);
            if (received.Frame is FileTransferTransportProbeFrameV6 probe &&
                (targetTransport is null || string.Equals(probe.TargetTransport, targetTransport, StringComparison.Ordinal)))
            {
                return received;
            }
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var startIndex = 0;
        while (true)
        {
            var index = text.IndexOf(value, startIndex, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            startIndex = index + value.Length;
        }
    }

    private sealed class ThrowingDataSession(
        string sessionId,
        string transferId,
        string failureMessage) : IFileTransferDataSession
    {
        public string SessionId { get; } = sessionId;

        public string TransferId { get; } = transferId;

        public bool IsAvailable => true;

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged
        {
            add { }
            remove { }
        }

        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
            => Task.FromException(new InvalidOperationException(failureMessage));

        public void Dispose()
        {
        }
    }
}
