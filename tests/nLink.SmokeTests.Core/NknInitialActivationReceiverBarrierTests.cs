using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Fact]
    [Trait("FileTransferInfraSuite", "InitialActivationReceiverBarrier")]
    public void InitialActivationReceiverBarrier_TestLaneFailureOriginIsOptInAndRunScoped()
    {
        using var legacy = new FakeNknAccelerationLane();
        NknAccelerationFailureOrigin? legacyOrigin = null;
        legacy.StateChanged += (_, args) => legacyOrigin = args.FailureOrigin;
        legacy.SetAvailable(false, "sidecar_tuna_stream_eof");
        Assert.Null(legacyOrigin);

        using var lane = new FakeNknAccelerationLane(enableFailureOrigins: true);
        NknAccelerationFailureOrigin? observed = null;
        lane.StateChanged += (_, args) => observed = args.FailureOrigin;
        lane.SetAvailable(false, "sidecar_tuna_stream_eof");
        var first = Assert.IsType<NknAccelerationFailureOrigin>(observed);
        Assert.False(first.IsDefault);
        Assert.True(lane.IsFailureOriginCurrent(first));
        var applied = 0;
        Assert.False(legacy.TryApplyFailureOrigin(first, () => { applied++; return true; }));
        Assert.False(lane.TryApplyFailureOrigin(first with { ProcessRunId = "wrong-run" }, () => { applied++; return true; }));
        Assert.Equal(0, applied);
        Assert.True(lane.TryApplyFailureOrigin(first, () => { applied++; return true; }));
        Assert.Equal(1, applied);
        lane.SetAvailable(true);
        Assert.False(lane.IsFailureOriginCurrent(first));
        lane.SetAvailable(false, "sidecar_tuna_stream_eof");
        var second = Assert.IsType<NknAccelerationFailureOrigin>(observed);
        Assert.Equal(first.LaneInstanceId, second.LaneInstanceId);
        Assert.True(second.ClientGeneration > first.ClientGeneration);
        Assert.NotEqual(first.ProcessRunId, second.ProcessRunId);
        Assert.True(second.FailureGeneration > first.FailureGeneration);
        Assert.False(lane.TryApplyFailureOrigin(first, () => { applied++; return true; }));
        Assert.True(lane.IsFailureOriginCurrent(second));
        lane.Dispose();
        Assert.False(lane.IsFailureOriginCurrent(second));
        Assert.Equal(1, applied);
    }

    [Fact]
    [Trait("FileTransferInfraSuite", "InitialActivationReceiverBarrier")]
    public Task InitialActivationReceiverSidecarFailure_BeforePreparedPublishesCurrentBufferedState()
        => RunInitialActivationReceiverBarrierAsync();

    [Fact]
    [Trait("FileTransferInfraSuite", "InitialActivationReceiverBarrier")]
    public Task InitialActivationReceiverSidecarFailure_SessionOpenedDuringPausePublishesCurrentBufferedState()
        => RunInitialActivationReceiverBarrierAsync("late_open");

    [Theory]
    [InlineData("prior_session")]
    [InlineData("prior_session_disposed")]
    [Trait("FileTransferInfraSuite", "InitialActivationReceiverBarrier")]
    public Task InitialActivationReceiverSidecarFailure_IndependentSessionGenerationsPublishCurrentBufferedState(string control)
        => RunInitialActivationReceiverBarrierAsync(control);

    [Theory]
    [InlineData("prior_session_wrong_identity")]
    [InlineData("prior_session_wrong_generation")]
    [InlineData("prior_session_replaced")]
    [Trait("FileTransferInfraSuite", "InitialActivationReceiverBarrier")]
    public Task InitialActivationReceiverSidecarFailure_StalePhysicalWitnessCannotRelease(string control)
        => RunInitialActivationReceiverBarrierAsync(control);

    [Theory]
    [InlineData("duplicate_eof")]
    [InlineData("admission_replaced")]
    [Trait("FileTransferInfraSuite", "InitialActivationReceiverBarrier")]
    public Task InitialActivationReceiverSidecarFailure_StaleOrDuplicateCannotReleaseAgain(string control)
        => RunInitialActivationReceiverBarrierAsync(control);

    private async Task RunInitialActivationReceiverBarrierAsync(string? control = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var receiverClient = new FakeNknClient("receiver.initial-activation-barrier.address");
        var senderClient = new FakeNknClient("sender.initial-activation-barrier.address");
        var lane = new FakeNknAccelerationLane(enableFailureOrigins: true);
        using var transport = new NknSignalingTransport(receiverClient, NknTransportOptions.Load(),
            new NknIdentity("receiver-initial-activation-barrier", receiverClient.Address),
            NknTunaAccelerationOptions.Disabled, lane);
        using var peer = new NknSignalingTransport(senderClient, NknTransportOptions.Load(),
            new NknIdentity("sender-initial-activation-barrier", senderClient.Address));
        var sessionId = await ApproveNknSessionAsync(transport, peer, cts.Token,
            InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
        const string transferId = "initial_activation_receiver_barrier";
        const string fileName = "initial-activation-receiver-barrier.bin";
        const string route = FileTransferRouteResolver.RegularNknV4FastToken;
        using var priorDataSession = control?.StartsWith("prior_session", StringComparison.Ordinal) == true
            ? await transport.OpenFileTransferDataSessionAsync(sessionId, "prior_receiver_barrier_session", cts.Token)
            : null;
        if (control == "prior_session_disposed")
        {
            priorDataSession!.Dispose();
        }
        var payload = Enumerable.Range(1, 80).Select(static value => (byte)value).ToArray();
        var sha256 = Convert.ToBase64String(SHA256.HashData(payload));
        using var destination = new MemoryStream();
        using var receiver = new SessionFileTransferService();
        IFileTransferDataSession? replacementDataSession = null;
        if (control == "prior_session_replaced")
        {
            ((IInitialActivationReceiverBarrierBridge)transport).ReceiverBarrierFailureObserved += (_, args) =>
            {
                args.Authority.Source.DataSession.Dispose();
                replacementDataSession = transport.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token)
                    .GetAwaiter().GetResult();
            };
        }
        receiver.AttachTransport(transport);
        var failures = new ConcurrentQueue<InitialActivationReceiverBarrierFailureEventArgs>();
        ((IInitialActivationReceiverBarrierBridge)transport).ReceiverBarrierFailureObserved += (_, args) => failures.Enqueue(args);
        void PauseInitialNegotiation()
        {
            transport.SeedRuntimeUnlockOfferCriticalSectionForTests(sessionId, "receiver-initial-offer", 17, 11,
                observedSend: true, observedLane: "nkn", peerReceived: true);
            Assert.True(Assert.IsType<bool>(InvokePrivateMethod(transport,
                "PauseFileTransferDataSessionsForTunaActivationNegotiation", "initial_receiver_test", sessionId, "runtime_unlock")));
        }
        if (control == "late_open")
        {
            PauseInitialNegotiation();
            Assert.Null(receiver.Snapshot.Inbound);
        }

        await peer.SendFileTransferOfferAsync(new FileTransferOfferV2
        {
            SessionId = sessionId, TransferId = transferId, FileName = fileName,
            FileSizeBytes = payload.Length, PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            FileTransferRoute = route,
        }, cts.Token);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision,
            TimeSpan.FromSeconds(3));
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), cts.Token);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.AwaitingMetadata,
            TimeSpan.FromSeconds(3));
        await peer.SendFileTransferSessionOpenAsync(new FileTransferSessionOpenV2
        {
            SessionId = sessionId, TransferId = transferId, ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            FileTransferRoute = route, SessionRole = FileTransferProtocol.SessionRoleSender,
            ChunkSizeBytes = 4, InitialPipelineDepth = 1,
        }, cts.Token);
        using var senderSession = await peer.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
        var states = new ConcurrentQueue<FileTransferStateFrameV4>();
        var collectStates = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (await senderSession.ReceiveAsync(cts.Token) is FileTransferStateFrameV4 state &&
                        state is not FileTransferReceiverStateFrameV6)
                    {
                        states.Enqueue(state);
                    }
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        }, CancellationToken.None);
        try
        {
            await senderSession.SendAsync(new FileTransferManifestFrameV4
            {
                SessionId = sessionId, TransferId = transferId, FileName = fileName, FileSizeBytes = payload.Length,
                ChunkSizeBytes = 4, ChunkCount = 20, Sha256Base64 = sha256,
            }, cts.Token);
            await senderSession.SendAsync(new FileTransferChunkBatchFrameV4
            {
                SessionId = sessionId, TransferId = transferId, StartChunkIndex = 0, ChunkCount = 1,
                DataSegments = [payload.Take(4).ToArray()],
            }, cts.Token);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.BytesTransferred == 4, TimeSpan.FromSeconds(3));
            if (control != "late_open")
            {
                // One committed chunk may be coalesced by normal feedback cadence.
                // Prove actual initial feedback independently of the durable write.
                await WaitUntilAsync(() => states.Any(state => state.BytesCommitted == 0 &&
                    state.ContiguousCommittedChunkIndex == 0), TimeSpan.FromSeconds(3));
            }
            var context = GetPrivateField(receiver, "inboundTransfer")!;
            var serviceGate = GetPrivateField(receiver, "gate")!;
            FileTransferCoordinatorState CaptureState()
            {
                lock (serviceGate)
                {
                    var method = typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                        .Single(candidate => candidate.Name == "CreateCoordinatorStateLocked" &&
                            candidate.GetParameters().Length == 1 && candidate.GetParameters()[0].ParameterType == context.GetType());
                    return Assert.IsType<FileTransferCoordinatorState>(method.Invoke(null, [context]));
                }
            }
            RuntimeUnlockAdmissionDecision RequestAdmission(string reason)
            {
                lock (serviceGate)
                {
                    var method = typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                        .Single(candidate => candidate.Name == "RequestRuntimeUnlockAdmissionLocked" &&
                            candidate.GetParameters()[0].ParameterType == context.GetType());
                    return Assert.IsType<RuntimeUnlockAdmissionDecision>(
                        method.Invoke(null, [context, "precommit_probe_responder", reason, false]));
                }
            }
            var original = CaptureState();
            Assert.Equal(FileTransferRoute.RegularNknV4Fast, original.RouteSelection.Route);
            Assert.Equal(FileTransferProtocol.ProtocolVersionV4, original.CurrentRuntimeOwner!.Value.ProtocolVersion);
            Assert.NotNull(original.CurrentRoutePublicationToken);
            Assert.Null(original.PersistentState.FileTransferRouteTransition.CurrentOperation);
            var dataSession = Assert.IsAssignableFrom<IFileTransferDataSession>(ContextValue(context, "DataSession"));
            if (priorDataSession is not null)
            {
                Assert.Equal(1L, original.CurrentDataSessionGeneration);
                var transportGeneration = Assert.IsType<long>(dataSession.GetType().GetProperty("Generation")!.GetValue(dataSession));
                Assert.True(transportGeneration > original.CurrentDataSessionGeneration);
                Assert.Equal($"{transferId}:1", original.CurrentDataSessionIdentity);
            }
            using var otherDataSession = await transport.OpenFileTransferDataSessionAsync(
                sessionId, "other_initial_activation_receiver_barrier", cts.Token);

            if (control != "late_open")
            {
                PauseInitialNegotiation();
            }
            await WaitUntilAsync(() => Assert.IsType<bool>(ContextValue(context, "PullTransportPaused")), TimeSpan.FromSeconds(3));
            Assert.False(dataSession.IsAvailable);
            Assert.False(otherDataSession.IsAvailable);
            Assert.Equal("tuna_activation_negotiating", ContextValue(context, "PullTransportPauseReason"));
            var sharedPauseGeneration = GetPrivateField(transport, "fileTransferTunaActivationPauseGeneration");
            if (control == "late_open")
            {
                var bound = Assert.IsType<InitialActivationReceiverBarrierOperation>(
                    CaptureState().PersistentState.InitialActivationReceiverBarrier.Current);
                Assert.Equal(InitialActivationReceiverBarrierState.Bound, bound.State);
                Assert.Same(dataSession, bound.Authority.Source.DataSession);
                Assert.Equal(Assert.IsType<long>(sharedPauseGeneration), bound.Authority.PauseGeneration);
                Assert.Empty(states);
            }
            // Exercise the real Coordinator admission path without inventing
            // a sender Prepared transition or a paired fallback receipt.
            var admission = RequestAdmission("initial_receiver_test");
            Assert.True(admission.Granted, admission.Reason);
            Assert.NotNull(admission.AdmissionToken);
            transport.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            var admitted = CaptureState();
            Assert.NotNull(admitted.CurrentRuntimeUnlockAdmission);
            Assert.Equal(original.CurrentRuntimeOwner, admitted.CurrentRuntimeOwner);
            Assert.Equal(original.CurrentRoutePublicationToken, admitted.CurrentRoutePublicationToken);
            Assert.Null(admitted.PersistentState.FileTransferRouteTransition.CurrentOperation);

            // Actual Nkn receive delivery remains live while receiver feedback is
            // held. These are not injected availability or receive-frame events.
            for (var chunkIndex = 1; chunkIndex <= 16; chunkIndex++)
            {
                await senderSession.SendAsync(new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId, TransferId = transferId, StartChunkIndex = chunkIndex, ChunkCount = 1,
                    DataSegments = [payload.Skip(chunkIndex * 4).Take(4).ToArray()],
                }, cts.Token);
            }
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.BytesTransferred == 68, TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => Convert.ToInt64(ContextValue(context, "TunaActivationBarrierStateSendSuppressedCount")) > 0,
                TimeSpan.FromSeconds(3));
            Assert.Equal(68L, destination.Length);
            Assert.True(Assert.IsType<bool>(ContextValue(context, "PullTransportPaused")));
            Assert.DoesNotContain(states, state => state.BytesCommitted >= 68);
            Assert.Null(CaptureState().PersistentState.FileTransferRouteTransition.CurrentOperation);
            Assert.Null(GetPrivateField(transport, "runtimeUnlockPreCommitProbeStartedSnapshot"));

            FileTransferCoordinatorState? replacementState = null;
            if (control == "admission_replaced")
            {
                var bound = Assert.IsType<InitialActivationReceiverBarrierOperation>(
                    CaptureState().PersistentState.InitialActivationReceiverBarrier.Current);
                var replacement = RequestAdmission("initial_receiver_admission_replaced");
                Assert.True(replacement.Granted, replacement.Reason);
                Assert.True(replacement.AdmissionToken!.Value.AdmissionGeneration > admission.AdmissionToken!.Value.AdmissionGeneration);
                replacementState = CaptureState();
                Assert.Equal(original.CurrentRuntimeOwner, replacementState.CurrentRuntimeOwner);
                Assert.Equal(original.CurrentRoutePublicationToken, replacementState.CurrentRoutePublicationToken);
                Assert.Null(replacementState.PersistentState.InitialActivationReceiverBarrier.Current);
                var invalidated = Assert.IsType<InitialActivationReceiverBarrierOperation>(
                    replacementState.PersistentState.InitialActivationReceiverBarrier.LastTerminal);
                Assert.Equal(bound.Authority, invalidated.Authority);
                Assert.Equal(InitialActivationReceiverBarrierState.Invalidated, invalidated.State);
                Assert.False(bound.Authority.ExecutionFence.IsValid);
            }
            var logStart = GetOperationalLogLength();
            int CloseEffectCount() => ReadOperationalLogTail(logStart).Split('\n').Count(line =>
                line.Contains("effect_kind=receiver_barrier_close_applied;", StringComparison.Ordinal));
            if (control is "prior_session_wrong_identity" or "prior_session_wrong_generation")
            {
                lock (GetPrivateField(transport, "accelerationGate")!)
                {
                    var shadows = Assert.IsAssignableFrom<System.Collections.IDictionary>(
                        GetPrivateField(transport, "initialReceiverBarriers"));
                    var shadow = shadows[transferId]!;
                    var changed = shadow.GetType().GetMethod("<Clone>$")!.Invoke(shadow, null)!;
                    var property = control == "prior_session_wrong_identity"
                        ? "TransportDataSessionIdentity" : "TransportDataSessionGeneration";
                    changed.GetType().GetProperty(property)!.SetValue(changed,
                        control == "prior_session_wrong_identity" ? "wrong-instance:2" : (object)999L);
                    shadows[transferId] = changed;
                }
            }
            lane.SetAvailable(false, "sidecar_tuna_stream_eof");
            Assert.Contains("event=tuna_acceleration_state_changed; available=0; reason=sidecar_tuna_stream_eof",
                ReadOperationalLogTail(logStart), StringComparison.Ordinal);
            if (control is "prior_session_wrong_identity" or "prior_session_wrong_generation" or "prior_session_replaced")
            {
                Assert.Null(Assert.Single(failures).Decision);
                Assert.True(Assert.IsType<bool>(ContextValue(context, "PullTransportPaused")));
                Assert.False(otherDataSession.IsAvailable);
                Assert.Equal(sharedPauseGeneration, GetPrivateField(transport, "fileTransferTunaActivationPauseGeneration"));
                Assert.DoesNotContain(states, state => state.BytesCommitted >= 68);
                Assert.Equal(0, CloseEffectCount());
                if (replacementDataSession is not null)
                {
                    // Closing the old receive channel may terminalize Core normally;
                    // the captured stale failure cannot close either session's barrier.
                    Assert.NotSame(dataSession, replacementDataSession);
                    Assert.False(replacementDataSession.IsAvailable);
                    Assert.False(Assert.Single(failures).Authority.ExecutionFence.IsValid);
                    Assert.NotEqual(InitialActivationReceiverBarrierState.Closed,
                        CaptureState().PersistentState.InitialActivationReceiverBarrier.LastTerminal?.State);
                }
                else
                {
                    Assert.False(dataSession.IsAvailable);
                    Assert.Equal(original.CurrentRuntimeOwner, CaptureState().CurrentRuntimeOwner);
                    Assert.Equal(original.CurrentRoutePublicationToken, CaptureState().CurrentRoutePublicationToken);
                }
                return;
            }
            if (replacementState is not null)
            {
                Assert.NotEqual(InitialActivationReceiverBarrierDecisionStatus.Accepted, Assert.Single(failures).Decision?.Status);
                var rejected = CaptureState();
                Assert.Equal(replacementState.CurrentRuntimeUnlockAdmission, rejected.CurrentRuntimeUnlockAdmission);
                Assert.Equal(original.CurrentRuntimeOwner, rejected.CurrentRuntimeOwner);
                Assert.Equal(original.CurrentRoutePublicationToken, rejected.CurrentRoutePublicationToken);
                Assert.Equal(replacementState.PersistentState.InitialActivationReceiverBarrier.LastTerminal,
                    rejected.PersistentState.InitialActivationReceiverBarrier.LastTerminal);
                Assert.True(Assert.IsType<bool>(ContextValue(context, "PullTransportPaused")));
                Assert.False(dataSession.IsAvailable);
                Assert.False(otherDataSession.IsAvailable);
                Assert.Equal(sharedPauseGeneration, GetPrivateField(transport, "fileTransferTunaActivationPauseGeneration"));
                Assert.DoesNotContain(states, state => state.BytesCommitted >= 68);
                Assert.Equal(0, CloseEffectCount());
                return;
            }
            // Baseline red: this real pre-Prepared failure leaves the exact
            // receiver barrier active despite already buffered durable progress.
            await WaitUntilAsync(() => !Assert.IsType<bool>(ContextValue(context, "PullTransportPaused")), TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => states.Any(state => state.ContiguousCommittedChunkIndex == 17 &&
                state.DurableReceivedHighestChunkIndex == 16 && state.BytesCommitted == 68), TimeSpan.FromSeconds(3));
            var closed = CaptureState();
            Assert.Equal(original.CurrentRuntimeOwner, closed.CurrentRuntimeOwner);
            Assert.Equal(original.CurrentRoutePublicationToken, closed.CurrentRoutePublicationToken);
            Assert.Same(dataSession, ContextValue(context, "DataSession"));
            Assert.Equal(original.CurrentDataSessionIdentity, closed.CurrentDataSessionIdentity);
            Assert.Equal(original.CurrentDataSessionGeneration, closed.CurrentDataSessionGeneration);
            Assert.Null(closed.PersistentState.FileTransferRouteTransition.CurrentOperation);
            Assert.False(otherDataSession.IsAvailable);
            Assert.Equal(sessionId, GetPrivateField(transport, "fileTransferTunaActivationPauseSessionId"));
            Assert.Equal(sharedPauseGeneration, GetPrivateField(transport, "fileTransferTunaActivationPauseGeneration"));
            Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound?.State);
            Assert.Equal(InitialActivationReceiverBarrierDecisionStatus.Accepted, Assert.Single(failures).Decision?.Status);
            await WaitUntilAsync(() => CloseEffectCount() == 1, TimeSpan.FromSeconds(3));
            if (control == "duplicate_eof")
            {
                lane.SetAvailable(false, "sidecar_tuna_stream_eof");
                Assert.Single(failures);
                var duplicate = CaptureState();
                Assert.Equal(closed.PersistentState.InitialActivationReceiverBarrier.LastTerminal,
                    duplicate.PersistentState.InitialActivationReceiverBarrier.LastTerminal);
                Assert.Equal(closed.CurrentRuntimeUnlockAdmission, duplicate.CurrentRuntimeUnlockAdmission);
                Assert.Equal(original.CurrentRuntimeOwner, duplicate.CurrentRuntimeOwner);
                Assert.Equal(original.CurrentRoutePublicationToken, duplicate.CurrentRoutePublicationToken);
                Assert.False(Assert.IsType<bool>(ContextValue(context, "PullTransportPaused")));
                Assert.False(otherDataSession.IsAvailable);
                Assert.Equal(1, CloseEffectCount());
            }
        }
        finally
        {
            replacementDataSession?.Dispose();
            await cts.CancelAsync();
            await collectStates;
        }
    }
}
