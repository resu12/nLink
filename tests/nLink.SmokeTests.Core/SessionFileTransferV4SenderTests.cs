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
    private const string RetiredPostTunaFallbackSparseRuntimeSkip =
        "Retired: Phase 2 routes post-Tuna fallback through the default V6 runtime instead of the legacy sparse regular-NKN runtime.";
    private const string RetiredRegularNknV4ToV6RecoverySkip =
        "Retired: regular NKN now remains on V4; primary regular-NKN V6 recovery is diagnostic-only.";

    [Fact]
    public async Task PrimaryRegularNknV4Fast_CompletesEndToEnd_WithSparseReceiverIntegrity()
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
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, sessionOpen.ProtocolVersion);
        Assert.All(senderTransport.SentDataFrames, static frame =>
            Assert.True(FileTransferProtocol.IsV4DataFrame(frame), $"Expected only V4 sender data frames, got {frame.Type}."));
        Assert.All(receiverTransport.SentDataFrames, static frame =>
            Assert.True(FileTransferProtocol.IsV4DataFrame(frame), $"Expected only V4 receiver data frames, got {frame.Type}."));
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferManifestFrameV4 and not FileTransferManifestFrameV6);
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferChunkBatchFrameV4 and not FileTransferChunkBatchFrameV6);
        Assert.Contains(receiverTransport.SentDataFrames, static frame => frame is FileTransferStateFrameV4 and not FileTransferReceiverStateFrameV6);
        Assert.Contains(receiverTransport.SentCompletes, complete =>
            string.Equals(complete.TransferId, transferId, StringComparison.Ordinal) &&
            complete.FileSizeBytes == payload.Length);
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferCompleteFrameV6);
        Assert.DoesNotContain(
            receiverTransport.SentDataFrames,
            static frame => frame is not FileTransferStateFrameV4);
        var logTail = ReadV4SenderLogSnapshot(logStart);
        var transferLog = FilterV4SenderTransferLog(logTail, transferId);
        Assert.Contains("event=filetransfer_v4_negotiated; transfer_id=transfer_v4_sender_e2e", transferLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_sender_started; transfer_id=transfer_v4_sender_e2e", transferLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_receiver_started; transfer_id=transfer_v4_sender_e2e", transferLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_efficiency_summary; direction=outbound; transfer_id=transfer_v4_sender_e2e", transferLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_efficiency_summary; direction=inbound; transfer_id=transfer_v4_sender_e2e", transferLog, StringComparison.Ordinal);
        Assert.Contains("raw_bytes_sent_total=", transferLog, StringComparison.Ordinal);
        Assert.Contains("raw_batch_bytes_received_total=", transferLog, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_primary_regular_nkn_bulk_v6_selected", transferLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknV4Fast_OutboundProgressTracksReceiverSparseBytesWritten()
    {
        const string transferId = "transfer_v4_sender_progress_sparse_written";
        const string sessionId = "session_v4_sender_progress_sparse_written";
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var droppedFrontierBatchCount = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.OutboundDataFrameDeliveryOverrideWithLaneAsync = (_, frame, _, _) =>
        {
            if (frame is FileTransferChunkBatchFrameV4 batch &&
                frame is not FileTransferChunkBatchFrameV6 &&
                batch.StartChunkIndex <= 0 &&
                batch.StartChunkIndex + batch.ChunkCount > 0)
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
            new FileTransferSendDescriptor("v4-progress-sparse-written.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        var writtenProgressTarget = 3L * 21 * 1024;
        await WaitUntilAsync(
            () =>
            {
                var inbound = receiver.Snapshot.Inbound;
                var outbound = sender.Snapshot.Outbound;
                return Volatile.Read(ref droppedFrontierBatchCount) > 0 &&
                       inbound?.BytesTransferred == 0 &&
                       (inbound.BytesAcceptedForTransport ?? 0L) >= writtenProgressTarget &&
                       (outbound?.ProgressBytes ?? 0L) >= writtenProgressTarget;
            },
            timeoutMs: 10000);

        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, Assert.Single(senderTransport.SentSessionOpens).ProtocolVersion);
        Assert.Equal(0, receiver.Snapshot.Inbound!.BytesTransferred);
        Assert.True(receiver.Snapshot.Inbound!.BytesAcceptedForTransport >= writtenProgressTarget);
        Assert.True(sender.Snapshot.Outbound!.ProgressBytes >= writtenProgressTarget);
        Assert.Contains(
            receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>(),
            state => state is not FileTransferReceiverStateFrameV6 &&
                     state.ContiguousCommittedChunkIndex == 0 &&
                     state.BytesCommitted >= writtenProgressTarget);

        var receiverProgressAtPause = receiver.Snapshot.Inbound!.BytesAcceptedForTransport!.Value;
        var stateCountBeforePause = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Count();
        Assert.NotNull(await receiver.PauseTransferAsync(transferId, "receiver_pause", CancellationToken.None));
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound is { IsPeerPaused: true } outbound &&
                  outbound.ProgressBytes >= receiverProgressAtPause,
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames
                .OfType<FileTransferStateFrameV4>()
                .Skip(stateCountBeforePause)
                .Any(state => state is not FileTransferReceiverStateFrameV6 &&
                              state.TransferPaused &&
                              state.BytesCommitted >= receiverProgressAtPause),
            timeoutMs: 5000);
    }

    [Fact]
    public async Task PrimaryRegularNknV4Fast_TunaActivationPauseStopsSenderPump()
    {
        const string transferId = "transfer_v4_sender_tuna_activation_pause";
        const string sessionId = "session_v4_sender_tuna_activation_pause";
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
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-tuna-activation-pause.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(),
            timeoutMs: 5000);

        senderTransport.SetLocalDataSessionsUnavailableForTests("tuna_activation_negotiating");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_v4_sender_pump_transport_paused;",
                StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentPauseControls.Any(static pause =>
                pause.Paused &&
                string.Equals(pause.Reason, "tuna_activation_negotiating", StringComparison.Ordinal)),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.IsPeerPaused == true &&
                  string.Equals(receiver.Snapshot.Inbound.PeerPauseReason, "tuna_activation_negotiating", StringComparison.Ordinal),
            timeoutMs: 5000);

        var chunkBatchesAtPause = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Count();
        await Task.Delay(300);

        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, Assert.Single(senderTransport.SentSessionOpens).ProtocolVersion);
        Assert.Equal(chunkBatchesAtPause, senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Count());
        Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);

        senderTransport.SetLocalDataSessionsAvailableForTests("tuna_activation_negotiation_released");
        await WaitUntilAsync(
            () => senderTransport.SentPauseControls.Any(static pause =>
                !pause.Paused &&
                string.Equals(pause.Reason, "tuna_activation_negotiation_released", StringComparison.Ordinal)),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.IsPeerPaused == false,
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_transport_pause_control_retry_scheduled; direction=outbound", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknV4Fast_TunaActivationPauseBeforeManifestWaitsForRelease()
    {
        const string transferId = "transfer_v4_sender_tuna_activation_pause_before_manifest";
        const string sessionId = "session_v4_sender_tuna_activation_pause_before_manifest";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 1_500_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            ThrowWhenUnavailableDataSessionSend = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.Connect(receiverTransport);
        senderTransport.OutboundSessionOpenDeliveryOverrideAsync = (target, message, ct) =>
        {
            target.ReceiveDeliveredSessionOpen(message);
            senderTransport.SetLocalDataSessionsUnavailableForTests("tuna_activation_negotiating");
            return Task.FromResult(true);
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-tuna-activation-pause-before-manifest.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentPauseControls.Any(pause =>
                pause.Paused &&
                string.Equals(pause.TransferId, transferId, StringComparison.Ordinal) &&
                string.Equals(pause.Reason, "tuna_activation_negotiating", StringComparison.Ordinal)),
            timeoutMs: 20_000);
        Assert.DoesNotContain(
            senderTransport.SentDataFrames,
            frame => string.Equals(frame.TransferId, transferId, StringComparison.Ordinal) &&
                     frame is FileTransferManifestFrameV4);
        senderTransport.SetLocalDataSessionsAvailableForTests("tuna_activation_negotiation_released");
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, Assert.Single(senderTransport.SentSessionOpens).ProtocolVersion);
        Assert.Contains(
            senderTransport.SentDataFrames,
            frame => string.Equals(frame.TransferId, transferId, StringComparison.Ordinal) &&
                     frame is FileTransferManifestFrameV4 and not FileTransferManifestFrameV6);
        Assert.Equal(payload, destination.ToArray());
        var logTail = ReadV4SenderLogSnapshot(logStart);
        Assert.DoesNotContain("event=filetransfer_v4_sender_failed;", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_paused; direction=outbound", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknV4Fast_TunaActivationFailureResumeDoesNotStarveTransfer()
    {
        const string transferId = "transfer_v4_sender_tuna_activation_failure_resume";
        const string sessionId = "session_v4_sender_tuna_activation_failure_resume";
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
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-tuna-activation-failure-resume.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(),
            timeoutMs: 5000);

        senderTransport.SetLocalDataSessionsUnavailableForTests("tuna_activation_negotiating");
        await WaitUntilAsync(
            () => ReadV4SenderLogSnapshot(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentPauseControls.Any(static pause =>
                pause.Paused &&
                string.Equals(pause.Reason, "tuna_activation_negotiating", StringComparison.Ordinal)),
            timeoutMs: 5000);

        senderTransport.SetLocalDataSessionsAvailableForTests("tuna_activation_failed_regular_v4_resumed");
        await WaitUntilAsync(
            () => senderTransport.SentPauseControls.Any(static pause =>
                !pause.Paused &&
                string.Equals(pause.Reason, "tuna_activation_failed_regular_v4_resumed", StringComparison.Ordinal)),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, Assert.Single(senderTransport.SentSessionOpens).ProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, Assert.Single(senderTransport.SentOffers).FileTransferRoute);
        Assert.Equal(payload, destination.ToArray());
        var logTail = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain("event=filetransfer_v4_sender_failed;", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTunaFallbackV6_TunaActivationFailureResumeDoesNotStarveTransfer()
    {
        const string transferId = "transfer_v6_post_fallback_tuna_activation_failure_resume";
        const string sessionId = "session_v6_post_fallback_tuna_activation_failure_resume";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_500_000).Select(static index => (byte)(index % 251)).ToArray();
        var releasePostPauseFrames = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame is FileTransferChunkBatchFrameV6 { StartChunkIndex: > 0 } &&
                Volatile.Read(ref releasePostPauseFrames) == 0)
            {
                while (Volatile.Read(ref releasePostPauseFrames) == 0)
                {
                    await Task.Delay(10, ct);
                }
            }

            return false;
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("post-fallback-v6-tuna-activation-failure-resume.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0),
            timeoutMs: 5000);
        senderTransport.SetConnectedDataSessionsUnavailableForTests("tuna_activation_negotiating");
        await WaitUntilAsync(
            () => ReadV4SenderLogSnapshot(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);

        senderTransport.SetConnectedDataSessionsAvailableForTests("tuna_activation_failed_regular_v4_resumed");
        Volatile.Write(ref releasePostPauseFrames, 1);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, Assert.Single(senderTransport.SentSessionOpens).ProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.PostTunaFallbackV6Token, Assert.Single(senderTransport.SentOffers).FileTransferRoute);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        Assert.False(senderTransport.IsPostTunaFileFallbackActiveForRouteSelection);
        Assert.False(receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection);
        var logTail = ReadV4SenderLogSnapshot(logStart);
        Assert.DoesNotContain("event=filetransfer_v6_sender_failed;", logTail, StringComparison.Ordinal);
        Assert.Contains(
            senderTransport.SentPauseControls,
            pause => !pause.Paused &&
                     string.Equals(pause.Reason, "tuna_activation_failed_regular_v4_resumed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task V6SparseSender_CompletesFromTerminalReadyStateWhenLifecycleCompleteIsLost()
    {
        const string transferId = "transfer_v6_sparse_terminal_ready_complete";
        const string sessionId = "session_v6_sparse_terminal_ready_complete";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 768_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            IsDiagnosticRegularNknV6RouteEnabled = true,
            TransportAccelerationStatusReason = "test_tuna_pending",
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            IsDiagnosticRegularNknV6RouteEnabled = true,
            TransportAccelerationStatusReason = "test_tuna_pending",
            OutboundCompleteDeliveryOverrideAsync = (_, _, _) => Task.FromResult(true),
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-terminal-ready.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 15000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, Assert.Single(senderTransport.SentSessionOpens).ProtocolVersion);
        Assert.Contains(receiverTransport.SentCompletes, complete =>
            string.Equals(complete.TransferId, transferId, StringComparison.Ordinal));
        Assert.Contains(
            receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>(),
            state => state.TerminalReady &&
                     state.ContiguousCommittedChunkIndex >= sender.Snapshot.Outbound!.ChunkCount);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v4_terminal_ready_completion_inferred;", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=complete_received;", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_OutboundProgressTracksReceiverSparseBytesWritten()
    {
        const string transferId = "transfer_primary_regular_nkn_progress_sparse_written";
        const string sessionId = "session_primary_regular_nkn_progress_sparse_written";
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var droppedFrontierBatchCount = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            IsDiagnosticRegularNknV6RouteEnabled = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            IsDiagnosticRegularNknV6RouteEnabled = true,
        };
        senderTransport.OutboundDataFrameDeliveryOverrideWithLaneAsync = (_, frame, _, _) =>
        {
            if (frame is FileTransferChunkBatchFrameV6 batch &&
                batch.StartChunkIndex <= 0 &&
                batch.StartChunkIndex + batch.ChunkCount > 0)
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
            new FileTransferSendDescriptor("primary-regular-nkn-progress.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        var writtenProgressTarget = 3L * 21 * 1024;
        await WaitUntilAsync(
            () =>
            {
                var inbound = receiver.Snapshot.Inbound;
                var outbound = sender.Snapshot.Outbound;
                return Volatile.Read(ref droppedFrontierBatchCount) > 0 &&
                       inbound?.BytesTransferred == 0 &&
                       (inbound.BytesAcceptedForTransport ?? 0L) >= writtenProgressTarget &&
                       (outbound?.ProgressBytes ?? 0L) >= writtenProgressTarget;
            },
            timeoutMs: 10000);

        Assert.Equal(0, receiver.Snapshot.Inbound!.BytesTransferred);
        Assert.True(receiver.Snapshot.Inbound!.BytesAcceptedForTransport >= writtenProgressTarget);
        Assert.True(sender.Snapshot.Outbound!.ProgressBytes >= writtenProgressTarget);
        Assert.Contains(
            receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>(),
            state => state.ContiguousCommittedChunkIndex == 0 &&
                     state.BytesCommitted >= writtenProgressTarget);
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
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
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
        var primaryRegularBulkV6Selected = false;
        await WaitUntilAsync(
            () =>
            {
                var logTail = ReadOperationalLogTail(logStart);
                primaryRegularBulkV6Selected =
                    logTail.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=outbound", StringComparison.Ordinal) &&
                    logTail.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=inbound", StringComparison.Ordinal);
                return primaryRegularBulkV6Selected;
            },
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
        Assert.True(primaryRegularBulkV6Selected);
        Assert.Contains("event=filetransfer_v6_transport_probe_ack_sent; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.Contains("target_transport=tuna", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("reason=proof_timeout", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_TunaProbeAckHangDoesNotBlockPeerRepairRequest()
    {
        const string transferId = "transfer_primary_regular_nkn_bulk_v6_probe_ack_hang";
        const string sessionId = "session_primary_regular_nkn_bulk_v6_probe_ack_hang";
        var previousProbeAckTimeout = SessionFileTransferService.V6TransportProbeAckSendTimeoutOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var hungProbeAck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.OutboundTransportProbeDeliveryOverrideAsync = (_, _, _) => hungProbeAck.Task;
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        try
        {
            SessionFileTransferService.V6TransportProbeAckSendTimeoutOverrideForTests = TimeSpan.FromMilliseconds(100);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("primary-regular-nkn-bulk-v6-probe-ack-hang.bin", payload.Length, transferId),
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

            senderTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
            await receiverSession.SendAsync(
                new FileTransferTransportProbeFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    TransportEpoch = 1,
                    ProbeId = "v6-probe:1:ack-hang",
                    TargetTransport = "tuna",
                },
                CancellationToken.None);
            await receiverSession.SendAsync(
                new FileTransferFrontierRequestFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    TransportEpoch = 1,
                    RepairRequestId = "v6-frontier:1:probe-ack-hang",
                    Priority = "frontier",
                    RecoveryMode = "frontier_repair_only",
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 24, ChunkCount = 12 }],
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_frontier_repair_requested; direction=outbound", StringComparison.Ordinal),
                timeoutMs: 5000);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_transport_probe_ack_failed; direction=outbound", StringComparison.Ordinal),
                timeoutMs: 5000);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("repair_request_id=v6-frontier:1:probe-ack-hang", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_transport_probe_ack_failed; direction=outbound", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V6TransportProbeAckSendTimeoutOverrideForTests = previousProbeAckTimeout;
            hungProbeAck.TrySetResult(true);
        }
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
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
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
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
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
    public async Task PrimaryRegularNknBulkV6_TunaActivationFrontierRequestDoesNotCreateLegacyRegularNknHandoff()
    {
        const string transferId = "transfer_primary_regular_nkn_bulk_v6_tuna_frontier_no_legacy";
        const string sessionId = "session_primary_regular_nkn_bulk_v6_tuna_frontier_no_legacy";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            IsDiagnosticRegularNknV6RouteEnabled = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            IsDiagnosticRegularNknV6RouteEnabled = true,
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("primary-regular-nkn-bulk-v6-tuna-frontier-no-legacy.bin", payload.Length, transferId),
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

        var acceptedTail = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .Where(static batch => batch.RepairRequestId is null)
            .Select(static batch => batch.StartChunkIndex + batch.ChunkCount)
            .DefaultIfEmpty(0)
            .Max();
        Assert.InRange(acceptedTail, 2, 800);
        var frontierChunk = acceptedTail - 1;

        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);
        var handoffEpoch = senderTransport.SentTransportEpochs.Last().TransportEpoch;
        var repairRequestId = $"v6-frontier:{handoffEpoch}:{frontierChunk}:1";

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                TransportEpoch = handoffEpoch,
                RepairRequestId = repairRequestId,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = frontierChunk, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Any(batch =>
                    string.Equals(batch.RepairRequestId, repairRequestId, StringComparison.Ordinal) &&
                    batch.TransportEpoch == handoffEpoch),
            timeoutMs: 5000);

        await receiverTransport.SendFileTransferRepairProofAsync(
            new FileTransferRepairProofV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                TransportEpoch = handoffEpoch,
                RepairRequestId = repairRequestId,
                AppliedChunkCount = 1,
                CommittedChunkIndex = acceptedTail,
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);

        var logAfterCleanState = GetOperationalLogLength();
        var batchCountBeforeCleanState = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = acceptedTail,
                DurableReceivedHighestChunkIndex = acceptedTail + 31,
                CreditUntilChunkIndexExclusive = acceptedTail + 64,
                MissingRanges = [],
                BytesCommitted = acceptedTail * 21 * 1024,
                TransportEpoch = handoffEpoch,
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforeCleanState)
                .Any(batch =>
                    batch.StartChunkIndex >= acceptedTail &&
                    batch.RepairRequestId is null &&
                    batch.RecoveryMode is null),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        var postCleanStateLog = ReadOperationalLogTail(logAfterCleanState);
        var announcedEpoch = Assert.Single(senderTransport.SentTransportEpochs.Where(epoch => epoch.TransportEpoch == handoffEpoch));
        Assert.Equal("normal_to_tuna_activation", announcedEpoch.HandoffKind);
        Assert.Equal("tuna", announcedEpoch.TargetTransport);
        Assert.DoesNotContain(
            senderTransport.SentTransportEpochs,
            epoch => string.Equals(epoch.HandoffKind, "regular_nkn_recovery", StringComparison.Ordinal));
        Assert.DoesNotContain("event=filetransfer_v6_handoff_epoch_started; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("handoff_kind=regular_nkn_recovery; source_transport=unknown; target_transport=regular_nkn; reason=peer_repair_request", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_tail_blocked_until_frontier_proof; direction=outbound;", postCleanStateLog, StringComparison.Ordinal);
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
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
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
    public async Task PrimaryRegularNknBulkV6_UsesSparseCreditEngineUnderV6Envelope()
    {
        const string transferId = "transfer_v6_sparse_runtime_flag";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_flag");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_flag");
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            var routeRuntimeLogTail = string.Empty;
            await WaitUntilAsync(
                () =>
                {
                    var currentTail = ReadOperationalLogTail(logStart);
                    var observed =
                        currentTail.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=outbound", StringComparison.Ordinal) &&
                        currentTail.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=inbound", StringComparison.Ordinal);
                    if (observed)
                    {
                        routeRuntimeLogTail = currentTail;
                    }

                    return observed;
                },
                timeoutMs: 5000);

            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 15000);

            Assert.Equal(payload, destination.ToArray()[..payload.Length]);
            Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferChunkBatchFrameV6);
            Assert.Contains(receiverTransport.SentDataFrames, static frame => frame is FileTransferReceiverStateFrameV6);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=outbound", routeRuntimeLogTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=inbound", routeRuntimeLogTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v4_chunk_batch_sent;", logTail, StringComparison.Ordinal);
            Assert.Contains("frame_type=filetransfer.receiver_state.v6", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v4_finalize_started;", logTail, StringComparison.Ordinal);
        }
        finally
        {
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_RequestsV4FileOnlyFrontierRepairsUnderV6Envelope()
    {
        const string transferId = "transfer_v6_sparse_runtime_file_only_repair";
        const int expectedFrontierRepairChunks = 12;
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_file_only_repair");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_file_only_repair");
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(
                frame is FileTransferChunkBatchFrameV6 { RepairRequestId: null } batch &&
                batch.StartChunkIndex < FileTransferProtocol.MaxStateMissingChunksV4);
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
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
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_PrioritizesFirstFrontierRepairAndSuppressesRecentRepeat()
    {
        const string transferId = "transfer_v6_regular_nkn_frontier_repair_lane";
        const string sessionId = "session_v6_regular_nkn_frontier_repair_lane";
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
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-regular-nkn-frontier-repair-lane.bin", payload.Length, transferId),
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
                .Any(static frame => frame.StartChunkIndex == 0),
            timeoutMs: 5000);

        var repairState = new FileTransferReceiverStateFrameV6
        {
            SessionId = offer.SessionId,
            TransferId = transferId,
            Epoch = 2,
            ContiguousCommittedChunkIndex = 0,
            DurableReceivedHighestChunkIndex = 24,
            CreditUntilChunkIndexExclusive = 128,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
            BytesCommitted = 0,
        };
        await receiverSession.SendAsync(repairState, CancellationToken.None);

        await WaitUntilAsync(
            () =>
            {
                var logTail = ReadOperationalLogTail(logStart);
                return logTail.Contains("event=filetransfer_v4_repair_sent;", StringComparison.Ordinal) &&
                       logTail.Contains("repair_request_key=0:12:0:12", StringComparison.Ordinal) &&
                       logTail.Contains("repair_delivery_escalation_reason=primary_regular_nkn_frontier_first_send", StringComparison.Ordinal);
            },
            timeoutMs: 5000);

        Assert.Contains(
            senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>(),
            static batch => batch.BatchProfile == "v4_repair_21k" &&
                            batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant);

        await receiverSession.SendAsync(repairState with { Epoch = 3 }, CancellationToken.None);
        await WaitUntilAsync(
            () =>
            {
                var logTail = ReadOperationalLogTail(logStart);
                return logTail.Contains("event=filetransfer_v4_repair_suppressed;", StringComparison.Ordinal) &&
                       logTail.Contains("repair_request_key=0:12:0:12", StringComparison.Ordinal) &&
                       logTail.Contains("reason=recently_sent", StringComparison.Ordinal) &&
                       logTail.Contains("repair_interval_ms=750", StringComparison.Ordinal);
            },
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        var scheduledFrontierFirstSendCount = log
            .Split('\n')
            .Count(static line => line.Contains("event=filetransfer_v4_repair_scheduled;", StringComparison.Ordinal) &&
                                  line.Contains("repair_request_key=0:12:0:12", StringComparison.Ordinal) &&
                                  line.Contains("repair_delivery_escalation_reason=primary_regular_nkn_frontier_first_send", StringComparison.Ordinal));
        Assert.Equal(1, scheduledFrontierFirstSendCount);
        Assert.Contains("scheduled_chunk_count=12", log, StringComparison.Ordinal);
        Assert.Contains("scheduled_chunk_indices=0,1,2,3,4,5,6,7,8,9,10,11", log, StringComparison.Ordinal);
        Assert.Contains("sent_chunk_count=12", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_frontier_repair_anchor_barrier;", log, StringComparison.Ordinal);
        Assert.Contains("anchor_start_chunk_index=0; anchor_chunk_count=3; remaining_chunk_count=9", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_mode=control_bulk_escalated", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_escalation_reason=primary_regular_nkn_frontier_first_send", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_UsesFrontierRepairTransactionIdForSenderDedupe()
    {
        const string transferId = "transfer_v6_regular_nkn_frontier_repair_transaction";
        const string sessionId = "session_v6_regular_nkn_frontier_repair_transaction";
        const string repairTransactionId = "regular-nkn-frontier:0:12:1";
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
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-regular-nkn-frontier-repair-transaction.bin", payload.Length, transferId),
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
                .Any(static frame => frame.StartChunkIndex == 0),
            timeoutMs: 5000);

        var repairState = new FileTransferReceiverStateFrameV6
        {
            SessionId = offer.SessionId,
            TransferId = transferId,
            Epoch = 2,
            ContiguousCommittedChunkIndex = 0,
            DurableReceivedHighestChunkIndex = 24,
            CreditUntilChunkIndexExclusive = 128,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
            BytesCommitted = 0,
            RepairRequestId = repairTransactionId,
            Priority = "frontier",
            RecoveryMode = "regular_nkn_frontier_stall_control_bulk",
        };
        await receiverSession.SendAsync(repairState, CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(batch =>
                string.Equals(batch.RepairRequestId, repairTransactionId, StringComparison.Ordinal) &&
                batch.Priority == "frontier"),
            timeoutMs: 5000);

        await receiverSession.SendAsync(repairState with { Epoch = 3 }, CancellationToken.None);
        await WaitUntilAsync(
            () =>
            {
                var logTail = ReadOperationalLogTail(logStart);
                return logTail.Contains("event=filetransfer_v4_repair_suppressed;", StringComparison.Ordinal) &&
                       logTail.Contains($"repair_request_key={repairTransactionId}", StringComparison.Ordinal) &&
                       logTail.Contains($"protocol_repair_request_id={repairTransactionId}", StringComparison.Ordinal) &&
                       logTail.Contains("reason=recently_sent", StringComparison.Ordinal);
            },
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_primary_regular_nkn_frontier_repair_transaction_received", log, StringComparison.Ordinal);
        Assert.Contains($"repair_request_key={repairTransactionId}", log, StringComparison.Ordinal);
        Assert.Contains($"protocol_repair_request_id={repairTransactionId}", log, StringComparison.Ordinal);
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
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
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
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_sent",
                    StringComparison.Ordinal),
                timeoutMs: 5000);
            var logTail = ReadOperationalLogTail(logStart);
            var evidenceLog = logTail + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_prepared", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_sent", logTail, StringComparison.Ordinal);
            Assert.Contains($"event=filetransfer_bridge_recovery_policy_selected; direction=outbound; transfer_id={transferId}", evidenceLog, StringComparison.Ordinal);
            Assert.Contains("bridge_recovery_policy=primary_regular_nkn_quiet", evidenceLog, StringComparison.Ordinal);
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
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
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
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
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
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
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
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
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
                  senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(),
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
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_sent; direction=outbound",
                StringComparison.Ordinal),
            timeoutMs: 5000);

        Assert.Equal(transportEpochCountBeforeRebind, senderTransport.SentTransportEpochs.Count + receiverTransport.SentTransportEpochs.Count);
        Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_rebind_started; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_sent; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_epoch_started; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_transport_rebind_generation_started; direction=outbound", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_DefersOutboundV6PeerLivenessTerminal()
    {
        const string transferId = "transfer_v6_sparse_runtime_v6_liveness";
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropReceiverFeedback = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_v6_liveness");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_v6_liveness");
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.DataSessionSendDelayMs = 20;
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        receiverTransport.OutboundHeartbeatDeliveryOverrideAsync = (_, _, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1);
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
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
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_DefersInboundV6PeerLivenessTerminal()
    {
        const string transferId = "transfer_v6_sparse_runtime_inbound_v6_liveness";
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropSenderTraffic = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_inbound_v6_liveness");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_inbound_v6_liveness");
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.DataSessionSendDelayMs = 20;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropSenderTraffic) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        senderTransport.OutboundHeartbeatDeliveryOverrideAsync = (_, _, _) =>
            Task.FromResult(Volatile.Read(ref dropSenderTraffic) == 1);
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
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
        }
    }

    [Fact(Skip = RetiredPostTunaFallbackSparseRuntimeSkip)]
    public async Task PostTunaFallbackSparseRuntime_OutboundFeedbackSilenceDefersTerminal()
    {
        const string transferId = "transfer_v6_post_fallback_feedback_silence";
        const string sessionId = "session_v6_post_fallback_feedback_silence";
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousFeedbackDelay = SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests;
        var previousRefreshCooldown = SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests;
        var previousStaleCreditDelay = SessionFileTransferService.V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelayOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropReceiverFeedback = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        try
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-post-fallback-feedback-silence.bin", payload.Length, transferId),
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
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0),
                timeoutMs: 5000);

            receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
            senderTransport.RequestAllDataSessionHandoffs(
                "tuna_activation_negotiated",
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna);
            await WaitUntilAsync(
                () => senderTransport.ObservedV6TransportEpochs.Any(static snapshot =>
                    snapshot.IsUnresolved &&
                    snapshot.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    snapshot.TargetTransport == FileTransferTransportKind.Tuna),
                timeoutMs: 5000);
            var activationProbe = await ReceiveDataFrameOfTypeAsync<FileTransferTransportProbeFrameV6>(receiverSession);
            await receiverTransport.SendFileTransferTransportProbeAsync(
                new FileTransferTransportProbeV6
                {
                    SessionId = activationProbe.SessionId,
                    TransferId = activationProbe.TransferId,
                    TransportEpoch = activationProbe.TransportEpoch,
                    ProbeId = activationProbe.ProbeId,
                    TargetTransport = activationProbe.TargetTransport,
                },
                CancellationToken.None);
            await WaitUntilAsync(
                () => senderTransport.ObservedV6TransportEpochs.Any(snapshot =>
                    !snapshot.IsUnresolved &&
                    snapshot.State == V6TransportEpochState.Recovered &&
                    snapshot.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation),
                timeoutMs: 5000);

            senderTransport.RequestAllDataSessionHandoffs(
                "sidecar_read_failed",
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn);
            await WaitUntilAsync(
                () => senderTransport.ObservedV6TransportEpochs.Any(static snapshot =>
                    snapshot.IsUnresolved &&
                    snapshot.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                    snapshot.TargetTransport == FileTransferTransportKind.RegularNkn),
                timeoutMs: 5000);
            var probe = await ReceiveDataFrameOfTypeAsync<FileTransferTransportProbeFrameV6>(receiverSession);
            await receiverTransport.SendFileTransferTransportProbeAsync(
                new FileTransferTransportProbeV6
                {
                    SessionId = probe.SessionId,
                    TransferId = probe.TransferId,
                    TransportEpoch = probe.TransportEpoch,
                    ProbeId = probe.ProbeId,
                    TargetTransport = probe.TargetTransport,
                },
                CancellationToken.None);
            await WaitUntilAsync(
                () => senderTransport.ObservedV6TransportEpochs.Any(snapshot =>
                    !snapshot.IsUnresolved &&
                    snapshot.State == V6TransportEpochState.Recovered &&
                    snapshot.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback),
                timeoutMs: 5000);

            Volatile.Write(ref dropReceiverFeedback, 1);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v4_post_tuna_fallback_peer_feedback_timeout_deferred; direction=outbound",
                    StringComparison.Ordinal),
                timeoutMs: 10000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain($"event=filetransfer_v4_peer_feedback_timeout; transfer_id={transferId}", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = previousFeedbackDelay;
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = previousRefreshCooldown;
            SessionFileTransferService.V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelayOverrideForTests = previousStaleCreditDelay;
        }
    }

    [Fact(Skip = RetiredPostTunaFallbackSparseRuntimeSkip)]
    public async Task PostTunaFallbackSparseRuntime_InboundReceiveSilenceDefersTerminal()
    {
        const string transferId = "transfer_v6_post_fallback_receive_silence";
        const string sessionId = "session_v6_post_fallback_receive_silence";
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropSenderTraffic = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.DataSessionSendDelayMs = 20;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropSenderTraffic) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-post-fallback-receive-silence.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Receiving &&
                      destination.Length > 0,
                timeoutMs: 5000);

            receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
            receiverTransport.RequestAllDataSessionHandoffs(
                "tuna_activation_negotiated",
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna);
            await WaitUntilAsync(
                () => receiverTransport.ObservedV6TransportEpochs.Any(static snapshot =>
                    snapshot.IsUnresolved &&
                    snapshot.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    snapshot.TargetTransport == FileTransferTransportKind.Tuna),
                timeoutMs: 5000);

            receiverTransport.RequestAllDataSessionHandoffs(
                "sidecar_read_failed",
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn);
            await WaitUntilAsync(
                () => receiverTransport.ObservedV6TransportEpochs.Any(static snapshot =>
                    snapshot.IsUnresolved &&
                    snapshot.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                    snapshot.TargetTransport == FileTransferTransportKind.RegularNkn),
                timeoutMs: 5000);

            Volatile.Write(ref dropSenderTraffic, 1);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v4_post_tuna_fallback_peer_receive_timeout_deferred; direction=inbound",
                    StringComparison.Ordinal),
                timeoutMs: 10000);

            Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound?.State);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain($"event=filetransfer_v4_peer_receive_timeout; transfer_id={transferId}", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_PausedTransportBlocksV4SenderPump()
    {
        const string transferId = "transfer_v6_sparse_runtime_transport_pause";
        const string sessionId = "session_v6_sparse_runtime_transport_pause";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
        senderTransport.TransportAccelerationStatusReason = "test_tuna_pending";
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
        receiverTransport.TransportAccelerationStatusReason = "test_tuna_pending";
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        try
        {
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
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_TunaFallbackPauseAllowsQueuedRepair()
    {
        const string transferId = "transfer_v6_sparse_runtime_fallback_repair";
        const string sessionId = "session_v6_sparse_runtime_fallback_repair";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
        senderTransport.TransportAccelerationStatusReason = "test_tuna_pending";
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
        receiverTransport.TransportAccelerationStatusReason = "test_tuna_pending";
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        try
        {
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-fallback-repair.bin", payload.Length, transferId),
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
            senderTransport.SetLocalDataSessionsUnavailableForTests("remote_remote_closed");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal),
                timeoutMs: 5000);

            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 2,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = 16,
                    CreditUntilChunkIndexExclusive = 32,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count() > chunkBatchesBeforePause,
                timeoutMs: 5000);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v4_repair_sent;", logTail, StringComparison.Ordinal);
            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
        }
        finally
        {
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_AbandonsPendingSendsAfterPausedTransportFailure()
    {
        const string transferId = "transfer_v6_sparse_runtime_abandon_pending";
        const string sessionId = "session_v6_sparse_runtime_abandon_pending";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 4_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var firstSendGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldSendGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunkSendAttempts = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
        senderTransport.TransportAccelerationStatusReason = "test_tuna_pending";
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
        receiverTransport.TransportAccelerationStatusReason = "test_tuna_pending";
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
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_BoundsHungNormalTransportSends()
    {
        const string transferId = "transfer_v6_sparse_runtime_bound_normal_send";
        const string sessionId = "session_v6_sparse_runtime_bound_normal_send";
        var previousSendTimeout = SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame is not FileTransferChunkBatchFrameV6)
            {
                return false;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return true;
        };
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        try
        {
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
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(),
            timeoutMs: 5000);

        var canceled = await sender.CancelTransferAsync(transferId, "user_canceled", CancellationToken.None);

        Assert.NotNull(canceled);
        Assert.Equal(FileTransferTransferState.Canceled, canceled!.State);
        Assert.Equal(FileTransferResultCodes.CanceledLocal, canceled.ErrorCode);
        Assert.Contains(senderTransport.SentCancels, cancel => string.Equals(cancel.TransferId, transferId, StringComparison.Ordinal));
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferCancelFrameV4 and not FileTransferCancelFrameV6);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 6000);
        await WaitUntilAsync(() => System.Threading.Volatile.Read(ref cancelControlAttempts) >= 2, timeoutMs: 3000);
        Assert.True(cancelControlAttempts >= 2);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);
    }

    [Fact]
    public async Task V4Sender_LocalCancel_RetrySurvivesTransientOperationCanceled()
    {
        const string transferId = "transfer_v4_sender_cancel_retry_operation_canceled";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 768_000).Select(static index => (byte)(index % 251)).ToArray();
        var cancelControlAttempts = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_cancel_retry_operation_canceled");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_cancel_retry_operation_canceled");
        senderTransport.DataSessionSendDelayMs = 10;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(frame is FileTransferCancelFrameV4);
        senderTransport.OutboundCancelDeliveryOverrideAsync = (_, _, _) =>
        {
            var attempt = Interlocked.Increment(ref cancelControlAttempts);
            return attempt <= 2
                ? Task.FromException<bool>(new OperationCanceledException("Injected transient cancel send cancellation."))
                : Task.FromResult(false);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-cancel-retry-operation-canceled.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(),
            timeoutMs: 5000);

        var canceled = await sender.CancelTransferAsync(transferId, "user_canceled", CancellationToken.None);

        Assert.NotNull(canceled);
        Assert.Equal(FileTransferTransferState.Canceled, canceled!.State);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 6000);
        await WaitUntilAsync(() => System.Threading.Volatile.Read(ref cancelControlAttempts) >= 3, timeoutMs: 3000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_cancel_control_retry_deferred", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Sender_LocalCancel_UsesRedundantDataCancelWhenControlPathIsLost()
    {
        const string transferId = "transfer_v6_fallback_sender_cancel_data_retry";
        var payload = Enumerable.Range(0, 1_500_000).Select(static index => (byte)(index % 251)).ToArray();
        var cancelDataAttempts = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_sender_cancel_data_retry");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_sender_cancel_data_retry");
        senderTransport.DataSessionSendDelayMs = 10;
        senderTransport.OutboundCancelDeliveryOverrideAsync = (_, _, _) => Task.FromResult(true);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferCancelFrameV6)
            {
                Interlocked.Increment(ref cancelDataAttempts);
            }

            return Task.FromResult(false);
        };
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-cancel-data-retry.bin", payload.Length, transferId),
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
        Assert.True(cancelDataAttempts > 0);
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferCancelFrameV6);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 6000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);
    }

    [Fact]
    public async Task V6FallbackSender_LocalCancel_IgnoresCanceledCallerTokenAndUsesPriorityControl()
    {
        const string transferId = "transfer_v6_fallback_sender_cancel_priority_token";
        var payload = Enumerable.Range(0, 1_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_sender_cancel_priority_token");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_sender_cancel_priority_token");
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(frame is FileTransferCancelFrameV6 or FileTransferChunkBatchFrameV6);
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-cancel-priority-token.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(),
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
            timeoutMs: 10_000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);

        var logText = ReadOperationalLogText();
        Assert.Contains("event=filetransfer_lifecycle_priority_sent; kind=cancel", logText, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_lifecycle_priority_received; kind=cancel", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6FallbackReceiver_RemoteCancelBypassesBlockedLifecycleTail()
    {
        const string transferId = "transfer_v6_fallback_cancel_bypasses_lifecycle";
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_cancel_bypasses_lifecycle");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_cancel_bypasses_lifecycle");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-cancel-bypasses-lifecycle.bin", payload.Length, transferId),
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
                SessionId = "session_v6_fallback_cancel_bypasses_lifecycle",
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
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(),
            timeoutMs: 5000);

        var canceledCount = await sender.CancelActiveTransfersForSessionEndAsync("session_end", CancellationToken.None);

        Assert.Equal(1, canceledCount);
        Assert.Equal(FileTransferTransferState.Canceled, sender.Snapshot.Outbound?.State);
        Assert.Equal(FileTransferResultCodes.CanceledLocal, sender.Snapshot.Outbound?.ErrorCode);
        Assert.Contains(senderTransport.SentCancels, cancel => string.Equals(cancel.Reason, "session_end", StringComparison.Ordinal));
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferCancelFrameV4 and not FileTransferCancelFrameV6);
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

    [Fact(Skip = RetiredRegularNknV4ToV6RecoverySkip)]
    public async Task V4Sender_ControlReceiveStallExhausted_StartsV6RegularNknRecoveryWithoutSessionDisconnect()
    {
        const string transferId = "transfer_v4_sender_control_stall_exhausted";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 5_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_control_stall_exhausted");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_control_stall_exhausted");
        senderTransport.DataSessionSendDelayMs = 5;
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
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
            Assert.Equal(FileTransferResultCodes.TransportIncompatible, sender.Snapshot.Outbound?.ErrorCode);
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

    [Fact]
    public async Task FileTunaV4_LiveSwitchOffRecovery_PromotesSameTransferToPostTunaFallbackV6()
    {
        const string transferId = "transfer_file_tuna_v4_live_switch_off_replay";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 768_000).Select(static index => (byte)(index % 229)).ToArray();
        var allowFrontierChunk = 0;
        var droppedFrontierBatchCount = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_file_tuna_v4_live_switch_off_replay");
        using var receiverTransport = new LoopbackFileTransferTransport("session_file_tuna_v4_live_switch_off_replay");
        ConfigureFileTunaV4RouteForTest(senderTransport, receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferChunkBatchFrameV4 { StartChunkIndex: 0 } &&
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
            new FileTransferSendDescriptor("file-tuna-v4-live-switch-off-replay.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static state =>
                state.MissingRanges.Any(static range =>
                    range.StartChunkIndex == 0 &&
                    range.ChunkCount > 0)),
            timeoutMs: 10000);
        Assert.True(Volatile.Read(ref droppedFrontierBatchCount) > 0);

        senderTransport.SetConnectedDataSessionsUnavailableForTests(
            "header_switch_off",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        var postTunaFallbackV6Observed = false;
        await WaitUntilAsync(
            () =>
            {
                postTunaFallbackV6Observed = postTunaFallbackV6Observed ||
                    ReadOperationalLogTail(logStart).Contains(
                        "route=post_tuna_fallback_v6; protocol_version=6;",
                        StringComparison.Ordinal);
                return postTunaFallbackV6Observed;
            },
            timeoutMs: 5000);
        Volatile.Write(ref allowFrontierChunk, 1);
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var log = ReadOperationalLogTail(logStart);
        Assert.True(postTunaFallbackV6Observed);
        Assert.Single(senderTransport.SentOffers.Where(offer => offer.TransferId == transferId));
        Assert.Single(senderTransport.SentSessionOpens.Where(open => open.TransferId == transferId));
        Assert.Empty(senderTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
        Assert.Empty(receiverTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
        Assert.Contains("event=filetransfer_v6_epoch_started; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_epoch_started; direction=inbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_safety_replay_started;", log, StringComparison.Ordinal);
        Assert.Contains("reason=post_tuna_fallback_v6_started", log, StringComparison.Ordinal);
        var postTunaReplayLine = log.Split(Environment.NewLine)
            .FirstOrDefault(static line =>
                line.Contains("event=filetransfer_transport_rebind_safety_replay_started;", StringComparison.Ordinal) &&
                line.Contains("reason=post_tuna_fallback_v6_started", StringComparison.Ordinal));
        Assert.NotNull(postTunaReplayLine);
        Assert.Contains("requested_chunk_count=1", postTunaReplayLine, StringComparison.Ordinal);
        Assert.Contains("post_tuna_v6_frontier_sweep=0", postTunaReplayLine, StringComparison.Ordinal);
        Assert.DoesNotContain("post_tuna_v6_frontier_sweep=1", log, StringComparison.Ordinal);
        Assert.Contains("handoff_kind=tuna_to_normal_fallback", log, StringComparison.Ordinal);
        Assert.Contains("route=post_tuna_fallback_v6", log, StringComparison.Ordinal);
        Assert.True(
            senderTransport.SentDataFrames.Where(frame => frame.TransferId == transferId).Any(FileTransferProtocol.IsV6DataFrame),
            "Expected same-transfer fallback V6 sender frames.");
        Assert.True(
            receiverTransport.SentDataFrames.Where(frame => frame.TransferId == transferId).Any(FileTransferProtocol.IsV6DataFrame),
            "Expected same-transfer fallback V6 receiver frames.");
        Assert.Contains(
            receiverTransport.SentDataFrames.Where(frame => frame.TransferId == transferId),
            static frame => frame is FileTransferReceiverStateFrameV6);
        Assert.DoesNotContain("event=filetransfer_v6_sender_started;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_receiver_started;", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileTunaV4_LiveSwitchOffRecovery_CanReenableTunaForSameTransfer()
    {
        const string transferId = "transfer_file_tuna_v4_live_switch_off_reenable";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 1_572_864).Select(static index => (byte)(index % 241)).ToArray();
        var allowFrontierChunk = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_file_tuna_v4_live_switch_off_reenable");
        using var receiverTransport = new LoopbackFileTransferTransport("session_file_tuna_v4_live_switch_off_reenable");
        ConfigureFileTunaV4RouteForTest(senderTransport, receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame.TransferId == transferId &&
                frame is FileTransferTransportProbeFrameV6 probe)
            {
                receiverTransport.NextDataFrameTransportKind = string.Equals(probe.TargetTransport, "tuna", StringComparison.OrdinalIgnoreCase)
                    ? FileTransferTransportKind.Tuna
                    : FileTransferTransportKind.RegularNkn;
            }

            if (frame is FileTransferChunkBatchFrameV4 { StartChunkIndex: 0 } &&
                Volatile.Read(ref allowFrontierChunk) == 0)
            {
                return true;
            }

            if (frame.TransferId == transferId)
            {
                await Task.Delay(2, ct);
            }

            return false;
        };
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame.TransferId == transferId &&
                frame is FileTransferTransportProbeFrameV6 probe)
            {
                senderTransport.NextDataFrameTransportKind = string.Equals(probe.TargetTransport, "tuna", StringComparison.OrdinalIgnoreCase)
                    ? FileTransferTransportKind.Tuna
                    : FileTransferTransportKind.RegularNkn;
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
            new FileTransferSendDescriptor("file-tuna-v4-live-switch-off-reenable.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static state =>
                state.MissingRanges.Any(static range =>
                    range.StartChunkIndex == 0 &&
                    range.ChunkCount > 0)),
            timeoutMs: 10000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests(
            "header_switch_off",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "route=post_tuna_fallback_v6; protocol_version=6;",
                StringComparison.Ordinal),
            timeoutMs: 5000);
        Volatile.Write(ref allowFrontierChunk, 1);
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_live_route_epoch_recovered; direction=outbound",
                StringComparison.Ordinal),
            timeoutMs: 10000);

        senderTransport.IsFileTunaActiveForRouteSelection = true;
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = false;
        receiverTransport.IsFileTunaActiveForRouteSelection = true;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = false;
        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_reenabled",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        receiverTransport.RequestAllDataSessionHandoffs(
            "tuna_reenabled",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "previous_route=post_tuna_fallback_v6; new_route=file_tuna_v4",
                StringComparison.Ordinal),
            timeoutMs: 10000);

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 25000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var log = ReadV4SenderLogSnapshot(logStart);
        Assert.Single(senderTransport.SentOffers.Where(offer => offer.TransferId == transferId));
        Assert.Single(senderTransport.SentSessionOpens.Where(open => open.TransferId == transferId));
        Assert.Empty(senderTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
        Assert.Empty(receiverTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
        Assert.Contains("event=filetransfer_live_route_epoch_started; direction=outbound", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_live_route_epoch_started; direction=inbound", log, StringComparison.Ordinal);
        Assert.Contains("route=post_tuna_fallback_v6", log, StringComparison.Ordinal);
        Assert.Contains("route=file_tuna_v4", log, StringComparison.Ordinal);
        Assert.Contains("handoff_kind=normal_to_tuna_activation", log, StringComparison.Ordinal);
        Assert.Contains("target_transport=tuna", log, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "handoff_kind=normal_to_tuna_activation; source_transport=regular_nkn; target_transport=tuna; reason=tuna_reenabled; state=target_proof_pending",
            log,
            StringComparison.Ordinal);
        Assert.Contains("live_route_epoch=", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_runtime_started; direction=outbound; role=sender;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_runtime_started; direction=inbound; role=receiver;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_live_route_epoch_terminal; direction=outbound", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_live_route_epoch_terminal; direction=inbound", log, StringComparison.Ordinal);

        const string activeTunaNextTransferId = "transfer_file_tuna_v4_after_live_reenable_active";
        await RunCompletedLoopbackTransferAsync(
            sender,
            receiver,
            activeTunaNextTransferId,
            "file-tuna-v4-after-live-reenable-active.bin");
        var activeTunaNextOffer = senderTransport.SentOffers.Single(offer => offer.TransferId == activeTunaNextTransferId);
        Assert.Equal(FileTransferRouteResolver.FileTunaV4Token, activeTunaNextOffer.FileTransferRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, activeTunaNextOffer.PreferredDataProtocolVersion);

        senderTransport.IsFileTunaActiveForRouteSelection = false;
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = false;
        receiverTransport.IsFileTunaActiveForRouteSelection = false;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = false;
        const string inactiveNextTransferId = "transfer_file_tuna_v4_after_live_reenable_inactive";
        await RunCompletedLoopbackTransferAsync(
            sender,
            receiver,
            inactiveNextTransferId,
            "file-tuna-v4-after-live-reenable-inactive.bin");
        var inactiveNextOffer = senderTransport.SentOffers.Single(offer => offer.TransferId == inactiveNextTransferId);
        Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, inactiveNextOffer.FileTransferRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, inactiveNextOffer.PreferredDataProtocolVersion);
    }

    [Fact]
    public async Task FileTunaV4_LiveSwitchOffRecovery_CanCycleTunaOffOnOffForSameTransfer()
    {
        const string transferId = "transfer_file_tuna_v4_live_cycle_off_on_off";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 1_572_864).Select(static index => (byte)(index % 239)).ToArray();
        var allowFrontierChunk = 0;
        var blockPostReenableV4Chunks = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_file_tuna_v4_live_cycle_off_on_off");
        using var receiverTransport = new LoopbackFileTransferTransport("session_file_tuna_v4_live_cycle_off_on_off");
        ConfigureFileTunaV4RouteForTest(senderTransport, receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame.TransferId == transferId &&
                frame is FileTransferTransportProbeFrameV6 probe)
            {
                receiverTransport.NextDataFrameTransportKind = string.Equals(probe.TargetTransport, "tuna", StringComparison.OrdinalIgnoreCase)
                    ? FileTransferTransportKind.Tuna
                    : FileTransferTransportKind.RegularNkn;
            }

            if (frame is FileTransferChunkBatchFrameV4 { StartChunkIndex: 0 } &&
                Volatile.Read(ref allowFrontierChunk) == 0)
            {
                return true;
            }

            if (frame is FileTransferChunkBatchFrameV4 { StartChunkIndex: > 0 } &&
                Volatile.Read(ref blockPostReenableV4Chunks) == 1)
            {
                return true;
            }

            if (frame.TransferId == transferId)
            {
                await Task.Delay(2, ct);
            }

            return false;
        };
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame.TransferId == transferId &&
                frame is FileTransferTransportProbeFrameV6 probe)
            {
                senderTransport.NextDataFrameTransportKind = string.Equals(probe.TargetTransport, "tuna", StringComparison.OrdinalIgnoreCase)
                    ? FileTransferTransportKind.Tuna
                    : FileTransferTransportKind.RegularNkn;
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
            new FileTransferSendDescriptor("file-tuna-v4-live-cycle-off-on-off.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static state =>
                state.MissingRanges.Any(static range =>
                    range.StartChunkIndex == 0 &&
                    range.ChunkCount > 0)),
            timeoutMs: 10000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests(
            "header_switch_off_first",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "live_route_epoch=1; route=post_tuna_fallback_v6",
                StringComparison.Ordinal),
            timeoutMs: 5000);
        Volatile.Write(ref allowFrontierChunk, 1);
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_live_route_epoch_recovered; direction=outbound",
                StringComparison.Ordinal),
            timeoutMs: 10000);

        Volatile.Write(ref blockPostReenableV4Chunks, 1);
        senderTransport.IsFileTunaActiveForRouteSelection = true;
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = false;
        receiverTransport.IsFileTunaActiveForRouteSelection = true;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = false;
        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_reenabled",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        receiverTransport.RequestAllDataSessionHandoffs(
            "tuna_reenabled",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "previous_route=post_tuna_fallback_v6; new_route=file_tuna_v4",
                StringComparison.Ordinal),
            timeoutMs: 10000);

        senderTransport.IsFileTunaActiveForRouteSelection = false;
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        receiverTransport.IsFileTunaActiveForRouteSelection = false;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        receiverTransport.SetConnectedDataSessionsUnavailableForTests(
            "helper_switch_off_second",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "live_route_epoch=3; previous_route=file_tuna_v4; route=post_tuna_fallback_v6",
                StringComparison.Ordinal),
            timeoutMs: 5000);
        Volatile.Write(ref blockPostReenableV4Chunks, 0);
        receiverTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 30000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var log = ReadV4SenderLogSnapshot(logStart);
        Assert.Single(senderTransport.SentOffers.Where(offer => offer.TransferId == transferId));
        Assert.Single(senderTransport.SentSessionOpens.Where(open => open.TransferId == transferId));
        Assert.Empty(senderTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
        Assert.Empty(receiverTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
        Assert.Contains("live_route_epoch=1; route=post_tuna_fallback_v6", log, StringComparison.Ordinal);
        Assert.Contains("live_route_epoch=2; route=file_tuna_v4", log, StringComparison.Ordinal);
        Assert.Contains("live_route_epoch=3; previous_route=file_tuna_v4; route=post_tuna_fallback_v6", log, StringComparison.Ordinal);
        var recoveredLiveRouteEpoch3Lines = log
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line =>
                line.Contains("event=filetransfer_live_route_epoch_recovered", StringComparison.Ordinal) &&
                line.Contains("live_route_epoch=3", StringComparison.Ordinal) &&
                line.Contains("route=post_tuna_fallback_v6", StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(recoveredLiveRouteEpoch3Lines, line => line.Contains("direction=outbound", StringComparison.Ordinal));
        Assert.Contains(recoveredLiveRouteEpoch3Lines, line => line.Contains("direction=inbound", StringComparison.Ordinal));
        Assert.Contains("handoff_kind=normal_to_tuna_activation", log, StringComparison.Ordinal);
        Assert.Contains("target_transport=tuna", log, StringComparison.Ordinal);
        Assert.Contains("target_transport=regular_nkn", log, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "handoff_kind=normal_to_tuna_activation; source_transport=regular_nkn; target_transport=tuna; reason=tuna_reenabled; state=target_proof_pending",
            log,
            StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_live_route_epoch_terminal; direction=outbound", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_live_route_epoch_terminal; direction=inbound", log, StringComparison.Ordinal);
        Assert.False(senderTransport.IsPostTunaFileFallbackActiveForRouteSelection);
        Assert.False(receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection);

        const string nextTransferId = "transfer_file_tuna_v4_after_live_cycle_inactive";
        await RunCompletedLoopbackTransferAsync(
            sender,
            receiver,
            nextTransferId,
            "file-tuna-v4-after-live-cycle-inactive.bin");
        var nextOffer = senderTransport.SentOffers.Single(offer => offer.TransferId == nextTransferId);
        Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, nextOffer.FileTransferRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, nextOffer.PreferredDataProtocolVersion);
    }

    [Fact]
    public async Task FileTunaV4_LiveSwitchOffRecovery_DoesNotUseLiveV4RepairAndCompletesSameTransfer()
    {
        const string transferId = "transfer_file_tuna_v4_live_switch_off_hung_repair";
        const string sessionId = "session_file_tuna_v4_live_switch_off_hung_repair";
        var previousTimeout = SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests;
        SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests = TimeSpan.FromMilliseconds(100);
        try
        {
            var logStart = ReadOperationalLogText().Length;
            var payload = Enumerable.Range(0, 768_000).Select(static index => (byte)(index % 233)).ToArray();
            var allowFrontierChunk = 0;
            var delayNextLiveFallbackRepair = 0;
            var delayedLiveFallbackRepairCount = 0;
            using var senderTransport = new LoopbackFileTransferTransport(sessionId);
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
            ConfigureFileTunaV4RouteForTest(senderTransport, receiverTransport);
            senderTransport.AllowUnavailableV4FallbackRecoveryFramesForTests = true;
            receiverTransport.AllowUnavailableV4FallbackRecoveryFramesForTests = true;
            senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
            {
                if (frame is FileTransferChunkBatchFrameV4 { StartChunkIndex: 0 } &&
                    Volatile.Read(ref allowFrontierChunk) == 0)
                {
                    return true;
                }

                if (frame is FileTransferChunkBatchFrameV4 { StartChunkIndex: 0 } &&
                    Interlocked.Exchange(ref delayNextLiveFallbackRepair, 0) == 1)
                {
                    Interlocked.Increment(ref delayedLiveFallbackRepairCount);
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }

                return false;
            };
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            using var destination = new NonDisposingMemoryStream();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("file-tuna-v4-live-switch-off-hung-repair.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static state =>
                    state.MissingRanges.Any(static range =>
                        range.StartChunkIndex == 0 &&
                        range.ChunkCount > 0)),
                timeoutMs: 10000);

            senderTransport.SetConnectedDataSessionsUnavailableForTests(
                "header_switch_off",
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "route=post_tuna_fallback_v6; protocol_version=6;",
                    StringComparison.Ordinal),
                timeoutMs: 5000);
            Volatile.Write(ref allowFrontierChunk, 1);
            senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 25000);

            Assert.Equal(payload, destination.ToArray()[..payload.Length]);
            Assert.Equal(0, Volatile.Read(ref delayedLiveFallbackRepairCount));
            Assert.Single(senderTransport.SentOffers.Where(offer => offer.TransferId == transferId));
            Assert.Single(senderTransport.SentSessionOpens.Where(open => open.TransferId == transferId));
            Assert.Empty(senderTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
            Assert.Empty(receiverTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
            var log = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_epoch_started; direction=outbound;", log, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_epoch_started; direction=inbound;", log, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_transport_rebind_safety_replay_started;", log, StringComparison.Ordinal);
            Assert.Contains("reason=post_tuna_fallback_v6_started", log, StringComparison.Ordinal);
            Assert.Contains("route=post_tuna_fallback_v6", log, StringComparison.Ordinal);
            Assert.True(
                senderTransport.SentDataFrames.Where(frame => frame.TransferId == transferId).Any(FileTransferProtocol.IsV6DataFrame),
                "Expected same-transfer fallback V6 sender frames.");
            Assert.True(
                receiverTransport.SentDataFrames.Where(frame => frame.TransferId == transferId).Any(FileTransferProtocol.IsV6DataFrame),
                "Expected same-transfer fallback V6 receiver frames.");
            Assert.Contains(
                receiverTransport.SentDataFrames.Where(frame => frame.TransferId == transferId),
                static frame => frame is FileTransferReceiverStateFrameV6);
            Assert.DoesNotContain("event=filetransfer_live_v4_fallback_cleanup_completed; direction=outbound;", log, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_sender_started;", log, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_receiver_started;", log, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests = previousTimeout;
        }
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

    [Fact(Skip = RetiredPostTunaFallbackSparseRuntimeSkip)]
    public async Task V4Sender_PostFallbackSparseFrontierProofPreservesBackfillRepairRange()
    {
        const string transferId = "transfer_v4_sender_post_fallback_sparse_frontier_backfill";
        const string sessionId = "session_v4_sender_post_fallback_sparse_frontier_backfill";
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
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-post-fallback-sparse-frontier-backfill.bin", payload.Length, transferId),
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

        var batchCountBeforeProof = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 24,
                DurableReceivedHighestChunkIndex = 48,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 24, ChunkCount = 32 }],
                BytesCommitted = 24 * 21 * 1024,
                TransportEpoch = 1,
                RepairRequestId = "v6-state-frontier:1:post-fallback-backfill",
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforeProof)
                .Any(static batch =>
                    batch.StartChunkIndex == 24 &&
                    batch.ChunkCount > 1 &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v4_repair_scheduled;", log, StringComparison.Ordinal);
        Assert.Contains("repair_request_key=24:32:24:32", log, StringComparison.Ordinal);
        Assert.Contains("requested_chunk_count=32", log, StringComparison.Ordinal);
        Assert.DoesNotContain("repair_request_key=24:1:24:1", log, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V6FallbackSender_HandoffRecoveryUnblocksTailAndIgnoresStaleRecoveryFrames()
    {
        const string transferId = "transfer_v6_fallback_handoff_tail_unblock";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 1_024_000).Select(static index => (byte)(index % 239)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_handoff_tail_unblock");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_handoff_tail_unblock");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-handoff-tail-unblock.bin", payload.Length, transferId),
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
    public async Task V6FallbackSender_HandoffSparseBackfillKeepsTailBlockedAndSendsRepairWindow()
    {
        const string transferId = "transfer_v6_fallback_sparse_backfill_window";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 1_024_000).Select(static index => (byte)(index % 237)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_sparse_backfill_window");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_sparse_backfill_window");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-sparse-backfill-window.bin", payload.Length, transferId),
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
    public async Task V6FallbackSender_DuplicateTargetHandoffReusesEpochAndAcceptsPeerRepair()
    {
        const string transferId = "transfer_v6_fallback_duplicate_handoff_epoch";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 1_024_000).Select(static index => (byte)(index % 233)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_duplicate_handoff_epoch");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_duplicate_handoff_epoch");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-duplicate-handoff-epoch.bin", payload.Length, transferId),
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

    private static void ConfigureDiagnosticRegularNknV6RouteForTest(
        LoopbackFileTransferTransport senderTransport,
        LoopbackFileTransferTransport receiverTransport)
    {
        senderTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
        receiverTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
    }

    private static void ConfigureFileTunaV4RouteForTest(
        LoopbackFileTransferTransport senderTransport,
        LoopbackFileTransferTransport receiverTransport)
    {
        senderTransport.IsFileTunaActiveForRouteSelection = true;
        receiverTransport.IsFileTunaActiveForRouteSelection = true;
    }

    private static void ConfigurePostTunaFallbackV6RouteForTest(
        LoopbackFileTransferTransport senderTransport,
        LoopbackFileTransferTransport receiverTransport)
    {
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
    }

    private static async Task RunCompletedLoopbackTransferAsync(
        SessionFileTransferService sender,
        SessionFileTransferService receiver,
        string transferId,
        string fileName)
    {
        var payload = Enumerable.Range(0, 128_000).Select(static index => (byte)(index % 251)).ToArray();
        using var destination = new NonDisposingMemoryStream();
        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor(fileName, payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound is { } inbound &&
                  inbound.TransferId == transferId &&
                  inbound.State == FileTransferTransferState.PendingDecision,
            timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound is { } outbound &&
                  receiver.Snapshot.Inbound is { } inbound &&
                  outbound.TransferId == transferId &&
                  outbound.State == FileTransferTransferState.Completed &&
                  inbound.TransferId == transferId &&
                  inbound.State == FileTransferTransferState.Completed,
            timeoutMs: 20_000);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
    }

    private static string ReadV4SenderLogSnapshot(int logStart)
        => ReadOperationalLogTail(logStart) + Environment.NewLine + LocalOperationalLog.GetRecentLogText();

    private static string FilterV4SenderTransferLog(string logText, string transferId)
        => string.Join(
            Environment.NewLine,
            logText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("transfer_id=" + transferId, StringComparison.Ordinal)));

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

    private static async Task<TFrame> ReceiveDataFrameOfTypeAsync<TFrame>(IFileTransferDataSession session)
        where TFrame : FileTransferDataFrame
    {
        using var cts = new CancellationTokenSource(5000);
        while (true)
        {
            var received = await session.ReceiveWithMetadataAsync(cts.Token);
            if (received.Frame is TFrame frame)
            {
                return frame;
            }
        }
    }
}
