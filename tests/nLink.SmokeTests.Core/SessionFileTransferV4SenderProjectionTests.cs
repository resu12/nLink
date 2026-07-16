using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Projection_CadenceSuppressionPreservesNarrowedDurableGapAndAuthority()
    {
        const string transferId = "transfer_regular_v4_projection_narrowed";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var serviceType = typeof(SessionFileTransferService);
        var context = CreateProjectionInboundContext(transferId);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "projection_narrowed");

        var initial = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var initialRange = Assert.Single(initial.MissingRanges);
        Assert.Equal(9, initialRange.StartChunkIndex);
        Assert.Equal(12, initialRange.ChunkCount);
        var initialContract = GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract");
        Assert.NotNull(initialContract);
        var initialAuthority = GetPrivatePropertyValue(initialContract, "Authority");
        var initialAuthorityId = Assert.IsType<FileTransferAuthorityId>(
            initialAuthority!.GetType().GetProperty("AuthorityId")!.GetValue(initialAuthority));

        MarkProjectionTailDurable(context);
        var narrowed = CreateInboundState(service, context, "chunk_batch_committed");

        var narrowedRange = Assert.Single(narrowed.MissingRanges);
        Assert.Equal(9, narrowedRange.StartChunkIndex);
        Assert.Equal(3, narrowedRange.ChunkCount);
        var retainedContract = GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract");
        Assert.NotNull(retainedContract);
        Assert.Same(initialContract, retainedContract);
        var retainedAuthority = GetPrivatePropertyValue(retainedContract, "Authority");
        Assert.Equal(
            initialAuthorityId,
            Assert.IsType<FileTransferAuthorityId>(retainedAuthority!.GetType().GetProperty("AuthorityId")!.GetValue(retainedAuthority)));

        var repairStates = Assert.IsAssignableFrom<IDictionary>(GetPrivatePropertyValue(context, "V4ReceiverRepairRequests"));
        var repairState = Assert.Single(repairStates.Values.Cast<object>());
        Assert.Equal(1, GetPrivateProperty<int>(repairState, "AttemptCount"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Projection_SettledStalledRetainsEvidenceWithoutReceiverReauthorization()
    {
        const string transferId = "transfer_regular_v4_projection_after_settled_stalled";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var serviceType = typeof(SessionFileTransferService);
        var context = CreateProjectionInboundContext(transferId);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "projection_settled_stalled");

        var initial = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var repairStates = Assert.IsAssignableFrom<IDictionary>(GetPrivatePropertyValue(context, "V4ReceiverRepairRequests"));
        var repairKey = Assert.Single(repairStates.Keys.Cast<string>());
        var currentContract = GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract");
        Assert.NotNull(currentContract);
        var settlementCountBefore = transport.RegularV4FrontierRepairSettlements.Count;

        var settled = Assert.IsType<bool>(serviceType
            .GetMethod("TrySettleInboundRegularV4FrontierRepairStalledLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    repairKey,
                    "unit_test_projection_retention",
                    initial.ContiguousCommittedChunkIndex,
                    initial.DurableReceivedHighestChunkIndex,
                    initial.MissingRanges[0].ChunkCount,
                    12,
                    11_000L,
                ]));
        Assert.True(settled);
        Assert.Equal(settlementCountBefore + 1, transport.RegularV4FrontierRepairSettlements.Count);

        while (transport.RegularV4ControlFeedbackPressures.TryDequeue(out _))
        {
        }

        MarkProjectionTailDurable(context);
        var retained = CreateInboundState(service, context, "chunk_batch_committed");

        var retainedRange = Assert.Single(retained.MissingRanges);
        Assert.Equal(9, retainedRange.StartChunkIndex);
        Assert.Equal(3, retainedRange.ChunkCount);
        Assert.Same(currentContract, GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract"));
        Assert.Empty(transport.RegularV4ControlFeedbackPressures);
        Assert.Equal(settlementCountBefore + 1, transport.RegularV4FrontierRepairSettlements.Count);

        using var peerService = new SessionFileTransferService();
        var peerContext = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 9,
            chunksAcceptedForTransport: 32,
            remoteGrantedUntilExclusive: 32,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(peerService, peerContext);
        typeof(SessionFileTransferService)
            .GetMethod("ApplyOutboundV4State", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(peerService, [peerContext, retained]);

        Assert.NotNull(GetPrivatePropertyValue(peerContext, "CurrentRegularV4FrontierRepairContract"));
        Assert.Single(GetQueuedV4RepairSends(peerContext));
        Assert.Equal(settlementCountBefore + 1, transport.RegularV4FrontierRepairSettlements.Count);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Projection_PauseAndPendingWriteDoNotEraseDurableEvidence()
    {
        const string transferId = "transfer_regular_v4_projection_pause_pending";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "projection_pause_pending");

        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        MarkProjectionTailDurable(context);
        var pendingWrites = Assert.IsType<HashSet<int>>(GetPrivatePropertyValue(context, "ReceiverSparseChunksPendingWrite"));
        pendingWrites.Add(9);
        SetPrivateProperty(context, "UserPaused", true);
        SetPrivateProperty(context, "UserPauseReason", "unit_test_pause");

        var paused = CreateInboundState(service, context, "chunk_batch_committed");

        var pausedRange = Assert.Single(paused.MissingRanges);
        Assert.Equal(9, pausedRange.StartChunkIndex);
        Assert.Equal(3, pausedRange.ChunkCount);
        Assert.True(paused.TransferPaused);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Projection_InitialPendingWriteRemainsInDurableAuthorizedProjection(int pendingChunkIndex)
    {
        const string transferId = "transfer_regular_v4_projection_initial_pending";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "projection_initial_pending");
        var pendingWrites = Assert.IsType<HashSet<int>>(GetPrivatePropertyValue(context, "ReceiverSparseChunksPendingWrite"));
        pendingWrites.Add(pendingChunkIndex);

        var state = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);

        var projectedRange = Assert.Single(state.MissingRanges);
        Assert.Equal(9, projectedRange.StartChunkIndex);
        Assert.Equal(12, projectedRange.ChunkCount);
        var projection = GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection");
        Assert.NotNull(projection);
        var projectionRanges = Assert.IsAssignableFrom<IReadOnlyList<FileTransferRangeV4>>(
            GetPrivatePropertyValue(projection, "ProjectedRanges"));
        var durableRange = Assert.Single(projectionRanges);
        Assert.Equal(9, durableRange.StartChunkIndex);
        Assert.Equal(12, durableRange.ChunkCount);
        var contract = GetPrivatePropertyValue(projection, "SourceContract");
        var authority = GetPrivatePropertyValue(contract!, "Authority");
        var typedAuthority = Assert.IsType<FileTransferRegularV4FrontierRepairAuthority>(authority);
        Assert.Equal(12, typedAuthority.PendingRepairCount);
        var repairStates = Assert.IsAssignableFrom<IDictionary>(
            GetPrivatePropertyValue(context, "V4ReceiverRepairRequests"));
        var repairStateKey = Assert.Single(repairStates.Keys.Cast<string>());
        Assert.Equal(typedAuthority.RepairKey, repairStateKey);
        var pressure = Assert.Single(transport.RegularV4ControlFeedbackPressures);
        Assert.Equal(typedAuthority, pressure.RepairAuthority);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Projection_ImmediateStalledSuccessorInstallsPressureBeforePredecessorRetirement()
    {
        const string transferId = "transfer_regular_v4_projection_pending_due";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        using var dataSession = new RecordingDataSession($"session_{transferId}", transferId);
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        SetPrivateProperty(context, "DataSession", dataSession);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "projection_pending_due");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        MarkProjectionTailDurable(context);
        var narrowed = CreateInboundState(service, context, "chunk_batch_committed");
        var narrowedRange = Assert.Single(narrowed.MissingRanges);
        Assert.Equal(9, narrowedRange.StartChunkIndex);
        Assert.Equal(3, narrowedRange.ChunkCount);
        var projectionBefore = GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection");
        Assert.NotNull(projectionBefore);
        var sourceContractBefore = GetPrivatePropertyValue(projectionBefore, "SourceContract");
        var pendingWrites = Assert.IsType<HashSet<int>>(GetPrivatePropertyValue(context, "ReceiverSparseChunksPendingWrite"));
        pendingWrites.Add(10);

        var dueState = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);

        var dueRange = Assert.Single(dueState.MissingRanges);
        Assert.Equal(9, dueRange.StartChunkIndex);
        Assert.Equal(3, dueRange.ChunkCount);
        var projectionAfter = GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection");
        Assert.NotNull(projectionAfter);
        Assert.Same(sourceContractBefore, GetPrivatePropertyValue(projectionAfter, "SourceContract"));
        var successorAttemptContract = GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract");
        Assert.NotNull(successorAttemptContract);
        Assert.NotSame(sourceContractBefore, successorAttemptContract);
        var sourceLifecycle = GetPrivatePropertyValue(sourceContractBefore!, "Lifecycle");
        Assert.True(GetPrivateProperty<bool>(sourceLifecycle!, "IsSettled"));
        Assert.Equal(
            FileTransferRegularV4FrontierRepairSettlementOutcome.Superseded,
            Assert.IsType<FileTransferRegularV4FrontierRepairSettlementOutcome>(
                GetPrivatePropertyValue(sourceLifecycle!, "SettlementOutcome")));

        SetPrivateProperty(
            context,
            "V4FrontierStallStartedUtc",
            DateTimeOffset.UtcNow - TimeSpan.FromSeconds(11));
        var afterSuccessorRetention = CreateInboundState(
            service,
            context,
            "frontier_stall_repair_due",
            forceMissingRange: true);
        var retainedAfterSuccessorSettlement = Assert.Single(afterSuccessorRetention.MissingRanges);
        Assert.Equal(9, retainedAfterSuccessorSettlement.StartChunkIndex);
        Assert.Equal(3, retainedAfterSuccessorSettlement.ChunkCount);

        var successorLifecycle = GetPrivatePropertyValue(successorAttemptContract!, "Lifecycle");
        Assert.True(GetPrivateProperty<bool>(successorLifecycle!, "IsSettled"));
        Assert.Equal(
            FileTransferRegularV4FrontierRepairSettlementOutcome.SettledStalled,
            Assert.IsType<FileTransferRegularV4FrontierRepairSettlementOutcome>(
                GetPrivatePropertyValue(successorLifecycle!, "SettlementOutcome")));
        var retainedProjection = GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection");
        Assert.NotNull(retainedProjection);
        Assert.Same(sourceContractBefore, GetPrivatePropertyValue(retainedProjection, "SourceContract"));

        var predecessorAuthority = Assert.IsType<FileTransferRegularV4FrontierRepairAuthority>(
            GetPrivatePropertyValue(sourceContractBefore!, "Authority"));
        var successorAuthority = Assert.IsType<FileTransferRegularV4FrontierRepairAuthority>(
            GetPrivatePropertyValue(successorAttemptContract!, "Authority"));
        var receipt = Assert.Single(transport.RegularV4PressureInstallReceipts);
        Assert.Equal(successorAuthority, receipt.Authority);
        var settlements = transport.RegularV4FrontierRepairSettlements.ToArray();
        Assert.Equal(2, settlements.Length);
        Assert.Equal(predecessorAuthority.AuthorityId, settlements[0].AuthorityId);
        Assert.Equal(FileTransferRegularV4FrontierRepairSettlementOutcome.Superseded, settlements[0].Outcome);
        Assert.Equal(successorAuthority.AuthorityId, settlements[1].AuthorityId);
        Assert.Equal(FileTransferRegularV4FrontierRepairSettlementOutcome.SettledStalled, settlements[1].Outcome);

        var log = ReadOperationalLogTail(logStart);
        var installIndex = log.IndexOf(
            $"event=filetransfer_regular_v4_frontier_repair_successor_pressure_install_decided; authority_schema=2; authority_id={successorAuthority.AuthorityId}",
            StringComparison.Ordinal);
        var predecessorSettlementIndex = log.IndexOf(
            $"event=filetransfer_regular_v4_frontier_repair_contract_settled; authority_schema=2; authority_id={predecessorAuthority.AuthorityId}",
            StringComparison.Ordinal);
        var successorSettlementIndex = log.IndexOf(
            $"event=filetransfer_regular_v4_frontier_repair_contract_settled; authority_schema=2; authority_id={successorAuthority.AuthorityId}",
            StringComparison.Ordinal);
        Assert.True(installIndex >= 0, log);
        Assert.True(predecessorSettlementIndex > installIndex, log);
        Assert.True(successorSettlementIndex > predecessorSettlementIndex, log);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Projection_DurableFillClearsProjectionAndRestoresEmptyProgressState()
    {
        const string transferId = "transfer_regular_v4_projection_filled";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "projection_filled");

        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var written = Assert.IsType<BitArray>(GetPrivatePropertyValue(context, "ReceiverSparseChunksWritten"));
        for (var chunkIndex = 9; chunkIndex < 21; chunkIndex++)
        {
            written[chunkIndex] = true;
        }

        SetPrivateProperty(context, "NextChunkIndex", 21);
        SetPrivateProperty(context, "PullHighestReceivedChunkIndex", 20);
        var filled = CreateInboundState(service, context, "chunk_batch_committed");

        Assert.Empty(filled.MissingRanges);
        var repairStates = Assert.IsAssignableFrom<IDictionary>(GetPrivatePropertyValue(context, "V4ReceiverRepairRequests"));
        Assert.Empty(repairStates);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Projection_DuplicatePeerStatesReuseQueuedAuthorityAndRepair()
    {
        const string transferId = "transfer_regular_v4_projection_peer_dedupe";
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 9,
            chunksAcceptedForTransport: 32,
            remoteGrantedUntilExclusive: 32,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        serviceType.GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);

        var first = CreatePeerProjectionState(transferId, epoch: 8);
        InvokeEnqueueAuthorizedV4RepairsFromState(service, context, first);
        var firstQueued = Assert.Single(GetQueuedV4RepairSends(context));
        var firstContract = GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract");
        Assert.NotNull(firstContract);
        var firstAuthority = GetPrivatePropertyValue(firstContract, "Authority");
        Assert.NotNull(firstAuthority);
        var firstAuthorityId = Assert.IsType<FileTransferAuthorityId>(
            firstAuthority.GetType().GetProperty("AuthorityId")!.GetValue(firstAuthority));

        InvokeEnqueueAuthorizedV4RepairsFromState(service, context, CreatePeerProjectionState(transferId, epoch: 9));

        Assert.Same(firstQueued, Assert.Single(GetQueuedV4RepairSends(context)));
        var retainedContract = GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract");
        Assert.NotNull(retainedContract);
        Assert.Same(firstContract, retainedContract);
        var retainedAuthority = GetPrivatePropertyValue(retainedContract, "Authority");
        Assert.NotNull(retainedAuthority);
        Assert.Equal(
            firstAuthorityId,
            Assert.IsType<FileTransferAuthorityId>(retainedAuthority.GetType().GetProperty("AuthorityId")!.GetValue(retainedAuthority)));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Projection_RetainedStateUsesNormalProgressCoalescing()
    {
        const string transferId = "transfer_regular_v4_projection_coalescing";
        using var service = new SessionFileTransferService();
        var context = CreateProjectionInboundContext(transferId);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "projection_coalescing");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        MarkProjectionTailDurable(context);
        var retained = CreateInboundState(service, context, "chunk_batch_committed");
        var now = DateTimeOffset.UtcNow;
        SetPrivateProperty(context, "V4LastStateSentUtc", now);
        SetPrivateProperty(context, "V4LastStateCreditUntilChunkIndexExclusive", retained.CreditUntilChunkIndexExclusive);
        SetPrivateProperty(context, "V4LastStateContiguousCommittedChunkIndex", retained.ContiguousCommittedChunkIndex);
        SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", retained.DurableReceivedHighestChunkIndex);
        SetPrivateProperty(context, "V4LastStateBytesCommitted", retained.BytesCommitted);

        var suppressed = Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod("ShouldSuppressInboundV4StateLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, retained, "chunk_batch_committed", false, false, now]));

        Assert.True(suppressed);
        var range = Assert.Single(retained.MissingRanges);
        Assert.Equal(9, range.StartChunkIndex);
        Assert.Equal(3, range.ChunkCount);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public async Task RegularV4Projection_NewerNarrowedStateOvertakesOlderSendAndAppliesOnce()
    {
        const string transferId = "transfer_regular_v4_projection_reordered";
        using var receiveService = new SessionFileTransferService();
        using var receiveTransport = new LoopbackFileTransferTransport($"session_{transferId}");
        receiveService.AttachTransport(receiveTransport);
        var receiveContext = CreateProjectionInboundContext(transferId);
        SeedCurrentRuntimeOwnerForTest(receiveContext, FileTransferDirection.Inbound, "projection_reordered_receive");
        using var dataSession = new ReorderingProjectionDataSession($"session_{transferId}", transferId);
        SetPrivateProperty(receiveContext, "DataSession", dataSession);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(receiveService, receiveContext);

        var firstSend = Assert.IsAssignableFrom<Task<bool>>(typeof(SessionFileTransferService)
            .GetMethod("SendInboundV4StateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(receiveService, [receiveContext, "frontier_stall_repair_due", false, true, true, true]));
        await dataSession.FirstSendCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5));

        MarkProjectionTailDurable(receiveContext);
        var secondSend = Assert.IsAssignableFrom<Task<bool>>(typeof(SessionFileTransferService)
            .GetMethod("SendInboundV4StateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(receiveService, [receiveContext, "chunk_batch_committed", false, false, false, true]));
        Assert.True(await secondSend.WaitAsync(TimeSpan.FromSeconds(5)));
        dataSession.ReleaseFirstSend.TrySetResult();
        Assert.True(await firstSend.WaitAsync(TimeSpan.FromSeconds(5)));

        var completionOrder = dataSession.CompletedStates;
        Assert.Equal(2, completionOrder.Count);
        var newer = completionOrder[0];
        var older = completionOrder[1];
        Assert.True(newer.Epoch > older.Epoch);
        var newerRange = Assert.Single(newer.MissingRanges);
        Assert.Equal(9, newerRange.StartChunkIndex);
        Assert.Equal(3, newerRange.ChunkCount);
        Assert.NotEmpty(older.MissingRanges);

        using var sendService = new SessionFileTransferService();
        var sendContext = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 9,
            chunksAcceptedForTransport: 32,
            remoteGrantedUntilExclusive: 32,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sendService, sendContext);
        var apply = typeof(SessionFileTransferService)
            .GetMethod("ApplyOutboundV4State", BindingFlags.Instance | BindingFlags.NonPublic)!;

        apply.Invoke(sendService, [sendContext, newer]);
        var currentContract = GetPrivatePropertyValue(sendContext, "CurrentRegularV4FrontierRepairContract");
        Assert.NotNull(currentContract);
        apply.Invoke(sendService, [sendContext, older]);

        Assert.Equal(newer.Epoch, GetPrivateProperty<int>(sendContext, "V4LastStateEpoch"));
        Assert.Same(currentContract, GetPrivatePropertyValue(sendContext, "CurrentRegularV4FrontierRepairContract"));
        Assert.Single(GetQueuedV4RepairSends(sendContext));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Projection_FrontierAdvanceStagesNarrowedSuccessorBeforeEmptyState()
    {
        const string transferId = "transfer_regular_v4_projection_frontier_successor";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "projection_frontier_successor");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var originalContract = GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract");
        Assert.NotNull(originalContract);

        MarkProjectionTailDurable(context);
        var written = Assert.IsType<BitArray>(GetPrivatePropertyValue(context, "ReceiverSparseChunksWritten"));
        written[9] = true;
        SetPrivateProperty(context, "NextChunkIndex", 10);
        var successorState = CreateInboundState(service, context, "chunk_batch_committed");

        var successorRange = Assert.Single(successorState.MissingRanges);
        Assert.Equal(10, successorRange.StartChunkIndex);
        Assert.Equal(2, successorRange.ChunkCount);
        var successorContract = GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract");
        Assert.NotNull(successorContract);
        Assert.NotSame(originalContract, successorContract);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Projection_TerminalizationInvalidatesRetainedProjection()
    {
        const string transferId = "transfer_regular_v4_projection_terminal";
        using var service = new SessionFileTransferService();
        var context = CreateProjectionInboundContext(transferId);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "projection_terminal");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        Assert.NotNull(GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection"));
        SetPrivateProperty(context, "State", FileTransferTransferState.Failed);

        typeof(SessionFileTransferService)
            .GetMethod("ReconcileInboundRegularV4FrontierRepairProjectionLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "unit_test_terminal"]);

        Assert.Null(GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Projection_DataSessionReplacementInvalidatesExactBinding()
    {
        const string transferId = "transfer_regular_v4_projection_data_session";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "projection_data_session");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var originalProjection = GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection");
        Assert.NotNull(originalProjection);
        var originalContract = GetPrivatePropertyValue(originalProjection, "SourceContract");
        var originalAuthority = GetPrivatePropertyValue(originalContract, "Authority");
        var originalAuthorityId = Assert.IsType<FileTransferAuthorityId>(
            originalAuthority!.GetType().GetProperty("AuthorityId")!.GetValue(originalAuthority));
        Assert.Equal(0L, GetPrivateProperty<long>(context, "DataSessionGeneration"));
        using var replacement = new RecordingDataSession($"session_{transferId}", transferId);

        typeof(SessionFileTransferService)
            .GetMethod("ReplaceInboundDataSessionLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, replacement]);

        Assert.Equal(1L, GetPrivateProperty<long>(context, "DataSessionGeneration"));
        Assert.Null(GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection"));
        Assert.True(GetPrivateProperty<bool>(context, "V4RegularFrontierRepairProjectionRefreshRequired"));

        var reboundState = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        Assert.NotEmpty(reboundState.MissingRanges);
        var reboundProjection = GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection");
        Assert.NotNull(reboundProjection);
        Assert.Equal(1L, GetPrivateProperty<long>(reboundProjection, "DataSessionGeneration"));
        var reboundContract = GetPrivatePropertyValue(reboundProjection, "SourceContract");
        var reboundAuthority = GetPrivatePropertyValue(reboundContract, "Authority");
        var reboundAuthorityId = Assert.IsType<FileTransferAuthorityId>(
            reboundAuthority!.GetType().GetProperty("AuthorityId")!.GetValue(reboundAuthority));
        Assert.NotEqual(originalAuthorityId, reboundAuthorityId);
    }

    [Theory]
    [InlineData("runtime_owner")]
    [InlineData("publication")]
    [InlineData("route")]
    [InlineData("protocol")]
    [InlineData("leg")]
    [InlineData("transport")]
    [InlineData("bridge")]
    [InlineData("repair_authority")]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Projection_RuntimeScopeReplacementInvalidatesProjection(string replacement)
    {
        var transferId = $"transfer_regular_v4_projection_scope_{replacement}";
        using var service = new SessionFileTransferService();
        var context = CreateProjectionInboundContext(transferId);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, $"projection_scope_{replacement}");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        Assert.NotNull(GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection"));

        switch (replacement)
        {
            case "runtime_owner":
                var owner = Assert.IsType<RuntimeOwnerToken>(GetPrivatePropertyValue(context, "CurrentRuntimeOwner"));
                SetPrivateProperty(context, "CurrentRuntimeOwner", owner with { RuntimeGeneration = owner.RuntimeGeneration + 1 });
                break;
            case "publication":
                var publication = Assert.IsType<RoutePublicationToken>(GetPrivatePropertyValue(context, "CurrentRoutePublicationToken"));
                SetPrivateProperty(
                    context,
                    "CurrentRoutePublicationToken",
                    publication with { PublicationGeneration = publication.PublicationGeneration + 1 });
                break;
            case "route":
                var tunaRoute = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
                SetPrivateProperty(context, "RouteSelection", tunaRoute);
                SetPrivateProperty(context, "RouteRuntime", tunaRoute.RuntimeDescriptor);
                break;
            case "protocol":
                SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV6);
                break;
            case "leg":
                var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
                SetPrivateProperty(leg, "Generation", leg.Generation + 1);
                break;
            case "transport":
                GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg").TransportEpochId++;
                break;
            case "bridge":
                GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg").BridgeRecoveryGeneration++;
                break;
            case "repair_authority":
                SetPrivateProperty(context, "CurrentRepairReplayDecision", null);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(replacement), replacement, null);
        }

        typeof(SessionFileTransferService)
            .GetMethod("ReconcileInboundRegularV4FrontierRepairProjectionLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, $"unit_test_{replacement}_replacement"]);

        Assert.Null(GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection"));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("session")]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Projection_ManifestOrSessionResetClearsProjection(string reset)
    {
        var transferId = $"transfer_regular_v4_projection_reset_{reset}";
        using var service = new SessionFileTransferService();
        var context = CreateProjectionInboundContext(transferId);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, $"projection_reset_{reset}");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        Assert.NotNull(GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection"));

        if (reset == "manifest")
        {
            SetPrivateProperty(context, "ReceiverSparseChunksWritten", null);
            var resetState = CreateInboundState(service, context, "manifest_reset");
            Assert.Empty(resetState.MissingRanges);
        }
        else
        {
            // Session identity is immutable on a transfer context. A session reset
            // installs a fresh context and therefore cannot inherit the prior
            // session's durable projection.
            context = CreateProjectionInboundContext($"replacement_session_{transferId}");
        }

        Assert.Null(GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public async Task RegularV4Projection_RegularV4ReplacementWithholdsStateUntilFreshAuthorityExists()
    {
        const string transferId = "transfer_regular_v4_projection_replacement_withheld";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "projection_replacement_withheld");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var owner = Assert.IsType<RuntimeOwnerToken>(GetPrivatePropertyValue(context, "CurrentRuntimeOwner"));
        SetPrivateProperty(context, "CurrentRuntimeOwner", owner with { RuntimeGeneration = owner.RuntimeGeneration + 1 });
        typeof(SessionFileTransferService)
            .GetMethod("ReconcileInboundRegularV4FrontierRepairProjectionLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "unit_test_regular_v4_replacement"]);
        Assert.True(GetPrivateProperty<bool>(context, "V4RegularFrontierRepairProjectionRefreshRequired"));
        using var dataSession = new RecordingDataSession($"session_{transferId}", transferId);
        SetPrivateProperty(context, "DataSession", dataSession);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var sendTask = Assert.IsAssignableFrom<Task<bool>>(typeof(SessionFileTransferService)
            .GetMethod("SendInboundV4StateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "chunk_batch_committed", false, false, false, true]));

        Assert.False(await sendTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(dataSession.SentFrames);
        Assert.True(GetPrivateProperty<bool>(context, "V4RegularFrontierRepairProjectionRefreshRequired"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public async Task RegularV4Projection_ScopeReplacementAfterCaptureRejectsStateBeforeDelivery()
    {
        const string transferId = "transfer_regular_v4_projection_presend_replaced";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "projection_presend_replaced");
        using var dataSession = new RecordingDataSession($"session_{transferId}", transferId);
        SetPrivateProperty(context, "DataSession", dataSession);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SessionFileTransferService.InboundRegularV4ProjectionPreSendBarrierForTests = async (_, _) =>
        {
            captured.TrySetResult();
            await release.Task;
        };

        try
        {
            var sendTask = Assert.IsAssignableFrom<Task<bool>>(typeof(SessionFileTransferService)
                .GetMethod("SendInboundV4StateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, "frontier_stall_repair_due", false, true, true, true]));
            await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var owner = Assert.IsType<RuntimeOwnerToken>(GetPrivatePropertyValue(context, "CurrentRuntimeOwner"));
            SetPrivateProperty(context, "CurrentRuntimeOwner", owner with { RuntimeGeneration = owner.RuntimeGeneration + 1 });
            typeof(SessionFileTransferService)
                .GetMethod("ReconcileInboundRegularV4FrontierRepairProjectionLocked", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [context, "unit_test_presend_scope_replacement"]);
            release.TrySetResult();

            Assert.False(await sendTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(dataSession.SentFrames);
            Assert.Null(GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection"));
        }
        finally
        {
            release.TrySetResult();
            SessionFileTransferService.InboundRegularV4ProjectionPreSendBarrierForTests = null;
        }
    }

    [Theory]
    [InlineData("runtime_owner")]
    [InlineData("projection_generation")]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public async Task RegularV4Projection_ControlPlanePreTransportValidationRejectsReplacedScopeOrProjection(
        string replacement)
    {
        var transferId = $"transfer_regular_v4_projection_control_plane_presend_{replacement}";
        using var service = new SessionFileTransferService();
        using var transport = new DeferredControlPlaneValidationLoopbackTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "projection_control_plane_presend");
        using var dataSession = new RecordingDataSession($"session_{transferId}", transferId);
        SetPrivateProperty(context, "DataSession", dataSession);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        try
        {
            var sendTask = Assert.IsAssignableFrom<Task<bool>>(typeof(SessionFileTransferService)
                .GetMethod("SendInboundV4StateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, "frontier_stall_repair_due", false, true, true, true]));
            await transport.RequestCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var serviceGate = Assert.IsType<object>(typeof(SessionFileTransferService)
                .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service));
            lock (serviceGate)
            {
                if (string.Equals(replacement, "runtime_owner", StringComparison.Ordinal))
                {
                    var owner = Assert.IsType<RuntimeOwnerToken>(GetPrivatePropertyValue(context, "CurrentRuntimeOwner"));
                    SetPrivateProperty(context, "CurrentRuntimeOwner", owner with { RuntimeGeneration = owner.RuntimeGeneration + 1 });
                    typeof(SessionFileTransferService)
                        .GetMethod("ReconcileInboundRegularV4FrontierRepairProjectionLocked", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, [context, "unit_test_control_plane_scope_replacement"]);
                }
                else
                {
                    MarkProjectionTailDurable(context);
                    var narrowed = CreateInboundState(service, context, "chunk_batch_committed");
                    var narrowedRange = Assert.Single(narrowed.MissingRanges);
                    Assert.Equal(9, narrowedRange.StartChunkIndex);
                    Assert.Equal(3, narrowedRange.ChunkCount);
                }
            }

            transport.ReleaseValidation.TrySetResult();

            Assert.False(await sendTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(transport.LastValidationAccepted);
            Assert.Empty(dataSession.SentFrames);
            Assert.Equal(
                string.Equals(replacement, "projection_generation", StringComparison.Ordinal),
                GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection") is not null);
        }
        finally
        {
            transport.ReleaseValidation.TrySetResult();
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public async Task RegularV4Projection_DataSessionFallbackValidationAndInvocationAreAtomic()
    {
        const string transferId = "transfer_regular_v4_projection_data_fallback_atomic";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "projection_data_fallback_atomic");
        var serviceGate = Assert.IsType<object>(typeof(SessionFileTransferService)
            .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service));
        using var dataSession = new BlockingProjectionCommitDataSession(
            $"session_{transferId}",
            transferId,
            serviceGate);
        SetPrivateProperty(context, "DataSession", dataSession);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var mutationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationAcquiredGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? mutationTask = null;
        try
        {
            var sendTask = Task.Run(async () =>
            {
                var innerSendTask = Assert.IsAssignableFrom<Task<bool>>(typeof(SessionFileTransferService)
                    .GetMethod("SendInboundV4StateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, [context, "frontier_stall_repair_due", false, true, true, true]));
                return await innerSendTask.ConfigureAwait(false);
            });
            await dataSession.SendInvoked.Task.WaitAsync(TimeSpan.FromSeconds(5));

            mutationTask = Task.Run(() =>
            {
                mutationStarted.TrySetResult();
                lock (serviceGate)
                {
                    mutationAcquiredGate.TrySetResult();
                    var owner = Assert.IsType<RuntimeOwnerToken>(GetPrivatePropertyValue(context, "CurrentRuntimeOwner"));
                    SetPrivateProperty(context, "CurrentRuntimeOwner", owner with { RuntimeGeneration = owner.RuntimeGeneration + 1 });
                    typeof(SessionFileTransferService)
                        .GetMethod("ReconcileInboundRegularV4FrontierRepairProjectionLocked", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, [context, "unit_test_data_fallback_scope_replacement"]);
                }
            });
            await mutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(dataSession.CoreGateHeldOnInvocation);
            var unexpectedlyEnteredCoreGate = Monitor.TryEnter(serviceGate);
            if (unexpectedlyEnteredCoreGate)
            {
                Monitor.Exit(serviceGate);
            }
            Assert.False(unexpectedlyEnteredCoreGate);
            Assert.False(mutationAcquiredGate.Task.IsCompleted);

            dataSession.ReleaseSend.TrySetResult();
            Assert.True(await sendTask.WaitAsync(TimeSpan.FromSeconds(5)));
            await mutationTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(mutationAcquiredGate.Task.IsCompletedSuccessfully);
            Assert.IsType<FileTransferStateFrameV4>(Assert.Single(dataSession.SentFrames));
        }
        finally
        {
            dataSession.ReleaseSend.TrySetResult();
            if (mutationTask is not null)
            {
                await mutationTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private sealed class ReorderingProjectionDataSession(string sessionId, string transferId) : IFileTransferDataSession
    {
        private readonly object sync = new();
        private readonly List<FileTransferStateFrameV4> completedStates = [];
        private int sendCount;

        public string SessionId { get; } = sessionId;

        public string TransferId { get; } = transferId;

        public bool IsAvailable => true;

        public TaskCompletionSource FirstSendCaptured { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstSend { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<FileTransferStateFrameV4> CompletedStates
        {
            get
            {
                lock (sync)
                {
                    return completedStates.ToArray();
                }
            }
        }

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged
        {
            add { }
            remove { }
        }

        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public async Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
        {
            var state = Assert.IsType<FileTransferStateFrameV4>(frame);
            var ordinal = Interlocked.Increment(ref sendCount);
            if (ordinal == 1)
            {
                FirstSendCaptured.TrySetResult();
                await ReleaseFirstSend.Task.WaitAsync(ct);
            }

            lock (sync)
            {
                completedStates.Add(state);
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class BlockingProjectionCommitDataSession(
        string sessionId,
        string transferId,
        object coreGate) : IFileTransferDataSession
    {
        private readonly object sync = new();
        private readonly List<FileTransferDataFrame> sentFrames = [];

        public string SessionId { get; } = sessionId;

        public string TransferId { get; } = transferId;

        public bool IsAvailable => true;

        public bool CoreGateHeldOnInvocation { get; private set; }

        public TaskCompletionSource SendInvoked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSend { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<FileTransferDataFrame> SentFrames
        {
            get
            {
                lock (sync)
                {
                    return sentFrames.ToArray();
                }
            }
        }

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged
        {
            add { }
            remove { }
        }

        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
        {
            CoreGateHeldOnInvocation = Monitor.IsEntered(coreGate);
            lock (sync)
            {
                sentFrames.Add(frame);
            }

            SendInvoked.TrySetResult();
            ReleaseSend.Task.Wait(ct);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            ReleaseSend.TrySetResult();
        }
    }

    private static object CreateProjectionInboundContext(string transferId)
    {
        var context = CreateRegularNknV4InboundContext(
            transferId,
            chunkCount: 256,
            nextChunkIndex: 9,
            durableHighestChunkIndex: 20,
            creditUntilChunkIndexExclusive: 256);
        var written = Assert.IsType<BitArray>(GetPrivatePropertyValue(context, "ReceiverSparseChunksWritten"));
        for (var chunkIndex = 9; chunkIndex < 21; chunkIndex++)
        {
            written[chunkIndex] = false;
        }
        return context;
    }

    private static void MarkProjectionTailDurable(object context)
    {
        var written = Assert.IsType<BitArray>(GetPrivatePropertyValue(context, "ReceiverSparseChunksWritten"));
        for (var chunkIndex = 12; chunkIndex < 21; chunkIndex++)
        {
            written[chunkIndex] = true;
        }

        SetPrivateProperty(context, "PullHighestReceivedChunkIndex", 20);
    }

    private static FileTransferStateFrameV4 CreateInboundState(
        SessionFileTransferService service,
        object context,
        string reason,
        bool forceMissingRange = false)
        => Assert.IsType<FileTransferStateFrameV4>(typeof(SessionFileTransferService)
            .GetMethod("CreateInboundV4StateLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, reason, false, forceMissingRange]));

    private static FileTransferStateFrameV4 CreatePeerProjectionState(string transferId, int epoch)
        => new()
        {
            SessionId = $"session_{transferId}",
            TransferId = transferId,
            Epoch = epoch,
            ContiguousCommittedChunkIndex = 9,
            DurableReceivedHighestChunkIndex = 14,
            CreditUntilChunkIndexExclusive = 12,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 9, ChunkCount = 3 }],
            BytesCommitted = 15L * 21 * 1024,
        };

    private static void InvokeEnqueueAuthorizedV4RepairsFromState(
        SessionFileTransferService service,
        object context,
        FileTransferStateFrameV4 state)
        => typeof(SessionFileTransferService)
            .GetMethod("EnqueueAuthorizedV4RepairsFromState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, state, null, false, null, null, null, 0L]);
}
