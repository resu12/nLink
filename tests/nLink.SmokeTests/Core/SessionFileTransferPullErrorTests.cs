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
public sealed class SessionFileTransferPullErrorTests : SessionFileTransferServiceTestBase
{
    [Fact]
    public async Task PullSession_StallsAtFirstChunk_FailsTerminallyInsteadOfLoopingForever()
    {
        const string transferId = "transfer_service_pull_stall";
        var payload = Enumerable.Range(0, 32_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_stall");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_stall");
        senderTransport.Connect(receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferChunkDataFrameV2)
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
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-stall.bin", payload.Length, transferId, ChunkSizeBytes: 4096), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed, timeoutMs: 12000);
        var inbound = receiver.Snapshot.Inbound!;
        Assert.Equal(FileTransferResultCodes.PullSessionStalled, inbound.ErrorCode);
        Assert.Equal(FileTransferResultCodes.PullSessionStalled, inbound.StatusMessage);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.True(Regex.Matches(logTail, "event=filetransfer_request_sent;.*start_chunk=0; requested_chunk_count=1", RegexOptions.CultureInvariant).Count <= 4, "Expected the stalled pull session to fail fast instead of repeatedly requesting chunk 0.");
    }

    [Fact]
    public async Task InboundCancel_IsNotBlockedBehindWindowUpdateControlChatter()
    {
        const string transferId = "transfer_service_inbound_cancel_priority";
        var payload = Enumerable.Range(0, 96_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_inbound_cancel_priority");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_inbound_cancel_priority");
        senderTransport.Connect(receiverTransport);
        senderTransport.OutboundChunkDeliveryOverrideAsync = (_, _, _) => Task.FromResult(true);
        var blockedControlEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlockedControl = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.InboundDispatchBeforeWorkAsyncForTests = (lane, operation) =>
        {
            if (lane == "control" && operation == "window_update" && !releaseBlockedControl.Task.IsCompleted)
            {
                blockedControlEntered.TrySetResult(true);
                return releaseBlockedControl.Task;
            }

            return Task.CompletedTask;
        };
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("cancel-priority.bin", payload.Length, transferId, ChunkSizeBytes: 4096), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await blockedControlEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var outbound = sender.Snapshot.Outbound!;
        await receiverTransport.SendFileTransferCancelAsync(new FileTransferCancelV1 { SessionId = outbound.SessionId, TransferId = outbound.TransferId, Reason = "test_cancel", }, CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Canceled, timeoutMs: 3000);
        Assert.Equal(FileTransferTransferState.Canceled, sender.Snapshot.Outbound?.State);
        releaseBlockedControl.TrySetResult(true);
    }

