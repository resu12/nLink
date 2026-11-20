using NLink.Core.FileTransfer;
using System.Security.Cryptography;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferServiceFinalizationAndErrorTests : SessionFileTransferServiceTestBase
{
    [Fact]
    public async Task SaveAs_StaleOfferTokenCannotAcceptSameIdReplacement()
    {
        const string sessionId = "session_save_as_replacement";
        using var peer = new LoopbackFileTransferTransport(sessionId);
        using var local = new LoopbackFileTransferTransport(sessionId);
        peer.Connect(local);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(local);
        var offer = new FileTransferOfferV2
        {
            SessionId = sessionId, TransferId = "transfer_reused", FileName = "same.txt", FileSizeBytes = 4,
            PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4, FileTransferRoute = "regular_nkn_v4_fast",
        };
        await peer.SendFileTransferOfferAsync(offer, CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        var original = receiver.Snapshot.Inbound!.InboundOfferToken;
        await peer.SendFileTransferCancelAsync(new FileTransferCancelV1 { SessionId = sessionId, TransferId = offer.TransferId }, CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.Canceled);
        await peer.SendFileTransferOfferAsync(offer, CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        var replacement = receiver.Snapshot.Inbound!.InboundOfferToken;
        Assert.NotEqual(original, replacement);
        static Task<FileTransferReceiveDestination> Open(FileTransferIncomingOffer _, CancellationToken ct)
            => throw new InvalidOperationException("No manifest has been sent.");
        Assert.Null(await receiver.AcceptIncomingTransferForOfferAsync((sessionId, original), offer.TransferId, Open, CancellationToken.None));
        Assert.Empty(local.SentAccepts);
        Assert.Equal(FileTransferTransferState.PendingDecision, receiver.Snapshot.InboundState);
        Assert.NotNull(await receiver.AcceptIncomingTransferForOfferAsync((sessionId, replacement), offer.TransferId, Open, CancellationToken.None));
        Assert.Single(local.SentAccepts);
    }

    [Fact]
    public async Task DestinationOpenFailure_KeepsLocalPathOutOfPeerError()
    {
        const string sessionId = "session_private_destination";
        const string transferId = "transfer_private_destination";
        const string detail = @"Cannot open Z:\Private Folder\renamed.txt";
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        Assert.NotNull(await sender.TryStartSendAsync(new FileTransferSendDescriptor("offered.txt", 1, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[1])), CancellationToken.None));
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        Assert.NotNull(await receiver.AcceptIncomingTransferAsync(transferId,
            (_, _) => Task.FromException<FileTransferReceiveDestination>(new InvalidOperationException(detail)), CancellationToken.None));
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.Failed &&
                                   sender.Snapshot.OutboundState == FileTransferTransferState.Failed);
        Assert.Equal(detail, receiver.Snapshot.Inbound!.StatusMessage);
        var error = Assert.Single(receiverTransport.SentErrors);
        Assert.Equal(transferId, error.TransferId);
        Assert.Equal(receiver.Snapshot.Inbound.ErrorCode, error.ErrorCode);
        Assert.Equal("The receiver could not open the file destination.", error.Message);
    }

    [Fact]
    public async Task TryStartSendAsync_SendsOfferBeforePreparingMetadata()
    {
        const string transferId = "transfer_service_prehash";
        var payload = Enumerable.Range(0, 8192).Select(static i => (byte)(i % 251)).ToArray();
        var expectedHash = Convert.ToBase64String(SHA256.HashData(payload));
        var openReadCount = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_service_prehash");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_prehash");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("prehash.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
            _ =>
            {
                Interlocked.Increment(ref openReadCount);
                return Task.FromResult<Stream>(new MemoryStream(payload, writable: false));
            },
            CancellationToken.None);

        Assert.NotNull(started);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);

        Assert.Equal(0, Volatile.Read(ref openReadCount));
        Assert.Equal(FileTransferTransferState.AwaitingAcceptance, sender.Snapshot.OutboundState);
        Assert.Null(sender.Snapshot.Outbound!.Sha256Base64);
        Assert.Null(receiver.Snapshot.Inbound!.Sha256Base64);

        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult(FileTransferReceiveDestination.FromStream(new MemoryStream())),
            CancellationToken.None);

        await WaitUntilAsync(() => sender.Snapshot.Outbound?.Sha256Base64 == expectedHash);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.Sha256Base64 == expectedHash);

        Assert.True(Volatile.Read(ref openReadCount) >= 1);
        Assert.NotEqual(FileTransferTransferState.AwaitingAcceptance, sender.Snapshot.OutboundState);
    }

    [Fact]
    public async Task AwaitingAcceptance_RetriesOfferWhenInitialOfferIsLost()
    {
        const string transferId = "transfer_service_offer_retry";
        var payload = Enumerable.Range(0, 4096).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_offer_retry");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_offer_retry");
        senderTransport.Connect(receiverTransport);

        var offerAttempts = 0;
        senderTransport.OutboundOfferDeliveryOverrideAsync = (_, _, _) =>
            Task.FromResult(Interlocked.Increment(ref offerAttempts) == 1);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("retry-offer.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision,
            timeoutMs: 6000);

        Assert.True(Volatile.Read(ref offerAttempts) >= 2);
        Assert.Equal(FileTransferTransferState.AwaitingAcceptance, sender.Snapshot.OutboundState);
    }

    [Fact]
    public async Task DuplicateOfferForPendingInbound_DoesNotDeclineAsBusy()
    {
        const string transferId = "transfer_service_duplicate_offer_pending";
        var payload = Enumerable.Range(0, 4096).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_duplicate_offer_pending");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_duplicate_offer_pending");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("duplicate-offer.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        await senderTransport.SendFileTransferOfferAsync(
            new FileTransferOfferV2
            {
                SessionId = string.Empty,
                TransferId = transferId,
                FileName = "duplicate-offer.bin",
                FileSizeBytes = payload.Length,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                FileTransferRoute = "regular_nkn_v4_fast",
            },
            CancellationToken.None);

        await Task.Delay(200);

        Assert.Empty(receiverTransport.SentDeclines);
        Assert.Equal(FileTransferTransferState.PendingDecision, receiver.Snapshot.InboundState);
        Assert.Equal(FileTransferTransferState.AwaitingAcceptance, sender.Snapshot.OutboundState);
    }

    [Fact]
    public async Task DuplicateOfferAfterAccept_ResendsAcceptAndUnsticksSender()
    {
        const string transferId = "transfer_service_duplicate_offer_resends_accept";
        var payload = Enumerable.Range(0, 8192).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_duplicate_offer_resends_accept");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_duplicate_offer_resends_accept");
        senderTransport.Connect(receiverTransport);

        var acceptAttempts = 0;
        receiverTransport.OutboundAcceptDeliveryOverrideAsync = (_, _, _) =>
            Task.FromResult(Interlocked.Increment(ref acceptAttempts) == 1);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("resend-accept.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult(FileTransferReceiveDestination.FromStream(new MemoryStream())),
            CancellationToken.None);

        await WaitUntilAsync(
            () => sender.Snapshot.OutboundState == FileTransferTransferState.Completed &&
                  receiver.Snapshot.InboundState == FileTransferTransferState.Completed,
            timeoutMs: 8000);

        Assert.True(Volatile.Read(ref acceptAttempts) >= 2);
    }

    [Fact]
    public async Task SuccessfulReceive_WritesPartFileUntilVerification_ThenMovesToFinalPath()
    {
        const string transferId = "transfer_service_temp_finalize";
        var payload = Enumerable.Range(0, 8192).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_temp_finalize");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_temp_finalize");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var finalPath = Path.Combine(tempRoot, "final.bin");
        var tempPath = finalPath + ".part";
        var allowFinalize = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("final.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);

            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(
                transferId,
                (_, _) => Task.FromResult(CreateTempReceiveDestination(finalPath, beforeMoveAsync: _ => allowFinalize.Task)),
                CancellationToken.None);

            await WaitUntilAsync(
                () => File.Exists(tempPath) &&
                      !File.Exists(finalPath) &&
                      receiver.Snapshot.InboundState != FileTransferTransferState.Completed,
                timeoutMs: 5000);

            allowFinalize.TrySetResult(true);

            await WaitUntilAsync(() =>
                sender.Snapshot.OutboundState == FileTransferTransferState.Completed &&
                receiver.Snapshot.InboundState == FileTransferTransferState.Completed);

            Assert.True(File.Exists(finalPath));
            Assert.False(File.Exists(tempPath));
            Assert.Equal(payload, File.ReadAllBytes(finalPath));
            Assert.Equal(finalPath, receiver.Snapshot.Inbound!.SavedFilePath);
            Assert.Equal(Path.GetDirectoryName(finalPath), receiver.Snapshot.Inbound.SavedDirectoryPath);
            Assert.Equal(Path.GetFileName(finalPath), receiver.Snapshot.Inbound.SavedFileName);
        }
        finally
        {
            allowFinalize.TrySetResult(true);
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Receiver_StaysVerifyingUntilFinalizeSucceeds()
    {
        const string transferId = "transfer_service_finalize_boundary";
        var payload = Enumerable.Range(0, 2048).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_finalize_boundary");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_finalize_boundary");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var finalPath = Path.Combine(tempRoot, "boundary.bin");
        var tempPath = finalPath + ".part";
        var allowFinalize = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("boundary.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);

            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(
                transferId,
                (_, _) => Task.FromResult(CreateTempReceiveDestination(finalPath, beforeMoveAsync: _ => allowFinalize.Task)),
                CancellationToken.None);

            await WaitUntilAsync(
                () => File.Exists(tempPath) &&
                      !File.Exists(finalPath) &&
                      receiver.Snapshot.InboundState != FileTransferTransferState.Completed &&
                      sender.Snapshot.OutboundState != FileTransferTransferState.Completed,
                timeoutMs: 5000);

            Assert.True(File.Exists(tempPath));
            Assert.False(File.Exists(finalPath));
            Assert.NotEqual(FileTransferTransferState.Completed, receiver.Snapshot.InboundState);
            Assert.NotEqual(FileTransferTransferState.Completed, sender.Snapshot.OutboundState);

            allowFinalize.TrySetResult(true);

            await WaitUntilAsync(() =>
                sender.Snapshot.OutboundState == FileTransferTransferState.Completed &&
                receiver.Snapshot.InboundState == FileTransferTransferState.Completed);
        }
        finally
        {
            allowFinalize.TrySetResult(true);
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}

