using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackCheckpointReplay")]
    public void RetiredOutboundCheckpoint_CannotReopenAfterOwnerReplacement()
    {
        const string transferId = "outbound_checkpoint_replay";
        var type = typeof(SessionFileTransferService);
        using var service = new SessionFileTransferService();
        var context = CreatePostTunaFallbackV6OutboundContext(transferId);
        type.GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);
        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = $"session_{transferId}",
            TransferId = transferId,
            TransportEpoch = 3,
            RepairRequestId = "v6-regular-nkn-state-refresh:1",
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 100, ChunkCount = 1 }],
            Priority = "state_refresh",
            RecoveryMode = "regular_nkn_state_refresh",
        };
        var mark = type.GetMethod("MarkOutboundFallbackCheckpointRequestedLocked", BindingFlags.Instance | BindingFlags.NonPublic)!;
        lock (GetServiceGate(service))
        {
            Assert.True(Assert.IsType<bool>(mark.Invoke(service, [context, request, "checkpoint_replay_fixture"])));
            Assert.True(Assert.IsType<bool>(type.GetMethod("RetireOutboundFallbackCheckpointExchangeLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
                    [context, request.RepairRequestId, "checkpoint_replay_fixture"])));
            var retired = CreateCoordinatorStateForTest(context);
            Assert.Null(retired.CurrentFallbackCheckpointLease);
            Assert.IsType<FileTransferLeg>(type.GetMethod("StartOutboundTransferLegLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
                    [context, retired.RouteSelection, "checkpoint_replay_fixture", FileTransferLegState.Active,
                        true, true, 3L, retired.CurrentLeg!.BridgeRecoveryGeneration + 1, null, false, null, null, null, false]));
            var replacement = CreateCoordinatorStateForTest(context);
            Assert.Equal(retired.CurrentRuntimeOwner!.Value.RuntimeGeneration + 1,
                replacement.CurrentRuntimeOwner!.Value.RuntimeGeneration);
            var fingerprint = FileTransferCoordinator.CalculateSnapshotStateFingerprint(replacement);
            Assert.False(Assert.IsType<bool>(mark.Invoke(service, [context, request, "checkpoint_replay_fixture"])));
            Assert.Equal(fingerprint, FileTransferCoordinator.CalculateSnapshotStateFingerprint(CreateCoordinatorStateForTest(context)));
            Assert.Null(CreateCoordinatorStateForTest(context).CurrentFallbackCheckpointLease);

            var fresh = request with { RepairRequestId = "v6-regular-nkn-state-refresh:2" };
            Assert.True(Assert.IsType<bool>(mark.Invoke(service, [context, fresh, "checkpoint_replay_fixture"])));
            var current = CreateCoordinatorStateForTest(context);
            var lease = Assert.IsType<FallbackCheckpointLease>(current.CurrentFallbackCheckpointLease);
            Assert.Equal(replacement.CurrentRuntimeOwner, current.CurrentRuntimeOwner);
            Assert.Equal(current.CurrentRuntimeOwner, lease.RuntimeOwner);
            Assert.Equal(fresh.RepairRequestId, lease.CheckpointRequestId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "FallbackCheckpointReplay")]
    public async Task RetiredFallbackCheckpointReplay_DoesNotReplaceOwnerOrRetireRepair(bool replaceOwner)
    {
        var transferId = $"checkpoint_replay_{replaceOwner}";
        var sessionId = $"session_{transferId}";
        var type = typeof(SessionFileTransferService);
        using var service = new SessionFileTransferService();
        using var transport = new PeerVisibleControlPlaneLoopbackTransport(sessionId, deliverFrames: false);
        service.AttachTransport(transport);
        var context = CreatePostTunaFallbackV6InboundContext(transferId);
        SetPrivateProperty(context, "V6ReceiverTransportEpoch", 3L);
        SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 255);
        SetPrivateProperty(context, "V6DestinationMode", Enum.Parse(
            type.GetNestedType("V6ReceiveDestinationMode", BindingFlags.NonPublic)!, "SparseSeekable"));
        var written = new System.Collections.BitArray(256, true);
        written[100] = false;
        SetPrivateProperty(context, "ReceiverSparseChunksWritten", written);
        type.GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);
        var mark = type.GetMethod("MarkInboundFallbackCheckpointRequestedLocked", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = sessionId,
            TransferId = transferId,
            TransportEpoch = 3,
            RepairRequestId = "v6-regular-nkn-state-refresh:1",
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 100, ChunkCount = 1 }],
            Priority = "state_refresh",
            RecoveryMode = "regular_nkn_state_refresh",
        };

        // Requests are incoming observations; the real handlers/coordinator own
        // every checkpoint acceptance and owner replacement in this fixture.
        await CompleteCheckpointAsync(request);
        var first = CreateCoordinatorStateForTest(context);
        if (replaceOwner)
        {
            await CompleteCheckpointAsync(request with { RepairRequestId = "v6-regular-nkn-state-refresh:2" });
            var replacement = CreateCoordinatorStateForTest(context);
            Assert.Equal(first.CurrentRuntimeOwner!.Value.RuntimeGeneration + 1,
                replacement.CurrentRuntimeOwner!.Value.RuntimeGeneration);
            Assert.Equal(first.CurrentRoutePublicationToken!.Value.PublicationGeneration + 1,
                replacement.CurrentRoutePublicationToken!.Value.PublicationGeneration);
        }

        var send = type.GetMethod("SendInboundV6FrontierRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await Assert.IsAssignableFrom<Task<bool>>(send.Invoke(service, [context, "chunk_batch_committed", false]));
        await WaitUntilAsync(() => GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract") is not null,
            timeoutMs: 5000);
        var repair = GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract")!;
        var lifecycle = GetPrivatePropertyValue(repair, "Lifecycle")!;
        await WaitUntilAsync(() => GetPrivateProperty<bool>(lifecycle, "DispatchObserved"), timeoutMs: 5000);
        FileTransferCoordinatorState source;
        var logStart = GetOperationalLogLength();
        lock (GetServiceGate(service))
        {
            source = CreateCoordinatorStateForTest(context);
            var fingerprint = FileTransferCoordinator.CalculateSnapshotStateFingerprint(source);
            Assert.False(Assert.IsType<bool>(mark.Invoke(service, [context, request, "state_refresh_received"])));
            var after = CreateCoordinatorStateForTest(context);
            Assert.Equal(fingerprint, FileTransferCoordinator.CalculateSnapshotStateFingerprint(after));
            Assert.Equal(source.CurrentRuntimeOwner, after.CurrentRuntimeOwner);
            Assert.Equal(source.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
            Assert.Same(source.PersistentState.CheckpointRepairContinuityOrigin, after.PersistentState.CheckpointRepairContinuityOrigin);
            Assert.Null(after.CurrentFallbackCheckpointLease);
            Assert.Same(repair, GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
            Assert.False(GetPrivateProperty<bool>(lifecycle, "IsSettled"));
            Assert.Equal(100, GetPrivateProperty<int>(context, "NextChunkIndex"));
        }
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("reason=retired_checkpoint_request_replay", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_fallback_checkpoint_recovery_leg_started;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_fallback_checkpoint_requested;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime_owner_retired_state_refresh_received", log, StringComparison.Ordinal);

        // The rejection key must retain transport scope. A new transport's
        // request still needs, and can obtain, a new coordinator-owned lease.
        var nextEpochRequest = request with { TransportEpoch = 4 };
        lock (GetServiceGate(service))
        {
            Assert.True(Assert.IsType<bool>(mark.Invoke(service, [context, nextEpochRequest, "state_refresh_received"])));
        }
        var next = CreateCoordinatorStateForTest(context);
        var lease = Assert.IsType<FallbackCheckpointLease>(next.CurrentFallbackCheckpointLease);
        Assert.Equal(next.CurrentRuntimeOwner, lease.RuntimeOwner);
        Assert.Equal(nextEpochRequest.RepairRequestId, lease.CheckpointRequestId);
        Assert.Equal(4L, next.CurrentLeg!.TransportEpochId);
        Assert.Equal(source.CurrentRuntimeOwner!.Value.RuntimeGeneration + 1, next.CurrentRuntimeOwner!.Value.RuntimeGeneration);

        async Task CompleteCheckpointAsync(FileTransferFrontierRequestFrameV6 incoming)
        {
            lock (GetServiceGate(service))
            {
                Assert.True(Assert.IsType<bool>(mark.Invoke(service, [context, incoming, "state_refresh_received"])));
                var pending = CreateCoordinatorStateForTest(context);
                var pendingLease = Assert.IsType<FallbackCheckpointLease>(pending.CurrentFallbackCheckpointLease);
                // Pending duplicate delivery remains idempotent, not retired.
                Assert.True(Assert.IsType<bool>(mark.Invoke(service, [context, incoming, "state_refresh_received"])));
                Assert.Equal(pending.CurrentRuntimeOwner, CreateCoordinatorStateForTest(context).CurrentRuntimeOwner);
                Assert.Equal(pendingLease, CreateCoordinatorStateForTest(context).CurrentFallbackCheckpointLease);
            }
            Assert.True(await Assert.IsAssignableFrom<Task<bool>>(type.GetMethod("SendInboundV6ReceiverStateAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
                    [context, "regular_nkn_state_refresh", true, false, null])));
            Assert.True(transport.LastValidationAccepted);
            var accepted = CreateCoordinatorStateForTest(context);
            Assert.Null(accepted.CurrentFallbackCheckpointLease);
            Assert.Equal(incoming.RepairRequestId, Assert.IsType<CheckpointRepairContinuityOrigin>(
                accepted.PersistentState.CheckpointRepairContinuityOrigin).Snapshot.AcceptedCheckpoint.CheckpointRequestId);
        }
    }
}
