using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("FileTransferRuntimeSuite", "PublicationUseEvidence")]
    public async Task PublicationUse_RealV4SubmissionCapturesAuthoredReceiptWithoutChangingTransfer(bool authoritySession)
    {
        const string transferId = "publication_use_submission";
        const string sessionId = "session_publication_use_submission";
        var payload = Enumerable.Range(0, 256 * 1024).Select(i => (byte)(i % 251)).ToArray();
        using LoopbackFileTransferTransport senderTransport = authoritySession
            ? new LoopbackFileTransferTransport(sessionId) : new PublicationUseLegacyTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame is FileTransferChunkBatchFrameV4)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return false;
        };
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("publication.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), deadline.Token);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), deadline.Token);
        await entered.Task.WaitAsync(deadline.Token);
        try
        {
            var context = typeof(SessionFileTransferService).GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(sender)!;
            if (!authoritySession)
            {
                var legacy = Assert.IsType<PublicationUseLegacySession>(GetPrivatePropertyValue(context, "DataSession"));
                Assert.True(legacy.SendCount > 0);
                Assert.False((object)legacy is IFileTransferDataPathAuthoritySession);
            }
            else
            {
                var authorities = GetOutboundDataPathSendAuthorities(context);
                var captures = authorities.Select(FileTransferPublicationUseEvidence.GetSend).ToArray();
                Assert.All(captures, capture => Assert.NotNull(capture));
                Assert.Contains(captures, capture => capture!.Kind == FileTransferPublicationUseKind.V4PrePumpRequest);
                Assert.Contains(captures, capture => capture!.Kind == FileTransferPublicationUseKind.V4PhysicalSubmission);
                var emitter = GetPrivatePropertyValue(context, "CoordinatorSnapshotEmitter")!;
                var token = Assert.IsType<RoutePublicationToken>(GetPrivatePropertyValue(context, "CurrentRoutePublicationToken"));
                var current = Assert.IsType<FileTransferRoutePublicationEvidence>(emitter.GetType()
                    .GetMethod("GetRoutePublicationEvidence", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(emitter, [token]));
                Assert.All(captures, capture =>
                {
                    Assert.Equal(current with { ObservedStateGeneration = capture!.Publication.ObservedStateGeneration }, capture.Publication);
                    Assert.InRange(capture.Publication.ObservedStateGeneration, current.StateGeneration, current.ObservedStateGeneration);
                });
            }
        }
        finally { release.TrySetResult(); }
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 10000);
        Assert.Equal(payload, destination.ToArray());
    }

    private sealed class PublicationUseLegacyTransport(string sessionId) : LoopbackFileTransferTransport(sessionId), IFileTransferSignalingTransport
    {
        async Task<IFileTransferDataSession> IFileTransferSignalingTransport.OpenFileTransferDataSessionAsync(
            string session, string transfer, CancellationToken ct)
            => new PublicationUseLegacySession(await base.OpenFileTransferDataSessionAsync(session, transfer, ct));
    }

    private sealed class PublicationUseLegacySession(IFileTransferDataSession inner) : IFileTransferDataSession
    {
        public int SendCount { get; private set; }
        public string SessionId => inner.SessionId;
        public string TransferId => inner.TransferId;
        public bool IsAvailable => inner.IsAvailable;
        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged
        {
            add => inner.AvailabilityChanged += value;
            remove => inner.AvailabilityChanged -= value;
        }
        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct) => inner.ReceiveAsync(ct);
        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct) => inner.ReceiveWithMetadataAsync(ct);
        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
        {
            SendCount++;
            return inner.SendAsync(frame, ct);
        }
        public void Dispose() => inner.Dispose();
    }
}
