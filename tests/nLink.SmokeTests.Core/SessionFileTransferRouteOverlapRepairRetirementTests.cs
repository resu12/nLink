using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "RuntimeUnlockResponderPeerProbe")]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteOverlap")]
    public async Task FallbackRouteOverlap_StalledPredecessorRemainsSettledWhenCurrentSuccessorRetiresForTuna()
        => await RunInboundResponderHandoffWithFallbackRepairAsync(stalledSuccessor: true);

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteOverlap")]
    public async Task FallbackRepublication_StalledRepairRemainsSettledWhenCurrentOwnerRetires()
    {
        const string transferId = "transfer_stalled_fallback_republication";
        var sessionId = $"session_{transferId}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport(sessionId);
        service.AttachTransport(transport);
        var context = CreatePostTunaFallbackV6InboundContext(transferId);
        var dataSession = new MutableAvailabilityDataSession(sessionId, transferId, isAvailable: true);
        SetPrivateProperty(context, "DataSession", dataSession);
        SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 255);
        typeof(SessionFileTransferService).GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var lineage = await CreateStalledInboundRepairSuccessorForHandoffAsync(service, context);
        var source = CreateCoordinatorStateForTest(context);
        var logStart = GetOperationalLogLength();

        // Exercise the service's real Coordinator StartTransfer/retirement path.
        // This is a local ownership fixture, not a simulated peer checkpoint or
        // permission to classify the old progress obligation as recovered.
        var leg = Assert.IsType<FileTransferLeg>(typeof(SessionFileTransferService)
            .GetMethod("StartInboundTransferLegLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, source.RouteSelection, "state_refresh_received",
                FileTransferLegState.Active, true, true, source.CurrentLeg!.TransportEpochId,
                source.CurrentLeg.BridgeRecoveryGeneration + 1, null, null]));
        var target = CreateCoordinatorStateForTest(context);
        Assert.Equal(source.CurrentRuntimeOwner!.Value.RuntimeGeneration + 1,
            target.CurrentRuntimeOwner!.Value.RuntimeGeneration);
        Assert.Equal(source.CurrentRoutePublicationToken!.Value.PublicationGeneration + 1,
            target.CurrentRoutePublicationToken!.Value.PublicationGeneration);
        Assert.Equal(source.CurrentLeg.Generation + 1, leg.Generation);
        Assert.Equal(source.CurrentLeg.BridgeRecoveryGeneration + 1, leg.BridgeRecoveryGeneration);
        Assert.Equal(source.RouteSelection, target.RouteSelection);
        Assert.Equal(source.CurrentDataSessionIdentity, target.CurrentDataSessionIdentity);
        Assert.Equal(source.CurrentDataSessionGeneration, target.CurrentDataSessionGeneration);
        Assert.Same(dataSession, GetPrivatePropertyValue(context, "DataSession"));
        Assert.Equal(100, GetPrivateProperty<int>(context, "NextChunkIndex"));
        Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
        Assert.Null(target.CurrentRepairReplayDecision);

        var firstLifecycle = GetPrivatePropertyValue(lineage[0], "Lifecycle")!;
        var successorLifecycle = GetPrivatePropertyValue(lineage[1], "Lifecycle")!;
        Assert.Equal("SettledStalled", GetPrivatePropertyValue(firstLifecycle, "SettlementOutcome")?.ToString());
        Assert.Equal("StaleRejected", GetPrivatePropertyValue(successorLifecycle, "SettlementOutcome")?.ToString());
        Assert.Equal("runtime_owner_retired_state_refresh_received",
            GetPrivatePropertyValue(successorLifecycle, "SettlementReason"));
        var settle = typeof(SessionFileTransferService).GetMethod("TrySettleFallbackFrontierRepairContract",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var contract in lineage)
        {
            var lifecycle = GetPrivatePropertyValue(contract, "Lifecycle")!;
            var outcome = GetPrivatePropertyValue(lifecycle, "SettlementOutcome");
            var progress = Enum.Parse(lifecycle.GetType().GetGenericArguments()[0], "ProgressObserved");
            Assert.False(Assert.IsType<bool>(settle.Invoke(null, [contract, progress, "late_old_source_progress", null])));
            Assert.Equal(outcome, GetPrivatePropertyValue(lifecycle, "SettlementOutcome"));
        }
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("decision_reason=runtime_owner_retired_state_refresh_received", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_fallback_frontier_repair_progress_observed;", log, StringComparison.Ordinal);
        Assert.Equal(target.CurrentRuntimeOwner, CreateCoordinatorStateForTest(context).CurrentRuntimeOwner);
        Assert.Equal(100, GetPrivateProperty<int>(context, "NextChunkIndex"));
    }

    private static async Task<object[]> CreateStalledInboundRepairSuccessorForHandoffAsync(
        SessionFileTransferService service, object context, bool configureDestination = true)
    {
        var serviceType = typeof(SessionFileTransferService);
        var logStart = GetOperationalLogLength();
        if (configureDestination)
        {
            SetPrivateProperty(context, "V6DestinationMode", Enum.Parse(
                serviceType.GetNestedType("V6ReceiveDestinationMode", BindingFlags.NonPublic)!, "SparseSeekable"));
        }
        var source = CreateCoordinatorStateForTest(context);
        var dataSession = GetPrivatePropertyValue(context, "DataSession");
        // Invoke the real scheduler and let its unchanged grace wake dispatch A.
        var send = serviceType.GetMethod("SendInboundV6FrontierRequestAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.False(await Assert.IsAssignableFrom<Task<bool>>(
            send.Invoke(service, [context, "chunk_batch_committed", false])));
        await WaitUntilAsync(() => GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract") is not null,
            timeoutMs: 5000);
        var first = GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract")!;
        var firstAuthority = GetPrivateProperty<FallbackFrontierRepairAuthority>(first, "Authority");
        var firstLifecycle = GetPrivatePropertyValue(first, "Lifecycle")!;
        await WaitUntilAsync(() => GetPrivateProperty<bool>(firstLifecycle, "DispatchObserved"), timeoutMs: 5000);
        Assert.Equal(FallbackFrontierRepairSourceKind.ReceiverRequest, firstAuthority.SourceKind);
        Assert.Equal(100, firstAuthority.FrontierChunkIndex);
        Assert.False(GetPrivateProperty<bool>(firstLifecycle, "IsSettled"));
        var retryInterval = Assert.IsType<TimeSpan>(serviceType.GetMethod(
            "ResolveInboundV6FrontierRequestRetryIntervalLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, false]));

        // The production rescue cadence owns settlement and replacement. No
        // direct TrySettle call or invented successor receipt supplies this edge.
        for (var attempt = 0; attempt < 3 &&
            ReferenceEquals(first, GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract")); attempt++)
        {
            await Task.Delay(retryInterval + TimeSpan.FromMilliseconds(50));
            Assert.True(await Assert.IsAssignableFrom<Task<bool>>(
                send.Invoke(service, [context, "retry", true])));
        }

        var successor = GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract")!;
        Assert.NotSame(first, successor);
        var successorAuthority = GetPrivateProperty<FallbackFrontierRepairAuthority>(successor, "Authority");
        Assert.Equal(firstAuthority.AuthorityId, successorAuthority.PredecessorAuthorityId);
        Assert.Equal(firstAuthority.DecisionGeneration + 1, successorAuthority.DecisionGeneration);
        var firstDecision = GetPrivateProperty<RepairReplayDecision>(first, "RepairDecision");
        var successorDecision = GetPrivateProperty<RepairReplayDecision>(successor, "RepairDecision");
        Assert.Equal(firstDecision.RoutePublicationToken, successorDecision.RoutePublicationToken);
        Assert.Equal(firstAuthority.RuntimeOwner, successorAuthority.RuntimeOwner);
        Assert.Equal(firstAuthority.TransportEpoch, successorAuthority.TransportEpoch);
        Assert.Equal(firstAuthority.BridgeRecoveryGeneration, successorAuthority.BridgeRecoveryGeneration);
        Assert.Equal(firstAuthority.BridgeDecisionGeneration, successorAuthority.BridgeDecisionGeneration);
        Assert.Equal(firstAuthority.CheckpointRequestId, successorAuthority.CheckpointRequestId);
        Assert.Equal(firstAuthority.FrontierChunkIndex, successorAuthority.FrontierChunkIndex);
        Assert.Equal("SettledStalled", GetPrivatePropertyValue(firstLifecycle, "SettlementOutcome")?.ToString());
        Assert.True(GetPrivateProperty<bool>(firstLifecycle, "IsSettled"));
        Assert.False(GetPrivateProperty<bool>(GetPrivatePropertyValue(successor, "Lifecycle")!, "IsSettled"));
        Assert.Equal(100, GetPrivateProperty<int>(context, "NextChunkIndex"));
        var current = CreateCoordinatorStateForTest(context);
        Assert.Same(dataSession, GetPrivatePropertyValue(context, "DataSession"));
        Assert.Equal(source.CurrentDataSessionIdentity, current.CurrentDataSessionIdentity);
        Assert.Equal(source.CurrentDataSessionGeneration, current.CurrentDataSessionGeneration);
        Assert.Equal(source.CurrentLeg!.CheckpointGeneration, current.CurrentLeg!.CheckpointGeneration);
        Assert.Equal(successorDecision,
            Assert.IsType<RepairReplayDecision>(current.CurrentRepairReplayDecision));
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("decision_status=dispatched; decision_reason=exact_receiver_request_dispatched", log, StringComparison.Ordinal);
        Assert.Contains("settlement_outcome=SettledStalled;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_fallback_frontier_repair_progress_observed;", log, StringComparison.Ordinal);
        return [first, successor];
    }

    private static void AssertStalledInboundRepairLineageRetiredByHandoff(object context, object[] lineage,
        in FileTransferCoordinatorPersistentState persistent, string log)
    {
        var first = lineage[0];
        var successor = lineage[1];
        Assert.Equal("SettledStalled", GetPrivatePropertyValue(
            GetPrivatePropertyValue(first, "Lifecycle")!, "SettlementOutcome")?.ToString());
        Assert.Equal("StaleRejected", GetPrivatePropertyValue(
            GetPrivatePropertyValue(successor, "Lifecycle")!, "SettlementOutcome")?.ToString());
        Assert.Equal("tuna_reactivation_replaced_fallback_owner", GetPrivatePropertyValue(
            GetPrivatePropertyValue(successor, "Lifecycle")!, "SettlementReason"));
        Assert.True(GetPrivateProperty<bool>(GetPrivatePropertyValue(successor, "Lifecycle")!, "DispatchObserved"));
        Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
        Assert.Equal(100, GetPrivateProperty<int>(context, "NextChunkIndex"));
        var terminal = Assert.IsType<FileTransferRouteTransitionOperation>(persistent.FileTransferRouteTransition.LastTerminalOperation);
        Assert.Equal(FileTransferRouteTransitionState.Committed, terminal.State);
        Assert.Equal(100, terminal.Authority.DurableCheckpoint.ContiguousCommittedChunkIndex);
        Assert.Equal(255, terminal.Authority.DurableCheckpoint.DurableHighestChunkIndex);
        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied,
            terminal.DispositionReceipt!.Value.Status);
        Assert.Contains("decision_reason=tuna_reactivation_replaced_fallback_owner", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_fallback_frontier_repair_progress_observed;", log, StringComparison.Ordinal);
        Assert.Null(CreateCoordinatorStateForTest(context).CurrentRepairReplayDecision);

        // A late settlement attempt cannot rewrite either terminal outcome.
        var settle = typeof(SessionFileTransferService).GetMethod("TrySettleFallbackFrontierRepairContract",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var contract in lineage)
        {
            var lifecycle = GetPrivatePropertyValue(contract, "Lifecycle")!;
            var outcome = GetPrivatePropertyValue(lifecycle, "SettlementOutcome");
            var progressObserved = Enum.Parse(lifecycle.GetType().GetGenericArguments()[0], "ProgressObserved");
            Assert.False(Assert.IsType<bool>(settle.Invoke(null,
                [contract, progressObserved, "late_route_overlap_callback", null])));
            Assert.Equal(outcome, GetPrivatePropertyValue(lifecycle, "SettlementOutcome"));
        }
    }
}
