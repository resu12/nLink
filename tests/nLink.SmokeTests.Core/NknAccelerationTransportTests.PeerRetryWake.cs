using System.Collections;
using System.Collections.Concurrent;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData("local_stop")]
    [InlineData("peer_stop")]
    [InlineData("dialer_late")]
    [InlineData("coordinator_reject")]
    [InlineData("old_timestamp")]
    [InlineData("stale_decision")]
    [InlineData("wrong_session")]
    [InlineData("expired")]
    [InlineData("not_unlock")]
    [InlineData("data_session_replaced")]
    [InlineData("terminal")]
    [InlineData("no_pending_intent")]
    [Trait("Category", "Smoke")]
    [Trait("FileTransferInfraSuite", "RuntimeUnlockRetryDispatch")]
    public async Task RuntimeUnlockRetryDispatch_HelperOnlyReenable_RechecksOnlyExactCoordinatorIntent(string scenario)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var options = NknTransportOptions.Load();
        var hostClient = new FakeNknClient("host.peer-retry-wake.address");
        var helperClient = new FakeNknClient("helper.peer-retry-wake.address");
        var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0)
        {
            StrictPayerHandoffForTests = true,
        };
        using var host = new NknSignalingTransport(hostClient, options,
            new NknIdentity("host-peer-retry-wake-id", hostClient.Address),
            NknTunaAccelerationOptions.Disabled, hostLane);
        using var helper = new NknSignalingTransport(helperClient, options,
            new NknIdentity("helper-peer-retry-wake-id", helperClient.Address));
        var sessionId = await ApproveNknSessionAsync(host, helper, cts.Token,
            InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
            suppressAutomaticAccelerationNegotiation: true);
        const string transferId = "transfer_peer_retry_wake";
        _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
        var peerStopped = scenario == "peer_stop";
        InvokePrivateMethod(host, peerStopped
            ? "MarkAccelerationPeerUserStoppedForCurrentSession"
            : "MarkAccelerationUserStoppedForCurrentSession", sessionId);
        var stoppedAt = Assert.IsType<long>(GetPrivateField(host, peerStopped
            ? "accelerationPeerUserStoppedUtcMs" : "accelerationUserStoppedUtcMs"));
        var logStart = GetOperationalLogLength();
        var requests = new ConcurrentQueue<RuntimeUnlockRetryDispatchAuthorityRequest>();
        var request = QueueExactFallbackPairedTailYieldRuntimeUnlockRetryForTests(host, sessionId, transferId,
            "runtime_unlock_offer_send_not_observed", "fallback_tail_pairing_committed",
            observed =>
            {
                requests.Enqueue(observed);
                return scenario == "coordinator_reject"
                    ? RuntimeUnlockRetryDispatchDecisionStatus.Rejected
                    : RuntimeUnlockRetryDispatchDecisionStatus.Granted;
            });
        await host.DrainCoordinatorQueuedRuntimeUnlockRetryDispatchAuthorityRequestsForTestsAsync();
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains(
            "decision_reason=retry_dispatch_negotiation_slot_ineligible", StringComparison.Ordinal),
            TimeSpan.FromSeconds(4));
        var pending = GetPrivateField(host, "pendingRuntimeUnlockRetryNegotiationIntent");
        Assert.NotNull(pending);
        Assert.Empty(requests);
        Assert.False(hostLane.CanOfferListener);
        Assert.Null(GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(host));

        var payload = new TransportAccelerationPayerIntentPayload
        {
            SessionId = scenario == "wrong_session" ? "different-session" : sessionId,
            SenderRole = "helper",
            Intent = "will_listen",
            SupportedLanes = ["file"],
            Trigger = scenario == "not_unlock" ? "session_approved" : "runtime_unlock",
            PayerDecisionId = 9,
            SentAtUnixMs = scenario == "old_timestamp" ? stoppedAt - 1 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ExpiresAtUnixMs = scenario == "expired" ? 1 : DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
            Nonce = "aa11223344556677889900aabbccddee",
            SidecarProtocolVersion = 1,
        };
        if (scenario == "stale_decision")
            Assert.True(Assert.IsType<bool>(InvokePrivateMethod(host, "TryObserveRemotePayerDecision", 10L, "test")));
        if (scenario == "terminal")
            SetPrivateProperty(GetPrivateField(host, "runtimeUnlockRecoveryRetryState")!,
                "ContractState", SessionRecoveryContractState.Failed);
        if (scenario == "no_pending_intent")
            SetPrivateField(host, "pendingRuntimeUnlockRetryNegotiationIntent", null);
        var sessions = Assert.IsAssignableFrom<IDictionary>(GetPrivateField(host, "fileTransferDataSessions"));
        var originalSession = sessions[transferId]!;
        IDisposable? replacement = null;
        if (scenario == "data_session_replaced")
        {
            replacement = Assert.IsAssignableFrom<IDisposable>(Activator.CreateInstance(
                originalSession.GetType(), host, sessionId, transferId, 2L));
            sessions[transferId] = replacement;
        }
        try
        {
            // Seed only physical dialer readiness, not an authenticated handshake or a route grant.
            if (scenario != "dialer_late")
                Assert.True(await hostLane.StartDialerSidecarAsync("test-helper-listener", helperClient.Address, cts.Token));
            Assert.Empty(requests);
            var envelope = BuildSecureAccelerationEnvelope(helper, MsgType.TransportAccelerationPayerIntent,
                payload, "transport_acceleration_payer_intent", payload.Nonce, sequence: 1000);
            InvokePrivateMethod(host, "HandleTransportAccelerationPayerIntent", helperClient.Address, envelope);
            if (scenario == "dialer_late")
            {
                await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains(
                    "decision_reason=retry_dispatch_listener_preparation_source_scope_replaced", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(3));
                Assert.Empty(requests);
                Assert.Null(GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(host));
                Assert.True(await hostLane.StartDialerSidecarAsync("test-helper-listener", helperClient.Address, cts.Token));
            }
            await host.DrainCoordinatorQueuedRuntimeUnlockRetryDispatchAuthorityRequestsForTestsAsync();

            var shouldRequest = scenario is "local_stop" or "peer_stop" or "dialer_late" or "coordinator_reject";
            if (shouldRequest)
            {
                await WaitUntilAsync(() => requests.Count > 0, TimeSpan.FromSeconds(3));
                if (scenario != "coordinator_reject")
                    await WaitUntilAsync(() => GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(host) is not null,
                        TimeSpan.FromSeconds(3));
                await host.DrainRuntimeUnlockRetryDispatchInstallFollowUpsForTestsAsync();
                Assert.True(requests.Count > 0, ReadOperationalLogTail(logStart));
                Assert.Equal(request, Assert.Single(requests));
                Assert.False(host.IsAccelerationUserStoppedForCurrentSessionForTests);
                Assert.Null(GetPrivateField(host, "accelerationPeerUserStoppedSessionId"));
                Assert.Contains("trigger=peer_payer_intent", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
            }
            else
            {
                Assert.Empty(requests);
                Assert.Equal(scenario is "old_timestamp" or "stale_decision" or "wrong_session" or "expired" or "not_unlock",
                    host.IsAccelerationUserStoppedForCurrentSessionForTests);
            }

            if (scenario is "local_stop" or "peer_stop" or "dialer_late")
            {
                var receipt = Assert.IsType<RuntimeUnlockRetryDispatchInstallReceipt>(
                    GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(host));
                Assert.Equal(request, receipt.Request);
                Assert.Equal(request.SourcePublicationAuthority!.RoutePublicationGeneration,
                    receipt.Authority.SourcePublicationAuthority!.PublicationGeneration);
                Assert.Equal(RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield, receipt.Authority.ReleaseKind);
                AssertRuntimeUnlockDispatchGrantBeforeSchedule(ReadOperationalLogTail(logStart));
                // Replayed wire evidence cannot install or request a second retry.
                InvokePrivateMethod(host, "HandleTransportAccelerationPayerIntent", helperClient.Address, envelope);
                await host.DrainRuntimeUnlockRetryDispatchInstallFollowUpsForTestsAsync();
                Assert.Single(requests);
                Assert.Equal(receipt, GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(host));
            }
            else
            {
                Assert.Null(GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(host));
                Assert.DoesNotContain("event=runtime_unlock_retry_dispatch_authority_granted;",
                    ReadOperationalLogTail(logStart), StringComparison.Ordinal);
            }
            Assert.Equal(1, hostLane.StartDialerCalls);
            if (scenario != "dialer_late") Assert.Equal(0, hostLane.EnsureListenerCalls);
            Assert.False(hostLane.IsLocalPaidListenerActive);
            Assert.Equal(FileTransferRouteResolver.PostTunaFallbackV6Token,
                CaptureCurrentRuntimeUnlockFileTransferOwnerAuthorityForTests(host, sessionId).RouteToken);
        }
        finally
        {
            sessions[transferId] = originalSession;
            replacement?.Dispose();
        }
    }
}
