using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferSenderCacheTests : SessionFileTransferServiceTestBase
{
    [Fact]
    public async Task PullSession_V3NonSeekableSource_RepairsFromSenderCache_AndCompletes()
    {
        const string transferId = "transfer_service_v3_sender_cache_nonseek";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 2 * 1024 * 1024).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_v3_sender_cache_nonseek")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_v3_sender_cache_nonseek")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);

        var dropped = 0;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, _) =>
        {
            if (frame is FileTransferChunkBatchFrameV3 { StartChunkIndex: 0 } batch)
            {
                if (Interlocked.CompareExchange(ref dropped, 1, 0) != 0)
                {
                    return Task.FromResult(false);
                }

                for (var offset = 1; offset < batch.DataSegments.Count; offset++)
                {
                    target.ReceiveDeliveredDataFrame(new FileTransferChunkDataFrameV3
                    {
                        SessionId = batch.SessionId,
                        TransferId = batch.TransferId,
                        ChunkIndex = batch.StartChunkIndex + offset,
                        ChunkCount = batch.ChunkCount,
                        Data = batch.DataSegments[offset],
                    });
                }

                return Task.FromResult(true);
            }

            if (frame is FileTransferChunkDataFrameV3 { ChunkIndex: 0 })
            {
                return Task.FromResult(Interlocked.CompareExchange(ref dropped, 1, 0) == 0);
            }

            return Task.FromResult(false);
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v3-sender-cache-nonseek.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new NonSeekableChunkedReadStream(payload, 2048)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferRepairRequestFrameV3>().Any(), timeoutMs: 15000);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 25000);

        Assert.Equal(payload, destination.ToArray());
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_sender_repair_cache_policy", logTail, StringComparison.Ordinal);
        Assert.Contains("source_can_seek=0", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_sender_repair_unavailable", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_sender_cache_exhausted", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullSession_V3PartialReadSource_ReadsExactChunks_AndCompletes()
    {
        const string transferId = "transfer_service_v3_sender_cache_partial_read";
        var payload = Enumerable.Range(0, 768 * 1024).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_v3_sender_cache_partial_read")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_v3_sender_cache_partial_read")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v3-sender-cache-partial-read.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new NonSeekableChunkedReadStream(payload, 997)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 15000);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task PullSession_V3RepairSetForFutureChunk_IsSkippedBeforeSend()
    {
        const string transferId = "transfer_service_v3_sender_cache_future_skip";
        const string sessionId = "session_service_v3_sender_cache_future_skip";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 5 * 1024 * 1024).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);
        var injected = 0;
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, _) =>
        {
            if (frame is FileTransferGrantWindowFrameV3 && Interlocked.CompareExchange(ref injected, 1, 0) == 0)
            {
                target.ReceiveDeliveredDataFrame(new FileTransferRepairRequestSetFrameV3
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Ranges =
                    [
                        new FileTransferRepairRangeV3 { StartChunkIndex = 100, RequestedChunkCount = 2 },
                    ],
                });
            }

            return Task.FromResult(false);
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("v3-sender-cache-future-skip.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("reason=not_yet_sent", StringComparison.Ordinal), timeoutMs: 5000);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 25000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_sender_repair_chunk_skipped", logTail, StringComparison.Ordinal);
        Assert.Contains("reason=not_yet_sent", logTail, StringComparison.Ordinal);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task PullSession_V3NonSeekableSource_CacheOverflowFailsCleanly()
    {
        const string transferId = "transfer_service_v3_sender_cache_overflow";
        const string sessionId = "session_service_v3_sender_cache_overflow";
        const int chunkSizeBytes = 40 * 1024;
        const long fileSizeBytes = 70L * 1024 * 1024;
        var logStart = ReadOperationalLogText().Length;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(frame is FileTransferChunkDataFrameV3 or FileTransferChunkBatchFrameV3);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v3-sender-cache-overflow.bin", fileSizeBytes, transferId, chunkSizeBytes),
            _ => Task.FromResult<Stream>(new DeterministicNonSeekableReadStream(fileSizeBytes, maxReadSize: 4096)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>().Any(), timeoutMs: 5000);
        var manifest = senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>().Single();

        senderTransport.ReceiveDeliveredDataFrame(new FileTransferGrantWindowFrameV3
        {
            SessionId = sessionId,
            TransferId = transferId,
            NextExpectedChunkIndex = 0,
            GrantedUntilChunkIndexExclusive = manifest.ChunkCount,
        });

        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Failed, timeoutMs: 30000);

        Assert.Equal(FileTransferResultCodes.SenderCacheExhausted, sender.Snapshot.Outbound!.ErrorCode);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_sender_cache_exhausted", logTail, StringComparison.Ordinal);
        Assert.Contains("error_code=sender_cache_exhausted", logTail, StringComparison.Ordinal);
    }

    private sealed class NonSeekableChunkedReadStream : Stream
    {
        private readonly byte[] data;
        private readonly int maxReadSize;
        private int position;

        public NonSeekableChunkedReadStream(byte[] data, int maxReadSize)
        {
            this.data = data;
            this.maxReadSize = Math.Max(1, maxReadSize);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= data.Length)
            {
                return 0;
            }

            var read = Math.Min(Math.Min(count, maxReadSize), data.Length - position);
            Buffer.BlockCopy(data, position, buffer, offset, read);
            position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position >= data.Length)
            {
                return ValueTask.FromResult(0);
            }

            var read = Math.Min(Math.Min(buffer.Length, maxReadSize), data.Length - position);
            data.AsMemory(position, read).CopyTo(buffer);
            position += read;
            return ValueTask.FromResult(read);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DeterministicNonSeekableReadStream(long length, int maxReadSize) : Stream
    {
        private long position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= length)
            {
                return 0;
            }

            var read = (int)Math.Min(Math.Min(count, maxReadSize), length - position);
            for (var index = 0; index < read; index++)
            {
                buffer[offset + index] = (byte)((position + index) % 251);
            }

            position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position >= length)
            {
                return ValueTask.FromResult(0);
            }

            var read = (int)Math.Min(Math.Min(buffer.Length, maxReadSize), length - position);
            for (var index = 0; index < read; index++)
            {
                buffer.Span[index] = (byte)((position + index) % 251);
            }

            position += read;
            return ValueTask.FromResult(read);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