    [Fact]
    public async Task PullSession_MismatchedDataFrameSessionId_IsRejectedAndFails()
    {
        const string transferId = "transfer_service_pull_session_mismatch";
        var payload = Enumerable.Range(0, 16_384).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_session_mismatch");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_session_mismatch");
        senderTransport.Connect(receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, ct) =>
        {
            if (frame is FileTransferChunkDataFrameV2 chunk)
            {
                target.ReceiveDeliveredDataFrame(chunk with { SessionId = "wrong_session" });
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var logStart = GetOperationalLogLength();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-session-mismatch.bin", payload.Length, transferId, ChunkSizeBytes: 4096), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed, timeoutMs: 13000);
        Assert.Equal(0, receiver.Snapshot.Inbound!.BytesTransferred);
    }

    [Fact]
    public async Task Decline_TransitionsOutboundAndInboundToDeclined()
    {
        const string transferId = "transfer_service_decline";
        var payload = new byte[256];
        using var senderTransport = new LoopbackFileTransferTransport("session_service_decline");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_decline");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("decline.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.DeclineIncomingTransferAsync(transferId, "not_now", CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Declined && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Declined);
        Assert.Equal("not_now", receiver.Snapshot.Inbound!.StatusMessage);
        Assert.Equal("not_now", sender.Snapshot.Outbound!.StatusMessage);
    }

    [Fact]
    public async Task CancelBeforeAcceptance_PropagatesCanceledState()
    {
        const string transferId = "transfer_service_cancel";
        var payload = new byte[1024];
        using var senderTransport = new LoopbackFileTransferTransport("session_service_cancel");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_cancel");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("cancel.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await sender.CancelTransferAsync(transferId, "user_canceled", CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Canceled && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled);
        Assert.Equal("user_canceled", sender.Snapshot.Outbound!.StatusMessage);
        Assert.Equal("user_canceled", receiver.Snapshot.Inbound!.StatusMessage);
    }

    [Fact(Skip = "Legacy receiver-cancel micro-state coverage no longer matches the current transfer pipeline.")]
    public async Task ReceiverCancelDuringReceiving_PropagatesCanceledState()
    {
        const string transferId = "transfer_service_receiver_cancel";
        var payload = Enumerable.Range(0, 8192).Select(static i => (byte)(i % 251)).ToArray();
        var cancelTriggered = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_service_receiver_cancel");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_receiver_cancel");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        senderTransport.AfterChunkDeliveredAsync = async (message, ct) =>
        {
            if (message.TransferId != transferId || message.ChunkIndex != 0 || Interlocked.Exchange(ref cancelTriggered, 1) != 0)
            {
                return;
            }

            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.TransferId == transferId && receiver.Snapshot.Inbound.State == FileTransferTransferState.Receiving && receiver.Snapshot.Inbound.BytesTransferred > 0, timeoutMs: 3000);
            await receiver.CancelTransferAsync(transferId, "receiver_canceled", CancellationToken.None);
        };
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("receiver-cancel.bin", payload.Length, transferId, ChunkSizeBytes: 2048), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Canceled && receiver.Snapshot.InboundState == FileTransferTransferState.Canceled);
        Assert.Equal("receiver_canceled", sender.Snapshot.Outbound!.StatusMessage);
        Assert.Equal("receiver_canceled", receiver.Snapshot.Inbound!.StatusMessage);
    }

    [Fact(Skip = "Legacy receiver-cancel micro-state coverage no longer matches the current transfer pipeline.")]
    public async Task ReceiverCancelDuringReceiving_DeletesTempArtifact()
    {
        const string transferId = "transfer_service_receiver_cancel_temp_cleanup";
        var payload = Enumerable.Range(0, 8192).Select(static i => (byte)(i % 251)).ToArray();
        var cancelTriggered = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_service_receiver_cancel_temp_cleanup");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_receiver_cancel_temp_cleanup");
        senderTransport.Connect(receiverTransport);
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var finalPath = Path.Combine(tempRoot, "cancel.bin");
        var tempPath = finalPath + ".part";
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        senderTransport.AfterChunkDeliveredAsync = async (message, ct) =>
        {
            if (message.TransferId != transferId || message.ChunkIndex != 0 || Interlocked.Exchange(ref cancelTriggered, 1) != 0)
            {
                return;
            }

            await WaitUntilAsync(() => File.Exists(tempPath), timeoutMs: 3000);
            await receiver.CancelTransferAsync(transferId, "receiver_canceled", CancellationToken.None);
        };
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        try
        {
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("cancel.bin", payload.Length, transferId, ChunkSizeBytes: 2048), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult(CreateTempReceiveDestination(finalPath)), CancellationToken.None);
            await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Canceled && receiver.Snapshot.InboundState == FileTransferTransferState.Canceled);
            Assert.False(File.Exists(finalPath));
            Assert.False(File.Exists(tempPath));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BusyInboundOffer_IsAutoDeclinedForSecondSender()
    {
        const string firstTransferId = "transfer_service_busy_a";
        const string secondTransferId = "transfer_service_busy_b";
        var payload = new byte[512];
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_busy");
        using var firstSenderTransport = new LoopbackFileTransferTransport("session_service_busy");
        using var secondSenderTransport = new LoopbackFileTransferTransport("session_service_busy");
        receiverTransport.Connect(firstSenderTransport);
        secondSenderTransport.Connect(receiverTransport);
        using var firstSender = new SessionFileTransferService();
        using var secondSender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        firstSender.AttachTransport(firstSenderTransport);
        secondSender.AttachTransport(secondSenderTransport);
        receiver.AttachTransport(receiverTransport);
        await firstSender.TryStartSendAsync(new FileTransferSendDescriptor("first.bin", payload.Length, firstTransferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision && receiver.Snapshot.Inbound.TransferId == firstTransferId);
        await secondSender.TryStartSendAsync(new FileTransferSendDescriptor("second.bin", payload.Length, secondTransferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => secondSender.Snapshot.Outbound?.State == FileTransferTransferState.Declined);
        Assert.Equal(firstTransferId, receiver.Snapshot.Inbound!.TransferId);
        Assert.Equal(FileTransferTransferState.PendingDecision, receiver.Snapshot.Inbound.State);
        Assert.Equal("busy", secondSender.Snapshot.Outbound!.StatusMessage);
    }

    [Fact(Skip = "Legacy inconsistent-start error-path coverage no longer matches the current transfer pipeline.")]
    public async Task InconsistentStartChunkCount_FailsReceiverAndPropagatesError()
    {
        const string transferId = "transfer_service_start_chunk_mismatch";
        var payload = Enumerable.Range(0, 4096).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_start_chunk_mismatch")
        {
            OutboundStartTransform = message => message with
            {
                ChunkCount = message.ChunkCount + 1
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_start_chunk_mismatch");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("bad-start.bin", payload.Length, transferId, ChunkSizeBytes: 1024), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Failed && receiver.Snapshot.InboundState == FileTransferTransferState.Failed);
        Assert.Equal(InvalidStateErrorCode(), receiver.Snapshot.Inbound!.ErrorCode);
        Assert.Equal(InvalidStateErrorCode(), sender.Snapshot.Outbound!.ErrorCode);
        Assert.Empty(receiverTransport.SentCompletes);
        Assert.Single(receiverTransport.SentErrors);
    }

    [Fact(Skip = "Legacy truncated-final-chunk error-path coverage no longer matches the current transfer pipeline.")]
    public async Task TruncatedFinalChunk_FailsReceiverWithFileSizeMismatch()
    {
        const string transferId = "transfer_service_truncated_final_chunk";
        var payload = Enumerable.Range(0, 4096).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_truncated_final_chunk")
        {
            OutboundChunkTransform = message =>
            {
                if (message.ChunkIndex != message.ChunkCount - 1)
                {
                    return message;
                }

                var bytes = Convert.FromBase64String(message.DataBase64);
                var truncated = bytes.AsSpan(0, bytes.Length / 2).ToArray();
                return message with
                {
                    DataBase64 = Convert.ToBase64String(truncated)
                };
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_truncated_final_chunk");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("truncated.bin", payload.Length, transferId, ChunkSizeBytes: 2048), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Failed && receiver.Snapshot.InboundState == FileTransferTransferState.Failed);
        Assert.Equal(FileSizeMismatchErrorCode(), receiver.Snapshot.Inbound!.ErrorCode);
        Assert.Equal(FileSizeMismatchErrorCode(), sender.Snapshot.Outbound!.ErrorCode);
        Assert.Empty(receiverTransport.SentCompletes);
        Assert.Single(receiverTransport.SentErrors);
    }

    [Fact(Skip = "Legacy state-sequence coverage no longer matches the current transfer pipeline.")]
    public async Task TransferChanged_ReportsExpectedStateSequence_ForSuccessfulTransfer()
    {
        const string transferId = "transfer_service_state_sequence";
        var payload = Enumerable.Range(0, 8192).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_state_sequence");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_state_sequence");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        var senderStates = new List<FileTransferTransferState>();
        var receiverStates = new List<FileTransferTransferState>();
        sender.TransferChanged += (_, e) =>
        {
            lock (senderStates)
            {
                senderStates.Add(e.Snapshot.OutboundState);
            }
        };
        receiver.TransferChanged += (_, e) =>
        {
            lock (receiverStates)
            {
                receiverStates.Add(e.Snapshot.InboundState);
            }
        };
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("sequence.bin", payload.Length, transferId, ChunkSizeBytes: 2048), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Completed && receiver.Snapshot.InboundState == FileTransferTransferState.Completed);
        FileTransferTransferState[] senderSequence;
        FileTransferTransferState[] receiverSequence;
        lock (senderStates)
        {
            senderSequence = senderStates.ToArray();
        }

        lock (receiverStates)
        {
            receiverSequence = receiverStates.ToArray();
        }

        AssertContainsOrderedSubsequence(senderSequence, FileTransferTransferState.Idle, FileTransferTransferState.Offering, FileTransferTransferState.AwaitingAcceptance, FileTransferTransferState.Sending, FileTransferTransferState.AwaitingCompletion, FileTransferTransferState.Completed);
        AssertContainsOrderedSubsequence(receiverSequence, FileTransferTransferState.Idle, FileTransferTransferState.Receiving, FileTransferTransferState.Verifying, FileTransferTransferState.Completed);
        Assert.Contains(FileTransferTransferState.PendingDecision, receiverSequence);
        Assert.Contains(FileTransferTransferState.AwaitingStart, receiverSequence);
    }

    [Fact]
    public async Task HashMismatch_FailsReceiverAndPropagatesFailureToSender()
    {
        const string transferId = "transfer_service_hash_mismatch";
        var firstPayload = Enumerable.Range(0, 4096).Select(static i => (byte)(i % 251)).ToArray();
        var secondPayload = firstPayload.ToArray();
        secondPayload[^1] ^= 0x5A;
        var openCount = 0;
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_hash_mismatch");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_hash_mismatch");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var finalPath = Path.Combine(tempRoot, "mismatch.bin");
        var tempPath = finalPath + ".part";
        try
        {
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("mismatch.bin", firstPayload.Length, transferId, ChunkSizeBytes: 1024), _ =>
            {
                var payload = Interlocked.Increment(ref openCount) == 1 ? firstPayload : secondPayload;
                return Task.FromResult<Stream>(new MemoryStream(payload, writable: false));
            }, CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult(CreateTempReceiveDestination(finalPath)), CancellationToken.None);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Failed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed);
            Assert.Equal(HashMismatchErrorCode(), receiver.Snapshot.Inbound!.ErrorCode);
            Assert.Equal(HashMismatchErrorCode(), sender.Snapshot.Outbound!.ErrorCode);
            Assert.Null(receiver.Snapshot.Inbound.SavedFilePath);
            Assert.False(File.Exists(finalPath));
            Assert.False(File.Exists(tempPath));
            await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=integrity_verify_failed", StringComparison.Ordinal), timeoutMs: 1500);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=integrity_verify_failed", logTail, StringComparison.Ordinal);
            Assert.Contains("error_code=integrity_mismatch", logTail, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact(Skip = "Legacy finalize-collision error-path coverage no longer matches the current transfer pipeline.")]
    public async Task FinalizeCollision_FailsTransfer_AndPreservesTempArtifact()
    {
        const string transferId = "transfer_service_finalize_collision";
        var payload = Enumerable.Range(0, 4096).Select(static i => (byte)(i % 251)).ToArray();
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_finalize_collision");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_finalize_collision");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var finalPath = Path.Combine(tempRoot, "collision.bin");
        var tempPath = finalPath + ".part";
        var blockerCreated = 0;
        senderTransport.AfterChunkDeliveredAsync = async (message, ct) =>
        {
            if (message.TransferId != transferId || message.ChunkIndex != 0 || Interlocked.Exchange(ref blockerCreated, 1) != 0)
            {
                return;
            }

            await WaitUntilAsync(() => File.Exists(tempPath), timeoutMs: 3000);
            File.WriteAllText(finalPath, "existing");
            await Task.Yield();
        };
        try
        {
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("collision.bin", payload.Length, transferId, ChunkSizeBytes: 2048), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult(CreateTempReceiveDestination(finalPath)), CancellationToken.None);
            await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Failed && receiver.Snapshot.InboundState == FileTransferTransferState.Failed);
            Assert.Equal(FileTransferResultCodes.FinalizeFailed, receiver.Snapshot.Inbound!.ErrorCode);
            Assert.Equal(FileTransferResultCodes.FinalizeFailed, sender.Snapshot.Outbound!.ErrorCode);
            Assert.Null(receiver.Snapshot.Inbound.SavedFilePath);
            Assert.True(File.Exists(finalPath));
            Assert.True(File.Exists(tempPath));
            Assert.NotEqual(payload, File.ReadAllBytes(finalPath));
            await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=temp_finalize_failed", StringComparison.Ordinal), timeoutMs: 1500);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=temp_finalize_failed", logTail, StringComparison.Ordinal);
            Assert.Contains("error_code=finalize_failed", logTail, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TransportDisconnectDuringReceiving_FailsAfterGraceAndDeletesTempArtifact()
    {
        const string transferId = "transfer_service_disconnect_cleanup";
        var payload = Enumerable.Range(0, 32768).Select(static i => (byte)(i % 251)).ToArray();
        var disconnectTriggered = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_service_disconnect_cleanup");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_disconnect_cleanup");
        senderTransport.Connect(receiverTransport);
        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var finalPath = Path.Combine(tempRoot, "disconnect.bin");
        var tempPath = finalPath + ".part";
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (target, frame, ct) =>
        {
            if (frame is not FileTransferChunkDataFrameV2 chunk || chunk.TransferId != transferId || chunk.ChunkIndex != 0 || Interlocked.Exchange(ref disconnectTriggered, 1) != 0)
            {
                return false;
            }

            target.ReceiveDeliveredDataFrame(frame);
            receiverTransport.RaiseDisconnected();
            await Task.Yield();
            return true;
        };
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        try
        {
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("disconnect.bin", payload.Length, transferId, ChunkSizeBytes: 2048), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult(CreateTempReceiveDestination(finalPath)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.Failed, timeoutMs: 35000);
            Assert.Equal(FileTransferResultCodes.TransportDisconnected, receiver.Snapshot.Inbound!.ErrorCode);
            Assert.Equal(FileTransferResultCodes.TransportDisconnected, sender.Snapshot.Outbound!.ErrorCode);
            Assert.False(File.Exists(finalPath));
            Assert.False(File.Exists(tempPath));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact(Skip = "Legacy completion-boundary coverage no longer matches the current transfer pipeline.")]
    public async Task Sender_StaysAwaitingCompletionUntilReceiverCompleteArrives()
    {
        const string transferId = "transfer_service_complete_boundary";
        var payload = Enumerable.Range(0, 2048).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_complete_boundary");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_complete_boundary");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var releaseComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiverTransport.BeforeCompleteDeliveredAsync = (_, ct) => releaseComplete.Task.WaitAsync(ct);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("complete-boundary.bin", payload.Length, transferId, ChunkSizeBytes: 2048), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.AwaitingCompletion, timeoutMs: 3000);
        Assert.NotEqual(FileTransferTransferState.Completed, sender.Snapshot.OutboundState);
        Assert.NotEqual(FileTransferTransferState.Completed, receiver.Snapshot.InboundState);
        releaseComplete.TrySetResult(true);
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Completed && receiver.Snapshot.InboundState == FileTransferTransferState.Completed);
    }

}
