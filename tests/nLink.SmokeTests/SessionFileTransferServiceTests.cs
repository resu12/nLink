using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using System.Security.Cryptography;

namespace NLink.SmokeTests;

public sealed class SessionFileTransferServiceTests
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
            ct =>
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

    [Fact]
    public async Task RoundTrip_DoesNotReadPastAdvertisedChunkSize_WhenArrayPoolReturnsLargerBuffer()
    {
        const string transferId = "transfer_service_chunk_size_cap";
        var payload = Enumerable.Range(0, 49_153).Select(static i => (byte)(i % 251)).ToArray();
        var observedChunkSizes = new List<int>();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_chunk_size_cap")
        {
            OutboundChunkTransform = message =>
            {
                observedChunkSizes.Add(Convert.FromBase64String(message.DataBase64).Length);
                return message;
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_chunk_size_cap");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pooled.bin", payload.Length, transferId, ChunkSizeBytes: 24_576),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        Assert.NotNull(started);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed);

        Assert.Equal([24_576, 24_576, 1], observedChunkSizes);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task TryStartSendAsync_PreHashesFileBeforeSendingOffer()
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

        Assert.Equal(1, Volatile.Read(ref openReadCount));
        Assert.Equal(FileTransferTransferState.AwaitingAcceptance, sender.Snapshot.OutboundState);
        Assert.Equal(expectedHash, sender.Snapshot.Outbound!.Sha256Base64);
        Assert.Equal(expectedHash, receiver.Snapshot.Inbound!.Sha256Base64);
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
        var firstChunkObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        senderTransport.AfterChunkDeliveredAsync = async (message, ct) =>
        {
            if (message.TransferId != transferId || message.ChunkIndex != 0)
            {
                return;
            }

            await WaitUntilAsync(() => File.Exists(tempPath), timeoutMs: 3000);
            Assert.False(File.Exists(finalPath));
            firstChunkObserved.TrySetResult(true);
            await releaseSend.Task.WaitAsync(ct);
        };

        try
        {
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("final.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);

            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(
                transferId,
                (_, _) => Task.FromResult(CreateTempReceiveDestination(finalPath)),
                CancellationToken.None);

            await firstChunkObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
            releaseSend.TrySetResult(true);

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
            releaseSend.TrySetResult(true);
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

            await WaitUntilAsync(() =>
                receiver.Snapshot.InboundState == FileTransferTransferState.Verifying &&
                sender.Snapshot.OutboundState == FileTransferTransferState.AwaitingCompletion,
                timeoutMs: 3000);

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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("decline.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.DeclineIncomingTransferAsync(transferId, "not_now", CancellationToken.None);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Declined &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Declined);

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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("cancel.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await sender.CancelTransferAsync(transferId, "user_canceled", CancellationToken.None);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Canceled &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled);

        Assert.Equal("user_canceled", sender.Snapshot.Outbound!.StatusMessage);
        Assert.Equal("user_canceled", receiver.Snapshot.Inbound!.StatusMessage);
    }

    [Fact]
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

            await WaitUntilAsync(
                () =>
                    receiver.Snapshot.Inbound?.TransferId == transferId &&
                    receiver.Snapshot.Inbound.State == FileTransferTransferState.Receiving &&
                    receiver.Snapshot.Inbound.BytesTransferred > 0,
                timeoutMs: 3000);

            await receiver.CancelTransferAsync(transferId, "receiver_canceled", CancellationToken.None);
        };
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("receiver-cancel.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.OutboundState == FileTransferTransferState.Canceled &&
            receiver.Snapshot.InboundState == FileTransferTransferState.Canceled);

        Assert.Equal("receiver_canceled", sender.Snapshot.Outbound!.StatusMessage);
        Assert.Equal("receiver_canceled", receiver.Snapshot.Inbound!.StatusMessage);
    }

    [Fact]
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
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("cancel.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);

            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(
                transferId,
                (_, _) => Task.FromResult(CreateTempReceiveDestination(finalPath)),
                CancellationToken.None);

            await WaitUntilAsync(() =>
                sender.Snapshot.OutboundState == FileTransferTransferState.Canceled &&
                receiver.Snapshot.InboundState == FileTransferTransferState.Canceled);

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
    public async Task SecondOutboundSend_IsRejectedWhileOutboundTransferIsActive()
    {
        const string firstTransferId = "transfer_service_outbound_a";
        const string secondTransferId = "transfer_service_outbound_b";
        var payload = new byte[1024];
        using var senderTransport = new LoopbackFileTransferTransport("session_service_outbound_busy");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_outbound_busy");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        var firstStart = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("first.bin", payload.Length, firstTransferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        Assert.NotNull(firstStart);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        var secondStart = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("second.bin", payload.Length, secondTransferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        Assert.Null(secondStart);
        Assert.Equal(firstTransferId, sender.Snapshot.Outbound!.TransferId);
        Assert.Equal(FileTransferTransferState.AwaitingAcceptance, sender.Snapshot.Outbound.State);
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

        await firstSender.TryStartSendAsync(
            new FileTransferSendDescriptor("first.bin", payload.Length, firstTransferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision &&
            receiver.Snapshot.Inbound.TransferId == firstTransferId);

        await secondSender.TryStartSendAsync(
            new FileTransferSendDescriptor("second.bin", payload.Length, secondTransferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => secondSender.Snapshot.Outbound?.State == FileTransferTransferState.Declined);

        Assert.Equal(firstTransferId, receiver.Snapshot.Inbound!.TransferId);
        Assert.Equal(FileTransferTransferState.PendingDecision, receiver.Snapshot.Inbound.State);
        Assert.Equal("busy", secondSender.Snapshot.Outbound!.StatusMessage);
    }

    [Fact]
    public async Task InconsistentStartChunkCount_FailsReceiverAndPropagatesError()
    {
        const string transferId = "transfer_service_start_chunk_mismatch";
        var payload = Enumerable.Range(0, 4096).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_start_chunk_mismatch")
        {
            OutboundStartTransform = message => message with { ChunkCount = message.ChunkCount + 1 },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_start_chunk_mismatch");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("bad-start.bin", payload.Length, transferId, ChunkSizeBytes: 1024),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.OutboundState == FileTransferTransferState.Failed &&
            receiver.Snapshot.InboundState == FileTransferTransferState.Failed);

        Assert.Equal(InvalidStateErrorCode(), receiver.Snapshot.Inbound!.ErrorCode);
        Assert.Equal(InvalidStateErrorCode(), sender.Snapshot.Outbound!.ErrorCode);
        Assert.Empty(receiverTransport.SentCompletes);
        Assert.Single(receiverTransport.SentErrors);
    }

    [Fact]
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("out-of-order.bin", payload.Length, transferId, ChunkSizeBytes: 1024),
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
            receiver.Snapshot.InboundState == FileTransferTransferState.Completed);

        Assert.Null(receiver.Snapshot.Inbound!.ErrorCode);
        Assert.Null(sender.Snapshot.Outbound!.ErrorCode);
        Assert.Single(receiverTransport.SentCompletes);
        Assert.Empty(receiverTransport.SentErrors);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
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
                return message with { DataBase64 = Convert.ToBase64String(truncated) };
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_truncated_final_chunk");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("truncated.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.OutboundState == FileTransferTransferState.Failed &&
            receiver.Snapshot.InboundState == FileTransferTransferState.Failed);

        Assert.Equal(FileSizeMismatchErrorCode(), receiver.Snapshot.Inbound!.ErrorCode);
        Assert.Equal(FileSizeMismatchErrorCode(), sender.Snapshot.Outbound!.ErrorCode);
        Assert.Empty(receiverTransport.SentCompletes);
        Assert.Single(receiverTransport.SentErrors);
    }

    [Fact]
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("sequence.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
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
            receiver.Snapshot.InboundState == FileTransferTransferState.Completed);

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

        AssertContainsOrderedSubsequence(
            senderSequence,
            FileTransferTransferState.Idle,
            FileTransferTransferState.Offering,
            FileTransferTransferState.AwaitingAcceptance,
            FileTransferTransferState.Sending,
            FileTransferTransferState.AwaitingCompletion,
            FileTransferTransferState.Completed);

        AssertContainsOrderedSubsequence(
            receiverSequence,
            FileTransferTransferState.Idle,
            FileTransferTransferState.Receiving,
            FileTransferTransferState.Verifying,
            FileTransferTransferState.Completed);
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
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("mismatch.bin", firstPayload.Length, transferId, ChunkSizeBytes: 1024),
                _ =>
                {
                    var payload = Interlocked.Increment(ref openCount) == 1 ? firstPayload : secondPayload;
                    return Task.FromResult<Stream>(new MemoryStream(payload, writable: false));
                },
                CancellationToken.None);

            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(
                transferId,
                (_, _) => Task.FromResult(CreateTempReceiveDestination(finalPath)),
                CancellationToken.None);

            await WaitUntilAsync(() =>
                sender.Snapshot.Outbound?.State == FileTransferTransferState.Failed &&
                receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed);

            Assert.Equal(HashMismatchErrorCode(), receiver.Snapshot.Inbound!.ErrorCode);
            Assert.Equal(HashMismatchErrorCode(), sender.Snapshot.Outbound!.ErrorCode);
            Assert.Null(receiver.Snapshot.Inbound.SavedFilePath);
            Assert.False(File.Exists(finalPath));
            Assert.False(File.Exists(tempPath));
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=integrity_verify_failed", StringComparison.Ordinal),
                timeoutMs: 1500);
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

    [Fact]
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
            if (message.TransferId != transferId ||
                message.ChunkIndex != 0 ||
                Interlocked.Exchange(ref blockerCreated, 1) != 0)
            {
                return;
            }

            await WaitUntilAsync(() => File.Exists(tempPath), timeoutMs: 3000);
            File.WriteAllText(finalPath, "existing");
            await Task.Yield();
        };

        try
        {
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("collision.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);

            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(
                transferId,
                (_, _) => Task.FromResult(CreateTempReceiveDestination(finalPath)),
                CancellationToken.None);

            await WaitUntilAsync(() =>
                sender.Snapshot.OutboundState == FileTransferTransferState.Failed &&
                receiver.Snapshot.InboundState == FileTransferTransferState.Failed);

            Assert.Equal(FileTransferResultCodes.FinalizeFailed, receiver.Snapshot.Inbound!.ErrorCode);
            Assert.Equal(FileTransferResultCodes.FinalizeFailed, sender.Snapshot.Outbound!.ErrorCode);
            Assert.Null(receiver.Snapshot.Inbound.SavedFilePath);
            Assert.True(File.Exists(finalPath));
            Assert.True(File.Exists(tempPath));
            Assert.NotEqual(payload, File.ReadAllBytes(finalPath));
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=temp_finalize_failed", StringComparison.Ordinal),
                timeoutMs: 1500);
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
    public async Task TransportDisconnectDuringReceiving_FailsInboundAndDeletesTempArtifact()
    {
        const string transferId = "transfer_service_disconnect_cleanup";
        var payload = Enumerable.Range(0, 8192).Select(static i => (byte)(i % 251)).ToArray();
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
        senderTransport.AfterChunkDeliveredAsync = async (message, ct) =>
        {
            if (message.TransferId != transferId || message.ChunkIndex != 0 || Interlocked.Exchange(ref disconnectTriggered, 1) != 0)
            {
                return;
            }

            await WaitUntilAsync(() => File.Exists(tempPath), timeoutMs: 3000);
            receiverTransport.RaiseDisconnected();
            await Task.Yield();
        };
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        try
        {
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("disconnect.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);

            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(
                transferId,
                (_, _) => Task.FromResult(CreateTempReceiveDestination(finalPath)),
                CancellationToken.None);

            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.Failed);

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

    [Fact]
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("complete-boundary.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(
            () => sender.Snapshot.OutboundState == FileTransferTransferState.AwaitingCompletion,
            timeoutMs: 3000);

        Assert.NotEqual(FileTransferTransferState.Completed, sender.Snapshot.OutboundState);
        Assert.NotEqual(FileTransferTransferState.Completed, receiver.Snapshot.InboundState);

        releaseComplete.TrySetResult(true);

        await WaitUntilAsync(() =>
            sender.Snapshot.OutboundState == FileTransferTransferState.Completed &&
            receiver.Snapshot.InboundState == FileTransferTransferState.Completed);
    }

    private static string InvalidStateErrorCode() => "invalid_state";

    private static string FileSizeMismatchErrorCode() => FileTransferResultCodes.SizeMismatch;

    private static string HashMismatchErrorCode() => FileTransferResultCodes.IntegrityMismatch;

    private static void AssertContainsOrderedSubsequence(
        IReadOnlyList<FileTransferTransferState> actual,
        params FileTransferTransferState[] expected)
    {
        var actualIndex = 0;
        foreach (var expectedState in expected)
        {
            while (actualIndex < actual.Count && actual[actualIndex] != expectedState)
            {
                actualIndex++;
            }

            Assert.True(actualIndex < actual.Count, $"Expected state '{expectedState}' was not observed. Actual: {string.Join(", ", actual)}");
            actualIndex++;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (!condition())
        {
            await Task.Delay(25, cts.Token);
        }
    }

    private static FileTransferReceiveDestination CreateTempReceiveDestination(
        string finalPath,
        Func<CancellationToken, Task>? beforeMoveAsync = null)
    {
        var directoryPath = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException("Final path must include a directory.");
        Directory.CreateDirectory(directoryPath);

        var tempPath = finalPath + ".part";
        var preserveTempArtifact = false;
        var stream = new FileStream(
            tempPath,
            new FileStreamOptions
            {
                Access = FileAccess.Write,
                Mode = FileMode.Create,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });

        return new FileTransferReceiveDestination(
            stream,
            async ct =>
            {
                await stream.FlushAsync(ct).ConfigureAwait(false);
                await stream.DisposeAsync().ConfigureAwait(false);
                if (beforeMoveAsync is not null)
                {
                    await beforeMoveAsync(ct).ConfigureAwait(false);
                }

                try
                {
                    File.Move(tempPath, finalPath);
                }
                catch
                {
                    preserveTempArtifact = true;
                    throw;
                }
            },
            finalPath: finalPath,
            safeFileName: Path.GetFileName(finalPath),
            dispose: () =>
            {
                try
                {
                    stream.Dispose();
                }
                finally
                {
                    if (!preserveTempArtifact && File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
            },
            disposeAsync: async () =>
            {
                try
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (!preserveTempArtifact && File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
            });
    }

    private static int GetOperationalLogLength()
    {
        return LocalOperationalLog.GetRecentLogText().Length;
    }

    private static string ReadOperationalLogTail(int startIndex)
    {
        var logText = LocalOperationalLog.GetRecentLogText();
        if (startIndex <= 0 || startIndex >= logText.Length)
        {
            return startIndex >= logText.Length ? string.Empty : logText;
        }

        return logText[startIndex..];
    }

    private sealed class LoopbackFileTransferTransport : IFileTransferSignalingTransport, ISignalingTransport
    {
        private readonly string sessionId;
        private LoopbackFileTransferTransport? peer;

        public LoopbackFileTransferTransport(string sessionId)
        {
            this.sessionId = sessionId;
        }

        public Func<FileTransferStartV1, FileTransferStartV1>? OutboundStartTransform { get; init; }

        public Func<FileTransferChunkV1, FileTransferChunkV1>? OutboundChunkTransform { get; init; }

        public Func<FileTransferChunkV1, CancellationToken, Task>? AfterChunkDeliveredAsync { get; set; }

        public Func<LoopbackFileTransferTransport, FileTransferChunkV1, CancellationToken, Task<bool>>? OutboundChunkDeliveryOverrideAsync { get; set; }

        public Func<FileTransferCompleteV1, CancellationToken, Task>? BeforeCompleteDeliveredAsync { get; set; }

        public List<FileTransferErrorV1> SentErrors { get; } = [];

        public List<FileTransferCompleteV1> SentCompletes { get; } = [];

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<FileTransferOfferReceivedEventArgs>? FileTransferOfferReceived;
        public event EventHandler<FileTransferAcceptReceivedEventArgs>? FileTransferAcceptReceived;
        public event EventHandler<FileTransferDeclineReceivedEventArgs>? FileTransferDeclineReceived;
        public event EventHandler<FileTransferStartReceivedEventArgs>? FileTransferStartReceived;
        public event EventHandler<FileTransferChunkReceivedEventArgs>? FileTransferChunkReceived;
        public event EventHandler<FileTransferCancelReceivedEventArgs>? FileTransferCancelReceived;
        public event EventHandler<FileTransferErrorReceivedEventArgs>? FileTransferErrorReceived;
        public event EventHandler<FileTransferCompleteReceivedEventArgs>? FileTransferCompleteReceived;

        public void Connect(LoopbackFileTransferTransport other)
        {
            peer = other;
            other.peer = this;
        }

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task SendFileTransferOfferAsync(FileTransferOfferV1 message, CancellationToken ct)
            => DeliverAsync(
                message with { SessionId = NormalizeSessionId(message.SessionId) },
                (target, payload) => target.FileTransferOfferReceived?.Invoke(target, new FileTransferOfferReceivedEventArgs(payload, "loopback-peer")),
                ct);

        public Task SendFileTransferAcceptAsync(FileTransferAcceptV1 message, CancellationToken ct)
            => DeliverAsync(
                message with { SessionId = NormalizeSessionId(message.SessionId) },
                (target, payload) => target.FileTransferAcceptReceived?.Invoke(target, new FileTransferAcceptReceivedEventArgs(payload, "loopback-peer")),
                ct);

        public Task SendFileTransferDeclineAsync(FileTransferDeclineV1 message, CancellationToken ct)
            => DeliverAsync(
                message with { SessionId = NormalizeSessionId(message.SessionId) },
                (target, payload) => target.FileTransferDeclineReceived?.Invoke(target, new FileTransferDeclineReceivedEventArgs(payload, "loopback-peer")),
                ct);

        public Task SendFileTransferStartAsync(FileTransferStartV1 message, CancellationToken ct)
            => DeliverAsync(
                ApplyStartTransform(message with { SessionId = NormalizeSessionId(message.SessionId) }),
                (target, payload) => target.FileTransferStartReceived?.Invoke(target, new FileTransferStartReceivedEventArgs(payload, "loopback-peer")),
                ct);

        public async Task SendFileTransferChunkAsync(FileTransferChunkV1 message, CancellationToken ct)
        {
            var payload = ApplyChunkTransform(message with { SessionId = NormalizeSessionId(message.SessionId) });
            var target = peer ?? throw new InvalidOperationException("Loopback peer is not connected.");
            var handled = false;
            if (OutboundChunkDeliveryOverrideAsync is not null)
            {
                handled = await OutboundChunkDeliveryOverrideAsync(target, payload, ct);
            }

            if (!handled)
            {
                DeliverChunkToPeer(payload);
            }

            if (AfterChunkDeliveredAsync is not null)
            {
                await AfterChunkDeliveredAsync(payload, ct);
            }
        }

        public Task SendFileTransferCancelAsync(FileTransferCancelV1 message, CancellationToken ct)
            => DeliverAsync(
                message with { SessionId = NormalizeSessionId(message.SessionId) },
                (target, payload) => target.FileTransferCancelReceived?.Invoke(target, new FileTransferCancelReceivedEventArgs(payload, "loopback-peer")),
                ct);

        public Task SendFileTransferErrorAsync(FileTransferErrorV1 message, CancellationToken ct)
            => DeliverAsync(
                TrackError(message with { SessionId = NormalizeSessionId(message.SessionId) }),
                (target, payload) => target.FileTransferErrorReceived?.Invoke(target, new FileTransferErrorReceivedEventArgs(payload, "loopback-peer")),
                ct);

        public async Task SendFileTransferCompleteAsync(FileTransferCompleteV1 message, CancellationToken ct)
        {
            var payload = TrackComplete(message with { SessionId = NormalizeSessionId(message.SessionId) });
            if (BeforeCompleteDeliveredAsync is not null)
            {
                await BeforeCompleteDeliveredAsync(payload, ct);
            }

            await DeliverAsync(
                payload,
                (target, deliveredPayload) => target.FileTransferCompleteReceived?.Invoke(target, new FileTransferCompleteReceivedEventArgs(deliveredPayload, "loopback-peer")),
                ct);
        }

        public void Dispose()
        {
        }

        public void RaiseDisconnected()
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
            peer?.Disconnected?.Invoke(peer, EventArgs.Empty);
        }

        public void DeliverChunkToPeer(FileTransferChunkV1 payload)
        {
            var target = peer ?? throw new InvalidOperationException("Loopback peer is not connected.");
            target.ReceiveDeliveredChunk(payload);
        }

        public void ReceiveDeliveredChunk(FileTransferChunkV1 payload)
        {
            FileTransferChunkReceived?.Invoke(this, new FileTransferChunkReceivedEventArgs(payload, "loopback-peer"));
        }

        private Task DeliverAsync<TPayload>(
            TPayload payload,
            Action<LoopbackFileTransferTransport, TPayload> deliver,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var target = peer ?? throw new InvalidOperationException("Loopback peer is not connected.");
            deliver(target, payload);
            return Task.CompletedTask;
        }

        private string NormalizeSessionId(string? sessionId)
            => string.IsNullOrWhiteSpace(sessionId) ? this.sessionId : sessionId.Trim();

        private FileTransferStartV1 ApplyStartTransform(FileTransferStartV1 message)
            => OutboundStartTransform?.Invoke(message) ?? message;

        private FileTransferChunkV1 ApplyChunkTransform(FileTransferChunkV1 message)
            => OutboundChunkTransform?.Invoke(message) ?? message;

        private FileTransferErrorV1 TrackError(FileTransferErrorV1 message)
        {
            SentErrors.Add(message);
            return message;
        }

        private FileTransferCompleteV1 TrackComplete(FileTransferCompleteV1 message)
        {
            SentCompletes.Add(message);
            return message;
        }
    }

    private class NonDisposingMemoryStream : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
        }
    }

    private sealed class DelayedWriteMemoryStream : NonDisposingMemoryStream
    {
        private readonly int delayMilliseconds;

        public DelayedWriteMemoryStream(int delayMilliseconds)
        {
            this.delayMilliseconds = delayMilliseconds;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            await base.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
    }
}
