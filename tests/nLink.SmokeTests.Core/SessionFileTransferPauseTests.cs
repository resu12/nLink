using System.Security.Cryptography;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferPauseTests : SessionFileTransferServiceTestBase
{
    [Fact]
    public async Task LoopbackDataSessionGeneration_IsPositiveStableAndSessionBound()
    {
        const string sessionId = "session_loopback_generation";
        const string transferId = "transfer_loopback_generation";
        using var transport = new LoopbackFileTransferTransport(sessionId);
        var session = await transport.OpenFileTransferDataSessionAsync(sessionId, transferId, CancellationToken.None);
        var generation = Assert.IsAssignableFrom<IFileTransferDataSessionGenerationProvider>(session).Generation;
        Assert.True(generation > 0);
        Assert.Equal(sessionId, session.SessionId);
        Assert.Equal(transferId, session.TransferId);

        var reopened = await transport.OpenFileTransferDataSessionAsync(sessionId, transferId, CancellationToken.None);
        Assert.Same(session, reopened);
        Assert.Equal(generation, Assert.IsAssignableFrom<IFileTransferDataSessionGenerationProvider>(reopened).Generation);
        var other = await transport.OpenFileTransferDataSessionAsync(sessionId, transferId + "_other", CancellationToken.None);
        Assert.NotSame(session, other);
        Assert.True(Assert.IsAssignableFrom<IFileTransferDataSessionGenerationProvider>(other).Generation > generation);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.OpenFileTransferDataSessionAsync(sessionId + "_other", transferId, CancellationToken.None));
    }

    [Fact]
    public void TransferSnapshot_DefaultPauseState_IsNotPaused()
    {
        var snapshot = new FileTransferTransferSnapshot(
            "session_pause_default",
            "transfer_pause_default",
            FileTransferDirection.Outbound,
            FileTransferTransferState.Sending,
            "pause-default.bin",
            FileSizeBytes: 128,
            Sha256Base64: null,
            BytesTransferred: 0,
            ChunksTransferred: 0,
            ChunkCount: 1,
            ChunkSizeBytes: 128,
            ErrorCode: null,
            StatusMessage: null);

        Assert.False(snapshot.IsPaused);
        Assert.False(snapshot.IsPauseBlockedByTransport);
        Assert.Null(snapshot.PauseReason);
        Assert.False(snapshot.IsPeerPaused);
        Assert.Null(snapshot.PeerPauseReason);
    }

    [Fact]
    public async Task OutboundAwaitingAcceptance_CanPauseAndResume()
    {
        const string transferId = "transfer_pause_outbound_waiting";
        using var senderTransport = new LoopbackFileTransferTransport("session_pause_outbound_waiting");
        using var receiverTransport = new LoopbackFileTransferTransport("session_pause_outbound_waiting");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pause-outbound.bin", 16, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[16], writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.AwaitingAcceptance);

        var paused = await sender.PauseTransferAsync(transferId, "ui_pause", CancellationToken.None);

        Assert.NotNull(paused);
        Assert.True(paused!.IsPaused);
        Assert.Equal("ui_pause", paused.PauseReason);
        Assert.Equal(FileTransferTransferState.AwaitingAcceptance, paused.State);
        Assert.True(sender.Snapshot.Outbound!.IsPaused);

        var resumed = await sender.ResumeTransferAsync(transferId, "ui_resume", CancellationToken.None);

        Assert.NotNull(resumed);
        Assert.False(resumed!.IsPaused);
        Assert.Equal("ui_resume", resumed.PauseReason);
        Assert.Equal("Waiting for receiver response.", resumed.StatusMessage);
        Assert.False(sender.Snapshot.Outbound!.IsPaused);
    }

    [Fact]
    public async Task InboundAwaitingMetadata_CanPauseAndResumeAfterAccept()
    {
        const string transferId = "transfer_pause_inbound_metadata";
        using var senderTransport = new LoopbackFileTransferTransport("session_pause_inbound_metadata");
        using var receiverTransport = new LoopbackFileTransferTransport("session_pause_inbound_metadata");
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        await senderTransport.SendFileTransferOfferAsync(
            new FileTransferOfferV2
            {
                SessionId = "session_pause_inbound_metadata",
                TransferId = transferId,
                FileName = "pause-inbound.bin",
                FileSizeBytes = 16,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(new MemoryStream()),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.AwaitingMetadata);

        var paused = await receiver.PauseTransferAsync(transferId, null, CancellationToken.None);

        Assert.NotNull(paused);
        Assert.True(paused!.IsPaused);
        Assert.Equal("user_requested", paused.PauseReason);
        Assert.Equal("Transfer paused.", paused.StatusMessage);

        var resumed = await receiver.ResumeTransferAsync(transferId, "resume_reason", CancellationToken.None);

        Assert.NotNull(resumed);
        Assert.False(resumed!.IsPaused);
        Assert.Equal("resume_reason", resumed.PauseReason);
        Assert.Equal("Waiting for sender to prepare the file.", resumed.StatusMessage);
    }

    [Fact]
    public async Task PausePendingDecision_IsIgnored()
    {
        const string transferId = "transfer_pause_pending_ignored";
        using var senderTransport = new LoopbackFileTransferTransport("session_pause_pending_ignored");
        using var receiverTransport = new LoopbackFileTransferTransport("session_pause_pending_ignored");
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        await senderTransport.SendFileTransferOfferAsync(
            new FileTransferOfferV2
            {
                SessionId = "session_pause_pending_ignored",
                TransferId = transferId,
                FileName = "pending.bin",
                FileSizeBytes = 16,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                FileTransferRoute = "regular_nkn_v4_fast",
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        var paused = await receiver.PauseTransferAsync(transferId, "pause", CancellationToken.None);

        Assert.Null(paused);
        Assert.False(receiver.Snapshot.Inbound!.IsPaused);
        Assert.Equal(FileTransferTransferState.PendingDecision, receiver.Snapshot.Inbound.State);
        Assert.Equal("regular_nkn_v4_fast", receiver.Snapshot.Inbound.RouteToken);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, receiver.Snapshot.Inbound.ProtocolVersion);
    }

    [Fact]
    public async Task ResumeWhenNotPaused_IsIgnored()
    {
        const string transferId = "transfer_resume_not_paused";
        using var senderTransport = new LoopbackFileTransferTransport("session_resume_not_paused");
        using var receiverTransport = new LoopbackFileTransferTransport("session_resume_not_paused");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("resume-not-paused.bin", 16, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[16], writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.AwaitingAcceptance);

        var resumed = await sender.ResumeTransferAsync(transferId, "resume", CancellationToken.None);

        Assert.Null(resumed);
        Assert.False(sender.Snapshot.Outbound!.IsPaused);
    }

    [Fact]
    public async Task CancelWhilePaused_TransitionsTerminal()
    {
        const string transferId = "transfer_pause_cancel";
        using var senderTransport = new LoopbackFileTransferTransport("session_pause_cancel");
        using var receiverTransport = new LoopbackFileTransferTransport("session_pause_cancel");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pause-cancel.bin", 16, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[16], writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.AwaitingAcceptance);
        Assert.NotNull(await sender.PauseTransferAsync(transferId, "pause", CancellationToken.None));

        var canceled = await sender.CancelTransferAsync(transferId, "cancel_after_pause", CancellationToken.None);

        Assert.NotNull(canceled);
        Assert.Equal(FileTransferTransferState.Canceled, canceled!.State);
        Assert.True(canceled.IsTerminal);
        Assert.Equal(FileTransferResultCodes.CanceledLocal, canceled.ErrorCode);
        Assert.False(canceled.IsPaused);
        Assert.Null(await sender.ResumeTransferAsync(transferId, "resume_after_cancel", CancellationToken.None));
    }

    [Fact]
    public async Task TransportPause_DoesNotSetUserPauseSnapshot()
    {
        const string transferId = "transfer_transport_pause_distinct";
        using var senderTransport = new LoopbackFileTransferTransport("session_transport_pause_distinct");
        using var receiverTransport = new LoopbackFileTransferTransport("session_transport_pause_distinct");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("transport-pause.bin", 16, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[16], writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.AwaitingAcceptance);

        SetOutboundTransportPausedForTests(sender, true);

        Assert.False(sender.Snapshot.Outbound!.IsPaused);
        Assert.False(sender.Snapshot.Outbound.IsPauseBlockedByTransport);
    }

    [Fact]
    public async Task OutboundPausedBeforeReceiverCredit_DoesNotSendChunksUntilResumed()
    {
        const string transferId = "transfer_pause_outbound_before_credit";
        var payload = Enumerable.Range(0, 128_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_pause_outbound_before_credit");
        using var receiverTransport = new LoopbackFileTransferTransport("session_pause_outbound_before_credit");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pause-before-credit.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.AwaitingAcceptance);
        Assert.NotNull(await sender.PauseTransferAsync(transferId, "pause_before_credit", CancellationToken.None));

        await WaitUntilAsync(() => receiver.Snapshot.Inbound is { State: FileTransferTransferState.PendingDecision, TransferId: transferId });
        Assert.NotNull(await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None));
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound is { IsPeerPaused: true },
            timeoutMs: 5000);
        await Task.Delay(300);

        Assert.Empty(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>());
        Assert.True(sender.Snapshot.Outbound!.IsPaused);

        Assert.NotNull(await sender.ResumeTransferAsync(transferId, "resume", CancellationToken.None));
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 15000);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
    }

    [Fact]
    public async Task OutboundPauseDuringActiveTransfer_StopsNewSchedulingAfterInFlightDrain()
    {
        const string transferId = "transfer_pause_outbound_active";
        var payload = Enumerable.Range(0, 4_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_pause_outbound_active");
        using var receiverTransport = new LoopbackFileTransferTransport("session_pause_outbound_active");
        senderTransport.DataSessionSendDelayMs = 50;
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pause-active.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(),
            timeoutMs: 5000);
        AssertRegularV4Transfer(senderTransport, sender, transferId);

        Assert.NotNull(await sender.PauseTransferAsync(transferId, "pause_active", CancellationToken.None));
        await Task.Delay(900);
        var drainedCount = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Count();
        await Task.Delay(350);
        Assert.Equal(drainedCount, senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Count());
        Assert.True(sender.Snapshot.Outbound!.IsPaused);

        Assert.NotNull(await sender.ResumeTransferAsync(transferId, "resume", CancellationToken.None));
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 30000);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
    }

    [Fact]
    public async Task OutboundPause_RetriesControlAfterTransientTimeout()
    {
        const string transferId = "transfer_pause_retry_control";
        var payload = Enumerable.Range(0, 4_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_pause_retry_control");
        using var receiverTransport = new LoopbackFileTransferTransport("session_pause_retry_control");
        senderTransport.DataSessionSendDelayMs = 50;
        var pauseAttempts = 0;
        senderTransport.OutboundPauseControlDeliveryOverrideAsync = (_, _, token) =>
        {
            if (Interlocked.Increment(ref pauseAttempts) == 1)
            {
                throw new OperationCanceledException(token);
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
            new FileTransferSendDescriptor("pause-retry-control.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(),
            timeoutMs: 5000);
        AssertRegularV4Transfer(senderTransport, sender, transferId);

        Assert.NotNull(await sender.PauseTransferAsync(transferId, "pause_retry", CancellationToken.None));

        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound is { IsPeerPaused: true },
            timeoutMs: 7000);
        await WaitUntilAsync(() => Volatile.Read(ref pauseAttempts) >= 2, timeoutMs: 5000);
        Assert.True(pauseAttempts >= 2);
        Assert.True(senderTransport.SentPauseControls.Count >= 2);
    }

    [Fact]
    public async Task OutboundPause_RetriesAfterSuccessfulButUndeliveredControlSend()
    {
        const string transferId = "transfer_pause_retry_lost_control";
        var payload = Enumerable.Range(0, 4_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_pause_retry_lost_control");
        using var receiverTransport = new LoopbackFileTransferTransport("session_pause_retry_lost_control");
        senderTransport.DataSessionSendDelayMs = 50;
        var pauseAttempts = 0;
        senderTransport.OutboundPauseControlDeliveryOverrideAsync = (_, _, _) =>
        {
            var attempt = Interlocked.Increment(ref pauseAttempts);
            return Task.FromResult(attempt == 1);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pause-retry-lost-control.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(),
            timeoutMs: 5000);
        AssertRegularV4Transfer(senderTransport, sender, transferId);

        Assert.NotNull(await sender.PauseTransferAsync(transferId, "pause_retry_lost", CancellationToken.None));

        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound is { IsPeerPaused: true },
            timeoutMs: 7000);
        await WaitUntilAsync(() => Volatile.Read(ref pauseAttempts) >= 2, timeoutMs: 5000);
        Assert.True(pauseAttempts >= 2);
        Assert.True(senderTransport.SentPauseControls.Count >= 2);
    }

    [Fact]
    public async Task InboundPausedBeforeManifest_ClampsCreditUntilResumeAndCompletes()
    {
        const string transferId = "transfer_pause_inbound_before_manifest";
        const string sessionId = "session_pause_inbound_before_manifest";
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
            receiverTransport,
            receiver,
            transferId,
            sessionId,
            "pause-before-manifest.bin",
            payload.Length,
            (_, _) => Task.FromResult<Stream>(destination),
            chunkSizeBytes: 4);

        Assert.NotNull(await receiver.PauseTransferAsync(transferId, "pause_before_manifest", CancellationToken.None));
        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "pause-before-manifest.bin", payload.Length, 4, sha256), CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame =>
                frame.CreditUntilChunkIndexExclusive == 0 &&
                frame.ContiguousCommittedChunkIndex == 0),
            timeoutMs: 5000);
        Assert.True(receiver.Snapshot.Inbound!.IsPaused);
        Assert.Equal(FileTransferTransferState.Receiving, receiver.Snapshot.Inbound.State);

        Assert.NotNull(await receiver.ResumeTransferAsync(transferId, "resume", CancellationToken.None));
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0),
            timeoutMs: 5000);

        await senderSession.SendAsync(CreateChunkBatch(sessionId, transferId, payload, startChunkIndex: 0, chunkSizeBytes: 4, chunkCount: 3), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 5000);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
    }

    [Fact]
    public async Task InboundPauseDuringReceiving_SuppressesCreditAndMissingRangesUntilResume()
    {
        const string transferId = "transfer_pause_inbound_receiving";
        const string sessionId = "session_pause_inbound_receiving";
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
            receiverTransport,
            receiver,
            transferId,
            sessionId,
            "pause-receiving.bin",
            payload.Length,
            (_, _) => Task.FromResult<Stream>(destination),
            chunkSizeBytes: 4);
        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "pause-receiving.bin", payload.Length, 4, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

        await senderSession.SendAsync(CreateChunkBatch(sessionId, transferId, payload, startChunkIndex: 0, chunkSizeBytes: 4, chunkCount: 1), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.BytesTransferred == 4, timeoutMs: 5000);
        var stateCountBeforePause = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count();

        Assert.NotNull(await receiver.PauseTransferAsync(transferId, "pause_receiving", CancellationToken.None));
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Skip(stateCountBeforePause).Any(static frame =>
                frame.SessionId == "session_pause_inbound_receiving" &&
                frame.TransferId == "transfer_pause_inbound_receiving" &&
                frame.TransferPaused &&
                frame.ContiguousCommittedChunkIndex == 1 &&
                frame.CreditUntilChunkIndexExclusive == 1 &&
                frame.MissingRanges.Count == 0),
            timeoutMs: 5000);

        AssertDuplicateV6PauseFeedbackCoalesces(receiver);

        var stateCountAfterPause = receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Count();
        await senderSession.SendAsync(CreateChunkBatch(sessionId, transferId, payload, startChunkIndex: 2, chunkSizeBytes: 4, chunkCount: 1), CancellationToken.None);
        await Task.Delay(300);
        Assert.DoesNotContain(
            receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Skip(stateCountAfterPause),
            static frame => frame.MissingRanges.Count > 0 || frame.CreditUntilChunkIndexExclusive > 1);

        Assert.NotNull(await receiver.ResumeTransferAsync(transferId, "resume", CancellationToken.None));
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Skip(stateCountAfterPause).Any(static frame =>
                frame.SessionId == "session_pause_inbound_receiving" &&
                frame.TransferId == "transfer_pause_inbound_receiving" &&
                !frame.TransferPaused && frame.CreditUntilChunkIndexExclusive > 1),
            timeoutMs: 5000);
        AssertDuplicateV6PauseFeedbackCoalesces(receiver);
        await senderSession.SendAsync(CreateChunkBatch(sessionId, transferId, payload, startChunkIndex: 1, chunkSizeBytes: 4, chunkCount: 2), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 5000);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
    }

    [Fact]
    public async Task InboundUserPause_IgnoresChunkDataUntilResume()
    {
        const string transferId = "transfer_pause_inbound_freezes_progress";
        const string sessionId = "session_pause_inbound_freezes_progress";
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
            receiverTransport,
            receiver,
            transferId,
            sessionId,
            "pause-freezes-progress.bin",
            payload.Length,
            (_, _) => Task.FromResult<Stream>(destination),
            chunkSizeBytes: 4);
        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "pause-freezes-progress.bin", payload.Length, 4, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

        await senderSession.SendAsync(CreateChunkBatch(sessionId, transferId, payload, startChunkIndex: 0, chunkSizeBytes: 4, chunkCount: 1), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.BytesTransferred == 4, timeoutMs: 5000);

        Assert.NotNull(await receiver.PauseTransferAsync(transferId, "freeze", CancellationToken.None));
        await senderSession.SendAsync(CreateChunkBatch(sessionId, transferId, payload, startChunkIndex: 1, chunkSizeBytes: 4, chunkCount: 2), CancellationToken.None);
        await Task.Delay(300);

        Assert.Equal(4, receiver.Snapshot.Inbound!.BytesTransferred);
        Assert.Equal(1, receiver.Snapshot.Inbound.ChunksTransferred);
        Assert.Equal(FileTransferTransferState.Receiving, receiver.Snapshot.Inbound.State);
        Assert.Equal(payload[..4], destination.ToArray()[..4]);
        Assert.True(destination.Length <= 4);

        Assert.NotNull(await receiver.ResumeTransferAsync(transferId, "resume", CancellationToken.None));
        await senderSession.SendAsync(CreateChunkBatch(sessionId, transferId, payload, startChunkIndex: 1, chunkSizeBytes: 4, chunkCount: 2), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 5000);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
    }

    [Fact]
    public async Task InboundPeerPause_IgnoresChunkDataUntilPeerResumes()
    {
        const string transferId = "transfer_pause_peer_freezes_progress";
        const string sessionId = "session_pause_peer_freezes_progress";
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
            receiverTransport,
            receiver,
            transferId,
            sessionId,
            "peer-pause-freezes-progress.bin",
            payload.Length,
            (_, _) => Task.FromResult<Stream>(destination),
            chunkSizeBytes: 4);
        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "peer-pause-freezes-progress.bin", payload.Length, 4, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

        await senderSession.SendAsync(CreateChunkBatch(sessionId, transferId, payload, startChunkIndex: 0, chunkSizeBytes: 4, chunkCount: 1), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.BytesTransferred == 4, timeoutMs: 5000);

        await senderSession.SendAsync(CreatePeerPauseState(sessionId, transferId, epoch: 1, transferPaused: true, pauseReason: "sender_pause"), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound is { IsPeerPaused: true }, timeoutMs: 5000);
        await senderSession.SendAsync(CreateChunkBatch(sessionId, transferId, payload, startChunkIndex: 1, chunkSizeBytes: 4, chunkCount: 2), CancellationToken.None);
        await Task.Delay(300);

        Assert.Equal(4, receiver.Snapshot.Inbound!.BytesTransferred);
        Assert.Equal(1, receiver.Snapshot.Inbound.ChunksTransferred);
        Assert.Equal(FileTransferTransferState.Receiving, receiver.Snapshot.Inbound.State);
        Assert.Equal(payload[..4], destination.ToArray()[..4]);
        Assert.True(destination.Length <= 4);

        await senderSession.SendAsync(CreatePeerPauseState(sessionId, transferId, epoch: 2, transferPaused: false, pauseReason: "sender_resume"), CancellationToken.None);
        await senderSession.SendAsync(CreateChunkBatch(sessionId, transferId, payload, startChunkIndex: 1, chunkSizeBytes: 4, chunkCount: 2), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 5000);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
    }

    [Fact]
    public async Task LocalCancelWhileInboundPaused_NotifiesPeer()
    {
        const string transferId = "transfer_pause_inbound_cancel";
        const string sessionId = "session_pause_inbound_cancel";
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        await StartManualInboundV6ReceiverAsync(
            senderTransport,
            receiverTransport,
            receiver,
            transferId,
            sessionId,
            "pause-cancel-inbound.bin",
            16,
            (_, _) => Task.FromResult<Stream>(new MemoryStream()),
            chunkSizeBytes: 4);
        Assert.NotNull(await receiver.PauseTransferAsync(transferId, "pause", CancellationToken.None));

        var canceled = await receiver.CancelTransferAsync(transferId, "cancel_paused_inbound", CancellationToken.None);

        Assert.NotNull(canceled);
        Assert.Equal(FileTransferTransferState.Canceled, canceled!.State);
        Assert.Contains(receiverTransport.SentCancels, cancel => cancel.TransferId == transferId);
        Assert.False(canceled.IsPaused);
        Assert.Null(await receiver.ResumeTransferAsync(transferId, "resume_after_cancel", CancellationToken.None));
    }

    [Fact]
    public async Task UserPauseSurvivesTransportRecovery_UntilUserResumes()
    {
        const string transferId = "transfer_pause_transport_recovery";
        var payload = Enumerable.Range(0, 128_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_pause_transport_recovery");
        using var receiverTransport = new LoopbackFileTransferTransport("session_pause_transport_recovery");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pause-recovery.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.AwaitingAcceptance);
        Assert.NotNull(await sender.PauseTransferAsync(transferId, "pause", CancellationToken.None));
        await WaitUntilAsync(() => receiver.Snapshot.Inbound is { State: FileTransferTransferState.PendingDecision, TransferId: transferId });
        Assert.NotNull(await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None));
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound is { IsPeerPaused: true },
            timeoutMs: 5000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");
        await Task.Delay(300);

        Assert.True(sender.Snapshot.Outbound!.IsPaused);
        Assert.Empty(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>());

        Assert.NotNull(await sender.ResumeTransferAsync(transferId, "resume", CancellationToken.None));
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 15000);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
    }

    [Fact]
    public async Task OutboundUserPause_NotifiesInboundPeerPause()
    {
        const string transferId = "transfer_pause_outbound_peer_visible";
        var payload = Enumerable.Range(0, 128_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_pause_outbound_peer_visible");
        using var receiverTransport = new LoopbackFileTransferTransport("session_pause_outbound_peer_visible");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pause-outbound-peer.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.AwaitingAcceptance);
        Assert.NotNull(await sender.PauseTransferAsync(transferId, "sender_pause", CancellationToken.None));

        await WaitUntilAsync(() => receiver.Snapshot.Inbound is { State: FileTransferTransferState.PendingDecision, TransferId: transferId });
        Assert.NotNull(await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None));
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound is { IsPeerPaused: true, IsPaused: false },
            timeoutMs: 5000);

        Assert.True(sender.Snapshot.Outbound!.IsPaused);
        Assert.False(receiver.Snapshot.Inbound!.IsPaused);
        Assert.True(receiver.Snapshot.Inbound.IsPeerPaused);
        Assert.Equal("sender_pause", receiver.Snapshot.Inbound.PeerPauseReason);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(frame =>
                frame.GetType() == typeof(FileTransferStateFrameV4) &&
                frame.SessionId == receiverTransport.CurrentFileTransferSessionId &&
                frame.TransferId == transferId &&
                frame.CreditUntilChunkIndexExclusive == 0),
            timeoutMs: 5000);

        Assert.NotNull(await sender.ResumeTransferAsync(transferId, "sender_resume", CancellationToken.None));
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound is { IsPeerPaused: false },
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 15000);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
    }

    [Theory]
    [InlineData(FileTransferProtocol.ProtocolVersionV4)]
    [InlineData(FileTransferProtocol.ProtocolVersionV6)]
    public async Task InboundPeerResumeControl_PreservesNegotiatedProtocolAndCorrelation(int protocolVersion)
    {
        const string sessionId = "session_pause_control_protocol";
        const string transferId = "transfer_pause_control_protocol";
        var routeToken = protocolVersion == FileTransferProtocol.ProtocolVersionV4
            ? "regular_nkn_v4_fast"
            : "post_tuna_fallback_v6";
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = protocolVersion == FileTransferProtocol.ProtocolVersionV6;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = protocolVersion == FileTransferProtocol.ProtocolVersionV6;
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();
        var payload = new byte[] { 1, 2, 3, 4 };

        await senderTransport.SendFileTransferOfferAsync(new FileTransferOfferV2
        {
            SessionId = sessionId, TransferId = transferId, FileName = "pause-control.bin",
            FileSizeBytes = payload.Length, PreferredDataProtocolVersion = protocolVersion, FileTransferRoute = routeToken,
        }, CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await senderTransport.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2
        {
            SessionId = sessionId, TransferId = transferId, ProtocolVersion = protocolVersion, FileTransferRoute = routeToken,
            SessionRole = FileTransferProtocol.SessionRoleSender, ChunkSizeBytes = payload.Length, InitialPipelineDepth = 1,
        }, CancellationToken.None);
        var senderSession = await senderTransport.OpenFileTransferDataSessionAsync(sessionId, transferId, CancellationToken.None);

        var handler = typeof(SessionFileTransferService).GetMethod(
            "HandleIncomingPauseControlAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(handler);
        Task HandleControlAsync(FileTransferPauseControlV6 control)
            => Assert.IsAssignableFrom<Task>(handler!.Invoke(receiver, [control]));

        var paused = new FileTransferPauseControlV6
        {
            SessionId = sessionId, TransferId = transferId, Epoch = 2, Paused = true, Reason = "peer_pause",
        };
        await HandleControlAsync(paused);
        FileTransferManifestFrameV4 manifest = protocolVersion == FileTransferProtocol.ProtocolVersionV4
            ? new FileTransferManifestFrameV4()
            : new FileTransferManifestFrameV6();
        await senderSession.SendAsync(manifest with
        {
            SessionId = sessionId, TransferId = transferId, FileName = "pause-control.bin",
            FileSizeBytes = payload.Length, ChunkSizeBytes = payload.Length, ChunkCount = 1,
            Sha256Base64 = Convert.ToBase64String(SHA256.HashData(payload)),
        }, CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(frame =>
            frame.SessionId == sessionId && frame.TransferId == transferId && frame.CreditUntilChunkIndexExclusive == 0), timeoutMs: 5000);
        Assert.True(receiver.Snapshot.Inbound!.IsPeerPaused);
        Assert.Equal(routeToken, receiver.Snapshot.Inbound.RouteToken);
        Assert.Equal(protocolVersion, receiver.Snapshot.Inbound.ProtocolVersion);

        var resumed = paused with { Epoch = 3, Paused = false, Reason = "peer_resume" };
        await HandleControlAsync(resumed with { SessionId = sessionId + "_wrong" });
        Assert.True(receiver.Snapshot.Inbound.IsPeerPaused);
        await HandleControlAsync(resumed with { TransferId = transferId + "_wrong" });
        Assert.True(receiver.Snapshot.Inbound.IsPeerPaused);
        await HandleControlAsync(resumed with { Epoch = 1 });
        Assert.True(receiver.Snapshot.Inbound.IsPeerPaused);
        Assert.DoesNotContain(receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>(), frame =>
            frame.SessionId == sessionId && frame.TransferId == transferId && frame.CreditUntilChunkIndexExclusive > 0);

        await HandleControlAsync(resumed);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(frame =>
            frame.SessionId == sessionId && frame.TransferId == transferId && frame.CreditUntilChunkIndexExclusive > 0), timeoutMs: 5000);
        var state = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().First(frame =>
            frame.SessionId == sessionId && frame.TransferId == transferId && frame.CreditUntilChunkIndexExclusive > 0);
        if (protocolVersion == FileTransferProtocol.ProtocolVersionV4)
        {
            Assert.IsType<FileTransferStateFrameV4>(state);
            Assert.DoesNotContain(receiverTransport.SentDataFrames, frame => frame is FileTransferReceiverStateFrameV6);
        }
        else
        {
            Assert.IsType<FileTransferReceiverStateFrameV6>(state);
        }

        var logCursor = GetOperationalLogLength();
        await HandleControlAsync(resumed);
        Assert.Contains(
            $"event=filetransfer_v6_pause_control_received; transfer_id={transferId}; session_id={sessionId}; direction=Inbound; epoch=3; previous_epoch=3; duplicate=1; applied=0; peer_paused=0",
            ReadOperationalLogTail(logCursor), StringComparison.Ordinal);
        Assert.False(receiver.Snapshot.Inbound.IsPeerPaused);
        Assert.Equal(sessionId, receiver.Snapshot.Inbound.SessionId);
        Assert.Equal(transferId, receiver.Snapshot.Inbound.TransferId);
        Assert.Equal(routeToken, receiver.Snapshot.Inbound.RouteToken);
        Assert.Equal(protocolVersion, receiver.Snapshot.Inbound.ProtocolVersion);
    }

    [Fact]
    public async Task InboundUserPause_NotifiesOutboundPeerPause()
    {
        const string transferId = "transfer_pause_inbound_peer_visible";
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_pause_inbound_peer_visible");
        using var receiverTransport = new LoopbackFileTransferTransport("session_pause_inbound_peer_visible");
        senderTransport.DataSessionSendDelayMs = 10;
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pause-inbound-peer.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0),
            timeoutMs: 5000);

        Assert.NotNull(await receiver.PauseTransferAsync(transferId, "receiver_pause", CancellationToken.None));
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound is { IsPeerPaused: true, IsPaused: false },
            timeoutMs: 5000);

        Assert.True(receiver.Snapshot.Inbound!.IsPaused);
        Assert.False(sender.Snapshot.Outbound!.IsPaused);
        Assert.True(sender.Snapshot.Outbound.IsPeerPaused);
        Assert.Equal("receiver_pause", sender.Snapshot.Outbound.PeerPauseReason);

        await Task.Delay(900);
        var drainedCount = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Count();
        await Task.Delay(400);
        Assert.Equal(drainedCount, senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Count());

        Assert.NotNull(await receiver.ResumeTransferAsync(transferId, "receiver_resume", CancellationToken.None));
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound is { IsPeerPaused: false },
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 15000);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
    }

    private static void SetOutboundTransportPausedForTests(SessionFileTransferService service, bool value)
    {
        var field = typeof(SessionFileTransferService).GetField("outboundTransfer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var context = field!.GetValue(service);
        Assert.NotNull(context);
        var property = context!.GetType().GetProperty("PullTransportPaused", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        Assert.NotNull(property);
        property!.SetValue(context, value);
    }

    private static void AssertRegularV4Transfer(
        LoopbackFileTransferTransport senderTransport,
        SessionFileTransferService sender,
        string transferId)
    {
        var snapshot = Assert.IsType<FileTransferTransferSnapshot>(sender.Snapshot.Outbound);
        Assert.Equal(transferId, snapshot.TransferId);
        Assert.Equal(senderTransport.CurrentFileTransferSessionId, snapshot.SessionId);
        Assert.Equal("regular_nkn_v4_fast", snapshot.RouteToken);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, snapshot.ProtocolVersion);
        Assert.All(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>(), frame =>
        {
            Assert.IsType<FileTransferChunkBatchFrameV4>(frame);
            Assert.Equal(snapshot.SessionId, frame.SessionId);
            Assert.Equal(transferId, frame.TransferId);
        });
    }

    private static void AssertDuplicateV6PauseFeedbackCoalesces(SessionFileTransferService receiver)
    {
        const System.Reflection.BindingFlags instanceFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        const System.Reflection.BindingFlags staticFlags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var context = typeof(SessionFileTransferService).GetField("inboundTransfer", instanceFlags)!.GetValue(receiver)!;
        var gate = typeof(SessionFileTransferService).GetField("gate", instanceFlags)!.GetValue(receiver)!;
        lock (gate)
        {
            var lastSent = Assert.IsType<DateTimeOffset>(context.GetType().GetProperty("V6LastReceiverStateSentUtc")!.GetValue(context));
            var frontier = Assert.IsType<int>(context.GetType().GetProperty("V6LastReceiverStateCommittedChunkIndex")!.GetValue(context));
            var interval = Assert.IsType<int>(typeof(SessionFileTransferService).GetField("V6ReceiverStateRetryIntervalMs", staticFlags)!.GetRawConstantValue());
            var method = typeof(SessionFileTransferService).GetMethod("ShouldCoalesceForcedInboundV6PostTunaFallbackControlLocked", staticFlags)!;
            // Query the existing policy inside its original interval; do not alter any clock or limit.
            Assert.True(Assert.IsType<bool>(method.Invoke(null,
                [context, lastSent.AddMilliseconds(1), lastSent, frontier, TimeSpan.FromMilliseconds(interval), "receiver_state", "duplicate_pause_state_regression"])));
        }
    }

    private static async Task<IFileTransferDataSession> StartManualInboundV6ReceiverAsync(
        LoopbackFileTransferTransport senderTransport,
        LoopbackFileTransferTransport receiverTransport,
        SessionFileTransferService receiver,
        string transferId,
        string sessionId,
        string fileName,
        long fileSizeBytes,
        FileTransferWriteStreamFactory openWriteStreamAsync,
        int chunkSizeBytes)
    {
        const string routeToken = "post_tuna_fallback_v6";
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        foreach (var transport in new[] { senderTransport, receiverTransport })
        {
            Assert.Equal(sessionId, transport.CurrentFileTransferSessionId);
            var route = FileTransferRouteResolver.Resolve(FileTransferRouteResolverInput.FromTransport(transport));
            Assert.Equal(routeToken, route.TelemetryToken);
            Assert.Equal(FileTransferProtocol.ProtocolVersionV6, route.ProtocolVersion);
        }

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

        var logStart = GetOperationalLogLength();
        await senderTransport.SendFileTransferSessionOpenAsync(
            new FileTransferSessionOpenV2
            {
                SessionId = sessionId,
                TransferId = transferId,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                FileTransferRoute = routeToken,
                SessionRole = FileTransferProtocol.SessionRoleSender,
                ChunkSizeBytes = chunkSizeBytes,
                InitialPipelineDepth = 1,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains(
            $"event=filetransfer_v6_receiver_started; transfer_id={transferId}; session_id={sessionId};", StringComparison.Ordinal), timeoutMs: 5000);
        var snapshot = Assert.IsType<FileTransferTransferSnapshot>(receiver.Snapshot.Inbound);
        Assert.Equal(sessionId, snapshot.SessionId);
        Assert.Equal(transferId, snapshot.TransferId);
        Assert.Equal(routeToken, snapshot.RouteToken);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, snapshot.ProtocolVersion);
        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(sessionId, transferId, CancellationToken.None);
        Assert.Equal(sessionId, receiverSession.SessionId);
        Assert.Equal(transferId, receiverSession.TransferId);
        Assert.True(Assert.IsAssignableFrom<IFileTransferDataSessionGenerationProvider>(receiverSession).Generation > 0);
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

    private static FileTransferChunkBatchFrameV6 CreateChunkBatch(
        string sessionId,
        string transferId,
        byte[] payload,
        int startChunkIndex,
        int chunkSizeBytes,
        int chunkCount)
    {
        var segments = new List<byte[]>(chunkCount);
        for (var offset = 0; offset < chunkCount; offset++)
        {
            var chunkIndex = startChunkIndex + offset;
            var start = chunkIndex * chunkSizeBytes;
            var length = Math.Min(chunkSizeBytes, payload.Length - start);
            segments.Add(payload.Skip(start).Take(length).ToArray());
        }

        return new FileTransferChunkBatchFrameV6
        {
            SessionId = sessionId,
            TransferId = transferId,
            StartChunkIndex = startChunkIndex,
            ChunkCount = chunkCount,
            DataSegments = segments,
        };
    }

    private static FileTransferReceiverStateFrameV6 CreatePeerPauseState(
        string sessionId,
        string transferId,
        int epoch,
        bool transferPaused,
        string pauseReason)
        => new()
        {
            SessionId = sessionId,
            TransferId = transferId,
            Epoch = epoch,
            ContiguousCommittedChunkIndex = 0,
            DurableReceivedHighestChunkIndex = -1,
            CreditUntilChunkIndexExclusive = 0,
            MissingRanges = [],
            BytesCommitted = 0,
            TerminalReady = false,
            TransferPaused = transferPaused,
            TransferPauseReason = pauseReason,
        };
}
