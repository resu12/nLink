using System.Reflection;
using System.Collections.Immutable;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootstrapCorrelation_ServiceAvailabilityResumesOnlyExactHeldPhase(bool replaceDataSession)
    {
        const string transferId = "bootstrap_service_held_resume";
        using var service = new SessionFileTransferService();
        var dataSession = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        using var bridge = new BootstrapPhaseTestBridge(dataSession);
        var issuer = FileTransferBootstrapCorrelation.CreateIssuer("held-peer", dataSession.SessionId, transferId, FileTransferDirection.Outbound);
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, dataSession,
            beforeActivation: context => PinBootstrapServiceAgreement(context, issuer));
        var processing = ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge,
            LocalStopEvaluationForTest(context, dataSession));
        var first = Assert.Single(bridge.Executions);
        Assert.Equal(FileTransferBootstrapPhase.Announce, first.Command.Phase);
        var held = new FileTransferBootstrapPhaseReceipt(first.Command, FileTransferBootstrapPhaseReceiptStatus.Held,
            true, [], "modeled_held_before_any_submission");
        first.Delivery.TrySetResult(held);
        first.Settlement.TrySetResult(held);
        await processing;
        await WaitUntilAsync(() => CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapExchange!.First.Held, timeoutMs: 3000);
        Assert.True(CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapExchange!.First.Held);
        Assert.Empty(dataSession.SentFrames);
        if (replaceDataSession)
        {
            SetPrivateProperty(context, "DataSessionGeneration", GetPrivateProperty<long>(context, "DataSessionGeneration") + 1);
        }
        var beforeWake = CreateCoordinatorStateForTest(context);
        var wake = Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod(
            "HandleDataSessionAvailabilityChangedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
            [dataSession, new FileTransferDataSessionAvailabilityChangedEventArgs(true, "test_available", false)]));
        if (replaceDataSession)
        {
            await wake;
            Assert.Single(bridge.Executions);
            Assert.Equal(beforeWake.PersistentState.FileTransferRouteTransition,
                CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition);
        }
        else
        {
            await WaitUntilAsync(() => bridge.Executions.Count == 2, timeoutMs: 3000);
            var resumed = bridge.Executions[1];
            Assert.Equal(first.Command, resumed.Command);
            var failed = new FileTransferBootstrapPhaseReceipt(resumed.Command, FileTransferBootstrapPhaseReceiptStatus.Failed,
                true, [], "modeled_close_without_submission");
            resumed.Delivery.TrySetResult(failed);
            resumed.Settlement.TrySetResult(failed);
            await wake;
            Assert.True(CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapExchange!.Invalidated);
        }
        var final = CreateCoordinatorStateForTest(context);
        Assert.Equal(beforeWake.CurrentRuntimeOwner, final.CurrentRuntimeOwner);
        Assert.Equal(beforeWake.CurrentRoutePublicationToken, final.CurrentRoutePublicationToken);
        Assert.False(final.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Completed);
        Assert.Empty(dataSession.SentFrames);
    }

    [Fact]
    public async Task BootstrapCorrelation_ServiceSuccessorCompletesWithoutLegacyRecoveryMarkers()
    {
        const string transferId = "bootstrap_service_successor";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        var dataSession = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        using var bridge = new BootstrapPhaseTestBridge(dataSession);
        service.AttachTransport(bridge);
        var context = CreateRegularNknV4OutboundContext(transferId, remoteNextExpectedChunkIndex: 4, chunksAcceptedForTransport: 4,
            remoteGrantedUntilExclusive: 4, lastGrantReceivedUtc: DateTimeOffset.UtcNow, seedRuntimeOwner: false, chunkCount: 128);
        SetPrivateProperty(context, "DataSession", dataSession);
        SetPrivateProperty(context, "DataSessionGeneration", 17L);
        SetPrivateProperty(context, "V4LastStateEpoch", 3);
        SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 6);
        var history = FileTransferCoordinatorTests.CreateCorrelatedOutboundServiceSuccessorHistory(CreateCoordinatorStateForTest(context));
        SetPrivateProperty(context, "CoordinatorPersistentState", history.State.PersistentState);
        typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "ApplyCoordinatorDecisionToContextLocked" && method.GetParameters().Length == 4 &&
                method.GetParameters()[0].ParameterType == context.GetType()).Invoke(null, [context, history, false, false]);
        typeof(SessionFileTransferService).GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);
        StartCoordinatorSnapshotStreamForTest(context);
        var state = CreateCoordinatorStateForTest(context);
        var prior = state.PersistentState.FileTransferRouteTransition.BootstrapExchange!;
        Assert.True(prior.Completed);
        Assert.Equal(3, state.CurrentLiveRouteEpoch!.EpochId);
        Assert.Equal(0L, GetPrivateProperty<long>(context, "LastRecoveredV6TransportEpoch"));
        Assert.Equal(0, GetPrivateProperty<int>(context, "LastRecoveredV6TransportLiveRouteEpochId"));
        Assert.Same(dataSession, GetPrivatePropertyValue(context, "DataSession"));
        var oldResponse = Assert.IsType<FileTransferFallbackBootstrapStateFrameV6>(prior.RetainedPeerFrame);
        var issuer = state.PersistentState.FileTransferRouteTransition.BootstrapAgreement!.Accept!.FallbackBootstrapIssuerId;
        var announce = oldResponse with { SenderRequestId = null, ReceiverChallengeId = $"fbc1:{issuer}:2:{new string('c', 64)}" };
        bool Receive(FileTransferFallbackBootstrapStateFrameV6 frame) => Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod(
            "TryHandleBootstrapCorrelationFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
            [FileTransferDirection.Outbound, dataSession, new FileTransferReceivedDataFrame(frame, FileTransferTransportKind.RegularNkn, "control", DateTimeOffset.UtcNow)]));
        Assert.True(Receive(oldResponse));
        Assert.Empty(bridge.Executions);
        Assert.True(Receive(announce));
        var solicit = Assert.Single(bridge.Executions);
        Assert.Equal(FileTransferBootstrapPhase.Solicit, solicit.Command.Phase);
        Assert.Equal(2, solicit.Command.Bootstrap.BootstrapGeneration);
        Assert.NotEqual(prior.LocalId, solicit.Command.Frame.SenderRequestId);
        Assert.Null(solicit.Command.Frame.ReceiverChallengeId); // Announce is not a binding.
        Assert.True(Receive(oldResponse));
        Assert.Single(bridge.Executions);
        var response = announce with { SenderRequestId = solicit.Command.Frame.SenderRequestId };
        Assert.True(Receive(response));
        solicit.Delivery.TrySetResult(ServiceBootstrapReceipt(solicit.Command, settled: false));
        await WaitUntilAsync(() => CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapExchange!.First.Delivery is not null, timeoutMs: 3000);
        Assert.Single(bridge.Executions);
        solicit.Settlement.TrySetResult(ServiceBootstrapReceipt(solicit.Command, settled: true));
        await WaitUntilAsync(() => bridge.Executions.Count == 2, timeoutMs: 3000);
        var confirm = bridge.Executions[1];
        Assert.Equal(FileTransferBootstrapPhase.Confirm, confirm.Command.Phase);
        Assert.Equal(response.ReceiverChallengeId, confirm.Command.Frame.ReceiverChallengeId);
        Assert.Null(confirm.Command.EpochFrame);
        confirm.Delivery.TrySetResult(ServiceBootstrapReceipt(confirm.Command, settled: false));
        confirm.Settlement.TrySetResult(ServiceBootstrapReceipt(confirm.Command, settled: true));
        await WaitUntilAsync(() => CreateCoordinatorStateForTest(context).CurrentLiveRouteEpoch?.EpochId == 4, timeoutMs: 5000);
        var committed = CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value;
        Assert.NotNull(committed.RetainedFrameApplyCommand);
        Assert.Null(committed.RetainedFrameApplyReceipt);
        Assert.Null(committed.DispositionReceipt);
        Assert.Null(GetPrivatePropertyValue(context, "InstalledOutboundSparsePump"));

        // This bounded phase fixture has no pump loop; drive its existing install/replay handoff explicitly.
        object install;
        lock (GetServiceGate(service))
        {
            var staged = StageSparsePumpInstall(service, context, predecessor: null, "test_correlated_successor_retained_replay");
            Assert.True(staged.Accepted, staged.Reason);
            var installed = CommitSparsePumpInstall(service, context, Assert.IsAssignableFrom<object>(staged.Install), dataSession);
            Assert.True(installed.Accepted, installed.Reason);
            install = Assert.IsAssignableFrom<object>(installed.Install);
        }
        var replay = typeof(SessionFileTransferService).GetMethod(
            "TryApplyOutboundCommittedFileTransferRouteTransitionRetainedFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await Assert.IsAssignableFrom<Task>(replay.Invoke(service, [context, install, dataSession]));
        try
        {
            await WaitUntilAsync(() => CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.LastTerminalOperation is
                { Authority.Kind: FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6,
                  DispositionReceipt.Status: FileTransferRouteTransitionDispositionApplyStatus.Applied }, timeoutMs: 5000);
        }
        catch (OperationCanceledException)
        {
            var failed = CreateCoordinatorStateForTest(context);
            Assert.Fail(System.Text.Json.JsonSerializer.Serialize(new
            {
                failed.RouteSelection, Exchange = failed.PersistentState.FileTransferRouteTransition.BootstrapExchange,
                Transition = failed.PersistentState.FileTransferRouteTransition.CurrentOperation,
            }) + Environment.NewLine + string.Join(Environment.NewLine, ReadOperationalLogTail(logStart).Split('\n')
                .Where(line => line.Contains("transfer_id=" + transferId, StringComparison.Ordinal) && !line.Contains("coordinator_snapshot", StringComparison.Ordinal))));
        }
        var final = CreateCoordinatorStateForTest(context);
        Assert.Equal(4, final.CurrentLiveRouteEpoch!.EpochId);
        Assert.True(final.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Completed);
        Assert.Equal(2, final.PersistentState.FileTransferRouteTransition.LastFallbackV6ProofBootstrapGeneration);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, final.RouteSelection.Route);
        var terminal = final.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value;
        Assert.Equal(FileTransferRouteTransitionRetainedFrameApplyStatus.Applied, terminal.RetainedFrameApplyReceipt!.Value.Status);
        Assert.Equal(committed.RetainedFrameApplyCommand, terminal.RetainedFrameApplyReceipt.Value.Command);
        Assert.Null(GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution"));
        var sentCount = dataSession.SentFrames.Count;
        await Assert.IsAssignableFrom<Task>(replay.Invoke(service, [context, install, dataSession]));
        Assert.Equal(sentCount, dataSession.SentFrames.Count);
        Assert.Equal(terminal, CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.LastTerminalOperation);
    }

    [Fact]
    public async Task BootstrapCorrelation_ServiceAnnounceCannotBeSkippedByEarlySolicit()
    {
        const string transferId = "bootstrap_service_announce";
        using var service = new SessionFileTransferService();
        var dataSession = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        using var bridge = new BootstrapPhaseTestBridge(dataSession);
        var issuer = FileTransferBootstrapCorrelation.CreateIssuer("service-peer", dataSession.SessionId, transferId, FileTransferDirection.Outbound);
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, dataSession,
            beforeActivation: context => PinBootstrapServiceAgreement(context, issuer));
        var firstTask = ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge,
            LocalStopEvaluationForTest(context, dataSession));
        var announce = Assert.Single(bridge.Executions);
        Assert.Equal(FileTransferBootstrapPhase.Announce, announce.Command.Phase);
        var solicit = new FileTransferFallbackBootstrapRequestFrameV6
        {
            SessionId = dataSession.SessionId, TransferId = transferId, TransportEpoch = 1,
            SenderRequestId = $"fbc1:{issuer}:23:{new string('b', 64)}",
        };
        bool Receive() => Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod("TryHandleBootstrapCorrelationFrame",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
            [FileTransferDirection.Inbound, dataSession, new FileTransferReceivedDataFrame(solicit, FileTransferTransportKind.RegularNkn, "bulk", DateTimeOffset.UtcNow)]));
        Assert.True(Receive());
        Assert.True(Receive());
        Assert.Single(bridge.Executions);
        announce.Delivery.TrySetResult(ServiceBootstrapReceipt(announce.Command, settled: false));
        await WaitUntilAsync(() => CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapExchange!.First.Delivery is not null, timeoutMs: 3000);
        Assert.Single(bridge.Executions);
        Assert.Null(CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.CurrentOperation);
        announce.Settlement.TrySetResult(ServiceBootstrapReceipt(announce.Command, settled: true));
        await firstTask.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => bridge.Executions.Count == 2, timeoutMs: 3000);
        var response = bridge.Executions[1];
        Assert.Equal(FileTransferBootstrapPhase.Response, response.Command.Phase);
        Assert.Equal(announce.Command.Identity, response.Command.PredecessorIdentity);
        Assert.Null(response.Command.EpochFrame);
        Assert.Equal(announce.Command.Frame.ReceiverChallengeId, response.Command.Frame.ReceiverChallengeId);
        Assert.True(Receive());
        Assert.Equal(2, bridge.Executions.Count);
        response.Delivery.TrySetResult(ServiceBootstrapReceipt(response.Command, settled: false));
        response.Settlement.TrySetResult(ServiceBootstrapReceipt(response.Command, settled: true));
        await WaitUntilAsync(() => CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapExchange!.Final!.IsReady, timeoutMs: 3000);
        Assert.False(CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapExchange!.Completed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BootstrapCorrelation_ServiceResponseWaitsForSettlementAndOwnedWriteDrain(bool pendingWrite, bool pauseBeforeSettlement)
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync(blockWrite: pendingWrite);
        var service = fixture.Service;
        var dataSession = fixture.Session;
        var bridge = fixture.Bridge;
        var context = fixture.Context;
        var transferId = dataSession.TransferId;
        var senderIssuer = FileTransferBootstrapCorrelation.CreateIssuer("peer", dataSession.SessionId, transferId, FileTransferDirection.Outbound);
        var before = CreateCoordinatorStateForTest(context);
        var solicit = new FileTransferFallbackBootstrapRequestFrameV6
        {
            SessionId = dataSession.SessionId, TransferId = transferId, TransportEpoch = 1,
            SenderRequestId = $"fbc1:{senderIssuer}:371:{new string('b', 64)}",
        };
        var writing = pendingWrite ? fixture.WriteAsync() : null;
        bool Receive(FileTransferDataFrame frame) => Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod(
            "TryHandleBootstrapCorrelationFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
            [FileTransferDirection.Inbound, dataSession, new FileTransferReceivedDataFrame(frame, FileTransferTransportKind.RegularNkn, "control", DateTimeOffset.UtcNow)]));
        Assert.True(Receive(solicit));
        Assert.True(Receive(solicit));
        if (pendingWrite)
        {
            Assert.Empty(bridge.Executions);
            Assert.Null(CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapExchange);
            fixture.Stream.CompleteWrite();
            await writing!.WaitAsync(TimeSpan.FromSeconds(3));
        }
        await WaitUntilAsync(() => bridge.Executions.Count == 1, timeoutMs: 3000);
        var execution = Assert.Single(bridge.Executions);
        Assert.Equal(FileTransferBootstrapPhase.Response, execution.Command.Phase);
        Assert.Equal(1, execution.Command.Bootstrap.BootstrapGeneration);
        var response = Assert.IsType<FileTransferFallbackBootstrapStateFrameV6>(execution.Command.Frame);
        Assert.Equal(solicit.SenderRequestId, response.SenderRequestId);
        execution.Delivery.TrySetResult(ServiceBootstrapReceipt(execution.Command, settled: false));
        await WaitUntilAsync(() => CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapExchange?.First.Delivery is not null, timeoutMs: 3000);
        Assert.True(Receive(solicit with { ReceiverChallengeId = response.ReceiverChallengeId }));
        Assert.False(CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapExchange!.Completed);
        Assert.Equal(before.PersistentState.FileTransferRouteTransition.LastTerminalOperation,
            CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.LastTerminalOperation);
        Assert.Single(bridge.Executions);
        if (pauseBeforeSettlement)
        {
            Assert.NotNull(await service.PauseTransferAsync(transferId, "test_pause", CancellationToken.None));
            Assert.True(CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapExchange!.Invalidated);
        }
        var priorDelivery = CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapExchange!.First.Delivery;
        var settlementLogStart = GetOperationalLogLength();
        execution.Settlement.TrySetResult(ServiceBootstrapReceipt(execution.Command, settled: true));
        if (pauseBeforeSettlement)
        {
            Assert.True(Receive(solicit with { ReceiverChallengeId = response.ReceiverChallengeId }));
            await WaitUntilAsync(() => ReadOperationalLogTail(settlementLogStart).Contains("decision_reason=bootstrap_phase_callback_scope_stale", StringComparison.Ordinal), timeoutMs: 3000);
            var after = CreateCoordinatorStateForTest(context);
            Assert.True(after.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Invalidated);
            Assert.Null(after.PersistentState.FileTransferRouteTransition.BootstrapExchange!.First.Settlement);
            Assert.Same(priorDelivery, after.PersistentState.FileTransferRouteTransition.BootstrapExchange!.First.Delivery);
            Assert.Equal(before.PersistentState.FileTransferRouteTransition.LastTerminalOperation, after.PersistentState.FileTransferRouteTransition.LastTerminalOperation);
        }
        else
        {
            await WaitUntilAsync(() => CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.LastTerminalOperation is
                { Authority.Kind: FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6,
                  DispositionReceipt.Status: FileTransferRouteTransitionDispositionApplyStatus.Applied }, timeoutMs: 5000);
            var after = CreateCoordinatorStateForTest(context);
            Assert.True(after.PersistentState.FileTransferRouteTransition.BootstrapExchange!.Completed);
            Assert.Equal(PostTunaFallbackPeerProofFrameIdentity.Create(solicit with { ReceiverChallengeId = response.ReceiverChallengeId }),
                after.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value.RetainedFrameApplyReceipt!.Value.Command.FrameIdentity);
            Assert.Equal(FileTransferRoute.PostTunaFallbackV6, after.RouteSelection.Route);
        }
        Assert.Single(bridge.Executions);
    }

    private static void PinBootstrapServiceAgreement(object context, string senderIssuer)
    {
        var state = CreateCoordinatorStateForTest(context);
        var offer = new FileTransferOfferV2
        {
            SessionId = state.SessionId, TransferId = state.TransferId, FileName = "bootstrap.bin", FileSizeBytes = 256 * 21 * 1024,
            PreferredDataProtocolVersion = 4, FileTransferRoute = "regular_nkn_v4_fast",
            FallbackBootstrapCorrelationVersion = 1, FallbackBootstrapIssuerId = senderIssuer,
        };
        void Adopt(FileTransferBootstrapAgreementDecision decision)
        {
            Assert.True(decision.Accepted, decision.Reason);
            typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(method => method.Name == "AdoptBootstrapAgreementLocked" && method.GetParameters()[0].ParameterType == context.GetType())
                .Invoke(null, [context, decision]);
            state = CreateCoordinatorStateForTest(context);
        }
        Adopt(FileTransferCoordinator.PinFallbackBootstrapOffer(state, offer));
        Adopt(FileTransferCoordinator.ApplyFallbackBootstrapAccept(state, new()
        {
            SessionId = state.SessionId, TransferId = state.TransferId, AcceptedDataProtocolVersion = 4,
            FileTransferRoute = "regular_nkn_v4_fast", FallbackBootstrapCorrelationVersion = 1,
            FallbackBootstrapIssuerId = FileTransferCoordinator.CreateLocalFallbackBootstrapIssuer(state),
        }));
        Adopt(FileTransferCoordinator.ApplyFallbackBootstrapSessionOpen(state, new()
        {
            SessionId = state.SessionId, TransferId = state.TransferId, ProtocolVersion = 4,
            FileTransferRoute = "regular_nkn_v4_fast", SessionRole = FileTransferProtocol.SessionRoleSender,
            ChunkSizeBytes = 21 * 1024, InitialPipelineDepth = 8,
            FallbackBootstrapCorrelationVersion = 1, FallbackBootstrapIssuerId = senderIssuer,
        }));
    }

    private static FileTransferBootstrapPhaseReceipt ServiceBootstrapReceipt(FileTransferBootstrapPhaseCommand command, bool settled)
    {
        var copies = ImmutableArray.CreateBuilder<FileTransferBootstrapPhysicalCopy>();
        foreach (var identity in new[] { command.EpochFrameIdentity, command.FrameIdentity }.OfType<PostTunaFallbackPeerProofFrameIdentity>())
        {
            copies.Add(new("control", identity, FileTransferBootstrapCopyOutcome.Succeeded));
            copies.Add(new("bulk", identity, settled ? FileTransferBootstrapCopyOutcome.Succeeded : FileTransferBootstrapCopyOutcome.Pending));
        }
        return new(command, settled ? FileTransferBootstrapPhaseReceiptStatus.Settled : FileTransferBootstrapPhaseReceiptStatus.Delivered,
            true, copies.ToImmutable(), "modeled_service_copy_evidence");
    }

    private sealed record BootstrapServiceExecution(FileTransferBootstrapPhaseCommand Command,
        TaskCompletionSource<FileTransferBootstrapPhaseReceipt> Delivery, TaskCompletionSource<FileTransferBootstrapPhaseReceipt> Settlement);

    private sealed class BootstrapPhaseTestBridge(InitialBootstrapRecordingDataSession session)
        : InitialBootstrapCheckpointTransport(session), IFileTransferRouteTransitionBridge
    {
        public List<BootstrapServiceExecution> Executions { get; } = [];
        bool IFileTransferRouteTransitionBridge.SupportsFallbackBootstrapCorrelation => true;
        FileTransferBootstrapPhaseExecution IFileTransferRouteTransitionBridge.ExecuteFallbackBootstrapPhase(
            FileTransferBootstrapPhaseCommand command, IFileTransferDataSession dataSession, Func<bool> current, CancellationToken ct)
        {
            Assert.True(current());
            Assert.Same(session, dataSession);
            var execution = new BootstrapServiceExecution(command, new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
            Executions.Add(execution);
            return new(execution.Delivery.Task, execution.Settlement.Task);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task BootstrapCorrelation_ServiceHandshakeUsesOnlySenderOpen(bool senderSupports, bool receiverSupports)
    {
        const string sessionId = "session_bootstrap_service_handshake";
        const string transferId = "bootstrap_service_handshake";
        using var senderBridge = new BootstrapServiceBridge(sessionId, senderSupports);
        using var receiverBridge = new BootstrapServiceBridge(sessionId, receiverSupports);
        senderBridge.Connect(receiverBridge);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderBridge);
        receiver.AttachTransport(receiverBridge);
        var openCaptured = new TaskCompletionSource<FileTransferSessionOpenV2>(TaskCreationOptions.RunContinuationsAsynchronously);
        senderBridge.OutboundSessionOpenDeliveryOverrideAsync = (_, open, _) =>
        {
            openCaptured.TrySetResult(open);
            return Task.FromResult(true);
        };
        senderBridge.OutboundDataFrameDeliveryOverrideAsync = (_, _, _) => Task.FromResult(true);
        await sender.TryStartSendAsync(new("bootstrap.bin", 1024, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[1024], writable: false)), CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 3000);
        var senderContext = typeof(SessionFileTransferService).GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!;
        var receiverContext = typeof(SessionFileTransferService).GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(receiver)!;
        var offer = Assert.Single(senderBridge.SentOffers);
        Assert.Equal(senderSupports ? 1 : (int?)null, offer.FallbackBootstrapCorrelationVersion);
        if (senderSupports)
        {
            Assert.Equal(FileTransferCoordinator.CreateLocalFallbackBootstrapIssuer(CreateCoordinatorStateForTest(senderContext)), offer.FallbackBootstrapIssuerId);
        }
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(new MemoryStream()), CancellationToken.None);
        var open = await openCaptured.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var accept = Assert.Single(receiverBridge.SentAccepts);
        var selected = senderSupports && receiverSupports;
        Assert.Equal(selected ? 1 : (int?)null, accept.FallbackBootstrapCorrelationVersion);
        Assert.Equal(accept.FallbackBootstrapCorrelationVersion, open.FallbackBootstrapCorrelationVersion);
        Assert.Equal(selected ? offer.FallbackBootstrapIssuerId : null, open.FallbackBootstrapIssuerId);
        Assert.Equal(selected, CreateCoordinatorStateForTest(senderContext).PersistentState.FileTransferRouteTransition.BootstrapAgreement?.IsEnabled ?? false);
        Assert.False(CreateCoordinatorStateForTest(receiverContext).PersistentState.FileTransferRouteTransition.BootstrapAgreement?.IsEnabled ?? false);
        Assert.Empty(receiverBridge.SentSessionOpens);

        async Task ReceiveOpen(FileTransferSessionOpenV2 message) => await Assert.IsAssignableFrom<Task>(
            typeof(SessionFileTransferService).GetMethod("HandleIncomingSessionOpenAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(receiver, [message]));
        await ReceiveOpen(open);
        var afterOpen = CreateCoordinatorStateForTest(receiverContext);
        Assert.Equal(selected, afterOpen.PersistentState.FileTransferRouteTransition.BootstrapAgreement?.IsEnabled ?? false);
        if (afterOpen.PersistentState.FileTransferRouteTransition.BootstrapAgreement is not null)
        {
            var dataSession = GetPrivatePropertyValue(receiverContext, "DataSession");
            var generation = GetPrivateProperty<long>(receiverContext, "DataSessionGeneration");
            await ReceiveOpen(open);
            Assert.Same(dataSession, GetPrivatePropertyValue(receiverContext, "DataSession"));
            Assert.Equal(generation, GetPrivateProperty<long>(receiverContext, "DataSessionGeneration"));
            var beforeConflict = CreateCoordinatorStateForTest(receiverContext);
            await ReceiveOpen(open with { FallbackBootstrapCorrelationVersion = 1, FallbackBootstrapIssuerId = new string('d', 64) });
            Assert.Equal(beforeConflict.PersistentState, CreateCoordinatorStateForTest(receiverContext).PersistentState);
            Assert.False(receiver.Snapshot.Inbound!.IsTerminal);
        }
        Assert.Empty(receiverBridge.SentSessionOpens);
    }

    private sealed class BootstrapServiceBridge(string sessionId, bool supports = true)
        : LoopbackFileTransferTransport(sessionId), IFileTransferRouteTransitionBridge
    {
        bool IFileTransferRouteTransitionBridge.SupportsFallbackBootstrapCorrelation => supports;
    }
}
