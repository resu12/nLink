using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "InitialActivationFailureHandoff")]
    public Task InitialActivationSidecarFailure_ActualCallbackRollsBackExactAttachedSender(bool checkMalformedObservations)
        => RunInitialActivationSidecarFailureAsync(checkMalformedObservations);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "InitialActivationFailureHandoff")]
    public Task InitialActivationSidecarFailure_ListenerTerminalEofBeforeRemoteClosedRollsBackOnce(bool checkMalformedObservations)
        => RunInitialActivationSidecarFailureAsync(checkMalformedObservations,
            sidecarReason: "listener_terminal_tuna_stream_eof");

    [Theory]
    [InlineData("data_session_disposed")]
    [InlineData("service_disposed")]
    [InlineData("data_session_replaced")]
    [InlineData("data_session_generation_aba")]
    [InlineData("probe_acked")]
    [InlineData("raw_transfer_conflict")]
    [InlineData("raw_lease_transfer_conflict")]
    [InlineData("installed_retry_mode")]
    [Trait("FileTransferInfraSuite", "InitialActivationFailureHandoff")]
    public Task InitialActivationSidecarFailure_RealLifecycleChangesRejectOldFailure(string lifecycleChange)
        => RunInitialActivationSidecarFailureAsync(false, lifecycleChange);

    [Theory]
    [InlineData("data_session_disposed")]
    [InlineData("service_disposed")]
    [InlineData("data_session_replaced")]
    [InlineData("data_session_generation_aba")]
    [InlineData("probe_acked")]
    [InlineData("raw_transfer_conflict")]
    [InlineData("raw_lease_transfer_conflict")]
    [InlineData("installed_retry_mode")]
    [Trait("FileTransferInfraSuite", "InitialActivationFailureHandoff")]
    public Task InitialActivationSidecarFailure_ListenerTerminalEofRejectsChangedLifecycle(string lifecycleChange)
        => RunInitialActivationSidecarFailureAsync(false, lifecycleChange, "listener_terminal_tuna_stream_eof");

    private async Task RunInitialActivationSidecarFailureAsync(bool checkMalformedObservations,
        string? lifecycleChange = null, string sidecarReason = "sidecar_tuna_stream_eof")
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // Independent cases must not share the invite-validation throttle key.
        var identity = Guid.NewGuid().ToString("N");
        var client = new FakeNknClient($"host.initial-activation-failure.{identity}.address");
        var peerClient = new FakeNknClient($"peer.initial-activation-failure.{identity}.address");
        var lane = new FakeNknAccelerationLane();
        using var transport = new NknSignalingTransport(client, NknTransportOptions.Load(),
            new NknIdentity("host-initial-activation-failure", client.Address), NknTunaAccelerationOptions.Disabled, lane);
        using var peer = new NknSignalingTransport(peerClient, NknTransportOptions.Load(),
            new NknIdentity("peer-initial-activation-failure", peerClient.Address));
        var sessionId = await ApproveNknSessionAsync(transport, peer, cts.Token,
            InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
        const string transferId = "initial_activation_failure_handoff";
        using var service = new SessionFileTransferService();
        using var wrongDataSession = await peer.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
        object? context = null;
        var observations = new List<RuntimeUnlockInitialActivationPreCommitProbeFailureObservation>();
        RuntimeUnlockInitialActivationPreCommitProbeFailureObservedEventArgs? delivered = null;
        FileTransferCoordinatorState? beforeOriginalDelivery = null;
        var rejectedCases = new List<string>();
        var bridge = (IRuntimeUnlockRetryDispatchAuthorityBridge)transport;
        // Run malformed observations while the exact authority is still live,
        // before the service's subscription consumes the original real event.
        bridge.RuntimeUnlockInitialActivationPreCommitProbeFailureObserved += (_, args) =>
        {
            observations.Add(args.Observation);
            delivered = args;
            Assert.NotNull(context);
            if (lifecycleChange is "data_session_disposed" or "service_disposed" or "data_session_replaced" or "data_session_generation_aba")
            {
                // Hold delivery while an independent lifecycle worker changes
                // the source. Do not nest replacement's rollback in the EOF stack.
                Task.Run(() =>
                {
                    if (lifecycleChange == "service_disposed") { service.Dispose(); }
                    else
                    {
                        args.Observation.SourceDataSession.Dispose();
                        if (lifecycleChange is "data_session_replaced" or "data_session_generation_aba")
                        {
                            var successor = transport.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token).GetAwaiter().GetResult();
                            SessionFileTransferV4SenderTests.ReplaceInitialActivationNknDataSessionForTest(service, context, successor);
                            if (lifecycleChange == "data_session_generation_aba")
                            {
                                successor.Dispose();
                                var restoredLogicalScope = transport.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token).GetAwaiter().GetResult();
                                SessionFileTransferV4SenderTests.ReplaceInitialActivationNknDataSessionForTest(service, context, restoredLogicalScope);
                                Assert.Equal(3L, SessionFileTransferV4SenderTests.CaptureInitialActivationNknSenderForTest(context).CurrentDataSessionGeneration);
                            }
                        }
                    }
                }).WaitAsync(TimeSpan.FromSeconds(3), cts.Token).GetAwaiter().GetResult();
                beforeOriginalDelivery = SessionFileTransferV4SenderTests.CaptureInitialActivationNknSenderForTest(context);
                return;
            }
            if (!checkMalformedObservations) { return; }
            var current = SessionFileTransferV4SenderTests.CaptureInitialActivationNknSenderForTest(context);
            var execution = ContextValue(context, "FileTransferRouteTransitionExecution");
            Assert.Equal(FileTransferRouteTransitionState.Draining,
                current.PersistentState.FileTransferRouteTransition.CurrentOperation!.Value.State);
            var handler = typeof(SessionFileTransferService).GetMethod(
                "OnRuntimeUnlockInitialActivationPreCommitProbeFailureObserved", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var (name, invalid) in InvalidInitialActivationFailures(args.Observation, wrongDataSession))
            {
                var rejected = new RuntimeUnlockInitialActivationPreCommitProbeFailureObservedEventArgs(invalid);
                handler.Invoke(service, [transport, rejected]);
                Assert.NotEqual(RuntimeUnlockInitialActivationPreCommitProbeFailureStatus.Accepted, rejected.Decision.Status);
                var unchanged = SessionFileTransferV4SenderTests.CaptureInitialActivationNknSenderForTest(context);
                Assert.Equal(SnapshotIdentity(current), SnapshotIdentity(unchanged));
                Assert.Equal(current.CurrentRuntimeOwner, unchanged.CurrentRuntimeOwner);
                Assert.Equal(current.CurrentRoutePublicationToken, unchanged.CurrentRoutePublicationToken);
                Assert.Equal(current.CurrentRuntimeUnlockPendingPreCommitProbe, unchanged.CurrentRuntimeUnlockPendingPreCommitProbe);
                Assert.Same(args.Observation.SourceDataSession, ContextValue(context, "DataSession"));
                Assert.Same(execution, ContextValue(context, "FileTransferRouteTransitionExecution"));
                Assert.Equal(current.CurrentDataSessionGeneration, unchanged.CurrentDataSessionGeneration);
                Assert.Equal(current.CurrentDataSessionIdentity, unchanged.CurrentDataSessionIdentity);
                Assert.True(Assert.IsType<bool>(ContextValue(context, "PullTransportPaused")));
                rejectedCases.Add(name);
            }
            var wrongAttachment = new RuntimeUnlockInitialActivationPreCommitProbeFailureObservedEventArgs(args.Observation);
            handler.Invoke(service, [peer, wrongAttachment]);
            Assert.NotEqual(RuntimeUnlockInitialActivationPreCommitProbeFailureStatus.Accepted, wrongAttachment.Decision.Status);
            Assert.Equal(SnapshotIdentity(current), SnapshotIdentity(SessionFileTransferV4SenderTests.CaptureInitialActivationNknSenderForTest(context)));
            rejectedCases.Add("wrong_attachment");
            var nullObservation = new RuntimeUnlockInitialActivationPreCommitProbeFailureObservedEventArgs(null!);
            handler.Invoke(service, [transport, nullObservation]);
            Assert.Equal(RuntimeUnlockInitialActivationPreCommitProbeFailureStatus.Rejected, nullObservation.Decision.Status);
            Assert.Equal(SnapshotIdentity(current), SnapshotIdentity(SessionFileTransferV4SenderTests.CaptureInitialActivationNknSenderForTest(context)));
            rejectedCases.Add("null_observation");
        };
        service.AttachTransport(transport);
        var dataSession = await transport.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
        context = SessionFileTransferV4SenderTests.CreateInitialActivationNknSenderForTest(
            service, transport, dataSession, sessionId, transferId);
        var original = SessionFileTransferV4SenderTests.CaptureInitialActivationNknSenderForTest(context);
        transport.SeedRuntimeUnlockOfferCriticalSectionForTests(sessionId, "initial-failure-offer", 17, 11,
            observedSend: true, observedLane: "nkn", peerReceived: true);
        Assert.True(Assert.IsType<bool>(InvokePrivateMethod(transport,
            "PauseFileTransferDataSessionsForTunaActivationNegotiation", "initial_activation_test", sessionId, "runtime_unlock")));
        Assert.False(dataSession.IsAvailable);
        await WaitUntilAsync(() => Assert.IsType<bool>(ContextValue(context, "PullTransportPaused")), TimeSpan.FromSeconds(3));
        Assert.True(Assert.IsType<bool>(ContextValue(context, "PullTransportPaused")));
        Assert.Equal("tuna_activation_negotiating", Assert.IsType<string>(ContextValue(context, "PullTransportPauseReason")));
        transport.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // The adapter transaction may not know the transfer yet. The real launch
        // must resolve the one attached data session and retain its exact binding.
        var transaction = new RuntimeUnlockTransactionSnapshot(sessionId, TransferId: null,
            TransactionGeneration: 7, OfferGeneration: 11, State: RuntimeUnlockTransactionState.AnswerReceived,
            CreatedUtcMs: now - 100, UpdatedUtcMs: now, DeadlineUtcMs: now + 60_000,
            ListenerReady: true, ListenerUnavailable: false, ObservedSend: true, ObservedLane: "nkn",
            PeerReceived: true, AnswerReceived: true, PathProbeId: null, PathProbeState: RuntimeUnlockPathProbeState.None,
            PathProbeTransport: FileTransferTransportKind.Unknown, PathProbeAckedUtcMs: 0, PathProbeFailureReason: null,
            RouteCommitPending: false, RouteCommitted: false, FailureReason: null, RetiredReason: null,
            TunaPathLeaseGeneration: 13, TunaPathLeaseState: RuntimeUnlockTunaPathLeaseState.ListenerReady,
            TunaPathLeaseListenerRunId: "initial-listener-13", TunaPathLeaseCurrent: true);
        SetPrivateField(transport, "runtimeUnlockTransactionState", transaction);
        SetPrivateField(transport, "runtimeUnlockTunaPathLeaseState", new RuntimeUnlockTunaPathLeaseSnapshot(
            sessionId, null, 7, 11, 13, "initial-listener-13", 17, RuntimeUnlockTunaPathLeaseState.ListenerReady,
            now - 100, now, now + 60_000, FailureReason: null, RetiredReason: null));
        var logStart = GetOperationalLogLength();
        InvokePrivateMethod(transport, "RequestRuntimeUnlockPreCommitProbeLaunch", sessionId,
            NknAccelerationLaneKind.File, "initial_activation_test", transaction);
        await WaitUntilAsync(() => GetPrivateField(transport, "runtimeUnlockPreCommitProbeStartedSnapshot") is
            RuntimeUnlockRouteCommitSnapshot { PathProbeState: "Started" }, TimeSpan.FromSeconds(3));
        var started = Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(
            GetPrivateField(transport, "runtimeUnlockPreCommitProbeStartedSnapshot"));
        var beforeFailure = SessionFileTransferV4SenderTests.CaptureInitialActivationNknSenderForTest(context);
        var transition = Assert.IsType<FileTransferRouteTransitionOperation>(
            beforeFailure.PersistentState.FileTransferRouteTransition.CurrentOperation);
        Assert.Equal(FileTransferRouteTransitionState.Draining, transition.State);
        Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Installed, transition.PauseBindReceipt!.Value.Status);
        Assert.Equal(transferId, started.TransferId);
        Assert.Equal(7L, started.TransactionGeneration);
        Assert.Equal(11L, started.OfferGeneration);
        Assert.Equal(13L, started.TunaPathLeaseGeneration);
        Assert.True(started.LaunchGeneration > 0);
        await WaitUntilAsync(() => !lane.Sent.IsEmpty, TimeSpan.FromSeconds(3));
        Assert.NotEmpty(lane.Sent);
        Assert.NotNull(ContextValue(context, "LastRuntimeUnlockPreCommitProbeLaunchDisposition"));
        var shadow = GetPrivateField(transport, "fileTransferRouteTransitionExecutionShadow")!;
        Assert.NotNull(ContextValue(shadow, "Current"));
        Assert.Same(dataSession, ContextValue(context, "DataSession"));

        if (lifecycleChange is "probe_acked" or "raw_transfer_conflict" or "raw_lease_transfer_conflict" or "installed_retry_mode")
        {
            if (lifecycleChange == "probe_acked")
            {
                Assert.True(((IRuntimeUnlockRouteCommitProofProvider)transport).NotifyRuntimeUnlockPathProbeResult(
                    sessionId, transferId, transportEpoch: 0, started.PathProbeId!, FileTransferTransportKind.Tuna,
                    acked: true, "initial_activation_test_ack", expectedLocalTransactionGeneration: started.TransactionGeneration,
                    expectedLocalOfferGeneration: started.OfferGeneration, expectedLocalTunaPathLeaseGeneration: started.TunaPathLeaseGeneration,
                    expectedLocalTunaPathLeaseListenerRunId: started.TunaPathLeaseListenerRunId,
                    expectedLocalPathProbeState: RuntimeUnlockPathProbeState.Started));
                Assert.Equal(RuntimeUnlockPathProbeState.Acked,
                    Assert.IsType<RuntimeUnlockTransactionSnapshot>(GetPrivateField(transport, "runtimeUnlockTransactionState")).PathProbeState);
            }
            else if (lifecycleChange == "raw_transfer_conflict")
            {
                var currentTransaction = Assert.IsType<RuntimeUnlockTransactionSnapshot>(GetPrivateField(transport, "runtimeUnlockTransactionState"));
                SetPrivateField(transport, "runtimeUnlockTransactionState", currentTransaction with { TransferId = "different-transfer" });
            }
            else if (lifecycleChange == "raw_lease_transfer_conflict")
            {
                var currentLease = Assert.IsType<RuntimeUnlockTunaPathLeaseSnapshot>(GetPrivateField(transport, "runtimeUnlockTunaPathLeaseState"));
                SetPrivateField(transport, "runtimeUnlockTunaPathLeaseState", currentLease with { TransferId = "different-transfer" });
            }
            else
            {
                var proof = GetPrivateField(transport, "runtimeUnlockOfferProofState")!;
                proof.GetType().GetProperty("AuthorityMode")!.SetValue(proof, GetRuntimeUnlockOfferAuthorityModeForTests("InstalledRetry"));
            }
            var sourceBeforeEof = SessionFileTransferV4SenderTests.CaptureInitialActivationNknSenderForTest(context);
            lane.SetAvailable(false, sidecarReason);
            Assert.Empty(observations);
            if (lifecycleChange != "probe_acked")
            {
                Assert.Equal(SnapshotIdentity(sourceBeforeEof),
                    SnapshotIdentity(SessionFileTransferV4SenderTests.CaptureInitialActivationNknSenderForTest(context)));
            }
            return;
        }

        // This is the real subscribed sidecar-down callback, not the downstream
        // service logging helper. The probe is already Started before the EOF.
        lane.SetAvailable(false, sidecarReason);
        if (sidecarReason == "listener_terminal_tuna_stream_eof")
        {
            // Reproduce terminal EOF followed by remote_closed; the later
            // callback must not recover or duplicate the original observation.
            lane.SetAvailable(false, "remote_closed");
        }
        Assert.Single(observations);
        Assert.Equal(NknSignalingTransport.NormalizeAccelerationSidecarResetReason(sidecarReason), observations[0].SidecarReason);
        if (beforeOriginalDelivery is not null)
        {
            Assert.NotNull(delivered);
            Assert.NotEqual(RuntimeUnlockInitialActivationPreCommitProbeFailureStatus.Accepted, delivered.Decision.Status);
            Assert.Equal(SnapshotIdentity(beforeOriginalDelivery),
                SnapshotIdentity(SessionFileTransferV4SenderTests.CaptureInitialActivationNknSenderForTest(context)));
            var stale = new RuntimeUnlockInitialActivationPreCommitProbeFailureObservedEventArgs(observations[0]);
            typeof(SessionFileTransferService).GetMethod("OnRuntimeUnlockInitialActivationPreCommitProbeFailureObserved",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [transport, stale]);
            Assert.NotEqual(RuntimeUnlockInitialActivationPreCommitProbeFailureStatus.Accepted, stale.Decision.Status);
            Assert.Equal(SnapshotIdentity(beforeOriginalDelivery),
                SnapshotIdentity(SessionFileTransferV4SenderTests.CaptureInitialActivationNknSenderForTest(context)));
            return;
        }
        Assert.Equal(checkMalformedObservations ? 30 : 0, rejectedCases.Count);
        var failed = Assert.IsType<RuntimeUnlockTransactionSnapshot>(GetPrivateField(transport, "runtimeUnlockTransactionState"));
        Assert.True(failed.IsTerminal);
        Assert.Equal(RuntimeUnlockPathProbeState.Failed, failed.PathProbeState);
        Assert.False(((IRuntimeUnlockRouteCommitProofProvider)transport).TryGetRuntimeUnlockRouteCommitProof(
            sessionId, transferId, out _));

        var afterFailure = SessionFileTransferV4SenderTests.CaptureInitialActivationNknSenderForTest(context);
        // Baseline red: transport failed the Started probe, but this exact
        // Prepared/Draining authority remains current instead of rolling back.
        Assert.Null(afterFailure.PersistentState.FileTransferRouteTransition.CurrentOperation);
        var retired = Assert.IsType<FileTransferRouteTransitionOperation>(
            afterFailure.PersistentState.FileTransferRouteTransition.LastTerminalOperation);
        Assert.Equal(transition.Authority, retired.Authority);
        Assert.Equal(FileTransferRouteTransitionState.Invalidated, retired.State);
        var disposition = Assert.IsType<FileTransferRouteTransitionDispositionReceipt>(retired.DispositionReceipt);
        Assert.Equal(FileTransferRouteTransitionDispositionKind.RolledBack, disposition.Command.Kind);
        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied, disposition.Status);
        Assert.Null(afterFailure.CurrentRuntimeUnlockPendingPreCommitProbe);
        await WaitUntilAsync(() => !Assert.IsType<bool>(ContextValue(context, "PullTransportPaused")), TimeSpan.FromSeconds(3));
        Assert.Equal(original.CurrentRuntimeOwner, afterFailure.CurrentRuntimeOwner);
        Assert.Equal(original.CurrentRoutePublicationToken, afterFailure.CurrentRoutePublicationToken);
        Assert.Same(dataSession, ContextValue(context, "DataSession"));
        Assert.Equal(original.CurrentDataSessionGeneration, afterFailure.CurrentDataSessionGeneration);
        Assert.Equal(original.CurrentDataSessionIdentity, afterFailure.CurrentDataSessionIdentity);
        Assert.False(FileTransferCoordinator.TryGetCurrentRuntimeUnlockRetryDispatchInstallReceipt(afterFailure, out _));
        lane.SetAvailable(false, sidecarReason);
        Assert.Single(observations);
        var duplicate = SessionFileTransferV4SenderTests.CaptureInitialActivationNknSenderForTest(context);
        Assert.Equal(SnapshotIdentity(afterFailure), SnapshotIdentity(duplicate));
        Assert.DoesNotContain("event=filetransfer_route_selected; route=file_tuna_v4", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
    }

    private static object? ContextValue(object value, string name)
        => value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value);

    private static (long Generation, string? Fingerprint) SnapshotIdentity(FileTransferCoordinatorState state)
    {
        // Keep the large persistent value boxed; generic value equality copies it
        // repeatedly on the already active transport callback stack.
        var persistent = ContextValue(state, "PersistentState")!;
        return (Assert.IsType<long>(ContextValue(persistent, "SnapshotStateGeneration")),
            (string?)ContextValue(persistent, "SnapshotStateFingerprint"));
    }

    private static IEnumerable<(string Name, RuntimeUnlockInitialActivationPreCommitProbeFailureObservation Observation)>
        InvalidInitialActivationFailures(RuntimeUnlockInitialActivationPreCommitProbeFailureObservation exact,
            IFileTransferDataSession wrongDataSession)
    {
        RuntimeUnlockInitialActivationPreCommitProbeFailureObservation WithAuthority(FileTransferRouteTransitionAuthority authority)
            => exact with { PauseBinding = exact.PauseBinding with { Command = exact.PauseBinding.Command with { Authority = authority } } };
        var authority = exact.Authority;
        var activation = Assert.IsType<FileTransferTunaActivationTransitionProofBoundary>(authority.ProofBoundary);
        yield return ("source_owner", WithAuthority(authority with { SourceRuntimeOwner = authority.SourceRuntimeOwner with { RuntimeGeneration = authority.SourceRuntimeOwner.RuntimeGeneration + 1 } }));
        yield return ("source_publication", WithAuthority(authority with { SourceRoutePublication = authority.SourceRoutePublication with { PublicationGeneration = authority.SourceRoutePublication.PublicationGeneration + 1 } }));
        yield return ("transition", WithAuthority(authority with { TransitionGeneration = authority.TransitionGeneration + 1 }));
        yield return ("direction", WithAuthority(authority with { Direction = FileTransferDirection.Inbound }));
        yield return ("terminal", WithAuthority(authority with { SourceTerminalGeneration = authority.SourceTerminalGeneration + 1 }));
        yield return ("leg", WithAuthority(authority with { SourceLegGeneration = authority.SourceLegGeneration + 1 }));
        yield return ("transport_epoch", WithAuthority(authority with { SourceTransportEpoch = authority.SourceTransportEpoch + 1 }));
        yield return ("bridge_generation", WithAuthority(authority with { SourceBridgeRecoveryGeneration = authority.SourceBridgeRecoveryGeneration + 1 }));
        yield return ("admission", WithAuthority(authority with { ProofBoundary = activation with { RuntimeUnlockAdmission = activation.RuntimeUnlockAdmission with { AdmissionGeneration = activation.RuntimeUnlockAdmission.AdmissionGeneration + 1 } } }));
        yield return ("pending_probe", WithAuthority(authority with { ProofBoundary = activation with { PendingPreCommitProbe = activation.PendingPreCommitProbe with { PendingGeneration = activation.PendingPreCommitProbe.PendingGeneration + 1 } } }));
        yield return ("launch", exact with { Probe = exact.Probe with { LaunchGeneration = exact.Probe.LaunchGeneration + 1 } });
        yield return ("transaction", exact with { Probe = exact.Probe with { TransactionGeneration = exact.Probe.TransactionGeneration + 1 } });
        yield return ("offer", exact with { Probe = exact.Probe with { OfferGeneration = exact.Probe.OfferGeneration + 1 } });
        yield return ("lease", exact with { Probe = exact.Probe with { TunaPathLeaseGeneration = exact.Probe.TunaPathLeaseGeneration + 1 } });
        yield return ("listener", exact with { Probe = exact.Probe with { TunaPathLeaseListenerRunId = "wrong-listener" } });
        yield return ("probe", exact with { Probe = exact.Probe with { PathProbeId = "wrong-probe" } });
        yield return ("acked", exact with { Probe = exact.Probe with { PathProbeState = "Acked", PathProbeAckedUtcMs = 1 } });
        yield return ("data_session_reference", exact with { SourceDataSession = wrongDataSession });
        yield return ("data_session_identity", exact with { SourceDataSessionIdentity = "wrong-data-session" });
        yield return ("data_session_generation", exact with { SourceDataSessionGeneration = exact.SourceDataSessionGeneration + 1 });
        yield return ("checkpoint_request", exact with { SourceCheckpointRequestId = "wrong-checkpoint" });
        yield return ("checkpoint_generation", exact with { SourceCheckpointGeneration = exact.SourceCheckpointGeneration + 1 });
        yield return ("pause_receipt", exact with { PauseBinding = exact.PauseBinding with { Status = FileTransferRouteTransitionPauseBindStatus.Rejected } });
        yield return ("transfer", exact with { Probe = exact.Probe with { TransferId = "wrong-transfer" } });
        yield return ("session", exact with { Probe = exact.Probe with { SessionId = "wrong-session" } });
        yield return ("probe_transport", exact with { Probe = exact.Probe with { PathProbeTransport = FileTransferTransportKind.RegularNkn } });
        yield return ("sidecar_reason", exact with { SidecarReason = string.Empty });
        yield return ("null_probe", exact with { Probe = null! });
    }
}

