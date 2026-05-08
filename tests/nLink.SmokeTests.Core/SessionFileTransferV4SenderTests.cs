using System.Reflection;
using System.Security.Cryptography;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferV4SenderTests : SessionFileTransferServiceTestBase
{
    [Fact]
    public async Task V4FileOnlyTransfer_CompletesEndToEnd_WithSparseReceiverIntegrity()
    {
        const string transferId = "transfer_v4_sender_e2e";
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_e2e");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_e2e");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-e2e.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 15000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var sessionOpen = Assert.Single(senderTransport.SentSessionOpens);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV5, sessionOpen.ProtocolVersion);
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferManifestFrameV5);
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferChunkBatchFrameV5);
        Assert.Contains(receiverTransport.SentDataFrames, static frame => frame is FileTransferStateFrameV5);
        Assert.Contains(receiverTransport.SentDataFrames, static frame => frame is FileTransferCompleteFrameV5);
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is not FileTransferStateFrameV5 and not FileTransferCompleteFrameV5);
    }

    [Fact]
    public async Task V4Sender_LocalCancel_RetriesControlCancelWhenFirstSignalIsLost()
    {
        const string transferId = "transfer_v4_sender_cancel_retry";
        var payload = Enumerable.Range(0, 768_000).Select(static index => (byte)(index % 251)).ToArray();
        var cancelControlAttempts = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_cancel_retry");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_cancel_retry");
        senderTransport.DataSessionSendDelayMs = 10;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(frame is FileTransferCancelFrameV5);
        senderTransport.OutboundCancelDeliveryOverrideAsync = (_, _, _) =>
        {
            var attempt = Interlocked.Increment(ref cancelControlAttempts);
            return Task.FromResult(attempt == 1);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-cancel-retry.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Any(),
            timeoutMs: 5000);

        var canceled = await sender.CancelTransferAsync(transferId, "user_canceled", CancellationToken.None);

        Assert.NotNull(canceled);
        Assert.Equal(FileTransferTransferState.Canceled, canceled!.State);
        Assert.Equal(FileTransferResultCodes.CanceledLocal, canceled.ErrorCode);
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferCancelFrameV5);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 6000);
        Assert.True(cancelControlAttempts >= 2);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);
    }

    [Fact]
    public async Task V5Sender_LocalCancel_IgnoresCanceledCallerTokenAndUsesPriorityControl()
    {
        const string transferId = "transfer_v5_sender_cancel_priority_token";
        var payload = Enumerable.Range(0, 1_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v5_sender_cancel_priority_token");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v5_sender_cancel_priority_token");
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(frame is FileTransferCancelFrameV5);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v5-cancel-priority-token.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Any(),
            timeoutMs: 5000);

        using var canceledToken = new CancellationTokenSource();
        canceledToken.Cancel();
        var canceled = await sender.CancelTransferAsync(transferId, "user_canceled", canceledToken.Token);

        Assert.NotNull(canceled);
        Assert.Equal(FileTransferTransferState.Canceled, canceled!.State);
        Assert.Equal(FileTransferResultCodes.CanceledLocal, canceled.ErrorCode);
        Assert.Contains(senderTransport.SentCancels, cancel => string.Equals(cancel.TransferId, transferId, StringComparison.Ordinal));
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 6000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);

        var logText = ReadOperationalLogText();
        Assert.Contains("event=filetransfer_lifecycle_priority_sent; kind=cancel", logText, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_lifecycle_priority_received; kind=cancel", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V5Receiver_RemoteCancelBypassesBlockedLifecycleTail()
    {
        const string transferId = "transfer_v5_cancel_bypasses_lifecycle";
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v5_cancel_bypasses_lifecycle");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v5_cancel_bypasses_lifecycle");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v5-cancel-bypasses-lifecycle.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Receiving,
            timeoutMs: 5000);

        var releaseLifecycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.InboundDispatchBeforeWorkAsyncForTests = (lane, operation) =>
            lane == "lifecycle" && operation == "complete"
                ? releaseLifecycle.Task
                : Task.CompletedTask;

        await senderTransport.SendFileTransferCompleteAsync(
            new FileTransferCompleteV1
            {
                SessionId = "session_v5_cancel_bypasses_lifecycle",
                TransferId = transferId,
                FileSizeBytes = payload.Length,
                Sha256Base64 = Convert.ToBase64String(SHA256.HashData(payload)),
            },
            CancellationToken.None);

        var canceled = await sender.CancelTransferAsync(transferId, "user_canceled", CancellationToken.None);

        Assert.NotNull(canceled);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 3000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);

        releaseLifecycle.SetResult();
    }

    [Fact]
    public async Task V4Sender_SessionEndCancel_NotifiesReceiverWhileTransferIsActive()
    {
        const string transferId = "transfer_v4_sender_session_end_cancel";
        var payload = Enumerable.Range(0, 5_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_session_end_cancel");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_session_end_cancel");
        senderTransport.DataSessionSendDelayMs = 20;
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-session-end-cancel.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Any(),
            timeoutMs: 5000);

        var canceledCount = await sender.CancelActiveTransfersForSessionEndAsync("session_end", CancellationToken.None);

        Assert.Equal(1, canceledCount);
        Assert.Equal(FileTransferTransferState.Canceled, sender.Snapshot.Outbound?.State);
        Assert.Equal(FileTransferResultCodes.CanceledLocal, sender.Snapshot.Outbound?.ErrorCode);
        Assert.Contains(senderTransport.SentCancels, cancel => string.Equals(cancel.Reason, "session_end", StringComparison.Ordinal));
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferCancelFrameV5);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 6000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);
    }

    [Fact]
    public async Task V4Sender_PeerSilence_TerminalsInsteadOfSendingForever()
    {
        const string transferId = "transfer_v4_sender_peer_silence";
        var previousTimeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromMilliseconds(300);
        try
        {
            var payload = Enumerable.Range(0, 5_000_000).Select(static index => (byte)(index % 251)).ToArray();
            var receiverPeerFrameCount = 0;
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_peer_silence");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_peer_silence");
            receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            {
                if (frame is FileTransferStateFrameV5 &&
                    Interlocked.Increment(ref receiverPeerFrameCount) == 1)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            using var destination = new NonDisposingMemoryStream();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-sender-peer-silence.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                      senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Any(),
                timeoutMs: 5000);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Failed, timeoutMs: 5000);

            Assert.Equal(FileTransferResultCodes.TransportDisconnected, sender.Snapshot.Outbound!.ErrorCode);
            Assert.Contains("Receiver stopped responding", sender.Snapshot.Outbound.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousTimeout;
        }
    }

    [Fact]
    public async Task V4Sender_WithScreenshare_FailsCleanlyWhenMixedDisabledByEnvironment()
    {
        const string transferId = "transfer_v4_sender_mixed_disabled";
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "0");
        try
        {
            var payload = Enumerable.Range(0, 64_000).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_disabled");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_disabled");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            sender.SetSessionScreenShareActive(true);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-mixed-disabled.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
            var offer = senderTransport.SentOffers.Single();
            await receiverTransport.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
                },
                CancellationToken.None);

            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Failed, timeoutMs: 5000);
            Assert.Equal(FileTransferResultCodes.V4FileOnlyRequired, sender.Snapshot.Outbound?.ErrorCode);
            Assert.DoesNotContain(senderTransport.SentDataFrames, static frame => frame is FileTransferManifestFrameV5);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact]
    public async Task V4Sender_WithScreenshareAndMixedFlag_CompletesWithTwoChunkNormalBatches()
    {
        const string transferId = "transfer_v4_sender_mixed_flag_on";
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "1");
        try
        {
            var logStart = ReadOperationalLogText().Length;
            var payload = Enumerable.Range(0, 192_000).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_flag_on");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_flag_on");
            senderTransport.DataSessionSendDelayMs = 25;
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            sender.SetSessionScreenShareActive(true);
            receiver.SetSessionScreenShareActive(true);
            using var destination = new NonDisposingMemoryStream();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-mixed-flag-on.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.IsV4MixedScreenShareTransferActive &&
                      receiver.IsV4MixedScreenShareTransferActive,
                timeoutMs: 5000);

            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 15000);

            Assert.Equal(payload, destination.ToArray()[..payload.Length]);
            var normalBatches = senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV5>()
                .Where(static batch => batch.BatchProfile != "v4_repair_21k")
                .ToArray();
            Assert.NotEmpty(normalBatches);
            Assert.All(normalBatches, static batch =>
            {
                Assert.InRange(batch.DataSegments.Count, 1, 2);
                Assert.Equal("v4_default_21k_2x", batch.BatchProfile);
            });
            Assert.Contains(normalBatches, static batch => batch.DataSegments.Count == 2);
            Assert.Contains(
                normalBatches,
                static batch => batch.DataSegments.Count == 2 &&
                    batch.DataSegments[0].Length == 21 * 1024 &&
                    batch.DataSegments[1].Length == 21 * 1024);

            var log = ReadOperationalLogTail(logStart);
            Assert.Contains($"event=filetransfer_v5_mixed_enabled; transfer_id={transferId}", log, StringComparison.Ordinal);
            Assert.Contains("normal_batch_segments=2", log, StringComparison.Ordinal);
            Assert.Contains("mixed_screenshare=1", log, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact]
    public async Task V4Sender_WithMixedFlag_LatchesMixedPacingAfterScreenshareWasObserved()
    {
        const string transferId = "transfer_v4_sender_mixed_latched";
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "1");
        try
        {
            var logStart = ReadOperationalLogText().Length;
            var payload = Enumerable.Range(0, 128_000).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_latched");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_latched");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            sender.SetSessionScreenShareActive(true);
            receiver.SetSessionScreenShareActive(true);
            sender.SetSessionScreenShareActive(false);
            receiver.SetSessionScreenShareActive(false);
            using var destination = new NonDisposingMemoryStream();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-mixed-latched.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 15000);

            Assert.Equal(payload, destination.ToArray()[..payload.Length]);
            var normalBatches = senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV5>()
                .Where(static batch => batch.BatchProfile != "v4_repair_21k")
                .ToArray();
            Assert.NotEmpty(normalBatches);
            Assert.All(normalBatches, static batch =>
            {
                Assert.InRange(batch.DataSegments.Count, 1, 2);
                Assert.Equal("v4_default_21k_2x", batch.BatchProfile);
            });
            Assert.Contains(normalBatches, static batch => batch.DataSegments.Count == 2);

            var log = ReadOperationalLogTail(logStart);
            Assert.Contains($"event=filetransfer_v5_mixed_enabled; transfer_id={transferId}", log, StringComparison.Ordinal);
            Assert.Contains("screen_share_active=0", log, StringComparison.Ordinal);
            Assert.Contains("screen_share_observed=1", log, StringComparison.Ordinal);
            Assert.Contains("normal_batch_segments=2", log, StringComparison.Ordinal);
            Assert.Contains("mixed_screenshare=1", log, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact]
    public async Task V4Sender_WithDegradedScreenshareAndMixedFlag_UsesTwoChunkNormalBatches()
    {
        const string transferId = "transfer_v4_sender_mixed_degraded";
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "1");
        try
        {
            var logStart = ReadOperationalLogText().Length;
            var payload = Enumerable.Range(0, 128_000).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_degraded");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_degraded");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            sender.SetSessionScreenShareActive(true);
            receiver.SetSessionScreenShareActive(true);
            sender.SetSessionScreenShareDegraded(true);
            receiver.SetSessionScreenShareDegraded(true);
            using var destination = new NonDisposingMemoryStream();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-mixed-degraded.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 15000);

            Assert.Equal(payload, destination.ToArray()[..payload.Length]);
            var normalBatches = senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV5>()
                .Where(static batch => batch.BatchProfile != "v4_repair_21k")
                .ToArray();
            Assert.NotEmpty(normalBatches);
            Assert.All(normalBatches, static batch =>
            {
                Assert.InRange(batch.DataSegments.Count, 1, 2);
                Assert.Equal("v4_default_21k_2x", batch.BatchProfile);
            });
            Assert.Contains(normalBatches, static batch => batch.DataSegments.Count == 2);

            var log = ReadOperationalLogTail(logStart);
            Assert.Contains($"event=filetransfer_v5_mixed_enabled; transfer_id={transferId}", log, StringComparison.Ordinal);
            Assert.Contains("screen_share_degraded=1", log, StringComparison.Ordinal);
            Assert.Contains("normal_batch_segments=2", log, StringComparison.Ordinal);
            Assert.Contains("mixed_screenshare=1", log, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact]
    public async Task V4Sender_MissingRangesSchedulePriorityRepair_AndComplete()
    {
        const string transferId = "transfer_v4_sender_repair_priority";
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 239)).ToArray();
        var droppedFirstBatch = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_priority");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_priority");
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, _) =>
        {
            if (frame is FileTransferChunkBatchFrameV5 { StartChunkIndex: 0 } &&
                Interlocked.Exchange(ref droppedFirstBatch, 1) == 0)
            {
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-repair-priority.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(1, droppedFirstBatch);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        Assert.True(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Count(static batch => batch.StartChunkIndex == 0) >= 2);
        Assert.Contains(
            senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>(),
            static batch => batch.StartChunkIndex == 0 &&
                batch.BatchProfile == "v4_repair_21k" &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.BulkOnly);
        await WaitUntilAsync(
            () => LocalOperationalLog.GetRecentLogText().Contains($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", StringComparison.Ordinal),
            timeoutMs: 5000);
        var log = LocalOperationalLog.GetRecentLogText();
        Assert.Contains($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_v4_repair_sent; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_v4_chunk_batch_sent; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_mode=bulk_only", log, StringComparison.Ordinal);
        Assert.Contains("repair_request_key=", log, StringComparison.Ordinal);
        Assert.Contains("repair_send=1", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_TransportRebind_QueuesBoundedSafetyReplayFromPeerFrontier()
    {
        const string transferId = "transfer_v4_sender_transport_rebind_replay";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_transport_rebind_replay");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_transport_rebind_replay");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-transport-rebind-replay.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV5>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Count(static batch => batch.StartChunkIndex == 0) == 1,
            timeoutMs: 5000);

        var batchCountBeforeRebind = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Count();
        senderTransport.RaiseDisconnected();
        senderTransport.RaiseReconnected();

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_transport_rebind_safety_replay_started;",
                StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV5>()
                .Skip(batchCountBeforeRebind)
                .Any(static batch =>
                    batch.StartChunkIndex == 0 &&
                    batch.BatchProfile == "v4_repair_21k" &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_transport_rebind_generation_started; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_safety_replay_started;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v5_tail_blocked_until_frontier_proof; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_mode=control_bulk_escalated", log, StringComparison.Ordinal);
        Assert.Contains("requested_chunk_count=1", log, StringComparison.Ordinal);
        Assert.Contains("replay_chunk_cap=64", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_TransportRebind_RearmsSafetyReplayWhenNoFeedbackArrives()
    {
        const string transferId = "transfer_v4_sender_transport_rebind_rearm";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_transport_rebind_rearm");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_transport_rebind_rearm");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-transport-rebind-rearm.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV5>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Count(static batch => batch.StartChunkIndex == 0) == 1,
            timeoutMs: 5000);

        senderTransport.RaiseDisconnected();
        senderTransport.RaiseReconnected();

        await WaitUntilAsync(
            () => CountOccurrences(
                ReadOperationalLogTail(logStart),
                "event=filetransfer_transport_rebind_safety_replay_started;") >= 2,
            timeoutMs: 9000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_rebind_safety_replay_rearmed;", log, StringComparison.Ordinal);
        Assert.Contains("reason=post_fallback_sender_wait", log, StringComparison.Ordinal);
        Assert.True(
            senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV5>()
                .Count(static batch =>
                    batch.StartChunkIndex == 0 &&
                    batch.BatchProfile == "v4_repair_21k" &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant) >= 2);
    }

    [Fact]
    public async Task V4Sender_PostRebindZeroCredit_EmergencyReplaysExactFrontier()
    {
        const string transferId = "transfer_v4_sender_rebind_zero_credit_emergency";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_rebind_zero_credit_emergency");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_rebind_zero_credit_emergency");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-rebind-zero-credit-emergency.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV5>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 0,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        senderTransport.RaiseDisconnected();
        senderTransport.RaiseReconnected();

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV5>()
                .Any(static batch =>
                    batch.StartChunkIndex == 0 &&
                    batch.ChunkCount == 1 &&
                    batch.BatchProfile == "v4_repair_21k" &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_transport_rebind_emergency_frontier_replay;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_safety_replay_started;", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_escalation_reason=transport_rebind_emergency_frontier", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_PostRebindDuplicateFrontierState_ReplaysOnlyExactFrontier()
    {
        const string transferId = "transfer_v4_sender_rebind_frontier_only";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_rebind_frontier_only");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_rebind_frontier_only");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-rebind-frontier-only.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV5>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Any(static batch => batch.StartChunkIndex == 0), timeoutMs: 5000);

        senderTransport.RaiseDisconnected();
        senderTransport.RaiseReconnected();
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_rebind_generation_started; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);

        var missingFrontierState = new FileTransferStateFrameV5
        {
            SessionId = offer.SessionId,
            TransferId = transferId,
            Epoch = 2,
                ContiguousCommittedChunkIndex = 4,
                DurableReceivedHighestChunkIndex = 16,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 4, ChunkCount = 32 }],
                BytesCommitted = 4 * 21 * 1024,
            };
        await receiverSession.SendAsync(missingFrontierState, CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV5>()
                .Any(static batch =>
                    batch.StartChunkIndex == 4 &&
                    batch.ChunkCount == 1 &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var batchCountBeforeDuplicate = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Count();
        var duplicateStart = ReadOperationalLogText().Length;
        await receiverSession.SendAsync(missingFrontierState, CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(duplicateStart).Contains("event=filetransfer_transport_rebind_frontier_only_replay;", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV5>()
                .Skip(batchCountBeforeDuplicate)
                .Any(static batch =>
                    batch.StartChunkIndex == 4 &&
                    batch.ChunkCount == 1 &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var fullLog = ReadOperationalLogTail(logStart);
        var duplicateLog = ReadOperationalLogTail(duplicateStart);
        Assert.Contains("event=filetransfer_transport_rebind_frontier_only_started;", fullLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_frontier_only_replay;", duplicateLog, StringComparison.Ordinal);
        Assert.Contains("replay_start_chunk_index=4; replay_end_chunk_exclusive=5; requested_chunk_count=1", duplicateLog, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_escalation_reason=transport_rebind_frontier_only", duplicateLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V5Sender_HandoffRecoveryUnblocksTailAndIgnoresStaleRecoveryFrames()
    {
        const string transferId = "transfer_v5_handoff_tail_unblock";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 1_024_000).Select(static index => (byte)(index % 239)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v5_handoff_tail_unblock");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v5_handoff_tail_unblock");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v5-handoff-tail-unblock.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV5>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Any(static batch => batch.StartChunkIndex == 0), timeoutMs: 5000);

        senderTransport.RaiseDisconnected();
        senderTransport.RaiseReconnected();
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_rebind_generation_started; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 8,
                DurableReceivedHighestChunkIndex = 15,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 8 * 21 * 1024,
                TransportEpoch = 1,
            },
            CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 3,
                ContiguousCommittedChunkIndex = 16,
                DurableReceivedHighestChunkIndex = 31,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 16 * 21 * 1024,
                TransportEpoch = 1,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v5_handoff_tail_unblocked; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV5>()
                .Any(static batch =>
                    batch.StartChunkIndex >= 16 &&
                    batch.TransportEpoch == 0 &&
                    batch.RepairRequestId is null &&
                    batch.RecoveryMode is null),
            timeoutMs: 5000);

        var staleStart = ReadOperationalLogText().Length;
        await receiverSession.SendAsync(
            new FileTransferRepairRequestFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                TransportEpoch = 1,
                RepairRequestId = "stale-after-recovered",
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 16, ChunkCount = 1 }],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(staleStart).Contains("reason=recovered_epoch", StringComparison.Ordinal),
            timeoutMs: 5000);

        var staleLog = ReadOperationalLogTail(staleStart);
        Assert.Contains("event=filetransfer_v5_recovery_frame_ignored; direction=outbound;", staleLog, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v5_tail_blocked_until_frontier_proof; direction=outbound;", staleLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V5Sender_NormalToTunaActivationStartsSymmetricHandoffAndBlocksTail()
    {
        const string transferId = "transfer_v5_tuna_activation_handoff";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 1_024_000).Select(static index => (byte)(index % 241)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v5_tuna_activation_handoff");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v5_tuna_activation_handoff");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v5-tuna-activation-handoff.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV5>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 8,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Any(static batch => batch.StartChunkIndex == 0), timeoutMs: 5000);

        var batchCountBeforeHandoff = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Count();
        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_activation_negotiated",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v5_handoff_epoch_started; direction=outbound;", StringComparison.Ordinal) &&
                  ReadOperationalLogTail(logStart).Contains("handoff_kind=normal_to_tuna_activation", StringComparison.Ordinal) &&
                  ReadOperationalLogTail(logStart).Contains("target_transport=tuna", StringComparison.Ordinal),
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 4,
                DurableReceivedHighestChunkIndex = 16,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 4, ChunkCount = 12 }],
                BytesCommitted = 4 * 21 * 1024,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV5>()
                .Skip(batchCountBeforeHandoff)
                .Any(static batch =>
                    batch.StartChunkIndex == 4 &&
                    batch.ChunkCount == 1 &&
                    batch.TransportEpoch > 0 &&
                    string.Equals(batch.RecoveryMode, "frontier_repair_only", StringComparison.Ordinal)),
            timeoutMs: 5000);

        var newBatches = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV5>()
            .Skip(batchCountBeforeHandoff)
            .ToArray();
        Assert.DoesNotContain(
            newBatches,
            static batch => batch.StartChunkIndex > 4 &&
                            batch.RepairRequestId is null &&
                            string.IsNullOrWhiteSpace(batch.RecoveryMode));

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v5_tail_blocked_until_frontier_proof; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("target_transport=tuna", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4FileOnlyTransfer_TransportRebindSafetyReplay_FillsDroppedFrontierAndCompletes()
    {
        const string transferId = "transfer_v4_transport_rebind_e2e";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 768_000).Select(static index => (byte)(index % 229)).ToArray();
        var allowFrontierChunk = 0;
        var droppedFrontierBatchCount = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_transport_rebind_e2e");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_transport_rebind_e2e");
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferChunkBatchFrameV5 { StartChunkIndex: 0 } &&
                Volatile.Read(ref allowFrontierChunk) == 0)
            {
                Interlocked.Increment(ref droppedFrontierBatchCount);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-transport-rebind-e2e.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV5>().Any(static state =>
                state.MissingRanges.Any(static range =>
                    range.StartChunkIndex == 0 &&
                    range.ChunkCount > 0)),
            timeoutMs: 10000);
        Assert.True(Volatile.Read(ref droppedFrontierBatchCount) > 0);

        Volatile.Write(ref allowFrontierChunk, 1);
        senderTransport.RaiseDisconnected();
        senderTransport.RaiseReconnected();

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_transport_rebind_generation_started; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_generation_started; direction=inbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_state_forced; direction=inbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_safety_replay_started;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_recovered; direction=inbound;", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_SuppressesDuplicateMissingRangeRepairKeys()
    {
        const string transferId = "transfer_v4_sender_repair_dedupe";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_dedupe");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_dedupe");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-repair-dedupe.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV5>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        var repairState = new FileTransferStateFrameV5
        {
            SessionId = offer.SessionId,
            TransferId = transferId,
            Epoch = 2,
            ContiguousCommittedChunkIndex = 0,
            DurableReceivedHighestChunkIndex = 8,
            CreditUntilChunkIndexExclusive = 16,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
            BytesCommitted = 0,
        };
        await receiverSession.SendAsync(repairState, CancellationToken.None);
        await receiverSession.SendAsync(repairState, CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_repair_dedupe", StringComparison.Ordinal), timeoutMs: 5000);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_state_received; transfer_id=transfer_v4_sender_repair_dedupe; session_id=session_v4_sender_repair_dedupe; epoch=2; previous_epoch=2; duplicate=1; applied=0", StringComparison.Ordinal), timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Equal(1, CountOccurrences(log, "event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_repair_dedupe"));
        Assert.Contains("duplicate=1; applied=0", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v4_repair_suppressed; transfer_id=transfer_v4_sender_repair_dedupe", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_RetriedMissingRange_EscalatesRepairDelivery()
    {
        const string transferId = "transfer_v4_sender_repair_escalates";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_escalates");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_escalates");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-repair-escalates.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV5>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        var repairState = new FileTransferStateFrameV5
        {
            SessionId = offer.SessionId,
            TransferId = transferId,
            Epoch = 2,
            ContiguousCommittedChunkIndex = 0,
            DurableReceivedHighestChunkIndex = 8,
            CreditUntilChunkIndexExclusive = 16,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
            BytesCommitted = 0,
        };

        await receiverSession.SendAsync(repairState, CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Any(static batch =>
                batch.BatchProfile == "v4_repair_21k" &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.BulkOnly),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("repair_delivery_mode=bulk_only", StringComparison.Ordinal),
            timeoutMs: 5000);

        await Task.Delay(900);
        await receiverSession.SendAsync(repairState with { Epoch = 3 }, CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Any(static batch =>
                batch.BatchProfile == "v4_repair_21k" &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("repair_delivery_mode=bulk_only", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_mode=control_bulk_escalated", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_escalation_reason=frontier_not_advanced", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_MaxBatchSegmentsEnvironment_CapsNormalBatches()
    {
        const string transferId = "transfer_v4_sender_batch_cap";
        const string envName = "NLINK_FILETRANSFER_V4_MAX_BATCH_SEGMENTS";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "2");
        try
        {
            var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 241)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_batch_cap");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_batch_cap");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-batch-cap.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
            var offer = senderTransport.SentOffers.Single();
            await receiverTransport.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
            await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV5>().Any(), timeoutMs: 5000);

            var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
            await receiverSession.SendAsync(
                new FileTransferStateFrameV5
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 16,
                    MissingRanges = [],
                    BytesCommitted = 0,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Sum(static batch => batch.DataSegments.Count) >= 12,
                timeoutMs: 5000);

            var batches = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().ToArray();
            Assert.NotEmpty(batches);
            Assert.All(batches, static batch => Assert.InRange(batch.DataSegments.Count, 1, 2));
            Assert.Contains(batches, static batch => batch.DataSegments.Count == 2 && batch.BatchProfile == "v4_default_21k_2x");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact]
    public async Task V4Sender_SuppressesSameMissingRange_WhenDurableHighestAdvances()
    {
        const string transferId = "transfer_v4_sender_repair_stable_key";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 229)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_stable_key");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_stable_key");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-repair-stable-key.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV5>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_repair_stable_key", StringComparison.Ordinal), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 3,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 12,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_repair_suppressed; transfer_id=transfer_v4_sender_repair_stable_key", StringComparison.Ordinal), timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Equal(1, CountOccurrences(log, "event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_repair_stable_key"));
        Assert.Contains("event=filetransfer_v4_repair_suppressed; transfer_id=transfer_v4_sender_repair_stable_key", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_SuppressesRepair_FromStaleSameFrontierMissingRanges()
    {
        const string transferId = "v4_stale_same";
        const string sessionId = "session_v4_stale_same";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 211)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-stale-missing-range.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV5>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 10,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 9,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains($"event=filetransfer_v4_stale_state_missing_ranges_suppressed; transfer_id={transferId}", StringComparison.Ordinal), timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains($"event=filetransfer_v4_state_received; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("stale=1; applied=0", log, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_v4_stale_state_missing_ranges_suppressed; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("reason=stale_epoch", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_stale_state_missing_ranges_applied; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_TreatsSameEpochReceiverStateAsDuplicate()
    {
        const string transferId = "v4_duplicate_epoch";
        const string sessionId = "session_v4_duplicate_epoch";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 211)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-duplicate-epoch.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV5>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 10,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 10,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 1,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
                TransferPaused = true,
                TransferPauseReason = "duplicate_pause",
            },
            CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id={sessionId}; epoch=10; previous_epoch=10; duplicate=1; applied=0", StringComparison.Ordinal), timeoutMs: 5000);
        await Task.Delay(250);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id={sessionId}; epoch=10; previous_epoch=10; duplicate=1; applied=0", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.False(sender.Snapshot.Outbound!.IsPeerPaused);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_SuppressesRepair_FromStaleMovedFrontierMissingRanges()
    {
        const string transferId = "v4_stale_moved";
        const string sessionId = "session_v4_stale_moved";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 211)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-stale-moved-frontier-missing-range.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV5>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 10,
                ContiguousCommittedChunkIndex = 1,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 21 * 1024,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 9,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains($"event=filetransfer_v4_stale_state_missing_ranges_suppressed; transfer_id={transferId}", StringComparison.Ordinal), timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains($"event=filetransfer_v4_state_received; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("stale=1; applied=0", log, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_v4_stale_state_missing_ranges_suppressed; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("reason=frontier_moved", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_stale_state_missing_ranges_applied; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_PostRebind_SuppressesStaleRepairStateBehindCurrentFrontier()
    {
        const string transferId = "v4_post_rebind_stale_behind";
        const string sessionId = "session_v4_post_rebind_stale_behind";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 211)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-post-rebind-stale-behind.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV5>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        senderTransport.RaiseDisconnected();
        senderTransport.RaiseReconnected();
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_rebind_generation_started; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 4,
                DurableReceivedHighestChunkIndex = 16,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges = [],
                BytesCommitted = 4 * 21 * 1024,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains($"contiguous_committed_chunk_index=4", StringComparison.Ordinal), timeoutMs: 5000);
        var staleStart = ReadOperationalLogText().Length;

        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 16,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(staleStart).Contains($"event=filetransfer_v4_stale_state_missing_ranges_suppressed; transfer_id={transferId}", StringComparison.Ordinal),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(staleStart);
        Assert.Contains($"event=filetransfer_v4_stale_state_missing_ranges_suppressed; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("reason=frontier_moved", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_rebind_duplicate_state_repair_enqueued; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_PrioritizesFrontierMissingRange_AndKeepsFarHoles()
    {
        const string transferId = "transfer_v4_sender_frontier_first";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 223)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_frontier_first");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_frontier_first");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-frontier-first.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV5,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV5>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV5>().Any(static frame =>
                frame.StartChunkIndex <= 10 &&
                frame.StartChunkIndex + frame.ChunkCount > 10),
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferStateFrameV5
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 20,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges =
                [
                    new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 3 },
                    new FileTransferRangeV4 { StartChunkIndex = 10, ChunkCount = 4 },
                ],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_frontier_first", StringComparison.Ordinal), timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_frontier_first", log, StringComparison.Ordinal);
        Assert.Contains("range_count=2; requested_chunk_count=7; scheduled_chunk_count=7", log, StringComparison.Ordinal);
        Assert.Contains("first_start_chunk_index=0; last_end_chunk_exclusive=14", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_PostRebindMissingRanges_SchedulesExactFrontierRepairOnly()
    {
        await Task.CompletedTask;
        var method = typeof(SessionFileTransferService).GetMethod(
            "SelectV4RepairRangesForSend",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var ranges = new[]
        {
            new FileTransferRangeV4 { StartChunkIndex = 16, ChunkCount = 3 },
            new FileTransferRangeV4 { StartChunkIndex = 30, ChunkCount = 4 },
        };

        var selected = Assert.IsAssignableFrom<IReadOnlyList<FileTransferRangeV4>>(
            method!.Invoke(null, [ranges, 16, true]));

        var range = Assert.Single(selected);
        Assert.Equal(16, range.StartChunkIndex);
        Assert.Equal(3, range.ChunkCount);
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
