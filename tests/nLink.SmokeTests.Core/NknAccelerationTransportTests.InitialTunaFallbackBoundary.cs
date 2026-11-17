using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("FileTransferInfraSuite", "InitialTunaFallbackBoundary")]
    public Task PreparedInitialTuna_LocalStopCapturesExactInitialSource(bool helperPays, bool payerSends)
        => RunPreparedInitialTunaFallbackAsync(helperPays, payerSends);

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, false, 2)]
    [InlineData(false, true, 0)]
    [InlineData(false, true, 1)]
    [InlineData(false, true, 2)]
    [InlineData(true, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 1)]
    [InlineData(true, true, 2)]
    [Trait("FileTransferInfraSuite", "InitialTunaFallbackBoundary")]
    public Task PreparedInitialTuna_LocalStopPublishesFallbackAfterExactReceipts(bool helperPays, bool payerSends, int stopMode)
        => RunPreparedInitialTunaFallbackAsync(helperPays, payerSends, stopMode);

    [Theory]
    [InlineData("current")]
    [InlineData("service_generation")]
    [InlineData("user_pause")]
    [Trait("FileTransferInfraSuite", "InitialTunaFallbackBoundary")]
    public Task PreparedInitialTuna_PendingWriteWakeRequiresExactSource(string writeSource)
        => RunPreparedInitialTunaFallbackAsync(false, true, stopMode: 1, writeSource);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "InitialTunaFallbackBoundary")]
    public Task PreparedInitialTuna_StopCapturesCallbacksBeforeRetiringLane(bool payerSends)
        => RunPreparedInitialTunaFallbackAsync(false, payerSends, stopMode: 0, pendingCallbacks: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "InitialTunaFallbackBoundary")]
    public Task PreparedInitialTuna_LocalStopAfterNegotiationResetPreservesSource(bool payerSends)
        => RunPreparedInitialTunaFallbackAsync(false, payerSends, stopMode: 0, resetBeforeStop: true);

    [Theory]
    [InlineData("listener_terminal_tuna_write_failed")]
    [InlineData("listener_terminal_tuna_stream_eof")]
    [Trait("FileTransferInfraSuite", "InitialTunaFallbackBoundary")]
    public Task PreparedInitialTuna_SpontaneousSidecarFailurePublishesFallbackAndCompletes(string reason)
        => RunPreparedInitialTunaFallbackAsync(false, true, stopMode: 2, sidecarFailureReason: reason);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "InitialTunaFallbackBoundary")]
    public Task PreparedInitialTuna_RemoteClosePublishesFallbackAndCompletes(bool helperPays)
        => RunPreparedInitialTunaFallbackAsync(helperPays, true, stopMode: 2,
            sidecarFailureReason: "remote_closed", dialerFailsFirst: true);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("FileTransferInfraSuite", "InitialTunaFallbackBoundary")]
    public Task PreparedInitialTuna_RemoteCloseOrderingPreservesFallback(bool helperPays, bool peerDownFirst)
        => RunPreparedInitialTunaFallbackAsync(helperPays, true, stopMode: 2,
            sidecarFailureReason: "remote_closed", dialerFailsFirst: true, peerDownBeforeLocalFailure: peerDownFirst);

    [Theory]
    [InlineData("service_generation")]
    [InlineData("user_pause")]
    [Trait("FileTransferInfraSuite", "InitialTunaFallbackBoundary")]
    public Task PreparedInitialTuna_FailureWriteDrainCannotOutliveServiceSource(string source)
        => RunPreparedInitialTunaFallbackAsync(false, true, stopMode: 2, writeSource: source,
            sidecarFailureReason: "listener_terminal_tuna_write_failed");

    private Task RunPreparedInitialTunaFallbackAsync(bool helperPays, bool payerSends, int stopMode = -1,
        string? writeSource = null, bool pendingCallbacks = false, bool resetBeforeStop = false,
        string? sidecarFailureReason = null, bool dialerFailsFirst = false, bool? peerDownBeforeLocalFailure = null)
        => WithPublicPreparationPairAsync(helperPays, async pair =>
        {
            pair.PayerLane.FilePeer = pair.DialerLane;
            pair.DialerLane.FilePeer = pair.PayerLane;
            await pair.Payer.RequestAccelerationNegotiationAsync("runtime_unlock", pair.Token);
            await pair.WaitReady();
            await WaitPreparationRequestIdle(pair);
            var sender = payerSends ? pair.PayerService : pair.DialerService;
            var receiver = payerSends ? pair.DialerService : pair.PayerService;
            var reads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fallbackReads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var opens = 0;
            var bytes = new byte[writeSource is null && sidecarFailureReason is null ? 4 : 256 * 1024];
            await sender.TryStartSendAsync(new("prepared.bin", bytes.Length, "prepared-admission"),
                _ => Task.FromResult<Stream>(stopMode >= 0 && Interlocked.Increment(ref opens) > 1
                    ? new JoinedBootstrapReadGate(bytes, sidecarFailureReason is not null &&
                        sender.Snapshot.Outbound?.RouteToken == "post_tuna_fallback_v6" ? fallbackReads.Task : reads.Task)
                    : new MemoryStream(bytes)), pair.Token);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision,
                TimeSpan.FromSeconds(3));
            using var destination = new InitialFallbackWriteGate(writeSource is not null);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var downEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var downRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var proofRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var priorDelivery = pair.DialerClient.ShouldDeliverSendAsync;
            var prior = SessionFileTransferService.BootstrapFeedbackBeforeSubmissionForTests;
            SessionFileTransferService.BootstrapFeedbackBeforeSubmissionForTests = id =>
            {
                Assert.Equal("prepared-admission", id);
                entered.TrySetResult();
                return release.Task;
            };
            try
            {
                await receiver.AcceptIncomingTransferAsync("prepared-admission",
                    (_, _) => Task.FromResult<Stream>(destination), pair.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                AssertInitialStopCapture(sender, payerSends ? pair.Payer : pair.Dialer, "outboundTransfer");
                AssertInitialStopCapture(receiver, payerSends ? pair.Dialer : pair.Payer, "inboundTransfer");
                var senderStop = AssertRejectedInitialStops(sender, payerSends ? pair.Payer : pair.Dialer, "outboundTransfer");
                var receiverStop = AssertRejectedInitialStops(receiver, payerSends ? pair.Dialer : pair.Payer, "inboundTransfer");
                if (stopMode >= 0)
                {
                    var outbound = GetPrivateField(sender, "outboundTransfer")!;
                    var inbound = GetPrivateField(receiver, "inboundTransfer")!;
                    using var callbacks = pendingCallbacks ? new InitialFallbackPendingCallbacks(sender,
                        payerSends ? pair.Payer : pair.Dialer, outbound) : null;
                    var writing = writeSource is null ? null : StartInitialFallbackOwnedWrite(receiver, inbound);
                    if (writing is not null) { await destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
                    if (resetBeforeStop)
                    {
                        Assert.True(Assert.IsType<bool>(InvokePrivateMethod(payerSends ? pair.Payer : pair.Dialer,
                            "ResetAccelerationNegotiation", "header_switch_off")));
                        Assert.False(Assert.IsType<bool>(InvokePrivateMethod(payerSends ? pair.Payer : pair.Dialer,
                            "IsAccelerationNegotiatedAndHealthy")));
                    }
                    if (sidecarFailureReason is not null)
                    {
                        var before = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound);
                        var lane = pair.PayerLane.CaptureCurrentLaneLease();
                        foreach (var stale in new[] { default(NknAccelerationFailureOrigin), new(Guid.NewGuid(), 1, "replaced-run", 1) })
                        {
                            InvokePrivateMethod(pair.Payer, "OnAccelerationStateChanged", pair.PayerLane,
                                new AccelerationStateChangedEventArgs(false, sidecarFailureReason, stale));
                            var after = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound);
                            Assert.Equal(before.PersistentState, after.PersistentState);
                            Assert.Equal(before.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
                            Assert.Equal(lane, pair.PayerLane.CaptureCurrentLaneLease());
                            Assert.False((bool)GetPrivateField(pair.Payer, "fileTransferFallbackProofPending")!);
                        }
                        var peerFailureReason = dialerFailsFirst ? "remote_closed" : "sidecar_tuna_stream_eof";
                        var payerLease = pair.PayerLane.CaptureCurrentLaneLease();
                        if (peerDownBeforeLocalFailure is not null)
                        {
                            pair.DialerClient.ShouldDeliverSendAsync = async (_, payload, _) =>
                            {
                                if (EnvelopeCodec.TryDeserialize(payload, out var envelope))
                                {
                                    if (envelope.Type == MsgType.TransportAccelerationDown)
                                    {
                                        downEntered.TrySetResult();
                                        await downRelease.Task;
                                    }
                                    else if (envelope.Type == MsgType.FileTransferDataFrame) { await proofRelease.Task; }
                                }
                                return true;
                            };
                        }
                        if (writeSource is not null || dialerFailsFirst) { pair.DialerLane.FailRuntime(peerFailureReason); }
                        if (peerDownBeforeLocalFailure is not null) { await downEntered.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
                        if (peerDownBeforeLocalFailure == true)
                        {
                            downRelease.TrySetResult();
                            await WaitUntilAsync(() => pair.PayerLane.Current is null && pair.PayerLane.GlobalStops > 0,
                                TimeSpan.FromSeconds(3));
                            var retired = Assert.IsType<NknTunaLaneLease>(payerLease);
                            var beforeLateFailure = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound);
                            InvokePrivateMethod(pair.Payer, "OnAccelerationStateChanged", pair.PayerLane,
                                new AccelerationStateChangedEventArgs(false, sidecarFailureReason,
                                    new(retired.LaneInstanceId, retired.ClientGeneration,
                                        retired.DialerOwner?.RunId ?? retired.Listener!.ProcessRunId, 1)));
                            var afterLateFailure = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(sender, outbound);
                            Assert.Equal(beforeLateFailure.PersistentState, afterLateFailure.PersistentState);
                            Assert.Equal(beforeLateFailure.CurrentRoutePublicationToken, afterLateFailure.CurrentRoutePublicationToken);
                        }
                        else if (pair.PayerLane.Current is not null) { pair.PayerLane.FailRuntime(sidecarFailureReason); }
                        else { Assert.True(pair.PayerLane.GlobalStops > 0); }
                        if (writeSource is null && !dialerFailsFirst && pair.DialerLane.Current is not null)
                            pair.DialerLane.FailRuntime(peerFailureReason);
                        downRelease.TrySetResult();
                        proofRelease.TrySetResult();
                        // Let the next physical send expose whether Core installed its source hold.
                        reads.TrySetResult();
                    }
                    else
                    {
                        if (stopMode != 1) { await (payerSends ? pair.Payer : pair.Dialer).StopAccelerationAsync("header_switch_off", pair.Token); }
                        if (stopMode != 0) { await (payerSends ? pair.Dialer : pair.Payer).StopAccelerationAsync("runtime_disabled", pair.Token); }
                    }
                    callbacks?.AssertCapturedThenSettle();
                    release.TrySetResult();
                    if (writing is not null)
                    {
                        var wake = ContextProperty(inbound, "CurrentFallbackV6ProofBootstrapPendingWriteWake")!;
                        Assert.NotNull(wake);
                        Assert.NotNull(ContextProperty(wake, "InitialSource"));
                        Assert.Null(ContextProperty(wake, "RouteCommit"));
                        var execution = ContextProperty(inbound, "BootstrapSourceHoldExecution")!;
                        AssertInitialWriteStillHeld(receiver, inbound);
                        if (writeSource == "service_generation")
                            SetPrivateProperty(inbound, "DataSessionGeneration", (long)ContextProperty(inbound, "DataSessionGeneration")! + 1);
                        if (writeSource == "user_pause") { SetPrivateProperty(inbound, "UserPaused", true); }
                        destination.Release.TrySetResult();
                        await writing.WaitAsync(TimeSpan.FromSeconds(3));
                        if (writeSource != "current")
                        {
                            // Reenter the real completed-work callback with its old capture; it must remain inert.
                            await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod(
                                "CompleteBootstrapSourceHoldDrainAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                                .Invoke(receiver, [execution])).WaitAsync(TimeSpan.FromSeconds(3));
                            AssertInitialWriteStillHeld(receiver, inbound);
                            return;
                        }
                    }
                    try
                    {
                        await WaitUntilAsync(() => InitialFallbackPublished(sender, outbound) && InitialFallbackPublished(receiver, inbound),
                            TimeSpan.FromSeconds(8));
                    }
                    catch (TimeoutException error)
                    {
                        throw new TimeoutException(DescribeInitialFallback(sender, outbound) + "\n" + DescribeInitialFallback(receiver, inbound), error);
                    }
                    AssertJoinedBootstrapFallbackPublished(sender, outbound, receiver, inbound);
                    AssertInitialFallbackSourcePreserved(sender, outbound);
                    AssertInitialFallbackSourcePreserved(receiver, inbound);
                    callbacks?.AssertRetiredByExactDispositionAndStaleDrainIsInert();
                    AssertRejectedInitialStop(sender, payerSends ? pair.Payer : pair.Dialer, outbound, senderStop);
                    AssertRejectedInitialStop(receiver, payerSends ? pair.Dialer : pair.Payer, inbound, receiverStop);
                    if (sidecarFailureReason is not null)
                    {
                        var failureSide = (Service: sender, Transport: pair.Payer, Context: outbound, Stop: senderStop);
                        var observation = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(failureSide.Service, failureSide.Context)
                            .PersistentState.FileTransferRouteTransition.BootstrapExchange!.Bootstrap.Observation;
                        // A dialer-first failure can establish the sender's hold through exact peer evidence.
                        if (dialerFailsFirst && observation.InitiationKind == FileTransferBootstrapInitiationKind.PeerEvidence)
                        {
                            failureSide = (receiver, pair.Dialer, inbound, receiverStop);
                            observation = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(failureSide.Service, failureSide.Context)
                                .PersistentState.FileTransferRouteTransition.BootstrapExchange!.Bootstrap.Observation;
                        }
                        Assert.Equal(FileTransferBootstrapInitiationKind.LocalTransportFailure, observation.InitiationKind);
                        if (dialerFailsFirst) { Assert.Equal("sidecar_remote_closed", observation.Source); }
                        AssertRejectedInitialStop(failureSide.Service, failureSide.Transport, failureSide.Context, failureSide.Stop with
                        {
                            InitiationKind = observation.InitiationKind, Source = observation.Source, ProofKind = observation.ProofKind,
                            LocalStop = failureSide.Stop.LocalStop! with { FailureOrigin = observation.LocalStop!.FailureOrigin },
                        });
                    }
                    // The write fixture injects a receive batch without the sender's send ledger;
                    // it proves drain/currentness, not end-to-end payload completion.
                    if (writeSource is not null) { return; }
                    // Observe exact live publication/disposition receipts before terminal cleanup can retire them.
                    fallbackReads.TrySetResult();
                    reads.TrySetResult();
                    await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                        receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed, TimeSpan.FromSeconds(8));
                    Assert.Equal(bytes.Length, destination.ToArray().Length);
                    Assert.Equal(System.Security.Cryptography.SHA256.HashData(bytes),
                        System.Security.Cryptography.SHA256.HashData(destination.ToArray()));
                }
            }
            finally
            {
                // Retire the held source before letting its feedback continuation run.
                receiver.ResetSessionState();
                downRelease.TrySetResult();
                proofRelease.TrySetResult();
                pair.DialerClient.ShouldDeliverSendAsync = priorDelivery;
                release.TrySetResult();
                reads.TrySetResult();
                fallbackReads.TrySetResult();
                destination.Release.TrySetResult();
                SessionFileTransferService.BootstrapFeedbackBeforeSubmissionForTests = prior;
                await sender.CancelTransferAsync("prepared-admission", "test_complete", pair.Token);
            }
        });

    private static void AssertInitialStopCapture(SessionFileTransferService service,
        NknSignalingTransport transport, string contextName)
    {
        FileTransferCoordinatorState state;
        lock (GetPrivateField(service, "gate")!)
        {
            state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, GetPrivateField(service, contextName)!);
            Assert.NotNull(FileTransferCoordinator.CaptureFallbackBootstrapSourceScope(state));
            Assert.False(state.IsTerminal);
            Assert.Null(state.CurrentRouteCommit);
            Assert.True(FileTransferCoordinator.CanStartFallbackBootstrapExchange(state));
            Assert.Null(state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold);
        }
        lock (GetPrivateField(transport, "controlSecureStateGate")!)
        {
            var transportState = ((IDictionary)GetPrivateField(transport, "fileTransferStates")!)[state.TransferId]!;
            Assert.False((bool)Value(transportState, "IsTerminal")!);
            Assert.True((bool)Value(transportState, "CorrelatedBootstrapSelected")!);
            Assert.Equal(FileTransferCoordinator.CreateLocalFallbackBootstrapIssuer(state), Value(transportState, "BootstrapLocalIssuer"));
        }
        lock (GetPrivateField(transport, "gate")!)
        {
            var session = ((IDictionary)GetPrivateField(transport, "fileTransferDataSessions")!)[state.TransferId]!;
            Assert.False((bool)Value(session, "IsDisposed")!);
            Assert.Equal(state.SessionId, Value(session, "SessionId"));
            var publications = ((IDictionary)GetPrivateField(transport, "fileTransferRoutePublicationAuthorities")!).Values
                .Cast<FileTransferRoutePublicationAuthority>().ToArray();
            var publication = Assert.Single(publications);
            Assert.Equal("accepted", publication.CoordinatorDecisionReason);
            Assert.Equal(FileTransferCoordinator.CreateRoutePublicationAuthority(state.CurrentRoutePublicationToken!.Value,
                publication.CoordinatorDecisionReason), publication);
            Assert.Null(publication.RouteCommitGeneration);
            var hint = Assert.Single(((IDictionary)GetPrivateField(transport, "fileTransferRouteHints")!).Values.Cast<object>());
            Assert.Equal(FileTransferRoute.FileTunaV4, Value(hint, "Route"));
            Assert.Equal(4, Value(hint, "ProtocolVersion"));
        }
        foreach (var reason in new[] { "runtime_disabled", "header_switch_off" })
        {
            var source = Assert.Single(Assert.IsType<FileTransferBootstrapLocalStopSource[]>(
                InvokePrivateMethod(transport, "CaptureFallbackBootstrapLocalStopSources", reason)));
            Assert.Equal(FileTransferCoordinator.CaptureFallbackBootstrapSourceScope(state, source.Scope.Publication.CoordinatorDecisionReason), source.Scope);
            Assert.Equal(((IFileTransferDataSessionGenerationProvider)source.DataSession).Generation, source.ProviderGeneration);
        }
        foreach (var reason in new[] { "remote_runtime_disabled", "remote_header_switch_off", "unknown", "HEADER_SWITCH_OFF" })
            Assert.Empty(Assert.IsType<FileTransferBootstrapLocalStopSource[]>(
                InvokePrivateMethod(transport, "CaptureFallbackBootstrapLocalStopSources", reason)));
        AssertInitialServiceSourceBindings(service, state, contextName);

        static object? Value(object target, string name) => target.GetType().GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);
    }

    private static object? ContextProperty(object context, string name) => context.GetType()
        .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(context);

    private static Task StartInitialFallbackOwnedWrite(SessionFileTransferService receiver, object inbound)
    {
        var session = Assert.IsAssignableFrom<IFileTransferDataSession>(ContextProperty(inbound, "DataSession"));
        var ingress = typeof(SessionFileTransferService).GetMethod("CaptureInboundBootstrapWriteIngress",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [inbound, session]);
        Assert.NotNull(ingress);
        var batch = new FileTransferChunkBatchFrameV4 { SessionId = session.SessionId, TransferId = session.TransferId,
            StartChunkIndex = 0, ChunkCount = 1, DataSegments = [new byte[(int)ContextProperty(inbound, "ChunkSizeBytes")!]] };
        return Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod("HandleInboundV4ChunkBatchFromReceiveAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(receiver, [inbound, batch, FileTransferTransportKind.Tuna, ingress]));
    }

    private static void AssertInitialWriteStillHeld(SessionFileTransferService receiver, object inbound)
    {
        var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(receiver, inbound);
        Assert.Equal(FileTransferRoute.FileTunaV4, state.CurrentRoutePublicationToken!.Value.Route);
        Assert.Null(state.CurrentRouteCommit);
        Assert.Null(state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold!.Receipt);
        Assert.Null(state.PersistentState.FileTransferRouteTransition.BootstrapExchange);
        Assert.Equal(0, (long)ContextProperty(inbound, "BytesTransferred")!);
    }

    private sealed class InitialFallbackWriteGate(bool held) : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (held) { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
            await base.WriteAsync(buffer, ct);
        }
    }

    private static FileTransferFallbackV6ProofBootstrapEvaluation AssertRejectedInitialStops(
        SessionFileTransferService service, NknSignalingTransport transport, string contextName)
    {
        var context = GetPrivateField(service, contextName)!;
        var stop = Assert.Single(Assert.IsType<FileTransferBootstrapLocalStopSource[]>(
            InvokePrivateMethod(transport, "CaptureFallbackBootstrapLocalStopSources", "runtime_disabled")));
        var publication = stop.Scope.Publication;
        var evaluation = new FileTransferFallbackV6ProofBootstrapEvaluation(publication.SessionId, publication.Direction,
            1, 1, "runtime_disabled", "local_runtime_disabled", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        { InitiationKind = FileTransferBootstrapInitiationKind.LocalRuntimeDisable, LocalStop = stop };
        var unboundFailure = stop with { FailureOrigin = new(Guid.NewGuid(), 1, "unbound-run", 1) };
        AssertRejectedInitialStop(service, transport, context, evaluation with
        {
            InitiationKind = FileTransferBootstrapInitiationKind.LocalTransportFailure, Source = "sidecar_tuna_write_failed",
            ProofKind = "local_transport_failure", LocalStop = unboundFailure,
        });
        foreach (var changed in new[] {
            evaluation with { LocalStop = null },
            evaluation with { InitiationKind = FileTransferBootstrapInitiationKind.PeerEvidence },
            evaluation with { Source = "remote_runtime_disabled" },
            evaluation with { TriggerTransferId = publication.TransferId },
            evaluation with { LocalStop = stop with { ProviderGeneration = stop.ProviderGeneration + 1 } },
            evaluation with { LocalStop = stop with { Scope = stop.Scope with { LocalIssuer = new string('a', 64) } } },
            evaluation with { LocalStop = stop with { Scope = stop.Scope with { Publication = publication with { RouteCommitGeneration = 1 } } } },
            evaluation with { LocalStop = stop with { Scope = stop.Scope with { Publication = publication with { LiveRouteEpoch = 1 } } } },
            evaluation with { LocalStop = stop with { Scope = stop.Scope with { Source = stop.Scope.Source with { CheckpointGeneration = 1 } } } } })
            AssertRejectedInitialStop(service, transport, context, changed);
        return evaluation;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertRejectedInitialStop(SessionFileTransferService service, NknSignalingTransport transport,
        object context, FileTransferFallbackV6ProofBootstrapEvaluation evaluation)
    {
        lock (GetPrivateField(service, "gate")!)
        {
            var before = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
            var processing = Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod(
                "ProcessFallbackV6ProofBootstrapEvaluationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [transport, evaluation]));
            Assert.True(processing.IsCompletedSuccessfully);
            var after = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
            Assert.Equal(before.PersistentState, after.PersistentState);
            Assert.Equal(before.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertInitialServiceSourceBindings(SessionFileTransferService service,
        FileTransferCoordinatorState state, string contextName)
    {
        var agreement = state.PersistentState.FileTransferRouteTransition.BootstrapAgreement!;
        var context = GetPrivateField(service, contextName)!;
        FileTransferDataFrame frame = state.Direction == FileTransferDirection.Inbound
            ? new FileTransferFallbackBootstrapRequestFrameV6 { SessionId = state.SessionId, TransferId = state.TransferId,
                TransportEpoch = 1, SenderRequestId = $"fbc1:{agreement.Offer.FallbackBootstrapIssuerId}:1:{new string('a', 64)}" }
            : new FileTransferFallbackBootstrapStateFrameV6 { SessionId = state.SessionId, TransferId = state.TransferId,
                TransportEpoch = 1, ReceiverChallengeId = $"fbc1:{agreement.Accept!.FallbackBootstrapIssuerId}:1:{new string('b', 64)}",
                Epoch = 1, DurableReceivedHighestChunkIndex = -1, TransferPaused = true,
                TransferPauseReason = FileTransferBootstrapCorrelation.RecoveryMode };
        object?[] args = [context, frame, state.Direction, null];
        var create = typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "TryCreateFallbackV6ProofBootstrapPeerFrameBinding" &&
                method.GetParameters()[0].ParameterType == context.GetType());
        Assert.True(Assert.IsType<bool>(create.Invoke(null, args)));
        var binding = Assert.IsType<FileTransferFallbackV6ProofBootstrapPeerFrameBinding>(args[3]);
        var source = Assert.IsType<FileTransferInitialTunaFallbackSource>(binding.InitialSource);
        Assert.Equal(FileTransferCoordinator.CaptureInitialTunaFallbackSource(state), source);
        Assert.Null(binding.SourceRouteCommit);
        var wake = Assert.IsType<FileTransferFallbackV6ProofBootstrapEvaluation>(Call("CreateBootstrapCorrelationWakeEvaluation", state, binding));
        Assert.Equal(0, wake.TriggerSourceScope!.RouteCommitGeneration);
        Assert.True(Matches("MatchesFallbackV6ProofBootstrapPeerFrameEvaluationTrigger", binding, wake));
        foreach (var changed in new[] {
            binding with { InitialSource = null },
            binding with { SourceRouteCommit = default(RouteCommitToken) },
            binding with { InitialSource = source with { DataSessionIdentity = "replaced" } },
            binding with { InitialSource = source with { DataSessionGeneration = source.DataSessionGeneration + 1 } },
            binding with { InitialSource = source with { Publication = source.Publication with { PublicationGeneration = 2 } } },
            binding with { DataSessionReferenceGeneration = binding.DataSessionReferenceGeneration + 1 },
            binding with { DataSessionGeneration = binding.DataSessionGeneration + 1 } })
        {
            Assert.False(Matches("MatchesFallbackV6ProofBootstrapPeerFrameBinding", binding, changed));
            Assert.False(Matches("MatchesFallbackV6ProofBootstrapPeerFrameBindingAfterPendingWriteDrain", binding, changed));
        }
        Assert.False(Matches("MatchesFallbackV6ProofBootstrapPeerFrameEvaluationTrigger", binding with { InitialSource = null }, wake));
        Assert.False(Matches("MatchesFallbackV6ProofBootstrapPeerFrameEvaluationTrigger",
            binding with { SourceRouteCommit = default(RouteCommitToken) }, wake));
        // A missing source commit must not admit the legacy peer-frame path.
        args[1] = new FileTransferFrontierRequestFrameV6 { SessionId = state.SessionId, TransferId = state.TransferId, TransportEpoch = 1 };
        Assert.False(Assert.IsType<bool>(create.Invoke(null, args)));

        static object? Call(string name, params object?[] arguments) => typeof(SessionFileTransferService)
            .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments);
        static bool Matches(string name, params object?[] arguments) => Assert.IsType<bool>(Call(name, arguments));
    }

    private static bool InitialFallbackPublished(SessionFileTransferService service, object context)
    {
        var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
        return state.CurrentRoutePublicationToken is { Route: FileTransferRoute.PostTunaFallbackV6, PublicationGeneration: 2 } &&
            state.PersistentState.FileTransferRouteTransition.LastTerminalOperation is
                { DispositionReceipt.Status: FileTransferRouteTransitionDispositionApplyStatus.Applied };
    }

    private static string DescribeInitialFallback(SessionFileTransferService service, object context)
    {
        var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
        var transition = state.PersistentState.FileTransferRouteTransition;
        var snapshot = state.Direction == FileTransferDirection.Outbound ? service.Snapshot.Outbound : service.Snapshot.Inbound;
        return System.Text.Json.JsonSerializer.Serialize(new { state.Direction, state.IsTerminal,
            snapshot?.State, snapshot?.ErrorCode, snapshot?.StatusMessage, snapshot?.BytesTransferred,
            state.RouteSelection, state.CurrentRoutePublicationToken, transition.BootstrapExchange,
            transition.CurrentOperation, transition.LastTerminalOperation });
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertInitialFallbackSourcePreserved(SessionFileTransferService service, object context)
    {
        var state = SessionFileTransferV4SenderTests.CaptureJoinedNknStateForTest(service, context);
        var transition = state.PersistentState.FileTransferRouteTransition;
        var bootstrap = transition.BootstrapExchange!.Bootstrap;
        var source = Assert.IsType<FileTransferInitialTunaFallbackSource>(bootstrap.InitialSource);
        Assert.False(source.IsDefault);
        Assert.Null(bootstrap.SourceRouteCommit);
        var terminal = transition.LastTerminalOperation!.Value;
        Assert.Null(terminal.Authority.SourceRouteCommit);
        Assert.Null(terminal.Authority.PredecessorTransitionAuthorityId);
        Assert.Equal(source, Assert.IsType<FileTransferFallbackTransitionProofBoundary>(terminal.Authority.ProofBoundary).InitialSource);
        Assert.Equal(1, state.CurrentLiveRouteEpoch!.EpochId);
        Assert.Equal(1, transition.LastTransitionGeneration);
    }
}
