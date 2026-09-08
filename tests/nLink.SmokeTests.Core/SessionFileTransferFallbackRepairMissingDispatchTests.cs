using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData("exact")]
    [InlineData("duplicate")]
    [InlineData("new_active_repair")]
    [InlineData("queue_rejected")]
    [InlineData("stale_receive_session")]
    [Trait("FileTransferRuntimeSuite", "FallbackTailUnblockRecheck")]
    public async Task FallbackTailUnblockRecheck_RecentlySentSparsePeerRepairMissingDispatch_RequeuesExactHeldRequest(string scenario)
    {
        var transferId = $"transfer_sparse_held_repair_missing_dispatch_{scenario}";
        var sessionId = $"session_{transferId}";
        using var bridge = new MissingDispatchRecheckTransport(sessionId)
        {
            RejectRechecks = scenario == "queue_rejected",
        };
        using var service = new SessionFileTransferService();
        service.AttachTransport(bridge);
        var context = CreatePostTunaFallbackV6OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 128,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 256,
            chunkCount: 256,
            startCoordinatorSnapshotStream: false);
        using var dataSession = new RecordingDataSession(sessionId, transferId);
        SetPrivateProperty(context, "DataSession", dataSession);
        var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        leg.State = FileTransferLegState.Active;
        leg.CanSendData = true;
        leg.CheckpointRequestId = null;
        leg.CheckpointPriority = null;
        leg.CheckpointRequestedUtc = null;
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        object install;
        RuntimeUnlockRetryDispatchAuthorityRequest retryRequest;
        lock (GetServiceGate(service))
        {
            // Finish raw fixture source setup before the immutable stream begins.
            StartCoordinatorSnapshotStreamForTest(context);
            PrepareMissingDispatchPairedTail(service, context, leg);
            retryRequest = Assert.IsType<RuntimeUnlockRetryDispatchAuthorityRequest>(
                GetCoordinatorFallbackTailYieldSubstate(context).PendingRetryRecheckRequest);
            var staged = StageSparsePumpInstall(service, context, null, "held_missing_dispatch_fixture");
            Assert.True(staged.Accepted, staged.Reason);
            install = Assert.IsAssignableFrom<object>(staged.Install);
            var committed = CommitSparsePumpInstall(service, context, install, dataSession);
            Assert.True(committed.Accepted, committed.Reason);
        }

        ApplyRequest("first", chunkCount: 2);
        var firstQueued = Assert.Single(GetQueuedV4RepairSends(context));
        var originalKey = GetStringProperty(firstQueued, "RepairRequestKey");
        bridge.RaiseAuthorityRequest(retryRequest);
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Held, Assert.Single(bridge.Decisions).Status);
        Assert.NotNull(GetCoordinatorFallbackTailYieldSubstate(context).CurrentRetryDispatchHold);

        // Let the production sparse pump send and stamp LastSentUtc. No synthetic
        // sent bookkeeping or direct settlement call supplies the target condition.
        using var source = new MemoryStream(new byte[256 * 21 * 1024], writable: false);
        using var pumpCancellation = new CancellationTokenSource();
        var priorCompletion = SessionFileTransferService.OutboundSparseSenderPumpNormalCompletionForTests;
        Task? pump = null;
        try
        {
            SessionFileTransferService.OutboundSparseSenderPumpNormalCompletionForTests = (id, _) =>
                id == transferId && dataSession.SentFrames.Count > 0;
            pump = Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
                .GetMethod("RunOutboundV4SenderPumpAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, source, dataSession, pumpCancellation.Token, install]));
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            pumpCancellation.Cancel();
            if (pump is not null)
            {
                try { await pump; }
                catch (OperationCanceledException) when (pumpCancellation.IsCancellationRequested) { }
            }
            SessionFileTransferService.OutboundSparseSenderPumpNormalCompletionForTests = priorCompletion;
        }

        Assert.NotEmpty(dataSession.SentFrames.OfType<FileTransferChunkBatchFrameV4>());
        var repairRequests = Assert.IsAssignableFrom<IDictionary>(
            GetPrivatePropertyValue(context, "PullV4SenderPumpRepairRequests"));
        var sentState = Assert.IsAssignableFrom<object>(repairRequests[originalKey]);
        Assert.NotNull(GetPrivatePropertyValue(sentState, "LastSentUtc"));
        Assert.False(GetPrivateProperty<bool>(sentState, "Queued"));
        Assert.False(GetPrivateProperty<bool>(sentState, "InFlight"));

        // A different live repair between equal ranges forces a genuine successor,
        // matching the artifact rather than an active-window correlation alias.
        ApplyRequest("intermediate", chunkCount: 1);
        var intermediate = Assert.IsAssignableFrom<object>(
            GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
        var intermediateAuthority = GetPrivateProperty<FallbackFrontierRepairAuthority>(intermediate, "Authority");
        var before = Assert.IsType<FileTransferFallbackTailYieldRetryDispatchHold>(
            GetCoordinatorFallbackTailYieldSubstate(context).CurrentRetryDispatchHold);
        Assert.Equal(intermediateAuthority, before.BlockingRepair);
        var sentCount = dataSession.SentFrames.Count;
        var logStart = GetOperationalLogLength();

        if (scenario == "stale_receive_session")
        {
            using var wrongSession = new RecordingDataSession(sessionId, transferId);
            ApplyRequest("suppressed", chunkCount: 2, receivedSession: wrongSession);
            Assert.Same(intermediate, GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
            Assert.Equal(before, GetCoordinatorFallbackTailYieldSubstate(context).CurrentRetryDispatchHold);
            Assert.Null(GetPrivatePropertyValue(context, "LastSettledFallbackFrontierRepairAuthority"));
            Assert.Equal(0L, GetPrivateProperty<long>(context, "LastSettledFallbackFrontierRepairUtcMs"));
            Assert.Empty(bridge.Rechecks);
            Assert.Empty(wrongSession.SentFrames);
            Assert.Equal(sentCount, dataSession.SentFrames.Count);
        }

        ApplyRequest("suppressed", chunkCount: 2);

        var suppressed = Assert.IsAssignableFrom<object>(
            GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
        var authority = GetPrivateProperty<FallbackFrontierRepairAuthority>(suppressed, "Authority");
        var lifecycle = Assert.IsAssignableFrom<object>(GetPrivatePropertyValue(suppressed, "Lifecycle"));
        Assert.NotSame(intermediate, suppressed);
        Assert.Equal(intermediateAuthority.AuthorityId, authority.PredecessorAuthorityId);
        Assert.Equal(originalKey, authority.CanonicalRepairKey);
        Assert.True(GetPrivateProperty<bool>(lifecycle, "IsSettled"));
        Assert.Equal("MissingDispatch", GetPrivatePropertyValue(lifecycle, "SettlementOutcome")?.ToString());
        Assert.False(GetPrivateProperty<bool>(lifecycle, "DispatchObserved"));
        Assert.Equal(sentCount, dataSession.SentFrames.Count);
        var hold = Assert.IsType<FileTransferFallbackTailYieldRetryDispatchHold>(
            GetCoordinatorFallbackTailYieldSubstate(context).CurrentRetryDispatchHold);
        Assert.Equal(authority, hold.BlockingRepair);
        Assert.Equal(before.HoldGeneration + 1, hold.HoldGeneration);
        Assert.Equal(before.Request, hold.Request);
        Assert.Equal(before.Release, hold.Release);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("decision_reason=repair_queue_suppressed_recently_sent;", log, StringComparison.Ordinal);
        Assert.Contains("reason=recently_sent;", log, StringComparison.Ordinal);

        // Baseline red: production reaches the exact settlement above but never
        // records this witness or asks the bridge to recheck the held request.
        Assert.Equal(authority, Assert.IsType<FallbackFrontierRepairAuthority>(
            GetPrivatePropertyValue(context, "LastSettledFallbackFrontierRepairAuthority")));
        Assert.Equal("MissingDispatch", GetPrivatePropertyValue(context, "LastSettledFallbackFrontierRepairOutcome"));
        var witnessUtcMs = GetPrivateProperty<long>(context, "LastSettledFallbackFrontierRepairUtcMs");
        Assert.True(witnessUtcMs > 0);
        var command = Assert.Single(bridge.Rechecks);
        Assert.Equal(hold, command.Hold);
        Assert.Equal(authority, command.Observation.SettledRepair);
        Assert.Equal("MissingDispatch", command.Observation.SettlementOutcome);
        Assert.Null(command.Observation.SuccessorAuthorityId);
        var receipt = Assert.IsType<FileTransferFallbackTailYieldRetryDispatchRecheckReceipt>(
            GetCoordinatorFallbackTailYieldSubstate(context).LastRetryDispatchRecheckReceipt);
        Assert.Equal(command, receipt.Command);
        Assert.Equal(bridge.RejectRechecks
            ? FileTransferFallbackTailYieldRetryDispatchRecheckReceiptStatus.Rejected
            : FileTransferFallbackTailYieldRetryDispatchRecheckReceiptStatus.Queued, receipt.Status);
        Assert.Equal(1, GetCoordinatorFallbackTailYieldSubstate(context).LastRetryDispatchRecheckGeneration);

        if (scenario is "duplicate" or "queue_rejected")
        {
            ApplyRequest("suppressed", chunkCount: 2);
            lock (GetServiceGate(service))
            {
                typeof(SessionFileTransferService)
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Single(method => method.Name == "TryQueueFallbackPairedTailYieldRuntimeUnlockRetryDispatchLocked" &&
                        method.GetParameters().Length == 3 && method.GetParameters()[0].ParameterType == context.GetType())
                    .Invoke(service, [context, hold.Release, "duplicate_missing_dispatch_recheck"]);
            }
            Assert.Same(suppressed, GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
            Assert.Equal(witnessUtcMs, GetPrivateProperty<long>(context, "LastSettledFallbackFrontierRepairUtcMs"));
            Assert.Equal(authority, GetPrivatePropertyValue(context, "LastSettledFallbackFrontierRepairAuthority"));
            Assert.Equal(hold, GetCoordinatorFallbackTailYieldSubstate(context).CurrentRetryDispatchHold);
            Assert.Equal(command, Assert.Single(bridge.Rechecks));
            Assert.Equal(receipt, GetCoordinatorFallbackTailYieldSubstate(context).LastRetryDispatchRecheckReceipt);
            Assert.Equal(1, GetCoordinatorFallbackTailYieldSubstate(context).LastRetryDispatchRecheckGeneration);
            if (bridge.RejectRechecks)
            {
                Assert.Null(GetCoordinatorFallbackTailYieldSubstate(context).PendingRetryDispatchRecheckCommand);
            }
        }
        else if (scenario == "new_active_repair")
        {
            var owner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
            var publication = GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken");
            ApplyRequest("after_recheck", chunkCount: 3);
            var active = Assert.IsAssignableFrom<object>(GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
            var activeAuthority = GetPrivateProperty<FallbackFrontierRepairAuthority>(active, "Authority");
            Assert.NotSame(suppressed, active);
            Assert.False(GetPrivateProperty<bool>(GetPrivatePropertyValue(active, "Lifecycle")!, "IsSettled"));

            // Deliver the captured request through the attached Core callback,
            // after the new repair wins. The old observation must not grant it.
            bridge.RaiseAuthorityRequest(command.Request);
            Assert.Equal(2, bridge.Decisions.Count);
            Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Held, bridge.Decisions[^1].Status);
            Assert.Equal("fallback_paired_tail_yield_pending", bridge.Decisions[^1].Reason);
            var rebound = Assert.IsType<FileTransferFallbackTailYieldRetryDispatchHold>(
                GetCoordinatorFallbackTailYieldSubstate(context).CurrentRetryDispatchHold);
            Assert.Equal(activeAuthority, rebound.BlockingRepair);
            Assert.Equal(hold.Request, rebound.Request);
            Assert.Equal(hold.Release, rebound.Release);
            Assert.Equal(hold.HoldGeneration + 1, rebound.HoldGeneration);
            Assert.Null(GetCoordinatorFallbackTailYieldSubstate(context).PendingRetryDispatchRecheckCommand);
            Assert.Equal(owner, GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"));
            Assert.Equal(publication, GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken"));
            Assert.Equal(command, Assert.Single(bridge.Rechecks));
            Assert.Equal(witnessUtcMs, GetPrivateProperty<long>(context, "LastSettledFallbackFrontierRepairUtcMs"));
        }
        Assert.Equal(sentCount, dataSession.SentFrames.Count);
        Assert.Equal(0, bridge.StageCount);

        void ApplyRequest(string suffix, int chunkCount, IFileTransferDataSession? receivedSession = null)
        {
            var owner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
            var request = new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 3,
                RepairRequestId = $"v6-frontier:128:{suffix}",
                RequestCorrelationId = $"frq-held-missing-dispatch-{suffix}",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 128, ChunkCount = chunkCount }],
                Priority = "frontier",
                RecoveryMode = "regular_nkn_frontier_stall_control_bulk",
            };
            typeof(SessionFileTransferService)
                .GetMethod("ApplyOutboundV6RepairRequestCore", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, request, FileTransferTransportKind.RegularNkn, install, owner, receivedSession ?? dataSession, 1L, 0L]);
        }
    }

    private static void PrepareMissingDispatchPairedTail(
        SessionFileTransferService service, object context, FileTransferLeg leg)
    {
        object?[] arguments =
        [
            context, leg, leg.TransportEpochId, leg.ProvenCommittedChunkIndex,
            leg.ProvenHighestObservedChunkIndex, "held_missing_dispatch_yield", null,
        ];
        Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod("TryMarkOutboundFallbackTailQuiescentWithCoordinatorLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, arguments)));
        var yield = Assert.IsType<FallbackTailYieldDecision>(arguments[^1]);
        var state = CreateCoordinatorStateForTest(context);
        typeof(SessionFileTransferService)
            .GetMethod("ClearOutboundFallbackTailRecoveryOwnershipAfterYieldLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, leg, "held_missing_dispatch_paired_tail"]);
        state = CreateCoordinatorStateForTest(context);
        var transferId = GetPrivateProperty<string>(context, "TransferId");
        var sessionId = GetPrivateProperty<string>(context, "SessionId");
        var localReceipt = FileTransferCoordinator.CreateFallbackTailLocalProofReceipt(
            state, yield, $"local:{transferId}:1", $"local-frame:{transferId}:1",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.False(localReceipt.IsDefault);
        var local = FileTransferCoordinator.ApplyFallbackTailLocalProofReceipt(state, localReceipt);
        Assert.True(local.Accepted);
        var localState = AdoptFallbackTailYieldDecisionForTest(context, local);
        var peer = new FileTransferFallbackTailPeerProof(
            sessionId, transferId, FileTransferDirection.Inbound,
            new FileTransferAuthorityId(CreatePeerFallbackTailYieldAuthorityId(transferId)),
            PredecessorAuthorityId: null, YieldGeneration: 1,
            yield.OwnerKey, yield.PairKey, leg.LiveRouteEpochId, leg.Generation,
            leg.BridgeRecoveryGeneration, leg.TransportEpochId,
            yield.CheckpointRequestIdSnapshot, leg.ProvenCommittedChunkIndex,
            leg.ProvenHighestObservedChunkIndex, $"peer-frame:{transferId}:1");
        var peerReceipt = FileTransferCoordinator.CreateFallbackTailPeerProofReceipt(
            localState, peer, $"peer:{transferId}:1", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.False(peerReceipt.IsDefault);
        var paired = FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt(localState, peerReceipt);
        Assert.True(paired.Accepted);
        Assert.NotNull(paired.PairedRelease);
        _ = AdoptFallbackTailYieldDecisionForTest(context, paired);
        typeof(SessionFileTransferService)
            .GetMethod("HandleOutboundFallbackTailPairedReleaseLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "held_missing_dispatch_pair_proof"]);
    }

    private sealed class MissingDispatchRecheckTransport(string sessionId) :
        LoopbackFileTransferTransport(sessionId), IRuntimeUnlockRetryDispatchAuthorityBridge
    {
        private RuntimeUnlockRetryDispatchAuthorityRequest? pendingRequest;
        public bool RejectRechecks { get; init; }
        public int StageCount { get; private set; }
        public List<RuntimeUnlockRetryDispatchDecision> Decisions { get; } = [];
        public List<FileTransferFallbackTailYieldRetryDispatchRecheckCommand> Rechecks { get; } = [];
        public event EventHandler<RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs>? RuntimeUnlockRetryDispatchAuthorityRequested;
        public event EventHandler<RuntimeUnlockRetryDispatchInitialEvaluationRequestedEventArgs>? RuntimeUnlockRetryDispatchInitialEvaluationRequested { add { } remove { } }
        public event EventHandler<RuntimeUnlockRetryDispatchSettlementReportedEventArgs>? RuntimeUnlockRetryDispatchSettlementReported { add { } remove { } }
        public event EventHandler<RuntimeUnlockRetryDispatchDeadlineEvaluationEventArgs>? RuntimeUnlockRetryDispatchDeadlineEvaluationRequested { add { } remove { } }
        public event EventHandler<RuntimeUnlockRetryDispatchExecutionObservedEventArgs>? RuntimeUnlockRetryDispatchExecutionObserved { add { } remove { } }
        public event EventHandler<RuntimeUnlockRetryDispatchSuccessorEvaluationEventArgs>? RuntimeUnlockRetryDispatchSuccessorEvaluationRequested { add { } remove { } }

        public void RaiseAuthorityRequest(RuntimeUnlockRetryDispatchAuthorityRequest request)
        {
            pendingRequest = request;
            RuntimeUnlockRetryDispatchAuthorityRequested?.Invoke(this, new(request));
        }

        public bool ObserveRuntimeUnlockRetryDispatchNonGrantDecision(RuntimeUnlockRetryDispatchDecision decision)
        {
            Decisions.Add(decision);
            return true;
        }

        public bool QueueFallbackTailYieldRetryDispatchRecheck(FileTransferFallbackTailYieldRetryDispatchRecheckCommand command)
        {
            if (command.IsDefault || command.Request != pendingRequest ||
                Decisions.LastOrDefault().Status != RuntimeUnlockRetryDispatchDecisionStatus.Held)
            {
                return false;
            }
            if (!Rechecks.Contains(command)) { Rechecks.Add(command); }
            return !RejectRechecks;
        }

        public RuntimeUnlockRetryDispatchDecisionStageResult StageRuntimeUnlockRetryDispatchDecision(RuntimeUnlockRetryDispatchDecision decision)
        {
            StageCount++;
            throw new NotSupportedException("The held-recheck fixture must not grant a retry while a repair is active.");
        }
        public RuntimeUnlockRetryDispatchInstallResult CommitRuntimeUnlockRetryDispatchInstall(RuntimeUnlockRetryDispatchInstallReceipt receipt) => throw new NotSupportedException();
        public RuntimeUnlockRetryDispatchScopeInvalidationReceipt ApplyRuntimeUnlockRetryDispatchScopeInvalidation(RuntimeUnlockRetryDispatchScopeInvalidationCommand command) => throw new NotSupportedException();
        public RuntimeUnlockRetryDispatchScopeInvalidationStageResult StageRuntimeUnlockRetryDispatchScopeInvalidation(RuntimeUnlockRetryDispatchScopeInvalidationCommand command) => throw new NotSupportedException();
        public bool ConfirmRuntimeUnlockRetryDispatchInstall(RuntimeUnlockRetryDispatchInstallReceipt receipt) => false;
        public bool ObserveRuntimeUnlockRetryDispatchSettlementDecision(RuntimeUnlockRetryDispatchSettlementDecision decision) => false;
    }
}
