using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData("native")]
    [InlineData("retained")]
    [InlineData("wrong_command")]
    [InlineData("missing_command")]
    [InlineData("expiry")]
    [InlineData("terminal")]
    [InlineData("capacity")]
    [InlineData("queue_rejected")]
    [InlineData("duplicate")]
    [InlineData("replaced_session")]
    [InlineData("local_retry")]
    [InlineData("installed")]
    [Trait("FileTransferRuntimeSuite", "FallbackHandoffReservation")]
    public void FallbackHandoffReservation_DefersRepairUntilExactDispatchOrSafeRelease(string scenario)
    {
        var transferId = $"transfer_handoff_{scenario}";
        var sessionId = $"session_{transferId}";
        using var bridge = new MissingDispatchRecheckTransport(sessionId)
        {
            RejectGrantedStage = scenario != "installed", InstallGrantedStage = scenario == "installed",
            RejectRechecks = scenario == "queue_rejected",
        };
        using var service = new SessionFileTransferService();
        using var data = new RecordingDataSession(sessionId, transferId);
        service.AttachTransport(bridge);
        var context = CreatePostTunaFallbackV6OutboundContext(transferId,
            remoteNextExpectedChunkIndex: 128, startCoordinatorSnapshotStream: false, dataSession: data);
        var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        leg.State = FileTransferLegState.Active;
        leg.CanSendData = true;
        leg.CheckpointRequestId = null;
        leg.CheckpointPriority = null;
        leg.CheckpointRequestedUtc = null;
        typeof(SessionFileTransferService).GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        lock (GetServiceGate(service))
        {
            StartCoordinatorSnapshotStreamForTest(context);
            PrepareMissingDispatchPairedTail(service, context, leg);
        }
        var request = GetCoordinatorFallbackTailYieldSubstate(context).PendingRetryRecheckRequest!.Value;
        ApplyRepair(0);
        bridge.RaiseAuthorityRequest(request);
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Held, Assert.Single(bridge.Decisions).Status);
        var first = GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract");
        lock (GetServiceGate(service))
        {
            typeof(SessionFileTransferService).GetMethod("TrySettleOutboundFallbackFrontierProgressLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [context, 128, 129, "reservation_test"]);
        }
        var command = Assert.Single(bridge.Rechecks);
        var publication = GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken");
        var owner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        if (scenario == "queue_rejected")
        {
            Assert.Null(GetCoordinatorFallbackTailYieldSubstate(context).HandoffReservation);
            ApplyRepair(1);
            Assert.NotSame(first, GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
            return;
        }

        Assert.Equal(command, GetCoordinatorFallbackTailYieldSubstate(context).HandoffReservation);
        ApplyRepair(1);
        Assert.Same(first, GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
        var deferred = Assert.IsAssignableFrom<IList>(GetPrivatePropertyValue(context, "DeferredFallbackHandoffRepairs"));
        Assert.Single(deferred.Cast<object>());
        Assert.Equal(publication, GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken"));
        Assert.Equal(owner, GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"));

        if (scenario == "duplicate")
        {
            ApplyRepair(1);
            Assert.Single(deferred.Cast<object>());
            Assert.Equal(command.DeadlineUtcMs,
                GetCoordinatorFallbackTailYieldSubstate(context).HandoffReservation!.Value.DeadlineUtcMs);
        }
        if (scenario == "local_retry")
        {
            var retry = typeof(SessionFileTransferService).GetMethod("TryIssueOutboundFallbackFrontierRetrySuccessorLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            object?[] arguments = [context, first, FallbackFrontierRepairSourceKind.SenderTimeoutRetry, "test_late_retry", null];
            lock (GetServiceGate(service))
            {
                Assert.False(Assert.IsType<bool>(retry.Invoke(service, arguments)));
            }
            Assert.Same(first, GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
            Assert.Equal(command, GetCoordinatorFallbackTailYieldSubstate(context).HandoffReservation);
        }
        if (scenario == "capacity")
        {
            for (var i = 2; i <= 17; i++) { ApplyRepair(i); }
        }
        else if (scenario is "wrong_command" or "missing_command")
        {
            bridge.RaiseAuthorityRequest(request, scenario == "missing_command"
                ? null : command with { RecheckGeneration = command.RecheckGeneration + 1 });
            Assert.Single(bridge.Decisions);
            Assert.Equal(0, bridge.StageCount);
            Assert.Equal(command, GetCoordinatorFallbackTailYieldSubstate(context).HandoffReservation);
            Assert.Single(deferred.Cast<object>());
            ExpireHandoff(service, context, command);
        }
        else if (scenario == "expiry")
        {
            ExpireHandoff(service, context, command);
            var after = GetCoordinatorFallbackTailYieldSubstate(context);
            bridge.RaiseAuthorityRequest(request, command);
            Assert.Equal(after, GetCoordinatorFallbackTailYieldSubstate(context));
            Assert.Single(bridge.Decisions);
            Assert.Equal(0, bridge.StageCount);
        }
        else if (scenario is "terminal" or "replaced_session")
        {
            if (scenario == "terminal")
            {
                SetPrivateProperty(context, "State", FileTransferTransferState.Completed);
            }
            else
            {
                SetPrivateProperty(context, "DataSessionGeneration", 2L);
            }
            var before = GetCoordinatorFallbackTailYieldSubstate(context);
            bridge.RaiseAuthorityRequest(request, command);
            Assert.Equal(before, GetCoordinatorFallbackTailYieldSubstate(context));
            Assert.Single(bridge.Decisions);
            ExpireHandoff(service, context, command);
            Assert.Same(first, GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
            Assert.Empty(deferred);
            Assert.Equal(0, bridge.StageCount);
            return;
        }
        else
        {
            bridge.RaiseAuthorityRequest(request, command);
            Assert.Equal(1, bridge.StageCount);
            var granted = Assert.Single(bridge.Decisions.Where(item => item.Granted));
            Assert.Equal(command.DeadlineUtcMs, granted.Contract!.Authority.FallbackHandoffDeadlineUtcMs);
            if (scenario == "installed")
            {
                Assert.NotNull(GetCoordinatorFallbackTailYieldSubstate(context).LastConsumedReservation);
                Assert.Equal(FileTransferFallbackTailYieldRetryDispatchRecheckReceiptStatus.Queued,
                    GetCoordinatorFallbackTailYieldSubstate(context).LastRetryDispatchRecheckReceipt!.Value.Status);
            }
            else
            {
                Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, bridge.Decisions[^1].Status);
            }
            var count = bridge.Decisions.Count;
            var after = GetCoordinatorFallbackTailYieldSubstate(context);
            bridge.RaiseAuthorityRequest(request, command);
            Assert.Equal(count, bridge.Decisions.Count);
            Assert.Equal(after, GetCoordinatorFallbackTailYieldSubstate(context));
        }

        Assert.Null(GetCoordinatorFallbackTailYieldSubstate(context).HandoffReservation);
        Assert.Null(GetCoordinatorFallbackTailYieldSubstate(context).CurrentReservation);
        Assert.Empty(deferred);
        Assert.NotSame(first, GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
        Assert.Equal(publication, GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken"));
        Assert.Equal(owner, GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"));
        Assert.Empty(data.SentFrames);

        void ApplyRepair(int index)
        {
            var frame = new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId, TransferId = transferId, TransportEpoch = 3,
                RepairRequestId = $"v6-frontier:handoff:{index}", RequestCorrelationId = $"handoff-{index}",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 128 + index, ChunkCount = 1 }],
                Priority = "frontier", RecoveryMode = "regular_nkn_frontier_stall_control_bulk",
            };
            var method = typeof(SessionFileTransferService).GetMethod(scenario == "retained" && index > 0
                    ? "ApplyOutboundV6RepairRequestCore" : "ApplyOutboundAuthorizedFallbackFrontierRequest",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            lock (GetServiceGate(service))
            {
                method.Invoke(service, scenario == "retained" && index > 0
                    ? [context, frame, FileTransferTransportKind.RegularNkn, null, null, null, 0L, 0L]
                    : [context, frame, FileTransferTransportKind.RegularNkn]);
            }
        }
    }

    private static void ExpireHandoff(SessionFileTransferService service, object context,
        FileTransferFallbackTailYieldRetryDispatchRecheckCommand command)
    {
        lock (GetServiceGate(service))
        {
            typeof(SessionFileTransferService).GetMethod("ReleaseFallbackHandoffLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
                [context, command, FallbackHandoffReleaseReason.DeadlineExpired, command.DeadlineUtcMs]);
        }
    }
}
