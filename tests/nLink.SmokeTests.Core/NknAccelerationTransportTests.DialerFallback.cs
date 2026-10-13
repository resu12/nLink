using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [Trait("Category", "Smoke")]
    [Trait("FileTransferInfraSuite", "RuntimeUnlockRetryDispatch")]
    public Task RuntimeUnlockRetryDispatch_ConnectedDialerPreservesRoleAndRequiresCoordinatorGrant(
        bool canListen, bool coordinatorHolds)
        => RunConnectedDialerRetryAsync(canListen, coordinatorHolds, continueAfterInstall: false);

    [Fact]
    [Trait("Category", "Smoke")]
    [Trait("FileTransferInfraSuite", "RuntimeUnlockRetryDispatch")]
    public Task RuntimeUnlockRetryDispatch_ConnectedDialerContinuationDoesNotRequireLocalPaidListener()
        => RunConnectedDialerRetryAsync(false, false, continueAfterInstall: true);

    private async Task RunConnectedDialerRetryAsync(
        bool canListen, bool coordinatorHolds, bool continueAfterInstall)
    {
        var previousBarrier = NknSignalingTransport.RuntimeUnlockRetryDispatchInstallFollowUpBarrierForTests;
        var releaseFollowUp = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decisionRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.fallback-dialer.address");
            var helperClient = new FakeNknClient("helper.fallback-dialer.address");
            var listener = new RetryableTunaAccelerationSession(true, 0, NknAccelerationLaneKind.File);
            var dialer = new RetryableTunaAccelerationSession(canListen, 0, NknAccelerationLaneKind.File)
            {
                StrictPayerHandoffForTests = true,
            };
            using var host = new NknSignalingTransport(hostClient, options,
                new NknIdentity("host-fallback-dialer-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled, listener);
            using var helper = new NknSignalingTransport(helperClient, options,
                new NknIdentity("helper-fallback-dialer-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled, dialer);
            var sessionId = await ApproveNknSessionAsync(host, helper, cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer,
                suppressAutomaticAccelerationNegotiation: true);
            const string transferId = "transfer_fallback_connected_dialer";
            _ = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            listener.MarkCoordinatorScopedListenerAvailableForTests(helperClient.Address);
            Assert.True(await dialer.StartDialerSidecarAsync(listener.LocalTunaAddress!, hostClient.Address, cts.Token));
            var lease = Assert.IsType<NknTunaLaneLease>(dialer.CaptureCurrentLaneLease());
            Assert.NotNull(lease.DialerOwner);
            Assert.Null(lease.Listener);
            Assert.Null(dialer.CaptureCurrentListenerAdoptionSnapshot());
            NknSignalingTransport.RuntimeUnlockRetryDispatchInstallFollowUpBarrierForTests = () => releaseFollowUp.Task;

            var logStart = GetOperationalLogLength();
            var request = QueueExactFallbackPairedTailYieldRuntimeUnlockRetryForTests(helper,
                sessionId, transferId, "runtime_unlock_offer_send_not_observed", "post_tuna_fallback_tail_unblocked",
                statusSelector: _ =>
                {
                    decisionRequested.TrySetResult(true);
                    return coordinatorHolds ? RuntimeUnlockRetryDispatchDecisionStatus.Held
                        : RuntimeUnlockRetryDispatchDecisionStatus.Granted;
                });
            await helper.DrainCoordinatorQueuedRuntimeUnlockRetryDispatchAuthorityRequestsForTestsAsync();
            await decisionRequested.Task.WaitAsync(TimeSpan.FromSeconds(4), cts.Token);
            await WaitUntilAsync(() => coordinatorHolds
                    ? ReadOperationalLogTail(logStart).Contains("test_coordinator_held", StringComparison.Ordinal)
                    : GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(helper).HasValue,
                TimeSpan.FromSeconds(4));

            var receipt = GetLastCommittedRuntimeUnlockRetryDispatchInstallReceipt(helper);
            var tail = ReadOperationalLogTail(logStart);
            if (coordinatorHolds)
            {
                Assert.Null(receipt);
                Assert.DoesNotContain("event=runtime_unlock_retry_dispatch_install_committed;", tail, StringComparison.Ordinal);
                Assert.DoesNotContain("event=filetransfer_fallback_tail_paired_release_consumed;", tail, StringComparison.Ordinal);
            }
            else
            {
                Assert.NotNull(receipt);
                Assert.Equal(request, receipt.Value.Request);
                Assert.Equal(RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield, receipt.Value.Authority.ReleaseKind);
                Assert.Equal(RuntimeUnlockRetryDispatchListenerExecutionMode.AdoptCurrentCoordinatorScoped,
                    receipt.Value.Authority.ListenerExecutionMode);
                Assert.Contains("decision_status=Converted", tail, StringComparison.Ordinal);
            }
            Assert.Same(lease, dialer.CaptureCurrentLaneLease());
            Assert.Equal(0, dialer.EnsureListenerCalls);
            Assert.Equal(0, dialer.ListenerAdoptionCalls);
            Assert.Equal(0, dialer.StopCalls);
            Assert.False(dialer.IsLocalPaidListenerActive);
            Assert.DoesNotContain("decision_reason=retry_dispatch_listener_preparation_source_scope_replaced", tail, StringComparison.Ordinal);
            var publication = CaptureCurrentRuntimeUnlockFileTransferOwnerAuthorityForTests(helper, sessionId);
            Assert.Equal(FileTransferRouteResolver.PostTunaFallbackV6Token, publication.RouteToken);
            Assert.Equal(FileTransferProtocol.ProtocolVersionV6, publication.ProtocolVersion);
            Assert.Equal(request.SourcePublicationAuthority.RoutePublicationGeneration, publication.RoutePublicationGeneration);
            Assert.False(helper.IsAccelerationAvailableForTests);

            if (continueAfterInstall)
            {
                // The existing test stops at the install barrier. Exercise the
                // installed continuation without turning the dialer into a payer.
                releaseFollowUp.TrySetResult(true);
                await helper.DrainRuntimeUnlockRetryDispatchInstallFollowUpsForTestsAsync()
                    .WaitAsync(cts.Token);
                await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Split('\n').Any(line =>
                    line.Contains("event=runtime_unlock_post_tuna_fallback_activation_disposition;", StringComparison.Ordinal) &&
                    (line.Contains("disposition=completed;", StringComparison.Ordinal) ||
                     line.Contains("disposition=held;", StringComparison.Ordinal) ||
                     line.Contains("disposition=rejected;", StringComparison.Ordinal))), TimeSpan.FromSeconds(4));
                var continuation = ReadOperationalLogTail(logStart);
                Assert.DoesNotContain(
                    "event=tuna_acceleration_offer_preflight_rejected; reason=listener_unavailable;",
                    continuation, StringComparison.Ordinal);
                // A physical connection and retry grant alone do not supply
                // the missing authenticated peer handshake.
                Assert.Contains("event=runtime_unlock_sender_dialer_continuation; decision_status=Held;",
                    continuation, StringComparison.Ordinal);
                Assert.DoesNotContain("decision_reason=authenticated_handshake_requires_coordinator_probe_admission",
                    continuation, StringComparison.Ordinal);
                Assert.Equal(0, dialer.EnsureListenerCalls);
                Assert.Equal(0, dialer.ListenerAdoptionCalls);
                Assert.Same(lease, dialer.CaptureCurrentLaneLease());
            }
        }
        finally
        {
            releaseFollowUp.TrySetResult(true);
            NknSignalingTransport.RuntimeUnlockRetryDispatchInstallFollowUpBarrierForTests = previousBarrier;
            FakeNknClient.ResetNetwork();
        }
    }
}
