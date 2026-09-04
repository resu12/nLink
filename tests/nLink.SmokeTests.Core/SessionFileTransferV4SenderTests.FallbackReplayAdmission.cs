using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackReplayAdmission")]
    public void FallbackReplayAdmission_Epoch4WithoutPeerRequest_DoesNotIssueOrQueueLocalReplay()
    {
        using var sender = new SessionFileTransferService();
        var context = CreateEpoch4ReplayAdmissionContext(sender, "replay_no_peer", FileTransferDirection.Outbound);
        var before = ReplayAdmissionBookkeeping(context);

        Assert.False(TryGetReplayAdmissionPeerContract(sender, context, out _));
        QueueReplayAdmissionSafetyReplay(sender, context);
        QueueReplayAdmissionSafetyReplay(sender, context);

        Assert.Equal(before, ReplayAdmissionBookkeeping(context));
        Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
        Assert.Empty(GetQueuedV4RepairSends(context));
    }

    [Theory]
    [InlineData("settled")]
    [InlineData("advanced")]
    [InlineData("user_paused")]
    [InlineData("peer_paused")]
    [InlineData("transport_paused")]
    [InlineData("terminal")]
    [InlineData("live_epoch")]
    [InlineData("runtime_owner")]
    [InlineData("publication")]
    [InlineData("leg")]
    [InlineData("transport_epoch")]
    [InlineData("bridge_recovery")]
    [InlineData("data_session_generation")]
    [InlineData("data_session_identity")]
    [InlineData("checkpoint")]
    [InlineData("peer_binding")]
    [InlineData("wire_alias")]
    [InlineData("replaced_context")]
    [InlineData("disposed")]
    [Trait("FileTransferRuntimeSuite", "FallbackReplayAdmission")]
    public void FallbackReplayAdmission_StaleOrUnavailablePeerContract_IsReadOnlyRejected(string mutation)
    {
        using var sender = new SessionFileTransferService();
        var context = CreateEpoch4ReplayAdmissionContext(sender, "replay_stale_" + mutation, FileTransferDirection.Outbound);
        var request = CreateReplayAdmissionRequest(context);
        ApplyReplayAdmissionRequest(sender, context, request);
        var contract = GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract");
        Assert.NotNull(contract);
        MarkReplayAdmissionDispatchComplete(context, contract);

        switch (mutation)
        {
            case "settled":
                SettleReplayAdmissionContract(contract);
                break;
            case "advanced":
                SetPrivateProperty(context, "RemoteNextExpectedChunkIndex", 101);
                break;
            case "user_paused":
                SetPrivateProperty(context, "UserPaused", true);
                break;
            case "peer_paused":
                SetPrivateProperty(context, "PeerPaused", true);
                break;
            case "transport_paused":
                SetPrivateProperty(context, "PullTransportPaused", true);
                break;
            case "terminal":
                SetPrivateProperty(context, "State", FileTransferTransferState.Completed);
                break;
            case "live_epoch":
                SetPrivateProperty(GetPrivatePropertyValue(context, "CurrentLiveRouteEpoch")!, "EpochId", 5);
                break;
            case "runtime_owner":
                SetPrivateProperty(context, "CurrentRuntimeOwner",
                    GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner") with { RuntimeGeneration = 6 });
                break;
            case "publication":
                SetPrivateProperty(context, "CurrentRoutePublicationToken",
                    GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken") with { PublicationGeneration = 6 });
                break;
            case "leg":
                SetPrivateProperty(GetPrivatePropertyValue(context, "CurrentTransferLeg")!, "Generation", 6);
                break;
            case "transport_epoch":
                SetPrivateProperty(GetPrivatePropertyValue(context, "CurrentTransferLeg")!, "TransportEpochId", 2L);
                break;
            case "bridge_recovery":
                SetPrivateProperty(GetPrivatePropertyValue(context, "CurrentTransferLeg")!, "BridgeRecoveryGeneration", 2);
                break;
            case "data_session_generation":
                SetPrivateProperty(context, "DataSessionGeneration", 2L);
                break;
            case "data_session_identity":
                SetPrivateProperty(context, "DataSession", new CompletingDataSession(request.SessionId, request.TransferId));
                break;
            case "checkpoint":
                SetPrivateProperty(GetPrivatePropertyValue(context, "CurrentTransferLeg")!, "CheckpointGeneration", 1);
                break;
            case "peer_binding":
                SetPrivateProperty(contract, "PeerRequestBinding", null);
                break;
            case "wire_alias":
                SetPrivateProperty(contract, "Authority",
                    GetPrivateProperty<FallbackFrontierRepairAuthority>(contract, "Authority") with
                    { ProtocolRepairRequestId = "transport_rebind_safety_replay:1:100:100:64" });
                break;
            case "replaced_context":
                CreateEpoch4ReplayAdmissionContext(sender, request.TransferId, FileTransferDirection.Outbound);
                break;
            case "disposed":
                sender.Dispose();
                break;
        }

        var before = ReplayAdmissionBookkeeping(context);
        Assert.False(TryGetReplayAdmissionPeerContract(sender, context, out _));
        QueueReplayAdmissionSafetyReplay(sender, context);

        Assert.Equal(before, ReplayAdmissionBookkeeping(context));
        Assert.Same(contract, GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
        Assert.Empty(GetQueuedV4RepairSends(context));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackReplayAdmission")]
    public void FallbackReplayAdmission_ExactPeerRequestAfterHold_RetainsAliasAndBoundedDispatch()
    {
        using var sender = new SessionFileTransferService();
        var context = CreateEpoch4ReplayAdmissionContext(sender, "replay_exact", FileTransferDirection.Outbound);
        var request = CreateReplayAdmissionRequest(context);
        ApplyReplayAdmissionRequest(sender, context, request);
        var contract = GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract")!;
        MarkReplayAdmissionDispatchComplete(context, contract);

        Assert.True(TryGetReplayAdmissionPeerContract(sender, context, out var admitted));
        Assert.Same(contract, admitted);
        QueueReplayAdmissionSafetyReplay(sender, context);

        var queued = Assert.Single(GetQueuedV4RepairSends(context));
        Assert.Equal(request.RepairRequestId, GetStringProperty(queued, "ProtocolRepairRequestId"));
        Assert.Equal(new[] { 100 }, Assert.IsAssignableFrom<IEnumerable<int>>(GetPrivatePropertyValue(queued, "ChunkIndices")));
        var queuedContract = GetPrivatePropertyValue(queued, "FallbackFrontierRepairContract")!;
        var authority = GetPrivateProperty<FallbackFrontierRepairAuthority>(queuedContract, "Authority");
        Assert.Equal(request.RequestCorrelationId, authority.RequestCorrelationId);
        Assert.Equal(100, authority.FrontierChunkIndex);
        Assert.Equal(1, authority.RequestedChunkCount);

        var beforeDuplicate = ReplayAdmissionBookkeeping(context);
        QueueReplayAdmissionSafetyReplay(sender, context);
        Assert.Equal(beforeDuplicate, ReplayAdmissionBookkeeping(context));
        Assert.Single(GetQueuedV4RepairSends(context));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackReplayAdmission")]
    public void FallbackReplayAdmission_TwoRetrySuccessors_RemainAdmissibleToIssuingReceiver()
    {
        const string transferId = "replay_paired_successors";
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        var outbound = CreateEpoch4ReplayAdmissionContext(sender, transferId, FileTransferDirection.Outbound);
        var inbound = CreateEpoch4ReplayAdmissionContext(receiver, transferId, FileTransferDirection.Inbound);
        StartCoordinatorSnapshotStreamForTest(outbound);
        StartCoordinatorSnapshotStreamForTest(inbound);
        Assert.False(string.IsNullOrWhiteSpace(GetPrivateProperty<string>(outbound, "CoordinatorInstanceId")));
        Assert.False(string.IsNullOrWhiteSpace(GetPrivateProperty<string>(inbound, "CoordinatorInstanceId")));
        var request = IssueReplayAdmissionReceiverRequest(receiver, inbound);
        ApplyReplayAdmissionRequest(sender, outbound, request);
        var predecessor = GetPrivatePropertyValue(outbound, "CurrentFallbackFrontierRepairContract")!;
        var initialAuthority = GetPrivateProperty<FallbackFrontierRepairAuthority>(predecessor, "Authority");
        var initialIdentity = GetPrivatePropertyValue(predecessor, "Identity");
        Assert.Equal(3, initialAuthority.RequestedChunkCount);

        for (var retry = 0; retry < 2; retry++)
        {
            object?[] arguments =
            [outbound, predecessor, FallbackFrontierRepairSourceKind.SenderLivenessRescue, "test_peer_bound_retry", null];
            var issue = typeof(SessionFileTransferService).GetMethod(
                "TryIssueOutboundFallbackFrontierRetrySuccessorLocked", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.True((bool)issue.Invoke(sender, arguments)!);
            var successor = arguments[4]!;
            var authority = GetPrivateProperty<FallbackFrontierRepairAuthority>(successor, "Authority");
            Assert.NotEqual(initialAuthority.AuthorityId, authority.AuthorityId);
            Assert.Equal(GetPrivateProperty<FallbackFrontierRepairAuthority>(predecessor, "Authority").AuthorityId,
                authority.PredecessorAuthorityId);
            Assert.Equal(initialAuthority.ProtocolRepairRequestId, authority.ProtocolRepairRequestId);
            Assert.Equal(initialAuthority.RequestCorrelationId, authority.RequestCorrelationId);
            Assert.Equal(initialAuthority.RangeFingerprint, authority.RangeFingerprint);
            Assert.Equal(initialAuthority.RequestedChunkCount, authority.RequestedChunkCount);
            Assert.Same(initialIdentity, GetPrivatePropertyValue(successor, "Identity"));
            Assert.True(TryGetReplayAdmissionPeerContract(sender, outbound, out var admitted));
            Assert.Same(successor, admitted);

            var batch = new FileTransferChunkBatchFrameV6
            {
                SessionId = request.SessionId,
                TransferId = request.TransferId,
                TransportEpoch = request.TransportEpoch,
                RepairRequestId = authority.ProtocolRepairRequestId,
                StartChunkIndex = 108,
                ChunkCount = 1,
            };
            Assert.True(ValidateReplayAdmissionReceiverBatch(inbound, batch));
            Assert.False(ValidateReplayAdmissionReceiverBatch(inbound, batch with
            {
                RepairRequestId = "transport_rebind_safety_replay:1:100:100:64",
            }));
            predecessor = successor;
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackReplayAdmission")]
    public void FallbackReplayAdmission_TwoRecentlySentRetrySuccessors_PreserveHoldWithoutRearm()
    {
        using var sender = new SessionFileTransferService();
        var context = CreateEpoch4ReplayAdmissionContext(sender, "replay_successor_hold", FileTransferDirection.Outbound);
        var request = CreateReplayAdmissionRequest(context);
        ApplyReplayAdmissionRequest(sender, context, request);
        var predecessorSend = Assert.Single(GetQueuedV4RepairSends(context));
        var predecessor = GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract")!;
        MarkReplayAdmissionDispatchComplete(context, predecessor);

        for (var retry = 0; retry < 2; retry++)
        {
            object?[] arguments = [context, predecessorSend, null, null];
            Assert.True((bool)typeof(SessionFileTransferService).GetMethod(
                "TryRequeueOutboundFallbackFrontierV4RetryLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(sender, arguments)!);
            var successor = arguments[2]!;
            predecessorSend = Assert.Single(GetQueuedV4RepairSends(context));
            Assert.Same(successor, GetPrivatePropertyValue(predecessorSend, "FallbackFrontierRepairContract"));
            MarkReplayAdmissionDispatchComplete(context, successor, DateTimeOffset.UtcNow);
            SetPrivateProperty(context, "PullTransportLastSafetyReplayGeneration", 1);
            SetPrivateProperty(context, "PullTransportLastSafetyReplayUtc", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
            var before = ReplayAdmissionBookkeeping(context);
            var logStart = GetOperationalLogLength();

            QueueReplayAdmissionSafetyReplay(sender, context);

            Assert.Equal(before, ReplayAdmissionBookkeeping(context));
            Assert.Empty(GetQueuedV4RepairSends(context));
            Assert.Same(successor, GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
            Assert.Equal(request.RepairRequestId,
                GetPrivateProperty<FallbackFrontierRepairAuthority>(successor, "Authority").ProtocolRepairRequestId);
            Assert.Contains("exact_frontier_suppression_reason=exact_frontier_repair_recently_sent",
                ReadOperationalLogTail(logStart), StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackReplayAdmission")]
    public void FallbackReplayAdmission_ProgressedRequestA_WaitsForFreshFrontierRequestB()
    {
        using var sender = new SessionFileTransferService();
        var context = CreateEpoch4ReplayAdmissionContext(sender, "replay_new_frontier", FileTransferDirection.Outbound);
        var requestA = CreateReplayAdmissionRequest(context);
        ApplyReplayAdmissionRequest(sender, context, requestA);
        var contractA = GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract")!;
        MarkReplayAdmissionDispatchComplete(context, contractA);
        SettleReplayAdmissionContract(contractA);
        SetPrivateProperty(context, "RemoteNextExpectedChunkIndex", 101);
        var before = ReplayAdmissionBookkeeping(context);

        QueueReplayAdmissionSafetyReplay(sender, context);

        Assert.Equal(before, ReplayAdmissionBookkeeping(context));
        Assert.Empty(GetQueuedV4RepairSends(context));
        Assert.False(TryGetReplayAdmissionPeerContract(sender, context, out _));

        var requestB = requestA with
        {
            RepairRequestId = "v6-frontier:101:fresh-peer-request",
            RequestCorrelationId = "frq-replay-new-frontier-b",
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 101, ChunkCount = 1 }],
        };
        ApplyReplayAdmissionRequest(sender, context, requestB);

        Assert.True(TryGetReplayAdmissionPeerContract(sender, context, out var contractB));
        Assert.NotSame(contractA, contractB);
        var queued = Assert.Single(GetQueuedV4RepairSends(context));
        Assert.Same(contractB, GetPrivatePropertyValue(queued, "FallbackFrontierRepairContract"));
        Assert.Equal(requestB.RepairRequestId, GetStringProperty(queued, "ProtocolRepairRequestId"));
        Assert.Equal(new[] { 101 }, Assert.IsAssignableFrom<IEnumerable<int>>(GetPrivatePropertyValue(queued, "ChunkIndices")));
        Assert.Equal(requestB.RequestCorrelationId,
            GetPrivateProperty<FallbackFrontierRepairAuthority>(contractB!, "Authority").RequestCorrelationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "FallbackReplayAdmission")]
    public void FallbackReplayAdmission_DataSessionReplacement_RejectsAButAcceptsFreshRequestB(bool replaceIdentity)
    {
        using var sender = new SessionFileTransferService();
        var context = CreateEpoch4ReplayAdmissionContext(sender, "replay_session_rebind_" + replaceIdentity,
            FileTransferDirection.Outbound);
        var requestA = CreateReplayAdmissionRequest(context);
        ApplyReplayAdmissionRequest(sender, context, requestA);
        var contractA = GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract")!;
        MarkReplayAdmissionDispatchComplete(context, contractA);
        if (replaceIdentity)
        {
            SetPrivateProperty(context, "DataSession", new CompletingDataSession(requestA.SessionId, requestA.TransferId));
        }
        else
        {
            SetPrivateProperty(context, "DataSessionGeneration", 2L);
        }
        var before = ReplayAdmissionBookkeeping(context);

        Assert.False(TryGetReplayAdmissionPeerContract(sender, context, out _));
        QueueReplayAdmissionSafetyReplay(sender, context);
        Assert.Equal(before, ReplayAdmissionBookkeeping(context));
        Assert.Empty(GetQueuedV4RepairSends(context));

        var requestB = requestA with
        {
            RepairRequestId = "v6-frontier:100:fresh-session-request",
            RequestCorrelationId = "frq-replay-new-session-b",
        };
        ApplyReplayAdmissionRequest(sender, context, requestB);

        Assert.True(TryGetReplayAdmissionPeerContract(sender, context, out var contractB));
        Assert.NotSame(contractA, contractB);
        Assert.True(GetPrivateProperty<bool>(GetPrivatePropertyValue(contractA, "Lifecycle")!, "IsSettled"));
        var queued = Assert.Single(GetQueuedV4RepairSends(context));
        Assert.Same(contractB, GetPrivatePropertyValue(queued, "FallbackFrontierRepairContract"));
        Assert.Equal(requestB.RepairRequestId, GetStringProperty(queued, "ProtocolRepairRequestId"));
        Assert.Equal(requestB.RequestCorrelationId,
            GetPrivateProperty<FallbackFrontierRepairAuthority>(contractB!, "Authority").RequestCorrelationId);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackReplayAdmission")]
    public void FallbackReplayAdmission_ABARepairWindow_RejectsOriginalSendRetryCallback()
    {
        using var sender = new SessionFileTransferService();
        var context = CreateEpoch4ReplayAdmissionContext(sender, "replay_aba_callback", FileTransferDirection.Outbound);
        var requestA = CreateReplayAdmissionRequest(context);
        ApplyReplayAdmissionRequest(sender, context, requestA);
        var originalSend = Assert.Single(GetQueuedV4RepairSends(context));
        var originalContract = GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract")!;

        ApplyReplayAdmissionRequest(sender, context, requestA with
        {
            RepairRequestId = "v6-frontier:100:window-b",
            RequestCorrelationId = "frq-replay-aba-b",
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 100, ChunkCount = 2 }],
        });
        var currentRequest = requestA with
        {
            RepairRequestId = "v6-frontier:100:window-a-again",
            RequestCorrelationId = "frq-replay-aba-current",
        };
        ApplyReplayAdmissionRequest(sender, context, currentRequest);
        var currentContract = GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract")!;
        Assert.NotSame(originalContract, currentContract);
        Assert.Equal(GetPrivateProperty<FallbackFrontierRepairAuthority>(originalContract, "Authority").RangeFingerprint,
            GetPrivateProperty<FallbackFrontierRepairAuthority>(currentContract, "Authority").RangeFingerprint);
        var currentSend = Assert.Single(GetQueuedV4RepairSends(context));
        var before = ReplayAdmissionBookkeeping(context);

        object?[] callbackArguments = [context, originalSend, null, null];
        Assert.False((bool)typeof(SessionFileTransferService).GetMethod(
            "TryRequeueOutboundFallbackFrontierV4RetryLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sender, callbackArguments)!);

        Assert.Null(callbackArguments[2]);
        Assert.Equal(0, callbackArguments[3]);
        Assert.Equal(before, ReplayAdmissionBookkeeping(context));
        Assert.Same(currentContract, GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
        Assert.Same(currentSend, Assert.Single(GetQueuedV4RepairSends(context)));
        Assert.False(GetPrivateProperty<bool>(GetPrivatePropertyValue(currentContract, "Lifecycle")!, "IsSettled"));
        Assert.Equal(currentRequest.RepairRequestId, GetStringProperty(currentSend, "ProtocolRepairRequestId"));
    }

    private static object CreateEpoch4ReplayAdmissionContext(
        SessionFileTransferService service, string transferId, FileTransferDirection direction)
    {
        var context = direction == FileTransferDirection.Outbound
            ? CreatePostTunaFallbackV6OutboundContext(transferId, chunksAcceptedForTransport: 180, remoteGrantedUntilExclusive: 180)
            : CreatePostTunaFallbackV6InboundContext(transferId);
        var route = GetPrivateProperty<FileTransferRouteSelection>(context, "RouteSelection");
        SetPrivateProperty(context, "CurrentLiveRouteEpoch", FileTransferCoordinator.StartLiveRouteEpoch(
            3, route, FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn, "test_epoch4_replay_admission"));
        SetPrivateProperty(context, "LastLiveRouteEpochId", 4);
        SetPrivateProperty(context, "CurrentTransferLeg", null);
        var leg = SeedCurrentRuntimeOwnerForTest(context, direction, "test_epoch4_replay_admission", startCoordinatorSnapshotStream: false);
        SetPrivateProperty(leg, "Generation", 5);
        leg.TransportEpochId = 1;
        var owner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner") with { RuntimeGeneration = 5 };
        SetPrivateProperty(context, "CurrentRuntimeOwner", owner);
        SetPrivateProperty(context, "LastRuntimeOwnerGeneration", 5);
        SetPrivateProperty(context, "LastTransferLegGeneration", 5);
        SetPrivateProperty(context, "CurrentRoutePublicationToken",
            GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken") with
            { RuntimeOwner = owner, RuntimeGeneration = 5, LegGeneration = 5, PublicationGeneration = 5 });
        SetPrivateProperty(context, "LastRoutePublicationGeneration", 5);
        if (direction == FileTransferDirection.Outbound)
        {
            SetPrivateProperty(context, "PullTransportRebindGeneration", 1);
        }
        typeof(SessionFileTransferService).GetField(
            direction == FileTransferDirection.Outbound ? "outboundTransfer" : "inboundTransfer",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);
        return context;
    }

    private static FileTransferFrontierRequestFrameV6 CreateReplayAdmissionRequest(object context) => new()
    {
        SessionId = GetPrivateProperty<string>(context, "SessionId"),
        TransferId = GetPrivateProperty<string>(context, "TransferId"),
        TransportEpoch = 1,
        RepairRequestId = "v6-frontier:100:peer-admission",
        RequestCorrelationId = "frq-peer-replay-admission",
        MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 100, ChunkCount = 1 }],
        Priority = "frontier",
        RecoveryMode = "regular_nkn_frontier_stall_control_bulk",
    };

    private static FileTransferFrontierRequestFrameV6 IssueReplayAdmissionReceiverRequest(
        SessionFileTransferService receiver, object inbound)
    {
        var request = CreateReplayAdmissionRequest(inbound) with
        {
            MissingRanges =
            [
                new FileTransferRangeV4 { StartChunkIndex = 100, ChunkCount = 2 },
                new FileTransferRangeV4 { StartChunkIndex = 108, ChunkCount = 1 },
            ],
        };
        var identity = typeof(SessionFileTransferService).GetMethod(
            "CreateFallbackFrontierRepairIdentity", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [request.MissingRanges, 256, 255]);
        object?[] arguments = [inbound, identity, FallbackFrontierRepairSourceKind.ReceiverRequest,
            request.RepairRequestId, null, request.Priority, request.RecoveryMode, "test_receiver_issued_replay", null, null, null];
        var issue = typeof(SessionFileTransferService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "TryIssueFallbackFrontierRepairContractLocked" &&
                method.GetParameters()[0].ParameterType.Name == "InboundTransferContext");
        Assert.True((bool)issue.Invoke(receiver, arguments)!, arguments[10]?.ToString());
        var contract = arguments[9]!;
        SetPrivateProperty(inbound, "CurrentFallbackFrontierRepairContract", contract);
        var authority = GetPrivateProperty<FallbackFrontierRepairAuthority>(contract, "Authority");
        return request with { RequestCorrelationId = authority.RequestCorrelationId };
    }

    private static void ApplyReplayAdmissionRequest(SessionFileTransferService sender, object context,
        FileTransferFrontierRequestFrameV6 request) => typeof(SessionFileTransferService).GetMethod(
            "ApplyOutboundV6RepairRequest", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sender, [context, request]);

    private static bool TryGetReplayAdmissionPeerContract(SessionFileTransferService sender, object context, out object? contract)
    {
        object?[] arguments = [context, null, null];
        var method = typeof(SessionFileTransferService).GetMethod("TryGetOutboundFallbackFrontierPeerRepairLocked",
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!;
        var accepted = (bool)method.Invoke(method.IsStatic ? null : sender, arguments)!;
        contract = arguments[1];
        return accepted;
    }

    private static void QueueReplayAdmissionSafetyReplay(SessionFileTransferService sender, object context) =>
        typeof(SessionFileTransferService).GetMethod("QueueOutboundV4TransportRebindSafetyReplayLocked",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sender, [context, "post_fallback_sender_wait", true]);

    private static void MarkReplayAdmissionDispatchComplete(object context, object contract, DateTimeOffset? sentUtc = null)
    {
        var lifecycle = GetPrivatePropertyValue(contract, "Lifecycle")!;
        Assert.True((bool)lifecycle.GetType().GetMethod("TryMarkDispatched")!.Invoke(lifecycle, null)!);
        var queue = GetPrivatePropertyValue(context, "PullV4SenderPumpRepairQueue")!;
        queue.GetType().GetMethod("Clear")!.Invoke(queue, null);
        Assert.IsAssignableFrom<ISet<int>>(GetPrivatePropertyValue(context, "PullV4SenderPumpRepairQueuedChunkIndices")).Clear();
        var authority = GetPrivateProperty<FallbackFrontierRepairAuthority>(contract, "Authority");
        var requests = Assert.IsAssignableFrom<System.Collections.IDictionary>(
            GetPrivatePropertyValue(context, "PullV4SenderPumpRepairRequests"));
        var repairState = requests[authority.CanonicalRepairKey]!;
        SetPrivateProperty(repairState, "Queued", false);
        SetPrivateProperty(repairState, "QueuedUtc", null);
        SetPrivateProperty(repairState, "InFlight", false);
        SetPrivateProperty(repairState, "LastSentUtc", sentUtc ?? DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
        SetPrivateProperty(repairState, "SentCount", 1);
        SetPrivateProperty(repairState, "LastSentRemoteFrontierChunkIndex", 100);
    }

    private static void SettleReplayAdmissionContract(object contract)
    {
        var lifecycle = GetPrivatePropertyValue(contract, "Lifecycle")!;
        var outcome = Enum.Parse(lifecycle.GetType().GetGenericArguments()[0], "ProgressObserved");
        Assert.True((bool)lifecycle.GetType().GetMethod("TrySettle")!.Invoke(lifecycle,
            [outcome, "test_frontier_progress", null])!);
    }

    private static object?[] ReplayAdmissionBookkeeping(object context) =>
    [
        GetPrivatePropertyValue(context, "LastRepairReplayGeneration"),
        GetPrivatePropertyValue(context, "PullTransportSafetyReplayRearmCount"),
        GetPrivatePropertyValue(context, "PullTransportLastSafetyReplayGeneration"),
        GetPrivatePropertyValue(context, "PullTransportLastSafetyReplayFrontierChunkIndex"),
        GetPrivatePropertyValue(context, "PullTransportLastSafetyReplayEndChunkIndex"),
        GetPrivatePropertyValue(context, "PullTransportLastSafetyReplayUtc"),
        GetQueuedV4RepairSends(context).Length,
    ];

    private static bool ValidateReplayAdmissionReceiverBatch(object inbound, FileTransferChunkBatchFrameV6 batch)
    {
        object?[] arguments = [inbound, batch, null, null];
        return (bool)typeof(SessionFileTransferService).GetMethod(
            "TryGetInboundFallbackFrontierBatchContractLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, arguments)!;
    }
}
