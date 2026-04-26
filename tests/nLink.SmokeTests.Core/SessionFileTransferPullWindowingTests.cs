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
public sealed class SessionFileTransferPullWindowingTests : SessionFileTransferServiceTestBase
{
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
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-streaming.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 15000);
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
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-tuned-healthy.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);
        Assert.Equal(payload, destination.ToArray());
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        Assert.Equal(40 * 1024, manifest.ChunkSizeBytes);
        var maxGrantWindowBytes = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Select(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes).DefaultIfEmpty(0).Max();
        Assert.True(maxGrantWindowBytes >= (2 * 1024 * 1024) - manifest.ChunkSizeBytes, $"Expected a healthy V3 grant window near 2 MiB, but saw {maxGrantWindowBytes} bytes.");
        Assert.Empty(receiverTransport.SentWindowUpdates);
        Assert.True(receiverTransport.SentPressureStates.Count <= 3, $"Expected V3 healthy flow to keep pressure chatter low, but saw {receiverTransport.SentPressureStates.Count} pressure-state messages.");
    }

    [Fact]
    public async Task PullSession_V3Streaming_OnConservativeNknStartup_UsesReducedStartupChunkAndGrantWindow()
    {
        const string transferId = "transfer_service_pull_v3_nkn_conservative_startup";
        var payload = Enumerable.Range(0, 3_500_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_nkn_conservative_startup")
        {
            SupportsFileTransferV3Streaming = true,
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_nkn_conservative_startup")
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
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-nkn-conservative-startup.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 25000);
        Assert.Equal(payload, destination.ToArray());
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        Assert.Equal(24 * 1024, manifest.ChunkSizeBytes);
        var firstGrantWindow = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().FirstOrDefault();
        Assert.NotNull(firstGrantWindow);
        var firstGrantWindowBytes = (firstGrantWindow!.GrantedUntilChunkIndexExclusive - firstGrantWindow.NextExpectedChunkIndex) * manifest.ChunkSizeBytes;
        Assert.True(firstGrantWindowBytes <= (512 * 1024) + (manifest.ChunkSizeBytes * 2), $"Expected conservative NKN startup to keep the initial grant window near 512 KiB, but saw {firstGrantWindowBytes} bytes.");
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("profile=nkn_conservative_startup", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullSession_V3Streaming_OnConservativeNknStartup_DegradesEarlierUnderRepairChurn()
    {
        const string transferId = "transfer_service_pull_v3_nkn_conservative_repair";
        var payload = Enumerable.Range(0, 2_500_000).Select(static i => (byte)(i % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_pull_v3_nkn_conservative_repair")
        {
            SupportsFileTransferV3Streaming = true,
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_pull_v3_nkn_conservative_repair")
        {
            SupportsFileTransferV3Streaming = true,
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.Connect(receiverTransport);
        var delayedChunkGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedFrames = new ConcurrentQueue<(LoopbackFileTransferTransport Target, FileTransferDataFrameV2 Frame)>();
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, ct) =>
        {
            if (!delayedChunkGate.Task.IsCompleted && frame is FileTransferChunkDataFrameV3 or FileTransferChunkBatchFrameV3)
            {
                delayedFrames.Enqueue((target, frame));
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var logStart = GetOperationalLogLength();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-nkn-conservative-repair.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferRepairRequestFrameV3>().Any(), timeoutMs: 15000);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("profile=nkn_conservative_startup_degraded", StringComparison.Ordinal), timeoutMs: 8000);
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        var maxGrantWindowBytesBeforeRelease = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Select(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes).DefaultIfEmpty(0).Max();
        Assert.True(maxGrantWindowBytesBeforeRelease < 2 * 1024 * 1024, $"Expected conservative NKN startup under repair churn to stay below the old 2 MiB healthy window, but saw {maxGrantWindowBytesBeforeRelease} bytes.");
        delayedChunkGate.TrySetResult(true);
        while (delayedFrames.TryDequeue(out var delayed))
        {
            delayed.Target.ReceiveDeliveredDataFrame(delayed.Frame);
        }

        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 25000);
        Assert.Equal(payload, destination.ToArray());
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
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-tuned-expand.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 30000);
        Assert.Equal(payload, destination.ToArray());
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        Assert.Equal(40 * 1024, manifest.ChunkSizeBytes);
        var maxGrantWindowBytes = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Select(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes).DefaultIfEmpty(0).Max();
        Assert.True(maxGrantWindowBytes >= (4 * 1024 * 1024) - manifest.ChunkSizeBytes, $"Expected V3 healthy flow to step up near 4 MiB, but saw {maxGrantWindowBytes} bytes.");
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
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-tuned-screenshare.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);
        Assert.Equal(payload, destination.ToArray());
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        Assert.Equal(24 * 1024, manifest.ChunkSizeBytes);
        var maxGrantWindowBytes = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Select(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes).DefaultIfEmpty(0).Max();
        Assert.True(maxGrantWindowBytes >= (256 * 1024) - manifest.ChunkSizeBytes, $"Expected balanced screenshare V3 grant window near 256 KiB, but saw {maxGrantWindowBytes} bytes.");
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
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-midstream-screenshare-clamp.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Any(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * 40 * 1024 >= (2 * 1024 * 1024) - (40 * 1024)), timeoutMs: 10000);
        var grantCountBeforeScreenshare = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Count();
        sender.SetSessionScreenShareActive(true);
        receiver.SetSessionScreenShareActive(true);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Skip(grantCountBeforeScreenshare).Any(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * 40 * 1024 <= 256 * 1024), timeoutMs: 10000);
        var reducedGrant = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Skip(grantCountBeforeScreenshare).First(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * 40 * 1024 <= 256 * 1024);
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
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-tuned-degraded.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);
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
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-batch-no-duplicates.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 15000);
        Assert.Equal(payload, destination.ToArray());
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        var batches = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV3>().ToList();
        Assert.Contains(batches, static batch => batch.DataSegments.Count > 2);
        var sentChunkIndices = senderTransport.SentDataFrames.Where(static frame => frame is FileTransferChunkDataFrameV3 or FileTransferChunkBatchFrameV3).SelectMany(frame => frame switch
        {
            FileTransferChunkDataFrameV3 chunk => [chunk.ChunkIndex],
            FileTransferChunkBatchFrameV3 batch => Enumerable.Range(batch.StartChunkIndex, batch.DataSegments.Count),
            _ => Enumerable.Empty<int>(),
        }).OrderBy(static chunkIndex => chunkIndex).ToList();
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
            if (frame is FileTransferChunkDataFrameV3 chunk && chunk.TransferId == transferId && chunk.ChunkIndex is >= 90 and < 130)
            {
                delayedFrames.Enqueue(frame);
                if (Interlocked.Exchange(ref releaseStarted, 1) == 0)
                {
                    _ = Task.Run(async () =>
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
                    }, CancellationToken.None);
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
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("pull-v3-reorder-limited.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 35000);
        Assert.Equal(payload, destination.ToArray());
        var manifest = Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV3>());
        var grantWindows = receiverTransport.SentDataFrames.OfType<FileTransferGrantWindowFrameV3>().Select(frame => (frame.GrantedUntilChunkIndexExclusive - frame.NextExpectedChunkIndex) * manifest.ChunkSizeBytes).ToList();
        var expandedIndex = grantWindows.FindIndex(windowBytes => windowBytes >= (4 * 1024 * 1024) - manifest.ChunkSizeBytes);
        Assert.True(expandedIndex >= 0, "Expected the transfer to reach the healthy expanded 4 MiB window before the reorder clamp.");
        Assert.Contains(grantWindows.Skip(expandedIndex + 1), windowBytes => windowBytes <= (2 * 1024 * 1024) + manifest.ChunkSizeBytes);
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
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("send-ahead-clamp.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()), CancellationToken.None);
        await WaitUntilAsync(() => Volatile.Read(ref sentChunkCount) >= 32, timeoutMs: 5000);
        await Task.Delay(400);
        Assert.Equal(32, Volatile.Read(ref sentChunkCount));
        Assert.Equal(31, Volatile.Read(ref highestSentChunkIndex));
        var logTail = ReadOperationalLogTail(logStartIndex);
        Assert.Contains("event=window_send_ahead_clamped", logTail, StringComparison.Ordinal);
        Assert.Contains("effective_send_limit=32", logTail, StringComparison.Ordinal);
        await sender.CancelTransferAsync(transferId, "test_done", CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Canceled && receiver.Snapshot.InboundState == FileTransferTransferState.Canceled);
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
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("screenshare-startup-cap.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(new NonDisposingMemoryStream()), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferRequestChunksFrameV2>().Any(static frame => frame.StartChunkIndex == 0 && frame.RequestedChunkCount == 3 && frame.PipelineDepth == 3), timeoutMs: 5000);
        await Task.Delay(400);
        Assert.DoesNotContain(receiverTransport.SentDataFrames.OfType<FileTransferRequestChunksFrameV2>(), static frame => frame.RequestedChunkCount > 3);
        await sender.CancelTransferAsync(transferId, "test_done", CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Canceled && receiver.Snapshot.InboundState == FileTransferTransferState.Canceled, timeoutMs: 12000);
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
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("screenshare-active-profile.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferRequestChunksFrameV2>().Any(static frame => frame.StartChunkIndex == 0 && frame.RequestedChunkCount == 3 && frame.PipelineDepth == 3), timeoutMs: 5000);
        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferRequestChunksFrameV2>().Any(static frame => frame.StartChunkIndex > 0 && frame.RequestedChunkCount == 2 && frame.PipelineDepth == 3), timeoutMs: 5000);
        Assert.Equal(24576, receiver.Snapshot.Inbound?.ChunkSizeBytes);
        await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Completed && receiver.Snapshot.InboundState == FileTransferTransferState.Completed, timeoutMs: 12000);
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
        var started = await sender.TryStartSendAsync(new FileTransferSendDescriptor("pooled.bin", payload.Length, transferId, ChunkSizeBytes: 24_576), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        Assert.NotNull(started);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed);
        Assert.Equal([24_576, 24_576, 1], observedChunkSizes);
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
                if (message.ChunkIndex == droppedChunkIndex && Interlocked.CompareExchange(ref initialDrop, 1, 0) == 0)
                {
                    return Task.FromResult(true);
                }

                if (message.ChunkIndex == droppedChunkIndex && Volatile.Read(ref allowRepairDelivery) == 0)
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
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("gap-grant-suppression.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentMissingRanges.Any(static range => range.StartChunkIndex == droppedChunkIndex), timeoutMs: 5000);
        await WaitUntilAsync(() => Volatile.Read(ref repairChunkHeld) != 0, timeoutMs: 5000);
        Assert.All(receiverTransport.SentWindowUpdates.Where(update => update.NextExpectedChunkIndex == droppedChunkIndex), update => Assert.True(update.GrantedUntilChunkIndexExclusive <= initialSteadyStateGrant));
        var deferredLogTail = ReadOperationalLogTail(logStartIndex);
        Assert.Contains("event=window_extension_deferred_due_to_gap", deferredLogTail, StringComparison.Ordinal);
        Interlocked.Exchange(ref allowRepairDelivery, 1);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact(Skip = "Obsolete gap-progress ack coverage after file-transfer pipeline refactors.")]
    public async Task OpenGap_AdvancingContiguousProgress_EmitsGapProgressAckWithoutGrantGrowth()
    {
        const string transferId = "transfer_service_gap_progress_ack";
        const int chunkSizeBytes = 1024;
        var droppedChunkIndices = new HashSet<int>
        {
            129,
            130,
            131
        };
        var droppedSequentialChunks = new HashSet<int>();
        var allowFinalRepairChunk = 0;
        var logStartIndex = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_gap_progress_ack")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (droppedChunkIndices.Contains(message.ChunkIndex) && droppedSequentialChunks.Add(message.ChunkIndex))
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
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("gap-progress-ack.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentWindowUpdates.Any(update => update.NextExpectedChunkIndex == 129), timeoutMs: 5000);
        var deferredGrant = receiverTransport.SentWindowUpdates.Where(update => update.NextExpectedChunkIndex == 129).Select(update => update.GrantedUntilChunkIndexExclusive).Last();
        await WaitUntilAsync(() => receiverTransport.SentWindowUpdates.Any(update => update.NextExpectedChunkIndex >= 131 && update.GrantedUntilChunkIndexExclusive == deferredGrant), timeoutMs: 5000);
        var logTail = ReadOperationalLogTail(logStartIndex);
        Assert.Contains("reason=gap_progress_ack", logTail, StringComparison.Ordinal);
        Interlocked.Exchange(ref allowFinalRepairChunk, 1);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);
        Assert.Equal(payload, destination.ToArray());
    }

    [Fact(Skip = "Obsolete repair batching coverage after file-transfer pipeline refactors.")]
    public async Task MissingRange_RepairMode_SendsTwoChunkBatchBeforeWaitingForAck()
    {
        const string transferId = "transfer_service_repair_batch";
        const int chunkSizeBytes = 1024;
        var droppedChunkIndices = new HashSet<int>
        {
            129,
            130,
            131,
            132
        };
        var initiallyDropped = new HashSet<int>();
        var missingRangeObserved = 0;
        var repairChunkIndices = new List<int>();
        var repairGate = new object ();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_repair_batch")
        {
            OutboundChunkDeliveryOverrideAsync = (target, message, _) =>
            {
                if (droppedChunkIndices.Contains(message.ChunkIndex) && initiallyDropped.Add(message.ChunkIndex))
                {
                    return Task.FromResult(true);
                }

                if (Volatile.Read(ref missingRangeObserved) != 0 && message.ChunkIndex >= 129 && message.ChunkIndex <= 132)
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
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("repair-batch.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
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
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_degraded_repair_cap");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var payload = Enumerable.Range(0, chunkSizeBytes * 700).Select(static i => (byte)(i % 251)).ToArray();
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("degraded-repair-cap.bin", payload.Length, transferId, ChunkSizeBytes: chunkSizeBytes), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(() => receiverTransport.SentMissingRanges.Count(static range => range.StartChunkIndex == droppedChunkIndex) >= 3, timeoutMs: 5000);
        Interlocked.Exchange(ref allowRepairDelivery, 1);
        await WaitUntilAsync(() => receiverTransport.SentWindowUpdates.Any(update => update.NextExpectedChunkIndex >= droppedChunkIndex && update.GrantedUntilChunkIndexExclusive - update.NextExpectedChunkIndex <= 32), timeoutMs: 10000);
        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 20000);
        Assert.Equal(payload, destination.ToArray());
    }

}
