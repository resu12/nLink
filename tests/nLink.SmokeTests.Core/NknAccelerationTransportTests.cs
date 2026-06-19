using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using NLink.Core;
using NLink.Core.Configuration;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "Core")]
public sealed class NknAccelerationTransportTests : CoreSmokeTestsBase
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RecoveryStateContract_RuntimeUnlockRetryDispatchesBeforeLivenessTerminalizes()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousStaleWindow = NknSignalingTransport.RuntimeUnlockRecoveryContractStaleNegotiationWindowOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        var previousReceiveRecoveryBlocker = NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests;
        var previousObservedBlocker = NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests;
        var previousPressureOverride = NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests;
        var previousAuthorityDeadline = NknSignalingTransport.RuntimeUnlockRetryAuthorityDeadlineOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoveryContractStaleNegotiationWindowOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.FromSeconds(20);
        NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests = null;
        NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests = null;
        NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests = null;
        NknSignalingTransport.RuntimeUnlockRetryAuthorityDeadlineOverrideForTests = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var options = NknTransportOptions.Load();
            var tunaSidecarPath = Path.Combine(Path.GetTempPath(), "nlink-recovery-contract-runtime-unlock.exe");
            var hostTunaOptions = NknTunaAccelerationOptions.CreateRuntimePilot(
                tunaSidecarPath,
                NknAccelerationLaneKind.File);
            var helperTunaOptions = NknTunaAccelerationOptions.CreatePassiveDialer(
                tunaSidecarPath,
                NknAccelerationLaneKind.File);
            var hostClient = new FakeNknClient("host.recovery.contract.runtime-unlock.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.recovery.contract.runtime-unlock.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-recovery-contract-runtime-unlock-id", hostClient.Address),
                hostTunaOptions,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-recovery-contract-runtime-unlock-id", helperClient.Address),
                helperTunaOptions,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_recovery_contract_runtime_unlock";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            host.SeedSessionLivenessProofForTests(sessionId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "runtime_unlock_offer_send_not_observed");
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                7L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "tuna_activation_offer_send_timeout");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=session_recovery_contract_started;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));

            SetPrivateField(host, "accelerationNegotiationScheduled", 1);
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            hostLane.SetCanListen(true);
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationIfEligible", "runtime_unlock");

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=session_recovery_contract_stale_negotiation_superseded;", StringComparison.Ordinal) &&
                           tail.Contains("event=session_recovery_contract_retry_dispatched;", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(5));

            var contractProvider = Assert.IsAssignableFrom<ISessionRecoveryStateContract>(host);
            if (contractProvider.TryGetActiveSessionRecoveryContract(sessionId, out var snapshot))
            {
                Assert.Equal(SessionRecoveryContractKind.RuntimeUnlockActivation, snapshot.Kind);
                Assert.True(snapshot.RetryDispatched);
                Assert.False(snapshot.RetryRequired);
                Assert.True(snapshot.QueuedBehindActiveNegotiation);
            }

            var logTail = ReadOperationalLogTail(logStart);
            var positiveLogTail = logTail + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=session_recovery_contract_retry_queued;", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_retry_authority_granted;", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_stale_negotiation_superseded;", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_retry_dispatched;", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_queued; reason=runtime_unlock;", positiveLogTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=session_liveness_timeout;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRecoveryContractStaleNegotiationWindowOverrideForTests = previousStaleWindow;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests = previousReceiveRecoveryBlocker;
            NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests = previousObservedBlocker;
            NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests = previousPressureOverride;
            NknSignalingTransport.RuntimeUnlockRetryAuthorityDeadlineOverrideForTests = previousAuthorityDeadline;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RecoveryStateContract_RuntimeUnlockAuthorityTrustsBulkEndpointWithoutRecentPeerProof()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousBulkBypassWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousPeerProofFreshness = NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(500);
        NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = TimeSpan.FromMilliseconds(-1);
        var blockedControlOffer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.recovery.authority.peer-proof.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.recovery.authority.peer-proof.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            hostClient.BeforeSendCoreAsync = async (destination, payload, channel, ct) =>
            {
                if (channel == NknBridgeChannel.Control &&
                    string.Equals(destination, helperClient.ConnectedAddress, StringComparison.Ordinal) &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    await blockedControlOffer.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-recovery-authority-peer-proof-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-recovery-authority-peer-proof-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_recovery_authority_peer_proof";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            SetPrivateField(host, "remoteBulkEndpoint", helperClient.ConnectedBulkAddress);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "runtime_unlock_offer_send_not_observed");
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                11L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "tuna_activation_offer_send_timeout");
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            hostLane.SetCanListen(true);
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationIfEligible", "runtime_unlock");

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=session_recovery_contract_retry_authority_observed;", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_offer_queued; reason=runtime_unlock;", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(5));
            var logTail = ReadOperationalLogTail(logStart);
            var positiveLogTail = logTail + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
            Assert.Contains(
                "event=tuna_acceleration_control_send_preferred_bulk_observed_lane_selected; purpose=offer",
                positiveLogTail,
                StringComparison.Ordinal);
            Assert.True(
                positiveLogTail.Contains("observed_lane=control_to_bulk_endpoint", StringComparison.Ordinal) ||
                positiveLogTail.Contains("observed_lane=bulk_queue_fallback", StringComparison.Ordinal),
                positiveLogTail);
            Assert.Contains("event=session_recovery_contract_retry_authority_observed;", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("authorized_observed_lane=control_to_bulk_endpoint", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_queued; reason=runtime_unlock;", positiveLogTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_control_send_observed_without_recent_peer_proof;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_control_observed_untrusted;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=session_recovery_contract_retry_authority_send_blocked;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_activation_offer_not_observed;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("queue_local_only=1", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedControlOffer.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousBulkBypassWait;
            NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = previousPeerProofFreshness;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RecoveryStateContract_RuntimeUnlockAuthorityTrustsBulkEndpointWithCurrentPostTunaFallbackProof()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousBulkBypassWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousPeerProofFreshness = NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromSeconds(5);
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(500);
        NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = TimeSpan.FromMilliseconds(-1);
        var blockedControlOffer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var tunaSidecarPath = Path.Combine(Path.GetTempPath(), "nlink-recovery-authority-fallback-proof.exe");
            var hostClient = new FakeNknClient("host.recovery.authority.fallback-proof.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.recovery.authority.fallback-proof.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            hostClient.BeforeSendCoreAsync = async (destination, payload, channel, ct) =>
            {
                if (channel == NknBridgeChannel.Control &&
                    string.Equals(destination, helperClient.ConnectedAddress, StringComparison.Ordinal) &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    await blockedControlOffer.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-recovery-authority-fallback-proof-id", hostClient.Address),
                NknTunaAccelerationOptions.CreateRuntimePilot(tunaSidecarPath, NknAccelerationLaneKind.File),
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-recovery-authority-fallback-proof-id", helperClient.Address),
                NknTunaAccelerationOptions.CreatePassiveDialer(tunaSidecarPath, NknAccelerationLaneKind.File),
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_recovery_authority_fallback_proof";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            SetPrivateField(host, "remoteBulkEndpoint", helperClient.ConnectedBulkAddress);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            var authorityRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "post_tuna_fallback_state_refresh_failed")
            {
                RouteToken = FileTransferRouteResolver.PostTunaFallbackV6Token,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                LiveRouteEpoch = 2,
                TransferLegGeneration = 3,
                BridgeRecoveryGeneration = 1,
                TransportEpoch = 17,
                CheckpointRequestId = "v6-regular-nkn-state-refresh:17",
                AuthorityReason = "post_tuna_fallback_state_refresh_failed",
            };
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackLegAuthorityStarted",
                authorityRequest,
                sessionId,
                transferId,
                "post_tuna_fallback_state_refresh_failed");
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackLegAuthorityBridgeRecoveryLifecycle",
                "receive_resumed",
                "test_post_fallback_receive_resumed");
            InvokePrivateMethod(
                host,
                "RecordPostTunaFallbackReceiverFrontierProofHint",
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = 17,
                    ContiguousCommittedChunkIndex = 24,
                    DurableReceivedHighestChunkIndex = 24,
                    CreditUntilChunkIndexExclusive = 96,
                },
                "received",
                sessionId);

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "runtime_unlock_offer_send_not_observed");
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                12L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "post_tuna_fallback_tuna_activation_offer_send_timeout");
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            hostLane.SetCanListen(true);
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationIfEligible", "runtime_unlock");

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart) + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
                    return tail.Contains("event=session_recovery_contract_retry_authority_observed;", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_offer_queued; reason=runtime_unlock;", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(5));

            var logTail = ReadOperationalLogTail(logStart);
            var positiveLogTail = logTail + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
            Assert.Contains(
                "event=tuna_acceleration_control_send_preferred_bulk_observed_lane_selected; purpose=offer",
                positiveLogTail,
                StringComparison.Ordinal);
            Assert.Contains("observed_lane=control_to_bulk_endpoint", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_retry_authority_observed;", positiveLogTail, StringComparison.Ordinal);
            Assert.True(
                positiveLogTail.Contains("authorized_observed_lane=control_to_bulk_endpoint", StringComparison.Ordinal) ||
                positiveLogTail.Contains("authorized_observed_lane=bulk_queue_fallback", StringComparison.Ordinal),
                positiveLogTail);
            Assert.DoesNotContain("event=tuna_acceleration_control_send_observed_without_recent_peer_proof;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_control_observed_untrusted;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=session_recovery_contract_retry_authority_send_blocked;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_activation_offer_not_observed;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedControlOffer.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousBulkBypassWait;
            NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = previousPeerProofFreshness;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RecoveryStateContract_RuntimeUnlockObservedOfferReplayPrefersBulkEndpoint()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        var previousBulkBypassWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousPeerProofFreshness = NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromMilliseconds(300);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(25);
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(500);
        NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = TimeSpan.FromMinutes(5);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var tunaSidecarPath = Path.Combine(Path.GetTempPath(), "nlink-recovery-authority-replay-bulk.exe");
            var hostClient = new FakeNknClient("host.recovery.authority.replay-bulk.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.recovery.authority.replay-bulk.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            hostClient.ShouldDeliverSendAsync = (_, payload, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };

            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-recovery-authority-replay-bulk-id", hostClient.Address),
                NknTunaAccelerationOptions.CreateRuntimePilot(tunaSidecarPath, NknAccelerationLaneKind.File),
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-recovery-authority-replay-bulk-id", helperClient.Address),
                NknTunaAccelerationOptions.CreatePassiveDialer(tunaSidecarPath, NknAccelerationLaneKind.File),
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_recovery_authority_replay_bulk";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            SetPrivateField(host, "remoteBulkEndpoint", helperClient.ConnectedBulkAddress);
            host.SeedSessionLivenessProofForTests(sessionId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "runtime_unlock_offer_send_not_observed");
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                17L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "tuna_activation_offer_send_timeout");
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            hostLane.SetCanListen(true);
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationIfEligible", "runtime_unlock");

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=tuna_acceleration_offer_queued; reason=runtime_unlock;", StringComparison.Ordinal) &&
                           tail.Contains("observed_lane=control_to_bulk_endpoint", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(5));

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=tuna_acceleration_offer_replay_sent;", StringComparison.Ordinal) &&
                           tail.Contains("observed_lane=control_to_bulk_endpoint", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(5));

            var logTail = ReadOperationalLogTail(logStart);
            var positiveLogTail = logTail + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=session_recovery_contract_retry_authority_observed;", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("authorized_observed_lane=control_to_bulk_endpoint", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_replay_sent;", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("observed_lane=control_to_bulk_endpoint", positiveLogTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_control_priority_offer_replay_observed_trusted;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_control_priority_observed_untrusted; purpose=offer_replay", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("queue_local_only=1", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousBulkBypassWait;
            NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = previousPeerProofFreshness;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RecoveryStateContract_RuntimeUnlockObservedOfferReplayRejectsPriorityWhenBulkEndpointIsBlocked()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        var previousBulkBypassWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousDirectSendWait = NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests;
        var previousPeerProofFreshness = NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests;
        var previousRecoveryRequest = NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromMilliseconds(500);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(120);
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(120);
        NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = TimeSpan.FromMilliseconds(40);
        NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = TimeSpan.FromMilliseconds(-1);
        var blockedReplayBulk = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockReplayBulk = 0;
        NknSignalingTransport? hostTransportForHook = null;
        var recoveryRequestCount = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var tunaSidecarPath = Path.Combine(Path.GetTempPath(), "nlink-recovery-authority-replay-blocked-bulk.exe");
            var hostClient = new FakeNknClient("host.recovery.authority.replay-blocked-bulk.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.recovery.authority.replay-blocked-bulk.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            hostClient.BeforeSendCoreAsync = async (destination, payload, channel, ct) =>
            {
                if (Volatile.Read(ref blockReplayBulk) == 1 &&
                    (string.Equals(destination, helperClient.ConnectedBulkAddress, StringComparison.Ordinal) ||
                     channel == NknBridgeChannel.Bulk) &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    await blockedReplayBulk.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            hostClient.ShouldDeliverSendAsync = (_, payload, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = (transport, reason, sessionId) =>
            {
                if (!ReferenceEquals(transport, hostTransportForHook))
                {
                    return false;
                }

                Assert.Equal("tuna_activation_offer_replay_send_timeout", reason);
                Assert.False(string.IsNullOrWhiteSpace(sessionId));
                Interlocked.Increment(ref recoveryRequestCount);
                return true;
            };
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-recovery-authority-replay-blocked-bulk-id", hostClient.Address),
                NknTunaAccelerationOptions.CreateRuntimePilot(tunaSidecarPath, NknAccelerationLaneKind.File),
                hostLane);
            hostTransportForHook = host;
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-recovery-authority-replay-blocked-bulk-id", helperClient.Address),
                NknTunaAccelerationOptions.CreatePassiveDialer(tunaSidecarPath, NknAccelerationLaneKind.File),
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_recovery_authority_replay_blocked_bulk";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            SetPrivateField(host, "remoteBulkEndpoint", helperClient.ConnectedBulkAddress);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "runtime_unlock_offer_send_not_observed");
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                19L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "tuna_activation_offer_send_timeout");
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            hostLane.SetCanListen(true);
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationIfEligible", "runtime_unlock");

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=tuna_acceleration_offer_queued; reason=runtime_unlock;", StringComparison.Ordinal) &&
                           tail.Contains("observed_lane=control_to_bulk_endpoint", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(5));

            Volatile.Write(ref blockReplayBulk, 1);

            await WaitUntilAsync(
                () => Volatile.Read(ref recoveryRequestCount) > 0,
                TimeSpan.FromSeconds(5));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains(
                "event=tuna_acceleration_control_bulk_observed_send_allowed_by_runtime_unlock_observed_replay; purpose=offer_replay",
                logTail,
                StringComparison.Ordinal);
            Assert.Contains("reason=bounded_observed_offer_replay", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_send_waiting_for_preferred_bulk_observed_lane; purpose=offer_replay", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_send_preferred_bulk_observed_lane_unavailable; purpose=offer_replay; fallback_lane=control_priority;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_priority_observed_untrusted; purpose=offer_replay", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=runtime_unlock_authority_missing_recent_peer_proof", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_control_send_recovery_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("purpose=offer_replay", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=tuna_activation_offer_replay_send_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_replay_rejected;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_offer_replay_sent;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_control_observed_trusted_by_runtime_unlock_authority; purpose=offer_replay", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedReplayBulk.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousBulkBypassWait;
            NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = previousDirectSendWait;
            NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = previousPeerProofFreshness;
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = previousRecoveryRequest;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RecoveryStateContract_RuntimeUnlockObservedOfferReplayBypassesBulkProofBlocker()
    {
        FakeNknClient.ResetNetwork();
        var previousPeerProofFreshness = NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests;
        NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = TimeSpan.FromMilliseconds(-1);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.recovery.authority.replay-proof-blocker.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.recovery.authority.replay-proof-blocker.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-recovery-authority-replay-proof-blocker-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-recovery-authority-replay-proof-blocker-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_recovery_authority_replay_proof_blocker";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                "nonce_replay_proof_blocker",
                payerDecisionId: 51,
                generation: 23,
                observedSend: true,
                observedLane: "control_to_bulk_endpoint",
                answerTimeoutScheduled: true);
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                23L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "regular_v4_unproven_recovery_escalation",
                true);
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");
            InvokePrivateMethod(
                host,
                "MarkRuntimeUnlockRecoveryContractRetryObserved",
                sessionId,
                23L,
                "control_to_bulk_endpoint");

            var logStart = GetOperationalLogLength();
            var replayReason = InvokePrivateMethod(
                host,
                "GetRuntimeUnlockBulkQueueFallbackObservedProofFailureReason",
                sessionId,
                "offer_replay");
            var initialOfferReason = InvokePrivateMethod(
                host,
                "GetRuntimeUnlockBulkQueueFallbackObservedProofFailureReason",
                sessionId,
                "offer");

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Null(replayReason);
            Assert.Equal("runtime_unlock_authority_missing_recent_peer_proof", Assert.IsType<string>(initialOfferReason));
            Assert.Contains(
                "event=tuna_acceleration_control_bulk_observed_send_allowed_by_runtime_unlock_observed_replay; purpose=offer_replay",
                logTail,
                StringComparison.Ordinal);
            Assert.Contains("previous_observed_lane=control_to_bulk_endpoint", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=bounded_observed_offer_replay", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = previousPeerProofFreshness;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RecoveryStateContract_RuntimeUnlockAuthorityRejectsPriorityOnlyProbeWithoutRecentPeerProof()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousBulkBypassWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousPeerProofFreshness = NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests;
        var previousRecoveryRequest = NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests;
        var previousPressureOverride = NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.FromSeconds(20);
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(120);
        NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = TimeSpan.FromMilliseconds(-1);
        NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests = _ => true;
        var blockedBulkOffer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockRuntimeUnlockOfferSends = 0;
        NknSignalingTransport? hostTransportForHook = null;
        var recoveryRequestCount = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var tunaSidecarPath = Path.Combine(Path.GetTempPath(), "nlink-recovery-authority-priority-only.exe");
            var hostClient = new FakeNknClient("host.recovery.authority.priority-only.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.recovery.authority.priority-only.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.ShouldDeliverSendAsync = (_, payload, _) =>
            {
                if (Volatile.Read(ref blockRuntimeUnlockOfferSends) == 1 &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };
            hostClient.BeforeSendCoreAsync = async (destination, payload, channel, ct) =>
            {
                if (Volatile.Read(ref blockRuntimeUnlockOfferSends) == 1 &&
                    (channel == NknBridgeChannel.Bulk ||
                     (channel == NknBridgeChannel.Control &&
                      string.Equals(destination, helperClient.ConnectedBulkAddress, StringComparison.Ordinal))) &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    await blockedBulkOffer.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = (transport, reason, sessionId) =>
            {
                if (!ReferenceEquals(transport, hostTransportForHook))
                {
                    return false;
                }

                Assert.Equal("tuna_activation_offer_send_timeout", reason);
                Assert.False(string.IsNullOrWhiteSpace(sessionId));
                Interlocked.Increment(ref recoveryRequestCount);
                return true;
            };

            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-recovery-authority-priority-only-id", hostClient.Address),
                NknTunaAccelerationOptions.CreateRuntimePilot(tunaSidecarPath, NknAccelerationLaneKind.File),
                hostLane);
            hostTransportForHook = host;
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-recovery-authority-priority-only-id", helperClient.Address),
                NknTunaAccelerationOptions.CreatePassiveDialer(tunaSidecarPath, NknAccelerationLaneKind.File),
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_recovery_authority_priority_only";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            Volatile.Write(ref blockRuntimeUnlockOfferSends, 1);

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "runtime_unlock_offer_send_not_observed");
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                13L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "tuna_activation_offer_send_timeout");
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            hostLane.SetCanListen(true);
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationIfEligible", "runtime_unlock");

            await WaitUntilAsync(
                () => Volatile.Read(ref recoveryRequestCount) > 0,
                TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_activation_offer_not_observed;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_control_priority_sent; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_send_waiting_for_preferred_bulk_observed_lane; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_bulk_queue_fallback_trusted_by_runtime_unlock_authority;", logTail, StringComparison.Ordinal);
            Assert.Contains("lane=bulk_queue_fallback", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_send_preferred_bulk_observed_lane_unavailable; purpose=offer; fallback_lane=control_priority;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_send_observed_without_recent_peer_proof;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_priority_observed_untrusted;", logTail, StringComparison.Ordinal);
            Assert.Contains("observed_lane=control_priority", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_control_send_recovery_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_activation_offer_not_observed;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_rejected; reason=runtime_unlock;", logTail, StringComparison.Ordinal);
            Assert.Contains("recovery_requested=1", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_control_observed_trusted_by_runtime_unlock_authority;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=session_recovery_contract_retry_authority_observed;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_offer_queued; reason=runtime_unlock;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedBulkOffer.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousBulkBypassWait;
            NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = previousPeerProofFreshness;
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = previousRecoveryRequest;
            NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests = previousPressureOverride;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RecoveryStateContract_RuntimeUnlockRetryAuthorityAllowsBulkFallbackObservedSend()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousDirectSendWait = NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests;
        var previousObservedBlocker = NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = TimeSpan.FromMilliseconds(50);
        NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests = _ => "receive_stall_recovery_awaiting_receive_proof";
        var blockedDirectControlOffer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var tunaSidecarPath = Path.Combine(Path.GetTempPath(), "nlink-recovery-authority-runtime-unlock.exe");
            var hostClient = new FakeNknClient("host.recovery.authority.runtime-unlock.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.recovery.authority.runtime-unlock.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.BeforeSendCoreAsync = async (_, payload, channel, ct) =>
            {
                if (channel == NknBridgeChannel.Control &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    await blockedDirectControlOffer.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-recovery-authority-runtime-unlock-id", hostClient.Address),
                NknTunaAccelerationOptions.CreateRuntimePilot(tunaSidecarPath, NknAccelerationLaneKind.File),
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-recovery-authority-runtime-unlock-id", helperClient.Address),
                NknTunaAccelerationOptions.CreatePassiveDialer(tunaSidecarPath, NknAccelerationLaneKind.File),
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_recovery_authority_runtime_unlock";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "runtime_unlock_offer_send_not_observed");
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                7L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "tuna_activation_offer_send_timeout");
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            hostLane.SetCanListen(true);
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationIfEligible", "runtime_unlock");

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(8));
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_control_send_preferred_bulk_observed_lane_selected; purpose=offer",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=session_recovery_contract_retry_authority_granted;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_retry_authority_send_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_sent; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("lane=bulk_queue_fallback", logTail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_retry_authority_observed;", logTail, StringComparison.Ordinal);
            Assert.Contains("authorized_observed_lane=bulk_queue_fallback", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_queued; reason=runtime_unlock;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_control_bulk_queue_fallback_skipped; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=session_liveness_timeout;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedDirectControlOffer.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = previousDirectSendWait;
            NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests = previousObservedBlocker;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RecoveryStateContract_RuntimeUnlockRetryAuthorityPrefersBulkEndpointOverFastControlPriority()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousControlSendWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousObservedBlocker = NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests;
        var previousPeerProofFreshness = NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(500);
        NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests = _ => "receive_stall_recovery_awaiting_receive_proof";
        NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = TimeSpan.FromMinutes(5);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var tunaSidecarPath = Path.Combine(Path.GetTempPath(), "nlink-recovery-authority-runtime-unlock-bulk-prefer.exe");
            var hostClient = new FakeNknClient("host.recovery.authority.runtime-unlock-bulk-prefer.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.recovery.authority.runtime-unlock-bulk-prefer.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.BeforeSendAsync = async (destination, payload, ct) =>
            {
                if (string.Equals(destination, helperClient.ConnectedBulkAddress, StringComparison.Ordinal) &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-recovery-authority-runtime-unlock-bulk-prefer-id", hostClient.Address),
                NknTunaAccelerationOptions.CreateRuntimePilot(tunaSidecarPath, NknAccelerationLaneKind.File),
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-recovery-authority-runtime-unlock-bulk-prefer-id", helperClient.Address),
                NknTunaAccelerationOptions.CreatePassiveDialer(tunaSidecarPath, NknAccelerationLaneKind.File),
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_recovery_authority_runtime_unlock_bulk_prefer";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            SetPrivateField(host, "remoteBulkEndpoint", helperClient.ConnectedBulkAddress);
            host.SeedSessionLivenessProofForTests(sessionId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "runtime_unlock_offer_send_not_observed");
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                7L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "tuna_activation_offer_send_timeout");
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            hostLane.SetCanListen(true);
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationIfEligible", "runtime_unlock");

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(8));
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_control_send_preferred_bulk_observed_lane_selected; purpose=offer",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=session_recovery_contract_retry_authority_granted;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_retry_authority_send_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_priority_sent; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_send_waiting_for_preferred_bulk_observed_lane; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_send_preferred_bulk_observed_lane_selected; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("observed_lane=control_to_bulk_endpoint", logTail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_retry_authority_observed;", logTail, StringComparison.Ordinal);
            Assert.Contains("authorized_observed_lane=control_to_bulk_endpoint", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_queued; reason=runtime_unlock;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=session_liveness_timeout;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousControlSendWait;
            NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests = previousObservedBlocker;
            NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = previousPeerProofFreshness;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RecoveryStateContract_RuntimeUnlockAuthorityObservedSendHoldsRegularV4UntilPeerAnswerOrTimeout()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferPeerResponseTimeout = NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        var previousRecoveryRequest = NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromSeconds(5);
        NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(25);
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.FromSeconds(20);
        NknSignalingTransport? hostTransportForHook = null;
        var recoveryRequestCount = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var tunaSidecarPath = Path.Combine(Path.GetTempPath(), "nlink-recovery-authority-peer-proof.exe");
            var hostClient = new FakeNknClient("host.recovery.authority.peer-proof.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.recovery.authority.peer-proof.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.ShouldDeliverSendAsync = (_, payload, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = (transport, reason, sessionId) =>
            {
                if (!ReferenceEquals(transport, hostTransportForHook))
                {
                    return false;
                }

                Assert.Equal("tuna_activation_offer_peer_response_timeout", reason);
                Assert.False(string.IsNullOrWhiteSpace(sessionId));
                Interlocked.Increment(ref recoveryRequestCount);
                return true;
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-recovery-authority-peer-proof-id", hostClient.Address),
                NknTunaAccelerationOptions.CreateRuntimePilot(tunaSidecarPath, NknAccelerationLaneKind.File),
                hostLane);
            hostTransportForHook = host;
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-recovery-authority-peer-proof-id", helperClient.Address),
                NknTunaAccelerationOptions.CreatePassiveDialer(tunaSidecarPath, NknAccelerationLaneKind.File),
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_recovery_authority_peer_proof";
            var dataSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "runtime_unlock_offer_send_not_observed");
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                7L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "tuna_activation_offer_send_timeout");
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            hostLane.SetCanListen(true);
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationIfEligible", "runtime_unlock");

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_offer_queued; reason=runtime_unlock;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            var contractProvider = Assert.IsAssignableFrom<ISessionRecoveryStateContract>(host);
            Assert.True(contractProvider.TryGetActiveSessionRecoveryContract(sessionId, out var snapshot));
            Assert.Equal(SessionRecoveryContractKind.RuntimeUnlockActivation, snapshot.Kind);
            Assert.Equal(SessionRecoveryContractState.RetryDispatched, snapshot.State);
            Assert.True(snapshot.RetryDispatched);
            Assert.False(snapshot.RetryObserved);
            Assert.True(snapshot.ObservedSendPending);
            Assert.False(snapshot.RetryAuthorityPending);

            var queuedTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=session_recovery_contract_retry_authority_observed;", queuedTail, StringComparison.Ordinal);
            Assert.Contains("observed_send_pending=1", queuedTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=session_recovery_contract_retry_observed;", queuedTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=session_recovery_contract_completed;", queuedTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_pause_deferred;", queuedTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;", queuedTail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                availabilityEvents,
                e => !e.IsAvailable &&
                     e.Reason == "tuna_activation_negotiating");

            await WaitUntilAsync(
                () => Volatile.Read(ref recoveryRequestCount) > 0 &&
                      ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_armed;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            var timeoutTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_offer_peer_response_timeout;", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_control_send_recovery_requested;", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("purpose=offer_peer_response", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("trigger=peer_response_timeout_without_peer_response", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("reason=tuna_activation_offer_peer_response_timeout", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("bridge_reason=runtime_unlock_retry_authority_offer_blocked", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("accepted=1", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_activation_offer_not_observed;", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("interruption_reason=runtime_unlock_offer_peer_response_timeout", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("retry_after_recovery_armed=1", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_listener_rearm_skipped;", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("reason=runtime_unlock_offer_peer_response_timeout", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("listener_ready_reuse=1", timeoutTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=session_recovery_contract_listener_rearm_required;", timeoutTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_offer_answer_timeout;", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_pause_deferred;", timeoutTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;", timeoutTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_tuna_activation_negotiation_regular_nkn_resumed;", timeoutTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_activation_failed_regular_v4_resumed;", timeoutTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=session_recovery_contract_completed;", timeoutTail, StringComparison.Ordinal);
            Assert.True(dataSession.IsAvailable);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests = previousOfferPeerResponseTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = previousRecoveryRequest;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RecoveryStateContract_RuntimeUnlockPeerResponseTimeoutRetriesDuringUnprovenRegularV4ReceiveRecovery()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferPeerResponseTimeout = NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        var previousRecoveryRequest = NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests;
        var previousReceiveRecoveryBlocker = NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromSeconds(5);
        NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(25);
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.FromSeconds(20);
        var receiveRecoveryBlockerEnabled = 0;
        NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests =
            _ => Volatile.Read(ref receiveRecoveryBlockerEnabled) == 0
                ? null
                : "receive_stall_recovery_awaiting_receive_proof";
        NknSignalingTransport? hostTransportForHook = null;
        var recoveryRequestCount = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var tunaSidecarPath = Path.Combine(Path.GetTempPath(), "nlink-recovery-authority-peer-proof-defer.exe");
            var hostClient = new FakeNknClient("host.recovery.authority.peer-proof.defer.aaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.recovery.authority.peer-proof.defer.bbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.ShouldDeliverSendAsync = (_, payload, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = (transport, _, _) =>
            {
                if (ReferenceEquals(transport, hostTransportForHook))
                {
                    Interlocked.Increment(ref recoveryRequestCount);
                }

                return true;
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-recovery-authority-peer-proof-defer-id", hostClient.Address),
                NknTunaAccelerationOptions.CreateRuntimePilot(tunaSidecarPath, NknAccelerationLaneKind.File),
                hostLane);
            hostTransportForHook = host;
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-recovery-authority-peer-proof-defer-id", helperClient.Address),
                NknTunaAccelerationOptions.CreatePassiveDialer(tunaSidecarPath, NknAccelerationLaneKind.File),
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_recovery_authority_peer_proof_defer";
            var dataSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "runtime_unlock_offer_send_not_observed");
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                7L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "tuna_activation_offer_send_timeout");
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            hostLane.SetCanListen(true);
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationIfEligible", "runtime_unlock");

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_offer_queued; reason=runtime_unlock;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            Volatile.Write(ref receiveRecoveryBlockerEnabled, 1);

            await WaitUntilAsync(
                () => Volatile.Read(ref recoveryRequestCount) > 0 &&
                      ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_armed;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            var timeoutTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_offer_peer_response_timeout;", timeoutTail, StringComparison.Ordinal);
            Assert.Contains(
                "event=tuna_acceleration_runtime_unlock_regular_v4_receive_recovery_requires_fresh_peer_response_retry;",
                timeoutTail,
                StringComparison.Ordinal);
            Assert.Contains("blocker_reason=receive_stall_recovery_awaiting_receive_proof", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_control_send_recovery_requested;", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("purpose=offer_peer_response", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("trigger=peer_response_timeout_without_peer_response", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("interruption_reason=runtime_unlock_offer_peer_response_timeout", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("recovery_requested=1", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("retry_after_recovery_armed=1", timeoutTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_armed;", timeoutTail, StringComparison.Ordinal);
            Assert.DoesNotContain("authority_failure_reason=regular_v4_receive_recovery_unproven", timeoutTail, StringComparison.Ordinal);
            var timeoutIndex = timeoutTail.IndexOf(
                "event=tuna_acceleration_runtime_unlock_offer_peer_response_timeout;",
                StringComparison.Ordinal);
            Assert.True(timeoutIndex >= 0);
            var afterTimeoutTail = timeoutTail[timeoutIndex..];
            Assert.DoesNotContain("event=session_recovery_contract_failed;", afterTimeoutTail, StringComparison.Ordinal);
            Assert.True(Volatile.Read(ref recoveryRequestCount) > 0);
            Assert.True(dataSession.IsAvailable);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests = previousOfferPeerResponseTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = previousRecoveryRequest;
            NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests = previousReceiveRecoveryBlocker;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RecoveryStateContract_RuntimeUnlockAuthoritySendBlockedArmsCutThroughUnderRegularV4Recovery()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.recovery.authority.send-blocked-cutthrough.aaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.recovery.authority.send-blocked-cutthrough.bbbbbbbbbbbbbb");
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-recovery-authority-send-blocked-cutthrough-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                accelerationLane: null);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-recovery-authority-send-blocked-cutthrough-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                accelerationLane: null);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_recovery_authority_send_blocked_cutthrough";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                13L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "tuna_activation_offer_send_timeout");
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractAuthoritySendStarted", "offer");
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractAuthorityBlocked", "runtime_unlock_retry_authority_offer_blocked");

            var shouldPromote = (bool)Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldPromoteRuntimeUnlockAuthoritySendBlockToCutThrough",
                sessionId));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=session_recovery_contract_retry_authority_send_blocked;", logTail, StringComparison.Ordinal);
            Assert.Contains("authority_failure_reason=runtime_unlock_retry_authority_offer_blocked", logTail, StringComparison.Ordinal);
            Assert.True(shouldPromote);
            Assert.DoesNotContain("authority_failure_reason=retry_exhausted_runtime_unlock_offer_send_not_observed", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RecoveryStateContract_RuntimeUnlockCutThroughFailureResumesRegularV4AndFailsContract()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferPeerResponseTimeout = NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        var previousRecoveryRequest = NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromSeconds(5);
        NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests = TimeSpan.FromMilliseconds(120);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(25);
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.FromSeconds(20);
        NknSignalingTransport? hostTransportForHook = null;
        var recoveryRequestCount = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var tunaSidecarPath = Path.Combine(Path.GetTempPath(), "nlink-recovery-cutthrough-fail.exe");
            var hostClient = new FakeNknClient("host.recovery.cutthrough.fail.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.recovery.cutthrough.fail.bbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.ShouldDeliverSendAsync = (_, payload, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = (transport, _, _) =>
            {
                if (ReferenceEquals(transport, hostTransportForHook))
                {
                    Interlocked.Increment(ref recoveryRequestCount);
                }

                return true;
            };

            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-recovery-cutthrough-fail-id", hostClient.Address),
                NknTunaAccelerationOptions.CreateRuntimePilot(tunaSidecarPath, NknAccelerationLaneKind.File),
                hostLane);
            hostTransportForHook = host;
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-recovery-cutthrough-fail-id", helperClient.Address),
                NknTunaAccelerationOptions.CreatePassiveDialer(tunaSidecarPath, NknAccelerationLaneKind.File),
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_recovery_cutthrough_fail";
            var dataSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "runtime_unlock_offer_send_not_observed");
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                7L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "tuna_activation_offer_send_timeout");
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            hostLane.SetCanListen(true);
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationIfEligible", "runtime_unlock");

            await WaitUntilAsync(
                () => Volatile.Read(ref recoveryRequestCount) > 0 &&
                      ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_armed;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "cutthrough_recovery_settled");

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=runtime_unlock_cutthrough_started;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=runtime_unlock_cutthrough_failed;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=runtime_unlock_cutthrough_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=runtime_unlock_cutthrough_offer_sent;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=runtime_unlock_cutthrough_failed;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=runtime_unlock_peer_response_not_received", logTail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_failed;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_resumed;", logTail, StringComparison.Ordinal);
            Assert.Contains("trigger=runtime_unlock_offer_peer_response_timeout", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=session_recovery_contract_completed;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
            Assert.True(dataSession.IsAvailable);
            Assert.Contains(
                availabilityEvents,
                e => !e.IsAvailable &&
                     e.Reason == "tuna_activation_negotiating");
            Assert.Equal(1, Volatile.Read(ref recoveryRequestCount));
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests = previousOfferPeerResponseTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = previousRecoveryRequest;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RecoveryStateContract_RuntimeUnlockPredispatchDeferralWithoutContractArmsReceiveProofRetry()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousReceiveRecoveryBlocker = NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests =
            _ => "receive_stall_recovery_in_progress";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var tunaSidecarPath = Path.Combine(Path.GetTempPath(), "nlink-recovery-predispatch-retry.exe");
            var hostClient = new FakeNknClient("host.recovery.predispatch.retry.aaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.recovery.predispatch.retry.bbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-recovery-predispatch-retry-id", hostClient.Address),
                NknTunaAccelerationOptions.CreateRuntimePilot(tunaSidecarPath, NknAccelerationLaneKind.File),
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-recovery-predispatch-retry-id", helperClient.Address),
                NknTunaAccelerationOptions.CreatePassiveDialer(tunaSidecarPath, NknAccelerationLaneKind.File),
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_recovery_predispatch_retry";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            var recoveryRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.RegularNknV4FastToken,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 0,
                AuthorityReason = "regular_v4_startup_local_only_no_ack",
            };
            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessStarted",
                recoveryRequest,
                sessionId,
                transferId,
                "session_liveness_timeout_pending");

            var logStart = GetOperationalLogLength();
            var method = typeof(NknSignalingTransport).GetMethod(
                "TryDeferRuntimeUnlockOfferDispatchForRegularV4ReceiveRecovery",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var methodArgs = new object?[] { sessionId, "runtime_unlock", 4L, null, 0L };
            var deferred = Assert.IsType<bool>(method!.Invoke(host, methodArgs));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.True(deferred);
            Assert.Equal("receive_stall_recovery_in_progress", Assert.IsType<string>(methodArgs[3]));
            Assert.Contains("blocker_reason=receive_stall_recovery_in_progress", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_scheduled=0", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_after_receive_proof_armed=1", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_armed;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled;",
                logTail,
                StringComparison.Ordinal);
            Assert.True(host.TryGetActiveSessionRecoveryContract(sessionId, out var snapshot));
            Assert.Equal(SessionRecoveryContractState.RecoverySettled, snapshot.State);
            Assert.True(snapshot.RetryRequired);
            Assert.False(snapshot.RetryDispatched);

            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessReceiveProofReceived",
                sessionId,
                transferId,
                "file_transfer_data_frame",
                "control");

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_regular_v4_recovery_liveness_receive_proof_observed;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled;", StringComparison.Ordinal) &&
                           tail.Contains("event=session_recovery_contract_retry_queued;", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));

            var proofTail = ReadOperationalLogTail(logStart);
            Assert.Contains("trigger=regular_v4_receive_proof_observed", proofTail, StringComparison.Ordinal);
            Assert.True(host.TryGetActiveSessionRecoveryContract(sessionId, out var scheduledSnapshot));
            Assert.True(
                scheduledSnapshot.RetryRequired ||
                scheduledSnapshot.RetryDispatched ||
                scheduledSnapshot.State is SessionRecoveryContractState.RetryQueued or SessionRecoveryContractState.RetryDispatching);
            Assert.True(scheduledSnapshot.RetryAuthorityGranted);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests = previousReceiveRecoveryBlocker;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RecoveryStateContract_RuntimeUnlockPredispatchFinalNoContractProbeBypassesRegularV4ReceiveRecovery()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousReceiveRecoveryBlocker = NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests =
            _ => "receive_stall_recovery_in_progress";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var tunaSidecarPath = Path.Combine(Path.GetTempPath(), "nlink-recovery-predispatch-final-probe.exe");
            var hostClient = new FakeNknClient("host.recovery.predispatch.finalprobe.aaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.recovery.predispatch.finalprobe.bbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-recovery-predispatch-final-probe-id", hostClient.Address),
                NknTunaAccelerationOptions.CreateRuntimePilot(tunaSidecarPath, NknAccelerationLaneKind.File),
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-recovery-predispatch-final-probe-id", helperClient.Address),
                NknTunaAccelerationOptions.CreatePassiveDialer(tunaSidecarPath, NknAccelerationLaneKind.File),
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_recovery_predispatch_final_probe";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            typeof(NknSignalingTransport)
                .GetField("accelerationNegotiationRetryAttempts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(host, 7);

            var logStart = GetOperationalLogLength();
            var method = typeof(NknSignalingTransport).GetMethod(
                "TryDeferRuntimeUnlockOfferDispatchForRegularV4ReceiveRecovery",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var methodArgs = new object?[] { sessionId, "runtime_unlock", 11L, null, 0L };
            var deferred = Assert.IsType<bool>(method!.Invoke(host, methodArgs));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.False(deferred);
            Assert.Equal("receive_stall_recovery_in_progress", Assert.IsType<string>(methodArgs[3]));
            Assert.Contains("event=tuna_acceleration_runtime_unlock_dispatch_regular_v4_receive_recovery_first_offer_probe_allowed;", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_attempts=", logTail, StringComparison.Ordinal);
            Assert.Contains("max_attempts=8", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=bounded_final_first_offer_observed_send_probe", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_runtime_unlock_dispatch_deferred_for_regular_v4_receive_recovery;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_regular_v4_receive_recovery_pending", logTail, StringComparison.Ordinal);
            Assert.False(host.TryGetActiveSessionRecoveryContract(sessionId, out _));
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests = previousReceiveRecoveryBlocker;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RecoveryStateContract_RuntimeUnlockRetryAuthorityDefersPredispatchRegularV4ReceiveRecoveryUntilProbeCandidate()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousRecoveryRequest = NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests;
        var previousReceiveRecoveryBlocker = NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.FromSeconds(20);
        var receiveRecoveryBlocker = "receive_stall_recovery_awaiting_receive_proof";
        NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests =
            _ => receiveRecoveryBlocker;
        NknSignalingTransport? hostTransportForHook = null;
        var recoveryRequestCount = 0;
        var offerSendCount = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var options = NknTransportOptions.Load();
            var tunaSidecarPath = Path.Combine(Path.GetTempPath(), "nlink-recovery-authority-predispatch-defer.exe");
            var hostClient = new FakeNknClient("host.recovery.authority.predispatch.defer.aaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.recovery.authority.predispatch.defer.bbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.ShouldDeliverSendAsync = (_, payload, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    Interlocked.Increment(ref offerSendCount);
                }

                return Task.FromResult(true);
            };
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = (transport, _, _) =>
            {
                if (ReferenceEquals(transport, hostTransportForHook))
                {
                    Interlocked.Increment(ref recoveryRequestCount);
                }

                return true;
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-recovery-authority-predispatch-defer-id", hostClient.Address),
                NknTunaAccelerationOptions.CreateRuntimePilot(tunaSidecarPath, NknAccelerationLaneKind.File),
                hostLane);
            hostTransportForHook = host;
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-recovery-authority-predispatch-defer-id", helperClient.Address),
                NknTunaAccelerationOptions.CreatePassiveDialer(tunaSidecarPath, NknAccelerationLaneKind.File),
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_recovery_authority_predispatch_defer";
            var dataSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            var recoveryRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.RegularNknV4FastToken,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 0,
                AuthorityReason = "regular_v4_startup_local_only_no_ack",
            };
            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessStarted",
                recoveryRequest,
                sessionId,
                transferId,
                "session_liveness_timeout_pending");

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "runtime_unlock_offer_send_not_observed");
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                9L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "tuna_activation_offer_send_timeout");
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");
            var method = typeof(NknSignalingTransport).GetMethod(
                "TryDeferRuntimeUnlockOfferDispatchForRegularV4ReceiveRecovery",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var methodArgs = new object?[] { sessionId, "runtime_unlock", 9L, null, 0L };
            var deferred = Assert.IsType<bool>(method!.Invoke(host, methodArgs));

            var tail = ReadOperationalLogTail(logStart);
            Assert.True(deferred);
            Assert.Equal("receive_stall_recovery_awaiting_receive_proof", Assert.IsType<string>(methodArgs[3]));
            Assert.Contains("event=session_recovery_contract_recovery_settled;", tail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_retry_authority_granted;", tail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_retry_dispatched;", tail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_dispatch_deferred_for_regular_v4_receive_recovery;", tail, StringComparison.Ordinal);
            Assert.Contains("blocker_reason=receive_stall_recovery_awaiting_receive_proof", tail, StringComparison.Ordinal);
            Assert.Contains("retry_after_receive_proof_armed=1", tail, StringComparison.Ordinal);
            Assert.Contains("authority_failure_reason=regular_v4_receive_recovery_pending", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_runtime_unlock_dispatch_regular_v4_receive_recovery_authority_bypassed;", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_offer_queued; reason=runtime_unlock;", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=session_recovery_contract_retry_authority_send_started;", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_regular_v4_receive_recovery_pending", tail, StringComparison.Ordinal);
            Assert.Equal(0, Volatile.Read(ref offerSendCount));
            Assert.Equal(0, Volatile.Read(ref recoveryRequestCount));
            Assert.False(host.RuntimeUnlockOfferStateForTests.HasOutboundOffer);
            Assert.True(host.TryGetActiveSessionRecoveryContract(sessionId, out var snapshot));
            Assert.Equal(SessionRecoveryContractState.RecoverySettled, snapshot.State);
            Assert.False(snapshot.RetryDispatched);
            Assert.False(snapshot.RetryAuthorityPending);
            Assert.False(snapshot.RetryAuthorityGranted);
            Assert.False(snapshot.ObservedSendPending);
            Assert.Equal("regular_v4_receive_recovery_pending", snapshot.AuthorityFailureReason);
            Assert.True(dataSession.IsAvailable);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = previousRecoveryRequest;
            NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests = previousReceiveRecoveryBlocker;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void NknTunaAccelerationOptions_DefaultsDisabledWithFileAndScreenLanes()
    {
        using var enabled = new EnvironmentOverride("NLINK_NKN_TUNA_ENABLED", null);
        using var lanes = new EnvironmentOverride("NLINK_NKN_TUNA_LANES", null);
        using var sidecar = new EnvironmentOverride("NLINK_NKN_TUNA_SIDECAR_EXE", null);
        using var listener = new EnvironmentOverride("NLINK_NKN_TUNA_LISTENER_ENDPOINT", null);

        var options = NknTunaAccelerationOptions.Load();

        Assert.False(options.Enabled);
        Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, options.Lanes);
        Assert.Null(options.SidecarExePath);
        Assert.Null(options.ListenerEndpoint);
        Assert.False(options.CanOfferListener);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferRouteStatus_TunaConfiguredEligibleInactive_SelectsRegularV4()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var tunaOptions = NknTunaAccelerationOptions.CreateRuntimePilot(
                Path.Combine(Path.GetTempPath(), "nlink-phase4-tuna-route-status.exe"),
                NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen);
            var hostClient = new FakeNknClient("host.tuna.route.configured.inactive.address");
            var helperClient = new FakeNknClient("helper.tuna.route.configured.inactive.address");
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-route-configured-inactive-id", hostClient.Address),
                tunaOptions,
                new FakeNknAccelerationLane(isAvailable: true));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-route-configured-inactive-id", helperClient.Address),
                tunaOptions,
                new FakeNknAccelerationLane(isAvailable: true));

            _ = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            AssertNknRoute(host, FileTransferRoute.RegularNknV4Fast, FileTransferProtocol.ProtocolVersionV4);
            Assert.False(host.IsFileTunaActiveForRouteSelection);
            Assert.False(host.IsPostTunaFileFallbackActiveForRouteSelection);
            Assert.False(((ITransportAccelerationStatus)host).ShouldUseFileTransferV6ForAcceleration);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferRouteStatus_FailedTunaActivationWithoutFallback_SelectsRegularV4()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.route.failed.activation.address");
            var helperClient = new FakeNknClient("helper.tuna.route.failed.activation.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-route-failed-activation-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-route-failed-activation-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            _ = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            var logStart = GetOperationalLogLength();
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("phase4_failed_activation", cts.Token);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_offer_preflight_rejected; reason=listener_unavailable", StringComparison.Ordinal),
                TimeSpan.FromSeconds(6));

            Assert.False(host.IsAccelerationAvailableForTests);
            Assert.False(helper.IsAccelerationAvailableForTests);
            AssertNknRoute(host, FileTransferRoute.RegularNknV4Fast, FileTransferProtocol.ProtocolVersionV4);
            Assert.False(host.IsFileTunaActiveForRouteSelection);
            Assert.False(host.IsPostTunaFileFallbackActiveForRouteSelection);
            Assert.False(((ITransportAccelerationStatus)host).ShouldUseFileTransferV6ForAcceleration);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferRouteStatus_ActiveFileTunaIsV4AndPostTunaFallbackIsV6()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.route.active.fallback.address");
            var helperClient = new FakeNknClient("helper.tuna.route.active.fallback.address");
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            var helperLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-route-active-fallback-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-route-active-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            Assert.True(host.IsFileTunaActiveForRouteSelection);
            Assert.False(host.IsPostTunaFileFallbackActiveForRouteSelection);
            AssertNknRoute(host, FileTransferRoute.FileTunaV4, FileTransferProtocol.ProtocolVersionV4);
            Assert.False(((ITransportAccelerationStatus)host).ShouldUseFileTransferV6ForAcceleration);

            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_phase4_tuna_route_status_fallback",
                cts.Token);
            await ((ITransportAccelerationControl)host).StopAccelerationAsync("header_switch_off", cts.Token);
            await WaitUntilAsync(
                () => host.IsPostTunaFileFallbackActiveForRouteSelection,
                TimeSpan.FromSeconds(3));

            Assert.False(host.IsFileTunaActiveForRouteSelection);
            Assert.True(host.IsPostTunaFileFallbackActiveForRouteSelection);
            AssertNknRoute(host, FileTransferRoute.PostTunaFallbackV6, FileTransferProtocol.ProtocolVersionV6);
            Assert.True(((ITransportAccelerationStatus)host).ShouldUseFileTransferV6ForAcceleration);

            host.ObserveFileTransferRouteCompleted(
                new FileTransferRouteCompletedNotification(
                    sessionId,
                    "transfer_phase4_post_tuna_fallback_v6_completed",
                    FileTransferRouteResolver.PostTunaFallbackV6Token,
                    FileTransferProtocol.ProtocolVersionV6));

            Assert.False(host.IsFileTunaActiveForRouteSelection);
            Assert.False(host.IsPostTunaFileFallbackActiveForRouteSelection);
            AssertNknRoute(host, FileTransferRoute.RegularNknV4Fast, FileTransferProtocol.ProtocolVersionV4);
            Assert.False(((ITransportAccelerationStatus)host).ShouldUseFileTransferV6ForAcceleration);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferRouteStatus_PostTunaFallbackReactivationSupersedesFallbackForNextTransfer()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.route.fallback.superseded.address");
            var helperClient = new FakeNknClient("helper.tuna.route.fallback.superseded.address");
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            var helperLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-route-fallback-superseded-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-route-fallback-superseded-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_phase4_tuna_route_status_superseded",
                cts.Token);
            await ((ITransportAccelerationControl)host).StopAccelerationAsync("header_switch_off", cts.Token);
            await WaitUntilAsync(
                () => host.IsPostTunaFileFallbackActiveForRouteSelection,
                TimeSpan.FromSeconds(3));

            var logStart = GetOperationalLogLength();
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);

            Assert.True(host.IsFileTunaActiveForRouteSelection);
            Assert.False(host.IsPostTunaFileFallbackActiveForRouteSelection);
            AssertNknRoute(host, FileTransferRoute.FileTunaV4, FileTransferProtocol.ProtocolVersionV4);
            Assert.False(((ITransportAccelerationStatus)host).ShouldUseFileTransferV6ForAcceleration);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_post_tuna_fallback_v6_route_superseded;", logTail, StringComparison.Ordinal);
            Assert.Contains("next_file_route=file_tuna_v4", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_summary;", logTail, StringComparison.Ordinal);
            Assert.Contains("completed_reason=tuna_activation_started", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RuntimeUnlock_WhenTunaAlreadyHealthyDuringPostTunaFallback_ReplaysFileTunaHandoff()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.runtime-unlock.active-fallback.address");
            var helperClient = new FakeNknClient("helper.runtime-unlock.active-fallback.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-runtime-unlock-active-fallback-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-runtime-unlock-active-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var transferId = "transfer_runtime_unlock_active_fallback_handoff";
            var dataSession = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                transferId,
                cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            SetPrivateField(host, "accelerationSessionId", sessionId);
            SetPrivateField(host, "accelerationNegotiatedLanes", NknAccelerationLaneKind.File);
            hostLane.MarkListenerAvailableForTests();
            await ((ITransportAccelerationControl)host).StopAccelerationAsync("header_switch_off", cts.Token);
            await WaitUntilAsync(
                () => host.IsPostTunaFileFallbackActiveForRouteSelection,
                TimeSpan.FromSeconds(3));

            SetPrivateField(host, "accelerationSessionId", sessionId);
            SetPrivateField(host, "accelerationNegotiatedLanes", NknAccelerationLaneKind.File);
            hostLane.MarkListenerAvailableForTests();

            var logStart = GetOperationalLogLength();
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=tuna_acceleration_runtime_unlock_file_fallback_handoff_requested;", StringComparison.Ordinal) &&
                           tail.Contains("event=filetransfer_data_session_handoff_broadcast;", StringComparison.Ordinal) &&
                           tail.Contains("handoff_kind=normal_to_tuna_activation", StringComparison.Ordinal) &&
                           tail.Contains("target_transport=tuna", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));

            Assert.True(host.IsFileTunaActiveForRouteSelection);
            Assert.False(host.IsPostTunaFileFallbackActiveForRouteSelection);
            AssertNknRoute(host, FileTransferRoute.FileTunaV4, FileTransferProtocol.ProtocolVersionV4);
            Assert.Contains(
                availabilityEvents,
                e => e.IsAvailable &&
                     e.RequiresResumeRequest &&
                     e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                     e.TargetTransport == FileTransferTransportKind.Tuna);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_post_tuna_fallback_v6_route_superseded;", logTail, StringComparison.Ordinal);
            Assert.Contains("next_file_route=file_tuna_v4", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_filetransfer_handoff_requested;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_offer_queued; reason=runtime_unlock", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferRouteStatus_PendingPostTunaFallbackSurvivesUserStopCleanupUntilConsumed()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.route.pending-fallback-cleanup.address");
            var helperClient = new FakeNknClient("helper.tuna.route.pending-fallback-cleanup.address");
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            var helperLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-route-pending-fallback-cleanup-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-route-pending-fallback-cleanup-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_phase4_tuna_route_pending_fallback_cleanup",
                cts.Token);
            await ((ITransportAccelerationControl)host).StopAccelerationAsync("header_switch_off", cts.Token);
            await WaitUntilAsync(
                () => host.IsPostTunaFileFallbackActiveForRouteSelection,
                TimeSpan.FromSeconds(3));

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "CompleteTunaFallbackProof", "remote_header_switch_off");

            Assert.False(host.IsFileTunaActiveForRouteSelection);
            Assert.True(host.IsPostTunaFileFallbackActiveForRouteSelection);
            AssertNknRoute(host, FileTransferRoute.PostTunaFallbackV6, FileTransferProtocol.ProtocolVersionV6);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_fallback_summary_deferred;", logTail, StringComparison.Ordinal);
            Assert.Contains("deferred_reason=remote_header_switch_off", logTail, StringComparison.Ordinal);
            Assert.Contains("pending_file_route=1", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_fallback_summary;", logTail, StringComparison.Ordinal);

            host.ObserveFileTransferRouteCompleted(
                new FileTransferRouteCompletedNotification(
                    sessionId,
                    "transfer_phase4_tuna_route_pending_fallback_completed",
                    FileTransferRouteResolver.PostTunaFallbackV6Token,
                    FileTransferProtocol.ProtocolVersionV6));

            Assert.False(host.IsFileTunaActiveForRouteSelection);
            Assert.False(host.IsPostTunaFileFallbackActiveForRouteSelection);
            AssertNknRoute(host, FileTransferRoute.RegularNknV4Fast, FileTransferProtocol.ProtocolVersionV4);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferRouteCompletedNotification_ClearsTransportBusyStateForNextOffer()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.route-completed-busy.address");
            var helperClient = new FakeNknClient("helper.tuna.route-completed-busy.address");
            var hostIdentity = new NknIdentity("host-route-completed-busy-id", hostClient.Address);
            var helperIdentity = new NknIdentity("helper-route-completed-busy-id", helperClient.Address);
            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);

            var firstOfferReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondOfferReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += delegate (object? _, FileTransferOfferReceivedEventArgs e)
            {
                if (string.Equals(e.Message.TransferId, "transfer_route_completed_busy_1", StringComparison.Ordinal))
                {
                    firstOfferReceived.TrySetResult(e.Message);
                }
                else if (string.Equals(e.Message.TransferId, "transfer_route_completed_busy_2", StringComparison.Ordinal))
                {
                    secondOfferReceived.TrySetResult(e.Message);
                }
            };

            await helper.SendFileTransferOfferAsync(
                new FileTransferOfferV2
                {
                    SessionId = sessionId,
                    TransferId = "transfer_route_completed_busy_1",
                    FileName = "first.bin",
                    FileSizeBytes = 1024L,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                    FileTransferRoute = FileTransferRouteResolver.FileTunaV4Token,
                },
                cts.Token);
            await firstOfferReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            helper.ObserveFileTransferRouteCompleted(
                new FileTransferRouteCompletedNotification(
                    sessionId,
                    "transfer_route_completed_busy_1",
                    FileTransferRouteResolver.FileTunaV4Token,
                    FileTransferProtocol.ProtocolVersionV4));
            host.ObserveFileTransferRouteCompleted(
                new FileTransferRouteCompletedNotification(
                    sessionId,
                    "transfer_route_completed_busy_1",
                    FileTransferRouteResolver.FileTunaV4Token,
                    FileTransferProtocol.ProtocolVersionV4));

            await helper.SendFileTransferOfferAsync(
                new FileTransferOfferV2
                {
                    SessionId = sessionId,
                    TransferId = "transfer_route_completed_busy_2",
                    FileName = "second.bin",
                    FileSizeBytes = 1024L,
                    PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                    FileTransferRoute = FileTransferRouteResolver.FileTunaV4Token,
                },
                cts.Token);

            var secondOffer = await secondOfferReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            Assert.Equal("transfer_route_completed_busy_2", secondOffer.TransferId);
            var logTail = ReadOperationalLogText();
            Assert.DoesNotContain("concurrent_transfer_busy", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void FileTransferRouteStatus_DiagnosticRegularNknV6_IsDisabledByReleaseDefault()
    {
        using var diagnostic = new EnvironmentOverride("NLINK_FILETRANSFER_DIAGNOSTIC_REGULAR_NKN_V6", null);
        var options = NknTransportOptions.Load();
        var client = new FakeNknClient("host.tuna.route.diagnostic.default.address");
        using var transport = new NknSignalingTransport(
            client,
            options,
            new NknIdentity("host-tuna-route-diagnostic-default-id", client.Address),
            NknTunaAccelerationOptions.Disabled,
            new FakeNknAccelerationLane(isAvailable: true));

        Assert.False(transport.IsDiagnosticRegularNknV6RouteEnabled);
        AssertNknRoute(transport, FileTransferRoute.RegularNknV4Fast, FileTransferProtocol.ProtocolVersionV4);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferRouteStatus_ActiveFileTuna_SelectsFileTunaV4ByDefault()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.route.diagnostic.file.v4.address");
            var helperClient = new FakeNknClient("helper.tuna.route.diagnostic.file.v4.address");
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            var helperLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-route-diagnostic-file-v4-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-route-diagnostic-file-v4-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);

            Assert.True(((ITransportAccelerationStatus)host).IsTransportAccelerationActive);
            Assert.True(host.IsFileTunaActiveForRouteSelection);
            Assert.False(host.IsPostTunaFileFallbackActiveForRouteSelection);
            Assert.False(((ITransportAccelerationStatus)host).ShouldUseFileTransferV6ForAcceleration);
            AssertNknRoute(host, FileTransferRoute.FileTunaV4, FileTransferProtocol.ProtocolVersionV4);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TransportAccelerationOffer_HelperFallbackPayerDelay_IsShort()
    {
        var field = typeof(NknSignalingTransport).GetField(
            "HelperPaidOfferHelpeePriorityDelay",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.IsType<TimeSpan>(field.GetValue(null)));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaFallbackProofLogging_IsLowNoiseAfterInitialProof()
    {
        var windowField = typeof(NknSignalingTransport).GetField(
            "TunaFallbackProofLogWindow",
            BindingFlags.Static | BindingFlags.NonPublic);
        var everyFramesField = typeof(NknSignalingTransport).GetField(
            "TunaFallbackProofLogEveryFrames",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(windowField);
        Assert.NotNull(everyFramesField);
        Assert.Equal(TimeSpan.FromMinutes(1), Assert.IsType<TimeSpan>(windowField.GetValue(null)));
        Assert.Equal(5000L, Assert.IsType<long>(everyFramesField.GetValue(null)));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaSidecarClient_SendsIdleWarmupBeforeFirstDataAndAfterQuietPeriod()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = ((IPEndPoint)listener.LocalEndpoint).ToString();
            using var client = new NknTunaSidecarClient(NknAccelerationLaneKind.Screen, queueCapacity: 16);
            var connectTask = client.ConnectAsync(endpoint, TimeSpan.FromSeconds(5), cts.Token);
            using var server = await listener.AcceptTcpClientAsync(cts.Token);
            await using var serverStream = server.GetStream();
            var statusPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                @event = "status",
                address = "nlink-tuna-sidecar.test-listener-address",
                appProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion,
                frameProtocolVersion = NknTunaSidecarFrameProtocol.ProtocolVersion,
                sidecarVersion = NknTunaSidecarCompatibility.ExpectedSidecarVersion,
                lanes = new[] { "screen" },
            });
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Status,
                NknTunaSidecarLane.Control,
                sequence: 0,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                statusPayload,
                cts.Token);
            await connectTask;

            Assert.True(await client.TrySendAsync(NknBridgeChannel.Media, [1, 2, 3], cts.Token));
            var firstWarmup = await NknTunaSidecarFrameProtocol.ReadFrameAsync(serverStream, cts.Token);
            var firstData = await NknTunaSidecarFrameProtocol.ReadFrameAsync(serverStream, cts.Token);

            Assert.Equal(NknTunaSidecarFrameType.Ping, firstWarmup.Type);
            Assert.Equal(NknTunaSidecarLane.Control, firstWarmup.Lane);
            Assert.Empty(firstWarmup.Payload);
            Assert.Equal(NknTunaSidecarFrameType.Data, firstData.Type);
            Assert.Equal(NknTunaSidecarLane.Media, firstData.Lane);
            Assert.Equal((ulong)1, firstData.Sequence);

            await Task.Delay(700, cts.Token);
            Assert.True(await client.TrySendAsync(NknBridgeChannel.Media, [4, 5, 6], cts.Token));
            var secondWarmup = await NknTunaSidecarFrameProtocol.ReadFrameAsync(serverStream, cts.Token);
            var secondData = await NknTunaSidecarFrameProtocol.ReadFrameAsync(serverStream, cts.Token);

            Assert.Equal(NknTunaSidecarFrameType.Ping, secondWarmup.Type);
            Assert.Equal(NknTunaSidecarFrameType.Data, secondData.Type);
            Assert.Equal((ulong)2, secondData.Sequence);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaSidecarClient_SequenceGapIsDiagnosticAndDoesNotDisableLane()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = ((IPEndPoint)listener.LocalEndpoint).ToString();
            using var client = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
            var received = new ConcurrentQueue<NknIncomingMessage>();
            client.MessageReceived += (_, message) => received.Enqueue(message);
            var connectTask = client.ConnectAsync(endpoint, TimeSpan.FromSeconds(5), cts.Token);
            using var server = await listener.AcceptTcpClientAsync(cts.Token);
            await using var serverStream = server.GetStream();
            var statusPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                @event = "status",
                address = "nlink-tuna-sidecar.test-listener-address",
                appProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion,
                frameProtocolVersion = NknTunaSidecarFrameProtocol.ProtocolVersion,
                sidecarVersion = NknTunaSidecarCompatibility.ExpectedSidecarVersion,
                lanes = new[] { "file" },
            });
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Status,
                NknTunaSidecarLane.Control,
                sequence: 0,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                statusPayload,
                cts.Token);
            await connectTask;

            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Data,
                NknTunaSidecarLane.Bulk,
                sequence: 1,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                new byte[] { 1 },
                cts.Token);
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Data,
                NknTunaSidecarLane.Bulk,
                sequence: 3,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                new byte[] { 3 },
                cts.Token);
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Data,
                NknTunaSidecarLane.Bulk,
                sequence: 2,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                new byte[] { 2 },
                cts.Token);
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Data,
                NknTunaSidecarLane.Bulk,
                sequence: 4,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                new byte[] { 4 },
                cts.Token);

            await WaitUntilAsync(() => received.Count == 4, TimeSpan.FromSeconds(2));
            var diagnostics = client.GetDiagnosticsSnapshot();
            Assert.True(client.IsAvailable);
            Assert.Equal(4, diagnostics.BulkFramesReceived);
            Assert.Equal(1, diagnostics.SequenceGap);
            Assert.Equal(1, diagnostics.SequenceReordered);
            Assert.Equal(string.Empty, diagnostics.LastUnavailableReason);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaSidecarClient_InterleavedLaneSequencesAreNotFalseGaps()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = ((IPEndPoint)listener.LocalEndpoint).ToString();
            using var client = new NknTunaSidecarClient(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, queueCapacity: 16);
            var received = new ConcurrentQueue<NknIncomingMessage>();
            client.MessageReceived += (_, message) => received.Enqueue(message);
            var connectTask = client.ConnectAsync(endpoint, TimeSpan.FromSeconds(5), cts.Token);
            using var server = await listener.AcceptTcpClientAsync(cts.Token);
            await using var serverStream = server.GetStream();
            var statusPayload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                @event = "status",
                address = "nlink-tuna-sidecar.test-listener-address",
                appProtocolVersion = NknTunaSidecarCompatibility.AppProtocolVersion,
                frameProtocolVersion = NknTunaSidecarFrameProtocol.ProtocolVersion,
                sidecarVersion = NknTunaSidecarCompatibility.ExpectedSidecarVersion,
                lanes = new[] { "file", "screen" },
            });
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Status,
                NknTunaSidecarLane.Control,
                sequence: 0,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                statusPayload,
                cts.Token);
            await connectTask;

            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Data,
                NknTunaSidecarLane.Bulk,
                sequence: 1,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                new byte[] { 1 },
                cts.Token);
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Data,
                NknTunaSidecarLane.Media,
                sequence: 2,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                new byte[] { 2 },
                cts.Token);
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Data,
                NknTunaSidecarLane.Bulk,
                sequence: 3,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                new byte[] { 3 },
                cts.Token);

            await WaitUntilAsync(() => received.Count == 3, TimeSpan.FromSeconds(2));
            var diagnostics = client.GetDiagnosticsSnapshot();
            Assert.True(client.IsAvailable);
            Assert.Equal(2, diagnostics.BulkFramesReceived);
            Assert.Equal(1, diagnostics.MediaFramesReceived);
            Assert.Equal(0, diagnostics.SequenceGap);
            Assert.Equal(0, diagnostics.SequenceReordered);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("missing_app_protocol", null, 1, "expected", "sidecar_app_protocol_mismatch")]
    [InlineData("wrong_frame_protocol", 1, 99, "expected", "sidecar_frame_protocol_mismatch")]
    [InlineData("stale_sidecar_version", 1, 1, "0.6.9", "sidecar_version_mismatch")]
    [Trait("Category", "Smoke")]
    public async Task TunaSidecarClient_RejectsProtocolOrVersionMismatch(
        string scenario,
        int? appProtocolVersion,
        int? frameProtocolVersion,
        string sidecarVersion,
        string expectedReason)
    {
        _ = scenario;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var endpoint = ((IPEndPoint)listener.LocalEndpoint).ToString();
            using var client = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
            var connectTask = client.ConnectAsync(endpoint, TimeSpan.FromSeconds(5), cts.Token);
            using var server = await listener.AcceptTcpClientAsync(cts.Token);
            await using var serverStream = server.GetStream();
            var status = new Dictionary<string, object?>
            {
                ["event"] = "status",
                ["address"] = "nlink-tuna-sidecar.test-listener-address",
                ["frameProtocolVersion"] = frameProtocolVersion,
                ["sidecarVersion"] = string.Equals(sidecarVersion, "expected", StringComparison.Ordinal)
                    ? NknTunaSidecarCompatibility.ExpectedSidecarVersion
                    : sidecarVersion,
                ["lanes"] = new[] { "file" },
            };
            if (appProtocolVersion.HasValue)
            {
                status["appProtocolVersion"] = appProtocolVersion.Value;
            }

            var statusPayload = JsonSerializer.SerializeToUtf8Bytes(status);
            await NknTunaSidecarFrameProtocol.WriteFrameAsync(
                serverStream,
                NknTunaSidecarFrameType.Status,
                NknTunaSidecarLane.Control,
                sequence: 0,
                timestampUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                statusPayload,
                cts.Token);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => connectTask);
            Assert.Contains(expectedReason, ex.Message, StringComparison.Ordinal);
            Assert.False(client.IsAvailable);
            Assert.Equal(expectedReason, client.GetDiagnosticsSnapshot().LastUnavailableReason);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaSidecarClient_CanceledSendTokenDoesNotCountAsSidecarRejection()
    {
        using var client = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
        typeof(NknTunaSidecarClient)
            .GetField("available", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, 1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.TrySendAsync(NknBridgeChannel.Bulk, [1, 2, 3], cts.Token));

        var diagnostics = client.GetDiagnosticsSnapshot();
        Assert.True(client.IsAvailable);
        Assert.Equal(0, diagnostics.SendRejected);
        Assert.Equal(0, diagnostics.QueueOverflow);
        Assert.True(string.IsNullOrWhiteSpace(diagnostics.LastUnavailableReason));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaSidecarClient_MediaQueuePressureFallsBackOneFrameWithoutDisablingTuna()
    {
        var previousTimeout = NknTunaSidecarClient.MediaQueueWriteTimeoutOverrideForTests;
        NknTunaSidecarClient.MediaQueueWriteTimeoutOverrideForTests = 50;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new NknTunaSidecarClient(NknAccelerationLaneKind.Screen, queueCapacity: 16);
            typeof(NknTunaSidecarClient)
                .GetField("available", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(client, 1);

            for (var i = 0; i < 16; i++)
            {
                Assert.True(await client.TrySendAsync(NknBridgeChannel.Media, [1, 2, 3], cts.Token));
            }

            Assert.False(await client.TrySendAsync(NknBridgeChannel.Media, [4, 5, 6], cts.Token));
            var diagnostics = client.GetDiagnosticsSnapshot();
            Assert.Equal(16, diagnostics.MediaFramesAccepted);
            Assert.Equal(0, diagnostics.MediaFramesWritten);
            Assert.Equal(1, diagnostics.QueueOverflow);
            Assert.True(client.IsAvailable);
            Assert.True(string.IsNullOrWhiteSpace(diagnostics.LastUnavailableReason));
        }
        finally
        {
            NknTunaSidecarClient.MediaQueueWriteTimeoutOverrideForTests = previousTimeout;
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaSidecarClient_BulkQueuePressureMarksSidecarUnavailableForNknFallback()
    {
        var previousTimeout = NknTunaSidecarClient.BulkQueueWriteTimeoutOverrideForTests;
        NknTunaSidecarClient.BulkQueueWriteTimeoutOverrideForTests = 50;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
            typeof(NknTunaSidecarClient)
                .GetField("available", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(client, 1);

            for (var i = 0; i < 16; i++)
            {
                Assert.True(await client.TrySendAsync(NknBridgeChannel.Bulk, [1, 2, 3], cts.Token));
            }

            Assert.False(await client.TrySendAsync(NknBridgeChannel.Bulk, [4, 5, 6], cts.Token));
            var diagnostics = client.GetDiagnosticsSnapshot();
            Assert.Equal(16, diagnostics.BulkFramesAccepted);
            Assert.Equal(0, diagnostics.BulkFramesWritten);
            Assert.Equal(1, diagnostics.QueueOverflow);
            Assert.False(client.IsAvailable);
            Assert.Equal("queue_overflow", diagnostics.LastUnavailableReason);
        }
        finally
        {
            NknTunaSidecarClient.BulkQueueWriteTimeoutOverrideForTests = previousTimeout;
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TunaAccelerationLane_RetainsLastDiagnosticsAfterStop()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var lane = new NknTunaAccelerationLane(
            NknTunaAccelerationOptions.CreateRuntimePilot(
                Path.Combine(Environment.CurrentDirectory, "nlink-tuna-sidecar.exe"),
                NknAccelerationLaneKind.File,
                canOfferListener: false));
        using var client = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
        typeof(NknTunaSidecarClient)
            .GetField("available", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, 1);
        typeof(NknTunaAccelerationLane)
            .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(lane, client);

        Assert.True(await lane.TrySendAsync(NknBridgeChannel.Bulk, [1, 2, 3], cts.Token));
        client.MarkUnavailableFromSidecarEvent("sidecar_tuna_stream_eof");
        await lane.StopAsync("test_stop", cts.Token);

        var diagnostics = lane.GetDiagnosticsSnapshot();
        Assert.Equal(1, diagnostics.BulkFramesAccepted);
        Assert.Equal("sidecar_tuna_stream_eof", diagnostics.LastUnavailableReason);
        Assert.Equal("sidecar_tuna_stream_eof", diagnostics.TerminalSidecarReason);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaAccelerationLane_SuppressesLocalListenerWithoutEmittingStaleUnavailableEvent()
    {
        var supervisor = new RecordingTunaListenerSidecarSupervisor();
        using var lane = new NknTunaAccelerationLane(
            NknTunaAccelerationOptions.CreateRuntimePilot(
                Path.Combine(Environment.CurrentDirectory, "nlink-tuna-sidecar.exe"),
                NknAccelerationLaneKind.File,
                canOfferListener: true),
            supervisor);
        using var listenerClient = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
        typeof(NknTunaSidecarClient)
            .GetField("available", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(listenerClient, 1);
        typeof(NknTunaAccelerationLane)
            .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(lane, listenerClient);
        var roleField = typeof(NknTunaAccelerationLane)
            .GetField("clientRole", BindingFlags.Instance | BindingFlags.NonPublic)!;
        roleField.SetValue(lane, Enum.Parse(roleField.FieldType, "Listener"));

        var unavailableEvents = 0;
        lane.StateChanged += (_, e) =>
        {
            if (!e.IsAvailable)
            {
                unavailableEvents++;
            }
        };

        typeof(NknTunaAccelerationLane)
            .GetMethod("StopCurrentListenerBeforeDialer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(lane, null);

        Assert.False(listenerClient.IsAvailable);
        Assert.Null(typeof(NknTunaAccelerationLane)
            .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(lane));
        Assert.Equal(0, unavailableEvents);
        Assert.Equal(["payer_switch_to_dialer"], supervisor.StopReasons);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaAccelerationLane_IgnoresUnavailableEventFromReplacedSidecarClient()
    {
        using var lane = new NknTunaAccelerationLane(
            NknTunaAccelerationOptions.CreateRuntimePilot(
                Path.Combine(Environment.CurrentDirectory, "nlink-tuna-sidecar.exe"),
                NknAccelerationLaneKind.File,
                canOfferListener: true));
        using var staleClient = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
        using var currentClient = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
        typeof(NknTunaSidecarClient)
            .GetField("available", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(currentClient, 1);
        typeof(NknTunaAccelerationLane)
            .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(lane, currentClient);

        var unavailableEvents = 0;
        lane.StateChanged += (_, e) =>
        {
            if (!e.IsAvailable)
            {
                unavailableEvents++;
            }
        };

        var handler = typeof(NknTunaAccelerationLane)
            .GetMethod("OnClientStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        handler.Invoke(
            lane,
            new object?[] { staleClient, new AccelerationStateChangedEventArgs(false, "remote_closed") });

        Assert.Equal(0, unavailableEvents);
        Assert.Same(
            currentClient,
            typeof(NknTunaAccelerationLane)
                .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(lane));

        handler.Invoke(
            lane,
            new object?[] { currentClient, new AccelerationStateChangedEventArgs(false, "remote_closed") });

        Assert.Equal(1, unavailableEvents);
        Assert.Null(typeof(NknTunaAccelerationLane)
            .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(lane));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TunaAccelerationLane_ForwardsReadyOnlyForCurrentSidecarClient()
    {
        using var lane = new NknTunaAccelerationLane(
            NknTunaAccelerationOptions.CreateRuntimePilot(
                Path.Combine(Environment.CurrentDirectory, "nlink-tuna-sidecar.exe"),
                NknAccelerationLaneKind.File,
                canOfferListener: true));
        using var staleClient = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
        using var currentClient = new NknTunaSidecarClient(NknAccelerationLaneKind.File, queueCapacity: 16);
        typeof(NknTunaSidecarClient)
            .GetField("available", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(staleClient, 1);
        typeof(NknTunaSidecarClient)
            .GetField("available", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(currentClient, 1);
        typeof(NknTunaAccelerationLane)
            .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(lane, currentClient);

        var readyEvents = 0;
        lane.StateChanged += (_, e) =>
        {
            if (e.IsAvailable && string.Equals(e.Reason, "ready", StringComparison.Ordinal))
            {
                readyEvents++;
            }
        };

        var forwardReady = typeof(NknTunaAccelerationLane)
            .GetMethod("ForwardCurrentClientReadyIfAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!;
        forwardReady.Invoke(lane, new object?[] { staleClient, "ready" });
        Assert.Equal(0, readyEvents);

        forwardReady.Invoke(lane, new object?[] { currentClient, "ready" });
        Assert.Equal(1, readyEvents);
    }

    [Theory]
    [InlineData("session_security_state_not_eligible", false)]
    [InlineData("reset_session_tracking", false)]
    [InlineData("dispose", false)]
    [InlineData("sidecar_disposed", false)]
    [InlineData("sidecar_read_failed", true)]
    [InlineData("sidecar_tuna_stream_eof", true)]
    [InlineData("sidecar_byte_cap_reached", true)]
    [InlineData("sidecar_remote_byte_cap_reached", true)]
    [InlineData("remote_read_failed", true)]
    [InlineData("header_switch_off", false)]
    [InlineData("remote_header_switch_off", false)]
    [InlineData("soak_switch_off", false)]
    [InlineData("runtime_disabled", false)]
    [InlineData("user_stopped_tuna", false)]
    [Trait("Category", "Smoke")]
    public void TunaFallbackProof_ResetReasonClassifierDistinguishesFailureFromTeardown(
        string reason,
        bool expected)
        => Assert.Equal(expected, NknSignalingTransport.ShouldStartTunaFallbackProofForResetReason(reason));

    [Theory]
    [InlineData("tuna_stream_eof", "sidecar_tuna_stream_eof", true)]
    [InlineData("sidecar_tuna_stream_eof", "sidecar_tuna_stream_eof", true)]
    [InlineData("remote_closed", "sidecar_remote_closed", true)]
    [InlineData("sidecar_remote_closed", "sidecar_remote_closed", true)]
    [Trait("Category", "Smoke")]
    public void TunaFallbackProof_SidecarResetReasonNormalizerDoesNotDoublePrefix(
        string reason,
        string expectedReason,
        bool startsFallback)
    {
        var normalized = NknSignalingTransport.NormalizeAccelerationSidecarResetReason(reason);

        Assert.Equal(expectedReason, normalized);
        Assert.Equal(startsFallback, NknSignalingTransport.ShouldStartTunaFallbackProofForResetReason(normalized));
    }

    [Theory]
    [InlineData("byte_cap_reached", true)]
    [InlineData("sidecar_remote_closed", true)]
    [InlineData("sidecar_read_failed", true)]
    [InlineData("remote_sidecar_remote_closed", true)]
    [InlineData("header_switch_off", false)]
    [InlineData("remote_header_switch_off", false)]
    [InlineData("user_stopped_tuna", false)]
    [InlineData("reset_session_tracking", false)]
    [InlineData("dispose", false)]
    [Trait("Category", "Smoke")]
    public void TunaFallbackProof_ImmediateFileProbeClassifierIncludesSidecarDrop(
        string reason,
        bool expected)
        => Assert.Equal(expected, NknSignalingTransport.ShouldStartImmediateFileTransferFallbackProbe(reason));

    [Theory]
    [InlineData("session_security_state_not_eligible", true)]
    [InlineData("reset_session_tracking", true)]
    [InlineData("dispose", true)]
    [InlineData("sidecar_disposed", false)]
    [InlineData("sidecar_read_failed", false)]
    [InlineData("remote_read_failed", false)]
    [InlineData("header_switch_off", false)]
    [Trait("Category", "Smoke")]
    public void TunaFallbackProof_CompletionReasonClassifierPreservesActiveProofDuringSidecarCleanup(
        string reason,
        bool expected)
        => Assert.Equal(expected, NknSignalingTransport.ShouldCompleteTunaFallbackProofForResetReason(reason));

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataFrame_RoutesBulkThroughAccelerationOnlyAfterAccepted()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.address");
            var helperClient = new FakeNknClient("helper.tuna.file.address");
            var fakeLane = new FakeNknAccelerationLane();
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            var rawNknDataFrames = new ConcurrentQueue<NknIncomingMessage>();
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.FileTransferDataFrame)
                {
                    rawNknDataFrames.Enqueue(e);
                }
            };

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_tuna_file_accel", cts.Token);
            var preNegotiationLogStart = GetOperationalLogLength();
            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = "transfer_tuna_file_accel",
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);

            await WaitUntilAsync(() => rawNknDataFrames.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Empty(fakeLane.Sent);
            Assert.DoesNotContain("event=tuna_fallback_started;", ReadOperationalLogTail(preNegotiationLogStart), StringComparison.Ordinal);

            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = "transfer_tuna_file_accel",
                    StartChunkIndex = 1,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);

            await WaitUntilAsync(() => fakeLane.Sent.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Equal(NknBridgeChannel.Bulk, fakeLane.Sent.Single().Lane);
            Assert.True(EnvelopeCodec.TryDeserialize(fakeLane.Sent.Single().Payload, out var acceleratedEnvelope));
            Assert.Equal(MsgType.FileTransferDataFrame, acceleratedEnvelope.Type);
            Assert.Single(rawNknDataFrames);
            Assert.Equal(NknBridgeChannel.Bulk, rawNknDataFrames.Single().Channel);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataSession_TunaAcceptedEmitsNormalToTunaActivationHandoff()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.handoff.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.handoff.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-activation-handoff-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-handoff-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_tuna_activation_handoff", cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            var logStart = GetOperationalLogLength();
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    e.TargetTransport == FileTransferTransportKind.Tuna),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_activation_filetransfer_handoff_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=test_accept", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAcceleration_RuntimeUnlockHandoffsRegularNknFileTransferWhenTunaReady()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.pause.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.pause.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-pause-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-pause-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var logStart = GetOperationalLogLength();
            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var dataSession = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_pause",
                cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            var hostAccelerationStatus = Assert.IsAssignableFrom<ITransportAccelerationStatus>(host);
            Assert.False(hostAccelerationStatus.IsTransportAccelerationActive);
            Assert.False(hostAccelerationStatus.ShouldUseFileTransferV6ForAcceleration);

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    e.TargetTransport == FileTransferTransportKind.Tuna),
                TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => host.IsFileTunaActiveForRouteSelection,
                TimeSpan.FromSeconds(6));
            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(6));
            Assert.False(hostAccelerationStatus.ShouldUseFileTransferV6ForAcceleration);

            Assert.True(host.IsAccelerationAvailableForTests);
            Assert.True(helper.IsAccelerationAvailableForTests);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_activation_filetransfer_handoff_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("handoff_kind=normal_to_tuna_activation", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_tuna_activation_suppressed_for_route;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("file_tuna_v6", logTail, StringComparison.Ordinal);
            if (logTail.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;", StringComparison.Ordinal))
            {
                Assert.Contains("reason=activation_negotiation_pending", logTail, StringComparison.Ordinal);
                Assert.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_resumed;", logTail, StringComparison.Ordinal);
            }
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RegularNknV4Route_TunaActivationPausesAndHandoffsActiveFileTransfer()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.activation.regular-v4-no-pause.address");
            var helperClient = new FakeNknClient("helper.tuna.activation.regular-v4-no-pause.address");
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-activation-regular-v4-no-pause-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-activation-regular-v4-no-pause-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_regular_v4_no_pause";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            var dataSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            var logStart = GetOperationalLogLength();

            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                    ForceRegularNknBulk = true,
                },
                cts.Token);

            InvokePrivateMethod(
                host,
                "PauseFileTransferDataSessionsForTunaActivationNegotiation",
                "activation_negotiation_pending",
                sessionId,
                "runtime_unlock");
            await Task.Delay(200, cts.Token);

            Assert.False(dataSession.IsAvailable);
            Assert.Contains(
                availabilityEvents,
                e => !e.IsAvailable &&
                     e.Reason == "tuna_activation_negotiating");

            InvokePrivateMethod(
                host,
                "SetFileTransferDataSessionsAvailability",
                false,
                "receive_stall_recovery",
                true,
                FileTransferTransportHandoffKind.RegularNknRecovery,
                FileTransferTransportKind.RegularNkn);
            InvokePrivateMethod(
                host,
                "SetFileTransferDataSessionsAvailability",
                true,
                "transport_recovered",
                false,
                FileTransferTransportHandoffKind.None,
                FileTransferTransportKind.RegularNkn);
            await Task.Delay(200, cts.Token);

            Assert.False(dataSession.IsAvailable);
            Assert.DoesNotContain(
                availabilityEvents,
                e => e.Reason == "receive_stall_recovery" ||
                     e.Reason == "transport_recovered");

            InvokePrivateMethod(
                host,
                "RequestFileTransferTunaActivationHandoff",
                sessionId,
                NknAccelerationLaneKind.File,
                "test_accept");

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    e.TargetTransport == FileTransferTransportKind.Tuna),
                TimeSpan.FromSeconds(2));

            Assert.Contains(
                availabilityEvents,
                e => e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                     e.TargetTransport == FileTransferTransportKind.Tuna);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_tuna_activation_negotiation_availability_suppressed;", logTail, StringComparison.Ordinal);
            Assert.Contains("incoming_reason=receive_stall_recovery", logTail, StringComparison.Ordinal);
            Assert.Contains("incoming_reason=transport_recovered", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_resumed;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_data_session_handoff_invoking;", logTail, StringComparison.Ordinal);
            Assert.Contains("handoff_kind=normal_to_tuna_activation", logTail, StringComparison.Ordinal);
            Assert.Contains("target_transport=tuna", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_tuna_activation_suppressed_for_route;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_data_session_send_canceled_for_tuna_activation_pause;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RegularNknV4Route_RecoveredTunaActivationPauseLabelsCanceledDataSend()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.activation.recovered-cancel.address");
            var helperClient = new FakeNknClient("helper.tuna.activation.recovered-cancel.address");
            var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCanceledSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blockNextBulk = 1;
            hostClient.BeforeSendCoreAsync = async (_, _, channel, ct) =>
            {
                if (channel == NknBridgeChannel.Bulk &&
                    Interlocked.Exchange(ref blockNextBulk, 0) == 1)
                {
                    sendStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        await releaseCanceledSend.Task.WaitAsync(cts.Token).ConfigureAwait(false);
                        throw;
                    }
                }
            };

            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-activation-recovered-cancel-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-activation-recovered-cancel-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_recovered_cancel";
            var dataSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var logStart = GetOperationalLogLength();
            var sendTask = dataSession.SendAsync(
                new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                    ForceRegularNknBulk = true,
                },
                cts.Token);

            await sendStarted.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            InvokePrivateMethod(
                host,
                "PauseFileTransferDataSessionsForTunaActivationNegotiation",
                "activation_negotiation_pending",
                sessionId,
                "runtime_unlock");
            InvokePrivateMethod(
                host,
                "ResumeFileTransferDataSessionsAfterTunaActivationNegotiation",
                "tuna_activation_failed_regular_v4_resumed",
                sessionId,
                "listener_unavailable");
            releaseCanceledSend.TrySetResult();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await sendTask.ConfigureAwait(false));
            Assert.Contains("tuna_activation_negotiating", ex.Message, StringComparison.OrdinalIgnoreCase);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_data_session_send_canceled_for_tuna_activation_pause;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=tuna_activation_negotiating", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task PostTunaFallbackV6Route_TunaReactivationKeepsFallbackAvailableUntilHandoff()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.activation.post-fallback-no-pause.address");
            var helperClient = new FakeNknClient("helper.tuna.activation.post-fallback-no-pause.address");
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-activation-post-fallback-no-pause-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-activation-post-fallback-no-pause-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_post_fallback_no_pause";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            var dataSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(
                host,
                "PauseFileTransferDataSessionsForTunaActivationNegotiation",
                "peer_offer_dialer_starting",
                sessionId,
                "runtime_unlock");

            await Task.Delay(200, cts.Token);

            Assert.True(dataSession.IsAvailable);
            Assert.DoesNotContain(
                availabilityEvents,
                e => !e.IsAvailable &&
                     !e.RequiresResumeRequest &&
                     e.Reason == "tuna_activation_negotiating");

            InvokePrivateMethod(
                host,
                "RequestFileTransferTunaActivationHandoff",
                sessionId,
                NknAccelerationLaneKind.File,
                "tuna_activation_answer_ack");
            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    e.TargetTransport == FileTransferTransportKind.Tuna),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains(
                "event=filetransfer_tuna_activation_negotiation_post_tuna_fallback_pause_suppressed;",
                logTail,
                StringComparison.Ordinal);
            Assert.Contains("suppress_reason=active_post_tuna_fallback_route", logTail, StringComparison.Ordinal);
            Assert.Contains("trigger=runtime_unlock", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_data_session_handoff_invoking;", logTail, StringComparison.Ordinal);
            Assert.Contains("handoff_kind=normal_to_tuna_activation", logTail, StringComparison.Ordinal);
            Assert.Contains("target_transport=tuna", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswer_NonceMismatchResumesRegularV4ActivationPause()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.activation.nonce-resume.address");
            var helperClient = new FakeNknClient("helper.tuna.activation.nonce-resume.address");
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-activation-nonce-resume-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-activation-nonce-resume-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var dataSession = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_nonce_resume",
                cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            InvokePrivateMethod(
                host,
                "PauseFileTransferDataSessionsForTunaActivationNegotiation",
                "activation_negotiation_pending",
                sessionId,
                "runtime_unlock");
            SetPrivateField(host, "outboundAccelerationOfferNonce", "cc11223344556677889900aabbccddee");
            SetPrivateField(host, "outboundAccelerationOfferTrigger", "runtime_unlock");
            var answer = CreateAnswerPayload(
                sessionId,
                "dd11223344556677889900aabbccddee",
                accepted: true,
                supportedLanes: new[] { "file" });
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationAnswer,
                answer,
                "transport_acceleration_answer",
                answer.Nonce,
                sequence: 1);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(host, "HandleTransportAccelerationAnswer", helperClient.Address, envelope);

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.Reason == "tuna_activation_failed_regular_v4_resumed" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.None),
                TimeSpan.FromSeconds(2));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_activation_failed_regular_v4_resumed;", logTail, StringComparison.Ordinal);
            Assert.Contains("failure_reason=nonce_mismatch", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOfferAnswerTimeout_ResumesRegularV4ActivationPause()
    {
        FakeNknClient.ResetNetwork();
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromMilliseconds(75);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.activation.timeout-resume.address");
            var helperClient = new FakeNknClient("helper.tuna.activation.timeout-resume.address");
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-activation-timeout-resume-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-activation-timeout-resume-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var dataSession = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_timeout_resume",
                cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            InvokePrivateMethod(
                host,
                "PauseFileTransferDataSessionsForTunaActivationNegotiation",
                "activation_negotiation_pending",
                sessionId,
                "runtime_unlock");
            SetPrivateField(host, "outboundAccelerationOfferNonce", "ee11223344556677889900aabbccddee");
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(host, "ScheduleAccelerationOfferAnswerTimeout", "ee11223344556677889900aabbccddee");

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.Reason == "tuna_activation_failed_regular_v4_resumed"),
                TimeSpan.FromSeconds(2));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_offer_answer_timeout;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_failed_regular_v4_resumed;", logTail, StringComparison.Ordinal);
            Assert.Contains("failure_reason=offer_answer_timeout", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ActivationSidecarTerminalBeforeNegotiatedReadiness_ResumesRegularV4()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.activation.sidecar-terminal.address");
            var helperClient = new FakeNknClient("helper.tuna.activation.sidecar-terminal.address");
            var hostLane = new FakeNknAccelerationLane(isAvailable: false);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-activation-sidecar-terminal-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-activation-sidecar-terminal-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var dataSession = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_sidecar_terminal",
                cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            InvokePrivateMethod(
                host,
                "PauseFileTransferDataSessionsForTunaActivationNegotiation",
                "activation_negotiation_pending",
                sessionId,
                "runtime_unlock");
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(host, "ResetAccelerationNegotiation", "sidecar_local_ipc_eof");

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.Reason == "tuna_activation_failed_regular_v4_resumed"),
                TimeSpan.FromSeconds(2));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_activation_failed_regular_v4_resumed;", logTail, StringComparison.Ordinal);
            Assert.Contains("failure_reason=sidecar_local_ipc_eof", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_fallback_filetransfer_rebind_requested;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAcceleration_RemoteWillListenIntentDoesNotAddRegularNknPause()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.intent-no-pause.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.intent-no-pause.address");
            var helperLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-intent-no-pause-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-intent-no-pause-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var dataSession = await helper.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_intent_no_pause",
                cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            availabilityEvents.Clear();
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(
                helper,
                "YieldLocalPaidListenerToRemoteHelpee",
                "payer_intent_will_listen",
                4L);

            await Task.Delay(200, cts.Token);

            Assert.DoesNotContain(
                availabilityEvents,
                e => !e.IsAvailable &&
                     e.Reason == "tuna_activation_negotiating");
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_payer_yield;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAcceleration_RuntimeUnlockResumesRegularNknWhenListenerUnavailableBeforeHandoff()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.pause-drain.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.pause-drain.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);

            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-pause-drain-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-pause-drain-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var logStart = GetOperationalLogLength();
            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_offer_preflight_rejected; reason=listener_unavailable", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));

            const string transferId = "transfer_tuna_activation_pause_drain";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            var dataSession = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                transferId,
                cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                    ForceRegularNknBulk = true,
                },
                cts.Token);

            await Task.Delay(150, cts.Token);
            Assert.DoesNotContain(
                availabilityEvents,
                e => !e.IsAvailable &&
                     e.Reason == "tuna_activation_negotiating");

            var unavailableRuntimeUnlockLogStart = GetOperationalLogLength();
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);
            var unavailableRuntimeUnlockTail = string.Empty;
            await WaitUntilAsync(
                () =>
                {
                    unavailableRuntimeUnlockTail = ReadOperationalLogTail(unavailableRuntimeUnlockLogStart);
                    return unavailableRuntimeUnlockTail.Contains(
                        "event=tuna_acceleration_offer_preflight_rejected; reason=listener_unavailable; trigger=runtime_unlock",
                        StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));

            Assert.True(dataSession.IsAvailable);
            Assert.DoesNotContain(
                "event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;",
                unavailableRuntimeUnlockTail,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "event=filetransfer_tuna_activation_negotiation_regular_nkn_pause_retained;",
                unavailableRuntimeUnlockTail,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                availabilityEvents,
                e => !e.IsAvailable &&
                     e.Reason == "tuna_activation_negotiating");
            availabilityEvents.Clear();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);
            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    e.TargetTransport == FileTransferTransportKind.Tuna),
                TimeSpan.FromSeconds(5));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_offer_preflight_rejected; reason=listener_unavailable", unavailableRuntimeUnlockTail, StringComparison.Ordinal);
            if (logTail.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;", StringComparison.Ordinal))
            {
                Assert.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_resumed;", logTail, StringComparison.Ordinal);
            }

            Assert.Contains("event=tuna_activation_filetransfer_handoff_requested;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_activation_failed_regular_v4_resumed;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_UsesBulkBypassWhenControlSendIsBlocked()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        var blockedControlOffer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.bulk-bypass.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.bulk-bypass.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var blockedOfferCount = 0;
            hostClient.BeforeSendAsync = async (destination, payload, ct) =>
            {
                if (string.Equals(destination, helperClient.ConnectedAddress, StringComparison.Ordinal) &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    Interlocked.Increment(ref blockedOfferCount);
                    await blockedControlOffer.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-bulk-bypass-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-bulk-bypass-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var logStart = GetOperationalLogLength();
            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_bulk_bypass",
                cts.Token);

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(6));

            Assert.True(Volatile.Read(ref blockedOfferCount) > 0);
            Assert.True(hostLane.EnsureListenerCalls > 0);
            Assert.True(helperLane.StartDialerCalls > 0);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_started; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_sent; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_queued;", logTail, StringComparison.Ordinal);
            Assert.Contains("observed_lane=control_to_bulk_endpoint", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedControlOffer.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockControlPlaneDuplicatePreservesAnswerPeerProof()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.control-ack-proof.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.control-ack-proof.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-control-ack-proof-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-control-ack-proof-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_control_ack_proof",
                cts.Token);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(6));

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart)
                    .Contains("event=runtime_unlock_control_plane_delivery;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=runtime_unlock_control_plane_delivery;", logTail, StringComparison.Ordinal);
            Assert.Contains("peer_visible_any=1", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_lifecycle_ack_sent; message_type=transport_acceleration_offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=runtime_unlock_transaction_peer_received;", logTail, StringComparison.Ordinal);
            Assert.True(
                logTail.Contains("reason=transport_acceleration_answer", StringComparison.Ordinal) ||
                logTail.Contains("reason=transport_acceleration_offer_received", StringComparison.Ordinal),
                "Expected peer-visible runtime unlock proof from either the answer path or the peer-received offer path.");
            Assert.Contains("event=tuna_acceleration_offer_queued;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=runtime_unlock_control_plane_peer_receipt_missing;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_FallsBackToBulkQueueWhenDirectControlCopiesHang()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousDirectSendWait = NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = TimeSpan.FromMilliseconds(50);
        var blockedDirectControlOffer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.bulk-queue-fallback.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.bulk-queue-fallback.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var blockedDirectControlCount = 0;
            hostClient.BeforeSendCoreAsync = async (_, payload, channel, ct) =>
            {
                if (channel == NknBridgeChannel.Control &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    Interlocked.Increment(ref blockedDirectControlCount);
                    await blockedDirectControlOffer.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-bulk-queue-fallback-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-bulk-queue-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            _ = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(6));

            Assert.True(Volatile.Read(ref blockedDirectControlCount) > 0);
            Assert.True(hostLane.EnsureListenerCalls > 0);
            Assert.True(helperLane.StartDialerCalls > 0);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_control_priority_failed; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("error=Timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_priority_failed; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_sent; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("lane=bulk_queue_fallback", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_queued;", logTail, StringComparison.Ordinal);
            Assert.Contains("observed_lane=bulk_queue_fallback", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_control_send_wait_timeout; purpose=offer;", logTail, StringComparison.Ordinal);

            blockedDirectControlOffer.TrySetResult(null);
            await Task.Delay(100, cts.Token);
        }
        finally
        {
            blockedDirectControlOffer.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = previousDirectSendWait;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_WaitsForSlowActivationControlSend()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.slow-offer.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.slow-offer.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var delayedOfferSendCount = 0;
            hostClient.BeforeSendAsync = async (_, payload, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    Interlocked.Increment(ref delayedOfferSendCount);
                    await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-slow-offer-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-slow-offer-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_slow_offer",
                cts.Token);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(12));

            Assert.True(Volatile.Read(ref delayedOfferSendCount) > 0);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_control_queue_accepted; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_received_raw;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_activation_offer_not_observed;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_control_send_wait_timeout; purpose=offer;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockDefersReceiveStallForRecoveredPostTunaFallbackRoute()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.post-fallback-bypass.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.post-fallback-bypass.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-post-fallback-bypass-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                null);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-post-fallback-bypass-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                null);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_post_fallback_bypass";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);

            SetPrivateField(host, "outboundAccelerationOfferTrigger", "runtime_unlock");
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");

            var deferred = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldDeferPostTunaFallbackReceiveStallForRuntimeUnlockOffer",
                "receive_stall_recovery_in_progress",
                sessionId));
            Assert.True(deferred);

            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");

            var regularDeferred = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldDeferPostTunaFallbackReceiveStallForRuntimeUnlockOffer",
                "receive_stall_recovery_in_progress",
                sessionId));
            Assert.False(regularDeferred);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockKeepsPostTunaFallbackAvailableBeforeObservedOfferSend()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousControlSendWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousDirectSendWait = NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(80);
        NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = TimeSpan.FromMilliseconds(40);
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromMilliseconds(250);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(25);
        var blockedOfferSend = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.post-fallback-pre-pause.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.post-fallback-pre-pause.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.BeforeSendCoreAsync = async (_, payload, _, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    await blockedOfferSend.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };

            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-post-fallback-pre-pause-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-post-fallback-pre-pause-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_post_fallback_pre_pause";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            var dataSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            var negotiationTask = ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_tuna_activation_negotiation_post_tuna_fallback_pause_suppressed;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains(
                "event=filetransfer_tuna_activation_negotiation_post_tuna_fallback_pause_suppressed;",
                logTail,
                StringComparison.Ordinal);
            Assert.Contains("suppress_reason=active_post_tuna_fallback_route", logTail, StringComparison.Ordinal);
            Assert.True(
                logTail.Contains("trigger=post_tuna_fallback_offer_send_prepare", StringComparison.Ordinal) ||
                logTail.Contains("trigger=offer_send_prepare", StringComparison.Ordinal) ||
                logTail.Contains("trigger=offer_queue_accepted", StringComparison.Ordinal),
                "Expected Tuna reactivation to observe active post-Tuna fallback before the activation offer was observed.");
            Assert.DoesNotContain("event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                availabilityEvents,
                e => !e.IsAvailable &&
                     !e.RequiresResumeRequest &&
                     e.Reason == "tuna_activation_negotiating");
            Assert.True(dataSession.IsAvailable);

            blockedOfferSend.TrySetResult(null);
            await negotiationTask.ConfigureAwait(false);
        }
        finally
        {
            blockedOfferSend.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousControlSendWait;
            NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = previousDirectSendWait;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_WaitsForBridgeRecoveryBeforeRuntimeUnlockOffer()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousBridgeRecoveryWait = NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests = TimeSpan.FromSeconds(2);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.bridge-recovery.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.bridge-recovery.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var offerSendAttempted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            hostClient.BeforeSendCoreAsync = (_, payload, _, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    offerSendAttempted.TrySetResult(null);
                }

                return Task.CompletedTask;
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-bridge-recovery-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-bridge-recovery-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_bridge_recovery",
                cts.Token);
            InvokePrivateMethod(
                host,
                "MarkFileTransferTunaActivationBridgeRecoveryStarted",
                "control_receive_stalled");
            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "control_receive_stalled"));
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            var negotiationTask = ((ITransportAccelerationControl)host)
                .RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await Task.Delay(200, cts.Token);
            Assert.False(offerSendAttempted.Task.IsCompleted);

            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.Ready,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: 100,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: null));
            await Task.Delay(100, cts.Token);
            Assert.False(offerSendAttempted.Task.IsCompleted);

            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryCompleted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "control_receive_stalled"));
            await WaitUntilAsync(
                () => offerSendAttempted.Task.IsCompleted,
                TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(6));
            await negotiationTask.WaitAsync(cts.Token);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_activation_control_send_waiting_for_bridge_recovery;", logTail, StringComparison.Ordinal);
            Assert.Contains("purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_control_send_bridge_recovery_settled;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_queued;", logTail, StringComparison.Ordinal);
            Assert.True(
                logTail.Contains("observed_lane=control_priority", StringComparison.Ordinal) ||
                logTail.Contains("observed_lane=control_to_bulk_endpoint", StringComparison.Ordinal) ||
                logTail.Contains("observed_lane=bulk_queue_fallback", StringComparison.Ordinal),
                "Runtime-unlock offer should have an explicit peer-visible observed lane.");
            Assert.Contains("queue_local_only=0", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests = previousBridgeRecoveryWait;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockBridgeRecoveryWaitTimeoutArmsRetry()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousBridgeRecoveryWait = NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests;
        var previousRecoveryRequest = NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests = TimeSpan.FromMilliseconds(80);
        NknSignalingTransport? hostTransportForHook = null;
        var recoveryRequestCount = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.bridge-wait-timeout.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.bridge-wait-timeout.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = (transport, reason, sessionId) =>
            {
                if (!ReferenceEquals(transport, hostTransportForHook))
                {
                    return false;
                }

                Assert.Equal("tuna_activation_offer_send_timeout", reason);
                Assert.False(string.IsNullOrWhiteSpace(sessionId));
                Interlocked.Increment(ref recoveryRequestCount);
                return true;
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-bridge-wait-timeout-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            hostTransportForHook = host;
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-bridge-wait-timeout-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_bridge_wait_timeout",
                cts.Token);
            InvokePrivateMethod(
                host,
                "MarkFileTransferTunaActivationBridgeRecoveryStarted",
                "control_receive_stalled");
            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "control_receive_stalled"));
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            var negotiationTask = ((ITransportAccelerationControl)host)
                .RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_activation_offer_not_observed;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            await WaitUntilAsync(
                () => Volatile.Read(ref recoveryRequestCount) > 0,
                TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_activation_control_send_recovery_requested;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_activation_control_send_waiting_for_bridge_recovery;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_control_send_deferred_for_regular_v4_recovery;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_control_send_recovery_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("trigger=bridge_recovery_wait_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_armed;", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_scheduled=0", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_after_recovery_armed=1", logTail, StringComparison.Ordinal);

            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryReceiveResumed,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "receive_stall_recovery_receive_resumed"));

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled;", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));

            await negotiationTask.WaitAsync(cts.Token);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests = previousBridgeRecoveryWait;
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = previousRecoveryRequest;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockSendTimeoutRequestsBridgeRecoveryBeforeReplay()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousControlSendWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousDirectSendWait = NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests;
        var previousBridgeRecoveryWait = NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        var previousRecoveryRequest = NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(120);
        NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = TimeSpan.FromMilliseconds(50);
        NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests = TimeSpan.FromSeconds(2);
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.FromSeconds(20);
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromSeconds(2);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(25);
        var blockedOfferSend = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        NknSignalingTransport? hostTransportForHook = null;
        var recoveryRequestCount = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.recovery-request.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.recovery-request.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.BeforeSendCoreAsync = async (_, payload, _, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    await blockedOfferSend.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = (transport, reason, sessionId) =>
            {
                if (!ReferenceEquals(transport, hostTransportForHook))
                {
                    return false;
                }

                Assert.Equal("tuna_activation_offer_send_timeout", reason);
                Assert.False(string.IsNullOrWhiteSpace(sessionId));
                Interlocked.Increment(ref recoveryRequestCount);
                return true;
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-recovery-request-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            hostTransportForHook = host;
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-recovery-request-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_recovery_request",
                cts.Token);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            var negotiationTask = ((ITransportAccelerationControl)host)
                .RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_activation_offer_not_observed;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(4));
            await WaitUntilAsync(
                () => Volatile.Read(ref recoveryRequestCount) > 0,
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_activation_control_send_recovery_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=tuna_activation_offer_send_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("accepted=1", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_outbound_offer_retired; reason=offer_send_not_observed", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_armed;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_scheduled=0", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_after_recovery_armed=1", logTail, StringComparison.Ordinal);
            Assert.Contains("replay_scheduled=0", logTail, StringComparison.Ordinal);
            Assert.Contains("answer_timeout_scheduled=0", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_offer_replay_sent;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_offer_answer_timeout;", logTail, StringComparison.Ordinal);

            blockedOfferSend.TrySetResult(null);
            await Task.Delay(150, cts.Token);
            Assert.DoesNotContain(
                "event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled;",
                ReadOperationalLogTail(logStart),
                StringComparison.Ordinal);

            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryReceiveResumed,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "receive_stall_recovery_receive_resumed"));

            await WaitUntilAsync(
                () =>
                {
                    var currentTail = ReadOperationalLogTail(logStart);
                    return currentTail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled;", StringComparison.Ordinal) &&
                           currentTail.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_offer_queued;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(6));
            await negotiationTask.WaitAsync(cts.Token);

            logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedOfferSend.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousControlSendWait;
            NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = previousDirectSendWait;
            NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests = previousBridgeRecoveryWait;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = previousRecoveryRequest;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockPostTunaFallbackSendTimeoutRequestsRecoveryWithoutPause()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousControlSendWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousDirectSendWait = NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests;
        var previousBridgeRecoveryWait = NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        var previousRecoveryRequest = NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(120);
        NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = TimeSpan.FromMilliseconds(50);
        NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests = TimeSpan.FromSeconds(2);
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromSeconds(2);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(25);
        var blockedOfferSend = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        NknSignalingTransport? hostTransportForHook = null;
        var recoveryRequestCount = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.post-fallback-recovery-request.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.post-fallback-recovery-request.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.BeforeSendCoreAsync = async (_, payload, _, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    await blockedOfferSend.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = (transport, reason, sessionId) =>
            {
                if (!ReferenceEquals(transport, hostTransportForHook))
                {
                    return false;
                }

                Assert.Equal("tuna_activation_offer_send_timeout", reason);
                Assert.False(string.IsNullOrWhiteSpace(sessionId));
                Interlocked.Increment(ref recoveryRequestCount);
                return true;
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-post-fallback-recovery-request-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            hostTransportForHook = host;
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-post-fallback-recovery-request-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_post_fallback_recovery_request";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            var negotiationTask = ((ITransportAccelerationControl)host)
                .RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_tuna_activation_negotiation_post_tuna_fallback_pause_suppressed;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_activation_offer_not_observed;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(4));
            await WaitUntilAsync(
                () => Volatile.Read(ref recoveryRequestCount) > 0,
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_activation_control_send_recovery_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=tuna_activation_offer_send_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("accepted=1", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_armed;", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_scheduled=0", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_after_recovery_armed=1", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", logTail, StringComparison.Ordinal);
            Assert.Contains(
                "event=filetransfer_tuna_activation_negotiation_post_tuna_fallback_pause_suppressed;",
                logTail,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;",
                logTail,
                StringComparison.Ordinal);

            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryReceiveResumed,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "receive_stall_recovery_receive_resumed"));
            blockedOfferSend.TrySetResult(null);

            await WaitUntilAsync(
                () =>
                {
                    var currentTail = ReadOperationalLogTail(logStart);
                    return currentTail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled;", StringComparison.Ordinal) &&
                           currentTail.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));
            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(8));
            await negotiationTask.WaitAsync(cts.Token);
        }
        finally
        {
            blockedOfferSend.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousControlSendWait;
            NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = previousDirectSendWait;
            NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests = previousBridgeRecoveryWait;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = previousRecoveryRequest;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockPostTunaFallbackSendTimeoutJoinsExistingRecovery()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousControlSendWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousDirectSendWait = NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests;
        var previousBridgeRecoveryWait = NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        var previousRecoveryRequest = NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(120);
        NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = TimeSpan.FromMilliseconds(50);
        NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests = TimeSpan.FromSeconds(2);
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromSeconds(2);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(25);
        var blockedOfferSend = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        NknSignalingTransport? hostTransportForHook = null;
        var recoveryRequestCount = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.post-fallback-existing-recovery.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.post-fallback-existing-recovery.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.BeforeSendCoreAsync = async (_, payload, _, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    await blockedOfferSend.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = (transport, reason, sessionId) =>
            {
                if (!ReferenceEquals(transport, hostTransportForHook))
                {
                    return false;
                }

                Assert.Equal("tuna_activation_offer_send_timeout", reason);
                Assert.False(string.IsNullOrWhiteSpace(sessionId));
                Interlocked.Increment(ref recoveryRequestCount);
                InvokePrivateMethod(
                    transport,
                    "MarkFileTransferTunaActivationBridgeRecoveryStarted",
                    "post_tuna_fallback_state_refresh_failed");
                InvokePrivateMethod(
                    transport,
                    "OnBridgeLifecycle",
                    transport,
                    new BridgeLifecycleEvent(
                        BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                        StartMode: null,
                        Pid: null,
                        ReadyTimeMs: null,
                        PingRttMs: null,
                        UptimeMs: null,
                        ExitCode: null,
                        ExitReasonKind: null,
                        ExitReasonText: "post_tuna_fallback_state_refresh_failed"));
                return false;
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-post-fallback-existing-recovery-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            hostTransportForHook = host;
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-post-fallback-existing-recovery-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_post_fallback_existing_recovery";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var fallbackStarted = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "StartTunaFallbackProofIfNeeded",
                "header_switch_off",
                sessionId,
                NknAccelerationLaneKind.File));
            Assert.True(fallbackStarted);
            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(host);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    83,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.WaitingForTargetTransport,
                    "header_switch_off",
                    IsUnresolved: true));
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            var negotiationTask = ((ITransportAccelerationControl)host)
                .RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_activation_offer_not_observed;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(4));
            await WaitUntilAsync(
                () => Volatile.Read(ref recoveryRequestCount) > 0,
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_activation_control_send_recovery_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=tuna_activation_offer_send_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("accepted=0", logTail, StringComparison.Ordinal);
            Assert.Contains("interruption_reason=offer_interrupted_by_bridge_recovery", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_armed;", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_scheduled=0", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_after_recovery_armed=1", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", logTail, StringComparison.Ordinal);

            blockedOfferSend.TrySetResult(null);
            await Task.Delay(150, cts.Token);
            Assert.DoesNotContain(
                "event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled;",
                ReadOperationalLogTail(logStart),
                StringComparison.Ordinal);

            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryReceiveResumed,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "receive_stall_recovery_receive_resumed"));

            await WaitUntilAsync(
                () =>
                {
                    var currentTail = ReadOperationalLogTail(logStart);
                    return currentTail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_deferred_for_fallback_repair;", StringComparison.Ordinal) ||
                           currentTail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_fallback_repair_soft_settle_deferred;", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));
            var finalTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled;", finalTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", finalTail, StringComparison.Ordinal);
            if (negotiationTask.IsCompleted)
            {
                await negotiationTask.WaitAsync(cts.Token);
            }
        }
        finally
        {
            blockedOfferSend.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousControlSendWait;
            NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = previousDirectSendWait;
            NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests = previousBridgeRecoveryWait;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = previousRecoveryRequest;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockRecoveredPostTunaFallbackRouteHintJoinsExistingRecovery()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousControlSendWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousDirectSendWait = NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests;
        var previousBridgeRecoveryWait = NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        var previousRecoveryRequest = NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(120);
        NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = TimeSpan.FromMilliseconds(50);
        NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests = TimeSpan.FromSeconds(2);
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromSeconds(2);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(25);
        var blockedOfferSend = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        NknSignalingTransport? hostTransportForHook = null;
        var recoveryRequestCount = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.recovered-post-fallback-existing-recovery.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.recovered-post-fallback-existing-recovery.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.BeforeSendCoreAsync = async (_, payload, _, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    await blockedOfferSend.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = (transport, reason, sessionId) =>
            {
                if (!ReferenceEquals(transport, hostTransportForHook))
                {
                    return false;
                }

                Assert.Equal("tuna_activation_offer_send_timeout", reason);
                Assert.False(string.IsNullOrWhiteSpace(sessionId));
                Interlocked.Increment(ref recoveryRequestCount);
                InvokePrivateMethod(
                    transport,
                    "OnBridgeLifecycle",
                    transport,
                    new BridgeLifecycleEvent(
                        BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                        StartMode: null,
                        Pid: null,
                        ReadyTimeMs: null,
                        PingRttMs: null,
                        UptimeMs: null,
                        ExitCode: null,
                        ExitReasonKind: null,
                        ExitReasonText: "post_tuna_fallback_state_refresh_failed"));
                return false;
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-recovered-post-fallback-existing-recovery-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            hostTransportForHook = host;
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-recovered-post-fallback-existing-recovery-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_recovered_post_fallback_existing_recovery";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_recovered_post_tuna_fallback_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            var negotiationTask = ((ITransportAccelerationControl)host)
                .RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_activation_offer_not_observed;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(4));
            await WaitUntilAsync(
                () => Volatile.Read(ref recoveryRequestCount) > 0,
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_activation_control_send_recovery_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("accepted=0", logTail, StringComparison.Ordinal);
            Assert.Contains("interruption_reason=offer_interrupted_by_bridge_recovery", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_armed;", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_scheduled=0", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_after_recovery_armed=1", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", logTail, StringComparison.Ordinal);

            blockedOfferSend.TrySetResult(null);
            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryReceiveResumed,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "receive_stall_recovery_receive_resumed"));

            await WaitUntilAsync(
                () =>
                {
                    var currentTail = ReadOperationalLogTail(logStart);
                    return currentTail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled;", StringComparison.Ordinal) &&
                           currentTail.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));
            await negotiationTask.WaitAsync(cts.Token);
        }
        finally
        {
            blockedOfferSend.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousControlSendWait;
            NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = previousDirectSendWait;
            NknSignalingTransport.FileTransferTunaActivationBridgeRecoveryWaitOverrideForTests = previousBridgeRecoveryWait;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = previousRecoveryRequest;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockBridgeRecoveryInterruptsWeakObservedGeneration()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.runtime-unlock.bridge-recovery-interrupt.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.runtime-unlock.bridge-recovery-interrupt.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-runtime-unlock-bridge-recovery-interrupt-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-runtime-unlock-bridge-recovery-interrupt-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var logStart = GetOperationalLogLength();

            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                nonce: "runtime_unlock_bridge_recovery_nonce",
                payerDecisionId: 101,
                generation: 9,
                observedSend: false,
                observedLane: null);

            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "runtime_unlock_bridge_recovery_started"));

            var state = host.RuntimeUnlockOfferStateForTests;
            Assert.False(state.HasOutboundOffer);
            Assert.True(state.IsRetired);
            Assert.Equal("offer_interrupted_by_bridge_recovery", state.RetiredReason);
            Assert.True(state.RetryArmed);
            Assert.False(state.RetryQueued);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_activation_offer_not_observed;", logTail, StringComparison.Ordinal);
            Assert.Contains("interruption_reason=offer_interrupted_by_bridge_recovery", logTail, StringComparison.Ordinal);
            Assert.Contains("observed_send=0", logTail, StringComparison.Ordinal);
            Assert.Contains("observed_lane=(none)", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_after_recovery_armed=1", logTail, StringComparison.Ordinal);
            Assert.Contains("answer_timeout_scheduled=0", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockBridgeRecoveryInterruptsObservedGenerationWithoutPeerReceipt()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.runtime-unlock.bridge-recovery-observed-interrupt.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.runtime-unlock.bridge-recovery-observed-interrupt.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-runtime-unlock-bridge-recovery-observed-interrupt-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-runtime-unlock-bridge-recovery-observed-interrupt-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var logStart = GetOperationalLogLength();

            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                nonce: "runtime_unlock_bridge_recovery_observed_interrupt_nonce",
                payerDecisionId: 111,
                generation: 19,
                observedSend: true,
                observedLane: "control_priority");

            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "runtime_unlock_bridge_recovery_started"));

            var state = host.RuntimeUnlockOfferStateForTests;
            Assert.False(state.HasOutboundOffer);
            Assert.True(state.IsRetired);
            Assert.Equal("offer_interrupted_by_bridge_recovery", state.RetiredReason);
            Assert.True(state.RetryArmed);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_activation_offer_not_observed;", logTail, StringComparison.Ordinal);
            Assert.Contains("interruption_reason=offer_interrupted_by_bridge_recovery", logTail, StringComparison.Ordinal);
            Assert.Contains("observed_send=1", logTail, StringComparison.Ordinal);
            Assert.Contains("observed_lane=control_priority", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_after_recovery_armed=1", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_runtime_unlock_observed_offer_preserved;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockBridgeRecoveryPreservesObservedAnswerWindow()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.runtime-unlock.bridge-recovery-observed-preserve.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.runtime-unlock.bridge-recovery-observed-preserve.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-runtime-unlock-bridge-recovery-observed-preserve-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-runtime-unlock-bridge-recovery-observed-preserve-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var nonce = "runtime_unlock_bridge_recovery_observed_preserve_nonce";
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                nonce,
                payerDecisionId: 112,
                generation: 20,
                observedSend: true,
                observedLane: "control_to_bulk_endpoint",
                answerTimeoutScheduled: true);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "runtime_unlock_bridge_recovery_started"));

            var state = host.RuntimeUnlockOfferStateForTests;
            Assert.True(state.HasOutboundOffer);
            Assert.False(state.IsRetired);
            Assert.False(state.RetryArmed);

            var answer = CreateAnswerPayload(
                sessionId,
                nonce,
                accepted: true,
                supportedLanes: new[] { "file" },
                payerDecisionId: 112);
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationAnswer,
                answer,
                "transport_acceleration_answer",
                answer.Nonce,
                sequence: 112);

            InvokePrivateMethod(host, "HandleTransportAccelerationAnswer", helperClient.Address, envelope);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_negotiated;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(6));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_observed_offer_preserved;", logTail, StringComparison.Ordinal);
            Assert.Contains("interruption_reason=offer_interrupted_by_bridge_recovery", logTail, StringComparison.Ordinal);
            Assert.Contains("observed_send=1", logTail, StringComparison.Ordinal);
            Assert.Contains("answer_ack_sent", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_stale_offer_answer_ignored;", logTail, StringComparison.Ordinal);
            Assert.Equal(NknAccelerationLaneKind.File, host.AccelerationNegotiatedLanesForTests);
            Assert.True(host.IsAccelerationAvailableForTests);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockQueueClearPreservesObservedAnswerWindow()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.runtime-unlock.queue-clear-observed-preserve.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.runtime-unlock.queue-clear-observed-preserve.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-runtime-unlock-queue-clear-observed-preserve-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-runtime-unlock-queue-clear-observed-preserve-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var nonce = "runtime_unlock_queue_clear_observed_preserve_nonce";
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                nonce,
                payerDecisionId: 113,
                generation: 21,
                observedSend: true,
                observedLane: "control_to_bulk_endpoint",
                answerTimeoutScheduled: true);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.QueueCleared,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "bulk_queue_cleared",
                    QueueLane: "bulk",
                    QueueClears: 7,
                    ClearedSinceLast: 3));

            var state = host.RuntimeUnlockOfferStateForTests;
            Assert.True(state.HasOutboundOffer);
            Assert.False(state.IsRetired);
            Assert.False(state.RetryArmed);

            var answer = CreateAnswerPayload(
                sessionId,
                nonce,
                accepted: true,
                supportedLanes: new[] { "file" },
                payerDecisionId: 113);
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationAnswer,
                answer,
                "transport_acceleration_answer",
                answer.Nonce,
                sequence: 113);

            InvokePrivateMethod(host, "HandleTransportAccelerationAnswer", helperClient.Address, envelope);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_negotiated;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(6));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_observed_offer_preserved;", logTail, StringComparison.Ordinal);
            Assert.Contains("interruption_reason=offer_interrupted_by_queue_clear", logTail, StringComparison.Ordinal);
            Assert.Contains("queue_lane=bulk", logTail, StringComparison.Ordinal);
            Assert.Contains("queue_clears=7", logTail, StringComparison.Ordinal);
            Assert.Contains("cleared_since_last=3", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_stale_offer_answer_ignored;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_activation_offer_not_observed;", logTail, StringComparison.Ordinal);
            Assert.Equal(NknAccelerationLaneKind.File, host.AccelerationNegotiatedLanesForTests);
            Assert.True(host.IsAccelerationAvailableForTests);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockQueueClearInterruptsUnreceivedGeneration()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.runtime-unlock.queue-clear-interrupt.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.runtime-unlock.queue-clear-interrupt.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-runtime-unlock-queue-clear-interrupt-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-runtime-unlock-queue-clear-interrupt-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var logStart = GetOperationalLogLength();

            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                nonce: "runtime_unlock_queue_clear_nonce",
                payerDecisionId: 102,
                generation: 10,
                observedSend: true,
                observedLane: "bulk_queue_fallback");

            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.QueueCleared,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "bulk_queue_cleared",
                    QueueLane: "bulk",
                    QueueClears: 4,
                    ClearedSinceLast: 2));

            var state = host.RuntimeUnlockOfferStateForTests;
            Assert.False(state.HasOutboundOffer);
            Assert.True(state.IsRetired);
            Assert.Equal("offer_interrupted_by_queue_clear", state.RetiredReason);
            Assert.True(state.RetryArmed);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_activation_offer_not_observed;", logTail, StringComparison.Ordinal);
            Assert.Contains("interruption_reason=offer_interrupted_by_queue_clear", logTail, StringComparison.Ordinal);
            Assert.Contains("queue_lane=bulk", logTail, StringComparison.Ordinal);
            Assert.Contains("queue_clears=4", logTail, StringComparison.Ordinal);
            Assert.Contains("cleared_since_last=2", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockQueueClearAfterPeerReceiveDoesNotRetireGeneration()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.runtime-unlock.queue-clear-peer-received.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.runtime-unlock.queue-clear-peer-received.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-runtime-unlock-queue-clear-peer-received-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-runtime-unlock-queue-clear-peer-received-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var logStart = GetOperationalLogLength();

            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                nonce: "runtime_unlock_queue_clear_peer_nonce",
                payerDecisionId: 103,
                generation: 11,
                observedSend: true,
                observedLane: "control_priority",
                peerReceived: true);

            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.QueueCleared,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "control_queue_cleared",
                    QueueLane: "control",
                    QueueClears: 1,
                    ClearedSinceLast: 1));

            var state = host.RuntimeUnlockOfferStateForTests;
            Assert.True(state.HasOutboundOffer);
            Assert.False(state.IsRetired);
            Assert.True(state.PeerReceived);
            Assert.False(state.RetryArmed);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain(
                $"event=tuna_acceleration_activation_offer_not_observed; session_id={sessionId};",
                logTail,
                StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockDefersRegularV4PauseUntilPeerAnswerAccepted()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        var blockedAnswerSend = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerSendAttempts = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.observed-offer.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.observed-offer.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            helperClient.BeforeSendCoreAsync = async (_, payload, _, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationAnswer)
                {
                    Interlocked.Increment(ref answerSendAttempts);
                    await blockedAnswerSend.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-observed-offer-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-observed-offer-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_observed_offer";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            var dataSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                    ForceRegularNknBulk = true,
                },
                cts.Token);

            var logStart = GetOperationalLogLength();
            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_offer_queued;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            await WaitUntilAsync(
                () => Volatile.Read(ref answerSendAttempts) >= 1,
                TimeSpan.FromSeconds(3));
            var peerPendingTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_pause_deferred;", peerPendingTail, StringComparison.Ordinal);
            Assert.Contains("reason=runtime_unlock_offer_observed_waiting_for_answer", peerPendingTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;", peerPendingTail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                availabilityEvents,
                e =>
                     !e.IsAvailable &&
                     e.Reason == "tuna_activation_negotiating");

            blockedAnswerSend.TrySetResult(null);
            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    e.TargetTransport == FileTransferTransportKind.Tuna),
                TimeSpan.FromSeconds(3));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;", logTail, StringComparison.Ordinal);
            Assert.Contains("trigger=answer_accepted", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_queued;", logTail, StringComparison.Ordinal);
            Assert.Contains("observed_lane=", logTail, StringComparison.Ordinal);
            Assert.Contains("queue_local_only=0", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_resumed;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_filetransfer_handoff_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("handoff_kind=normal_to_tuna_activation", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_activation_failed_regular_v4_resumed;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedAnswerSend.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_UnobservedRuntimeUnlockOfferResumesListenerStartPause()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousControlSendWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(50);
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromMilliseconds(250);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromSeconds(5);
        var blockedOfferSend = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.accepted-queue.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.accepted-queue.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.BeforeSendCoreAsync = async (_, payload, _, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    await blockedOfferSend.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-accepted-queue-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-accepted-queue-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var dataSession = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_accepted_queue",
                cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_activation_offer_not_observed;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_control_queue_accepted; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_queue_excluded_from_observed_wait; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_send_wait_timeout; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_activation_offer_not_observed;", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_scheduled=1", logTail, StringComparison.Ordinal);
            Assert.Contains("replay_scheduled=0", logTail, StringComparison.Ordinal);
            Assert.Contains("answer_timeout_scheduled=0", logTail, StringComparison.Ordinal);
            Assert.Contains("pause_deferred=1", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_rejected;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain($"event=filetransfer_tuna_activation_negotiation_regular_nkn_paused; session_id={sessionId};", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain($"event=filetransfer_tuna_activation_negotiation_regular_nkn_resumed; session_id={sessionId};", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain($"event=tuna_activation_failed_regular_v4_resumed; session_id={sessionId};", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain($"event=filetransfer_tuna_activation_negotiation_regular_nkn_pause_retained; session_id={sessionId};", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_offer_queued;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_outbound_offer_retired; reason=offer_send_not_observed", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", logTail, StringComparison.Ordinal);
            var availabilitySnapshot = availabilityEvents.ToArray();
            if (availabilitySnapshot.Any(e => !e.IsAvailable && e.Reason == "tuna_activation_negotiating"))
            {
                Assert.True(
                    availabilitySnapshot.Last().IsAvailable,
                    "A failed runtime-unlock offer must not leave the regular NKN data session paused.");
            }
            Assert.DoesNotContain("event=tuna_acceleration_offer_answer_timeout;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedOfferSend.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousControlSendWait;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockPendingQueueUnderRegularV4PressureDoesNotCountAsObserved()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousControlSendWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        var previousPressureOverride = NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(50);
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromMilliseconds(250);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(25);
        NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests = _ => true;
        var blockedOfferSend = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.queue-pressure.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.queue-pressure.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.BeforeSendCoreAsync = async (_, payload, _, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    await blockedOfferSend.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-queue-pressure-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-queue-pressure-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_queue_pressure";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            var dataSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                    ForceRegularNknBulk = true,
                },
                cts.Token);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_offer_rejected;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_control_queue_accepted; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_queue_excluded_from_observed_wait; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=observed_send_requires_direct_or_bulk_proof", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_send_wait_timeout; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_activation_offer_not_observed;", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_scheduled=1", logTail, StringComparison.Ordinal);
            Assert.Contains("replay_scheduled=0", logTail, StringComparison.Ordinal);
            Assert.Contains("answer_timeout_scheduled=0", logTail, StringComparison.Ordinal);
            Assert.Contains("pause_deferred=1", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_rejected;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain($"event=filetransfer_tuna_activation_negotiation_regular_nkn_paused; session_id={sessionId};", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain($"event=filetransfer_tuna_activation_negotiation_regular_nkn_resumed; session_id={sessionId};", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain($"event=tuna_activation_failed_regular_v4_resumed; session_id={sessionId};", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_control_queue_accepted_as_observed; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_offer_queued;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_outbound_offer_retired; reason=offer_send_not_observed", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain($"event=filetransfer_tuna_activation_negotiation_regular_nkn_pause_retained; session_id={sessionId};", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                availabilityEvents,
                e => !e.IsAvailable &&
                     e.Reason == "tuna_activation_negotiating");
            Assert.DoesNotContain("event=tuna_acceleration_offer_answer_timeout;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedOfferSend.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousControlSendWait;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests = previousPressureOverride;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockCompletedQueueUnderRegularV4PressureCountsAsExplicitObserved()
    {
        var method = typeof(NknSignalingTransport).GetMethod(
            "MapAccelerationControlQueueAcceptedObservedAttemptAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var logStart = GetOperationalLogLength();
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(
            null,
            new object?[]
            {
                Task.FromResult(true),
                "offer",
                MsgType.TransportAccelerationOffer,
                "regular_v4_control_feedback_pressure",
            }));
        await task;

        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.True((bool)result.GetType().GetProperty("Succeeded")!.GetValue(result)!);
        Assert.Equal(
            "control_queue_explicit_observed",
            result.GetType().GetProperty("ObservedLane")!.GetValue(result));

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=tuna_acceleration_control_queue_accepted_as_observed; purpose=offer", logTail, StringComparison.Ordinal);
        Assert.Contains("reason=regular_v4_control_feedback_pressure", logTail, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TransportAccelerationOffer_RuntimeUnlockReceiveStallBypassArmsQueueAcceptedEscape()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.receive-stall-escape.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-receive-stall-escape-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);

            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                "sess_receive_stall_escape",
                "nonce_receive_stall_escape",
                payerDecisionId: 17,
                generation: 3);

            var armMethod = typeof(NknSignalingTransport).GetMethod(
                "ArmRuntimeUnlockQueueAcceptedObservedEscape",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var reasonMethod = typeof(NknSignalingTransport).GetMethod(
                "GetRuntimeUnlockOfferQueueAcceptedObservedReason",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(armMethod);
            Assert.NotNull(reasonMethod);

            var logStart = GetOperationalLogLength();
            armMethod!.Invoke(host, new object?[] { "regular_v4_receive_stall_bypass" });
            var reason = Assert.IsType<string>(reasonMethod!.Invoke(host, Array.Empty<object?>()));

            Assert.Equal("regular_v4_receive_stall_bypass", reason);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_control_queue_observed_escape_armed;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=regular_v4_receive_stall_bypass", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TransportAccelerationOffer_RuntimeUnlockRegularV4PressureDoesNotCountAsQueueObservedProof()
    {
        FakeNknClient.ResetNetwork();
        var previousPressureOverride = NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests;
        NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests = _ => true;
        try
        {
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.receive-stall-no-queue-proof.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-receive-stall-no-queue-proof-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);

            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                "sess_receive_stall_no_queue_proof",
                "nonce_receive_stall_no_queue_proof",
                payerDecisionId: 19,
                generation: 4);

            var armMethod = typeof(NknSignalingTransport).GetMethod(
                "ArmRuntimeUnlockQueueAcceptedObservedEscape",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var reasonMethod = typeof(NknSignalingTransport).GetMethod(
                "GetRuntimeUnlockOfferQueueAcceptedObservedReason",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(armMethod);
            Assert.NotNull(reasonMethod);

            Assert.Null(reasonMethod!.Invoke(host, Array.Empty<object?>()));

            armMethod!.Invoke(host, new object?[] { "test_explicit_queue_escape" });
            var reason = Assert.IsType<string>(reasonMethod.Invoke(host, Array.Empty<object?>()));

            Assert.Equal("test_explicit_queue_escape", reason);
        }
        finally
        {
            NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests = previousPressureOverride;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockAuthorityDoesNotBypassReceiveRecoveryInProgress()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.receive-stall-authority.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.receive-stall-authority.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-receive-stall-authority-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-receive-stall-authority-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_receive_stall_authority";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                "nonce_receive_stall_authority",
                payerDecisionId: 23,
                generation: 5);
            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                5L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "regular_v4_unproven_recovery_escalation",
                true);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=session_recovery_contract_retry_authority_granted;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");

            var bypass = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldBypassRegularV4ReceiveStallForRuntimeUnlockOffer",
                "receive_stall_recovery_in_progress",
                sessionId));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.False(bypass);
            Assert.Contains("event=tuna_activation_control_send_regular_v4_receive_stall_bypass_blocked;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=awaiting_bridge_recovery_settle", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_activation_control_send_regular_v4_receive_stall_authority_probe_allowed;", logTail, StringComparison.Ordinal);

            var recoveryRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.RegularNknV4FastToken,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 0,
                AuthorityReason = "regular_v4_startup_local_only_no_ack",
            };
            var recoveryState = InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessStarted",
                recoveryRequest,
                sessionId,
                transferId,
                "session_liveness_timeout_pending");
            Assert.NotNull(recoveryState);
            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessBridgeRecoveryLifecycle",
                "started",
                "test_regular_v4_recovery_started_without_completion");
            var boundedBypass = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldBypassRegularV4ReceiveStallForRuntimeUnlockOffer",
                "receive_stall_recovery_in_progress",
                sessionId,
                0L,
                true));
            var boundedTail = ReadOperationalLogTail(logStart);

            Assert.False(boundedBypass);
            Assert.Contains("allow_stale_in_progress_authority_probe=1", boundedTail, StringComparison.Ordinal);
            Assert.Contains("reason=awaiting_bridge_recovery_settle_before_authority_probe", boundedTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_activation_control_send_regular_v4_receive_stall_authority_probe_allowed;", boundedTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockAuthorityBypassesStaleReceiveRecoveryInProgressWithBoundedProbe()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.receive-stall-stale-authority.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.receive-stall-stale-authority.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-receive-stall-stale-authority-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-receive-stall-stale-authority-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_receive_stall_stale_authority";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                "nonce_receive_stall_stale_authority",
                payerDecisionId: 24,
                generation: 6);
            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                6L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "regular_v4_unproven_recovery_escalation",
                true);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=session_recovery_contract_retry_authority_granted;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");

            var recoveryRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.RegularNknV4FastToken,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 0,
                AuthorityReason = "regular_v4_startup_local_only_no_ack",
            };
            var recoveryState = InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessStarted",
                recoveryRequest,
                sessionId,
                transferId,
                "session_liveness_timeout_pending");
            Assert.NotNull(recoveryState);
            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessBridgeRecoveryLifecycle",
                "started",
                "test_regular_v4_recovery_started_without_completion");
            var startedProperty = recoveryState.GetType().GetProperty(
                "BridgeRecoveryStartedUtcMs",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(startedProperty);
            startedProperty!.SetValue(
                recoveryState,
                DateTimeOffset.UtcNow.AddSeconds(-30).ToUnixTimeMilliseconds());

            var boundedBypass = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldBypassRegularV4ReceiveStallForRuntimeUnlockOffer",
                "receive_stall_recovery_in_progress",
                sessionId,
                0L,
                true));
            var boundedTail = ReadOperationalLogTail(logStart);

            Assert.True(boundedBypass);
            Assert.Contains("event=tuna_activation_control_send_regular_v4_receive_stall_authority_probe_allowed;", boundedTail, StringComparison.Ordinal);
            Assert.Contains("reason=stale_bridge_recovery_bounded_authority_observed_send_probe", boundedTail, StringComparison.Ordinal);
            Assert.Contains("allow_stale_in_progress_authority_probe=1", boundedTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockObservedOfferReplayBypassesReceiveRecoveryWithoutQueueEscape()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.observed-replay.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.observed-replay.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-observed-replay-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-observed-replay-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_observed_replay";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                "nonce_observed_replay",
                payerDecisionId: 31,
                generation: 7,
                observedSend: true,
                observedLane: "control_priority",
                answerTimeoutScheduled: true);
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                6L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "regular_v4_unproven_recovery_escalation",
                true);
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");
            InvokePrivateMethod(
                host,
                "MarkRuntimeUnlockRecoveryContractRetryObserved",
                sessionId,
                7L,
                "control_priority");

            var logStart = GetOperationalLogLength();
            var replayBypass = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldBypassRegularV4ReceiveStallForRuntimeUnlockObservedOfferReplay",
                "receive_stall_recovery_in_progress",
                sessionId,
                "offer_replay"));
            var initialOfferBypass = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldBypassRegularV4ReceiveStallForRuntimeUnlockObservedOfferReplay",
                "receive_stall_recovery_in_progress",
                sessionId,
                "offer"));
            var queueReason = InvokePrivateMethod(
                host,
                "GetRuntimeUnlockOfferQueueAcceptedObservedReason");

            var logTail = ReadOperationalLogTail(logStart);
            Assert.True(replayBypass);
            Assert.False(initialOfferBypass);
            Assert.Null(queueReason);
            Assert.Contains("event=tuna_activation_control_send_regular_v4_receive_stall_observed_replay_allowed;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=bounded_observed_offer_replay", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_control_queue_observed_escape_armed;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockObservedOfferReplayBypassesPostFallbackReceiveRecoveryWithCurrentProof()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.post-fallback-observed-replay.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.post-fallback-observed-replay.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-post-fallback-observed-replay-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-post-fallback-observed-replay-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_post_fallback_observed_replay";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                "nonce_post_fallback_observed_replay",
                payerDecisionId: 43,
                generation: 11,
                observedSend: true,
                observedLane: "control_to_bulk_endpoint",
                answerTimeoutScheduled: true);
            InvokePrivateMethod(
                host,
                "RecordPostTunaFallbackReceiverFrontierProofHint",
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = 23,
                    ContiguousCommittedChunkIndex = 16,
                    DurableReceivedHighestChunkIndex = 20,
                    CreditUntilChunkIndexExclusive = 40,
                },
                "received",
                sessionId);
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                10L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "post_tuna_fallback_state_refresh_failed",
                true);
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");
            InvokePrivateMethod(
                host,
                "MarkRuntimeUnlockRecoveryContractRetryObserved",
                sessionId,
                11L,
                "control_to_bulk_endpoint");

            var logStart = GetOperationalLogLength();
            var replayBypass = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldBypassPostTunaFallbackReceiveStallForRuntimeUnlockObservedOfferReplay",
                "receive_stall_recovery_in_progress",
                sessionId,
                "offer_replay",
                250L));
            var initialOfferBypass = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldBypassPostTunaFallbackReceiveStallForRuntimeUnlockObservedOfferReplay",
                "receive_stall_recovery_in_progress",
                sessionId,
                "offer",
                250L));
            var queueReason = InvokePrivateMethod(
                host,
                "GetRuntimeUnlockOfferQueueAcceptedObservedReason");

            var logTail = ReadOperationalLogTail(logStart);
            Assert.True(replayBypass);
            Assert.False(initialOfferBypass);
            Assert.Null(queueReason);
            Assert.Contains("event=tuna_activation_control_send_post_tuna_fallback_receive_stall_observed_replay_allowed;", logTail, StringComparison.Ordinal);
            Assert.Contains("observed_lane=control_to_bulk_endpoint", logTail, StringComparison.Ordinal);
            Assert.Contains("proof=", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=bounded_observed_offer_replay", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_control_queue_observed_escape_armed;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockAuthorityBypassesStalePostFallbackReceiveStallWithCurrentProof()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.post-fallback-receive-stall-authority.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.post-fallback-receive-stall-authority.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-post-fallback-receive-stall-authority-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-post-fallback-receive-stall-authority-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_post_fallback_receive_stall_authority";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                "nonce_post_fallback_receive_stall_authority",
                payerDecisionId: 41,
                generation: 9);
            InvokePrivateMethod(
                host,
                "RecordPostTunaFallbackReceiverFrontierProofHint",
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = 17,
                    ContiguousCommittedChunkIndex = 12,
                    DurableReceivedHighestChunkIndex = 12,
                    CreditUntilChunkIndexExclusive = 32,
                },
                "received",
                sessionId);
            var authorityRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "post_tuna_fallback_state_refresh_failed")
            {
                RouteToken = FileTransferRouteResolver.PostTunaFallbackV6Token,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                LiveRouteEpoch = 2,
                TransferLegGeneration = 3,
                BridgeRecoveryGeneration = 1,
                TransportEpoch = 17,
                CheckpointRequestId = "v6-regular-nkn-state-refresh:17",
                AuthorityReason = "post_tuna_fallback_state_refresh_failed",
            };
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackLegAuthorityStarted",
                authorityRequest,
                sessionId,
                transferId,
                "post_tuna_fallback_state_refresh_failed");
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackLegAuthorityBridgeRecoveryLifecycle",
                "receive_resumed",
                "test_post_fallback_receive_resumed");

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                9L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "post_tuna_fallback_state_refresh_failed",
                true);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=session_recovery_contract_retry_authority_granted;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");

            var activeRecoveryBypass = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldBypassPostTunaFallbackReceiveStallForRuntimeUnlockAuthorityProbe",
                "receive_stall_recovery_in_progress",
                sessionId,
                250L,
                false));
            Assert.False(activeRecoveryBypass);

            var staleGateBypass = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldBypassPostTunaFallbackReceiveStallForRuntimeUnlockAuthorityProbe",
                "receive_stall_recovery_in_progress",
                sessionId,
                0L,
                false));

            var bypassAfterWait = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldBypassPostTunaFallbackReceiveStallForRuntimeUnlockAuthorityProbe",
                "receive_stall_recovery_in_progress",
                sessionId,
                0L,
                true));
            var logTail = ReadOperationalLogTail(logStart);

            Assert.True(staleGateBypass);
            Assert.True(bypassAfterWait);
            Assert.Contains("event=tuna_activation_control_send_post_tuna_fallback_receive_stall_bypass_blocked;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=awaiting_bridge_recovery_settle", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_control_send_post_tuna_fallback_receive_stall_authority_probe_allowed;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=current_fallback_proof_cleared_stale_recovery_gate", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=bounded_authority_observed_send_probe_after_wait", logTail, StringComparison.Ordinal);
            Assert.Contains("proof=fallback_authority_receive_proof", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockDefersPostFallbackProbeWhileCheckpointAuthorityAwaitingProof()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.post-fallback-authority-deferred.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.post-fallback-authority-deferred.address");
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-post-fallback-authority-deferred-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-post-fallback-authority-deferred-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_post_fallback_authority_deferred";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                "nonce_post_fallback_authority_deferred",
                payerDecisionId: 42,
                generation: 10);
            InvokePrivateMethod(
                host,
                "RecordPostTunaFallbackReceiverFrontierProofHint",
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = 21,
                    ContiguousCommittedChunkIndex = 32,
                    DurableReceivedHighestChunkIndex = 48,
                    CreditUntilChunkIndexExclusive = 64,
                },
                "received",
                sessionId);
            var authorityRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "post_tuna_fallback_state_refresh_failed")
            {
                RouteToken = FileTransferRouteResolver.PostTunaFallbackV6Token,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                LiveRouteEpoch = 2,
                TransferLegGeneration = 4,
                BridgeRecoveryGeneration = 2,
                TransportEpoch = 21,
                CheckpointRequestId = "v6-regular-nkn-state-refresh:21",
                AuthorityReason = "post_tuna_fallback_state_refresh_failed",
            };
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackLegAuthorityStarted",
                authorityRequest,
                sessionId,
                transferId,
                "post_tuna_fallback_state_refresh_failed");

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                10L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "post_tuna_fallback_state_refresh_failed",
                true);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=session_recovery_contract_retry_authority_granted;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");

            var staleGateBypass = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldBypassPostTunaFallbackReceiveStallForRuntimeUnlockAuthorityProbe",
                "receive_stall_recovery_in_progress",
                sessionId,
                0L,
                true));
            var retryAllowed = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldAllowAccelerationRetryDespiteFallbackControlProofPending",
                sessionId,
                "post_tuna_fallback_state_refresh_failed",
                NknAccelerationLaneKind.File,
                "runtime_unlock_offer_send_not_observed",
                "test"));
            var logTail = ReadOperationalLogTail(logStart);

            Assert.False(staleGateBypass);
            Assert.False(retryAllowed);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_retry_after_post_tuna_fallback_stability_deferred;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=fallback_authority_awaiting_receive_proof", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_control_send_post_tuna_fallback_receive_stall_bypass_blocked;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_activation_control_send_post_tuna_fallback_receive_stall_authority_probe_allowed;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_allowed_post_tuna_fallback_current_authority;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockFinalProbeBypassesPostFallbackCheckpointPending()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.post-fallback-final-probe.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.post-fallback-final-probe.address");
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-post-fallback-final-probe-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-post-fallback-final-probe-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_post_fallback_final_probe";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                "nonce_post_fallback_final_probe",
                payerDecisionId: 43,
                generation: 11);
            var authorityRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "post_tuna_fallback_state_refresh_failed")
            {
                RouteToken = FileTransferRouteResolver.PostTunaFallbackV6Token,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                LiveRouteEpoch = 2,
                TransferLegGeneration = 5,
                BridgeRecoveryGeneration = 3,
                TransportEpoch = 22,
                CheckpointRequestId = "v6-regular-nkn-state-refresh:22",
                AuthorityReason = "post_tuna_fallback_state_refresh_failed",
            };
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackLegAuthorityStarted",
                authorityRequest,
                sessionId,
                transferId,
                "post_tuna_fallback_state_refresh_failed");
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackNknProofPending",
                "post_tuna_fallback_state_refresh_failed",
                sessionId,
                NknAccelerationLaneKind.File,
                authorityRequest);

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                11L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "post_tuna_fallback_state_refresh_failed",
                true);

            var recoveryState = GetPrivateField(host, "runtimeUnlockRecoveryRetryState");
            Assert.NotNull(recoveryState);
            var stateType = recoveryState!.GetType();
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            stateType.GetProperty(
                "RetryDeadlineUtcMs",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(
                recoveryState,
                nowMs + 5_000);
            stateType.GetProperty(
                "LivenessDeferralDeadlineUtcMs",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(
                recoveryState,
                nowMs + 5_000);

            var softSettleMethod = typeof(NknSignalingTransport).GetMethod(
                "ShouldSoftSettleRuntimeUnlockRetryAfterFallbackRepair",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(softSettleMethod);
            var args = new object?[] { 11L, sessionId, null };
            var softSettled = Assert.IsType<bool>(softSettleMethod!.Invoke(host, args));
            var settleReason = Assert.IsType<string>(args[2]);
            Assert.True(softSettled);
            Assert.Equal("active_post_tuna_fallback_final_observed_send_probe", settleReason);

            var retryAllowed = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldAllowAccelerationRetryDespiteFallbackControlProofPending",
                sessionId,
                "post_tuna_fallback_state_refresh_failed",
                NknAccelerationLaneKind.File,
                "runtime_unlock_offer_send_not_observed",
                "test"));
            Assert.True(retryAllowed);

            InvokePrivateMethod(host, "ScheduleRuntimeUnlockRetryAfterRecoveryIfArmed", "test_final_probe");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=session_recovery_contract_retry_authority_granted;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");

            var staleGateBypass = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldBypassPostTunaFallbackReceiveStallForRuntimeUnlockAuthorityProbe",
                "receive_stall_recovery_in_progress",
                sessionId,
                0L,
                true));
            var logTail = ReadOperationalLogTail(logStart);

            Assert.True(staleGateBypass);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_retry_after_post_tuna_fallback_final_probe_allowed;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retry_allowed_post_tuna_fallback_final_probe;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_control_send_post_tuna_fallback_receive_stall_authority_probe_allowed;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=bounded_final_observed_send_probe_without_fallback_proof", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockFinalProbeDefersWhilePostFallbackBridgeRecoveryInProgress()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.post-fallback-final-probe-bridge-recovery.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.post-fallback-final-probe-bridge-recovery.address");
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-post-fallback-final-probe-bridge-recovery-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-post-fallback-final-probe-bridge-recovery-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_post_fallback_final_probe_bridge_recovery";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                "nonce_post_fallback_final_probe_bridge_recovery",
                payerDecisionId: 45,
                generation: 13);
            var authorityRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "post_tuna_fallback_state_refresh_failed")
            {
                RouteToken = FileTransferRouteResolver.PostTunaFallbackV6Token,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                LiveRouteEpoch = 2,
                TransferLegGeneration = 6,
                BridgeRecoveryGeneration = 4,
                TransportEpoch = 23,
                CheckpointRequestId = "v6-regular-nkn-state-refresh:23",
                AuthorityReason = "post_tuna_fallback_state_refresh_failed",
            };
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackLegAuthorityStarted",
                authorityRequest,
                sessionId,
                transferId,
                "post_tuna_fallback_state_refresh_failed");
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackLegAuthorityBridgeRecoveryLifecycle",
                "started",
                "test_post_fallback_bridge_recovery_in_progress");
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackNknProofPending",
                "post_tuna_fallback_state_refresh_failed",
                sessionId,
                NknAccelerationLaneKind.File,
                authorityRequest);

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                13L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "post_tuna_fallback_state_refresh_failed",
                true);

            var recoveryState = GetPrivateField(host, "runtimeUnlockRecoveryRetryState");
            Assert.NotNull(recoveryState);
            var stateType = recoveryState!.GetType();
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            stateType.GetProperty(
                "RetryDeadlineUtcMs",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(
                recoveryState,
                nowMs + 5_000);
            stateType.GetProperty(
                "LivenessDeferralDeadlineUtcMs",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(
                recoveryState,
                nowMs + 5_000);

            var softSettleMethod = typeof(NknSignalingTransport).GetMethod(
                "ShouldSoftSettleRuntimeUnlockRetryAfterFallbackRepair",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(softSettleMethod);
            var args = new object?[] { 13L, sessionId, null };
            var softSettled = Assert.IsType<bool>(softSettleMethod!.Invoke(host, args));
            var settleReason = Assert.IsType<string>(args[2]);
            Assert.False(softSettled);
            Assert.Equal("fallback_authority_bridge_recovery_in_progress", settleReason);

            var retryAllowed = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldAllowAccelerationRetryDespiteFallbackControlProofPending",
                sessionId,
                "post_tuna_fallback_state_refresh_failed",
                NknAccelerationLaneKind.File,
                "runtime_unlock_offer_send_not_observed",
                "test"));
            Assert.False(retryAllowed);

            stateType.GetProperty(
                "Settled",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(
                recoveryState,
                true);
            stateType.GetProperty(
                "RetryAuthorityGranted",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(
                recoveryState,
                true);
            stateType.GetProperty(
                "RetryAuthorityPending",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(
                recoveryState,
                true);
            stateType.GetProperty(
                "RetryDispatched",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(
                recoveryState,
                true);
            stateType.GetProperty(
                "ObservedSendDeadlineUtcMs",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(
                recoveryState,
                nowMs + 5_000);
            stateType.GetProperty(
                "AuthorityFailureReason",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(
                recoveryState,
                "post_tuna_fallback_checkpoint_pending_final_probe");

            var staleGateBypass = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldBypassPostTunaFallbackReceiveStallForRuntimeUnlockAuthorityProbe",
                "receive_stall_recovery_in_progress",
                sessionId,
                0L,
                true));
            var logTail = ReadOperationalLogTail(logStart);

            Assert.False(staleGateBypass);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_retry_after_post_tuna_fallback_final_probe_deferred;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=fallback_authority_bridge_recovery_in_progress", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_control_send_post_tuna_fallback_receive_stall_bypass_blocked;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_runtime_unlock_retry_after_post_tuna_fallback_final_probe_allowed;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_allowed_post_tuna_fallback_final_probe;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_activation_control_send_post_tuna_fallback_receive_stall_authority_probe_allowed;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockPeerResponseListenerRearmBypassesPostFallbackBridgeRecovery()
    {
        FakeNknClient.ResetNetwork();
        var previousReceiveRecoveryBlocker = NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests;
        NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests =
            _ => "receive_stall_recovery_in_progress";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.post-fallback-listener-rearm-bridge.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.post-fallback-listener-rearm-bridge.address");
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-post-fallback-listener-rearm-bridge-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-post-fallback-listener-rearm-bridge-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_post_fallback_listener_rearm_bridge";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);

            var authorityRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "post_tuna_fallback_state_refresh_failed")
            {
                RouteToken = FileTransferRouteResolver.PostTunaFallbackV6Token,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                LiveRouteEpoch = 2,
                TransferLegGeneration = 7,
                BridgeRecoveryGeneration = 5,
                TransportEpoch = 24,
                CheckpointRequestId = "v6-regular-nkn-state-refresh:24",
                AuthorityReason = "post_tuna_fallback_state_refresh_failed",
            };
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackLegAuthorityStarted",
                authorityRequest,
                sessionId,
                transferId,
                "post_tuna_fallback_state_refresh_failed");
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackLegAuthorityBridgeRecoveryLifecycle",
                "started",
                "test_post_fallback_bridge_recovery_in_progress");
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackNknProofPending",
                "post_tuna_fallback_state_refresh_failed",
                sessionId,
                NknAccelerationLaneKind.File,
                authorityRequest);

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                14L,
                sessionId,
                "runtime_unlock_offer_peer_response_timeout",
                "post_tuna_fallback_state_refresh_failed",
                true);

            var softSettleMethod = typeof(NknSignalingTransport).GetMethod(
                "ShouldSoftSettleRuntimeUnlockRetryAfterFallbackRepair",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(softSettleMethod);
            var args = new object?[] { 14L, sessionId, null };
            var softSettled = Assert.IsType<bool>(softSettleMethod!.Invoke(host, args));
            var settleReason = Assert.IsType<string>(args[2]);
            Assert.True(softSettled);
            Assert.Equal("active_post_tuna_fallback_listener_rearm_required", settleReason);

            var retryAllowedBeforeRearm = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldAllowAccelerationRetryDespiteFallbackControlProofPending",
                sessionId,
                "post_tuna_fallback_state_refresh_failed",
                NknAccelerationLaneKind.File,
                "runtime_unlock_offer_peer_response_timeout",
                "preflight"));
            Assert.True(retryAllowedBeforeRearm);

            InvokePrivateMethod(
                host,
                "MarkRuntimeUnlockRecoveryContractListenerRearmCompleted",
                sessionId,
                "runtime_unlock");

            var retryAllowedAfterRearm = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldAllowAccelerationRetryDespiteFallbackControlProofPending",
                sessionId,
                "post_tuna_fallback_state_refresh_failed",
                NknAccelerationLaneKind.File,
                "runtime_unlock_offer_peer_response_timeout",
                "delayed"));
            Assert.True(retryAllowedAfterRearm);

            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");
            var dispatchDeferred = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "TryDeferRuntimeUnlockOfferDispatchForRegularV4ReceiveRecovery",
                sessionId,
                "runtime_unlock",
                101L,
                null,
                null));
            Assert.False(dispatchDeferred);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains(
                "event=tuna_acceleration_runtime_unlock_retry_after_post_tuna_fallback_listener_rearm_allowed;",
                logTail,
                StringComparison.Ordinal);
            Assert.Contains(
                "reason=peer_response_listener_rearm_must_precede_observed_send_probe",
                logTail,
                StringComparison.Ordinal);
            Assert.Contains(
                "event=tuna_acceleration_retry_allowed_fallback_control_unproven_for_listener_rearm;",
                logTail,
                StringComparison.Ordinal);
            Assert.Contains(
                "event=tuna_acceleration_retry_allowed_fallback_control_unproven_after_listener_rearm;",
                logTail,
                StringComparison.Ordinal);
            Assert.Contains(
                "event=tuna_acceleration_runtime_unlock_dispatch_regular_v4_receive_recovery_post_fallback_authority_bypassed;",
                logTail,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "event=tuna_acceleration_runtime_unlock_retry_after_post_tuna_fallback_final_probe_allowed;",
                logTail,
                StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests = previousReceiveRecoveryBlocker;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockLocalListenerRetryAuthorityDoesNotExpireBeforeOffer()
    {
        FakeNknClient.ResetNetwork();
        var previousAuthorityDeadline = NknSignalingTransport.RuntimeUnlockRetryAuthorityDeadlineOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        NknSignalingTransport.RuntimeUnlockRetryAuthorityDeadlineOverrideForTests = TimeSpan.FromMilliseconds(25);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromSeconds(5);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.listener-authority.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.listener-authority.address");
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-listener-authority-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                accelerationLane: null);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-listener-authority-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_listener_authority";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                5L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "regular_v4_unproven_recovery_escalation",
                true);
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");

            await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token);

            var contractProvider = Assert.IsAssignableFrom<ISessionRecoveryStateContract>(host);
            Assert.True(contractProvider.TryGetActiveSessionRecoveryContract(sessionId, out var snapshot));
            Assert.Equal(SessionRecoveryContractKind.RuntimeUnlockActivation, snapshot.Kind);
            Assert.Equal(SessionRecoveryContractState.RetryDispatched, snapshot.State);
            Assert.True(snapshot.RetryAuthorityPending);
            Assert.True(snapshot.RetryAuthorityGranted);
            Assert.False(snapshot.ObservedSendPending);
            Assert.Null(snapshot.AuthorityFailureReason);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=session_recovery_contract_retry_authority_granted;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_retry_dispatched;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=session_recovery_contract_retry_authority_failed;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.RuntimeUnlockRetryAuthorityDeadlineOverrideForTests = previousAuthorityDeadline;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockAuthorityDoesNotExpireWhileObservedSendInFlight()
    {
        FakeNknClient.ResetNetwork();
        var previousAuthorityDeadline = NknSignalingTransport.RuntimeUnlockRetryAuthorityDeadlineOverrideForTests;
        var previousReceiveRecoveryBlocker = NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests;
        var previousObservedBlocker = NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests;
        var previousPressureOverride = NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests;
        NknSignalingTransport.RuntimeUnlockRetryAuthorityDeadlineOverrideForTests = TimeSpan.FromMilliseconds(25);
        NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests = null;
        NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests = null;
        NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.inflight-authority.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.inflight-authority.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-inflight-authority-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-inflight-authority-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_inflight_authority";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                5L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "regular_v4_unproven_recovery_escalation",
                true);
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractAuthoritySendStarted", "offer");

            await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token);

            var contractProvider = Assert.IsAssignableFrom<ISessionRecoveryStateContract>(host);
            Assert.True(contractProvider.TryGetActiveSessionRecoveryContract(sessionId, out var snapshot));
            Assert.Equal(SessionRecoveryContractState.RetryDispatched, snapshot.State);
            Assert.True(snapshot.RetryAuthorityPending);
            Assert.True(snapshot.RetryAuthorityGranted);
            Assert.True(snapshot.ObservedSendPending);
            Assert.Null(snapshot.AuthorityFailureReason);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=session_recovery_contract_retry_authority_send_started;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=session_recovery_contract_retry_authority_failed;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.RuntimeUnlockRetryAuthorityDeadlineOverrideForTests = previousAuthorityDeadline;
            NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests = previousReceiveRecoveryBlocker;
            NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests = previousObservedBlocker;
            NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests = previousPressureOverride;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockReceiveStallSkipsBulkQueueFallbackProof()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousControlSendWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousDirectSendWait = NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        var previousObservedBlocker = NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests;
        var previousRecoveryRequest = NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromSeconds(5);
        NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = TimeSpan.FromMilliseconds(50);
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromMilliseconds(250);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(25);
        NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests = _ => "receive_stall_recovery_awaiting_receive_proof";
        var blockedOfferSend = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        NknSignalingTransport? hostTransportForHook = null;
        var recoveryRequestCount = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.receive-stall-skip.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.receive-stall-skip.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.BeforeSendCoreAsync = async (_, payload, _, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    await blockedOfferSend.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = (transport, reason, sessionId) =>
            {
                if (!ReferenceEquals(transport, hostTransportForHook))
                {
                    return false;
                }

                Assert.Equal("tuna_activation_offer_send_timeout", reason);
                Assert.False(string.IsNullOrWhiteSpace(sessionId));
                Interlocked.Increment(ref recoveryRequestCount);
                return true;
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-receive-stall-skip-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            hostTransportForHook = host;
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-receive-stall-skip-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_receive_stall_skip";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var recoveryRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.RegularNknV4FastToken,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 0,
                AuthorityReason = "regular_v4_startup_local_only_no_ack",
            };
            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessStarted",
                recoveryRequest,
                sessionId,
                transferId,
                "session_liveness_timeout_pending");
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => Volatile.Read(ref recoveryRequestCount) > 0,
                TimeSpan.FromSeconds(7));
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_activation_offer_not_observed;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(7));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_control_queue_accepted; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_queue_excluded_from_observed_wait; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_bulk_queue_fallback_skipped; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=runtime_unlock_active_filetransfer_requires_direct_observed_send", logTail, StringComparison.Ordinal);
            Assert.Contains("blocker_reason=receive_stall_recovery_awaiting_receive_proof", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_control_send_recovery_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("trigger=observed_send_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("accepted=1", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_activation_offer_not_observed;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_control_send_wait_timeout; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_offer_queued;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_offer_answer_timeout;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedOfferSend.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousControlSendWait;
            NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = previousDirectSendWait;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests = previousObservedBlocker;
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = previousRecoveryRequest;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockPostTunaFallbackProofAllowsInitialBulkQueueFallback()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousControlSendWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousDirectSendWait = NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        var previousObservedBlocker = NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests;
        var previousPeerProofFreshness = NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(500);
        NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = TimeSpan.FromMilliseconds(50);
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromSeconds(5);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(25);
        NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests = _ => "receive_stall_recovery_awaiting_receive_proof";
        NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = TimeSpan.FromMilliseconds(-1);
        var blockedDirectOfferSend = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var tunaSidecarPath = Path.Combine(Path.GetTempPath(), "nlink-initial-fallback-proof-runtime-unlock.exe");
            var hostClient = new FakeNknClient("host.tuna.file.activation.initial-fallback-proof.aaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.initial-fallback-proof.bbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.BeforeSendCoreAsync = async (_, payload, channel, ct) =>
            {
                if (channel == NknBridgeChannel.Control &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    await blockedDirectOfferSend.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-initial-fallback-proof-id", hostClient.Address),
                NknTunaAccelerationOptions.CreateRuntimePilot(tunaSidecarPath, NknAccelerationLaneKind.File),
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-initial-fallback-proof-id", helperClient.Address),
                NknTunaAccelerationOptions.CreatePassiveDialer(tunaSidecarPath, NknAccelerationLaneKind.File),
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_initial_fallback_proof";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            SetPrivateField(host, "remoteBulkEndpoint", helperClient.ConnectedBulkAddress);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            InvokePrivateMethod(
                host,
                "RecordPostTunaFallbackReceiverFrontierProofHint",
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = 17,
                    ContiguousCommittedChunkIndex = 24,
                    DurableReceivedHighestChunkIndex = 24,
                    CreditUntilChunkIndexExclusive = 96,
                },
                "received",
                sessionId);

            var logStart = GetOperationalLogLength();
            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains(
                               "event=tuna_acceleration_control_bulk_queue_fallback_trusted_by_post_tuna_fallback_repair;",
                               StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_offer_queued; reason=runtime_unlock;", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(7));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_control_queue_accepted; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_queue_excluded_from_observed_wait; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_bulk_queue_fallback_trusted_by_post_tuna_fallback_repair;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_sent; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("lane=bulk_queue_fallback", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_queued; reason=runtime_unlock;", logTail, StringComparison.Ordinal);
            Assert.Contains("observed_lane=bulk_queue_fallback", logTail, StringComparison.Ordinal);
            Assert.Contains("queue_local_only=0", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_control_bulk_queue_fallback_skipped; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_activation_control_send_recovery_requested;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_activation_offer_not_observed;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedDirectOfferSend.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousControlSendWait;
            NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = previousDirectSendWait;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            NknSignalingTransport.RuntimeUnlockOfferObservedSendBlockerReasonOverrideForTests = previousObservedBlocker;
            NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = previousPeerProofFreshness;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockPostTunaFallbackDirectSuccessStillSendsBulkQueueDuplicate()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousControlSendWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousDirectSendWait = NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        var previousPeerProofFreshness = NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(500);
        NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = TimeSpan.FromMilliseconds(250);
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromSeconds(5);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(25);
        NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = TimeSpan.FromMilliseconds(-1);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var tunaSidecarPath = Path.Combine(Path.GetTempPath(), "nlink-initial-fallback-direct-success-runtime-unlock.exe");
            var hostClient = new FakeNknClient("host.tuna.file.activation.initial-fallback-direct.aaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.initial-fallback-direct.bbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-initial-fallback-direct-id", hostClient.Address),
                NknTunaAccelerationOptions.CreateRuntimePilot(tunaSidecarPath, NknAccelerationLaneKind.File),
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-initial-fallback-direct-id", helperClient.Address),
                NknTunaAccelerationOptions.CreatePassiveDialer(tunaSidecarPath, NknAccelerationLaneKind.File),
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_initial_fallback_direct_success";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            SetPrivateField(host, "remoteBulkEndpoint", helperClient.ConnectedBulkAddress);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            InvokePrivateMethod(
                host,
                "RecordPostTunaFallbackReceiverFrontierProofHint",
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = 17,
                    ContiguousCommittedChunkIndex = 24,
                    DurableReceivedHighestChunkIndex = 24,
                    CreditUntilChunkIndexExclusive = 96,
                },
                "received",
                sessionId);

            var logStart = GetOperationalLogLength();
            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains(
                               "event=tuna_acceleration_control_bulk_endpoint_observed_untrusted; purpose=offer",
                               StringComparison.Ordinal) &&
                           tail.Contains("reason=post_tuna_fallback_requires_bulk_queue_duplicate", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_offer_queued; reason=runtime_unlock;", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(7));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_control_bulk_endpoint_observed_untrusted; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=post_tuna_fallback_requires_bulk_queue_duplicate", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_sent; purpose=offer; message_type=transport_acceleration_offer; lane=bulk_queue_fallback", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_queued; reason=runtime_unlock;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_activation_offer_not_observed;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousControlSendWait;
            NknSignalingTransport.AccelerationControlDirectSendWaitOverrideForTests = previousDirectSendWait;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            NknSignalingTransport.RuntimeUnlockRetryAuthorityPeerProofFreshnessOverrideForTests = previousPeerProofFreshness;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RegularV4PressureSendTimeoutRequestsRecoveryAndDefersRetry()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousControlSendWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        var previousPressureOverride = NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests;
        var previousRecoveryRequest = NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(50);
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromMilliseconds(250);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(25);
        NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests = _ => true;
        var blockedOfferSend = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        NknSignalingTransport? hostTransportForHook = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.regular-pressure-recovery.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.regular-pressure-recovery.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.BeforeSendCoreAsync = async (_, payload, _, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    await blockedOfferSend.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = (transport, reason, sessionId) =>
            {
                if (!ReferenceEquals(transport, hostTransportForHook))
                {
                    return false;
                }

                Assert.Equal("tuna_activation_offer_send_timeout", reason);
                Assert.False(string.IsNullOrWhiteSpace(sessionId));
                return true;
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-regular-pressure-recovery-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            hostTransportForHook = host;
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-regular-pressure-recovery-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_regular_pressure_recovery";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var recoveryRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.RegularNknV4FastToken,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 0,
                AuthorityReason = "regular_v4_startup_local_only_no_ack",
            };
            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessStarted",
                recoveryRequest,
                sessionId,
                transferId,
                "session_liveness_timeout_pending");
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=tuna_acceleration_activation_offer_not_observed;", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_armed;", StringComparison.Ordinal) &&
                           tail.Contains("event=session_recovery_contract_started;", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_control_queue_accepted; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_queue_excluded_from_observed_wait; purpose=offer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_control_send_recovery_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("trigger=observed_send_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("accepted=1", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_armed;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("recovery_reason=tuna_activation_offer_send_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_scheduled=0", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_after_recovery_armed=1", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", logTail, StringComparison.Ordinal);

            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryReceiveResumed,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "receive_stall_recovery_receive_resumed"));

            await Task.Delay(TimeSpan.FromMilliseconds(150), cts.Token);
            var bridgeOnlyTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_regular_v4_recovery_liveness_bridge_lifecycle; lifecycle=receive_resumed", bridgeOnlyTail, StringComparison.Ordinal);
            await WaitUntilAsync(
                () =>
                {
                    var currentTail = ReadOperationalLogTail(logStart);
                    return currentTail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled;", StringComparison.Ordinal) &&
                           currentTail.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));
        }
        finally
        {
            blockedOfferSend.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousControlSendWait;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            NknSignalingTransport.RuntimeUnlockOfferQueueAcceptedPressureOverrideForTests = previousPressureOverride;
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = previousRecoveryRequest;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TransportAccelerationOffer_RuntimeUnlockReceiveStallGateBypassesOnlyAfterRecoverySettles()
    {
        var deferMethod = typeof(NknSignalingTransport).GetMethod(
            "ShouldDeferRuntimeUnlockSoftSettleForReceiveStallBlocker",
            BindingFlags.Static | BindingFlags.NonPublic);
        var bypassMethod = typeof(NknSignalingTransport).GetMethod(
            "ShouldBypassRuntimeUnlockReceiveStallAfterBoundedWait",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(deferMethod);
        Assert.NotNull(bypassMethod);

        Assert.True(Assert.IsType<bool>(deferMethod!.Invoke(
            null,
            new object?[] { "receive_stall_recovery_in_progress", 250L })));
        Assert.False(Assert.IsType<bool>(deferMethod.Invoke(
            null,
            new object?[] { "receive_stall_recovery_in_progress", 0L })));
        Assert.False(Assert.IsType<bool>(deferMethod.Invoke(
            null,
            new object?[] { "receive_stall_recovery_awaiting_receive_proof", 0L })));
        Assert.False(Assert.IsType<bool>(deferMethod.Invoke(
            null,
            new object?[] { "regular_v4_control_feedback_pressure", 250L })));

        Assert.False(Assert.IsType<bool>(bypassMethod!.Invoke(
            null,
            new object?[] { "receive_stall_recovery_in_progress" })));
        Assert.True(Assert.IsType<bool>(bypassMethod.Invoke(
            null,
            new object?[] { "receive_stall_recovery_awaiting_receive_proof" })));
        Assert.False(Assert.IsType<bool>(bypassMethod.Invoke(
            null,
            new object?[] { "regular_v4_control_feedback_pressure" })));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockRetryUsesAuthorityProbeAfterRegularV4SoftSettle()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.regular-v4-soft-settle.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.regular-v4-soft-settle.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: true,
                failedDialAttemptsBeforeSuccess: 0,
                failedListenerAttemptsBeforeSuccess: 100);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-regular-v4-soft-settle-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-regular-v4-soft-settle-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_regular_v4_soft_settle";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var recoveryRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.RegularNknV4FastToken,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 0,
                AuthorityReason = "regular_v4_startup_local_only_no_ack",
            };
            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessStarted",
                recoveryRequest,
                sessionId,
                transferId,
                "session_liveness_timeout_pending");
            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessBridgeRecoveryLifecycle",
                "completed",
                "test_regular_v4_recovery_completed_without_receive_proof");

            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "session_liveness_timeout_pending");
            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                101L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "session_liveness_timeout_pending");

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return (tail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_bridge_completed_authority_probe_allowed;", StringComparison.Ordinal) ||
                            tail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_authority_probe_allowed;", StringComparison.Ordinal)) &&
                           tail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled;", StringComparison.Ordinal) &&
                           tail.Contains("event=session_recovery_contract_retry_authority_granted;", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));

            var scheduledTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain(
                "event=filetransfer_regular_v4_recovery_liveness_receive_proof_observed;",
                scheduledTail,
                StringComparison.Ordinal);
            Assert.Contains(
                "bounded_contract_observed_send_probe",
                scheduledTail,
                StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockSoftSettledPredispatchDeferralSchedulesAuthorityProbe()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.regular-v4-predispatch-soft-settle.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.regular-v4-predispatch-soft-settle.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: true,
                failedDialAttemptsBeforeSuccess: 0,
                failedListenerAttemptsBeforeSuccess: 100);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-regular-v4-predispatch-soft-settle-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-regular-v4-predispatch-soft-settle-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_regular_v4_predispatch_soft_settle";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var recoveryRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.RegularNknV4FastToken,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 0,
                AuthorityReason = "regular_v4_startup_local_only_no_ack",
            };
            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessStarted",
                recoveryRequest,
                sessionId,
                transferId,
                "session_liveness_timeout_pending");
            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessBridgeRecoveryLifecycle",
                "completed",
                "test_regular_v4_recovery_completed_without_receive_proof");
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "session_liveness_timeout_pending");
            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                505L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "session_liveness_timeout_pending");
            InvokePrivateMethod(
                host,
                "MarkRuntimeUnlockRecoveryContractDispatchDeferredForRegularV4ReceiveRecovery",
                sessionId,
                "regular_v4_receive_recovery_pending");

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_bridge_completed_authority_probe_allowed;", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled;", StringComparison.Ordinal) &&
                           tail.Contains("event=session_recovery_contract_retry_authority_granted;", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));

            var scheduledTail = ReadOperationalLogTail(logStart);
            Assert.Contains("reason=bounded_contract_observed_send_probe", scheduledTail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_retry_authority_granted;", scheduledTail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "event=tuna_acceleration_runtime_unlock_retry_after_recovery_deferred_for_regular_v4_receive_proof;",
                scheduledTail[
                    scheduledTail.LastIndexOf(
                        "event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_bridge_completed_authority_probe_allowed;",
                        StringComparison.Ordinal)..],
                StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationRetry_RuntimeUnlockRecoveryContractCapsHighAttemptBackoff()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.contract-backoff.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.contract-backoff.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: true,
                failedDialAttemptsBeforeSuccess: 0,
                failedListenerAttemptsBeforeSuccess: 100);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-contract-backoff-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-contract-backoff-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_contract_backoff";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);

            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "runtime_unlock_offer_answer_timeout");
            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 6);
            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                707L,
                sessionId,
                "runtime_unlock_offer_answer_timeout",
                "tuna_activation_offer_answer_timeout",
                true);
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=session_recovery_contract_retry_backoff_capped;", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_answer_timeout", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));

            var scheduledTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=session_recovery_contract_retry_backoff_capped;", scheduledTail, StringComparison.Ordinal);
            Assert.Contains("reason=runtime_unlock_offer_answer_timeout", scheduledTail, StringComparison.Ordinal);
            Assert.Contains("generic_delay_ms=", scheduledTail, StringComparison.Ordinal);
            Assert.Contains("capped_delay_ms=2000", scheduledTail, StringComparison.Ordinal);
            Assert.Contains("cap_reason=runtime_unlock_listener_rearm_contract", scheduledTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_answer_timeout", scheduledTail, StringComparison.Ordinal);
            Assert.Contains("delay_ms=2000", scheduledTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockRetrySchedulesAfterRegularV4BridgeCompletedSoftSettle()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.regular-v4-completed-soft-settle.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.regular-v4-completed-soft-settle.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: true,
                failedDialAttemptsBeforeSuccess: 0,
                failedListenerAttemptsBeforeSuccess: 100);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-regular-v4-completed-soft-settle-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-regular-v4-completed-soft-settle-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_regular_v4_completed_soft_settle";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var recoveryRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.RegularNknV4FastToken,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 0,
                AuthorityReason = "regular_v4_startup_local_only_no_ack",
            };
            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessStarted",
                recoveryRequest,
                sessionId,
                transferId,
                "session_liveness_timeout_pending");
            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessBridgeRecoveryLifecycle",
                "completed",
                "test_regular_v4_recovery_completed_without_receive_proof");
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "session_liveness_timeout_pending");
            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                202L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "session_liveness_timeout_pending");

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_bridge_completed_authority_probe_allowed;", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled;", StringComparison.Ordinal) &&
                           tail.Contains("event=session_recovery_contract_retry_authority_granted;", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));

            var scheduledTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain(
                "event=filetransfer_regular_v4_recovery_liveness_receive_proof_observed;",
                scheduledTail,
                StringComparison.Ordinal);
            Assert.Contains(
                "event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_bridge_completed_authority_probe_allowed;",
                scheduledTail,
                StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockRetrySchedulesAfterRegularV4StartedRecoveryProbeDelay()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.regular-v4-started-probe-delay.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.regular-v4-started-probe-delay.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: true,
                failedDialAttemptsBeforeSuccess: 0,
                failedListenerAttemptsBeforeSuccess: 100);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-regular-v4-started-probe-delay-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-regular-v4-started-probe-delay-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_regular_v4_started_probe_delay";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var recoveryRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.RegularNknV4FastToken,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 0,
                AuthorityReason = "regular_v4_startup_local_only_no_ack",
            };
            var state = InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessStarted",
                recoveryRequest,
                sessionId,
                transferId,
                "session_liveness_timeout_pending");
            Assert.NotNull(state);
            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessBridgeRecoveryLifecycle",
                "started",
                "test_regular_v4_recovery_started_without_completion");
            var startedProperty = state.GetType().GetProperty(
                "BridgeRecoveryStartedUtcMs",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(startedProperty);
            startedProperty!.SetValue(
                state,
                DateTimeOffset.UtcNow.AddSeconds(-15).ToUnixTimeMilliseconds());

            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "session_liveness_timeout_pending");
            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                303L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "session_liveness_timeout_pending");

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_started_recovery_expired_authority_probe_allowed;", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled;", StringComparison.Ordinal) &&
                           tail.Contains("event=session_recovery_contract_retry_authority_granted;", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));

            var scheduledTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain(
                "event=filetransfer_regular_v4_recovery_liveness_receive_proof_observed;",
                scheduledTail,
                StringComparison.Ordinal);
            Assert.Contains(
                "event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_started_recovery_expired_authority_probe_allowed;",
                scheduledTail,
                StringComparison.Ordinal);
            Assert.Contains("probe_delay_ms=12000", scheduledTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockAuthorityExtendsWaitForStartedRegularV4RecoveryProbe()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousProbeDelay = NknSignalingTransport.RuntimeUnlockRegularV4BridgeRecoveryStartedAuthorityProbeDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRegularV4BridgeRecoveryStartedAuthorityProbeDelayOverrideForTests =
            TimeSpan.FromMilliseconds(150);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.regular-v4-authority-wait.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.regular-v4-authority-wait.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-regular-v4-authority-wait-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-regular-v4-authority-wait-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_regular_v4_authority_wait";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var recoveryRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.RegularNknV4FastToken,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 0,
                AuthorityReason = "regular_v4_startup_local_only_no_ack",
            };
            var state = InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessStarted",
                recoveryRequest,
                sessionId,
                transferId,
                "session_liveness_timeout_pending");
            Assert.NotNull(state);
            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessBridgeRecoveryLifecycle",
                "started",
                "test_regular_v4_recovery_started_without_completion");
            var startedProperty = state.GetType().GetProperty(
                "BridgeRecoveryStartedUtcMs",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(startedProperty);
            startedProperty!.SetValue(
                state,
                DateTimeOffset.UtcNow.AddMilliseconds(-30).ToUnixTimeMilliseconds());

            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                404L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "session_liveness_timeout_pending");
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "session_liveness_timeout_pending");
            InvokePrivateMethod(
                host,
                "MarkRuntimeUnlockRecoveryContractDispatchDeferredForRegularV4ReceiveRecovery",
                sessionId,
                "regular_v4_receive_recovery_pending");
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");
            Assert.True(host.TryGetActiveSessionRecoveryContract(sessionId, out var snapshot));
            Assert.True(snapshot.RetryDispatched);
            Assert.True(snapshot.RetryAuthorityPending);
            Assert.True(snapshot.RetryAuthorityGranted);
            var recoveryState = Assert.IsAssignableFrom<IFileTransferRecoveryLivenessState>(host);
            Assert.True(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out var livenessSnapshot));
            Assert.Equal(FileTransferRecoveryLivenessState.BridgeRecoveryStarted, livenessSnapshot.State);
            var authorityMethod = typeof(NknSignalingTransport).GetMethod(
                "TryGetRuntimeUnlockRetryAuthorityForCurrentOffer",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(authorityMethod);
            var authorityArgs = new object?[] { null, sessionId };
            Assert.True(Assert.IsType<bool>(authorityMethod!.Invoke(host, authorityArgs)));
            var remainingMethod = typeof(NknSignalingTransport).GetMethod(
                "TryGetRegularV4StartedRecoveryAuthorityProbeRemaining",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(remainingMethod);
            var remainingArgs = new object?[] { sessionId, 0L, 0L };
            Assert.True(Assert.IsType<bool>(remainingMethod!.Invoke(host, remainingArgs)));
            var budgetMethod = typeof(NknSignalingTransport).GetMethod(
                "GetFileTransferTunaActivationBridgeRecoveryControlSendWaitBudget",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(budgetMethod);
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                "nonce_authority_wait",
                12,
                405,
                preserveRecoveryState: true);
            var logStart = GetOperationalLogLength();
            var budget = Assert.IsType<TimeSpan>(budgetMethod!.Invoke(
                host,
                new object?[] { "offer", sessionId, TimeSpan.FromMilliseconds(20) }));

            var tail = ReadOperationalLogTail(logStart);
            Assert.True(budget >= TimeSpan.FromMilliseconds(100));
            Assert.Contains(
                "event=tuna_activation_control_send_wait_budget_extended_for_regular_v4_authority_probe;",
                tail,
                StringComparison.Ordinal);
            Assert.Contains("reason=awaiting_bounded_authority_probe_window", tail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRegularV4BridgeRecoveryStartedAuthorityProbeDelayOverrideForTests =
                previousProbeDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RegularV4RecoveryLiveness_BridgeLifecycleRefreshesDeferralDeadlineWithinCap()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            var client = new FakeNknClient("regular.v4.recovery.liveness.deadline.address");
            using var transport = new NknSignalingTransport(
                client,
                options,
                new NknIdentity("regular-v4-recovery-liveness-deadline-id", client.Address),
                NknTunaAccelerationOptions.Disabled,
                accelerationLane: null);

            const string sessionId = "session_regular_v4_recovery_liveness_deadline";
            const string transferId = "transfer_regular_v4_recovery_liveness_deadline";
            var request = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.RegularNknV4FastToken,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 0,
                AuthorityReason = "regular_v4_startup_local_only_no_ack",
            };

            var state = InvokePrivateMethod(
                transport,
                "MarkFileTransferRegularV4RecoveryLivenessStarted",
                request,
                sessionId,
                transferId,
                "session_liveness_timeout_pending");
            Assert.NotNull(state);
            var stateType = state.GetType();
            var createdUtcMs = Assert.IsType<long>(stateType.GetProperty(
                "CreatedUtcMs",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(state));
            var deadlineProperty = stateType.GetProperty(
                "LivenessDeferralDeadlineUtcMs",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(deadlineProperty);
            var shortenedDeadlineUtcMs = createdUtcMs + 10_000;
            deadlineProperty!.SetValue(state, shortenedDeadlineUtcMs);

            var recoveryState = Assert.IsAssignableFrom<IFileTransferRecoveryLivenessState>(transport);
            Assert.True(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out var initialSnapshot));
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(shortenedDeadlineUtcMs), initialSnapshot.LivenessDeferralDeadlineUtc);
            Assert.False(initialSnapshot.ReceiveProofObserved);

            InvokePrivateMethod(
                transport,
                "MarkFileTransferRegularV4RecoveryLivenessBridgeRecoveryLifecycle",
                "completed",
                "test_regular_v4_recovery_completed_without_receive_proof");

            Assert.True(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out var refreshedSnapshot));
            Assert.Equal(FileTransferRecoveryLivenessState.BridgeRecoveryCompletedAwaitingProof, refreshedSnapshot.State);
            Assert.True(refreshedSnapshot.LivenessDeferralDeadlineUtc.ToUnixTimeMilliseconds() > shortenedDeadlineUtcMs);
            Assert.True(refreshedSnapshot.LivenessDeferralDeadlineUtc.ToUnixTimeMilliseconds() <= createdUtcMs + 210_000);
            Assert.False(refreshedSnapshot.ReceiveProofObserved);
            Assert.False(refreshedSnapshot.TerminalRecommended);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RegularV4RecoveryLiveness_StaleBridgeRecoveryStartedBecomesExhausted()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            var client = new FakeNknClient("regular.v4.recovery.liveness.started-expired.address");
            using var transport = new NknSignalingTransport(
                client,
                options,
                new NknIdentity("regular-v4-recovery-liveness-started-expired-id", client.Address),
                NknTunaAccelerationOptions.Disabled,
                accelerationLane: null);

            const string sessionId = "session_regular_v4_recovery_liveness_started_expired";
            const string transferId = "transfer_regular_v4_recovery_liveness_started_expired";
            var request = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.RegularNknV4FastToken,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 0,
                AuthorityReason = "regular_v4_startup_local_only_no_ack",
            };

            var state = InvokePrivateMethod(
                transport,
                "MarkFileTransferRegularV4RecoveryLivenessStarted",
                request,
                sessionId,
                transferId,
                "session_liveness_timeout_pending");
            Assert.NotNull(state);
            InvokePrivateMethod(
                transport,
                "MarkFileTransferRegularV4RecoveryLivenessBridgeRecoveryLifecycle",
                "started",
                "test_regular_v4_recovery_started_without_completion");

            var recoveryState = Assert.IsAssignableFrom<IFileTransferRecoveryLivenessState>(transport);
            Assert.True(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out var activeSnapshot));
            Assert.Equal(FileTransferRecoveryLivenessState.BridgeRecoveryStarted, activeSnapshot.State);
            Assert.False(activeSnapshot.TerminalRecommended);

            var stateType = state.GetType();
            var startedProperty = stateType.GetProperty(
                "BridgeRecoveryStartedUtcMs",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(startedProperty);
            startedProperty!.SetValue(
                state,
                DateTimeOffset.UtcNow.AddSeconds(-60).ToUnixTimeMilliseconds());

            Assert.True(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out var exhaustedSnapshot));
            Assert.Equal(FileTransferRecoveryLivenessState.Exhausted, exhaustedSnapshot.State);
            Assert.True(exhaustedSnapshot.RecoveryExhausted);
            Assert.True(exhaustedSnapshot.TerminalRecommended);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RegularV4RecoveryLiveness_RequiresValidatedFileTransferProofAfterBridgeReceiveResumed()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            var client = new FakeNknClient("regular.v4.recovery.liveness.proof.address");
            using var transport = new NknSignalingTransport(
                client,
                options,
                new NknIdentity("regular-v4-recovery-liveness-proof-id", client.Address),
                NknTunaAccelerationOptions.Disabled,
                accelerationLane: null);

            const string sessionId = "session_regular_v4_recovery_liveness_proof";
            const string transferId = "transfer_regular_v4_recovery_liveness_proof";
            var request = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.RegularNknV4FastToken,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 0,
                AuthorityReason = "regular_v4_startup_local_only_no_ack",
            };

            InvokePrivateMethod(
                transport,
                "MarkFileTransferRegularV4RecoveryLivenessStarted",
                request,
                sessionId,
                transferId,
                "session_liveness_timeout_pending");
            InvokePrivateMethod(
                transport,
                "MarkFileTransferRegularV4RecoveryLivenessBridgeRecoveryLifecycle",
                "started",
                "session_liveness_timeout_pending");
            InvokePrivateMethod(
                transport,
                "MarkFileTransferRegularV4RecoveryLivenessBridgeRecoveryLifecycle",
                "completed",
                "test_regular_v4_recovery_completed_without_receive_proof");
            InvokePrivateMethod(
                transport,
                "MarkFileTransferRegularV4RecoveryLivenessBridgeRecoveryLifecycle",
                "receive_resumed",
                "bridge_raw_receive_resumed");

            var recoveryState = Assert.IsAssignableFrom<IFileTransferRecoveryLivenessState>(transport);
            Assert.True(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out var bridgeOnlySnapshot));
            Assert.Equal(FileTransferRecoveryLivenessState.BridgeRecoveryCompletedAwaitingProof, bridgeOnlySnapshot.State);
            Assert.False(bridgeOnlySnapshot.ReceiveProofObserved);
            Assert.False(bridgeOnlySnapshot.TerminalRecommended);

            InvokePrivateMethod(
                transport,
                "MarkFileTransferRegularV4RecoveryLivenessReceiveProofReceived",
                sessionId,
                transferId,
                "file_transfer_data_frame",
                "control");

            Assert.True(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out var validatedSnapshot));
            Assert.Equal(FileTransferRecoveryLivenessState.ReceiveProofObserved, validatedSnapshot.State);
            Assert.True(validatedSnapshot.ReceiveProofObserved);
            Assert.True(validatedSnapshot.TerminalRecommended);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RegularV4RecoveryLiveness_RetiresWhenSameTransferRouteHintLeavesRegularV4()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            var client = new FakeNknClient("regular.v4.recovery.liveness.route.superseded.address");
            using var transport = new NknSignalingTransport(
                client,
                options,
                new NknIdentity("regular-v4-recovery-liveness-route-superseded-id", client.Address),
                NknTunaAccelerationOptions.Disabled,
                accelerationLane: null);

            const string sessionId = "session_regular_v4_recovery_liveness_route_superseded";
            const string transferId = "transfer_regular_v4_recovery_liveness_route_superseded";
            var request = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.RegularNknV4FastToken,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 0,
                AuthorityReason = "regular_v4_startup_local_only_no_ack",
            };

            InvokePrivateMethod(
                transport,
                "MarkFileTransferRegularV4RecoveryLivenessStarted",
                request,
                sessionId,
                transferId,
                "session_liveness_timeout_pending");

            var recoveryState = Assert.IsAssignableFrom<IFileTransferRecoveryLivenessState>(transport);
            Assert.True(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out var regularSnapshot));
            Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, regularSnapshot.RouteToken);

            InvokePrivateMethod(
                transport,
                "TrackFileTransferRouteHint",
                "unrelated_transfer",
                FileTransferRouteResolver.FileTunaV4Token,
                FileTransferProtocol.ProtocolVersionV4,
                "test_unrelated_route_hint");
            Assert.True(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out _));

            InvokePrivateMethod(
                transport,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.FileTunaV4Token,
                FileTransferProtocol.ProtocolVersionV4,
                "test_same_transfer_route_hint");
            Assert.False(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out _));
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void FallbackLegAuthority_RetiresWhenSameTransferRouteHintLeavesPostTunaFallback()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            var client = new FakeNknClient("fallback.leg.authority.route.superseded.address");
            using var transport = new NknSignalingTransport(
                client,
                options,
                new NknIdentity("fallback-leg-authority-route-superseded-id", client.Address),
                NknTunaAccelerationOptions.Disabled,
                accelerationLane: null);

            const string sessionId = "session_fallback_leg_authority_route_superseded";
            const string transferId = "transfer_fallback_leg_authority_route_superseded";
            var request = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "post_tuna_fallback_state_refresh_failed")
            {
                RouteToken = FileTransferRouteResolver.PostTunaFallbackV6Token,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                LiveRouteEpoch = 3,
                TransferLegGeneration = 2,
                BridgeRecoveryGeneration = 1,
                TransportEpoch = 7,
                CheckpointRequestId = "v6-regular-nkn-state-refresh:1",
                AuthorityReason = "post_tuna_fallback_state_refresh_failed",
            };

            InvokePrivateMethod(
                transport,
                "MarkFileTransferFallbackLegAuthorityStarted",
                request,
                sessionId,
                transferId,
                "post_tuna_fallback_state_refresh_failed");

            var recoveryState = Assert.IsAssignableFrom<IFileTransferRecoveryLivenessState>(transport);
            Assert.True(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out var fallbackSnapshot));
            Assert.Equal(FileTransferRouteResolver.PostTunaFallbackV6Token, fallbackSnapshot.RouteToken);

            InvokePrivateMethod(
                transport,
                "TrackFileTransferRouteHint",
                "unrelated_transfer",
                FileTransferRouteResolver.FileTunaV4Token,
                FileTransferProtocol.ProtocolVersionV4,
                "test_unrelated_route_hint");
            Assert.True(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out _));

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                transport,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.FileTunaV4Token,
                FileTransferProtocol.ProtocolVersionV4,
                "test_same_transfer_tuna_reactivation");

            Assert.False(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out _));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_fallback_leg_authority_superseded_by_route_hint;", logTail, StringComparison.Ordinal);
            Assert.Contains("superseded_by_route=file_tuna_v4", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void FallbackLegAuthority_LateCompletedProofAfterTunaRouteHintIsSupersededWithDirection()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            var client = new FakeNknClient("fallback.leg.authority.late.completed.superseded.address");
            using var transport = new NknSignalingTransport(
                client,
                options,
                new NknIdentity("fallback-leg-authority-late-completed-superseded-id", client.Address),
                NknTunaAccelerationOptions.Disabled,
                accelerationLane: null);

            const string sessionId = "session_fallback_leg_authority_late_completed_superseded";
            const string transferId = "transfer_fallback_leg_authority_late_completed_superseded";
            InvokePrivateMethod(
                transport,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.FileTunaV4Token,
                FileTransferProtocol.ProtocolVersionV4,
                "runtime_unlock_route_commit_accepted");

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                transport,
                "MarkFileTransferFallbackLegAuthorityCompleted",
                sessionId,
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                2,
                3,
                1,
                7L,
                "v6-regular-nkn-state-refresh:1",
                "chunk_batch_committed",
                "receiver_state_control_plane",
                "inbound");

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_fallback_leg_authority_superseded_by_route_hint;", logTail, StringComparison.Ordinal);
            Assert.Contains("direction=inbound", logTail, StringComparison.Ordinal);
            Assert.Contains("superseded_by_route=file_tuna_v4", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_fallback_leg_authority_completed;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void FallbackLegAuthority_PendingNormalToTunaHandoffDoesNotSupersedeFallbackRouteHint()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            var client = new FakeNknClient("fallback.leg.authority.pending.handoff.address");
            using var transport = new NknSignalingTransport(
                client,
                options,
                new NknIdentity("fallback-leg-authority-pending-handoff-id", client.Address),
                NknTunaAccelerationOptions.Disabled,
                accelerationLane: null);

            const string sessionId = "session_fallback_leg_authority_pending_handoff";
            const string transferId = "transfer_fallback_leg_authority_pending_handoff";
            InvokePrivateMethod(
                transport,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_current_fallback_route");

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                transport,
                "TrackFileTransferRouteHintForHandoff",
                transferId,
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna,
                "handoff_broadcast",
                "runtime_unlock");

            var request = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.PostTunaFallbackV6Token,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                LiveRouteEpoch = 2,
                TransferLegGeneration = 3,
                BridgeRecoveryGeneration = 1,
                TransportEpoch = 1,
                CheckpointRequestId = "v6-regular-nkn-state-refresh:2",
                AuthorityReason = "post_tuna_fallback_session_liveness_timeout_pending",
            };

            transport.RequestFileTransferReceiveRecovery(request);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_normal_to_tuna_handoff_route_hint_deferred;", logTail, StringComparison.Ordinal);
            Assert.Contains("source=handoff_broadcast", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_bridge_receive_recovery_request_unsupported;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_fallback_leg_authority_stale_request_ignored;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_fallback_leg_authority_superseded_by_route_hint;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void FallbackLegAuthority_StaleRecoveryRequestIgnoredWhenRouteHintIsFileTunaV4()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            var client = new FakeNknClient("fallback.leg.authority.stale.request.address");
            using var transport = new NknSignalingTransport(
                client,
                options,
                new NknIdentity("fallback-leg-authority-stale-request-id", client.Address),
                NknTunaAccelerationOptions.Disabled,
                accelerationLane: null);

            const string sessionId = "session_fallback_leg_authority_stale_request";
            const string transferId = "transfer_fallback_leg_authority_stale_request";
            InvokePrivateMethod(
                transport,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.FileTunaV4Token,
                FileTransferProtocol.ProtocolVersionV4,
                "test_current_tuna_v4_route");

            var request = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "post_tuna_fallback_state_refresh_failed")
            {
                RouteToken = FileTransferRouteResolver.PostTunaFallbackV6Token,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                LiveRouteEpoch = 1,
                TransferLegGeneration = 2,
                BridgeRecoveryGeneration = 1,
                TransportEpoch = 9,
                CheckpointRequestId = "v6-regular-nkn-state-refresh:stale",
                AuthorityReason = "post_tuna_fallback_state_refresh_failed",
            };

            var logStart = GetOperationalLogLength();
            transport.RequestFileTransferReceiveRecovery(request);

            var recoveryState = Assert.IsAssignableFrom<IFileTransferRecoveryLivenessState>(transport);
            Assert.False(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out _));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_fallback_leg_authority_stale_request_ignored;", logTail, StringComparison.Ordinal);
            Assert.Contains("current_route=file_tuna_v4", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_fallback_leg_authority_started;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_bridge_receive_recovery_request_unsupported;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void FallbackLegAuthority_LivenessRecoveryUsesPostTunaBridgeReason()
    {
        var request = new FileTransferReceiveRecoveryRequest(
            "session_fallback_leg_authority_liveness_reason",
            "transfer_fallback_leg_authority_liveness_reason",
            FileTransferDirection.Outbound,
            "session_liveness_timeout_pending")
        {
            RouteToken = FileTransferRouteResolver.PostTunaFallbackV6Token,
            ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            LiveRouteEpoch = 4,
            TransferLegGeneration = 3,
            BridgeRecoveryGeneration = 2,
            TransportEpoch = 9,
            CheckpointRequestId = "v6-regular-nkn-state-refresh:liveness",
            AuthorityReason = "post_tuna_fallback_session_liveness_timeout_pending",
        };

        var method = typeof(NknSignalingTransport).GetMethod(
            "ResolveFileTransferReceiveStallRecoveryBridgeReason",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var bridgeReason = Assert.IsType<string>(
            method!.Invoke(null, [request, "session_liveness_timeout_pending", true]));

        Assert.Equal("post_tuna_fallback_session_liveness_timeout_pending", bridgeReason);
        Assert.StartsWith("post_tuna_fallback", bridgeReason, StringComparison.Ordinal);
        Assert.NotEqual("session_liveness_timeout_pending", bridgeReason);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void FallbackLegAuthority_BridgeLifecycleRefreshesDeferralDeadlineWithinCap()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            var client = new FakeNknClient("fallback.leg.authority.liveness.deadline.address");
            using var transport = new NknSignalingTransport(
                client,
                options,
                new NknIdentity("fallback-leg-authority-liveness-deadline-id", client.Address),
                NknTunaAccelerationOptions.Disabled,
                accelerationLane: null);

            const string sessionId = "session_fallback_leg_authority_liveness_deadline";
            const string transferId = "transfer_fallback_leg_authority_liveness_deadline";
            var request = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "post_tuna_fallback_state_refresh_failed")
            {
                RouteToken = FileTransferRouteResolver.PostTunaFallbackV6Token,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                LiveRouteEpoch = 4,
                TransferLegGeneration = 3,
                BridgeRecoveryGeneration = 2,
                TransportEpoch = 9,
                CheckpointRequestId = "v6-regular-nkn-state-refresh:deadline",
                AuthorityReason = "post_tuna_fallback_state_refresh_failed",
            };

            var state = InvokePrivateMethod(
                transport,
                "MarkFileTransferFallbackLegAuthorityStarted",
                request,
                sessionId,
                transferId,
                "post_tuna_fallback_state_refresh_failed");
            Assert.NotNull(state);
            var stateType = state.GetType();
            var createdUtcMs = Assert.IsType<long>(stateType.GetProperty(
                "CreatedUtcMs",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(state));
            var deadlineProperty = stateType.GetProperty(
                "LivenessDeferralDeadlineUtcMs",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(deadlineProperty);
            var shortenedDeadlineUtcMs = createdUtcMs + 10_000;
            deadlineProperty!.SetValue(state, shortenedDeadlineUtcMs);

            var recoveryState = Assert.IsAssignableFrom<IFileTransferRecoveryLivenessState>(transport);
            Assert.True(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out var initialSnapshot));
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(shortenedDeadlineUtcMs), initialSnapshot.LivenessDeferralDeadlineUtc);
            Assert.False(initialSnapshot.ReceiveProofObserved);

            InvokePrivateMethod(
                transport,
                "MarkFileTransferFallbackLegAuthorityBridgeRecoveryLifecycle",
                "completed",
                "test_fallback_recovery_completed_without_receive_proof");

            Assert.True(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out var refreshedSnapshot));
            Assert.Equal(FileTransferRecoveryLivenessState.BridgeRecoveryCompletedAwaitingProof, refreshedSnapshot.State);
            Assert.True(refreshedSnapshot.LivenessDeferralDeadlineUtc.ToUnixTimeMilliseconds() > shortenedDeadlineUtcMs);
            Assert.True(refreshedSnapshot.LivenessDeferralDeadlineUtc.ToUnixTimeMilliseconds() <= createdUtcMs + 210_000);
            Assert.False(refreshedSnapshot.ReceiveProofObserved);
            Assert.False(refreshedSnapshot.TerminalRecommended);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void FallbackLegAuthority_V6EpochProofCompletesAuthorityAndClearsRecoverySnapshot()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            var client = new FakeNknClient("fallback.leg.authority.v6-epoch-complete.address");
            using var transport = new NknSignalingTransport(
                client,
                options,
                new NknIdentity("fallback-leg-authority-v6-epoch-complete-id", client.Address),
                NknTunaAccelerationOptions.Disabled,
                accelerationLane: null);

            const string sessionId = "session_fallback_leg_authority_v6_epoch_complete";
            const string transferId = "transfer_fallback_leg_authority_v6_epoch_complete";
            var request = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "post_tuna_fallback_state_refresh_failed")
            {
                RouteToken = FileTransferRouteResolver.PostTunaFallbackV6Token,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                LiveRouteEpoch = 5,
                TransferLegGeneration = 4,
                BridgeRecoveryGeneration = 2,
                TransportEpoch = 11,
                CheckpointRequestId = "v6-regular-nkn-state-refresh:epoch-complete",
                AuthorityReason = "post_tuna_fallback_state_refresh_failed",
            };

            InvokePrivateMethod(
                transport,
                "MarkFileTransferFallbackLegAuthorityStarted",
                request,
                sessionId,
                transferId,
                "post_tuna_fallback_state_refresh_failed");
            InvokePrivateMethod(
                transport,
                "MarkFileTransferFallbackNknProofPending",
                "post_tuna_fallback_state_refresh_failed",
                sessionId,
                NknAccelerationLaneKind.File,
                request);

            var recoveryState = Assert.IsAssignableFrom<IFileTransferRecoveryLivenessState>(transport);
            Assert.True(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out var activeSnapshot));
            Assert.Equal(FileTransferRecoveryLivenessState.AuthorityActive, activeSnapshot.State);
            Assert.Equal(FileTransferRouteResolver.PostTunaFallbackV6Token, activeSnapshot.RouteToken);

            var epoch = new FileTransferV6TransportEpochSnapshot(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                11,
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.Tuna,
                FileTransferTransportKind.RegularNkn,
                V6TransportEpochState.Recovered,
                "receiver_state_sparse_runtime",
                IsUnresolved: false);
            var logStart = GetOperationalLogLength();

            var completed = Assert.IsType<bool>(InvokePrivateMethod(
                transport,
                "CompleteFileTransferFallbackNknProofFromV6Epoch",
                epoch));

            Assert.True(completed);
            Assert.False(recoveryState.TryGetActiveFileTransferRecoveryLivenessSnapshot(sessionId, out _));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_fallback_nkn_proof_observed;", logTail, StringComparison.Ordinal);
            Assert.Contains("proof=filetransfer_v6_epoch_recovered", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_fallback_leg_authority_completed;", logTail, StringComparison.Ordinal);
            Assert.Contains("transfer_id=", logTail, StringComparison.Ordinal);
            Assert.Contains("leg_generation=4", logTail, StringComparison.Ordinal);
            Assert.Contains("transport_epoch=11", logTail, StringComparison.Ordinal);
            Assert.Contains("checkpoint_request_id=v6-regular-nkn-state-refresh:epoch-complete", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FallbackLegAuthority_CurrentReceiveProofAllowsRuntimeUnlockRetryDespiteUnresolvedEpoch()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.fallback.leg.authority.runtime-unlock.retry.address");
            var helperClient = new FakeNknClient("helper.fallback.leg.authority.runtime-unlock.retry.address");
            using var transport = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-fallback-leg-authority-runtime-unlock-retry-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                accelerationLane: null);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-fallback-leg-authority-runtime-unlock-retry-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                accelerationLane: null);

            var sessionId = await ApproveNknSessionAsync(
                transport,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_fallback_leg_authority_runtime_unlock_retry";
            _ = await transport.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            InvokePrivateMethod(
                transport,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");

            var request = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "post_tuna_fallback_state_refresh_failed")
            {
                RouteToken = FileTransferRouteResolver.PostTunaFallbackV6Token,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                LiveRouteEpoch = 2,
                TransferLegGeneration = 3,
                BridgeRecoveryGeneration = 1,
                TransportEpoch = 2,
                CheckpointRequestId = "v6-regular-nkn-state-refresh:current",
                AuthorityReason = "post_tuna_fallback_state_refresh_failed",
            };
            InvokePrivateMethod(
                transport,
                "MarkFileTransferFallbackLegAuthorityStarted",
                request,
                sessionId,
                transferId,
                "post_tuna_fallback_state_refresh_failed");

            var pendingEpoch = new FileTransferV6TransportEpochSnapshot(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                2,
                FileTransferTransportHandoffKind.RegularNknRecovery,
                FileTransferTransportKind.Tuna,
                FileTransferTransportKind.RegularNkn,
                V6TransportEpochState.TargetProofPending,
                "transport_recovered_unproven",
                IsUnresolved: true);

            var blocked = Assert.IsType<bool>(InvokePrivateMethod(
                transport,
                "ShouldAllowAccelerationRetryDespiteUnresolvedV6Epoch",
                pendingEpoch,
                "runtime_unlock_offer_send_not_observed",
                "preflight"));
            Assert.False(blocked);

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                transport,
                "MarkFileTransferFallbackLegAuthorityBridgeRecoveryLifecycle",
                "receive_resumed",
                "test_receive_resumed");

            var allowed = Assert.IsType<bool>(InvokePrivateMethod(
                transport,
                "ShouldAllowAccelerationRetryDespiteUnresolvedV6Epoch",
                pendingEpoch,
                "runtime_unlock_offer_send_not_observed",
                "preflight"));

            Assert.True(allowed);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_fallback_leg_authority_bridge_lifecycle; lifecycle=receive_resumed", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retry_allowed_post_tuna_fallback_current_authority;", logTail, StringComparison.Ordinal);
            Assert.Contains("proof=fallback_authority_receive_proof", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_LateObservedRuntimeUnlockOfferStillActivatesTransfer()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousControlSendWait = NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests;
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = TimeSpan.FromMilliseconds(50);
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromSeconds(2);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(25);
        var blockedOfferSend = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.late-offer.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.late-offer.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var blockOfferSends = 1;
            var offerSendAttempts = 0;
            hostClient.BeforeSendCoreAsync = async (_, payload, _, ct) =>
            {
                if (Volatile.Read(ref blockOfferSends) != 0 &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationOffer)
                {
                    Interlocked.Increment(ref offerSendAttempts);
                    await blockedOfferSend.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-late-offer-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-late-offer-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            var dataSession = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_late_offer",
                cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => Volatile.Read(ref offerSendAttempts) >= 1,
                TimeSpan.FromSeconds(3));
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_activation_offer_not_observed;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            var unobservedOfferLog = ReadOperationalLogTail(logStart);
            Assert.Contains("pause_deferred=1", unobservedOfferLog, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;", unobservedOfferLog, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_tuna_activation_negotiation_regular_nkn_pause_retained;", unobservedOfferLog, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_tuna_activation_negotiation_regular_nkn_resumed;", unobservedOfferLog, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_activation_failed_regular_v4_resumed;", unobservedOfferLog, StringComparison.Ordinal);
            await WaitUntilAsync(
                () =>
                {
                    var currentTail = ReadOperationalLogTail(logStart);
                    return currentTail.Contains("event=tuna_acceleration_outbound_offer_retired; reason=offer_send_not_observed", StringComparison.Ordinal) &&
                           currentTail.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));

            await WaitUntilAsync(
                () => Volatile.Read(ref offerSendAttempts) >= 2,
                TimeSpan.FromSeconds(3));
            Volatile.Write(ref blockOfferSends, 0);
            blockedOfferSend.TrySetResult(null);

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.Reason == "tuna_activation_negotiating"),
                TimeSpan.FromSeconds(3));

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_negotiated;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(6));
            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    e.TargetTransport == FileTransferTransportKind.Tuna),
                TimeSpan.FromSeconds(3));

            var logTail = ReadOperationalLogTail(logStart);
            var positiveLogTail = logTail + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=tuna_acceleration_outbound_offer_retired; reason=offer_send_not_observed", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", positiveLogTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retired_offer_answer_accepted;", logTail, StringComparison.Ordinal);
            Assert.True(
                Volatile.Read(ref offerSendAttempts) >= 2 ||
                positiveLogTail.Contains("event=tuna_acceleration_offer_replay_sent;", StringComparison.Ordinal),
                "Expected either a replay send attempt or logged replay evidence before negotiation completed.");
            Assert.Contains("event=tuna_acceleration_negotiated;", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_filetransfer_handoff_requested;", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("handoff_kind=normal_to_tuna_activation", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_resumed;", positiveLogTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedOfferSend.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.AccelerationControlBulkBypassWaitOverrideForTests = previousControlSendWait;
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockRetrySoftSettlesDuringActivePostTunaFallbackRepair()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.post-fallback-soft-settle.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.post-fallback-soft-settle.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: true,
                failedDialAttemptsBeforeSuccess: 0,
                failedListenerAttemptsBeforeSuccess: 100);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-post-fallback-soft-settle-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-post-fallback-soft-settle-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_post_fallback_soft_settle";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            Assert.True(Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "StartTunaFallbackProofIfNeeded",
                "header_switch_off",
                sessionId,
                NknAccelerationLaneKind.File)));
            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(host);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    77,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.WaitingForTargetTransport,
                    "header_switch_off",
                    IsUnresolved: true));

            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "tuna_activation_offer_send_timeout");
            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                99L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "tuna_activation_offer_send_timeout");

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_fallback_repair_soft_settle_deferred;", StringComparison.Ordinal) ||
                           tail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_deferred_for_fallback_repair;", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockRetrySoftSettlesWhenPostTunaFallbackHasReceiverProof()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.FromMilliseconds(25);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.post-fallback-proof-soft-settle.address");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.post-fallback-proof-soft-settle.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: true,
                failedDialAttemptsBeforeSuccess: 0,
                failedListenerAttemptsBeforeSuccess: 100);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-post-fallback-proof-soft-settle-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-post-fallback-proof-soft-settle-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_post_fallback_proof_soft_settle";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            Assert.True(Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "StartTunaFallbackProofIfNeeded",
                "header_switch_off",
                sessionId,
                NknAccelerationLaneKind.File)));
            InvokePrivateMethod(
                host,
                "RecordPostTunaFallbackReceiverFrontierProofHint",
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = 77,
                    ContiguousCommittedChunkIndex = 12,
                    DurableReceivedHighestChunkIndex = 12,
                    CreditUntilChunkIndexExclusive = 32,
                },
                "received",
                sessionId);

            var logStart = GetOperationalLogLength();
            var fallbackPendingAllowed = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldAllowAccelerationRetryDespiteFallbackControlProofPending",
                sessionId,
                "header_switch_off",
                NknAccelerationLaneKind.File,
                "runtime_unlock_offer_send_not_observed",
                "test"));
            Assert.True(fallbackPendingAllowed);

            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoveryStarted", "tuna_activation_offer_send_timeout");
            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                101L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "tuna_activation_offer_send_timeout");
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled;", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains(
                "event=tuna_acceleration_retry_allowed_post_tuna_fallback_current_authority;",
                logTail,
                StringComparison.Ordinal);
            Assert.Contains("proof=receiver_state", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_blocked_fallback_control_unproven;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationPayerIntent_UsesBulkBypassWhenControlSendIsBlocked()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        var blockedControlPayerIntent = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.intent-bypass.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.intent-bypass.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var blockedIntentCount = 0;
            hostClient.BeforeSendAsync = async (destination, payload, ct) =>
            {
                if (string.Equals(destination, helperClient.ConnectedAddress, StringComparison.Ordinal) &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationPayerIntent)
                {
                    Interlocked.Increment(ref blockedIntentCount);
                    await blockedControlPayerIntent.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-intent-bypass-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-intent-bypass-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_payer_intent_bypass",
                cts.Token);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(6));

            Assert.True(Volatile.Read(ref blockedIntentCount) > 0);
            Assert.True(hostLane.EnsureListenerCalls > 0);
            Assert.True(helperLane.StartDialerCalls > 0);
            var logTail = string.Empty;
            await WaitUntilAsync(
                () =>
                {
                    var currentTail = ReadOperationalLogTail(logStart);
                    var observed =
                        currentTail.Contains("event=tuna_acceleration_control_bulk_bypass_started; purpose=payer_intent", StringComparison.Ordinal) &&
                        currentTail.Contains("event=tuna_acceleration_control_bulk_bypass_sent; purpose=payer_intent", StringComparison.Ordinal) &&
                        currentTail.Contains("event=tuna_acceleration_payer_intent_queued;", StringComparison.Ordinal) &&
                        currentTail.Contains("event=tuna_acceleration_negotiated;", StringComparison.Ordinal);
                    if (observed)
                    {
                        logTail = currentTail;
                    }

                    return observed;
                },
                TimeSpan.FromSeconds(3));
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_started; purpose=payer_intent", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_sent; purpose=payer_intent", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_payer_intent_queued;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedControlPayerIntent.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswer_UsesBulkBypassWhenControlSendIsBlocked()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        var blockedControlAnswer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.answer-bypass.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.answer-bypass.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var blockedAnswerCount = 0;
            helperClient.BeforeSendAsync = async (destination, payload, ct) =>
            {
                if (string.Equals(destination, hostClient.ConnectedAddress, StringComparison.Ordinal) &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationAnswer)
                {
                    Interlocked.Increment(ref blockedAnswerCount);
                    await blockedControlAnswer.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-answer-bypass-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-answer-bypass-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_answer_bypass",
                cts.Token);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(6));

            Assert.True(Volatile.Read(ref blockedAnswerCount) > 0);
            Assert.True(hostLane.EnsureListenerCalls > 0);
            Assert.True(helperLane.StartDialerCalls > 0);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_started; purpose=answer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_control_bulk_bypass_sent; purpose=answer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_answer_received_raw;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_answer_ack_sent;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_answer_ack_received;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedControlAnswer.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswer_RuntimeUnlockReplaysPastInitialDroppedCopies()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousAnswerReplayDelay = NknSignalingTransport.AccelerationAnswerReplayDelayOverrideForTests;
        var previousAnswerReplayAttempts = NknSignalingTransport.AccelerationAnswerReplayAttemptsOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationAnswerReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(20);
        NknSignalingTransport.AccelerationAnswerReplayAttemptsOverrideForTests = 8;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.answer-replay.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.answer-replay.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var droppedAnswerCopies = 0;
            helperClient.ShouldDeliverSendAsync = (_, payload, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationAnswer &&
                    Interlocked.Increment(ref droppedAnswerCopies) <= 12)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-answer-replay-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-answer-replay-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_answer_replay",
                cts.Token);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(6));

            Assert.True(Volatile.Read(ref droppedAnswerCopies) >= 12);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_answer_replay_", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_answer_received_raw;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_answer_ack_received;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.AccelerationAnswerReplayAttemptsOverrideForTests = previousAnswerReplayAttempts;
            NknSignalingTransport.AccelerationAnswerReplayDelayOverrideForTests = previousAnswerReplayDelay;
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswerAck_DefersDialerFileTransferHandoffUntilAck()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        var blockedAnswerAck = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.ack-gate.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.ack-gate.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var blockedAckCount = 0;
            hostClient.BeforeSendAsync = async (_, payload, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationAnswerAck)
                {
                    Interlocked.Increment(ref blockedAckCount);
                    await blockedAnswerAck.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-ack-gate-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-ack-gate-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_ack_gate_host",
                cts.Token);
            var helperDataSession = await helper.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_ack_gate_helper",
                cts.Token);
            var helperAvailabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            helperDataSession.AvailabilityChanged += (_, e) => helperAvailabilityEvents.Enqueue(e);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && Volatile.Read(ref blockedAckCount) > 0,
                TimeSpan.FromSeconds(6));
            Assert.False(helper.IsAccelerationAvailableForTests);
            Assert.DoesNotContain(
                helperAvailabilityEvents,
                e => e.IsAvailable &&
                     e.RequiresResumeRequest &&
                     e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                     e.TargetTransport == FileTransferTransportKind.Tuna);

            blockedAnswerAck.SetResult(null);

            await WaitUntilAsync(
                () => helper.IsAccelerationAvailableForTests &&
                      helperAvailabilityEvents.Any(e =>
                          e.IsAvailable &&
                          e.RequiresResumeRequest &&
                          e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                          e.TargetTransport == FileTransferTransportKind.Tuna),
                TimeSpan.FromSeconds(6));

            var logTail = ReadOperationalLogTail(logStart);
            var positiveLogTail = logTail + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=tuna_acceleration_answer_ack_pending;", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_answer_ack_received;", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("reason=tuna_activation_answer_ack", positiveLogTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedAnswerAck.TrySetResult(null);
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswerAck_RuntimeUnlockTimeoutSchedulesRuntimeUnlockRetry()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousAnswerAckTimeout = NknSignalingTransport.AccelerationAnswerAckTimeoutOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.AccelerationAnswerAckTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
        var blockedAnswerAck = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.activation.ack-timeout.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var helperClient = new FakeNknClient("helper.tuna.file.activation.ack-timeout.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            hostClient.BeforeSendAsync = async (_, payload, ct) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationAnswerAck)
                {
                    await blockedAnswerAck.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-file-activation-ack-timeout-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-activation-ack-timeout-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_ack_timeout_host",
                cts.Token);
            _ = await helper.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_activation_ack_timeout_helper",
                cts.Token);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=tuna_acceleration_answer_ack_timeout;", StringComparison.Ordinal) &&
                           tail.Contains("retry_reason=runtime_unlock_answer_ack_timeout", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_answer_ack_timeout", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(6));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("trigger=runtime_unlock", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_reason=runtime_unlock_answer_ack_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_answer_ack_timeout", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=answer_ack_timeout", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedAnswerAck.TrySetResult(null);
            NknSignalingTransport.AccelerationAnswerAckTimeoutOverrideForTests = previousAnswerAckTimeout;
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockPendingAnswerAckOwnsGenerationUntilAckOrTimeout()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.offer.pending-answer-ack-owner.address");
            var helperClient = new FakeNknClient("helper.tuna.offer.pending-answer-ack-owner.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: false,
                failedDialAttemptsBeforeSuccess: 0,
                supportedLanes: NknAccelerationLaneKind.File);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-offer-pending-answer-ack-owner-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-offer-pending-answer-ack-owner-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var originalOffer = CreateOfferPayload(
                sessionId,
                "af11223344556677889900aabbccddee",
                supportedLanes: new[] { "file" },
                payerDecisionId: 41L);
            originalOffer.Trigger = "runtime_unlock";
            originalOffer.SenderRole = "helpee";
            var pendingGeneration = Assert.IsType<long>(InvokePrivateMethod(
                host,
                "BeginPendingAccelerationAnswerAck",
                originalOffer,
                NknAccelerationLaneKind.File));
            var logStart = GetOperationalLogLength();

            var duplicateOffer = CreateOfferPayload(
                sessionId,
                originalOffer.Nonce,
                supportedLanes: new[] { "file" },
                payerDecisionId: originalOffer.PayerDecisionId);
            duplicateOffer.Trigger = "runtime_unlock";
            duplicateOffer.SenderRole = "helpee";
            var duplicateTask = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(
                host,
                "HandleTransportAccelerationOfferAsync",
                helperClient.Address,
                duplicateOffer,
                "duplicate-runtime-unlock-offer",
                cts.Token));
            await duplicateTask.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            var freshOffer = CreateOfferPayload(
                sessionId,
                "af99887766554433221100ffeeddccbb",
                supportedLanes: new[] { "file" },
                payerDecisionId: 42L);
            freshOffer.Trigger = "runtime_unlock";
            freshOffer.SenderRole = "helpee";
            var freshTask = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(
                host,
                "HandleTransportAccelerationOfferAsync",
                helperClient.Address,
                freshOffer,
                "fresh-runtime-unlock-offer",
                cts.Token));
            await freshTask.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            Assert.Equal(pendingGeneration, Assert.IsType<long>(GetPrivateField(host, "pendingAccelerationAnswerAckGeneration")));
            Assert.Equal(originalOffer.Nonce, Assert.IsType<string>(GetPrivateField(host, "pendingAccelerationAnswerAckNonce")));
            Assert.Equal(originalOffer.PayerDecisionId, Assert.IsType<long>(GetPrivateField(host, "pendingAccelerationAnswerAckPayerDecisionId")));
            Assert.Equal(0, hostLane.StartDialerCalls);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_offer_duplicate_pending_answer_ack;", logTail, StringComparison.Ordinal);
            Assert.Contains("action=answer_replay", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_offer_rejected_pending_answer_ack;", logTail, StringComparison.Ordinal);
            Assert.Contains("reject_reason=answer_ack_pending", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_answer_sent; accepted=0; reason=answer_ack_pending", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataSession_TunaAcceptedBeforeSessionReplaysPendingNormalToTunaActivationHandoffWithoutLegacyV6Route()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.pending.activation.address");
            var helperClient = new FakeNknClient("helper.tuna.file.pending.activation.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-pending-activation-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-pending-activation-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);

            var logStart = GetOperationalLogLength();
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_pending_handoff_recorded;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_tuna_pending_activation_handoff", cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    e.TargetTransport == FileTransferTransportKind.Tuna),
                TimeSpan.FromSeconds(2));

            var replayTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_pending_handoff_replayed;", replayTail, StringComparison.Ordinal);
            Assert.Contains("handoff_kind=normal_to_tuna_activation", replayTail, StringComparison.Ordinal);
            Assert.Contains("target_transport=tuna", replayTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_pending_handoff_suppressed_for_route;", replayTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataSession_TunaFallbackBeforeSessionReplaysPendingRegularNknHandoff()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.pending.fallback.address");
            var helperClient = new FakeNknClient("helper.tuna.file.pending.fallback.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-pending-fallback-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-pending-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);

            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            var logStart = GetOperationalLogLength();
            fakeLane.SetAvailable(false, "remote_closed");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_pending_handoff_recorded;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            const string transferId = "transfer_tuna_pending_fallback_handoff";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                    e.TargetTransport == FileTransferTransportKind.RegularNkn),
                TimeSpan.FromSeconds(2));

            var replayTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_pending_handoff_replayed;", replayTail, StringComparison.Ordinal);
            Assert.Contains("handoff_kind=tuna_to_normal_fallback", replayTail, StringComparison.Ordinal);
            Assert.Contains("target_transport=regular_nkn", replayTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataSession_ActiveTunaWithoutPendingIntentSynthesizesV4ActivationHandoffWithoutLegacyV6Route()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.synthesized.activation.address");
            var helperClient = new FakeNknClient("helper.tuna.file.synthesized.activation.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-synthesized-activation-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-synthesized-activation-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);

            SetPrivateField(helper, "accelerationSessionId", sessionId);
            SetPrivateField(helper, "accelerationNegotiatedLanes", NknAccelerationLaneKind.File);

            var logStart = GetOperationalLogLength();
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, "transfer_tuna_synthesized_activation_handoff", cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    e.TargetTransport == FileTransferTransportKind.Tuna),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_active_tuna_handoff_synthesized;", logTail, StringComparison.Ordinal);
            Assert.Contains("handoff_kind=normal_to_tuna_activation", logTail, StringComparison.Ordinal);
            Assert.Contains("target_transport=tuna", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_pending_handoff_replayed;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataSession_ActiveTunaV4RouteSuppressesStalePendingRegularNknHandoff()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.stale.pending.regular.address");
            var helperClient = new FakeNknClient("helper.tuna.file.stale.pending.regular.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-stale-pending-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-stale-pending-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);

            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            var logStart = GetOperationalLogLength();
            const string transferId = "transfer_tuna_v4_suppresses_stale_pending_regular";
            InvokePrivateMethod(
                helper,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.FileTunaV4Token,
                FileTransferProtocol.ProtocolVersionV4,
                "test_file_tuna_v4_new_transfer");

            Assert.True(Assert.IsType<bool>(InvokePrivateMethod(
                helper,
                "TryRecordPendingFileTransferV6Handoff",
                sessionId,
                "transport_recovered_unproven",
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn,
                "test_stale_after_completed_transfer")));

            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            await Task.Delay(150, cts.Token);

            Assert.DoesNotContain(
                availabilityEvents,
                e => !e.IsAvailable &&
                     e.RequiresResumeRequest &&
                     e.TargetTransport == FileTransferTransportKind.RegularNkn);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_pending_handoff_suppressed_for_active_tuna_route;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_pending_handoff_replayed;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataSession_ActiveTunaV4RouteSuppressesImmediateStaleRegularNknAvailability()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.stale.immediate.regular.address");
            var helperClient = new FakeNknClient("helper.tuna.file.stale.immediate.regular.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-stale-immediate-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-stale-immediate-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);

            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            var logStart = GetOperationalLogLength();
            const string transferId = "transfer_tuna_v4_suppresses_stale_immediate_regular";
            InvokePrivateMethod(
                helper,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.FileTunaV4Token,
                FileTransferProtocol.ProtocolVersionV4,
                "test_file_tuna_v4_reactivated");

            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            InvokePrivateMethod(
                helper,
                "SetFileTransferDataSessionsAvailability",
                false,
                "transport_recovered_unproven",
                true,
                FileTransferTransportHandoffKind.RegularNknRecovery,
                FileTransferTransportKind.RegularNkn);

            await Task.Delay(150, cts.Token);

            Assert.True(dataSession.IsAvailable);
            Assert.DoesNotContain(
                availabilityEvents,
                e => !e.IsAvailable &&
                     e.RequiresResumeRequest &&
                     e.TargetTransport == FileTransferTransportKind.RegularNkn);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_availability_suppressed_for_active_tuna_route;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_data_session_availability_invoking;", logTail, StringComparison.Ordinal);

            InvokePrivateMethod(
                helper,
                "SetFileTransferDataSessionsAvailability",
                false,
                "receive_stall_recovery",
                true,
                FileTransferTransportHandoffKind.RegularNknRecovery,
                FileTransferTransportKind.RegularNkn);

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.Reason == "receive_stall_recovery" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.RegularNknRecovery &&
                    e.TargetTransport == FileTransferTransportKind.RegularNkn),
                TimeSpan.FromSeconds(2));

            logTail = ReadOperationalLogTail(logStart);
            Assert.Contains(
                "event=filetransfer_v6_availability_active_tuna_suppression_bypassed_for_receive_stall;",
                logTail,
                StringComparison.Ordinal);

            SetPrivateField(helper, "accelerationUserStoppedSessionId", sessionId);
            InvokePrivateMethod(
                helper,
                "SetFileTransferDataSessionsAvailability",
                false,
                "sidecar_remote_closed",
                true,
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn);

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                    e.TargetTransport == FileTransferTransportKind.RegularNkn),
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataSession_ActiveTunaV4RouteAllowsExplicitPostTunaRecoveredAvailability()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.explicit.fallback.recovered.address");
            var helperClient = new FakeNknClient("helper.tuna.file.explicit.fallback.recovered.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-explicit-fallback-recovered-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-explicit-fallback-recovered-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);

            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            var logStart = GetOperationalLogLength();
            const string transferId = "transfer_tuna_v4_allows_explicit_fallback_recovered";
            InvokePrivateMethod(
                helper,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.FileTunaV4Token,
                FileTransferProtocol.ProtocolVersionV4,
                "runtime_unlock_route_commit_accepted");

            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            InvokePrivateMethod(
                helper,
                "SetFileTransferDataSessionsAvailability",
                false,
                "transport_recovered",
                true,
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn);

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.Reason == "transport_recovered" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                    e.TargetTransport == FileTransferTransportKind.RegularNkn),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains(
                "event=filetransfer_v6_availability_active_tuna_suppression_bypassed_for_fallback_recovery;",
                logTail,
                StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_availability_suppressed_for_active_tuna_route;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataFrame_ActiveSessionCanMoveBackToTunaAfterExplicitReenable()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.reenable.address");
            var helperClient = new FakeNknClient("helper.tuna.file.reenable.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-reenable-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-reenable-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            var rawNknDataFrames = new ConcurrentQueue<NknIncomingMessage>();
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.FileTransferDataFrame)
                {
                    rawNknDataFrames.Enqueue(e);
                }
            };

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_reenable";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);
            await WaitUntilAsync(() => fakeLane.Sent.Count == 1, TimeSpan.FromSeconds(2));

            await ((ITransportAccelerationControl)helper).StopAccelerationAsync("header_switch_off", cts.Token);
            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 1,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);
            await WaitUntilAsync(() => rawNknDataFrames.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Single(fakeLane.Sent);

            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 2,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);

            await WaitUntilAsync(() => fakeLane.Sent.Count == 2, TimeSpan.FromSeconds(2));
            Assert.Single(rawNknDataFrames);
            Assert.All(fakeLane.Sent, sent => Assert.Equal(NknBridgeChannel.Bulk, sent.Lane));
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationStatus_FollowsNegotiatedHealthyLane()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.status.address");
            var helperClient = new FakeNknClient("helper.tuna.status.address");
            var fakeLane = new FakeNknAccelerationLane();
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-status-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-status-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            var status = (ITransportAccelerationStatus)helper;
            var observedStates = new ConcurrentQueue<bool>();
            status.TransportAccelerationStateChanged += (_, e) => observedStates.Enqueue(e.IsActive);

            Assert.False(status.IsTransportAccelerationActive);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);

            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            Assert.True(status.IsTransportAccelerationActive);
            Assert.Contains(true, observedStates);

            fakeLane.SetAvailable(false, "test_down");

            await WaitUntilAsync(() => !status.IsTransportAccelerationActive, TimeSpan.FromSeconds(2));
            Assert.Contains(false, observedStates);
            Assert.Equal("sidecar_test_down", status.TransportAccelerationStatusReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RetriesAfterTransientSidecarUnavailable()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.retry.address");
            var helperClient = new FakeNknClient("helper.tuna.retry.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: true,
                failedDialAttemptsBeforeSuccess: 0,
                deferSupportedLanesUntilAvailable: true);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 1);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-retry-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-retry-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(8));

            Assert.True(hostLane.EnsureListenerCalls > 0);
            Assert.Equal(2, helperLane.StartDialerCalls);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, host.AccelerationNegotiatedLanesForTests);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, helper.AccelerationNegotiatedLanesForTests);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationRetry_IsSuppressedOnlyForFallbackV6Epoch()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.retry.v6-epoch.address");
            var helperClient = new FakeNknClient("helper.tuna.retry.v6-epoch.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: true,
                failedDialAttemptsBeforeSuccess: 0,
                failedListenerAttemptsBeforeSuccess: 100);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-retry-v6-epoch-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-retry-v6-epoch-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            const string transferId = "transfer_v6_epoch_blocks_retry";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(host);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    41,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.TargetProofPending,
                    "sidecar_byte_cap_reached",
                    IsUnresolved: true));

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationRetry", "phase5_v6_epoch_unresolved");
            var blockedTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_retry_blocked_v6_epoch_unresolved;", blockedTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=phase5_v6_epoch_unresolved", blockedTail, StringComparison.Ordinal);

            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var runtimeUnlockLogStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationRetry", "runtime_unlock_offer_answer_timeout");
            var runtimeUnlockBlockedTail = ReadOperationalLogTail(runtimeUnlockLogStart);
            Assert.Contains("event=tuna_acceleration_retry_blocked_v6_epoch_unresolved;", runtimeUnlockBlockedTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_allowed_post_tuna_fallback_unresolved;", runtimeUnlockBlockedTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_answer_timeout", runtimeUnlockBlockedTail, StringComparison.Ordinal);

            Assert.True(Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "StartTunaFallbackProofIfNeeded",
                "header_switch_off",
                sessionId,
                NknAccelerationLaneKind.File)));
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    41,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.WaitingForTargetTransport,
                    "sidecar_byte_cap_reached",
                    IsUnresolved: true));
            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var activeRepairLogStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationRetry", "runtime_unlock_offer_answer_timeout");
            var activeRepairTail = ReadOperationalLogTail(activeRepairLogStart);
            Assert.Contains("event=tuna_acceleration_retry_blocked_v6_epoch_unresolved;", activeRepairTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retry_blocked_post_tuna_fallback_unresolved;", activeRepairTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_answer_timeout", activeRepairTail, StringComparison.Ordinal);

            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    41,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Terminal,
                    "transfer_terminal",
                    IsUnresolved: false));
            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var retryLogStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationRetry", "phase5_v6_epoch_terminal");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(retryLogStart).Contains("event=tuna_acceleration_retry_scheduled; reason=phase5_v6_epoch_terminal", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    "transfer_regular_nkn_recovery_allows_retry",
                    FileTransferDirection.Inbound,
                    42,
                    FileTransferTransportHandoffKind.RegularNknRecovery,
                    FileTransferTransportKind.RegularNkn,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.FrontierRepairOnly,
                    "receive_stall_recovery",
                    IsUnresolved: true));
            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var regularRecoveryLogStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationRetry", "phase5_regular_nkn_recovery_unresolved");
            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(regularRecoveryLogStart);
                    return tail.Contains("event=tuna_acceleration_retry_allowed_regular_nkn_recovery_unresolved;", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_retry_scheduled; reason=phase5_regular_nkn_recovery_unresolved", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationRetry_RuntimeUnlockAuthorityCarriesDelayedPostFallbackRegularNknRecovery()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.retry.post-fallback-authority.address");
            var helperClient = new FakeNknClient("helper.tuna.retry.post-fallback-authority.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-retry-post-fallback-authority-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-retry-post-fallback-authority-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            const string transferId = "transfer_runtime_unlock_post_fallback_authority";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);

            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                17L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "post_tuna_fallback_tuna_activation_offer_send_timeout_recovery_failed",
                true);
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");

            var contractProvider = Assert.IsAssignableFrom<ISessionRecoveryStateContract>(host);
            Assert.True(contractProvider.TryGetActiveSessionRecoveryContract(sessionId, out var snapshot));
            Assert.True(snapshot.RetryAuthorityGranted);
            Assert.True(snapshot.RetryAuthorityPending);

            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(host);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    77,
                    FileTransferTransportHandoffKind.RegularNknRecovery,
                    FileTransferTransportKind.RegularNkn,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.TargetProofPending,
                    "receive_stall_recovery",
                    IsUnresolved: true));

            var logStart = GetOperationalLogLength();
            var allowed = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldAllowAccelerationRetryDespiteUnresolvedV6Epoch",
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    77,
                    FileTransferTransportHandoffKind.RegularNknRecovery,
                    FileTransferTransportKind.RegularNkn,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.TargetProofPending,
                    "receive_stall_recovery",
                    IsUnresolved: true),
                "runtime_unlock_offer_send_not_observed",
                "delayed"));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.True(allowed);
            Assert.Contains("event=tuna_acceleration_retry_allowed_current_recovery_contract_authority; reason=runtime_unlock_offer_send_not_observed; stage=delayed", logTail, StringComparison.Ordinal);
            Assert.Contains("reason_detail=post_tuna_fallback_runtime_unlock_authority", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_skipped_v6_epoch_unresolved; reason=runtime_unlock_offer_send_not_observed", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationRetry_RuntimeUnlockBlocksPostTunaFallbackControlProofPending()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.retry.fallback-proof.address");
            var helperClient = new FakeNknClient("helper.tuna.retry.fallback-proof.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: true,
                failedDialAttemptsBeforeSuccess: 0,
                failedListenerAttemptsBeforeSuccess: 100);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-retry-fallback-proof-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-retry-fallback-proof-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            const string transferId = "transfer_fallback_control_unproven_allows_retry";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var started = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "StartTunaFallbackProofIfNeeded",
                "header_switch_off",
                sessionId,
                NknAccelerationLaneKind.File));
            Assert.True(started);

            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(host);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    43,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.WaitingForTargetTransport,
                    "header_switch_off",
                    IsUnresolved: true));
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackNknProofPending",
                "post_tuna_fallback_state_refresh_failed",
                sessionId,
                NknAccelerationLaneKind.File);

            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationRetry", "runtime_unlock_offer_answer_timeout");

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_retry_blocked_v6_epoch_unresolved;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retry_blocked_post_tuna_fallback_unresolved;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_answer_timeout", logTail, StringComparison.Ordinal);

            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var listenerUnavailableLogStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationRetry", "runtime_unlock_preflight_listener_unavailable");

            var listenerUnavailableTail = ReadOperationalLogTail(listenerUnavailableLogStart);
            Assert.Contains("event=tuna_acceleration_retry_blocked_v6_epoch_unresolved;", listenerUnavailableTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retry_blocked_post_tuna_fallback_unresolved;", listenerUnavailableTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_preflight_listener_unavailable", listenerUnavailableTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationRetry_RuntimeUnlockAllowsSameSessionActivationRecoveryProofPending()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.retry.activation-proof.address");
            var helperClient = new FakeNknClient("helper.tuna.retry.activation-proof.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: true,
                failedDialAttemptsBeforeSuccess: 0,
                failedListenerAttemptsBeforeSuccess: 100);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-retry-activation-proof-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-retry-activation-proof-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_activation_recovery_unproven_allows_retry";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackNknProofPending",
                "tuna_activation_offer_send_timeout_recovery_failed",
                sessionId,
                NknAccelerationLaneKind.File);

            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var logStart = GetOperationalLogLength();
            var scheduled = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ScheduleAccelerationNegotiationRetry",
                "runtime_unlock_offer_send_not_observed"));
            Assert.True(scheduled);

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=tuna_acceleration_retry_allowed_runtime_unlock_recovery_unproven;", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_send_not_observed", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain("event=tuna_acceleration_retry_blocked_fallback_control_unproven;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationRetry_RuntimeUnlockPeerResponseTimeoutListenerRearmBypassesCurrentPostFallbackProofPending()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.retry.peer-timeout-listener-rearm-fallback-proof.address");
            var helperClient = new FakeNknClient("helper.tuna.retry.peer-timeout-listener-rearm-fallback-proof.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: false,
                failedDialAttemptsBeforeSuccess: 0,
                allowListenerStartWhenCanListenFalse: true);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-retry-peer-timeout-listener-rearm-fallback-proof-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-retry-peer-timeout-listener-rearm-fallback-proof-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            const string transferId = "transfer_runtime_unlock_peer_timeout_listener_rearm_fallback_proof";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackNknProofPending",
                "post_tuna_fallback_tail_reconciliation_failed",
                sessionId,
                NknAccelerationLaneKind.File);
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                111L,
                sessionId,
                "runtime_unlock_offer_peer_response_timeout",
                "tuna_activation_offer_peer_response_timeout",
                true);

            var logStart = GetOperationalLogLength();
            var allowed = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldAllowAccelerationRetryDespiteFallbackControlProofPending",
                sessionId,
                "post_tuna_fallback_tail_reconciliation_failed",
                NknAccelerationLaneKind.File,
                "runtime_unlock_offer_peer_response_timeout",
                "preflight"));
            Assert.True(allowed);

            var tail = ReadOperationalLogTail(logStart);
            Assert.Contains(
                "event=tuna_acceleration_retry_allowed_fallback_control_unproven_for_listener_rearm;",
                tail,
                StringComparison.Ordinal);
            Assert.Contains("reason=runtime_unlock_offer_peer_response_timeout", tail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "event=tuna_acceleration_retry_blocked_fallback_control_unproven_for_post_tuna_fallback;",
                tail,
                StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationRetry_RuntimeUnlockPeerResponseTimeoutListenerRearmBypassesFallbackBridgeRecoveryInProgress()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.retry.peer-timeout-listener-rearm-fallback-bridge.address");
            var helperClient = new FakeNknClient("helper.tuna.retry.peer-timeout-listener-rearm-fallback-bridge.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: false,
                failedDialAttemptsBeforeSuccess: 0,
                allowListenerStartWhenCanListenFalse: true);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-retry-peer-timeout-listener-rearm-fallback-bridge-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-retry-peer-timeout-listener-rearm-fallback-bridge-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            const string transferId = "transfer_runtime_unlock_peer_timeout_listener_rearm_fallback_bridge";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var authorityRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "post_tuna_fallback_state_refresh_failed")
            {
                RouteToken = FileTransferRouteResolver.PostTunaFallbackV6Token,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                LiveRouteEpoch = 5,
                TransferLegGeneration = 7,
                BridgeRecoveryGeneration = 2,
                TransportEpoch = 21,
                CheckpointRequestId = "v6-regular-nkn-state-refresh:21",
                AuthorityReason = "post_tuna_fallback_state_refresh_failed",
            };
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackLegAuthorityStarted",
                authorityRequest,
                sessionId,
                transferId,
                "post_tuna_fallback_state_refresh_failed");
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackLegAuthorityBridgeRecoveryLifecycle",
                "started",
                "test_post_fallback_bridge_recovery_started");
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackNknProofPending",
                "post_tuna_fallback_tuna_activation_offer_replay_send_timeout",
                sessionId,
                NknAccelerationLaneKind.File);
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                222L,
                sessionId,
                "runtime_unlock_offer_peer_response_timeout",
                "tuna_activation_offer_peer_response_timeout",
                true);

            var logStart = GetOperationalLogLength();
            var allowed = Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "ShouldAllowAccelerationRetryDespiteFallbackControlProofPending",
                sessionId,
                "post_tuna_fallback_tuna_activation_offer_replay_send_timeout",
                NknAccelerationLaneKind.File,
                "runtime_unlock_offer_peer_response_timeout",
                "preflight"));
            Assert.True(allowed);

            var tail = ReadOperationalLogTail(logStart);
            Assert.Contains(
                "event=tuna_acceleration_retry_allowed_fallback_control_unproven_for_listener_rearm;",
                tail,
                StringComparison.Ordinal);
            Assert.Contains("fallback_reason=post_tuna_fallback_tuna_activation_offer_replay_send_timeout", tail, StringComparison.Ordinal);
            Assert.Contains("reason_detail=listener_rearm_must_precede_observed_send_probe", tail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "event=tuna_acceleration_retry_blocked_fallback_control_unproven_for_post_tuna_fallback;",
                tail,
                StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationRetry_RuntimeUnlockBlocksUnrelatedFallbackControlProofPending()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.retry.unrelated-fallback-proof.address");
            var helperClient = new FakeNknClient("helper.tuna.retry.unrelated-fallback-proof.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: true,
                failedDialAttemptsBeforeSuccess: 0,
                failedListenerAttemptsBeforeSuccess: 100);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-retry-unrelated-fallback-proof-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-retry-unrelated-fallback-proof-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            _ = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            InvokePrivateMethod(
                host,
                "MarkFileTransferFallbackNknProofPending",
                "post_tuna_fallback_state_refresh_failed",
                "session_unrelated_fallback_proof",
                NknAccelerationLaneKind.File);

            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationRetry", "runtime_unlock_offer_answer_timeout");

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_retry_blocked_fallback_control_unproven;", logTail, StringComparison.Ordinal);
            Assert.Contains("session_id=session_unrelated_fallback_proof", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_allowed_fallback_control_unproven;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_offer_answer_timeout", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_LateUnlockCanRetryAfterListenerUnavailableExhausted()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.late-unlock.address");
            var helperClient = new FakeNknClient("helper.tuna.late-unlock.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-late-unlock-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-late-unlock-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            var logStart = GetOperationalLogLength();
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_offer_preflight_rejected; reason=listener_unavailable; trigger=runtime_unlock", StringComparison.Ordinal),
                TimeSpan.FromSeconds(8));
            Assert.False(host.IsAccelerationAvailableForTests);
            Assert.False(helper.IsAccelerationAvailableForTests);

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(4));

            Assert.True(hostLane.EnsureListenerCalls >= 1);
            Assert.InRange(helperLane.StartDialerCalls, 1, 2);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, host.AccelerationNegotiatedLanesForTests);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, helper.AccelerationNegotiatedLanesForTests);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockLogsAndRetriesTransientPreflightListenerUnavailable()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.preflight-retry.address");
            var helperClient = new FakeNknClient("helper.tuna.preflight-retry.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-preflight-retry-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-preflight-retry-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var logStart = GetOperationalLogLength();
            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var dataSession = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_preflight_retry_pause",
                cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_offer_preflight_rejected; reason=listener_unavailable", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            Assert.DoesNotContain(
                "event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;",
                ReadOperationalLogTail(logStart),
                StringComparison.Ordinal);

            hostLane.SetCanListen(true);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    e.TargetTransport == FileTransferTransportKind.Tuna),
                TimeSpan.FromSeconds(3));
            var tail = ReadOperationalLogTail(logStart);

            Assert.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;", tail, StringComparison.Ordinal);
            Assert.Contains("reason=activation_negotiation_pending", tail, StringComparison.Ordinal);
            Assert.Contains("reason=listener_unavailable", tail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_tuna_activation_negotiation_regular_nkn_resumed;", tail, StringComparison.Ordinal);
            Assert.Contains("retry_scheduled=1", tail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_preflight_listener_unavailable", tail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_filetransfer_handoff_requested;", tail, StringComparison.Ordinal);
            Assert.True(hostLane.EnsureListenerCalls > 0);
            Assert.True(helperLane.StartDialerCalls >= 1);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockDoesNotPauseRegularV4WhileListenerStartupFails()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.listener-start-no-pause.address");
            var helperClient = new FakeNknClient("helper.tuna.listener-start-no-pause.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: false,
                failedDialAttemptsBeforeSuccess: 0,
                failedListenerAttemptsBeforeSuccess: 1);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-listener-start-no-pause-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-listener-start-no-pause-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_listener_start_no_pause";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            var dataSession = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                transferId,
                cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            var logStart = GetOperationalLogLength();

            hostLane.SetCanListen(true);
            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_retry_scheduled; reason=listener_sidecar_unavailable",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));

            var firstAttemptTail = ReadOperationalLogTail(logStart);
            Assert.Contains(
                "event=filetransfer_tuna_activation_negotiation_regular_nkn_pause_deferred; session_id=",
                firstAttemptTail,
                StringComparison.Ordinal);
            Assert.Contains("reason=runtime_unlock_listener_starting", firstAttemptTail, StringComparison.Ordinal);
            var retryIndex = firstAttemptTail.IndexOf(
                "event=tuna_acceleration_retry_scheduled; reason=listener_sidecar_unavailable",
                StringComparison.Ordinal);
            var pauseIndex = firstAttemptTail.IndexOf(
                "event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;",
                StringComparison.Ordinal);
            Assert.True(retryIndex >= 0, "Expected listener-start retry evidence.");
            Assert.True(
                pauseIndex < 0 || pauseIndex > retryIndex,
                "Regular V4 must not be paused by the failed listener-start attempt.");

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(6));
            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    e.TargetTransport == FileTransferTransportKind.Tuna),
                TimeSpan.FromSeconds(3));

            var tail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_activation_filetransfer_handoff_requested;", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_tuna_activation_negotiation_regular_nkn_pause_retained;", tail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_SessionReadyListenerUnavailableSchedulesRetry()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.session-ready-preflight-retry.address");
            var helperClient = new FakeNknClient("helper.tuna.session-ready-preflight-retry.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-session-ready-preflight-retry-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-session-ready-preflight-retry-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var logStart = GetOperationalLogLength();
            await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=tuna_acceleration_offer_preflight_rejected; reason=listener_unavailable", StringComparison.Ordinal) &&
                           tail.Contains("trigger=session_security_state_ready", StringComparison.Ordinal) &&
                           tail.Contains("retry_scheduled=1", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));

            hostLane.SetCanListen(true);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(5));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_retry_scheduled; reason=preflight_listener_unavailable", logTail, StringComparison.Ordinal);
            Assert.True(hostLane.EnsureListenerCalls > 0);
            Assert.True(helperLane.StartDialerCalls >= 1);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_BothUnlockedSidesUseHelpeeAsPaidListener()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.FromMilliseconds(250);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.payer-priority.address");
            var helperClient = new FakeNknClient("helper.tuna.payer-priority.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-payer-priority-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-payer-priority-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(4));

            Assert.True(hostLane.EnsureListenerCalls > 0);
            Assert.Equal(0, hostLane.StartDialerCalls);
            Assert.Equal(0, helperLane.EnsureListenerCalls);
            Assert.Equal(1, helperLane.StartDialerCalls);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, host.AccelerationNegotiatedLanesForTests);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, helper.AccelerationNegotiatedLanesForTests);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_HelperOnlyUnlockSkipsHelpeePriorityDelayAfterHelpeeDialerOnlyIntent()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousHelpeeIntentGraceDelay = NknSignalingTransport.HelperPaidOfferHelpeeIntentGraceDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.FromSeconds(6);
        NknSignalingTransport.HelperPaidOfferHelpeeIntentGraceDelayOverrideForTests = TimeSpan.FromMilliseconds(750);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.helper-only-intent.address");
            var helperClient = new FakeNknClient("helper.tuna.helper-only-intent.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-helper-only-intent-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-helper-only-intent-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);
            var logStart = GetOperationalLogLength();

            await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(4));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_payer_intent_received; intent=dialer_only", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_offer_delay_short_circuited; reason=helpee_payer_intent_dialer_only", logTail, StringComparison.Ordinal);
            Assert.Equal(0, hostLane.EnsureListenerCalls);
            Assert.True(hostLane.StartDialerCalls >= 1);
            Assert.True(helperLane.EnsureListenerCalls > 0);
            Assert.Equal(0, helperLane.StartDialerCalls);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, host.AccelerationNegotiatedLanesForTests);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, helper.AccelerationNegotiatedLanesForTests);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.HelperPaidOfferHelpeeIntentGraceDelayOverrideForTests = previousHelpeeIntentGraceDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_HelperOnlyUnlockUsesShortGraceWhenHelpeeIntentIsMissing()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousHelpeeIntentGraceDelay = NknSignalingTransport.HelperPaidOfferHelpeeIntentGraceDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.FromSeconds(5);
        NknSignalingTransport.HelperPaidOfferHelpeeIntentGraceDelayOverrideForTests = TimeSpan.FromMilliseconds(100);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.helper-only-missing-intent.address");
            var helperClient = new FakeNknClient("helper.tuna.helper-only-missing-intent.address");
            var helperLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-helper-only-missing-intent-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-helper-only-missing-intent-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);
            var logStart = GetOperationalLogLength();

            await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            await WaitUntilAsync(
                () => helperLane.EnsureListenerCalls > 0,
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_offer_delay_short_circuited; reason=helpee_payer_intent_unobserved", logTail, StringComparison.Ordinal);
            Assert.True(helperLane.EnsureListenerCalls > 0);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.HelperPaidOfferHelpeeIntentGraceDelayOverrideForTests = previousHelpeeIntentGraceDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RetriesWhenInitialOfferGetsNoAnswer()
    {
        FakeNknClient.ResetNetwork();
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromMilliseconds(100);
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.offer.noanswer.address");
            var helperClient = new FakeNknClient("helper.tuna.offer.noanswer.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var droppedOffers = 0;
            var droppedOfferGate = new object();
            string? droppedOfferMessageId = null;
            hostClient.ShouldDeliverSendAsync = (_, payload, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var env) &&
                    env.Type == MsgType.TransportAccelerationOffer)
                {
                    lock (droppedOfferGate)
                    {
                        if (droppedOfferMessageId is null)
                        {
                            droppedOfferMessageId = env.MessageId;
                        }

                        if (string.Equals(droppedOfferMessageId, env.MessageId, StringComparison.Ordinal))
                        {
                            droppedOffers++;
                            return Task.FromResult(false);
                        }
                    }
                }

                return Task.FromResult(true);
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-offer-noanswer-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-offer-noanswer-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);
            var logStart = GetOperationalLogLength();

            await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(8));

            Assert.True(droppedOffers >= 1);
            Assert.True(hostLane.EnsureListenerCalls >= 2);
            Assert.Equal(1, helperLane.StartDialerCalls);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_offer_answer_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retry_scheduled; reason=offer_answer_timeout; attempt=1;", logTail, StringComparison.Ordinal);
            Assert.Contains("max_attempts=3; delay_ms=250; listener_ready_reuse=1", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_ReplaysSameOfferBeforeAnswerTimeout()
    {
        FakeNknClient.ResetNetwork();
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousOfferReplayDelay = NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests;
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromSeconds(2);
        NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.offer.replay.address");
            var helperClient = new FakeNknClient("helper.tuna.offer.replay.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var offerSendCount = 0;
            hostClient.ShouldDeliverSendAsync = (_, payload, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var env) &&
                    env.Type == MsgType.TransportAccelerationOffer &&
                    Interlocked.Increment(ref offerSendCount) <= 3)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-offer-replay-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-offer-replay-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);
            var logStart = GetOperationalLogLength();

            await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(8));

            Assert.True(Volatile.Read(ref offerSendCount) >= 4);
            Assert.Equal(1, helperLane.StartDialerCalls);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_offer_replay_sent;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_offer_answer_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.AccelerationOfferReplayDelayOverrideForTests = previousOfferReplayDelay;
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAcceleration_DoesNotAdvertiseOrConnectListenerBeforeApprovedSession()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.preconsent.address");
            var helperClient = new FakeNknClient("helper.tuna.preconsent.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-preconsent-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-preconsent-id", helperClient.Address));
            var pendingJoinRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var rawOffers = new ConcurrentQueue<Envelope>();
            host.IncomingJoinRequest += (_, e) => pendingJoinRaised.TrySetResult(e);
            hostClient.BeforeSendAsync = (_, payload, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var env) &&
                    env.Type == MsgType.TransportAccelerationOffer)
                {
                    rawOffers.Enqueue(env);
                }

                return Task.CompletedTask;
            };

            await host.HostByAddressAsync(cts.Token);
            var invite = CreateValidatedInviteForTarget(
                new PeerAddress(host.LocalPeerAddress),
                out var rawToken,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare,
                boundHelperAddress: new PeerAddress(helper.LocalPeerAddress));
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);
            await pendingJoinRaised.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await Task.Delay(250, cts.Token);

            Assert.Equal(0, hostLane.EnsureListenerCalls);
            Assert.Empty(rawOffers);
            Assert.False(host.IsAccelerationAvailableForTests);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_PreSessionDoesNotStartDialer()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.presession.address");
            var helperClient = new FakeNknClient("helper.tuna.presession.address");
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-presession-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);
            var logStart = GetOperationalLogLength();
            var offer = CreateOfferPayload("sess_tuna_presession", "00112233445566778899aabbccddeeff");

            var task = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(
                helper,
                "HandleTransportAccelerationOfferAsync",
                hostClient.Address,
                offer,
                "pre-session-offer-message",
                cts.Token));
            await task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            Assert.Equal(0, helperLane.StartDialerCalls);
            Assert.False(helper.IsAccelerationAvailableForTests);
            Assert.Contains("reason=session_not_eligible", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("session_id_mismatch", "reason=session_id_mismatch")]
    [InlineData("source_identity_mismatch", "reason=source_identity_mismatch")]
    [InlineData("expired", "reason=expired")]
    [InlineData("unsupported_version", "reason=sidecar_app_protocol_mismatch")]
    [InlineData("unsupported_lane", "event=tuna_acceleration_answer_sent; accepted=0; reason=unsupported_lane")]
    public async Task TransportAccelerationOffer_InvalidMessagesDoNotStartDialer(string scenario, string expectedLog)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var scenarioTag = scenario.Replace('_', '-');
            var hostClient = new FakeNknClient("host.tuna.offer.invalid." + scenarioTag);
            var helperClient = new FakeNknClient("helper.tuna.offer.invalid." + scenarioTag);
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-offer-invalid-id-" + scenario, hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-offer-invalid-id-" + scenario, helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var nonce = "aa11223344556677889900aabbccdd" + scenario.Length.ToString("x2");
            var offer = CreateOfferPayload(
                scenario == "session_id_mismatch" ? "sess_tuna_wrong_offer" : sessionId,
                nonce,
                supportedLanes: scenario == "unsupported_lane" ? new[] { "bogus" } : new[] { "file" },
                expiresAtUnixMs: scenario == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds() : null,
                sidecarProtocolVersion: scenario == "unsupported_version" ? 99 : null);
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationOffer,
                offer,
                "transport_acceleration_offer",
                offer.Nonce,
                sequence: 1);
            var logStart = GetOperationalLogLength();

            if (scenario == "source_identity_mismatch")
            {
                InvokeNknIncomingMessage(
                    host,
                    helperClient,
                    new NknIncomingMessage(
                        source: "spoof.tuna.offer.invalid.address",
                        payload: EnvelopeCodec.Serialize(envelope),
                        isTopic: false,
                        topic: null,
                        channel: NknBridgeChannel.Control));
            }
            else
            {
                await helperClient.SendAsync(hostClient.ConnectedAddress, EnvelopeCodec.Serialize(envelope), cts.Token);
            }

            await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains(expectedLog, StringComparison.Ordinal), TimeSpan.FromSeconds(3));
            Assert.Equal(0, hostLane.StartDialerCalls);
            Assert.False(host.IsAccelerationAvailableForTests);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationDown_DisablesPeerAcceleration()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.down.address");
            var helperClient = new FakeNknClient("helper.tuna.down.address");
            var hostLane = new FakeNknAccelerationLane();
            var helperLane = new FakeNknAccelerationLane();
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-down-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-down-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);
            var rawDownMessages = new ConcurrentQueue<Envelope>();
            helperClient.MessageReceived += (_, e) =>
            {
                if (e.Channel == NknBridgeChannel.Control &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.TransportAccelerationDown)
                {
                    rawDownMessages.Enqueue(env);
                }
            };

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);

            Assert.True(host.IsAccelerationAvailableForTests);
            Assert.True(helper.IsAccelerationAvailableForTests);

            var logStart = GetOperationalLogLength();
            hostLane.SetAvailable(false, "read_failed");

            await WaitUntilAsync(() => rawDownMessages.Count == 1, TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => !host.IsAccelerationAvailableForTests && !helper.IsAccelerationAvailableForTests, TimeSpan.FromSeconds(3));
            Assert.Equal(NknAccelerationLaneKind.None, helper.AccelerationNegotiatedLanesForTests);
            var logTail = ReadOperationalLogTail(logStart);
            var positiveLogTail = logTail + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=tuna_fallback_started;", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_filetransfer_rebind_requested;", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_down_notify_queued", positiveLogTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_remote_down", positiveLogTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationDown_ReadFailureDuringActiveTransferArmsRuntimeUnlockListenerRearm()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.down-runtime-unlock-rearm.address");
            var helperClient = new FakeNknClient("helper.tuna.down-runtime-unlock-rearm.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-down-runtime-unlock-rearm-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-down-runtime-unlock-rearm-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            const string transferId = "transfer_tuna_down_runtime_unlock_rearm";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            hostLane.MarkListenerAvailableForTests();

            var logStart = GetOperationalLogLength();
            var initialEnsureListenerCalls = hostLane.EnsureListenerCalls;
            await hostLane.StopAsync("read_failed", cts.Token);

            await WaitUntilAsync(
                () => hostLane.EnsureListenerCalls > initialEnsureListenerCalls,
                TimeSpan.FromSeconds(5));

            var tail = ReadOperationalLogTail(logStart);
            Assert.Contains(
                "event=tuna_acceleration_runtime_unlock_listener_rearm_after_sidecar_drop;",
                tail,
                StringComparison.Ordinal);
            Assert.Contains(
                "event=session_recovery_contract_listener_rearm_completed;",
                tail,
                StringComparison.Ordinal);
            Assert.Contains(
                "event=runtime_unlock_offer_dispatched_after_listener_rearm;",
                tail,
                StringComparison.Ordinal);

            var contractProvider = Assert.IsAssignableFrom<ISessionRecoveryStateContract>(host);
            Assert.True(contractProvider.TryGetActiveSessionRecoveryContract(sessionId, out var snapshot));
            Assert.Equal(SessionRecoveryContractKind.RuntimeUnlockActivation, snapshot.Kind);
            Assert.NotEqual(SessionRecoveryContractState.Failed, snapshot.State);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationStop_ResumesRegularNknBeforeBlockedDownNotificationCompletes()
    {
        FakeNknClient.ResetNetwork();
        var blockedDownNotification = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.stop-blocked-down.address");
            var helperClient = new FakeNknClient("helper.tuna.stop-blocked-down.address");
            var hostLane = new FakeNknAccelerationLane();
            var helperLane = new FakeNknAccelerationLane();
            var blockedDownCount = 0;
            hostClient.BeforeSendAsync = async (destination, payload, ct) =>
            {
                if (string.Equals(destination, helperClient.ConnectedAddress, StringComparison.Ordinal) &&
                    EnvelopeCodec.TryDeserialize(payload, out var envelope) &&
                    envelope.Type == MsgType.TransportAccelerationDown)
                {
                    Interlocked.Increment(ref blockedDownCount);
                    await blockedDownNotification.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-stop-blocked-down-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-stop-blocked-down-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            _ = await host.OpenFileTransferDataSessionAsync(
                sessionId,
                "transfer_tuna_stop_blocked_down",
                cts.Token);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            var logStart = GetOperationalLogLength();

            var stopTask = ((ITransportAccelerationControl)host).StopAccelerationAsync("header_switch_off", cts.Token);

            await WaitUntilAsync(() => Volatile.Read(ref blockedDownCount) > 0, TimeSpan.FromSeconds(2));
            Assert.True(stopTask.IsCompletedSuccessfully);
            Assert.False(host.IsAccelerationAvailableForTests);
            Assert.True(host.IsAccelerationUserStoppedForCurrentSessionForTests);
            Assert.True(host.AccelerationDiagnosticsForTests.FallbackEpoch > 0);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_user_stop_filetransfer_fallback_forced;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_reset; reason=header_switch_off; fallback_proof_suppressed=0", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("lanes=file", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_filetransfer_rebind_requested;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            blockedDownNotification.TrySetResult(null);
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationStop_FreshPeerRuntimeUnlockClearsUserStoppedSessionGuard()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.user-stop.address");
            var helperClient = new FakeNknClient("helper.tuna.user-stop.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-user-stop-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-user-stop-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            await WaitUntilAsync(
                () =>
                    Convert.ToInt32(GetPrivateField(host, "accelerationNegotiationScheduled"), CultureInfo.InvariantCulture) == 0 &&
                    Convert.ToInt32(GetPrivateField(helper, "accelerationNegotiationScheduled"), CultureInfo.InvariantCulture) == 0,
                TimeSpan.FromSeconds(5));
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);

            await ((ITransportAccelerationControl)host).StopAccelerationAsync("header_switch_off", cts.Token);
            await WaitUntilAsync(
                () => !host.IsAccelerationAvailableForTests && !helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(3));
            Assert.True(host.IsAccelerationUserStoppedForCurrentSessionForTests);
            var logStart = GetOperationalLogLength();

            await ((ITransportAccelerationControl)helper).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            var matchedClearTail = string.Empty;
            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    if (!host.IsAccelerationUserStoppedForCurrentSessionForTests &&
                        tail.Contains("event=tuna_acceleration_user_stop_cleared; trigger=peer_payer_intent", StringComparison.Ordinal))
                    {
                        matchedClearTail = tail;
                        return true;
                    }

                    return false;
                },
                TimeSpan.FromSeconds(3));
            var logTail = matchedClearTail + Environment.NewLine + ReadOperationalLogTail(logStart);
            Assert.False(host.IsAccelerationUserStoppedForCurrentSessionForTests);
            Assert.Contains("event=tuna_acceleration_user_stop_cleared; trigger=peer_payer_intent", logTail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationStop_RemoteDownDoesNotBlockPeerPaidReunlock()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.remote-stop-reunlock.address");
            var helperClient = new FakeNknClient("helper.tuna.remote-stop-reunlock.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-remote-stop-reunlock-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-remote-stop-reunlock-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);

            await ((ITransportAccelerationControl)helper).StopAccelerationAsync("header_switch_off", cts.Token);
            await WaitUntilAsync(
                () => !host.IsAccelerationAvailableForTests && !helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(20));
            Assert.False(host.IsAccelerationUserStoppedForCurrentSessionForTests);
            Assert.True(helper.IsAccelerationUserStoppedForCurrentSessionForTests);
            var hostDialerCallsBeforeReunlock = hostLane.StartDialerCalls;

            await ((ITransportAccelerationControl)helper).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(20));
            Assert.True(hostLane.StartDialerCalls > hostDialerCallsBeforeReunlock);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, host.AccelerationNegotiatedLanesForTests);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, helper.AccelerationNegotiatedLanesForTests);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationStop_RuntimeUnlockClearsUserStoppedSessionGuard()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.user-stop-reunlock.address");
            var helperClient = new FakeNknClient("helper.tuna.user-stop-reunlock.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-user-stop-reunlock-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-user-stop-reunlock-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);

            await ((ITransportAccelerationControl)host).StopAccelerationAsync("header_switch_off", cts.Token);
            await WaitUntilAsync(
                () => !host.IsAccelerationAvailableForTests && host.IsAccelerationUserStoppedForCurrentSessionForTests,
                TimeSpan.FromSeconds(10));
            var hostDialerCallsBeforeReunlock = hostLane.StartDialerCalls;

            await ((ITransportAccelerationControl)host).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);
            Assert.False(host.IsAccelerationUserStoppedForCurrentSessionForTests);
            var settleUntil = DateTimeOffset.UtcNow.AddSeconds(2);
            while (DateTimeOffset.UtcNow < settleUntil &&
                   hostLane.StartDialerCalls <= hostDialerCallsBeforeReunlock &&
                   Convert.ToInt32(GetPrivateField(host, "accelerationNegotiationRetryAttempts"), CultureInfo.InvariantCulture) <= 0)
            {
                await Task.Delay(50, cts.Token);
            }

            await ((ITransportAccelerationControl)helper).RequestAccelerationNegotiationAsync("runtime_unlock", cts.Token);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(20));
            Assert.True(hostLane.StartDialerCalls > hostDialerCallsBeforeReunlock);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, host.AccelerationNegotiatedLanesForTests);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, helper.AccelerationNegotiatedLanesForTests);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswer_RejectedAnswerPreservesPeerReasonAndClearsNonce()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.answer.reject.address");
            var helperClient = new FakeNknClient("helper.tuna.answer.reject.address");
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-answer-reject-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-answer-reject-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var nonce = "bb11223344556677889900aabbccddee";
            SetPrivateField(host, "outboundAccelerationOfferNonce", nonce);
            var answer = CreateAnswerPayload(sessionId, nonce, accepted: false, rejectReason: "sidecar_unavailable");
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationAnswer,
                answer,
                "transport_acceleration_answer",
                answer.Nonce,
                sequence: 101);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(host, "HandleTransportAccelerationAnswer", helperClient.Address, envelope);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_answer_rejected; reason=sidecar_unavailable", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            Assert.Null(GetPrivateField(host, "outboundAccelerationOfferNonce"));
            Assert.Equal(NknAccelerationLaneKind.None, host.AccelerationNegotiatedLanesForTests);
            Assert.False(host.IsAccelerationAvailableForTests);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswer_ForRetiredOutboundOfferAcceptedAfterPeerOfferCompletesHandshake()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.answer.retired-offer.address");
            var helperClient = new FakeNknClient("helper.tuna.answer.retired-offer.address");
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-answer-retired-offer-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-answer-retired-offer-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var outboundNonce = "ba11223344556677889900aabbccddee";
            SetPrivateField(host, "outboundAccelerationOfferNonce", outboundNonce);
            SetPrivateField(host, "outboundAccelerationOfferTrigger", "runtime_unlock");
            SetPrivateField(host, "outboundAccelerationOfferPayerDecisionId", 4L);
            var peerOffer = CreateOfferPayload(
                sessionId,
                "ab11223344556677889900aabbccddee",
                payerDecisionId: 5L);
            _ = Assert.IsType<long>(InvokePrivateMethod(
                host,
                "BeginPendingAccelerationAnswerAck",
                peerOffer,
                NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen));
            var answer = CreateAnswerPayload(
                sessionId,
                outboundNonce,
                accepted: true,
                payerDecisionId: 4L);
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationAnswer,
                answer,
                "transport_acceleration_answer",
                answer.Nonce,
                sequence: 102);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(host, "HandleTransportAccelerationAnswer", helperClient.Address, envelope);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_retired_offer_answer_accepted;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(6));
            Assert.Null(GetPrivateField(host, "outboundAccelerationOfferNonce"));
            Assert.Null(GetPrivateField(host, "pendingAccelerationAnswerAckNonce"));
            Assert.Equal(
                NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen,
                host.AccelerationNegotiatedLanesForTests);
            Assert.True(host.IsAccelerationAvailableForTests);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("reason=nonce_mismatch", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswer_RuntimeUnlockLateAnswerAfterPeerResponseTimeoutIsAccepted()
    {
        FakeNknClient.ResetNetwork();
        var previousOfferPeerResponseTimeout = NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests;
        NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests = TimeSpan.FromMilliseconds(75);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.answer.late-peer-response-runtime-unlock.address");
            var helperClient = new FakeNknClient("helper.tuna.answer.late-peer-response-runtime-unlock.address");
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-answer-late-peer-response-runtime-unlock-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-answer-late-peer-response-runtime-unlock-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var nonce = "bd11223344556677889900aabbccddee";
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                nonce,
                payerDecisionId: 11L,
                generation: 6L,
                observedSend: true,
                observedLane: "control_to_bulk_endpoint",
                peerReceived: false,
                answerTimeoutScheduled: true);
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                5L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "tuna_activation_offer_send_timeout");
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");
            InvokePrivateMethod(
                host,
                "MarkRuntimeUnlockRecoveryContractRetryObserved",
                sessionId,
                6L,
                "control_to_bulk_endpoint");
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(
                host,
                "ScheduleRuntimeUnlockOfferPeerResponseTimeout",
                nonce,
                11L,
                6L,
                sessionId,
                "control_to_bulk_endpoint");

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_pending_runtime_unlock_answer_preserved;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));

            SetPrivateField(host, "outboundAccelerationOfferNonce", "be11223344556677889900aabbccddee");
            SetPrivateField(host, "outboundAccelerationOfferTrigger", "runtime_unlock");
            SetPrivateField(host, "outboundAccelerationOfferPayerDecisionId", 12L);
            SetPrivateField(host, "outboundAccelerationOfferGeneration", 7L);

            var answer = CreateAnswerPayload(
                sessionId,
                nonce,
                accepted: true,
                supportedLanes: new[] { "file" },
                payerDecisionId: 11L);
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationAnswer,
                answer,
                "transport_acceleration_answer",
                answer.Nonce,
                sequence: 104);

            InvokePrivateMethod(host, "HandleTransportAccelerationAnswer", helperClient.Address, envelope);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_retired_offer_answer_accepted;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(6));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("reason=runtime_unlock_offer_peer_response_timeout_pending_runtime_unlock_answer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_runtime_unlock_offer_peer_response_timeout;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retired_offer_answer_accepted;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_activation_filetransfer_handoff_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_completed;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("reason=nonce_mismatch", logTail, StringComparison.Ordinal);
            Assert.Null(GetPrivateField(host, "outboundAccelerationOfferNonce"));
            Assert.Equal(NknAccelerationLaneKind.File, host.AccelerationNegotiatedLanesForTests);
            Assert.True(host.IsAccelerationAvailableForTests);
        }
        finally
        {
            NknSignalingTransport.RuntimeUnlockOfferPeerResponseTimeoutOverrideForTests = previousOfferPeerResponseTimeout;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswer_RuntimeUnlockLateAnswerAfterObservedOfferTimeoutIsIgnoredAndRearmed()
    {
        FakeNknClient.ResetNetwork();
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromMilliseconds(75);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.answer.late-runtime-unlock.address");
            var helperClient = new FakeNknClient("helper.tuna.answer.late-runtime-unlock.address");
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-answer-late-runtime-unlock-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-answer-late-runtime-unlock-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var nonce = "bc11223344556677889900aabbccddee";
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                nonce,
                payerDecisionId: 7L,
                generation: 3L,
                observedSend: true,
                observedLane: "control_priority",
                answerTimeoutScheduled: true);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(host, "ScheduleAccelerationOfferAnswerTimeout", nonce);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=session_recovery_contract_listener_rearm_required;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));

            var answer = CreateAnswerPayload(
                sessionId,
                nonce,
                accepted: true,
                supportedLanes: new[] { "file" },
                payerDecisionId: 7L);
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationAnswer,
                answer,
                "transport_acceleration_answer",
                answer.Nonce,
                sequence: 103);

            InvokePrivateMethod(host, "HandleTransportAccelerationAnswer", helperClient.Address, envelope);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_stale_offer_answer_ignored;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(6));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_offer_answer_timeout;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_listener_rearm_required;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=runtime_unlock_offer_peer_response_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("recovery_reason=tuna_activation_offer_peer_response_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("peer_received=0", logTail, StringComparison.Ordinal);
            Assert.Contains("listener_ready_reuse=0", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_stale_offer_answer_ignored; reason=retired_generation", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retired_offer_answer_accepted;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_answer_ack_sent;", logTail, StringComparison.Ordinal);
            Assert.Equal(NknAccelerationLaneKind.None, host.AccelerationNegotiatedLanesForTests);
            Assert.False(host.IsAccelerationAvailableForTests);
        }
        finally
        {
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswer_RuntimeUnlockAnswerTimeoutWithoutPeerReceiptArmsCutThroughRecovery()
    {
        FakeNknClient.ResetNetwork();
        var previousOfferAnswerTimeout = NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests;
        var previousRecoveryRequest = NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests;
        NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = TimeSpan.FromMilliseconds(75);
        NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = (_, _, _) => true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.answer-cutthrough-runtime-unlock.address");
            var helperClient = new FakeNknClient("helper.tuna.answer-cutthrough-runtime-unlock.address");
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-answer-cutthrough-runtime-unlock-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-answer-cutthrough-runtime-unlock-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            const string transferId = "transfer_runtime_unlock_answer_timeout_cutthrough";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");

            var nonce = "cb11223344556677889900aabbccddee";
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                nonce,
                payerDecisionId: 17L,
                generation: 9L,
                observedSend: true,
                observedLane: "control_to_bulk_endpoint",
                peerReceived: false,
                answerTimeoutScheduled: true);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(host, "ScheduleAccelerationOfferAnswerTimeout", nonce);

            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    return tail.Contains("event=tuna_acceleration_offer_answer_timeout;", StringComparison.Ordinal) &&
                           tail.Contains("event=session_recovery_contract_started;", StringComparison.Ordinal) &&
                           tail.Contains("retry_reason=runtime_unlock_offer_peer_response_timeout", StringComparison.Ordinal) &&
                           tail.Contains("recovery_reason=tuna_activation_offer_peer_response_timeout", StringComparison.Ordinal) &&
                           tail.Contains("cutthrough_pending=1", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(3));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_offer_answer_timeout;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_activation_offer_not_observed;", logTail, StringComparison.Ordinal);
            Assert.Contains("peer_received=0", logTail, StringComparison.Ordinal);
            Assert.Contains("retry_reason=runtime_unlock_offer_peer_response_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("recovery_reason=tuna_activation_offer_peer_response_timeout", logTail, StringComparison.Ordinal);
            Assert.Contains("cutthrough_pending=1", logTail, StringComparison.Ordinal);
            var contractProvider = Assert.IsAssignableFrom<ISessionRecoveryStateContract>(host);
            Assert.True(contractProvider.TryGetActiveSessionRecoveryContract(sessionId, out var snapshot));
            Assert.Equal(SessionRecoveryContractKind.RuntimeUnlockActivation, snapshot.Kind);
            Assert.Equal("runtime_unlock_offer_peer_response_timeout", snapshot.RetryReason);
            Assert.Equal("tuna_activation_offer_peer_response_timeout", snapshot.RecoveryReason);
            Assert.NotEqual(SessionRecoveryContractState.Failed, snapshot.State);
        }
        finally
        {
            NknSignalingTransport.AccelerationOfferAnswerTimeoutOverrideForTests = previousOfferAnswerTimeout;
            NknSignalingTransport.RuntimeUnlockOfferSendRecoveryRequestOverrideForTests = previousRecoveryRequest;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswer_RuntimeUnlockPendingAnswerAfterPayerYieldResetRequiresCurrentLease()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.answer-payer-yield-runtime-unlock.address");
            var helperClient = new FakeNknClient("helper.tuna.answer-payer-yield-runtime-unlock.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-answer-payer-yield-runtime-unlock-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-answer-payer-yield-runtime-unlock-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var nonce = "bd11223344556677889900aabbccddee";
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                nonce,
                payerDecisionId: 8L,
                generation: 4L,
                observedSend: true,
                observedLane: "control_to_bulk_endpoint",
                answerTimeoutScheduled: true);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(host, "YieldLocalPaidListenerToRemoteHelpee", "payer_intent_will_listen", 8L);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_acceleration_reset; reason=sidecar_payer_yield_to_helpee;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));

            var answer = CreateAnswerPayload(
                sessionId,
                nonce,
                accepted: true,
                supportedLanes: new[] { "file" },
                payerDecisionId: 8L);
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationAnswer,
                answer,
                "transport_acceleration_answer",
                answer.Nonce,
                sequence: 104);

            InvokePrivateMethod(host, "HandleTransportAccelerationAnswer", helperClient.Address, envelope);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=runtime_unlock_answer_rejected_tuna_path_lease_unavailable;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(6));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_pending_runtime_unlock_answer_preserved; reason=payer_yield_pending_runtime_unlock_answer", logTail, StringComparison.Ordinal);
            Assert.Contains("event=runtime_unlock_answer_rejected_tuna_path_lease_unavailable;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retired_offer_answer_accepted;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_negotiated;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("reason=nonce_mismatch", logTail, StringComparison.Ordinal);
            Assert.Equal(NknAccelerationLaneKind.None, host.AccelerationNegotiatedLanesForTests);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswer_RuntimeUnlockPendingAnswerAfterSidecarCloseIsIgnoredAndRetried()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.answer-sidecar-close-runtime-unlock.address");
            var helperClient = new FakeNknClient("helper.tuna.answer-sidecar-close-runtime-unlock.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-answer-sidecar-close-runtime-unlock-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-answer-sidecar-close-runtime-unlock-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var nonce = "bd55223344556677889900aabbccddee";
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                nonce,
                payerDecisionId: 12L,
                generation: 6L,
                observedSend: true,
                observedLane: "control_to_bulk_endpoint",
                answerTimeoutScheduled: true);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(host, "ResetAccelerationNegotiation", "sidecar_remote_closed");

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_runtime_unlock_interrupted_offer_reset_retry_scheduled;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));

            var answer = CreateAnswerPayload(
                sessionId,
                nonce,
                accepted: true,
                supportedLanes: new[] { "file" },
                payerDecisionId: 12L);
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationAnswer,
                answer,
                "transport_acceleration_answer",
                answer.Nonce,
                sequence: 105);

            InvokePrivateMethod(host, "HandleTransportAccelerationAnswer", helperClient.Address, envelope);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_stale_offer_answer_ignored;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("retry_reason=runtime_unlock_sidecar_remote_closed_after_observed_send", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=retired_generation", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_pending_runtime_unlock_answer_preserved; reason=sidecar_remote_closed_pending_runtime_unlock_answer", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retired_offer_answer_accepted;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_activation_filetransfer_handoff_requested;", logTail, StringComparison.Ordinal);
            Assert.Equal(NknAccelerationLaneKind.None, host.AccelerationNegotiatedLanesForTests);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void TransportAccelerationAnswer_UnobservedRuntimeUnlockOfferResetArmsFreshRetryState()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.answer-unobserved-reset-runtime-unlock.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-answer-unobserved-reset-runtime-unlock-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);

            const string sessionId = "sess_runtime_unlock_unobserved_reset_retry";
            var nonce = "be11223344556677889900aabbccddee";
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                nonce,
                payerDecisionId: 9L,
                generation: 5L,
                observedSend: false,
                observedLane: null,
                answerTimeoutScheduled: false);
            Assert.True(host.RuntimeUnlockOfferStateForTests.HasOutboundOffer);
            Assert.False(host.RuntimeUnlockOfferStateForTests.IsRetired);
            Assert.Equal(5L, GetPrivateField(host, "outboundAccelerationOfferGeneration"));
            Assert.Equal(9L, GetPrivateField(host, "outboundAccelerationOfferPayerDecisionId"));
            Assert.Equal(nonce, GetPrivateField(host, "outboundAccelerationOfferNonce"));

            var captured = host.CaptureUnobservedRuntimeUnlockOfferResetRetryForTests("sidecar_remote_closed");

            Assert.True(captured.Captured);
            Assert.Equal(sessionId, captured.SessionId);
            Assert.Equal(9L, captured.PayerDecisionId);
            Assert.Equal(5L, captured.Generation);
            Assert.Equal("runtime_unlock_sidecar_remote_closed", captured.RetryReason);
            Assert.Equal(nonce, GetPrivateField(host, "outboundAccelerationOfferNonce"));
            Assert.True(host.RuntimeUnlockOfferStateForTests.HasOutboundOffer);
            Assert.True(host.RuntimeUnlockOfferStateForTests.IsRetired);
            Assert.Equal("sidecar_remote_closed_unobserved_runtime_unlock_offer_reset", host.RuntimeUnlockOfferStateForTests.RetiredReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswer_UserStoppedRejectAfterRuntimeUnlockRetriesFreshOffer()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.answer.peer-stop-retry.address");
            var helperClient = new FakeNknClient("helper.tuna.answer.peer-stop-retry.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-answer-peer-stop-retry-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-answer-peer-stop-retry-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var expectedNonce = "99112233445566778899aabbccddee00";
            SetPrivateField(host, "outboundAccelerationOfferNonce", expectedNonce);
            SetPrivateField(host, "outboundAccelerationOfferTrigger", "runtime_unlock");
            var answer = CreateAnswerPayload(
                sessionId,
                expectedNonce,
                accepted: false,
                supportedLanes: Array.Empty<string>(),
                rejectReason: "user_stopped_tuna");
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationAnswer,
                answer,
                "transport_acceleration_answer",
                answer.Nonce,
                sequence: 101);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(host, "HandleTransportAccelerationAnswer", helperClient.Address, envelope);
            hostLane.SetCanListen(true);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(5));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_answer_rejected; reason=user_stopped_tuna; offer_trigger=runtime_unlock", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retry_scheduled; reason=peer_user_stopped_tuna", logTail, StringComparison.Ordinal);
            Assert.True(hostLane.EnsureListenerCalls >= 1);
            Assert.Equal(1, helperLane.StartDialerCalls);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, host.AccelerationNegotiatedLanesForTests);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, helper.AccelerationNegotiatedLanesForTests);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationAnswer_SidecarUnavailableRejectAfterRuntimeUnlockReusesCurrentListener()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.answer.runtime-unlock-sidecar-retry.address");
            var helperClient = new FakeNknClient("helper.tuna.answer.runtime-unlock-sidecar-retry.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-answer-runtime-unlock-sidecar-retry-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-answer-runtime-unlock-sidecar-retry-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
            InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            hostLane.MarkListenerAvailableForTests();
            Assert.True(hostLane.IsAvailable);
            await Task.Delay(500, cts.Token);

            var expectedNonce = "98112233445566778899aabbccddee00";
            SetPrivateField(host, "accelerationNegotiationScheduled", 0);
            SetPrivateField(host, "pendingAccelerationAnswerAckSessionId", null);
            SetPrivateField(host, "pendingAccelerationAnswerAckNonce", null);
            SetPrivateField(host, "pendingAccelerationAnswerAckLanes", NknAccelerationLaneKind.None);
            SetPrivateField(host, "pendingAccelerationAnswerAckPayerDecisionId", 0L);
            host.SeedRuntimeUnlockOfferCriticalSectionForTests(
                sessionId,
                expectedNonce,
                payerDecisionId: 0,
                generation: 7,
                observedSend: true,
                observedLane: "control_priority",
                peerReceived: true,
                answerTimeoutScheduled: true);
            var answer = CreateAnswerPayload(
                sessionId,
                expectedNonce,
                accepted: false,
                supportedLanes: Array.Empty<string>(),
                rejectReason: "sidecar_unavailable");
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationAnswer,
                answer,
                "transport_acceleration_answer",
                answer.Nonce,
                sequence: 101);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(host, "HandleTransportAccelerationAnswer", helperClient.Address, envelope);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_sidecar_unavailable",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_acceleration_answer_rejected; reason=sidecar_unavailable; offer_trigger=runtime_unlock", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_sidecar_unavailable", logTail, StringComparison.Ordinal);
            Assert.Contains("event=runtime_unlock_answer_rejected_tuna_path_lease_unavailable;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_path_lease_failed;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_path_lease_sidecar_unavailable;", logTail, StringComparison.Ordinal);
            Assert.Contains("listener_ready_reuse=0", logTail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_listener_rearm_required; session_id=", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=session_recovery_contract_listener_rearm_skipped; session_id=", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_retry_scheduled; reason=sidecar_unavailable", logTail, StringComparison.Ordinal);
            Assert.True(hostLane.StopCalls >= 1);
            Assert.Equal("runtime_unlock_sidecar_unavailable", hostLane.LastStopReason);
            Assert.Equal(NknAccelerationLaneKind.None, host.AccelerationNegotiatedLanesForTests);
            Assert.False(host.IsAccelerationAvailableForTests);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("nonce_mismatch", "reason=nonce_mismatch")]
    [InlineData("session_id_mismatch", "reason=session_id_mismatch")]
    [InlineData("source_identity_mismatch", "reason=source_identity_mismatch")]
    [InlineData("expired", "reason=expired")]
    [InlineData("unsupported_version", "reason=sidecar_app_protocol_mismatch")]
    [InlineData("unsupported_lane", "event=tuna_acceleration_answer_rejected; reason=unsupported_lane")]
    [InlineData("payer_decision_mismatch", "reason=payer_decision_mismatch")]
    public async Task TransportAccelerationAnswer_InvalidMessagesCannotEnableAcceleration(string scenario, string expectedLog)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var scenarioTag = scenario.Replace('_', '-');
            var hostClient = new FakeNknClient("host.tuna.answer.invalid." + scenarioTag);
            var helperClient = new FakeNknClient("helper.tuna.answer.invalid." + scenarioTag);
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-answer-invalid-id-" + scenario, hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-answer-invalid-id-" + scenario, helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            var expectedNonce = "cc11223344556677889900aabbccddee";
            SetPrivateField(host, "outboundAccelerationOfferNonce", expectedNonce);
            if (scenario == "payer_decision_mismatch")
            {
                SetPrivateField(host, "outboundAccelerationOfferPayerDecisionId", 41L);
            }

            var answer = CreateAnswerPayload(
                scenario == "session_id_mismatch" ? "sess_tuna_wrong_answer" : sessionId,
                scenario == "nonce_mismatch" ? "dd11223344556677889900aabbccddee" : expectedNonce,
                accepted: true,
                supportedLanes: scenario == "unsupported_lane" ? new[] { "bogus" } : new[] { "file" },
                expiresAtUnixMs: scenario == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds() : null,
                sidecarProtocolVersion: scenario == "unsupported_version" ? 99 : null,
                payerDecisionId: scenario == "payer_decision_mismatch" ? 42L : 0L);
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationAnswer,
                answer,
                "transport_acceleration_answer",
                answer.Nonce,
                sequence: 1);
            var logStart = GetOperationalLogLength();

            if (scenario == "source_identity_mismatch")
            {
                InvokeNknIncomingMessage(
                    host,
                    helperClient,
                    new NknIncomingMessage(
                        source: "spoof.tuna.answer.invalid.address",
                        payload: EnvelopeCodec.Serialize(envelope),
                        isTopic: false,
                        topic: null,
                        channel: NknBridgeChannel.Control));
            }
            else
            {
                await helperClient.SendAsync(hostClient.ConnectedAddress, EnvelopeCodec.Serialize(envelope), cts.Token);
            }

            await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains(expectedLog, StringComparison.Ordinal), TimeSpan.FromSeconds(3));
            Assert.Equal(NknAccelerationLaneKind.None, host.AccelerationNegotiatedLanesForTests);
            Assert.False(host.IsAccelerationAvailableForTests);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Theory]
    [Trait("Category", "Smoke")]
    [InlineData("session_id_mismatch", "reason=session_id_mismatch")]
    [InlineData("source_identity_mismatch", "reason=source_identity_mismatch")]
    public async Task TransportAccelerationDown_MismatchDoesNotResetActiveAcceleration(string scenario, string expectedLog)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var scenarioTag = scenario.Replace('_', '-');
            var hostClient = new FakeNknClient("host.tuna.down.invalid." + scenarioTag);
            var helperClient = new FakeNknClient("helper.tuna.down.invalid." + scenarioTag);
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-down-invalid-id-" + scenario, hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-down-invalid-id-" + scenario, helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            var down = CreateDownPayload(
                scenario == "session_id_mismatch" ? "sess_tuna_wrong_down" : sessionId,
                "ee11223344556677889900aabbccddee");
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationDown,
                down,
                "transport_acceleration_down",
                down.Nonce,
                sequence: 1);
            var logStart = GetOperationalLogLength();

            if (scenario == "source_identity_mismatch")
            {
                InvokeNknIncomingMessage(
                    host,
                    helperClient,
                    new NknIncomingMessage(
                        source: "spoof.tuna.down.invalid.address",
                        payload: EnvelopeCodec.Serialize(envelope),
                        isTopic: false,
                        topic: null,
                        channel: NknBridgeChannel.Control));
            }
            else
            {
                await helperClient.SendAsync(hostClient.ConnectedAddress, EnvelopeCodec.Serialize(envelope), cts.Token);
            }

            await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains(expectedLog, StringComparison.Ordinal), TimeSpan.FromSeconds(3));
            Assert.True(host.IsAccelerationAvailableForTests);
            Assert.Equal(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, host.AccelerationNegotiatedLanesForTests);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationDown_StartsFallbackProofWhenLocalLaneAlreadyUnavailable()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.down.after-unavailable.address");
            var helperClient = new FakeNknClient("helper.tuna.down.after-unavailable.address");
            var hostLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-down-after-unavailable-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-down-after-unavailable-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            host.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);
            hostLane.SetAvailable(false, "test_local_unavailable_before_down");
            await WaitUntilAsync(() => !host.IsAccelerationAvailableForTests, TimeSpan.FromSeconds(2));

            var down = CreateDownPayload(sessionId, "ef11223344556677889900aabbccddee");
            var envelope = BuildSecureAccelerationEnvelope(
                helper,
                MsgType.TransportAccelerationDown,
                down,
                "transport_acceleration_down",
                down.Nonce,
                sequence: 1);
            var logStart = GetOperationalLogLength();

            await helperClient.SendAsync(hostClient.ConnectedAddress, EnvelopeCodec.Serialize(envelope), cts.Token);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_fallback_started;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(6));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("reason=remote_read_failed", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_filetransfer_rebind_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_remote_down; reason=read_failed", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataFrame_FallsBackToNknWhenAccelerationSendFails()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.fallback.address");
            var helperClient = new FakeNknClient("helper.tuna.file.fallback.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true, sendResult: false);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-fallback-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            var rawNknDataFrames = new ConcurrentQueue<NknIncomingMessage>();
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.FileTransferDataFrame)
                {
                    rawNknDataFrames.Enqueue(e);
                }
            };

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_fallback";
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var logStart = GetOperationalLogLength();
            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);

            await WaitUntilAsync(() => rawNknDataFrames.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Empty(fakeLane.Sent);
            Assert.Equal(NknBridgeChannel.Bulk, rawNknDataFrames.Single().Channel);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_fallback_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_nkn_frame_sent; message_type=file_transfer_data_frame; channel=bulk", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferDataFrame_StartsFallbackProofWhenAccelerationBecomesUnavailableBeforeSend()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.unavailable.fallback.address");
            var helperClient = new FakeNknClient("helper.tuna.file.unavailable.fallback.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-unavailable-fallback-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-unavailable-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            var rawNknDataFrames = new ConcurrentQueue<NknIncomingMessage>();
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.FileTransferDataFrame)
                {
                    rawNknDataFrames.Enqueue(e);
                }
            };

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_unavailable_fallback";
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);
            fakeLane.IsAvailable = false;
            var logStart = GetOperationalLogLength();
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);

            await WaitUntilAsync(() => rawNknDataFrames.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Empty(fakeLane.Sent);
            Assert.Equal(NknBridgeChannel.Bulk, rawNknDataFrames.Single().Channel);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_fallback_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=tuna_unavailable_before_send", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_filetransfer_rebind_requested;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_disable_handoff_nkn_pending;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_nkn_frame_sent; message_type=file_transfer_data_frame; channel=bulk", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Theory]
    [InlineData("byte_cap_reached")]
    [InlineData("remote_byte_cap_reached")]
    [Trait("Category", "Smoke")]
    public async Task FileTransferByteCapFallback_WaitsForBulkReceiveProof(string reason)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.cap.probe.address");
            var helperClient = new FakeNknClient("helper.tuna.file.cap.probe.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-cap-probe-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-cap-probe-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_cap_probe";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            var logStart = GetOperationalLogLength();
            fakeLane.SetAvailable(false, reason);
            var availabilityReason = $"sidecar_{reason}";

            await WaitUntilAsync(
                () =>
                {
                    var snapshot = availabilityEvents.ToArray();
                    return snapshot.Any(e => !e.IsAvailable && string.Equals(e.Reason, availabilityReason, StringComparison.Ordinal));
                },
                TimeSpan.FromSeconds(2));

            var events = availabilityEvents.ToArray();
            Assert.Contains(events, e => !e.IsAvailable && string.Equals(e.Reason, availabilityReason, StringComparison.Ordinal) && e.RequiresResumeRequest);
            Assert.DoesNotContain(events, e => e.IsAvailable && string.Equals(e.Reason, "transport_probe_unproven", StringComparison.Ordinal));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_fallback_nkn_probe_started;", logTail, StringComparison.Ordinal);
            Assert.Contains($"reason={availabilityReason}", logTail, StringComparison.Ordinal);
            Assert.Contains("trigger=cap_handoff_immediate", logTail, StringComparison.Ordinal);
            Assert.Contains("delay_ms=0", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_fallback_nkn_proof_pending;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferFallbackProof_TunaDropWithoutActiveTransferDoesNotPoisonNextRoute()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.no-active-fallback.address");
            var helperClient = new FakeNknClient("helper.tuna.file.no-active-fallback.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-no-active-fallback-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-no-active-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            var logStart = GetOperationalLogLength();
            fakeLane.SetAvailable(false, "remote_closed");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_post_tuna_fallback_v6_route_suppressed_no_active_transfer;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    "transfer_no_active_stale_fallback_epoch",
                    FileTransferDirection.Outbound,
                    1,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "transport_recovered_unproven",
                    IsUnresolved: false));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains(
                "event=filetransfer_post_tuna_fallback_v6_route_suppressed_no_active_transfer;",
                logTail,
                StringComparison.Ordinal);
            Assert.Contains(
                "event=filetransfer_v6_fallback_recovered_state_synthesis_suppressed_no_active_transfer;",
                logTail,
                StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_fallback_started;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_fallback_recovered_state_synthesized;", logTail, StringComparison.Ordinal);
            Assert.False(helper.IsPostTunaFileFallbackActiveForRouteSelection);
            Assert.False(helper.ShouldUseFileTransferV6ForAcceleration);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferFallbackProof_GenericControlWaitsForV6EpochObserver()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.live-v4-proof.address");
            var helperClient = new FakeNknClient("helper.tuna.file.live-v4-proof.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-live-v4-proof-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-live-v4-proof-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_live_v4_proof";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            var logStart = GetOperationalLogLength();
            fakeLane.SetAvailable(false, "byte_cap_reached");
            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback),
                TimeSpan.FromSeconds(2));

            var completedFromGenericControl = Assert.IsType<bool>(
                InvokePrivateMethod(helper, "CompleteFileTransferFallbackNknProofIfPending", "nkn_control_chat_received", sessionId));
            Assert.False(completedFromGenericControl);
            var genericProofTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_fallback_nkn_proof_waiting_for_v6_epoch;", genericProofTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_post_tuna_fallback_nkn_proved;", genericProofTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_post_tuna_fallback_cleanup_completed;", genericProofTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_live_v4_fallback_nkn_proved;", genericProofTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_live_v4_fallback_cleanup_completed;", genericProofTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_disable_handoff_completed;", genericProofTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferFallbackProof_LocalV4NknSendWaitsForV6EpochObserver()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.live-v4-local-proof.address");
            var helperClient = new FakeNknClient("helper.tuna.file.live-v4-local-proof.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-live-v4-local-proof-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-live-v4-local-proof-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_live_v4_local_proof";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            var logStart = GetOperationalLogLength();
            fakeLane.SetAvailable(false, "byte_cap_reached");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_fallback_nkn_proof_pending;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV4
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("proof=file_transfer_v4_bulk_frame_sent", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_fallback_nkn_frame_sent; message_type=file_transfer_data_frame; channel=bulk", logTail, StringComparison.Ordinal);
            Assert.Contains("proof=file_transfer_v4_bulk_frame_sent", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_fallback_nkn_proof_waiting_for_v6_epoch;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_fallback_nkn_proof_observed;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_post_tuna_fallback_nkn_proved;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_post_tuna_fallback_cleanup_completed;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_live_v4_fallback_nkn_proved;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_live_v4_fallback_cleanup_completed;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferFallbackProof_CurrentV6ReceiverStateCompletesPostTunaFallbackProof()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.post-fallback-v6-proof.address");
            var helperClient = new FakeNknClient("helper.tuna.file.post-fallback-v6-proof.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-post-fallback-v6-proof-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-post-fallback-v6-proof-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_post_fallback_v6_proof";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            fakeLane.SetAvailable(false, "byte_cap_reached");
            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback),
                TimeSpan.FromSeconds(2));

            var logStart = GetOperationalLogLength();
            var completed = Assert.IsType<bool>(
                InvokePrivateMethod(helper, "CompleteFileTransferFallbackNknProofIfPending", "file_transfer_v6_state_frame_received", sessionId));

            Assert.True(completed);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_fallback_nkn_proof_observed;", logTail, StringComparison.Ordinal);
            Assert.Contains("proof=file_transfer_v6_state_frame_received", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_post_tuna_fallback_nkn_proved;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_post_tuna_fallback_cleanup_completed;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_disable_handoff_completed;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_fallback_nkn_proof_waiting_for_v6_epoch;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferFallbackProof_SameEpochWaitingOverridesPriorRecoveredObservation()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.v6-proof-waiting.address");
            var helperClient = new FakeNknClient("helper.tuna.file.v6-proof-waiting.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-v6-proof-waiting-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-v6-proof-waiting-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_v6_proof_waiting";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            fakeLane.SetAvailable(false, "byte_cap_reached");
            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback),
                TimeSpan.FromSeconds(2));

            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    18,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "transport_probe_ack",
                    IsUnresolved: false));

            var waitingLogStart = GetOperationalLogLength();
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    18,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.WaitingForTargetTransport,
                    "proof_timeout",
                    IsUnresolved: true));

            var waitingTail = ReadOperationalLogTail(waitingLogStart);
            Assert.Contains("event=filetransfer_v6_epoch_observed;", waitingTail, StringComparison.Ordinal);
            Assert.Contains("state=waiting_for_target_transport", waitingTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_epoch_observation_ignored_final_fallback;", waitingTail, StringComparison.Ordinal);

            var fallbackState = GetPrivateField(helper, "tunaFallbackProofState");
            Assert.NotNull(fallbackState);
            var fallbackStateType = fallbackState.GetType();
            Assert.Equal(
                V6TransportEpochState.WaitingForTargetTransport,
                Assert.IsType<V6TransportEpochState>(fallbackStateType.GetProperty("FileV6EpochState")!.GetValue(fallbackState)));
            Assert.Equal(18L, Assert.IsType<long>(fallbackStateType.GetProperty("FileV6TransportEpoch")!.GetValue(fallbackState)));
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferFallbackProof_AllowsRegularNknRecoveryAfterRecoveredFallback()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.v6-proof-regular-recovery.address");
            var helperClient = new FakeNknClient("helper.tuna.file.v6-proof-regular-recovery.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-v6-proof-regular-recovery-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-v6-proof-regular-recovery-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_v6_proof_regular_recovery";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            fakeLane.SetAvailable(false, "byte_cap_reached");
            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback),
                TimeSpan.FromSeconds(2));

            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    21,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "transport_probe_ack",
                    IsUnresolved: false));

            var recoveryLogStart = GetOperationalLogLength();
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Inbound,
                    22,
                    FileTransferTransportHandoffKind.RegularNknRecovery,
                    FileTransferTransportKind.RegularNkn,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.TargetProofPending,
                    "all_channels_zero_receive_max_restarts",
                    IsUnresolved: true));

            var recoveryTail = ReadOperationalLogTail(recoveryLogStart);
            Assert.Contains("event=filetransfer_v6_epoch_observed;", recoveryTail, StringComparison.Ordinal);
            Assert.Contains("handoff_kind=regular_nkn_recovery", recoveryTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_epoch_observation_ignored_final_fallback;", recoveryTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferFallbackRecovered_UserStopStartsFreshFallbackAfterReactivation()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.peer-recovered-user-stop.address");
            var helperClient = new FakeNknClient("helper.tuna.file.peer-recovered-user-stop.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-peer-recovered-user-stop-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-peer-recovered-user-stop-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_peer_recovered_user_stop";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            var logStart = GetOperationalLogLength();
            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Inbound,
                    17,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "frontier_chunk_proof",
                    IsUnresolved: false));

            await ((ITransportAccelerationControl)helper).StopAccelerationAsync("header_switch_off", cts.Token);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=tuna_disable_handoff_started;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_fallback_recovered_state_synthesized;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_fallback_start_replaced_final_for_user_stop;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_fallback_started;", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=header_switch_off", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_disable_handoff_started;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_fallback_handoff_suppressed_duplicate;", logTail, StringComparison.Ordinal);
            Assert.Contains(
                availabilityEvents,
                e => !e.IsAvailable &&
                     e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                     e.TargetTransport == FileTransferTransportKind.RegularNkn);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferFallbackRecovered_FromPeerV6EpochSuppressesStaleTunaLaneAndReentry()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.peer-recovered-suppress.address");
            var helperClient = new FakeNknClient("helper.tuna.file.peer-recovered-suppress.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-peer-recovered-suppress-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-peer-recovered-suppress-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            var rawNknDataFrames = new ConcurrentQueue<NknIncomingMessage>();
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    e.Channel == NknBridgeChannel.Bulk &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.FileTransferDataFrame)
                {
                    rawNknDataFrames.Enqueue(e);
                }
            };

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_peer_recovered_suppress";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            var logStart = GetOperationalLogLength();
            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Inbound,
                    17,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "frontier_chunk_proof",
                    IsUnresolved: false));

            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);

            await WaitUntilAsync(() => rawNknDataFrames.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Empty(fakeLane.Sent);
            var fallbackEventCountBeforeLaneDrop = availabilityEvents.Count(e =>
                e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                !e.IsAvailable);

            fakeLane.SetAvailable(false, "remote_closed");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_fallback_start_suppressed_final;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_fallback_recovered_state_synthesized;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_file_acceleration_suppressed_regular_nkn_fallback;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_fallback_start_suppressed_final;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_fallback_handoff_suppressed_duplicate;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_fallback_started;", logTail, StringComparison.Ordinal);
            Assert.Equal(
                fallbackEventCountBeforeLaneDrop,
                availabilityEvents.Count(e =>
                    e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                    !e.IsAvailable));
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferFallbackRecovered_SuppressesAcceleratedFileBulkUntilFreshActivation()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.suppress.after.fallback.address");
            var helperClient = new FakeNknClient("helper.tuna.file.suppress.after.fallback.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-suppress-after-fallback-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-suppress-after-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            var rawNknDataFrames = new ConcurrentQueue<NknIncomingMessage>();
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    e.Channel == NknBridgeChannel.Bulk &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.FileTransferDataFrame)
                {
                    rawNknDataFrames.Enqueue(e);
                }
            };

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_suppress_after_fallback";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 0,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);
            await WaitUntilAsync(() => fakeLane.Sent.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Empty(rawNknDataFrames);

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                helper,
                "StartTunaFallbackProofAndRebindIfNeeded",
                "receive_stall_recovery",
                sessionId,
                NknAccelerationLaneKind.File);
            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    11,
                    FileTransferTransportHandoffKind.RegularNknRecovery,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "transport_probe_ack",
                    IsUnresolved: false));

            await dataSession.SendAsync(
                new FileTransferChunkBatchFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    StartChunkIndex = 1,
                    ChunkCount = 1,
                    DataSegments = new[] { new byte[1024] },
                    BatchProfile = "v4_default_21k",
                },
                cts.Token);

            await WaitUntilAsync(() => rawNknDataFrames.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Single(fakeLane.Sent);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=tuna_file_acceleration_suppressed_regular_nkn_fallback;", logTail, StringComparison.Ordinal);
            Assert.Contains("effective_transport=nkn", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferTunaActivationPause_PostFallbackRecoveryKeepsFallbackAvailabilityAuthoritative()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.activation.pause-recovery.address");
            var helperClient = new FakeNknClient("helper.tuna.activation.pause-recovery.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: false);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-activation-pause-recovery-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-activation-pause-recovery-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_activation_pause_recovery";
            var dataSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(
                host,
                "SetFileTransferDataSessionsAvailability",
                false,
                "sidecar_remote_closed",
                true,
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn);
            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.Reason == "sidecar_remote_closed"),
                TimeSpan.FromSeconds(2));

            InvokePrivateMethod(
                host,
                "PauseFileTransferDataSessionsForTunaActivationNegotiation",
                "selected_payer_starting_listener",
                sessionId,
                "runtime_unlock");

            await Task.Delay(200, cts.Token);

            Assert.False(dataSession.IsAvailable);
            Assert.DoesNotContain(
                availabilityEvents,
                e => !e.IsAvailable &&
                     !e.RequiresResumeRequest &&
                     e.Reason == "tuna_activation_negotiating");

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains(
                "event=filetransfer_tuna_activation_negotiation_post_tuna_fallback_pause_suppressed;",
                logTail,
                StringComparison.Ordinal);
            Assert.True(
                logTail.Contains("suppress_reason=active_post_tuna_fallback_route", StringComparison.Ordinal) ||
                logTail.Contains("suppress_reason=active_post_tuna_fallback_state", StringComparison.Ordinal) ||
                logTail.Contains("suppress_reason=unresolved_post_tuna_fallback_v6_epoch", StringComparison.Ordinal),
                "Expected activation pause suppression to be tied to active post-Tuna fallback evidence.");
            Assert.Contains("trigger=runtime_unlock", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_tuna_activation_negotiation_regular_nkn_paused;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_tuna_activation_negotiation_transport_recovered_suppressed;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RuntimeUnlockRecovery_WithFreshPostTunaFallbackReceiverProofDoesNotMarkFallbackUnavailable()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.runtime-unlock.protect-fallback.address");
            var helperClient = new FakeNknClient("helper.tuna.runtime-unlock.protect-fallback.address");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-runtime-unlock-protect-fallback-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-runtime-unlock-protect-fallback-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_runtime_unlock_protects_post_tuna_fallback";
            var dataSession = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            SetPrivateField(host, "outboundAccelerationOfferNonce", "runtime_unlock_protect_nonce");
            SetPrivateField(host, "outboundAccelerationOfferTrigger", "runtime_unlock");

            InvokePrivateMethod(
                host,
                "RecordTunaFallbackFileTransferDataFrameReceived",
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = 91,
                    Epoch = 2,
                    ContiguousCommittedChunkIndex = 8,
                    DurableReceivedHighestChunkIndex = 12,
                    CreditUntilChunkIndexExclusive = 24,
                    BytesCommitted = 8 * 64 * 1024L,
                    RecoveryMode = "post_tuna_fallback_survival",
                },
                NknBridgeChannel.Bulk,
                256,
                sessionId);

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "OnBridgeLifecycle",
                host,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "post_tuna_fallback_tuna_activation_offer_send_timeout"));

            await Task.Delay(150, cts.Token);

            Assert.True(dataSession.IsAvailable);
            Assert.DoesNotContain(
                availabilityEvents,
                e => !e.IsAvailable &&
                     e.Reason == "receive_stall_recovery");

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains(
                "event=filetransfer_post_tuna_fallback_runtime_unlock_recovery_availability_protected;",
                logTail,
                StringComparison.Ordinal);
            Assert.Contains("proof=receiver_state", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "event=filetransfer_data_session_availability_invoking; transfer_id=transfer_runtime_unlock_protects_post_tuna_fallback; session_id=",
                logTail,
                StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Theory]
    [InlineData("post_tuna_fallback_state_refresh_failed")]
    [InlineData("all_channels_zero_receive")]
    [Trait("Category", "Smoke")]
    public async Task FileTransferReceiveStallRecovery_ProcessReadyDoesNotStartFallbackProofBeforeRecoveryCompletes(string recoveryReason)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.file.receive-stall.process-ready.address");
            var helperClient = new FakeNknClient("helper.file.receive-stall.process-ready.address");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-file-process-ready-id", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("helper-file-process-ready-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_file_process_ready_deferred";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            InvokePrivateMethod(
                helper,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            InvokePrivateMethod(
                helper,
                "RecordTunaFallbackFileTransferDataFrameReceived",
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = 92,
                    Epoch = 2,
                    ContiguousCommittedChunkIndex = 3,
                    DurableReceivedHighestChunkIndex = 5,
                    CreditUntilChunkIndexExclusive = 16,
                    BytesCommitted = 3 * 64 * 1024L,
                    RecoveryMode = "post_tuna_fallback_survival",
                },
                NknBridgeChannel.Bulk,
                256,
                sessionId);
            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Inbound,
                    92,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "transport_probe_ack",
                    IsUnresolved: false));
            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: recoveryReason));

            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.Ready,
                    StartMode: BridgeStartMode.Cold,
                    Pid: 1234,
                    ReadyTimeMs: 100,
                    PingRttMs: 3,
                    UptimeMs: 100,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: null));

            await Task.Delay(150, cts.Token);

            Assert.Contains(
                availabilityEvents,
                e => !e.IsAvailable &&
                     e.Reason == "receive_stall_recovery" &&
                     !e.RequiresResumeRequest &&
                     e.HandoffKind == FileTransferTransportHandoffKind.None);
            Assert.DoesNotContain(
                availabilityEvents,
                e => e.Reason == "receive_stall_recovery" &&
                     e.RequiresResumeRequest);
            Assert.DoesNotContain(
                availabilityEvents,
                e => !e.IsAvailable &&
                     e.Reason == "transport_recovered_unproven");
            var processReadyLogTail = ReadOperationalLogTail(logStart);
            Assert.Contains(
                "event=filetransfer_post_tuna_fallback_receive_recovery_start_handoff_deferred;",
                processReadyLogTail,
                StringComparison.Ordinal);
            Assert.Contains(
                "event=filetransfer_bridge_ready_deferred_until_receive_stall_recovery_completed;",
                processReadyLogTail,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "event=filetransfer_fallback_nkn_probe_scheduled;",
                processReadyLogTail,
                StringComparison.Ordinal);

            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryCompleted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: recoveryReason));

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.Reason == "transport_recovered_unproven"),
                TimeSpan.FromSeconds(2));

            var completedLogTail = ReadOperationalLogTail(logStart);
            Assert.Contains(
                "event=filetransfer_fallback_nkn_recovery_completed_unproven;",
                completedLogTail,
                StringComparison.Ordinal);
            Assert.Contains(
                "event=filetransfer_fallback_nkn_probe_scheduled;",
                completedLogTail,
                StringComparison.Ordinal);
            Assert.Contains(
                "trigger=receive_stall_recovery_completed_unproven",
                completedLogTail,
                StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferReceiveStallRecovery_RegularNknPrimaryDoesNotStartV6Epoch()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.file.receive-stall.no-epoch.address");
            var helperClient = new FakeNknClient("helper.file.receive-stall.no-epoch.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-file-receive-stall-no-epoch-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-file-receive-stall-no-epoch-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_file_receive_stall_no_epoch";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "bulk_receive_stalled"));

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    !e.RequiresResumeRequest &&
                    e.Reason == "receive_stall_recovery" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.None),
                TimeSpan.FromSeconds(2));

            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.Ready,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: 100,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: null));

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    !e.RequiresResumeRequest &&
                    e.Reason == "transport_recovered" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.None),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_regular_nkn_receive_recovery_no_epoch;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_fallback_nkn_proof_pending;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_fallback_nkn_ready_unproven;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferReceiveStallRecovery_SessionLivenessPendingPreservesRegularNknAvailability()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.file.session-liveness-recovery.address");
            var helperClient = new FakeNknClient("helper.file.session-liveness-recovery.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-file-session-liveness-recovery-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-file-session-liveness-recovery-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_file_session_liveness_recovery";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "session_liveness_timeout_pending"));

            await Task.Delay(TimeSpan.FromMilliseconds(100), cts.Token);

            Assert.DoesNotContain(
                availabilityEvents,
                static e => !e.IsAvailable &&
                            (e.Reason == "session_liveness_timeout_pending" ||
                             e.Reason == "receive_stall_recovery"));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_session_liveness_receive_recovery_availability_preserved;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_data_session_availability_broadcast;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferReceiveStallRecovery_PreservesRegularNknEpochKindForDelayedProbe()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.receive-stall-kind.address");
            var helperClient = new FakeNknClient("helper.tuna.file.receive-stall-kind.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-receive-stall-kind-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-receive-stall-kind-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_receive_stall_kind";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            var directKind = Assert.IsType<FileTransferTransportHandoffKind>(
                InvokePrivateMethod(
                    helper,
                    "ResolveFileTransferDataSessionAvailabilityHandoffKind",
                    "bulk_receive_stalled",
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.RegularNkn));
            Assert.Equal(FileTransferTransportHandoffKind.RegularNknRecovery, directKind);

            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    23,
                    FileTransferTransportHandoffKind.RegularNknRecovery,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.TargetProofPending,
                    "receive_stall_recovery",
                    IsUnresolved: true));

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                helper,
                "SetFileTransferDataSessionsAvailability",
                false,
                "transport_recovered_unproven",
                true,
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn);

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.Reason == "transport_recovered_unproven" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.RegularNknRecovery &&
                    e.TargetTransport == FileTransferTransportKind.RegularNkn),
                TimeSpan.FromSeconds(2));

            Assert.DoesNotContain(
                availabilityEvents,
                e => e.Reason == "transport_recovered_unproven" &&
                     e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_availability_handoff_kind_preserved;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferReceiveStallRecovery_DuringUnresolvedTunaFallbackPreservesFallbackEpoch()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.receive-stall-during-fallback.address");
            var helperClient = new FakeNknClient("helper.tuna.file.receive-stall-during-fallback.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-receive-stall-during-fallback-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-receive-stall-during-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_receive_stall_during_fallback";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            InvokePrivateMethod(
                helper,
                "StartTunaFallbackProofAndRebindIfNeeded",
                "sidecar_remote_closed",
                sessionId,
                NknAccelerationLaneKind.File);

            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Inbound,
                    41,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.FrontierRepairOnly,
                    "sidecar_remote_closed",
                    IsUnresolved: true));

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "all_channels_zero_receive"));

            await Task.Delay(150, cts.Token);

            Assert.DoesNotContain(
                availabilityEvents,
                e => e.Reason == "receive_stall_recovery" &&
                     e.RequiresResumeRequest);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_receive_stall_recovery_preserved_tuna_fallback_epoch;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_post_tuna_fallback_receive_recovery_start_handoff_deferred;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_fallback_nkn_proof_pending;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferReceiveStallRecovery_AfterRecoveredTunaFallbackStartsRegularNknRecoveryEpoch()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.receive-stall-after-fallback.address");
            var helperClient = new FakeNknClient("helper.tuna.file.receive-stall-after-fallback.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-receive-stall-after-fallback-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-receive-stall-after-fallback-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_receive_stall_after_fallback";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    31,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "transport_probe_ack",
                    IsUnresolved: false));

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryStarted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "all_channels_zero_receive"));

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    !e.RequiresResumeRequest &&
                    e.Reason == "receive_stall_recovery" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.None &&
                    e.TargetTransport == FileTransferTransportKind.RegularNkn),
                TimeSpan.FromSeconds(2));

            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.Ready,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: 100,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: null));

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.Reason == "transport_recovered_unproven" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.RegularNknRecovery &&
                    e.TargetTransport == FileTransferTransportKind.RegularNkn),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_fallback_nkn_proof_pending;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_regular_nkn_receive_recovery_no_epoch;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferReceiveStallExhausted_WithActiveSessionBroadcastsRecoverableRegularNknEpoch()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.receive-stall-exhausted.address");
            var helperClient = new FakeNknClient("helper.tuna.file.receive-stall-exhausted.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-receive-stall-exhausted-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-receive-stall-exhausted-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_receive_stall_exhausted";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryExhausted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "control_receive_stalled_max_restarts"));

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.Reason == "control_receive_stalled_max_restarts" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.RegularNknRecovery &&
                    e.TargetTransport == FileTransferTransportKind.RegularNkn),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_control_receive_stall_recovery_broadcast;", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_control_receive_stall_terminal_broadcast;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferReceiveStallExhausted_RuntimeUnlockRecoveryFailureDoesNotSurfaceSessionDisconnect()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.runtime-unlock-recovery-exhausted.address");
            var helperClient = new FakeNknClient("helper.tuna.runtime-unlock-recovery-exhausted.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-runtime-unlock-recovery-exhausted-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-runtime-unlock-recovery-exhausted-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_runtime_unlock_recovery_exhausted";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            var lifecycleEvents = new ConcurrentQueue<BridgeLifecycleEvent>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            helper.BridgeLifecycle += (_, e) => lifecycleEvents.Enqueue(e);

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryExhausted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "tuna_activation_offer_send_timeout_recovery_failed"));

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.Reason == "tuna_activation_offer_send_timeout_recovery_failed" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.RegularNknRecovery &&
                    e.TargetTransport == FileTransferTransportKind.RegularNkn),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_control_receive_stall_recovery_broadcast;", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_activation_recovery_exhausted_session_disconnect_suppressed;", logTail, StringComparison.Ordinal);
            Assert.Contains(lifecycleEvents, e => e.Kind == BridgeLifecycleEventKind.ReceiveStallRecoveryDeferred);
            Assert.DoesNotContain(lifecycleEvents, e => e.Kind == BridgeLifecycleEventKind.ReceiveStallRecoveryExhausted);
            Assert.DoesNotContain("event=filetransfer_control_receive_stall_terminal_broadcast;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferReceiveStallExhausted_RuntimeUnlockRecoveryFailureWithRuntimeOnlyTransferDoesNotSurfaceSessionDisconnect()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.runtime-only-recovery-exhausted.address");
            var helperClient = new FakeNknClient("helper.tuna.runtime-only-recovery-exhausted.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-runtime-only-recovery-exhausted-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-runtime-only-recovery-exhausted-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_runtime_only_recovery_exhausted";
            var offerReceived = new TaskCompletionSource<FileTransferOfferV2>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acceptReceived = new TaskCompletionSource<FileTransferAcceptV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += (_, e) => offerReceived.TrySetResult(e.Message);
            helper.FileTransferAcceptReceived += (_, e) => acceptReceived.TrySetResult(e.Message);

            await helper.SendFileTransferOfferAsync(
                    new FileTransferOfferV2
                    {
                        SessionId = sessionId,
                        TransferId = transferId,
                        FileName = "runtime-only.bin",
                        FileSizeBytes = 1024,
                        PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                    },
                    cts.Token)
                .ConfigureAwait(false);
            await offerReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token).ConfigureAwait(false);
            await host.SendFileTransferAcceptAsync(
                    new FileTransferAcceptV1
                    {
                        SessionId = sessionId,
                        TransferId = transferId,
                        AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                    },
                    cts.Token)
                .ConfigureAwait(false);
            await acceptReceived.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token).ConfigureAwait(false);

            var lifecycleEvents = new ConcurrentQueue<BridgeLifecycleEvent>();
            helper.BridgeLifecycle += (_, e) => lifecycleEvents.Enqueue(e);

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryExhausted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "tuna_activation_offer_send_timeout_recovery_failed"));

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_activation_recovery_exhausted_session_disconnect_suppressed;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_control_receive_stall_recovery_broadcast;", logTail, StringComparison.Ordinal);
            Assert.Contains(lifecycleEvents, e => e.Kind == BridgeLifecycleEventKind.ReceiveStallRecoveryDeferred);
            Assert.DoesNotContain(lifecycleEvents, e => e.Kind == BridgeLifecycleEventKind.ReceiveStallRecoveryExhausted);
            Assert.DoesNotContain("event=filetransfer_control_receive_stall_terminal_broadcast;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferReceiveStallExhausted_AfterRecoveredRegularNknEpochUsesCooldown()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.receive-stall-exhausted-recovered.address");
            var helperClient = new FakeNknClient("helper.tuna.file.receive-stall-exhausted-recovered.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-receive-stall-exhausted-recovered-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-receive-stall-exhausted-recovered-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_receive_stall_exhausted_recovered";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);

            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryExhausted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "control_receive_stalled_max_restarts"));

            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.Reason == "control_receive_stalled_max_restarts" &&
                    e.HandoffKind == FileTransferTransportHandoffKind.RegularNknRecovery &&
                    e.TargetTransport == FileTransferTransportKind.RegularNkn),
                TimeSpan.FromSeconds(2));

            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    21,
                    FileTransferTransportHandoffKind.RegularNknRecovery,
                    FileTransferTransportKind.RegularNkn,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "transport_probe_ack",
                    IsUnresolved: false));

            var eventCountBeforeRepeat = availabilityEvents.Count;
            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                helper,
                "OnBridgeLifecycle",
                helper,
                new BridgeLifecycleEvent(
                    BridgeLifecycleEventKind.ReceiveStallRecoveryExhausted,
                    StartMode: null,
                    Pid: null,
                    ReadyTimeMs: null,
                    PingRttMs: null,
                    UptimeMs: null,
                    ExitCode: null,
                    ExitReasonKind: null,
                    ExitReasonText: "control_receive_stalled_max_restarts"));

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_control_receive_stall_recovery_broadcast_suppressed;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));
            Assert.Equal(eventCountBeforeRepeat, availabilityEvents.Count);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_control_receive_stall_recovery_after_recovered_epoch_allowed", logTail, StringComparison.Ordinal);
            Assert.Contains("suppress_reason=cooldown", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_control_receive_stall_terminal_broadcast;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FileTransferFallback_DoesNotRestartRecoveredV6EpochFromSecondarySidecarError()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.file.fallback.dedupe.address");
            var helperClient = new FakeNknClient("helper.tuna.file.fallback.dedupe.address");
            var fakeLane = new FakeNknAccelerationLane(isAvailable: true);
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-file-fallback-dedupe-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-file-fallback-dedupe-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            const string transferId = "transfer_tuna_file_fallback_dedupe";
            var dataSession = await helper.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var availabilityEvents = new ConcurrentQueue<FileTransferDataSessionAvailabilityChangedEventArgs>();
            dataSession.AvailabilityChanged += (_, e) => availabilityEvents.Enqueue(e);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File, sessionId);

            fakeLane.SetAvailable(false, "byte_cap_reached");
            await WaitUntilAsync(
                () => availabilityEvents.Any(e =>
                    !e.IsAvailable &&
                    e.RequiresResumeRequest &&
                    e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback),
                TimeSpan.FromSeconds(2));

            var observer = Assert.IsAssignableFrom<IFileTransferV6TransportEpochObserver>(helper);
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    13,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.Recovered,
                    "transport_probe_ack",
                    IsUnresolved: false));

            var staleObservationLogStart = GetOperationalLogLength();
            observer.ObserveFileTransferV6TransportEpoch(
                new FileTransferV6TransportEpochSnapshot(
                    sessionId,
                    transferId,
                    FileTransferDirection.Outbound,
                    14,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.Tuna,
                    FileTransferTransportKind.RegularNkn,
                    V6TransportEpochState.TargetProofPending,
                    "secondary_sidecar_error",
                    IsUnresolved: true));

            var staleObservationTail = ReadOperationalLogTail(staleObservationLogStart);
            Assert.Contains("event=filetransfer_v6_epoch_observation_ignored_final_fallback;", staleObservationTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_epoch_observed;", staleObservationTail, StringComparison.Ordinal);
            Assert.DoesNotContain("file_v6_epoch_state=target_proof_pending", staleObservationTail, StringComparison.Ordinal);

            var eventCountBeforeSecondaryError = availabilityEvents.Count(e =>
                e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback);
            var logStart = GetOperationalLogLength();

            InvokePrivateMethod(
                helper,
                "RebindFileTransferDataSessionsForTunaFallback",
                "sidecar_send_failed",
                sessionId,
                NknAccelerationLaneKind.File);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_fallback_handoff_suppressed_duplicate;", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));
            Assert.Equal(
                eventCountBeforeSecondaryError,
                availabilityEvents.Count(e => e.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback));
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockRecoveryContractRearmsLocalListenerDespiteUnavailableFlag()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.runtime-unlock-listener-rearm.address");
            var helperClient = new FakeNknClient("helper.tuna.runtime-unlock-listener-rearm.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: false,
                failedDialAttemptsBeforeSuccess: 0,
                allowListenerStartWhenCanListenFalse: true);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-runtime-unlock-listener-rearm-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-runtime-unlock-listener-rearm-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                77L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "runtime_unlock_retry_authority_offer_blocked",
                true);

            await WaitUntilAsync(
                () => host.IsAccelerationAvailableForTests && helper.IsAccelerationAvailableForTests,
                TimeSpan.FromSeconds(8));

            var tail = ReadOperationalLogTail(logStart);
            Assert.Contains("requires_local_listener_retry=1", tail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_listener_rearm_required;", tail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_listener_rearm_completed;", tail, StringComparison.Ordinal);
            Assert.Contains("event=runtime_unlock_offer_dispatched_after_listener_rearm;", tail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_payer_intent_queued; intent=will_listen", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_payer_intent_queued; intent=dialer_only; role=helpee; trigger=runtime_unlock", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_offer_preflight_rejected; reason=listener_unavailable; trigger=runtime_unlock", tail, StringComparison.Ordinal);
            Assert.Contains("event=tuna_acceleration_negotiated;", tail, StringComparison.Ordinal);
            Assert.True(hostLane.EnsureListenerCalls >= 1);
            Assert.InRange(helperLane.StartDialerCalls, 1, 2);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockListenerRearmFailureFailsContractWithoutRetryLoop()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.runtime-unlock-listener-rearm-fail.address");
            var helperClient = new FakeNknClient("helper.tuna.runtime-unlock-listener-rearm-fail.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-runtime-unlock-listener-rearm-fail-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-runtime-unlock-listener-rearm-fail-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);

            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                99L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "runtime_unlock_retry_authority_offer_blocked",
                true);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=session_recovery_contract_listener_rearm_failed;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(4));

            var tail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=session_recovery_contract_listener_rearm_required;", tail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_listener_rearm_failed;", tail, StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_failed;", tail, StringComparison.Ordinal);
            Assert.Contains("authority_failure_reason=runtime_unlock_listener_rearm_failed", tail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "event=tuna_acceleration_retry_scheduled; reason=runtime_unlock_listener_rearm_failed",
                tail,
                StringComparison.Ordinal);
            Assert.DoesNotContain("event=runtime_unlock_offer_dispatched_after_listener_rearm;", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_offer_queued; reason=runtime_unlock", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_negotiated;", tail, StringComparison.Ordinal);

            var contractProvider = Assert.IsAssignableFrom<ISessionRecoveryStateContract>(host);
            Assert.True(contractProvider.TryGetActiveSessionRecoveryContract(sessionId, out var snapshot));
            Assert.Equal(SessionRecoveryContractKind.RuntimeUnlockActivation, snapshot.Kind);
            Assert.Equal(SessionRecoveryContractState.Failed, snapshot.State);
            Assert.False(snapshot.RetryRequired);
            Assert.False(snapshot.RetryAuthorityPending);
            Assert.False(snapshot.ObservedSendPending);
            Assert.Equal("runtime_unlock_listener_rearm_failed", snapshot.AuthorityFailureReason);
            Assert.True(hostLane.EnsureListenerCalls >= 1);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockListenerRearmIsNotDeferredByRegularV4ReceiveRecovery()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.FromSeconds(20);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.runtime-unlock-listener-rearm-no-defer.address");
            var helperClient = new FakeNknClient("helper.tuna.runtime-unlock-listener-rearm-no-defer.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: false,
                failedDialAttemptsBeforeSuccess: 0,
                allowListenerStartWhenCanListenFalse: true);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-runtime-unlock-listener-rearm-no-defer-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-runtime-unlock-listener-rearm-no-defer-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            const string transferId = "transfer_tuna_activation_listener_rearm_defer";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            var recoveryRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.RegularNknV4FastToken,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 0,
                AuthorityReason = "regular_v4_startup_local_only_no_ack",
            };
            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessStarted",
                recoveryRequest,
                sessionId,
                transferId,
                "session_liveness_timeout_pending");

            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                88L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "regular_v4_unproven_recovery_escalation",
                true);
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationIfEligible", "runtime_unlock");

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=runtime_unlock_offer_dispatched_after_listener_rearm;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(8));

            var tail = ReadOperationalLogTail(logStart);
            var positiveTail = tail + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=session_recovery_contract_listener_rearm_required;", positiveTail, StringComparison.Ordinal);
            Assert.Contains(
                "event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_listener_rearm_allowed;",
                positiveTail,
                StringComparison.Ordinal);
            Assert.Contains("event=session_recovery_contract_listener_rearm_completed;", positiveTail, StringComparison.Ordinal);
            Assert.Contains("event=runtime_unlock_offer_dispatched_after_listener_rearm;", positiveTail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "event=session_recovery_contract_listener_rearm_deferred_for_regular_v4_recovery;",
                tail,
                StringComparison.Ordinal);
            Assert.DoesNotContain("authority_failure_reason=runtime_unlock_listener_rearm_failed", tail, StringComparison.Ordinal);

            var contractProvider = Assert.IsAssignableFrom<ISessionRecoveryStateContract>(host);
            Assert.True(contractProvider.TryGetActiveSessionRecoveryContract(sessionId, out var snapshot));
            Assert.Equal(SessionRecoveryContractKind.RuntimeUnlockActivation, snapshot.Kind);
            Assert.NotEqual(SessionRecoveryContractState.Failed, snapshot.State);
            Assert.True(snapshot.RetryDispatched);
            Assert.NotEqual("runtime_unlock_listener_rearm_failed", snapshot.AuthorityFailureReason);
            Assert.True(hostLane.EnsureListenerCalls >= 1);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportAccelerationOffer_RuntimeUnlockPostFallbackAuthorityBypassesStaleRegularV4ReceiveRecovery()
    {
        FakeNknClient.ResetNetwork();
        var previousHelpeePriorityDelay = NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests;
        var previousReceiveRecoveryBlocker = NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests;
        NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = TimeSpan.Zero;
        NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests =
            _ => "receive_stall_recovery_in_progress";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.runtime-unlock-post-fallback-stale-v4-blocker.address");
            var helperClient = new FakeNknClient("helper.tuna.runtime-unlock-post-fallback-stale-v4-blocker.address");
            var hostLane = new RetryableTunaAccelerationSession(
                canListen: false,
                failedDialAttemptsBeforeSuccess: 0,
                allowListenerStartWhenCanListenFalse: true);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-tuna-runtime-unlock-post-fallback-stale-v4-blocker-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-runtime-unlock-post-fallback-stale-v4-blocker-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            const string transferId = "transfer_tuna_activation_post_fallback_stale_v4_blocker";
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.PostTunaFallbackV6Token,
                FileTransferProtocol.ProtocolVersionV6,
                "test_post_tuna_fallback_route");
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);

            SetPrivateField(host, "accelerationNegotiationRetryAttempts", 0);
            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                88L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "post_tuna_fallback_state_refresh_failed",
                true);
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");
            InvokePrivateMethod(host, "ScheduleAccelerationNegotiationIfEligible", "runtime_unlock");

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=tuna_acceleration_runtime_unlock_dispatch_regular_v4_receive_recovery_post_fallback_authority_bypassed;",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(8));

            var tail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=session_recovery_contract_listener_rearm_completed;", tail, StringComparison.Ordinal);
            Assert.Contains("event=runtime_unlock_offer_dispatched_after_listener_rearm;", tail, StringComparison.Ordinal);
            Assert.Contains(
                "event=tuna_acceleration_runtime_unlock_dispatch_regular_v4_receive_recovery_post_fallback_authority_bypassed;",
                tail,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "event=tuna_acceleration_runtime_unlock_dispatch_deferred_for_regular_v4_receive_recovery;",
                tail,
                StringComparison.Ordinal);

            var contractProvider = Assert.IsAssignableFrom<ISessionRecoveryStateContract>(host);
            Assert.True(contractProvider.TryGetActiveSessionRecoveryContract(sessionId, out var snapshot));
            Assert.Equal(SessionRecoveryContractKind.RuntimeUnlockActivation, snapshot.Kind);
            Assert.True(snapshot.RetryDispatched);
            Assert.NotEqual("regular_v4_receive_recovery_pending", snapshot.AuthorityFailureReason);
            Assert.True(hostLane.EnsureListenerCalls >= 1);
        }
        finally
        {
            NknSignalingTransport.HelperPaidOfferHelpeePriorityDelayOverrideForTests = previousHelpeePriorityDelay;
            NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests = previousReceiveRecoveryBlocker;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RuntimeUnlockCutThroughPendingListenerStartupDoesNotExpireAuthorityBeforeListenerReady()
    {
        FakeNknClient.ResetNetwork();
        var previousAuthorityDeadline = NknSignalingTransport.RuntimeUnlockRetryAuthorityDeadlineOverrideForTests;
        NknSignalingTransport.RuntimeUnlockRetryAuthorityDeadlineOverrideForTests = TimeSpan.FromMilliseconds(25);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.runtime-unlock-cutthrough-listener-ready.address");
            var helperClient = new FakeNknClient("helper.tuna.runtime-unlock-cutthrough-listener-ready.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            var helperLane = new RetryableTunaAccelerationSession(canListen: false, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-runtime-unlock-cutthrough-listener-ready-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-runtime-unlock-cutthrough-listener-ready-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                helperLane);

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            const string transferId = "transfer_runtime_unlock_cutthrough_listener_ready";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            Assert.True(Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "HasActiveRegularV4FileTransferRouteHint",
                sessionId)));
            Assert.True(Assert.IsType<bool>(InvokePrivateMethod(
                host,
                "IsActiveRegularV4FileTransferRouteHint",
                sessionId,
                transferId)));

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                123L,
                sessionId,
                "runtime_unlock_offer_peer_response_timeout",
                "tuna_activation_offer_peer_response_timeout",
                false);
            InvokePrivateMethod(host, "MarkFileTransferTunaActivationBridgeRecoverySettled", "test_recovery_settled");
            var armedTail = ReadOperationalLogTail(logStart);
            var positiveArmedTail = armedTail + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=session_recovery_contract_started;", positiveArmedTail, StringComparison.Ordinal);
            Assert.True(
                positiveArmedTail.Contains("cutthrough_pending=1", StringComparison.Ordinal),
                positiveArmedTail);
            Assert.Contains("event=session_recovery_contract_retry_authority_granted;", positiveArmedTail, StringComparison.Ordinal);

            await Task.Delay(TimeSpan.FromMilliseconds(75), cts.Token);
            var contractProvider = Assert.IsAssignableFrom<ISessionRecoveryStateContract>(host);
            Assert.True(contractProvider.TryGetActiveSessionRecoveryContract(sessionId, out var beforeReady));
            Assert.Equal(SessionRecoveryContractKind.RuntimeUnlockActivation, beforeReady.Kind);
            Assert.NotEqual(SessionRecoveryContractState.Failed, beforeReady.State);
            Assert.True(
                beforeReady.RetryAuthorityPending ||
                beforeReady.State == SessionRecoveryContractState.Completed,
                $"state={beforeReady.State}; retry_required={beforeReady.RetryRequired}; retry_dispatching={beforeReady.RetryDispatching}; retry_dispatched={beforeReady.RetryDispatched}; retry_authority_granted={beforeReady.RetryAuthorityGranted}; observed_send_pending={beforeReady.ObservedSendPending}; authority_failure_reason={beforeReady.AuthorityFailureReason ?? "(none)"}");

            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractListenerRearmCompleted", sessionId, "runtime_unlock");

            Assert.True(contractProvider.TryGetActiveSessionRecoveryContract(sessionId, out var afterReady));
            Assert.Equal(SessionRecoveryContractKind.RuntimeUnlockActivation, afterReady.Kind);
            Assert.NotEqual(SessionRecoveryContractState.Failed, afterReady.State);
            Assert.True(afterReady.RetryAuthorityPending || afterReady.State == SessionRecoveryContractState.Completed);
            Assert.True(afterReady.RetryAuthorityGranted || afterReady.State == SessionRecoveryContractState.Completed);
            Assert.True(
                afterReady.ObservedSendDeadlineUtc > DateTimeOffset.UtcNow ||
                afterReady.State == SessionRecoveryContractState.Completed);

            var tail = ReadOperationalLogTail(logStart);
            var positiveTail = tail + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=session_recovery_contract_listener_rearm_completed;", positiveTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=session_recovery_contract_retry_authority_failed;", tail, StringComparison.Ordinal);
            Assert.DoesNotContain("authority_failure_reason=runtime_unlock_retry_authority_expired", tail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.RuntimeUnlockRetryAuthorityDeadlineOverrideForTests = previousAuthorityDeadline;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RuntimeUnlockCutThroughContractIsNotDowngradedByGenericRecoveryArm()
    {
        FakeNknClient.ResetNetwork();
        var previousSoftSettleDelay = NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests;
        NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = TimeSpan.FromSeconds(5);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.runtime-unlock-cutthrough-downgrade.address");
            var helperClient = new FakeNknClient("helper.tuna.runtime-unlock-cutthrough-downgrade.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-runtime-unlock-cutthrough-downgrade-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-runtime-unlock-cutthrough-downgrade-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            const string transferId = "transfer_runtime_unlock_cutthrough_downgrade";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");
            InvokePrivateMethod(
                host,
                "MarkFileTransferTunaActivationBridgeRecoveryStarted",
                "tuna_activation_offer_peer_response_timeout");

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                1234L,
                sessionId,
                "runtime_unlock_offer_peer_response_timeout",
                "tuna_activation_offer_peer_response_timeout",
                false);
            var initialTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=session_recovery_contract_started;", initialTail, StringComparison.Ordinal);
            Assert.Contains("retry_reason=runtime_unlock_offer_peer_response_timeout", initialTail, StringComparison.Ordinal);
            Assert.Contains("cutthrough_pending=1", initialTail, StringComparison.Ordinal);

            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                1234L,
                sessionId,
                "runtime_unlock_offer_send_not_observed",
                "regular_v4_unproven_recovery_escalation",
                true);

            var contractProvider = Assert.IsAssignableFrom<ISessionRecoveryStateContract>(host);
            Assert.True(contractProvider.TryGetActiveSessionRecoveryContract(sessionId, out var snapshot));
            Assert.Equal(SessionRecoveryContractKind.RuntimeUnlockActivation, snapshot.Kind);
            Assert.Equal("runtime_unlock_offer_peer_response_timeout", snapshot.RetryReason);
            Assert.Equal("tuna_activation_offer_peer_response_timeout", snapshot.RecoveryReason);

            var tail = ReadOperationalLogTail(logStart);
            Assert.Contains(
                "event=tuna_acceleration_runtime_unlock_retry_after_recovery_arm_downgrade_ignored;",
                tail,
                StringComparison.Ordinal);
            Assert.Contains("preserved_retry_reason=runtime_unlock_offer_peer_response_timeout", tail, StringComparison.Ordinal);
            Assert.Contains("cutthrough_pending=1", tail, StringComparison.Ordinal);
        }
        finally
        {
            NknSignalingTransport.RuntimeUnlockRecoverySoftSettleDelayOverrideForTests = previousSoftSettleDelay;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RuntimeUnlockCutThroughPendingBypassesRegularV4ReceiveProofDeferral()
    {
        FakeNknClient.ResetNetwork();
        var previousReceiveRecoveryBlocker = NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests;
        NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests =
            _ => "receive_stall_recovery_in_progress";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.runtime-unlock-cutthrough-regularv4.address");
            var helperClient = new FakeNknClient("helper.tuna.runtime-unlock-cutthrough-regularv4.address");
            var hostLane = new RetryableTunaAccelerationSession(canListen: true, failedDialAttemptsBeforeSuccess: 0);
            using var host = new NknSignalingTransport(
                hostClient,
                options,
                new NknIdentity("host-runtime-unlock-cutthrough-regularv4-id", hostClient.Address),
                NknTunaAccelerationOptions.Disabled,
                hostLane);
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-runtime-unlock-cutthrough-regularv4-id", helperClient.Address));

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            const string transferId = "transfer_runtime_unlock_cutthrough_regularv4";
            _ = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, cts.Token);
            InvokePrivateMethod(
                host,
                "TrackFileTransferRouteHint",
                transferId,
                FileTransferRouteResolver.RegularNknV4FastToken,
                FileTransferProtocol.ProtocolVersionV4,
                "test_regular_route");

            var recoveryRequest = new FileTransferReceiveRecoveryRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.RegularNknV4FastToken,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 0,
                AuthorityReason = "regular_v4_startup_local_only_no_ack",
            };
            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessStarted",
                recoveryRequest,
                sessionId,
                transferId,
                "session_liveness_timeout_pending");
            InvokePrivateMethod(
                host,
                "MarkFileTransferRegularV4RecoveryLivenessBridgeRecoveryLifecycle",
                "completed",
                "test_regular_v4_recovery_completed_without_receive_proof");
            InvokePrivateMethod(
                host,
                "MarkFileTransferTunaActivationBridgeRecoveryStarted",
                "tuna_activation_offer_peer_response_timeout");
            InvokePrivateMethod(
                host,
                "ArmRuntimeUnlockRetryAfterRecovery",
                1234L,
                sessionId,
                "runtime_unlock_offer_peer_response_timeout",
                "tuna_activation_offer_peer_response_timeout",
                false);

            var logStart = GetOperationalLogLength();
            InvokePrivateMethod(
                host,
                "MarkFileTransferTunaActivationBridgeRecoverySettled",
                "cutthrough_regular_v4_recovery_settled");

            var matchedCutThroughTail = string.Empty;
            await WaitUntilAsync(
                () =>
                {
                    var tail = ReadOperationalLogTail(logStart);
                    if (tail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_regular_v4_cutthrough_allowed;", StringComparison.Ordinal) &&
                           tail.Contains("reason=cutthrough_must_precede_receive_proof", StringComparison.Ordinal) &&
                           tail.Contains("trigger=cutthrough_regular_v4_recovery_settled", StringComparison.Ordinal) &&
                           tail.Contains("event=tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled;", StringComparison.Ordinal) &&
                           tail.Contains("retry_reason=runtime_unlock_offer_peer_response_timeout", StringComparison.Ordinal))
                    {
                        matchedCutThroughTail = tail;
                        return true;
                    }

                    return false;
                },
                TimeSpan.FromSeconds(3));

            var logTail = matchedCutThroughTail + Environment.NewLine + ReadOperationalLogTail(logStart);
            Assert.Contains("reason=cutthrough_must_precede_receive_proof", logTail, StringComparison.Ordinal);
            Assert.Contains("trigger=cutthrough_regular_v4_recovery_settled", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("reason=awaiting_validated_filetransfer_receive_proof", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=tuna_acceleration_runtime_unlock_retry_after_recovery_deferred_for_regular_v4_receive_proof;", logTail, StringComparison.Ordinal);

            var contractProvider = Assert.IsAssignableFrom<ISessionRecoveryStateContract>(host);
            Assert.True(contractProvider.TryGetActiveSessionRecoveryContract(sessionId, out var snapshot));
            Assert.Equal(SessionRecoveryContractKind.RuntimeUnlockActivation, snapshot.Kind);
            Assert.Equal("runtime_unlock_offer_peer_response_timeout", snapshot.RetryReason);
            Assert.True(snapshot.RetryRequired || snapshot.RetryDispatched);
            Assert.True(snapshot.RetryAuthorityGranted);

            InvokePrivateMethod(host, "MarkRuntimeUnlockRecoveryContractRetryDispatched", "runtime_unlock");

            var method = typeof(NknSignalingTransport).GetMethod(
                "TryDeferRuntimeUnlockOfferDispatchForRegularV4ReceiveRecovery",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var methodArgs = new object?[] { sessionId, "runtime_unlock", 44L, null, 0L };
            var deferred = Assert.IsType<bool>(method!.Invoke(host, methodArgs));

            var dispatchTail = ReadOperationalLogTail(logStart);
            Assert.False(deferred);
            Assert.Equal("receive_stall_recovery_in_progress", Assert.IsType<string>(methodArgs[3]));
            Assert.Contains(
                "event=tuna_acceleration_runtime_unlock_dispatch_regular_v4_receive_recovery_authority_bypassed;",
                dispatchTail,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "event=tuna_acceleration_runtime_unlock_dispatch_deferred_for_regular_v4_receive_recovery;",
                dispatchTail,
                StringComparison.Ordinal);

            InvokePrivateMethod(
                host,
                "MarkFileTransferTunaActivationBridgeRecoveryStarted",
                "stale_recovery_started_after_cutthrough_dispatch");
            InvokePrivateMethod(
                host,
                "MarkFileTransferTunaActivationBridgeRecoverySettled",
                "stale_recovery_settled_after_cutthrough_dispatch");

            var staleSettleTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain(
                "authority_failure_reason=regular_v4_receive_recovery_pending",
                staleSettleTail,
                StringComparison.Ordinal);
            Assert.True(contractProvider.TryGetActiveSessionRecoveryContract(sessionId, out var afterStaleSettle));
            Assert.NotEqual(SessionRecoveryContractState.Failed, afterStaleSettle.State);
            Assert.Equal("runtime_unlock_offer_peer_response_timeout", afterStaleSettle.RetryReason);
            Assert.NotEqual("regular_v4_receive_recovery_pending", afterStaleSettle.AuthorityFailureReason);
        }
        finally
        {
            NknSignalingTransport.RuntimeUnlockRegularV4ReceiveRecoveryBlockerOverrideForTests = previousReceiveRecoveryBlocker;
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ChatMessage_StaysOnNknAfterAccelerationAccepted()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.tuna.chat.address");
            var helperClient = new FakeNknClient("helper.tuna.chat.address");
            var fakeLane = new FakeNknAccelerationLane();
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("host-tuna-chat-id", hostClient.Address));
            using var helper = new NknSignalingTransport(
                helperClient,
                options,
                new NknIdentity("helper-tuna-chat-id", helperClient.Address),
                NknTunaAccelerationOptions.Disabled,
                fakeLane);
            var rawNknChat = new ConcurrentQueue<NknIncomingMessage>();
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.Chat)
                {
                    rawNknChat.Enqueue(e);
                }
            };

            var sessionId = await ApproveNknSessionAsync(
                host,
                helper,
                cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer | InviteCapabilities.ScreenShare);
            helper.SetAccelerationAcceptedForTests(NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen, sessionId);

            await helper.SendChatMessageAsync(new byte[] { 1, 2, 3, 4 }, cts.Token);

            await WaitUntilAsync(() => rawNknChat.Count == 1, TimeSpan.FromSeconds(2));
            Assert.Empty(fakeLane.Sent);
            Assert.Equal(NknBridgeChannel.Control, rawNknChat.Single().Channel);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    private static TransportAccelerationOfferPayload CreateOfferPayload(
        string sessionId,
        string nonce,
        string[]? supportedLanes = null,
        long? expiresAtUnixMs = null,
        int? sidecarProtocolVersion = null,
        long payerDecisionId = 0)
        => new()
        {
            SessionId = sessionId,
            SenderRole = "helper",
            TunaAddress = "nlink-tuna-sidecar.test-offer-address",
            SupportedLanes = supportedLanes ?? new[] { "file", "screen" },
            PayerDecisionId = payerDecisionId,
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ExpiresAtUnixMs = expiresAtUnixMs ?? DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
            Nonce = nonce,
            SidecarProtocolVersion = sidecarProtocolVersion ?? 1,
        };

    private static TransportAccelerationAnswerPayload CreateAnswerPayload(
        string sessionId,
        string nonce,
        bool accepted,
        string[]? supportedLanes = null,
        long? expiresAtUnixMs = null,
        int? sidecarProtocolVersion = null,
        string? rejectReason = null,
        long payerDecisionId = 0)
        => new()
        {
            SessionId = sessionId,
            Accepted = accepted,
            SupportedLanes = supportedLanes ?? (accepted ? new[] { "file", "screen" } : Array.Empty<string>()),
            ExpiresAtUnixMs = expiresAtUnixMs ?? DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(),
            Nonce = nonce,
            SidecarProtocolVersion = sidecarProtocolVersion ?? 1,
            RejectReason = rejectReason,
            PayerDecisionId = payerDecisionId,
        };

    private static TransportAccelerationDownPayload CreateDownPayload(string sessionId, string nonce, long payerDecisionId = 0)
        => new()
        {
            SessionId = sessionId,
            SupportedLanes = new[] { "file", "screen" },
            SentAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Nonce = nonce,
            SidecarProtocolVersion = 1,
            Reason = "read_failed",
            PayerDecisionId = payerDecisionId,
        };

    private static Envelope BuildSecureAccelerationEnvelope<TPayload>(
        NknSignalingTransport senderTransport,
        MsgType msgType,
        TPayload payload,
        string secureMessageType,
        string requestId,
        long sequence)
    {
        var key = Assert.IsType<byte[]>(GetPrivateField(senderTransport, "controlSessionSharedKey")).AsSpan().ToArray();
        var envelopeCode = Assert.IsType<string>(GetPrivateField(senderTransport, "currentEnvelopeCode"));
        var sessionId = Assert.IsType<SessionId>(senderTransport.CurrentSessionSecurityState.SessionId);
        var securePayload = SessionSecureEnvelopeCodec.Encrypt(
            key,
            new SessionSecureEnvelopeMetadata(
                Family: SessionSecureMessageFamily.RemoteControl,
                MessageType: secureMessageType,
                SessionId: sessionId,
                SenderIdentity: new PeerAddress(senderTransport.LocalPeerAddress),
                Sequence: sequence,
                RequestId: requestId),
            JsonSerializer.SerializeToUtf8Bytes(payload));

        return new Envelope(
            Version: 1,
            Code: envelopeCode,
            MessageId: Guid.NewGuid().ToString("N"),
            Type: msgType,
            Payload: securePayload,
            UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyTo: null);
    }

    private static void AssertNknRoute(
        NknSignalingTransport transport,
        FileTransferRoute expectedRoute,
        int expectedProtocolVersion)
    {
        var selection = FileTransferRouteResolver.Resolve(FileTransferRouteResolverInput.FromTransport(transport));

        Assert.Equal(expectedRoute, selection.Route);
        Assert.Equal(expectedProtocolVersion, selection.ProtocolVersion);
    }

    private sealed class RecordingTunaListenerSidecarSupervisor : INknTunaListenerSidecarSupervisor
    {
        public List<string> StopReasons { get; } = [];

        public bool CanOfferListener => true;

        public Task<NknTunaListenerSidecarEndpoint?> EnsureStartedAsync(
            NknTunaListenerStartRequest request,
            CancellationToken ct)
            => Task.FromResult<NknTunaListenerSidecarEndpoint?>(null);

        public void Stop(string reason)
            => StopReasons.Add(reason);

        public void Dispose()
        {
        }
    }

    private sealed class RetryableTunaAccelerationSession : INknTunaAccelerationSession
    {
        private int canListen;
        private readonly int failedDialAttemptsBeforeSuccess;
        private readonly int failedListenerAttemptsBeforeSuccess;
        private readonly NknAccelerationLaneKind supportedLanes;
        private readonly bool deferSupportedLanesUntilAvailable;
        private readonly bool allowListenerStartWhenCanListenFalse;
        private int available;
        private int ensureListenerCalls;
        private int startDialerCalls;
        private int stopCalls;
        private string? lastStopReason;

        public RetryableTunaAccelerationSession(
            bool canListen,
            int failedDialAttemptsBeforeSuccess,
            NknAccelerationLaneKind supportedLanes = NknAccelerationLaneKind.File | NknAccelerationLaneKind.Screen,
            bool deferSupportedLanesUntilAvailable = false,
            int failedListenerAttemptsBeforeSuccess = 0,
            bool allowListenerStartWhenCanListenFalse = false)
        {
            this.canListen = canListen ? 1 : 0;
            this.failedDialAttemptsBeforeSuccess = failedDialAttemptsBeforeSuccess;
            this.failedListenerAttemptsBeforeSuccess = failedListenerAttemptsBeforeSuccess;
            this.supportedLanes = supportedLanes;
            this.deferSupportedLanesUntilAvailable = deferSupportedLanesUntilAvailable;
            this.allowListenerStartWhenCanListenFalse = allowListenerStartWhenCanListenFalse;
        }

        public bool IsAvailable => Volatile.Read(ref available) != 0;

        public bool CanOfferListener => Volatile.Read(ref canListen) != 0;

        public NknAccelerationLaneKind ConfiguredLanes => supportedLanes;

        public NknAccelerationLaneKind SupportedLanes
            => deferSupportedLanesUntilAvailable && !IsAvailable
                ? NknAccelerationLaneKind.None
                : supportedLanes;

        public string? LocalTunaAddress { get; private set; }

        public bool IsLocalPaidListenerActive { get; private set; }

        public int EnsureListenerCalls => Volatile.Read(ref ensureListenerCalls);

        public int StartDialerCalls => Volatile.Read(ref startDialerCalls);

        public int StopCalls => Volatile.Read(ref stopCalls);

        public string? LastStopReason => Volatile.Read(ref lastStopReason);

        public void SetCanListen(bool value)
            => Volatile.Write(ref canListen, value ? 1 : 0);

        public void MarkListenerAvailableForTests()
        {
            LocalTunaAddress = "nlink-tuna-sidecar.test-listener-address";
            IsLocalPaidListenerActive = true;
            Volatile.Write(ref available, 1);
        }

        public event EventHandler<NknIncomingMessage>? MessageReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<AccelerationStateChangedEventArgs>? StateChanged;

        public NknAccelerationLaneDiagnostics GetDiagnosticsSnapshot()
            => new(IsAvailable, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, string.Empty, 0);

        public Task<bool> EnsureListenerSidecarConnectedAsync(string expectedRemotePeer, CancellationToken ct)
        {
            var calls = Interlocked.Increment(ref ensureListenerCalls);
            if ((!CanOfferListener && !allowListenerStartWhenCanListenFalse) || ct.IsCancellationRequested)
            {
                return Task.FromResult(false);
            }

            if (calls <= failedListenerAttemptsBeforeSuccess)
            {
                return Task.FromResult(false);
            }

            LocalTunaAddress = "nlink-tuna-sidecar.test-listener-address";
            IsLocalPaidListenerActive = true;
            MarkAvailable("listener_ready");
            return Task.FromResult(true);
        }

        public Task<bool> StartDialerSidecarAsync(string tunaAddress, string expectedRemotePeer, CancellationToken ct)
        {
            var calls = Interlocked.Increment(ref startDialerCalls);
            if (ct.IsCancellationRequested || calls <= failedDialAttemptsBeforeSuccess)
            {
                return Task.FromResult(false);
            }

            LocalTunaAddress = "nlink-tuna-sidecar.test-dialer-address";
            IsLocalPaidListenerActive = false;
            MarkAvailable("dialer_ready");
            return Task.FromResult(true);
        }

        public Task<bool> TrySendAsync(NknBridgeChannel lane, byte[] envelopeBytes, CancellationToken ct)
            => Task.FromResult(false);

        public Task StopAsync(string reason, CancellationToken ct)
        {
            Interlocked.Increment(ref stopCalls);
            Volatile.Write(ref lastStopReason, reason);
            Volatile.Write(ref available, 0);
            IsLocalPaidListenerActive = false;
            StateChanged?.Invoke(this, new AccelerationStateChangedEventArgs(false, reason));
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Volatile.Write(ref available, 0);
            IsLocalPaidListenerActive = false;
        }

        private void MarkAvailable(string reason)
        {
            if (Interlocked.Exchange(ref available, 1) == 0)
            {
                StateChanged?.Invoke(this, new AccelerationStateChangedEventArgs(true, reason));
            }
        }
    }
}
