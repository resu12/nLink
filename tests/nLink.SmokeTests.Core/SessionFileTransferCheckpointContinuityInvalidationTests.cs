using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData("peer_state_paused")]
    [InlineData("stale_peer_state")]
    [InlineData("local_failure")]
    [Trait("FileTransferRuntimeSuite", "CheckpointRepairContinuity")]
    public void CheckpointContinuity_ActualPeerStateAndLocalFailurePathsInvalidateOnlyAcceptedEvidence(string path)
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var contextType = serviceType.GetNestedType("InboundTransferContext", BindingFlags.NonPublic)!;
        var route = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var sessionId = "continuity-invalidation-session-" + Guid.NewGuid().ToString("N");
        var transferId = "continuity-invalidation-transfer-" + Guid.NewGuid().ToString("N");
        var context = Activator.CreateInstance(contextType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [new FileTransferOfferV2
            {
                SessionId = sessionId, TransferId = transferId, FileName = "invalidation.bin", FileSizeBytes = 256 * 1024,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6, FileTransferRoute = route.TelemetryToken,
            }, route], culture: null)!;
        SetPrivateProperty(context, "State", FileTransferTransferState.Receiving);
        SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV6);
        SetPrivateProperty(context, "ChunkSizeBytes", 1024);
        SetPrivateProperty(context, "ChunkCount", 256);
        SetPrivateProperty(context, "NextChunkIndex", 100);
        SetPrivateProperty(context, "PullHighestReceivedChunkIndex", 255);
        SetPrivateProperty(context, "DataSession", new RecordingDataSession(sessionId, transferId));
        SetPrivateProperty(context, "DataSessionGeneration", 1L);
        var leg = SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound,
            "continuity_invalidation", startCoordinatorSnapshotStream: false);
        leg.TransportEpochId = 3;
        leg.BridgeRecoveryGeneration = 1;
        serviceType.GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);
        StartCoordinatorSnapshotStreamForTest(context);
        lock (GetServiceGate(service))
        {
            ApplyCheckpoint(FileTransferCoordinatorEventKind.FallbackCheckpointRequested, FileTransferLegState.CheckpointPending, false);
            ApplyCheckpoint(FileTransferCoordinatorEventKind.FallbackCheckpointAccepted, FileTransferLegState.RecoveryActive, true);
        }
        var before = CreateCoordinatorStateForTest(context);
        var origin = Assert.IsType<CheckpointRepairContinuityOrigin>(before.PersistentState.CheckpointRepairContinuityOrigin);
        if (path == "local_failure")
        {
            serviceType.GetMethod("FailInboundLocally", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, "test_failure", "local continuity invalidation"]);
            Assert.Equal(FileTransferTransferState.Failed, GetPrivateProperty<FileTransferTransferState>(context, "State"));
        }
        else
        {
            SetPrivateProperty(context, "PeerV4LastStateEpoch", path == "stale_peer_state" ? 2 : 0);
            serviceType.GetMethod("ApplyInboundV4PeerState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, new FileTransferStateFrameV4
                {
                    SessionId = sessionId, TransferId = transferId, Epoch = 1, TransferPaused = true,
                }]);
            Assert.Equal(path != "stale_peer_state", GetPrivateProperty<bool>(context, "PeerPaused"));
        }
        var after = CreateCoordinatorStateForTest(context);
        if (path == "stale_peer_state")
        {
            Assert.Same(origin, after.PersistentState.CheckpointRepairContinuityOrigin);
            Assert.Equal(before.PersistentState, after.PersistentState);
        }
        else
        {
            Assert.Null(after.PersistentState.CheckpointRepairContinuityOrigin);
            Assert.Equal(before.PersistentState.SnapshotStateGeneration + 1, after.PersistentState.SnapshotStateGeneration);
        }
        Assert.Equal(before.CurrentRuntimeOwner, after.CurrentRuntimeOwner);
        Assert.Equal(before.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);

        void ApplyCheckpoint(FileTransferCoordinatorEventKind kind, FileTransferLegState state, bool canSend)
        {
            var coordinator = CreateCoordinatorStateForTest(context);
            var currentLeg = coordinator.CurrentLeg!;
            var decision = FileTransferCoordinator.Apply(new(kind, route, FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.Unknown, "regular_nkn_state_refresh", state, canSend, 100, 255,
                currentLeg.TransportEpochId, currentLeg.BridgeRecoveryGeneration, "continuity-invalidation-checkpoint", "state_refresh"), coordinator);
            Assert.Null(decision.MutationRejectedReason);
            serviceType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(method => method.Name == "ApplyCoordinatorDecisionToContextLocked" && method.GetParameters().Length == 4 &&
                    method.GetParameters()[0].ParameterType == contextType)
                .Invoke(null, [context, decision, true, true]);
        }
    }
}
