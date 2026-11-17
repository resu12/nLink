using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "RecentProgressDecisionClock")]
    public Task RecentProgressCommit_RecordsValidatedWakeAndCommitClockBeforeDelayedLogDelivery(bool delayLog)
        => CreateRecentProgressCommitClockEvidenceFixtureAsync(delayLog);

    internal static async Task<IReadOnlyList<string>> CreateRecentProgressCommitClockEvidenceFixtureAsync(bool delayLog)
    {
        var logStart = GetOperationalLogLength();
        string? originalArm = null;
        DateTimeOffset expectedDeadline = default;
        var lines = await CreateCheckpointRepairContinuityServiceEvidenceFixtureAsync(
            delayedDisposition: true,
            afterProgress: async (service, context, _) =>
            {
                const int chunkSize = 21 * 1024;
                using var destination = new MemoryStream(new byte[256 * chunkSize], writable: true);
                var chunk = Enumerable.Repeat((byte)0xc8, chunkSize).ToArray();
                async Task WriteChunk(int index)
                {
                    var batch = new FileTransferChunkBatchFrameV6
                    {
                        SessionId = GetPrivateProperty<string>(context, "SessionId"),
                        TransferId = GetPrivateProperty<string>(context, "TransferId"),
                        TransportEpoch = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg").TransportEpochId,
                        StartChunkIndex = index, ChunkCount = 1, DataSegments = [chunk],
                    };
                    await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
                        .GetMethod("HandleInboundV6SparseChunkBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(service, [context, batch, new List<(int, byte[])> { (index, chunk) },
                            destination, FileTransferTransportKind.RegularNkn, null]));
                    Assert.Equal(chunk, destination.ToArray().Skip(index * chunkSize).Take(chunkSize));
                }
                // A real noncontiguous write emits the frontier-stalled diagnostic
                // without advancing progress or replacing this wake's source.
                await WriteChunk(149);
                var wake = GetPrivatePropertyValue(context, "CurrentV6FrontierRecentProgressGraceWake")!;
                expectedDeadline = GetPrivateProperty<DateTimeOffset>(wake, "DueUtc");
                var generation = GetPrivateProperty<long>(wake, "WakeGeneration").ToString();
                originalArm = ReadOperationalLogTail(logStart).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Last(line => line.Contains("event=filetransfer_v6_frontier_recent_progress_grace_deferred;", StringComparison.Ordinal) &&
                        GetDelimitedLogField(line, "transfer_id") == GetPrivateProperty<string>(context, "TransferId") &&
                        GetDelimitedLogField(line, "wake_generation") == generation);
                Assert.True(DateTimeOffset.UtcNow < expectedDeadline);

                Task loggerDelay = Task.CompletedTask;
                if (delayLog)
                {
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var logGate = typeof(LocalOperationalLog).GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                    loggerDelay = Task.Run(() =>
                    {
                        lock (logGate)
                        {
                            ready.TrySetResult();
                            var delay = expectedDeadline.AddMilliseconds(20) - DateTimeOffset.UtcNow;
                            if (delay > TimeSpan.Zero) { Thread.Sleep(delay); }
                        }
                    });
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
                }
                try
                {
                    await WriteChunk(101);
                    Assert.Equal(102, GetPrivateProperty<int>(context, "NextChunkIndex"));
                }
                finally { await loggerDelay; }
            }, initialHighest: 150);

        Assert.NotNull(originalArm);
        var commit = Assert.Single(lines, line => line.Contains("event=filetransfer_v6_sparse_write_committed;", StringComparison.Ordinal) &&
            GetDelimitedLogField(line, "next_chunk_index") == "102");
        var decision = DateTimeOffset.Parse(GetDelimitedLogField(commit, "commit_decision_utc"));
        var receipt = long.Parse(GetDelimitedLogField(commit, "log_writer_attempt_utc_ticks"));
        Assert.True(decision < expectedDeadline && decision.UtcTicks <= receipt);
        if (delayLog) { Assert.True(receipt > expectedDeadline.UtcTicks); }
        Assert.Equal("current", GetDelimitedLogField(commit, "commit_source_status"));
        Assert.Equal("none", GetDelimitedLogField(commit, "authority_effect"));
        foreach (var field in new[] { "direction", "route", "protocol_version", "live_route_epoch", "runtime_generation",
            "terminal_generation", "leg_generation", "transport_epoch", "bridge_recovery_generation", "bridge_decision_generation",
            "checkpoint_request_id", "checkpoint_generation", "route_publication_generation", "route_commit_generation",
            "data_session_identity", "data_session_generation", "frontier_chunk_index", "wake_generation", "committed_progress_utc", "wake_deadline_utc" })
        {
            Assert.Equal(GetDelimitedLogField(originalArm, field), GetDelimitedLogField(commit, field));
        }
        Assert.Equal("1", GetDelimitedLogField(commit, "contiguous_chunks_committed"));
        return lines;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "RecentProgressDecisionClock")]
    public async Task RecentProgressArm_RecordsItsGuardClockBeforeDelayedLogDelivery(bool delayLog)
    {
        long expectedGeneration = 0;
        DateTimeOffset expectedDeadline = default;
        var lines = await CreateCheckpointRepairContinuityServiceEvidenceFixtureAsync(
            delayedDisposition: true,
            afterProgress: async (service, context, _) =>
            {
                var wake = GetPrivatePropertyValue(context, "CurrentV6FrontierRecentProgressGraceWake")!;
                Assert.NotNull(wake);
                expectedGeneration = GetPrivateProperty<long>(wake, "WakeGeneration") + 1;
                expectedDeadline = GetPrivateProperty<DateTimeOffset>(wake, "DueUtc");
                Assert.True(DateTimeOffset.UtcNow < expectedDeadline);

                Task loggerDelay = Task.CompletedTask;
                if (delayLog)
                {
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var logGate = typeof(LocalOperationalLog).GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                    loggerDelay = Task.Run(() =>
                    {
                        lock (logGate)
                        {
                            ready.TrySetResult();
                            var delay = expectedDeadline.AddMilliseconds(20) - DateTimeOffset.UtcNow;
                            if (delay > TimeSpan.Zero) { Thread.Sleep(delay); }
                        }
                    });
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
                }

                try
                {
                    const int chunkSize = 21 * 1024;
                    using var destination = new MemoryStream(new byte[256 * chunkSize], writable: true);
                    var chunk = Enumerable.Repeat((byte)0xb7, chunkSize).ToArray();
                    var batch = new FileTransferChunkBatchFrameV6
                    {
                        SessionId = GetPrivateProperty<string>(context, "SessionId"),
                        TransferId = GetPrivateProperty<string>(context, "TransferId"),
                        TransportEpoch = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg").TransportEpochId,
                        StartChunkIndex = 151, ChunkCount = 1, DataSegments = [chunk],
                    };
                    await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
                        .GetMethod("HandleInboundV6SparseChunkBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(service, [context, batch, new List<(int, byte[])> { (151, chunk) },
                            destination, FileTransferTransportKind.RegularNkn, null]));
                    Assert.Equal(chunk, destination.ToArray().Skip(151 * chunkSize).Take(chunkSize));
                    Assert.Equal(101, GetPrivateProperty<int>(context, "NextChunkIndex"));
                }
                finally { await loggerDelay; }
            }, initialHighest: 150);

        var arm = Assert.Single(lines, line => line.Contains("event=filetransfer_v6_frontier_recent_progress_grace_deferred;", StringComparison.Ordinal) &&
            GetDelimitedLogField(line, "wake_generation") == expectedGeneration.ToString() &&
            GetDelimitedLogField(line, "highest_received_chunk_index") == "151");
        var decision = DateTimeOffset.Parse(GetDelimitedLogField(arm, "arm_decision_utc"));
        var committed = DateTimeOffset.Parse(GetDelimitedLogField(arm, "committed_progress_utc"));
        var receipt = long.Parse(GetDelimitedLogField(arm, "log_writer_attempt_utc_ticks"));
        Assert.Equal(expectedDeadline, DateTimeOffset.Parse(GetDelimitedLogField(arm, "wake_deadline_utc")));
        Assert.Equal(committed.AddSeconds(1), expectedDeadline);
        Assert.True(decision >= committed && decision < expectedDeadline && decision.UtcTicks <= receipt);
        if (delayLog) { Assert.True(receipt >= expectedDeadline.UtcTicks); }
        Assert.Equal("rearmed", GetDelimitedLogField(arm, "decision_status"));
        Assert.Equal("none", GetDelimitedLogField(arm, "authority_effect"));
    }
}
