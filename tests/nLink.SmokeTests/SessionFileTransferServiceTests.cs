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
    public async Task PipelinedSend_AllowsMultipleChunksInFlight_AndBoundsWindowSize()
    {
        const string transferId = "transfer_service_pipeline_window";
        const int expectedWindow = 8;
        var payload = Enumerable.Range(0, 40_960).Select(static i => (byte)(i % 251)).ToArray();
        var startedChunkCount = 0;
        var currentInFlight = 0;
        var maxInFlight = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var senderTransport = new LoopbackFileTransferTransport("session_service_pipeline_window")
        {
            OutboundChunkDeliveryOverrideAsync = async (target, message, ct) =>
            {
                Interlocked.Increment(ref startedChunkCount);
                var inFlight = Interlocked.Increment(ref currentInFlight);
                UpdateMaximum(ref maxInFlight, inFlight);
                try
                {
                    await gate.Task.WaitAsync(ct);
                    target.ReceiveDeliveredChunk(message);
                    return true;
                }
                finally
                {
                    Interlocked.Decrement(ref currentInFlight);
                }
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pipeline_window");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("window.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() => Volatile.Read(ref startedChunkCount) >= expectedWindow);
        await Task.Delay(100);

        Assert.Equal(expectedWindow, Volatile.Read(ref startedChunkCount));
        Assert.Equal(expectedWindow, Volatile.Read(ref maxInFlight));

        gate.TrySetResult(true);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 5000);

        Assert.Equal(payload, destination.ToArray());
        Assert.Equal(payload.Length, sender.Snapshot.Outbound!.BytesTransferred);
        Assert.Equal(payload.Length, receiver.Snapshot.Inbound!.BytesTransferred);
    }

    [Fact]
    public async Task OutboundSend_WaitsForInitialWindowUpdateBeforeSendingFirstChunk()
    {
        const string transferId = "transfer_service_waits_for_initial_window";
        var payload = Enumerable.Range(0, 24_576).Select(static i => (byte)(i % 251)).ToArray();
        var firstChunkSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitialWindow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var senderTransport = new LoopbackFileTransferTransport("session_service_waits_for_initial_window")
        {
            OutboundChunkDeliveryOverrideAsync = (_, _, _) =>
            {
                firstChunkSent.TrySetResult(true);
                return Task.FromResult(false);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_waits_for_initial_window")
        {
            BeforeWindowUpdateDeliveredAsync = async (_, ct) =>
            {
                await releaseInitialWindow.Task.WaitAsync(ct);
            },
        };
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("gated.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        Assert.NotNull(started);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await Task.Delay(200);
        Assert.False(firstChunkSent.Task.IsCompleted);

        releaseInitialWindow.TrySetResult(true);

        await WaitUntilAsync(() => firstChunkSent.Task.IsCompleted);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 5000);
    }

    [Fact]
    public async Task InboundStart_SendsInitialWindowUpdate_AndRefreshesAfterContiguousProgress()
    {
        const string transferId = "transfer_service_window_update_refresh";
        var payload = Enumerable.Range(0, FileTransferProtocol.MaxChunkRawBytes * 80).Select(static i => (byte)(i % 251)).ToArray();

        using var senderTransport = new LoopbackFileTransferTransport("session_service_window_update_refresh");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_window_update_refresh");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("window-refresh.bin", payload.Length, transferId, ChunkSizeBytes: FileTransferProtocol.MaxChunkRawBytes),
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
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 10000);

        Assert.True(receiverTransport.SentWindowUpdates.Count >= 2);
        Assert.True(receiverTransport.SentWindowUpdates.Count < 40);
        Assert.Equal(0, receiverTransport.SentWindowUpdates[0].NextExpectedChunkIndex);
        Assert.True(receiverTransport.SentWindowUpdates[0].GrantedUntilChunkIndexExclusive > receiverTransport.SentWindowUpdates[0].NextExpectedChunkIndex);
        Assert.True(receiverTransport.SentWindowUpdates.Any(update => update.NextExpectedChunkIndex >= 8));
        Assert.Equal(payload, destination.ToArray());

        var flow = sender.GetFlowControlDiagnosticsSnapshot();
        Assert.True(flow.WindowUpdatesReceived >= 2);
        Assert.True(flow.MaxGrantedUntilExclusive >= receiverTransport.SentWindowUpdates[0].GrantedUntilChunkIndexExclusive);
    }

    [Fact]
    public async Task InboundWindowUpdate_RefillsBoundedRunway_BeforeSenderHitsNearEmpty()
    {
        const string transferId = "transfer_service_window_update_refill_before_empty";
        var payload = Enumerable.Range(0, FileTransferProtocol.MaxChunkRawBytes * 48).Select(static i => (byte)(i % 251)).ToArray();

        using var senderTransport = new LoopbackFileTransferTransport("session_service_window_update_refill_before_empty");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_window_update_refill_before_empty");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        receiver.SetFlowControlPolicy(new FileTransferFlowControlPolicy(
            FileTransferFlowControlMode.InteractiveCritical,
            TargetOutstandingBytes: 384L * 1024,
            ReorderSlackBytes: 128L * 1024,
            LocalInFlightChunkSends: 4,
            ChunkPacingMs: 0,
            MinExtensionStepChunks: 8,
            LowWatermarkChunks: 12));
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("window-refill-before-empty.bin", payload.Length, transferId, ChunkSizeBytes: FileTransferProtocol.MaxChunkRawBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        Assert.NotNull(started);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentWindowUpdates.Count(update => update.NextExpectedChunkIndex > 0) >= 1, timeoutMs: 5000);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 10000);

        var firstProgressUpdate = receiverTransport.SentWindowUpdates.First(static update => update.NextExpectedChunkIndex > 0);

        Assert.True(firstProgressUpdate.NextExpectedChunkIndex <= 4);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task InteractiveCriticalPolicy_TemporarilyClampsNormalOutboundChunkScheduling()
    {
        const string transferId = "transfer_service_bulk_clamp";
        var payload = Enumerable.Range(0, FileTransferProtocol.MaxChunkRawBytes * 12).Select(static i => (byte)(i % 251)).ToArray();
        var startedChunkCount = 0;

        using var senderTransport = new LoopbackFileTransferTransport("session_service_bulk_clamp")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                Interlocked.Increment(ref startedChunkCount);
                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_bulk_clamp");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.SetFlowControlPolicy(FileTransferFlowControlPolicy.InteractiveCriticalDefault with { ChunkPacingMs = 0 });
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("bulk-clamp.bin", payload.Length, transferId, ChunkSizeBytes: FileTransferProtocol.MaxChunkRawBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await Task.Delay(250);
        Assert.Equal(0, Volatile.Read(ref startedChunkCount));

        await WaitUntilAsync(() => Volatile.Read(ref startedChunkCount) > 0, timeoutMs: 3000);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 10000);

        var flow = sender.GetFlowControlDiagnosticsSnapshot();
        Assert.True(flow.BulkClampEnteredCount >= 1);
        Assert.True(flow.BulkClampReleasedCount >= 1);
        Assert.True(flow.BulkClampTotalMs >= 500);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task LeavingInteractiveCritical_ReleasesBulkClampEarly()
    {
        const string transferId = "transfer_service_bulk_clamp_release";
        var payload = Enumerable.Range(0, FileTransferProtocol.MaxChunkRawBytes * 12).Select(static i => (byte)(i % 251)).ToArray();
        var startedChunkCount = 0;

        using var senderTransport = new LoopbackFileTransferTransport("session_service_bulk_clamp_release")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                Interlocked.Increment(ref startedChunkCount);
                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_bulk_clamp_release");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.SetFlowControlPolicy(FileTransferFlowControlPolicy.InteractiveCriticalDefault with { ChunkPacingMs = 0 });
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("bulk-clamp-release.bin", payload.Length, transferId, ChunkSizeBytes: FileTransferProtocol.MaxChunkRawBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref startedChunkCount));

        sender.SetFlowControlPolicy(FileTransferFlowControlPolicy.InteractiveDefault with { ChunkPacingMs = 0 });

        await WaitUntilAsync(() => Volatile.Read(ref startedChunkCount) > 0, timeoutMs: 1200);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 10000);

        var flow = sender.GetFlowControlDiagnosticsSnapshot();
        Assert.True(flow.BulkClampEnteredCount >= 1);
        Assert.True(flow.BulkClampReleasedCount >= 1);
        Assert.Equal(FileTransferFlowControlMode.Interactive, flow.FlowMode);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task DroppedInitialWindowUpdate_IsRecoveredByStartupRefresh_BeforeFirstChunkProgress()
    {
        const string transferId = "transfer_service_window_update_startup_refresh";
        var payload = Enumerable.Range(0, FileTransferProtocol.MaxChunkRawBytes * 64).Select(static i => (byte)(i % 251)).ToArray();
        var dropInitialWindowUpdate = 1;

        using var senderTransport = new LoopbackFileTransferTransport("session_service_window_update_startup_refresh");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_window_update_startup_refresh")
        {
            OutboundWindowUpdateDeliveryOverrideAsync = (_, message, _) =>
            {
                if (message.NextExpectedChunkIndex == 0 &&
                    Interlocked.Exchange(ref dropInitialWindowUpdate, 0) == 1)
                {
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            },
        };
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("window-startup-refresh.bin", payload.Length, transferId, ChunkSizeBytes: FileTransferProtocol.MaxChunkRawBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        Assert.NotNull(started);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentWindowUpdates.Count(update => update.NextExpectedChunkIndex == 0) >= 2,
            timeoutMs: 5000);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 15000);

        Assert.Equal(payload, destination.ToArray());

        var flow = receiver.GetFlowControlDiagnosticsSnapshot();
        Assert.Equal(FileTransferFlowControlMode.Interactive, flow.StartupPolicyMode);
        Assert.True(flow.InitialGrantedUntilExclusive > 0);
        Assert.True(flow.StartupWindowRefreshSent >= 1);
        Assert.True(flow.WindowUpdateRefreshResends >= 1);
    }

    [Fact]
    public async Task InboundWindowUpdate_SendFailureAfterDelivery_DoesNotFailTransfer()
    {
        const string transferId = "transfer_service_window_update_post_delivery_failure";
        var payload = Enumerable.Range(0, FileTransferProtocol.MaxChunkRawBytes * 80).Select(static i => (byte)(i % 251)).ToArray();
        var failAfterDelivery = 1;

        using var senderTransport = new LoopbackFileTransferTransport("session_service_window_update_post_delivery_failure");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_window_update_post_delivery_failure")
        {
            AfterWindowUpdateDeliveredAsync = (_, _) =>
            {
                if (Interlocked.Exchange(ref failAfterDelivery, 0) == 1)
                {
                    throw new InvalidOperationException("window_update_post_delivery_failure");
                }

                return Task.CompletedTask;
            },
        };
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("window-post-delivery.bin", payload.Length, transferId, ChunkSizeBytes: FileTransferProtocol.MaxChunkRawBytes),
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
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 10000);

        Assert.Equal(payload, destination.ToArray());
        Assert.Null(sender.Snapshot.Outbound!.ErrorCode);
        Assert.Null(receiver.Snapshot.Inbound!.ErrorCode);

        var flow = receiver.GetFlowControlDiagnosticsSnapshot();
        Assert.True(flow.WindowUpdateSendFailures >= 1);
    }

    [Fact]
    public async Task LostWindowUpdate_IsRecoveredByIdleRefresh()
    {
        const string transferId = "transfer_service_window_update_idle_refresh";
        var payload = Enumerable.Range(0, FileTransferProtocol.MaxChunkRawBytes * 96).Select(static i => (byte)(i % 251)).ToArray();
        var holdExtensionUpdatesUntilUtc = DateTimeOffset.UtcNow.AddMilliseconds(900);

        using var senderTransport = new LoopbackFileTransferTransport("session_service_window_update_idle_refresh");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_window_update_idle_refresh")
        {
            OutboundWindowUpdateDeliveryOverrideAsync = (_, message, _) =>
            {
                if (message.GrantedUntilChunkIndexExclusive > 48 &&
                    DateTimeOffset.UtcNow < holdExtensionUpdatesUntilUtc)
                {
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            },
        };
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("window-idle-refresh.bin", payload.Length, transferId, ChunkSizeBytes: FileTransferProtocol.MaxChunkRawBytes),
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
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 15000);

        Assert.Equal(payload, destination.ToArray());
        Assert.Null(sender.Snapshot.Outbound!.ErrorCode);

        var flow = receiver.GetFlowControlDiagnosticsSnapshot();
        Assert.True(flow.WindowUpdateRefreshResends >= 1);
    }

    [Fact]
    public async Task RapidWindowChanges_AreCoalesced_ToLatestState()
    {
        const string transferId = "transfer_service_window_update_coalesced";
        var payload = Enumerable.Range(0, FileTransferProtocol.MaxChunkRawBytes * 96).Select(static i => (byte)(i % 251)).ToArray();
        var releaseBlockedWindowUpdate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        FileTransferWindowUpdateV2? blockedUpdate = null;
        var blocked = 0;

        using var senderTransport = new LoopbackFileTransferTransport("session_service_window_update_coalesced");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_window_update_coalesced")
        {
            BeforeWindowUpdateDeliveredAsync = async (message, ct) =>
            {
                if (message.NextExpectedChunkIndex >= 8 &&
                    Interlocked.CompareExchange(ref blocked, 1, 0) == 0)
                {
                    blockedUpdate = message;
                    await releaseBlockedWindowUpdate.Task.WaitAsync(ct);
                }
            },
        };
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("window-coalesced.bin", payload.Length, transferId, ChunkSizeBytes: FileTransferProtocol.MaxChunkRawBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        Assert.NotNull(started);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() => blockedUpdate is not null, timeoutMs: 5000);
        await Task.Delay(300);
        releaseBlockedWindowUpdate.TrySetResult(true);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 10000);

        Assert.Equal(payload, destination.ToArray());
        Assert.NotNull(blockedUpdate);
        Assert.True(receiverTransport.SentWindowUpdates[^1].NextExpectedChunkIndex >= blockedUpdate!.NextExpectedChunkIndex);

        var flow = receiver.GetFlowControlDiagnosticsSnapshot();
        Assert.True(flow.WindowUpdateCoalesced >= 1);
    }

    [Fact]
    public async Task OutOfOrderGap_ExtendsCredit_RequestsRepair_AndCompletes()
    {
        const string transferId = "transfer_service_gap_repair";
        var payload = Enumerable.Range(0, FileTransferProtocol.MaxChunkRawBytes * 72).Select(static i => (byte)(i % 251)).ToArray();
        var deliveredChunkOne = 0;

        using var senderTransport = new LoopbackFileTransferTransport("session_service_gap_repair")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (message.ChunkIndex == 1 && Interlocked.Increment(ref deliveredChunkOne) == 1)
                {
                    return Task.FromResult(true);
                }

                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_gap_repair");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("gap-repair.bin", payload.Length, transferId, ChunkSizeBytes: FileTransferProtocol.MaxChunkRawBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        Assert.NotNull(started);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentMissingRanges.Count >= 1, timeoutMs: 5000);
        await WaitUntilAsync(() => receiverTransport.SentWindowUpdates.Count >= 2, timeoutMs: 5000);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 10000);

        Assert.Equal(2, Volatile.Read(ref deliveredChunkOne));
        Assert.True(receiverTransport.SentWindowUpdates.Max(static update => update.GrantedUntilChunkIndexExclusive) >
                    receiverTransport.SentWindowUpdates[0].GrantedUntilChunkIndexExclusive);
        Assert.Single(receiverTransport.SentMissingRanges);
        Assert.Equal(1, receiverTransport.SentMissingRanges[0].Ranges[0].StartChunkIndex);
        Assert.Equal(payload, destination.ToArray());

        var flow = sender.GetFlowControlDiagnosticsSnapshot();
        Assert.True(flow.MissingRangeRequestsReceived >= 1);
        Assert.True(flow.RepairChunksResent >= 1);
    }

    [Fact]
    public async Task OutOfOrderGap_SuppressesSmallWindowUpdates_AndThrottlesRepairRetries()
    {
        const string transferId = "transfer_service_gap_suppressed_window_updates";
        var payload = Enumerable.Range(0, FileTransferProtocol.MaxChunkRawBytes * 96).Select(static i => (byte)(i % 251)).ToArray();
        FileTransferChunkV1? delayedChunk = null;
        var firstChunkOneDelivery = 0;

        using var senderTransport = new LoopbackFileTransferTransport("session_service_gap_suppressed_window_updates")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (message.ChunkIndex == 1 && Interlocked.Increment(ref firstChunkOneDelivery) == 1)
                {
                    delayedChunk = message;
                    return Task.FromResult(true);
                }

                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_gap_suppressed_window_updates");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("gap-suppressed.bin", payload.Length, transferId, ChunkSizeBytes: FileTransferProtocol.MaxChunkRawBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        Assert.NotNull(started);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentMissingRanges.Count >= 1 &&
                  receiverTransport.SentWindowUpdates.Count >= 2,
            timeoutMs: 5000);

        await Task.Delay(750);

        Assert.Single(receiverTransport.SentMissingRanges);
        Assert.NotNull(delayedChunk);

        senderTransport.DeliverChunkToPeer(delayedChunk!);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 10000);

        var grantedSequence = receiverTransport.SentWindowUpdates
            .Select(static update => update.GrantedUntilChunkIndexExclusive)
            .ToArray();
        Assert.True(grantedSequence.SequenceEqual(grantedSequence.OrderBy(static value => value)));

        Assert.True(receiverTransport.SentWindowUpdates.Count < 50);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task OutOfOrderGap_TriggersPressureRepair_AndEscalatesRepeatedRequest()
    {
        const string transferId = "transfer_service_gap_pressure_repair";
        var payload = Enumerable.Range(0, FileTransferProtocol.MaxChunkRawBytes * 96).Select(static i => (byte)(i % 251)).ToArray();
        var delayedChunks = new Dictionary<int, FileTransferChunkV1>();
        var withheldDeliveriesByChunk = new Dictionary<int, int>();

        using var senderTransport = new LoopbackFileTransferTransport("session_service_gap_pressure_repair")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (message.ChunkIndex is >= 1 and <= 12)
                {
                    lock (delayedChunks)
                    {
                        withheldDeliveriesByChunk.TryGetValue(message.ChunkIndex, out var deliveryCount);
                        deliveryCount++;
                        withheldDeliveriesByChunk[message.ChunkIndex] = deliveryCount;
                        if (deliveryCount <= 2)
                        {
                            delayedChunks[message.ChunkIndex] = message;
                            return Task.FromResult(true);
                        }
                    }
                }

                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_gap_pressure_repair");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("gap-pressure.bin", payload.Length, transferId, ChunkSizeBytes: FileTransferProtocol.MaxChunkRawBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        Assert.NotNull(started);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentMissingRanges.Count >= 2, timeoutMs: 5000);

        var firstRequest = receiverTransport.SentMissingRanges[0].Ranges.Single();
        var secondRequest = receiverTransport.SentMissingRanges[1].Ranges.Single();

        Assert.Equal(1, firstRequest.StartChunkIndex);
        Assert.Equal(8, firstRequest.EndChunkIndexInclusive - firstRequest.StartChunkIndex + 1);
        Assert.Equal(1, secondRequest.StartChunkIndex);
        Assert.True(secondRequest.EndChunkIndexInclusive - secondRequest.StartChunkIndex + 1 > 8);

        List<FileTransferChunkV1> releaseChunks;
        lock (delayedChunks)
        {
            releaseChunks = delayedChunks.Values.OrderBy(static chunk => chunk.ChunkIndex).ToList();
        }

        foreach (var delayedChunk in releaseChunks)
        {
            senderTransport.DeliverChunkToPeer(delayedChunk);
        }

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 10000);

        var flow = receiver.GetFlowControlDiagnosticsSnapshot();
        Assert.True(flow.RepairTriggerByBufferPressureCount + flow.RepairTriggerBySeverePressureCount >= 1);
        Assert.True(flow.MissingRangeEscalatedCount >= 1);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task FinalChunks_SuppressProgressOnlyTailWindowUpdates()
    {
        const string transferId = "transfer_service_tail_window_suppression";
        var payload = Enumerable.Range(0, FileTransferProtocol.MaxChunkRawBytes * 20).Select(static i => (byte)(i % 251)).ToArray();

        using var senderTransport = new LoopbackFileTransferTransport("session_service_tail_window_suppression");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_tail_window_suppression");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("tail-window-suppression.bin", payload.Length, transferId, ChunkSizeBytes: FileTransferProtocol.MaxChunkRawBytes),
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
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 10000);

        var chunkCount = checked((int)Math.Ceiling(payload.Length / (double)FileTransferProtocol.MaxChunkRawBytes));
        var terminalGrantUpdates = receiverTransport.SentWindowUpdates
            .Count(update => update.GrantedUntilChunkIndexExclusive == chunkCount);

        Assert.True(terminalGrantUpdates <= 2);

        var flow = receiver.GetFlowControlDiagnosticsSnapshot();
        Assert.True(flow.WindowUpdateSuppressedNoExtension + flow.WindowUpdateSuppressedTail >= 1);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public async Task MissingRangeSendFailure_DoesNotTerminateTransfer()
    {
        const string transferId = "transfer_service_missing_range_send_failure";
        var payload = Enumerable.Range(0, FileTransferProtocol.MaxChunkRawBytes * 72).Select(static i => (byte)(i % 251)).ToArray();
        var deliveredChunkOne = 0;
        var failMissingRangeSend = 1;

        using var senderTransport = new LoopbackFileTransferTransport("session_service_missing_range_send_failure")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (message.ChunkIndex == 1 && Interlocked.Increment(ref deliveredChunkOne) == 1)
                {
                    return Task.FromResult(true);
                }

                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_missing_range_send_failure")
        {
            AfterMissingRangeDeliveredAsync = (_, _) =>
            {
                if (Interlocked.Exchange(ref failMissingRangeSend, 0) == 1)
                {
                    throw new InvalidOperationException("missing_range_post_delivery_failure");
                }

                return Task.CompletedTask;
            },
        };
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("missing-range-send-failure.bin", payload.Length, transferId, ChunkSizeBytes: FileTransferProtocol.MaxChunkRawBytes),
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
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 10000);

        Assert.Equal(payload, destination.ToArray());
        Assert.Null(receiver.Snapshot.Inbound!.ErrorCode);

        var flow = receiver.GetFlowControlDiagnosticsSnapshot();
        Assert.True(flow.MissingRangeSendFailures >= 1);
    }

    [Fact]
    public async Task DelayedOriginalChunk_AfterRepair_IsIgnoredAsDuplicate()
    {
        const string transferId = "transfer_service_duplicate_after_repair";
        var payload = Enumerable.Range(0, FileTransferProtocol.MaxChunkRawBytes * 72).Select(static i => (byte)(i % 251)).ToArray();
        FileTransferChunkV1? delayedChunk = null;
        var chunkOneSendCount = 0;

        using var senderTransport = new LoopbackFileTransferTransport("session_service_duplicate_after_repair")
        {
            OutboundChunkDeliveryOverrideAsync = async (target, message, ct) =>
            {
                if (message.ChunkIndex == 1 && Interlocked.Increment(ref chunkOneSendCount) == 1)
                {
                    delayedChunk = message;
                    return true;
                }

                target.ReceiveDeliveredChunk(message);
                if (message.ChunkIndex == 1 && delayedChunk is not null)
                {
                    await Task.Delay(50, ct);
                    target.ReceiveDeliveredChunk(delayedChunk);
                }

                return true;
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_duplicate_after_repair");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("duplicate-repair.bin", payload.Length, transferId, ChunkSizeBytes: FileTransferProtocol.MaxChunkRawBytes),
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
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 10000);

        Assert.Equal(payload, destination.ToArray());
        Assert.Null(receiver.Snapshot.Inbound!.ErrorCode);

        var flow = receiver.GetFlowControlDiagnosticsSnapshot();
        Assert.True(flow.DuplicateChunksReceived >= 1);
    }

    [Fact]
    public async Task BackwardWindowUpdate_IsIgnored_InsteadOfFailingTransfer()
    {
        const string transferId = "transfer_service_backward_window_update";
        var payload = Enumerable.Range(0, FileTransferProtocol.MaxChunkRawBytes * 96).Select(static i => (byte)(i % 251)).ToArray();

        using var senderTransport = new LoopbackFileTransferTransport("session_service_backward_window_update");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_backward_window_update");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new DelayedWriteMemoryStream(delayMilliseconds: 5);

        var started = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("backward-window-update.bin", payload.Length, transferId, ChunkSizeBytes: FileTransferProtocol.MaxChunkRawBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        Assert.NotNull(started);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);

        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentWindowUpdates.Any(static update => update.TransferId == transferId),
            timeoutMs: 5000);
        var sessionId = sender.Snapshot.Outbound!.SessionId;

        senderTransport.ReceiveWindowUpdate(
            new FileTransferWindowUpdateV2
            {
                SessionId = sessionId,
                TransferId = transferId,
                NextExpectedChunkIndex = 0,
                GrantedUntilChunkIndexExclusive = 8,
                BytesReceived = 0,
            });

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 10000);

        Assert.Equal(payload, destination.ToArray());
        Assert.Null(sender.Snapshot.Outbound!.ErrorCode);
    }

    [Fact]
    public async Task PipelinedSend_Completes_WhenChunkSendTasksFinishOutOfOrder()
    {
        const string transferId = "transfer_service_pipeline_out_of_order_completion";
        var payload = Enumerable.Range(0, 16_384).Select(static i => (byte)(i % 251)).ToArray();
        var sendOrder = new List<int>();
        var chunkSignals = new Dictionary<int, TaskCompletionSource<bool>>();

        using var senderTransport = new LoopbackFileTransferTransport("session_service_pipeline_out_of_order_completion")
        {
            OutboundChunkDeliveryOverrideAsync = async (target, message, ct) =>
            {
                lock (sendOrder)
                {
                    sendOrder.Add(message.ChunkIndex);
                    chunkSignals[message.ChunkIndex] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                await chunkSignals[message.ChunkIndex].Task.WaitAsync(ct);
                target.ReceiveDeliveredChunk(message);
                return true;
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pipeline_out_of_order_completion");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("out-of-order.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
        {
            lock (sendOrder)
            {
                return sendOrder.Count == 4;
            }
        });

        Assert.Equal([0, 1, 2, 3], sendOrder.OrderBy(static index => index));

        chunkSignals[3].TrySetResult(true);
        chunkSignals[2].TrySetResult(true);
        chunkSignals[1].TrySetResult(true);
        chunkSignals[0].TrySetResult(true);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 5000);

        Assert.Equal(payload, destination.ToArray());
        Assert.Equal(payload.Length, sender.Snapshot.Outbound!.BytesTransferred);
        Assert.Equal(payload.Length, receiver.Snapshot.Inbound!.BytesTransferred);
        Assert.Equal(4, sender.Snapshot.Outbound.ChunksTransferred);
    }

    [Fact]
    public async Task PipelinedSend_FailedChunkSend_FailsTransferExactlyOnce()
    {
        const string transferId = "transfer_service_pipeline_failed_chunk";
        var payload = Enumerable.Range(0, 20_480).Select(static i => (byte)(i % 251)).ToArray();
        var blocker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var senderTransport = new LoopbackFileTransferTransport("session_service_pipeline_failed_chunk")
        {
            OutboundChunkDeliveryOverrideAsync = async (target, message, ct) =>
            {
                if (message.ChunkIndex == 1)
                {
                    throw new InvalidOperationException("scripted chunk send failure");
                }

                await blocker.Task.WaitAsync(ct);
                target.ReceiveDeliveredChunk(message);
                return true;
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pipeline_failed_chunk");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("failed.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Failed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed,
            timeoutMs: 5000);

        Assert.Single(senderTransport.SentErrors);
        Assert.Equal(FileTransferResultCodes.ReadFailed, sender.Snapshot.Outbound!.ErrorCode);
        Assert.Equal(FileTransferResultCodes.ReadFailed, receiver.Snapshot.Inbound!.ErrorCode);
    }

    [Fact]
    public async Task CancelDuringPipelinedSend_StopsFurtherChunkScheduling()
    {
        const string transferId = "transfer_service_pipeline_cancel";
        const int expectedWindow = 8;
        var payload = Enumerable.Range(0, 65_536).Select(static i => (byte)(i % 251)).ToArray();
        var startedChunkCount = 0;
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var senderTransport = new LoopbackFileTransferTransport("session_service_pipeline_cancel")
        {
            OutboundChunkDeliveryOverrideAsync = async (_, message, ct) =>
            {
                Interlocked.Increment(ref startedChunkCount);
                await gate.Task.WaitAsync(ct);
                return true;
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pipeline_cancel");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("cancel.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()),
            CancellationToken.None);

        await WaitUntilAsync(() => Volatile.Read(ref startedChunkCount) >= expectedWindow);

        await sender.CancelTransferAsync(transferId, null, CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Canceled &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 5000);

        await Task.Delay(100);
        Assert.Equal(expectedWindow, Volatile.Read(ref startedChunkCount));
    }

    [Fact]
    public async Task PipelinedSend_ProgressRemainsMonotonic_AndTerminatesOnce_WithoutOrphanedInflightSends()
    {
        const string transferId = "transfer_service_pipeline_stability";
        const int chunkSize = 4096;
        const int expectedWindow = 4;
        var payload = Enumerable.Range(0, chunkSize * 10 + 123).Select(static i => (byte)(i % 251)).ToArray();
        var currentInFlight = 0;
        var maxInFlight = 0;
        var startedChunkCount = 0;
        var completionOrder = new List<int>();
        var senderBytesProgress = new List<long>();
        var senderTerminalStates = new List<FileTransferTransferState>();
        var chunkSignals = new Dictionary<int, TaskCompletionSource<bool>>();

        using var senderTransport = new LoopbackFileTransferTransport("session_service_pipeline_stability")
        {
            OutboundChunkDeliveryOverrideAsync = async (target, message, ct) =>
            {
                Interlocked.Increment(ref startedChunkCount);
                var inFlight = Interlocked.Increment(ref currentInFlight);
                UpdateMaximum(ref maxInFlight, inFlight);

                TaskCompletionSource<bool> signal;
                lock (chunkSignals)
                {
                    signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    chunkSignals[message.ChunkIndex] = signal;
                }

                try
                {
                    await signal.Task.WaitAsync(ct);
                    lock (completionOrder)
                    {
                        completionOrder.Add(message.ChunkIndex);
                    }

                    target.ReceiveDeliveredChunk(message);
                    return true;
                }
                finally
                {
                    Interlocked.Decrement(ref currentInFlight);
                }
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pipeline_stability");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.SetFlowControlPolicy(new FileTransferFlowControlPolicy(
            FileTransferFlowControlMode.Interactive,
            TargetOutstandingBytes: 1L * 1024 * 1024,
            ReorderSlackBytes: 768L * 1024,
            LocalInFlightChunkSends: expectedWindow,
            ChunkPacingMs: 0,
            MinExtensionStepChunks: 16,
            LowWatermarkChunks: 16));
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        sender.TransferChanged += (_, e) =>
        {
            var outbound = e.Snapshot.Outbound;
            if (outbound is null)
            {
                return;
            }

            senderBytesProgress.Add(outbound.BytesTransferred);
            if (outbound.State is FileTransferTransferState.Completed or FileTransferTransferState.Failed or FileTransferTransferState.Canceled or FileTransferTransferState.Declined)
            {
                senderTerminalStates.Add(outbound.State);
            }
        };

        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("stability.bin", payload.Length, transferId, ChunkSizeBytes: chunkSize),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
        {
            lock (chunkSignals)
            {
                return chunkSignals.Count >= expectedWindow;
            }
        });

        Assert.Equal(expectedWindow, Volatile.Read(ref startedChunkCount));

        ReleaseChunkSignalsDescending(chunkSignals, 0, 3);

        await WaitUntilAsync(() =>
        {
            lock (chunkSignals)
            {
                return chunkSignals.Count >= expectedWindow * 2;
            }
        });

        Assert.Equal(expectedWindow * 2, Volatile.Read(ref startedChunkCount));

        ReleaseChunkSignalsDescending(chunkSignals, 4, 7);

        await WaitUntilAsync(() =>
        {
            lock (chunkSignals)
            {
                return chunkSignals.Count == 11;
            }
        });

        ReleaseChunkSignalsDescending(chunkSignals, 8, 10);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 5000);

        Assert.Equal(payload, destination.ToArray());
        Assert.Equal(11, Volatile.Read(ref startedChunkCount));
        Assert.Equal(expectedWindow, Volatile.Read(ref maxInFlight));
        Assert.Equal(0, Volatile.Read(ref currentInFlight));

        Assert.NotEmpty(senderBytesProgress);
        Assert.Equal(senderBytesProgress.OrderBy(static value => value), senderBytesProgress);
        Assert.Equal(payload.Length, senderBytesProgress[^1]);

        Assert.Single(senderTerminalStates);
        Assert.Equal(FileTransferTransferState.Completed, senderTerminalStates[0]);

        Assert.Equal(11, completionOrder.Count);
        Assert.Equal(11, completionOrder.Distinct().Count());
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
    public async Task SenderCancelDuringBlockedInboundWrite_TransitionsReceiverWithoutWaitingForChunkBacklog()
    {
        const string transferId = "transfer_service_cancel_priority";
        var payload = Enumerable.Range(0, 8192).Select(static i => (byte)(i % 251)).ToArray();
        var cancelTriggered = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_service_cancel_priority");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_cancel_priority");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        using var destination = new BlockingWriteMemoryStream();

        senderTransport.AfterChunkDeliveredAsync = async (message, ct) =>
        {
            if (message.TransferId != transferId || message.ChunkIndex != 0 || Interlocked.Exchange(ref cancelTriggered, 1) != 0)
            {
                return;
            }

            await destination.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
            await sender.CancelTransferAsync(transferId, "user_canceled", CancellationToken.None);
        };

        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        try
        {
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("cancel-priority.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);

            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(
                transferId,
                (_, _) => Task.FromResult<Stream>(destination),
                CancellationToken.None);

            await WaitUntilAsync(() =>
                sender.Snapshot.OutboundState == FileTransferTransferState.Canceled &&
                receiver.Snapshot.InboundState == FileTransferTransferState.Canceled,
                timeoutMs: 3000);
        }
        finally
        {
            destination.ReleaseWrites();
        }

        Assert.Equal("user_canceled", sender.Snapshot.Outbound!.StatusMessage);
        Assert.Equal("user_canceled", receiver.Snapshot.Inbound!.StatusMessage);
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
        var logStart = CaptureOperationalLogSnapshot();
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
                () => ReadOperationalLogDelta(logStart).Contains("event=integrity_verify_failed", StringComparison.Ordinal),
                timeoutMs: 1500);
            var logTail = ReadOperationalLogDelta(logStart);
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
        var logStart = CaptureOperationalLogSnapshot();
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
                () => ReadOperationalLogDelta(logStart).Contains("event=temp_finalize_failed", StringComparison.Ordinal),
                timeoutMs: 1500);
            var logTail = ReadOperationalLogDelta(logStart);
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

    private static string CaptureOperationalLogSnapshot()
    {
        return LocalOperationalLog.GetRecentLogText();
    }

    private static string ReadOperationalLogDelta(string snapshot)
    {
        var logText = LocalOperationalLog.GetRecentLogText();
        if (string.IsNullOrEmpty(snapshot))
        {
            return logText;
        }

        return logText.StartsWith(snapshot, StringComparison.Ordinal)
            ? logText[snapshot.Length..]
            : logText;
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }

    private static void ReleaseChunkSignalsDescending(
        Dictionary<int, TaskCompletionSource<bool>> chunkSignals,
        int startInclusive,
        int endInclusive)
    {
        for (var chunkIndex = endInclusive; chunkIndex >= startInclusive; chunkIndex--)
        {
            TaskCompletionSource<bool> signal;
            lock (chunkSignals)
            {
                signal = chunkSignals[chunkIndex];
            }

            signal.TrySetResult(true);
        }
    }

    private sealed class LoopbackFileTransferTransport : IFileTransferSignalingTransport, ISignalingTransport, IFileTransferFlowControlPolicyAwareTransport
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

        public Func<FileTransferWindowUpdateV2, CancellationToken, Task>? BeforeWindowUpdateDeliveredAsync { get; set; }

        public Func<FileTransferWindowUpdateV2, CancellationToken, Task>? AfterWindowUpdateDeliveredAsync { get; set; }

        public Func<LoopbackFileTransferTransport, FileTransferWindowUpdateV2, CancellationToken, Task<bool>>? OutboundWindowUpdateDeliveryOverrideAsync { get; set; }

        public Func<FileTransferMissingRangeV1, CancellationToken, Task>? BeforeMissingRangeDeliveredAsync { get; set; }

        public Func<FileTransferMissingRangeV1, CancellationToken, Task>? AfterMissingRangeDeliveredAsync { get; set; }

        public Func<LoopbackFileTransferTransport, FileTransferMissingRangeV1, CancellationToken, Task<bool>>? OutboundMissingRangeDeliveryOverrideAsync { get; set; }

        public Func<FileTransferCompleteV1, CancellationToken, Task>? BeforeCompleteDeliveredAsync { get; set; }

        public List<FileTransferErrorV1> SentErrors { get; } = [];

        public List<FileTransferCompleteV1> SentCompletes { get; } = [];

        public List<FileTransferWindowUpdateV2> SentWindowUpdates { get; } = [];

        public List<FileTransferMissingRangeV1> SentMissingRanges { get; } = [];

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
        public event EventHandler<FileTransferWindowUpdateReceivedEventArgs>? FileTransferWindowUpdateReceived;
        public event EventHandler<FileTransferMissingRangeReceivedEventArgs>? FileTransferMissingRangeReceived;
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

        public Task SendFileTransferWindowUpdateAsync(FileTransferWindowUpdateV2 message, CancellationToken ct)
            => DeliverWindowUpdateAsync(message with { SessionId = NormalizeSessionId(message.SessionId) }, ct);

        public Task SendFileTransferMissingRangeAsync(FileTransferMissingRangeV1 message, CancellationToken ct)
            => DeliverMissingRangeAsync(message with { SessionId = NormalizeSessionId(message.SessionId) }, ct);

        public void SetFileTransferFlowControlPolicy(FileTransferFlowControlPolicy policy)
        {
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

        public void ReceiveWindowUpdate(FileTransferWindowUpdateV2 payload)
        {
            FileTransferWindowUpdateReceived?.Invoke(this, new FileTransferWindowUpdateReceivedEventArgs(payload, "loopback-peer"));
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

        private async Task DeliverWindowUpdateAsync(FileTransferWindowUpdateV2 message, CancellationToken ct)
        {
            SentWindowUpdates.Add(message);
            if (BeforeWindowUpdateDeliveredAsync is not null)
            {
                await BeforeWindowUpdateDeliveredAsync(message, ct);
            }

            var target = peer ?? throw new InvalidOperationException("Loopback peer is not connected.");
            var handled = false;
            if (OutboundWindowUpdateDeliveryOverrideAsync is not null)
            {
                handled = await OutboundWindowUpdateDeliveryOverrideAsync(target, message, ct);
            }

            if (!handled)
            {
                await DeliverAsync(
                    message,
                    (deliveredTarget, payload) => deliveredTarget.FileTransferWindowUpdateReceived?.Invoke(deliveredTarget, new FileTransferWindowUpdateReceivedEventArgs(payload, "loopback-peer")),
                    ct);
            }

            if (AfterWindowUpdateDeliveredAsync is not null)
            {
                await AfterWindowUpdateDeliveredAsync(message, ct);
            }
        }

        private async Task DeliverMissingRangeAsync(FileTransferMissingRangeV1 message, CancellationToken ct)
        {
            SentMissingRanges.Add(message);
            if (BeforeMissingRangeDeliveredAsync is not null)
            {
                await BeforeMissingRangeDeliveredAsync(message, ct);
            }

            var target = peer ?? throw new InvalidOperationException("Loopback peer is not connected.");
            var handled = false;
            if (OutboundMissingRangeDeliveryOverrideAsync is not null)
            {
                handled = await OutboundMissingRangeDeliveryOverrideAsync(target, message, ct);
            }

            if (!handled)
            {
                await DeliverAsync(
                    message,
                    (deliveredTarget, payload) => deliveredTarget.FileTransferMissingRangeReceived?.Invoke(deliveredTarget, new FileTransferMissingRangeReceivedEventArgs(payload, "loopback-peer")),
                    ct);
            }

            if (AfterMissingRangeDeliveredAsync is not null)
            {
                await AfterMissingRangeDeliveredAsync(message, ct);
            }
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

    private sealed class BlockingWriteMemoryStream : NonDisposingMemoryStream
    {
        private readonly TaskCompletionSource<bool> releaseWrites = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int writeStarted;

        public TaskCompletionSource<bool> WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseWrites()
            => releaseWrites.TrySetResult(true);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref writeStarted, 1) == 0)
            {
                WriteStarted.TrySetResult(true);
                await releaseWrites.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }

            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}
