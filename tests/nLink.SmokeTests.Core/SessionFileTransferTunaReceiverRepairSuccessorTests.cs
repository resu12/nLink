using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void FileTunaV4ReceiverStateRepair_ChangedRangeInstallsExactSuccessor(bool addMissingRange)
    {
        var transferId = $"tuna_changed_range_{addMissingRange}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var (context, predecessor) = CreateDispatchedTunaReceiverStateRepair(service, transport, transferId);
        var predecessorAuthority = GetPrivateProperty<FileTransferRegularV4FrontierRepairAuthority>(predecessor, "Authority");
        var owner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        var state = CreateTunaReceiverRepairState(transferId, epoch: 2, frontierRangeCount: 3);
        if (addMissingRange)
        {
            state = state with
            {
                MissingRanges = [.. state.MissingRanges, new FileTransferRangeV4 { StartChunkIndex = 2166, ChunkCount = 3 }],
            };
        }
        var repairKey = addMissingRange ? "1923:6:1923:3,2166:3" : "1923:3:1923:3";

        // The operational logger redacts the long rejection reason. Probe the
        // same immutable coordinator state without applying either decision.
        var coordinatorState = CreateCoordinatorStateForTest(context);
        var request = new FileTransferCoordinatorActionRequest(
            FileTransferCoordinatorActionKind.RepairReplay,
            "v4_repair_schedule",
            repairKey,
            RuntimeOwner: owner,
            Route: leg.RouteSelection.Route,
            ProtocolVersion: leg.ProtocolVersion,
            LiveRouteEpoch: leg.LiveRouteEpochId,
            LegGeneration: leg.Generation,
            TransportEpochId: leg.TransportEpochId,
            BridgeRecoveryGeneration: leg.BridgeRecoveryGeneration,
            RepairReplayKind: RepairReplayKind.V4Repair);
        var withoutPredecessor = FileTransferCoordinator.AuthorizeAction(coordinatorState, request);
        Assert.False(withoutPredecessor.Accepted);
        Assert.Equal("regular_v4_frontier_repair_pressure_binding_predecessor_mismatch", withoutPredecessor.RejectionReason);
        Assert.Same(coordinatorState, withoutPredecessor.State);
        var withPredecessor = FileTransferCoordinator.AuthorizeAction(
            coordinatorState,
            request with { PredecessorAuthorityId = predecessorAuthority.AuthorityId });
        Assert.True(withPredecessor.Accepted, withPredecessor.RejectionReason);
        Assert.Equal(coordinatorState.PersistentState, CreateCoordinatorStateForTest(context).PersistentState);

        var logStart = GetOperationalLogLength();
        ApplyTunaReceiverRepairState(service, context, state);

        Assert.Equal(2, GetPrivateProperty<int>(context, "V4LastStateEpoch"));
        Assert.Equal(1923, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
        Assert.Equal(owner, GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"));
        var queued = GetQueuedV4RepairSends(context).ToArray();
        var rejectionLog = string.Join(Environment.NewLine, ReadOperationalLogTail(logStart)
            .Split('\n').Where(line => line.Contains("event=filetransfer_recovery_authority_rejected;", StringComparison.Ordinal)));
        Assert.True(queued.Length == 1,
            $"Expected one changed-range successor after an installed, dispatched Tuna-v4 repair; actual={queued.Length}; " +
            $"repair_key={repairKey}; coordinator_without_predecessor={withoutPredecessor.RejectionReason}; " +
            $"coordinator_with_exact_predecessor={withPredecessor.Accepted}.{Environment.NewLine}{rejectionLog}");
        var successor = GetPrivatePropertyValue(queued[0], "RegularV4RepairContract")!;
        var successorAuthority = GetPrivateProperty<FileTransferRegularV4FrontierRepairAuthority>(successor, "Authority");
        Assert.NotEqual(predecessorAuthority.AuthorityId, successorAuthority.AuthorityId);
        Assert.Equal(predecessorAuthority.AuthorityId, successorAuthority.PredecessorAuthorityId);
        Assert.Equal(repairKey, successorAuthority.RepairKey);
        Assert.Same(successor, GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract"));
        var expectedChunks = Enumerable.Range(1923, 3)
            .Concat(addMissingRange ? Enumerable.Range(2166, 3) : [])
            .ToArray();
        Assert.Equal(expectedChunks, Assert.IsAssignableFrom<IReadOnlyList<int>>(GetPrivatePropertyValue(queued[0], "ChunkIndices")));
        var installed = Assert.Single(transport.RegularV4PressureInstallReceipts.Where(
            receipt => receipt.Authority.AuthorityId == successorAuthority.AuthorityId));
        Assert.Equal(FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed, installed.Status);
        Assert.False(GetPrivateProperty<bool>(GetPrivatePropertyValue(successor, "Lifecycle")!, "DispatchObserved"));
        var retired = Assert.Single(transport.RegularV4FrontierRepairSettlements);
        Assert.Equal(predecessorAuthority.AuthorityId, retired.AuthorityId);
        Assert.Equal(FileTransferRegularV4FrontierRepairSettlementOutcome.Superseded, retired.Outcome);
        var log = ReadOperationalLogTail(logStart);
        var installIndex = log.IndexOf(
            $"event=filetransfer_regular_v4_frontier_repair_successor_pressure_install_decided; authority_schema=2; authority_id={successorAuthority.AuthorityId}",
            StringComparison.Ordinal);
        var retirementIndex = log.IndexOf(
            $"event=filetransfer_regular_v4_frontier_repair_contract_settled; authority_schema=2; authority_id={predecessorAuthority.AuthorityId}",
            StringComparison.Ordinal);
        Assert.True(installIndex >= 0 && retirementIndex > installIndex, log);
        Assert.False((bool)typeof(SessionFileTransferService)
            .GetMethod("TryMarkRegularV4FrontierRepairDispatched", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, predecessor])!);
        Assert.Same(successor, GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract"));
        Assert.Single(transport.RegularV4FrontierRepairSettlements);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void FileTunaV4ReceiverStateRepair_FailedInstallOrRetirementRetriesExactSuccessor(bool rejectRetirement, bool addMissingRange)
    {
        var transferId = $"tuna_retry_{rejectRetirement}_{addMissingRange}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var (context, predecessor) = CreateDispatchedTunaReceiverStateRepair(service, transport, transferId);
        var predecessorAuthority = GetPrivateProperty<FileTransferRegularV4FrontierRepairAuthority>(predecessor, "Authority");
        var predecessorLifecycle = GetPrivatePropertyValue(predecessor, "Lifecycle")!;
        if (rejectRetirement)
        {
            transport.RegularV4PressureSettlementApplyOverride = command => new(
                command,
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Rejected,
                "test_retirement_rejected",
                CurrentAuthorityCleared: false,
                RetainedPredecessorCleared: false);
        }
        else
        {
            transport.RegularV4PressureInstallOverride = pressure =>
            {
                Assert.Empty(GetQueuedV4RepairSends(context));
                Assert.False(GetPrivateProperty<bool>(predecessorLifecycle, "IsSettled"));
                return new(
                    pressure.RepairAuthority!,
                    FileTransferRegularV4FrontierRepairPressureInstallStatus.Rejected,
                    "test_install_rejected",
                    GetPrivateProperty<long>(GetPrivatePropertyValue(context, "PendingRegularV4FrontierRepairPressureBinding")!, "InstallGeneration"),
                    pressure.Reason);
            };
        }
        var state = CreateTunaReceiverRepairState(transferId, epoch: 2, frontierRangeCount: 3);
        if (addMissingRange)
        {
            state = state with { MissingRanges = [.. state.MissingRanges, new FileTransferRangeV4 { StartChunkIndex = 2166, ChunkCount = 3 }] };
        }
        ApplyTunaReceiverRepairState(service, context, state);
        var successor = GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract")!;
        Assert.NotSame(predecessor, successor);
        var successorAuthority = GetPrivateProperty<FileTransferRegularV4FrontierRepairAuthority>(successor, "Authority");
        Assert.Equal(predecessorAuthority.AuthorityId, successorAuthority.PredecessorAuthorityId);
        Assert.Empty(GetQueuedV4RepairSends(context));
        Assert.False(GetPrivateProperty<bool>(predecessorLifecycle, "IsSettled"));
        Assert.NotNull(GetPrivatePropertyValue(context, rejectRetirement
            ? "InstalledRegularV4FrontierRepairPressureBinding" : "PendingRegularV4FrontierRepairPressureBinding"));
        var receiptCount = transport.RegularV4PressureInstallReceipts.Count;
        var settlementCount = transport.RegularV4FrontierRepairSettlements.Count;
        var decision = CreateCoordinatorStateForTest(context).CurrentRepairReplayDecision;

        // Different evidence cannot replace an unfinished successor transaction.
        ApplyTunaReceiverRepairState(service, context, CreateTunaReceiverRepairState(transferId, epoch: 3, frontierRangeCount: 6));
        Assert.Same(successor, GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract"));
        Assert.Equal(decision, CreateCoordinatorStateForTest(context).CurrentRepairReplayDecision);
        Assert.Equal(receiptCount, transport.RegularV4PressureInstallReceipts.Count);
        Assert.Equal(settlementCount, transport.RegularV4FrontierRepairSettlements.Count);
        Assert.Empty(GetQueuedV4RepairSends(context));

        transport.RegularV4PressureInstallOverride = null;
        transport.RegularV4PressureSettlementApplyOverride = null;
        ApplyTunaReceiverRepairState(service, context, state with { Epoch = 4 });

        var queued = Assert.Single(GetQueuedV4RepairSends(context));
        Assert.Same(successor, GetPrivatePropertyValue(queued, "RegularV4RepairContract"));
        Assert.Equal(decision, CreateCoordinatorStateForTest(context).CurrentRepairReplayDecision);
        var receipts = transport.RegularV4PressureInstallReceipts.Where(receipt => receipt.Authority.AuthorityId == successorAuthority.AuthorityId).ToArray();
        Assert.Equal(rejectRetirement ? 1 : 2, receipts.Length);
        Assert.Single(receipts.Select(receipt => receipt.InstallGeneration).Distinct());
        Assert.Equal(FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed, receipts[^1].Status);
        Assert.True(GetPrivateProperty<bool>(predecessorLifecycle, "IsSettled"));
        Assert.Null(GetPrivatePropertyValue(context, "PendingRegularV4FrontierRepairPressureInstallRetry"));
        Assert.Null(GetPrivatePropertyValue(context, "RetainedRegularV4FrontierRepairPressurePredecessor"));
    }

    [Theory]
    [InlineData("runtime_owner")]
    [InlineData("transport_epoch")]
    [InlineData("data_session_missing")]
    [InlineData("terminal")]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void FileTunaV4ReceiverStateRepair_StaleScopeCannotAuthorizeSuccessor(string replacement)
    {
        var transferId = $"tuna_stale_{replacement}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var (context, predecessor) = CreateDispatchedTunaReceiverStateRepair(service, transport, transferId);
        switch (replacement)
        {
            case "runtime_owner": SetPrivateProperty(context, "CurrentRuntimeOwner", null); break;
            case "transport_epoch": GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg").TransportEpochId++; break;
            case "data_session_missing": SetPrivateProperty(context, "DataSession", null); break;
            case "terminal": SetPrivateProperty(context, "State", FileTransferTransferState.Completed); break;
        }
        var decision = CreateCoordinatorStateForTest(context).CurrentRepairReplayDecision;
        typeof(SessionFileTransferService)
            .GetMethod("EnqueueV4RepairsFromState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, CreateTunaReceiverRepairState(transferId, epoch: 2, frontierRangeCount: 3)]);

        Assert.Same(predecessor, GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract"));
        Assert.Equal(decision, CreateCoordinatorStateForTest(context).CurrentRepairReplayDecision);
        Assert.Empty(GetQueuedV4RepairSends(context));
        Assert.Single(transport.RegularV4PressureInstallReceipts);
        Assert.Empty(transport.RegularV4FrontierRepairSettlements);
        Assert.Equal(1923, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void FileTunaV4ReceiverStateRepair_StaleReceiveSessionCannotAuthorizeSuccessor()
    {
        const string transferId = "tuna_stale_receive_session";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var (context, predecessor) = CreateDispatchedTunaReceiverStateRepair(service, transport, transferId);
        var dataSession = GetPrivateProperty<RecordingDataSession>(context, "DataSession");
        var owner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        object pumpInstall;
        lock (GetServiceGate(service))
        {
            var staged = StageSparsePumpInstall(service, context, predecessor: null, "test_receiver_repair");
            Assert.True(staged.Accepted, staged.Reason);
            var installed = CommitSparsePumpInstall(service, context, staged.Install!, dataSession);
            Assert.True(installed.Accepted, installed.Reason);
            pumpInstall = installed.Install!;
        }
        SetPrivateProperty(context, "DataSessionGeneration", 2L);
        var decision = CreateCoordinatorStateForTest(context).CurrentRepairReplayDecision;
        typeof(SessionFileTransferService)
            .GetMethod("EnqueueV4RepairsFromReceiveState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, CreateTunaReceiverRepairState(transferId, 2, 3), pumpInstall, owner, dataSession, 1L]);

        Assert.Same(predecessor, GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract"));
        Assert.Equal(decision, CreateCoordinatorStateForTest(context).CurrentRepairReplayDecision);
        Assert.Empty(GetQueuedV4RepairSends(context));
        Assert.Single(transport.RegularV4PressureInstallReceipts);
        Assert.Empty(transport.RegularV4FrontierRepairSettlements);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void FileTunaV4ReceiverStateRepair_TerminalDuringInstallCannotRetirePredecessorOrDispatch()
    {
        const string transferId = "tuna_terminal_install_callback";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var (context, predecessor) = CreateDispatchedTunaReceiverStateRepair(service, transport, transferId);
        transport.RegularV4PressureInstallOverride = pressure =>
        {
            var generation = GetPrivateProperty<long>(GetPrivatePropertyValue(context, "PendingRegularV4FrontierRepairPressureBinding")!, "InstallGeneration");
            SetPrivateProperty(context, "State", FileTransferTransferState.Completed);
            return new(pressure.RepairAuthority!, FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed,
                "test_late_install_receipt", generation, pressure.Reason);
        };
        ApplyTunaReceiverRepairState(service, context, CreateTunaReceiverRepairState(transferId, 2, 3));

        Assert.Empty(GetQueuedV4RepairSends(context));
        Assert.Empty(transport.RegularV4FrontierRepairSettlements);
        Assert.False(GetPrivateProperty<bool>(GetPrivatePropertyValue(predecessor, "Lifecycle")!, "IsSettled"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void FileTunaV4ReceiverStateRepair_ExactRepeatOrStaleEpochPreservesDispatchedAuthority(bool staleEpoch)
    {
        var transferId = $"tuna_unchanged_repair_{staleEpoch}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var (context, predecessor) = CreateDispatchedTunaReceiverStateRepair(service, transport, transferId);
        var coordinatorState = CreateCoordinatorStateForTest(context);

        ApplyTunaReceiverRepairState(service, context, CreateTunaReceiverRepairState(
            transferId, epoch: staleEpoch ? 0 : 2, frontierRangeCount: staleEpoch ? 3 : 12));

        Assert.Empty(GetQueuedV4RepairSends(context));
        Assert.Same(predecessor, GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract"));
        Assert.Single(transport.RegularV4PressureInstallReceipts);
        Assert.Empty(transport.RegularV4FrontierRepairSettlements);
        Assert.Equal(coordinatorState.CurrentRepairReplayDecision, CreateCoordinatorStateForTest(context).CurrentRepairReplayDecision);
        Assert.Equal(staleEpoch ? 1 : 2, GetPrivateProperty<int>(context, "V4LastStateEpoch"));
        Assert.Equal(1923, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
        Assert.False(GetPrivateProperty<bool>(GetPrivatePropertyValue(predecessor, "Lifecycle")!, "IsSettled"));
    }

    private static (object Context, object Contract) CreateDispatchedTunaReceiverStateRepair(
        SessionFileTransferService service,
        LoopbackFileTransferTransport transport,
        string transferId)
    {
        var context = CreateFileTunaV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 1923,
            chunksAcceptedForTransport: 2361,
            remoteGrantedUntilExclusive: 2800,
            chunkCount: 3200);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        ApplyTunaReceiverRepairState(service, context, CreateTunaReceiverRepairState(transferId, epoch: 1, frontierRangeCount: 12));
        var queued = Assert.Single(GetQueuedV4RepairSends(context));
        var contract = GetPrivatePropertyValue(queued, "RegularV4RepairContract")!;
        var authority = GetPrivateProperty<FileTransferRegularV4FrontierRepairAuthority>(contract, "Authority");
        Assert.Equal(FileTransferRouteResolver.FileTunaV4Token, authority.RouteToken);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, authority.ProtocolVersion);
        Assert.Equal("1923:12:1923:12", authority.RepairKey);
        Assert.Equal(12, authority.PendingRepairCount);
        Assert.Null(authority.PredecessorAuthorityId);
        var installed = Assert.Single(transport.RegularV4PressureInstallReceipts);
        Assert.Equal(authority, installed.Authority);
        Assert.Equal(FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed, installed.Status);
        Assert.NotNull(GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));
        Assert.Null(GetPrivatePropertyValue(context, "PendingRegularV4FrontierRepairPressureBinding"));

        // Reproduce the pump's completed-dispatch boundary without live I/O.
        // The service still validates the coordinator's installed receipt.
        Assert.True((bool)typeof(SessionFileTransferService)
            .GetMethod("TryMarkRegularV4FrontierRepairDispatched", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, contract])!);
        var queue = GetPrivatePropertyValue(context, "PullV4SenderPumpRepairQueue")!;
        Assert.Same(queued, queue.GetType().GetMethod("Dequeue")!.Invoke(queue, null));
        var indices = GetPrivatePropertyValue(context, "PullV4SenderPumpRepairQueuedChunkIndices")!;
        indices.GetType().GetMethod("Clear")!.Invoke(indices, null);
        var requests = Assert.IsAssignableFrom<IDictionary>(GetPrivatePropertyValue(context, "PullV4SenderPumpRepairRequests"));
        var repairState = requests[authority.RepairKey]!;
        SetPrivateProperty(repairState, "Queued", false);
        SetPrivateProperty(repairState, "InFlight", false);
        Assert.True(GetPrivateProperty<bool>(GetPrivatePropertyValue(contract, "Lifecycle")!, "DispatchObserved"));
        Assert.False(GetPrivateProperty<bool>(GetPrivatePropertyValue(contract, "Lifecycle")!, "IsSettled"));
        Assert.Equal(1923, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
        Assert.Empty(GetQueuedV4RepairSends(context));
        return (context, contract);
    }

    private static FileTransferStateFrameV4 CreateTunaReceiverRepairState(string transferId, int epoch, int frontierRangeCount)
        => new()
        {
            SessionId = $"session_{transferId}",
            TransferId = transferId,
            Epoch = epoch,
            ContiguousCommittedChunkIndex = 1923,
            DurableReceivedHighestChunkIndex = 2360,
            CreditUntilChunkIndexExclusive = 2800,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 1923, ChunkCount = frontierRangeCount }],
            BytesCommitted = 1923L * 21 * 1024,
        };

    private static void ApplyTunaReceiverRepairState(SessionFileTransferService service, object context, FileTransferStateFrameV4 state)
        => typeof(SessionFileTransferService)
            .GetMethod("ApplyOutboundV4State", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, state]);
}
