using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferV6TransportEpochTests : SessionFileTransferServiceTestBase
{
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
    public async Task V6Epoch_RecoveredRegularNknFallbackUsesRedundantNormalRequestWindows()
    {
        const string transferId = "transfer_v6_epoch_regular_nkn_redundant_normal";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_epoch_regular_nkn_redundant_normal");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_epoch_regular_nkn_redundant_normal");
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
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                batch.StartChunkIndex == 0 &&
                batch.BatchProfile == "v6_request_window_regular_nkn_redundant" &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.BulkOnly &&
                batch.ForceRegularNknBulk),
            timeoutMs: 5000);
        Assert.Contains(
            "reason=regular_nkn_recovered_after_tuna_fallback",
            ReadOperationalLogTail(logStart),
            StringComparison.Ordinal);
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
    public async Task V6Epoch_RegularNknRedundantNormalTimeoutDisablesNormalButKeepsFrontierProof()
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
                    batch.BatchProfile == "v6_request_window_regular_nkn_redundant")
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
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_regular_nkn_redundant_data_disabled", StringComparison.Ordinal),
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
            Assert.Contains("reason=send_timeout", logTail, StringComparison.Ordinal);

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
            await WaitUntilAsync(() => senderTransport.MaxConcurrentDataSessionSends >= 8, timeoutMs: 5000);

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
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_chunk_batch_send_timeout", StringComparison.Ordinal),
                timeoutMs: 5000);
            Assert.Contains("event=filetransfer_v6_chunk_batch_send_timeout", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V6SenderTransportSendTimeoutOverrideForTests = previousSendTimeout;
        }
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
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_peer_disconnect_deferred_for_epoch", StringComparison.Ordinal),
            timeoutMs: 5000);
        var outbound = sender.Snapshot.Outbound;
        Assert.NotNull(outbound);
        Assert.NotEqual(FileTransferTransferState.Failed, outbound!.State);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain("event=filetransfer_terminalized_by_peer_down; direction=outbound", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Heartbeat_UnresolvedRegularNknWaitingDefersPeerDisconnect()
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
            await Task.Delay(350);

            var outbound = sender.Snapshot.Outbound;
            Assert.NotNull(outbound);
            Assert.NotEqual(FileTransferTransferState.Failed, outbound!.State);
            Assert.Null(outbound.ErrorCode);
            Assert.Equal("Waiting for regular NKN", outbound.StatusMessage);
            Assert.DoesNotContain("event=filetransfer_v6_heartbeat_timeout", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
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
            },
            CancellationToken.None);
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
