using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    internal enum InitialTunaRecoveryCase { Off, OffOn, OffOnOff }
    internal enum FallbackRepairCase { Resume, AcceptedTail, NoLegacyHandoff, CurrentPeerRange }
    internal enum RegularActivationCase { AcceptedTail, SameTransfer, ReadyBarrier }

    internal static async Task RunCoordinatorRegularActivationForTestAsync(RegularActivationCase scenario,
        MemoryStream? activationDestination = null)
    {
        using var tests = new NknAccelerationTransportTests();
        await tests.RunJoinedRouteRecoveryAsync(cycle: true, regularActivationCase: scenario,
            activationDestination: activationDestination);
    }

    internal static async Task RunCoordinatorFallbackRepairForTestAsync(FallbackRepairCase scenario)
    {
        using var tests = new NknAccelerationTransportTests();
        await tests.RunJoinedRouteRecoveryAsync(cycle: true,
            initialTunaCase: InitialTunaRecoveryCase.OffOn, fallbackRepairCase: scenario);
    }

    internal static async Task RunInitialTunaRouteCycleForTestAsync(InitialTunaRecoveryCase scenario,
        Action<SessionFileTransferService, object>? onReactivated = null)
    {
        using var tests = new NknAccelerationTransportTests();
        await tests.RunJoinedRouteRecoveryAsync(cycle: scenario != InitialTunaRecoveryCase.Off,
            initialTunaCase: scenario, onReactivated: onReactivated);
    }

    internal static async Task RunInitialTunaSwitchOffRecoveryForTestAsync()
    {
        using var tests = new NknAccelerationTransportTests();
        await tests.RunJoinedRouteRecoveryAsync(cycle: false);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public Task JoinedBootstrapCorrelation_RegularTransferCyclesOffOnOff()
        => RunJoinedRouteRecoveryAsync(cycle: true);

    internal static async Task RunPhysicalFallbackSendCycleForTestAsync()
    {
        using var tests = new NknAccelerationTransportTests();
        await tests.RunJoinedRouteRecoveryAsync(cycle: true, holdPhysicalSend: true);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public Task JoinedBootstrapCorrelation_EarlyCheckpointProofPromotesThroughReactivation()
        => RunJoinedRouteRecoveryAsync(cycle: true, earlyCheckpointProof: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public void EarlyCheckpointProofPromotion_RequiresExactCoordinatorCustody(bool failedBeforeQuiescence)
    {
        var envelopes = FileTransferCoordinatorTests.CreateEarlyCheckpointClearProofSnapshotFixture(
            FileTransferDirection.Outbound, failedBeforeQuiescence);
        var lines = FileTransferInitialRegularRouteEvidenceTests.Emit(envelopes, (type, emitter, envelope) =>
            typeof(SessionFileTransferService).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Single(method => method.Name == "EnqueueFallbackTailYieldDecisionAuthorityEffect" &&
                    method.GetParameters()[0].ParameterType == type)
                .Invoke(null, [emitter, envelope.CoordinatorInstanceId, envelope.SessionId, envelope.TransferId,
                    FileTransferDirection.Outbound, envelope.Aggregate.AfterStateGeneration, envelope]));
        var log = string.Join(Environment.NewLine, lines);
        AssertEarlyCheckpointProofPromotion(log);

        var quiescent = Assert.Single(lines.Where(line => line.Contains(
                "event=filetransfer_coordinator_transition_snapshot;", StringComparison.Ordinal))
            .Select(line =>
            {
                var payload = line.Split(';').Select(part => part.Trim())
                    .Single(part => part.StartsWith("snapshot_payload=", StringComparison.Ordinal))["snapshot_payload=".Length..];
                var base64 = payload.Replace('-', '+').Replace('_', '/');
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
                    base64.PadRight((base64.Length + 3) / 4 * 4, '=')));
                return (Payload: payload, Json: System.Text.Json.Nodes.JsonNode.Parse(json)!);
            }), item => item.Json["decisionKind"]!.GetValue<string>() == "FallbackTailQuiescent");
        static string Encode(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        foreach (var field in failedBeforeQuiescence
            ? new[] { "peerProofFingerprint", "failedPredecessorAuthorityId", "failedSupersessionCarriedPeerProof" }
            : new[] { "peerProofFingerprint" })
        {
            var mutated = quiescent.Json.DeepClone();
            var operation = Assert.Single(mutated["aggregate"]!["operations"]!.AsArray(), item =>
                item!["operationRole"]!.GetValue<string>() == "yield_current" &&
                item["operationKind"]!.GetValue<string>() == "Quiescent")!;
            if (field == "failedSupersessionCarriedPeerProof")
                operation["fallbackTailYield"]![field] = false;
            else
                operation["fallbackTailYield"]![field] = "wrong-proof-or-authority";
            var invalidLog = log.Replace(quiescent.Payload, Encode(mutated.ToJsonString()), StringComparison.Ordinal);
            Assert.NotEqual(log, invalidLog);
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertEarlyCheckpointProofPromotion(invalidLog));
        }
    }

    private async Task RunJoinedRouteRecoveryAsync(bool cycle, bool earlyCheckpointProof = false,
        bool holdPhysicalSend = false, int priorDataSessions = 0, bool stopReceiver = false,
        InitialTunaRecoveryCase? initialTunaCase = null,
        Action<SessionFileTransferService, object>? onReactivated = null,
        FallbackRepairCase? fallbackRepairCase = null,
        RegularActivationCase? regularActivationCase = null, MemoryStream? activationDestination = null)
    {
        var startsOnTuna = !cycle || initialTunaCase is not null;
        var secondStop = cycle && initialTunaCase != InitialTunaRecoveryCase.OffOn;
        var firstFallbackEpoch = startsOnTuna ? 1 : 2;
        var transferId = initialTunaCase is { } scenario ? $"joined-initial-tuna-{scenario}" :
            cycle ? "joined-regular-tuna-cycle" : "joined-initial-tuna-switch-off";
        // Migrated fixtures retain their original per-edge deadlines below; no
        // production timeout or send-timeout override is introduced for them.
        using var timeout = initialTunaCase is null
            ? new CancellationTokenSource(TimeSpan.FromSeconds(cycle ? 80 : 40)) : new CancellationTokenSource();
        var previousSendTimeout = SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests;
        if (!cycle && initialTunaCase is null)
            SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests = TimeSpan.FromMilliseconds(100);
        var logStart = GetOperationalLogLength();
        var physicalSendReached = new TaskCompletionSource<(int Start, int Count)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePhysicalSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var physicalSendFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var hostClient = new FakeNknClient("host.joined-route-recovery.address");
            var helperClient = new FakeNknClient("helper.joined-route-recovery.address");
            var options = NknTransportOptions.Load();
            var hostIdentity = new NknIdentity("host-joined-route-recovery", hostClient.Address);
            var helperIdentity = new NknIdentity("helper-joined-route-recovery", helperClient.Address);
            // Registration only: these adapters are never connected to the live network.
            using var hostPressure = new RealNknClientAdapter(hostIdentity, options);
            using var helperPressure = new RealNknClientAdapter(helperIdentity, options);
            using INknTunaAccelerationSession hostLane = !startsOnTuna
                ? new RetryableTunaAccelerationSession(true, 0, supportedLanes: NknAccelerationLaneKind.File)
                : new PreparationHandshakeLane(allowRuntimeRestart: cycle);
            using INknTunaAccelerationSession helperLane = !startsOnTuna
                ? new RetryableTunaAccelerationSession(false, 0, supportedLanes: NknAccelerationLaneKind.File)
                : new PreparationHandshakeLane(allowRuntimeRestart: cycle, canListen: false);
            if (startsOnTuna)
            {
                ((PreparationHandshakeLane)hostLane).FilePeer = (PreparationHandshakeLane)helperLane;
                ((PreparationHandshakeLane)helperLane).FilePeer = (PreparationHandshakeLane)hostLane;
            }
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity,
                NknTunaAccelerationOptions.Disabled, hostLane, hostPressure);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity,
                NknTunaAccelerationOptions.Disabled, helperLane, helperPressure);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(host);
            receiver.AttachTransport(helper);
            var sessionId = await ApproveNknSessionAsync(host, helper, timeout.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
            for (var index = 0; index < priorDataSessions; index++)
            {
                using var priorHost = await host.OpenFileTransferDataSessionAsync(sessionId, $"prior-transfer-{index}", timeout.Token);
                using var priorHelper = await helper.OpenFileTransferDataSessionAsync(sessionId, $"prior-transfer-{index}", timeout.Token);
            }
            var sent = new ConcurrentQueue<(bool FromHost, FileTransferDataFrame Frame, bool AfterFallback, bool Regular)>();
            var droppedRegular = new ConcurrentDictionary<byte[], byte>();
            var delayedCheckpoints = new List<NknIncomingMessage>();
            var holdCheckpoints = earlyCheckpointProof;
            var offers = new ConcurrentQueue<FileTransferOfferV2>();
            var opens = new ConcurrentQueue<FileTransferSessionOpenV2>();
            var cancels = new ConcurrentQueue<FileTransferCancelV1>();
            var stopEvaluations = new ConcurrentQueue<FileTransferFallbackV6ProofBootstrapEvaluation>();
            var stopDiagnostics = new ConcurrentQueue<object>();
            var allowFrontier = 0;
            var droppedFrontierBatches = 0;
            var fallbackPublished = 0;
            var holdCycleTail = cycle && regularActivationCase is null ? 1 : 0;
            var payloadTailBarrier = 70;
            var withheldFrontier = fallbackRepairCase == FallbackRepairCase.CurrentPeerRange ? 3 : 0;
            var holdActivationAck = regularActivationCase == RegularActivationCase.ReadyBarrier;
            var activationAcks = new List<(bool FromHost, bool Regular, NknIncomingMessage Packet)>();
            var suppressFeedback = 0;
            var blockReenabledV4 = 0;
            var captureFrames = 1;
            var reactivatedLogStart = 0;
            var reactivatedFrameStart = 0;
            byte[]? heldPhysicalPayload = null;
            var physicalSendSelected = 0;
            long heldPhysicalLease = 0;
            FileTransferRouteTransitionPhysicalDrainCommand? heldDrain = null;
            var publications = new List<RoutePublicationToken>();
            var terminalProjectionObservations = new ConcurrentQueue<(FileTransferDirection Direction, bool FallbackActive)>();
            sender.TransferChanged += (_, args) =>
            {
                if (args.Snapshot.Outbound is { State: FileTransferTransferState.Completed } completed && completed.TransferId == transferId)
                    terminalProjectionObservations.Enqueue((FileTransferDirection.Outbound, host.IsPostTunaFileFallbackActiveForRouteSelection));
            };
            receiver.TransferChanged += (_, args) =>
            {
                if (args.Snapshot.Inbound is { State: FileTransferTransferState.Completed } completed && completed.TransferId == transferId)
                    terminalProjectionObservations.Enqueue((FileTransferDirection.Inbound, helper.IsPostTunaFileFallbackActiveForRouteSelection));
            };
            helper.FileTransferOfferReceived += (_, args) => offers.Enqueue(args.Message);
            helper.FileTransferSessionOpenReceived += (_, args) => opens.Enqueue(args.Message);
            helper.FileTransferCancelReceived += (_, args) => cancels.Enqueue(args.Message);
            host.FileTransferCancelReceived += (_, args) => cancels.Enqueue(args.Message);
            ((IFileTransferRouteTransitionBridge)host).FallbackV6ProofBootstrapEvaluationRequested +=
                (_, args) => ObserveStop(sender, "outboundTransfer", args.Evaluation);
            ((IFileTransferRouteTransitionBridge)helper).FallbackV6ProofBootstrapEvaluationRequested +=
                (_, args) => ObserveStop(receiver, "inboundTransfer", args.Evaluation);
            BindForward(hostLane, (lane, bytes, ct) => Forward(true, helperLane, lane, bytes, ct));
            BindForward(helperLane, (lane, bytes, ct) => Forward(false, hostLane, lane, bytes, ct));
            hostClient.BeforeSendCoreAsync = (_, bytes, lane, ct) => ObserveRegular(true, lane, bytes, ct);
            helperClient.BeforeSendCoreAsync = (_, bytes, lane, ct) => ObserveRegular(false, lane, bytes, ct);
            hostClient.ShouldDeliverSendAsync = (_, bytes, _) => Task.FromResult(!droppedRegular.TryRemove(bytes, out _));
            helperClient.ShouldDeliverSendAsync = (_, bytes, _) => Task.FromResult(!droppedRegular.TryRemove(bytes, out _));

            if (startsOnTuna)
            {
                await host.RequestAccelerationNegotiationAsync("runtime_unlock", timeout.Token);
                await WaitFor("initial Tuna readiness", () =>
                    sender.ObserveSessionPreparationForDiagnostics(sessionId, FileTransferDirection.Outbound, DateTimeOffset.UtcNow).Ready &&
                    receiver.ObserveSessionPreparationForDiagnostics(sessionId, FileTransferDirection.Inbound, DateTimeOffset.UtcNow).Ready &&
                    (long)GetPrivateField(host, "accelerationNegotiationSlotGeneration")! == 0 &&
                    (long)GetPrivateField(helper, "accelerationNegotiationSlotGeneration")! == 0);
            }
            var modulus = initialTunaCase switch
            {
                InitialTunaRecoveryCase.Off => 229,
                InitialTunaRecoveryCase.OffOn => 241,
                _ => cycle ? 239 : 233,
            };
            var payloadLength = fallbackRepairCase switch
            {
                FallbackRepairCase.Resume => 2_000_000,
                FallbackRepairCase.AcceptedTail or FallbackRepairCase.NoLegacyHandoff => 20_000_000,
                FallbackRepairCase.CurrentPeerRange => 4_000_000,
                _ => cycle ? 1_572_864 : 768_000,
            };
            if (regularActivationCase is { } activationCase)
                payloadLength = activationCase switch
                {
                    RegularActivationCase.AcceptedTail => 512 * 21 * 1024,
                    RegularActivationCase.SameTransfer => 4_000_000,
                    _ => 20_000_000,
                };
            var payload = Enumerable.Range(0, payloadLength).Select(index => (byte)(index % modulus)).ToArray();
            using var destination = activationDestination ?? new MemoryStream();
            await sender.TryStartSendAsync(new("joined-route-recovery.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), timeout.Token);
            // ReadyBarrier's committed fixture uses WaitUntilAsync without an
            // override: SessionFileTransferServiceTestBase defaults to 3000 ms.
            await WaitFor("offer", () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision,
                regularActivationCase == RegularActivationCase.ReadyBarrier ? 3 :
                initialTunaCase is null && regularActivationCase is null ? 8 : 5);
            var outbound = GetPrivateField(sender, "outboundTransfer")!;
            var inbound = GetPrivateField(receiver, "inboundTransfer")!;
            await receiver.AcceptIncomingTransferAsync(transferId,
                (_, _) => Task.FromResult<Stream>(destination), timeout.Token);
            var initialRoute = startsOnTuna ? FileTransferRoute.FileTunaV4 : FileTransferRoute.RegularNknV4Fast;
            await WaitFor("initial runtime and frontier feedback", () =>
                Published(sender, outbound, initialRoute) && Published(receiver, inbound, initialRoute) &&
                sent.Any(item => !item.FromHost && item.Frame is FileTransferStateFrameV4 state &&
                    state.MissingRanges.Any(range => range.StartChunkIndex == withheldFrontier && range.ChunkCount > 0)),
                initialTunaCase is null ? 8 : 10);
            Assert.True(Volatile.Read(ref droppedFrontierBatches) > 0);
            AssertJoinedBootstrapAgreement(sender, outbound, receiver, inbound, sessionId, host, helper);
            Assert.Equal(1L, ContextValue(outbound, "DataSessionGeneration"));
            Assert.Equal(1L, ContextValue(inbound, "DataSessionGeneration"));
            Assert.Equal(priorDataSessions + 1L,
                Assert.IsAssignableFrom<IFileTransferDataSessionGenerationProvider>(ContextValue(outbound, "DataSession")).Generation);
            Assert.Equal(priorDataSessions + 1L,
                Assert.IsAssignableFrom<IFileTransferDataSessionGenerationProvider>(ContextValue(inbound, "DataSession")).Generation);
            Assert.Equal(startsOnTuna ? FileTransferRouteResolver.FileTunaV4Token : FileTransferRouteResolver.RegularNknV4FastToken,
                Assert.Single(offers).FileTransferRoute);
            Assert.Equal(4, Assert.Single(offers).PreferredDataProtocolVersion);
            if (startsOnTuna && cycle)
                CapturePublications(FileTransferRoute.FileTunaV4, 0);
            if (cycle && !startsOnTuna)
            {
                if (regularActivationCase is not null)
                {
                    Assert.True((int)ContextValue(outbound, "ChunksAcceptedForTransport")! >
                        (int)ContextValue(outbound, "RemoteNextExpectedChunkIndex")!);
                    Assert.Equal(0, ContextValue(outbound, "RemoteNextExpectedChunkIndex"));
                }
                await host.RequestAccelerationNegotiationAsync("runtime_unlock", timeout.Token);
                if (regularActivationCase == RegularActivationCase.ReadyBarrier)
                {
                    await WaitFor("physical readiness with correlated probe ACK withheld", () =>
                    {
                        lock (activationAcks) return activationAcks.Count > 0 && hostLane.IsAvailable && helperLane.IsAvailable;
                    }, 5);
                    await Task.Delay(1000, timeout.Token);
                    var batchesBeforeHandoff = sent.Count(item => item.FromHost && item.Frame is FileTransferChunkBatchFrameV4);
                    await Task.Delay(1200, timeout.Token);
                    Assert.Equal(batchesBeforeHandoff, sent.Count(item => item.FromHost && item.Frame is FileTransferChunkBatchFrameV4));
                    Assert.True(Published(sender, outbound, FileTransferRoute.RegularNknV4Fast));
                    Assert.True(Published(receiver, inbound, FileTransferRoute.RegularNknV4Fast));
                    (bool FromHost, bool Regular, NknIncomingMessage Packet)[] packets;
                    lock (activationAcks)
                    {
                        holdActivationAck = false;
                        packets = activationAcks.ToArray();
                        activationAcks.Clear();
                    }
                    Assert.NotEmpty(packets);
                    foreach (var packet in packets)
                    {
                        if (packet.Regular)
                            typeof(FakeNknClient).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .Invoke(packet.FromHost ? helperClient : hostClient, [packet.Packet]);
                        else
                            ((RetryableTunaAccelerationSession)(packet.FromHost ? helperLane : hostLane))
                                .ReceivePeerBytesForTests(packet.Packet.Channel, packet.Packet.Payload);
                    }
                }
                await WaitFor("first Tuna publication", () => Published(sender, outbound, FileTransferRoute.FileTunaV4) &&
                    Published(receiver, inbound, FileTransferRoute.FileTunaV4), regularActivationCase is null ? 8 : 10);
                CapturePublications(FileTransferRoute.FileTunaV4, 1);
                if (regularActivationCase is not null)
                {
                    foreach (var current in new[] { State(sender, outbound), State(receiver, inbound) })
                    {
                        var completed = current.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value;
                        Assert.Equal(FileTransferRouteTransitionKind.RegularV4ToTunaV4, completed.Authority.Kind);
                        Assert.Equal((sessionId, transferId), (completed.Authority.SessionId, completed.Authority.TransferId));
                        Assert.Equal(current.CurrentRoutePublicationToken, completed.RoutePublicationToken);
                        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied, completed.DispositionReceipt!.Value.Status);
                        var drain = Assert.IsType<FileTransferRouteTransitionPhysicalDrainReceipt>(completed.DrainReceipt);
                        Assert.Equal(completed.DrainCommand, drain.Command);
                        Assert.Equal(FileTransferRouteTransitionPhysicalDrainOutcome.Completed, drain.Outcome);
                        Assert.Equal(0, completed.Authority.DurableCheckpoint.ContiguousCommittedChunkIndex);
                        Assert.Equal(current.CurrentDataSessionGeneration, completed.Authority.DataSessionGeneration);
                        Assert.Equal(FileTransferRoute.RegularNknV4Fast, completed.Authority.SourceRoute);
                        Assert.Equal(FileTransferRoute.FileTunaV4, completed.Authority.TargetRoute);
                    }
                    Volatile.Write(ref allowFrontier, 1);
                    await WaitFor("Tuna V4 payload after coordinator handoff", () => sent.Any(item => item.FromHost &&
                        !item.Regular && item.Frame is FileTransferChunkBatchFrameV4 and not FileTransferChunkBatchFrameV6), 10);
                    await WaitFor("activation same-transfer completion", () =>
                        sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                        receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, 25);
                    Assert.Equal(payload, destination.ToArray());
                    Assert.Single(offers);
                    Assert.Single(opens);
                    Assert.Single(sent.Where(item => item.FromHost && item.Frame is FileTransferManifestFrameV4));
                    Assert.Empty(cancels);
                    Assert.DoesNotContain(sent, item => item.Frame is FileTransferManifestFrameV6 or
                        FileTransferChunkBatchFrameV6 or FileTransferCompleteFrameV6);
                    Assert.DoesNotContain("file_tuna_v6", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
                    return;
                }
                if (earlyCheckpointProof || holdPhysicalSend)
                {
                    // The ordering/physical-hold cases need an acknowledged prefix
                    // before holding work. The ordinary cycle keeps its
                    // missing-frontier repair coverage unchanged.
                    Volatile.Write(ref allowFrontier, 1);
                    await WaitFor("prefix committed and acknowledged before checkpoint barrier", () =>
                        receiver.Snapshot.Inbound?.ChunksTransferred >= 69 &&
                        ContextValue(outbound, "RemoteNextExpectedChunkIndex") is int frontier && frontier >= 69);
                }
            }
            var retiredOwner = State(sender, outbound).CurrentRuntimeOwner;
            var retiredInstall = ContextValue(outbound, "InstalledOutboundSparsePump");
            var retiredSession = ContextValue(outbound, "DataSession");
            var capture = typeof(NknSignalingTransport).GetMethod("CaptureFallbackBootstrapLocalStopSources",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.NotEmpty((Array)capture.Invoke(host, ["header_switch_off"])!);
            await (stopReceiver ? helper : host).StopAccelerationAsync("header_switch_off", timeout.Token);
            Assert.Contains(stopEvaluations, evaluation => evaluation.Direction == (stopReceiver ? FileTransferDirection.Inbound : FileTransferDirection.Outbound) &&
                evaluation.InitiationKind == FileTransferBootstrapInitiationKind.LocalRuntimeDisable &&
                evaluation.LocalStop?.Scope.Publication.TransferId == transferId);
            await WaitFor("correlated fallback publication", () =>
                Published(sender, outbound, FileTransferRoute.PostTunaFallbackV6) &&
                Published(receiver, inbound, FileTransferRoute.PostTunaFallbackV6), initialTunaCase is null ? 8 : 5);
            AssertJoinedBootstrapFallbackPublished(sender, outbound, receiver, inbound);
            if (earlyCheckpointProof || holdPhysicalSend)
                Assert.Equal(69, ContextValue(outbound, "PullPostTunaRecoveryFrontierChunkIndex"));
            if (cycle)
            {
                CapturePublications(FileTransferRoute.PostTunaFallbackV6, firstFallbackEpoch);
                Volatile.Write(ref suppressFeedback, 1);
                var currentOwner = State(sender, outbound).CurrentRuntimeOwner;
                var failure = Assert.IsAssignableFrom<Task<bool>>(typeof(SessionFileTransferService)
                    .GetMethod("FailOutboundV4Async", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(sender, [outbound, retiredSession, retiredOwner, "receive_failed",
                        "Injected retired sparse-runtime receive callback.", true, retiredInstall]));
                Assert.False(await failure);
                Assert.Equal(currentOwner, State(sender, outbound).CurrentRuntimeOwner);
                Assert.Equal(FileTransferTransferState.Sending, sender.Snapshot.Outbound!.State);
                Assert.Empty(cancels);
                Volatile.Write(ref suppressFeedback, 0);
            }
            Volatile.Write(ref fallbackPublished, 1);
            var resumedRepairStart = 0;
            if (fallbackRepairCase == FallbackRepairCase.Resume)
            {
                // Pause at the published handoff while the initial frontier is
                // still withheld, not after the tail barrier creates a new
                // unresolved repair that correctly blocks reactivation.
                Assert.NotNull(await sender.PauseTransferAsync(transferId, "fixture_fallback_pause", timeout.Token));
                resumedRepairStart = sent.Count;
                Assert.NotNull(await sender.ResumeTransferAsync(transferId, "fixture_fallback_resume", timeout.Token));
            }
            Volatile.Write(ref allowFrontier, 1);
            if (earlyCheckpointProof)
            {
                await WaitFor("early peer proof retained before checkpoint acceptance", () =>
                    FileTransferCoordinator.GetFallbackTailYieldSubstate(State(sender, outbound))
                        .PendingPeerProofAwaitingCheckpoint is not null, 12);
                var beforeAcceptance = State(sender, outbound);
                var held = FileTransferCoordinator.GetFallbackTailYieldSubstate(beforeAcceptance);
                var earlyProof = Assert.IsType<FileTransferFallbackTailPeerProofReceipt>(held.PendingPeerProofAwaitingCheckpoint);
                Assert.NotNull(beforeAcceptance.CurrentFallbackCheckpointLease);
                Assert.Equal(FileTransferRoute.PostTunaFallbackV6, beforeAcceptance.RouteSelection.Route);
                Assert.Equal(2, beforeAcceptance.CurrentRoutePublicationToken!.Value.LiveRouteEpoch);
                Assert.Null(held.CurrentOperation);
                Assert.Null(held.PendingPeerProofBeforeLocalYield);
                Assert.Null(held.CurrentReservation);
                Assert.Null(held.PendingRetryRecheckRequest);
                Assert.Contains(sent, item => !item.FromHost && item.Frame is FileTransferFallbackTailYieldFrameV6 proof &&
                    SessionFileTransferService.CreateFallbackTailYieldFrameFingerprint(proof) == earlyProof.Proof.FrameFingerprint);
                NknIncomingMessage[] checkpointPackets;
                lock (delayedCheckpoints)
                {
                    holdCheckpoints = false;
                    checkpointPackets = delayedCheckpoints.ToArray();
                    delayedCheckpoints.Clear();
                }
                Assert.NotEmpty(checkpointPackets);
                // Release unchanged encrypted packets through the fake network's
                // normal receive boundary, not directly into Core or the data session.
                foreach (var packet in checkpointPackets)
                    typeof(FakeNknClient).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(hostClient, [packet]);
            }
            var reenableStarted = DateTimeOffset.MinValue;
            if (cycle)
            {
                await WaitFor("first fallback recovery", () => ReadOperationalLogTail(logStart).Split('\n').Any(line =>
                    line.Contains("event=filetransfer_fallback_checkpoint_accepted; direction=outbound;", StringComparison.Ordinal) &&
                    line.Contains($"live_route_epoch={firstFallbackEpoch};", StringComparison.Ordinal)) &&
                    receiver.Snapshot.Inbound?.BytesTransferred > 0, initialTunaCase is null ? 8 : 10);
                if (fallbackRepairCase is not null)
                {
                    var current = State(sender, outbound);
                    var publication = Assert.IsType<RoutePublicationToken>(current.CurrentRoutePublicationToken);
                    Assert.Equal((sessionId, transferId, FileTransferDirection.Outbound,
                        FileTransferRoute.PostTunaFallbackV6, 6),
                        (publication.SessionId, publication.TransferId, publication.Direction,
                            publication.Route, publication.ProtocolVersion));
                    Assert.True(FileTransferCoordinator.IsRoutePublicationTokenCurrent(current, publication, out _));
                    Assert.Equal(current.CurrentLeg!.Generation, publication.LegGeneration);
                    Assert.Equal(current.CurrentRuntimeOwner, publication.RuntimeOwner);
                    Assert.Equal(current.CurrentDataSessionGeneration, ContextValue(outbound, "DataSessionGeneration"));
                    if (fallbackRepairCase is FallbackRepairCase.Resume or FallbackRepairCase.CurrentPeerRange)
                    {
                        await WaitFor("exact current peer range repaired", () => HasCurrentPeerRepair(
                            resumedRepairStart, fallbackRepairCase == FallbackRepairCase.CurrentPeerRange), 5);
                        // Resume ends at its repair proof; the current-range
                        // case also verifies the formerly timed-out route cycle.
                        Assert.True(Published(sender, outbound, FileTransferRoute.PostTunaFallbackV6));
                        Assert.True(Published(receiver, inbound, FileTransferRoute.PostTunaFallbackV6));
                        Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound!.State);
                        Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound!.State);
                        Assert.Single(offers);
                        Assert.Single(opens);
                        Assert.Single(sent.Where(item => item.FromHost && item.Frame is FileTransferManifestFrameV4));
                        Assert.Empty(cancels);
                        if (fallbackRepairCase == FallbackRepairCase.Resume) return;
                    }
                }
                if (holdPhysicalSend)
                {
                    var batch = await physicalSendReached.Task.WaitAsync(TimeSpan.FromSeconds(8));
                    await WaitFor("held fallback physical send lease", () =>
                    {
                        lock (GetPrivateField(sender, "gate")!)
                        {
                            var leases = (System.Collections.IDictionary)ContextValue(outbound, "ActiveOutboundPhysicalSendLeases")!;
                            var lease = leases.Values.Cast<object>().FirstOrDefault(item =>
                                (int)ContextValue(item, "StartChunkIndex")! == batch.Start &&
                                (int)ContextValue(item, "ChunkCount")! == batch.Count &&
                                ContextValue(item, "PhysicalCompletionTask") is Task { IsCompleted: false });
                            if (lease is null) return false;
                            heldPhysicalLease = (long)ContextValue(lease, "LeaseGeneration")!;
                            Assert.Equal(FileTransferRoute.PostTunaFallbackV6,
                                ((RoutePublicationToken)ContextValue(lease, "RoutePublication")!).Route);
                            return true;
                        }
                    });
                    Assert.False(physicalSendFinished.Task.IsCompleted);
                }
                Volatile.Write(ref blockReenabledV4, 1);
                reenableStarted = DateTimeOffset.UtcNow;
                reactivatedLogStart = GetOperationalLogLength();
                reactivatedFrameStart = sent.Count;
                await host.RequestAccelerationNegotiationAsync("runtime_unlock", timeout.Token);
                if (holdPhysicalSend)
                {
                    await WaitFor("transition captures held physical send", () =>
                    {
                        var state = State(sender, outbound);
                        if (state.PersistentState.FileTransferRouteTransition.CurrentOperation is not
                            { DrainCommand: { } drain } operation || !drain.CapturedPhysicalSendLeaseIds.Contains(heldPhysicalLease))
                            return false;
                        heldDrain = drain;
                        Assert.Null(operation.DrainReceipt);
                        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, state.CurrentRoutePublicationToken!.Value.Route);
                        Assert.False(physicalSendFinished.Task.IsCompleted);
                        return true;
                    });
                    releasePhysicalSend.TrySetResult();
                    await physicalSendFinished.Task.WaitAsync(TimeSpan.FromSeconds(8));
                    await WaitFor("held physical send retirement", () =>
                    {
                        lock (GetPrivateField(sender, "gate")!)
                            return !((System.Collections.IDictionary)ContextValue(outbound, "ActiveOutboundPhysicalSendLeases")!)
                                .Contains(heldPhysicalLease);
                    });
                    Assert.Empty(cancels);
                }
                await WaitFor("second Tuna publication", () => Published(sender, outbound, FileTransferRoute.FileTunaV4) &&
                    Published(receiver, inbound, FileTransferRoute.FileTunaV4), initialTunaCase is null ? 8 : 10);
                CapturePublications(FileTransferRoute.FileTunaV4, firstFallbackEpoch + 1);
                if (holdPhysicalSend)
                {
                    var completed = State(sender, outbound).PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value;
                    Assert.Equal(heldDrain, completed.DrainCommand);
                    var receipt = Assert.IsType<FileTransferRouteTransitionPhysicalDrainReceipt>(completed.DrainReceipt);
                    Assert.Equal(heldDrain, receipt.Command);
                    Assert.Contains(heldPhysicalLease, receipt.CompletedPhysicalSendLeaseIds);
                    Assert.Equal(FileTransferRouteTransitionPhysicalDrainOutcome.Completed, receipt.Outcome);
                    Assert.Single(ReadOperationalLogTail(logStart).Split('\n').Where(line =>
                        line.Contains("event=filetransfer_outbound_physical_send_lease_retired;", StringComparison.Ordinal) &&
                        line.Contains($"send_lease_generation={heldPhysicalLease};", StringComparison.Ordinal)));
                }
                // This is a focused ordering regression, not the full-cycle gate
                // below. That gate still requires runtime-start evidence and bytes.
                if (earlyCheckpointProof)
                {
                    AssertEarlyCheckpointProofPromotion(ReadOperationalLogTail(logStart));
                    return;
                }
                if (initialTunaCase is not null)
                    await WaitFor("reenabled Tuna runtime recovery", () => ReadOperationalLogTail(logStart).Split('\n').Any(line =>
                        line.Contains("event=filetransfer_live_route_epoch_recovered; direction=outbound", StringComparison.Ordinal) &&
                        line.Contains($"live_route_epoch={firstFallbackEpoch + 1}; route=file_tuna_v4;", StringComparison.Ordinal)), 10);
                onReactivated?.Invoke(sender, outbound);
                if (secondStop)
                {
                    Assert.NotEmpty((Array)capture.Invoke(helper, ["header_switch_off"])!);
                    await helper.StopAccelerationAsync("header_switch_off", timeout.Token);
                    Assert.Contains(stopEvaluations, evaluation => evaluation.Direction == FileTransferDirection.Inbound &&
                        evaluation.InitiationKind == FileTransferBootstrapInitiationKind.LocalRuntimeDisable &&
                        evaluation.LocalStop?.Scope.Publication.LiveRouteEpoch == firstFallbackEpoch + 1);
                    await WaitFor("second correlated fallback publication", () =>
                        Published(sender, outbound, FileTransferRoute.PostTunaFallbackV6) &&
                        Published(receiver, inbound, FileTransferRoute.PostTunaFallbackV6), initialTunaCase is null ? 8 : 10);
                    AssertJoinedBootstrapFallbackPublished(sender, outbound, receiver, inbound);
                    CapturePublications(FileTransferRoute.PostTunaFallbackV6, firstFallbackEpoch + 2);
                }
                Volatile.Write(ref holdCycleTail, 0);
                Volatile.Write(ref blockReenabledV4, 0);
                if (fallbackRepairCase == FallbackRepairCase.AcceptedTail)
                {
                    var frontier = (int)ContextValue(outbound, "RemoteNextExpectedChunkIndex")!;
                    var publication = State(sender, outbound).CurrentRoutePublicationToken!.Value;
                    await WaitFor("normal send after coordinator Tuna recovery", () => sent.Any(item =>
                        item.FromHost && item.AfterFallback &&
                        item.Frame is FileTransferChunkBatchFrameV4 and not FileTransferChunkBatchFrameV6 &&
                        item.Frame is FileTransferChunkBatchFrameV4 batch &&
                        batch.StartChunkIndex >= frontier &&
                        ReadOperationalLogTail(logStart).Split('\n').Any(line =>
                            line.Contains("event=filetransfer_v4_chunk_batch_sent;", StringComparison.Ordinal) &&
                            line.Contains($"start_chunk_index={batch.StartChunkIndex};", StringComparison.Ordinal) &&
                            line.Contains("repair_send=0;", StringComparison.Ordinal) &&
                            line.Contains($"route_publication_generation={publication.PublicationGeneration};", StringComparison.Ordinal) &&
                            line.Contains("route=file_tuna_v4; protocol_version=4;", StringComparison.Ordinal))), 5);
                    Assert.Contains(ReadOperationalLogTail(logStart).Split('\n'), line =>
                        line.Contains("event=filetransfer_live_route_epoch_recovered; direction=outbound;", StringComparison.Ordinal) &&
                        line.Contains("route=file_tuna_v4; protocol_version=4;", StringComparison.Ordinal));
                }
            }
            if (fallbackRepairCase == FallbackRepairCase.NoLegacyHandoff)
            {
                var publication = State(sender, outbound).CurrentRoutePublicationToken!.Value;
                await WaitFor("peer repair after coordinator Tuna recovery", () =>
                    ReadOperationalLogTail(reactivatedLogStart).Split('\n').Any(line =>
                        line.Contains("event=filetransfer_v4_chunk_batch_sent;", StringComparison.Ordinal) &&
                        line.Contains("repair_send=1;", StringComparison.Ordinal) &&
                        line.Contains($"route_publication_generation={publication.PublicationGeneration};", StringComparison.Ordinal) &&
                        line.Contains("route=file_tuna_v4; protocol_version=4;", StringComparison.Ordinal)), 5);
                var afterRepair = GetOperationalLogLength();
                await WaitFor("normal send after current peer repair", () =>
                    ReadOperationalLogTail(afterRepair).Split('\n').Any(line =>
                        line.Contains("event=filetransfer_v4_chunk_batch_sent;", StringComparison.Ordinal) &&
                        line.Contains("repair_send=0;", StringComparison.Ordinal) &&
                        line.Contains($"route_publication_generation={publication.PublicationGeneration};", StringComparison.Ordinal) &&
                        line.Contains("route=file_tuna_v4; protocol_version=4;", StringComparison.Ordinal)), 5);
                var repairLog = ReadOperationalLogTail(reactivatedLogStart);
                var frames = sent.Skip(reactivatedFrameStart).ToArray();
                Assert.Contains(frames, item =>
                {
                    if (item.FromHost || item.Frame is not FileTransferStateFrameV4 state ||
                        state is FileTransferReceiverStateFrameV6 || state.MissingRanges.Count == 0)
                        return false;
                    var fingerprint = Assert.IsType<string>(typeof(SessionFileTransferService).GetMethod(
                        "CreateFileTransferV4StateWireFingerprint", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, [state]));
                    return repairLog.Split('\n').Any(line =>
                        line.Contains("event=filetransfer_v4_state_received;", StringComparison.Ordinal) &&
                        line.Contains($"v4_state_wire_fingerprint={fingerprint};", StringComparison.Ordinal) &&
                        line.Contains("applied=1;", StringComparison.Ordinal) &&
                        line.Contains($"route_publication_generation={publication.PublicationGeneration};", StringComparison.Ordinal) &&
                        line.Contains("route=file_tuna_v4; protocol_version=4;", StringComparison.Ordinal)) &&
                        frames.Skip(Array.IndexOf(frames, item) + 1).Any(sentItem => sentItem.FromHost &&
                            sentItem.Frame is FileTransferChunkBatchFrameV4 and not FileTransferChunkBatchFrameV6 &&
                            sentItem.Frame is FileTransferChunkBatchFrameV4 batch && state.MissingRanges.Any(range =>
                                batch.StartChunkIndex >= range.StartChunkIndex &&
                                batch.StartChunkIndex + batch.ChunkCount <= range.StartChunkIndex + range.ChunkCount) &&
                            repairLog.Split('\n').Any(line =>
                                line.Contains("event=filetransfer_v4_chunk_batch_sent;", StringComparison.Ordinal) &&
                                line.Contains($"start_chunk_index={batch.StartChunkIndex};", StringComparison.Ordinal) &&
                                line.Contains("repair_send=1;", StringComparison.Ordinal) &&
                                line.Contains($"route_publication_generation={publication.PublicationGeneration};", StringComparison.Ordinal)));
                });
                Assert.Contains(repairLog.Split('\n'), line =>
                    line.Contains("event=filetransfer_v4_chunk_batch_sent;", StringComparison.Ordinal) &&
                    line.Contains("repair_send=1;", StringComparison.Ordinal) &&
                    line.Contains("route=file_tuna_v4; protocol_version=4;", StringComparison.Ordinal) &&
                    line.Contains($"live_route_epoch={firstFallbackEpoch + 1};", StringComparison.Ordinal));
                var repairLines = repairLog.Split('\n');
                var publicationStart = Array.FindIndex(repairLines, line =>
                    line.Contains("event=filetransfer_live_route_epoch_started; direction=outbound;", StringComparison.Ordinal) &&
                    line.Contains($"route_publication_generation={publication.PublicationGeneration};", StringComparison.Ordinal) &&
                    line.Contains("route=file_tuna_v4; protocol_version=4;", StringComparison.Ordinal));
                Assert.True(publicationStart >= 0);
                var currentTunaLog = string.Join('\n', repairLines.Skip(publicationStart));
                Assert.DoesNotContain("event=filetransfer_v6_handoff_epoch_started; direction=outbound", currentTunaLog);
                Assert.DoesNotContain("handoff_kind=regular_nkn_recovery;", currentTunaLog);
            }
            if (fallbackRepairCase == FallbackRepairCase.NoLegacyHandoff)
            {
                // This contract ends at its repair/resumed-send proof. The
                // accepted-tail and current-range cases continue to completion.
                Assert.True(Published(sender, outbound, FileTransferRoute.FileTunaV4));
                Assert.True(Published(receiver, inbound, FileTransferRoute.FileTunaV4));
                Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound!.State);
                Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound!.State);
                Assert.Single(offers);
                Assert.Single(opens);
                Assert.Single(sent.Where(item => item.FromHost && item.Frame is FileTransferManifestFrameV4));
                Assert.Empty(cancels);
                return;
            }
            var completionSeconds = initialTunaCase switch
            {
                InitialTunaRecoveryCase.Off => 90,
                InitialTunaRecoveryCase.OffOn => Math.Max(0, 40 - (DateTimeOffset.UtcNow - reenableStarted).TotalSeconds),
                InitialTunaRecoveryCase.OffOnOff => 30,
                _ => 25,
            };
            await WaitFor("same-transfer completion", () =>
                sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, completionSeconds);
            Assert.Equal(payload, destination.ToArray());
            Assert.Single(offers);
            Assert.Single(opens);
            Assert.Single(sent.Where(item => item.FromHost && item.Frame is FileTransferManifestFrameV4));
            Assert.Empty(cancels);
            if (!cycle)
                Assert.DoesNotContain(sent, item => item.FromHost && item.AfterFallback &&
                    item.Frame is FileTransferChunkBatchFrameV4 and not FileTransferChunkBatchFrameV6);
            Assert.Contains(sent, item => item.FromHost && FileTransferProtocol.IsV6DataFrame(item.Frame));
            Assert.Contains(sent, item => !item.FromHost && item.Frame is FileTransferReceiverStateFrameV6);
            var log = ReadOperationalLogTail(logStart);
            // Correlated transitions publish live-route epochs, not the legacy transport-epoch event.
            foreach (var direction in new[] { "outbound", "inbound" })
                Assert.Contains(log.Split('\n'), line =>
                    line.Contains($"event=filetransfer_live_route_epoch_started; direction={direction};", StringComparison.Ordinal) &&
                    line.Contains($"transfer_id={transferId};", StringComparison.Ordinal) &&
                    line.Contains($"live_route_epoch={firstFallbackEpoch}; previous_route=file_tuna_v4; route=post_tuna_fallback_v6; protocol_version=6;", StringComparison.Ordinal) &&
                    line.Contains("handoff_kind=tuna_to_normal_fallback; target_transport=regular_nkn;", StringComparison.Ordinal));
            Assert.Contains("event=filetransfer_fallback_checkpoint_requested; direction=outbound;", log, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_fallback_checkpoint_accepted; direction=outbound;", log, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_outbound_sparse_worker_handoff_installed;", log, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_post_tuna_fallback_cleanup_completed; direction=outbound;", log, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_live_v4_fallback_cleanup_completed; direction=outbound;", log, StringComparison.Ordinal);
            if (cycle)
            {
                Assert.DoesNotContain("file_tuna_v6", log, StringComparison.Ordinal);
                foreach (var direction in new[] { FileTransferDirection.Outbound, FileTransferDirection.Inbound })
                {
                    var tuna = publications.Where(p => p.Direction == direction && p.Route == FileTransferRoute.FileTunaV4).ToArray();
                    Assert.Equal(2, tuna.Length);
                    Assert.Equal(2, tuna.Select(p => p.PublicationGeneration).Distinct().Count());
                    foreach (var publication in tuna)
                    {
                        if (publication.LiveRouteEpoch > 0)
                        {
                            Assert.Equal(FileTransferCoordinatorActionKind.RouteCommitPublication, publication.ActionKind);
                            Assert.True(publication.RouteCommitGeneration > 0);
                        }
                        var publicationLines = log.Split('\n').Where(line =>
                            line.Contains($"direction={direction.ToString().ToLowerInvariant()};", StringComparison.Ordinal) &&
                            line.Contains($"route_publication_generation={publication.PublicationGeneration};", StringComparison.Ordinal) &&
                            line.Contains($"live_route_epoch={publication.LiveRouteEpoch};", StringComparison.Ordinal)).ToArray();
                        foreach (var eventName in publication.LiveRouteEpoch == 0
                            ? new[] { "route_selected", "runtime_started" }
                            : new[] { "route_selected", "runtime_started", "live_route_epoch_started", "live_route_epoch_recovered" })
                            Assert.True(publicationLines.Any(line => line.Contains($"event=filetransfer_{eventName};", StringComparison.Ordinal)),
                                $"Missing {eventName} for {direction} Tuna epoch {publication.LiveRouteEpoch}, publication {publication.PublicationGeneration}.");
                        if (publication.LiveRouteEpoch == 0) continue;
                        var started = Array.FindIndex(publicationLines, line =>
                            line.Contains("event=filetransfer_live_route_epoch_started;", StringComparison.Ordinal));
                        var recovered = Array.FindIndex(publicationLines, started + 1, line =>
                            line.Contains("event=filetransfer_live_route_epoch_recovered;", StringComparison.Ordinal));
                        Assert.True(recovered > started, "Tuna recovery must follow its route-start evidence.");
                        SessionFileTransferV4SenderTests.AssertSameRoutePublicationTuple(
                            publicationLines[started], publicationLines[recovered]);
                    }
                }
                Assert.Contains(log.Split('\n'), line =>
                    line.Contains("event=filetransfer_runtime_owner_stale_update_rejected; direction=outbound;", StringComparison.Ordinal) &&
                    line.Contains("source=v4_sender_failed", StringComparison.Ordinal) &&
                    line.Contains("reason=runtime_generation_mismatch", StringComparison.Ordinal));
                var terminalProjectionDiagnostics = JsonSerializer.Serialize(new
                {
                    Host = TerminalProjectionState(host, sender, outbound),
                    Helper = TerminalProjectionState(helper, receiver, inbound),
                });
                foreach (var direction in new[] { "outbound", "inbound" })
                    foreach (var transitionGeneration in secondStop
                        ? new[] { firstFallbackEpoch, firstFallbackEpoch + 2 } : new[] { firstFallbackEpoch })
                        Assert.Single(log.Split('\n').Where(line =>
                            line.Contains("event=filetransfer_post_tuna_fallback_cleanup_completed;", StringComparison.Ordinal) &&
                            line.Contains($"transfer_id={transferId};", StringComparison.Ordinal) &&
                            line.Contains($"direction={direction};", StringComparison.Ordinal) &&
                            line.Contains($"transition_generation={transitionGeneration};", StringComparison.Ordinal)));
                Assert.Equal(false, GetPrivateField(host, "fileTransferFallbackProofPending"));
                Assert.Equal(false, GetPrivateField(helper, "fileTransferFallbackProofPending"));
                var finalRoute = secondStop ? FileTransferRoute.PostTunaFallbackV6 : FileTransferRoute.FileTunaV4;
                Assert.Equal(finalRoute, State(sender, outbound).CurrentLeg!.RouteSelection.Route);
                Assert.Equal(finalRoute, State(receiver, inbound).CurrentLeg!.RouteSelection.Route);
                Assert.False(host.IsPostTunaFileFallbackActiveForRouteSelection, terminalProjectionDiagnostics);
                Assert.False(helper.IsPostTunaFileFallbackActiveForRouteSelection, terminalProjectionDiagnostics);
                foreach (var direction in new[] { FileTransferDirection.Outbound, FileTransferDirection.Inbound })
                {
                    Assert.Contains(terminalProjectionObservations, observation => observation.Direction == direction);
                    Assert.All(terminalProjectionObservations.Where(observation => observation.Direction == direction),
                        observation => Assert.False(observation.FallbackActive, terminalProjectionDiagnostics));
                    if (secondStop) Assert.Single(log.Split('\n').Where(line =>
                        line.Contains("event=filetransfer_fallback_projection_terminal_retired;", StringComparison.Ordinal) &&
                        line.Contains($"transfer_id={transferId};", StringComparison.Ordinal) &&
                        line.Contains($"direction={direction.ToString().ToLowerInvariant()};", StringComparison.Ordinal)));
                }
                Volatile.Write(ref captureFrames, 0);
                if (!secondStop)
                {
                    // Use the readiness retained by the actual reactivation;
                    // do not inject a next-transfer seed or a new preparation.
                    await NextTransfer("joined-initial-tuna-next-active", FileTransferRouteResolver.FileTunaV4Token);
                    await host.StopAccelerationAsync("fixture_switch_off_after_completion", timeout.Token);
                    await helper.StopAccelerationAsync("fixture_switch_off_after_completion", timeout.Token);
                }
                await NextTransfer("joined-regular-tuna-cycle-next", FileTransferRouteResolver.RegularNknV4FastToken);
            }

            async Task NextTransfer(string nextTransferId, string expectedRoute)
            {
                var nextPayload = initialTunaCase is null ? payload[..1024] :
                    Enumerable.Range(0, 128_000).Select(static index => (byte)(index % 251)).ToArray();
                using var nextDestination = new MemoryStream();
                await sender.TryStartSendAsync(new("next.bin", nextPayload.Length, nextTransferId),
                    _ => Task.FromResult<Stream>(new MemoryStream(nextPayload, writable: false)), timeout.Token);
                await WaitFor("next regular offer", () => receiver.Snapshot.Inbound is
                    { State: FileTransferTransferState.PendingDecision } incoming && incoming.TransferId == nextTransferId,
                    initialTunaCase is null ? 8 : 5);
                await receiver.AcceptIncomingTransferAsync(nextTransferId, (_, _) => Task.FromResult<Stream>(nextDestination), timeout.Token);
                await WaitFor("next regular completion", () => sender.Snapshot.Outbound is
                    { State: FileTransferTransferState.Completed } completed && completed.TransferId == nextTransferId &&
                    receiver.Snapshot.Inbound is { State: FileTransferTransferState.Completed } received &&
                    received.TransferId == nextTransferId, initialTunaCase is null ? 8 : 20);
                Assert.Equal(nextPayload, nextDestination.ToArray());
                var nextOffer = Assert.Single(offers.Where(offer => offer.TransferId == nextTransferId));
                Assert.Equal(expectedRoute, nextOffer.FileTransferRoute);
                Assert.Equal(4, nextOffer.PreferredDataProtocolVersion);
            }

            bool HasCurrentPeerRepair(int afterFrame, bool requireAdvancedRange)
            {
                var frames = sent.ToArray();
                // Observe actual authenticated peer requests; neither a transport
                // switch nor a synthetic safety-replay ID grants repair authority.
                for (var index = frames.Length - 1; index >= 0; index--)
                {
                    var item = frames[index];
                    if (item.FromHost || !item.AfterFallback) continue;
                    var request = item.Frame switch
                    {
                        FileTransferFrontierRequestFrameV6 frontier => (
                            frontier.TransportEpoch, frontier.RepairRequestId, frontier.MissingRanges),
                        FileTransferReceiverStateFrameV6 state => (
                            state.TransportEpoch, state.RepairRequestId, state.MissingRanges),
                        _ => default,
                    };
                    if (string.IsNullOrWhiteSpace(request.RepairRequestId) ||
                        request.MissingRanges is not { Count: > 0 }) continue;
                    if (requireAdvancedRange && request.MissingRanges[0].StartChunkIndex == 0) return false;
                    return frames.Skip(Math.Max(index + 1, afterFrame)).Any(candidate =>
                        candidate.FromHost && candidate.Frame is FileTransferChunkBatchFrameV6 batch &&
                        batch.SessionId == sessionId && batch.TransferId == transferId &&
                        batch.TransportEpoch == request.TransportEpoch &&
                        batch.RepairRequestId == request.RepairRequestId &&
                        request.MissingRanges.Any(range => batch.StartChunkIndex >= range.StartChunkIndex &&
                            batch.StartChunkIndex + batch.ChunkCount <= range.StartChunkIndex + range.ChunkCount));
                }
                return false;
            }

            void CapturePublications(FileTransferRoute route, int epoch)
            {
                foreach (var state in new[] { State(sender, outbound), State(receiver, inbound) })
                {
                    var token = state.CurrentRoutePublicationToken!.Value;
                    Assert.Equal((route, epoch, route == FileTransferRoute.FileTunaV4 ? 4 : 6),
                        (token.Route, token.LiveRouteEpoch, token.ProtocolVersion));
                    publications.Add(token);
                }
            }

            void ObserveStop(SessionFileTransferService service, string contextName,
                FileTransferFallbackV6ProofBootstrapEvaluation evaluation)
            {
                stopEvaluations.Enqueue(evaluation);
                if (evaluation.LocalStop is not { } stop || GetPrivateField(service, contextName) is not { } context) return;
                var state = State(service, context);
                var session = ContextValue(context, "DataSession");
                stopDiagnostics.Enqueue(new
                {
                    evaluation.Direction, evaluation.InitiationKind, Captured = stop.Scope,
                    Current = FileTransferCoordinator.CaptureFallbackBootstrapSourceScope(state, stop.Scope.Publication.CoordinatorDecisionReason),
                    SameDataSession = ReferenceEquals(stop.DataSession, session),
                    stop.ProviderGeneration, CurrentProviderGeneration = (session as IFileTransferDataSessionGenerationProvider)?.Generation,
                });
            }

            async Task ObserveRegular(bool fromHost, NknBridgeChannel lane, byte[] bytes, CancellationToken ct)
            {
                if (Observe(fromHost, lane, bytes, regular: true)) { droppedRegular.TryAdd(bytes, 0); }
                else if (initialTunaCase is not null && cycle && fromHost &&
                    EnvelopeCodec.TryDeserialize(bytes, out var delayed) && delayed.Type == MsgType.FileTransferDataFrame)
                    await Task.Delay(2, ct);
                if (ReferenceEquals(bytes, heldPhysicalPayload))
                {
                    try { await releasePhysicalSend.Task.WaitAsync(ct); }
                    finally { physicalSendFinished.TrySetResult(); }
                }
            }
            bool Observe(bool fromHost, NknBridgeChannel lane, byte[] bytes, bool regular = false)
            {
                if (Volatile.Read(ref captureFrames) == 0) { return false; }
                Assert.True(EnvelopeCodec.TryDeserialize(bytes, out var envelope));
                if (envelope.Type != MsgType.FileTransferDataFrame) { return false; }
                var recipient = fromHost ? helper : host;
                var sendingTransport = fromHost ? host : helper;
                var sendingClient = fromHost ? hostClient : helperClient;
                var expectedSender = lane == NknBridgeChannel.Bulk ? sendingClient.BulkAddress : sendingTransport.LocalPeerAddress;
                var secure = SessionSecureEnvelopeCodec.Decrypt(
                    Assert.IsType<byte[]>(GetPrivateField(recipient, "fileTransferSessionSharedKey")), envelope.Payload,
                    new SessionSecureEnvelopeExpectation(SessionSecureMessageFamily.FileTransfer, "file_transfer_data_frame",
                        new SessionId(sessionId), new PeerAddress(expectedSender), transferId));
                Assert.True(FileTransferDataFrameCodec.TryDeserialize(secure.Plaintext, out var frame) ||
                    FileTransferDataFrameCodec.TryDeserializeLegacyV4(secure.Plaintext, out frame));
                Assert.Equal((sessionId, transferId), (frame!.SessionId, frame.TransferId));
                sent.Enqueue((fromHost, frame, Volatile.Read(ref fallbackPublished) != 0, regular));
                if (frame is FileTransferRuntimeUnlockPreCommitProbeAckFrame)
                {
                    lock (activationAcks)
                    {
                        if (holdActivationAck)
                        {
                            activationAcks.Add((fromHost, regular,
                                new NknIncomingMessage(expectedSender, bytes.ToArray(), false, null, lane)));
                            return true;
                        }
                    }
                }
                if (fromHost && frame is FileTransferManifestFrameV4 manifest)
                    Volatile.Write(ref payloadTailBarrier, Math.Max(70, manifest.ChunkCount - 3));
                if (holdPhysicalSend && regular && fromHost &&
                    frame is FileTransferChunkBatchFrameV6 held && held.StartChunkIndex + held.DataSegments.Count >= 73 &&
                    Interlocked.CompareExchange(ref physicalSendSelected, 1, 0) == 0)
                {
                    heldPhysicalPayload = bytes;
                    physicalSendReached.TrySetResult((held.StartChunkIndex, held.DataSegments.Count));
                }
                if (regular && !fromHost && frame is FileTransferReceiverStateFrameV6)
                {
                    lock (delayedCheckpoints)
                    {
                        if (holdCheckpoints)
                        {
                            delayedCheckpoints.Add(new NknIncomingMessage(expectedSender, bytes.ToArray(), false, null, lane));
                            return true;
                        }
                    }
                }
                if (!fromHost && Volatile.Read(ref suppressFeedback) != 0 && frame is FileTransferStateFrameV4 &&
                    frame is not FileTransferReceiverStateFrameV6 { RecoveryMode: "regular_nkn_state_refresh" })
                    return true;
                if (fromHost && Volatile.Read(ref blockReenabledV4) != 0 &&
                    frame is FileTransferChunkBatchFrameV4 and not FileTransferChunkBatchFrameV6)
                    return true;
                // Retained V4 tail bytes also count toward completion after fallback;
                // keep this payload barrier across both wire versions until the second stop.
                // The ordering/physical-hold variants need one genuine fallback batch to
                // settle its frontier recovery; withhold only the final batch then.
                // V6 can reach this boundary before the test observes both publications.
                // Do not drop the physical-hold case's prerequisite fallback batch in that gap.
                var heldTailEnd = fallbackRepairCase is FallbackRepairCase.AcceptedTail or FallbackRepairCase.CurrentPeerRange
                    ? Volatile.Read(ref payloadTailBarrier)
                    : (holdPhysicalSend && frame is FileTransferChunkBatchFrameV6) ||
                      (earlyCheckpointProof && Volatile.Read(ref fallbackPublished) != 0) ? 73 : 70;
                if (fromHost && Volatile.Read(ref holdCycleTail) != 0 && frame is FileTransferChunkBatchFrameV4 tail &&
                    tail.StartChunkIndex + tail.DataSegments.Count >= heldTailEnd)
                    return true;
                // Keep the payload frontier missing until fallback disposition is applied.
                // Bootstrap and all feedback still travel normally.
                if (fromHost && Volatile.Read(ref allowFrontier) == 0 &&
                    frame is FileTransferChunkBatchFrameV4 frontierBatch && frontierBatch.StartChunkIndex == withheldFrontier)
                {
                    Interlocked.Increment(ref droppedFrontierBatches);
                    return true;
                }
                return false;
            }
            async Task<bool> Forward(bool fromHost, INknTunaAccelerationSession target,
                NknBridgeChannel lane, byte[] bytes, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (!target.IsAvailable) { return false; }
                if (Observe(fromHost, lane, bytes)) { return true; }
                if (initialTunaCase is not null && cycle && fromHost) await Task.Delay(2, ct);
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                if (target is PreparationHandshakeLane prepared) prepared.ReceivePeerBytesForTests(lane, bytes);
                else ((RetryableTunaAccelerationSession)target).ReceivePeerBytesForTests(lane, bytes);
                return true;
            }
            async Task WaitFor(string edge, Func<bool> predicate, double seconds = 8)
            {
                try { await WaitUntilAsync(predicate, TimeSpan.FromSeconds(seconds)); }
                catch (TimeoutException error)
                {
                    throw new TimeoutException($"Joined route recovery edge: {edge}; sender={sender.Snapshot.Outbound}; receiver={receiver.Snapshot.Inbound}" +
                        Environment.NewLine + "Frontier feedback scope: " + JsonSerializer.Serialize(new
                        {
                            Sender = FrontierFeedbackState(sender, outboundDirection: true),
                            Receiver = FrontierFeedbackState(receiver, outboundDirection: false),
                        }) +
                        Environment.NewLine + "Local-stop scope observations: " + System.Text.Json.JsonSerializer.Serialize(stopDiagnostics) +
                        Environment.NewLine + string.Join(Environment.NewLine, ReadOperationalLogTail(logStart).Split('\n')
                            .Where(line => !line.Contains("snapshot_payload=", StringComparison.Ordinal)).TakeLast(35)), error);
                }
            }
        }
        finally
        {
            releasePhysicalSend.TrySetResult();
            timeout.Cancel();
            SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests = previousSendTimeout;
            FakeNknClient.ResetNetwork();
        }

        static FileTransferCoordinatorState State(SessionFileTransferService service, object context)
            => SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
        static object? ContextValue(object context, string property)
            => context.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(context);
        static object? FrontierFeedbackState(SessionFileTransferService service, bool outboundDirection)
        {
            lock (GetPrivateField(service, "gate")!)
            {
                var context = GetPrivateField(service, outboundDirection ? "outboundTransfer" : "inboundTransfer");
                if (context is null) return null;
                var state = State(service, context);
                var yield = FileTransferCoordinator.GetFallbackTailYieldSubstate(state).CurrentOperation;
                return new
                {
                    state.CurrentRoutePublicationToken,
                    state.CurrentDataSessionGeneration,
                    state.CurrentFallbackCheckpointLease,
                    YieldPairing = yield?.PairingState.ToString(),
                    HasLocalYieldProof = yield?.LocalProof is not null,
                    HasPeerYieldProof = yield?.PeerProof is not null,
                    RecoveryActive = ContextValue(context, "PullPostTunaRecoveryActive"),
                    RepairActive = outboundDirection ? ContextValue(context, "PullTransportFrontierOnlyRepairActive") : null,
                    RepairStart = outboundDirection ? ContextValue(context, "PullTransportFrontierOnlyRepairStartChunkIndex") : null,
                    HasFrontierContract = ContextValue(context, "CurrentFallbackFrontierRepairContract") is not null,
                    HasPendingYieldRecheck = outboundDirection && ContextValue(context, "PendingFallbackTailPeerFirstYieldUnblockRecheck") is not null,
                    PhysicalSendCount = outboundDirection
                        ? ((System.Collections.IDictionary)ContextValue(context, "ActiveOutboundPhysicalSendLeases")!).Count : 0,
                    Committed = ContextValue(context, outboundDirection ? "RemoteNextExpectedChunkIndex" : "NextChunkIndex"),
                    LastStateCommitted = outboundDirection ? null : ContextValue(context, "V6LastReceiverStateCommittedChunkIndex"),
                    LastStateSentUtc = outboundDirection ? null : ContextValue(context, "V6LastReceiverStateSentUtc"),
                };
            }
        }
        static object TerminalProjectionState(NknSignalingTransport transport, SessionFileTransferService service, object context)
        {
            var state = State(service, context);
            var projection = GetPrivateField(transport, "tunaFallbackProofState");
            var shadow = GetPrivateField(transport, "fileTransferRouteTransitionExecutionShadow")!;
            var input = FileTransferRouteResolverInput.FromTransport(transport);
            return new
            {
                state.IsTerminal,
                state.TerminalGeneration,
                HasCurrentPublication = state.CurrentRoutePublicationToken is not null,
                LegGeneration = state.CurrentLeg?.Generation,
                LegRoute = state.CurrentLeg?.RouteSelection.TelemetryToken,
                LegTransportEpoch = state.CurrentLeg?.TransportEpochId,
                state.CurrentDataSessionGeneration,
                input.IsPostTunaFileFallbackActive,
                NextRouteCandidate = FileTransferRouteResolver.Resolve(input with { IsFileTunaActive = false }).TelemetryToken,
                ProjectionPublication = projection is null ? null : ContextValue(projection, "FileProjectionPublication"),
                ProjectionRetired = projection is not null && ContextValue(projection, "IsFileProjectionRetired") is true,
                FallbackEpoch = projection is null ? null : ContextValue(projection, "Epoch"),
                TransportEpoch = projection is null ? null : ContextValue(projection, "FileV6TransportEpoch"),
                ProjectionDataSessionGeneration = projection is null ? null : ContextValue(projection, "FileProjectionDataSessionGeneration"),
                FileState = projection is null ? null : ContextValue(projection, "FileState")?.ToString(),
                HasCurrentTransition = ContextValue(shadow, "Current") is not null,
                HasRetainedPredecessor = ContextValue(shadow, "RetainedPredecessor") is not null,
                HasPendingAvailability = ContextValue(shadow, "PendingAvailabilityDisposition") is not null,
                ProofPending = GetPrivateField(transport, "fileTransferFallbackProofPending"),
                ProofGeneration = GetPrivateField(transport, "fileTransferFallbackProofGeneration"),
            };
        }
        static void BindForward(INknTunaAccelerationSession lane, Func<NknBridgeChannel, byte[], CancellationToken, Task<bool>> forward)
        {
            if (lane is PreparationHandshakeLane prepared) prepared.ForwardBytesForTests = forward;
            else ((RetryableTunaAccelerationSession)lane).ForwardBytesForTests = forward;
        }
        static bool Published(SessionFileTransferService service, object context, FileTransferRoute route)
            => State(service, context) is
                { CurrentRoutePublicationToken: { } publication } state && publication.Route == route &&
                FileTransferCoordinator.IsRoutePublicationTokenCurrent(state, publication, out _) &&
                (publication.LiveRouteEpoch == 0 ||
                 state.PersistentState.FileTransferRouteTransition.LastTerminalOperation is
                    { DispositionReceipt.Status: FileTransferRouteTransitionDispositionApplyStatus.Applied } terminal &&
                 terminal.RoutePublicationToken == publication);
    }

    private static void AssertEarlyCheckpointProofPromotion(string log)
    {
        static string Field(string line, string name) => line.Split(';').Select(part => part.Trim())
            .Single(part => part.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..];
        static string? Text(JsonElement value, string name) => value.GetProperty(name).GetString();
        static JsonElement Operation(JsonElement envelope, string role, string? kind = null) => Assert.Single(
            envelope.GetProperty("aggregate").GetProperty("operations").EnumerateArray(),
            item => Text(item, "operationRole") == role && (kind is null || Text(item, "operationKind") == kind));

        var lines = log.Split('\n');
        var snapshots = lines.Where(line =>
                line.Contains("event=filetransfer_coordinator_transition_snapshot;", StringComparison.Ordinal) &&
                line.Contains("direction=outbound;", StringComparison.Ordinal))
            .Select(line =>
            {
                var payload = Field(line, "snapshot_payload").Replace('-', '+').Replace('_', '/');
                using var document = JsonDocument.Parse(Convert.FromBase64String(
                    payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
                return (Envelope: document.RootElement.Clone(), Sequence: long.Parse(Field(line, "log_writer_sequence")));
            }).ToArray();
        var retained = Assert.Single(snapshots, entry => Text(entry.Envelope, "decisionReason") ==
            "fallback_tail_checkpoint_pending_peer_proof_retained");
        var accepted = Assert.Single(snapshots, entry => Text(entry.Envelope, "decisionKind") == "FallbackCheckpointAccepted" &&
            Text(entry.Envelope, "decisionStatus") == "Accepted");
        var yielded = Assert.Single(snapshots, entry => Text(entry.Envelope, "decisionKind") == "FallbackTailQuiescent" &&
            Text(entry.Envelope, "decisionStatus") == "Accepted");
        Assert.True(retained.Sequence < accepted.Sequence && accepted.Sequence < yielded.Sequence);
        Assert.Equal(Text(retained.Envelope, "coordinatorInstanceId"), Text(accepted.Envelope, "coordinatorInstanceId"));
        Assert.Equal(Text(retained.Envelope, "coordinatorInstanceId"), Text(yielded.Envelope, "coordinatorInstanceId"));

        var held = Operation(retained.Envelope, "peer_proof_awaiting_checkpoint");
        var retired = Operation(accepted.Envelope, "peer_proof_awaiting_checkpoint");
        var promoted = Operation(accepted.Envelope, "peer_proof_pending_initial");
        var consumption = Assert.Single(snapshots, entry =>
            entry.Sequence > accepted.Sequence && entry.Sequence <= yielded.Sequence &&
            Text(entry.Envelope, "coordinatorInstanceId") == Text(retained.Envelope, "coordinatorInstanceId") &&
            entry.Envelope.GetProperty("aggregate").GetProperty("operations").EnumerateArray().Any(operation =>
                Text(operation, "operationRole") == "peer_proof_pending_initial" &&
                Text(operation, "receiptIdentity") == Text(held, "receiptIdentity") &&
                Text(operation, "afterState") == "None"));
        var consumed = Operation(consumption.Envelope, "peer_proof_pending_initial");
        var current = Operation(yielded.Envelope, "yield_current", "Quiescent");
        if (consumption.Sequence != yielded.Sequence)
        {
            // A failed yield may consume the proof before an exact coordinator-issued
            // quiescent successor carries it forward. Validate that complete handoff.
            Assert.Equal("FallbackTailFailed", Text(consumption.Envelope, "decisionKind"));
            var failed = Operation(consumption.Envelope, "yield_current", "Failed");
            var removed = Operation(yielded.Envelope, "yield_current", "Failed");
            var terminal = Operation(yielded.Envelope, "yield_terminal", "Failed");
            Assert.Equal("PeerProofObserved", Text(failed, "afterState"));
            Assert.Equal("PeerProofObserved", Text(removed, "beforeState"));
            Assert.Equal("None", Text(removed, "afterState"));
            Assert.Equal("Invalidated", Text(terminal, "afterState"));
            Assert.Equal(Text(failed, "authorityId"), Text(removed, "authorityId"));
            Assert.Equal(Text(failed, "authorityId"), Text(terminal, "authorityId"));
            Assert.Equal(Text(failed, "authorityId"), Text(current.GetProperty("fallbackTailYield"), "failedPredecessorAuthorityId"));
            Assert.True(current.GetProperty("fallbackTailYield").GetProperty("failedSupersessionCarriedPeerProof").GetBoolean());
            Assert.Equal("failed_fallback_tail_yield_superseded_by_quiescence",
                Text(terminal.GetProperty("fallbackTailYield"), "invalidationReason"));
            foreach (var operation in new[] { failed, removed, terminal })
                foreach (var field in new[] { "peerProofIdentity", "peerProofFingerprint", "sourceBoundary", "effectiveBoundary" })
                    Assert.Equal(current.GetProperty("fallbackTailYield").GetProperty(field).GetRawText(),
                        operation.GetProperty("fallbackTailYield").GetProperty(field).GetRawText());
            var supersession = Assert.Single(current.GetProperty("fallbackTailYield").GetProperty("receipts").EnumerateArray(),
                receipt => Text(receipt, "stage") == "failed_supersession");
            Assert.Equal("Superseded", Text(supersession, "receiptStatus"));
            Assert.True(supersession.GetProperty("receiptValidated").GetBoolean());
        }
        Assert.Equal("None", Text(held, "beforeState"));
        Assert.Equal("Pending", Text(held, "afterState"));
        Assert.Equal("None", Text(retired, "afterState"));
        Assert.Equal("Pending", Text(promoted, "afterState"));
        Assert.Equal("None", Text(consumed, "afterState"));
        Assert.Equal("PeerProofObserved", Text(current, "afterState"));
        foreach (var operation in new[] { retired, promoted, consumed })
            Assert.Equal(Text(held, "receiptIdentity"), Text(operation, "receiptIdentity"));
        foreach (var operation in new[] { retired, promoted, consumed, current })
            foreach (var field in new[] { "peerProofIdentity", "peerProofFingerprint" })
                Assert.Equal(Text(held.GetProperty("fallbackTailYield"), field),
                    Text(operation.GetProperty("fallbackTailYield"), field));
        var observation = Assert.Single(lines, line =>
            line.Contains("effect_kind=peer_proof_observed;", StringComparison.Ordinal) &&
            line.Contains($"coordinator_instance_id={Text(retained.Envelope, "coordinatorInstanceId")};", StringComparison.Ordinal) &&
            line.Contains($"receipt_identity={Text(held, "receiptIdentity")};", StringComparison.Ordinal));
        Assert.Equal(Text(retained.Envelope, "transitionId"), Field(observation, "transition_id"));
        Assert.Equal(Text(held, "receiptFingerprint"), Field(observation, "receipt_fingerprint"));
        Assert.True(long.Parse(Field(observation, "log_writer_sequence")) > retained.Sequence);
    }
}
