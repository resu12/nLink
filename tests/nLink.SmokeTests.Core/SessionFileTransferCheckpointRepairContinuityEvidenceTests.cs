using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "CheckpointRepairContinuity")]
    public async Task CheckpointRepairContinuity_RealServiceIssuesStampedCurrentRepairAfterCheckpointAcceptance()
        => await CreateCheckpointRepairContinuityEvidenceFixtureAsync();

    internal static async Task<IReadOnlyList<string>> CreateCheckpointRepairContinuityEvidenceFixtureAsync()
    {
        var transferId = "transfer_checkpoint_continuity_" + Guid.NewGuid().ToString("N");
        var sessionId = "session_" + transferId;
        var serviceType = typeof(SessionFileTransferService);
        using var delivery = new PeerVisibleControlPlaneLoopbackTransport(sessionId, deliverFrames: false);
        using var service = new SessionFileTransferService();
        service.AttachTransport(delivery);
        var contextType = serviceType.GetNestedType("InboundTransferContext", BindingFlags.NonPublic)!;
        var route = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        const int chunkSize = 21 * 1024;
        var context = Activator.CreateInstance(contextType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [new FileTransferOfferV2
            {
                SessionId = sessionId, TransferId = transferId, FileName = "continuity.bin",
                FileSizeBytes = 256L * chunkSize, PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                FileTransferRoute = route.TelemetryToken,
            }, route], culture: null)!;
        SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV6);
        SetPrivateProperty(context, "State", FileTransferTransferState.Receiving);
        SetPrivateProperty(context, "ChunkSizeBytes", chunkSize);
        SetPrivateProperty(context, "ChunkCount", 256);
        SetPrivateProperty(context, "PullSessionActive", true);
        SetPrivateProperty(context, "PullManifestReceived", true);
        SetPrivateProperty(context, "PullPostTunaRecoveryActive", true);
        SetPrivateProperty(context, "ReceiverSparseWriteActive", true);
        SetPrivateProperty(context, "V6DestinationMode", Enum.Parse(
            serviceType.GetNestedType("V6ReceiveDestinationMode", BindingFlags.NonPublic)!, "SparseSeekable"));
        SetPrivateProperty(context, "NextChunkIndex", 100);
        SetPrivateProperty(context, "PullHighestReceivedChunkIndex", 255);
        SetPrivateProperty(context, "V6ReceiverTransportEpoch", 3L);
        SetPrivateProperty(context, "DataSession", new RecordingDataSession(sessionId, transferId));
        SetPrivateProperty(context, "DataSessionGeneration", 1L);
        var leg = SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound,
            "checkpoint_continuity_fixture", startCoordinatorSnapshotStream: false);
        leg.TransportEpochId = 3;
        leg.BridgeRecoveryGeneration = 1;
        serviceType.GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);

        // The constructor already emitted an empty-context start. Close that
        // uncaptured setup stream, then let a fresh production emitter describe
        // the fully seeded fixture graph as its initial state.
        var setupEmitter = GetPrivatePropertyValue(context, "CoordinatorSnapshotEmitter")!;
        var emitterType = setupEmitter.GetType();
        emitterType.GetMethod("Complete", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(setupEmitter,
            [GetPrivateProperty<string>(context, "CoordinatorInstanceId"), sessionId, transferId, FileTransferDirection.Inbound]);
        await CoreSmokeTestsBase.WaitUntilAsync(() => (int)emitterType
            .GetField("drainScheduled", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(setupEmitter)! == 0,
            TimeSpan.FromSeconds(3));
        var coordinatorId = Guid.NewGuid().ToString("N");
        contextType.GetField("<CoordinatorInstanceId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, coordinatorId);
        contextType.GetField("<CoordinatorSnapshotEmitter>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, Activator.CreateInstance(emitterType, nonPublic: true));
        SetPrivateProperty(context, "CoordinatorPersistentState", new FileTransferCoordinatorPersistentState
        {
            CoordinatorInstanceId = coordinatorId,
        });
        var logStart = GetOperationalLogLength();
        StartCoordinatorSnapshotStreamForTest(context);
        var initialState = CreateCoordinatorStateForTest(context);
        Assert.Equal(FileTransferCoordinator.CalculateSnapshotStateFingerprint(initialState),
            initialState.PersistentState.SnapshotStateFingerprint);

        // Exercise the production Infra publication observer with an actual Coordinator
        // publication projection; this fake client never connects or starts live NKN.
        using var publicationObserver = new NknSignalingTransport(new FakeNknClient("checkpoint.continuity.fixture"),
            NknTransportOptions.Load(), new NknIdentity("checkpoint-continuity", "checkpoint.continuity.fixture"));
        var initial = CreateCoordinatorStateForTest(context);
        publicationObserver.ObserveFileTransferRouteHint(new(sessionId, transferId, route.TelemetryToken,
            route.ProtocolVersion, "coordinator_route_publication_successor", FallbackLegState: "active",
            FallbackLegCanSendData: true, FallbackLegGeneration: leg.Generation,
            PublicationAuthority: FileTransferCoordinator.CreateRoutePublicationAuthority(initial.CurrentRoutePublicationToken, "accepted"),
            FallbackTransportEpoch: leg.TransportEpochId, FallbackBridgeRecoveryGeneration: leg.BridgeRecoveryGeneration));

        const string checkpointRequest = "checkpoint-continuity:service:1";
        object wake;
        lock (GetServiceGate(service))
        {
            var requested = ApplyCheckpoint(FileTransferCoordinatorEventKind.FallbackCheckpointRequested,
                FileTransferLegState.CheckpointPending, false, checkpointRequest);
            var requestedLease = Assert.IsType<FallbackCheckpointLease>(requested.State.CurrentFallbackCheckpointLease);
            var now = DateTimeOffset.UtcNow;
            Assert.False(Assert.IsType<bool>(serviceType.GetMethod("ShouldSendInboundV6FrontierRequestLocked",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [context, now, false, "chunk_batch_committed"])));
            wake = GetPrivatePropertyValue(context, "CurrentV6FrontierStallGraceWake")!;
            Assert.NotNull(wake);
            var heldState = CreateCoordinatorStateForTest(context);
            Assert.Equal(requestedLease.CheckpointRequestId, heldState.CurrentLeg!.CheckpointRequestId);
            Assert.Equal(requestedLease, heldState.CurrentFallbackCheckpointLease);
            var accepted = ApplyCheckpoint(FileTransferCoordinatorEventKind.FallbackCheckpointAccepted,
                FileTransferLegState.RecoveryActive, true, requestedLease.CheckpointRequestId);
            Assert.Equal(requested.State.PersistentState.SnapshotStateGeneration + 1,
                accepted.State.PersistentState.SnapshotStateGeneration);
            Assert.NotNull(accepted.State.PersistentState.CheckpointRepairContinuityOrigin);
            Assert.Null(accepted.State.CurrentFallbackCheckpointLease);
        }

        var frozenSource = GetPrivateProperty<CheckpointContinuitySourceStamp>(wake, "CheckpointContinuitySource");
        var beforeWake = CreateCoordinatorStateForTest(context);
        Assert.NotEqual(frozenSource.SourceScopeFingerprint,
            FileTransferCoordinator.CreateCheckpointContinuitySourceStamp(beforeWake)!.SourceScopeFingerprint);
        serviceType.GetMethod("SchedulePendingInboundV6FrontierStallGraceWake",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [context]);
        await CoreSmokeTestsBase.WaitUntilAsync(() => ReadOperationalLogTail(logStart)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(line =>
                line.Contains("event=filetransfer_v6_frontier_stall_grace_recheck_decided;", StringComparison.Ordinal) &&
                line.Contains("decision_status=stale;", StringComparison.Ordinal)), TimeSpan.FromSeconds(3));
        var afterWake = CreateCoordinatorStateForTest(context);
        Assert.Equal(beforeWake.PersistentState, afterWake.PersistentState);
        Assert.Equal(beforeWake.CurrentRuntimeOwner, afterWake.CurrentRuntimeOwner);
        Assert.Equal(beforeWake.CurrentRoutePublicationToken, afterWake.CurrentRoutePublicationToken);
        Assert.Equal(beforeWake.CurrentRepairReplayDecision, afterWake.CurrentRepairReplayDecision);
        Assert.Equal(beforeWake.PersistentState.SnapshotStateGeneration, afterWake.PersistentState.SnapshotStateGeneration);
        Assert.Equal(beforeWake.PersistentState.SnapshotStateFingerprint, afterWake.PersistentState.SnapshotStateFingerprint);
        Assert.Equal(0, delivery.SendCount);
        Assert.True(await Assert.IsAssignableFrom<Task<bool>>(serviceType.GetMethod("SendInboundV6FrontierRequestAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [context, "chunk_batch_committed", false])));
        var contract = GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract")!;
        Assert.NotNull(contract);
        var authority = GetPrivateProperty<FallbackFrontierRepairAuthority>(contract, "Authority");
        Assert.Equal(FallbackFrontierRepairSourceKind.ReceiverRequest, authority.SourceKind);
        Assert.Equal("(none)", authority.CheckpointRequestId);
        Assert.NotNull(GetPrivatePropertyValue(contract, "CheckpointContinuityStamp"));
        Assert.Equal(authority.ProtocolRepairRequestId,
            Assert.IsType<FileTransferFrontierRequestFrameV6>(delivery.LastRequest!.Frame).RepairRequestId);

        object progress;
        lock (GetServiceGate(service))
        {
            SetPrivateProperty(context, "NextChunkIndex", 101);
            progress = serviceType.GetMethod("TrySettleInboundFallbackFrontierProgressLocked",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                [context, contract, authority.ProtocolRepairRequestId, authority.TransportEpoch,
                    FileTransferTransportKind.RegularNkn, 100, 101, "chunk_batch_committed"])!;
            Assert.NotNull(progress);
        }
        Assert.True(await Assert.IsAssignableFrom<Task<bool>>(serviceType.GetMethod("SendInboundV6ReceiverStateAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
            [context, "chunk_batch_committed", true, false, progress])));
        service.Dispose();
        Assert.True(SpinWait.SpinUntil(() => ReadOperationalLogTail(logStart).Contains(
            "event=filetransfer_coordinator_snapshot_stream_completed;", StringComparison.Ordinal), TimeSpan.FromSeconds(3)));
        var lines = ReadOperationalLogTail(logStart).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines, line => line.Contains("event=filetransfer_coordinator_snapshot_stream_started;", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.True(
            line.Contains("; log_writer_id=", StringComparison.Ordinal) &&
            line.Contains("; log_writer_sequence=", StringComparison.Ordinal) &&
            line.Contains("; log_writer_attempt_utc_ticks=", StringComparison.Ordinal),
            $"Fixture requires persisted operational log capture, not the unstamped recent ring: {LocalOperationalLog.LogFilePath}"));
        var holdLine = Assert.Single(lines, line => line.Contains("event=filetransfer_v6_frontier_request_deferred;", StringComparison.Ordinal));
        var staleLine = Assert.Single(lines, line => line.Contains("event=filetransfer_v6_frontier_stall_grace_recheck_decided;", StringComparison.Ordinal));
        Assert.Contains("authority_effect=none;", staleLine, StringComparison.Ordinal);
        Assert.Contains("decision_status=stale;", staleLine, StringComparison.Ordinal);
        foreach (var field in new[] { "continuity_profile", "continuity_coordinator_id", "continuity_source_fingerprint",
            "checkpoint_request_id", "checkpoint_generation", "data_session_identity", "data_session_generation",
            "wake_generation", "stall_started_utc", "wake_deadline_utc" })
        {
            Assert.Equal(GetDelimitedLogField(holdLine, field), GetDelimitedLogField(staleLine, field));
        }
        foreach (var name in new[] { "filetransfer_v6_frontier_request_deferred", "filetransfer_fallback_frontier_repair_authority_granted",
            "filetransfer_control_plane_delivery_observed", "filetransfer_fallback_frontier_repair_dispatched",
            "filetransfer_v6_frontier_request_sent", "filetransfer_fallback_frontier_repair_contract_settled",
            "filetransfer_fallback_frontier_repair_progress_observed", "filetransfer_v6_fallback_frontier_repair_progress_state_forced",
            "filetransfer_v6_receiver_state_sent" })
        {
            Assert.Contains(lines, line => line.Contains("event=" + name + ";", StringComparison.Ordinal) &&
                line.Contains("continuity_source_fingerprint=fte1-", StringComparison.Ordinal));
        }
        return Array.AsReadOnly(lines);

        FileTransferCoordinatorDecision ApplyCheckpoint(FileTransferCoordinatorEventKind kind,
            FileTransferLegState legState, bool canSend, string requestId)
        {
            var state = CreateCoordinatorStateForTest(context);
            var currentLeg = state.CurrentLeg!;
            var decision = FileTransferCoordinator.Apply(new(kind, route, FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.Unknown, "regular_nkn_state_refresh", legState, canSend, 100, 255,
                currentLeg.TransportEpochId, currentLeg.BridgeRecoveryGeneration, requestId, "state_refresh"), state);
            Assert.Null(decision.MutationRejectedReason);
            // The service adopts the whole runtime graph, including leg history;
            // replacing only CurrentTransferLeg leaves a stale same-ID history leg.
            serviceType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(method => method.Name == "ApplyCoordinatorDecisionToContextLocked" &&
                    method.GetParameters().Length == 4 &&
                    method.GetParameters()[0].ParameterType == contextType)
                .Invoke(null, [context, decision, true, true]);
            var adopted = CreateCoordinatorStateForTest(context);
            Assert.Equal(decision.State.CurrentLeg!.CheckpointRequestId, adopted.CurrentLeg!.CheckpointRequestId);
            Assert.Equal(decision.State.CurrentFallbackCheckpointLease, adopted.CurrentFallbackCheckpointLease);
            Assert.Contains(adopted.LegHistory, historyLeg => ReferenceEquals(historyLeg, adopted.CurrentLeg));
            return decision;
        }
    }
}
