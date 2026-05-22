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
    public async Task V6Epoch_TargetReadyAloneDoesNotRecoverAndBlocksNormalScheduling()
    {
        const string transferId = "transfer_v6_epoch_target_ready_no_proof";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_target_ready_no_proof");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_target_ready_no_proof");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        senderTransport.RequestAllDataSessionHandoffs(
            "target_ready_without_proof",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);

        var epoch = senderTransport.SentTransportEpochs.Last();
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = epoch.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 4,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 2 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await Task.Delay(350);

        Assert.DoesNotContain(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>(), static batch => batch.StartChunkIndex == 0);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_epoch_started", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_epoch_recovered", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_epoch_request_blocked", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_TargetProbeAckOverControlRecoversAndUnblocksNormalRequests()
    {
        const string transferId = "transfer_v6_epoch_probe_ack_recovers";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_probe_ack_recovers");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_probe_ack_recovers");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);

        var probe = await ReceiveProbeAsync(receiverSession);
        Assert.Equal(FileTransferTransportKind.Tuna, probe.TransportKind);
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
                CreditUntilChunkIndexExclusive = 4,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0),
            timeoutMs: 5000);
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
    public async Task V6Epoch_PeerRegularNknFrontierRequestForcesRegularNknWhenEpochControlMissed()
    {
        const string transferId = "transfer_v6_epoch_peer_regular_nkn_request_forces_regular";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_peer_regular_nkn_request_forces_regular");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_peer_regular_nkn_request_forces_regular");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
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

        var peerRecoveryEpoch = probeFrame.TransportEpoch + 1;
        senderTransport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                TransportEpoch = peerRecoveryEpoch,
                RepairRequestId = $"v6-frontier:{peerRecoveryEpoch}:0:1",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(batch =>
                batch.TransportEpoch == peerRecoveryEpoch &&
                batch.StartChunkIndex == 0 &&
                string.Equals(batch.Priority, "frontier", StringComparison.OrdinalIgnoreCase) &&
                batch.ForceRegularNknBulk &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        Assert.Contains(
            "event=filetransfer_v6_regular_nkn_priority_force_inferred",
            ReadOperationalLogTail(logStart),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_PeerRegularNknFrontierRequestForcesRegularNknWhenReceivedTransportIsUnknown()
    {
        const string transferId = "transfer_v6_epoch_peer_regular_nkn_request_unknown_transport";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_peer_regular_nkn_request_unknown_transport");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_peer_regular_nkn_request_unknown_transport");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
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

        var peerRecoveryEpoch = probeFrame.TransportEpoch + 1;
        senderTransport.NextDataFrameTransportKind = FileTransferTransportKind.Unknown;
        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                TransportEpoch = peerRecoveryEpoch,
                RepairRequestId = $"v6-frontier:{peerRecoveryEpoch}:0:1",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(batch =>
                batch.TransportEpoch == peerRecoveryEpoch &&
                batch.StartChunkIndex == 0 &&
                string.Equals(batch.Priority, "frontier", StringComparison.OrdinalIgnoreCase) &&
                batch.ForceRegularNknBulk &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        Assert.Contains(
            "event=filetransfer_v6_regular_nkn_priority_force_inferred",
            ReadOperationalLogTail(logStart),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_PeerRegularNknFrontierRequestForcesRegularNknWhenFirstCopyArrivesOverTuna()
    {
        const string transferId = "transfer_v6_epoch_peer_regular_nkn_request_tuna_copy";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_peer_regular_nkn_request_tuna_copy");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_peer_regular_nkn_request_tuna_copy");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
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

        var peerRecoveryEpoch = probeFrame.TransportEpoch + 1;
        senderTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                TransportEpoch = peerRecoveryEpoch,
                RepairRequestId = $"v6-frontier:{peerRecoveryEpoch}:0:1",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(batch =>
                batch.TransportEpoch == peerRecoveryEpoch &&
                batch.StartChunkIndex == 0 &&
                string.Equals(batch.Priority, "frontier", StringComparison.OrdinalIgnoreCase) &&
                batch.ForceRegularNknBulk &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        Assert.Contains(
            "event=filetransfer_v6_regular_nkn_priority_force_inferred",
            ReadOperationalLogTail(logStart),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_RecoveredTunaActivationEnablesRegularNknSupplementWhenProgressStalls()
    {
        var previousDelay = SessionFileTransferService.V6TunaRedundantDataProbeDelayOverrideForTests;
        var previousMinimumBytes = SessionFileTransferService.V6TunaRedundantDataMinimumBytesAfterProofOverrideForTests;
        SessionFileTransferService.V6TunaRedundantDataProbeDelayOverrideForTests = TimeSpan.FromMilliseconds(25);
        SessionFileTransferService.V6TunaRedundantDataMinimumBytesAfterProofOverrideForTests = 64 * 1024;
        try
        {
            const string transferId = "transfer_v6_epoch_tuna_redundant_normal";
            var logStart = GetOperationalLogLength();
            using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_tuna_redundant_normal");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_tuna_redundant_normal");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);

            var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
            receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
            senderTransport.RequestAllDataSessionHandoffs(
                "normal_to_tuna_activation",
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna);
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
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0),
                timeoutMs: 5000);

            await Task.Delay(75);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = probeFrame.SessionId,
                    TransferId = transferId,
                    Epoch = 2,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 2,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 1, ChunkCount = 1 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 1),
                timeoutMs: 5000);
            Assert.Contains(
                senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>(),
                static batch =>
                    batch.StartChunkIndex == 1 &&
                    (batch.BatchProfile == "v6_request_window_regular_nkn_redundant" ||
                     batch.ForceRegularNknBulk));
            Assert.Contains(
                "event=filetransfer_v6_tuna_regular_nkn_supplement_enabled",
                ReadOperationalLogTail(logStart),
                StringComparison.Ordinal);

            await Task.Delay(75);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = probeFrame.SessionId,
                    TransferId = transferId,
                    Epoch = 3,
                    ContiguousCommittedChunkIndex = 2,
                    DurableReceivedHighestChunkIndex = 2,
                    CreditUntilChunkIndexExclusive = 4,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 3, ChunkCount = 1 }],
                    BytesCommitted = 128 * 1024,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                    batch.StartChunkIndex == 3 &&
                    batch.BatchProfile == "v6_request_window" &&
                    !batch.ForceRegularNknBulk),
                timeoutMs: 5000);
            Assert.Contains(
                "event=filetransfer_v6_tuna_regular_nkn_supplement_disabled",
                ReadOperationalLogTail(logStart),
                StringComparison.Ordinal);

            await Task.Delay(75);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = probeFrame.SessionId,
                    TransferId = transferId,
                    Epoch = 4,
                    ContiguousCommittedChunkIndex = 3,
                    DurableReceivedHighestChunkIndex = 3,
                    CreditUntilChunkIndexExclusive = 5,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 4, ChunkCount = 1 }],
                    BytesCommitted = 128 * 1024,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                    batch.StartChunkIndex == 4 &&
                    batch.BatchProfile == "v6_request_window" &&
                    !batch.ForceRegularNknBulk),
                timeoutMs: 5000);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Equal(
                1,
                CountOccurrences(logTail, "event=filetransfer_v6_tuna_regular_nkn_supplement_enabled"));
        }
        finally
        {
            SessionFileTransferService.V6TunaRedundantDataProbeDelayOverrideForTests = previousDelay;
            SessionFileTransferService.V6TunaRedundantDataMinimumBytesAfterProofOverrideForTests = previousMinimumBytes;
        }
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

        senderTransport.SetLocalDataSessionsUnavailableForTests("sender_request_feedback_stalled");
        senderTransport.RequestAllDataSessionHandoffs(
            "sender_request_feedback_stalled",
            FileTransferTransportHandoffKind.RegularNknRecovery,
            FileTransferTransportKind.RegularNkn);
        await Task.Delay(300);

        Assert.Equal(epochCountAfterProof, senderTransport.SentTransportEpochs.Count);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_epoch_recovered_restart_suppressed", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_epoch_recovered_restart_pause_cleared", logTail, StringComparison.Ordinal);
        Assert.Contains("route=post_tuna_fallback_v6", logTail, StringComparison.Ordinal);
        Assert.Contains("was_paused=1", logTail, StringComparison.Ordinal);
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
    public async Task V6Epoch_ExactFrontierRepairProofOverControlRecoversWhenProbeAckIsMissing()
    {
        const string transferId = "transfer_v6_epoch_frontier_proof_recovers";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_frontier_proof_recovers");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_frontier_proof_recovers");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);
        var probe = await ReceiveProbeAsync(receiverSession);
        var probeFrame = Assert.IsType<FileTransferTransportProbeFrameV6>(probe.Frame);
        var repairRequestId = $"repair:{probeFrame.TransportEpoch}:0";

        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                TransportEpoch = probeFrame.TransportEpoch,
                RepairRequestId = repairRequestId,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(batch =>
                batch.TransportEpoch == probeFrame.TransportEpoch &&
                batch.RepairRequestId == repairRequestId &&
                batch.StartChunkIndex == 0),
            timeoutMs: 5000);
        await receiverTransport.SendFileTransferRepairProofAsync(
            new FileTransferRepairProofV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                TransportEpoch = probeFrame.TransportEpoch,
                RepairRequestId = repairRequestId,
                AppliedChunkCount = 1,
                CommittedChunkIndex = 1,
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered", StringComparison.Ordinal), timeoutMs: 5000);
    }

    [Fact]
    public async Task V6Epoch_StateFrontierRequestWithoutRepairIdInfersProofIdAndRecovers()
    {
        const string transferId = "transfer_v6_epoch_state_frontier_proof_recovers";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_state_frontier_proof_recovers");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_state_frontier_proof_recovers");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);
        var probe = await ReceiveProbeAsync(receiverSession);
        var probeFrame = Assert.IsType<FileTransferTransportProbeFrameV6>(probe.Frame);

        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 0,
                CreditUntilChunkIndexExclusive = 4,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
                TransportEpoch = probeFrame.TransportEpoch,
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(batch =>
                batch.TransportEpoch == probeFrame.TransportEpoch &&
                batch.StartChunkIndex == 0 &&
                batch.RepairRequestId is not null &&
                batch.RepairRequestId.StartsWith("v6-state-frontier:", StringComparison.Ordinal)),
            timeoutMs: 5000);
        var repairRequestId = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .Last(batch =>
                batch.TransportEpoch == probeFrame.TransportEpoch &&
                batch.StartChunkIndex == 0 &&
                batch.RepairRequestId is not null)
            .RepairRequestId!;

        await receiverTransport.SendFileTransferRepairProofAsync(
            new FileTransferRepairProofV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                TransportEpoch = probeFrame.TransportEpoch,
                RepairRequestId = repairRequestId,
                AppliedChunkCount = 1,
                CommittedChunkIndex = 1,
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered", StringComparison.Ordinal), timeoutMs: 5000);
        Assert.Contains("event=filetransfer_v6_state_frontier_repair_request_inferred", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Epoch_RepairProofForEarlierInFlightFrontierRequestStillRecovers()
    {
        const string transferId = "transfer_v6_epoch_frontier_proof_out_of_order";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_frontier_proof_out_of_order");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_frontier_proof_out_of_order");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);
        var probe = await ReceiveProbeAsync(receiverSession);
        var probeFrame = Assert.IsType<FileTransferTransportProbeFrameV6>(probe.Frame);
        var firstRepairRequestId = $"repair:{probeFrame.TransportEpoch}:0";
        var secondRepairRequestId = $"repair:{probeFrame.TransportEpoch}:1";

        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                TransportEpoch = probeFrame.TransportEpoch,
                RepairRequestId = firstRepairRequestId,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(batch =>
                batch.TransportEpoch == probeFrame.TransportEpoch &&
                batch.RepairRequestId == firstRepairRequestId &&
                batch.StartChunkIndex == 0),
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                TransportEpoch = probeFrame.TransportEpoch,
                RepairRequestId = secondRepairRequestId,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains($"repair_request_id={secondRepairRequestId}", StringComparison.Ordinal),
            timeoutMs: 5000);

        await receiverTransport.SendFileTransferRepairProofAsync(
            new FileTransferRepairProofV6
            {
                SessionId = probeFrame.SessionId,
                TransferId = transferId,
                TransportEpoch = probeFrame.TransportEpoch,
                RepairRequestId = firstRepairRequestId,
                AppliedChunkCount = 1,
                CommittedChunkIndex = 1,
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered", StringComparison.Ordinal), timeoutMs: 5000);
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
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_waiting; direction=inbound", StringComparison.Ordinal),
                timeoutMs: 5000);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_heartbeat_timeout_deferred_for_epoch_waiting; direction=inbound", StringComparison.Ordinal),
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
    public async Task V6Sender_FeedbackStallWithLargeTransportBacklogDoesNotRestartBridge()
    {
        const string transferId = "transfer_v6_sender_feedback_backlog_suppresses_recovery";
        var previousFeedbackDelay = SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests;
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
        SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = TimeSpan.FromMilliseconds(250);
        SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromSeconds(30);
        try
        {
            var logStart = GetOperationalLogLength();
            using var senderTransport = new LoopbackFileTransferTransport("session_v6_sender_feedback_backlog_suppresses_recovery");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sender_feedback_backlog_suppresses_recovery");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);

            var receiverSession = await StartManualOutboundV6SenderAsync(
                sender,
                senderTransport,
                receiverTransport,
                transferId,
                payloadSize: 8_000_000);
            var offer = senderTransport.SentOffers.Single();
            receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
            senderTransport.RequestAllDataSessionHandoffs(
                "normal_to_tuna_activation",
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna);
            var probe = await ReceiveProbeAsync(receiverSession);
            Assert.Equal(FileTransferTransportKind.Tuna, probe.TransportKind);
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
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 300,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 300 }],
                    BytesCommitted = 0,
                    TransportEpoch = probeFrame.TransportEpoch,
                },
                CancellationToken.None);
            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Sum(static batch => batch.ChunkCount) >= 260,
                timeoutMs: 5000);
            await Task.Delay(250);

            Assert.DoesNotContain(
                senderTransport.ReceiveRecoveryRequests,
                request => request.Direction == FileTransferDirection.Outbound &&
                           request.TransferId == transferId &&
                           request.Reason == "sender_request_feedback_stalled");

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_sender_feedback_stale_recovery_suppressed", logTail, StringComparison.Ordinal);
            Assert.True(
                logTail.Contains("reason=outstanding_transport_backlog", StringComparison.Ordinal) ||
                logTail.Contains("reason=recent_chunk_sends", StringComparison.Ordinal) ||
                logTail.Contains("reason=in_flight_sends", StringComparison.Ordinal),
                $"Expected a backlog/activity suppression reason in log tail: {logTail}");
            Assert.DoesNotContain("event=filetransfer_v6_sender_feedback_stale_normal_pipeline_paused", logTail, StringComparison.Ordinal);
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
            EnableDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
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
                timeoutMs: 5000);

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
            EnableDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
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
        return await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
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

    private static void EnableDiagnosticRegularNknV6RouteForTest(
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
}
