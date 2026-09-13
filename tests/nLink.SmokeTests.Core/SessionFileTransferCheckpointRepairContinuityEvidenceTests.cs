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
        => await CreateCheckpointRepairContinuityServiceEvidenceFixtureAsync(delayedDisposition: false);

    [Fact]
    [Trait("FileTransferRuntimeSuite", "CheckpointRepairContinuity")]
    public async Task CheckpointRepairContinuity_DelayedFallbackDispositionPreservesExactSparseRecovery()
        => await CreateCheckpointRepairContinuityDelayedDispositionEvidenceFixtureAsync();

    internal static async Task<IReadOnlyList<string>> CreateCheckpointRepairContinuityDelayedDispositionEvidenceFixtureAsync()
        => await CreateCheckpointRepairContinuityServiceEvidenceFixtureAsync(delayedDisposition: true);

    private static async Task<IReadOnlyList<string>> CreateCheckpointRepairContinuityServiceEvidenceFixtureAsync(bool delayedDisposition)
    {
        // Keep synthetic identities below secret-token redaction thresholds so
        // persisted diagnostics retain the exact data-session projection.
        var transferId = "continuity_" + Guid.NewGuid().ToString("N")[..16];
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
        if (delayedDisposition)
        {
            // Establish actual Tuna authority before the captured stream; no
            // fallback/continuity decision is hidden in this setup boundary.
            ApplyRuntime(FileTransferCoordinatorTests.CreateCheckpointContinuityTunaServiceSource(sessionId, transferId),
                forwardPublication: false, adopt: false);
            var written = new System.Collections.BitArray(256);
            for (var index = 0; index < 100; index++) { written[index] = true; }
            written[255] = true;
            SetPrivateProperty(context, "ReceiverSparseChunksWritten", written);
            SetPrivateProperty(context, "V4CreditUntilChunkIndexExclusive", 256);
            SetPrivateProperty(context, "V6SparseAcceptWindowEndExclusive", 256);
        }

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
        FileTransferRouteTransitionAuthority? delayedAuthority = null;
        FileTransferRouteTransitionDispositionCommand? delayedCommand = null;
        if (delayedDisposition)
        {
            var fallback = FileTransferCoordinatorTests.CreateCheckpointContinuityFallbackBeforeDisposition(initialState,
                envelope => FileTransferCheckpointResponderCoexistenceEvidenceTests.EnqueueIntoContext(context, envelope));
            ApplyRuntime(fallback.RuntimeDecision, forwardPublication: false, adopt: false);
            SetPrivateProperty(context, "CoordinatorPersistentState", fallback.RuntimeDecision.State.PersistentState);
            delayedAuthority = fallback.Authority;
            delayedCommand = fallback.Disposition;
            leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
            Assert.Equal(route, CreateCoordinatorStateForTest(context).RouteSelection);
        }

        // Exercise the production Infra publication observer with an actual Coordinator
        // publication projection; this fake client never connects or starts live NKN.
        using var publicationObserver = new NknSignalingTransport(new FakeNknClient("checkpoint.continuity.fixture"),
            NknTransportOptions.Load(), new NknIdentity("checkpoint-continuity", "checkpoint.continuity.fixture"));
        var initial = CreateCoordinatorStateForTest(context);
        var emitter = GetPrivatePropertyValue(context, "CoordinatorSnapshotEmitter")!;
        var publicationEvidence = (FileTransferRoutePublicationEvidence?)emitterType
            .GetMethod("GetRoutePublicationEvidence", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(emitter, [initial.CurrentRoutePublicationToken]);
        if (delayedDisposition) { Assert.NotNull(publicationEvidence); }
        publicationObserver.ObserveFileTransferRouteHint(new(sessionId, transferId, route.TelemetryToken,
            route.ProtocolVersion, "coordinator_route_publication_successor", FallbackLegState: "active",
            FallbackLegCanSendData: true, FallbackLegGeneration: leg.Generation,
            PublicationAuthority: FileTransferCoordinator.CreateRoutePublicationAuthority(initial.CurrentRoutePublicationToken, "accepted"),
            FallbackTransportEpoch: leg.TransportEpochId, FallbackBridgeRecoveryGeneration: leg.BridgeRecoveryGeneration)
        {
            PublicationEvidence = publicationEvidence,
        });

        var wake = ApplyContinuityEvidenceCheckpointCycle(service, context, serviceType, delayedDisposition,
            delayedCommand, delayedAuthority, sessionId, transferId, ApplyCheckpoint);

        await AssertContinuityEvidenceStaleWakeAsync(context, wake, serviceType, service, logStart);
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

        if (delayedDisposition)
        {
            using var destination = new MemoryStream(new byte[256 * chunkSize], writable: true);
            var chunk = Enumerable.Repeat((byte)0xa5, chunkSize).ToArray();
            var batch = new FileTransferChunkBatchFrameV6
            {
                SessionId = sessionId, TransferId = transferId, TransportEpoch = authority.TransportEpoch,
                StartChunkIndex = 100, ChunkCount = 1, RepairRequestId = authority.ProtocolRepairRequestId,
                BatchId = "continuity-sparse-progress", DataSegments = [chunk],
            };
            await Assert.IsAssignableFrom<Task>(serviceType.GetMethod("HandleInboundV6SparseChunkBatchAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
                [context, batch, new List<(int ChunkIndex, byte[] ChunkBytes)> { (100, chunk) }, destination,
                    FileTransferTransportKind.RegularNkn, contract]));
            Assert.Equal(101, GetPrivateProperty<int>(context, "NextChunkIndex"));
            Assert.Equal(chunk, destination.ToArray().Skip(100 * chunkSize).Take(chunkSize));
        }
        else
        {
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
        }
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
        var holdLine = Assert.Single(lines, line => line.Contains("event=filetransfer_v6_frontier_request_deferred;", StringComparison.Ordinal) &&
            (!delayedDisposition || GetDelimitedLogField(line, "frontier_chunk_index") == "100"));
        Assert.Equal(sessionId, GetDelimitedLogField(holdLine, "session_id"));
        Assert.Equal(transferId, GetDelimitedLogField(holdLine, "transfer_id"));
        Assert.Equal(transferId + ":1", GetDelimitedLogField(holdLine, "data_session_identity"));
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
        if (delayedDisposition)
        {
            var applied = Assert.Single(lines, line => line.Contains("event=filetransfer_route_transition_disposition_applied;", StringComparison.Ordinal) &&
                line.Contains("authority_schema=2;", StringComparison.Ordinal));
            Assert.Equal("fallback_peer_proof", GetDelimitedLogField(applied, "proof_boundary_kind"));
            Assert.Equal("(none)", GetDelimitedLogField(applied, "data_session_identity"));
            Assert.Equal("(none)", GetDelimitedLogField(applied, "checkpoint_request_id"));
            Assert.True(Array.IndexOf(lines, holdLine) < Array.IndexOf(lines, applied));
            var sparseIndex = Array.FindIndex(lines, line => line.Contains("event=filetransfer_v6_sparse_write_committed;", StringComparison.Ordinal));
            var forcedIndex = Array.FindIndex(lines, line => line.Contains("event=filetransfer_v6_fallback_frontier_repair_progress_state_forced;", StringComparison.Ordinal));
            var publishedIndex = Array.FindIndex(lines, line => line.Contains("event=filetransfer_v6_receiver_state_sent;", StringComparison.Ordinal) &&
                line.Contains("continuity_source_fingerprint=fte1-", StringComparison.Ordinal));
            Assert.True(sparseIndex >= 0 && sparseIndex < forcedIndex && forcedIndex < publishedIndex);
        }
        return Array.AsReadOnly(lines);

        void ApplyRuntime(FileTransferCoordinatorDecision decision, bool forwardPublication, bool adopt)
            => serviceType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(method => method.Name == "ApplyCoordinatorDecisionToContextLocked" &&
                    method.GetParameters().Length == 4 && method.GetParameters()[0].ParameterType == contextType)
                .Invoke(null, [context, decision, forwardPublication, adopt]);

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

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static object ApplyContinuityEvidenceCheckpointCycle(SessionFileTransferService service, object context,
        Type serviceType, bool delayedDisposition, FileTransferRouteTransitionDispositionCommand? delayedCommand,
        FileTransferRouteTransitionAuthority? delayedAuthority, string sessionId, string transferId,
        Func<FileTransferCoordinatorEventKind, FileTransferLegState, bool, string, FileTransferCoordinatorDecision> ApplyCheckpoint)
    {
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
            if (delayedDisposition)
            {
                ApplyContinuityEvidenceDelayedDisposition(context, delayedCommand, delayedAuthority, serviceType, sessionId, transferId);
            }
        }
        return wake;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ApplyContinuityEvidenceDelayedDisposition(object context,
        FileTransferRouteTransitionDispositionCommand? delayedCommand,
        FileTransferRouteTransitionAuthority? delayedAuthority, Type serviceType, string sessionId, string transferId)
    {
        var beforeDisposition = CreateCoordinatorStateForTest(context);
        var disposition = FileTransferCoordinator.ApplyFileTransferRouteTransitionDispositionReceipt(beforeDisposition,
            new(delayedCommand!.Value, FileTransferRouteTransitionDispositionApplyStatus.Applied,
                "filetransfer_route_transition_committed"));
        Assert.True(disposition.Status == FileTransferRouteTransitionDecisionStatus.Committed, disposition.Reason);
        FileTransferCheckpointResponderCoexistenceEvidenceTests.EnqueueIntoContext(context, disposition.SnapshotEnvelope!);
        SetPrivateProperty(context, "CoordinatorPersistentState", disposition.State.PersistentState);
        Assert.Same(beforeDisposition.PersistentState.CheckpointRepairContinuityOrigin,
            disposition.State.PersistentState.CheckpointRepairContinuityOrigin);
        Assert.Equal(beforeDisposition.CurrentRuntimeOwner, disposition.State.CurrentRuntimeOwner);
        Assert.Equal(beforeDisposition.CurrentRoutePublicationToken, disposition.State.CurrentRoutePublicationToken);
        // This is the production event-specific logger. Its unprefixed
        // activation data-session/checkpoint identities are legitimately absent.
        serviceType.GetMethod("LogFileTransferRouteTransitionDecision", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, ["filetransfer_route_transition_disposition_applied", sessionId, transferId,
                FileTransferDirection.Inbound, delayedAuthority, "Applied", "filetransfer_route_transition_committed",
                0, 0, null, null, disposition.State.CurrentRoutePublicationToken, disposition.State.CurrentRouteCommit,
                null, 0]);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task AssertContinuityEvidenceStaleWakeAsync(object context, object wake,
        Type serviceType, SessionFileTransferService service, int logStart)
    {
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
    }
}
