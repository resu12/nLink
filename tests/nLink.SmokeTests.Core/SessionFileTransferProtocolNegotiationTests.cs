using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferProtocolNegotiationTests : SessionFileTransferServiceTestBase
{
    [Fact]
    public async Task OutboundOffer_OnV4CapableTransport_AdvertisesV4()
    {
        const string transferId = "transfer_protocol_offer_v4";
        var payload = new byte[] { 1, 2, 3, 4 };
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_offer_v4");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_offer_v4");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("offer-v4.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        var offer = Assert.Single(senderTransport.SentOffers);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, offer.PreferredDataProtocolVersion);
    }

    [Fact]
    public async Task OutboundStart_OnNonV4Transport_FailsBeforeOffer()
    {
        const string transferId = "transfer_protocol_transport_incompatible";
        var payload = new byte[] { 1, 2, 3, 4 };
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_transport_incompatible")
        {
            SupportsFileTransferV4Streaming = false,
        };
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("no-v4.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Failed);
        Assert.Empty(senderTransport.SentOffers);
        Assert.Equal(FileTransferResultCodes.TransportIncompatible, sender.Snapshot.Outbound!.ErrorCode);
        Assert.Contains("event=filetransfer_v4_required_transport_incompatible", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InboundAccept_ForV4Offer_SendsV4Accept()
    {
        const string transferId = "transfer_protocol_accept_v4";
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_accept_v4");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_accept_v4");
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        await senderTransport.SendFileTransferOfferAsync(
            new FileTransferOfferV2
            {
                SessionId = "session_protocol_accept_v4",
                TransferId = transferId,
                FileName = "accept-v4.bin",
                FileSizeBytes = 4,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()),
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentAccepts.Count == 1);
        var accept = Assert.Single(receiverTransport.SentAccepts);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, accept.AcceptedDataProtocolVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task InboundOffer_WithLegacyOrMissingProtocol_IsDeclinedWithoutPendingTransfer(int? preferredVersion)
    {
        const string transferId = "transfer_protocol_legacy_offer";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_legacy_offer");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_legacy_offer");
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        await senderTransport.SendFileTransferOfferAsync(
            new FileTransferOfferV2
            {
                SessionId = "session_protocol_legacy_offer",
                TransferId = transferId,
                FileName = "legacy-offer.bin",
                FileSizeBytes = 4,
                PreferredDataProtocolVersion = preferredVersion,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentDeclines.Count == 1);
        var decline = Assert.Single(receiverTransport.SentDeclines);
        Assert.Equal(FileTransferResultCodes.TransportIncompatible, decline.Reason);
        Assert.Null(receiver.Snapshot.Inbound);
        Assert.Contains("event=filetransfer_legacy_negotiation_rejected", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task OutboundAccept_WithLegacyOrMissingProtocol_FailsTransfer(int? acceptedVersion)
    {
        const string transferId = "transfer_protocol_legacy_accept";
        var payload = new byte[] { 1, 2, 3, 4 };
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_legacy_accept");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_legacy_accept");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("legacy-accept.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.Count == 1);

        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = "session_protocol_legacy_accept",
                TransferId = transferId,
                AcceptedDataProtocolVersion = acceptedVersion,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Failed);
        Assert.Equal(FileTransferResultCodes.TransportIncompatible, sender.Snapshot.Outbound!.ErrorCode);
        Assert.Empty(senderTransport.SentDataFrames);
        Assert.Contains("event=filetransfer_legacy_negotiation_rejected", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutboundAccept_WithV4Protocol_StartsV4SenderRuntime()
    {
        const string transferId = "transfer_protocol_v4_accept_guard";
        var payload = new byte[] { 1, 2, 3, 4 };
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_v4_accept_guard");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_v4_accept_guard");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-accept.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.Count == 1);

        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = "session_protocol_v4_accept_guard",
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Count == 1);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV4>().Any());
        Assert.Equal(FileTransferTransferState.Sending, sender.Snapshot.Outbound!.State);
        Assert.Null(sender.Snapshot.Outbound.ErrorCode);
        var sessionOpen = Assert.Single(senderTransport.SentSessionOpens);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, sessionOpen.ProtocolVersion);
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferManifestFrameV4);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v4_negotiated", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_sender_started", logTail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task InboundSessionOpen_WithNonV4Protocol_DoesNotStartDataSession(int protocolVersion)
    {
        const string transferId = "transfer_protocol_non_v4_session_open";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_non_v4_session_open");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_non_v4_session_open");
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        await senderTransport.SendFileTransferOfferAsync(
            new FileTransferOfferV2
            {
                SessionId = "session_protocol_non_v4_session_open",
                TransferId = transferId,
                FileName = "non-v4-session-open.bin",
                FileSizeBytes = 4,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.AwaitingMetadata);
        var sessionId = receiver.Snapshot.Inbound!.SessionId;

        await senderTransport.SendFileTransferSessionOpenAsync(
            new FileTransferSessionOpenV2
            {
                SessionId = sessionId,
                TransferId = transferId,
                ProtocolVersion = protocolVersion,
                SessionRole = FileTransferProtocol.SessionRoleSender,
                ChunkSizeBytes = 4096,
                InitialPipelineDepth = 1,
            },
            CancellationToken.None);

        await Task.Delay(250);
        Assert.Equal(FileTransferTransferState.AwaitingMetadata, receiver.Snapshot.Inbound!.State);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain("event=filetransfer_session_opened", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_legacy_negotiation_rejected", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_session_open_rejected", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InboundSessionOpen_WithV4Protocol_StartsReceiverAndWaitsForManifest()
    {
        const string transferId = "transfer_protocol_v4_session_open_guard";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_protocol_v4_session_open_guard");
        using var receiverTransport = new LoopbackFileTransferTransport("session_protocol_v4_session_open_guard");
        senderTransport.Connect(receiverTransport);
        using var receiver = new SessionFileTransferService();
        receiver.AttachTransport(receiverTransport);

        await senderTransport.SendFileTransferOfferAsync(
            new FileTransferOfferV2
            {
                SessionId = "session_protocol_v4_session_open_guard",
                TransferId = transferId,
                FileName = "v4-session-open.bin",
                FileSizeBytes = 4,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.AwaitingMetadata);
        var sessionId = receiver.Snapshot.Inbound!.SessionId;

        await senderTransport.SendFileTransferSessionOpenAsync(
            new FileTransferSessionOpenV2
            {
                SessionId = sessionId,
                TransferId = transferId,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                SessionRole = FileTransferProtocol.SessionRoleSender,
                ChunkSizeBytes = 4096,
                InitialPipelineDepth = 1,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_receiver_started", StringComparison.Ordinal));
        Assert.Equal(FileTransferTransferState.AwaitingMetadata, receiver.Snapshot.Inbound!.State);
        Assert.Null(receiver.Snapshot.Inbound.ErrorCode);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_session_opened", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_negotiated", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v4_session_open_rejected", logTail, StringComparison.Ordinal);
    }
}
