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
public sealed class SessionFileTransferPullLifecycleTests : SessionFileTransferServiceTestBase
{
    [Fact]
    public async Task PullSession_SingleTimeout_CompletesWithoutStallingFirstChunk()
    {
        const string transferId = "transfer_service_pull_single_timeout";
        var payload = Enumerable.Range(0, 48_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_single_timeout");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_single_timeout");
        senderTransport.Connect(receiverTransport);
        var delayedChunkZeroFrames = new ConcurrentQueue<FileTransferChunkDataFrameV2>();
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (target, frame, ct) =>
        {
            if (frame is FileTransferChunkDataFrameV2 chunk && chunk.ChunkIndex == 0 && delayedChunkZeroFrames.IsEmpty)
            {
                delayedChunkZeroFrames.Enqueue(chunk);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(3500, ct).ConfigureAwait(false);
                        target.ReceiveDeliveredDataFrame(chunk);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }, CancellationToken.None);
                return true;
            }

            return false;
        };
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var logStart = GetOperationalLogLength();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-single-timeout.bin", payload.Length, transferId, ChunkSizeBytes: 4096), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 12000);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Equal(payload, destination.ToArray());
        Assert.Contains("event=filetransfer_request_timeout_detected", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("error_code=pull_session_stalled", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullSession_HealthyTransfer_CompletesStartupPhase_WithoutRepeatedStartupResendNoise()
    {
        const string transferId = "transfer_service_pull_startup_resend_noise";
        var payload = Enumerable.Range(0, 96_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_startup_resend_noise");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_startup_resend_noise");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var logStart = GetOperationalLogLength();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-startup-resend.bin", payload.Length, transferId, ChunkSizeBytes: 4096), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 12000);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Equal(payload, destination.ToArray());
        Assert.Contains("event=window_startup_completed", logTail, StringComparison.Ordinal);
        Assert.True(Regex.Matches(logTail, "event=window_update_sent;.*reason=startup_resend", RegexOptions.CultureInvariant).Count <= 1, "Expected startup resend logging to stop after healthy pull-session progress was established.");
    }

    [Fact]
    public async Task PullSession_SessionOpenArrivingBeforeStart_StillCompletesTransfer()
    {
        const string transferId = "transfer_service_session_open_before_start";
        var payload = Enumerable.Range(0, 96_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_session_open_before_start");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_session_open_before_start");
        senderTransport.Connect(receiverTransport);
        FileTransferStartV2? delayedStart = null;
        var sessionOpenDelivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        senderTransport.OutboundStartDeliveryOverrideAsync = (_, message, _) =>
        {
            delayedStart = message;
            return Task.FromResult(true);
        };
        senderTransport.OutboundSessionOpenDeliveryOverrideAsync = (target, message, ct) =>
        {
            target.ReceiveDeliveredSessionOpen(message);
            sessionOpenDelivered.TrySetResult(true);
            return Task.FromResult(true);
        };
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("session-open-before-start.bin", payload.Length, transferId, ChunkSizeBytes: 4096), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await sessionOpenDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FileTransferTransferState.AwaitingMetadata, receiver.Snapshot.Inbound?.State);
        Assert.NotNull(delayedStart);
        receiverTransport.ReceiveDeliveredStart(delayedStart!);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 12000);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task PullSession_RepeatedTimeouts_EnterDegradedMode_AndRecoverAtPipelineThreeOrHigher()
    {
        const string transferId = "transfer_service_pull_repeated_timeout";
        var payload = Enumerable.Range(0, 64_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_repeated_timeout");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_repeated_timeout");
        senderTransport.Connect(receiverTransport);
        var heldChunks = new ConcurrentQueue<FileTransferChunkDataFrameV2>();
        var holdChunks = 1;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, ct) =>
        {
            if (frame is FileTransferChunkDataFrameV2 chunk && Volatile.Read(ref holdChunks) != 0)
            {
                heldChunks.Enqueue(chunk);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var logStart = GetOperationalLogLength();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-repeated-timeout.bin", payload.Length, transferId, ChunkSizeBytes: 4096), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() =>
        {
            var logTail = ReadOperationalLogTail(logStart);
            return logTail.Contains($"event=filetransfer_session_degraded_entered; transfer_id={transferId}", StringComparison.Ordinal) && logTail.Contains($"event=filetransfer_request_timeout_detected; transfer_id={transferId}", StringComparison.Ordinal);
        }, timeoutMs: 12000);
        Interlocked.Exchange(ref holdChunks, 0);
        while (heldChunks.TryDequeue(out var delayedChunk))
        {
            receiverTransport.ReceiveDeliveredDataFrame(delayedChunk);
        }

        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 15000);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Equal(payload, destination.ToArray());
        Assert.Contains($"event=filetransfer_session_degraded_entered; transfer_id={transferId}", logTail, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_pipeline_changed; direction=Inbound; transfer_id={transferId}", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutboundProgress_ReportsSentAndAcknowledgedBytesSeparately()
    {
        const string transferId = "transfer_service_ack_progress";
        var payload = Enumerable.Range(0, 200_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_ack_progress");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_ack_progress");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new DelayedWriteMemoryStream(delayMilliseconds: 20);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("ack-progress.bin", payload.Length, transferId, ChunkSizeBytes: 4096), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() =>
        {
            var snapshot = sender.Snapshot.Outbound;
            return snapshot is not null && snapshot.BytesAcceptedForTransport.HasValue && snapshot.BytesAcknowledgedByReceiver.HasValue && snapshot.BytesAcceptedForTransport.Value > snapshot.BytesAcknowledgedByReceiver.Value;
        });
        var outbound = sender.Snapshot.Outbound!;
        Assert.Contains(outbound.State, [FileTransferTransferState.Sending, FileTransferTransferState.AwaitingCompletion]);
        Assert.Equal(outbound.BytesAcknowledgedByReceiver, outbound.BytesTransferred);
        Assert.NotNull(outbound.BytesAcceptedForTransport);
        Assert.NotNull(outbound.BytesAcknowledgedByReceiver);
        Assert.True(outbound.BytesAcceptedForTransport > outbound.BytesAcknowledgedByReceiver);
        Assert.True(outbound.ProgressFraction >= (double)outbound.BytesTransferred / payload.Length);
        Assert.Null(receiver.Snapshot.Inbound!.BytesAcceptedForTransport);
        Assert.Null(receiver.Snapshot.Inbound.BytesAcknowledgedByReceiver);
    }

    [Fact(Skip = "Obsolete internal pressure-state coverage after file-transfer pipeline refactors.")]
    public async Task CatchUpOnlyPressure_ClampsSequentialSend_UntilNormalPressureRevisionArrives()
    {
        const string transferId = "transfer_service_pressure_block";
        const int chunkSizeBytes = 1024;
        var payload = Enumerable.Range(0, chunkSizeBytes * 300).Select(static i => (byte)(i % 251)).ToArray();
        var sentChunkIndices = new ConcurrentQueue<int>();
        var logStartIndex = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pressure_block")
        {
            OutboundChunkDeliveryOverrideAsync = (_, message, _) =>
            {
                sentChunkIndices.Enqueue(message.ChunkIndex);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pressure_block");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pressure-block.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()), CancellationToken.None);
        await WaitUntilAsync(() => sentChunkIndices.Count >= 32, timeoutMs: 5000);
        senderTransport.ReceiveDeliveredPressureState(new FileTransferPressureStateV1 { SessionId = "session_service_pressure_block", TransferId = transferId, Revision = 1, Mode = FileTransferProtocol.PressureModeCatchUpOnly, SuggestedSendAheadChunks = 2, ReceiverNextExpectedChunkIndex = 32, Reason = FileTransferProtocol.PressureReasonBulkBacklog, });
        await Task.Delay(500);
        Assert.DoesNotContain(40, sentChunkIndices);
        Assert.True(sender.IsCatchUpOnlyPressureActive);
        senderTransport.ReceiveDeliveredPressureState(new FileTransferPressureStateV1 { SessionId = "session_service_pressure_block", TransferId = transferId, Revision = 1, Mode = FileTransferProtocol.PressureModeNormal, SuggestedSendAheadChunks = 32, ReceiverNextExpectedChunkIndex = 40, Reason = FileTransferProtocol.PressureReasonBulkBacklog, });
        await Task.Delay(250);
        Assert.DoesNotContain(40, sentChunkIndices);
        Assert.True(sender.IsCatchUpOnlyPressureActive);
        senderTransport.ReceiveDeliveredPressureState(new FileTransferPressureStateV1 { SessionId = "session_service_pressure_block", TransferId = transferId, Revision = 2, Mode = FileTransferProtocol.PressureModeNormal, SuggestedSendAheadChunks = 32, ReceiverNextExpectedChunkIndex = 40, Reason = FileTransferProtocol.PressureReasonBulkBacklog, });
        await WaitUntilAsync(() => sentChunkIndices.Contains(40), timeoutMs: 5000);
        Assert.False(sender.IsCatchUpOnlyPressureActive);
        var logTail = ReadOperationalLogTail(logStartIndex);
        Assert.Contains("event=sequential_send_blocked_by_pressure", logTail, StringComparison.Ordinal);
        Assert.Contains("event=pressure_state_received", logTail, StringComparison.Ordinal);
        await sender.CancelTransferAsync(transferId, "test_done", CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Canceled && receiver.Snapshot.InboundState == FileTransferTransferState.Canceled);
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
        var firstStart = await sender.TryStartSendAsync(new FileTransferSendDescriptor("first.bin", payload.Length, firstTransferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        Assert.NotNull(firstStart);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        var secondStart = await sender.TryStartSendAsync(new FileTransferSendDescriptor("second.bin", payload.Length, secondTransferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        Assert.Null(secondStart);
        Assert.Equal(firstTransferId, sender.Snapshot.Outbound!.TransferId);
        Assert.Equal(FileTransferTransferState.AwaitingAcceptance, sender.Snapshot.Outbound.State);
    }

}