public sealed partial class SessionFileTransferV4SenderTests
{
    internal static object CreateInitialActivationNknSenderForTest(SessionFileTransferService service,
        NknSignalingTransport transport, IFileTransferDataSession dataSession, string sessionId, string transferId)
    {
        var context = CreateRegularNknV4OutboundContext(transferId, 0, 0, 64, DateTimeOffset.UtcNow, seedRuntimeOwner: false);
        SetPrivateProperty(context, "SessionId", sessionId);
        Assert.IsAssignableFrom<IFileTransferDataSession>(GetPrivatePropertyValue(context, "DataSession")).Dispose();
        SetPrivateProperty(context, "DataSession", dataSession);
        SetPrivateProperty(context, "DataSessionGeneration", 1L);
        SetPrivateProperty(context, "V4LastStateEpoch", 1);
        SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", -1);
        typeof(SessionFileTransferService).GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);
        typeof(SessionFileTransferService).GetMethod("SubscribeDataSessionEvents", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [dataSession]);
        StartCoordinatorSnapshotStreamForTest(context);
        Assert.NotNull(typeof(SessionFileTransferService).GetMethod("EnsureOutboundTransferLegLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "initial_activation_test_start", null, null]));
        var publish = typeof(SessionFileTransferService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "TryPublishRouteSelectedIfAuthorizedLocked" &&
                method.GetParameters()[0].ParameterType == context.GetType() && method.GetParameters()[4].ParameterType.IsByRef);
        object?[] arguments = [context, FileTransferRouteResolverInput.FromTransport(transport), "initial_activation_test_start", "initial_activation_test_start", null, null];
        Assert.True(Assert.IsType<bool>(publish.Invoke(service, arguments)));
        var publication = Assert.IsType<RoutePublicationToken>(arguments[4]);
        var hint = typeof(SessionFileTransferService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "TryPublishRouteHintIfAuthorizedLocked" && method.GetParameters()[0].ParameterType == context.GetType());
        Assert.True(Assert.IsType<bool>(hint.Invoke(service,
            [context, "initial_activation_test_hint", "initial_activation_test_start", "service_initial_route_publication", null, null, null, publication])));
        return context;
    }

    internal static FileTransferCoordinatorState CaptureInitialActivationNknSenderForTest(object context)
        => CreateCoordinatorStateForTest(context);

    internal static void ReplaceInitialActivationNknDataSessionForTest(SessionFileTransferService service,
        object context, IFileTransferDataSession dataSession)
    {
        var gate = typeof(SessionFileTransferService).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        lock (gate)
        {
            Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService)
                .GetMethod("ReplaceOutboundDataSessionLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, dataSession])));
        }
    }
}
