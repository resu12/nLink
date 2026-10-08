using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Trait("FileTransferInfraSuite", "SessionPreparationLane")]
[Trait("Area", "Core")]
public sealed class NknTunaLaneOwnershipTests
{
    [Theory]
    [InlineData(false, "stop")]
    [InlineData(true, "stop")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "replace")]
    [InlineData(true, "replace")]
    [InlineData(false, "dispose")]
    [InlineData(true, "dispose")]
    public async Task RetiredStart_CannotPublishOrStopSuccessor(bool listener, string change)
    {
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var pendingClient = ReadyClient();
        using var owner = Owner();
        using var canceled = new CancellationTokenSource();
        var old = Begin(lane);
        switch (change)
        {
            case "stop": await lane.StopAsync("stop_during_start", CancellationToken.None); break;
            case "cancel": canceled.Cancel(); break;
            case "replace": _ = Begin(lane); break;
            case "dispose": lane.Dispose(); break;
        }
        var stops = supervisor.GlobalStops;
        Assert.False(Install(lane, pendingClient, listener, owner, old, canceled.Token, supervisor.Request));
        Assert.False(pendingClient.IsAvailable);
        Assert.Equal(stops, supervisor.GlobalStops);
        if (change == "dispose") return;

        using var successor = ReadyClient();
        using var nextOwner = Owner();
        var next = Begin(lane);
        Assert.True(Install(lane, successor, false, nextOwner, next));
        Finish(lane, old);
        Assert.Null(lane.CaptureCurrentLaneLease()); // Old finally cannot release successor's pending slot.
        Finish(lane, next);
        var receipt = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());
        using var late = ReadyClient();
        Assert.False(Install(lane, late, listener, owner, old, CancellationToken.None, supervisor.Request));
        Assert.Equal(receipt, lane.CaptureCurrentLaneLease());
        Assert.True(successor.IsAvailable);
        Assert.Equal(stops, supervisor.GlobalStops);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactCleanup_StopsOnlyTheCapturedResourceAndIsIdempotent(bool listener)
    {
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var client = ReadyClient();
        using var owner = Owner();
        InstallReady(lane, client, listener, owner, supervisor.Request);
        var receipt = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());
        var unavailable = 0;
        lane.StateChanged += (_, e) => { if (!e.IsAvailable) unavailable++; };
        Assert.True(lane.TryStopCurrentLaneLease(receipt, "exact_cleanup"));
        Assert.False(lane.TryStopCurrentLaneLease(receipt, "duplicate_cleanup"));
        Assert.False(client.IsAvailable);
        Assert.Null(lane.CaptureCurrentLaneLease());
        Assert.Equal(listener ? 1 : 0, supervisor.ExactStops);
        Assert.Equal(0, supervisor.GlobalStops);
        Assert.Equal(0, unavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedLease_RejectsPredecessorCleanupEvenForSamePhysicalConnection(bool listener)
    {
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var client = ReadyClient();
        using var owner = Owner();
        InstallReady(lane, client, listener, owner, supervisor.Request);
        var predecessor = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());
        Assert.True(lane.TryRetainCurrentLaneLease(predecessor, out var retained));
        Assert.Same(predecessor.Client, retained!.Client);
        Assert.True(retained.ClientGeneration > predecessor.ClientGeneration);
        Assert.False(lane.TryStopCurrentLaneLease(predecessor, "late_preparation_cleanup"));
        Assert.False(lane.TryRetainCurrentLaneLease(predecessor, out _));
        Assert.True(client.IsAvailable);
        Assert.True(lane.TryStopCurrentLaneLease(retained, "current_owner_cleanup"));
    }

    [Theory]
    [InlineData("lane")]
    [InlineData("client_generation")]
    [InlineData("start_generation")]
    [InlineData("client")]
    [InlineData("owner")]
    public void WrongLease_IsDiagnosticOnly(string change)
    {
        using var lane = CreateLane();
        using var client = ReadyClient();
        using var owner = Owner();
        using var otherOwner = Owner();
        using var otherClient = ReadyClient();
        InstallReady(lane, client, false, owner);
        var current = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());
        var wrong = change switch
        {
            "lane" => current with { LaneInstanceId = Guid.NewGuid() },
            "client_generation" => current with { ClientGeneration = current.ClientGeneration + 1 },
            "start_generation" => current with { StartGeneration = current.StartGeneration + 1 },
            "client" => current with { Client = otherClient },
            _ => current with { DialerOwner = otherOwner },
        };
        Assert.False(lane.TryStopCurrentLaneLease(wrong, "wrong_owner"));
        Assert.False(lane.TryRetainCurrentLaneLease(wrong, out _));
        Assert.Equal(current, lane.CaptureCurrentLaneLease());
        Assert.True(client.IsAvailable);
    }

    [Fact]
    public void SupervisorOwnershipChange_RejectsOldLaneCleanupWithoutDisposingClient()
    {
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var client = ReadyClient();
        using var owner = Owner();
        InstallReady(lane, client, true, owner, supervisor.Request);
        var old = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());
        Assert.True(supervisor.TryAdoptCurrentListener(old.Listener!, supervisor.Request));
        Assert.Null(lane.CaptureCurrentLaneLease());
        Assert.False(lane.TryStopCurrentLaneLease(old, "stale_supervisor_owner"));
        Assert.True(client.IsAvailable);
        Assert.Equal(0, supervisor.ExactStops);
    }

    [Fact]
    public void CleanupOutsideLaneLock_CannotStopReplacementInstalledDuringProcessShutdown()
    {
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var first = ReadyClient();
        using var successor = ReadyClient();
        using var owner = Owner();
        InstallReady(lane, first, true, owner, supervisor.Request);
        var old = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());
        supervisor.BeforeExactStop = () =>
        {
            Assert.False(Monitor.IsEntered(Field(lane, "gate")));
            InstallReady(lane, successor, false, owner);
        };
        Assert.True(lane.TryStopCurrentLaneLease(old, "stop_old"));
        Assert.True(successor.IsAvailable);
        Assert.Same(successor, lane.CaptureCurrentLaneLease()!.Client);
        Assert.Equal(0, supervisor.GlobalStops);
    }

    [Fact]
    public void SameAddressReplacement_CannotBeCapturedAsTheOldClientsPhysicalLease()
    {
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var client = ReadyClient();
        using var owner = Owner();
        InstallReady(lane, client, true, owner, supervisor.Request);
        var old = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());
        supervisor.ReplaceRun();
        Assert.Null(lane.CaptureCurrentLaneLease());
        Assert.False(lane.TryStopCurrentLaneLease(old, "old_run"));
        var replacement = supervisor.CaptureCurrentListenerAdoptionSnapshot();
        Assert.False(lane.TryAdoptCurrentListener(replacement!, supervisor.Request, out _));
        Assert.False(lane.TryStopCurrentLaneLease(old with { Listener = replacement }, "substituted_run"));
        Assert.Equal(replacement, supervisor.CaptureCurrentListenerAdoptionSnapshot());
        Assert.Equal(0, supervisor.ExactStops);
        Assert.True(client.IsAvailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListenerRetry_DuringPeerPaidDialerStartupCannotRetireStartOrDropPayload(bool listenerConfigured)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            using var supervisor = new ExactSupervisor(initiallyIdle: true);
            using var lane = CreateLane(listenerConfigured ? supervisor : null);
            using var client = new NknTunaSidecarClient(NknAccelerationLaneKind.File, 16);
            using var owner = Owner();
            // Hold the same non-preparation slot used by StartDialerSidecarAsync
            // while its real IPC client is still waiting for sidecar status.
            var dialStart = Begin(lane);
            var pendingDial = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Set(lane, "dialerStartTask", pendingDial.Task);
            var connect = client.ConnectAsync(((IPEndPoint)server.LocalEndpoint).ToString(),
                TimeSpan.FromSeconds(8), lifetime.Token);
            using var connection = await server.AcceptTcpClientAsync(lifetime.Token);

            for (var i = 0; i < 3; i++)
            {
                Assert.False(await lane.EnsureListenerSidecarConnectedAsync(supervisor.Request, lifetime.Token));
                Assert.Same(dialStart, Field(lane, "pendingLaneStart"));
                Assert.Equal(0, supervisor.GlobalStops);
            }

            await WriteStatusAsync(connection, lifetime.Token);
            await connect.WaitAsync(lifetime.Token);
            Assert.True(Install(lane, client, false, owner, dialStart));
            Finish(lane, dialStart);
            pendingDial.SetResult(true);
            Set(lane, "dialerStartTask", null!);
            var lease = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());

            var payload = Enumerable.Repeat((byte)7, 64 * 1024).ToArray();
            Assert.True(await lane.TrySendAsync(NknBridgeChannel.Bulk, payload, lifetime.Token));
            var frame = await NknTunaSidecarFrameProtocol.ReadFrameAsync(connection.GetStream(), lifetime.Token);
            if (frame.Type == NknTunaSidecarFrameType.Ping)
                frame = await NknTunaSidecarFrameProtocol.ReadFrameAsync(connection.GetStream(), lifetime.Token);
            Assert.Equal(NknTunaSidecarFrameType.Data, frame.Type);
            Assert.Equal(NknTunaSidecarLane.Bulk, frame.Lane);
            Assert.Equal(payload, frame.Payload);
            Assert.Equal(lease, lane.CaptureCurrentLaneLease());
            Assert.Equal(0, supervisor.GlobalStops);
        }
        finally { server.Stop(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealListenerConnect_StopOrCancelWhileWaitingForStatusCannotPublish(bool cancel)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var requestCancellation = new CancellationTokenSource();
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            using var supervisor = new ExactSupervisor(((IPEndPoint)server.LocalEndpoint).ToString());
            using var lane = CreateLane(supervisor);
            var start = lane.EnsureListenerSidecarConnectedAsync(supervisor.Request, requestCancellation.Token);
            using var connection = await server.AcceptTcpClientAsync(lifetime.Token);
            Assert.Null(lane.CaptureCurrentLaneLease());
            if (cancel) requestCancellation.Cancel();
            else await lane.StopAsync("stop_before_status", CancellationToken.None);
            if (cancel)
            {
                // The existing sidecar client can report canceled status-wait
                // as either cancellation or unsuccessful connection.
                try { Assert.False(await start.WaitAsync(lifetime.Token)); }
                catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested) { }
            }
            else
            {
                var status = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    @event = "status", address = "test-listener", lanes = new[] { "file" },
                    appProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion,
                    frameProtocolVersion = NknTunaSidecarFrameProtocol.ProtocolVersion,
                    sidecarVersion = NknTunaSidecarCompatibility.ExpectedSidecarVersion,
                });
                await NknTunaSidecarFrameProtocol.WriteFrameAsync(connection.GetStream(), NknTunaSidecarFrameType.Status,
                    NknTunaSidecarLane.Control, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), status, lifetime.Token);
                Assert.False(await start.WaitAsync(lifetime.Token));
            }
            Assert.False(lane.IsAvailable);
            Assert.Null(lane.CaptureCurrentLaneLease());
            Assert.Equal(0, supervisor.ExactStops); // Legacy startup does not own preparation cleanup.
            Assert.Equal(cancel ? 0 : 1, supervisor.GlobalStops);
        }
        finally { server.Stop(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationConnection_RevocationStopsOnlyUnretainedLease(bool retain)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var revocation = new CancellationTokenSource();
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            using var supervisor = new ExactSupervisor(((IPEndPoint)server.LocalEndpoint).ToString(), initiallyIdle: true);
            using var lane = CreateLane(supervisor);
            var request = new NknTunaLanePreparationRequest("preparation-owner", "test-peer", LocalIsPayer: true);
            var start = lane.StartPreparationLaneAsync(request, revocation.Token);
            using var connection = await server.AcceptTcpClientAsync(lifetime.Token);
            Assert.Null(await lane.StartPreparationLaneAsync(request, lifetime.Token));
            Assert.False(await lane.EnsureListenerSidecarConnectedAsync(supervisor.Request, lifetime.Token));
            Assert.False(await lane.StartDialerSidecarAsync("other-tuna", "test-peer", lifetime.Token));
            await WriteStatusAsync(connection, lifetime.Token);
            var original = Assert.IsType<NknTunaLaneLease>(await start.WaitAsync(lifetime.Token));
            Assert.Equal(NknTunaListenerLifetimePolicy.SessionPreparation, original.Listener!.CurrentRequest.LifetimePolicy);
            var retained = original;
            if (retain) Assert.True(lane.TryRetainCurrentLaneLease(original, out retained));
            revocation.Cancel();
            if (retain)
            {
                Assert.True(lane.IsAvailable);
                // Read real data after cancellation, proving the adopted client's
                // read loop is no longer linked to the preparation token.
                var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lane.MessageReceived += (_, _) => received.TrySetResult();
                await NknTunaSidecarFrameProtocol.WriteFrameAsync(connection.GetStream(), NknTunaSidecarFrameType.Data,
                    NknTunaSidecarLane.Bulk, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new byte[] { 7 }, lifetime.Token);
                await received.Task.WaitAsync(lifetime.Token);
                Assert.False(lane.TryStopCurrentLaneLease(original, "stale_preparation"));
                Assert.True(lane.TryStopCurrentLaneLease(retained!, "new_owner"));
            }
            else Assert.False(lane.IsAvailable);
            Assert.Equal(1, supervisor.ExactStops);
            Assert.Equal(0, supervisor.GlobalStops);
        }
        finally { server.Stop(); }
    }

    [Fact]
    public async Task PreparationConnection_LossDisposesExactClientWithoutStoppingSuccessor()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var revocation = new CancellationTokenSource();
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            using var supervisor = new ExactSupervisor(((IPEndPoint)server.LocalEndpoint).ToString(), initiallyIdle: true);
            using var lane = CreateLane(supervisor);
            using var successor = ReadyClient();
            using var owner = Owner();
            var events = new List<AccelerationStateChangedEventArgs>();
            lane.StateChanged += (_, e) => events.Add(e);
            var start = lane.StartPreparationLaneAsync(new("preparation-owner", "test-peer", true), revocation.Token);
            using var connection = await server.AcceptTcpClientAsync(lifetime.Token);
            await WriteStatusAsync(connection, lifetime.Token);
            var original = Assert.IsType<NknTunaLaneLease>(await start.WaitAsync(lifetime.Token));
            supervisor.BeforeExactStop = () => InstallReady(lane, successor, false, owner);

            original.Client.MarkUnavailableFromSidecarEvent("remote_closed");
            revocation.Cancel();

            Assert.Equal(1, (int)Field(original.Client, "disposed"));
            Assert.False(lane.TryStopCurrentLaneLease(original, "late_cleanup"));
            Assert.Same(successor, lane.CaptureCurrentLaneLease()!.Client);
            Assert.True(successor.IsAvailable);
            Assert.Equal(2, events.Count);
            Assert.True(events[0].IsAvailable);
            Assert.False(events[1].IsAvailable);
            Assert.All(events, e => Assert.Equal("preparation-owner", e.SessionPreparationExecutionIdentity));
            Assert.Equal(1, supervisor.ExactStops);
            Assert.Equal(0, supervisor.GlobalStops);
        }
        finally { server.Stop(); }
    }

    [Fact]
    public void ListenerAdoption_RotatesPhysicalCleanupOwnership()
    {
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var client = ReadyClient();
        using var owner = Owner();
        InstallReady(lane, client, true, owner, supervisor.Request);
        var original = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());
        var request = supervisor.Request with { ExecutionIdentity = "successor" };

        Assert.True(lane.TryAdoptCurrentListener(original.Listener!, request, out _));

        var successor = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());
        Assert.Equal(request, successor.Listener!.CurrentRequest);
        Assert.Equal(original.Listener!.OwnershipGeneration + 1, successor.Listener.OwnershipGeneration);
        Assert.False(lane.TryStopCurrentLaneLease(original, "stale_adopter"));
        Assert.True(client.IsAvailable);
        Assert.True(lane.TryStopCurrentLaneLease(successor, "current_adopter"));
        Assert.Equal(1, supervisor.ExactStops);
    }

    [Fact]
    public async Task PreparationStart_ReceiptCannotBorrowReplacementAtTheSameEndpoint()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            using var supervisor = new ExactSupervisor(((IPEndPoint)server.LocalEndpoint).ToString(), initiallyIdle: true)
            {
                ReplaceBeforeStartReturns = true,
            };
            using var lane = CreateLane(supervisor);
            var start = lane.StartPreparationLaneAsync(new("preparation-owner", "test-peer", true), lifetime.Token);
            using var connection = await server.AcceptTcpClientAsync(lifetime.Token);
            var replacement = supervisor.CaptureCurrentListenerAdoptionSnapshot();
            await WriteStatusAsync(connection, lifetime.Token);

            Assert.Null(await start.WaitAsync(lifetime.Token));
            Assert.False(lane.IsAvailable);
            Assert.Equal(replacement, supervisor.CaptureCurrentListenerAdoptionSnapshot());
            Assert.Equal(0, supervisor.ExactStops);
            Assert.Equal(0, supervisor.GlobalStops);
        }
        finally { server.Stop(); }
    }

    [Theory]
    [InlineData(false, "client")]
    [InlineData(true, "client")]
    [InlineData(false, "pending")]
    [InlineData(true, "pending")]
    [InlineData(false, "canceled")]
    [InlineData(true, "canceled")]
    public async Task PreparationStart_NeverTakesOverExistingOrPendingLane(bool payer, string state)
    {
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var client = ReadyClient();
        using var owner = Owner();
        using var ct = new CancellationTokenSource();
        if (state == "client") InstallReady(lane, client, false, owner);
        if (state == "pending") _ = Begin(lane);
        if (state == "canceled") ct.Cancel();
        var result = await lane.StartPreparationLaneAsync(new("new-preparation", "test-peer", payer, "tuna-address"), ct.Token);
        Assert.Null(result);
        Assert.Equal(0, supervisor.IdleStarts);
        Assert.Equal(0, supervisor.GlobalStops);
        if (state == "client") Assert.True(client.IsAvailable);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("request")]
    [InlineData("run")]
    [InlineData("exact")]
    public void RealSupervisor_ConditionalDetachChecksExactPhysicalAndOwnershipIdentity(string change)
    {
        using var supervisor = new NknTunaListenerSidecarSupervisor(new()
        {
            SidecarExePath = "not-started", WalletPath = "not-opened", TakeWalletPassword = () => throw new InvalidOperationException(),
            MaxPriceNknPerMb = "0", MaxTotalMiB = 1, MaxDurationSec = 60,
        });
        using var owner = Owner();
        var request = new NknTunaListenerStartRequest("test-peer", NknAccelerationLaneKind.File,
            NknTunaListenerLifetimePolicy.SessionPreparation, "owner");
        var snapshot = new NknTunaListenerAdoptionSnapshot(request, new("127.0.0.1:1", "address"),
            owner.Pid, owner.RunId, owner.StartedUtc, 3);
        Set(supervisor, "processOwner", owner);
        Set(supervisor, "currentRequest", request);
        Set(supervisor, "endpoint", snapshot.Endpoint);
        Set(supervisor, "ownershipGeneration", 3L);
        var expected = change switch
        {
            "generation" => snapshot with { OwnershipGeneration = 2 },
            "request" => snapshot with { CurrentRequest = request with { ExecutionIdentity = "old" } },
            "run" => snapshot with { ProcessRunId = "wrong-run" },
            _ => snapshot,
        };
        Assert.Equal(change == "exact", supervisor.TryDetachCurrentListener(expected, out var stop));
        Assert.False((bool)Field(owner, "stopped"));
        if (change == "exact")
        {
            Assert.NotNull(stop);
            Assert.False(supervisor.TryDetachCurrentListener(expected, out _));
            stop!("exact-owner-cleanup");
            Assert.True((bool)Field(owner, "stopped"));
        }
        else Assert.Null(stop);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSupervisor_IdleOnlyStartRejectsExistingOrQueuedOwnerWithoutReadingWallet(bool queued)
    {
        using var supervisor = new NknTunaListenerSidecarSupervisor(new()
        {
            SidecarExePath = "not-started", WalletPath = "not-opened", TakeWalletPassword = () => throw new InvalidOperationException(),
            MaxPriceNknPerMb = "0", MaxTotalMiB = 1, MaxDurationSec = 60,
        });
        var request = new NknTunaListenerStartRequest("test-peer", NknAccelerationLaneKind.File);
        var startGate = (SemaphoreSlim)Field(supervisor, "startGate");
        if (queued) await startGate.WaitAsync();
        else Set(supervisor, "currentRequest", request);
        try { Assert.Null(await supervisor.EnsureStartedIfIdleAsync(request, CancellationToken.None)); }
        finally { if (queued) startGate.Release(); }
    }

    [Fact]
    public async Task RealSupervisor_StopRetiresQueuedStartupBeforeWalletAccess()
    {
        using var supervisor = new NknTunaListenerSidecarSupervisor(new()
        {
            SidecarExePath = "not-started", WalletPath = "not-opened", TakeWalletPassword = () => throw new InvalidOperationException(),
            MaxPriceNknPerMb = "0", MaxTotalMiB = 1, MaxDurationSec = 60,
        });
        var startGate = (SemaphoreSlim)Field(supervisor, "startGate");
        await startGate.WaitAsync();
        var start = supervisor.EnsureStartedAsync(new("test-peer", NknAccelerationLaneKind.File), CancellationToken.None);
        Assert.False(start.IsCompleted);
        supervisor.Stop("retire_queued_start");
        startGate.Release();
        Assert.Null(await start.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static ValueTask WriteStatusAsync(TcpClient connection, CancellationToken ct)
        => NknTunaSidecarFrameProtocol.WriteFrameAsync(connection.GetStream(), NknTunaSidecarFrameType.Status,
            NknTunaSidecarLane.Control, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                @event = "status", address = "test-listener", lanes = new[] { "file" },
                appProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion,
                frameProtocolVersion = NknTunaSidecarFrameProtocol.ProtocolVersion,
                sidecarVersion = NknTunaSidecarCompatibility.ExpectedSidecarVersion,
            }), ct);

    [Theory]
    [InlineData(true, "tuna_bridge_terminal")]
    [InlineData(false, "tuna_bridge_terminal")]
    [InlineData(true, "tuna_stream_rejected")]
    [InlineData(false, "tuna_stream_rejected")]
    [InlineData(true, "error")]
    [InlineData(false, "error")]
    [InlineData(true, "tuna_cap_handoff_requested")]
    [InlineData(false, "tuna_cap_handoff_requested")]
    public void ListenerEvents_RetainCapturedPurposeAcrossOwnershipChange(bool preparation, string eventName)
        => AssertListenerEventPurpose(preparation, eventName, false);

    [Theory]
    [InlineData("tuna_bridge_terminal")]
    [InlineData("tuna_stream_rejected")]
    [InlineData("error")]
    [InlineData("tuna_cap_handoff_requested")]
    [Trait("FileTransferInfraSuite", "FallbackListenerPreparation")]
    public void FallbackListenerPreparation_ListenerFailureNeverRequestsRuntimeHandoff(string eventName)
        => AssertListenerEventPurpose(true, eventName, true);

    private static void AssertListenerEventPurpose(bool preparation, string eventName, bool fallbackPreparation)
    {
        NknTunaListenerSidecarSupervisor supervisor = null!;
        var request = new NknTunaListenerStartRequest("test-peer", NknAccelerationLaneKind.File,
            fallbackPreparation ? NknTunaListenerLifetimePolicy.CoordinatorScopedInitialActivation :
                preparation ? NknTunaListenerLifetimePolicy.SessionPreparation : NknTunaListenerLifetimePolicy.GenericBounded,
            "captured-owner", PreparationOnly: fallbackPreparation);
        var handoffs = 0;
        using (supervisor = new(new()
        {
            SidecarExePath = "not-launched", WalletPath = "not-opened", TakeWalletPassword = () => throw new InvalidOperationException(),
            MaxPriceNknPerMb = "0", MaxTotalMiB = 1, MaxDurationSec = 60,
            CapHandoffRequested = _ => handoffs++,
            StatusChanged = _ => Set(supervisor, "currentRequest", request with { ExecutionIdentity = "successor" }),
        }))
        using (var owner = Owner())
        {
            Set(supervisor, "currentRequest", request);
            Set(supervisor, "processOwner", owner);
            var events = new List<AccelerationStateChangedEventArgs>();
            supervisor.StateChanged += (_, e) => events.Add(e);
            Invoke(supervisor, "HandleOwnedStdoutLine", owner, (Process)Field(owner, "process"),
                JsonSerializer.Serialize(new { @event = eventName, reason = "test_reason", capReason = "test_cap" }),
                new TaskCompletionSource<NknTunaListenerSidecarEndpoint>(), Stopwatch.StartNew());
            if (!preparation && eventName == "tuna_cap_handoff_requested")
            {
                Assert.Empty(events);
                Assert.Equal(1, handoffs);
            }
            else
            {
                var observed = Assert.Single(events);
                Assert.False(observed.IsAvailable);
                Assert.Equal(preparation ? "captured-owner" : null, observed.SessionPreparationExecutionIdentity);
                Assert.Equal(0, handoffs);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationAdmission_RealConnectionRotatesCleanupAndEventPurpose(bool activate)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var revocation = new CancellationTokenSource();
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            using var supervisor = new ExactSupervisor(((IPEndPoint)server.LocalEndpoint).ToString(), initiallyIdle: true);
            using var lane = CreateLane(supervisor);
            var start = lane.StartPreparationLaneAsync(new("preparation-owner", "test-peer", true), revocation.Token);
            using var connection = await server.AcceptTcpClientAsync(lifetime.Token);
            await WriteStatusAsync(connection, lifetime.Token);
            var original = Assert.IsType<NknTunaLaneLease>(await start.WaitAsync(lifetime.Token));
            Assert.True(lane.TryTransferPreparationLaneLease(original, "admission-owner", false, out var retained));
            Assert.Equal("admission-owner", retained!.Listener!.CurrentRequest.ExecutionIdentity);
            Assert.False(await lane.StartDialerSidecarAsync("other", "test-peer", lifetime.Token));
            var current = retained;
            if (activate) Assert.True(lane.TryTransferPreparationLaneLease(retained, "admission-owner", true, out current));
            Assert.Equal(activate ? NknTunaListenerLifetimePolicy.CoordinatorAdmittedTransfer : NknTunaListenerLifetimePolicy.SessionPreparation,
                current!.Listener!.CurrentRequest.LifetimePolicy);
            Assert.True(current.ClientGeneration > original.ClientGeneration);
            revocation.Cancel();
            Assert.True(lane.IsAvailable);
            for (var i = 0; i < 3; i++)
            {
                var payload = Enumerable.Repeat((byte)(i + 1), 64 * 1024).ToArray();
                Assert.True(await lane.TrySendAsync(NknBridgeChannel.Bulk, payload, lifetime.Token));
                var frame = await NknTunaSidecarFrameProtocol.ReadFrameAsync(connection.GetStream(), lifetime.Token);
                if (frame.Type == NknTunaSidecarFrameType.Ping)
                    frame = await NknTunaSidecarFrameProtocol.ReadFrameAsync(connection.GetStream(), lifetime.Token);
                Assert.Equal(NknTunaSidecarFrameType.Data, frame.Type);
                Assert.Equal(NknTunaSidecarLane.Bulk, frame.Lane);
                Assert.Equal(payload, frame.Payload);
                Assert.Equal(current, lane.CaptureCurrentLaneLease());
            }
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lane.MessageReceived += (_, _) => received.TrySetResult();
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(connection.GetStream(), NknTunaSidecarFrameType.Data,
                NknTunaSidecarLane.Bulk, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new byte[] { 7 }, lifetime.Token);
            await received.Task.WaitAsync(lifetime.Token);
            Assert.False(lane.TryStopCurrentLaneLease(original, "old_preparation"));
            if (activate) Assert.False(lane.TryStopCurrentLaneLease(retained, "old_admission"));
            var lost = new TaskCompletionSource<AccelerationStateChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            lane.StateChanged += (_, e) => { if (!e.IsAvailable) lost.TrySetResult(e); };
            connection.Dispose();
            Assert.Equal(activate ? null : "admission-owner", (await lost.Task.WaitAsync(lifetime.Token)).SessionPreparationExecutionIdentity);
            Assert.Equal(0, supervisor.GlobalStops);
        }
        finally { server.Stop(); }
    }

    [Theory]
    [InlineData("retain", true)]
    [InlineData("activate", true)]
    [InlineData("generation", false)]
    [InlineData("run", false)]
    [InlineData("peer", false)]
    [InlineData("lanes", false)]
    [InlineData("identity", false)]
    [InlineData("generic", false)]
    public void PreparationAdmission_RealSupervisorKeepsExactOwnershipGuards(string change, bool accepted)
    {
        using var process = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 30")
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!;
        using var owner = (NknTunaSidecarProcessOwner)typeof(NknTunaSidecarProcessOwner)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke(new object?[] { "listener", process, null });
        using var supervisor = new NknTunaListenerSidecarSupervisor(new()
        {
            SidecarExePath = "not-started", WalletPath = "not-opened", TakeWalletPassword = () => throw new InvalidOperationException(),
            MaxPriceNknPerMb = "0", MaxTotalMiB = 1, MaxDurationSec = 60,
        });
        var request = new NknTunaListenerStartRequest("test-peer", NknAccelerationLaneKind.File,
            NknTunaListenerLifetimePolicy.SessionPreparation, "preparation-owner");
        var snapshot = new NknTunaListenerAdoptionSnapshot(request, new("127.0.0.1:1", "address"), owner.Pid, owner.RunId, owner.StartedUtc, 3);
        Set(supervisor, "processOwner", owner);
        Set(supervisor, "currentRequest", request);
        Set(supervisor, "endpoint", snapshot.Endpoint);
        Set(supervisor, "ownershipGeneration", 3L);
        var successor = request with { ExecutionIdentity = "admission-owner",
            LifetimePolicy = change == "retain" ? NknTunaListenerLifetimePolicy.SessionPreparation : NknTunaListenerLifetimePolicy.CoordinatorAdmittedTransfer };
        var expected = change == "generation" ? snapshot with { OwnershipGeneration = 2 }
            : change == "run" ? snapshot with { ProcessRunId = "wrong" } : snapshot;
        if (change == "peer") successor = successor with { ExpectedRemotePeer = "other" };
        if (change == "lanes") successor = successor with { Lanes = NknAccelerationLaneKind.Screen };
        if (change == "identity") successor = successor with { ExecutionIdentity = "" };
        if (change == "generic") successor = successor with { LifetimePolicy = NknTunaListenerLifetimePolicy.GenericBounded };
        Assert.Equal(accepted, supervisor.TryAdoptCurrentListener(expected, successor));
        var current = supervisor.CaptureCurrentListenerAdoptionSnapshot();
        Assert.Equal(accepted ? snapshot with { CurrentRequest = successor, OwnershipGeneration = 4 } : snapshot, current);
        if (accepted) Assert.False(supervisor.TryAdoptCurrentListener(snapshot, successor));
        Assert.True(owner.IsRunning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "FallbackListenerPreparation")]
    public async Task FallbackListenerPreparation_RealLaneIsEvidenceOnlyAndAdoptionRetiresOldCleanup(bool adopt)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var revocation = new CancellationTokenSource();
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            using var supervisor = new ExactSupervisor(((IPEndPoint)server.LocalEndpoint).ToString(), initiallyIdle: true);
            using var lane = CreateLane(supervisor);
            var events = new List<AccelerationStateChangedEventArgs>();
            lane.StateChanged += (_, e) => events.Add(e);
            var start = lane.StartPreparationLaneAsync(new("fallback-preparation", "test-peer", true,
                ListenerLifetimePolicy: NknTunaListenerLifetimePolicy.CoordinatorScopedInitialActivation), revocation.Token);
            using var connection = await server.AcceptTcpClientAsync(lifetime.Token);
            await WriteStatusAsync(connection, lifetime.Token);
            var lease = Assert.IsType<NknTunaLaneLease>(await start.WaitAsync(lifetime.Token));
            Assert.True(lease.Listener!.CurrentRequest.PreparationOnly);
            Assert.All(events, e => Assert.Equal("fallback-preparation", e.SessionPreparationExecutionIdentity));
            var current = lease;
            if (adopt)
            {
                Assert.True(lane.TryAdoptCurrentListener(lease.Listener,
                    lease.Listener.CurrentRequest with { ExecutionIdentity = "installed-retry", PreparationOnly = false }, out _));
                current = lane.CaptureCurrentLaneLease()!;
                Assert.NotEqual(lease, current);
            }
            revocation.Cancel();
            Assert.Equal(adopt, lane.IsAvailable);
            if (adopt)
            {
                Assert.False(lane.TryStopCurrentLaneLease(lease, "stale_preparation"));
                var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lane.MessageReceived += (_, _) => received.TrySetResult();
                await NknTunaSidecarFrameProtocol.WriteFrameAsync(connection.GetStream(), NknTunaSidecarFrameType.Data,
                    NknTunaSidecarLane.Bulk, 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new byte[] { 7 }, lifetime.Token);
                await received.Task.WaitAsync(lifetime.Token);
                Assert.True(lane.TryStopCurrentLaneLease(current, "installed_owner_cleanup"));
            }
            Assert.Equal(1, supervisor.ExactStops);
            Assert.Equal(0, supervisor.GlobalStops);
        }
        finally { server.Stop(); }
    }

    private static NknTunaAccelerationLane CreateLane(INknTunaListenerSidecarSupervisor? supervisor = null)
        => new(NknTunaAccelerationOptions.CreateRuntimePilot(
            Path.Combine(Environment.CurrentDirectory, "not-launched-sidecar.exe"), NknAccelerationLaneKind.File, true), supervisor);

    private static NknTunaSidecarClient ReadyClient()
    {
        var client = new NknTunaSidecarClient(NknAccelerationLaneKind.File, 16);
        Set(client, "available", 1);
        Set(client, "<LocalTunaAddress>k__BackingField", "test-listener");
        return client;
    }

    private static NknTunaSidecarProcessOwner Owner()
        => (NknTunaSidecarProcessOwner)typeof(NknTunaSidecarProcessOwner)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke(new object?[] { "dialer", new Process(), null });

    private static object Begin(NknTunaAccelerationLane lane) => Invoke(lane, "BeginLaneStartUnsafe")!;
    private static void Finish(NknTunaAccelerationLane lane, object operation) => Invoke(lane, "FinishLaneStart", operation);
    private static bool Install(NknTunaAccelerationLane lane, NknTunaSidecarClient client, bool listener,
        NknTunaSidecarProcessOwner owner, object operation, CancellationToken ct = default,
        NknTunaListenerStartRequest? request = null)
    {
        var role = typeof(NknTunaAccelerationLane).GetField("clientRole", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType;
        return (bool)Invoke(lane, "ReplaceStartedClient", client, Enum.Parse(role, listener ? "Listener" : "Dialer"),
            listener ? null : owner, operation, ct, request,
            listener ? ((INknTunaListenerSidecarSupervisor)Field(lane, "listenerSupervisor")).CaptureCurrentListenerAdoptionSnapshot() : null)!;
    }
    private static void InstallReady(NknTunaAccelerationLane lane, NknTunaSidecarClient client, bool listener,
        NknTunaSidecarProcessOwner owner, NknTunaListenerStartRequest? request = null)
    {
        var operation = Begin(lane);
        Assert.True(Install(lane, client, listener, owner, operation, request: request));
        Finish(lane, operation);
    }
    private static object? Invoke(object target, string name, params object?[] args)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static object Field(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private sealed class ExactSupervisor : INknTunaListenerSidecarSupervisor
    {
        public NknTunaListenerStartRequest Request { get; } = new("test-peer", NknAccelerationLaneKind.File,
            NknTunaListenerLifetimePolicy.CoordinatorScopedInitialActivation, "original-owner");
        private NknTunaListenerAdoptionSnapshot? snapshot;
        public int GlobalStops, ExactStops, IdleStarts;
        private readonly string ipc;
        public Action? BeforeExactStop;
        public bool ReplaceBeforeStartReturns;
        public bool CanOfferListener => true;
        public void ReplaceRun() => snapshot = snapshot! with { ProcessRunId = Guid.NewGuid().ToString("N") };
        public event EventHandler<AccelerationStateChangedEventArgs>? StateChanged { add { } remove { } }
        public ExactSupervisor(string ipc = "127.0.0.1:1", bool initiallyIdle = false)
        {
            this.ipc = ipc;
            if (!initiallyIdle) snapshot = new(Request, new(ipc, "test-listener"), 1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, 1);
        }
        public Task<NknTunaListenerAdoptionSnapshot?> EnsureStartedIfIdleAsync(NknTunaListenerStartRequest request, CancellationToken ct)
        {
            IdleStarts++;
            if (snapshot is not null) return Task.FromResult<NknTunaListenerAdoptionSnapshot?>(null);
            snapshot = new(request, new(ipc, "test-listener"), 1, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, 1);
            var started = snapshot;
            if (ReplaceBeforeStartReturns) ReplaceRun();
            return Task.FromResult<NknTunaListenerAdoptionSnapshot?>(started);
        }
        public Task<NknTunaListenerSidecarEndpoint?> EnsureStartedAsync(NknTunaListenerStartRequest request, CancellationToken ct)
            => Task.FromResult(snapshot?.Endpoint);
        public NknTunaListenerAdoptionSnapshot? CaptureCurrentListenerAdoptionSnapshot() => snapshot;
        public bool TryAdoptCurrentListener(NknTunaListenerAdoptionSnapshot expected, NknTunaListenerStartRequest request)
        {
            if (snapshot != expected) return false;
            snapshot = snapshot with { CurrentRequest = request, OwnershipGeneration = snapshot.OwnershipGeneration + 1 };
            return true;
        }
        public bool TryDetachCurrentListener(NknTunaListenerAdoptionSnapshot expected, out Action<string>? stop)
        {
            stop = null;
            if (snapshot != expected) return false;
            snapshot = null;
            stop = _ => { BeforeExactStop?.Invoke(); ExactStops++; };
            return true;
        }
        public void Stop(string reason) { GlobalStops++; snapshot = null; }
        public void Dispose() { snapshot = null; }
    }
}
