using System.Reflection;
using NLink.App.Services;
using NLink.Core;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class TunaPostTransferStatusTests
{
    private const string Pending = "runtime_unlock_pending_route_commit";
    private static readonly TransportAccelerationPendingFileTransfer Scope = new("session", "transfer", 1, 2);

    [Theory]
    [InlineData(FileTransferDirection.Inbound, FileTransferTransferState.Completed)]
    [InlineData(FileTransferDirection.Outbound, FileTransferTransferState.Completed)]
    [InlineData(FileTransferDirection.Inbound, FileTransferTransferState.Failed)]
    [InlineData(FileTransferDirection.Outbound, FileTransferTransferState.Failed)]
    [InlineData(FileTransferDirection.Inbound, FileTransferTransferState.Canceled)]
    [InlineData(FileTransferDirection.Outbound, FileTransferTransferState.Canceled)]
    [InlineData(FileTransferDirection.Inbound, FileTransferTransferState.Declined)]
    [InlineData(FileTransferDirection.Outbound, FileTransferTransferState.Declined)]
    public void ExactPendingTransfer_StopsEstablishingAtTerminalWithoutGrantingSessionActivity(
        FileTransferDirection direction, FileTransferTransferState terminal)
    {
        var transfer = Transfer(direction);
        var starting = Project(transfer, Scope);
        Assert.False(starting.IsActive);
        Assert.True(Present(starting).IsConnecting);

        transfer = transfer with { RouteToken = "file_tuna_v4", ProtocolVersion = 4 };
        var pending = Project(transfer, Scope);
        Assert.False(pending.IsActive);
        Assert.True(Present(pending).IsConnecting);

        var finished = Project(transfer with { State = terminal }, Scope);
        Assert.False(finished.IsActive);
        Assert.Equal("file_transfer_tuna_idle", finished.StatusReason);
        // File + Screen can be configured and the sidecar still ready. Neither
        // that observation nor file completion grants session/Screen activity.
        Assert.False(Present(finished, "listener_ready").IsConnecting);
        Assert.False(Present(finished, "peer_connected").IsConnecting);
        Assert.True(Present(finished).IsIdle);
        Assert.Equal("Tuna is idle for file transfer.", Present(finished).Text);
    }

    [Theory]
    [InlineData("listener_ready", true, true)]
    [InlineData("dialer_ready", true, false)]
    [InlineData("inactive", false, false)]
    public void IdlePresentation_PreservesRoleWithoutClaimingActivity(
        string runtimeStatus, bool unlockOn, bool localPayer)
    {
        var projection = Project(Transfer() with { State = FileTransferTransferState.Completed }, Scope);
        var presentation = TunaStatusPresentationMapper.FromState(
            projection.IsActive, projection.StatusReason, runtimeStatus, unlockOn, projection.FileUsesRegularNkn);
        Assert.False(projection.IsActive);
        Assert.True(presentation.IsIdle);
        Assert.False(presentation.IsConnecting);
        Assert.Equal(localPayer, presentation.IsLocalPayer);
        Assert.Equal("Tuna is idle for file transfer.", presentation.Text);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("session")]
    [InlineData("transfer")]
    public void TerminalTransfer_DoesNotSuppressUnscopedOrDifferentAttempt(string mismatch)
    {
        var scope = mismatch switch
        {
            "session" => Scope with { SessionId = "new-session" },
            "transfer" => Scope with { TransferId = "new-transfer" },
            _ => null,
        };
        var projection = Project(Transfer() with { State = FileTransferTransferState.Completed }, scope);
        Assert.False(projection.IsActive);
        Assert.Equal(Pending, projection.StatusReason);
        Assert.True(Present(projection).IsConnecting);
    }

    [Fact]
    public void UnrelatedRegularTransfer_DoesNotReviveFinishedAttempt()
    {
        var snapshot = new SessionFileTransferSnapshot(
            Transfer() with { State = FileTransferTransferState.Completed },
            Transfer(FileTransferDirection.Inbound) with { TransferId = "new-transfer" });
        var projection = TunaHeaderStateProjector.FromSnapshot(snapshot, "NKN", false, Pending, Scope);
        Assert.False(projection.IsActive);
        Assert.Equal("file_transfer_tuna_idle", projection.StatusReason);
        Assert.False(Present(projection).IsConnecting);
        Assert.True(projection.FileUsesRegularNkn);
    }

    [Theory]
    [InlineData(FileTransferDirection.Inbound, FileTransferTransferState.Completed)]
    [InlineData(FileTransferDirection.Outbound, FileTransferTransferState.Completed)]
    [InlineData(FileTransferDirection.Inbound, FileTransferTransferState.Failed)]
    [InlineData(FileTransferDirection.Outbound, FileTransferTransferState.Failed)]
    [InlineData(FileTransferDirection.Inbound, FileTransferTransferState.Canceled)]
    [InlineData(FileTransferDirection.Outbound, FileTransferTransferState.Canceled)]
    [InlineData(FileTransferDirection.Inbound, FileTransferTransferState.Declined)]
    [InlineData(FileTransferDirection.Outbound, FileTransferTransferState.Declined)]
    public void SessionRuntime_SettlementSurvivesOverlapAndReplacementBeforeUiRefresh(
        FileTransferDirection direction, FileTransferTransferState terminal)
    {
        using var transport = new ScriptedSignalingTransport();
        using var stale = new ScriptedSignalingTransport();
        using var runtime = CreateRuntime(transport);
        ObserveStatus(runtime, transport, new(false, Pending, Scope));
        var opposite = direction == FileTransferDirection.Outbound
            ? FileTransferDirection.Inbound : FileTransferDirection.Outbound;
        var old = Transfer(direction) with { RouteToken = "file_tuna_v4", State = terminal };
        var other = Transfer(opposite) with { TransferId = "other" };
        var replacement = Transfer(direction) with { TransferId = "replacement" };
        SessionFileTransferSnapshot latest = new(null, null);
        runtime.FileTransferChanged += (_, e) => latest = e.Snapshot;
        SessionFileTransferSnapshot Pair(FileTransferTransferSnapshot transfer)
            => direction == FileTransferDirection.Outbound ? new(transfer, other) : new(other, transfer);

        ObserveSnapshot(runtime, Pair(old));
        Assert.Equal("file_transfer_tuna_idle", runtime.TransportAccelerationPresentation.Reason);
        // Both snapshots can arrive before either page gets its queued UI refresh.
        ObserveSnapshot(runtime, Pair(replacement));
        ObserveStatus(runtime, transport, new(false, Pending, Scope));
        ObserveStatus(runtime, stale, new(false, Pending, Scope with { OfferGeneration = 99 }));
        Assert.Equal("file_transfer_tuna_idle", ProjectRuntime(runtime, latest).StatusReason);
        Assert.False(Present(ProjectRuntime(runtime, latest)).IsConnecting);

        // The replacement's file route does not confirm a new session connection.
        ObserveSnapshot(runtime, Pair(replacement with { RouteToken = "file_tuna_v4" }));
        Assert.False(ProjectRuntime(runtime, latest).IsActive);
        Assert.Equal("file_transfer_tuna_idle", ProjectRuntime(runtime, latest).StatusReason);
        ObserveSnapshot(runtime, Pair(replacement with { State = FileTransferTransferState.Completed }));
        other = other with { State = FileTransferTransferState.Completed };
        ObserveSnapshot(runtime, Pair(replacement with { State = FileTransferTransferState.Completed }));
        Assert.Equal("file_transfer_tuna_idle", ProjectRuntime(runtime, latest).StatusReason);
        ObserveSnapshot(runtime, new(null, null));
        Assert.False(Present(ProjectRuntime(runtime, latest)).IsConnecting);
        Assert.Equal(Pending, runtime.TransportAccelerationStatusReason);
        Assert.False(runtime.IsTransportAccelerationActive);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("transfer")]
    [InlineData("transaction")]
    [InlineData("offer")]
    [InlineData("unscoped")]
    [InlineData("reason")]
    [InlineData("active")]
    [InlineData("unwire")]
    public void SessionRuntime_NewAttemptOrStatusSupersedesDisplaySettlement(string change)
    {
        using var transport = new ScriptedSignalingTransport();
        using var runtime = CreateRuntime(transport);
        ObserveStatus(runtime, transport, new(false, Pending, Scope));
        ObserveSnapshot(runtime, new(Transfer() with { State = FileTransferTransferState.Completed }, null));
        Assert.Equal("file_transfer_tuna_idle", runtime.TransportAccelerationPresentation.Reason);
        var replacement = new SessionFileTransferSnapshot(Transfer() with { TransferId = "replacement" }, null);
        ObserveSnapshot(runtime, replacement);
        TransportAccelerationStateChangedEventArgs next = change switch
        {
            "session" => new(false, Pending, Scope with { SessionId = "next-session" }),
            "transfer" => new(false, Pending, Scope with { TransferId = "replacement" }),
            "transaction" => new(false, Pending, Scope with { TransactionGeneration = 2 }),
            "offer" => new(false, Pending, Scope with { OfferGeneration = 3 }),
            "unscoped" => new TransportAccelerationStateChangedEventArgs(false, Pending),
            "reason" => new(false, "waiting_for_answer", Scope),
            "active" => new(true, "free_dialer_active"),
            _ => new(false, "transport_unwired"),
        };
        ObserveStatus(runtime, transport, next);
        Assert.Equal(next.Reason, runtime.TransportAccelerationPresentation.Reason);
        Assert.Equal(next.PendingFileTransfer, runtime.TransportAccelerationPresentation.PendingFileTransfer);
        Assert.Equal(next.IsActive, runtime.IsTransportAccelerationActive);
        if (change == "active") ObserveSnapshot(runtime, new(null, null));
        var projection = ProjectRuntime(runtime, change == "active" ? new(null, null) : replacement);
        Assert.Equal(change == "active", projection.IsActive);
        // An unwired transport with an independently ready listener is still
        // allowed to show waiting-for-peer. Use an inactive runtime for teardown.
        Assert.Equal(change is not ("active" or "unwire"),
            Present(projection, change == "unwire" ? "inactive" : "listener_ready").IsConnecting);
        Assert.False(Present(projection, change == "unwire" ? "inactive" : "listener_ready").IsIdle);
    }

    [Fact]
    public void SessionRuntime_PendingStatusAfterTerminalSnapshotIsSettledBeforeReplacement()
    {
        using var transport = new ScriptedSignalingTransport();
        using var runtime = CreateRuntime(transport);
        ObserveSnapshot(runtime, new(Transfer() with { State = FileTransferTransferState.Completed }, null));
        ObserveStatus(runtime, transport, new(false, Pending, Scope));
        ObserveSnapshot(runtime, new(Transfer() with { TransferId = "replacement" }, null));
        Assert.Equal("file_transfer_tuna_idle", runtime.TransportAccelerationPresentation.Reason);
        Assert.Equal(Pending, runtime.TransportAccelerationStatusReason);
    }

    [Theory]
    [InlineData("waiting_for_answer", false, true)]
    [InlineData("negotiation_scheduled_session_security_state_ready", false, true)]
    [InlineData("header_switch_off", false, false)]
    [InlineData("handoff_rejected", false, false)]
    [InlineData("free_dialer_active", true, false)]
    public void IndependentSessionState_WinsOverOldFileCompletion(string reason, bool active, bool connecting)
    {
        var projection = TunaHeaderStateProjector.FromSnapshot(
            new(Transfer() with { State = FileTransferTransferState.Completed }, null), "NKN", active, reason, Scope);
        Assert.Equal(active, projection.IsActive);
        Assert.Equal(reason, projection.StatusReason);
        Assert.Equal(connecting, Present(projection).IsConnecting);
        Assert.False(Present(projection).IsIdle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadyTooltip_DoesNotAttributeAStaleOrMixedFileRoute(bool localPayer)
    {
        var regular = Transfer();
        var tuna = Transfer(FileTransferDirection.Inbound) with { TransferId = "second", RouteToken = "file_tuna_v4" };
        var payerText = localPayer ? "This computer is paying as the Tuna listener." : "The other computer is paying.";
        var reason = localPayer ? "paid_listener_active_file_regular_nkn_fallback" : "free_dialer_active_file_regular_nkn_fallback";
        foreach (var snapshot in new SessionFileTransferSnapshot[]
                 { new(null, null), new(regular with { State = FileTransferTransferState.Completed }, null), new(regular, tuna) })
        {
            var projection = TunaHeaderStateProjector.FromSnapshot(snapshot, "NKN", true, reason);
            Assert.False(projection.FileUsesRegularNkn);
            var presentation = TunaStatusPresentationMapper.FromState(projection.IsActive, projection.StatusReason, "inactive", false, projection.FileUsesRegularNkn);
            Assert.Equal("Tuna is ready. " + payerText, presentation.Text);
            Assert.Equal(localPayer, presentation.IsLocalPayer);
            Assert.False(presentation.IsConnecting);
        }
    }

    [Theory]
    [InlineData(FileTransferTransferState.PendingDecision, true)]
    [InlineData(FileTransferTransferState.Receiving, false)]
    public void RegularFileTooltip_OnlyConsidersStartedFiles(FileTransferTransferState otherState, bool expected)
    {
        var other = Transfer(FileTransferDirection.Inbound) with { State = otherState, TransferId = "other", RouteToken = "file_tuna_v4" };
        var projection = TunaHeaderStateProjector.FromSnapshot(new(Transfer(), other), "NKN", true, "paid_listener_active");
        Assert.Equal(expected, projection.FileUsesRegularNkn);
    }

    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    public void FileRouteTooltip_RegularTupleNeedsNknTransport(FileTransferDirection direction)
    {
        var transfer = Transfer(direction);
        SessionFileTransferSnapshot snapshot = direction == FileTransferDirection.Outbound ? new(transfer, null) : new(null, transfer);
        var nkn = TunaHeaderStateProjector.FromSnapshot(snapshot, "NKN", false, "transport_without_acceleration");
        var local = TunaHeaderStateProjector.FromSnapshot(snapshot, "DevLocal", false, "transport_without_acceleration");
        var detached = TunaHeaderStateProjector.FromSnapshot(snapshot, "(none)", false, "transport_unwired");
        Assert.True(nkn.FileUsesRegularNkn);
        Assert.False(local.FileUsesRegularNkn);
        Assert.False(detached.FileUsesRegularNkn);
        Assert.False(local.IsActive);
        Assert.Contains("This file uses regular NKN.", Present(nkn, "locked").Text);
        Assert.Equal("Tuna is unavailable on this local connection.", Present(local, "locked").Text);
        Assert.Equal("No active connection.", Present(detached, "inactive").Text);
    }

    [Fact]
    public void DetachedHeader_IgnoresLastTunaRouteAndPayer()
    {
        var staleFile = Transfer() with { RouteToken = "file_tuna_v4", ProtocolVersion = 4 };
        var detached = TunaHeaderStateProjector.FromSnapshot(new(staleFile, null), "(none)", true, "paid_listener_active");
        var tip = Present(detached, "listener_ready");
        Assert.False(detached.IsActive);
        Assert.False(detached.FileUsesRegularNkn);
        Assert.Equal("No active connection.", tip.Text);
        Assert.False(tip.IsConnecting);
        Assert.False(tip.IsLocalPayer);
    }

    [Theory]
    [InlineData("header_switch_off", "inactive", "Tuna was turned off for this session.")]
    [InlineData("inactive", "locked", "Tuna wallet is locked.")]
    public void FileRouteTooltip_StatesRouteOnce(string reason, string runtime, string expectedReason)
    {
        foreach (var hasFile in new[] { false, true })
        {
            var status = TunaStatusPresentationMapper.FromState(false, reason, runtime, false, hasFile);
            var route = hasFile ? "This file uses regular NKN." : "Regular NKN is being used.";
            Assert.Equal(expectedReason + " " + route, status.Text);
            Assert.False(status.IsConnecting);
            Assert.False(status.IsLocalPayer);
        }
    }

    [Theory]
    [InlineData("cap_reached", "Tuna cap reached.", false)]
    [InlineData("provider_paths_degraded", "Tuna relay paths are degraded. Tuna is retrying.", true)]
    [InlineData("listener_start_timeout", "Tuna listener startup timed out. Retrying if possible.", true)]
    public void FileRouteTooltip_PreservesReasonAlongsideCurrentRoute(string reason, string expectedReason, bool connecting)
    {
        var status = TunaStatusPresentationMapper.FromState(false, reason, "inactive", true, fileUsesRegularNkn: true);
        Assert.Equal(expectedReason + " This file uses regular NKN.", status.Text);
        Assert.Equal(connecting, status.IsConnecting);
    }

    [Theory]
    [InlineData("free_dialer_active")]
    [InlineData("paid_listener_active")]
    public void SessionRuntime_FileCompletionPreservesIndependentSessionTunaActivity(string reason)
    {
        using var transport = new ScriptedSignalingTransport();
        using var runtime = CreateRuntime(transport);
        ObserveStatus(runtime, transport, new(true, reason));
        var transfer = Transfer() with { RouteToken = "file_tuna_v4" };
        ObserveSnapshot(runtime, new(transfer, null));

        foreach (var snapshot in new SessionFileTransferSnapshot[]
        {
            new(transfer with { State = FileTransferTransferState.Completed }, null),
            new(null, null),
        })
        {
            ObserveSnapshot(runtime, snapshot);
            // Independent session/Screen activity must survive file completion
            // and removal of its card; idle presentation has no transport authority.
            Assert.True(runtime.IsTransportAccelerationActive);
            Assert.True(runtime.TransportAccelerationPresentation.IsActive);
            Assert.Equal(reason, runtime.TransportAccelerationStatusReason);
            var projection = ProjectRuntime(runtime, snapshot);
            Assert.True(projection.IsActive);
            Assert.Equal(reason, projection.StatusReason);
            Assert.False(Present(projection).IsIdle);
            Assert.False(Present(projection).IsConnecting);
        }
    }

    [Theory]
    [InlineData("regular_nkn_v4_fast", 4)]
    [InlineData("post_tuna_fallback_v6", 6)]
    [InlineData("file_tuna_v6", 6)]
    [InlineData("file_tuna_v4", 6)]
    public void PendingIdentity_DoesNotAuthorizeAnActiveRoute(string route, int protocol)
    {
        Assert.False(Project(Transfer() with { RouteToken = route, ProtocolVersion = protocol }, Scope).IsActive);
    }

    [Fact]
    public void SessionRuntime_ForwardsIdentityOnlyChanges_RejectsOldTransport_AndClearsOnUnwire()
    {
        using var transport = new ScriptedSignalingTransport();
        using var stale = new ScriptedSignalingTransport();
        using var runtime = new SessionRuntime(() => transport);
        typeof(SessionRuntime).GetField("transport", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runtime, transport);
        var handler = typeof(SessionRuntime).GetMethod("OnTransportAccelerationStateChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var changes = 0;
        runtime.TransportAccelerationStateChanged += (_, _) => changes++;
        void Observe(object sender, TransportAccelerationPendingFileTransfer? scope)
            => handler.Invoke(runtime, [sender, new TransportAccelerationStateChangedEventArgs(false, Pending, scope)]);

        Observe(transport, Scope);
        var firstPresentation = runtime.TransportAccelerationPresentation;
        Assert.Equal(Scope, runtime.TransportAccelerationPresentation.PendingFileTransfer);
        Assert.False(runtime.IsTransportAccelerationActive);
        Observe(stale, Scope with { TransferId = "stale" });
        Assert.Equal(Scope, runtime.TransportAccelerationPresentation.PendingFileTransfer);
        Assert.Equal(1, changes);

        var next = Scope with { TransferId = "next", OfferGeneration = 3 };
        Observe(transport, next);
        Assert.Equal(next, runtime.TransportAccelerationPresentation.PendingFileTransfer);
        Assert.Equal(Scope, firstPresentation.PendingFileTransfer);
        Assert.Equal(Pending, firstPresentation.Reason);
        Assert.False(firstPresentation.IsActive);
        Assert.NotSame(firstPresentation, runtime.TransportAccelerationPresentation);
        Assert.Equal(2, changes);
        // A new unscoped session/Screen attempt with the same text must not
        // inherit the old file's completed identity.
        Observe(transport, null);
        Assert.Null(runtime.TransportAccelerationPresentation.PendingFileTransfer);
        Assert.Equal(3, changes);
        Observe(transport, next);
        typeof(SessionRuntime).GetMethod("SetTransportAccelerationActive", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [false, "transport_unwired", null]);
        Assert.Null(runtime.TransportAccelerationPresentation.PendingFileTransfer);
    }

    private static TunaHeaderStateProjection Project(FileTransferTransferSnapshot transfer,
        TransportAccelerationPendingFileTransfer? scope)
        => TunaHeaderStateProjector.FromSnapshot(transfer.Direction == FileTransferDirection.Outbound
            ? new(transfer, null) : new(null, transfer), "NKN", false, Pending, scope);

    private static TunaStatusPresentation Present(TunaHeaderStateProjection projection, string runtime = "listener_ready")
        => TunaStatusPresentationMapper.FromState(projection.IsActive, projection.StatusReason, runtime, true, projection.FileUsesRegularNkn);

    private static SessionRuntime CreateRuntime(ScriptedSignalingTransport transport)
    {
        var runtime = new SessionRuntime(() => transport);
        typeof(SessionRuntime).GetField("transport", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runtime, transport);
        return runtime;
    }

    private static void ObserveStatus(SessionRuntime runtime, object sender, TransportAccelerationStateChangedEventArgs status)
        => typeof(SessionRuntime).GetMethod("OnTransportAccelerationStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [sender, status]);

    private static void ObserveSnapshot(SessionRuntime runtime, SessionFileTransferSnapshot snapshot)
        => typeof(SessionRuntime).GetMethod("OnFileTransferChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(runtime, [null, new SessionFileTransferSnapshotChangedEventArgs(snapshot)]);

    private static TunaHeaderStateProjection ProjectRuntime(SessionRuntime runtime, SessionFileTransferSnapshot snapshot)
    {
        var status = runtime.TransportAccelerationPresentation;
        return TunaHeaderStateProjector.FromSnapshot(snapshot, "NKN", status.IsActive, status.Reason, status.PendingFileTransfer);
    }

    private static FileTransferTransferSnapshot Transfer(FileTransferDirection direction = FileTransferDirection.Outbound)
        => new("session", "transfer", direction,
            direction == FileTransferDirection.Outbound ? FileTransferTransferState.Sending : FileTransferTransferState.Receiving,
            "payload.bin", 1024, null, 0, 0, 1, 1024, null, "Transferring",
            RouteToken: "regular_nkn_v4_fast", ProtocolVersion: 4);
}
