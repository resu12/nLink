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

    private async Task RunJoinedRouteRecoveryAsync(bool cycle, bool earlyCheckpointProof = false,
        bool holdPhysicalSend = false)
    {
        var transferId = cycle ? "joined-regular-tuna-cycle" : "joined-initial-tuna-switch-off";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(cycle ? 80 : 40));
        var previousSendTimeout = SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests;
        if (!cycle)
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
            using INknTunaAccelerationSession hostLane = cycle
                ? new RetryableTunaAccelerationSession(true, 0, supportedLanes: NknAccelerationLaneKind.File)
                : new PreparationHandshakeLane();
            using INknTunaAccelerationSession helperLane = cycle
                ? new RetryableTunaAccelerationSession(false, 0, supportedLanes: NknAccelerationLaneKind.File)
                : new PreparationHandshakeLane { CanListen = false };
            if (!cycle)
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
            var sent = new ConcurrentQueue<(bool FromHost, FileTransferDataFrame Frame, bool AfterFallback)>();
            var droppedRegular = new ConcurrentDictionary<byte[], byte>();
            var delayedCheckpoints = new List<NknIncomingMessage>();
            var holdCheckpoints = earlyCheckpointProof;
            var offers = new ConcurrentQueue<FileTransferOfferV2>();
            var opens = new ConcurrentQueue<FileTransferSessionOpenV2>();
            var cancels = new ConcurrentQueue<FileTransferCancelV1>();
            var stopEvaluations = new ConcurrentQueue<FileTransferFallbackV6ProofBootstrapEvaluation>();
            var stopDiagnostics = new ConcurrentQueue<object>();
            var allowFrontier = 0;
            var fallbackPublished = 0;
            var holdCycleTail = cycle ? 1 : 0;
            var suppressFeedback = 0;
            var blockReenabledV4 = 0;
            var captureFrames = 1;
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

            if (!cycle)
            {
                await host.RequestAccelerationNegotiationAsync("runtime_unlock", timeout.Token);
                await WaitFor("initial Tuna readiness", () =>
                    sender.ObserveSessionPreparationForDiagnostics(sessionId, FileTransferDirection.Outbound, DateTimeOffset.UtcNow).Ready &&
                    receiver.ObserveSessionPreparationForDiagnostics(sessionId, FileTransferDirection.Inbound, DateTimeOffset.UtcNow).Ready &&
                    (long)GetPrivateField(host, "accelerationNegotiationSlotGeneration")! == 0 &&
                    (long)GetPrivateField(helper, "accelerationNegotiationSlotGeneration")! == 0);
            }
            var payload = Enumerable.Range(0, cycle ? 1_572_864 : 768_000).Select(index => (byte)(index % (cycle ? 239 : 233))).ToArray();
            using var destination = new MemoryStream();
            await sender.TryStartSendAsync(new("joined-route-recovery.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)), timeout.Token);
            await WaitFor("offer", () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            var outbound = GetPrivateField(sender, "outboundTransfer")!;
            var inbound = GetPrivateField(receiver, "inboundTransfer")!;
            await receiver.AcceptIncomingTransferAsync(transferId,
                (_, _) => Task.FromResult<Stream>(destination), timeout.Token);
            var initialRoute = cycle ? FileTransferRoute.RegularNknV4Fast : FileTransferRoute.FileTunaV4;
            await WaitFor("initial runtime and frontier feedback", () =>
                Published(sender, outbound, initialRoute) && Published(receiver, inbound, initialRoute) &&
                sent.Any(item => !item.FromHost && item.Frame is FileTransferStateFrameV4 state &&
                    state.MissingRanges.Any(range => range.StartChunkIndex == 0 && range.ChunkCount > 0)));
            AssertJoinedBootstrapAgreement(sender, outbound, receiver, inbound, sessionId, host, helper);
            Assert.Equal(cycle ? FileTransferRouteResolver.RegularNknV4FastToken : FileTransferRouteResolver.FileTunaV4Token,
                Assert.Single(offers).FileTransferRoute);
            Assert.Equal(4, Assert.Single(offers).PreferredDataProtocolVersion);
            if (cycle)
            {
                await host.RequestAccelerationNegotiationAsync("runtime_unlock", timeout.Token);
                await WaitFor("first Tuna publication", () => Published(sender, outbound, FileTransferRoute.FileTunaV4) &&
                    Published(receiver, inbound, FileTransferRoute.FileTunaV4));
                CapturePublications(FileTransferRoute.FileTunaV4, 1);
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
            await host.StopAccelerationAsync("header_switch_off", timeout.Token);
            Assert.Contains(stopEvaluations, evaluation => evaluation.Direction == FileTransferDirection.Outbound &&
                evaluation.InitiationKind == FileTransferBootstrapInitiationKind.LocalRuntimeDisable &&
                evaluation.LocalStop?.Scope.Publication.TransferId == transferId);
            await WaitFor("correlated fallback publication", () =>
                Published(sender, outbound, FileTransferRoute.PostTunaFallbackV6) &&
                Published(receiver, inbound, FileTransferRoute.PostTunaFallbackV6));
            AssertJoinedBootstrapFallbackPublished(sender, outbound, receiver, inbound);
            if (earlyCheckpointProof || holdPhysicalSend)
                Assert.Equal(69, ContextValue(outbound, "PullPostTunaRecoveryFrontierChunkIndex"));
            if (cycle)
            {
                CapturePublications(FileTransferRoute.PostTunaFallbackV6, 2);
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
            if (cycle)
            {
                await WaitFor("first fallback recovery", () => ReadOperationalLogTail(logStart).Split('\n').Any(line =>
                    line.Contains("event=filetransfer_fallback_checkpoint_accepted; direction=outbound;", StringComparison.Ordinal) &&
                    line.Contains("live_route_epoch=2;", StringComparison.Ordinal)) &&
                    receiver.Snapshot.Inbound?.BytesTransferred > 0);
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
                    Published(receiver, inbound, FileTransferRoute.FileTunaV4));
                CapturePublications(FileTransferRoute.FileTunaV4, 3);
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
                Assert.NotEmpty((Array)capture.Invoke(helper, ["header_switch_off"])!);
                await helper.StopAccelerationAsync("header_switch_off", timeout.Token);
                Assert.Contains(stopEvaluations, evaluation => evaluation.Direction == FileTransferDirection.Inbound &&
                    evaluation.InitiationKind == FileTransferBootstrapInitiationKind.LocalRuntimeDisable &&
                    evaluation.LocalStop?.Scope.Publication.LiveRouteEpoch == 3);
                await WaitFor("second correlated fallback publication", () =>
                    Published(sender, outbound, FileTransferRoute.PostTunaFallbackV6) &&
                    Published(receiver, inbound, FileTransferRoute.PostTunaFallbackV6));
                AssertJoinedBootstrapFallbackPublished(sender, outbound, receiver, inbound);
                CapturePublications(FileTransferRoute.PostTunaFallbackV6, 4);
                Volatile.Write(ref holdCycleTail, 0);
                Volatile.Write(ref blockReenabledV4, 0);
            }
            await WaitFor("same-transfer completion", () =>
                sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, 25);
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
                    line.Contains($"live_route_epoch={(cycle ? 2 : 1)}; previous_route=file_tuna_v4; route=post_tuna_fallback_v6; protocol_version=6;", StringComparison.Ordinal) &&
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
                        Assert.Equal(FileTransferCoordinatorActionKind.RouteCommitPublication, publication.ActionKind);
                        Assert.True(publication.RouteCommitGeneration > 0);
                        var publicationLines = log.Split('\n').Where(line =>
                            line.Contains($"direction={direction.ToString().ToLowerInvariant()};", StringComparison.Ordinal) &&
                            line.Contains($"route_publication_generation={publication.PublicationGeneration};", StringComparison.Ordinal) &&
                            line.Contains($"live_route_epoch={publication.LiveRouteEpoch};", StringComparison.Ordinal)).ToArray();
                        foreach (var eventName in new[] { "route_selected", "runtime_started", "live_route_epoch_started", "live_route_epoch_recovered" })
                            Assert.True(publicationLines.Any(line => line.Contains($"event=filetransfer_{eventName};", StringComparison.Ordinal)),
                                $"Missing {eventName} for {direction} Tuna epoch {publication.LiveRouteEpoch}, publication {publication.PublicationGeneration}.");
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
                    foreach (var transitionGeneration in new[] { 2, 4 })
                        Assert.Single(log.Split('\n').Where(line =>
                            line.Contains("event=filetransfer_post_tuna_fallback_cleanup_completed;", StringComparison.Ordinal) &&
                            line.Contains($"transfer_id={transferId};", StringComparison.Ordinal) &&
                            line.Contains($"direction={direction};", StringComparison.Ordinal) &&
                            line.Contains($"transition_generation={transitionGeneration};", StringComparison.Ordinal)));
                Assert.Equal(false, GetPrivateField(host, "fileTransferFallbackProofPending"));
                Assert.Equal(false, GetPrivateField(helper, "fileTransferFallbackProofPending"));
                Assert.Equal(FileTransferRoute.PostTunaFallbackV6, State(sender, outbound).CurrentLeg!.RouteSelection.Route);
                Assert.Equal(FileTransferRoute.PostTunaFallbackV6, State(receiver, inbound).CurrentLeg!.RouteSelection.Route);
                Assert.False(host.IsPostTunaFileFallbackActiveForRouteSelection, terminalProjectionDiagnostics);
                Assert.False(helper.IsPostTunaFileFallbackActiveForRouteSelection, terminalProjectionDiagnostics);
                foreach (var direction in new[] { FileTransferDirection.Outbound, FileTransferDirection.Inbound })
                {
                    Assert.Contains(terminalProjectionObservations, observation => observation.Direction == direction);
                    Assert.All(terminalProjectionObservations.Where(observation => observation.Direction == direction),
                        observation => Assert.False(observation.FallbackActive, terminalProjectionDiagnostics));
                    Assert.Single(log.Split('\n').Where(line =>
                        line.Contains("event=filetransfer_fallback_projection_terminal_retired;", StringComparison.Ordinal) &&
                        line.Contains($"transfer_id={transferId};", StringComparison.Ordinal) &&
                        line.Contains($"direction={direction.ToString().ToLowerInvariant()};", StringComparison.Ordinal)));
                }
                Volatile.Write(ref captureFrames, 0);
                const string nextTransferId = "joined-regular-tuna-cycle-next";
                var nextPayload = payload[..1024];
                using var nextDestination = new MemoryStream();
                await sender.TryStartSendAsync(new("next.bin", nextPayload.Length, nextTransferId),
                    _ => Task.FromResult<Stream>(new MemoryStream(nextPayload, writable: false)), timeout.Token);
                await WaitFor("next regular offer", () => receiver.Snapshot.Inbound is
                    { TransferId: nextTransferId, State: FileTransferTransferState.PendingDecision });
                await receiver.AcceptIncomingTransferAsync(nextTransferId, (_, _) => Task.FromResult<Stream>(nextDestination), timeout.Token);
                await WaitFor("next regular completion", () => sender.Snapshot.Outbound is
                    { TransferId: nextTransferId, State: FileTransferTransferState.Completed } &&
                    receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed);
                Assert.Equal(nextPayload, nextDestination.ToArray());
                var nextOffer = Assert.Single(offers.Where(offer => offer.TransferId == nextTransferId));
                Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, nextOffer.FileTransferRoute);
                Assert.Equal(4, nextOffer.PreferredDataProtocolVersion);
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
                sent.Enqueue((fromHost, frame, Volatile.Read(ref fallbackPublished) != 0));
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
                var heldTailEnd = (holdPhysicalSend && frame is FileTransferChunkBatchFrameV6) ||
                    (earlyCheckpointProof && Volatile.Read(ref fallbackPublished) != 0) ? 73 : 70;
                if (fromHost && Volatile.Read(ref holdCycleTail) != 0 && frame is FileTransferChunkBatchFrameV4 tail &&
                    tail.StartChunkIndex + tail.DataSegments.Count >= heldTailEnd)
                    return true;
                // Keep the payload frontier missing until fallback disposition is applied.
                // Bootstrap and all feedback still travel normally.
                return fromHost && Volatile.Read(ref allowFrontier) == 0 &&
                    frame is FileTransferChunkBatchFrameV4 { StartChunkIndex: 0 };
            }
            async Task<bool> Forward(bool fromHost, INknTunaAccelerationSession target,
                NknBridgeChannel lane, byte[] bytes, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (!target.IsAvailable) { return false; }
                if (Observe(fromHost, lane, bytes)) { return true; }
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                if (target is PreparationHandshakeLane prepared) prepared.ReceivePeerBytesForTests(lane, bytes);
                else ((RetryableTunaAccelerationSession)target).ReceivePeerBytesForTests(lane, bytes);
                return true;
            }
            async Task WaitFor(string edge, Func<bool> predicate, int seconds = 8)
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
        static JsonElement Operation(JsonElement envelope, string role) => Assert.Single(
            envelope.GetProperty("aggregate").GetProperty("operations").EnumerateArray(),
            item => Text(item, "operationRole") == role);

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
        var accepted = Assert.Single(snapshots, entry => Text(entry.Envelope, "decisionKind") == "FallbackCheckpointAccepted");
        var yielded = Assert.Single(snapshots, entry => Text(entry.Envelope, "decisionKind") == "FallbackTailQuiescent");
        Assert.True(retained.Sequence < accepted.Sequence && accepted.Sequence < yielded.Sequence);
        Assert.Equal(Text(retained.Envelope, "coordinatorInstanceId"), Text(accepted.Envelope, "coordinatorInstanceId"));
        Assert.Equal(Text(retained.Envelope, "coordinatorInstanceId"), Text(yielded.Envelope, "coordinatorInstanceId"));

        var held = Operation(retained.Envelope, "peer_proof_awaiting_checkpoint");
        var retired = Operation(accepted.Envelope, "peer_proof_awaiting_checkpoint");
        var promoted = Operation(accepted.Envelope, "peer_proof_pending_initial");
        var consumed = Operation(yielded.Envelope, "peer_proof_pending_initial");
        var current = Operation(yielded.Envelope, "yield_current");
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
