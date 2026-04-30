using System.Collections.Concurrent;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferV3RepairSetTests : SessionFileTransferServiceTestBase
{
    [Theory]
    [InlineData(null)]
    [InlineData("BenignUntilProven")]
    public async Task PullSession_V3NknSparseFrontierGap_DefaultAndBenignGrace_RequestProactiveRepair_AndAvoidHardLimit(string? pressureMode)
    {
        const string transferId = "transfer_service_v3_proactive_frontier_gap";
        var previousPolicy = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY");
        var previousProactiveRepair = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR");
        var previousFrontierRepairMinGapMs = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_MIN_GAP_MS");
        var previousFrontierRepairRepeatMs = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_REPEAT_MS");
        var previousFrontierRepairChunks = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_CHUNKS");
        var previousProactiveRepairPressureMode = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_REPAIR_PRESSURE_MODE");
        var previousProactiveRepairGraceMs = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_REPAIR_GRACE_MS");
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", "SparseTolerant");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_REPAIR_PRESSURE_MODE", pressureMode);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_REPAIR_GRACE_MS", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_MIN_GAP_MS", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_REPEAT_MS", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_CHUNKS", null);
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "Current");
        var logStart = ReadOperationalLogText().Length;

        try
        {
            var payload = Enumerable.Range(0, 4 * 1024 * 1024).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_service_v3_proactive_frontier_gap")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            using var receiverTransport = new LoopbackFileTransferTransport("session_service_v3_proactive_frontier_gap")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            senderTransport.Connect(receiverTransport);

            var droppedChunk = 20;
            var droppedOnce = 0;
            senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, _) =>
            {
                if (frame is FileTransferChunkDataFrameV3 chunk &&
                    chunk.ChunkIndex == droppedChunk &&
                    Interlocked.CompareExchange(ref droppedOnce, 1, 0) == 0)
                {
                    return Task.FromResult(true);
                }

                if (frame is FileTransferChunkBatchFrameV3 batch &&
                    batch.StartChunkIndex <= droppedChunk &&
                    batch.StartChunkIndex + batch.DataSegments.Count > droppedChunk &&
                    Interlocked.CompareExchange(ref droppedOnce, 1, 0) == 0)
                {
                    for (var offset = 0; offset < batch.DataSegments.Count; offset++)
                    {
                        var chunkIndex = batch.StartChunkIndex + offset;
                        if (chunkIndex == droppedChunk)
                        {
                            continue;
                        }

                        target.ReceiveDeliveredDataFrame(new FileTransferChunkDataFrameV3
                        {
                            SessionId = batch.SessionId,
                            TransferId = batch.TransferId,
                            ChunkIndex = chunkIndex,
                            ChunkCount = batch.ChunkCount,
                            Data = batch.DataSegments[offset],
                        });
                    }

                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            };

            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("v3-proactive-frontier-gap.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            using var destination = new NonDisposingMemoryStream();
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_frontier_gap_repair_requested", StringComparison.Ordinal),
                timeoutMs: 10000);
            var repair = receiverTransport.SentDataFrames.OfType<FileTransferRepairRequestFrameV3>().First();
            Assert.Equal(droppedChunk, repair.StartChunkIndex);
            Assert.InRange(repair.RequestedChunkCount, 1, 32);
            Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferRequestChunksFrameV2);

            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 30000);
            Assert.Equal(payload, destination.ToArray());

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_frontier_gap_repair_eligible", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_frontier_gap_repair_requested", logTail, StringComparison.Ordinal);
            Assert.Contains($"repair_request_key={droppedChunk}:", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_frontier_gap_repair_sender_received", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_frontier_gap_repair_sender_scheduled", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_frontier_gap_repair_sender_sent", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_frontier_gap_repair_filled", logTail, StringComparison.Ordinal);
            if (string.Equals(pressureMode, "BenignUntilProven", StringComparison.Ordinal))
            {
                Assert.Contains("proactive_repair_pressure_state=benign_grace", logTail, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("proactive_repair_pressure_state=repeated_unfilled", logTail, StringComparison.Ordinal);
                Assert.DoesNotContain("proactive_repair_pressure_state=immediate_pressure", logTail, StringComparison.Ordinal);
            }

            Assert.Contains("grant_policy_after_repair=", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("updated_profile=healthy_limited", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("reason=proactive_gap_repeated", logTail, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", previousPolicy);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR", previousProactiveRepair);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_MIN_GAP_MS", previousFrontierRepairMinGapMs);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_REPEAT_MS", previousFrontierRepairRepeatMs);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_CHUNKS", previousFrontierRepairChunks);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_REPAIR_PRESSURE_MODE", previousProactiveRepairPressureMode);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_REPAIR_GRACE_MS", previousProactiveRepairGraceMs);
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
        }
    }

    [Fact]
    public async Task PullSession_V3NknSparseFrontierGap_BenignUntilProven_HardLimitsAfterGrace()
    {
        const string transferId = "transfer_service_v3_proactive_frontier_grace_expired";
        var previousPolicy = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY");
        var previousProactiveRepair = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR");
        var previousFrontierRepairMinGapMs = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_MIN_GAP_MS");
        var previousFrontierRepairRepeatMs = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_REPEAT_MS");
        var previousProactiveRepairPressureMode = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_REPAIR_PRESSURE_MODE");
        var previousProactiveRepairGraceMs = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_REPAIR_GRACE_MS");
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", "SparseTolerant");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_MIN_GAP_MS", "500");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_REPEAT_MS", "500");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_REPAIR_PRESSURE_MODE", "BenignUntilProven");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_REPAIR_GRACE_MS", "500");
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "Current");
        var logStart = ReadOperationalLogText().Length;

        try
        {
            var payload = Enumerable.Range(0, 4 * 1024 * 1024).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_service_v3_proactive_frontier_grace_expired")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            using var receiverTransport = new LoopbackFileTransferTransport("session_service_v3_proactive_frontier_grace_expired")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            senderTransport.Connect(receiverTransport);

            var droppedChunk = 20;
            var allowDroppedChunk = 0;
            senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, _) =>
            {
                if (Volatile.Read(ref allowDroppedChunk) != 0)
                {
                    return Task.FromResult(false);
                }

                if (frame is FileTransferChunkDataFrameV3 chunk && chunk.ChunkIndex == droppedChunk)
                {
                    return Task.FromResult(true);
                }

                if (frame is FileTransferChunkBatchFrameV3 batch &&
                    batch.StartChunkIndex <= droppedChunk &&
                    batch.StartChunkIndex + batch.DataSegments.Count > droppedChunk)
                {
                    for (var offset = 0; offset < batch.DataSegments.Count; offset++)
                    {
                        var chunkIndex = batch.StartChunkIndex + offset;
                        if (chunkIndex == droppedChunk)
                        {
                            continue;
                        }

                        target.ReceiveDeliveredDataFrame(new FileTransferChunkDataFrameV3
                        {
                            SessionId = batch.SessionId,
                            TransferId = batch.TransferId,
                            ChunkIndex = chunkIndex,
                            ChunkCount = batch.ChunkCount,
                            Data = batch.DataSegments[offset],
                        });
                    }

                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            };

            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("v3-proactive-frontier-grace-expired.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            using var destination = new NonDisposingMemoryStream();
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

            await WaitUntilAsync(
                () =>
                {
                    var logTail = ReadOperationalLogTail(logStart);
                    return (logTail.Contains("proactive_repair_pressure_state=repeated_unfilled", StringComparison.Ordinal) ||
                            logTail.Contains("proactive_repair_pressure_state=hard_gap_stall", StringComparison.Ordinal)) &&
                           (logTail.Contains("updated_profile=healthy_limited", StringComparison.Ordinal) ||
                            logTail.Contains("current_profile=healthy_limited", StringComparison.Ordinal) ||
                            logTail.Contains("profile=healthy_limited", StringComparison.Ordinal));
                },
                timeoutMs: 15000);
            Volatile.Write(ref allowDroppedChunk, 1);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 30000);
            Assert.Equal(payload, destination.ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", previousPolicy);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR", previousProactiveRepair);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_MIN_GAP_MS", previousFrontierRepairMinGapMs);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_REPEAT_MS", previousFrontierRepairRepeatMs);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_REPAIR_PRESSURE_MODE", previousProactiveRepairPressureMode);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_REPAIR_GRACE_MS", previousProactiveRepairGraceMs);
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
        }
    }

    [Fact]
    public async Task PullSession_V3NknSparseFrontierGap_ImmediatePressureRollback_HardLimits()
    {
        const string transferId = "transfer_service_v3_proactive_frontier_immediate_pressure";
        var previousPolicy = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY");
        var previousProactiveRepair = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR");
        var previousProactiveRepairPressureMode = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_REPAIR_PRESSURE_MODE");
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", "SparseTolerant");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_REPAIR_PRESSURE_MODE", "ImmediatePressure");
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "Current");
        var logStart = ReadOperationalLogText().Length;

        try
        {
            var payload = Enumerable.Range(0, 4 * 1024 * 1024).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_service_v3_proactive_frontier_immediate_pressure")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            using var receiverTransport = new LoopbackFileTransferTransport("session_service_v3_proactive_frontier_immediate_pressure")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            senderTransport.Connect(receiverTransport);

            var droppedChunk = 20;
            var droppedOnce = 0;
            senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, _) =>
            {
                if (frame is FileTransferChunkDataFrameV3 chunk &&
                    chunk.ChunkIndex == droppedChunk &&
                    Interlocked.CompareExchange(ref droppedOnce, 1, 0) == 0)
                {
                    return Task.FromResult(true);
                }

                if (frame is FileTransferChunkBatchFrameV3 batch &&
                    batch.StartChunkIndex <= droppedChunk &&
                    batch.StartChunkIndex + batch.DataSegments.Count > droppedChunk &&
                    Interlocked.CompareExchange(ref droppedOnce, 1, 0) == 0)
                {
                    for (var offset = 0; offset < batch.DataSegments.Count; offset++)
                    {
                        var chunkIndex = batch.StartChunkIndex + offset;
                        if (chunkIndex == droppedChunk)
                        {
                            continue;
                        }

                        target.ReceiveDeliveredDataFrame(new FileTransferChunkDataFrameV3
                        {
                            SessionId = batch.SessionId,
                            TransferId = batch.TransferId,
                            ChunkIndex = chunkIndex,
                            ChunkCount = batch.ChunkCount,
                            Data = batch.DataSegments[offset],
                        });
                    }

                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            };

            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("v3-proactive-frontier-immediate-pressure.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            using var destination = new NonDisposingMemoryStream();
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_frontier_gap_repair_requested", StringComparison.Ordinal),
                timeoutMs: 10000);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("proactive_repair_pressure_state=immediate_pressure", StringComparison.Ordinal) &&
                      ReadOperationalLogTail(logStart).Contains("updated_profile=healthy_limited", StringComparison.Ordinal),
                timeoutMs: 10000);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 30000);
            Assert.Equal(payload, destination.ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", previousPolicy);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR", previousProactiveRepair);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_REPAIR_PRESSURE_MODE", previousProactiveRepairPressureMode);
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
        }
    }

    [Fact]
    public async Task PullSession_V3SparseMultiGapLoss_RequestsCompactRepairSet_AndCompletes()
    {
        const string transferId = "transfer_service_v3_repair_set_sparse";
        var previousPolicy = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY");
        var previousProactiveRepair = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR");
        var previousFrontierRepairMinGapMs = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_MIN_GAP_MS");
        var previousFrontierRepairRepeatMs = Environment.GetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_REPEAT_MS");
        var previousProfile = Environment.GetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", "SparseTolerant");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR", null);
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_MIN_GAP_MS", "500");
        Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_REPEAT_MS", "500");
        Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, "Current");
        var logStart = ReadOperationalLogText().Length;

        try
        {
            var payload = Enumerable.Range(0, 5 * 1024 * 1024).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_service_v3_repair_set_sparse")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            using var receiverTransport = new LoopbackFileTransferTransport("session_service_v3_repair_set_sparse")
            {
                SupportsFileTransferV3Streaming = true,
                FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            };
            senderTransport.Connect(receiverTransport);

            var droppedOnce = new ConcurrentDictionary<int, byte>();
            var sparseLoss = new HashSet<int> { 20, 37, 38 };
            senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, _) =>
            {
                if (frame is FileTransferChunkDataFrameV3 chunk && sparseLoss.Contains(chunk.ChunkIndex) && droppedOnce.TryAdd(chunk.ChunkIndex, 0))
                {
                    return Task.FromResult(true);
                }

                if (frame is FileTransferChunkBatchFrameV3 batch &&
                    Enumerable.Range(batch.StartChunkIndex, batch.DataSegments.Count).Any(chunkIndex => sparseLoss.Contains(chunkIndex) && !droppedOnce.ContainsKey(chunkIndex)))
                {
                    for (var offset = 0; offset < batch.DataSegments.Count; offset++)
                    {
                        var chunkIndex = batch.StartChunkIndex + offset;
                        if (sparseLoss.Contains(chunkIndex) && droppedOnce.TryAdd(chunkIndex, 0))
                        {
                            continue;
                        }

                        target.ReceiveDeliveredDataFrame(new FileTransferChunkDataFrameV3
                        {
                            SessionId = batch.SessionId,
                            TransferId = batch.TransferId,
                            ChunkIndex = chunkIndex,
                            ChunkCount = batch.ChunkCount,
                            Data = batch.DataSegments[offset],
                        });
                    }

                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            };

            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("v3-repair-set-sparse.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            using var destination = new NonDisposingMemoryStream();
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

            await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferRepairRequestSetFrameV3>().Any(), timeoutMs: 15000);
            var repairSet = receiverTransport.SentDataFrames.OfType<FileTransferRepairRequestSetFrameV3>().First();
            Assert.Contains(repairSet.Ranges, static range => range.StartChunkIndex == 20 && range.RequestedChunkCount >= 1);
            Assert.Contains(repairSet.Ranges, static range => range.StartChunkIndex == 37 && range.RequestedChunkCount >= 2);
            Assert.True(repairSet.Ranges.Count <= FileTransferProtocol.MaxRepairSetRangesV3);
            Assert.True(repairSet.Ranges.Sum(static range => range.RequestedChunkCount) <= FileTransferProtocol.MaxRepairSetChunksV3);
            Assert.Contains("event=filetransfer_frontier_gap_repair_requested", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
            Assert.Contains("range_count=2", ReadOperationalLogTail(logStart), StringComparison.Ordinal);

            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 30000);
            Assert.Equal(payload, destination.ToArray());
            Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferRequestChunksFrameV2);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FILE_ONLY_REORDER_POLICY", previousPolicy);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_PROACTIVE_GAP_REPAIR", previousProactiveRepair);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_MIN_GAP_MS", previousFrontierRepairMinGapMs);
            Environment.SetEnvironmentVariable("NLINK_FILETRANSFER_V3_FRONTIER_REPAIR_REPEAT_MS", previousFrontierRepairRepeatMs);
            Environment.SetEnvironmentVariable(FileTransferPayloadEfficiencyProfile.EnvironmentVariableName, previousProfile);
        }
    }

    [Fact]
    public async Task PullSession_V3FrontierOnlyLoss_UsesSingleRangeRepair_AndDoesNotEmitV2Request()
    {
        const string transferId = "transfer_service_v3_repair_frontier";
        var payload = Enumerable.Range(0, 2 * 1024 * 1024).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_service_v3_repair_frontier")
        {
            SupportsFileTransferV3Streaming = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport("session_service_v3_repair_frontier")
        {
            SupportsFileTransferV3Streaming = true,
        };
        senderTransport.Connect(receiverTransport);

        var dropped = 0;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, _) =>
        {
            if (Interlocked.CompareExchange(ref dropped, 1, 0) != 0)
            {
                return Task.FromResult(false);
            }

            switch (frame)
            {
                case FileTransferChunkDataFrameV3 { ChunkIndex: 0 }:
                    return Task.FromResult(true);
                case FileTransferChunkBatchFrameV3 { StartChunkIndex: 0 } batch:
                    for (var offset = 1; offset < batch.DataSegments.Count; offset++)
                    {
                        target.ReceiveDeliveredDataFrame(new FileTransferChunkDataFrameV3
                        {
                            SessionId = batch.SessionId,
                            TransferId = batch.TransferId,
                            ChunkIndex = batch.StartChunkIndex + offset,
                            ChunkCount = batch.ChunkCount,
                            Data = batch.DataSegments[offset],
                        });
                    }

                    return Task.FromResult(true);
                default:
                    Interlocked.Exchange(ref dropped, 0);
                    return Task.FromResult(false);
            }
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        await sender.TryStartSendAsync(new FileTransferSendDescriptor("v3-repair-frontier.bin", payload.Length, transferId), _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        using var destination = new NonDisposingMemoryStream();
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(() => receiverTransport.SentDataFrames.OfType<FileTransferRepairRequestFrameV3>().Any(), timeoutMs: 15000);
        Assert.Empty(receiverTransport.SentDataFrames.OfType<FileTransferRepairRequestSetFrameV3>());

        await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed && receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, timeoutMs: 25000);
        Assert.Equal(payload, destination.ToArray());
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferRequestChunksFrameV2);
    }
}
