using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferV6TunaIntegrationTests : SessionFileTransferServiceTestBase
{
    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(90)]
    public async Task TunaActivation_UsesV6EpochProofWithoutReducingCommittedProgress(int committedPercent)
    {
        var transferId = $"transfer_v6_tuna_activation_{committedPercent}";
        using var senderTransport = new LoopbackFileTransferTransport($"session_v6_tuna_activation_{committedPercent}");
        using var receiverTransport = new LoopbackFileTransferTransport($"session_v6_tuna_activation_{committedPercent}");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        var manifest = senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Single();
        var committedChunk = Math.Clamp(manifest.ChunkCount * committedPercent / 100, 0, manifest.ChunkCount - 1);
        await SendReceiverStateAsync(receiverSession, manifest, committedChunk);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.ChunksTransferred >= committedChunk, timeoutMs: 5000);
        var progressBeforeEpoch = sender.Snapshot.Outbound!.ProgressBytes;

        receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_activation_negotiated",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(
            () => senderTransport.ObservedV6TransportEpochs.Any(snapshot =>
                snapshot.IsUnresolved &&
                snapshot.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                snapshot.TargetTransport == FileTransferTransportKind.Tuna),
            timeoutMs: 5000);
        Assert.Equal("Switching to Tuna", sender.Snapshot.Outbound?.StatusMessage);

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
            () => senderTransport.ObservedV6TransportEpochs.Any(snapshot =>
                !snapshot.IsUnresolved &&
                snapshot.State == V6TransportEpochState.Recovered &&
                snapshot.TransportEpoch == probeFrame.TransportEpoch),
            timeoutMs: 5000);
        Assert.True(sender.Snapshot.Outbound!.ProgressBytes >= progressBeforeEpoch);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(90)]
    public async Task TunaFallback_UsesV6EpochAndWaitsClearlyWithoutProof(int committedPercent)
    {
        var previousTimeout = SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests;
        SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
        try
        {
            var transferId = $"transfer_v6_tuna_fallback_{committedPercent}";
            using var senderTransport = new LoopbackFileTransferTransport($"session_v6_tuna_fallback_{committedPercent}");
            using var receiverTransport = new LoopbackFileTransferTransport($"session_v6_tuna_fallback_{committedPercent}");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);

            var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
            var manifest = senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Single();
            var committedChunk = Math.Clamp(manifest.ChunkCount * committedPercent / 100, 0, manifest.ChunkCount - 1);
            await SendReceiverStateAsync(receiverSession, manifest, committedChunk);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.ChunksTransferred >= committedChunk, timeoutMs: 5000);
            var progressBeforeEpoch = sender.Snapshot.Outbound!.ProgressBytes;

            senderTransport.RequestAllDataSessionHandoffs(
                "sidecar_byte_cap_reached",
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn);

            await WaitUntilAsync(
                () => string.Equals(sender.Snapshot.Outbound?.StatusMessage, "Waiting for regular NKN", StringComparison.Ordinal),
                timeoutMs: 5000);
            Assert.Contains(
                senderTransport.ObservedV6TransportEpochs,
                snapshot => snapshot.IsUnresolved &&
                            snapshot.State == V6TransportEpochState.WaitingForTargetTransport &&
                            snapshot.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                            snapshot.TargetTransport == FileTransferTransportKind.RegularNkn);
            Assert.True(sender.Snapshot.Outbound!.ProgressBytes >= progressBeforeEpoch);
        }
        finally
        {
            SessionFileTransferService.V6TransportEpochProofTimeoutOverrideForTests = previousTimeout;
        }
    }

    [Fact]
    public async Task TunaFallback_WhenDataSessionAlreadyUnavailableStillStartsV6EpochAndRecoversFromProof()
    {
        const string transferId = "transfer_v6_tuna_fallback_unavailable_epoch";
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_tuna_fallback_unavailable_epoch");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_tuna_fallback_unavailable_epoch");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        var manifest = senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Single();
        senderTransport.SetLocalDataSessionsUnavailableForTests("sidecar_remote_closed");
        senderTransport.RequestAllDataSessionHandoffs(
            "sidecar_remote_closed",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);

        await WaitUntilAsync(
            () => senderTransport.ObservedV6TransportEpochs.Any(snapshot =>
                snapshot.IsUnresolved &&
                snapshot.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                snapshot.TargetTransport == FileTransferTransportKind.RegularNkn),
            timeoutMs: 5000);
        var status = sender.Snapshot.Outbound?.StatusMessage;
        Assert.True(
            status is "Switching to regular NKN" or "Repairing over regular NKN",
            $"Unexpected fallback status: {status}");

        var probe = await ReceiveProbeAsync(receiverSession);
        Assert.Equal(FileTransferTransportKind.RegularNkn, probe.TransportKind);
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
            () => senderTransport.ObservedV6TransportEpochs.Any(snapshot =>
                !snapshot.IsUnresolved &&
                snapshot.State == V6TransportEpochState.Recovered &&
                snapshot.TransportEpoch == probeFrame.TransportEpoch),
            timeoutMs: 5000);
        senderTransport.SetLocalDataSessionsAvailableForTests("transport_recovered");
        await SendReceiverStateAsync(receiverSession, manifest, committedChunk: 0);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0),
            timeoutMs: 5000);
    }

    [Fact]
    public async Task TunaFallback_RecoveredRegularNknEpochSuppressesDuplicateFallbackTriggers()
    {
        const string transferId = "transfer_v6_tuna_fallback_duplicate_suppressed";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_tuna_fallback_duplicate_suppressed");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_tuna_fallback_duplicate_suppressed");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_send_rejected",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => senderTransport.ObservedV6TransportEpochs.Any(snapshot =>
                snapshot.IsUnresolved &&
                snapshot.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                snapshot.TargetTransport == FileTransferTransportKind.RegularNkn),
            timeoutMs: 5000);

        var probe = await ReceiveProbeAsync(receiverSession);
        Assert.Equal(FileTransferTransportKind.RegularNkn, probe.TransportKind);
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
            () => senderTransport.ObservedV6TransportEpochs.Any(snapshot =>
                !snapshot.IsUnresolved &&
                snapshot.State == V6TransportEpochState.Recovered &&
                snapshot.TransportEpoch == probeFrame.TransportEpoch),
            timeoutMs: 5000);

        senderTransport.RequestAllDataSessionHandoffs(
            "remote_queue_overflow",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        senderTransport.RequestAllDataSessionHandoffs(
            "bridge_receive_stall",
            FileTransferTransportHandoffKind.RegularNknRecovery,
            FileTransferTransportKind.RegularNkn);
        await Task.Delay(250);

        Assert.Single(senderTransport.SentTransportEpochs.Select(static epoch => epoch.TransportEpoch).Distinct());
        Assert.Contains(
            "event=filetransfer_v6_epoch_recovered_restart_suppressed",
            ReadOperationalLogTail(logStart),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FileTransferTransportHandoffKind.RegularNknRecovery, FileTransferTransportHandoffKind.TunaToNormalFallback)]
    [InlineData(FileTransferTransportHandoffKind.TunaToNormalFallback, FileTransferTransportHandoffKind.RegularNknRecovery)]
    public async Task TunaFallback_DifferentRegularNknEpochKindsSupersedeUnresolvedEpoch(
        FileTransferTransportHandoffKind firstKind,
        FileTransferTransportHandoffKind secondKind)
    {
        var suffix = $"{firstKind}_{secondKind}".ToLowerInvariant();
        var transferId = $"transfer_v6_tuna_fallback_unresolved_reuse_{suffix}";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport($"session_v6_tuna_fallback_unresolved_reuse_{suffix}");
        using var receiverTransport = new LoopbackFileTransferTransport($"session_v6_tuna_fallback_unresolved_reuse_{suffix}");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        senderTransport.RequestAllDataSessionHandoffs(
            "receive_stall_recovery",
            firstKind,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => senderTransport.ObservedV6TransportEpochs.Any(snapshot =>
                snapshot.IsUnresolved &&
                snapshot.HandoffKind == firstKind &&
                snapshot.TargetTransport == FileTransferTransportKind.RegularNkn),
            timeoutMs: 5000);
        var startedEpoch = senderTransport.ObservedV6TransportEpochs.Last(snapshot =>
            snapshot.IsUnresolved &&
            snapshot.HandoffKind == firstKind &&
            snapshot.TargetTransport == FileTransferTransportKind.RegularNkn);

        senderTransport.RequestAllDataSessionHandoffs(
            "transport_recovered_unproven",
            secondKind,
            FileTransferTransportKind.RegularNkn);
        await Task.Delay(250);

        var regularNknEpochIds = senderTransport.ObservedV6TransportEpochs
            .Where(static snapshot =>
                snapshot.TargetTransport == FileTransferTransportKind.RegularNkn &&
                snapshot.HandoffKind is FileTransferTransportHandoffKind.TunaToNormalFallback
                    or FileTransferTransportHandoffKind.RegularNknRecovery)
            .Select(static snapshot => snapshot.TransportEpoch)
            .Distinct()
            .ToArray();
        Assert.Equal(2, regularNknEpochIds.Length);
        Assert.Equal(startedEpoch.TransportEpoch, regularNknEpochIds[0]);
        Assert.True(regularNknEpochIds[1] > regularNknEpochIds[0]);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_epoch_terminal", logTail, StringComparison.Ordinal);
        Assert.Contains("reason=superseded", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TunaFallbackAvailability_BypassesBlockedLifecycleTailAndStartsV6Epoch()
    {
        const string transferId = "transfer_v6_tuna_fallback_lifecycle_bypass";
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_tuna_fallback_lifecycle_bypass");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_tuna_fallback_lifecycle_bypass");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);

        var releaseLifecycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.InboundDispatchBeforeWorkAsyncForTests = (lane, _) =>
            lane == "lifecycle"
                ? releaseLifecycle.Task
                : Task.CompletedTask;

        try
        {
            senderTransport.RequestAllDataSessionHandoffs(
                "sidecar_remote_closed",
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn);

            await WaitUntilAsync(
                () => senderTransport.ObservedV6TransportEpochs.Any(snapshot =>
                    snapshot.IsUnresolved &&
                    snapshot.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                    snapshot.TargetTransport == FileTransferTransportKind.RegularNkn),
                timeoutMs: 5000);

            Assert.Contains(
                "event=filetransfer_data_session_availability_observed",
                ReadOperationalLogText(),
                StringComparison.Ordinal);
        }
        finally
        {
            sender.InboundDispatchBeforeWorkAsyncForTests = null;
            releaseLifecycle.SetResult();
        }
    }

    [Fact]
    public async Task TunaReenableDuringActiveTransferStartsFreshEpochWithoutResettingProgress()
    {
        const string transferId = "transfer_v6_tuna_reenable_fresh_epoch";
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_tuna_reenable_fresh_epoch");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_tuna_reenable_fresh_epoch");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var receiverSession = await StartManualOutboundV6SenderAsync(sender, senderTransport, receiverTransport, transferId);
        var manifest = senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Single();
        await SendReceiverStateAsync(receiverSession, manifest, Math.Max(1, manifest.ChunkCount / 2));
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.ChunksTransferred >= Math.Max(1, manifest.ChunkCount / 2), timeoutMs: 5000);
        var progressBeforeEpoch = sender.Snapshot.Outbound!.ProgressBytes;

        senderTransport.RequestAllDataSessionHandoffs(
            "sidecar_remote_closed",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => senderTransport.ObservedV6TransportEpochs.Any(snapshot =>
                snapshot.IsUnresolved &&
                snapshot.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback),
            timeoutMs: 5000);
        var fallbackEpoch = senderTransport.ObservedV6TransportEpochs.Last(snapshot =>
            snapshot.IsUnresolved &&
            snapshot.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback);

        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_reenabled",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);

        await WaitUntilAsync(
            () => senderTransport.ObservedV6TransportEpochs.Any(snapshot =>
                snapshot.IsUnresolved &&
                snapshot.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                snapshot.TransportEpoch > fallbackEpoch.TransportEpoch),
            timeoutMs: 5000);
        Assert.Contains(
            senderTransport.ObservedV6TransportEpochs,
            snapshot => !snapshot.IsUnresolved &&
                        snapshot.State == V6TransportEpochState.Terminal &&
                        snapshot.TransportEpoch == fallbackEpoch.TransportEpoch);
        Assert.True(sender.Snapshot.Outbound!.ProgressBytes >= progressBeforeEpoch);
    }

    private static async Task<IFileTransferDataSession> StartManualOutboundV6SenderAsync(
        SessionFileTransferService sender,
        LoopbackFileTransferTransport senderTransport,
        LoopbackFileTransferTransport receiverTransport,
        string transferId)
    {
        var payload = Enumerable.Range(0, 384_000).Select(static index => (byte)(index % 251)).ToArray();
        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-tuna-integration.bin", payload.Length, transferId),
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

    private static Task SendReceiverStateAsync(
        IFileTransferDataSession receiverSession,
        FileTransferManifestFrameV6 manifest,
        int committedChunk)
        => receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = manifest.SessionId,
                TransferId = manifest.TransferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = committedChunk,
                DurableReceivedHighestChunkIndex = Math.Max(-1, committedChunk - 1),
                CreditUntilChunkIndexExclusive = Math.Min(manifest.ChunkCount, committedChunk + 4),
                MissingRanges =
                [
                    new FileTransferRangeV4
                    {
                        StartChunkIndex = committedChunk,
                        ChunkCount = Math.Min(2, manifest.ChunkCount - committedChunk),
                    },
                ],
                BytesCommitted = Math.Min(manifest.FileSizeBytes, (long)committedChunk * manifest.ChunkSizeBytes),
            },
            CancellationToken.None);

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
}
