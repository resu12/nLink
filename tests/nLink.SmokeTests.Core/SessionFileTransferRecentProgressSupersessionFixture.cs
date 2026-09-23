using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    internal static async Task<object> CreateJoinedRecentProgressForTestAsync(SessionFileTransferService service, object context)
    {
        var type = typeof(SessionFileTransferService);
        var logStart = GetOperationalLogLength();
        var request = type.GetMethod("SendInboundV6FrontierRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.False(await Assert.IsAssignableFrom<Task<bool>>(request.Invoke(service, [context, "chunk_batch_committed", false])));
        await WaitUntilAsync(() => GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract") is { } current &&
            GetPrivateProperty<bool>(GetPrivatePropertyValue(current, "Lifecycle")!, "DispatchObserved"), timeoutMs: 5000);
        var contract = GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract")!;
        var authority = GetPrivateProperty<FallbackFrontierRepairAuthority>(contract, "Authority");
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Contains("event=filetransfer_v6_frontier_request_sent;", StringComparison.Ordinal) &&
                line.Contains($"authority_id={authority.AuthorityId};", StringComparison.Ordinal)), timeoutMs: 5000);

        // One physical sparse write supplies progress. No progress receipt,
        // coordinator decision, timestamp or terminal outcome is fabricated.
        const int chunkSize = 21 * 1024;
        using var destination = new MemoryStream(new byte[256 * chunkSize], writable: true);
        var chunk = Enumerable.Repeat((byte)0xa5, chunkSize).ToArray();
        var batch = new FileTransferChunkBatchFrameV6
        {
            SessionId = GetPrivateProperty<string>(context, "SessionId"),
            TransferId = GetPrivateProperty<string>(context, "TransferId"),
            TransportEpoch = authority.TransportEpoch,
            StartChunkIndex = 100, ChunkCount = 1, RepairRequestId = authority.ProtocolRepairRequestId,
            BatchId = "recent-supersession-unit", DataSegments = [chunk],
        };
        await Assert.IsAssignableFrom<Task>(type.GetMethod("HandleInboundV6SparseChunkBatchAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
            [context, batch, new List<(int ChunkIndex, byte[] ChunkBytes)> { (100, chunk) }, destination,
                FileTransferTransportKind.RegularNkn, contract]));
        Assert.Equal(101, GetPrivateProperty<int>(context, "NextChunkIndex"));
        Assert.Equal(chunk, destination.ToArray().Skip(100 * chunkSize).Take(chunkSize));
        Assert.Equal("ProgressObserved", GetPrivatePropertyValue(GetPrivatePropertyValue(contract, "Lifecycle")!, "SettlementOutcome")?.ToString());
        var sparseBatch = new FileTransferChunkBatchFrameV6
        {
            SessionId = batch.SessionId, TransferId = batch.TransferId, TransportEpoch = authority.TransportEpoch,
            StartChunkIndex = 102, ChunkCount = 1, BatchId = "recent-supersession-sparse-unit", DataSegments = [chunk],
        };
        await Assert.IsAssignableFrom<Task>(type.GetMethod("HandleInboundV6SparseChunkBatchAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
            [context, sparseBatch, new List<(int ChunkIndex, byte[] ChunkBytes)> { (102, chunk) }, destination,
                FileTransferTransportKind.RegularNkn, null]));
        Assert.Equal(101, GetPrivateProperty<int>(context, "NextChunkIndex"));
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains(
            "reason=frontier_stalled; next_chunk_index=101;", StringComparison.Ordinal), timeoutMs: 1000);
        var initialWake = GetPrivatePropertyValue(context, "CurrentV6FrontierRecentProgressGraceWake")!;
        foreach (var index in new[] { 254, 255 })
        {
            var rearmBatch = new FileTransferChunkBatchFrameV6
            {
                SessionId = batch.SessionId, TransferId = batch.TransferId, TransportEpoch = authority.TransportEpoch,
                StartChunkIndex = index, ChunkCount = 1, BatchId = $"recent-supersession-rearm-{index}", DataSegments = [chunk],
            };
            await Assert.IsAssignableFrom<Task>(type.GetMethod("HandleInboundV6SparseChunkBatchAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
                [context, rearmBatch, new List<(int ChunkIndex, byte[] ChunkBytes)> { (index, chunk) }, destination,
                    FileTransferTransportKind.RegularNkn, null]));
        }
        var finalWake = GetPrivatePropertyValue(context, "CurrentV6FrontierRecentProgressGraceWake")!;
        Assert.Equal(GetPrivateProperty<DateTimeOffset>(initialWake, "DueUtc"), GetPrivateProperty<DateTimeOffset>(finalWake, "DueUtc"));
        Assert.Equal(GetPrivateProperty<long>(initialWake, "WakeGeneration") + 2, GetPrivateProperty<long>(finalWake, "WakeGeneration"));
        return GetPrivatePropertyValue(context, "CurrentV6FrontierRecentProgressGraceWake")!;
    }

    internal static async Task AssertJoinedRecentProgressSupersededForTestAsync(
        SessionFileTransferService service, object context, object wake, int logStart)
    {
        var due = GetPrivateProperty<DateTimeOffset>(wake, "DueUtc");
        var before = CaptureJoinedNknStateForTest(service, context);
        Assert.Equal(FileTransferRoute.FileTunaV4, before.RouteSelection.Route);
        Assert.True(DateTimeOffset.UtcNow < due, "The fixture must complete its real handoff before the unchanged one-second grace.");
        var generation = GetPrivateProperty<long>(wake, "WakeGeneration");
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Contains("event=filetransfer_v6_frontier_recent_progress_grace_recheck_decided;", StringComparison.Ordinal) &&
                line.Contains($"wake_generation={generation};", StringComparison.Ordinal) &&
                line.Contains("decision_status=stale; decision_reason=source_not_current;", StringComparison.Ordinal)), timeoutMs: 3000);
        var after = CaptureJoinedNknStateForTest(service, context);
        Assert.Equal(before.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
        Assert.Equal(before.CurrentRuntimeOwner, after.CurrentRuntimeOwner);
        Assert.Equal(before.LastRepairReplayGeneration, after.LastRepairReplayGeneration);
        Assert.Equal(101, GetPrivateProperty<int>(context, "NextChunkIndex"));
    }
}
