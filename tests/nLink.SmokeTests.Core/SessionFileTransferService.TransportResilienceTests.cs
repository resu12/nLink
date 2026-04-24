using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferServiceTransportResilienceTests : SessionFileTransferServiceTestBase
{
    [Fact]
    public async Task TransportDisconnectDuringReceiving_ReconnectWithinGrace_ResumesAndCompletes()
    {
        const string transferId = "transfer_service_disconnect_resume";
        var payload = Enumerable.Range(0, 32768).Select(static i => (byte)(i % 251)).ToArray();
        var disconnectTriggered = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_service_disconnect_resume");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_disconnect_resume");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (target, frame, ct) =>
        {
            if (frame is not FileTransferChunkDataFrameV2 chunk ||
                chunk.TransferId != transferId ||
                chunk.ChunkIndex != 0 ||
                Interlocked.Exchange(ref disconnectTriggered, 1) != 0)
            {
                return false;
            }

            target.ReceiveDeliveredDataFrame(frame);
            receiverTransport.RaiseDisconnected();
            await Task.Delay(1000, ct).ConfigureAwait(false);
            receiverTransport.RaiseReconnected();
            return true;
        };
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("disconnect-resume.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.OutboundState == FileTransferTransferState.Completed &&
            receiver.Snapshot.InboundState == FileTransferTransferState.Completed,
            timeoutMs: 10000);

        Assert.Equal(payload, destination.ToArray());
    }
}

