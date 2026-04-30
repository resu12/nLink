using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using NLink.Core;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferReceiverBufferingTests : SessionFileTransferServiceTestBase
{
    [Fact]
    public async Task V3Receiver_DelayedFirstChunk_WritesContiguousTailInBoundedBatches()
    {
        const int chunkSizeBytes = 4096;
        const string transferId = "transfer_receiver_buffer_bounded_writes";
        var payload = Enumerable.Range(0, 3 * 1024 * 1024).Select(static i => (byte)(i % 251)).ToArray();
        var sessionId = "session_receiver_buffer_bounded_writes_" + Guid.NewGuid().ToString("N");
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);

        var delayedFrames = new ConcurrentQueue<(LoopbackFileTransferTransport Target, FileTransferDataFrameV2 Frame)>();
        var firstRangeHeld = 0;
        var firstRangeReleased = 0;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, _) =>
        {
            if (Volatile.Read(ref firstRangeReleased) == 0 && Interlocked.CompareExchange(ref firstRangeHeld, 1, 0) == 0 && FrameContainsChunk(frame, transferId, 0))
            {
                delayedFrames.Enqueue((target, frame));
                return Task.FromResult(true);
            }

            if (Volatile.Read(ref firstRangeHeld) != 0 &&
                Volatile.Read(ref firstRangeReleased) == 0 &&
                FrameHighestChunkIndex(frame, transferId) >= 320)
            {
                ReleaseDelayedFrames(delayedFrames);
                Volatile.Write(ref firstRangeReleased, 1);
            }

            return Task.FromResult(false);
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("bounded-writes.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new WriteOnlySeekableMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 25000);
        Assert.Equal(payload, destination.ToArray());

        var logText = ReadRetainedOperationalLogs();
        var writeBatchBytes = ExtractLongFieldValues(logText, transferId, "filetransfer_receiver_write_batch_committed", "batch_bytes").ToList();
        Assert.True(writeBatchBytes.Count > 1, "Expected the delayed contiguous tail to be split into multiple bounded receiver write batches.");
        Assert.All(writeBatchBytes, value => Assert.InRange(value, 1, 1024 * 1024));
    }

    [Fact]
    public async Task V3Receiver_SustainedGap_LogsGapStallThroughputEvidence()
    {
        const int chunkSizeBytes = 4096;
        const string transferId = "transfer_receiver_gap_stall_telemetry";
        var payload = Enumerable.Range(0, 2 * 1024 * 1024).Select(static i => (byte)(i % 251)).ToArray();
        var sessionId = "session_receiver_gap_stall_telemetry_" + Guid.NewGuid().ToString("N");
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);

        var firstFrameHeld = 0;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, ct) =>
        {
            if (FrameContainsChunk(frame, transferId, 0) &&
                Interlocked.CompareExchange(ref firstFrameHeld, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(3500, ct).ConfigureAwait(false);
                        target.ReceiveDeliveredDataFrame(frame);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }, CancellationToken.None);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var logStart = GetOperationalLogLength();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("gap-stall-telemetry.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new WriteOnlySeekableMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v3_gap_stall_summary", StringComparison.Ordinal), timeoutMs: 10000);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 25000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Equal(payload, destination.ToArray());
        Assert.Contains("event=filetransfer_v3_receiver_throughput_summary", logTail, StringComparison.Ordinal);
        Assert.Contains("oldest_gap_age_ms=", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v3_gap_stall_summary", logTail, StringComparison.Ordinal);
        Assert.Contains("gap_start_chunk_index=", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V3Receiver_BufferPressure_ClampsFutureGrantsAndExitsAfterDrain()
    {
        const int chunkSizeBytes = 48 * 1024;
        const string transferId = "transfer_receiver_buffer_pressure";
        var payload = Enumerable.Range(0, 20 * 1024 * 1024).Select(static i => (byte)(i % 251)).ToArray();
        var sessionId = "session_receiver_buffer_pressure_" + Guid.NewGuid().ToString("N");
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
                Task.FromResult(frame is FileTransferChunkDataFrameV3 or FileTransferChunkBatchFrameV3),
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("receiver-buffer-pressure.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new WriteOnlySeekableMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>().Any(), timeoutMs: 5000);

        var chunkCount = senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>().Single().ChunkCount;
        var syntheticChunk = Enumerable.Range(0, chunkSizeBytes).Select(static i => (byte)(i % 251)).ToArray();
        for (var chunkIndex = 1; chunkIndex <= 350 && chunkIndex < chunkCount; chunkIndex++)
        {
            receiverTransport.ReceiveDeliveredDataFrame(new FileTransferChunkDataFrameV3
            {
                SessionId = sessionId,
                TransferId = transferId,
                ChunkIndex = chunkIndex,
                ChunkCount = chunkCount,
                Data = syntheticChunk,
            });
        }

        var pressureLog = string.Empty;
        await WaitUntilAsync(() =>
        {
            var logText = ReadRetainedOperationalLogs();
            if (!logText.Contains("event=filetransfer_receiver_buffer_pressure_entered", StringComparison.Ordinal) ||
                !logText.Contains("event=filetransfer_receiver_grant_clamped_for_buffer", StringComparison.Ordinal))
            {
                return false;
            }

            pressureLog = logText;
            return true;
        }, timeoutMs: 20000);

        receiverTransport.ReceiveDeliveredDataFrame(new FileTransferChunkDataFrameV3
        {
            SessionId = sessionId,
            TransferId = transferId,
            ChunkIndex = 0,
            ChunkCount = chunkCount,
            Data = syntheticChunk,
        });

        await WaitUntilAsync(() => ReadRetainedOperationalLogs().Contains("event=filetransfer_receiver_buffer_pressure_exited", StringComparison.Ordinal), timeoutMs: 10000);
        var logTail = pressureLog + ReadRetainedOperationalLogs();
        Assert.Contains("event=filetransfer_receiver_buffer_pressure_entered", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_receiver_grant_clamped_for_buffer", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_receiver_buffer_pressure_exited", logTail, StringComparison.Ordinal);
        Assert.All(ExtractLongFieldValues(logTail, transferId, "filetransfer_receiver_write_batch_committed", "batch_bytes"), value => Assert.InRange(value, 1, 1024 * 1024));
    }

    [Fact]
    public async Task V3Receiver_SeekableReadableDestination_UsesSparseWritesAndCompletesWithIntegrity()
    {
        const int chunkSizeBytes = 4096;
        const string transferId = "transfer_receiver_sparse_writes";
        var payload = Enumerable.Range(0, 3 * 1024 * 1024).Select(static i => (byte)(i % 251)).ToArray();
        var sessionId = "session_receiver_sparse_writes_" + Guid.NewGuid().ToString("N");
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);

        var delayedFrames = new ConcurrentQueue<(LoopbackFileTransferTransport Target, FileTransferDataFrameV2 Frame)>();
        var firstRangeHeld = 0;
        var firstRangeReleased = 0;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, _) =>
        {
            if (Volatile.Read(ref firstRangeReleased) == 0 && Interlocked.CompareExchange(ref firstRangeHeld, 1, 0) == 0 && FrameContainsChunk(frame, transferId, 0))
            {
                delayedFrames.Enqueue((target, frame));
                return Task.FromResult(true);
            }

            if (Volatile.Read(ref firstRangeHeld) != 0 &&
                Volatile.Read(ref firstRangeReleased) == 0 &&
                FrameHighestChunkIndex(frame, transferId) >= 320)
            {
                ReleaseDelayedFrames(delayedFrames);
                Volatile.Write(ref firstRangeReleased, 1);
            }

            return Task.FromResult(false);
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var logStart = GetOperationalLogLength();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("sparse-writes.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 25000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Equal(payload, destination.ToArray());
        Assert.Contains("event=filetransfer_receiver_sparse_mode_selected", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_receiver_sparse_write_summary", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_receiver_sparse_commit_summary", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_receiver_sparse_hash_readback_summary", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_receiver_buffer_pressure_entered", logTail, StringComparison.Ordinal);
        Assert.Empty(ExtractLongFieldValues(logTail, transferId, "filetransfer_receiver_write_batch_committed", "batch_bytes"));
    }

    [Fact]
    public async Task V3Receiver_SparseWrittenCorruption_FailsFinalHashVerification()
    {
        const int chunkSizeBytes = 4096;
        const string transferId = "transfer_receiver_sparse_corrupt_hash";
        var payload = Enumerable.Range(0, 512 * 1024).Select(static i => (byte)(i % 251)).ToArray();
        var sessionId = "session_receiver_sparse_corrupt_hash_" + Guid.NewGuid().ToString("N");
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);

        var corrupted = 0;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, _) =>
        {
            if (Interlocked.CompareExchange(ref corrupted, 1, 0) != 0)
            {
                return Task.FromResult(false);
            }

            var corruptedFrame = TryCorruptFrameChunk(frame, transferId, chunkIndex: 5);
            if (corruptedFrame is null)
            {
                Interlocked.Exchange(ref corrupted, 0);
                return Task.FromResult(false);
            }

            target.ReceiveDeliveredDataFrame(corruptedFrame);
            return Task.FromResult(true);
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var logStart = GetOperationalLogLength();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("sparse-corrupt.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed, timeoutMs: 15000);

        Assert.Equal(HashMismatchErrorCode(), receiver.Snapshot.Inbound?.ErrorCode);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_receiver_sparse_mode_selected", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_receiver_sparse_hash_readback_summary", logTail, StringComparison.Ordinal);
        Assert.Contains("event=integrity_verify_failed", logTail, StringComparison.Ordinal);
    }

    private static bool FrameContainsChunk(FileTransferDataFrameV2 frame, string transferId, int chunkIndex)
        => frame switch
        {
            FileTransferChunkDataFrameV3 chunk when chunk.TransferId == transferId => chunk.ChunkIndex == chunkIndex,
            FileTransferChunkBatchFrameV3 batch when batch.TransferId == transferId => chunkIndex >= batch.StartChunkIndex && chunkIndex < batch.StartChunkIndex + batch.DataSegments.Count,
            _ => false,
        };

    private static int FrameHighestChunkIndex(FileTransferDataFrameV2 frame, string transferId)
        => frame switch
        {
            FileTransferChunkDataFrameV3 chunk when chunk.TransferId == transferId => chunk.ChunkIndex,
            FileTransferChunkBatchFrameV3 batch when batch.TransferId == transferId => batch.StartChunkIndex + batch.DataSegments.Count - 1,
            _ => -1,
        };

    private static void ReleaseDelayedFrames(ConcurrentQueue<(LoopbackFileTransferTransport Target, FileTransferDataFrameV2 Frame)> delayedFrames)
    {
        while (delayedFrames.TryDequeue(out var delayed))
        {
            delayed.Target.ReceiveDeliveredDataFrame(delayed.Frame);
        }
    }

    private static FileTransferDataFrameV2? TryCorruptFrameChunk(FileTransferDataFrameV2 frame, string transferId, int chunkIndex)
    {
        switch (frame)
        {
            case FileTransferChunkDataFrameV3 chunk when chunk.TransferId == transferId && chunk.ChunkIndex == chunkIndex:
            {
                var bytes = chunk.Data.ToArray();
                bytes[0] ^= 0x7F;
                return new FileTransferChunkDataFrameV3
                {
                    SessionId = chunk.SessionId,
                    TransferId = chunk.TransferId,
                    ChunkIndex = chunk.ChunkIndex,
                    ChunkCount = chunk.ChunkCount,
                    Data = bytes,
                };
            }
            case FileTransferChunkBatchFrameV3 batch when batch.TransferId == transferId && chunkIndex >= batch.StartChunkIndex && chunkIndex < batch.StartChunkIndex + batch.DataSegments.Count:
            {
                var segments = batch.DataSegments.Select(static segment => segment.ToArray()).ToList();
                var segmentIndex = chunkIndex - batch.StartChunkIndex;
                segments[segmentIndex][0] ^= 0x7F;
                return new FileTransferChunkBatchFrameV3
                {
                    SessionId = batch.SessionId,
                    TransferId = batch.TransferId,
                    StartChunkIndex = batch.StartChunkIndex,
                    ChunkCount = batch.ChunkCount,
                    DataSegments = segments,
                };
            }
            default:
                return null;
        }
    }

    private static IEnumerable<long> ExtractLongFieldValues(string logText, string transferId, string eventName, string fieldName)
    {
        foreach (Match match in Regex.Matches(logText, $@"event={Regex.Escape(eventName)};[^\r\n]*transfer_id={Regex.Escape(transferId)};[^\r\n]*\b{Regex.Escape(fieldName)}=(\d+)"))
        {
            if (long.TryParse(match.Groups[1].Value, out var value))
            {
                yield return value;
            }
        }
    }

    private sealed class WriteOnlySeekableMemoryStream : Stream
    {
        private readonly MemoryStream inner = new();

        public override bool CanRead => false;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public byte[] ToArray() => inner.ToArray();

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => inner.Seek(offset, origin);

        public override void SetLength(long value)
            => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
            => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
