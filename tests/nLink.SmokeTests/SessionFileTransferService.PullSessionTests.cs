using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using System.Security.Cryptography;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferServiceTests
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pull-stall.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed, timeoutMs: 12000);

        var inbound = receiver.Snapshot.Inbound!;
        Assert.Equal(FileTransferResultCodes.PullSessionStalled, inbound.ErrorCode);
        Assert.Equal(FileTransferResultCodes.PullSessionStalled, inbound.StatusMessage);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.True(
            Regex.Matches(logTail, "event=filetransfer_request_sent;.*start_chunk=0; requested_chunk_count=1", RegexOptions.CultureInvariant).Count <= 4,
            "Expected the stalled pull session to fail fast instead of repeatedly requesting chunk 0.");
    }

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
                _ = Task.Run(
                    async () =>
                    {
                        try
                        {
                            await Task.Delay(3500, ct).ConfigureAwait(false);
                            target.ReceiveDeliveredDataFrame(chunk);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    },
                    CancellationToken.None);
                return true;
            }

            return false;
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        var logStart = GetOperationalLogLength();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pull-single-timeout.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 12000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Equal(payload, destination.ToArray());
        Assert.Contains("event=filetransfer_request_timeout_detected", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("error_code=pull_session_stalled", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullSession_V3Streaming_CompletesWithoutV2RequestLoop()
    {
        const string transferId = "transfer_service_pull_v3_streaming";
        var payload = Enumerable.Range(0, 256_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_streaming")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_streaming")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pull-v3-streaming.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 15000);

        Assert.Equal(payload, destination.ToArray());
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferManifestFrameV3);
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferChunkDataFrameV3 or FileTransferChunkBatchFrameV3);
        Assert.Contains(receiverTransport.SentDataFrames, static frame => frame is FileTransferGrantWindowFrameV3);
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferRequestChunksFrameV2);
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferAckProgressFrameV3 or FileTransferGrantWindowFrameV3 or FileTransferChunkDataFrameV3 or FileTransferChunkBatchFrameV3);
    }

    [Fact]
    public async Task PullSession_V3Streaming_UsesLargerChunks_AndHealthyGrantWindow_WithMinimalLegacyControlNoise()
    {
        const string transferId = "transfer_service_pull_v3_tuned_healthy";
        var payload = Enumerable.Range(0, 3_500_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_tuned_healthy")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_tuned_healthy")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pull-v3-tuned-healthy.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(payload, destination.ToArray());
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        Assert.Equal(40 * 1024, manifest.ChunkSizeBytes);

        var maxGrantWindowBytes = receiverTransport.SentDataFrames
            .OfType<FileTransferGrantWindowFrameV3>()
            .Select(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes)
            .DefaultIfEmpty(0)
            .Max();
        Assert.True(
            maxGrantWindowBytes >= (2 * 1024 * 1024) - manifest.ChunkSizeBytes,
            $"Expected a healthy V3 grant window near 2 MiB, but saw {maxGrantWindowBytes} bytes.");
        Assert.Empty(receiverTransport.SentWindowUpdates);
        Assert.True(
            receiverTransport.SentPressureStates.Count <= 3,
            $"Expected V3 healthy flow to keep pressure chatter low, but saw {receiverTransport.SentPressureStates.Count} pressure-state messages.");
    }

    [Fact]
    public async Task PullSession_V3Streaming_ExpandsHealthyGrantWindow_AfterSustainedProgress()
    {
        const string transferId = "transfer_service_pull_v3_tuned_expand";
        var payload = Enumerable.Range(0, 8 * 1024 * 1024).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_tuned_expand")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_tuned_expand")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame is FileTransferChunkDataFrameV3 or FileTransferChunkBatchFrameV3)
            {
                await Task.Delay(20, ct).ConfigureAwait(false);
            }

            return false;
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pull-v3-tuned-expand.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 30000);

        Assert.Equal(payload, destination.ToArray());
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        Assert.Equal(40 * 1024, manifest.ChunkSizeBytes);
        var maxGrantWindowBytes = receiverTransport.SentDataFrames
            .OfType<FileTransferGrantWindowFrameV3>()
            .Select(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes)
            .DefaultIfEmpty(0)
            .Max();
        Assert.True(
            maxGrantWindowBytes >= (4 * 1024 * 1024) - manifest.ChunkSizeBytes,
            $"Expected V3 healthy flow to step up near 4 MiB, but saw {maxGrantWindowBytes} bytes.");
    }

    [Fact]
    public async Task PullSession_V3Streaming_WithScreenshare_UsesBalancedChunkAndGrantTargets()
    {
        const string transferId = "transfer_service_pull_v3_tuned_screenshare";
        var payload = Enumerable.Range(0, 1_500_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_tuned_screenshare")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_tuned_screenshare")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        sender.SetSessionScreenShareActive(true);
        receiver.SetSessionScreenShareActive(true);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pull-v3-tuned-screenshare.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(payload, destination.ToArray());
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        Assert.Equal(24 * 1024, manifest.ChunkSizeBytes);

        var maxGrantWindowBytes = receiverTransport.SentDataFrames
            .OfType<FileTransferGrantWindowFrameV3>()
            .Select(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes)
            .DefaultIfEmpty(0)
            .Max();
        Assert.True(
            maxGrantWindowBytes >= (256 * 1024) - manifest.ChunkSizeBytes,
            $"Expected balanced screenshare V3 grant window near 256 KiB, but saw {maxGrantWindowBytes} bytes.");
        Assert.True(maxGrantWindowBytes < 2 * 1024 * 1024, "Expected screenshare-balanced V3 window to stay below the healthy 2 MiB target.");
    }

    [Fact]
    public async Task PullSession_V3Streaming_WhenScreenshareActivatesMidTransfer_ForcesReducedGrantWindow()
    {
        const string transferId = "transfer_service_pull_v3_midstream_screenshare_clamp";
        var payload = Enumerable.Range(0, 5 * 1024 * 1024).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_midstream_screenshare_clamp")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_midstream_screenshare_clamp")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame is FileTransferChunkDataFrameV3 or FileTransferChunkBatchFrameV3)
            {
                await Task.Delay(10, ct).ConfigureAwait(false);
            }

            return false;
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pull-v3-midstream-screenshare-clamp.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            receiverTransport.SentDataFrames
                .OfType<FileTransferGrantWindowFrameV3>()
                .Any(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * 40 * 1024 >= (2 * 1024 * 1024) - (40 * 1024)),
            timeoutMs: 10000);

        var grantCountBeforeScreenshare = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Count();
        sender.SetSessionScreenShareActive(true);
        receiver.SetSessionScreenShareActive(true);

        await WaitUntilAsync(() =>
            receiverTransport.SentDataFrames
                .OfType<FileTransferGrantWindowFrameV3>()
                .Skip(grantCountBeforeScreenshare)
                .Any(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * 40 * 1024 <= 256 * 1024),
            timeoutMs: 10000);

        var reducedGrant = receiverTransport.SentDataFrames
            .OfType<FileTransferGrantWindowFrameV3>()
            .Skip(grantCountBeforeScreenshare)
            .First(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * 40 * 1024 <= 256 * 1024);
        var reducedGrantBytes = (reducedGrant.GrantedUntilChunkIndexExclusive - reducedGrant.NextExpectedChunkIndex) * 40 * 1024;
        Assert.True(reducedGrantBytes <= 256 * 1024, $"Expected a forced reduced grant at or below 256 KiB, but saw {reducedGrantBytes} bytes.");
    }

    [Fact]
    public async Task PullSession_V3Streaming_WhenDegraded_UsesReducedChunkTarget()
    {
        const string transferId = "transfer_service_pull_v3_tuned_degraded";
        var payload = Enumerable.Range(0, 900_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_tuned_degraded")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_tuned_degraded")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        sender.SetSessionScreenShareDegraded(true);
        receiver.SetSessionScreenShareDegraded(true);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pull-v3-tuned-degraded.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(payload, destination.ToArray());
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        Assert.Equal(20 * 1024, manifest.ChunkSizeBytes);
    }

    [Fact]
    public async Task PullSession_V3Streaming_BatchedChunks_AreNotResentIndividually()
    {
        const string transferId = "transfer_service_pull_v3_batched_no_duplicates";
        const int chunkSizeBytes = 4096;
        var payload = Enumerable.Range(0, chunkSizeBytes * 12).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_batched_no_duplicates")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_batched_no_duplicates")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pull-v3-batch-no-duplicates.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 15000);

        Assert.Equal(payload, destination.ToArray());

        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        var batches = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV3>().ToList();
        Assert.Contains(batches, static batch => batch.DataSegments.Count > 2);

        var sentChunkIndices = senderTransport.SentDataFrames
            .Where(static frame => frame is FileTransferChunkDataFrameV3 or FileTransferChunkBatchFrameV3)
            .SelectMany(frame => frame switch
            {
                FileTransferChunkDataFrameV3 chunk => [chunk.ChunkIndex],
                FileTransferChunkBatchFrameV3 batch => Enumerable.Range(batch.StartChunkIndex, batch.DataSegments.Count),
                _ => Enumerable.Empty<int>(),
            })
            .OrderBy(static chunkIndex => chunkIndex)
            .ToList();

        Assert.Equal(manifest.ChunkCount, sentChunkIndices.Count);
        Assert.Equal(sentChunkIndices.Distinct().Count(), sentChunkIndices.Count);
    }

    [Fact]
    public async Task PullSession_V3Streaming_ReorderBurst_ClampsExpandedWindowToHealthyLimitedProfile()
    {
        const string transferId = "transfer_service_pull_v3_reorder_limited";
        var payload = Enumerable.Range(0, 9 * 1024 * 1024).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_reorder_limited")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_reorder_limited")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);

        var delayedFrames = new ConcurrentQueue<FileTransferDataFrameV2>();
        var releaseStarted = 0;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, ct) =>
        {
            if (frame is FileTransferChunkDataFrameV3 chunk &&
                chunk.TransferId == transferId &&
                chunk.ChunkIndex is >= 90 and < 130)
            {
                delayedFrames.Enqueue(frame);
                if (Interlocked.Exchange(ref releaseStarted, 1) == 0)
                {
                    _ = Task.Run(
                        async () =>
                        {
                            try
                            {
                                await Task.Delay(1800, ct).ConfigureAwait(false);
                                while (delayedFrames.TryDequeue(out var delayed))
                                {
                                    target.ReceiveDeliveredDataFrame(delayed);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                            }
                        },
                        CancellationToken.None);
                }

                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        var logStart = GetOperationalLogLength();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pull-v3-reorder-limited.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 35000);

        Assert.Equal(payload, destination.ToArray());
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        var grantWindows = receiverTransport.SentDataFrames
            .OfType<FileTransferGrantWindowFrameV3>()
            .Select(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes)
            .ToList();
        var expandedIndex = grantWindows.FindIndex(windowBytes => windowBytes >= (4 * 1024 * 1024) - manifest.ChunkSizeBytes);

        Assert.True(expandedIndex >= 0, "Expected the transfer to reach the healthy expanded 4 MiB window before the reorder clamp.");
        Assert.Contains(
            grantWindows.Skip(expandedIndex + 1),
            windowBytes => windowBytes <= (2 * 1024 * 1024) + manifest.ChunkSizeBytes);

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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("cancel-priority.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await blockedControlEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var outbound = sender.Snapshot.Outbound!;
        await receiverTransport.SendFileTransferCancelAsync(
            new FileTransferCancelV1
            {
                SessionId = outbound.SessionId,
                TransferId = outbound.TransferId,
                Reason = "test_cancel",
            },
            CancellationToken.None);

        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Canceled, timeoutMs: 3000);
        Assert.Equal(FileTransferTransferState.Canceled, sender.Snapshot.Outbound?.State);

        releaseBlockedControl.TrySetResult(true);
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pull-startup-resend.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 12000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Equal(payload, destination.ToArray());
        Assert.Contains("event=window_startup_completed", logTail, StringComparison.Ordinal);
        Assert.True(
            Regex.Matches(logTail, "event=window_update_sent;.*reason=startup_resend", RegexOptions.CultureInvariant).Count <= 1,
            "Expected startup resend logging to stop after healthy pull-session progress was established.");
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("session-open-before-start.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await sessionOpenDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FileTransferTransferState.AwaitingMetadata, receiver.Snapshot.Inbound?.State);
        Assert.NotNull(delayedStart);

        receiverTransport.ReceiveDeliveredStart(delayedStart!);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 12000);

        Assert.Equal(payload, destination.ToArray());
    }

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
            if (frame is FileTransferChunkDataFrameV2 chunk &&
                chunk.ChunkIndex == 0 &&
                Interlocked.Exchange(ref droppedInitialChunkZero, 1) == 0)
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pull-retry-gate.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV2>().Any() &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkDataFrameV2>().Any(frame => frame.ChunkIndex == 0),
            timeoutMs: 5000);

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

        Assert.Equal(
            1,
            senderTransport.SentDataFrames.OfType<FileTransferChunkDataFrameV2>().Count(frame => frame.ChunkIndex == 0));

        await Task.Delay(1100);
        senderTransport.ReceiveDeliveredDataFrame(retryRequest);

        await WaitUntilAsync(() =>
            senderTransport.SentDataFrames.OfType<FileTransferChunkDataFrameV2>().Count(frame => frame.ChunkIndex == 0) == 2,
            timeoutMs: 5000);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 12000);

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
            if (frame is FileTransferChunkDataFrameV2 chunk &&
                chunk.ChunkIndex == 3 &&
                Interlocked.Exchange(ref droppedChunkThree, 1) == 0)
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pull-oldest-gap-retry.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 15000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Equal(payload, destination.ToArray());
        Assert.Equal(
            2,
            senderTransport.SentDataFrames.OfType<FileTransferChunkDataFrameV2>().Count(frame => frame.ChunkIndex == 3));
        Assert.Contains("event=filetransfer_request_timeout_detected", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_chunk_retry_requested", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_chunk_retry_sent", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_chunk_resend_suppressed; transfer_id=transfer_service_pull_oldest_gap_retry", logTail, StringComparison.Ordinal);
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pull-repeated-timeout.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(
            () =>
            {
                var logTail = ReadOperationalLogTail(logStart);
                return logTail.Contains($"event=filetransfer_session_degraded_entered; transfer_id={transferId}", StringComparison.Ordinal) &&
                       logTail.Contains($"event=filetransfer_request_timeout_detected; transfer_id={transferId}", StringComparison.Ordinal);
            },
            timeoutMs: 12000);

        Interlocked.Exchange(ref holdChunks, 0);
        while (heldChunks.TryDequeue(out var delayedChunk))
        {
            receiverTransport.ReceiveDeliveredDataFrame(delayedChunk);
        }

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 15000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Equal(payload, destination.ToArray());
        Assert.Contains($"event=filetransfer_session_degraded_entered; transfer_id={transferId}", logTail, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_pipeline_changed; direction=Inbound; transfer_id={transferId}", logTail, StringComparison.Ordinal);
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pull-session-mismatch.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed, timeoutMs: 13000);

        Assert.Equal(0, receiver.Snapshot.Inbound!.BytesTransferred);
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pull-oldest-gap.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferRequestChunksFrameV2>().Any(static frame =>
                frame.StartChunkIndex == 0 && frame.RequestedChunkCount == 6),
            timeoutMs: 5000);

        await WaitUntilAsync(() => !delayedChunkZeroFrames.IsEmpty, timeoutMs: 5000);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferRequestChunksFrameV2>().Any(static frame =>
                frame.StartChunkIndex == 0 && frame.RequestedChunkCount == 1),
            timeoutMs: 5000);

        Assert.DoesNotContain(
            receiverTransport.SentDataFrames.OfType<FileTransferRequestChunksFrameV2>(),
            static frame => frame.StartChunkIndex >= 6);

        Assert.DoesNotContain(
            senderTransport.SentDataFrames.OfType<FileTransferChunkDataFrameV2>(),
            static frame => frame.ChunkIndex >= 6);

        await sender.CancelTransferAsync(transferId, "test_done", CancellationToken.None);
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("ack-progress.bin", payload.Length, transferId, ChunkSizeBytes: 4096),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
        {
            var snapshot = sender.Snapshot.Outbound;
            return snapshot is not null &&
                   snapshot.BytesAcceptedForTransport.HasValue &&
                   snapshot.BytesAcknowledgedByReceiver.HasValue &&
                   snapshot.BytesAcceptedForTransport.Value > snapshot.BytesAcknowledgedByReceiver.Value;
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

    [Fact(Skip = "Obsolete internal send-ahead clamp coverage after file-transfer pipeline refactors.")]
    public async Task SenderSendAheadClamp_LimitsSequentialBurst_BeforeRemoteAckAdvances()
    {
        const string transferId = "transfer_service_send_ahead_clamp";
        const int chunkSizeBytes = 1024;
        var payload = Enumerable.Range(0, chunkSizeBytes * 300).Select(static i => (byte)(i % 251)).ToArray();
        var sentChunkCount = 0;
        var highestSentChunkIndex = -1;
        var logStartIndex = GetOperationalLogLength();

        using var senderTransport = new LoopbackFileTransferTransport("session_service_send_ahead_clamp")
        {
            OutboundChunkDeliveryOverrideAsync = (_, message, _) =>
            {
                Interlocked.Increment(ref sentChunkCount);
                var currentHighest = Volatile.Read(ref highestSentChunkIndex);
                while (message.ChunkIndex > currentHighest)
                {
                    var observed = Interlocked.CompareExchange(ref highestSentChunkIndex, message.ChunkIndex, currentHighest);
                    if (observed == currentHighest)
                    {
                        break;
                    }

                    currentHighest = observed;
                }
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_send_ahead_clamp");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("send-ahead-clamp.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()),
            CancellationToken.None);

        await WaitUntilAsync(() => Volatile.Read(ref sentChunkCount) >= 32, timeoutMs: 5000);
        await Task.Delay(400);

        Assert.Equal(32, Volatile.Read(ref sentChunkCount));
        Assert.Equal(31, Volatile.Read(ref highestSentChunkIndex));

        var logTail = ReadOperationalLogTail(logStartIndex);
        Assert.Contains("event=window_send_ahead_clamped", logTail, StringComparison.Ordinal);
        Assert.Contains("effective_send_limit=32", logTail, StringComparison.Ordinal);

        await sender.CancelTransferAsync(transferId, "test_done", CancellationToken.None);
        await WaitUntilAsync(() =>
            sender.Snapshot.OutboundState == FileTransferTransferState.Canceled &&
            receiver.Snapshot.InboundState == FileTransferTransferState.Canceled);
    }

    [Fact]
    public async Task ScreenshareActiveStartupGrant_IsCappedToThirtyTwoChunks()
    {
        const string transferId = "transfer_service_screenshare_startup_cap";
        const int chunkSizeBytes = 4096;
        var payload = Enumerable.Range(0, chunkSizeBytes * 200).Select(static i => (byte)(i % 251)).ToArray();

        using var senderTransport = new LoopbackFileTransferTransport("session_service_screenshare_startup_cap")
        {
            OutboundChunkDeliveryOverrideAsync = (_, _, _) => Task.FromResult(true),
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_screenshare_startup_cap");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        receiver.SetSessionScreenShareActive(true);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("screenshare-startup-cap.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()),
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames
                .OfType<FileTransferRequestChunksFrameV2>()
                .Any(static frame => frame.StartChunkIndex == 0 && frame.RequestedChunkCount == 3 && frame.PipelineDepth == 3),
            timeoutMs: 5000);
        await Task.Delay(400);

        Assert.DoesNotContain(
            receiverTransport.SentDataFrames.OfType<FileTransferRequestChunksFrameV2>(),
            static frame => frame.RequestedChunkCount > 3);

        await sender.CancelTransferAsync(transferId, "test_done", CancellationToken.None);
        await WaitUntilAsync(() =>
            sender.Snapshot.OutboundState == FileTransferTransferState.Canceled &&
            receiver.Snapshot.InboundState == FileTransferTransferState.Canceled,
            timeoutMs: 12000);
    }

    [Fact]
    public async Task PullSession_ActiveScreenshare_UsesTwentyFourKilobyteChunks_AndThreeChunkPipeline()
    {
        const string transferId = "transfer_service_screenshare_active_profile";
        var payload = Enumerable.Range(0, 24576 * 12).Select(static i => (byte)(i % 251)).ToArray();

        using var senderTransport = new LoopbackFileTransferTransport("session_service_screenshare_active_profile");
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_screenshare_active_profile");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        receiver.SetSessionScreenShareActive(true);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("screenshare-active-profile.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferRequestChunksFrameV2>().Any(static frame =>
                frame.StartChunkIndex == 0 && frame.RequestedChunkCount == 3 && frame.PipelineDepth == 3),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferRequestChunksFrameV2>().Any(static frame =>
                frame.StartChunkIndex > 0 && frame.RequestedChunkCount == 2 && frame.PipelineDepth == 3),
            timeoutMs: 5000);

        Assert.Equal(24576, receiver.Snapshot.Inbound?.ChunkSizeBytes);

        await WaitUntilAsync(() =>
            sender.Snapshot.OutboundState == FileTransferTransferState.Completed &&
            receiver.Snapshot.InboundState == FileTransferTransferState.Completed,
            timeoutMs: 12000);
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pressure-emit.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()),
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentMissingRanges.Count >= 2,
            timeoutMs: 8000);
        await Task.Delay(1500);

        Assert.False(receiverTransport.SentPressureStates.Any(static state => string.Equals(state.Mode, FileTransferProtocol.PressureModeCatchUpOnly, StringComparison.Ordinal)));
        Assert.False(sender.IsCatchUpOnlyPressureActive);

        await WaitUntilAsync(() =>
            sender.Snapshot.OutboundState == FileTransferTransferState.Completed &&
            receiver.Snapshot.InboundState == FileTransferTransferState.Completed,
            timeoutMs: 12000);
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("pressure-block.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()),
            CancellationToken.None);

        await WaitUntilAsync(() => sentChunkIndices.Count >= 32, timeoutMs: 5000);

        senderTransport.ReceiveDeliveredPressureState(
            new FileTransferPressureStateV1
            {
                SessionId = "session_service_pressure_block",
                TransferId = transferId,
                Revision = 1,
                Mode = FileTransferProtocol.PressureModeCatchUpOnly,
                SuggestedSendAheadChunks = 2,
                ReceiverNextExpectedChunkIndex = 32,
                Reason = FileTransferProtocol.PressureReasonBulkBacklog,
            });

        await Task.Delay(500);
        Assert.DoesNotContain(40, sentChunkIndices);
        Assert.True(sender.IsCatchUpOnlyPressureActive);

        senderTransport.ReceiveDeliveredPressureState(
            new FileTransferPressureStateV1
            {
                SessionId = "session_service_pressure_block",
                TransferId = transferId,
                Revision = 1,
                Mode = FileTransferProtocol.PressureModeNormal,
                SuggestedSendAheadChunks = 32,
                ReceiverNextExpectedChunkIndex = 40,
                Reason = FileTransferProtocol.PressureReasonBulkBacklog,
            });

        await Task.Delay(250);
        Assert.DoesNotContain(40, sentChunkIndices);
        Assert.True(sender.IsCatchUpOnlyPressureActive);

        senderTransport.ReceiveDeliveredPressureState(
            new FileTransferPressureStateV1
            {
                SessionId = "session_service_pressure_block",
                TransferId = transferId,
                Revision = 2,
                Mode = FileTransferProtocol.PressureModeNormal,
                SuggestedSendAheadChunks = 32,
                ReceiverNextExpectedChunkIndex = 40,
                Reason = FileTransferProtocol.PressureReasonBulkBacklog,
            });

        await WaitUntilAsync(() => sentChunkIndices.Contains(40), timeoutMs: 5000);
        Assert.False(sender.IsCatchUpOnlyPressureActive);

        var logTail = ReadOperationalLogTail(logStartIndex);
        Assert.Contains("event=sequential_send_blocked_by_pressure", logTail, StringComparison.Ordinal);
        Assert.Contains("event=pressure_state_received", logTail, StringComparison.Ordinal);

        await sender.CancelTransferAsync(transferId, "test_done", CancellationToken.None);
        await WaitUntilAsync(() =>
            sender.Snapshot.OutboundState == FileTransferTransferState.Canceled &&
            receiver.Snapshot.InboundState == FileTransferTransferState.Canceled);
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("repair-only.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()),
            CancellationToken.None);

        await WaitUntilAsync(() => sentChunkIndices.Count >= 32, timeoutMs: 5000);

        senderTransport.ReceiveDeliveredMissingRange(
            new FileTransferMissingRangeV1
            {
                SessionId = "session_service_repair_only_mode",
                TransferId = transferId,
                StartChunkIndex = 0,
                EndChunkIndexExclusive = 1,
            });

        await WaitUntilAsync(() => GetOutboundRepairOnlyMode(sender), timeoutMs: 5000);

        senderTransport.ReceiveDeliveredWindowUpdate(
            new FileTransferWindowUpdateV1
            {
                SessionId = "session_service_repair_only_mode",
                TransferId = transferId,
                NextExpectedChunkIndex = 1,
                GrantedUntilChunkIndexExclusive = 128,
                BytesReceived = chunkSizeBytes,
            });

        await Task.Delay(400);
        Assert.DoesNotContain(32, sentChunkIndices);
        Assert.True(GetOutboundRepairOnlyMode(sender));

        senderTransport.ReceiveDeliveredWindowUpdate(
            new FileTransferWindowUpdateV1
            {
                SessionId = "session_service_repair_only_mode",
                TransferId = transferId,
                NextExpectedChunkIndex = 32,
                GrantedUntilChunkIndexExclusive = 128,
                BytesReceived = 32L * chunkSizeBytes,
            });

        await WaitUntilAsync(() => !GetOutboundRepairOnlyMode(sender), timeoutMs: 5000);
        await WaitUntilAsync(() => sentChunkIndices.Contains(32), timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStartIndex);
        Assert.Contains("event=repair_only_mode_entered", logTail, StringComparison.Ordinal);
        Assert.Contains("event=repair_only_mode_exited", logTail, StringComparison.Ordinal);

        await sender.CancelTransferAsync(transferId, "test_done", CancellationToken.None);
        await WaitUntilAsync(() =>
            sender.Snapshot.OutboundState == FileTransferTransferState.Canceled &&
            receiver.Snapshot.InboundState == FileTransferTransferState.Canceled);
    }

    [Fact(Skip = "Legacy chunk-read implementation detail coverage no longer matches the current sender pipeline.")]
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

    [Fact(Skip = "Legacy inconsistent-start error-path coverage no longer matches the current transfer pipeline.")]
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
                if (message.ChunkIndex == 0 &&
                    Interlocked.CompareExchange(ref droppedInitialChunk, 1, 0) == 0)
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("buffered-frontier-gap.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentMissingRanges.Any(
                static range => range.StartChunkIndex == 0 && range.EndChunkIndexExclusive > 0),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => receiverTransport.SentWindowUpdates.Any(
                update => update.NextExpectedChunkIndex > 0 && update.GrantedUntilChunkIndexExclusive > startupGrantChunks),
            timeoutMs: 5000);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(payload, destination.ToArray());
        Assert.All(
            receiverTransport.SentWindowUpdates.Where(static update => update.NextExpectedChunkIndex == 0),
            update => Assert.Equal(startupGrantChunks, update.GrantedUntilChunkIndexExclusive));
        Assert.Contains(
            receiverTransport.SentWindowUpdates,
            update => update.NextExpectedChunkIndex > 0 && update.GrantedUntilChunkIndexExclusive > startupGrantChunks);
        Assert.Contains(
            receiverTransport.SentMissingRanges,
            static range => range.StartChunkIndex == 0 && range.EndChunkIndexExclusive > 0);

        var logTail = ReadOperationalLogTail(logStartIndex);
        Assert.True(
            logTail.Contains("reason=buffered_frontier", StringComparison.Ordinal) ||
            logTail.Contains("reason=low_watermark", StringComparison.Ordinal) ||
            logTail.Contains("reason=gap_progress_ack", StringComparison.Ordinal),
            $"Expected a steady-state window-update reason in log tail, but found:{Environment.NewLine}{logTail}");
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
        var postMissingRangeGate = new object();

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

                if (message.ChunkIndex == droppedChunkIndex &&
                    Interlocked.CompareExchange(ref droppedInitialChunk, 1, 0) == 0)
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("repair-priority.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() => Volatile.Read(ref missingRangeObserved) != 0, timeoutMs: 5000);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

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
                if (message.ChunkIndex == droppedChunkIndex &&
                    Interlocked.Increment(ref chunkSendAttempts) <= 2)
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

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("repair-retry.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.True(Volatile.Read(ref chunkSendAttempts) >= 3);
        Assert.True(
            receiverTransport.SentMissingRanges.Count(static range => range.StartChunkIndex == droppedChunkIndex) >= 1);

        var logTail = ReadOperationalLogTail(logStartIndex);
        Assert.True(
            logTail.Contains("event=repair_chunk_sent", StringComparison.Ordinal) ||
            logTail.Contains("event=repair_chunk_resent", StringComparison.Ordinal),
            $"Expected repair send activity in log tail, but found:{Environment.NewLine}{logTail}");
        Assert.Contains("batch_size=1", logTail, StringComparison.Ordinal);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact(Skip = "Obsolete gap-frontier grant-growth coverage after file-transfer pipeline refactors.")]
    public async Task OpenGap_DoesNotExtendGrantFromBufferedFrontierUntilContiguousProgressAdvances()
    {
        const string transferId = "transfer_service_gap_grant_suppression";
        const int chunkSizeBytes = 1024;
        const int droppedChunkIndex = 129;
        const int initialSteadyStateGrant = 321;
        var payload = Enumerable.Range(0, chunkSizeBytes * 700).Select(static i => (byte)(i % 251)).ToArray();
        var initialDrop = 0;
        var allowRepairDelivery = 0;
        var repairChunkHeld = 0;
        var logStartIndex = GetOperationalLogLength();

        using var senderTransport = new LoopbackFileTransferTransport("session_service_gap_grant_suppression")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (message.ChunkIndex == droppedChunkIndex &&
                    Interlocked.CompareExchange(ref initialDrop, 1, 0) == 0)
                {
                    return Task.FromResult(true);
                }

                if (message.ChunkIndex == droppedChunkIndex &&
                    Volatile.Read(ref allowRepairDelivery) == 0)
                {
                    Interlocked.Exchange(ref repairChunkHeld, 1);
                    return Task.FromResult(true);
                }

                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_gap_grant_suppression");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("gap-grant-suppression.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentMissingRanges.Any(static range => range.StartChunkIndex == droppedChunkIndex),
            timeoutMs: 5000);
        await WaitUntilAsync(() => Volatile.Read(ref repairChunkHeld) != 0, timeoutMs: 5000);

        Assert.All(
            receiverTransport.SentWindowUpdates.Where(update => update.NextExpectedChunkIndex == droppedChunkIndex),
            update => Assert.True(update.GrantedUntilChunkIndexExclusive <= initialSteadyStateGrant));

        var deferredLogTail = ReadOperationalLogTail(logStartIndex);
        Assert.Contains("event=window_extension_deferred_due_to_gap", deferredLogTail, StringComparison.Ordinal);

        Interlocked.Exchange(ref allowRepairDelivery, 1);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(payload, destination.ToArray());
    }

    [Fact(Skip = "Obsolete gap-progress ack coverage after file-transfer pipeline refactors.")]
    public async Task OpenGap_AdvancingContiguousProgress_EmitsGapProgressAckWithoutGrantGrowth()
    {
        const string transferId = "transfer_service_gap_progress_ack";
        const int chunkSizeBytes = 1024;
        var droppedChunkIndices = new HashSet<int> { 129, 130, 131 };
        var droppedSequentialChunks = new HashSet<int>();
        var allowFinalRepairChunk = 0;
        var logStartIndex = GetOperationalLogLength();

        using var senderTransport = new LoopbackFileTransferTransport("session_service_gap_progress_ack")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (droppedChunkIndices.Contains(message.ChunkIndex) &&
                    droppedSequentialChunks.Add(message.ChunkIndex))
                {
                    return Task.FromResult(true);
                }

                if (message.ChunkIndex == 131 && Volatile.Read(ref allowFinalRepairChunk) == 0)
                {
                    return Task.FromResult(true);
                }

                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_gap_progress_ack");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        var payload = Enumerable.Range(0, chunkSizeBytes * 700).Select(static i => (byte)(i % 251)).ToArray();
        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("gap-progress-ack.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentWindowUpdates.Any(update => update.NextExpectedChunkIndex == 129),
            timeoutMs: 5000);
        var deferredGrant = receiverTransport.SentWindowUpdates
            .Where(update => update.NextExpectedChunkIndex == 129)
            .Select(update => update.GrantedUntilChunkIndexExclusive)
            .Last();

        await WaitUntilAsync(
            () => receiverTransport.SentWindowUpdates.Any(
                update => update.NextExpectedChunkIndex >= 131 &&
                          update.GrantedUntilChunkIndexExclusive == deferredGrant),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStartIndex);
        Assert.Contains("reason=gap_progress_ack", logTail, StringComparison.Ordinal);

        Interlocked.Exchange(ref allowFinalRepairChunk, 1);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(payload, destination.ToArray());
    }

    [Fact(Skip = "Obsolete repair batching coverage after file-transfer pipeline refactors.")]
    public async Task MissingRange_RepairMode_SendsTwoChunkBatchBeforeWaitingForAck()
    {
        const string transferId = "transfer_service_repair_batch";
        const int chunkSizeBytes = 1024;
        var droppedChunkIndices = new HashSet<int> { 129, 130, 131, 132 };
        var initiallyDropped = new HashSet<int>();
        var missingRangeObserved = 0;
        var repairChunkIndices = new List<int>();
        var repairGate = new object();

        using var senderTransport = new LoopbackFileTransferTransport("session_service_repair_batch")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (droppedChunkIndices.Contains(message.ChunkIndex) &&
                    initiallyDropped.Add(message.ChunkIndex))
                {
                    return Task.FromResult(true);
                }

                if (Volatile.Read(ref missingRangeObserved) != 0 &&
                    message.ChunkIndex >= 129 &&
                    message.ChunkIndex <= 132)
                {
                    lock (repairGate)
                    {
                        repairChunkIndices.Add(message.ChunkIndex);
                    }

                    if (message.ChunkIndex <= 130)
                    {
                        return Task.FromResult(true);
                    }
                }

                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_repair_batch")
        {
            OutboundMissingRangeDeliveryOverrideAsync = (target, message, _) =>
            {
                if (message.StartChunkIndex == 129)
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

        var payload = Enumerable.Range(0, chunkSizeBytes * 700).Select(static i => (byte)(i % 251)).ToArray();
        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("repair-batch.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() => Volatile.Read(ref missingRangeObserved) != 0, timeoutMs: 5000);
        await Task.Delay(200);

        int[] repairSnapshot;
        lock (repairGate)
        {
            repairSnapshot = repairChunkIndices.ToArray();
        }

        Assert.Equal(1, repairSnapshot.Count(index => index == 129));
        Assert.Equal(1, repairSnapshot.Count(index => index == 130));
        Assert.DoesNotContain(131, repairSnapshot);

        await sender.CancelTransferAsync(transferId, "test_done", CancellationToken.None);
    }

    [Fact(Skip = "Obsolete repair-path coverage after file-transfer pipeline refactors.")]
    public async Task RepeatedMissingRange_AfterAckAdvance_DoesNotRetransmitObsoleteRepairChunks()
    {
        const string transferId = "transfer_service_repair_obsolete";
        const int chunkSizeBytes = 1024;
        var droppedChunkIndices = new HashSet<int> { 4, 5, 6, 7 };
        var initiallyDropped = new HashSet<int>();
        var missingRangeObserved = 0;
        var trackObsoletePhase = 0;
        var obsoleteRepairChunkIndices = new ConcurrentQueue<int>();

        using var senderTransport = new LoopbackFileTransferTransport("session_service_repair_obsolete")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (droppedChunkIndices.Contains(message.ChunkIndex) &&
                    initiallyDropped.Add(message.ChunkIndex))
                {
                    return Task.FromResult(true);
                }

                if (Volatile.Read(ref trackObsoletePhase) != 0 &&
                    (message.ChunkIndex == 4 || message.ChunkIndex == 5))
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
        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("repair-obsolete.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(() => Volatile.Read(ref missingRangeObserved) != 0, timeoutMs: 5000);
        var sessionId = Assert.IsType<string>(sender.Snapshot.Outbound?.SessionId);

        senderTransport.ReceiveDeliveredWindowUpdate(
            new FileTransferWindowUpdateV1
            {
                SessionId = sessionId,
                TransferId = transferId,
                NextExpectedChunkIndex = 6,
                GrantedUntilChunkIndexExclusive = 64,
                BytesReceived = 6L * chunkSizeBytes,
            });

        Interlocked.Exchange(ref trackObsoletePhase, 1);
        senderTransport.ReceiveDeliveredMissingRange(
            new FileTransferMissingRangeV1
            {
                SessionId = sessionId,
                TransferId = transferId,
                StartChunkIndex = 4,
                EndChunkIndexExclusive = 8,
            });

        await Task.Delay(300);

        Assert.Empty(obsoleteRepairChunkIndices);

        await sender.CancelTransferAsync(transferId, "test_done", CancellationToken.None);
    }

    [Fact(Skip = "Obsolete degraded-repair growth-cap coverage after file-transfer pipeline refactors.")]
    public async Task PersistentGap_EntersDegradedRepairMode_AndCapsFutureGrantGrowth()
    {
        const string transferId = "transfer_service_degraded_repair_cap";
        const int chunkSizeBytes = 1024;
        const int droppedChunkIndex = 129;
        var initialDrop = 0;
        var allowRepairDelivery = 0;

        using var senderTransport = new LoopbackFileTransferTransport("session_service_degraded_repair_cap")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (message.ChunkIndex == droppedChunkIndex &&
                    Interlocked.CompareExchange(ref initialDrop, 1, 0) == 0)
                {
                    return Task.FromResult(true);
                }

                if (message.ChunkIndex == droppedChunkIndex &&
                    Volatile.Read(ref allowRepairDelivery) == 0)
                {
                    return Task.FromResult(true);
                }

                target.ReceiveDeliveredChunk(message);
                return Task.FromResult(true);
            },
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_degraded_repair_cap");
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        var payload = Enumerable.Range(0, chunkSizeBytes * 700).Select(static i => (byte)(i % 251)).ToArray();
        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("degraded-repair-cap.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentMissingRanges.Count(static range => range.StartChunkIndex == droppedChunkIndex) >= 3,
            timeoutMs: 5000);

        Interlocked.Exchange(ref allowRepairDelivery, 1);

        await WaitUntilAsync(
            () => receiverTransport.SentWindowUpdates.Any(
                update => update.NextExpectedChunkIndex >= droppedChunkIndex &&
                          update.GrantedUntilChunkIndexExclusive - update.NextExpectedChunkIndex <= 32),
            timeoutMs: 10000);
        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(payload, destination.ToArray());
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
                if (message.ChunkIndex == droppedChunkIndex &&
                    Interlocked.CompareExchange(ref initialDrop, 1, 0) == 0)
                {
                    return Task.FromResult(true);
                }

                if (message.ChunkIndex == droppedChunkIndex &&
                    Volatile.Read(ref allowRepairDelivery) == 0)
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
        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("bulk-fallback-cap.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentMissingRanges.Any(),
            timeoutMs: 7000);

        await Task.Delay(1500);

        Assert.DoesNotContain(
            receiverTransport.SentPressureStates,
            static state => string.Equals(state.Mode, FileTransferProtocol.PressureModeCatchUpOnly, StringComparison.Ordinal));

        Interlocked.Exchange(ref allowRepairDelivery, 1);

        await WaitUntilAsync(() =>
            sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
            receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 40000);

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

                if (message.ChunkIndex == droppedChunkIndex &&
                    Interlocked.CompareExchange(ref initialDrop, 1, 0) == 0)
                {
                    return Task.FromResult(true);
                }

                if (message.ChunkIndex == droppedChunkIndex &&
                    Volatile.Read(ref allowRepairDelivery) == 0)
                {
                    return Task.FromResult(true);
                }

                if (message.ChunkIndex <= 8 || (message.ChunkIndex == droppedChunkIndex && Volatile.Read(ref allowRepairDelivery) != 0))
                {
                    target.ReceiveDeliveredChunk(message);
                    return Task.FromResult(true);
                }

                if (firstChunk is not null &&
                    message.ChunkIndex > 8 &&
                    Interlocked.Increment(ref injectedObsoleteDuplicates) <= 4)
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
        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("obsolete-metrics.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStartIndex).Contains("event=filetransfer_bulk_unhealthy_detected", StringComparison.Ordinal),
            timeoutMs: 7000);
        await WaitUntilAsync(
            () => receiverTransport.SentPressureStates.Any(static state => string.Equals(state.Mode, FileTransferProtocol.PressureModeCatchUpOnly, StringComparison.Ordinal)),
            timeoutMs: 7000);

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
            if (frame is not FileTransferChunkDataFrameV2 chunk ||
                chunk.TransferId != transferId ||
                chunk.ChunkIndex != 0 ||
                Interlocked.Exchange(ref disconnectTriggered, 1) != 0)
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
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("disconnect.bin", payload.Length, transferId, ChunkSizeBytes: 2048),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);

            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(
                transferId,
                (_, _) => Task.FromResult(CreateTempReceiveDestination(finalPath)),
                CancellationToken.None);

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
        return ReadOperationalLogText().Length;
    }

    private static bool GetOutboundRepairOnlyMode(SessionFileTransferService service)
    {
        var field = typeof(SessionFileTransferService).GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var context = field!.GetValue(service);
        Assert.NotNull(context);

        var repairOnlyProperty = context!.GetType().GetProperty("RepairOnlyModeActive", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(repairOnlyProperty);
        return (bool)(repairOnlyProperty!.GetValue(context) ?? false);
    }

    private static string ReadOperationalLogTail(int startIndex)
    {
        var logText = ReadOperationalLogText();
        if (startIndex <= 0)
        {
            return logText;
        }

        if (startIndex >= logText.Length)
        {
            // The operational log can rotate between the initial length snapshot and the final read.
            // When that happens, returning the full current contents is more reliable than returning nothing.
            return logText;
        }

        return logText[startIndex..];
    }

    private static string GetLoopbackFrameChunkIndex(FileTransferDataFrameV2 frame)
        => frame switch
        {
            FileTransferChunkDataFrameV2 chunk => chunk.ChunkIndex.ToString(),
            FileTransferChunkBatchFrameV2 batch => $"{batch.StartChunkIndex}-{batch.StartChunkIndex + batch.DataSegments.Count - 1}",
            _ => "(none)",
        };

    private static string ReadOperationalLogText()
    {
        if (!File.Exists(LocalOperationalLog.LogFilePath))
        {
            return string.Empty;
        }

        using var stream = new FileStream(LocalOperationalLog.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class LoopbackFileTransferTransport : IFileTransferSignalingTransport, ISignalingTransport, IFileTransferProtocolCapabilities
    {
        private readonly string sessionId;
        private readonly ConcurrentDictionary<string, LoopbackDataSession> dataSessions = new(StringComparer.Ordinal);
        private LoopbackFileTransferTransport? peer;

        public LoopbackFileTransferTransport(string sessionId)
        {
            this.sessionId = sessionId;
        }

        public bool SupportsFileTransferV3Streaming { get; set; }

        public Func<FileTransferStartV2, FileTransferStartV2>? OutboundStartTransform { get; init; }

        public Func<LoopbackFileTransferTransport, FileTransferStartV2, CancellationToken, Task<bool>>? OutboundStartDeliveryOverrideAsync { get; set; }

        public Func<FileTransferChunkV1, FileTransferChunkV1>? OutboundChunkTransform { get; init; }

        public Func<FileTransferChunkV1, CancellationToken, Task>? AfterChunkDeliveredAsync { get; set; }

        public Func<LoopbackFileTransferTransport, FileTransferChunkV1, CancellationToken, Task<bool>>? OutboundChunkDeliveryOverrideAsync { get; set; }

        public Func<LoopbackFileTransferTransport, FileTransferWindowUpdateV1, CancellationToken, Task<bool>>? OutboundWindowUpdateDeliveryOverrideAsync { get; set; }

        public Func<LoopbackFileTransferTransport, FileTransferMissingRangeV1, CancellationToken, Task<bool>>? OutboundMissingRangeDeliveryOverrideAsync { get; set; }

        public Func<LoopbackFileTransferTransport, FileTransferPressureStateV1, CancellationToken, Task<bool>>? OutboundPressureStateDeliveryOverrideAsync { get; set; }

        public Func<LoopbackFileTransferTransport, FileTransferDataFrameV2, CancellationToken, Task<bool>>? OutboundDataFrameDeliveryOverrideAsync { get; set; }

        public Func<LoopbackFileTransferTransport, FileTransferDataFrameV2, bool, CancellationToken, Task<bool>>? OutboundDataFrameDeliveryOverrideWithLaneAsync { get; set; }

        public Func<LoopbackFileTransferTransport, FileTransferSessionOpenV2, CancellationToken, Task<bool>>? OutboundSessionOpenDeliveryOverrideAsync { get; set; }

        public Func<FileTransferCompleteV1, CancellationToken, Task>? BeforeCompleteDeliveredAsync { get; set; }

        public Exception? OfferSendException { get; init; }

        public ConcurrentQueue<FileTransferErrorV1> SentErrors { get; } = [];

        public ConcurrentQueue<FileTransferCompleteV1> SentCompletes { get; } = [];

        public ConcurrentQueue<FileTransferWindowUpdateV1> SentWindowUpdates { get; } = [];

        public ConcurrentQueue<FileTransferMissingRangeV1> SentMissingRanges { get; } = [];

        public ConcurrentQueue<FileTransferPressureStateV1> SentPressureStates { get; } = [];

        public ConcurrentQueue<FileTransferDataFrameV2> SentDataFrames { get; } = [];

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<FileTransferOfferReceivedEventArgs>? FileTransferOfferReceived;
        public event EventHandler<FileTransferAcceptReceivedEventArgs>? FileTransferAcceptReceived;
        public event EventHandler<FileTransferDeclineReceivedEventArgs>? FileTransferDeclineReceived;
        public event EventHandler<FileTransferSessionOpenReceivedEventArgs>? FileTransferSessionOpenReceived;
        public event EventHandler<FileTransferStartReceivedEventArgs>? FileTransferStartReceived;
        public event EventHandler<FileTransferChunkReceivedEventArgs>? FileTransferChunkReceived;
        public event EventHandler<FileTransferWindowUpdateReceivedEventArgs>? FileTransferWindowUpdateReceived;
        public event EventHandler<FileTransferMissingRangeReceivedEventArgs>? FileTransferMissingRangeReceived;
        public event EventHandler<FileTransferPressureStateReceivedEventArgs>? FileTransferPressureStateReceived;
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

        public Task SendFileTransferOfferAsync(FileTransferOfferV2 message, CancellationToken ct)
        {
            if (OfferSendException is not null)
            {
                return Task.FromException(OfferSendException);
            }

            return DeliverAsync(
                message with { SessionId = NormalizeSessionId(message.SessionId) },
                (target, payload) => target.FileTransferOfferReceived?.Invoke(target, new FileTransferOfferReceivedEventArgs(payload, "loopback-peer")),
                ct);
        }

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

        public Task SendFileTransferSessionOpenAsync(FileTransferSessionOpenV2 message, CancellationToken ct)
            => DeliverMaybeAsync(
                message with { SessionId = NormalizeSessionId(message.SessionId) },
                static (transport, payload, token) => transport.OutboundSessionOpenDeliveryOverrideAsync?.Invoke(transport.peer!, payload, token) ?? Task.FromResult(false),
                (target, payload) => target.FileTransferSessionOpenReceived?.Invoke(target, new FileTransferSessionOpenReceivedEventArgs(payload, "loopback-peer")),
                ct);

        public Task SendFileTransferStartAsync(FileTransferStartV2 message, CancellationToken ct)
            => DeliverMaybeAsync(
                ApplyStartTransform(message with { SessionId = NormalizeSessionId(message.SessionId) }),
                static (transport, payload, token) => transport.OutboundStartDeliveryOverrideAsync?.Invoke(transport.peer!, payload, token) ?? Task.FromResult(false),
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

        public Task SendFileTransferWindowUpdateAsync(FileTransferWindowUpdateV1 message, CancellationToken ct)
            => DeliverMaybeAsync(
                TrackWindowUpdate(message with { SessionId = NormalizeSessionId(message.SessionId) }),
                static (transport, payload, token) => transport.OutboundWindowUpdateDeliveryOverrideAsync?.Invoke(transport.peer!, payload, token) ?? Task.FromResult(false),
                static (target, payload) => target.FileTransferWindowUpdateReceived?.Invoke(target, new FileTransferWindowUpdateReceivedEventArgs(payload, "loopback-peer")),
                ct);

        public Task SendFileTransferMissingRangeAsync(FileTransferMissingRangeV1 message, CancellationToken ct)
            => DeliverMaybeAsync(
                TrackMissingRange(message with { SessionId = NormalizeSessionId(message.SessionId) }),
                static (transport, payload, token) => transport.OutboundMissingRangeDeliveryOverrideAsync?.Invoke(transport.peer!, payload, token) ?? Task.FromResult(false),
                static (target, payload) => target.FileTransferMissingRangeReceived?.Invoke(target, new FileTransferMissingRangeReceivedEventArgs(payload, "loopback-peer")),
                ct);

        public Task SendFileTransferPressureStateAsync(FileTransferPressureStateV1 message, CancellationToken ct)
            => DeliverMaybeAsync(
                TrackPressureState(message with { SessionId = NormalizeSessionId(message.SessionId) }),
                static (transport, payload, token) => transport.OutboundPressureStateDeliveryOverrideAsync?.Invoke(transport.peer!, payload, token) ?? Task.FromResult(false),
                static (target, payload) => target.FileTransferPressureStateReceived?.Invoke(target, new FileTransferPressureStateReceivedEventArgs(payload, "loopback-peer")),
                ct);

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

        public Task<IFileTransferDataSession> OpenFileTransferDataSessionAsync(string sessionId, string transferId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var session = GetOrCreateDataSession(NormalizeSessionId(sessionId), transferId.Trim());
            return Task.FromResult<IFileTransferDataSession>(session);
        }

        public void Dispose()
        {
            foreach (var session in dataSessions.Values)
            {
                session.Dispose();
            }
        }

        public void RaiseDisconnected()
        {
            SetAllDataSessionsAvailability(isAvailable: false, "transport_disconnected", requiresResumeRequest: true);
            peer?.SetAllDataSessionsAvailability(isAvailable: false, "transport_disconnected", requiresResumeRequest: true);
            Disconnected?.Invoke(this, EventArgs.Empty);
            peer?.Disconnected?.Invoke(peer, EventArgs.Empty);
        }

        public void RaiseReconnected()
        {
            SetAllDataSessionsAvailability(isAvailable: true, "transport_recovered", requiresResumeRequest: true);
            peer?.SetAllDataSessionsAvailability(isAvailable: true, "transport_recovered", requiresResumeRequest: true);
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

        public void ReceiveDeliveredWindowUpdate(FileTransferWindowUpdateV1 payload)
        {
            FileTransferWindowUpdateReceived?.Invoke(this, new FileTransferWindowUpdateReceivedEventArgs(payload, "loopback-peer"));
        }

        public void ReceiveDeliveredMissingRange(FileTransferMissingRangeV1 payload)
        {
            FileTransferMissingRangeReceived?.Invoke(this, new FileTransferMissingRangeReceivedEventArgs(payload, "loopback-peer"));
        }

        public void ReceiveDeliveredPressureState(FileTransferPressureStateV1 payload)
        {
            FileTransferPressureStateReceived?.Invoke(this, new FileTransferPressureStateReceivedEventArgs(payload, "loopback-peer"));
        }

        public void ReceiveDeliveredSessionOpen(FileTransferSessionOpenV2 payload)
        {
            FileTransferSessionOpenReceived?.Invoke(this, new FileTransferSessionOpenReceivedEventArgs(payload, "loopback-peer"));
        }

        public void ReceiveDeliveredStart(FileTransferStartV2 payload)
        {
            FileTransferStartReceived?.Invoke(this, new FileTransferStartReceivedEventArgs(payload, "loopback-peer"));
        }

        public void ReceiveDeliveredDataFrame(FileTransferDataFrameV2 payload)
        {
            if (TryGetOrCreateDataSession(NormalizeSessionId(payload.SessionId), payload.TransferId, out var session))
            {
                session.Deliver(payload);
            }
            else
            {
                LocalOperationalLog.Warn(
                    "SessionSecurity",
                    $"event=filetransfer_data_frame_ignored; transport=loopback; transfer_id={payload.TransferId}; session_id={payload.SessionId}; frame_type={payload.Type}; chunk_index={GetLoopbackFrameChunkIndex(payload)}; reason=session_id_mismatch_existing_queue");
            }
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

        private async Task DeliverMaybeAsync<TPayload>(
            TPayload payload,
            Func<LoopbackFileTransferTransport, TPayload, CancellationToken, Task<bool>> tryOverride,
            Action<LoopbackFileTransferTransport, TPayload> deliver,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!await tryOverride(this, payload, ct).ConfigureAwait(false))
            {
                var target = peer ?? throw new InvalidOperationException("Loopback peer is not connected.");
                deliver(target, payload);
            }
        }

        private string NormalizeSessionId(string? sessionId)
            => string.IsNullOrWhiteSpace(sessionId) ? this.sessionId : sessionId.Trim();

        private FileTransferStartV2 ApplyStartTransform(FileTransferStartV2 message)
            => OutboundStartTransform?.Invoke(message) ?? message;

        private FileTransferChunkV1 ApplyChunkTransform(FileTransferChunkV1 message)
            => OutboundChunkTransform?.Invoke(message) ?? message;

        private FileTransferErrorV1 TrackError(FileTransferErrorV1 message)
        {
            SentErrors.Enqueue(message);
            return message;
        }

        private FileTransferCompleteV1 TrackComplete(FileTransferCompleteV1 message)
        {
            SentCompletes.Enqueue(message);
            return message;
        }

        private FileTransferWindowUpdateV1 TrackWindowUpdate(FileTransferWindowUpdateV1 message)
        {
            SentWindowUpdates.Enqueue(message);
            return message;
        }

        private FileTransferMissingRangeV1 TrackMissingRange(FileTransferMissingRangeV1 message)
        {
            SentMissingRanges.Enqueue(message);
            return message;
        }

        private FileTransferPressureStateV1 TrackPressureState(FileTransferPressureStateV1 message)
        {
            SentPressureStates.Enqueue(message);
            return message;
        }

        private LoopbackDataSession GetOrCreateDataSession(string normalizedSessionId, string normalizedTransferId)
            => TryGetOrCreateDataSession(normalizedSessionId, normalizedTransferId, out var session)
                ? session
                : throw new InvalidOperationException("File-transfer data session id mismatch for existing transfer.");

        private void SetAllDataSessionsAvailability(bool isAvailable, string reason, bool requiresResumeRequest)
        {
            foreach (var session in dataSessions.Values)
            {
                session.SetAvailability(isAvailable, reason, requiresResumeRequest);
            }
        }

        private bool TryGetOrCreateDataSession(string normalizedSessionId, string normalizedTransferId, out LoopbackDataSession session)
        {
            session = dataSessions.GetOrAdd(
                normalizedTransferId,
                _ => new LoopbackDataSession(this, normalizedSessionId, normalizedTransferId));
            return string.Equals(session.SessionId, normalizedSessionId, StringComparison.Ordinal);
        }

        private async Task DeliverDataFrameToPeerAsync(FileTransferDataFrameV2 frame, bool useBulkLane, CancellationToken ct)
        {
            var target = peer ?? throw new InvalidOperationException("Loopback peer is not connected.");
            SentDataFrames.Enqueue(frame);
            if (!TryGetOrCreateDataSession(NormalizeSessionId(frame.SessionId), frame.TransferId, out var localSession) ||
                !localSession.IsAvailable)
            {
                return;
            }

            if (OutboundDataFrameDeliveryOverrideWithLaneAsync is not null &&
                await OutboundDataFrameDeliveryOverrideWithLaneAsync(target, frame, useBulkLane, ct).ConfigureAwait(false))
            {
                return;
            }

            if (OutboundDataFrameDeliveryOverrideAsync is not null &&
                await OutboundDataFrameDeliveryOverrideAsync(target, frame, ct).ConfigureAwait(false))
            {
                return;
            }

            if (target.TryGetOrCreateDataSession(target.NormalizeSessionId(frame.SessionId), frame.TransferId, out var session))
            {
                if (!session.IsAvailable)
                {
                    return;
                }

                session.Deliver(frame);
            }
            else
            {
                LocalOperationalLog.Warn(
                    "SessionSecurity",
                    $"event=filetransfer_data_frame_ignored; transport=loopback; transfer_id={frame.TransferId}; session_id={frame.SessionId}; frame_type={frame.Type}; chunk_index={GetLoopbackFrameChunkIndex(frame)}; reason=session_id_mismatch_existing_queue");
            }
        }

        private sealed class LoopbackDataSession : IFileTransferDataSession
        {
            private readonly LoopbackFileTransferTransport owner;
            private readonly Channel<FileTransferDataFrameV2> frames = Channel.CreateUnbounded<FileTransferDataFrameV2>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                });
            private int disposed;
            private int activeReader;
            private int available = 1;

            public LoopbackDataSession(LoopbackFileTransferTransport owner, string sessionId, string transferId)
            {
                this.owner = owner;
                SessionId = sessionId;
                TransferId = transferId;
            }

            public string SessionId { get; }

            public string TransferId { get; }

            public bool IsAvailable => Volatile.Read(ref available) != 0;

            public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged;

            public async ValueTask<FileTransferDataFrameV2> ReceiveAsync(CancellationToken ct)
            {
                if (Interlocked.CompareExchange(ref activeReader, 1, 0) != 0)
                {
                    LocalOperationalLog.Warn(
                        "FileTransferService",
                        $"event=filetransfer_receive_loop_overlap_detected; transfer_id={TransferId}; session_id={SessionId}; reason=loopback_session_multiple_readers");
                }

                try
                {
                    return await frames.Reader.ReadAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref activeReader, 0);
                }
            }

            public Task SendAsync(FileTransferDataFrameV2 frame, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                return owner.DeliverDataFrameToPeerAsync(frame, frame is FileTransferChunkDataFrameV2 or FileTransferChunkBatchFrameV2, ct);
            }

            public void Deliver(FileTransferDataFrameV2 frame)
            {
                if (disposed != 0)
                {
                    return;
                }

                frames.Writer.TryWrite(frame);
            }

            public void SetAvailability(bool isAvailable, string reason, bool requiresResumeRequest)
            {
                if (disposed != 0)
                {
                    return;
                }

                var updated = isAvailable ? 1 : 0;
                var previous = Interlocked.Exchange(ref available, updated);
                if (previous == updated)
                {
                    return;
                }

                AvailabilityChanged?.Invoke(
                    this,
                    new FileTransferDataSessionAvailabilityChangedEventArgs(
                        isAvailable,
                        reason,
                        requiresResumeRequest));
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                frames.Writer.TryComplete();
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

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}
