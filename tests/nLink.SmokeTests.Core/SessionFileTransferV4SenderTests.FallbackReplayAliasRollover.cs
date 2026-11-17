using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackReplayAdmission")]
    public void FallbackReplayAdmission_FinalChunkPeerAliasRollover_HoldsLocalRescueAndRetry()
    {
        const string transferId = "replay_final_chunk_alias_rollover";
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        var outbound = CreateEpoch4ReplayAdmissionContext(sender, transferId, FileTransferDirection.Outbound);
        var inbound = CreateEpoch4ReplayAdmissionContext(receiver, transferId, FileTransferDirection.Inbound);
        SetPrivateProperty(outbound, "RemoteNextExpectedChunkIndex", 255);
        SetPrivateProperty(outbound, "RemoteGrantedUntilExclusive", 256);
        SetPrivateProperty(outbound, "ChunksAcceptedForTransport", 256);
        SetPrivateProperty(outbound, "PullTransportFrontierOnlyRepairStartChunkIndex", 255);
        SetPrivateProperty(inbound, "NextChunkIndex", 255);
        SetPrivateProperty(inbound, "PullHighestReceivedChunkIndex", 254);
        StartCoordinatorSnapshotStreamForTest(outbound);
        StartCoordinatorSnapshotStreamForTest(inbound);

        var requestA = IssueFinalChunkReplayAlias(receiver, inbound, "a");
        var receiverContractA = GetPrivatePropertyValue(inbound, "CurrentFallbackFrontierRepairContract")!;
        ApplyReplayAdmissionRequest(sender, outbound, requestA);
        var senderContract = GetPrivatePropertyValue(outbound, "CurrentFallbackFrontierRepairContract")!;
        var senderAuthority = GetPrivateProperty<FallbackFrontierRepairAuthority>(senderContract, "Authority");
        MarkReplayAdmissionDispatchComplete(outbound, senderContract);

        // A stalled receiver replacement still has the same one-chunk window at EOF.
        var requestB = IssueFinalChunkReplayAlias(receiver, inbound, "b");
        var receiverContractB = GetPrivatePropertyValue(inbound, "CurrentFallbackFrontierRepairContract")!;
        Assert.NotSame(receiverContractA, receiverContractB);
        Assert.Equal(
            GetPrivateProperty<FallbackFrontierRepairAuthority>(receiverContractA, "Authority").RangeFingerprint,
            GetPrivateProperty<FallbackFrontierRepairAuthority>(receiverContractB, "Authority").RangeFingerprint);
        Assert.Equal(255, Assert.Single(requestB.MissingRanges).StartChunkIndex);
        Assert.Equal(1, Assert.Single(requestB.MissingRanges).ChunkCount);

        ApplyReplayAdmissionRequest(sender, outbound, requestB);
        Assert.Same(senderContract, GetPrivatePropertyValue(outbound, "CurrentFallbackFrontierRepairContract"));
        Assert.Equal(senderAuthority, GetPrivateProperty<FallbackFrontierRepairAuthority>(senderContract, "Authority"));
        var ordinarySendB = Assert.Single(GetQueuedV4RepairSends(outbound));
        Assert.Equal(requestB.RepairRequestId, GetStringProperty(ordinarySendB, "ProtocolRepairRequestId"));
        Assert.Equal(new[] { 255 },
            Assert.IsAssignableFrom<IEnumerable<int>>(GetPrivatePropertyValue(ordinarySendB, "ChunkIndices")));

        var batchB = new FileTransferChunkBatchFrameV6
        {
            SessionId = requestB.SessionId,
            TransferId = requestB.TransferId,
            TransportEpoch = requestB.TransportEpoch,
            RepairRequestId = requestB.RepairRequestId,
            StartChunkIndex = 255,
            ChunkCount = 1,
        };
        Assert.False(ValidateReplayAdmissionReceiverBatch(inbound, batchB with { RepairRequestId = requestA.RepairRequestId }));
        Assert.True(ValidateReplayAdmissionReceiverBatch(inbound, batchB));

        var queue = GetPrivatePropertyValue(outbound, "PullV4SenderPumpRepairQueue")!;
        queue.GetType().GetMethod("Clear")!.Invoke(queue, null);
        Assert.IsAssignableFrom<ISet<int>>(GetPrivatePropertyValue(outbound, "PullV4SenderPumpRepairQueuedChunkIndices")).Clear();
        var requests = Assert.IsAssignableFrom<System.Collections.IDictionary>(
            GetPrivatePropertyValue(outbound, "PullV4SenderPumpRepairRequests"));
        var repairState = requests[senderAuthority.CanonicalRepairKey]!;
        SetPrivateProperty(repairState, "Queued", false);
        SetPrivateProperty(repairState, "QueuedUtc", null);
        SetPrivateProperty(repairState, "InFlight", false);
        SetPrivateProperty(repairState, "LastSentUtc", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
        SetPrivateProperty(repairState, "LastSentRemoteFrontierChunkIndex", 255);
        var before = ReplayAdmissionBookkeeping(outbound);
        var logStart = GetOperationalLogLength();

        Assert.False(TryGetReplayAdmissionPeerContract(sender, outbound, out var admitted));
        Assert.Null(admitted);
        QueueReplayAdmissionSafetyReplay(sender, outbound);
        object?[] retryArguments = [outbound, ordinarySendB, null, null];
        Assert.False((bool)typeof(SessionFileTransferService).GetMethod(
            "TryRequeueOutboundFallbackFrontierV4RetryLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sender, retryArguments)!);
        Assert.Null(retryArguments[2]);
        Assert.Equal(0, retryArguments[3]);
        object?[] successorArguments = [outbound, senderContract,
            FallbackFrontierRepairSourceKind.SenderLivenessRescue, "test_final_chunk_alias_retry", null];
        Assert.False((bool)typeof(SessionFileTransferService).GetMethod(
            "TryIssueOutboundFallbackFrontierRetrySuccessorLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sender, successorArguments)!);
        Assert.Null(successorArguments[4]);

        Assert.Equal(before, ReplayAdmissionBookkeeping(outbound));
        Assert.Empty(GetQueuedV4RepairSends(outbound));
        Assert.Same(senderContract, GetPrivatePropertyValue(outbound, "CurrentFallbackFrontierRepairContract"));
        Assert.Equal(senderAuthority, GetPrivateProperty<FallbackFrontierRepairAuthority>(senderContract, "Authority"));
        Assert.False(GetPrivateProperty<bool>(GetPrivatePropertyValue(senderContract, "Lifecycle")!, "IsSettled"));
        Assert.True(ValidateReplayAdmissionReceiverBatch(inbound, batchB));
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("decision_reason=fallback_frontier_peer_request_alias_replaced", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_fallback_frontier_repair_authority_granted;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_rebind_safety_replay_rearmed;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_transport_rebind_safety_replay_started;", log, StringComparison.Ordinal);
    }

    private static FileTransferFrontierRequestFrameV6 IssueFinalChunkReplayAlias(
        SessionFileTransferService receiver, object inbound, string suffix)
    {
        var predecessor = GetPrivatePropertyValue(inbound, "CurrentFallbackFrontierRepairContract");
        FileTransferAuthorityId? predecessorId = null;
        if (predecessor is not null)
        {
            predecessorId = GetPrivateProperty<FallbackFrontierRepairAuthority>(predecessor, "Authority").AuthorityId;
            var lifecycle = GetPrivatePropertyValue(predecessor, "Lifecycle")!;
            var outcome = Enum.Parse(lifecycle.GetType().GetGenericArguments()[0], "SettledStalled");
            Assert.True((bool)lifecycle.GetType().GetMethod("TrySettle")!.Invoke(lifecycle,
                [outcome, "test_receiver_alias_rollover_without_progress", null])!);
        }

        var request = CreateReplayAdmissionRequest(inbound) with
        {
            RepairRequestId = $"v6-frontier:255:peer-{suffix}",
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 255, ChunkCount = 1 }],
        };
        var identity = typeof(SessionFileTransferService).GetMethod(
            "CreateFallbackFrontierRepairIdentity", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [request.MissingRanges, 256, 254]);
        object?[] arguments = [inbound, identity, FallbackFrontierRepairSourceKind.ReceiverRequest,
            request.RepairRequestId, null, request.Priority, request.RecoveryMode,
            "test_receiver_final_chunk_alias", predecessorId, null, null];
        var issue = typeof(SessionFileTransferService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "TryIssueFallbackFrontierRepairContractLocked" &&
                method.GetParameters()[0].ParameterType.Name == "InboundTransferContext");
        Assert.True((bool)issue.Invoke(receiver, arguments)!, arguments[10]?.ToString());
        var contract = arguments[9]!;
        SetPrivateProperty(inbound, "CurrentFallbackFrontierRepairContract", contract);
        var authority = GetPrivateProperty<FallbackFrontierRepairAuthority>(contract, "Authority");
        return request with { RequestCorrelationId = authority.RequestCorrelationId };
    }
}
