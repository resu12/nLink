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
