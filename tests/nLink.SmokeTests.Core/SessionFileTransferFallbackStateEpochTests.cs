using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(1181, 89)]
    [InlineData(5, 1)]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task FallbackToTunaStateEpoch_OnlyAuthorizedCommitResetsFloorAndRejectsStaleWorkers(
        int predecessorEpoch, int successorEpoch)
    {
        var transferId = $"fallback_to_tuna_epoch_{predecessorEpoch}";
        var sessionId = $"session_{transferId}";
        using var service = new SessionFileTransferService();
        using var transport = new EpochTransitionLoopbackTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        service.AttachTransport(transport);
        var dataSession = await transport.OpenFileTransferDataSessionAsync(
            sessionId, transferId, CancellationToken.None);
        var context = CreatePostTunaFallbackV6OutboundContext(transferId,
            remoteNextExpectedChunkIndex: 6, chunksAcceptedForTransport: 16,
            remoteGrantedUntilExclusive: 16, chunkCount: 128, dataSession: dataSession);
        SetPrivateProperty(context, "V4LastStateEpoch", predecessorEpoch);
        SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 7);
        var serviceType = typeof(SessionFileTransferService);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        serviceType.GetField("outboundTransfer", flags)!.SetValue(service, context);
        var predecessorOwner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var staged = StageSparsePumpInstall(service, context, null, "epoch_test_predecessor");
        Assert.True(staged.Accepted, staged.Reason);
        var installed = CommitSparsePumpInstall(service, context, staged.Install!, dataSession);
        Assert.True(installed.Accepted, installed.Reason);
        var predecessorInstall = installed.Install!;

        // Reuse the real coordinator-issued paired release, retry install and
        // admission history, preserving the service's data-session identity.
        var emitter = GetPrivatePropertyValue(context, "CoordinatorSnapshotEmitter")!;
        var enqueue = emitter.GetType().GetMethod("Enqueue", flags)!;
        var setup = FileTransferCoordinatorTests.CreateFallbackV6ToTunaRouteTransitionSetup(
            CreateCoordinatorStateForTest(context), envelope => enqueue.Invoke(emitter, [envelope]));
        transport.InstalledRetry = Assert.IsType<RuntimeUnlockRetryDispatchInstallReceipt>(
            FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(setup.State).InstalledAttempt!.Value.InstallReceipt);
        SetPrivateProperty(context, "RuntimeUnlockAdmissionToken", setup.State.CurrentRuntimeUnlockAdmission);
        SetPrivateProperty(context, "LastRuntimeUnlockAdmissionGeneration", setup.State.LastRuntimeUnlockAdmissionGeneration);
        SetPrivateProperty(context, "RuntimeUnlockPendingPreCommitProbe", setup.State.CurrentRuntimeUnlockPendingPreCommitProbe);
        SetPrivateProperty(context, "LastRuntimeUnlockPendingPreCommitProbeGeneration", setup.State.LastRuntimeUnlockPendingPreCommitProbeGeneration);
        var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(setup.State,
            setup.Request with
            {
                DurableCheckpoint = setup.Request.DurableCheckpoint with
                {
                    StateEpoch = predecessorEpoch,
                    CheckpointGeneration = 1,
                },
            });
        Assert.True(prepared.Status == FileTransferRouteTransitionDecisionStatus.Prepared, prepared.Reason);
        AdoptFileTransferRouteTransitionDecisionForGapTest(context, prepared);
        var authority = Assert.IsType<FileTransferRouteTransitionAuthority>(prepared.Authority);
        var pause = ((IFileTransferRouteTransitionBridge)transport).BindFileTransferRouteTransitionPause(
            Assert.IsType<FileTransferRouteTransitionPauseBindCommand>(prepared.PauseBindCommand));
        var emptyIds = Array.Empty<long>();
        var draining = FileTransferCoordinator.BeginFileTransferRouteTransitionPhysicalDrain(
            prepared.State, authority, pause, 0, emptyIds,
            FileTransferCoordinator.CreateFileTransferRouteTransitionPhysicalDrainFingerprint(emptyIds));
        AdoptFileTransferRouteTransitionDecisionForGapTest(context, draining);
        var execution = Activator.CreateInstance(
            serviceType.GetNestedType("FileTransferRouteTransitionExecutionShadow", BindingFlags.NonPublic)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null,
            args: [authority, dataSession, 1L, draining.DrainCommand, new Dictionary<long, Task>(),
                null, null, null, null, false, false, "epoch_test_reactivation"], culture: null)!;
        SetPrivateProperty(context, "FileTransferRouteTransitionExecution", execution);
        var commit = serviceType.GetMethod(
            "TryCommitOutboundFileTransferRouteTransitionAndCaptureFallbackCheckpointLocked", flags)!;
        object?[] arguments = [context, execution, null, null, null];
        Assert.False(Assert.IsType<bool>(commit.Invoke(service, arguments)));
        Assert.Equal("filetransfer_route_transition_final_proof_missing", arguments[3]);
        Assert.Equal(predecessorEpoch, GetPrivateProperty<int>(context, "V4LastStateEpoch"));
        Assert.Equal(predecessorOwner, GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"));

        var proof = setup.FinalProof;
        transport.RuntimeUnlockRouteCommitSnapshot = new RuntimeUnlockRouteCommitSnapshot(
            proof.SessionId, proof.TransferId, proof.TransactionGeneration, proof.OfferGeneration,
            proof.PeerVisibleProof, proof.PeerReceived, proof.AnswerReceived, proof.TargetRoute,
            proof.ProtocolVersion, proof.HandoffKind, proof.TargetTransport, proof.TransactionState.ToString(),
            proof.Reason, proof.PathProbeId, proof.PathProbeState.ToString(), proof.PathProbeTransport,
            proof.PathProbeAckedUtcMs, proof.PathProbeFailureReason, proof.TunaPathLeaseRequired,
            proof.TunaPathLeaseGeneration, proof.TunaPathLeaseState.ToString(), proof.TunaPathLeaseListenerRunId,
            proof.TunaPathLeaseCurrent, proof.TunaPathLeaseFailureReason, LaunchGeneration: 31);
        arguments = [context, execution, null, null, null];
        Assert.True(Assert.IsType<bool>(commit.Invoke(service, arguments)), arguments[3] as string);
        Assert.Equal(-1, GetPrivateProperty<int>(context, "V4LastStateEpoch"));
        var successorOwner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var successorPublication = GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken");
        Assert.NotEqual(predecessorOwner, successorOwner);
        Assert.Equal(FileTransferRoute.FileTunaV4,
            GetPrivateProperty<FileTransferRouteSelection>(context, "RouteSelection").Route);
        Assert.Equal(4, GetPrivateProperty<int>(context, "NegotiatedDataProtocolVersion"));
        staged = StageSparsePumpInstall(service, context, predecessorInstall, "epoch_test_successor");
        Assert.True(staged.Accepted, staged.Reason);
        installed = CommitSparsePumpInstall(service, context, staged.Install!, dataSession);
        Assert.True(installed.Accepted, installed.Reason);
        var successorInstall = installed.Install!;
        var apply = serviceType.GetMethod("ApplyOutboundV4StateAsync", flags)!;
        async Task<string> ApplyAsync(FileTransferStateFrameV4 frame, object? install, RuntimeOwnerToken owner)
        {
            var task = Assert.IsAssignableFrom<Task>(apply.Invoke(service,
                [context, frame, install, owner, dataSession, 1L]));
            await task;
            return GetPrivatePropertyValue(task.GetType().GetProperty("Result")!.GetValue(task)!, "Status")!.ToString()!;
        }

        var current = new FileTransferStateFrameV4
        {
            SessionId = sessionId, TransferId = transferId, Epoch = successorEpoch,
            ContiguousCommittedChunkIndex = 16, DurableReceivedHighestChunkIndex = 15,
            CreditUntilChunkIndexExclusive = 128,
        };
        Assert.Equal("Applied", await ApplyAsync(current, successorInstall, successorOwner));
        Assert.Equal(successorEpoch, GetPrivateProperty<int>(context, "V4LastStateEpoch"));
        Assert.Equal(16, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
        Assert.Equal(128, GetPrivateProperty<int>(context, "RemoteGrantedUntilExclusive"));
        var appliedCount = GetPrivateProperty<int>(context, "PullV4StateAppliedCountTotal");
        Assert.Equal("Idempotent", await ApplyAsync(current, successorInstall, successorOwner));
        Assert.Equal("Idempotent", await ApplyAsync(current with { Epoch = successorEpoch - 1 }, successorInstall, successorOwner));
        Assert.Equal("Rejected", await ApplyAsync(current with { Epoch = predecessorEpoch + 1, TransferPaused = true },
            predecessorInstall, predecessorOwner));
        Assert.Equal("Rejected", await ApplyAsync(current with { Epoch = successorEpoch + 1, TransferPaused = true },
            null, successorOwner));
        arguments = [context, execution, null, null, null];
        Assert.False(Assert.IsType<bool>(commit.Invoke(service, arguments)));
        Assert.Equal(successorEpoch, GetPrivateProperty<int>(context, "V4LastStateEpoch"));
        Assert.Equal(appliedCount, GetPrivateProperty<int>(context, "PullV4StateAppliedCountTotal"));
        Assert.Equal(16, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
        Assert.Equal(128, GetPrivateProperty<int>(context, "RemoteGrantedUntilExclusive"));
        Assert.False(GetPrivateProperty<bool>(context, "PeerPaused"));
        Assert.Equal(successorOwner, GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"));
        Assert.Equal(successorPublication, GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken"));
        Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
        Assert.Empty(GetQueuedV4RepairSends(context));
    }

    // Only acknowledges retirement of the exact Coordinator-issued retry install.
    // Route proof and route commit remain under the production Coordinator.
    private sealed class EpochTransitionLoopbackTransport(string sessionId) :
        LoopbackFileTransferTransport(sessionId), IRuntimeUnlockRetryDispatchAuthorityBridge
    {
        public RuntimeUnlockRetryDispatchInstallReceipt InstalledRetry { get; set; }
        private RuntimeUnlockRetryDispatchScopeInvalidationCommand? staged;
        public event EventHandler<RuntimeUnlockRetryDispatchInitialEvaluationRequestedEventArgs>? RuntimeUnlockRetryDispatchInitialEvaluationRequested { add { } remove { } }
        public event EventHandler<RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs>? RuntimeUnlockRetryDispatchAuthorityRequested { add { } remove { } }
        public event EventHandler<RuntimeUnlockRetryDispatchSettlementReportedEventArgs>? RuntimeUnlockRetryDispatchSettlementReported { add { } remove { } }
        public event EventHandler<RuntimeUnlockRetryDispatchDeadlineEvaluationEventArgs>? RuntimeUnlockRetryDispatchDeadlineEvaluationRequested { add { } remove { } }
        public event EventHandler<RuntimeUnlockRetryDispatchExecutionObservedEventArgs>? RuntimeUnlockRetryDispatchExecutionObserved { add { } remove { } }
        public event EventHandler<RuntimeUnlockRetryDispatchSuccessorEvaluationEventArgs>? RuntimeUnlockRetryDispatchSuccessorEvaluationRequested { add { } remove { } }
        public bool ObserveRuntimeUnlockRetryDispatchNonGrantDecision(RuntimeUnlockRetryDispatchDecision decision) => throw new NotSupportedException();
        public RuntimeUnlockRetryDispatchDecisionStageResult StageRuntimeUnlockRetryDispatchDecision(RuntimeUnlockRetryDispatchDecision decision) => throw new NotSupportedException();
        public RuntimeUnlockRetryDispatchInstallResult CommitRuntimeUnlockRetryDispatchInstall(RuntimeUnlockRetryDispatchInstallReceipt receipt) => throw new NotSupportedException();
        public bool ConfirmRuntimeUnlockRetryDispatchInstall(RuntimeUnlockRetryDispatchInstallReceipt receipt) => throw new NotSupportedException();
        public bool ObserveRuntimeUnlockRetryDispatchSettlementDecision(RuntimeUnlockRetryDispatchSettlementDecision decision) => throw new NotSupportedException();

        public RuntimeUnlockRetryDispatchScopeInvalidationStageResult StageRuntimeUnlockRetryDispatchScopeInvalidation(
            RuntimeUnlockRetryDispatchScopeInvalidationCommand command)
        {
            Assert.False(command.IsDefault);
            Assert.Equal(RuntimeUnlockRetryDispatchScopeInvalidationKind.FullScope, command.Kind);
            Assert.Equal(InstalledRetry, command.InstalledAttempt!.Value.InstallReceipt);
            Assert.Null(command.PendingAttempt);
            Assert.Null(command.RetainedPredecessor);
            Assert.Null(staged);
            staged = command;
            return new(command, RuntimeUnlockRetryDispatchScopeInvalidationStageStatus.Staged, "test_exact_retry_retirement");
        }

        public RuntimeUnlockRetryDispatchScopeInvalidationReceipt ApplyRuntimeUnlockRetryDispatchScopeInvalidation(
            RuntimeUnlockRetryDispatchScopeInvalidationCommand command)
        {
            Assert.Same(staged, command);
            return new(command, RuntimeUnlockRetryDispatchScopeInvalidationStatus.Applied, "test_exact_retry_retirement");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task FallbackStateEpoch_CommittedSuccessorStartsFreshAndRejectsOldOrDuplicateState(
        int predecessorEpoch)
    {
        var transferId = $"fallback_state_epoch_{predecessorEpoch}";
        var sessionId = $"session_{transferId}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        service.AttachTransport(transport);
        var context = CreateFileTunaV4OutboundContext(transferId,
            remoteNextExpectedChunkIndex: 0, chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0, chunkCount: 1024);
        var dataSession = await transport.OpenFileTransferDataSessionAsync(
            sessionId, transferId, CancellationToken.None);
        SetPrivateProperty(context, "DataSession", dataSession);
        var serviceType = typeof(SessionFileTransferService);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        serviceType.GetField("outboundTransfer", flags)!.SetValue(service, context);
        var predecessorOwner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var staged = StageSparsePumpInstall(service, context, null, "epoch_test_predecessor");
        Assert.True(staged.Accepted, staged.Reason);
        var installed = CommitSparsePumpInstall(service, context, staged.Install!, dataSession);
        Assert.True(installed.Accepted, installed.Reason);
        var predecessorInstall = installed.Install!;
        var apply = serviceType.GetMethod("ApplyOutboundV4StateAsync", flags)!;

        async Task<string> ApplyAsync(FileTransferStateFrameV4 state, object? install, RuntimeOwnerToken owner)
        {
            var task = Assert.IsAssignableFrom<Task>(apply.Invoke(service,
                [context, state, install, owner, dataSession, 1L]));
            await task;
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            return GetPrivatePropertyValue(result, "Status")!.ToString()!;
        }

        Assert.Equal("Applied", await ApplyAsync(new FileTransferStateFrameV4
        {
            SessionId = sessionId, TransferId = transferId, Epoch = predecessorEpoch,
            ContiguousCommittedChunkIndex = 0, DurableReceivedHighestChunkIndex = -1,
            CreditUntilChunkIndexExclusive = 512,
        }, predecessorInstall, predecessorOwner));
        Assert.Equal(predecessorEpoch, GetPrivateProperty<int>(context, "V4LastStateEpoch"));

        // Commit the real coordinator transition and replay its exact retained
        // paused bootstrap state through the production retained-frame path.
        var bootstrap = new FileTransferFallbackBootstrapStateFrameV6
        {
            SessionId = sessionId, TransferId = transferId,
            TransportEpoch = GetCurrentContextLeg(context).TransportEpochId + 1,
            Epoch = 3, ContiguousCommittedChunkIndex = 0,
            DurableReceivedHighestChunkIndex = -1, CreditUntilChunkIndexExclusive = 0,
            TransferPaused = true, TransferPauseReason = FileTransferBootstrapCorrelation.RecoveryMode,
            SenderRequestId = $"fbc1:{new string('a', 64)}:1:{new string('b', 64)}",
            ReceiverChallengeId = $"fbc1:{new string('b', 64)}:1:{new string('a', 64)}",
        };
        var fixture = CreateFallbackCheckpointDispatchFixture(context, transport, bootstrap);
        Assert.Equal(bootstrap.TransportEpoch, fixture.TargetTransportEpoch);
        var execution = fixture.CreateExecution();
        SetPrivateProperty(context, "FileTransferRouteTransitionExecution", execution);
        var commit = serviceType.GetMethod(
            "TryCommitOutboundFileTransferRouteTransitionAndCaptureFallbackCheckpointLocked", flags)!;
        object?[] arguments = [context, execution, null, null, null];
        Assert.True(Assert.IsType<bool>(commit.Invoke(service, arguments)), arguments[3] as string);
        var successorOwner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        Assert.NotEqual(predecessorOwner, successorOwner);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6,
            GetPrivateProperty<FileTransferRouteSelection>(context, "RouteSelection").Route);
        Assert.Equal(6, GetPrivateProperty<int>(context, "NegotiatedDataProtocolVersion"));

        staged = StageSparsePumpInstall(service, context, predecessorInstall, "epoch_test_successor");
        Assert.True(staged.Accepted, staged.Reason);
        installed = CommitSparsePumpInstall(service, context, staged.Install!, dataSession);
        Assert.True(installed.Accepted, installed.Reason);
        var successorInstall = installed.Install!;
        await Assert.IsAssignableFrom<Task>(serviceType.GetMethod(
            "TryApplyOutboundCommittedFileTransferRouteTransitionRetainedFrameAsync", flags)!
            .Invoke(service, [context, successorInstall, dataSession]));
        await WaitUntilAsync(() => GetPrivateProperty<int>(context, "V6LastReceiverStateEpoch") == 3,
            timeoutMs: 5000);
        Assert.Contains(transport.SentDataFrames, frame => frame is FileTransferFrontierRequestFrameV6 request &&
            request.RepairRequestId == fixture.ExpectedCheckpointRequestId);
        Assert.True(GetPrivateProperty<bool>(context, "PeerPaused"));
        Assert.Equal(0, GetPrivateProperty<int>(context, "RemoteGrantedUntilExclusive"));
        Assert.Equal(3, GetPrivateProperty<int>(context, "V6LastReceiverStateEpoch"));

        var current = new FileTransferReceiverStateFrameV6
        {
            SessionId = sessionId, TransferId = transferId, Epoch = 2,
            TransportEpoch = fixture.TargetTransportEpoch,
            ContiguousCommittedChunkIndex = 0, DurableReceivedHighestChunkIndex = -1,
            CreditUntilChunkIndexExclusive = 512, TransferPaused = false,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 512 }],
            Priority = "frontier",
        };
        Assert.Equal("Applied", await ApplyAsync(current, successorInstall, successorOwner));
        Assert.False(GetPrivateProperty<bool>(context, "PeerPaused"));
        Assert.Equal(512, GetPrivateProperty<int>(context, "RemoteGrantedUntilExclusive"));
        Assert.Equal(2, GetPrivateProperty<int>(context, "V4LastStateEpoch"));
        Assert.True(GetCurrentContextLeg(context).CanSendData);
        Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));

        var currentOwner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var currentPublication = GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken");
        var applicationCount = GetPrivateProperty<int>(context, "PullV4StateAppliedCountTotal");
        Assert.Equal("Idempotent", await ApplyAsync(current, successorInstall, successorOwner));
        Assert.Equal(applicationCount, GetPrivateProperty<int>(context, "PullV4StateAppliedCountTotal"));
        Assert.Equal("Rejected", await ApplyAsync(current with { Epoch = 4, TransferPaused = true },
            predecessorInstall, predecessorOwner));
        Assert.Equal("Rejected", await ApplyAsync(current with { Epoch = 4, TransferPaused = true },
            null, successorOwner));

        // The original commit cannot be replayed after its source owner has
        // been replaced, so it cannot clear the successor's epoch floor again.
        arguments = [context, execution, null, null, null];
        Assert.False(Assert.IsType<bool>(commit.Invoke(service, arguments)));
        Assert.Equal(2, GetPrivateProperty<int>(context, "V4LastStateEpoch"));
        Assert.False(GetPrivateProperty<bool>(context, "PeerPaused"));
        Assert.Equal(512, GetPrivateProperty<int>(context, "RemoteGrantedUntilExclusive"));
        Assert.Equal(currentOwner, GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"));
        Assert.Equal(currentPublication, GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken"));
        Assert.Empty(GetQueuedV4RepairSends(context));
        Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
    }
}
