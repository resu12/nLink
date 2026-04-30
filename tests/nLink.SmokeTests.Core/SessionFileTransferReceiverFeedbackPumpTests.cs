using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferReceiverFeedbackPumpTests : SessionFileTransferServiceTestBase
{
    [Fact]
    public async Task V3NknFileOnlySparse_DelayedFeedbackSend_DoesNotBlockReceiveLoop()
    {
        var previousPump = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_RECEIVER_FEEDBACK_PUMP");
        var previousFixedWindow = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES");
        var previousPayloadProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_RECEIVER_FEEDBACK_PUMP", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES", (16 * 1024 * 1024).ToString());
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "Current");
        try
        {
            const string sessionId = "session_receiver_feedback_pump_nonblocking";
            const string transferId = "transfer_receiver_feedback_pump_nonblocking";
            var payload = Enumerable.Range(0, 4 * 1024 * 1024).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport(sessionId)
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            senderTransport.Connect(receiverTransport);

            var feedbackSendCount = 0;
            var delayedFeedbackStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDelayedFeedback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiverTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
            {
                if (frame is FileTransferGrantWindowFrameV3 or FileTransferAckProgressFrameV3)
                {
                    var count = Interlocked.Increment(ref feedbackSendCount);
                    if (count == 2)
                    {
                        delayedFeedbackStarted.TrySetResult(true);
                        await releaseDelayedFeedback.Task.WaitAsync(ct);
                    }
                }

                return false;
            };

            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            var logStart = GetOperationalLogLength();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("receiver-feedback-pump-nonblocking.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            using var destination = new NonDisposingMemoryStream();
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

            await delayedFeedbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var delayedLogOffset = GetOperationalLogLength();
            await WaitUntilAsync(
                () => CountOccurrences(ReadOperationalLogTail(delayedLogOffset), "event=filetransfer_chunk_received") >= 2,
                timeoutMs: 5000);

            releaseDelayedFeedback.TrySetResult(true);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 30000);

            Assert.Equal(payload, destination.ToArray());
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v3_receiver_feedback_pump_started", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v3_receiver_feedback_enqueued", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v3_receiver_feedback_sent", logTail, StringComparison.Ordinal);
            Assert.Contains("mode=pump", logTail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_RECEIVER_FEEDBACK_PUMP", previousPump);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES", previousFixedWindow);
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousPayloadProfile);
        }
    }

    [Fact]
    public async Task V3NknFileOnlySparse_CompletionWaitsForQueuedCompleteFrame()
    {
        var previousPump = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_RECEIVER_FEEDBACK_PUMP");
        var previousFixedWindow = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_RECEIVER_FEEDBACK_PUMP", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES", (16 * 1024 * 1024).ToString());
        try
        {
            const string sessionId = "session_receiver_feedback_pump_complete";
            const string transferId = "transfer_receiver_feedback_pump_complete";
            var payload = Enumerable.Range(0, 512 * 1024).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport(sessionId)
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            senderTransport.Connect(receiverTransport);

            var completeSendStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCompleteSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            receiverTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
            {
                if (frame is FileTransferCompleteFrameV2)
                {
                    completeSendStarted.TrySetResult(true);
                    await releaseCompleteSend.Task.WaitAsync(ct);
                }

                return false;
            };

            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            var logStart = GetOperationalLogLength();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("receiver-feedback-pump-complete.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            using var destination = new NonDisposingMemoryStream();
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

            await completeSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(FileTransferTransferState.Completed, receiver.Snapshot.Inbound?.State);

            releaseCompleteSend.TrySetResult(true);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 30000);

            Assert.Equal(payload, destination.ToArray());
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("frame_type=filetransfer.session_complete.v2", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=complete", logTail, StringComparison.Ordinal);
            Assert.Contains("mode=pump", logTail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_RECEIVER_FEEDBACK_PUMP", previousPump);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FIXED_FILE_ONLY_WINDOW_BYTES", previousFixedWindow);
        }
    }

    [Fact]
    public async Task V3NknFileOnlySparse_FeedbackPumpRollback_UsesDirectSendPath()
    {
        var previousPump = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_RECEIVER_FEEDBACK_PUMP");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_RECEIVER_FEEDBACK_PUMP", "0");
        try
        {
            const string sessionId = "session_receiver_feedback_pump_rollback";
            const string transferId = "transfer_receiver_feedback_pump_rollback";
            var payload = Enumerable.Range(0, 256 * 1024).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport(sessionId)
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            var logStart = GetOperationalLogLength();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("receiver-feedback-pump-rollback.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            using var destination = new NonDisposingMemoryStream();
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 20000);

            Assert.Equal(payload, destination.ToArray());
            var logTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain("event=filetransfer_v3_receiver_feedback_pump_started", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v3_receiver_feedback_sent", logTail, StringComparison.Ordinal);
            Assert.Contains("mode=direct", logTail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_RECEIVER_FEEDBACK_PUMP", previousPump);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
