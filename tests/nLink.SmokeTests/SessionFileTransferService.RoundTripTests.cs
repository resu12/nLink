using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferServiceTests
{
    [Fact]
    public void Snapshot_DefaultsToIdleStates_WhenNoTransferIsActive()
    {
        using var service = new SessionFileTransferService();

        var snapshot = service.Snapshot;

        Assert.Null(snapshot.Outbound);
        Assert.Null(snapshot.Inbound);
        Assert.Equal(FileTransferTransferState.Idle, snapshot.OutboundState);
        Assert.Equal(FileTransferTransferState.Idle, snapshot.InboundState);
    }

    [Fact]
    public async Task RoundTrip_Completes_AndWritesExpectedBytes()
    {
        const string transferId = "transfer_service_roundtrip";
        var payload = Enumerable.Range(0, 40_000).Select(static i => (byte)(i % 251)).ToArray();
        var openReadCount = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_service_roundtrip");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_roundtrip");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("sample.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ =>
            {
                Interlocked.Increment(ref openReadCount);
                return Task.FromResult<Stream>(new MemoryStream(payload, writable: false));
            },
            CancellationToken.None);

        Assert.NotNull(started);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        using var destination = new NonDisposingMemoryStream();
        var accepted = await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        Assert.NotNull(accepted);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed);

        Assert.Equal(2, Volatile.Read(ref openReadCount));
        Assert.Equal(payload, destination.ToArray());
        Assert.Equal(payload.Length, sender.Snapshot.Outbound!.BytesTransferred);
        Assert.Equal(payload.Length, receiver.Snapshot.Inbound!.BytesTransferred);
    }

    [Fact]
    public async Task RoundTrip_Completes_WhenInboundChunkHandlersOverlap()
    {
        const string transferId = "transfer_service_chunk_overlap";
        var payload = Enumerable.Range(0, 345_143).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_chunk_overlap");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_chunk_overlap");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new DelayedWriteMemoryStream(delayMilliseconds: 15);

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("overlap.bin", payload.Length, transferId, ChunkSizeBytes: 16_384),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        Assert.NotNull(started);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        var accepted = await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        Assert.NotNull(accepted);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 5000);

        Assert.Equal(payload, destination.ToArray());
        Assert.Null(sender.Snapshot.Outbound!.ErrorCode);
        Assert.Null(receiver.Snapshot.Inbound!.ErrorCode);
    }
}
