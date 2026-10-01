using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "stale")]
    [InlineData(true, "stale")]
    [InlineData(false, "valid")]
    [InlineData(true, "valid")]
    [InlineData(false, "retry")]
    [InlineData(true, "retry")]
    public async Task InitialOffer_FinalRouteMatchesCoordinatorAndImmutableAgreement(bool correlated, string readiness)
    {
        var transferId = $"offer_route_{correlated}_{readiness}";
        var sessionId = "session_" + transferId;
        using var transport = new InitialOfferTransport(sessionId, correlated)
            { IsFileTunaActiveForRouteSelection = true };
        using var peer = new InitialOfferTransport(sessionId, correlated)
            { IsFileTunaActiveForRouteSelection = readiness == "valid" };
        if (readiness is "valid" or "stale")
            transport.SeedInitialTunaReadinessEvidenceForTests(transferId, FileTransferDirection.Outbound,
                leaseCurrent: readiness == "valid");
        if (readiness == "valid")
            peer.SeedInitialTunaReadinessEvidenceForTests(transferId, FileTransferDirection.Inbound);
        var attempts = 0;
        transport.OutboundOfferDeliveryOverrideAsync = (_, _, _) =>
            Task.FromResult(readiness == "retry" && Interlocked.Increment(ref attempts) == 1);
        transport.Connect(peer);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(transport);
        receiver.AttachTransport(peer);
        var payload = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        using var destination = new NonDisposingMemoryStream();
        await sender.TryStartSendAsync(new("offer.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        var expectedRoute = readiness == "valid" ? FileTransferRouteResolver.FileTunaV4Token
            : FileTransferRouteResolver.RegularNknV4FastToken;
        var offer = Assert.Single(transport.SentOffers);
        Assert.Equal(expectedRoute, offer.FileTransferRoute);
        Assert.Equal(4, offer.PreferredDataProtocolVersion);
        var context = typeof(SessionFileTransferService).GetField("outboundTransfer",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!;
        var state = CaptureJoinedNknStateForTest(sender, context);
        Assert.Equal(expectedRoute, state.CurrentRoutePublicationToken!.Value.RouteToken);
        Assert.Equal(expectedRoute, state.CurrentRuntimeOwner!.Value.RouteToken);
        if (correlated)
        {
            var agreement = Assert.IsType<FileTransferBootstrapAgreement>(state.PersistentState.FileTransferRouteTransition.BootstrapAgreement);
            Assert.Equal(offer, agreement.Offer);
            Assert.Equal(0, agreement.SourceBoundary.RuntimeGeneration);
        }
        else Assert.Null(state.PersistentState.FileTransferRouteTransition.BootstrapAgreement);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision, timeoutMs: 8000);
        Assert.All(transport.SentOffers, retry => Assert.Equal(offer, retry));
        if (readiness == "retry") Assert.True(transport.SentOffers.Count >= 2);
        await receiver.AcceptIncomingTransferAsync(transferId,
            (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Completed &&
            receiver.Snapshot.InboundState == FileTransferTransferState.Completed, timeoutMs: 8000);
        Assert.Equal(payload, destination.ToArray());
        Assert.Equal(expectedRoute, Assert.Single(peer.SentAccepts).FileTransferRoute);
        Assert.Equal(4, Assert.Single(peer.SentAccepts).AcceptedDataProtocolVersion);
        Assert.Equal(expectedRoute, Assert.Single(transport.SentSessionOpens).FileTransferRoute);
        Assert.Equal(4, Assert.Single(transport.SentSessionOpens).ProtocolVersion);
        Assert.Empty(transport.SentCancels);
        Assert.Empty(peer.SentCancels);
    }

    private sealed class InitialOfferTransport(string sessionId, bool correlated)
        : LoopbackFileTransferTransport(sessionId), IFileTransferRouteTransitionBridge, IFileTransferSignalingTransport
    {
        private long sessionGeneration;
        bool IFileTransferRouteTransitionBridge.SupportsFallbackBootstrapCorrelation => correlated;

        async Task<IFileTransferDataSession> IFileTransferSignalingTransport.OpenFileTransferDataSessionAsync(
            string session, string transfer, CancellationToken ct)
            => new InitialOfferDataSession(await OpenFileTransferDataSessionAsync(session, transfer, ct),
                Interlocked.Increment(ref sessionGeneration));
    }

    private sealed class InitialOfferDataSession(IFileTransferDataSession inner, long generation)
        : IFileTransferDataSession, IFileTransferPhysicalSendSubmissionSession, IFileTransferDataPathAuthoritySession,
            IFileTransferDataSessionGenerationProvider
    {
        public string SessionId => inner.SessionId;
        public string TransferId => inner.TransferId;
        public long Generation => generation;
        public bool IsAvailable => inner.IsAvailable;
        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged
        {
            add => inner.AvailabilityChanged += value;
            remove => inner.AvailabilityChanged -= value;
        }
        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct) => inner.ReceiveAsync(ct);
        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
            => inner.ReceiveWithMetadataAsync(ct);
        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct) => inner.SendAsync(frame, ct);
        public Task SendAsync(FileTransferDataFrame frame, FileTransferDataPathSendAuthority authority, CancellationToken ct)
            => ((IFileTransferDataPathAuthoritySession)inner).SendAsync(frame, authority, ct);
        public FileTransferPhysicalSendSubmission SubmitPhysicalSend(FileTransferDataFrame frame,
            FileTransferDataPathSendAuthority? authority, CancellationToken ct)
        {
            // Loopback has one physical delivery, with no redundant copies.
            var delivery = authority is null ? SendAsync(frame, ct) : SendAsync(frame, authority, ct);
            return new(delivery, delivery);
        }
        public void Dispose() => inner.Dispose();
    }
}
