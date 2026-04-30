using System.Security.Cryptography;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferPauseTests : SessionFileTransferServiceTestBase
{
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
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        var paused = await receiver.PauseTransferAsync(transferId, "pause", CancellationToken.None);

        Assert.Null(paused);
        Assert.False(receiver.Snapshot.Inbound!.IsPaused);
        Assert.Equal(FileTransferTransferState.PendingDecision, receiver.Snapshot.Inbound.State);
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

        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0),
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

        var senderSession = await StartManualInboundV4ReceiverAsync(
            senderTransport,
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
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame =>
                frame.CreditUntilChunkIndexExclusive == 0 &&
                frame.ContiguousCommittedChunkIndex == 0),
            timeoutMs: 5000);
        Assert.True(receiver.Snapshot.Inbound!.IsPaused);
        Assert.Equal(FileTransferTransferState.Receiving, receiver.Snapshot.Inbound.State);

        Assert.NotNull(await receiver.ResumeTransferAsync(transferId, "resume", CancellationToken.None));
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0),
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

        var senderSession = await StartManualInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "pause-receiving.bin",
            payload.Length,
            (_, _) => Task.FromResult<Stream>(destination),
            chunkSizeBytes: 4);
        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "pause-receiving.bin", payload.Length, 4, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

        await senderSession.SendAsync(CreateChunkBatch(sessionId, transferId, payload, startChunkIndex: 0, chunkSizeBytes: 4, chunkCount: 1), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.BytesTransferred == 4, timeoutMs: 5000);
        var stateCountBeforePause = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Count();

        Assert.NotNull(await receiver.PauseTransferAsync(transferId, "pause_receiving", CancellationToken.None));
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Skip(stateCountBeforePause).Any(static frame =>
                frame.ContiguousCommittedChunkIndex == 1 &&
                frame.CreditUntilChunkIndexExclusive == 1 &&
                frame.MissingRanges.Count == 0),
            timeoutMs: 5000);

        var stateCountAfterPause = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Count();
        await senderSession.SendAsync(CreateChunkBatch(sessionId, transferId, payload, startChunkIndex: 2, chunkSizeBytes: 4, chunkCount: 1), CancellationToken.None);
        await Task.Delay(300);
        Assert.DoesNotContain(
            receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Skip(stateCountAfterPause),
            static frame => frame.MissingRanges.Count > 0 || frame.CreditUntilChunkIndexExclusive > 1);

        Assert.NotNull(await receiver.ResumeTransferAsync(transferId, "resume", CancellationToken.None));
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 1),
            timeoutMs: 5000);
        await senderSession.SendAsync(CreateChunkBatch(sessionId, transferId, payload, startChunkIndex: 1, chunkSizeBytes: 4, chunkCount: 1), CancellationToken.None);
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

        var senderSession = await StartManualInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "pause-freezes-progress.bin",
            payload.Length,
            (_, _) => Task.FromResult<Stream>(destination),
            chunkSizeBytes: 4);
        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "pause-freezes-progress.bin", payload.Length, 4, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

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

        var senderSession = await StartManualInboundV4ReceiverAsync(
            senderTransport,
            receiver,
            transferId,
            sessionId,
            "peer-pause-freezes-progress.bin",
            payload.Length,
            (_, _) => Task.FromResult<Stream>(destination),
            chunkSizeBytes: 4);
        await senderSession.SendAsync(CreateManifest(sessionId, transferId, "peer-pause-freezes-progress.bin", payload.Length, 4, sha256), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0));

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

        await StartManualInboundV4ReceiverAsync(
            senderTransport,
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
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static frame => frame.CreditUntilChunkIndexExclusive > 0),
            timeoutMs: 5000);

        senderTransport.RaiseDisconnected();
        senderTransport.RaiseReconnected();
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

        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound is { IsPeerPaused: true, IsPaused: false },
            timeoutMs: 5000);

        Assert.True(sender.Snapshot.Outbound!.IsPaused);
        Assert.False(receiver.Snapshot.Inbound!.IsPaused);
        Assert.True(receiver.Snapshot.Inbound.IsPeerPaused);
        Assert.Equal("sender_pause", receiver.Snapshot.Inbound.PeerPauseReason);

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

    [Fact]
    public async Task InboundUserPause_NotifiesOutboundPeerPause()
    {
        const string transferId = "transfer_pause_inbound_peer_visible";
        var payload = Enumerable.Range(0, 8_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_pause_inbound_peer_visible");
        using var receiverTransport = new LoopbackFileTransferTransport("session_pause_inbound_peer_visible");
        senderTransport.DataSessionSendDelayMs = 50;
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

    private static async Task<IFileTransferDataSession> StartManualInboundV4ReceiverAsync(
        LoopbackFileTransferTransport senderTransport,
        SessionFileTransferService receiver,
        string transferId,
        string sessionId,
        string fileName,
        long fileSizeBytes,
        FileTransferWriteStreamFactory openWriteStreamAsync,
        int chunkSizeBytes)
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
                ChunkSizeBytes = chunkSizeBytes,
                InitialPipelineDepth = 1,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_receiver_started;", StringComparison.Ordinal), timeoutMs: 5000);
        return await senderTransport.OpenFileTransferDataSessionAsync(sessionId, transferId, CancellationToken.None);
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

    private static FileTransferChunkBatchFrameV4 CreateChunkBatch(
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

        return new FileTransferChunkBatchFrameV4
        {
            SessionId = sessionId,
            TransferId = transferId,
            StartChunkIndex = startChunkIndex,
            ChunkCount = chunkCount,
            DataSegments = segments,
        };
    }

    private static FileTransferStateFrameV4 CreatePeerPauseState(
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
