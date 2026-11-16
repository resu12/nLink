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
    [Fact]
    public async Task ListenerStartup_EndpointWithoutCurrentReceiptCannotConnect()
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            using var supervisor = new ExactSupervisor(((IPEndPoint)server.LocalEndpoint).ToString());
            using var lane = CreateLane(supervisor);
            supervisor.BeforeStart = _ => supervisor.TerminalFailure = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Assert.False(await lane.EnsureListenerSidecarConnectedAsync(supervisor.Request, timeout.Token));
            Assert.False(server.Pending()); // Rejected before even opening IPC, not a failed connect.
            Assert.Null(lane.CaptureCurrentLaneLease());
            Assert.False(lane.IsAvailable);
        }
        finally { server.Stop(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListenerStartup_ReadyCannotOutliveItsFailureReceipt(bool duringCallback)
    {
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var client = ReadyClient();
        using var owner = Owner();
        InstallReady(lane, client, true, owner, supervisor.Request);
        var notifications = 0;
        lane.StateChanged += (_, e) =>
        {
            if (e.IsAvailable) { notifications++; supervisor.TerminalFailure = true; }
        };
        if (!duringCallback) supervisor.TerminalFailure = true;
        Assert.False((bool)Invoke(lane, "ForwardCurrentClientReadyIfAvailable", client, "ready")!);
        Assert.Equal(duringCallback ? 1 : 0, notifications);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ListenerCapStop_ResetCommitRechecksExactSource(int retireAtCheck)
    {
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var client = ReadyClient();
        using var owner = Owner();
        InstallReady(lane, client, true, owner, supervisor.Request);
        var lease = lane.CaptureCurrentLaneLease()!;
        var transportClient = new FakeNknClient("cap-reset-fixture");
        using var transport = new NknSignalingTransport(transportClient, NknTransportOptions.Load(),
            new NknIdentity("cap-reset-fixture", transportClient.Address), NknTunaAccelerationOptions.Disabled, lane);
        var checks = 0;
        bool IsCurrent()
        {
            if (++checks == retireAtCheck)
            {
                Assert.True(lane.TryAdoptCurrentListener(lease.Listener!,
                    supervisor.Request with { ExecutionIdentity = "successor" }, out _));
                Set(transport, "accelerationSessionId", "successor-session");
            }
            return lane.CaptureCurrentLaneLease() == lease;
        }
        Assert.False((bool)Invoke(transport, "ResetAccelerationNegotiation", "byte_cap_reached", false, null,
            NLink.Core.FileTransfer.RuntimeUnlockInitialActivationReceiveRecoveryHoldInvalidationKind.UserCancellation,
            null, (Func<bool>)IsCurrent)!);
        Assert.Equal(retireAtCheck, checks);
        Assert.Equal("successor-session", Field(transport, "accelerationSessionId"));
        Assert.True(client.IsAvailable);
        Assert.Equal(0, supervisor.ExactStops);
        Assert.Equal(0, supervisor.GlobalStops);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("adopted")]
    [InlineData("replaced")]
    [InlineData("runtime_retired")]
    [InlineData("successor_during_cleanup")]
    public async Task ListenerCapStop_OnlyExactCurrentListenerCanCommit(string change)
    {
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var client = ReadyClient();
        using var successor = ReadyClient();
        using var owner = Owner();
        InstallReady(lane, client, true, owner, supervisor.Request);
        var source = lane.CaptureCurrentLaneLease()!.Listener!;
        var transportClient = new FakeNknClient("cap-lifetime-test");
        using var transport = new NknSignalingTransport(transportClient, NknTransportOptions.Load(),
            new NknIdentity("cap-lifetime-test", transportClient.Address), NknTunaAccelerationOptions.Disabled, lane);
        if (change == "adopted") Assert.True(lane.TryAdoptCurrentListener(source,
            supervisor.Request with { ExecutionIdentity = "successor" }, out _));
        if (change == "replaced")
        {
            supervisor.ReplaceRun();
            InstallReady(lane, successor, true, owner, supervisor.Request);
        }
        if (change == "successor_during_cleanup")
            supervisor.BeforeExactStop = () => InstallReady(lane, successor, false, owner);
        var accepted = await transport.StopListenerForCapAsync(source, "byte_cap_reached",
            () => change != "runtime_retired", CancellationToken.None);
        Assert.Equal(change is "current" or "successor_during_cleanup", accepted);
        Assert.Equal(accepted ? 1L : 0L, Field(transport, "accelerationExplicitStopGeneration"));
        Assert.Equal(0, Field(transport, "accelerationLaneStopDrains"));
        Assert.Equal(accepted ? 1 : 0, supervisor.ExactStops);
        Assert.Equal(0, supervisor.GlobalStops);
        if (change is "replaced" or "successor_during_cleanup") Assert.True(successor.IsAvailable);
        else Assert.Equal(!accepted, client.IsAvailable);
    }

    [Theory]
    [InlineData("File")]
    [InlineData("Screen")]
    [InlineData("File, Screen")]
    public async Task ListenerReplacement_RetiresOldCallbacksBeforeSupervisorShutdown(string configuredLanes)
    {
        var lanes = Enum.Parse<NknAccelerationLaneKind>(configuredLanes);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            using var supervisor = new ExactSupervisor(((IPEndPoint)server.LocalEndpoint).ToString());
            using var lane = CreateLane(supervisor, lanes);
            var startup = supervisor.Request with { Lanes = lanes, LifetimePolicy = NknTunaListenerLifetimePolicy.GenericBounded };
            supervisor.BeforeStart = request => supervisor.ReplaceRun(request);
            var firstStart = lane.EnsureListenerSidecarConnectedAsync(startup, lifetime.Token);
            using var firstConnection = await server.AcceptTcpClientAsync(lifetime.Token);
            await WriteStatusAsync(firstConnection, lifetime.Token, lanes);
            Assert.True(await firstStart.WaitAsync(lifetime.Token));
            var predecessor = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());
            // Capture the real subscriber to model an event queued before it is detached.
            var lateState = (EventHandler<AccelerationStateChangedEventArgs>)Field(predecessor.Client, "StateChanged");
            var events = new List<AccelerationStateChangedEventArgs>();
            lane.StateChanged += (_, e) => events.Add(e);
            var replacement = startup with
            {
                LifetimePolicy = NknTunaListenerLifetimePolicy.CoordinatorScopedInitialActivation,
                ExecutionIdentity = "coordinator-transfer-owner",
            };
            var shutdowns = 0;
            supervisor.BeforeStart = request =>
            {
                shutdowns++;
                Assert.Equal(replacement, request);
                Assert.False(Monitor.IsEntered(Field(lane, "gate")));
                // Do not close IPC while the supervisor still owns the old process.
                Assert.Equal(0, (int)Field(predecessor.Client, "disposed"));
                supervisor.ReplaceRun(request);
                lateState(predecessor.Client, new(false, "read_failed"));
                Assert.Empty(events);
                Assert.Null(lane.CaptureCurrentLaneLease());
            };

            var nextStart = lane.EnsureListenerSidecarConnectedAsync(replacement, lifetime.Token);
            if (nextStart.IsCompleted) Assert.True(await nextStart);
            using var nextConnection = await server.AcceptTcpClientAsync(lifetime.Token);
            await WriteStatusAsync(nextConnection, lifetime.Token, lanes);
            Assert.True(await nextStart.WaitAsync(lifetime.Token));
            Assert.Equal(1, (int)Field(predecessor.Client, "disposed"));
            var successor = Assert.IsType<NknTunaLaneLease>(lane.CaptureCurrentLaneLease());
            Assert.Equal(replacement, successor.Listener!.CurrentRequest);
            Assert.NotSame(predecessor.Client, successor.Client);
            Assert.True(successor.ClientGeneration > predecessor.ClientGeneration);
            Assert.Equal(lanes, lane.SupportedLanes);
            Assert.True(Assert.Single(events).IsAvailable);
            lateState(predecessor.Client, new(false, "read_failed"));
            Assert.False(lane.TryStopCurrentLaneLease(predecessor, "late_cleanup"));
            Assert.Equal(successor, lane.CaptureCurrentLaneLease());
            Assert.Single(events);
            Assert.Equal(1, shutdowns);

            // Replacement does not suppress genuine failures of the current client.
            successor.Client.MarkUnavailableFromSidecarEvent("read_failed");
            Assert.Equal(2, events.Count);
            var failure = events[1];
            Assert.False(failure.IsAvailable);
            Assert.Equal("read_failed", failure.Reason);
            var origin = Assert.IsType<NknAccelerationFailureOrigin>(failure.FailureOrigin);
            Assert.Equal(successor.Listener.ProcessRunId, origin.ProcessRunId);
            Assert.True(lane.IsFailureOriginCurrent(origin));
            lateState(predecessor.Client, new(false, "read_failed"));
            Assert.Equal(2, events.Count);
            Assert.True(lane.IsFailureOriginCurrent(origin));
            Assert.Equal(0, supervisor.GlobalStops);
        }
        finally { server.Stop(); }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("stop")]
    [InlineData("successor")]
    public async Task ListenerReplacement_RetiredStartCannotPublishOrTouchSuccessor(string change)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var cancellation = new CancellationTokenSource();
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            using var supervisor = new ExactSupervisor(((IPEndPoint)server.LocalEndpoint).ToString());
            using var lane = CreateLane(supervisor);
            using var predecessor = ReadyClient();
            using var successor = ReadyClient();
            using var owner = Owner();
            InstallReady(lane, predecessor, true, owner, supervisor.Request);
            var events = new List<AccelerationStateChangedEventArgs>();
            lane.StateChanged += (_, e) => events.Add(e);
            var request = supervisor.Request with { ExecutionIdentity = "replacement-owner" };
            supervisor.BeforeStart = next => supervisor.ReplaceRun(next);
            var start = lane.EnsureListenerSidecarConnectedAsync(request, cancellation.Token);
            using var connection = await server.AcceptTcpClientAsync(lifetime.Token);
            Assert.Equal(0, (int)Field(predecessor, "disposed"));
            Assert.Null(lane.CaptureCurrentLaneLease());
            if (change == "cancel") cancellation.Cancel();
            else if (change == "stop") await lane.StopAsync("stop_during_replacement", lifetime.Token);
            else InstallReady(lane, successor, false, owner);
            var current = lane.CaptureCurrentLaneLease();
            if (change != "cancel") await WriteStatusAsync(connection, lifetime.Token);
            try { Assert.False(await start.WaitAsync(lifetime.Token)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Assert.Equal(1, (int)Field(predecessor, "disposed"));
            Assert.Empty(events);
            Assert.Equal(current, lane.CaptureCurrentLaneLease());
            Assert.Equal(change == "successor", lane.IsAvailable);
            if (change == "successor") Assert.Same(successor, current!.Client);
            Assert.Equal(change == "stop" ? 1 : 0, supervisor.GlobalStops);
            Assert.Equal(0, supervisor.ExactStops);
        }
        finally { server.Stop(); }
    }

    [Fact]
    public async Task ListenerReplacement_SupervisorCallbackCannotLaunchAfterSuccessorTakesOwnership()
    {
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var predecessor = ReadyClient();
        using var successor = ReadyClient();
        using var owner = Owner();
        InstallReady(lane, predecessor, true, owner, supervisor.Request);
        var starts = 0;
        supervisor.BeforeStart = request =>
        {
            starts++;
            Assert.False(Monitor.IsEntered(Field(lane, "gate")));
            InstallReady(lane, successor, false, owner);
        };

        Assert.False(await lane.EnsureListenerSidecarConnectedAsync(
            supervisor.Request with { ExecutionIdentity = "replacement-owner" }, CancellationToken.None));

        Assert.Equal(1, starts);
        Assert.Equal(1, (int)Field(predecessor, "disposed"));
        Assert.Same(successor, lane.CaptureCurrentLaneLease()!.Client);
        Assert.True(successor.IsAvailable);
        Assert.Equal(0, supervisor.GlobalStops);
    }

    [Fact]
    public async Task ListenerReplacement_NewStartRetiresPreviouslyCapturedFailureEvenIfStartFails()
    {
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var predecessor = ReadyClient();
        using var owner = Owner();
        InstallReady(lane, predecessor, true, owner, supervisor.Request);
        NknAccelerationFailureOrigin? captured = null;
        lane.StateChanged += (_, e) => captured = e.FailureOrigin;
        Invoke(lane, "OnClientStateChanged", predecessor, new AccelerationStateChangedEventArgs(false, "read_failed"));
        var failure = Assert.IsType<NknAccelerationFailureOrigin>(captured);
        Assert.True(lane.IsFailureOriginCurrent(failure));
        var applied = false;
        supervisor.BeforeStart = request =>
        {
            Assert.False(lane.TryApplyFailureOrigin(failure, () => applied = true));
            // Refuse startup without starting a process or an IPC connection.
            Assert.True(supervisor.TryDetachCurrentListener(supervisor.CaptureCurrentListenerAdoptionSnapshot()!, out _));
        };

        Assert.False(await lane.EnsureListenerSidecarConnectedAsync(
            supervisor.Request with { ExecutionIdentity = "replacement-owner" }, CancellationToken.None));

        Assert.False(applied);
        Assert.False(lane.IsFailureOriginCurrent(failure));
        Assert.False(lane.IsAvailable);
        Assert.Null(lane.CaptureCurrentLaneLease());
        Assert.Equal(0, supervisor.GlobalStops);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListenerReplacement_EarlyAbortRetiresExactProcessBeforeClosingIpc(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        // Existing files pass preflight, but no password is returned and no process is launched.
        var existingFile = typeof(NknTunaLaneOwnershipTests).Assembly.Location;
        using var supervisor = new NknTunaListenerSidecarSupervisor(new()
        {
            SidecarExePath = existingFile, WalletPath = existingFile, TakeWalletPassword = () => null,
            MaxPriceNknPerMb = "0", MaxTotalMiB = 1, MaxDurationSec = 60,
        });
        using var lane = CreateLane(supervisor);
        using var predecessor = ReadyClient();
        using var owner = Owner();
        var process = (Process)Field(owner, "process");
        var request = new NknTunaListenerStartRequest("test-peer", NknAccelerationLaneKind.File);
        var snapshot = new NknTunaListenerAdoptionSnapshot(request, new("127.0.0.1:1", "test-listener"),
            owner.Pid, owner.RunId, owner.StartedUtc, 1);
        Set(supervisor, "processOwner", owner);
        Set(supervisor, "currentRequest", request);
        Set(supervisor, "endpoint", snapshot.Endpoint);
        Set(supervisor, "ownershipGeneration", 1L);
        InstallReady(lane, predecessor, true, owner, request);
        // The unstarted test process cannot appear running to CaptureCurrentListenerAdoptionSnapshot.
        Set(lane, "clientListenerLease", snapshot);
        var events = new List<AccelerationStateChangedEventArgs>();
        lane.StateChanged += (_, e) => events.Add(e);
        var detachedBeforeIpcClose = false;
        predecessor.StateChanged += (_, _) =>
        {
            detachedBeforeIpcClose = Field(supervisor, "processOwner") is null;
            Invoke(supervisor, "HandleOwnedStdoutLine", owner, process,
                "{\"event\":\"tuna_bridge_terminal\",\"terminalReason\":\"local_ipc_eof\"}",
                new TaskCompletionSource<NknTunaListenerSidecarEndpoint>(), Stopwatch.StartNew());
        };
        var startGate = (SemaphoreSlim)Field(supervisor, "startGate");
        if (cancel) await startGate.WaitAsync();
        try
        {
            var start = lane.EnsureListenerSidecarConnectedAsync(request with { ExecutionIdentity = "replacement" }, cancellation.Token);
            if (cancel)
            {
                Assert.False(start.IsCompleted);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            }
            else Assert.False(await start);
        }
        finally { if (cancel) startGate.Release(); }

        Assert.True(detachedBeforeIpcClose);
        Assert.True((bool)Field(owner, "stopped"));
        Assert.Equal(1, (int)Field(predecessor, "disposed"));
        Assert.Empty(events);
        Assert.Null(Field(lane, "pendingLaneStart"));
        Assert.False(lane.IsAvailable);
    }

    [Theory]
    [InlineData("same_client")]
    [InlineData("same_process_adopted")]
    [InlineData("new_process")]
    [InlineData("pending_successor")]
    public async Task ListenerReplacement_EarlyAbortCannotCleanUpSuccessor(string change)
    {
        using var cancellation = new CancellationTokenSource();
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var predecessor = ReadyClient();
        using var successor = ReadyClient();
        using var owner = Owner();
        InstallReady(lane, predecessor, true, owner, supervisor.Request);
        var original = supervisor.CaptureCurrentListenerAdoptionSnapshot()!;
        object? nextStart = null;
        supervisor.BeforeStart = request =>
        {
            if (change is "same_client" or "same_process_adopted")
                Assert.True(supervisor.TryAdoptCurrentListener(original, request));
            if (change == "new_process") supervisor.ReplaceRun(request);
            if (change == "same_client") InstallReady(lane, predecessor, true, owner, request);
            else if (change == "pending_successor") nextStart = Begin(lane);
            else if (change == "new_process") InstallReady(lane, successor, true, owner, request);
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lane.EnsureListenerSidecarConnectedAsync(
            supervisor.Request with { ExecutionIdentity = "successor-owner" }, cancellation.Token));

        Assert.Equal(0, supervisor.ExactStops);
        Assert.Equal(0, supervisor.GlobalStops);
        Assert.NotNull(supervisor.CaptureCurrentListenerAdoptionSnapshot());
        Assert.Equal(change == "new_process" ? 1 : 0, (int)Field(predecessor, "disposed"));
        if (change is "same_client" or "new_process")
            Assert.Same(change == "same_client" ? predecessor : successor, lane.CaptureCurrentLaneLease()!.Client);
        if (change == "pending_successor") Assert.Same(nextStart, Field(lane, "pendingLaneStart"));
    }

    [Theory]
    [InlineData("dialer_success")]
    [InlineData("failure")]
    [InlineData("synchronous_dialer_failure")]
    [InlineData("cancel")]
    [InlineData("adopt_client")]
    [InlineData("adopt_process")]
    [InlineData("adopt_process_new_client")]
    [InlineData("new_listener")]
    [InlineData("superseded")]
    [InlineData("stop")]
    [InlineData("dispose")]
    public async Task ListenerReplacement_DeferredCleanupSettlesWithSuccessor(string outcome)
    {
        using var cancellation = new CancellationTokenSource();
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var predecessor = ReadyClient();
        using var successor = ReadyClient();
        using var owner = Owner();
        InstallReady(lane, predecessor, true, owner, supervisor.Request);
        var original = supervisor.CaptureCurrentListenerAdoptionSnapshot()!;
        object? retiredStart = null;
        object? nextStart = null;
        supervisor.BeforeStart = _ =>
        {
            retiredStart = Field(lane, "pendingLaneStart");
            nextStart = Begin(lane);
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };
        var events = new List<AccelerationStateChangedEventArgs>();
        lane.StateChanged += (_, e) => events.Add(e);
        supervisor.BeforeExactStop = () =>
        {
            Assert.False(Monitor.IsEntered(Field(lane, "gate")));
            Assert.Null(supervisor.CaptureCurrentListenerAdoptionSnapshot());
            Assert.Equal(0, (int)Field(predecessor, "disposed"));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lane.EnsureListenerSidecarConnectedAsync(
            supervisor.Request with { ExecutionIdentity = "replacement" }, cancellation.Token));
        Assert.Equal(0, (int)Field(predecessor, "disposed"));
        Assert.Equal(0, supervisor.ExactStops);
        Assert.Same(nextStart, Field(lane, "pendingLaneStart"));

        // Drive the same completion boundary used by awaited dialer startup,
        // without launching a sidecar or using a live Tuna connection.
        var request = supervisor.Request with { ExecutionIdentity = "successor" };
        if (outcome == "dialer_success") Assert.True(Install(lane, successor, false, owner, nextStart!));
        else if (outcome is "adopt_client" or "adopt_process" or "adopt_process_new_client")
        {
            Assert.True(supervisor.TryAdoptCurrentListener(original, request));
            if (outcome == "adopt_client") Assert.True(Install(lane, predecessor, true, owner, nextStart!, request: request));
            if (outcome == "adopt_process_new_client") Assert.True(Install(lane, successor, true, owner, nextStart!, request: request));
        }
        else if (outcome == "new_listener")
        {
            supervisor.ReplaceRun(request);
            Assert.True(Install(lane, successor, true, owner, nextStart!, request: request));
        }
        else if (outcome == "superseded")
        {
            var newerStart = Begin(lane);
            Finish(lane, nextStart!);
            Assert.Same(newerStart, Field(lane, "pendingLaneStart"));
            Assert.Equal(0, (int)Field(predecessor, "disposed"));
            Assert.Equal(0, supervisor.ExactStops);
            nextStart = newerStart;
        }
        else if (outcome == "stop") await lane.StopAsync("stop_pending_successor", CancellationToken.None);
        else if (outcome == "dispose") lane.Dispose();
        else if (outcome == "synchronous_dialer_failure")
        {
            Assert.False(File.Exists(Path.Combine(Environment.CurrentDirectory, "not-launched-sidecar.exe")));
            Assert.False(await lane.StartDialerSidecarAsync("test-tuna", "test-peer", CancellationToken.None));
        }

        if (outcome is "stop" or "dispose") Assert.Equal(1, (int)Field(predecessor, "disposed"));
        if (outcome == "cancel")
        {
            using var successorCancellation = new CancellationTokenSource();
            successorCancellation.Cancel();
            try { Assert.Throws<OperationCanceledException>(() => successorCancellation.Token.ThrowIfCancellationRequested()); }
            finally { Finish(lane, nextStart!); }
        }
        else Finish(lane, nextStart!);

        var adopted = outcome is "adopt_client" or "adopt_process" or "adopt_process_new_client";
        Assert.Equal(adopted ? 0 : 1, (int)Field(predecessor, "disposed"));
        Assert.Equal(outcome is "dialer_success" or "failure" or "synchronous_dialer_failure" or "cancel" or "superseded" ? 1 : 0, supervisor.ExactStops);
        Assert.Equal(outcome == "stop" ? 1 : 0, supervisor.GlobalStops);
        Assert.Null(Field(lane, "pendingLaneStart"));
        Assert.Empty(events);
        if (outcome is "adopt_client" or "adopt_process_new_client" or "new_listener" or "dialer_success")
            Assert.Same(outcome == "adopt_client" ? predecessor : successor, lane.CaptureCurrentLaneLease()!.Client);
        if (adopted || outcome == "new_listener") Assert.NotNull(supervisor.CaptureCurrentListenerAdoptionSnapshot());
        else Assert.Null(supervisor.CaptureCurrentListenerAdoptionSnapshot());

        var stops = supervisor.ExactStops;
        Finish(lane, retiredStart!);
        Finish(lane, nextStart!);
        Assert.Equal(stops, supervisor.ExactStops);
        if (outcome == "adopt_process_new_client")
        {
            Assert.True(lane.TryStopCurrentLaneLease(lane.CaptureCurrentLaneLease()!, "adopted_owner_finished"));
            Assert.Equal(1, (int)Field(predecessor, "disposed"));
            Assert.Equal(1, (int)Field(successor, "disposed"));
            Assert.Equal(stops + 1, supervisor.ExactStops);
            Assert.Equal(0, supervisor.GlobalStops);
        }
        else if (adopted)
        {
            await lane.StopAsync("adopted_owner_finished", CancellationToken.None);
            Assert.Equal(1, (int)Field(predecessor, "disposed"));
            Assert.Equal(stops, supervisor.ExactStops);
        }
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationConnection_LossDisposesExactClientWithoutStoppingSuccessor(bool terminalFirst)
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

            if (terminalFirst)
                Invoke(lane, "OnListenerSupervisorStateChanged", supervisor,
                    new AccelerationStateChangedEventArgs(false, "listener_terminal_tuna_stream_eof",
                        sessionPreparationExecutionIdentity: "preparation-owner", listenerFailureSource: original.Listener));
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

    [Theory]
    [InlineData("listener_sidecar_error", "current")]
    [InlineData("listener_sidecar_error", "before")]
    [InlineData("listener_sidecar_error", "after")]
    [InlineData("listener_stream_rejected_test", "current")]
    [InlineData("listener_stream_rejected_test", "before")]
    [InlineData("listener_stream_rejected_test", "after")]
    [InlineData("listener_cap_test", "current")]
    [InlineData("listener_cap_test", "before")]
    [InlineData("listener_cap_test", "after")]
    [InlineData("listener_terminal_tuna_write_failed", "current")]
    [InlineData("listener_terminal_tuna_write_failed", "before")]
    [InlineData("listener_terminal_tuna_write_failed", "after")]
    public void ListenerEvents_DeferredNotificationRetainsExactFailureOrigin(string reason, string adoption)
    {
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var client = ReadyClient();
        using var owner = Owner();
        InstallReady(lane, client, true, owner, supervisor.Request);
        var original = lane.CaptureCurrentLaneLease()!.Listener!;
        var successor = supervisor.Request with { ExecutionIdentity = "successor" };
        var events = new List<AccelerationStateChangedEventArgs>();
        lane.StateChanged += (_, e) => events.Add(e);
        if (adoption == "before") Assert.True(lane.TryAdoptCurrentListener(original, successor, out _));

        Invoke(lane, "OnListenerSupervisorStateChanged", supervisor,
            new AccelerationStateChangedEventArgs(false, reason, listenerFailureSource: original));

        if (adoption == "before" || reason.StartsWith("listener_stream_rejected_", StringComparison.Ordinal)) Assert.Empty(events);
        else
        {
            var failure = Assert.IsType<NknAccelerationFailureOrigin>(Assert.Single(events).FailureOrigin);
            Assert.True(lane.IsFailureOriginCurrent(failure));
            if (adoption == "after")
            {
                Assert.True(lane.TryAdoptCurrentListener(original, successor, out _));
                Assert.False(lane.IsFailureOriginCurrent(failure));
                Assert.False(lane.TryApplyFailureOrigin(failure, () => throw new InvalidOperationException("stale_failure_applied")));
            }
        }
        Assert.True(client.IsAvailable);
        Assert.Equal(0, supervisor.GlobalStops);
        Assert.Equal(0, supervisor.ExactStops);
    }

    [Theory]
    [InlineData("listener_terminal_tuna_write_failed")]
    [InlineData("listener_terminal_tuna_stream_eof")]
    [InlineData("remote_closed")]
    public void ListenerEvents_RejectedStreamDoesNotConsumeTerminalFailure(string terminal)
    {
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var client = ReadyClient();
        using var owner = Owner();
        InstallReady(lane, client, true, owner, supervisor.Request);
        var source = lane.CaptureCurrentLaneLease()!.Listener!;
        var events = new List<AccelerationStateChangedEventArgs>();
        lane.StateChanged += (_, e) => events.Add(e);
        Invoke(lane, "OnListenerSupervisorStateChanged", supervisor,
            new AccelerationStateChangedEventArgs(false, "listener_stream_rejected_remote_address_mismatch", listenerFailureSource: source));
        Assert.Empty(events);
        Assert.Null(Field(lane, "currentFailureOrigin"));
        Assert.True(client.IsAvailable);
        if (terminal != "remote_closed")
            Invoke(lane, "OnListenerSupervisorStateChanged", supervisor,
                new AccelerationStateChangedEventArgs(false, terminal, listenerFailureSource: source));
        Invoke(lane, "OnClientStateChanged", client, new AccelerationStateChangedEventArgs(false, "remote_closed"));
        var failure = Assert.Single(events);
        Assert.Equal(terminal, failure.Reason);
        Assert.Equal(source.ProcessRunId, failure.FailureOrigin!.Value.ProcessRunId);
        Assert.True(lane.IsFailureOriginCurrent(failure.FailureOrigin.Value));
    }

    [Theory]
    [InlineData("error", false)]
    [InlineData("error", true)]
    [InlineData("tuna_bridge_terminal", false)]
    [InlineData("tuna_bridge_terminal", true)]
    [InlineData("exit", false)]
    [InlineData("exit", true)]
    [InlineData("tuna_stream_rejected", false)]
    [InlineData("tuna_stream_rejected", true)]
    public void ListenerStartup_FailureAfterReadyCannotPublishOrAdopt(string eventName, bool published)
    {
        using var supervisor = new NknTunaListenerSidecarSupervisor(new()
        {
            SidecarExePath = "not-started", WalletPath = "not-opened", TakeWalletPassword = () => throw new InvalidOperationException(),
            MaxPriceNknPerMb = "0", MaxTotalMiB = 1, MaxDurationSec = 60,
        });
        using var owner = Owner();
        var request = new NknTunaListenerStartRequest("test-peer", NknAccelerationLaneKind.File);
        var endpoint = new NknTunaListenerSidecarEndpoint("127.0.0.1:1", "address");
        Set(supervisor, "processOwner", owner);
        Set(supervisor, "currentRequest", request);
        var ready = new TaskCompletionSource<NknTunaListenerSidecarEndpoint>();
        ready.SetResult(endpoint);
        if (published)
            Assert.NotNull(Invoke(supervisor, "TryPublishStartedListener", owner, request, endpoint, 0L, CancellationToken.None));
        if (eventName == "exit")
            Invoke(supervisor, "HandleOwnedExit", owner, (Process)Field(owner, "process"), ready, 0L);
        else
            Invoke(supervisor, "HandleOwnedStdoutLine", owner, (Process)Field(owner, "process"),
                JsonSerializer.Serialize(new { @event = eventName, reason = "tuna_stream_eof" }), ready, Stopwatch.StartNew());
        var failed = eventName != "tuna_stream_rejected";
        Assert.Equal(failed, Field(supervisor, "terminalFailureObserved"));
        Assert.Equal(!failed, Invoke(supervisor, "TryPublishStartedListener", owner, request, endpoint, 0L, CancellationToken.None) is not null);
        Assert.True(ready.Task.IsCompletedSuccessfully); // The latch, not rewriting the completed TCS, rejects startup.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListenerStartup_FailureBetweenSnapshotAndInstallCannotPublishReady(bool clientClosed)
    {
        using var supervisor = new ExactSupervisor();
        using var lane = CreateLane(supervisor);
        using var client = ReadyClient();
        var source = supervisor.CaptureCurrentListenerAdoptionSnapshot()!;
        var operation = Begin(lane);
        if (clientClosed) Set(client, "available", 0);
        else supervisor.TerminalFailure = true;
        var role = typeof(NknTunaAccelerationLane).GetField("clientRole", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType;
        var events = new List<AccelerationStateChangedEventArgs>();
        lane.StateChanged += (_, e) => events.Add(e);
        Assert.False((bool)Invoke(lane, "ReplaceStartedClient", client, Enum.Parse(role, "Listener"), null,
            operation, CancellationToken.None, source.CurrentRequest, source)!);
        Assert.False(lane.IsAvailable);
        Assert.Empty(events);
        Finish(lane, operation);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("tuna_bridge_terminal")]
    [InlineData("exit")]
    public async Task ListenerStartup_TerminalBeforeReadyFailsPendingWait(string eventName)
    {
        using var supervisor = new NknTunaListenerSidecarSupervisor(new()
        {
            SidecarExePath = "not-started", WalletPath = "not-opened", TakeWalletPassword = () => throw new InvalidOperationException(),
            MaxPriceNknPerMb = "0", MaxTotalMiB = 1, MaxDurationSec = 60,
        });
        using var owner = Owner();
        Set(supervisor, "processOwner", owner);
        Set(supervisor, "currentRequest", new NknTunaListenerStartRequest("test-peer", NknAccelerationLaneKind.File));
        var ready = new TaskCompletionSource<NknTunaListenerSidecarEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (eventName == "exit")
            Invoke(supervisor, "HandleOwnedExit", owner, (Process)Field(owner, "process"), ready, 0L);
        else
            Invoke(supervisor, "HandleOwnedStdoutLine", owner, (Process)Field(owner, "process"),
                JsonSerializer.Serialize(new { @event = eventName, reason = "tuna_stream_eof" }), ready, Stopwatch.StartNew());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await ready.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True((bool)Field(supervisor, "terminalFailureObserved"));
        Assert.Null(supervisor.CaptureCurrentListenerAdoptionSnapshot());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSupervisor_StopRetiresQueuedStartupBeforeWalletAccess(bool dispose)
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
        if (dispose) supervisor.Dispose();
        else supervisor.Stop("retire_queued_start");
        startGate.Release();
        Assert.Null(await start.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("dispose")]
    [InlineData("detach")]
    public async Task RealSupervisor_RetirementCancelsReadinessWithoutFailureOrRetry(string retirement)
    {
        var statuses = new List<string>();
        using var supervisor = new NknTunaListenerSidecarSupervisor(new()
        {
            SidecarExePath = "not-started", WalletPath = "not-opened", TakeWalletPassword = () => throw new InvalidOperationException(),
            MaxPriceNknPerMb = "0", MaxTotalMiB = 1, MaxDurationSec = 60, StatusChanged = statuses.Add,
        });
        using var owner = Owner();
        var request = new NknTunaListenerStartRequest("test-peer", NknAccelerationLaneKind.File);
        var endpoint = new NknTunaListenerSidecarEndpoint("127.0.0.1:1", "address");
        var ready = new TaskCompletionSource<NknTunaListenerSidecarEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(supervisor, "processOwner", owner);
        Set(supervisor, "currentRequest", request);
        Set(supervisor, "pendingReady", ready);
        Set(supervisor, "endpoint", endpoint);
        if (retirement == "dispose") supervisor.Dispose();
        else if (retirement == "stop") supervisor.Stop("test_retired");
        else
        {
            Assert.True(supervisor.TryDetachCurrentListener(new(request, endpoint,
                owner.Pid, owner.RunId, owner.StartedUtc, 0), out var stop));
            stop!("test_retired");
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        var statusCount = statuses.Count;
        Assert.False((bool)Invoke(supervisor, "TryReportStartFailure", 0L, owner,
            "listener_ready_timeout", "ready_timeout")!);
        var retry = (Task<NknTunaListenerAdoptionSnapshot?>)Invoke(supervisor, "StartProcessAsync",
            request, Array.Empty<char>(), 2, 2, 0L, CancellationToken.None)!;
        Assert.Null(await retry);
        Assert.Equal(statusCount, statuses.Count);
        Assert.Null(Field(supervisor, "lastStartFailureReason"));
        Assert.Null(Field(supervisor, "pendingReady"));
    }

    [Theory]
    [InlineData("listener_ready_timeout")]
    [InlineData("listener_failed")]
    [InlineData("listener_canceled")]
    public void RealSupervisor_OnlyCurrentStartupCanReportFailure(string reason)
    {
        var statuses = new List<string>();
        using var supervisor = new NknTunaListenerSidecarSupervisor(new()
        {
            SidecarExePath = "not-started", WalletPath = "not-opened", TakeWalletPassword = () => throw new InvalidOperationException(),
            MaxPriceNknPerMb = "0", MaxTotalMiB = 1, MaxDurationSec = 60, StatusChanged = statuses.Add,
        });
        using var oldOwner = Owner();
        using var currentOwner = Owner();
        Set(supervisor, "processOwner", currentOwner);
        Set(supervisor, "stopGeneration", 2L);
        Assert.False((bool)Invoke(supervisor, "TryReportStartFailure", 1L, currentOwner, reason, null)!);
        Assert.False((bool)Invoke(supervisor, "TryReportStartFailure", 2L, oldOwner, reason, null)!);
        Assert.Empty(statuses);
        Assert.False((bool)Field(supervisor, "summaryObserved"));
        Assert.False((bool)Field(currentOwner, "stopped"));
        Assert.True((bool)Invoke(supervisor, "TryReportStartFailure", 2L, currentOwner, reason, null)!);
        Assert.Equal(new[] { reason }, statuses);
        Assert.Equal(reason, Field(supervisor, "lastStartFailureReason"));
        Assert.True((bool)Field(supervisor, "summaryObserved"));
    }

    [Fact]
    public void RealSupervisor_AccountingCallbackRetirementCannotPublishOldFailure()
    {
        var statuses = new List<string>();
        var sink = new RetiringStartupUsageSink();
        using var supervisor = new NknTunaListenerSidecarSupervisor(new()
        {
            SidecarExePath = "not-started", WalletPath = "not-opened", TakeWalletPassword = () => throw new InvalidOperationException(),
            MaxPriceNknPerMb = "0", MaxTotalMiB = 1, MaxDurationSec = 60,
            StatusChanged = statuses.Add, UsageSink = sink,
        });
        using var owner = Owner();
        Set(supervisor, "processOwner", owner);
        sink.OnIncomplete = supervisor.Dispose;
        Assert.False((bool)Invoke(supervisor, "TryReportStartFailure", 0L, owner,
            "listener_ready_timeout", "ready_timeout")!);
        Assert.Empty(statuses);
        Assert.True((bool)Field(owner, "stopped"));
    }

    private sealed class RetiringStartupUsageSink : INknTunaUsageTelemetrySink
    {
        public Action? OnIncomplete;
        public Action? OnSummary;
        public void RecordPayment(NknTunaPaymentTelemetry payment) { }
        public void RecordSummary(NknTunaSessionUsageTelemetry summary) => OnSummary?.Invoke();
        public void RecordIncomplete(string reason) => OnIncomplete?.Invoke();
    }

    private static ValueTask WriteStatusAsync(TcpClient connection, CancellationToken ct,
        NknAccelerationLaneKind lanes = NknAccelerationLaneKind.File)
        => NknTunaSidecarFrameProtocol.WriteFrameAsync(connection.GetStream(), NknTunaSidecarFrameType.Status,
            NknTunaSidecarLane.Control, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                @event = "status", address = "test-listener", lanes = NknAccelerationLaneCodec.ToNames(lanes),
                appProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion,
                frameProtocolVersion = NknTunaSidecarFrameProtocol.ProtocolVersion,
                sidecarVersion = NknTunaSidecarCompatibility.ExpectedSidecarVersion,
            }), ct);

    [Theory]
    [InlineData(true, "tuna_bridge_terminal")]
    [InlineData(false, "tuna_bridge_terminal")]
    [InlineData(true, "error")]
    [InlineData(false, "error")]
    [InlineData(true, "tuna_cap_handoff_requested")]
    [InlineData(false, "tuna_cap_handoff_requested")]
    public void ListenerEvents_RetiredDuringStatusCannotAffectSuccessor(bool preparation, string eventName)
        => AssertListenerEventPurpose(preparation, eventName, false, retire: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ListenerEvents_RejectedStreamCannotInvokeLifecycleStatusSubscriber(bool preparation)
        => AssertListenerEventPurpose(preparation, "tuna_stream_rejected", false, retire: true);

    [Theory]
    [InlineData(true, "tuna_bridge_terminal")]
    [InlineData(false, "tuna_bridge_terminal")]
    [InlineData(true, "tuna_stream_rejected")]
    [InlineData(false, "tuna_stream_rejected")]
    [InlineData(true, "error")]
    [InlineData(false, "error")]
    [InlineData(true, "tuna_cap_handoff_requested")]
    [InlineData(false, "tuna_cap_handoff_requested")]
    public void ListenerEvents_CurrentOwnerRetainsPurpose(bool preparation, string eventName)
        => AssertListenerEventPurpose(preparation, eventName, false);

    [Theory]
    [InlineData("stop")]
    [InlineData("dispose")]
    [InlineData("replace")]
    public void ListenerEvents_RetiredProcessCannotRequestCapHandoff(string retirement)
    {
        var handoffs = 0;
        var retired = false;
        using var owner = Owner();
        using var successor = Owner();
        NknTunaListenerSidecarSupervisor supervisor = null!;
        using (supervisor = new(new()
        {
            SidecarExePath = "not-started", WalletPath = "not-opened", TakeWalletPassword = () => throw new InvalidOperationException(),
            MaxPriceNknPerMb = "0", MaxTotalMiB = 1, MaxDurationSec = 60,
            CapHandoffRequested = _ => handoffs++,
            StatusChanged = _ =>
            {
                if (retired) return;
                retired = true;
                if (retirement == "stop") supervisor.Stop("test_retired");
                else if (retirement == "dispose") supervisor.Dispose();
                else Set(supervisor, "processOwner", successor);
            },
        }))
        {
            Set(supervisor, "currentRequest", new NknTunaListenerStartRequest("test-peer", NknAccelerationLaneKind.File));
            Set(supervisor, "processOwner", owner);
            var events = new List<AccelerationStateChangedEventArgs>();
            supervisor.StateChanged += (_, e) => events.Add(e);
            Invoke(supervisor, "HandleOwnedStdoutLine", owner, (Process)Field(owner, "process"),
                "{\"event\":\"tuna_cap_handoff_requested\",\"capReason\":\"test_cap\"}",
                new TaskCompletionSource<NknTunaListenerSidecarEndpoint>(), Stopwatch.StartNew());
            Assert.True(retired);
            Assert.Equal(0, handoffs);
            Assert.Empty(events);
            Assert.Null(Field(supervisor, "lastStartFailureReason"));
            Assert.False((bool)Field(successor, "stopped"));
        }
    }

    [Fact]
    public void ListenerEvents_AccountingRetirementCannotPublishSummaryStatus()
    {
        var statuses = new List<string>();
        var sink = new RetiringStartupUsageSink();
        using var supervisor = new NknTunaListenerSidecarSupervisor(new()
        {
            SidecarExePath = "not-started", WalletPath = "not-opened", TakeWalletPassword = () => throw new InvalidOperationException(),
            MaxPriceNknPerMb = "0", MaxTotalMiB = 1, MaxDurationSec = 60,
            StatusChanged = statuses.Add, UsageSink = sink,
        });
        using var owner = Owner();
        Set(supervisor, "processOwner", owner);
        Set(supervisor, "currentRequest", new NknTunaListenerStartRequest("test-peer", NknAccelerationLaneKind.File));
        sink.OnSummary = supervisor.Dispose;
        Invoke(supervisor, "HandleOwnedStdoutLine", owner, (Process)Field(owner, "process"),
            "{\"event\":\"summary\",\"capReached\":true}",
            new TaskCompletionSource<NknTunaListenerSidecarEndpoint>(), Stopwatch.StartNew());
        Assert.True((bool)Field(owner, "stopped"));
        Assert.Empty(statuses);
        Assert.True((bool)Field(supervisor, "summaryObserved"));
    }

    [Theory]
    [InlineData("tuna_bridge_terminal")]
    [InlineData("tuna_stream_rejected")]
    [InlineData("error")]
    [InlineData("tuna_cap_handoff_requested")]
    [Trait("FileTransferInfraSuite", "FallbackListenerPreparation")]
    public void FallbackListenerPreparation_ListenerFailureNeverRequestsRuntimeHandoff(string eventName)
        => AssertListenerEventPurpose(true, eventName, true);

    private static void AssertListenerEventPurpose(bool preparation, string eventName, bool fallbackPreparation, bool retire = false)
    {
        NknTunaListenerSidecarSupervisor supervisor = null!;
        var request = new NknTunaListenerStartRequest("test-peer", NknAccelerationLaneKind.File,
            fallbackPreparation ? NknTunaListenerLifetimePolicy.CoordinatorScopedInitialActivation :
                preparation ? NknTunaListenerLifetimePolicy.SessionPreparation : NknTunaListenerLifetimePolicy.GenericBounded,
            "captured-owner", PreparationOnly: fallbackPreparation);
        var handoffs = 0;
        var statuses = new List<string>();
        using (supervisor = new(new()
        {
            SidecarExePath = "not-launched", WalletPath = "not-opened", TakeWalletPassword = () => throw new InvalidOperationException(),
            MaxPriceNknPerMb = "0", MaxTotalMiB = 1, MaxDurationSec = 60,
            CapHandoffRequested = _ => handoffs++,
            StatusChanged = status =>
            {
                statuses.Add(status);
                if (retire) Set(supervisor, "currentRequest", request with { ExecutionIdentity = "successor" });
            },
        }))
        using (var owner = Owner())
        {
            Set(supervisor, "currentRequest", request);
            Set(supervisor, "processOwner", owner);
            Set(supervisor, "endpoint", new NknTunaListenerSidecarEndpoint("127.0.0.1:1", "test-listener"));
            var events = new List<AccelerationStateChangedEventArgs>();
            var callbackHeldGate = false;
            supervisor.StateChanged += (_, e) =>
            {
                callbackHeldGate |= Monitor.IsEntered(Field(supervisor, "gate"));
                events.Add(e);
            };
            Invoke(supervisor, "HandleOwnedStdoutLine", owner, (Process)Field(owner, "process"),
                JsonSerializer.Serialize(new { @event = eventName, reason = "test_reason", capReason = "test_cap" }),
                new TaskCompletionSource<NknTunaListenerSidecarEndpoint>(), Stopwatch.StartNew());
            Assert.False(callbackHeldGate);
            if (eventName == "tuna_stream_rejected")
            {
                Assert.Empty(statuses);
                Assert.Same(request, Field(supervisor, "currentRequest"));
                Assert.False((bool)Field(supervisor, "terminalFailureObserved"));
            }
            if (retire && eventName != "tuna_stream_rejected")
            {
                Assert.Empty(events);
                Assert.Equal(0, handoffs);
                Assert.Equal("successor", ((NknTunaListenerStartRequest)Field(supervisor, "currentRequest")).ExecutionIdentity);
            }
            else if (!preparation && eventName == "tuna_cap_handoff_requested")
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

    private static NknTunaAccelerationLane CreateLane(INknTunaListenerSidecarSupervisor? supervisor = null,
        NknAccelerationLaneKind lanes = NknAccelerationLaneKind.File)
        => new(NknTunaAccelerationOptions.CreateRuntimePilot(
            Path.Combine(Environment.CurrentDirectory, "not-launched-sidecar.exe"), lanes, true), supervisor);

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
    private static void Finish(NknTunaAccelerationLane lane, object operation) => Invoke(lane, "FinishLaneStart", operation, true);
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
        public Action<NknTunaListenerStartRequest>? BeforeStart;
        public bool ReplaceBeforeStartReturns;
        public bool TerminalFailure;
        public bool CanOfferListener => true;
        public void ReplaceRun(NknTunaListenerStartRequest? request = null)
            => snapshot = snapshot! with { ProcessRunId = Guid.NewGuid().ToString("N"), CurrentRequest = request ?? snapshot.CurrentRequest };
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
        {
            BeforeStart?.Invoke(request);
            return Task.FromResult(snapshot?.Endpoint);
        }
        public NknTunaListenerAdoptionSnapshot? CaptureCurrentListenerAdoptionSnapshot() => TerminalFailure ? null : snapshot;
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
