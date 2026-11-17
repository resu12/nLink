using System.Reflection;
using System.Runtime.CompilerServices;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferCoordinatorSuite", "FallbackRepairHandoff")]
    public void FallbackRepairRetryClosure_ExactStopPreservesPublicationAndRestoresGaps(bool peerStop)
    {
        var state = CreateAdmittedRepairHandoffForClosureTest();
        var drain = state.PersistentState.FallbackTailYield.RepairHandoff!.DrainReceipt!;
        var observation = new FileTransferFallbackRepairRetryStopObservation(drain, state.CurrentRuntimeUnlockAdmission!.Value, 3,
            peerStop ? FileTransferFallbackRepairRetryStopReason.PeerSwitchOff : FileTransferFallbackRepairRetryStopReason.LocalSwitchOff);
        var closed = FileTransferCoordinator.CloseStoppedInboundFallbackRepairRetry(state, observation);
        Assert.True(closed.Accepted, closed.Reason);
        Assert.Null(closed.State.CurrentRuntimeUnlockAdmission);
        Assert.Equal(state.CurrentRuntimeOwner, closed.State.CurrentRuntimeOwner);
        Assert.Equal(state.CurrentRoutePublicationToken, closed.State.CurrentRoutePublicationToken);
        Assert.Equal(observation, closed.State.PersistentState.FallbackTailYield.RepairHandoff!.RetryClosure);
        var restored = FileTransferCoordinator.RestoreInboundFallbackRepairHandoff(closed.State, drain.Capture.Command);
        Assert.True(restored.Accepted, restored.Reason);
        Assert.Equal(drain.Checkpoint, restored.RestoreCommand!.Checkpoint);
        Assert.Equal(drain.MissingRanges, restored.RestoreCommand.MissingRanges);
        AssertRejectedRepairHandoff(restored.State,
            FileTransferCoordinator.CloseStoppedInboundFallbackRepairRetry(restored.State, observation));
    }

    [Theory]
    [InlineData("admission")] [InlineData("provider")] [InlineData("hold")] [InlineData("pause")]
    [InlineData("checkpoint")] [InlineData("gaps")] [InlineData("session")] [InlineData("reason")]
    [InlineData("service")] [InlineData("publication")] [InlineData("terminal")]
    [InlineData("new_admission")] [InlineData("new_provider")] [InlineData("probe")]
    [Trait("FileTransferCoordinatorSuite", "FallbackRepairHandoff")]
    public void FallbackRepairRetryClosure_StaleStopCannotCloseAdmission(string mutation)
    {
        var state = CreateAdmittedRepairHandoffForClosureTest();
        var drain = state.PersistentState.FallbackTailYield.RepairHandoff!.DrainReceipt!;
        var observation = new FileTransferFallbackRepairRetryStopObservation(drain, state.CurrentRuntimeUnlockAdmission!.Value, 3,
            FileTransferFallbackRepairRetryStopReason.LocalSwitchOff);
        if (mutation == "probe")
        {
            var pending = RecordPendingPreCommitProbe(state, observation.Admission);
            Assert.NotNull(pending.State.CurrentRuntimeUnlockPendingPreCommitProbe);
            state = pending.State;
        }
        observation = mutation switch
        {
            "admission" => observation with { Admission = observation.Admission with { AdmissionGeneration = 999 } },
            "provider" => observation with { DataSessionProviderGeneration = 2 },
            "hold" => observation with { Drain = drain with { Capture = drain.Capture with
                { Command = drain.Capture.Command with { Generation = 999 } } } },
            "pause" => observation with { Drain = drain with { Capture = drain.Capture with { PauseInstance = 999 } } },
            "checkpoint" => observation with { Drain = drain with { Checkpoint = drain.Checkpoint with { StateEpoch = 999 } } },
            "gaps" => observation with { Drain = drain with { MissingRanges = [] } },
            "reason" => observation with { Reason = (FileTransferFallbackRepairRetryStopReason)999 },
            _ => observation,
        };
        state = mutation switch
        {
            "session" => state with { CurrentDataSessionIdentity = "replacement" },
            "service" => state with { CurrentDataSessionGeneration = 99 },
            "publication" => state with { CurrentRoutePublicationToken = null },
            "terminal" => state with { IsTerminal = true },
            "new_admission" => state with { CurrentRuntimeUnlockAdmission = observation.Admission with { AdmissionGeneration = 999 } },
            "new_provider" => state with { CurrentDataSessionProviderGeneration = 4 },
            _ => state,
        };
        var rejected = FileTransferCoordinator.CloseStoppedInboundFallbackRepairRetry(state, observation);
        AssertRejectedRepairHandoff(state, rejected);
        Assert.Equal(state.CurrentRuntimeUnlockAdmission, rejected.State.CurrentRuntimeUnlockAdmission);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static FileTransferCoordinatorState CreateAdmittedRepairHandoffForClosureTest()
        => AdmitRepairHandoffForClosureTest(DrainRepairHandoffForClosureTest(CreateInboundRepairHandoffSource()));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorState DrainRepairHandoffForClosureTest(
        (FileTransferCoordinatorState State, FileTransferFallbackTailPeerProofReceipt Peer) fixture)
    {
        var source = fixture.State with { CurrentDataSessionProviderGeneration = 3 };
        var prepared = FileTransferCoordinator.PrepareInboundFallbackRepairHandoff(source, fixture.Peer);
        var capture = CreateInboundRepairHandoffCapture(prepared.HoldCommand!, source.CurrentDataSessionProviderGeneration);
        var captured = FileTransferCoordinator.CaptureInboundFallbackRepairHandoff(prepared.State, capture);
        var drained = FileTransferCoordinator.DrainInboundFallbackRepairHandoff(captured.State, CreateInboundRepairHandoffDrain(capture));
        Assert.True(drained.Accepted, drained.Reason);
        return drained.State;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FileTransferCoordinatorState AdmitRepairHandoffForClosureTest(FileTransferCoordinatorState state)
    {
        var yielded = MarkFallbackTailQuiescent(state, state.RouteSelection, state.CurrentLeg!.TransportEpochId, 12_106, 12_483);
        var local = FileTransferCoordinator.CreateFallbackTailLocalProofReceipt(yielded.State,
            yielded.FallbackTailYieldDecision!.Value, "repair-closure-local", "repair-closure-frame", 50_001);
        var paired = FileTransferCoordinator.ApplyFallbackTailLocalProofReceipt(yielded.State, local);
        Assert.NotNull(paired.PairedRelease);
        var admitted = RequestRuntimeUnlockAdmission(paired.State, paired.State.CurrentRuntimeOwner);
        Assert.True(admitted.RuntimeUnlockAdmissionDecision!.Value.Granted);
        return admitted.State;
    }
}

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData("exact")] [InlineData("stale_observation")]
    [InlineData("provider_replaced")] [InlineData("stream_replaced")] [InlineData("new_admission")]
    [Trait("FileTransferRuntimeSuite", "FallbackRepairHandoff")]
    public async Task FallbackRepairRetryClosure_ExpiredStoppedRetryRestoresExactRuntime(string scenario)
    {
        using var transport = new StoppedRepairRetryTransport("session_fallback_repair_write");
        using var fixture = CreateRuntimeRepairHandoffFixture(transport: transport);
        ApplyRuntimeRepairHandoffPeer(fixture);
        await WaitUntilAsync(() => GetRuntimeRepairHandoff(fixture)?.State == FileTransferFallbackRepairHandoffState.Drained, 3000);
        // Drained is published before the same callback finishes its admission
        // recheck. Join that critical section before installing this test's retry.
        lock (GetServiceGate(fixture.Service))
        {
            var admission = SeedRuntimeUnlockAdmissionForTest(fixture.Context, FileTransferDirection.Inbound, "stopped_retry");
            var before = CreateCoordinatorStateForTest(fixture.Context);
            var drain = GetRuntimeRepairHandoff(fixture)!.DrainReceipt!;
            var written = fixture.Written;
            var paused = GetPrivateProperty<DateTimeOffset>(fixture.Context, "PullTransportPausedSinceUtc");
            Assert.Equal(TimeSpan.FromSeconds(15), GetPrivateProperty<DateTimeOffset>(fixture.Context, "PullTransportGraceDeadlineUtc") - paused);
            transport.Observation = new(drain, scenario == "stale_observation" ? admission with { AdmissionGeneration = 999 } : admission,
                before.CurrentDataSessionProviderGeneration, FileTransferFallbackRepairRetryStopReason.LocalSwitchOff);
            var expectedAdmission = admission;
            transport.BeforeReturn = () =>
            {
                if (scenario == "provider_replaced") { fixture.Invalidate("provider"); }
                if (scenario == "stream_replaced") { fixture.Invalidate("stream"); }
                if (scenario == "new_admission")
                {
                    expectedAdmission = SeedRuntimeUnlockAdmissionForTest(fixture.Context, FileTransferDirection.Inbound, "new_retry");
                }
            };
            Assert.False(InvokeRuntimeRepairHandoffRestore(fixture));
            Assert.Equal(0, transport.Observations);
            SetPrivateProperty(fixture.Context, "PullTransportGraceDeadlineUtc", DateTimeOffset.UtcNow.AddMilliseconds(-1));
            Assert.Equal(scenario == "exact", InvokeRuntimeRepairHandoffRestore(fixture));
            var after = CreateCoordinatorStateForTest(fixture.Context);
            Assert.Equal(before.CurrentRuntimeOwner, after.CurrentRuntimeOwner);
            Assert.Equal(before.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
            Assert.Same(written, fixture.Written);
            Assert.Same(scenario == "stream_replaced" ? fixture.OtherStream : fixture.Stream,
                GetPrivatePropertyValue(fixture.Context, "WriteStream"));
            if (scenario != "exact")
            {
                Assert.Equal(expectedAdmission, after.CurrentRuntimeUnlockAdmission);
                Assert.True(GetPrivateProperty<bool>(fixture.Context, "PullTransportPaused"));
                Assert.Null(GetRuntimeRepairHandoff(fixture)!.RestoreCommand);
                Assert.Null(GetRuntimeRepairHandoff(fixture)!.RetryClosure);
            }
            else
            {
                Assert.Null(after.CurrentRuntimeUnlockAdmission);
                Assert.False(GetPrivateProperty<bool>(fixture.Context, "PullTransportPaused"));
                Assert.Equal(drain.Checkpoint, GetRuntimeRepairHandoff(fixture)!.RestoreCommand!.Checkpoint);
                Assert.Equal(drain.MissingRanges, GetRuntimeRepairHandoff(fixture)!.RestoreCommand!.MissingRanges);
            }
        }
        if (scenario == "exact")
        {
            await fixture.WriteAsync();
            Assert.Equal(101, fixture.Frontier);
        }
    }

    private sealed class StoppedRepairRetryTransport(string sessionId) : LoopbackFileTransferTransport(sessionId),
        IFileTransferFallbackRepairRetryStopObserver
    {
        public FileTransferFallbackRepairRetryStopObservation? Observation { get; set; }
        public Action? BeforeReturn { get; set; }
        public int Observations { get; private set; }
        public FileTransferFallbackRepairRetryStopObservation? ObserveStoppedFallbackRepairRetry(
            FileTransferFallbackRepairHandoffDrainReceipt drain, RuntimeUnlockAdmissionToken admission, long provider)
        {
            Observations++;
            BeforeReturn?.Invoke();
            return Observation;
        }
    }
}

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "FallbackRepairRetryClosure")]
    public async Task FallbackRepairRetryClosure_StopPreservesSameSessionReplacementBetweenLocks(bool advanceGeneration)
    {
        FakeNknClient.ResetNetwork();
        var client = new FakeNknClient("host.repair-replacement.address");
        var peerClient = new FakeNknClient("peer.repair-replacement.address");
        using var host = new NknSignalingTransport(client, NknTransportOptions.Load(), new NknIdentity("replacement", client.Address),
            NknTunaAccelerationOptions.Disabled, new FakeNknAccelerationLane());
        using var peer = new NknSignalingTransport(peerClient, NknTransportOptions.Load(), new NknIdentity("replacement-peer", peerClient.Address),
            NknTunaAccelerationOptions.Disabled, new FakeNknAccelerationLane());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sessionId = await ApproveNknSessionAsync(host, peer, timeout.Token,
            InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
        var original = RuntimeUnlockTransactionSnapshot.Idle;
        foreach (var kind in new[] { RuntimeUnlockTransactionEventKind.OfferGenerationCreated,
            RuntimeUnlockTransactionEventKind.AnswerReceived, RuntimeUnlockTransactionEventKind.PathProbeAcked })
        {
            original = RuntimeUnlockTransaction.Apply(new(kind, sessionId, "repair-replacement-transfer", 7, 11,
                "replacement-test", PathProbeId: "replacement-probe", PathProbeTransport: FileTransferTransportKind.Tuna), original).State;
        }
        Assert.Equal(RuntimeUnlockTransactionState.RouteCommitPending, original.State);
        var replacement = original with
        {
            TransactionGeneration = advanceGeneration ? 8 : original.TransactionGeneration,
            OfferGeneration = advanceGeneration ? 12 : original.OfferGeneration,
        };
        Assert.NotSame(original, replacement);
        Assert.Equal(original.SessionId, replacement.SessionId);
        Assert.Equal(original.TransferId, replacement.TransferId);
        if (!advanceGeneration) Assert.Equal(original, replacement);

        // Adapter diagnostics only: no route, retry or coordinator authority is
        // installed. Completing this existing proof emits a synchronous callback
        // between reset's capture and retirement locks; no timing race is used.
        SetPrivateField(host, "runtimeUnlockTransactionState", original);
        SetPrivateField(host, "accelerationSessionId", sessionId);
        SetPrivateField(host, "accelerationNegotiationSlotGeneration", 41L);
        SetPrivateField(host, "transportAccelerationStatusReason", "replacement_fixture_armed");
        Assert.True(Assert.IsType<bool>(InvokePrivateMethod(host, "StartTunaFallbackProofIfNeeded",
            "replacement-fixture", sessionId, NknAccelerationLaneKind.Screen)));
        var accelerationGate = GetPrivateField(host, "accelerationGate")!;
        var replacements = 0;
        host.TransportAccelerationStateChanged += ReplaceBetweenResetLocks;
        try
        {
            await host.StopAccelerationAsync("header_switch_off", timeout.Token);
        }
        finally { host.TransportAccelerationStateChanged -= ReplaceBetweenResetLocks; }

        Assert.Equal(1, replacements);
        Assert.Equal(0L, Assert.IsType<long>(GetPrivateField(host, "accelerationNegotiationSlotGeneration")));
        Assert.Same(replacement, GetPrivateField(host, "runtimeUnlockTransactionState"));
        Assert.Equal(RuntimeUnlockTransactionState.RouteCommitPending, replacement.State);
        Assert.True(replacement.RouteCommitPending);
        Assert.Null(replacement.RetiredReason);

        void ReplaceBetweenResetLocks(object? sender, NLink.Core.TransportAccelerationStateChangedEventArgs args)
        {
            if (args.Reason != "header_switch_off") return;
            host.TransportAccelerationStateChanged -= ReplaceBetweenResetLocks;
            Assert.Same(host, sender);
            Assert.False(args.IsActive);
            Assert.Null(args.PendingFileTransfer);
            Assert.False(Monitor.IsEntered(accelerationGate));
            lock (accelerationGate)
            {
                Assert.Same(original, GetPrivateField(host, "runtimeUnlockTransactionState"));
                Assert.Null(GetPrivateField(host, "accelerationSessionId")); // Capture lock has completed.
                Assert.Equal(41L, Assert.IsType<long>(GetPrivateField(host, "accelerationNegotiationSlotGeneration"))); // Retirement lock has not.
                Assert.Null(GetPrivateField(host, "tunaFallbackProofState"));
                SetPrivateField(host, "runtimeUnlockTransactionState", replacement);
                replacements++;
            }
        }
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(true, false, false)]
    [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, true, true)] [InlineData(true, true, true)]
    [Trait("FileTransferInfraSuite", "FallbackRepairRetryClosure")]
    public async Task FallbackRepairRetryClosure_TransportRequiresExactStoppedIdleSession(bool peerStop, bool publicStop, bool foreignTransaction)
    {
        FakeNknClient.ResetNetwork();
        var client = new FakeNknClient("host.repair-closure.address");
        var peerClient = new FakeNknClient("peer.repair-closure.address");
        using var host = new NknSignalingTransport(client, NknTransportOptions.Load(), new NknIdentity("closure", client.Address),
            NknTunaAccelerationOptions.Disabled, new FakeNknAccelerationLane());
        using var peer = new NknSignalingTransport(peerClient, NknTransportOptions.Load(), new NknIdentity("closure-peer", peerClient.Address),
            NknTunaAccelerationOptions.Disabled, new FakeNknAccelerationLane());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sessionId = await ApproveNknSessionAsync(host, peer, timeout.Token,
            InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
        const string transferId = "repair-closure-transfer";
        using (await host.OpenFileTransferDataSessionAsync(sessionId, "previous", timeout.Token)) { }
        using var session = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, timeout.Token);
        var generation = ((IFileTransferDataSessionGenerationProvider)session).Generation;
        var state = FileTransferCoordinatorTests.CreateAdmittedRepairHandoffForClosureTest();
        var drain = state.PersistentState.FallbackTailYield.RepairHandoff!.DrainReceipt!;
        var source = drain.Capture.Command.Source;
        var owner = source.RuntimeOwner with { SessionId = sessionId, TransferId = transferId };
        var publication = source.RoutePublication!.Value with { SessionId = sessionId, TransferId = transferId, RuntimeOwner = owner };
        source = source with { SessionId = sessionId, TransferId = transferId, RuntimeOwner = owner, RoutePublication = publication };
        drain = drain with { Capture = drain.Capture with { Command = drain.Capture.Command with { Source = source } } };
        var admission = state.CurrentRuntimeUnlockAdmission!.Value with { SessionId = sessionId, TransferId = transferId, RuntimeOwner = owner };
        ((IFileTransferRouteHintObserver)host).ObserveFileTransferRouteHint(CreateAuthorizedRouteHintNotification(
            sessionId, transferId, source.RouteToken, 6, "repair-closure-test", publication.PublicationGeneration,
            publication.RuntimeGeneration, publication.TerminalGeneration, publication.LegGeneration, publication.LiveRouteEpoch,
            publication.RouteCommitGeneration, direction: FileTransferDirection.Inbound,
            fallbackTransportEpoch: source.TransportEpoch, fallbackBridgeRecoveryGeneration: source.BridgeRecoveryGeneration));
        var observer = (IFileTransferFallbackRepairRetryStopObserver)host;
        Assert.Null(observer.ObserveStoppedFallbackRepairRetry(drain, admission, generation));
        if (publicStop)
        {
            // Seed only the adapter's diagnostic transaction, not route or retry authority.
            var transaction = RuntimeUnlockTransactionSnapshot.Idle;
            foreach (var kind in new[] { RuntimeUnlockTransactionEventKind.OfferGenerationCreated,
                RuntimeUnlockTransactionEventKind.AnswerReceived, RuntimeUnlockTransactionEventKind.PathProbeAcked })
            {
                transaction = RuntimeUnlockTransaction.Apply(new(kind, foreignTransaction ? sessionId + "-retired-session" : sessionId, transferId, 7, 11,
                    "stopped-closure-test", PathProbeId: "stopped-closure-probe",
                    PathProbeTransport: FileTransferTransportKind.Tuna), transaction).State;
            }
            Assert.Equal(RuntimeUnlockTransactionState.RouteCommitPending, transaction.State);
            SetField("runtimeUnlockTransactionState", transaction);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            peer.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            await (peerStop ? peer : host).StopAccelerationAsync("header_switch_off", timeout.Token);
            await WaitUntilAsync(() => Equals(sessionId, GetPrivateField(host,
                peerStop ? "accelerationPeerUserStoppedSessionId" : "accelerationUserStoppedSessionId")),
                TimeSpan.FromSeconds(3));
            var retired = Assert.IsType<RuntimeUnlockTransactionSnapshot>(GetPrivateField(host, "runtimeUnlockTransactionState"));
            if (foreignTransaction)
            {
                Assert.Same(transaction, retired);
                Assert.Null(observer.ObserveStoppedFallbackRepairRetry(drain, admission, generation));
                return;
            }
            Assert.Equal(RuntimeUnlockTransactionState.Retired, retired.State);
            Assert.Equal(transaction.SessionId, retired.SessionId);
            Assert.Equal(transaction.TransactionGeneration, retired.TransactionGeneration);
            Assert.Equal(transaction.OfferGeneration, retired.OfferGeneration);
            Assert.False(retired.RouteCommitPending);
        }
        else
        {
            SetField(peerStop ? "accelerationPeerUserStoppedSessionId" : "accelerationUserStoppedSessionId", sessionId);
        }
        Assert.Equal(peerStop ? FileTransferFallbackRepairRetryStopReason.PeerSwitchOff : FileTransferFallbackRepairRetryStopReason.LocalSwitchOff,
            observer.ObserveStoppedFallbackRepairRetry(drain, admission, generation)!.Reason);
        foreach (var (field, busy, idle) in new (string, object?, object?)[]
        {
            ("accelerationNegotiationSlotGeneration", 1L, 0L),
            ("accelerationNegotiationScheduled", 1, 0),
            ("outboundAccelerationOfferNonce", "busy", null),
            ("pendingAccelerationAnswerAckNonce", "busy", null),
            ("runtimeUnlockTransactionState", RuntimeUnlockTransactionSnapshot.Idle with { State = RuntimeUnlockTransactionState.OfferPreparing }, RuntimeUnlockTransactionSnapshot.Idle),
        })
        {
            SetField(field, busy);
            Assert.Null(observer.ObserveStoppedFallbackRepairRetry(drain, admission, generation));
            SetField(field, idle);
        }
        SetField(peerStop ? "accelerationPeerUserStoppedSessionId" : "accelerationUserStoppedSessionId", null);
        Assert.Null(observer.ObserveStoppedFallbackRepairRetry(drain, admission, generation));
        SetField(peerStop ? "accelerationPeerUserStoppedSessionId" : "accelerationUserStoppedSessionId", sessionId);
        Assert.Null(observer.ObserveStoppedFallbackRepairRetry(drain, admission, generation - 1));
        session.Dispose();
        using var replacement = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, timeout.Token);
        Assert.Null(observer.ObserveStoppedFallbackRepairRetry(drain, admission, generation));

        void SetField(string field, object? value) => typeof(NknSignalingTransport)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, value);
    }
}
