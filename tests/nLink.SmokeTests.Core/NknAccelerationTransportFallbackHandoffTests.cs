using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackHandoffReservation")]
    public async Task FallbackHandoffReservation_RealQueuePublishesExactCommandOnce()
    {
        FakeNknClient.ResetNetwork();
        var options = NknTransportOptions.Load();
        var hostClient = new FakeNknClient("host.handoff-queue.address");
        var peerClient = new FakeNknClient("peer.handoff-queue.address");
        using var host = new NknSignalingTransport(hostClient, options,
            new NknIdentity("host-handoff", hostClient.Address), NknTunaAccelerationOptions.Disabled,
            new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0));
        using var peer = new NknSignalingTransport(peerClient, options,
            new NknIdentity("peer-handoff", peerClient.Address));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = await ApproveNknSessionAsync(host, peer, timeout.Token,
            InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        const string transfer = "handoff-queue-transfer";
        _ = await host.OpenFileTransferDataSessionAsync(session, transfer, timeout.Token);
        InvokePrivateMethod(host, "ClearAccelerationUserStoppedForCurrentSession");
        var request = QueueExactFallbackPairedTailYieldRuntimeUnlockRetryForTests(host, session, transfer,
            "runtime_unlock_offer_send_not_observed", "fallback_tail_pairing_committed",
            _ => RuntimeUnlockRetryDispatchDecisionStatus.Held);
        var decide = host.RuntimeUnlockRetryDispatchAuthorityDecisionOverrideForTests!;
        var source = decide(request).State;
        await host.DrainCoordinatorQueuedRuntimeUnlockRetryDispatchAuthorityRequestsForTestsAsync();
        Assert.NotNull(source);
        var repair = FileTransferCoordinator.AuthorizeAction(source!, new(
            FileTransferCoordinatorActionKind.RepairReplay, "handoff-test", "frontier",
            RuntimeOwner: source!.CurrentRuntimeOwner, RepairReplayKind: RepairReplayKind.FrontierStallRepair));
        Assert.True(repair.Accepted, repair.RejectionReason);
        Assert.True(FileTransferCoordinator.TryIssueFallbackFrontierRepairAuthority(
            repair.RepairReplayDecision!.Value, new("fallback-frontier:0:1:frng-handoff", "frng-handoff", 0, 0, 1, 1,
                FallbackFrontierRepairSourceKind.PeerRequest, "v6-frontier:0:1", "frq-handoff", "(none)",
                "frontier", "regular_nkn_frontier_stall_control_bulk", "handoff-test"),
            out var authority, out var reason), reason);
        var held = FileTransferCoordinator.BindFallbackTailYieldRetryDispatchHold(repair.State, request, authority!.Value);
        Assert.True(held.Accepted, held.Reason);
        var hold = held.State.PersistentState.FallbackTailYield.CurrentRetryDispatchHold!.Value;
        var issued = FileTransferCoordinator.IssueFallbackTailYieldRetryDispatchRecheck(held.State,
            new(hold, hold.BlockingRepair, "ProgressObserved", null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Assert.True(issued.Accepted, issued.Reason);
        var command = issued.State.PersistentState.FallbackTailYield.PendingRetryDispatchRecheckCommand!.Value;
        var competingRepair = FileTransferCoordinator.AuthorizeAction(issued.State, new(
            FileTransferCoordinatorActionKind.RepairReplay, "handoff-test", "successor",
            RuntimeOwner: issued.State.CurrentRuntimeOwner, RepairReplayKind: RepairReplayKind.FrontierStallRepair));
        Assert.False(competingRepair.Accepted);
        Assert.Equal("fallback_handoff_repair_deferred", competingRepair.RejectionReason);
        var release = command.Hold.Release;
        var unbound = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(issued.State, request,
            release.ReleaseKind, release.ReleaseAuthorityId, release.ReleaseGeneration, release.ReleaseOutcome);
        Assert.False(unbound.Granted);
        Assert.Equal("fallback_handoff_callback_not_current", unbound.Reason);
        var bound = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(issued.State, request,
            release.ReleaseKind, release.ReleaseAuthorityId, release.ReleaseGeneration, release.ReleaseOutcome, command);
        Assert.True(bound.Granted, bound.Reason);
        var bridge = (IRuntimeUnlockRetryDispatchAuthorityBridge)host;
        var callbacks = new List<RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs>();
        bridge.RuntimeUnlockRetryDispatchAuthorityRequested += (_, args) => callbacks.Add(args);
        Assert.True(bridge.QueueFallbackTailYieldRetryDispatchRecheck(command));
        Assert.True(bridge.QueueFallbackTailYieldRetryDispatchRecheck(command));
        await host.DrainCoordinatorQueuedRuntimeUnlockRetryDispatchAuthorityRequestsForTestsAsync();
        var callback = Assert.Single(callbacks);
        Assert.Equal(request, callback.Request);
        Assert.Equal(command, callback.RecheckCommand);
        Assert.Null(GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(host));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "FallbackHandoffReservation")]
    public async Task FallbackHandoffReservation_StageAndDispatchShareOriginalDeadline(bool expired)
    {
        using var fixture = await CreateRuntimeUnlockRetryDispatchInstallFixtureAsync("handoff-budget");
        var original = Assert.Single(fixture.Decisions);
        var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (expired ? -1 : 10_000);
        var authority = original.Contract!.Authority with { FallbackHandoffDeadlineUtcMs = deadline };
        var lifecycle = FileTransferCoordinator.GetRuntimeUnlockRetryLifecycle(original.State);
        var state = original.State with
        {
            PersistentState = original.State.PersistentState with
            {
                RuntimeUnlockRetryLifecycle = lifecycle with
                {
                    PendingAttempt = lifecycle.PendingAttempt!.Value with { Authority = authority },
                },
            },
        };
        var decision = original with
        {
            State = state,
            Contract = new RuntimeUnlockRetryDispatchContract(authority,
                original.Request.RequestActivePredecessorSupersession,
                original.Request.RegularV4LivenessGeneration, original.Request.BridgeDecisionGeneration),
        };
        var stage = fixture.Bridge.StageRuntimeUnlockRetryDispatchDecision(decision);
        if (expired)
        {
            Assert.False(stage.Accepted);
            Assert.Equal("retry_dispatch_handoff_deadline_expired", stage.Reason);
            return;
        }
        Assert.True(stage.Accepted, stage.Reason);
        Assert.Equal(deadline, stage.StageExpiresUtcMs);
        var duplicate = fixture.Bridge.StageRuntimeUnlockRetryDispatchDecision(decision);
        Assert.Equal(deadline, duplicate.StageExpiresUtcMs);
        var issued = IssueRuntimeUnlockRetryDispatchInstallReceiptForTests(decision, stage);
        Assert.Equal(deadline, issued.Receipt.StageExpiresUtcMs);
        Assert.Equal(deadline, issued.Receipt.DispatchDeadlineUtcMs);
    }
}
