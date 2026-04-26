using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using System.Security.Cryptography;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferPullRepairTests : SessionFileTransferServiceTestBase
{
    [Fact]
    public async Task PullSession_ExplicitRetryInsideResendGate_BlocksThenAllowsSingleResend()
    {
        const string transferId = "transfer_service_pull_retry_gate";
        var payload = Enumerable.Range(0, 48_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_retry_gate");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_retry_gate");
        senderTransport.Connect(receiverTransport);
        var droppedInitialChunkZero = 0;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferChunkDataFrameV2 chunk && chunk.ChunkIndex == 0 && Interlocked.Exchange(ref droppedInitialChunkZero, 1) == 0)
            {
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var logStart = GetOperationalLogLength();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-retry-gate.bin", payload.Length, transferId, ChunkSizeBytes: 4096), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV2>().Any() && senderTransport.SentDataFrames.OfType<FileTransferChunkDataFrameV2>().Any(frame => frame.ChunkIndex == 0), timeoutMs: 5000);
        var sessionId = senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV2>().First().SessionId;
        var retryRequest = new FileTransferRequestChunksFrameV2
        {
            SessionId = sessionId,
            TransferId = transferId,
            StartChunkIndex = 0,
            RequestedChunkCount = 1,
            PipelineDepth = 8,
        };
        senderTransport.ReceiveDeliveredDataFrame(retryRequest);
        senderTransport.ReceiveDeliveredDataFrame(retryRequest);
        await Task.Delay(300);
        Assert.Equal(1, senderTransport.SentDataFrames.OfType<FileTransferChunkDataFrameV2>().Count(frame => frame.ChunkIndex == 0));
        await Task.Delay(1100);
        senderTransport.ReceiveDeliveredDataFrame(retryRequest);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkDataFrameV2>().Count(frame => frame.ChunkIndex == 0) == 2, timeoutMs: 5000);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 12000);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Equal(payload, destination.ToArray());
        Assert.Contains("event=filetransfer_chunk_retry_requested", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_chunk_retry_gate_blocked", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_chunk_retry_sent", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullSession_OldestGapRetry_ResendsMissingChunkAndCompletes()
    {
        const string transferId = "transfer_service_pull_oldest_gap_retry";
        var payload = Enumerable.Range(0, 72_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_oldest_gap_retry");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_oldest_gap_retry");
        senderTransport.Connect(receiverTransport);
        var droppedChunkThree = 0;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferChunkDataFrameV2 chunk && chunk.ChunkIndex == 3 && Interlocked.Exchange(ref droppedChunkThree, 1) == 0)
            {
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var logStart = GetOperationalLogLength();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-oldest-gap-retry.bin", payload.Length, transferId, ChunkSizeBytes: 4096), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 15000);
        await WaitUntilAsync(() =>
        {
            var tail = ReadOperationalLogTail(logStart);
            return tail.Contains("event=filetransfer_request_timeout_detected", StringComparison.Ordinal)
                && tail.Contains("event=filetransfer_chunk_retry_requested", StringComparison.Ordinal)
                && tail.Contains("event=filetransfer_chunk_retry_sent", StringComparison.Ordinal);
        }, timeoutMs: 3000);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Equal(payload, destination.ToArray());
        Assert.Equal(2, senderTransport.SentDataFrames.OfType<FileTransferChunkDataFrameV2>().Count(frame => frame.ChunkIndex == 3));
        Assert.Contains("event=filetransfer_request_timeout_detected", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_chunk_retry_requested", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_chunk_retry_sent", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_chunk_resend_suppressed; transfer_id=transfer_service_pull_oldest_gap_retry", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullSession_OldestGapBlocksForwardRequests_UntilChunkZeroIsRecovered()
    {
        const string transferId = "transfer_service_pull_oldest_gap";
        var payload = Enumerable.Range(0, 32_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_oldest_gap");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_oldest_gap");
        senderTransport.Connect(receiverTransport);
        var delayedChunkZeroFrames = new ConcurrentQueue<FileTransferChunkDataFrameV2>();
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (target, frame, ct) =>
        {
            if (frame is FileTransferChunkDataFrameV2 chunk && chunk.ChunkIndex == 0)
            {
                delayedChunkZeroFrames.Enqueue(chunk);
                return true;
            }

            return false;
        };
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var logStart = GetOperationalLogLength();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-oldest-gap.bin", payload.Length, transferId, ChunkSizeBytes: 4096), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferRequestChunksFrameV2>().Any(static frame => frame.StartChunkIndex == 0 && frame.RequestedChunkCount == 6), timeoutMs: 5000);
        await WaitUntilAsync(() => !delayedChunkZeroFrames.IsEmpty, timeoutMs: 5000);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferRequestChunksFrameV2>().Any(static frame => frame.StartChunkIndex == 0 && frame.RequestedChunkCount == 1), timeoutMs: 5000);
        Assert.DoesNotContain(receiverTransport.SentDataFrames.OfType<FileTransferRequestChunksFrameV2>(), static frame => frame.StartChunkIndex >= 6);
        Assert.DoesNotContain(senderTransport.SentDataFrames.OfType<FileTransferChunkDataFrameV2>(), static frame => frame.ChunkIndex >= 6);
        await sender.CancelTransferAsync(transferId, "test_done", CancellationToken.None);
    }

    [Fact(Skip = "Obsolete internal pressure-state coverage after file-transfer pipeline refactors.")]
    public async Task ReceiverGapChurn_Alone_DoesNotEmitCatchUpOnlyPressureState()
    {
        const string transferId = "transfer_service_gap_only_no_pressure";
        const int chunkSizeBytes = 1024;
        var payload = Enumerable.Range(0, chunkSizeBytes * 120).Select(static i => (byte)(i % 251)).ToArray();
        LoopbackFileTransferTransport? senderTransportPeer = null;
        using var senderTransport = new LoopbackFileTransferTransport("session_service_gap_only_no_pressure")
        {
            OutboundChunkDeliveryOverrideAsync = (_, message, _) =>
            {
                if (message.ChunkIndex == 1)
                {
                    return Task.FromResult(true);
                }

                if (message.ChunkIndex > 2)
                {
                    return Task.FromResult(true);
                }

                senderTransportPeer!.DeliverChunkToPeer(message);
                return Task.FromResult(true);
            },
        };
        senderTransportPeer = senderTransport;
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_gap_only_no_pressure");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pressure-emit.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentMissingRanges.Count >= 2, timeoutMs: 8000);
        await Task.Delay(1500);
        Assert.False(receiverTransport.SentPressureStates.Any(static state => string.Equals(state.Mode, FileTransferProtocol.PressureModeCatchUpOnly, StringComparison.Ordinal)));
        Assert.False(sender.IsCatchUpOnlyPressureActive);
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Completed && receiver.Snapshot.InboundState == FileTransferTransferState.Completed, timeoutMs: 12000);
    }

    [Fact(Skip = "Obsolete repair-only mode coverage after file-transfer pipeline refactors.")]
    public async Task MissingRange_EntersRepairOnlyMode_UntilRemoteAckCatchesUp()
    {
        const string transferId = "transfer_service_repair_only_mode";
        const int chunkSizeBytes = 1024;
        var payload = Enumerable.Range(0, chunkSizeBytes * 200).Select(static i => (byte)(i % 251)).ToArray();
        var sentChunkIndices = new ConcurrentQueue<int>();
        var logStartIndex = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_repair_only_mode")
        {
            OutboundChunkDeliveryOverrideAsync = (_, message, _) =>
            {
                sentChunkIndices.Enqueue(message.ChunkIndex);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_repair_only_mode");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("repair-only.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()), CancellationToken.None);
        await WaitUntilAsync(() => sentChunkIndices.Count >= 32, timeoutMs: 5000);
        senderTransport.ReceiveDeliveredMissingRange(new FileTransferMissingRangeV1 { SessionId = "session_service_repair_only_mode", TransferId = transferId, StartChunkIndex = 0, EndChunkIndexExclusive = 1, });
        await WaitUntilAsync(() => GetOutboundRepairOnlyMode(sender), timeoutMs: 5000);
        senderTransport.ReceiveDeliveredWindowUpdate(new FileTransferWindowUpdateV1 { SessionId = "session_service_repair_only_mode", TransferId = transferId, NextExpectedChunkIndex = 1, GrantedUntilChunkIndexExclusive = 128, BytesReceived = chunkSizeBytes, });
        await Task.Delay(400);
        Assert.DoesNotContain(32, sentChunkIndices);
        Assert.True(GetOutboundRepairOnlyMode(sender));
        senderTransport.ReceiveDeliveredWindowUpdate(new FileTransferWindowUpdateV1 { SessionId = "session_service_repair_only_mode", TransferId = transferId, NextExpectedChunkIndex = 32, GrantedUntilChunkIndexExclusive = 128, BytesReceived = 32L * chunkSizeBytes, });
        await WaitUntilAsync(() => !GetOutboundRepairOnlyMode(sender), timeoutMs: 5000);
        await WaitUntilAsync(() => sentChunkIndices.Contains(32), timeoutMs: 5000);
        var logTail = ReadOperationalLogTail(logStartIndex);
        Assert.Contains("event=repair_only_mode_entered", logTail, StringComparison.Ordinal);
        Assert.Contains("event=repair_only_mode_exited", logTail, StringComparison.Ordinal);
        await sender.CancelTransferAsync(transferId, "test_done", CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Canceled && receiver.Snapshot.InboundState == FileTransferTransferState.Canceled);
    }

    [Fact(Skip = "Obsolete out-of-order repair-path coverage after file-transfer pipeline refactors.")]
    public async Task OutOfOrderChunk_IsBufferedAndCompletedWhenMissingChunkArrives()
    {
        const string transferId = "transfer_service_out_of_order";
        var payload = Enumerable.Range(0, 4096).Select(static i => (byte)(i % 251)).ToArray();
        FileTransferChunkV1? bufferedFirstChunk = null;
        using var senderTransport = new LoopbackFileTransferTransport("session_service_out_of_order")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (message.ChunkIndex == 0)
                {
                    bufferedFirstChunk = message;
                    return Task.FromResult(true);
                }

                if (message.ChunkIndex == 1 && bufferedFirstChunk is not null)
                {
                    target.ReceiveDeliveredChunk(message);
                    target.ReceiveDeliveredChunk(bufferedFirstChunk);
                    bufferedFirstChunk = null;
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_out_of_order");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("out-of-order.bin", payload.Length, transferId, ChunkSizeBytes: 1024), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Completed && receiver.Snapshot.InboundState == FileTransferTransferState.Completed);
        Assert.Null(receiver.Snapshot.Inbound!.ErrorCode);
        Assert.Null(sender.Snapshot.Outbound!.ErrorCode);
        Assert.Single(receiverTransport.SentCompletes);
        Assert.Empty(receiverTransport.SentErrors);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact(Skip = "Obsolete out-of-order repair-path coverage after file-transfer pipeline refactors.")]
    public async Task OutOfOrderGap_UsesBufferedFrontierAndMissingRange_ToCompleteTransfer()
    {
        const string transferId = "transfer_service_buffered_frontier_gap";
        const int chunkSizeBytes = 1024;
        const int startupGrantChunks = 128;
        var payload = Enumerable.Range(0, chunkSizeBytes * 700).Select(static i => (byte)(i % 251)).ToArray();
        var droppedInitialChunk = 0;
        var logStartIndex = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_buffered_frontier_gap")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (message.ChunkIndex == 0 && Interlocked.CompareExchange(ref droppedInitialChunk, 1, 0) == 0)
                {
                    return Task.FromResult(true);
                }

                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_buffered_frontier_gap")
        {
            OutboundWindowUpdateDeliveryOverrideAsync = (target, message, _) =>
            {
                target.ReceiveDeliveredWindowUpdate(message);
                return Task.FromResult(true);
            },
            OutboundMissingRangeDeliveryOverrideAsync = (target, message, _) =>
            {
                target.ReceiveDeliveredMissingRange(message);
                return Task.FromResult(true);
            },
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("buffered-frontier-gap.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentMissingRanges.Any(static range => range.StartChunkIndex == 0 && range.EndChunkIndexExclusive > 0), timeoutMs: 5000);
        await WaitUntilAsync(() => receiverTransport.SentWindowUpdates.Any(update => update.NextExpectedChunkIndex > 0 && update.GrantedUntilChunkIndexExclusive > startupGrantChunks), timeoutMs: 5000);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);
        Assert.Equal(payload, destination.ToArray());
        Assert.All(receiverTransport.SentWindowUpdates.Where(static update => update.NextExpectedChunkIndex == 0), update => Assert.Equal(startupGrantChunks, update.GrantedUntilChunkIndexExclusive));
        Assert.Contains(receiverTransport.SentWindowUpdates, update => update.NextExpectedChunkIndex > 0 && update.GrantedUntilChunkIndexExclusive > startupGrantChunks);
        Assert.Contains(receiverTransport.SentMissingRanges, static range => range.StartChunkIndex == 0 && range.EndChunkIndexExclusive > 0);
        var logTail = ReadOperationalLogTail(logStartIndex);
        Assert.True(logTail.Contains("reason=buffered_frontier", StringComparison.Ordinal) || logTail.Contains("reason=low_watermark", StringComparison.Ordinal) || logTail.Contains("reason=gap_progress_ack", StringComparison.Ordinal), $"Expected a steady-state window-update reason in log tail, but found:{Environment.NewLine}{logTail}");
    }

    [Fact(Skip = "Obsolete repair-path coverage after file-transfer pipeline refactors.")]
    public async Task MissingRange_RepairsRequestedChunkBeforeSendingNewSequentialChunk()
    {
        const string transferId = "transfer_service_repair_priority";
        const int chunkSizeBytes = 1024;
        const int droppedChunkIndex = 129;
        var payload = Enumerable.Range(0, chunkSizeBytes * 700).Select(static i => (byte)(i % 251)).ToArray();
        var droppedInitialChunk = 0;
        var missingRangeObserved = 0;
        var postMissingRangeChunkIndices = new List<int>();
        var postMissingRangeGate = new object ();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_repair_priority")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (Volatile.Read(ref missingRangeObserved) != 0)
                {
                    lock (postMissingRangeGate)
                    {
                        postMissingRangeChunkIndices.Add(message.ChunkIndex);
                    }
                }

                if (message.ChunkIndex == droppedChunkIndex && Interlocked.CompareExchange(ref droppedInitialChunk, 1, 0) == 0)
                {
                    return Task.FromResult(true);
                }

                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_repair_priority")
        {
            OutboundMissingRangeDeliveryOverrideAsync = (target, message, _) =>
            {
                if (message.StartChunkIndex == droppedChunkIndex)
                {
                    Interlocked.Exchange(ref missingRangeObserved, 1);
                }

                target.ReceiveDeliveredMissingRange(message);
                return Task.FromResult(true);
            },
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("repair-priority.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref missingRangeObserved) != 0, timeoutMs: 5000);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);
        int firstChunkAfterMissingRange;
        lock (postMissingRangeGate)
        {
            Assert.NotEmpty(postMissingRangeChunkIndices);
            firstChunkAfterMissingRange = postMissingRangeChunkIndices[0];
        }

        Assert.Equal(droppedChunkIndex, firstChunkAfterMissingRange);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact(Skip = "Obsolete repair-path coverage after file-transfer pipeline refactors.")]
    public async Task DroppedRepairChunk_IsRetriedWithoutNeedingSecondMissingRange()
    {
        const string transferId = "transfer_service_repair_retry";
        const int chunkSizeBytes = 1024;
        const int droppedChunkIndex = 129;
        var payload = Enumerable.Range(0, chunkSizeBytes * 700).Select(static i => (byte)(i % 251)).ToArray();
        var chunkSendAttempts = 0;
        var logStartIndex = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_repair_retry")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (message.ChunkIndex == droppedChunkIndex && Interlocked.Increment(ref chunkSendAttempts) <= 2)
                {
                    return Task.FromResult(true);
                }

                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_repair_retry");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("repair-retry.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);
        Assert.True(Volatile.Read(ref chunkSendAttempts) >= 3);
        Assert.True(receiverTransport.SentMissingRanges.Count(static range => range.StartChunkIndex == droppedChunkIndex) >= 1);
        var logTail = ReadOperationalLogTail(logStartIndex);
        Assert.True(logTail.Contains("event=repair_chunk_sent", StringComparison.Ordinal) || logTail.Contains("event=repair_chunk_resent", StringComparison.Ordinal), $"Expected repair send activity in log tail, but found:{Environment.NewLine}{logTail}");
        Assert.Contains("batch_size=1", logTail, StringComparison.Ordinal);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact(Skip = "Obsolete repair-path coverage after file-transfer pipeline refactors.")]
    public async Task RepeatedMissingRange_AfterAckAdvance_DoesNotRetransmitObsoleteRepairChunks()
    {
        const string transferId = "transfer_service_repair_obsolete";
        const int chunkSizeBytes = 1024;
        var droppedChunkIndices = new HashSet<int>
        {
            4,
            5,
            6,
            7
        };
        var initiallyDropped = new HashSet<int>();
        var missingRangeObserved = 0;
        var trackObsoletePhase = 0;
        var obsoleteRepairChunkIndices = new ConcurrentQueue<int>();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_repair_obsolete")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (droppedChunkIndices.Contains(message.ChunkIndex) && initiallyDropped.Add(message.ChunkIndex))
                {
                    return Task.FromResult(true);
                }

                if (Volatile.Read(ref trackObsoletePhase) != 0 && (message.ChunkIndex == 4 || message.ChunkIndex == 5))
                {
                    obsoleteRepairChunkIndices.Enqueue(message.ChunkIndex);
                }

                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_repair_obsolete")
        {
            OutboundMissingRangeDeliveryOverrideAsync = (target, message, _) =>
            {
                if (message.StartChunkIndex == 4)
                {
                    Interlocked.Exchange(ref missingRangeObserved, 1);
                }

                target.ReceiveDeliveredMissingRange(message);
                return Task.FromResult(true);
            },
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var payload = Enumerable.Range(0, chunkSizeBytes * 200).Select(static i => (byte)(i % 251)).ToArray();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("repair-obsolete.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref missingRangeObserved) != 0, timeoutMs: 5000);
        var sessionId = Assert.IsType<string>(sender.Snapshot.Outbound?.SessionId);
        senderTransport.ReceiveDeliveredWindowUpdate(new FileTransferWindowUpdateV1 { SessionId = sessionId, TransferId = transferId, NextExpectedChunkIndex = 6, GrantedUntilChunkIndexExclusive = 64, BytesReceived = 6L * chunkSizeBytes, });
        Interlocked.Exchange(ref trackObsoletePhase, 1);
        senderTransport.ReceiveDeliveredMissingRange(new FileTransferMissingRangeV1 { SessionId = sessionId, TransferId = transferId, StartChunkIndex = 4, EndChunkIndexExclusive = 8, });
        await Task.Delay(300);
        Assert.Empty(obsoleteRepairChunkIndices);
        await sender.CancelTransferAsync(transferId, "test_done", CancellationToken.None);
    }

    [Fact(Skip = "Obsolete bulk-fallback coverage after file-transfer pipeline refactors.")]
    public async Task PersistentEarlyGap_WithoutStaleBulk_DoesNotEnterBulkFallback_AndStillCompletes()
    {
        const string transferId = "transfer_service_bulk_fallback_cap";
        const int chunkSizeBytes = 1024;
        const int droppedChunkIndex = 5;
        var initialDrop = 0;
        var allowRepairDelivery = 0;
        var logStartIndex = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_bulk_fallback_cap")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (message.ChunkIndex == droppedChunkIndex && Interlocked.CompareExchange(ref initialDrop, 1, 0) == 0)
                {
                    return Task.FromResult(true);
                }

                if (message.ChunkIndex == droppedChunkIndex && Volatile.Read(ref allowRepairDelivery) == 0)
                {
                    return Task.FromResult(true);
                }

                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_bulk_fallback_cap");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var payload = Enumerable.Range(0, chunkSizeBytes * 700).Select(static i => (byte)(i % 251)).ToArray();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("bulk-fallback-cap.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentMissingRanges.Any(), timeoutMs: 7000);
        await Task.Delay(1500);
        Assert.DoesNotContain(receiverTransport.SentPressureStates, static state => string.Equals(state.Mode, FileTransferProtocol.PressureModeCatchUpOnly, StringComparison.Ordinal));
        Interlocked.Exchange(ref allowRepairDelivery, 1);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 40000);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact(Skip = "Obsolete bulk-unhealthy metrics coverage after file-transfer pipeline refactors.")]
    public async Task ObsoleteChunkArrivals_AppearInBulkUnhealthyMetrics()
    {
        const string transferId = "transfer_service_obsolete_chunk_metrics";
        const int chunkSizeBytes = 1024;
        const int droppedChunkIndex = 5;
        var initialDrop = 0;
        var allowRepairDelivery = 0;
        var injectedObsoleteDuplicates = 0;
        FileTransferChunkV1? firstChunk = null;
        var logStartIndex = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_obsolete_chunk_metrics")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (message.ChunkIndex == 0)
                {
                    firstChunk = message;
                }

                if (message.ChunkIndex == droppedChunkIndex && Interlocked.CompareExchange(ref initialDrop, 1, 0) == 0)
                {
                    return Task.FromResult(true);
                }

                if (message.ChunkIndex == droppedChunkIndex && Volatile.Read(ref allowRepairDelivery) == 0)
                {
                    return Task.FromResult(true);
                }

                if (message.ChunkIndex <= 8 || (message.ChunkIndex == droppedChunkIndex && Volatile.Read(ref allowRepairDelivery) != 0))
                {
                    target.ReceiveDeliveredChunk(message);
                    return Task.FromResult(true);
                }

                if (firstChunk is not null && message.ChunkIndex > 8 && Interlocked.Increment(ref injectedObsoleteDuplicates) <= 4)
                {
                    target.ReceiveDeliveredChunk(firstChunk);
                }

                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_obsolete_chunk_metrics");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var payload = Enumerable.Range(0, chunkSizeBytes * 700).Select(static i => (byte)(i % 251)).ToArray();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("obsolete-metrics.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStartIndex).Contains("event=filetransfer_bulk_unhealthy_detected", StringComparison.Ordinal), timeoutMs: 7000);
        await WaitUntilAsync(() => receiverTransport.SentPressureStates.Any(static state => string.Equals(state.Mode, FileTransferProtocol.PressureModeCatchUpOnly, StringComparison.Ordinal)), timeoutMs: 7000);
        var logTail = ReadOperationalLogTail(logStartIndex);
        var obsoleteRecentMatch = Regex.Match(logTail, @"obsolete_chunk_count_recent=(\d+)");
        Assert.Contains("obsolete_chunks_since_progress=", logTail, StringComparison.Ordinal);
        Assert.True(obsoleteRecentMatch.Success, $"Expected recent obsolete chunk count in log tail.{Environment.NewLine}{logTail}");
        Assert.True(int.Parse(obsoleteRecentMatch.Groups[1].Value) > 0, $"Expected obsolete_chunk_count_recent > 0.{Environment.NewLine}{logTail}");
        Assert.Contains("obsolete_chunk_arrival_ratio=", logTail, StringComparison.Ordinal);
        Assert.Contains("event=pressure_state_sent", logTail, StringComparison.Ordinal);
        Interlocked.Exchange(ref allowRepairDelivery, 1);
        await sender.CancelTransferAsync(transferId, "test_done", CancellationToken.None);
    }

}
