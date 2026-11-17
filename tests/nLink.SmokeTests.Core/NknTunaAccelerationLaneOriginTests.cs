using System.Diagnostics;
using System.Reflection;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class NknTunaAccelerationLaneOriginTests
{
    [Theory]
    [InlineData("listener_terminal_tuna_write_failed")]
    [InlineData("listener_terminal_tuna_stream_eof")]
    public void ListenerFailureOrigin_RequiresExactOwnershipAndReportsOnce(string reason)
    {
        using var lane = CreateLane();
        using var client = CreateClient();
        var source = new NknTunaListenerAdoptionSnapshot(
            new("peer", NknAccelerationLaneKind.File, NknTunaListenerLifetimePolicy.CoordinatorAdmittedTransfer, "transfer-owner"),
            new("test-ipc", "test-tuna"), 1, "listener-run", DateTimeOffset.UtcNow, 2);
        InstallListener(lane, client, source);
        var events = new List<AccelerationStateChangedEventArgs>();
        lane.StateChanged += (_, args) => events.Add(args);
        foreach (var stale in new[] { null, source with { OwnershipGeneration = 1 }, source with { ProcessRunId = "old-run" },
            source with { CurrentRequest = source.CurrentRequest with { ExecutionIdentity = "old-transfer" } } })
            InvokeLane(lane, "OnListenerSupervisorStateChanged", null, new AccelerationStateChangedEventArgs(false, reason, listenerFailureSource: stale));
        Assert.Empty(events);
        Assert.True(lane.IsAvailable);

        InvokeLane(lane, "OnListenerSupervisorStateChanged", null, new AccelerationStateChangedEventArgs(false, reason, listenerFailureSource: source));
        var origin = Assert.IsType<NknAccelerationFailureOrigin>(Assert.Single(events).FailureOrigin);
        Assert.True(lane.IsFailureOriginCurrent(origin));
        InvokeLane(lane, "OnListenerSupervisorStateChanged", null, new AccelerationStateChangedEventArgs(false, reason, listenerFailureSource: source));
        client.MarkUnavailableFromSidecarEvent("sidecar_tuna_stream_eof");
        Assert.Single(events);

        using var successor = CreateClient();
        InstallListener(lane, successor, source with { OwnershipGeneration = 3 });
        Assert.False(lane.IsFailureOriginCurrent(origin));
        InvokeLane(lane, "OnListenerSupervisorStateChanged", null, new AccelerationStateChangedEventArgs(false, reason, listenerFailureSource: source));
        Assert.Single(events);
        Assert.True(lane.IsAvailable);
    }

    [Fact]
    public void ListenerFailureOrigin_IpcEofIdentifiesCurrentClient()
    {
        using var lane = CreateLane();
        using var client = CreateClient();
        var source = new NknTunaListenerAdoptionSnapshot(new("peer", NknAccelerationLaneKind.File),
            new("ipc", "tuna"), 1, "current-listener-run", DateTimeOffset.UtcNow, 1);
        InstallListener(lane, client, source);
        AccelerationStateChangedEventArgs? observed = null;
        lane.StateChanged += (_, args) => observed = args;
        client.MarkUnavailableFromSidecarEvent("sidecar_tuna_stream_eof");
        var origin = Assert.IsType<NknAccelerationFailureOrigin>(observed!.FailureOrigin);
        Assert.Equal(source.ProcessRunId, origin.ProcessRunId);
        Assert.True(lane.IsFailureOriginCurrent(origin));
    }

    [Fact]
    [Trait("FileTransferInfraSuite", "InitialActivationReceiverClosure")]
    public void DialerFailureOrigin_IdentifiesExactLaneClientRunAndFailure()
    {
        using var lane = CreateLane();
        using var otherLane = CreateLane();
        using var owner = CreateUnstartedOwner();
        using var client = CreateClient();
        var events = new List<AccelerationStateChangedEventArgs>();
        lane.StateChanged += (_, args) => events.Add(args);
        InstallDialer(lane, client, owner);

        client.MarkUnavailableFromSidecarEvent("remote_closed");

        var failure = Assert.Single(events);
        Assert.False(failure.IsAvailable);
        Assert.Equal("remote_closed", failure.Reason);
        var origin = Assert.IsType<NknAccelerationFailureOrigin>(failure.FailureOrigin);
        Assert.False(origin.IsDefault);
        Assert.Equal(owner.RunId, origin.ProcessRunId);
        Assert.True(lane.IsFailureOriginCurrent(origin));
        Assert.False(otherLane.IsFailureOriginCurrent(origin));
        Assert.False(lane.IsFailureOriginCurrent(default));
        Assert.False(lane.IsFailureOriginCurrent(origin with { LaneInstanceId = Guid.NewGuid() }));
        Assert.False(lane.IsFailureOriginCurrent(origin with { ClientGeneration = origin.ClientGeneration + 1 }));
        Assert.False(lane.IsFailureOriginCurrent(origin with { ProcessRunId = "different-run" }));
        Assert.False(lane.IsFailureOriginCurrent(origin with { FailureGeneration = origin.FailureGeneration + 1 }));

        InvokeLane(lane, "OnClientStateChanged", client,
            new AccelerationStateChangedEventArgs(false, "remote_closed"));
        Assert.Single(events);
        Assert.True(lane.IsFailureOriginCurrent(origin));
    }

    [Fact]
    [Trait("FileTransferInfraSuite", "InitialActivationReceiverClosure")]
    public void DialerFailureOrigin_OldClientAndOldProcessCannotTargetSuccessor()
    {
        using var lane = CreateLane();
        using var oldOwner = CreateUnstartedOwner();
        using var currentOwner = CreateUnstartedOwner();
        using var oldClient = CreateClient();
        using var currentClient = CreateClient();
        var events = new List<AccelerationStateChangedEventArgs>();
        lane.StateChanged += (_, args) => events.Add(args);
        InstallDialer(lane, oldClient, oldOwner);
        InstallDialer(lane, currentClient, currentOwner);

        InvokeLane(lane, "OnClientStateChanged", oldClient,
            new AccelerationStateChangedEventArgs(false, "remote_closed"));
        InvokeLane(lane, "MarkDialerClientUnavailable", oldOwner, "sidecar_tuna_stream_eof");

        Assert.Empty(events);
        Assert.True(currentClient.IsAvailable);
        Assert.True(lane.IsAvailable);

        InvokeLane(lane, "MarkDialerClientUnavailable", currentOwner, "sidecar_tuna_stream_eof");
        var origin = Assert.IsType<NknAccelerationFailureOrigin>(Assert.Single(events).FailureOrigin);
        Assert.Equal(currentOwner.RunId, origin.ProcessRunId);
        Assert.True(lane.IsFailureOriginCurrent(origin));
        Assert.False(currentClient.IsAvailable);
    }

    [Fact]
    [Trait("FileTransferInfraSuite", "InitialActivationReceiverClosure")]
    public void DialerFailureOrigin_NewOwnerBeforeClientReplacementCannotRebindOldCallback()
    {
        using var lane = CreateLane();
        using var oldOwner = CreateUnstartedOwner();
        using var nextOwner = CreateUnstartedOwner();
        using var oldClient = CreateClient();
        using var nextClient = CreateClient();
        var events = new List<AccelerationStateChangedEventArgs>();
        lane.StateChanged += (_, args) => events.Add(args);
        InstallDialer(lane, oldClient, oldOwner);
        var ownerField = typeof(NknTunaAccelerationLane)
            .GetField("dialerProcessOwner", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // Process start publishes its owner before the new client connects.
        ownerField.SetValue(lane, nextOwner);

        InvokeLane(lane, "MarkDialerClientUnavailable", oldOwner, "sidecar_tuna_stream_eof");
        InvokeLane(lane, "MarkDialerClientUnavailable", nextOwner, "sidecar_tuna_stream_eof");
        Assert.True(oldClient.IsAvailable);
        oldClient.MarkUnavailableFromSidecarEvent("remote_closed");

        Assert.Empty(events);
        Assert.Same(nextOwner, ownerField.GetValue(lane));
        Assert.Same(oldClient, typeof(NknTunaAccelerationLane)
            .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lane));

        InstallDialer(lane, nextClient, nextOwner);
        nextClient.MarkUnavailableFromSidecarEvent("remote_closed");
        var origin = Assert.IsType<NknAccelerationFailureOrigin>(Assert.Single(events).FailureOrigin);
        Assert.Equal(nextOwner.RunId, origin.ProcessRunId);
        Assert.True(lane.IsFailureOriginCurrent(origin));
    }

    [Fact]
    [Trait("FileTransferInfraSuite", "InitialActivationReceiverClosure")]
    public void DialerFailureOrigin_SameRunIdDoesNotAuthorizeReplacedClient()
    {
        using var lane = CreateLane();
        using var owner = CreateUnstartedOwner();
        using var firstClient = CreateClient();
        using var nextClient = CreateClient();
        var events = new List<AccelerationStateChangedEventArgs>();
        lane.StateChanged += (_, args) => events.Add(args);
        InstallDialer(lane, firstClient, owner);
        firstClient.MarkUnavailableFromSidecarEvent("remote_closed");
        var first = Assert.IsType<NknAccelerationFailureOrigin>(Assert.Single(events).FailureOrigin);

        // Deliberately reuse the same run identity to prove that it is not
        // sufficient without the exact client and failure generations.
        InstallDialer(lane, nextClient, owner);
        Assert.False(lane.IsFailureOriginCurrent(first));
        nextClient.MarkUnavailableFromSidecarEvent("remote_closed");
        Assert.Equal(2, events.Count);
        var next = Assert.IsType<NknAccelerationFailureOrigin>(events[1].FailureOrigin);
        Assert.Equal(first.ProcessRunId, next.ProcessRunId);
        Assert.True(next.ClientGeneration > first.ClientGeneration);
        Assert.True(next.FailureGeneration > first.FailureGeneration);
        Assert.False(lane.IsFailureOriginCurrent(first));
        Assert.True(lane.IsFailureOriginCurrent(next));
    }

    [Fact]
    [Trait("FileTransferInfraSuite", "InitialActivationReceiverClosure")]
    public void DialerFailureOrigin_ReplacementDuringDeliveryRejectsCapturedOrigin()
    {
        using var lane = CreateLane();
        using var owner = CreateUnstartedOwner();
        using var successorOwner = CreateUnstartedOwner();
        using var client = CreateClient();
        using var successor = CreateClient();
        NknAccelerationFailureOrigin? delivered = null;
        bool? currentAtConsumer = null;
        var successorInstalled = false;
        lane.StateChanged += (_, args) =>
        {
            if (args.FailureOrigin is not null && !successorInstalled)
            {
                successorInstalled = true;
                InstallDialer(lane, successor, successorOwner);
            }
        };
        lane.StateChanged += (_, args) =>
        {
            delivered = args.FailureOrigin;
            currentAtConsumer = args.FailureOrigin is { } origin && lane.IsFailureOriginCurrent(origin);
        };
        InstallDialer(lane, client, owner);

        client.MarkUnavailableFromSidecarEvent("remote_closed");

        Assert.NotNull(delivered);
        Assert.False(currentAtConsumer);
        Assert.True(successor.IsAvailable);
        Assert.True(lane.IsAvailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "InitialActivationReceiverClosure")]
    public async Task DialerFailureOrigin_StopOrDisposeInvalidatesFailure(bool dispose)
    {
        using var lane = CreateLane();
        using var owner = CreateUnstartedOwner();
        using var client = CreateClient();
        NknAccelerationFailureOrigin? captured = null;
        lane.StateChanged += (_, args) => captured = args.FailureOrigin;
        InstallDialer(lane, client, owner);
        client.MarkUnavailableFromSidecarEvent("remote_closed");
        var origin = Assert.IsType<NknAccelerationFailureOrigin>(captured);
        Assert.True(lane.IsFailureOriginCurrent(origin));

        if (dispose)
        {
            lane.Dispose();
        }
        else
        {
            await lane.StopAsync("test_stop", CancellationToken.None);
        }

        Assert.False(lane.IsFailureOriginCurrent(origin));
    }

    [Fact]
    [Trait("FileTransferInfraSuite", "InitialActivationReceiverClosure")]
    public void DialerFailureOrigin_AtomicApplyRejectsReplacementWithoutRunningAction()
    {
        using var lane = CreateLane();
        using var owner = CreateUnstartedOwner();
        using var nextOwner = CreateUnstartedOwner();
        using var client = CreateClient();
        using var nextClient = CreateClient();
        NknAccelerationFailureOrigin? captured = null;
        lane.StateChanged += (_, args) => captured = args.FailureOrigin;
        InstallDialer(lane, client, owner);
        client.MarkUnavailableFromSidecarEvent("remote_closed");
        var origin = Assert.IsType<NknAccelerationFailureOrigin>(captured);
        var applied = 0;
        Assert.True(lane.TryApplyFailureOrigin(origin, () => { applied++; return true; }));
        Assert.Equal(1, applied);

        InstallDialer(lane, nextClient, nextOwner);

        Assert.False(lane.TryApplyFailureOrigin(origin, () => { applied++; return true; }));
        Assert.Equal(1, applied);
        Assert.True(nextClient.IsAvailable);
    }

    private static NknTunaAccelerationLane CreateLane()
        => new(NknTunaAccelerationOptions.CreateRuntimePilot(
            Path.Combine(Environment.CurrentDirectory, "nlink-tuna-sidecar.exe"),
            NknAccelerationLaneKind.File, canOfferListener: false));

    private static NknTunaSidecarClient CreateClient()
    {
        var client = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
        typeof(NknTunaSidecarClient).GetField("available", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, 1);
        return client;
    }

    private static NknTunaSidecarProcessOwner CreateUnstartedOwner()
        // An unstarted Process supplies an identity without launching a helper.
        => (NknTunaSidecarProcessOwner)typeof(NknTunaSidecarProcessOwner)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke(new object?[] { "dialer", new Process(), null });

    private static void InstallDialer(
        NknTunaAccelerationLane lane,
        NknTunaSidecarClient client,
        NknTunaSidecarProcessOwner owner)
    {
        var handler = typeof(NknTunaAccelerationLane)
            .GetMethod("OnClientStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        client.StateChanged += (EventHandler<AccelerationStateChangedEventArgs>)
            Delegate.CreateDelegate(typeof(EventHandler<AccelerationStateChangedEventArgs>), lane, handler);
        var role = typeof(NknTunaAccelerationLane)
            .GetField("clientRole", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType;
        Assert.True((bool)InvokeLane(lane, "ReplaceClient", client, Enum.Parse(role, "Dialer"), owner)!);
    }

    private static void InstallListener(NknTunaAccelerationLane lane, NknTunaSidecarClient client, NknTunaListenerAdoptionSnapshot source)
    {
        var handler = typeof(NknTunaAccelerationLane).GetMethod("OnClientStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        client.StateChanged += (EventHandler<AccelerationStateChangedEventArgs>)Delegate.CreateDelegate(typeof(EventHandler<AccelerationStateChangedEventArgs>), lane, handler);
        var role = typeof(NknTunaAccelerationLane).GetField("clientRole", BindingFlags.Instance | BindingFlags.NonPublic)!.FieldType;
        Assert.True((bool)InvokeLane(lane, "ReplaceClient", client, Enum.Parse(role, "Listener"), null)!);
        typeof(NknTunaAccelerationLane).GetField("listenerRequest", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(lane, source.CurrentRequest);
        typeof(NknTunaAccelerationLane).GetField("clientListenerLease", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(lane, source);
    }

    private static object? InvokeLane(NknTunaAccelerationLane lane, string method, params object?[] args)
        => typeof(NknTunaAccelerationLane).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(lane, args);
}
