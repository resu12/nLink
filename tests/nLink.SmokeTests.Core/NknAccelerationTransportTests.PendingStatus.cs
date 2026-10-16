using System.Reflection;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData("file")]
    [InlineData("file_screen")]
    [Trait("Category", "Smoke")]
    public void PendingStatus_CarriesExactAttemptIdentityWithoutRouteOrRuntimeEffects(string lanes)
    {
        var client = new FakeNknClient("pending-status.address");
        var lane = new FakeNknAccelerationLane();
        using var transport = new NknSignalingTransport(client, NknTransportOptions.Load(),
            new NknIdentity("pending-status", client.Address), NknTunaAccelerationOptions.Disabled, lane);
        const string session = "pending-session";
        var transaction = RuntimeUnlockTransactionSnapshot.Idle with
        {
            SessionId = session, TransactionGeneration = 1, OfferGeneration = 2,
            State = RuntimeUnlockTransactionState.RouteCommitPending, RouteCommitPending = true,
        };
        var lease = new RuntimeUnlockTunaPathLeaseSnapshot(session, "transfer", 1, 2, 3, "listener", 4,
            RuntimeUnlockTunaPathLeaseState.ListenerReady, 1, 1, long.MaxValue, null, null);
        SetPrivateField(transport, "currentSessionSecurityState", SessionSecurityState.Empty with { SessionId = new SessionId(session) });
        SetPrivateField(transport, "accelerationSessionId", session);
        SetPrivateField(transport, "accelerationNegotiatedLanes", lanes == "file"
            ? NknAccelerationLaneKind.File : NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen);
        SetPrivateField(transport, "runtimeUnlockTransactionState", transaction);
        SetPrivateField(transport, "runtimeUnlockTunaPathLeaseState", lease);
        var events = new List<TransportAccelerationStateChangedEventArgs>();
        transport.TransportAccelerationStateChanged += (_, args) => events.Add(args);
        void Notify() => typeof(NknSignalingTransport).GetMethod("NotifyTransportAccelerationStateChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(transport, ["negotiated"]);

        Notify();
        Assert.Equal("runtime_unlock_pending_route_commit", events.Single().Reason);
        Assert.Equal(new TransportAccelerationPendingFileTransfer(session, "transfer", 1, 2),
            events.Single().PendingFileTransfer);
        Assert.Equal(events.Single().PendingFileTransfer, transport.PendingAccelerationFileTransfer);
        Assert.False(events.Single().IsActive);
        Notify();
        Assert.Single(events);

        var nextTransaction = transaction with { TransactionGeneration = 5, OfferGeneration = 6 };
        var nextLease = lease with { TransferId = "next-transfer", TransactionGeneration = 5, OfferGeneration = 6 };
        SetPrivateField(transport, "runtimeUnlockTransactionState", nextTransaction);
        SetPrivateField(transport, "runtimeUnlockTunaPathLeaseState", nextLease);
        Notify();
        Assert.Equal(2, events.Count);
        Assert.Equal(new TransportAccelerationPendingFileTransfer(session, "next-transfer", 5, 6), events[^1].PendingFileTransfer);

        // Retirement must not erase the identity of the same old pending attempt
        // and make a completed transfer start pulsing again. This grants no activity.
        SetPrivateField(transport, "runtimeUnlockTunaPathLeaseState",
            nextLease with { State = RuntimeUnlockTunaPathLeaseState.Retired });
        Notify();
        Assert.Equal(new TransportAccelerationPendingFileTransfer(session, "next-transfer", 5, 6),
            transport.PendingAccelerationFileTransfer);
        Assert.False(transport.IsTransportAccelerationActive);
        Assert.Equal(2, events.Count);

        // Unbound, stale, or session-only readiness must not be promoted
        // into the display identity of the current file attempt.
        foreach (var invalid in new[]
        {
            lease,
            nextLease with { SessionId = "stale-session" },
            nextLease with { TransactionGeneration = 0, OfferGeneration = 0 },
            nextLease with { LeaseGeneration = 0 },
            nextLease with { TransferId = null },
        })
        {
            SetPrivateField(transport, "runtimeUnlockTunaPathLeaseState", invalid);
            Notify();
            Assert.Null(transport.PendingAccelerationFileTransfer);
            Assert.False(transport.IsTransportAccelerationActive);
            Assert.False(transport.IsFileTunaActiveForRouteSelection);
            Assert.Equal(nextTransaction, GetPrivateField(transport, "runtimeUnlockTransactionState"));
            Assert.Equal(invalid, GetPrivateField(transport, "runtimeUnlockTunaPathLeaseState"));
        }
        SetPrivateField(transport, "runtimeUnlockTunaPathLeaseState", nextLease);
        SetPrivateField(transport, "runtimeUnlockTransactionState", nextTransaction with { TransferId = "different-transfer" });
        Notify();
        Assert.Null(transport.PendingAccelerationFileTransfer);
        Assert.Empty(lane.Sent);
    }
}
