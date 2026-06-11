using System.Reflection;
using System.Security.Cryptography;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferV4SenderTests : SessionFileTransferServiceTestBase
{
    private const string RetiredV4CreditRepairRuntimeSkip =
        "Retired: Phase 3 V6 receiver-driven runtime no longer uses V4 credit/repair scheduling; covered by SessionFileTransferV6RuntimeTests.";
    private const string DeferredV6TransportEpochRuntimeSkip =
        "Deferred: Phase 4/5 will replace these transport epoch and Tuna handoff expectations with proof-based V6 tests.";
    private const string RetiredPostTunaFallbackSparseRuntimeSkip =
        "Retired: Phase 2 routes post-Tuna fallback through the default V6 runtime instead of the legacy sparse regular-NKN runtime.";
    private const string RetiredRegularNknV4ToV6RecoverySkip =
        "Retired: regular NKN now remains on V4; primary regular-NKN V6 recovery is diagnostic-only.";
    private const string RetiredDiagnosticRegularNknV6SparseCreditRuntimeSkip =
        "Retired: Phase 3 diagnostic regular-NKN V6 uses receiver-driven proof instead of the old V4 sparse-credit feedback assumptions.";

    [Fact]
    public async Task OutboundOffer_BindsCurrentTransportSessionIdBeforeRouteProof()
    {
        const string sessionId = "session_outbound_offer_route_proof";
        const string transferId = "transfer_outbound_offer_route_proof";
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        var snapshot = await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("route-proof-session.bin", 1024L, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[1024], writable: false)),
            CancellationToken.None);

        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = Assert.Single(senderTransport.SentOffers);
        Assert.Equal(sessionId, offer.SessionId);
        Assert.Equal(sessionId, snapshot!.SessionId);
        Assert.Equal(sessionId, sender.Snapshot.Outbound!.SessionId);
    }

    [Fact]
    public async Task OutboundOffer_InitialSendTimeoutLeavesTransferAwaitingAcceptanceAndRetries()
    {
        const string sessionId = "session_outbound_offer_retry_after_timeout";
        const string transferId = "transfer_outbound_offer_retry_after_timeout";
        var logStart = GetOperationalLogLength();
        var offerAttempts = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        senderTransport.OutboundOfferDeliveryOverrideAsync = async (_, _, ct) =>
        {
            if (Interlocked.Increment(ref offerAttempts) == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return true;
            }

            return false;
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        try
        {
            SessionFileTransferService.OfferSendTimeoutOverrideForTests = TimeSpan.FromMilliseconds(50);
            var snapshot = await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("retry-after-offer-timeout.bin", 1024L, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(new byte[1024], writable: false)),
                CancellationToken.None);

            Assert.Equal(FileTransferTransferState.AwaitingAcceptance, snapshot!.State);
            Assert.Equal(sessionId, snapshot.SessionId);
            await WaitUntilAsync(() => Volatile.Read(ref offerAttempts) >= 2, timeoutMs: 5000);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
            Assert.True(senderTransport.SentOffers.Count >= 2);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_offer_send_timed_out;", logTail, StringComparison.Ordinal);
            Assert.Contains("source=initial", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_offer_retry_sent;", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.OfferSendTimeoutOverrideForTests = null;
        }
    }

    [Fact]
    public async Task InboundRegularV4_TunaFallbackHandoffPromotesToPostTunaFallbackV6()
    {
        const string sessionId = "session_inbound_regular_v4_direct_fallback";
        const string transferId = "transfer_inbound_regular_v4_direct_fallback";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 768_000).Select(static index => (byte)(index % 251)).ToArray();
        var droppedInitialChunkBatchCount = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame.TransferId == transferId &&
                frame is FileTransferChunkBatchFrameV4)
            {
                Interlocked.Increment(ref droppedInitialChunkBatchCount);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("inbound-regular-v4-direct-fallback.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Receiving,
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => Volatile.Read(ref droppedInitialChunkBatchCount) > 0,
            timeoutMs: 5000);

        receiverTransport.SetConnectedDataSessionsUnavailableForTests(
            "sidecar_remote_closed",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_route_transitioned; direction=inbound;",
                StringComparison.Ordinal) &&
                ReadOperationalLogTail(logStart).Contains(
                    "previous_route=regular_nkn_v4_fast; new_route=post_tuna_fallback_v6",
                StringComparison.Ordinal),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_route_selected; direction=inbound;", log, StringComparison.Ordinal);
        Assert.Contains("route=post_tuna_fallback_v6; protocol_version=6;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_epoch_started; direction=inbound;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("new_route=file_tuna_v6", log, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundRegularV4_RegularNknRecoveryDoesNotPromoteToPostTunaFallbackV6()
    {
        var routeSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var shouldPromote = typeof(SessionFileTransferService)
            .GetMethod("ShouldPromoteRegularNknV4FallbackToPostTunaV6", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(
                null,
                [
                    routeSelection.RuntimeDescriptor,
                    FileTransferProtocol.ProtocolVersionV4,
                    "transport_recovered_unproven",
                    FileTransferTransportHandoffKind.RegularNknRecovery,
                    FileTransferTransportKind.RegularNkn,
                ]);

        Assert.False((bool)shouldPromote!);
    }

    [Fact]
    public void RegularNknV4Pressure_ReportsStaleRemoteFrontierEvenWhenCreditRemains()
    {
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport("session_regular_v4_remote_frontier_pressure");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            "transfer_regular_v4_remote_frontier_pressure",
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 512,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));

        typeof(SessionFileTransferService)
            .GetMethod("MaybeObserveRegularV4ControlFeedbackPressure", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, 0L, 256, 0]);

        var pressure = Assert.Single(transport.RegularV4ControlFeedbackPressures);
        Assert.Equal("transfer_regular_v4_remote_frontier_pressure", pressure.TransferId);
        Assert.Equal("regular_v4_sender_remote_frontier_pressure", pressure.Reason);
        Assert.Equal(156, pressure.FrontierLagChunks);
        Assert.Equal(0, pressure.PendingRepairCount);
    }

    [Fact]
    public void RegularNknV4Pressure_ArmsPeerSilenceReplayForStaleRemoteFrontier()
    {
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport("session_regular_v4_remote_frontier_replay");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            "transfer_regular_v4_remote_frontier_replay",
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 512,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullV4LastPeerFrameReceivedUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        typeof(SessionFileTransferService)
            .GetMethod("MaybeObserveRegularV4ControlFeedbackPressure", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, 0L, 256, 0]);

        var pressure = Assert.Single(transport.RegularV4ControlFeedbackPressures);
        Assert.Equal("regular_v4_sender_remote_frontier_pressure", pressure.Reason);
        var replay = Assert.Single(GetQueuedV4RepairSends(context));
        Assert.Equal(100, GetIntProperty(replay, "FirstStartChunkIndex"));
        Assert.Equal(164, GetIntProperty(replay, "LastEndChunkExclusive"));
        Assert.Equal("regular_v4_peer_silence_safety_replay", GetStringProperty(replay, "DeliveryEscalationReason"));

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_regular_v4_feedback_pressure_replay_armed;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("file_tuna_v6", log, StringComparison.Ordinal);
    }

    [Fact]
    public void RegularNknV4Pressure_ReportsRemoteFrontierRepairPressureBeforeCreditExhaustion()
    {
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport("session_regular_v4_repair_pressure");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            "transfer_regular_v4_repair_pressure",
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 512,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);

        typeof(SessionFileTransferService)
            .GetMethod("MaybeObserveRegularV4ControlFeedbackPressure", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, 0L, 256, 12]);

        var pressure = Assert.Single(transport.RegularV4ControlFeedbackPressures);
        Assert.Equal("transfer_regular_v4_repair_pressure", pressure.TransferId);
        Assert.Equal("regular_v4_sender_remote_frontier_pressure", pressure.Reason);
        Assert.Equal(156, pressure.FrontierLagChunks);
        Assert.Equal(12, pressure.PendingRepairCount);
    }

    [Fact]
    public void RegularNknV4Pressure_DoesNotReportHealthyAheadCreditAsPressure()
    {
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport("session_regular_v4_healthy_ahead_credit");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            "transfer_regular_v4_healthy_ahead_credit",
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 512,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);

        typeof(SessionFileTransferService)
            .GetMethod("MaybeObserveRegularV4ControlFeedbackPressure", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, 0L, 256, 0]);

        Assert.Empty(transport.RegularV4ControlFeedbackPressures);
    }

    [Fact]
    public void RegularNknV4Pressure_RefreshesStaleRemoteFrontierDuringIdlePumpSummary()
    {
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport("session_regular_v4_idle_frontier_pressure");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            "transfer_regular_v4_idle_frontier_pressure",
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 512,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(20));

        var method = typeof(SessionFileTransferService)
            .GetMethod("MaybeLogOutboundV4SenderPumpSummaryLocked", BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(service, [context, DateTimeOffset.UtcNow, false]);
        method.Invoke(service, [context, DateTimeOffset.UtcNow, false]);

        var pressure = Assert.Single(transport.RegularV4ControlFeedbackPressures);
        Assert.Equal("transfer_regular_v4_idle_frontier_pressure", pressure.TransferId);
        Assert.Equal("regular_v4_sender_remote_frontier_pressure", pressure.Reason);
        Assert.Equal(156, pressure.FrontierLagChunks);
        Assert.Equal(0, pressure.PendingRepairCount);
    }

    [Fact]
    public void RegularNknV4PeerSilenceSafetyReplay_RollsForwardAcrossAcceptedTail()
    {
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport("session_regular_v4_peer_silence_replay");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            "transfer_regular_v4_peer_silence_replay",
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 256,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullV4LastPeerFrameReceivedUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var method = typeof(SessionFileTransferService)
            .GetMethod("MaybeQueueOutboundRegularNknV4PeerSilenceSafetyReplayLocked", BindingFlags.Instance | BindingFlags.NonPublic)!;

        method.Invoke(service, [context, "regular_v4_sender_wait"]);

        var firstReplay = Assert.Single(GetQueuedV4RepairSends(context));
        Assert.Equal(100, GetIntProperty(firstReplay, "FirstStartChunkIndex"));
        Assert.Equal(164, GetIntProperty(firstReplay, "LastEndChunkExclusive"));
        Assert.Equal("regular_v4_peer_silence_safety_replay", GetStringProperty(firstReplay, "DeliveryEscalationReason"));

        SetPrivateProperty(context, "PullRegularNknV4LastPeerSilenceSafetyReplayUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        method.Invoke(service, [context, "regular_v4_sender_wait"]);

        var queued = GetQueuedV4RepairSends(context);
        Assert.Equal(2, queued.Length);
        Assert.Equal(164, GetIntProperty(queued[1], "FirstStartChunkIndex"));
        Assert.Equal(228, GetIntProperty(queued[1], "LastEndChunkExclusive"));

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_regular_v4_peer_silence_safety_replay_started;", log, StringComparison.Ordinal);
        Assert.Contains("route=regular_nkn_v4_fast; protocol_version=4", log, StringComparison.Ordinal);
        Assert.DoesNotContain("file_tuna_v6", log, StringComparison.Ordinal);
    }

    [Fact]
    public void RegularNknV4PeerSilenceSafetyReplay_DoesNotQueueBeforeSilenceWindow()
    {
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport("session_regular_v4_peer_silence_replay_fresh");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            "transfer_regular_v4_peer_silence_replay_fresh",
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 256,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullV4LastPeerFrameReceivedUtc", DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        typeof(SessionFileTransferService)
            .GetMethod("MaybeQueueOutboundRegularNknV4PeerSilenceSafetyReplayLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "regular_v4_sender_wait"]);

        Assert.Empty(GetQueuedV4RepairSends(context));
    }

    [Fact]
    public void FileTunaV4PostTunaReactivation_ArmsFrontierReplayForStalePeerState()
    {
        const string transferId = "transfer_file_tuna_v4_post_tuna_frontier_replay";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        var context = CreateFileTunaV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 256);
        SetPrivateProperty(context, "PullFileTunaV4PostTunaReactivationGeneration", 1);
        SetPrivateProperty(context, "PullFileTunaV4PostTunaReactivationStartedUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        SetPrivateProperty(context, "PullV4LastPeerFrameReceivedUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var queued = (bool)typeof(SessionFileTransferService)
            .GetMethod("MaybeQueueOutboundFileTunaV4PostTunaFrontierReplayLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "unit_test_file_tuna_v4_post_tuna_sender_wait"])!;

        Assert.True(queued);
        var replay = Assert.Single(GetQueuedV4RepairSends(context));
        Assert.Equal(100, GetIntProperty(replay, "FirstStartChunkIndex"));
        Assert.Equal(101, GetIntProperty(replay, "LastEndChunkExclusive"));
        Assert.Equal("file_tuna_v4_post_tuna_frontier_replay", GetStringProperty(replay, "DeliveryEscalationReason"));
        Assert.Equal("file_tuna_v4_post_tuna_frontier_replay", GetPrivateProperty<string>(context, "SparseSenderPumpLastWakeReason"));

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v4_post_reactivation_frontier_replay_started;", log, StringComparison.Ordinal);
        Assert.Contains("route=file_tuna_v4; protocol_version=4", log, StringComparison.Ordinal);
        Assert.Contains("reactivation_generation=1", log, StringComparison.Ordinal);
        Assert.DoesNotContain("file_tuna_v6", log, StringComparison.Ordinal);
    }

    [Fact]
    public void FileTunaV4PostTunaReactivation_DoesNotArmFrontierReplayWithoutReactivationMarker()
    {
        using var service = new SessionFileTransferService();
        var context = CreateFileTunaV4OutboundContext(
            "transfer_file_tuna_v4_no_reactivation_frontier_replay",
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 256);
        SetPrivateProperty(context, "PullV4LastPeerFrameReceivedUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var queued = (bool)typeof(SessionFileTransferService)
            .GetMethod("MaybeQueueOutboundFileTunaV4PostTunaFrontierReplayLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "unit_test_file_tuna_v4_sender_wait"])!;

        Assert.False(queued);
        Assert.Empty(GetQueuedV4RepairSends(context));
    }

    [Fact]
    public void RegularNknV4ReceiveRecoveryRequest_ArmsPeerSilenceSafetyReplayWithBoundedNormalSends()
    {
        const string transferId = "transfer_regular_v4_receive_recovery_replay";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 256,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullV4LastPeerFrameReceivedUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var shouldBoundSend = typeof(SessionFileTransferService)
            .GetMethod("ShouldBoundOutboundV4TransportSend", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)shouldBoundSend.Invoke(service, [context])!);

        var dispatched = (bool)typeof(SessionFileTransferService)
            .GetMethod("TryRequestFileTransferReceiveRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    new FileTransferReceiveRecoveryRequest(
                        $"session_{transferId}",
                        transferId,
                        FileTransferDirection.Outbound,
                        "session_liveness_timeout_pending"),
                ])!;

        Assert.True(dispatched);
        var recoveryRequest = Assert.Single(transport.ReceiveRecoveryRequests);
        Assert.Equal(transferId, recoveryRequest.TransferId);
        Assert.Equal(FileTransferDirection.Outbound, recoveryRequest.Direction);

        var replay = Assert.Single(GetQueuedV4RepairSends(context));
        Assert.Equal(100, GetIntProperty(replay, "FirstStartChunkIndex"));
        Assert.Equal("regular_v4_peer_silence_safety_replay", GetStringProperty(replay, "DeliveryEscalationReason"));
        Assert.True((bool)shouldBoundSend.Invoke(service, [context])!);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_regular_v4_peer_silence_safety_replay_started;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_regular_v4_receive_recovery_peer_silence_replay_armed;", log, StringComparison.Ordinal);
        Assert.Contains("regular_v4_peer_silence_replay_armed=1", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer_v4_transport_send_abandoned_for_regular_v4_peer_silence_recovery", log, StringComparison.Ordinal);
        Assert.DoesNotContain("file_tuna_v6", log, StringComparison.Ordinal);
    }

    [Fact]
    public void RegularNknV4RecoverySendFailure_DefersInsteadOfTerminalPeerDisconnect()
    {
        const string transferId = "transfer_regular_v4_recovery_send_failure_defer";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 256,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullV4LastPeerFrameReceivedUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        typeof(SessionFileTransferService)
            .GetMethod("MaybeQueueOutboundRegularNknV4PeerSilenceSafetyReplayLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "test_regular_v4_receive_recovery"]);
        Assert.NotEmpty(GetQueuedV4RepairSends(context));

        var deferred = (bool)typeof(SessionFileTransferService)
            .GetMethod("TryDeferOutboundRegularNknV4RecoveryTransportSendFailure", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    "sender_pump",
                    new InvalidOperationException(
                        "File-transfer V4 sender transport send failed.",
                        new ObjectDisposedException("LoopbackDataSession", "Bridge disconnected during regular V4 recovery.")),
                ])!;

        Assert.True(deferred);
        Assert.Equal(FileTransferTransferState.Sending, GetPrivateProperty<FileTransferTransferState>(context, "State"));
        Assert.True(GetPrivateProperty<bool>(context, "PullTransportPaused"));
        Assert.Equal("receive_stall_recovery", GetPrivateProperty<string>(context, "PullTransportPauseReason"));
        Assert.Contains(
            transport.ReceiveRecoveryRequests,
            request =>
                request.Direction == FileTransferDirection.Outbound &&
                string.Equals(request.TransferId, transferId, StringComparison.Ordinal) &&
                string.Equals(request.RouteToken, FileTransferRouteResolver.RegularNknV4FastToken, StringComparison.Ordinal) &&
                request.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_regular_v4_recovery_transport_send_failure_deferred;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v4_sender_failed;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("error_code=peer_disconnected", log, StringComparison.Ordinal);
        Assert.DoesNotContain("file_tuna_v6", log, StringComparison.Ordinal);
    }

    [Fact]
    public void RegularNknV4RecoverySendFailure_DefersForFreshReceiveRecoveryBeforeReplayArms()
    {
        const string transferId = "transfer_regular_v4_recovery_send_failure_fresh_recovery";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 27,
            remoteGrantedUntilExclusive: 3121,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullV4LastPeerFrameReceivedUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(20));
        SetPrivateProperty(context, "V6LastReceiveRecoveryRequestedUtc", DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        Assert.Empty(GetQueuedV4RepairSends(context));

        var deferred = (bool)typeof(SessionFileTransferService)
            .GetMethod("TryDeferOutboundRegularNknV4RecoveryTransportSendFailure", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    "sender_pump",
                    new InvalidOperationException(
                        "File-transfer V4 sender transport send failed.",
                        new InvalidOperationException("NKN bridge is not running.")),
                ])!;

        Assert.True(deferred);
        Assert.Equal(FileTransferTransferState.Sending, GetPrivateProperty<FileTransferTransferState>(context, "State"));
        Assert.True(GetPrivateProperty<bool>(context, "PullTransportPaused"));
        Assert.Equal("receive_stall_recovery", GetPrivateProperty<string>(context, "PullTransportPauseReason"));
        Assert.Contains(
            transport.ReceiveRecoveryRequests,
            request =>
                request.Direction == FileTransferDirection.Outbound &&
                string.Equals(request.TransferId, transferId, StringComparison.Ordinal) &&
                string.Equals(request.RouteToken, FileTransferRouteResolver.RegularNknV4FastToken, StringComparison.Ordinal) &&
                request.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_regular_v4_recovery_transport_send_failure_deferred;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v4_sender_failed;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("error_code=peer_disconnected", log, StringComparison.Ordinal);
        Assert.DoesNotContain("file_tuna_v6", log, StringComparison.Ordinal);
    }

    [Fact]
    public void RegularNknV4ReceiveRecoveryState_RewindsAcceptedFrontierAfterPeerFallsBehind()
    {
        const string transferId = "transfer_regular_v4_recovery_frontier_rebind";
        const int chunkSize = 21 * 1024;
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 512,
            remoteGrantedUntilExclusive: 512,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "V6LastReceiveRecoveryRequestedUtc", DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        typeof(SessionFileTransferService)
            .GetMethod("ApplyOutboundV4State", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    new FileTransferStateFrameV4
                    {
                        SessionId = $"session_{transferId}",
                        TransferId = transferId,
                        Epoch = 1,
                        ContiguousCommittedChunkIndex = 100,
                        DurableReceivedHighestChunkIndex = 99,
                        CreditUntilChunkIndexExclusive = 512,
                        MissingRanges = [],
                        BytesCommitted = 100L * chunkSize,
                    },
                ]);

        Assert.Equal(100, GetPrivateProperty<int>(context, "ChunksAcceptedForTransport"));
        Assert.Equal(100L * chunkSize, GetPrivateProperty<long>(context, "BytesAcceptedForTransport"));
        Assert.Equal("regular_v4_receive_recovery_frontier_rebind", GetPrivateProperty<string>(context, "SparseSenderPumpLastWakeReason"));
        Assert.Empty(GetQueuedV4RepairSends(context));

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_regular_v4_receive_recovery_frontier_rebind;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_sender_resume_rewind;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("file_tuna_v6", log, StringComparison.Ordinal);
    }

    [Fact]
    public void RegularNknV4ReceiveRecoveryState_DoesNotRewindWithoutRecoveryEvidence()
    {
        const string transferId = "transfer_regular_v4_no_recovery_frontier_rebind";
        const int chunkSize = 21 * 1024;
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 512,
            remoteGrantedUntilExclusive: 512,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        typeof(SessionFileTransferService)
            .GetMethod("ApplyOutboundV4State", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    new FileTransferStateFrameV4
                    {
                        SessionId = $"session_{transferId}",
                        TransferId = transferId,
                        Epoch = 1,
                        ContiguousCommittedChunkIndex = 100,
                        DurableReceivedHighestChunkIndex = 99,
                        CreditUntilChunkIndexExclusive = 512,
                        MissingRanges = [],
                        BytesCommitted = 100L * chunkSize,
                    },
                ]);

        Assert.Equal(512, GetPrivateProperty<int>(context, "ChunksAcceptedForTransport"));
        var log = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain("event=filetransfer_regular_v4_receive_recovery_frontier_rebind;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v4_sender_resume_rewind;", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknV4Fast_BoundsHungNormalTransportSends()
    {
        const string transferId = "transfer_regular_v4_bound_normal_send";
        const string sessionId = "session_regular_v4_bound_normal_send";
        var previousSendTimeout = SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame is not FileTransferChunkBatchFrameV4)
            {
                return false;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return true;
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        try
        {
            SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests = TimeSpan.FromMilliseconds(100);
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("regular-v4-bound-normal-send.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
            var offer = senderTransport.SentOffers.Single();
            await receiverTransport.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
            await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV4>().Any(), timeoutMs: 5000);

            var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
            await receiverSession.SendAsync(
                new FileTransferStateFrameV4
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 64,
                    MissingRanges = [],
                    BytesCommitted = 0,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.ActiveDataSessionSends > 0, timeoutMs: 5000);
            await WaitUntilAsync(
                () =>
                {
                    var log = ReadOperationalLogTail(logStart);
                    return log.Contains(
                               "event=filetransfer_v4_transport_send_timeout_deferred_for_regular_nkn_v4_fast_runtime",
                               StringComparison.Ordinal) &&
                           log.Contains("repair_send=0", StringComparison.Ordinal);
                },
                timeoutMs: 5000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            Assert.DoesNotContain("file_tuna_v6", ReadOperationalLogTail(logStart), StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests = previousSendTimeout;
        }
    }

    [Fact]
    public void RegularNknV4ReceiveStallPause_AllowsRepairReplayButBlocksNormalSends()
    {
        const string transferId = "transfer_regular_v4_receive_recovery_repair_drain";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 256,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullV4LastPeerFrameReceivedUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        SetPrivateProperty(context, "PullTransportPaused", true);
        SetPrivateProperty(context, "PullTransportPauseReason", "receive_stall_recovery");
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        typeof(SessionFileTransferService)
            .GetMethod("MaybeQueueOutboundRegularNknV4PeerSilenceSafetyReplayLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "session_liveness_timeout_pending"]);

        Assert.Single(GetQueuedV4RepairSends(context));
        var allowRepairWhilePaused = (bool)typeof(SessionFileTransferService)
            .GetMethod("ShouldAllowOutboundV4RepairWhileTransportPausedLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context])!;
        var blockNormalSend = (bool)typeof(SessionFileTransferService)
            .GetMethod("ShouldBlockOutboundV4TransportSendForTransportPauseLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, false])!;
        var blockRepairSend = (bool)typeof(SessionFileTransferService)
            .GetMethod("ShouldBlockOutboundV4TransportSendForTransportPauseLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, true])!;

        Assert.True(allowRepairWhilePaused);
        Assert.True(blockNormalSend);
        Assert.False(blockRepairSend);
    }

    [Fact]
    public void RegularNknV4ReceiveRecoveryRequest_DoesNotArmReplayForInboundOrFreshPeer()
    {
        const string transferId = "transfer_regular_v4_receive_recovery_no_replay";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 256,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullV4LastPeerFrameReceivedUtc", DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var method = typeof(SessionFileTransferService)
            .GetMethod("TryRequestFileTransferReceiveRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var outboundDispatched = (bool)method.Invoke(
            service,
            [
                new FileTransferReceiveRecoveryRequest(
                    $"session_{transferId}",
                    transferId,
                    FileTransferDirection.Outbound,
                    "session_liveness_timeout_pending"),
            ])!;
        var inboundDispatched = (bool)method.Invoke(
            service,
            [
                new FileTransferReceiveRecoveryRequest(
                    $"session_{transferId}",
                    transferId,
                    FileTransferDirection.Inbound,
                    "session_liveness_timeout_pending"),
            ])!;

        Assert.True(outboundDispatched);
        Assert.True(inboundDispatched);
        Assert.Equal(2, transport.ReceiveRecoveryRequests.Count);
        Assert.Empty(GetQueuedV4RepairSends(context));

        var log = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain("event=filetransfer_regular_v4_receive_recovery_peer_silence_replay_armed;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("regular_v4_peer_silence_replay_armed=1", log, StringComparison.Ordinal);
    }

    [Fact]
    public void RegularNknV4PeerSilenceSafetyReplay_RequeuesAfterSendTimeout()
    {
        const string transferId = "transfer_regular_v4_peer_silence_replay_requeue";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 256,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullV4LastPeerFrameReceivedUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        typeof(SessionFileTransferService)
            .GetMethod("MaybeQueueOutboundRegularNknV4PeerSilenceSafetyReplayLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "session_liveness_timeout_pending"]);

        var queuedRepair = Assert.Single(GetQueuedV4RepairSends(context));
        var repairQueue = context.GetType()
            .GetProperty("PullV4SenderPumpRepairQueue", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(context)!;
        var dequeuedRepair = repairQueue.GetType().GetMethod("Dequeue")!.Invoke(repairQueue, null)!;
        var queuedChunkIndices = context.GetType()
            .GetProperty("PullV4SenderPumpRepairQueuedChunkIndices", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(context)!;
        queuedChunkIndices.GetType().GetMethod("Clear")!.Invoke(queuedChunkIndices, null);

        var args = new object?[] { context, dequeuedRepair, 0 };
        var requeued = (bool)typeof(SessionFileTransferService)
            .GetMethod("TryRequeueOutboundRegularNknV4PeerSilenceSafetyReplayLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, args)!;

        Assert.True(requeued);
        Assert.Equal(GetIntProperty(queuedRepair, "RequestedChunkCount"), Assert.IsType<int>(args[2]));
        var replay = Assert.Single(GetQueuedV4RepairSends(context));
        Assert.Equal(100, GetIntProperty(replay, "FirstStartChunkIndex"));
        Assert.Equal("regular_v4_peer_silence_safety_replay", GetStringProperty(replay, "DeliveryEscalationReason"));
    }

    [Fact]
    public async Task PrimaryRegularNknV4Fast_CompletesEndToEnd_WithSparseReceiverIntegrity()
    {
        const string transferId = "transfer_v4_sender_e2e";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_e2e");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_e2e");
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-e2e.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 15000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var sessionOpen = Assert.Single(senderTransport.SentSessionOpens);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, sessionOpen.ProtocolVersion);
        Assert.All(senderTransport.SentDataFrames, static frame =>
            Assert.True(FileTransferProtocol.IsV4DataFrame(frame), $"Expected only V4 sender data frames, got {frame.Type}."));
        Assert.All(receiverTransport.SentDataFrames, static frame =>
            Assert.True(FileTransferProtocol.IsV4DataFrame(frame), $"Expected only V4 receiver data frames, got {frame.Type}."));
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferManifestFrameV4 and not FileTransferManifestFrameV6);
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferChunkBatchFrameV4 and not FileTransferChunkBatchFrameV6);
        Assert.Contains(receiverTransport.SentDataFrames, static frame => frame is FileTransferStateFrameV4 and not FileTransferReceiverStateFrameV6);
        Assert.Contains(receiverTransport.SentCompletes, complete =>
            string.Equals(complete.TransferId, transferId, StringComparison.Ordinal) &&
            complete.FileSizeBytes == payload.Length);
        Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame is FileTransferCompleteFrameV6);
        Assert.DoesNotContain(
            receiverTransport.SentDataFrames,
            static frame => frame is not FileTransferStateFrameV4);
        var logTail = ReadV4SenderLogSnapshot(logStart);
        var transferLog = FilterV4SenderTransferLog(logTail, transferId);
        Assert.Contains("event=filetransfer_v4_negotiated; transfer_id=transfer_v4_sender_e2e", transferLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_sender_started; transfer_id=transfer_v4_sender_e2e", transferLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_receiver_started; transfer_id=transfer_v4_sender_e2e", transferLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_efficiency_summary; direction=outbound; transfer_id=transfer_v4_sender_e2e", transferLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_efficiency_summary; direction=inbound; transfer_id=transfer_v4_sender_e2e", transferLog, StringComparison.Ordinal);
        Assert.Contains("raw_bytes_sent_total=", transferLog, StringComparison.Ordinal);
        Assert.Contains("raw_batch_bytes_received_total=", transferLog, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_primary_regular_nkn_bulk_v6_selected", transferLog, StringComparison.Ordinal);
    }

    private static object CreateRegularNknV4OutboundContext(
        string transferId,
        int remoteNextExpectedChunkIndex,
        int chunksAcceptedForTransport,
        int remoteGrantedUntilExclusive,
        DateTimeOffset lastGrantReceivedUtc)
    {
        var serviceType = typeof(SessionFileTransferService);
        var contextType = serviceType.GetNestedType("OutboundTransferContext", BindingFlags.NonPublic)!;
        FileTransferReadStreamFactory openReadStreamAsync =
            _ => Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>(), writable: false));
        var descriptor = new FileTransferSendDescriptor($"{transferId}.bin", 512L * 21 * 1024, transferId);
        var context = Activator.CreateInstance(
            contextType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [descriptor, openReadStreamAsync],
            culture: null)!;
        var routeSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        SetPrivateProperty(context, "SessionId", $"session_{transferId}");
        SetPrivateProperty(context, "RouteSelection", routeSelection);
        SetPrivateProperty(context, "RouteRuntime", routeSelection.RuntimeDescriptor);
        SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV4);
        SetPrivateProperty(context, "State", FileTransferTransferState.Sending);
        SetPrivateProperty(context, "ChunkSizeBytes", 21 * 1024);
        SetPrivateProperty(context, "ChunkCount", 512);
        SetPrivateProperty(context, "RemoteNextExpectedChunkIndex", remoteNextExpectedChunkIndex);
        SetPrivateProperty(context, "ChunksAcceptedForTransport", chunksAcceptedForTransport);
        SetPrivateProperty(context, "RemoteGrantedUntilExclusive", remoteGrantedUntilExclusive);
        SetPrivateProperty(context, "PullV4LastGrantReceivedUtc", lastGrantReceivedUtc);
        return context;
    }

    private static object CreateFileTunaV4OutboundContext(
        string transferId,
        int remoteNextExpectedChunkIndex = 100,
        int chunksAcceptedForTransport = 256,
        int remoteGrantedUntilExclusive = 256,
        int chunkCount = 256)
    {
        var serviceType = typeof(SessionFileTransferService);
        var contextType = serviceType.GetNestedType("OutboundTransferContext", BindingFlags.NonPublic)!;
        FileTransferReadStreamFactory openReadStreamAsync =
            _ => Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>(), writable: false));
        const int chunkSize = 21 * 1024;
        var descriptor = new FileTransferSendDescriptor($"{transferId}.bin", (long)chunkCount * chunkSize, transferId);
        var context = Activator.CreateInstance(
            contextType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [descriptor, openReadStreamAsync],
            culture: null)!;
        var routeSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        SetPrivateProperty(context, "SessionId", $"session_{transferId}");
        SetPrivateProperty(context, "RouteSelection", routeSelection);
        SetPrivateProperty(context, "RouteRuntime", routeSelection.RuntimeDescriptor);
        SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV4);
        SetPrivateProperty(context, "State", FileTransferTransferState.Sending);
        SetPrivateProperty(context, "ChunkSizeBytes", chunkSize);
        SetPrivateProperty(context, "ChunkCount", chunkCount);
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullSessionActive", true);
        SetPrivateProperty(context, "RemoteNextExpectedChunkIndex", remoteNextExpectedChunkIndex);
        SetPrivateProperty(context, "RemoteGrantedUntilExclusive", remoteGrantedUntilExclusive);
        SetPrivateProperty(context, "ChunksAcceptedForTransport", chunksAcceptedForTransport);
        return context;
    }

    private static object CreatePostTunaFallbackV6OutboundContext(
        string transferId,
        int remoteNextExpectedChunkIndex = 100,
        int chunksAcceptedForTransport = 256,
        int remoteGrantedUntilExclusive = 256,
        int chunkCount = 256)
    {
        var serviceType = typeof(SessionFileTransferService);
        var contextType = serviceType.GetNestedType("OutboundTransferContext", BindingFlags.NonPublic)!;
        FileTransferReadStreamFactory openReadStreamAsync =
            _ => Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>(), writable: false));
        const int chunkSize = 21 * 1024;
        var descriptor = new FileTransferSendDescriptor($"{transferId}.bin", (long)chunkCount * chunkSize, transferId);
        var context = Activator.CreateInstance(
            contextType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [descriptor, openReadStreamAsync],
            culture: null)!;
        var routeSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var bridgePolicyType = serviceType.GetNestedType("FileTransferBridgeRecoveryPolicy", BindingFlags.NonPublic)!;
        SetPrivateProperty(context, "SessionId", $"session_{transferId}");
        SetPrivateProperty(context, "RouteSelection", routeSelection);
        SetPrivateProperty(context, "RouteRuntime", routeSelection.RuntimeDescriptor);
        SetPrivateProperty(context, "BridgeRecoveryPolicy", Enum.Parse(bridgePolicyType, "PostTunaFallbackStrictRecovery"));
        SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV6);
        SetPrivateProperty(context, "State", FileTransferTransferState.Sending);
        SetPrivateProperty(context, "ChunkSizeBytes", chunkSize);
        SetPrivateProperty(context, "ChunkCount", chunkCount);
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullSessionActive", true);
        SetPrivateProperty(context, "PullPostTunaRecoveryActive", true);
        SetPrivateProperty(context, "PullTransportRebindGeneration", 3);
        SetPrivateProperty(context, "RemoteNextExpectedChunkIndex", remoteNextExpectedChunkIndex);
        SetPrivateProperty(context, "RemoteGrantedUntilExclusive", remoteGrantedUntilExclusive);
        SetPrivateProperty(context, "ChunksAcceptedForTransport", chunksAcceptedForTransport);
        SetPrivateProperty(context, "PullTransportFrontierOnlyRepairActive", true);
        SetPrivateProperty(context, "PullTransportFrontierOnlyRepairStartChunkIndex", remoteNextExpectedChunkIndex);
        return context;
    }

    private static object CreatePostTunaFallbackV6InboundContext(string transferId)
    {
        var serviceType = typeof(SessionFileTransferService);
        var contextType = serviceType.GetNestedType("InboundTransferContext", BindingFlags.NonPublic)!;
        const int chunkSize = 21 * 1024;
        var routeSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var offer = new FileTransferOfferV2
        {
            SessionId = $"session_{transferId}",
            TransferId = transferId,
            FileName = $"{transferId}.bin",
            FileSizeBytes = 256L * chunkSize,
            PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            FileTransferRoute = routeSelection.TelemetryToken,
        };
        var context = Activator.CreateInstance(
            contextType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [offer, routeSelection],
            culture: null)!;
        var bridgePolicyType = serviceType.GetNestedType("FileTransferBridgeRecoveryPolicy", BindingFlags.NonPublic)!;
        SetPrivateProperty(context, "BridgeRecoveryPolicy", Enum.Parse(bridgePolicyType, "PostTunaFallbackStrictRecovery"));
        SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV6);
        SetPrivateProperty(context, "State", FileTransferTransferState.Receiving);
        SetPrivateProperty(context, "ChunkSizeBytes", chunkSize);
        SetPrivateProperty(context, "ChunkCount", 256);
        SetPrivateProperty(context, "PullSessionActive", true);
        SetPrivateProperty(context, "PullManifestReceived", true);
        SetPrivateProperty(context, "PullPostTunaRecoveryActive", true);
        SetPrivateProperty(context, "ReceiverSparseWriteActive", true);
        SetPrivateProperty(context, "NextChunkIndex", 100);
        SetPrivateProperty(context, "PullHighestReceivedChunkIndex", 255);
        return context;
    }

    private static T GetPrivateProperty<T>(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(property);
        return Assert.IsType<T>(property!.GetValue(target));
    }

    [Fact]
    public void PostTunaFallbackV6RepairRevalidation_UsesFallbackLegAuthorityWindow()
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var contextType = serviceType.GetNestedType("OutboundTransferContext", BindingFlags.NonPublic)!;
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_authority_revalidate",
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 100,
            remoteGrantedUntilExclusive: 181,
            chunkCount: 256);
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        serviceType
            .GetMethod("StartOutboundPostTunaRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "test_authority_revalidate"]);

        var receiverState = new FileTransferReceiverStateFrameV6
        {
            SessionId = "session_transfer_post_tuna_fallback_v6_authority_revalidate",
            TransferId = "transfer_post_tuna_fallback_v6_authority_revalidate",
            Epoch = 1,
            ContiguousCommittedChunkIndex = 100,
            DurableReceivedHighestChunkIndex = 180,
            CreditUntilChunkIndexExclusive = 181,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 100, ChunkCount = 64 }],
            BytesCommitted = 100L * 21 * 1024,
            TransportEpoch = 3,
            Priority = "frontier",
            RecoveryMode = "frontier_repair_only",
        };
        serviceType
            .GetMethod("ApplyOutboundV4State", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, receiverState]);

        var queue = (System.Collections.IEnumerable)contextType
            .GetProperty("PullV4SenderPumpRepairQueue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context)!;
        var queuedRepair = queue.Cast<object>().Single();

        var revalidatedRepair = serviceType
            .GetMethod("RevalidateQueuedV4RepairSendLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, queuedRepair]);

        Assert.NotNull(revalidatedRepair);
        var revalidatedRepairType = revalidatedRepair!.GetType();
        var chunkIndices = ((IEnumerable<int>)revalidatedRepairType.GetProperty("ChunkIndices")!.GetValue(revalidatedRepair)!).ToArray();

        Assert.Equal(Enumerable.Range(100, 64), chunkIndices);
        Assert.Equal(181, revalidatedRepairType.GetProperty("ChunksAcceptedForTransport")!.GetValue(revalidatedRepair));
    }

    [Fact]
    public void FileTunaV4_PeerFallbackV6ReceiverStatePromotesToPostTunaFallbackV6()
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var context = CreateFileTunaV4OutboundContext(
            "transfer_file_tuna_v4_peer_fallback_v6_proof",
            remoteNextExpectedChunkIndex: 120,
            chunksAcceptedForTransport: 160,
            remoteGrantedUntilExclusive: 160,
            chunkCount: 256);
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var receiverState = new FileTransferReceiverStateFrameV6
        {
            SessionId = "session_transfer_file_tuna_v4_peer_fallback_v6_proof",
            TransferId = "transfer_file_tuna_v4_peer_fallback_v6_proof",
            Epoch = 5,
            ContiguousCommittedChunkIndex = 120,
            DurableReceivedHighestChunkIndex = 159,
            CreditUntilChunkIndexExclusive = 160,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 120, ChunkCount = 16 }],
            BytesCommitted = 120L * 21 * 1024,
            TransportEpoch = 4,
            RepairRequestId = "v6-regular-nkn-state-refresh:4",
            Priority = "state_refresh",
            RecoveryMode = "regular_nkn_state_refresh",
        };
        object?[] args = [context, receiverState, null];
        var logStart = GetOperationalLogLength();

        var promoted = (bool)serviceType
            .GetMethod("TryPromoteOutboundFileTunaV4FallbackFromPeerV6Proof", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, args)!;

        Assert.True(promoted);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, GetPrivateProperty<int>(context, "NegotiatedDataProtocolVersion"));
        var routeSelection = GetPrivateProperty<FileTransferRouteSelection>(context, "RouteSelection");
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, routeSelection.Route);
        var fallbackLeg = context.GetType()
            .GetProperty("CurrentTransferLeg", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context);
        Assert.NotNull(fallbackLeg);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, ((FileTransferRouteSelection)fallbackLeg!.GetType()
            .GetProperty("RouteSelection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(fallbackLeg)!).Route);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_peer_post_tuna_fallback_v6_proof_promoted_route; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("route=post_tuna_fallback_v6; protocol_version=6", log, StringComparison.Ordinal);
        Assert.DoesNotContain("reason=protocol_not_v4", log, StringComparison.Ordinal);
    }

    [Fact]
    public void FileTunaV4_StaleFallbackV6ReceiverStateAfterReactivationDoesNotPromote()
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_file_tuna_v4_stale_fallback_v6_proof_after_reactivation",
            remoteNextExpectedChunkIndex: 120,
            chunksAcceptedForTransport: 160,
            remoteGrantedUntilExclusive: 160,
            chunkCount: 256);
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var reactivated = (bool)serviceType
            .GetMethod("TryPromoteOutboundPostTunaFallbackV6ToFileTunaV4Locked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    "test_live_reactivation",
                    FileTransferTransportHandoffKind.NormalToTunaActivation,
                    FileTransferTransportKind.Tuna,
                ])!;
        Assert.True(reactivated);

        var staleReceiverState = new FileTransferReceiverStateFrameV6
        {
            SessionId = "session_transfer_file_tuna_v4_stale_fallback_v6_proof_after_reactivation",
            TransferId = "transfer_file_tuna_v4_stale_fallback_v6_proof_after_reactivation",
            Epoch = 12,
            ContiguousCommittedChunkIndex = 120,
            DurableReceivedHighestChunkIndex = 159,
            CreditUntilChunkIndexExclusive = 160,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 120, ChunkCount = 16 }],
            BytesCommitted = 120L * 21 * 1024,
            TransportEpoch = 1,
            RepairRequestId = null,
            Priority = "frontier",
            RecoveryMode = null,
        };
        object?[] args = [context, staleReceiverState, null];
        var logStart = GetOperationalLogLength();

        var promoted = (bool)serviceType
            .GetMethod("TryPromoteOutboundFileTunaV4FallbackFromPeerV6Proof", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, args)!;

        Assert.False(promoted);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, GetPrivateProperty<int>(context, "NegotiatedDataProtocolVersion"));
        var routeSelection = GetPrivateProperty<FileTransferRouteSelection>(context, "RouteSelection");
        Assert.Equal(FileTransferRoute.FileTunaV4, routeSelection.Route);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_peer_post_tuna_fallback_v6_proof_ignored; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("reason=stale_after_live_tuna_activation", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_peer_post_tuna_fallback_v6_proof_promoted_route;", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6Reactivation_StartsFreshFileTunaLegAndStopsFallbackAuthority()
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_reactivation_leg_reset");
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        serviceType
            .GetMethod("StartOutboundPostTunaRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "test_reactivation_leg_reset"]);
        SetPrivateProperty(context, "ChunksAcceptedForTransport", 256);
        SetPrivateProperty(context, "BytesAcceptedForTransport", 256L * 21 * 1024);

        var fallbackLeg = context.GetType()
            .GetProperty("CurrentTransferLeg", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context);
        Assert.NotNull(fallbackLeg);
        var fallbackRoute = (FileTransferRouteSelection)fallbackLeg!.GetType()
            .GetProperty("RouteSelection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(fallbackLeg)!;
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, fallbackRoute.Route);

        var logStart = GetOperationalLogLength();
        var promoted = (bool)serviceType
            .GetMethod("TryPromoteOutboundPostTunaFallbackV6ToFileTunaV4Locked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    "test_reactivation_leg_reset",
                    FileTransferTransportHandoffKind.NormalToTunaActivation,
                    FileTransferTransportKind.Tuna,
                ])!;

        Assert.True(promoted);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, GetPrivateProperty<int>(context, "NegotiatedDataProtocolVersion"));
        Assert.False(GetPrivateProperty<bool>(context, "PullPostTunaRecoveryActive"));
        Assert.False(GetPrivateProperty<bool>(context, "PullTransportPaused"));
        Assert.False(GetPrivateProperty<bool>(context, "PullTransportFrontierOnlyRepairActive"));
        Assert.Equal(100, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
        Assert.Equal(100, GetPrivateProperty<int>(context, "ChunksAcceptedForTransport"));
        Assert.Equal(100L * 21 * 1024, GetPrivateProperty<long>(context, "BytesAcceptedForTransport"));
        Assert.Equal(0, GetPrivateProperty<int>(context, "V6RegularNknStateRefreshSendInFlight"));
        Assert.Null(context.GetType()
            .GetProperty("V6TransportEpoch", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context));
        Assert.Null(context.GetType()
            .GetProperty("V6TransportHandoff", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context));

        var tunaLeg = context.GetType()
            .GetProperty("CurrentTransferLeg", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context);
        Assert.NotNull(tunaLeg);
        var tunaRoute = (FileTransferRouteSelection)tunaLeg!.GetType()
            .GetProperty("RouteSelection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(tunaLeg)!;
        Assert.Equal(FileTransferRoute.FileTunaV4, tunaRoute.Route);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, tunaRoute.ProtocolVersion);
        Assert.True((bool)tunaLeg.GetType()
            .GetProperty("CanSendData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(tunaLeg)!);

        var attachFallbackAuthority = serviceType
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method =>
                string.Equals(method.Name, "AttachFallbackLegAuthority", StringComparison.Ordinal) &&
                method.GetParameters()[0].ParameterType.Name == "OutboundTransferContext");
        var baseRequest = new FileTransferReceiveRecoveryRequest(
            "session_transfer_post_tuna_fallback_v6_reactivation_leg_reset",
            "transfer_post_tuna_fallback_v6_reactivation_leg_reset",
            FileTransferDirection.Outbound,
            "unit_test_after_reactivation");
        var annotatedRequest = (FileTransferReceiveRecoveryRequest)attachFallbackAuthority
            .Invoke(null, [context, baseRequest, "unit_test_after_reactivation", null, null])!;

        Assert.Null(annotatedRequest.RouteToken);
        Assert.Equal(0, annotatedRequest.ProtocolVersion);
        Assert.Equal(0, annotatedRequest.TransferLegGeneration);
        Assert.Null(annotatedRequest.AuthorityReason);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_fallback_leg_authority_superseded_by_route_hint; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("superseded_by_route=file_tuna_v4", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_leg_started; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("route=file_tuna_v4", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_tuna_reactivation_v4_frontier_reseeded;", log, StringComparison.Ordinal);
        Assert.Contains("reason=live_route_tuna_reactivated", log, StringComparison.Ordinal);
        Assert.Contains("previous_chunks_accepted_for_transport=256; chunks_accepted_for_transport=100", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTunaFallbackV6Outbound_AcceptsRedundantCompleteDataFrameAsTerminalProof()
    {
        const string transferId = "transfer_post_tuna_fallback_complete_data_frame";
        const string sha256Base64 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        var context = CreatePostTunaFallbackV6OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 255,
            chunksAcceptedForTransport: 255,
            remoteGrantedUntilExclusive: 256,
            chunkCount: 256);
        SetPrivateProperty(context, "Sha256Base64", sha256Base64);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var complete = new FileTransferCompleteFrameV6
        {
            SessionId = $"session_{transferId}",
            TransferId = transferId,
            FileSizeBytes = 256L * 21 * 1024,
            Sha256Base64 = sha256Base64,
            TransportEpoch = 3,
            RecoveryMode = "post_tuna_fallback",
        };
        var task = Assert.IsAssignableFrom<Task<bool>>(InvokePrivateMethod(
            service,
            "TryHandleOutboundLifecycleCompleteDataFrameAsync",
            context,
            complete));

        Assert.True(await task);
        Assert.Equal(FileTransferTransferState.Completed, GetPrivateProperty<FileTransferTransferState>(context, "State"));
        Assert.Equal(complete.FileSizeBytes, GetPrivateProperty<long>(context, "BytesTransferred"));
        Assert.Equal(complete.FileSizeBytes, GetPrivateProperty<long>(context, "BytesAcknowledgedByReceiver"));

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_lifecycle_priority_received; kind=complete", logTail, StringComparison.Ordinal);
        Assert.Contains("path=redundant_data_frame", logTail, StringComparison.Ordinal);
        Assert.Contains("protocol_version=6", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=session_liveness_timeout;", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6CheckpointProof_AllowsBackfillRepairBeyondAcceptedFrontier()
    {
        const string transferId = "transfer_post_tuna_fallback_backfill_authority";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 100,
            remoteGrantedUntilExclusive: 100,
            chunkCount: 256);
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        serviceType
            .GetMethod("StartOutboundPostTunaRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "test_backfill_authority"]);

        serviceType
            .GetMethod("ApplyOutboundV4State", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    new FileTransferReceiverStateFrameV6
                    {
                        SessionId = $"session_{transferId}",
                        TransferId = transferId,
                        Epoch = 1,
                        ContiguousCommittedChunkIndex = 100,
                        DurableReceivedHighestChunkIndex = 180,
                        CreditUntilChunkIndexExclusive = 200,
                        MissingRanges =
                        [
                            new FileTransferRangeV4
                            {
                                StartChunkIndex = 120,
                                ChunkCount = 8,
                            },
                        ],
                        BytesCommitted = 100L * 21 * 1024,
                        TransportEpoch = 3,
                        RecoveryMode = "regular_nkn_state_refresh",
                    },
                ]);

        var queuedRepair = Assert.Single(GetQueuedV4RepairSends(context));
        Assert.Equal(120, GetIntProperty(queuedRepair, "FirstStartChunkIndex"));
        Assert.Equal(128, GetIntProperty(queuedRepair, "LastEndChunkExclusive"));
        Assert.Equal(8, GetIntProperty(queuedRepair, "RequestedChunkCount"));
        Assert.Equal(0, GetIntProperty(queuedRepair, "SkippedFutureCount"));

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_fallback_checkpoint_accepted; direction=outbound;", logTail, StringComparison.Ordinal);
        Assert.Contains("proven_highest_observed_chunk=180", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_repair_scheduled; transfer_id=transfer_post_tuna_fallback_backfill_authority", logTail, StringComparison.Ordinal);
        Assert.Contains("scheduled_chunk_count=8", logTail, StringComparison.Ordinal);
        Assert.Contains("chunks_accepted_for_transport=181", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("reason=not_yet_sent", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("file_tuna_v6", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public void RegularNknV4Repair_DoesNotUseReceiverHighestObservedAsSendAuthority()
    {
        const string transferId = "transfer_regular_v4_future_repair_stays_blocked";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 100,
            remoteGrantedUntilExclusive: 200,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        typeof(SessionFileTransferService)
            .GetMethod("ApplyOutboundV4State", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    new FileTransferStateFrameV4
                    {
                        SessionId = $"session_{transferId}",
                        TransferId = transferId,
                        Epoch = 1,
                        ContiguousCommittedChunkIndex = 100,
                        DurableReceivedHighestChunkIndex = 180,
                        CreditUntilChunkIndexExclusive = 200,
                        MissingRanges =
                        [
                            new FileTransferRangeV4
                            {
                                StartChunkIndex = 120,
                                ChunkCount = 8,
                            },
                        ],
                        BytesCommitted = 100L * 21 * 1024,
                    },
                ]);

        Assert.Empty(GetQueuedV4RepairSends(context));
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("reason=not_yet_sent", logTail, StringComparison.Ordinal);
        Assert.Contains("scheduled_chunk_count=0", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("file_tuna_v6", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public void RegularNknV4Fast_LiveTunaActivationRewindsAcceptedFrontierBeforeFileTunaV4()
    {
        const string transferId = "transfer_regular_v4_live_tuna_activation_rewind";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport("session_regular_v4_live_tuna_activation_rewind");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 128,
            chunksAcceptedForTransport: 300,
            remoteGrantedUntilExclusive: 512,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);

        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullTransportPaused", true);
        SetPrivateProperty(context, "PullTransportPauseReason", "tuna_activation_negotiating");
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var promoted = (bool)typeof(SessionFileTransferService)
            .GetMethod("TryPromoteOutboundRegularNknV4ToFileTunaV4Locked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    "tuna_activation_answer_ack",
                    FileTransferTransportHandoffKind.NormalToTunaActivation,
                    FileTransferTransportKind.Tuna,
                ])!;

        Assert.True(promoted);
        Assert.Equal(128, GetPrivateProperty<int>(context, "ChunksAcceptedForTransport"));
        Assert.Equal(128L * 21 * 1024, GetPrivateProperty<long>(context, "BytesAcceptedForTransport"));
        Assert.False(GetPrivateProperty<bool>(context, "PullTransportPaused"));
        Assert.False(GetPrivateProperty<bool>(context, "PullTransportResumeRequestPending"));
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, GetPrivateProperty<int>(context, "NegotiatedDataProtocolVersion"));

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains(
            "event=filetransfer_v4_sender_resume_rewind;",
            logTail,
            StringComparison.Ordinal);
        Assert.Contains("reason=live_route_tuna_activated", logTail, StringComparison.Ordinal);
        Assert.Contains("remote_next_expected_chunk_index=128", logTail, StringComparison.Ordinal);
        Assert.Contains("chunks_accepted_before=300", logTail, StringComparison.Ordinal);
        Assert.Contains("chunks_accepted_after=128", logTail, StringComparison.Ordinal);
        Assert.Contains("previous_route=regular_nkn_v4_fast; new_route=file_tuna_v4", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("file_tuna_v6", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegularNknV4Fast_LiveTunaActivationPromotesSameTransferToFileTunaV4()
    {
        const string transferId = "transfer_regular_v4_live_tuna_activation";
        const string sessionId = "session_regular_v4_live_tuna_activation";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 4_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var releasePostActivationFrames = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame.TransferId == transferId &&
                frame is FileTransferChunkBatchFrameV4 { StartChunkIndex: > 0 } &&
                Volatile.Read(ref releasePostActivationFrames) == 0)
            {
                while (Volatile.Read(ref releasePostActivationFrames) == 0)
                {
                    await Task.Delay(10, ct);
                }
            }

            return false;
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("regular-v4-live-tuna-activation.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(static batch => batch.ForceRegularNknBulk),
            timeoutMs: 5000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests("tuna_activation_negotiating");
        await WaitUntilAsync(
            () =>
            {
                var log = ReadV4SenderLogSnapshot(logStart);
                return log.Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal) &&
                       log.Contains("event=filetransfer_transport_paused; direction=inbound", StringComparison.Ordinal);
            },
            timeoutMs: 5000);

        senderTransport.IsFileTunaActiveForRouteSelection = true;
        receiverTransport.IsFileTunaActiveForRouteSelection = true;
        senderTransport.SetConnectedDataSessionsAvailableForTests("tuna_activation_negotiated_transport_ready");
        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_activation_answer_ack",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        receiverTransport.RequestAllDataSessionHandoffs(
            "tuna_activation_answer_ack",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(
            () =>
            {
                var log = ReadV4SenderLogSnapshot(logStart);
                return log.Contains("previous_route=regular_nkn_v4_fast; new_route=file_tuna_v4", StringComparison.Ordinal) &&
                       log.Contains("event=filetransfer_live_route_epoch_recovered; direction=outbound", StringComparison.Ordinal) &&
                       log.Contains("event=filetransfer_live_route_epoch_recovered; direction=inbound", StringComparison.Ordinal);
            },
            timeoutMs: 10000);

        Volatile.Write(ref releasePostActivationFrames, 1);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV4>()
                .Any(static batch => !batch.ForceRegularNknBulk),
            timeoutMs: 10000);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 25000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var logTail = ReadV4SenderLogSnapshot(logStart);
        Assert.Single(senderTransport.SentOffers.Where(offer => offer.TransferId == transferId));
        Assert.Single(senderTransport.SentSessionOpens.Where(open => open.TransferId == transferId));
        Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, senderTransport.SentOffers.Single(offer => offer.TransferId == transferId).FileTransferRoute);
        Assert.Contains("event=filetransfer_route_transitioned; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_route_transitioned; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.Contains("previous_route=regular_nkn_v4_fast; new_route=file_tuna_v4", logTail, StringComparison.Ordinal);
        Assert.Contains("route=file_tuna_v4; protocol_version=4", logTail, StringComparison.Ordinal);
        Assert.Contains("handoff_kind=normal_to_tuna_activation", logTail, StringComparison.Ordinal);
        Assert.Contains("target_transport=tuna", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("file_tuna_v6", logTail, StringComparison.Ordinal);
        Assert.All(
            senderTransport.SentDataFrames.Where(frame => frame.TransferId == transferId),
            static frame => Assert.True(FileTransferProtocol.IsV4DataFrame(frame), $"Expected V4 sender frame, got {frame.Type}."));
        Assert.DoesNotContain(
            receiverTransport.SentDataFrames.Where(frame => frame.TransferId == transferId),
            static frame => frame is FileTransferManifestFrameV6 or FileTransferChunkBatchFrameV6 or FileTransferCompleteFrameV6);
    }

    [Fact]
    public async Task RegularNknV4Fast_LiveTunaActivationCanThenCycleOffOnOffSameTransfer()
    {
        const string transferId = "transfer_regular_v4_live_tuna_activation_cycle";
        const string sessionId = "session_regular_v4_live_tuna_activation_cycle";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 1_572_864).Select(static index => (byte)(index % 239)).ToArray();
        var allowFrontierChunk = 0;
        var blockPostReenableV4Chunks = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame.TransferId == transferId &&
                frame is FileTransferTransportProbeFrameV6 probe)
            {
                receiverTransport.NextDataFrameTransportKind = string.Equals(probe.TargetTransport, "tuna", StringComparison.OrdinalIgnoreCase)
                    ? FileTransferTransportKind.Tuna
                    : FileTransferTransportKind.RegularNkn;
            }

            if (frame.TransferId == transferId &&
                frame is FileTransferChunkBatchFrameV4 { StartChunkIndex: 0 } &&
                Volatile.Read(ref allowFrontierChunk) == 0)
            {
                return true;
            }

            if (frame.TransferId == transferId &&
                frame is FileTransferChunkBatchFrameV4 &&
                Volatile.Read(ref blockPostReenableV4Chunks) == 1)
            {
                return true;
            }

            if (frame.TransferId == transferId)
            {
                await Task.Delay(2, ct);
            }

            return false;
        };
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame.TransferId == transferId &&
                frame is FileTransferTransportProbeFrameV6 probe)
            {
                senderTransport.NextDataFrameTransportKind = string.Equals(probe.TargetTransport, "tuna", StringComparison.OrdinalIgnoreCase)
                    ? FileTransferTransportKind.Tuna
                    : FileTransferTransportKind.RegularNkn;
            }

            return Task.FromResult(false);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("regular-v4-live-tuna-activation-cycle.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static state =>
                state.MissingRanges.Any(static range =>
                    range.StartChunkIndex == 0 &&
                    range.ChunkCount > 0)),
            timeoutMs: 10000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests("tuna_activation_negotiating");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);

        senderTransport.IsFileTunaActiveForRouteSelection = true;
        receiverTransport.IsFileTunaActiveForRouteSelection = true;
        senderTransport.SetConnectedDataSessionsAvailableForTests("tuna_activation_negotiated_transport_ready");
        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_activation_answer_ack",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        receiverTransport.RequestAllDataSessionHandoffs(
            "tuna_activation_answer_ack",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "live_route_epoch=1; previous_route=regular_nkn_v4_fast; route=file_tuna_v4",
                StringComparison.Ordinal),
            timeoutMs: 10000);

        senderTransport.IsFileTunaActiveForRouteSelection = false;
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        receiverTransport.IsFileTunaActiveForRouteSelection = false;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        senderTransport.SetConnectedDataSessionsUnavailableForTests(
            "header_switch_off_first",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "live_route_epoch=2; previous_route=file_tuna_v4; route=post_tuna_fallback_v6",
                StringComparison.Ordinal),
            timeoutMs: 5000);
        Volatile.Write(ref allowFrontierChunk, 1);
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_live_route_epoch_recovered; direction=outbound",
                StringComparison.Ordinal) &&
                  ReadOperationalLogTail(logStart).Contains(
                "live_route_epoch=2; route=post_tuna_fallback_v6",
                StringComparison.Ordinal),
            timeoutMs: 10000);

        Volatile.Write(ref blockPostReenableV4Chunks, 1);
        senderTransport.IsFileTunaActiveForRouteSelection = true;
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = false;
        receiverTransport.IsFileTunaActiveForRouteSelection = true;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = false;
        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_reenabled",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        receiverTransport.RequestAllDataSessionHandoffs(
            "tuna_reenabled",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "live_route_epoch=3; previous_route=post_tuna_fallback_v6; route=file_tuna_v4",
                StringComparison.Ordinal),
            timeoutMs: 10000);

        senderTransport.IsFileTunaActiveForRouteSelection = false;
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        receiverTransport.IsFileTunaActiveForRouteSelection = false;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        receiverTransport.SetConnectedDataSessionsUnavailableForTests(
            "helper_switch_off_second",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "live_route_epoch=4; previous_route=file_tuna_v4; route=post_tuna_fallback_v6",
                StringComparison.Ordinal),
            timeoutMs: 5000);
        Volatile.Write(ref blockPostReenableV4Chunks, 0);
        receiverTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 30000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var log = ReadV4SenderLogSnapshot(logStart);
        Assert.Single(senderTransport.SentOffers.Where(offer => offer.TransferId == transferId));
        Assert.Single(senderTransport.SentSessionOpens.Where(open => open.TransferId == transferId));
        Assert.Empty(senderTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
        Assert.Empty(receiverTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
        AssertLiveRouteEpochProofSequence(
            log,
            (1, "regular_nkn_v4_fast", "file_tuna_v4", FileTransferProtocol.ProtocolVersionV4, "normal_to_tuna_activation", "tuna"),
            (2, "file_tuna_v4", "post_tuna_fallback_v6", FileTransferProtocol.ProtocolVersionV6, "tuna_to_normal_fallback", "regular_nkn"),
            (3, "post_tuna_fallback_v6", "file_tuna_v4", FileTransferProtocol.ProtocolVersionV4, "normal_to_tuna_activation", "tuna"),
            (4, "file_tuna_v4", "post_tuna_fallback_v6", FileTransferProtocol.ProtocolVersionV6, "tuna_to_normal_fallback", "regular_nkn"));
        Assert.Contains("live_route_epoch=1; previous_route=regular_nkn_v4_fast; route=file_tuna_v4", log, StringComparison.Ordinal);
        Assert.Contains("live_route_epoch=2; previous_route=file_tuna_v4; route=post_tuna_fallback_v6", log, StringComparison.Ordinal);
        Assert.Contains("live_route_epoch=3; previous_route=post_tuna_fallback_v6; route=file_tuna_v4", log, StringComparison.Ordinal);
        Assert.Contains("live_route_epoch=4; previous_route=file_tuna_v4; route=post_tuna_fallback_v6", log, StringComparison.Ordinal);
        Assert.Contains("handoff_kind=normal_to_tuna_activation", log, StringComparison.Ordinal);
        Assert.Contains("handoff_kind=tuna_to_normal_fallback", log, StringComparison.Ordinal);
        Assert.Contains("target_transport=tuna", log, StringComparison.Ordinal);
        Assert.Contains("target_transport=regular_nkn", log, StringComparison.Ordinal);
        Assert.DoesNotContain("file_tuna_v6", log, StringComparison.Ordinal);
        Assert.False(senderTransport.IsPostTunaFileFallbackActiveForRouteSelection);
        Assert.False(receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection);

        const string nextTransferId = "transfer_regular_v4_live_tuna_activation_cycle_next";
        await RunCompletedLoopbackTransferAsync(
            sender,
            receiver,
            nextTransferId,
            "regular-v4-live-tuna-activation-cycle-next.bin");
        var nextOffer = senderTransport.SentOffers.Single(offer => offer.TransferId == nextTransferId);
        Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, nextOffer.FileTransferRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, nextOffer.PreferredDataProtocolVersion);
    }

    [Fact]
    public async Task PrimaryRegularNknV4Fast_OutboundProgressTracksReceiverSparseBytesWritten()
    {
        const string transferId = "transfer_v4_sender_progress_sparse_written";
        const string sessionId = "session_v4_sender_progress_sparse_written";
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var droppedFrontierBatchCount = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.OutboundDataFrameDeliveryOverrideWithLaneAsync = (_, frame, _, _) =>
        {
            if (frame is FileTransferChunkBatchFrameV4 batch &&
                frame is not FileTransferChunkBatchFrameV6 &&
                batch.StartChunkIndex <= 0 &&
                batch.StartChunkIndex + batch.ChunkCount > 0)
            {
                Interlocked.Increment(ref droppedFrontierBatchCount);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-progress-sparse-written.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        var writtenProgressTarget = 3L * 21 * 1024;
        await WaitUntilAsync(
            () =>
            {
                var inbound = receiver.Snapshot.Inbound;
                var outbound = sender.Snapshot.Outbound;
                return Volatile.Read(ref droppedFrontierBatchCount) > 0 &&
                       inbound?.BytesTransferred == 0 &&
                       (inbound.BytesAcceptedForTransport ?? 0L) >= writtenProgressTarget &&
                       (outbound?.ProgressBytes ?? 0L) >= writtenProgressTarget;
            },
            timeoutMs: 10000);

        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, Assert.Single(senderTransport.SentSessionOpens).ProtocolVersion);
        Assert.Equal(0, receiver.Snapshot.Inbound!.BytesTransferred);
        Assert.True(receiver.Snapshot.Inbound!.BytesAcceptedForTransport >= writtenProgressTarget);
        Assert.True(sender.Snapshot.Outbound!.ProgressBytes >= writtenProgressTarget);
        Assert.Contains(
            receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>(),
            state => state is not FileTransferReceiverStateFrameV6 &&
                     state.ContiguousCommittedChunkIndex == 0 &&
                     state.BytesCommitted >= writtenProgressTarget);

        var receiverProgressAtPause = receiver.Snapshot.Inbound!.BytesAcceptedForTransport!.Value;
        var stateCountBeforePause = receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Count();
        Assert.NotNull(await receiver.PauseTransferAsync(transferId, "receiver_pause", CancellationToken.None));
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound is { IsPeerPaused: true } outbound &&
                  outbound.ProgressBytes >= receiverProgressAtPause,
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames
                .OfType<FileTransferStateFrameV4>()
                .Skip(stateCountBeforePause)
                .Any(state => state is not FileTransferReceiverStateFrameV6 &&
                              state.TransferPaused &&
                              state.BytesCommitted >= receiverProgressAtPause),
            timeoutMs: 5000);
    }

    [Fact]
    public async Task PrimaryRegularNknV4Fast_TunaActivationPauseStopsSenderPump()
    {
        const string transferId = "transfer_v4_sender_tuna_activation_pause";
        const string sessionId = "session_v4_sender_tuna_activation_pause";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-tuna-activation-pause.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(),
            timeoutMs: 5000);

        senderTransport.SetLocalDataSessionsUnavailableForTests("tuna_activation_negotiating");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_v4_sender_pump_transport_paused;",
                StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () =>
            {
                var tail = ReadOperationalLogTail(logStart);
                return tail.Contains("event=filetransfer_tuna_activation_transport_pause_control_suppressed; direction=outbound", StringComparison.Ordinal) &&
                       tail.Contains("paused=1", StringComparison.Ordinal);
            },
            timeoutMs: 5000);

        var chunkBatchesAtPause = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Count();
        await Task.Delay(TimeSpan.FromMilliseconds(6200));

        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, Assert.Single(senderTransport.SentSessionOpens).ProtocolVersion);
        Assert.Equal(chunkBatchesAtPause, senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Count());
        Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
        Assert.DoesNotContain(
            "event=filetransfer_regular_v4_peer_silence_safety_replay_started;",
            ReadOperationalLogTail(logStart),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            senderTransport.SentPauseControls,
            pause => string.Equals(pause.Reason, "tuna_activation_negotiating", StringComparison.Ordinal));
        Assert.NotEqual(true, receiver.Snapshot.Inbound?.IsPeerPaused);

        senderTransport.SetLocalDataSessionsAvailableForTests("tuna_activation_negotiation_released");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_transport_resumed; direction=outbound",
                StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain("event=filetransfer_transport_pause_control_retry_scheduled; direction=outbound", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknV4Fast_TunaActivationReadyWaitsForRouteHandoffBeforeResumingPump()
    {
        const string transferId = "transfer_v4_sender_tuna_activation_ready_waits_for_handoff";
        const string sessionId = "session_v4_sender_tuna_activation_ready_waits_for_handoff";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-tuna-activation-ready-waits.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(),
            timeoutMs: 5000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests("tuna_activation_negotiating");
        await WaitUntilAsync(
            () =>
            {
                var log = ReadOperationalLogTail(logStart);
                return log.Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal) &&
                       log.Contains("event=filetransfer_v4_sender_pump_transport_paused;", StringComparison.Ordinal);
            },
            timeoutMs: 5000);

        await Task.Delay(TimeSpan.FromMilliseconds(1000));
        var chunkBatchesAtPause = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Count();

        senderTransport.IsFileTunaActiveForRouteSelection = true;
        receiverTransport.IsFileTunaActiveForRouteSelection = true;
        senderTransport.SetConnectedDataSessionsAvailableForTests("tuna_activation_negotiated_transport_ready");
        await WaitUntilAsync(
            () =>
            {
                var log = ReadOperationalLogTail(logStart);
                return log.Contains("event=filetransfer_tuna_activation_ready_resume_deferred_until_route_handoff; direction=outbound", StringComparison.Ordinal);
            },
            timeoutMs: 5000);
        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        Assert.Equal(chunkBatchesAtPause, senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Count());

        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_activation_answer_ack",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        receiverTransport.RequestAllDataSessionHandoffs(
            "tuna_activation_answer_ack",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "previous_route=regular_nkn_v4_fast; new_route=file_tuna_v4",
                StringComparison.Ordinal),
            timeoutMs: 10000);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV4>()
                .Any(static batch => !batch.ForceRegularNknBulk),
            timeoutMs: 10000);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 25000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain(
            "event=filetransfer_transport_resumed; direction=outbound; transfer_id=transfer_v4_sender_tuna_activation_ready_waits_for_handoff; session_id=session_v4_sender_tuna_activation_ready_waits_for_handoff; reason=tuna_activation_negotiated_transport_ready",
            logTail,
            StringComparison.Ordinal);
        Assert.Contains("handoff_kind=normal_to_tuna_activation", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("file_tuna_v6", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknV4Fast_TunaActivationPauseBeforeManifestWaitsForRelease()
    {
        const string transferId = "transfer_v4_sender_tuna_activation_pause_before_manifest";
        const string sessionId = "session_v4_sender_tuna_activation_pause_before_manifest";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 1_500_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            ThrowWhenUnavailableDataSessionSend = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.Connect(receiverTransport);
        senderTransport.OutboundSessionOpenDeliveryOverrideAsync = (target, message, ct) =>
        {
            target.ReceiveDeliveredSessionOpen(message);
            senderTransport.SetLocalDataSessionsUnavailableForTests("tuna_activation_negotiating");
            return Task.FromResult(true);
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-tuna-activation-pause-before-manifest.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(
            () =>
            {
                var tail = ReadV4SenderLogSnapshot(logStart);
                return tail.Contains("event=filetransfer_tuna_activation_transport_pause_control_suppressed; direction=outbound", StringComparison.Ordinal) &&
                       tail.Contains("paused=1", StringComparison.Ordinal);
            },
            timeoutMs: 20_000);
        Assert.DoesNotContain(
            senderTransport.SentDataFrames,
            frame => string.Equals(frame.TransferId, transferId, StringComparison.Ordinal) &&
                     frame is FileTransferManifestFrameV4);
        Assert.DoesNotContain(
            senderTransport.SentPauseControls,
            pause => string.Equals(pause.TransferId, transferId, StringComparison.Ordinal) &&
                     string.Equals(pause.Reason, "tuna_activation_negotiating", StringComparison.Ordinal));
        senderTransport.SetLocalDataSessionsAvailableForTests("tuna_activation_negotiation_released");
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, Assert.Single(senderTransport.SentSessionOpens).ProtocolVersion);
        Assert.Contains(
            senderTransport.SentDataFrames,
            frame => string.Equals(frame.TransferId, transferId, StringComparison.Ordinal) &&
                     frame is FileTransferManifestFrameV4 and not FileTransferManifestFrameV6);
        Assert.Equal(payload, destination.ToArray());
        var logTail = ReadV4SenderLogSnapshot(logStart);
        Assert.DoesNotContain("event=filetransfer_v4_sender_failed;", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_paused; direction=outbound", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknV4Fast_TunaActivationFailureResumeDoesNotStarveTransfer()
    {
        const string transferId = "transfer_v4_sender_tuna_activation_failure_resume";
        const string sessionId = "session_v4_sender_tuna_activation_failure_resume";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-tuna-activation-failure-resume.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(),
            timeoutMs: 5000);

        senderTransport.SetLocalDataSessionsUnavailableForTests("tuna_activation_negotiating");
        await WaitUntilAsync(
            () => ReadV4SenderLogSnapshot(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () =>
            {
                var tail = ReadV4SenderLogSnapshot(logStart);
                return tail.Contains("event=filetransfer_tuna_activation_transport_pause_control_suppressed; direction=outbound", StringComparison.Ordinal) &&
                       tail.Contains("paused=1", StringComparison.Ordinal);
            },
            timeoutMs: 5000);

        senderTransport.SetLocalDataSessionsAvailableForTests("tuna_activation_failed_regular_v4_resumed");
        await WaitUntilAsync(
            () =>
            {
                var tail = ReadV4SenderLogSnapshot(logStart);
                return tail.Contains("event=filetransfer_tuna_activation_transport_pause_control_suppressed; direction=outbound", StringComparison.Ordinal) &&
                       tail.Contains("paused=0", StringComparison.Ordinal);
            },
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, Assert.Single(senderTransport.SentSessionOpens).ProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, Assert.Single(senderTransport.SentOffers).FileTransferRoute);
        Assert.Equal(payload, destination.ToArray());
        var logTail = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain("event=filetransfer_v4_sender_failed;", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_transport_pause_control_retry_scheduled; direction=outbound", logTail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("receive_stall_recovery")]
    [InlineData("transport_recovered_unproven")]
    public async Task PrimaryRegularNknV4Fast_RecoveryPauseDuringRuntimeUnlockDoesNotFailSender(string recoveryReason)
    {
        var suffix = recoveryReason.Replace('_', '-');
        var transferId = $"transfer_v4_sender_runtime_unlock_{suffix}";
        var sessionId = $"session_v4_sender_runtime_unlock_{suffix}";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_500_000).Select(static index => (byte)(index % 251)).ToArray();
        var blockedSendCount = 0;
        var sendBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlockedSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame is FileTransferChunkBatchFrameV4 &&
                Interlocked.Exchange(ref blockedSendCount, 1) == 0)
            {
                sendBlocked.TrySetResult();
                await releaseBlockedSend.Task.WaitAsync(ct).ConfigureAwait(false);
                throw new ObjectDisposedException($"{recoveryReason}_bridge_restart");
            }

            return false;
        };
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor($"v4-runtime-unlock-{suffix}.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(() => sendBlocked.Task.IsCompleted, timeoutMs: 5000);

            senderTransport.SetLocalDataSessionsUnavailableForTests(recoveryReason);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v4_transport_send_abandoned_for_transport_pause",
                    StringComparison.Ordinal),
                timeoutMs: 5000);
            releaseBlockedSend.TrySetResult();
            await Task.Delay(250);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            var pausedLog = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_transport_paused; direction=outbound", pausedLog, StringComparison.Ordinal);
            Assert.Contains($"reason={recoveryReason}", pausedLog, StringComparison.Ordinal);
            Assert.Contains(
                "event=filetransfer_v4_sender_pump_transport_paused_for_regular_v4_receive_stall_recovery",
                pausedLog,
                StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v4_sender_failed;", pausedLog, StringComparison.Ordinal);

            senderTransport.SetLocalDataSessionsAvailableForTests(recoveryReason);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 20000);
        }
        finally
        {
            releaseBlockedSend.TrySetResult();
        }

        Assert.Equal(1, Volatile.Read(ref blockedSendCount));
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain("event=filetransfer_v4_sender_failed;", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("file_tuna_v6", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknV4Fast_LateTunaActivationSendCancellationAfterResumeIsRetried()
    {
        const string transferId = "transfer_v4_sender_tuna_activation_late_cancellation";
        const string sessionId = "session_v4_sender_tuna_activation_late_cancellation";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 5_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var failNextSendAfterResume = 0;
        var lateCancellationThrown = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame.TransferId == transferId &&
                frame is FileTransferChunkBatchFrameV4 { StartChunkIndex: > 0 } &&
                Volatile.Read(ref failNextSendAfterResume) != 0 &&
                Interlocked.Exchange(ref lateCancellationThrown, 1) == 0)
            {
                await Task.Delay(5, ct).ConfigureAwait(false);
                throw new OperationCanceledException("The operation was canceled.");
            }

            return false;
        };
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-tuna-activation-late-cancellation.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(),
            timeoutMs: 5000);

        senderTransport.SetLocalDataSessionsUnavailableForTests("tuna_activation_negotiating");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);
        Volatile.Write(ref failNextSendAfterResume, 1);
        senderTransport.SetLocalDataSessionsAvailableForTests("tuna_activation_failed_regular_v4_resumed");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_resumed; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 25000);

        Assert.Equal(1, Volatile.Read(ref lateCancellationThrown));
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, Assert.Single(senderTransport.SentSessionOpens).ProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, Assert.Single(senderTransport.SentOffers).FileTransferRoute);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain("event=filetransfer_v4_sender_failed;", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("error_code=read_failed", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknV4Fast_SupersededRegularSendDoesNotStarveLateTunaActivation()
    {
        const string transferId = "transfer_v4_sender_tuna_activation_supersedes_regular_send";
        const string sessionId = "session_v4_sender_tuna_activation_supersedes_regular_send";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 8_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var blockOldRegularSend = 0;
        var blockedOldRegularSendCount = 0;
        var abandonedForLiveRouteChangeObserved = false;
        var routeChangeObserved = false;
        var fileTunaRouteObserved = false;
        var oldRegularSendBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldRegularSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame.TransferId == transferId &&
                frame is FileTransferChunkBatchFrameV4 { StartChunkIndex: > 0, ForceRegularNknBulk: true } &&
                Volatile.Read(ref blockOldRegularSend) != 0 &&
                Interlocked.Exchange(ref blockedOldRegularSendCount, 1) == 0)
            {
                oldRegularSendBlocked.TrySetResult();
                await releaseOldRegularSend.Task.WaitAsync(ct);
                return true;
            }

            return false;
        };
        senderTransport.Connect(receiverTransport);

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-tuna-activation-superseded-regular-send.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(static batch => batch.ForceRegularNknBulk),
                timeoutMs: 5000);

            senderTransport.SetConnectedDataSessionsUnavailableForTests("tuna_activation_negotiating");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal),
                timeoutMs: 5000);

            Volatile.Write(ref blockOldRegularSend, 1);
            senderTransport.SetConnectedDataSessionsAvailableForTests("tuna_activation_failed_regular_v4_resumed");
            await WaitUntilAsync(() => oldRegularSendBlocked.Task.IsCompleted, timeoutMs: 10000);

            senderTransport.IsFileTunaActiveForRouteSelection = true;
            receiverTransport.IsFileTunaActiveForRouteSelection = true;
            senderTransport.RequestAllDataSessionHandoffs(
                "tuna_activation_answer_ack",
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna);
            receiverTransport.RequestAllDataSessionHandoffs(
                "tuna_activation_answer_ack",
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna);

            await WaitUntilAsync(
                () =>
                {
                    var logTail = ReadOperationalLogTail(logStart);
                    abandonedForLiveRouteChangeObserved =
                        abandonedForLiveRouteChangeObserved ||
                        logTail.Contains(
                            "event=filetransfer_v4_transport_send_abandoned_for_live_route_change;",
                            StringComparison.Ordinal);
                    routeChangeObserved =
                        routeChangeObserved ||
                        logTail.Contains("previous_route=regular_nkn_v4_fast; new_route=file_tuna_v4", StringComparison.Ordinal);
                    fileTunaRouteObserved =
                        fileTunaRouteObserved ||
                        logTail.Contains("route=file_tuna_v4; protocol_version=4", StringComparison.Ordinal);
                    return abandonedForLiveRouteChangeObserved &&
                           routeChangeObserved &&
                           fileTunaRouteObserved;
                },
                timeoutMs: 10000);
            releaseOldRegularSend.TrySetResult();
            await WaitUntilAsync(
                () => senderTransport.SentDataFrames
                    .OfType<FileTransferChunkBatchFrameV4>()
                    .Any(static batch => !batch.ForceRegularNknBulk),
                timeoutMs: 10000);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 30000);
        }
        finally
        {
            releaseOldRegularSend.TrySetResult();
        }

        Assert.Equal(1, Volatile.Read(ref blockedOldRegularSendCount));
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.True(abandonedForLiveRouteChangeObserved, logTail);
        Assert.True(routeChangeObserved, logTail);
        Assert.True(fileTunaRouteObserved, logTail);
        Assert.DoesNotContain("file_tuna_v6", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v4_sender_failed;", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTunaFallbackV6_TunaActivationFailureResumeDoesNotStarveTransfer()
    {
        const string transferId = "transfer_v6_post_fallback_tuna_activation_failure_resume";
        const string sessionId = "session_v6_post_fallback_tuna_activation_failure_resume";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_500_000).Select(static index => (byte)(index % 251)).ToArray();
        var releasePostPauseFrames = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame is FileTransferChunkBatchFrameV6 { StartChunkIndex: > 0 } &&
                Volatile.Read(ref releasePostPauseFrames) == 0)
            {
                while (Volatile.Read(ref releasePostPauseFrames) == 0)
                {
                    await Task.Delay(10, ct);
                }
            }

            return false;
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("post-fallback-v6-tuna-activation-failure-resume.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0),
            timeoutMs: 5000);
        senderTransport.SetConnectedDataSessionsUnavailableForTests("tuna_activation_negotiating");
        await WaitUntilAsync(
            () => ReadV4SenderLogSnapshot(logStart).Contains("event=filetransfer_post_tuna_fallback_tuna_activation_pause_suppressed; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);

        senderTransport.SetConnectedDataSessionsAvailableForTests("tuna_activation_failed_regular_v4_resumed");
        Volatile.Write(ref releasePostPauseFrames, 1);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, Assert.Single(senderTransport.SentSessionOpens).ProtocolVersion);
        Assert.Equal(FileTransferRouteResolver.PostTunaFallbackV6Token, Assert.Single(senderTransport.SentOffers).FileTransferRoute);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        Assert.False(senderTransport.IsPostTunaFileFallbackActiveForRouteSelection);
        Assert.False(receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection);
        var logTail = ReadV4SenderLogSnapshot(logStart);
        Assert.DoesNotContain("event=filetransfer_v6_sender_failed;", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain(
            senderTransport.SentPauseControls,
            pause => string.Equals(pause.Reason, "tuna_activation_negotiating", StringComparison.Ordinal));
    }

    [Fact(Skip = RetiredDiagnosticRegularNknV6SparseCreditRuntimeSkip)]
    public async Task V6SparseSender_CompletesFromTerminalReadyStateWhenLifecycleCompleteIsLost()
    {
        const string transferId = "transfer_v6_sparse_terminal_ready_complete";
        const string sessionId = "session_v6_sparse_terminal_ready_complete";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 768_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            IsDiagnosticRegularNknV6RouteEnabled = true,
            TransportAccelerationStatusReason = "test_tuna_pending",
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            IsDiagnosticRegularNknV6RouteEnabled = true,
            TransportAccelerationStatusReason = "test_tuna_pending",
            OutboundCompleteDeliveryOverrideAsync = (_, _, _) => Task.FromResult(true),
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-terminal-ready.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 15000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, Assert.Single(senderTransport.SentSessionOpens).ProtocolVersion);
        Assert.Contains(receiverTransport.SentCompletes, complete =>
            string.Equals(complete.TransferId, transferId, StringComparison.Ordinal));
        Assert.Contains(
            receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>(),
            state => state.TerminalReady &&
                     state.ContiguousCommittedChunkIndex >= sender.Snapshot.Outbound!.ChunkCount);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v4_terminal_ready_completion_inferred;", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=complete_received;", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public void FileTunaV4SparseSender_CompletesFromTerminalReadyStateWhenLifecycleCompleteIsLost()
    {
        var context = CreateRegularNknV4OutboundContext(
            "transfer_file_tuna_v4_terminal_ready_complete",
            remoteNextExpectedChunkIndex: 512,
            chunksAcceptedForTransport: 512,
            remoteGrantedUntilExclusive: 512,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        var routeSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
        SetPrivateProperty(context, "RouteSelection", routeSelection);
        SetPrivateProperty(context, "RouteRuntime", routeSelection.RuntimeDescriptor);
        SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV4);
        SetPrivateProperty(context, "V4TerminalReady", true);
        SetPrivateProperty(context, "BytesTransferred", 512L * 21 * 1024);
        SetPrivateProperty(context, "BytesAcknowledgedByReceiver", 512L * 21 * 1024);
        SetPrivateProperty(context, "PullSenderPipelineCurrentInFlightFrames", 0);

        var method = typeof(SessionFileTransferService).GetMethod(
            "ShouldCompleteOutboundV4FromTerminalReadyStateLocked",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        Assert.True((bool)method!.Invoke(null, [context])!);
        Assert.Equal(FileTransferRouteResolver.FileTunaV4Token, routeSelection.TelemetryToken);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, routeSelection.ProtocolVersion);
    }

    [Fact(Skip = RetiredDiagnosticRegularNknV6SparseCreditRuntimeSkip)]
    public async Task PrimaryRegularNknBulkV6_OutboundProgressTracksReceiverSparseBytesWritten()
    {
        const string transferId = "transfer_primary_regular_nkn_progress_sparse_written";
        const string sessionId = "session_primary_regular_nkn_progress_sparse_written";
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var droppedFrontierBatchCount = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            IsDiagnosticRegularNknV6RouteEnabled = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            IsDiagnosticRegularNknV6RouteEnabled = true,
        };
        senderTransport.OutboundDataFrameDeliveryOverrideWithLaneAsync = (_, frame, _, _) =>
        {
            if (frame is FileTransferChunkBatchFrameV6 batch &&
                batch.StartChunkIndex <= 0 &&
                batch.StartChunkIndex + batch.ChunkCount > 0)
            {
                Interlocked.Increment(ref droppedFrontierBatchCount);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("primary-regular-nkn-progress.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        var writtenProgressTarget = 3L * 21 * 1024;
        await WaitUntilAsync(
            () =>
            {
                var inbound = receiver.Snapshot.Inbound;
                var outbound = sender.Snapshot.Outbound;
                return Volatile.Read(ref droppedFrontierBatchCount) > 0 &&
                       inbound?.BytesTransferred == 0 &&
                       (inbound.BytesAcceptedForTransport ?? 0L) >= writtenProgressTarget &&
                       (outbound?.ProgressBytes ?? 0L) >= writtenProgressTarget;
            },
            timeoutMs: 10000);

        Assert.Equal(0, receiver.Snapshot.Inbound!.BytesTransferred);
        Assert.True(receiver.Snapshot.Inbound!.BytesAcceptedForTransport >= writtenProgressTarget);
        Assert.True(sender.Snapshot.Outbound!.ProgressBytes >= writtenProgressTarget);
        Assert.Contains(
            receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>(),
            state => state.ContiguousCommittedChunkIndex == 0 &&
                     state.BytesCommitted >= writtenProgressTarget);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_TunaActivationProbeOverSparseReceiverAcksAndRecovers()
    {
        const string transferId = "transfer_primary_regular_nkn_bulk_v6_tuna_probe_ack";
        const string sessionId = "session_primary_regular_nkn_bulk_v6_tuna_probe_ack";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 1_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideWithLaneAsync = (target, frame, _, _) =>
        {
            if (frame is FileTransferTransportProbeFrameV6)
            {
                target.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
            }

            return Task.FromResult(false);
        };

        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new DelayedWriteMemoryStream(delayMilliseconds: 1);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("primary-regular-nkn-bulk-v6-tuna-probe.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(),
            timeoutMs: 5000);
        var primaryRegularBulkV6Selected = false;
        await WaitUntilAsync(
            () =>
            {
                var logTail = ReadOperationalLogTail(logStart);
                primaryRegularBulkV6Selected =
                    logTail.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=outbound", StringComparison.Ordinal) &&
                    logTail.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=inbound", StringComparison.Ordinal);
                return primaryRegularBulkV6Selected;
            },
            timeoutMs: 5000);
        await sender.PauseTransferAsync(transferId, "test_tuna_activation_probe", CancellationToken.None);

        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);

        await WaitUntilAsync(
            () => receiverTransport.SentTransportProbes.Any(
                probe => string.Equals(probe.TransferId, transferId, StringComparison.Ordinal) &&
                         string.Equals(probe.TargetTransport, "tuna", StringComparison.OrdinalIgnoreCase)),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.True(primaryRegularBulkV6Selected);
        Assert.Contains("event=filetransfer_v6_transport_probe_ack_sent; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.Contains("target_transport=tuna", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("reason=proof_timeout", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_TunaProbeAckHangDoesNotBlockPeerRepairRequest()
    {
        const string transferId = "transfer_primary_regular_nkn_bulk_v6_probe_ack_hang";
        const string sessionId = "session_primary_regular_nkn_bulk_v6_probe_ack_hang";
        var previousProbeAckTimeout = SessionFileTransferService.V6TransportProbeAckSendTimeoutOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var hungProbeAck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.OutboundTransportProbeDeliveryOverrideAsync = (_, _, _) => hungProbeAck.Task;
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        try
        {
            SessionFileTransferService.V6TransportProbeAckSendTimeoutOverrideForTests = TimeSpan.FromMilliseconds(100);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("primary-regular-nkn-bulk-v6-probe-ack-hang.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
            var offer = senderTransport.SentOffers.Single();
            await receiverTransport.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
            await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

            var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 64,
                    MissingRanges = [],
                    BytesCommitted = 0,
                },
                CancellationToken.None);
            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex >= 48),
                timeoutMs: 5000);

            senderTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
            await receiverSession.SendAsync(
                new FileTransferTransportProbeFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    TransportEpoch = 1,
                    ProbeId = "v6-probe:1:ack-hang",
                    TargetTransport = "tuna",
                },
                CancellationToken.None);
            await receiverSession.SendAsync(
                new FileTransferFrontierRequestFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    TransportEpoch = 1,
                    RepairRequestId = "v6-frontier:1:probe-ack-hang",
                    Priority = "frontier",
                    RecoveryMode = "frontier_repair_only",
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 24, ChunkCount = 12 }],
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_frontier_repair_requested; direction=outbound", StringComparison.Ordinal),
                timeoutMs: 5000);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_transport_probe_ack_failed; direction=outbound", StringComparison.Ordinal),
                timeoutMs: 5000);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("repair_request_id=v6-frontier:1:probe-ack-hang", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_transport_probe_ack_failed; direction=outbound", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V6TransportProbeAckSendTimeoutOverrideForTests = previousProbeAckTimeout;
            hungProbeAck.TrySetResult(true);
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_ResumeDuringTunaHandoffKeepsRepairEligible()
    {
        const string transferId = "transfer_primary_regular_nkn_bulk_v6_resume_handoff";
        const string sessionId = "session_primary_regular_nkn_bulk_v6_resume_handoff";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("primary-regular-nkn-bulk-v6-resume-handoff.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex >= 48),
            timeoutMs: 5000);

        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);

        Assert.NotNull(await sender.PauseTransferAsync(transferId, "test_tuna_activation_pause", CancellationToken.None));
        Assert.NotNull(await sender.ResumeTransferAsync(transferId, "test_tuna_activation_resume", CancellationToken.None));

        var repairBatchCountBefore = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 24,
                DurableReceivedHighestChunkIndex = 63,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 24, ChunkCount = 12 }],
                BytesCommitted = 24 * 21 * 1024,
                TransportEpoch = 1,
                RepairRequestId = "v6-state-frontier:1:resume-handoff",
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(repairBatchCountBefore)
                .Any(static batch =>
                    batch.StartChunkIndex == 24 &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain("reason=not_yet_sent; chunk_index=24", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v4_repair_sent; transfer_id=transfer_primary_regular_nkn_bulk_v6_resume_handoff; repair_request_key=24:12:24:12; range_count=1; requested_chunk_count=12; sent_chunk_count=0", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_TunaHandoffFrontierAtAcceptedTailResumesNormalSend()
    {
        const string transferId = "transfer_primary_regular_nkn_bulk_v6_handoff_frontier_accepted";
        const string sessionId = "session_primary_regular_nkn_bulk_v6_handoff_frontier_accepted";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("primary-regular-nkn-bulk-v6-handoff-frontier-accepted.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Any(static batch => batch.StartChunkIndex >= 48),
            timeoutMs: 5000);
        Assert.NotNull(await sender.PauseTransferAsync(transferId, "test_tuna_activation_pause", CancellationToken.None));
        var acceptedTail = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .Where(static batch => batch.RepairRequestId is null)
            .Select(static batch => batch.StartChunkIndex + batch.ChunkCount)
            .DefaultIfEmpty(0)
            .Max();
        Assert.InRange(acceptedTail, 1, 800);

        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);
        Assert.NotNull(await sender.ResumeTransferAsync(transferId, "test_tuna_activation_resume", CancellationToken.None));

        var logAfterHandoffState = GetOperationalLogLength();
        var batchCountBeforeHandoffProof = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = acceptedTail,
                DurableReceivedHighestChunkIndex = acceptedTail - 1,
                CreditUntilChunkIndexExclusive = acceptedTail + 64,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = acceptedTail, ChunkCount = 12 }],
                BytesCommitted = acceptedTail * 21 * 1024,
                TransportEpoch = 1,
                RepairRequestId = "v6-state-frontier:1:caught-up",
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logAfterHandoffState).Contains("event=filetransfer_v6_epoch_tail_caught_up_to_accepted;", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforeHandoffProof)
                .Any(batch =>
                    batch.StartChunkIndex >= acceptedTail &&
                    batch.RepairRequestId is null &&
                    batch.RecoveryMode is null),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        var proofLog = ReadOperationalLogTail(logAfterHandoffState);
        Assert.Contains("event=filetransfer_v6_epoch_tail_unblocked; direction=outbound;", logTail, StringComparison.Ordinal);
        Assert.Contains("reason=frontier_caught_up_to_accepted_tail", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_tail_blocked_until_frontier_proof; direction=outbound;", proofLog, StringComparison.Ordinal);
        Assert.DoesNotContain($"reason=not_yet_sent; chunk_index={acceptedTail}", proofLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_TunaActivationFrontierRequestDoesNotCreateLegacyRegularNknHandoff()
    {
        const string transferId = "transfer_primary_regular_nkn_bulk_v6_tuna_frontier_no_legacy";
        const string sessionId = "session_primary_regular_nkn_bulk_v6_tuna_frontier_no_legacy";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            IsDiagnosticRegularNknV6RouteEnabled = true,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            IsDiagnosticRegularNknV6RouteEnabled = true,
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("primary-regular-nkn-bulk-v6-tuna-frontier-no-legacy.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 128,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Any(static batch => batch.RepairRequestId is null && batch.StartChunkIndex >= 96),
            timeoutMs: 5000);

        var acceptedTail = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .Where(static batch => batch.RepairRequestId is null)
            .Select(static batch => batch.StartChunkIndex + batch.ChunkCount)
            .DefaultIfEmpty(0)
            .Max();
        Assert.InRange(acceptedTail, 2, 800);
        var frontierChunk = acceptedTail - 1;

        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);
        var handoffEpoch = senderTransport.SentTransportEpochs.Last().TransportEpoch;
        var repairRequestId = $"v6-frontier:{handoffEpoch}:{frontierChunk}:1";

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                TransportEpoch = handoffEpoch,
                RepairRequestId = repairRequestId,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = frontierChunk, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Any(batch =>
                    string.Equals(batch.RepairRequestId, repairRequestId, StringComparison.Ordinal) &&
                    batch.TransportEpoch == handoffEpoch),
            timeoutMs: 5000);

        await receiverTransport.SendFileTransferRepairProofAsync(
            new FileTransferRepairProofV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                TransportEpoch = handoffEpoch,
                RepairRequestId = repairRequestId,
                AppliedChunkCount = 1,
                CommittedChunkIndex = acceptedTail,
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);

        var logAfterCleanState = GetOperationalLogLength();
        var batchCountBeforeCleanState = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = acceptedTail,
                DurableReceivedHighestChunkIndex = acceptedTail + 31,
                CreditUntilChunkIndexExclusive = acceptedTail + 64,
                MissingRanges = [],
                BytesCommitted = acceptedTail * 21 * 1024,
                TransportEpoch = handoffEpoch,
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforeCleanState)
                .Any(batch =>
                    batch.StartChunkIndex >= acceptedTail &&
                    batch.RepairRequestId is null &&
                    batch.RecoveryMode is null),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        var postCleanStateLog = ReadOperationalLogTail(logAfterCleanState);
        var announcedEpoch = Assert.Single(senderTransport.SentTransportEpochs.Where(epoch => epoch.TransportEpoch == handoffEpoch));
        Assert.Equal("normal_to_tuna_activation", announcedEpoch.HandoffKind);
        Assert.Equal("tuna", announcedEpoch.TargetTransport);
        Assert.DoesNotContain(
            senderTransport.SentTransportEpochs,
            epoch => string.Equals(epoch.HandoffKind, "regular_nkn_recovery", StringComparison.Ordinal));
        Assert.DoesNotContain("event=filetransfer_v6_handoff_epoch_started; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("handoff_kind=regular_nkn_recovery; source_transport=unknown; target_transport=regular_nkn; reason=peer_repair_request", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_tail_blocked_until_frontier_proof; direction=outbound;", postCleanStateLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_TunaSafetyReplayProofRecoversAndCurrentReceiverRangeWins()
    {
        const string transferId = "transfer_primary_regular_nkn_bulk_v6_safety_replay_proof";
        const string sessionId = "session_primary_regular_nkn_bulk_v6_safety_replay_proof";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 4_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("primary-regular-nkn-bulk-v6-safety-replay.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 128,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Any(static batch => batch.RepairRequestId is null && batch.StartChunkIndex >= 96),
            timeoutMs: 5000);

        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);
        var handoffEpoch = senderTransport.SentTransportEpochs.Last().TransportEpoch;

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Any(batch =>
                    batch.TransportEpoch == handoffEpoch &&
                    batch.RepairRequestId is not null &&
                    batch.RepairRequestId.StartsWith("transport_rebind_safety_replay:", StringComparison.Ordinal)),
            timeoutMs: 5000);
        var safetyReplayBatch = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .First(batch =>
                batch.TransportEpoch == handoffEpoch &&
                batch.RepairRequestId is not null &&
                batch.RepairRequestId.StartsWith("transport_rebind_safety_replay:", StringComparison.Ordinal));
        var safetyReplayRepairId = safetyReplayBatch.RepairRequestId!;
        var proofCommittedChunk = safetyReplayBatch.StartChunkIndex + safetyReplayBatch.ChunkCount;

        await receiverTransport.SendFileTransferRepairProofAsync(
            new FileTransferRepairProofV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                TransportEpoch = handoffEpoch,
                RepairRequestId = safetyReplayRepairId,
                AppliedChunkCount = safetyReplayBatch.ChunkCount,
                CommittedChunkIndex = proofCommittedChunk,
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);

        var repairBatchCountAfterProof = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        var currentRepairId = $"v6-state-frontier:{handoffEpoch}:current-gap";
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = proofCommittedChunk,
                DurableReceivedHighestChunkIndex = proofCommittedChunk + 31,
                CreditUntilChunkIndexExclusive = 128,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = proofCommittedChunk, ChunkCount = 12 }],
                BytesCommitted = proofCommittedChunk * 21 * 1024,
                TransportEpoch = handoffEpoch,
                RepairRequestId = currentRepairId,
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(repairBatchCountAfterProof)
                .Any(batch =>
                    batch.StartChunkIndex == proofCommittedChunk &&
                    string.Equals(batch.RepairRequestId, currentRepairId, StringComparison.Ordinal)),
            timeoutMs: 5000);

        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_epoch_repair_request_registered; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("source=transport_rebind_safety_replay", logTail, StringComparison.Ordinal);
        Assert.Contains("reason=frontier_repair_proof", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain($"repair_request_id={safetyReplayRepairId}; current_transport_epoch={handoffEpoch}; last_repair_request_id=(none); reason=repair_request_mismatch", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_UsesSparseCreditEngineUnderV6Envelope()
    {
        const string transferId = "transfer_v6_sparse_runtime_flag";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_flag");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_flag");
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            var routeRuntimeLogTail = string.Empty;
            await WaitUntilAsync(
                () =>
                {
                    var currentTail = ReadOperationalLogTail(logStart);
                    var observed =
                        currentTail.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=outbound", StringComparison.Ordinal) &&
                        currentTail.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=inbound", StringComparison.Ordinal);
                    if (observed)
                    {
                        routeRuntimeLogTail = currentTail;
                    }

                    return observed;
                },
                timeoutMs: 5000);

            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 15000);

            Assert.Equal(payload, destination.ToArray()[..payload.Length]);
            Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferChunkBatchFrameV6);
            Assert.Contains(receiverTransport.SentDataFrames, static frame => frame is FileTransferReceiverStateFrameV6);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=outbound", routeRuntimeLogTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_selected; direction=inbound", routeRuntimeLogTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v4_chunk_batch_sent;", logTail, StringComparison.Ordinal);
            Assert.Contains("frame_type=filetransfer.receiver_state.v6", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v4_finalize_started;", logTail, StringComparison.Ordinal);
        }
        finally
        {
        }
    }

    [Fact(Skip = RetiredDiagnosticRegularNknV6SparseCreditRuntimeSkip)]
    public async Task PrimaryRegularNknBulkV6_RequestsV4FileOnlyFrontierRepairsUnderV6Envelope()
    {
        const string transferId = "transfer_v6_sparse_runtime_file_only_repair";
        const int expectedFrontierRepairChunks = 12;
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_file_only_repair");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_file_only_repair");
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(
                frame is FileTransferChunkBatchFrameV6 { RepairRequestId: null } batch &&
                batch.StartChunkIndex < FileTransferProtocol.MaxStateMissingChunksV4);
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-file-only-repair.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

            await WaitUntilAsync(
                () => receiverTransport.SentDataFrames
                    .OfType<FileTransferReceiverStateFrameV6>()
                    .Any(state => state.MissingRanges.Any(range =>
                        range.StartChunkIndex == 0 &&
                        range.ChunkCount == expectedFrontierRepairChunks)),
                timeoutMs: 15000);

            var repairState = receiverTransport.SentDataFrames
                .OfType<FileTransferReceiverStateFrameV6>()
                .First(state => state.MissingRanges.Any(range => range.StartChunkIndex == 0));
            Assert.Equal(expectedFrontierRepairChunks, repairState.MissingRanges[0].ChunkCount);
            Assert.DoesNotContain(receiverTransport.SentDataFrames, static frame => frame.GetType() == typeof(FileTransferStateFrameV4));
        }
        finally
        {
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_PrioritizesFirstFrontierRepairAndSuppressesRecentRepeat()
    {
        const string transferId = "transfer_v6_regular_nkn_frontier_repair_lane";
        const string sessionId = "session_v6_regular_nkn_frontier_repair_lane";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-regular-nkn-frontier-repair-lane.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 128,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Any(static frame => frame.StartChunkIndex == 0),
            timeoutMs: 5000);

        var repairState = new FileTransferReceiverStateFrameV6
        {
            SessionId = offer.SessionId,
            TransferId = transferId,
            Epoch = 2,
            ContiguousCommittedChunkIndex = 0,
            DurableReceivedHighestChunkIndex = 24,
            CreditUntilChunkIndexExclusive = 128,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
            BytesCommitted = 0,
        };
        await receiverSession.SendAsync(repairState, CancellationToken.None);

        await WaitUntilAsync(
            () =>
            {
                var logTail = ReadOperationalLogTail(logStart);
                return logTail.Contains("event=filetransfer_v4_repair_sent;", StringComparison.Ordinal) &&
                       logTail.Contains("repair_request_key=0:12:0:12", StringComparison.Ordinal) &&
                       logTail.Contains("repair_delivery_escalation_reason=primary_regular_nkn_frontier_first_send", StringComparison.Ordinal);
            },
            timeoutMs: 5000);

        Assert.Contains(
            senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>(),
            static batch => batch.BatchProfile == "v4_repair_21k" &&
                            batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant);

        await receiverSession.SendAsync(repairState with { Epoch = 3 }, CancellationToken.None);
        await WaitUntilAsync(
            () =>
            {
                var logTail = ReadOperationalLogTail(logStart);
                return logTail.Contains("event=filetransfer_v4_repair_suppressed;", StringComparison.Ordinal) &&
                       logTail.Contains("repair_request_key=0:12:0:12", StringComparison.Ordinal) &&
                       logTail.Contains("reason=recently_sent", StringComparison.Ordinal) &&
                       logTail.Contains("repair_interval_ms=750", StringComparison.Ordinal);
            },
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        var scheduledFrontierFirstSendCount = log
            .Split('\n')
            .Count(static line => line.Contains("event=filetransfer_v4_repair_scheduled;", StringComparison.Ordinal) &&
                                  line.Contains("repair_request_key=0:12:0:12", StringComparison.Ordinal) &&
                                  line.Contains("repair_delivery_escalation_reason=primary_regular_nkn_frontier_first_send", StringComparison.Ordinal));
        Assert.Equal(1, scheduledFrontierFirstSendCount);
        Assert.Contains("scheduled_chunk_count=12", log, StringComparison.Ordinal);
        Assert.Contains("scheduled_chunk_indices=0,1,2,3,4,5,6,7,8,9,10,11", log, StringComparison.Ordinal);
        Assert.Contains("sent_chunk_count=12", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_frontier_repair_anchor_barrier;", log, StringComparison.Ordinal);
        Assert.Contains("anchor_start_chunk_index=0; anchor_chunk_count=3; remaining_chunk_count=9", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_mode=control_bulk_escalated", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_escalation_reason=primary_regular_nkn_frontier_first_send", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_UsesFrontierRepairTransactionIdForSenderDedupe()
    {
        const string transferId = "transfer_v6_regular_nkn_frontier_repair_transaction";
        const string sessionId = "session_v6_regular_nkn_frontier_repair_transaction";
        const string repairTransactionId = "regular-nkn-frontier:0:12:1";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-regular-nkn-frontier-repair-transaction.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 128,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Any(static frame => frame.StartChunkIndex == 0),
            timeoutMs: 5000);

        var repairState = new FileTransferReceiverStateFrameV6
        {
            SessionId = offer.SessionId,
            TransferId = transferId,
            Epoch = 2,
            ContiguousCommittedChunkIndex = 0,
            DurableReceivedHighestChunkIndex = 24,
            CreditUntilChunkIndexExclusive = 128,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
            BytesCommitted = 0,
            RepairRequestId = repairTransactionId,
            Priority = "frontier",
            RecoveryMode = "regular_nkn_frontier_stall_control_bulk",
        };
        await receiverSession.SendAsync(repairState, CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(batch =>
                string.Equals(batch.RepairRequestId, repairTransactionId, StringComparison.Ordinal) &&
                batch.Priority == "frontier"),
            timeoutMs: 5000);

        await receiverSession.SendAsync(repairState with { Epoch = 3 }, CancellationToken.None);
        await WaitUntilAsync(
            () =>
            {
                var logTail = ReadOperationalLogTail(logStart);
                return logTail.Contains("event=filetransfer_v4_repair_suppressed;", StringComparison.Ordinal) &&
                       logTail.Contains($"repair_request_key={repairTransactionId}", StringComparison.Ordinal) &&
                       logTail.Contains($"protocol_repair_request_id={repairTransactionId}", StringComparison.Ordinal) &&
                       logTail.Contains("reason=recently_sent", StringComparison.Ordinal);
            },
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_primary_regular_nkn_frontier_repair_transaction_received", log, StringComparison.Ordinal);
        Assert.Contains($"repair_request_key={repairTransactionId}", log, StringComparison.Ordinal);
        Assert.Contains($"protocol_repair_request_id={repairTransactionId}", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_UsesCheckpointSyncWhenFeedbackStallsWithoutCredit()
    {
        const string transferId = "transfer_v6_bulk_checkpoint_no_credit";
        const string sessionId = "session_v6_bulk_checkpoint_no_credit";
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousFeedbackDelay = SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests;
        var previousRefreshCooldown = SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropReceiverFeedback = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.DataSessionSendDelayMs = 40;
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromSeconds(5);
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = TimeSpan.FromMilliseconds(100);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-bulk-checkpoint-no-credit.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                      senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
                timeoutMs: 5000);

            senderTransport.ReceiveDeliveredDataFrame(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 10_000,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 0,
                    MissingRanges = [],
                    BytesCommitted = 0,
                });
            Volatile.Write(ref dropReceiverFeedback, 1);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(static frame =>
                    string.Equals(frame.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal)),
                timeoutMs: 10000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            var checkpoint = senderTransport.SentDataFrames
                .OfType<FileTransferFrontierRequestFrameV6>()
                .First(frame => string.Equals(frame.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal));
            Assert.Equal("checkpoint_sync", checkpoint.Priority);
            Assert.StartsWith("v6-regular-nkn-checkpoint-sync:", checkpoint.RepairRequestId, StringComparison.Ordinal);
            var refreshHint = Assert.Single(checkpoint.MissingRanges);
            Assert.Equal(1, refreshHint.ChunkCount);
            Assert.NotEmpty(FileTransferDataFrameCodec.Serialize(checkpoint));
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_sent",
                    StringComparison.Ordinal),
                timeoutMs: 5000);
            var logTail = ReadOperationalLogTail(logStart);
            var evidenceLog = logTail + Environment.NewLine + LocalOperationalLog.GetRecentLogText();
            Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_prepared", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_sent", logTail, StringComparison.Ordinal);
            Assert.Contains($"event=filetransfer_bridge_recovery_policy_selected; direction=outbound; transfer_id={transferId}", evidenceLog, StringComparison.Ordinal);
            Assert.Contains("bridge_recovery_policy=primary_regular_nkn_quiet", evidenceLog, StringComparison.Ordinal);
            Assert.Contains("state=checkpoint_sync_requested", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_regular_nkn_state_refresh_requested", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                senderTransport.ReceiveRecoveryRequests,
                request => request.Direction == FileTransferDirection.Outbound &&
                           string.Equals(request.TransferId, transferId, StringComparison.Ordinal));
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = previousFeedbackDelay;
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = previousRefreshCooldown;
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_CheckpointProofAfterTunaEpochRecovers()
    {
        const string transferId = "transfer_v6_bulk_checkpoint_epoch_proof";
        const string sessionId = "session_v6_bulk_checkpoint_epoch_proof";
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousFeedbackDelay = SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests;
        var previousRefreshCooldown = SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropReceiverFeedback = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.DataSessionSendDelayMs = 40;
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromSeconds(5);
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = TimeSpan.FromMilliseconds(100);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-bulk-checkpoint-epoch-proof.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                      senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
                timeoutMs: 5000);

            senderTransport.ReceiveDeliveredDataFrame(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 10_000,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 0,
                    MissingRanges = [],
                    BytesCommitted = 0,
                });
            Volatile.Write(ref dropReceiverFeedback, 1);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(static frame =>
                    string.Equals(frame.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal)),
                timeoutMs: 10000);

            var checkpoint = senderTransport.SentDataFrames
                .OfType<FileTransferFrontierRequestFrameV6>()
                .First(frame => string.Equals(frame.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal));
            Assert.StartsWith("v6-regular-nkn-checkpoint-sync:", checkpoint.RepairRequestId, StringComparison.Ordinal);

            senderTransport.RequestAllDataSessionHandoffs(
                "normal_to_tuna_activation",
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna);
            await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);
            var handoffEpoch = senderTransport.SentTransportEpochs.Last().TransportEpoch;
            var checkpointHint = checkpoint.MissingRanges.Single().StartChunkIndex;
            var committed = Math.Min(payload.Length / (21 * 1024), checkpointHint + 3);

            await receiverTransport.SendFileTransferRepairProofAsync(
                new FileTransferRepairProofV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = handoffEpoch,
                    RepairRequestId = checkpoint.RepairRequestId,
                    AppliedChunkCount = 3,
                    CommittedChunkIndex = committed,
                    RecoveryMode = "target_proof_pending",
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_epoch_recovered; direction=outbound", StringComparison.Ordinal),
                timeoutMs: 5000);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_repair_proof_unmatched_frontier_accepted; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.Contains($"repair_request_id={checkpoint.RepairRequestId}", logTail, StringComparison.Ordinal);
            Assert.Contains("reason=frontier_repair_proof", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain($"repair_request_id={checkpoint.RepairRequestId}; current_transport_epoch={handoffEpoch}; last_repair_request_id=(none); reason=repair_request_mismatch", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = previousFeedbackDelay;
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = previousRefreshCooldown;
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_CheckpointSyncSingleSendFailureDefersBridgeRecovery()
    {
        const string transferId = "transfer_v6_bulk_checkpoint_send_failure_defer";
        const string sessionId = "session_v6_bulk_checkpoint_send_failure_defer";
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousFeedbackDelay = SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests;
        var previousRefreshCooldown = SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropReceiverFeedback = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferFrontierRequestFrameV6 frontier &&
                string.Equals(frontier.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected checkpoint sync send failure.");
            }

            return Task.FromResult(false);
        };
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromSeconds(5);
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = TimeSpan.FromSeconds(10);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-bulk-checkpoint-send-failure-defer.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                      senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
                timeoutMs: 5000);

            senderTransport.ReceiveDeliveredDataFrame(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 10_000,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 0,
                    MissingRanges = [],
                    BytesCommitted = 0,
                });
            Volatile.Write(ref dropReceiverFeedback, 1);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("suppression_reason=quiet_policy_first_failure", StringComparison.Ordinal),
                timeoutMs: 10000);
            await Task.Delay(300);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            Assert.Contains(senderTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>(), static frame =>
                string.Equals(frame.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_send_failed", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_primary_regular_nkn_frontier_feedback_failed_recoverable", logTail, StringComparison.Ordinal);
            Assert.Contains("recovery_action=defer_bridge_recovery", logTail, StringComparison.Ordinal);
            Assert.Contains("bridge_recovery_policy=primary_regular_nkn_quiet", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_receive_recovery_requested", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain(
                senderTransport.ReceiveRecoveryRequests,
                request => request.Direction == FileTransferDirection.Outbound &&
                           string.Equals(request.TransferId, transferId, StringComparison.Ordinal));
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = previousFeedbackDelay;
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = previousRefreshCooldown;
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_CheckpointSyncSendFailureRequestsReceiveRecovery()
    {
        const string transferId = "transfer_v6_bulk_checkpoint_send_failure";
        const string sessionId = "session_v6_bulk_checkpoint_send_failure";
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousFeedbackDelay = SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests;
        var previousRefreshCooldown = SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropReceiverFeedback = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferFrontierRequestFrameV6 frontier &&
                string.Equals(frontier.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected checkpoint sync send failure.");
            }

            return Task.FromResult(false);
        };
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromSeconds(5);
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = TimeSpan.FromMilliseconds(100);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-bulk-checkpoint-send-failure.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                      senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
                timeoutMs: 5000);

            senderTransport.ReceiveDeliveredDataFrame(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 10_000,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 0,
                    MissingRanges = [],
                    BytesCommitted = 0,
                });
            Volatile.Write(ref dropReceiverFeedback, 1);

            await WaitUntilAsync(
                () => senderTransport.ReceiveRecoveryRequests.Any(request =>
                    request.Direction == FileTransferDirection.Outbound &&
                    string.Equals(request.TransferId, transferId, StringComparison.Ordinal) &&
                    string.Equals(request.Reason, "primary_regular_nkn_bulk_v6_checkpoint_sync_failed", StringComparison.Ordinal)),
                timeoutMs: 10000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            Assert.Contains(senderTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>(), static frame =>
                string.Equals(frame.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal));
            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_send_failed", logTail, StringComparison.Ordinal);
            Assert.Contains("suppression_reason=quiet_policy_first_failure", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_primary_regular_nkn_frontier_feedback_failed_recoverable", logTail, StringComparison.Ordinal);
            Assert.Contains("recovery_action=request_bridge_recovery", logTail, StringComparison.Ordinal);
            Assert.Contains("bridge_recovery_policy=primary_regular_nkn_quiet", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_receive_recovery_requested", logTail, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", logTail, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_epoch_started; direction=outbound", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = previousFeedbackDelay;
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = previousRefreshCooldown;
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_RebindUsesCheckpointSyncWithoutTransportEpoch()
    {
        const string transferId = "transfer_v6_bulk_checkpoint_rebind";
        const string sessionId = "session_v6_bulk_checkpoint_rebind";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
            DataSessionSendDelayMs = 25,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-bulk-checkpoint-rebind.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(),
            timeoutMs: 5000);

        var transportEpochCountBeforeRebind = senderTransport.SentTransportEpochs.Count + receiverTransport.SentTransportEpochs.Count;
        senderTransport.SetLocalDataSessionsUnavailableForTests("receive_stall_recovery");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);
        senderTransport.SetLocalDataSessionsAvailableForTests("receive_stall_recovery");

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>().Any(static frame =>
                string.Equals(frame.RecoveryMode, "regular_nkn_checkpoint_sync", StringComparison.Ordinal)),
            timeoutMs: 10000);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_sent; direction=outbound",
                StringComparison.Ordinal),
            timeoutMs: 5000);

        Assert.Equal(transportEpochCountBeforeRebind, senderTransport.SentTransportEpochs.Count + receiverTransport.SentTransportEpochs.Count);
        Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_rebind_started; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_sent; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_epoch_started; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_transport_rebind_generation_started; direction=outbound", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_DefersOutboundV6PeerLivenessTerminal()
    {
        const string transferId = "transfer_v6_sparse_runtime_v6_liveness";
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropReceiverFeedback = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_v6_liveness");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_v6_liveness");
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.DataSessionSendDelayMs = 20;
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        receiverTransport.OutboundHeartbeatDeliveryOverrideAsync = (_, _, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1);
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-v6-liveness.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                      senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
                timeoutMs: 5000);

            Volatile.Write(ref dropReceiverFeedback, 1);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v6_heartbeat_timeout_deferred_for_v6_regular_nkn_sparse_runtime",
                    StringComparison.Ordinal),
                timeoutMs: 10000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain($"event=filetransfer_v6_heartbeat_timeout; direction=outbound; transfer_id={transferId}", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = previousHeartbeatInterval;
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = previousPeerLivenessTimeout;
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_DefersInboundV6PeerLivenessTerminal()
    {
        const string transferId = "transfer_v6_sparse_runtime_inbound_v6_liveness";
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 20_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropSenderTraffic = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_inbound_v6_liveness");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_sparse_runtime_inbound_v6_liveness");
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.DataSessionSendDelayMs = 20;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropSenderTraffic) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        senderTransport.OutboundHeartbeatDeliveryOverrideAsync = (_, _, _) =>
            Task.FromResult(Volatile.Read(ref dropSenderTraffic) == 1);
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-inbound-v6-liveness.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Receiving &&
                      destination.Length > 0,
                timeoutMs: 5000);

            Volatile.Write(ref dropSenderTraffic, 1);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v6_heartbeat_timeout_deferred_for_v6_regular_nkn_sparse_runtime; direction=inbound",
                    StringComparison.Ordinal),
                timeoutMs: 10000);

            Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound?.State);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain($"event=filetransfer_v6_heartbeat_timeout; direction=inbound; transfer_id={transferId}", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = previousHeartbeatInterval;
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = previousPeerLivenessTimeout;
        }
    }

    [Fact(Skip = RetiredPostTunaFallbackSparseRuntimeSkip)]
    public async Task PostTunaFallbackSparseRuntime_OutboundFeedbackSilenceDefersTerminal()
    {
        const string transferId = "transfer_v6_post_fallback_feedback_silence";
        const string sessionId = "session_v6_post_fallback_feedback_silence";
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousFeedbackDelay = SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests;
        var previousRefreshCooldown = SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests;
        var previousStaleCreditDelay = SessionFileTransferService.V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelayOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropReceiverFeedback = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropReceiverFeedback) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        try
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = TimeSpan.FromMilliseconds(50);
            SessionFileTransferService.V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelayOverrideForTests = TimeSpan.FromMilliseconds(50);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-post-fallback-feedback-silence.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
            var offer = senderTransport.SentOffers.Single();
            await receiverTransport.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

            var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 64,
                    MissingRanges = [],
                    BytesCommitted = 0,
                },
                CancellationToken.None);
            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0),
                timeoutMs: 5000);

            receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
            senderTransport.RequestAllDataSessionHandoffs(
                "tuna_activation_negotiated",
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna);
            await WaitUntilAsync(
                () => senderTransport.ObservedV6TransportEpochs.Any(static snapshot =>
                    snapshot.IsUnresolved &&
                    snapshot.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    snapshot.TargetTransport == FileTransferTransportKind.Tuna),
                timeoutMs: 5000);
            var activationProbe = await ReceiveDataFrameOfTypeAsync<FileTransferTransportProbeFrameV6>(receiverSession);
            await receiverTransport.SendFileTransferTransportProbeAsync(
                new FileTransferTransportProbeV6
                {
                    SessionId = activationProbe.SessionId,
                    TransferId = activationProbe.TransferId,
                    TransportEpoch = activationProbe.TransportEpoch,
                    ProbeId = activationProbe.ProbeId,
                    TargetTransport = activationProbe.TargetTransport,
                },
                CancellationToken.None);
            await WaitUntilAsync(
                () => senderTransport.ObservedV6TransportEpochs.Any(snapshot =>
                    !snapshot.IsUnresolved &&
                    snapshot.State == V6TransportEpochState.Recovered &&
                    snapshot.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation),
                timeoutMs: 5000);

            senderTransport.RequestAllDataSessionHandoffs(
                "sidecar_read_failed",
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn);
            await WaitUntilAsync(
                () => senderTransport.ObservedV6TransportEpochs.Any(static snapshot =>
                    snapshot.IsUnresolved &&
                    snapshot.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                    snapshot.TargetTransport == FileTransferTransportKind.RegularNkn),
                timeoutMs: 5000);
            var probe = await ReceiveDataFrameOfTypeAsync<FileTransferTransportProbeFrameV6>(receiverSession);
            await receiverTransport.SendFileTransferTransportProbeAsync(
                new FileTransferTransportProbeV6
                {
                    SessionId = probe.SessionId,
                    TransferId = probe.TransferId,
                    TransportEpoch = probe.TransportEpoch,
                    ProbeId = probe.ProbeId,
                    TargetTransport = probe.TargetTransport,
                },
                CancellationToken.None);
            await WaitUntilAsync(
                () => senderTransport.ObservedV6TransportEpochs.Any(snapshot =>
                    !snapshot.IsUnresolved &&
                    snapshot.State == V6TransportEpochState.Recovered &&
                    snapshot.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback),
                timeoutMs: 5000);

            Volatile.Write(ref dropReceiverFeedback, 1);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v4_post_tuna_fallback_peer_feedback_timeout_deferred; direction=outbound",
                    StringComparison.Ordinal),
                timeoutMs: 10000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain($"event=filetransfer_v4_peer_feedback_timeout; transfer_id={transferId}", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
            SessionFileTransferService.V6SenderRequestFeedbackStallRecoveryDelayOverrideForTests = previousFeedbackDelay;
            SessionFileTransferService.V6RegularNknSparseRuntimeStateRefreshCooldownOverrideForTests = previousRefreshCooldown;
            SessionFileTransferService.V6RegularNknSparseRuntimeStaleCreditReceiveRecoveryDelayOverrideForTests = previousStaleCreditDelay;
        }
    }

    [Fact(Skip = RetiredPostTunaFallbackSparseRuntimeSkip)]
    public async Task PostTunaFallbackSparseRuntime_InboundReceiveSilenceDefersTerminal()
    {
        const string transferId = "transfer_v6_post_fallback_receive_silence";
        const string sessionId = "session_v6_post_fallback_receive_silence";
        var previousV4Timeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var dropSenderTraffic = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        senderTransport.DataSessionSendDelayMs = 20;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(Volatile.Read(ref dropSenderTraffic) == 1 && FileTransferProtocol.IsV6DataFrame(frame));
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        try
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromMilliseconds(150);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-post-fallback-receive-silence.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Receiving &&
                      destination.Length > 0,
                timeoutMs: 5000);

            receiverTransport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
            receiverTransport.RequestAllDataSessionHandoffs(
                "tuna_activation_negotiated",
                FileTransferTransportHandoffKind.NormalToTunaActivation,
                FileTransferTransportKind.Tuna);
            await WaitUntilAsync(
                () => receiverTransport.ObservedV6TransportEpochs.Any(static snapshot =>
                    snapshot.IsUnresolved &&
                    snapshot.HandoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                    snapshot.TargetTransport == FileTransferTransportKind.Tuna),
                timeoutMs: 5000);

            receiverTransport.RequestAllDataSessionHandoffs(
                "sidecar_read_failed",
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn);
            await WaitUntilAsync(
                () => receiverTransport.ObservedV6TransportEpochs.Any(static snapshot =>
                    snapshot.IsUnresolved &&
                    snapshot.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                    snapshot.TargetTransport == FileTransferTransportKind.RegularNkn),
                timeoutMs: 5000);

            Volatile.Write(ref dropSenderTraffic, 1);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v4_post_tuna_fallback_peer_receive_timeout_deferred; direction=inbound",
                    StringComparison.Ordinal),
                timeoutMs: 10000);

            Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound?.State);
            var logTail = ReadOperationalLogTail(logStart);
            Assert.DoesNotContain($"event=filetransfer_v4_peer_receive_timeout; transfer_id={transferId}", logTail, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousV4Timeout;
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_PausedTransportBlocksV4SenderPump()
    {
        const string transferId = "transfer_v6_sparse_runtime_transport_pause";
        const string sessionId = "session_v6_sparse_runtime_transport_pause";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
        senderTransport.TransportAccelerationStatusReason = "test_tuna_pending";
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
        receiverTransport.TransportAccelerationStatusReason = "test_tuna_pending";
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        try
        {
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-transport-pause.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
            var offer = senderTransport.SentOffers.Single();
            await receiverTransport.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
            await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

            var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 1,
                    MissingRanges = [],
                    BytesCommitted = 0,
                },
                CancellationToken.None);
            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count() == 1,
                timeoutMs: 5000);

            var chunkBatchesBeforePause = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
            senderTransport.SetLocalDataSessionsUnavailableForTests("receive_stall_recovery");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal),
                timeoutMs: 5000);

            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = 16,
                    CreditUntilChunkIndexExclusive = 32,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 8 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v4_sender_pump_transport_paused_for_v6_regular_nkn_sparse_runtime",
                    StringComparison.Ordinal),
                timeoutMs: 5000);
            await Task.Delay(300);

            Assert.Equal(chunkBatchesBeforePause, senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count());
            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
        }
        finally
        {
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_TunaFallbackPauseAllowsQueuedRepair()
    {
        const string transferId = "transfer_v6_sparse_runtime_fallback_repair";
        const string sessionId = "session_v6_sparse_runtime_fallback_repair";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
        senderTransport.TransportAccelerationStatusReason = "test_tuna_pending";
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
        receiverTransport.TransportAccelerationStatusReason = "test_tuna_pending";
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        try
        {
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-fallback-repair.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
            var offer = senderTransport.SentOffers.Single();
            await receiverTransport.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
            await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

            var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 1,
                    MissingRanges = [],
                    BytesCommitted = 0,
                },
                CancellationToken.None);
            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count() == 1,
                timeoutMs: 5000);

            var chunkBatchesBeforePause = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
            senderTransport.SetLocalDataSessionsUnavailableForTests("remote_remote_closed");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal),
                timeoutMs: 5000);

            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 2,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = 16,
                    CreditUntilChunkIndexExclusive = 32,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                    BytesCommitted = 0,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count() > chunkBatchesBeforePause,
                timeoutMs: 5000);

            var logTail = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v4_repair_sent;", logTail, StringComparison.Ordinal);
            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
        }
        finally
        {
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_AbandonsPendingSendsAfterPausedTransportFailure()
    {
        const string transferId = "transfer_v6_sparse_runtime_abandon_pending";
        const string sessionId = "session_v6_sparse_runtime_abandon_pending";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 4_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var firstSendGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldSendGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunkSendAttempts = 0;
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
        senderTransport.TransportAccelerationStatusReason = "test_tuna_pending";
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
        receiverTransport.TransportAccelerationStatusReason = "test_tuna_pending";
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame is not FileTransferChunkBatchFrameV6)
            {
                return false;
            }

            var attempt = Interlocked.Increment(ref chunkSendAttempts);
            if (attempt == 1)
            {
                await firstSendGate.Task.WaitAsync(ct);
                throw new ObjectDisposedException("System.Threading.SemaphoreSlim");
            }

            await heldSendGate.Task.WaitAsync(ct);
            return true;
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        try
        {
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-abandon-pending.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
            var offer = senderTransport.SentOffers.Single();
            await receiverTransport.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
            await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

            var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 64,
                    MissingRanges = [],
                    BytesCommitted = 0,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.ActiveDataSessionSends >= 4, timeoutMs: 5000);

            senderTransport.SetLocalDataSessionsUnavailableForTests("receive_stall_recovery");
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_paused; direction=outbound", StringComparison.Ordinal),
                timeoutMs: 5000);
            firstSendGate.SetResult(true);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v4_pending_transport_sends_abandoned_for_v6_regular_nkn_sparse_runtime",
                    StringComparison.Ordinal),
                timeoutMs: 5000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
        }
        finally
        {
            heldSendGate.TrySetResult(true);
            firstSendGate.TrySetResult(true);
        }
    }

    [Fact]
    public async Task PrimaryRegularNknBulkV6_BoundsHungNormalTransportSends()
    {
        const string transferId = "transfer_v6_sparse_runtime_bound_normal_send";
        const string sessionId = "session_v6_sparse_runtime_bound_normal_send";
        var previousSendTimeout = SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests;
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        receiverTransport.FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame is not FileTransferChunkBatchFrameV6)
            {
                return false;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return true;
        };
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        try
        {
            SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests = TimeSpan.FromMilliseconds(100);
            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v6-sparse-runtime-bound-normal-send.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
            var offer = senderTransport.SentOffers.Single();
            await receiverTransport.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
            await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

            var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 64,
                    MissingRanges = [],
                    BytesCommitted = 0,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.ActiveDataSessionSends > 0, timeoutMs: 5000);
            await WaitUntilAsync(
                () =>
                {
                    var log = ReadOperationalLogTail(logStart);
                    return log.Contains(
                               "event=filetransfer_v4_transport_send_timeout_deferred_for_v6_regular_nkn_sparse_runtime",
                               StringComparison.Ordinal) &&
                           log.Contains("repair_send=0", StringComparison.Ordinal);
                },
                timeoutMs: 5000);

            Assert.NotEqual(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
        }
        finally
        {
            SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests = previousSendTimeout;
        }
    }

    [Fact]
    public async Task V4Sender_LocalCancel_RetriesControlCancelWhenFirstSignalIsLost()
    {
        const string transferId = "transfer_v4_sender_cancel_retry";
        var payload = Enumerable.Range(0, 768_000).Select(static index => (byte)(index % 251)).ToArray();
        var cancelControlAttempts = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_cancel_retry");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_cancel_retry");
        senderTransport.DataSessionSendDelayMs = 10;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(frame is FileTransferCancelFrameV6);
        senderTransport.OutboundCancelDeliveryOverrideAsync = (_, _, _) =>
        {
            var attempt = Interlocked.Increment(ref cancelControlAttempts);
            return Task.FromResult(attempt == 1);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-cancel-retry.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(),
            timeoutMs: 5000);

        var canceled = await sender.CancelTransferAsync(transferId, "user_canceled", CancellationToken.None);

        Assert.NotNull(canceled);
        Assert.Equal(FileTransferTransferState.Canceled, canceled!.State);
        Assert.Equal(FileTransferResultCodes.CanceledLocal, canceled.ErrorCode);
        Assert.Contains(senderTransport.SentCancels, cancel => string.Equals(cancel.TransferId, transferId, StringComparison.Ordinal));
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferCancelFrameV4 and not FileTransferCancelFrameV6);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 6000);
        await WaitUntilAsync(() => System.Threading.Volatile.Read(ref cancelControlAttempts) >= 2, timeoutMs: 3000);
        Assert.True(cancelControlAttempts >= 2);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);
    }

    [Fact]
    public async Task V4Sender_LocalCancel_RetrySurvivesTransientOperationCanceled()
    {
        const string transferId = "transfer_v4_sender_cancel_retry_operation_canceled";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 768_000).Select(static index => (byte)(index % 251)).ToArray();
        var cancelControlAttempts = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_cancel_retry_operation_canceled");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_cancel_retry_operation_canceled");
        senderTransport.DataSessionSendDelayMs = 10;
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(frame is FileTransferCancelFrameV4);
        senderTransport.OutboundCancelDeliveryOverrideAsync = (_, _, _) =>
        {
            var attempt = Interlocked.Increment(ref cancelControlAttempts);
            return attempt <= 2
                ? Task.FromException<bool>(new OperationCanceledException("Injected transient cancel send cancellation."))
                : Task.FromResult(false);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-cancel-retry-operation-canceled.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(),
            timeoutMs: 5000);

        var canceled = await sender.CancelTransferAsync(transferId, "user_canceled", CancellationToken.None);

        Assert.NotNull(canceled);
        Assert.Equal(FileTransferTransferState.Canceled, canceled!.State);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 6000);
        await WaitUntilAsync(() => System.Threading.Volatile.Read(ref cancelControlAttempts) >= 3, timeoutMs: 3000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_cancel_control_retry_deferred", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6Sender_LocalCancel_UsesRedundantDataCancelWhenControlPathIsLost()
    {
        const string transferId = "transfer_v6_fallback_sender_cancel_data_retry";
        var payload = Enumerable.Range(0, 1_500_000).Select(static index => (byte)(index % 251)).ToArray();
        var cancelDataAttempts = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_sender_cancel_data_retry");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_sender_cancel_data_retry");
        senderTransport.DataSessionSendDelayMs = 10;
        senderTransport.OutboundCancelDeliveryOverrideAsync = (_, _, _) => Task.FromResult(true);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferCancelFrameV6)
            {
                Interlocked.Increment(ref cancelDataAttempts);
            }

            return Task.FromResult(false);
        };
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-cancel-data-retry.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
            timeoutMs: 5000);

        var canceled = await sender.CancelTransferAsync(transferId, "user_canceled", CancellationToken.None);

        Assert.NotNull(canceled);
        Assert.Equal(FileTransferTransferState.Canceled, canceled!.State);
        Assert.Contains(senderTransport.SentCancels, cancel => string.Equals(cancel.TransferId, transferId, StringComparison.Ordinal));
        Assert.True(cancelDataAttempts > 0);
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferCancelFrameV6);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 6000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);
    }

    [Fact]
    public async Task V6FallbackSender_LocalCancel_IgnoresCanceledCallerTokenAndUsesPriorityControl()
    {
        const string transferId = "transfer_v6_fallback_sender_cancel_priority_token";
        var payload = Enumerable.Range(0, 1_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_sender_cancel_priority_token");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_sender_cancel_priority_token");
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
            Task.FromResult(frame is FileTransferCancelFrameV6 or FileTransferChunkBatchFrameV6);
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-cancel-priority-token.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(),
            timeoutMs: 5000);

        using var canceledToken = new CancellationTokenSource();
        canceledToken.Cancel();
        var canceled = await sender.CancelTransferAsync(transferId, "user_canceled", canceledToken.Token);

        Assert.NotNull(canceled);
        Assert.Equal(FileTransferTransferState.Canceled, canceled!.State);
        Assert.Equal(FileTransferResultCodes.CanceledLocal, canceled.ErrorCode);
        Assert.Contains(senderTransport.SentCancels, cancel => string.Equals(cancel.TransferId, transferId, StringComparison.Ordinal));
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 10_000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);

        var logText = ReadOperationalLogText();
        Assert.Contains("event=filetransfer_lifecycle_priority_sent; kind=cancel", logText, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_lifecycle_priority_received; kind=cancel", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6FallbackReceiver_RemoteCancelBypassesBlockedLifecycleTail()
    {
        const string transferId = "transfer_v6_fallback_cancel_bypasses_lifecycle";
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_cancel_bypasses_lifecycle");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_cancel_bypasses_lifecycle");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-cancel-bypasses-lifecycle.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Receiving,
            timeoutMs: 5000);

        var releaseLifecycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.InboundDispatchBeforeWorkAsyncForTests = (lane, operation) =>
            lane == "lifecycle" && operation == "complete"
                ? releaseLifecycle.Task
                : Task.CompletedTask;

        await senderTransport.SendFileTransferCompleteAsync(
            new FileTransferCompleteV1
            {
                SessionId = "session_v6_fallback_cancel_bypasses_lifecycle",
                TransferId = transferId,
                FileSizeBytes = payload.Length,
                Sha256Base64 = Convert.ToBase64String(SHA256.HashData(payload)),
            },
            CancellationToken.None);

        var canceled = await sender.CancelTransferAsync(transferId, "user_canceled", CancellationToken.None);

        Assert.NotNull(canceled);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 3000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);

        releaseLifecycle.SetResult();
    }

    [Fact]
    public async Task V4Sender_SessionEndCancel_NotifiesReceiverWhileTransferIsActive()
    {
        const string transferId = "transfer_v4_sender_session_end_cancel";
        var payload = Enumerable.Range(0, 5_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_session_end_cancel");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_session_end_cancel");
        senderTransport.DataSessionSendDelayMs = 20;
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-session-end-cancel.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV4>().Any(),
            timeoutMs: 5000);

        var canceledCount = await sender.CancelActiveTransfersForSessionEndAsync("session_end", CancellationToken.None);

        Assert.Equal(1, canceledCount);
        Assert.Equal(FileTransferTransferState.Canceled, sender.Snapshot.Outbound?.State);
        Assert.Equal(FileTransferResultCodes.CanceledLocal, sender.Snapshot.Outbound?.ErrorCode);
        Assert.Contains(senderTransport.SentCancels, cancel => string.Equals(cancel.Reason, "session_end", StringComparison.Ordinal));
        Assert.Contains(senderTransport.SentDataFrames, static frame => frame is FileTransferCancelFrameV4 and not FileTransferCancelFrameV6);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 6000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);
    }

    [Fact]
    public async Task V6FallbackSender_SessionEndCancel_SendsDataCancelBeforeSlowControlTimeout()
    {
        const string transferId = "transfer_v6_fallback_session_end_cancel_data_first";
        var payload = Enumerable.Range(0, 5_000_000).Select(static index => (byte)(index % 251)).ToArray();
        var controlStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controlCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dataCancelObserved = new TaskCompletionSource<FileTransferCancelFrameV6>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_session_end_cancel_data_first");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_session_end_cancel_data_first");
        senderTransport.DataSessionSendDelayMs = 10;
        senderTransport.OutboundCancelDeliveryOverrideAsync = async (_, _, ct) =>
        {
            controlStarted.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                controlCompleted.TrySetResult();
                return true;
            }
            catch (OperationCanceledException)
            {
                controlCompleted.TrySetResult();
                throw;
            }
        };
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferCancelFrameV6 cancel)
            {
                dataCancelObserved.TrySetResult(cancel);
            }

            return Task.FromResult(false);
        };
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-session-end-data-first.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
            timeoutMs: 5000);

        var cancelTask = sender.CancelActiveTransfersForSessionEndAsync("session_end", CancellationToken.None);

        await controlStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancelFrame = await dataCancelObserved.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.Equal("session_end", cancelFrame.Reason);
        Assert.Equal(transferId, cancelFrame.TransferId);
        Assert.False(controlCompleted.Task.IsCompleted);

        var canceledCount = await cancelTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, canceledCount);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 6000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound!.ErrorCode);
    }

    [Fact]
    public async Task V6FallbackReceiver_LocalCancel_NotifiesSenderWhileTransferIsActive()
    {
        const string transferId = "transfer_v6_fallback_receiver_local_cancel";
        var payload = Enumerable.Range(0, 5_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_receiver_local_cancel");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_receiver_local_cancel");
        senderTransport.DataSessionSendDelayMs = 20;
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-receiver-local-cancel.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Receiving &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
            timeoutMs: 5000);

        var canceled = await receiver.CancelTransferAsync(transferId, "user_canceled", CancellationToken.None);

        Assert.NotNull(canceled);
        Assert.Equal(FileTransferTransferState.Canceled, canceled!.State);
        Assert.Equal(FileTransferResultCodes.CanceledLocal, canceled.ErrorCode);
        Assert.Contains(receiverTransport.SentCancels, cancel => string.Equals(cancel.TransferId, transferId, StringComparison.Ordinal));
        Assert.Contains(receiverTransport.SentDataFrames, static frame => frame is FileTransferCancelFrameV6);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Canceled,
            timeoutMs: 6000);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, sender.Snapshot.Outbound!.ErrorCode);
    }

    [Fact]
    public async Task V6FallbackSender_PeerSessionEndTerminalizesWithoutPeerNotificationRace()
    {
        const string transferId = "transfer_v6_fallback_peer_session_end_sender";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 5_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_peer_session_end_sender");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_peer_session_end_sender");
        senderTransport.DataSessionSendDelayMs = 20;
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-peer-session-end-sender.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
            timeoutMs: 5000);

        var terminalized = await sender.TerminalizeActiveTransfersForPeerSessionEndAsync("remote_session_end", CancellationToken.None);
        sender.DetachTransport();

        Assert.Equal(1, terminalized);
        Assert.Equal(FileTransferTransferState.Canceled, sender.Snapshot.Outbound?.State);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, sender.Snapshot.Outbound?.ErrorCode);
        Assert.DoesNotContain(senderTransport.SentCancels, cancel => string.Equals(cancel.TransferId, transferId, StringComparison.Ordinal));
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_terminalized_by_peer_session_end; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_peer_disconnect_deferred_for_epoch; direction=outbound", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6FallbackReceiver_PeerSessionEndTerminalizesWithoutPeerNotificationRace()
    {
        const string transferId = "transfer_v6_fallback_peer_session_end_receiver";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 5_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_peer_session_end_receiver");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_peer_session_end_receiver");
        senderTransport.DataSessionSendDelayMs = 20;
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-peer-session-end-receiver.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Receiving &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
            timeoutMs: 5000);

        var terminalized = await receiver.TerminalizeActiveTransfersForPeerSessionEndAsync("remote_session_end", CancellationToken.None);
        receiver.DetachTransport();

        Assert.Equal(1, terminalized);
        Assert.Equal(FileTransferTransferState.Canceled, receiver.Snapshot.Inbound?.State);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, receiver.Snapshot.Inbound?.ErrorCode);
        Assert.DoesNotContain(receiverTransport.SentCancels, cancel => string.Equals(cancel.TransferId, transferId, StringComparison.Ordinal));
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_terminalized_by_peer_session_end; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_peer_disconnect_deferred_for_epoch; direction=inbound", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6FallbackSender_PeerDisconnectedTerminalizesWithoutV6Deferral()
    {
        const string transferId = "transfer_v6_fallback_peer_disconnected_sender";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 5_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_peer_disconnected_sender");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_peer_disconnected_sender");
        senderTransport.DataSessionSendDelayMs = 20;
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-peer-disconnected-sender.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
            timeoutMs: 5000);

        var terminalized = await sender.TerminalizeActiveTransfersForPeerDisconnectedAsync("transport_disconnected", CancellationToken.None);
        sender.DetachTransport();

        Assert.Equal(1, terminalized);
        Assert.Equal(FileTransferTransferState.Failed, sender.Snapshot.Outbound?.State);
        Assert.Equal(FileTransferResultCodes.PeerDisconnected, sender.Snapshot.Outbound?.ErrorCode);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_terminalized_by_peer_disconnect; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_peer_disconnect_deferred_for_epoch; direction=outbound", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6FallbackReceiver_RedundantPeerDisconnectedErrorDataFrameTerminalizesImmediately()
    {
        const string transferId = "transfer_v6_fallback_peer_disconnected_data_error";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 5_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_peer_disconnected_data_error");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_peer_disconnected_data_error");
        senderTransport.DataSessionSendDelayMs = 20;
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-peer-disconnected-data-error.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Receiving &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
            timeoutMs: 5000);

        var sessionId = sender.Snapshot.Outbound!.SessionId;
        using var dataSession = await senderTransport.OpenFileTransferDataSessionAsync(sessionId, transferId, CancellationToken.None);
        await dataSession.SendAsync(
            new FileTransferErrorFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                ErrorCode = FileTransferResultCodes.PeerDisconnected,
                Message = "Peer disconnected.",
                TransportEpoch = 1,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound?.State == FileTransferTransferState.Failed,
            timeoutMs: 5000);

        Assert.Equal(FileTransferResultCodes.PeerDisconnected, receiver.Snapshot.Inbound?.ErrorCode);
        Assert.Contains("Peer disconnected", receiver.Snapshot.Inbound?.StatusMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_lifecycle_priority_received; kind=error", logTail, StringComparison.Ordinal);
        Assert.Contains("path=redundant_data_frame", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletedOutboundTransfer_PeerDisconnectTerminalizationKeepsTerminalComplete()
    {
        const string transferId = "transfer_terminal_wins_completed_outbound";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_terminal_wins_completed_outbound");
        using var receiverTransport = new LoopbackFileTransferTransport("session_terminal_wins_completed_outbound");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await RunCompletedLoopbackTransferAsync(
            sender,
            receiver,
            transferId,
            "terminal-wins-completed-outbound.bin");

        var terminalized = await sender.TerminalizeActiveTransfersForPeerDisconnectedAsync(
            "session_liveness_timeout",
            CancellationToken.None);

        Assert.Equal(0, terminalized);
        Assert.Equal(FileTransferTransferState.Completed, sender.Snapshot.Outbound?.State);
        Assert.Null(sender.Snapshot.Outbound?.ErrorCode);
        Assert.Equal(sender.Snapshot.Outbound?.FileSizeBytes, sender.Snapshot.Outbound?.BytesTransferred);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_terminal_teardown_skipped_terminal_wins; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("attempted_error_code=peer_disconnected", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_terminalized_by_peer_disconnect; direction=outbound", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletedInboundTransfer_PeerDisconnectTerminalizationKeepsTerminalComplete()
    {
        const string transferId = "transfer_terminal_wins_completed_inbound";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport("session_terminal_wins_completed_inbound");
        using var receiverTransport = new LoopbackFileTransferTransport("session_terminal_wins_completed_inbound");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await RunCompletedLoopbackTransferAsync(
            sender,
            receiver,
            transferId,
            "terminal-wins-completed-inbound.bin");

        var terminalized = await receiver.TerminalizeActiveTransfersForPeerDisconnectedAsync(
            "session_liveness_timeout",
            CancellationToken.None);

        Assert.Equal(0, terminalized);
        Assert.Equal(FileTransferTransferState.Completed, receiver.Snapshot.Inbound?.State);
        Assert.Null(receiver.Snapshot.Inbound?.ErrorCode);
        Assert.Equal(receiver.Snapshot.Inbound?.FileSizeBytes, receiver.Snapshot.Inbound?.BytesTransferred);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_terminal_teardown_skipped_terminal_wins; direction=inbound", logTail, StringComparison.Ordinal);
        Assert.Contains("attempted_error_code=peer_disconnected", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_terminalized_by_peer_disconnect; direction=inbound", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionEndCancelTerminalWinsOverLaterPeerDisconnect()
    {
        const string transferId = "transfer_terminal_wins_session_end_cancel";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 128_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_terminal_wins_session_end_cancel");
        using var receiverTransport = new LoopbackFileTransferTransport("session_terminal_wins_session_end_cancel");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("terminal-wins-session-end-cancel.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound is { } inbound &&
                  inbound.TransferId == transferId &&
                  inbound.State == FileTransferTransferState.PendingDecision,
            timeoutMs: 5000);

        var canceledCount = await receiver.CancelActiveTransfersForSessionEndAsync(
            "session_end",
            CancellationToken.None);

        Assert.Equal(1, canceledCount);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound is { } outbound &&
                  outbound.TransferId == transferId &&
                  outbound.State == FileTransferTransferState.Canceled &&
                  outbound.ErrorCode == FileTransferResultCodes.CanceledRemote,
            timeoutMs: 6000);

        var terminalized = await sender.TerminalizeActiveTransfersForPeerDisconnectedAsync(
            "session_liveness_timeout",
            CancellationToken.None);

        Assert.Equal(0, terminalized);
        Assert.Equal(FileTransferTransferState.Canceled, sender.Snapshot.Outbound?.State);
        Assert.Equal(FileTransferResultCodes.CanceledRemote, sender.Snapshot.Outbound?.ErrorCode);
        Assert.Equal("session_end", sender.Snapshot.Outbound?.StatusMessage);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_terminal_teardown_skipped_terminal_wins; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("current_error_code=canceled_remote", logTail, StringComparison.Ordinal);
        Assert.Contains("attempted_error_code=peer_disconnected", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_terminalized_by_peer_disconnect; direction=outbound", logTail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletedTransfer_StaleUserPauseResumeCancelAreRejected()
    {
        const string transferId = "transfer_terminal_wins_stale_user_actions";
        using var senderTransport = new LoopbackFileTransferTransport("session_terminal_wins_stale_user_actions");
        using var receiverTransport = new LoopbackFileTransferTransport("session_terminal_wins_stale_user_actions");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);

        await RunCompletedLoopbackTransferAsync(
            sender,
            receiver,
            transferId,
            "terminal-wins-stale-user-actions.bin");

        var logStart = GetOperationalLogLength();
        Assert.Null(await sender.PauseTransferAsync(transferId, "late_pause", CancellationToken.None));
        Assert.Null(await sender.ResumeTransferAsync(transferId, "late_resume", CancellationToken.None));
        Assert.Null(await sender.CancelTransferAsync(transferId, "late_cancel", CancellationToken.None));

        Assert.Equal(FileTransferTransferState.Completed, sender.Snapshot.Outbound?.State);
        Assert.Null(sender.Snapshot.Outbound?.ErrorCode);
        Assert.Equal(sender.Snapshot.Outbound?.FileSizeBytes, sender.Snapshot.Outbound?.BytesTransferred);
        var logTail = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_terminal_stale_update_rejected; direction=outbound", logTail, StringComparison.Ordinal);
        Assert.Contains("source=user_pause", logTail, StringComparison.Ordinal);
        Assert.Contains("source=user_resume", logTail, StringComparison.Ordinal);
        Assert.Contains("source=user_cancel", logTail, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_user_paused", logTail, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_PeerSilence_TerminalsInsteadOfSendingForever()
    {
        const string transferId = "transfer_v4_sender_peer_silence";
        var previousTimeout = SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests;
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = TimeSpan.FromMilliseconds(300);
        SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = TimeSpan.FromMilliseconds(50);
        SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromMilliseconds(300);
        try
        {
            var payload = Enumerable.Range(0, 5_000_000).Select(static index => (byte)(index % 251)).ToArray();
            var dropPeerLiveness = 0;
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_peer_silence");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_peer_silence");
            senderTransport.DataSessionSendDelayMs = 100;
            receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
                Task.FromResult(Volatile.Read(ref dropPeerLiveness) == 1 && frame is FileTransferReceiverStateFrameV6);
            receiverTransport.OutboundHeartbeatDeliveryOverrideAsync = (_, _, _) =>
                Task.FromResult(Volatile.Read(ref dropPeerLiveness) == 1);
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            using var destination = new NonDisposingMemoryStream();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-sender-peer-silence.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                      senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
                timeoutMs: 5000);
            Volatile.Write(ref dropPeerLiveness, 1);
            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Failed, timeoutMs: 5000);

            Assert.Equal(FileTransferResultCodes.PeerDisconnected, sender.Snapshot.Outbound!.ErrorCode);
            Assert.Contains("Peer disconnected", sender.Snapshot.Outbound.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SessionFileTransferService.V4PeerSilenceTimeoutOverrideForTests = previousTimeout;
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = previousHeartbeatInterval;
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = previousPeerLivenessTimeout;
        }
    }

    [Fact(Skip = RetiredRegularNknV4ToV6RecoverySkip)]
    public async Task V4Sender_ControlReceiveStallExhausted_StartsV6RegularNknRecoveryWithoutSessionDisconnect()
    {
        const string transferId = "transfer_v4_sender_control_stall_exhausted";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 5_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_control_stall_exhausted");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_control_stall_exhausted");
        senderTransport.DataSessionSendDelayMs = 5;
        ConfigureDiagnosticRegularNknV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-control-stall-exhausted.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Sending &&
                  senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(),
            timeoutMs: 5000);

        senderTransport.SetLocalDataSessionsUnavailableForTests("control_receive_stalled_max_restarts");

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_control_channel_stalled_recovery; direction=outbound", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentTransportEpochs.Any(static epoch =>
                epoch.HandoffKind == "regular_nkn_recovery" &&
                epoch.TargetTransport == "regular_nkn"),
            timeoutMs: 5000);

        var outbound = sender.Snapshot.Outbound;
        Assert.NotNull(outbound);
        Assert.NotEqual(FileTransferTransferState.Failed, outbound!.State);
        Assert.NotEqual(FileTransferResultCodes.ControlChannelStalled, outbound.ErrorCode);
        Assert.DoesNotContain(senderTransport.SentErrors, static error => error.ErrorCode == FileTransferResultCodes.ControlChannelStalled);
        Assert.NotEqual(FileTransferTransferState.Failed, receiver.Snapshot.Inbound?.State);
    }

    [Fact]
    public async Task V4Sender_WithScreenshare_FailsCleanlyWhenMixedDisabledByEnvironment()
    {
        const string transferId = "transfer_v4_sender_mixed_disabled";
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "0");
        try
        {
            var payload = Enumerable.Range(0, 64_000).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_disabled");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_disabled");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            sender.SetSessionScreenShareActive(true);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-mixed-disabled.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
            var offer = senderTransport.SentOffers.Single();
            await receiverTransport.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                CancellationToken.None);

            await WaitUntilAsync(() => sender.Snapshot.Outbound?.State == FileTransferTransferState.Failed, timeoutMs: 5000);
            Assert.Equal(FileTransferResultCodes.TransportIncompatible, sender.Snapshot.Outbound?.ErrorCode);
            Assert.DoesNotContain(senderTransport.SentDataFrames, static frame => frame is FileTransferManifestFrameV6);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_WithScreenshareAndMixedFlag_CompletesWithTwoChunkNormalBatches()
    {
        const string transferId = "transfer_v4_sender_mixed_flag_on";
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "1");
        try
        {
            var logStart = ReadOperationalLogText().Length;
            var payload = Enumerable.Range(0, 192_000).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_flag_on");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_flag_on");
            senderTransport.DataSessionSendDelayMs = 25;
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            sender.SetSessionScreenShareActive(true);
            receiver.SetSessionScreenShareActive(true);
            using var destination = new NonDisposingMemoryStream();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-mixed-flag-on.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.IsV4MixedScreenShareTransferActive &&
                      receiver.IsV4MixedScreenShareTransferActive,
                timeoutMs: 5000);

            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 15000);

            Assert.Equal(payload, destination.ToArray()[..payload.Length]);
            var normalBatches = senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Where(static batch => batch.BatchProfile != "v4_repair_21k")
                .ToArray();
            Assert.NotEmpty(normalBatches);
            Assert.All(normalBatches, static batch =>
            {
                Assert.InRange(batch.DataSegments.Count, 1, 2);
                Assert.Equal("v4_default_21k_2x", batch.BatchProfile);
            });
            Assert.Contains(normalBatches, static batch => batch.DataSegments.Count == 2);
            Assert.Contains(
                normalBatches,
                static batch => batch.DataSegments.Count == 2 &&
                    batch.DataSegments[0].Length == 21 * 1024 &&
                    batch.DataSegments[1].Length == 21 * 1024);

            var log = ReadOperationalLogTail(logStart);
            Assert.Contains($"event=filetransfer_v6_mixed_enabled; transfer_id={transferId}", log, StringComparison.Ordinal);
            Assert.Contains("normal_batch_segments=2", log, StringComparison.Ordinal);
            Assert.Contains("mixed_screenshare=1", log, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_WithMixedFlag_LatchesMixedPacingAfterScreenshareWasObserved()
    {
        const string transferId = "transfer_v4_sender_mixed_latched";
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "1");
        try
        {
            var logStart = ReadOperationalLogText().Length;
            var payload = Enumerable.Range(0, 128_000).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_latched");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_latched");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            sender.SetSessionScreenShareActive(true);
            receiver.SetSessionScreenShareActive(true);
            sender.SetSessionScreenShareActive(false);
            receiver.SetSessionScreenShareActive(false);
            using var destination = new NonDisposingMemoryStream();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-mixed-latched.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 15000);

            Assert.Equal(payload, destination.ToArray()[..payload.Length]);
            var normalBatches = senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Where(static batch => batch.BatchProfile != "v4_repair_21k")
                .ToArray();
            Assert.NotEmpty(normalBatches);
            Assert.All(normalBatches, static batch =>
            {
                Assert.InRange(batch.DataSegments.Count, 1, 2);
                Assert.Equal("v4_default_21k_2x", batch.BatchProfile);
            });
            Assert.Contains(normalBatches, static batch => batch.DataSegments.Count == 2);

            var log = ReadOperationalLogTail(logStart);
            Assert.Contains($"event=filetransfer_v6_mixed_enabled; transfer_id={transferId}", log, StringComparison.Ordinal);
            Assert.Contains("screen_share_active=0", log, StringComparison.Ordinal);
            Assert.Contains("screen_share_observed=1", log, StringComparison.Ordinal);
            Assert.Contains("normal_batch_segments=2", log, StringComparison.Ordinal);
            Assert.Contains("mixed_screenshare=1", log, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_WithDegradedScreenshareAndMixedFlag_UsesTwoChunkNormalBatches()
    {
        const string transferId = "transfer_v4_sender_mixed_degraded";
        const string envName = "NLINK_FILETRANSFER_V4_MIXED_SCREENSHARE";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "1");
        try
        {
            var logStart = ReadOperationalLogText().Length;
            var payload = Enumerable.Range(0, 128_000).Select(static index => (byte)(index % 251)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_degraded");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_mixed_degraded");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            sender.SetSessionScreenShareActive(true);
            receiver.SetSessionScreenShareActive(true);
            sender.SetSessionScreenShareDegraded(true);
            receiver.SetSessionScreenShareDegraded(true);
            using var destination = new NonDisposingMemoryStream();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-mixed-degraded.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 15000);

            Assert.Equal(payload, destination.ToArray()[..payload.Length]);
            var normalBatches = senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Where(static batch => batch.BatchProfile != "v4_repair_21k")
                .ToArray();
            Assert.NotEmpty(normalBatches);
            Assert.All(normalBatches, static batch =>
            {
                Assert.InRange(batch.DataSegments.Count, 1, 2);
                Assert.Equal("v4_default_21k_2x", batch.BatchProfile);
            });
            Assert.Contains(normalBatches, static batch => batch.DataSegments.Count == 2);

            var log = ReadOperationalLogTail(logStart);
            Assert.Contains($"event=filetransfer_v6_mixed_enabled; transfer_id={transferId}", log, StringComparison.Ordinal);
            Assert.Contains("screen_share_degraded=1", log, StringComparison.Ordinal);
            Assert.Contains("normal_batch_segments=2", log, StringComparison.Ordinal);
            Assert.Contains("mixed_screenshare=1", log, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_MissingRangesSchedulePriorityRepair_AndComplete()
    {
        const string transferId = "transfer_v4_sender_repair_priority";
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 239)).ToArray();
        var droppedFirstBatch = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_priority");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_priority");
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (target, frame, _) =>
        {
            if (frame is FileTransferChunkBatchFrameV6 { StartChunkIndex: 0 } &&
                Interlocked.Exchange(ref droppedFirstBatch, 1) == 0)
            {
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-repair-priority.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(1, droppedFirstBatch);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        Assert.True(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count(static batch => batch.StartChunkIndex == 0) >= 2);
        Assert.Contains(
            senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>(),
            static batch => batch.StartChunkIndex == 0 &&
                batch.BatchProfile == "v4_repair_21k" &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.BulkOnly);
        await WaitUntilAsync(
            () => LocalOperationalLog.GetRecentLogText().Contains($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", StringComparison.Ordinal),
            timeoutMs: 5000);
        var log = LocalOperationalLog.GetRecentLogText();
        Assert.Contains($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_v4_repair_sent; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_v4_chunk_batch_sent; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_mode=bulk_only", log, StringComparison.Ordinal);
        Assert.Contains("repair_request_key=", log, StringComparison.Ordinal);
        Assert.Contains("repair_send=1", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V4Sender_TransportRebind_QueuesBoundedSafetyReplayFromPeerFrontier()
    {
        const string transferId = "transfer_v4_sender_transport_rebind_replay";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_transport_rebind_replay");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_transport_rebind_replay");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-transport-rebind-replay.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count(static batch => batch.StartChunkIndex == 0) == 1,
            timeoutMs: 5000);

        var batchCountBeforeRebind = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        senderTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_transport_rebind_safety_replay_started;",
                StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforeRebind)
                .Any(static batch =>
                    batch.StartChunkIndex == 0 &&
                    batch.BatchProfile == "v4_repair_21k" &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_transport_rebind_generation_started; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_safety_replay_started;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_tail_blocked_until_frontier_proof; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_mode=control_bulk_escalated", log, StringComparison.Ordinal);
        Assert.Contains("requested_chunk_count=1", log, StringComparison.Ordinal);
        Assert.Contains("replay_chunk_cap=64", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileTunaV4_LiveSwitchOffRecovery_PromotesSameTransferToPostTunaFallbackV6()
    {
        const string transferId = "transfer_file_tuna_v4_live_switch_off_replay";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 768_000).Select(static index => (byte)(index % 229)).ToArray();
        var allowFrontierChunk = 0;
        var droppedFrontierBatchCount = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_file_tuna_v4_live_switch_off_replay");
        using var receiverTransport = new LoopbackFileTransferTransport("session_file_tuna_v4_live_switch_off_replay");
        ConfigureFileTunaV4RouteForTest(senderTransport, receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferChunkBatchFrameV4 { StartChunkIndex: 0 } &&
                Volatile.Read(ref allowFrontierChunk) == 0)
            {
                Interlocked.Increment(ref droppedFrontierBatchCount);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("file-tuna-v4-live-switch-off-replay.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static state =>
                state.MissingRanges.Any(static range =>
                    range.StartChunkIndex == 0 &&
                    range.ChunkCount > 0)),
            timeoutMs: 10000);
        Assert.True(Volatile.Read(ref droppedFrontierBatchCount) > 0);

        senderTransport.SetConnectedDataSessionsUnavailableForTests(
            "header_switch_off",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        var postTunaFallbackV6Observed = false;
        await WaitUntilAsync(
            () =>
            {
                postTunaFallbackV6Observed = postTunaFallbackV6Observed ||
                    ReadOperationalLogTail(logStart).Contains(
                        "route=post_tuna_fallback_v6; protocol_version=6;",
                        StringComparison.Ordinal);
                return postTunaFallbackV6Observed;
            },
            timeoutMs: 5000);
        Volatile.Write(ref allowFrontierChunk, 1);
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var log = ReadOperationalLogTail(logStart);
        Assert.True(postTunaFallbackV6Observed);
        Assert.Single(senderTransport.SentOffers.Where(offer => offer.TransferId == transferId));
        Assert.Single(senderTransport.SentSessionOpens.Where(open => open.TransferId == transferId));
        Assert.Empty(senderTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
        Assert.Empty(receiverTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
        Assert.Contains("event=filetransfer_v6_epoch_started; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_epoch_started; direction=inbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_fallback_checkpoint_requested; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_fallback_checkpoint_accepted; direction=outbound;", log, StringComparison.Ordinal);
        var postTunaCheckpointLine = log.Split(Environment.NewLine)
            .FirstOrDefault(static line =>
                line.Contains("event=filetransfer_fallback_checkpoint_requested; direction=outbound;", StringComparison.Ordinal));
        Assert.NotNull(postTunaCheckpointLine);
        Assert.Contains("route=post_tuna_fallback_v6", postTunaCheckpointLine, StringComparison.Ordinal);
        Assert.Contains("protocol_version=6", postTunaCheckpointLine, StringComparison.Ordinal);
        Assert.DoesNotContain("post_tuna_v6_frontier_sweep=1", log, StringComparison.Ordinal);
        Assert.Contains("handoff_kind=tuna_to_normal_fallback", log, StringComparison.Ordinal);
        Assert.Contains("route=post_tuna_fallback_v6", log, StringComparison.Ordinal);
        Assert.True(
            senderTransport.SentDataFrames.Where(frame => frame.TransferId == transferId).Any(FileTransferProtocol.IsV6DataFrame),
            "Expected same-transfer fallback V6 sender frames.");
        Assert.True(
            receiverTransport.SentDataFrames.Where(frame => frame.TransferId == transferId).Any(FileTransferProtocol.IsV6DataFrame),
            "Expected same-transfer fallback V6 receiver frames.");
        Assert.Contains(
            receiverTransport.SentDataFrames.Where(frame => frame.TransferId == transferId),
            static frame => frame is FileTransferReceiverStateFrameV6);
        Assert.DoesNotContain("event=filetransfer_v6_sender_started;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_receiver_started;", log, StringComparison.Ordinal);
    }

    [Fact]
    public void FileTunaV4_LiveSwitchOffRecovery_AppliesFallbackCheckpointBelowOldV4StateEpoch()
    {
        const string transferId = "transfer_file_tuna_v4_live_switch_off_epoch_reset";
        var logStart = ReadOperationalLogText().Length;
        using var sender = new SessionFileTransferService();
        var context = CreatePostTunaFallbackV6OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 256,
            chunkCount: 512);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sender, context);
        var routeSelection = GetPrivateProperty<FileTransferRouteSelection>(context, "RouteSelection");
        var liveRouteEpoch = typeof(SessionFileTransferService)
            .GetMethod("StartLiveRouteEpoch", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(
                null,
                [
                    3,
                    routeSelection,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    FileTransferTransportKind.RegularNkn,
                    "header_switch_off",
                ]);
        SetPrivateProperty(context, "LastLiveRouteEpochId", 4);
        SetPrivateProperty(context, "CurrentLiveRouteEpoch", liveRouteEpoch);
        typeof(SessionFileTransferService)
            .GetMethod("StartOutboundPostTunaRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "header_switch_off"]);

        SetPrivateProperty(context, "V4LastStateEpoch", 625);
        typeof(SessionFileTransferService)
            .GetMethod("ApplyOutboundV4State", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                sender,
                [
                    context,
            new FileTransferReceiverStateFrameV6
            {
                SessionId = $"session_{transferId}",
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 100,
                DurableReceivedHighestChunkIndex = 99,
                CreditUntilChunkIndexExclusive = 128,
                MissingRanges = [],
                BytesCommitted = 100 * 21 * 1024,
                TransportEpoch = 3,
                RecoveryMode = "fallback_leg_checkpoint",
            }
                ]);

        var log = ReadOperationalLogTail(logStart);
        Assert.Equal(1, GetPrivateProperty<int>(context, "V4LastStateEpoch"));
        Assert.Equal(100, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
        Assert.Equal(128, GetPrivateProperty<int>(context, "RemoteGrantedUntilExclusive"));
        Assert.Contains("event=filetransfer_fallback_checkpoint_accepted; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_live_route_epoch_recovered; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("live_route_epoch=4; route=post_tuna_fallback_v6; protocol_version=6;", log, StringComparison.Ordinal);
        Assert.Contains("reason=fallback_checkpoint_accepted", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_fallback_state_epoch_floor_reset; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v4_state_received; transfer_id=[redacted]; session_id=[redacted]; epoch=1; previous_epoch=0;", log, StringComparison.Ordinal);
        Assert.Contains("applied=1; stale=0; duplicate=0; repair_request_id=(none); priority=(none); recovery_mode=fallback_leg_checkpoint", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileTunaV4_LiveSwitchOffRecovery_CanReenableTunaForSameTransfer()
    {
        const string transferId = "transfer_file_tuna_v4_live_switch_off_reenable";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 1_572_864).Select(static index => (byte)(index % 241)).ToArray();
        var allowFrontierChunk = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_file_tuna_v4_live_switch_off_reenable");
        using var receiverTransport = new LoopbackFileTransferTransport("session_file_tuna_v4_live_switch_off_reenable");
        ConfigureFileTunaV4RouteForTest(senderTransport, receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame.TransferId == transferId &&
                frame is FileTransferTransportProbeFrameV6 probe)
            {
                receiverTransport.NextDataFrameTransportKind = string.Equals(probe.TargetTransport, "tuna", StringComparison.OrdinalIgnoreCase)
                    ? FileTransferTransportKind.Tuna
                    : FileTransferTransportKind.RegularNkn;
            }

            if (frame is FileTransferChunkBatchFrameV4 { StartChunkIndex: 0 } &&
                Volatile.Read(ref allowFrontierChunk) == 0)
            {
                return true;
            }

            if (frame.TransferId == transferId)
            {
                await Task.Delay(2, ct);
            }

            return false;
        };
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame.TransferId == transferId &&
                frame is FileTransferTransportProbeFrameV6 probe)
            {
                senderTransport.NextDataFrameTransportKind = string.Equals(probe.TargetTransport, "tuna", StringComparison.OrdinalIgnoreCase)
                    ? FileTransferTransportKind.Tuna
                    : FileTransferTransportKind.RegularNkn;
            }

            return Task.FromResult(false);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("file-tuna-v4-live-switch-off-reenable.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static state =>
                state.MissingRanges.Any(static range =>
                    range.StartChunkIndex == 0 &&
                    range.ChunkCount > 0)),
            timeoutMs: 10000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests(
            "header_switch_off",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "route=post_tuna_fallback_v6; protocol_version=6;",
                StringComparison.Ordinal),
            timeoutMs: 5000);
        Volatile.Write(ref allowFrontierChunk, 1);
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_live_route_epoch_recovered; direction=outbound",
                StringComparison.Ordinal),
            timeoutMs: 10000);

        senderTransport.IsFileTunaActiveForRouteSelection = true;
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = false;
        receiverTransport.IsFileTunaActiveForRouteSelection = true;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = false;
        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_reenabled",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        receiverTransport.RequestAllDataSessionHandoffs(
            "tuna_reenabled",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "previous_route=post_tuna_fallback_v6; new_route=file_tuna_v4",
                StringComparison.Ordinal),
            timeoutMs: 10000);

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 25000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var log = ReadV4SenderLogSnapshot(logStart);
        Assert.Single(senderTransport.SentOffers.Where(offer => offer.TransferId == transferId));
        Assert.Single(senderTransport.SentSessionOpens.Where(open => open.TransferId == transferId));
        Assert.Empty(senderTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
        Assert.Empty(receiverTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
        Assert.Contains("event=filetransfer_live_route_epoch_started; direction=outbound", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_live_route_epoch_started; direction=inbound", log, StringComparison.Ordinal);
        Assert.Contains("route=post_tuna_fallback_v6", log, StringComparison.Ordinal);
        Assert.Contains("route=file_tuna_v4", log, StringComparison.Ordinal);
        Assert.Contains("handoff_kind=normal_to_tuna_activation", log, StringComparison.Ordinal);
        Assert.Contains("target_transport=tuna", log, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "handoff_kind=normal_to_tuna_activation; source_transport=regular_nkn; target_transport=tuna; reason=tuna_reenabled; state=target_proof_pending",
            log,
            StringComparison.Ordinal);
        Assert.Contains("live_route_epoch=", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_runtime_started; direction=outbound; role=sender;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_runtime_started; direction=inbound; role=receiver;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_live_route_epoch_terminal; direction=outbound", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_live_route_epoch_terminal; direction=inbound", log, StringComparison.Ordinal);

        const string activeTunaNextTransferId = "transfer_file_tuna_v4_after_live_reenable_active";
        await RunCompletedLoopbackTransferAsync(
            sender,
            receiver,
            activeTunaNextTransferId,
            "file-tuna-v4-after-live-reenable-active.bin");
        var activeTunaNextOffer = senderTransport.SentOffers.Single(offer => offer.TransferId == activeTunaNextTransferId);
        Assert.Equal(FileTransferRouteResolver.FileTunaV4Token, activeTunaNextOffer.FileTransferRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, activeTunaNextOffer.PreferredDataProtocolVersion);

        senderTransport.IsFileTunaActiveForRouteSelection = false;
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = false;
        receiverTransport.IsFileTunaActiveForRouteSelection = false;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = false;
        const string inactiveNextTransferId = "transfer_file_tuna_v4_after_live_reenable_inactive";
        await RunCompletedLoopbackTransferAsync(
            sender,
            receiver,
            inactiveNextTransferId,
            "file-tuna-v4-after-live-reenable-inactive.bin");
        var inactiveNextOffer = senderTransport.SentOffers.Single(offer => offer.TransferId == inactiveNextTransferId);
        Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, inactiveNextOffer.FileTransferRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, inactiveNextOffer.PreferredDataProtocolVersion);
    }

    [Fact]
    public async Task FileTunaV4_LiveSwitchOffRecovery_CanCycleTunaOffOnOffForSameTransfer()
    {
        const string transferId = "transfer_file_tuna_v4_live_cycle_off_on_off";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 1_572_864).Select(static index => (byte)(index % 239)).ToArray();
        var allowFrontierChunk = 0;
        var blockPostReenableV4Chunks = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_file_tuna_v4_live_cycle_off_on_off");
        using var receiverTransport = new LoopbackFileTransferTransport("session_file_tuna_v4_live_cycle_off_on_off");
        ConfigureFileTunaV4RouteForTest(senderTransport, receiverTransport);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame.TransferId == transferId &&
                frame is FileTransferTransportProbeFrameV6 probe)
            {
                receiverTransport.NextDataFrameTransportKind = string.Equals(probe.TargetTransport, "tuna", StringComparison.OrdinalIgnoreCase)
                    ? FileTransferTransportKind.Tuna
                    : FileTransferTransportKind.RegularNkn;
            }

            if (frame is FileTransferChunkBatchFrameV4 { StartChunkIndex: 0 } &&
                Volatile.Read(ref allowFrontierChunk) == 0)
            {
                return true;
            }

            if (frame is FileTransferChunkBatchFrameV4 &&
                Volatile.Read(ref blockPostReenableV4Chunks) == 1)
            {
                return true;
            }

            if (frame.TransferId == transferId)
            {
                await Task.Delay(2, ct);
            }

            return false;
        };
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame.TransferId == transferId &&
                frame is FileTransferTransportProbeFrameV6 probe)
            {
                senderTransport.NextDataFrameTransportKind = string.Equals(probe.TargetTransport, "tuna", StringComparison.OrdinalIgnoreCase)
                    ? FileTransferTransportKind.Tuna
                    : FileTransferTransportKind.RegularNkn;
            }

            return Task.FromResult(false);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("file-tuna-v4-live-cycle-off-on-off.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static state =>
                state.MissingRanges.Any(static range =>
                    range.StartChunkIndex == 0 &&
                    range.ChunkCount > 0)),
            timeoutMs: 10000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests(
            "header_switch_off_first",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "live_route_epoch=1; route=post_tuna_fallback_v6",
                StringComparison.Ordinal),
            timeoutMs: 5000);
        Volatile.Write(ref allowFrontierChunk, 1);
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_live_route_epoch_recovered; direction=outbound",
                StringComparison.Ordinal),
            timeoutMs: 10000);

        Volatile.Write(ref blockPostReenableV4Chunks, 1);
        senderTransport.IsFileTunaActiveForRouteSelection = true;
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = false;
        receiverTransport.IsFileTunaActiveForRouteSelection = true;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = false;
        senderTransport.RequestAllDataSessionHandoffs(
            "tuna_reenabled",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        receiverTransport.RequestAllDataSessionHandoffs(
            "tuna_reenabled",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "previous_route=post_tuna_fallback_v6; new_route=file_tuna_v4",
                StringComparison.Ordinal),
            timeoutMs: 10000);
        await WaitUntilAsync(
            () =>
            {
                var log = ReadOperationalLogTail(logStart);
                return log.Contains("event=filetransfer_live_route_epoch_recovered; direction=outbound", StringComparison.Ordinal) &&
                       log.Contains("live_route_epoch=2; route=file_tuna_v4", StringComparison.Ordinal);
            },
            timeoutMs: 10000);

        senderTransport.IsFileTunaActiveForRouteSelection = false;
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        receiverTransport.IsFileTunaActiveForRouteSelection = false;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        receiverTransport.SetConnectedDataSessionsUnavailableForTests(
            "helper_switch_off_second",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "live_route_epoch=3; previous_route=file_tuna_v4; route=post_tuna_fallback_v6",
                StringComparison.Ordinal),
            timeoutMs: 10000);
        Volatile.Write(ref blockPostReenableV4Chunks, 0);
        receiverTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 30000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var log = ReadV4SenderLogSnapshot(logStart);
        Assert.Single(senderTransport.SentOffers.Where(offer => offer.TransferId == transferId));
        Assert.Single(senderTransport.SentSessionOpens.Where(open => open.TransferId == transferId));
        Assert.Empty(senderTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
        Assert.Empty(receiverTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
        Assert.Contains("live_route_epoch=1; route=post_tuna_fallback_v6", log, StringComparison.Ordinal);
        Assert.Contains("live_route_epoch=2; route=file_tuna_v4", log, StringComparison.Ordinal);
        Assert.Contains("live_route_epoch=3; previous_route=file_tuna_v4; route=post_tuna_fallback_v6", log, StringComparison.Ordinal);
        var finalFallbackRecoveryStartedLines = log
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line =>
                line.Contains("event=filetransfer_post_tuna_recovery_started;", StringComparison.Ordinal) &&
                line.Contains("reason=helper_switch_off_second", StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(finalFallbackRecoveryStartedLines, line => line.Contains("direction=outbound", StringComparison.Ordinal));
        Assert.Contains(finalFallbackRecoveryStartedLines, line => line.Contains("direction=inbound", StringComparison.Ordinal));
        var fallbackInboundTransportEpochStartedLines = log
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line =>
                line.Contains("event=filetransfer_v6_epoch_started; direction=inbound", StringComparison.Ordinal) &&
                line.Contains("handoff_kind=tuna_to_normal_fallback", StringComparison.Ordinal) &&
                line.Contains("target_transport=regular_nkn", StringComparison.Ordinal))
            .ToArray();
        Assert.True(
            fallbackInboundTransportEpochStartedLines.Length >= 2,
            "Repeated off/on/off must start a fresh inbound V6 transport epoch for the final post-Tuna fallback. Lines: " +
            string.Join(Environment.NewLine, fallbackInboundTransportEpochStartedLines));
        Assert.Contains(
            fallbackInboundTransportEpochStartedLines,
            line => line.Contains("transport_epoch=2", StringComparison.Ordinal) &&
                    line.Contains("reason=helper_switch_off_second", StringComparison.Ordinal));
        Assert.Contains(
            log.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries),
            line => line.Contains("event=filetransfer_v6_epoch_recovered_restart_allowed; direction=inbound", StringComparison.Ordinal) &&
                    line.Contains("allowance=new_post_tuna_live_route_epoch", StringComparison.Ordinal) &&
                    line.Contains("reason=helper_switch_off_second", StringComparison.Ordinal));
        var recoveredLiveRouteEpoch3Lines = log
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line =>
                line.Contains("event=filetransfer_live_route_epoch_recovered", StringComparison.Ordinal) &&
                line.Contains("live_route_epoch=3", StringComparison.Ordinal) &&
                line.Contains("route=post_tuna_fallback_v6", StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(recoveredLiveRouteEpoch3Lines, line => line.Contains("direction=outbound", StringComparison.Ordinal));
        Assert.Contains(recoveredLiveRouteEpoch3Lines, line => line.Contains("direction=inbound", StringComparison.Ordinal));
        Assert.Contains("handoff_kind=normal_to_tuna_activation", log, StringComparison.Ordinal);
        Assert.Contains("target_transport=tuna", log, StringComparison.Ordinal);
        Assert.Contains("target_transport=regular_nkn", log, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "handoff_kind=normal_to_tuna_activation; source_transport=regular_nkn; target_transport=tuna; reason=tuna_reenabled; state=target_proof_pending",
            log,
            StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_live_route_epoch_terminal; direction=outbound", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_live_route_epoch_terminal; direction=inbound", log, StringComparison.Ordinal);
        Assert.False(senderTransport.IsPostTunaFileFallbackActiveForRouteSelection);
        Assert.False(receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection);

        const string nextTransferId = "transfer_file_tuna_v4_after_live_cycle_inactive";
        await RunCompletedLoopbackTransferAsync(
            sender,
            receiver,
            nextTransferId,
            "file-tuna-v4-after-live-cycle-inactive.bin");
        var nextOffer = senderTransport.SentOffers.Single(offer => offer.TransferId == nextTransferId);
        Assert.Equal(FileTransferRouteResolver.RegularNknV4FastToken, nextOffer.FileTransferRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV4, nextOffer.PreferredDataProtocolVersion);
    }

    [Fact]
    public void PostTunaFallbackV6LiveSparseRecovery_BoundsAndInterruptsV4PumpSends()
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var contextType = serviceType.GetNestedType("OutboundTransferContext", BindingFlags.NonPublic)!;
        FileTransferReadStreamFactory openReadStreamAsync =
            _ => Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>(), writable: false));
        var descriptor = new FileTransferSendDescriptor(
            "post-tuna-fallback-v6-live-sparse-bound.bin",
            1024,
            "transfer_post_tuna_fallback_v6_live_sparse_bound");
        var context = Activator.CreateInstance(
            contextType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [descriptor, openReadStreamAsync],
            culture: null)!;
        var routeSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        SetPrivateProperty(context, "SessionId", "session_post_tuna_fallback_v6_live_sparse_bound");
        SetPrivateProperty(context, "RouteSelection", routeSelection);
        SetPrivateProperty(context, "RouteRuntime", routeSelection.RuntimeDescriptor);
        SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV6);
        SetPrivateProperty(context, "State", FileTransferTransferState.Sending);
        SetPrivateProperty(context, "ChunkCount", 64);
        SetPrivateProperty(context, "PullSessionActive", true);
        SetPrivateProperty(context, "PullPostTunaRecoveryActive", true);
        SetPrivateProperty(context, "PullTransportRebindGeneration", 3);
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var shouldBound = (bool)serviceType
            .GetMethod("ShouldBoundOutboundV4TransportSend", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context])!;
        var shouldInterrupt = (bool)serviceType
            .GetMethod("ShouldInterruptOutboundV4TransportSendOnPumpSignal", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context])!;

        Assert.True(shouldBound);
        Assert.True(shouldInterrupt);
    }

    [Fact]
    public void PostTunaFallbackV6PeerSilenceReplay_WrapsToProvenFrontierAfterSweepTail()
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var contextType = serviceType.GetNestedType("OutboundTransferContext", BindingFlags.NonPublic)!;
        FileTransferReadStreamFactory openReadStreamAsync =
            _ => Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>(), writable: false));
        const int chunkSize = 21 * 1024;
        var descriptor = new FileTransferSendDescriptor(
            "post-tuna-fallback-v6-live-sparse-wrap.bin",
            256L * chunkSize,
            "transfer_post_tuna_fallback_v6_live_sparse_wrap");
        var context = Activator.CreateInstance(
            contextType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [descriptor, openReadStreamAsync],
            culture: null)!;
        var routeSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        SetPrivateProperty(context, "SessionId", "session_post_tuna_fallback_v6_live_sparse_wrap");
        SetPrivateProperty(context, "RouteSelection", routeSelection);
        SetPrivateProperty(context, "RouteRuntime", routeSelection.RuntimeDescriptor);
        SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV6);
        SetPrivateProperty(context, "State", FileTransferTransferState.Sending);
        SetPrivateProperty(context, "ChunkSizeBytes", chunkSize);
        SetPrivateProperty(context, "ChunkCount", 256);
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullSessionActive", true);
        SetPrivateProperty(context, "PullPostTunaRecoveryActive", true);
        SetPrivateProperty(context, "PullTransportRebindGeneration", 3);
        SetPrivateProperty(context, "RemoteNextExpectedChunkIndex", 100);
        SetPrivateProperty(context, "RemoteGrantedUntilExclusive", 256);
        SetPrivateProperty(context, "ChunksAcceptedForTransport", 256);
        SetPrivateProperty(context, "PullTransportFrontierOnlyRepairActive", true);
        SetPrivateProperty(context, "PullTransportFrontierOnlyRepairStartChunkIndex", 100);
        SetPrivateProperty(context, "PullTransportLastSafetyReplayGeneration", 3);
        SetPrivateProperty(context, "PullTransportLastSafetyReplayFrontierChunkIndex", 100);
        SetPrivateProperty(context, "PullTransportLastSafetyReplayEndChunkIndex", 256);
        SetPrivateProperty(context, "PullTransportLastSafetyReplayUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var logStart = ReadOperationalLogText().Length;
        serviceType
            .GetMethod("QueueOutboundV4TransportRebindSafetyReplayLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "post_tuna_fallback_peer_silence", true]);

        var queue = (System.Collections.IEnumerable)contextType
            .GetProperty("PullV4SenderPumpRepairQueue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context)!;
        var queuedRepair = queue.Cast<object>().Single();
        var queuedRepairType = queuedRepair.GetType();
        var chunkIndices = ((IEnumerable<int>)queuedRepairType.GetProperty("ChunkIndices")!.GetValue(queuedRepair)!).ToArray();

        Assert.Equal(100, queuedRepairType.GetProperty("FirstStartChunkIndex")!.GetValue(queuedRepair));
        Assert.Equal(164, queuedRepairType.GetProperty("LastEndChunkExclusive")!.GetValue(queuedRepair));
        Assert.Equal(Enumerable.Range(100, 64), chunkIndices);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_transport_rebind_frontier_only_replay;", log, StringComparison.Ordinal);
        Assert.Contains("post_tuna_v6_frontier_sweep=1", log, StringComparison.Ordinal);
        Assert.Contains("post_tuna_v6_frontier_sweep_wrapped=1", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6EpochlessFrontierRequest_AdvancesStaleRemoteFrontierAndQueuesRepair()
    {
        using var service = new SessionFileTransferService();
        using var senderTransport = new LoopbackFileTransferTransport("session_post_tuna_fallback_v6_epochless_frontier");
        service.AttachTransport(senderTransport);
        var serviceType = typeof(SessionFileTransferService);
        var contextType = serviceType.GetNestedType("OutboundTransferContext", BindingFlags.NonPublic)!;
        FileTransferReadStreamFactory openReadStreamAsync =
            _ => Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>(), writable: false));
        const int chunkSize = 21 * 1024;
        var descriptor = new FileTransferSendDescriptor(
            "post-tuna-fallback-v6-epochless-frontier.bin",
            256L * chunkSize,
            "transfer_post_tuna_fallback_v6_epochless_frontier");
        var context = Activator.CreateInstance(
            contextType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [descriptor, openReadStreamAsync],
            culture: null)!;
        var routeSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        SetPrivateProperty(context, "SessionId", "session_post_tuna_fallback_v6_epochless_frontier");
        SetPrivateProperty(context, "RouteSelection", routeSelection);
        SetPrivateProperty(context, "RouteRuntime", routeSelection.RuntimeDescriptor);
        SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV6);
        SetPrivateProperty(context, "State", FileTransferTransferState.Sending);
        SetPrivateProperty(context, "ChunkSizeBytes", chunkSize);
        SetPrivateProperty(context, "ChunkCount", 256);
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullSessionActive", true);
        SetPrivateProperty(context, "PullPostTunaRecoveryActive", true);
        SetPrivateProperty(context, "PullTransportRebindGeneration", 3);
        SetPrivateProperty(context, "RemoteNextExpectedChunkIndex", 100);
        SetPrivateProperty(context, "RemoteGrantedUntilExclusive", 256);
        SetPrivateProperty(context, "ChunksAcceptedForTransport", 256);
        SetPrivateProperty(context, "PullTransportFrontierOnlyRepairActive", true);
        SetPrivateProperty(context, "PullTransportFrontierOnlyRepairStartChunkIndex", 100);
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = "session_post_tuna_fallback_v6_epochless_frontier",
            TransferId = "transfer_post_tuna_fallback_v6_epochless_frontier",
            TransportEpoch = 0,
            RepairRequestId = "v6-frontier:128:test",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 128,
                    ChunkCount = 32,
                },
            ],
            Priority = "frontier",
            RecoveryMode = "regular_nkn_frontier_stall_control_bulk",
        };
        var logStart = GetOperationalLogLength();
        serviceType
            .GetMethod("ApplyOutboundV6RepairRequest", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, request]);

        Assert.Equal(128, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));

        var queue = (System.Collections.IEnumerable)contextType
            .GetProperty("PullV4SenderPumpRepairQueue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context)!;
        var queuedRepair = queue.Cast<object>().Single();
        var queuedRepairType = queuedRepair.GetType();
        var chunkIndices = ((IEnumerable<int>)queuedRepairType.GetProperty("ChunkIndices")!.GetValue(queuedRepair)!).ToArray();

        Assert.Equal(128, queuedRepairType.GetProperty("FirstStartChunkIndex")!.GetValue(queuedRepair));
        Assert.Equal(160, queuedRepairType.GetProperty("LastEndChunkExclusive")!.GetValue(queuedRepair));
        Assert.Equal(Enumerable.Range(128, 32), chunkIndices);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_epochless_post_tuna_fallback_frontier_request_accepted;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_frontier_request_advanced_remote_frontier;", log, StringComparison.Ordinal);
        Assert.Contains("transport_epoch=0", log, StringComparison.Ordinal);
    }

    [Fact]
    public void EpochlessV6FrontierRequest_OutsidePostTunaFallbackIsIgnored()
    {
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport("session_epochless_frontier_regular_v4_ignored");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            "transfer_epochless_frontier_regular_v4_ignored",
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 512,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = "session_transfer_epochless_frontier_regular_v4_ignored",
            TransferId = "transfer_epochless_frontier_regular_v4_ignored",
            TransportEpoch = 0,
            RepairRequestId = "v6-frontier:ignored",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 128,
                    ChunkCount = 1,
                },
            ],
            Priority = "frontier",
            RecoveryMode = "regular_nkn_frontier_stall_control_bulk",
        };
        var logStart = GetOperationalLogLength();
        typeof(SessionFileTransferService)
            .GetMethod("ApplyOutboundV6RepairRequest", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, request]);

        Assert.Equal(100, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_recovery_frame_ignored;", log, StringComparison.Ordinal);
        Assert.Contains("reason=missing_transport_epoch", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTunaFallbackV6StateRefreshFailure_RequestsReceiveRecoveryAndFrontierReplay()
    {
        using var service = new SessionFileTransferService();
        using var senderTransport = new LoopbackFileTransferTransport("session_post_tuna_fallback_v6_state_refresh_recovery");
        service.AttachTransport(senderTransport);
        var serviceType = typeof(SessionFileTransferService);
        var contextType = serviceType.GetNestedType("OutboundTransferContext", BindingFlags.NonPublic)!;
        FileTransferReadStreamFactory openReadStreamAsync =
            _ => Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>(), writable: false));
        const int chunkSize = 21 * 1024;
        var descriptor = new FileTransferSendDescriptor(
            "post-tuna-fallback-v6-state-refresh-recovery.bin",
            256L * chunkSize,
            "transfer_post_tuna_fallback_v6_state_refresh_recovery");
        var context = Activator.CreateInstance(
            contextType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [descriptor, openReadStreamAsync],
            culture: null)!;
        var routeSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var bridgePolicyType = serviceType.GetNestedType("FileTransferBridgeRecoveryPolicy", BindingFlags.NonPublic)!;
        SetPrivateProperty(context, "SessionId", "session_post_tuna_fallback_v6_state_refresh_recovery");
        SetPrivateProperty(context, "RouteSelection", routeSelection);
        SetPrivateProperty(context, "RouteRuntime", routeSelection.RuntimeDescriptor);
        SetPrivateProperty(context, "BridgeRecoveryPolicy", Enum.Parse(bridgePolicyType, "PostTunaFallbackStrictRecovery"));
        SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV6);
        SetPrivateProperty(context, "State", FileTransferTransferState.Sending);
        SetPrivateProperty(context, "ChunkSizeBytes", chunkSize);
        SetPrivateProperty(context, "ChunkCount", 256);
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullSessionActive", true);
        SetPrivateProperty(context, "PullPostTunaRecoveryActive", true);
        SetPrivateProperty(context, "PullTransportRebindGeneration", 3);
        SetPrivateProperty(context, "RemoteNextExpectedChunkIndex", 100);
        SetPrivateProperty(context, "RemoteGrantedUntilExclusive", 256);
        SetPrivateProperty(context, "ChunksAcceptedForTransport", 256);
        SetPrivateProperty(context, "PullTransportFrontierOnlyRepairActive", true);
        SetPrivateProperty(context, "PullTransportFrontierOnlyRepairStartChunkIndex", 100);
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = "session_post_tuna_fallback_v6_state_refresh_recovery",
            TransferId = "transfer_post_tuna_fallback_v6_state_refresh_recovery",
            TransportEpoch = 3,
            RepairRequestId = "v6-regular-nkn-state-refresh:test-failed",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 100,
                    ChunkCount = 1,
                },
            ],
            Priority = "state_refresh",
            RecoveryMode = "regular_nkn_state_refresh",
        };
        var logStart = GetOperationalLogLength();
        serviceType
            .GetMethod("QueueOutboundV4SparseRuntimeStateRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, new ThrowingDataSession(request.SessionId, request.TransferId), request]);

        await WaitUntilAsync(
            () => senderTransport.ReceiveRecoveryRequests.Any(recoveryRequest =>
                recoveryRequest.Direction == FileTransferDirection.Outbound &&
                string.Equals(recoveryRequest.TransferId, request.TransferId, StringComparison.Ordinal) &&
                string.Equals(recoveryRequest.Reason, "post_tuna_fallback_state_refresh_failed", StringComparison.Ordinal)),
            timeoutMs: 5000);

        var queue = (System.Collections.IEnumerable)contextType
            .GetProperty("PullV4SenderPumpRepairQueue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context)!;
        var queuedRepair = queue.Cast<object>().Single();
        var queuedRepairType = queuedRepair.GetType();
        var chunkIndices = ((IEnumerable<int>)queuedRepairType.GetProperty("ChunkIndices")!.GetValue(queuedRepair)!).ToArray();

        Assert.Equal(100, queuedRepairType.GetProperty("FirstStartChunkIndex")!.GetValue(queuedRepair));
        Assert.Equal(164, queuedRepairType.GetProperty("LastEndChunkExclusive")!.GetValue(queuedRepair));
        Assert.Equal(Enumerable.Range(100, 64), chunkIndices);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_regular_nkn_state_refresh_send_failed;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_post_tuna_fallback_state_refresh_receive_recovery_requested;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", log, StringComparison.Ordinal);
        Assert.Contains("post_tuna_v6_frontier_sweep=1", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6StaleInflightRepair_ForcesStateRefreshAndClearsStalePipeline()
    {
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_stale_inflight_recovery",
            remoteNextExpectedChunkIndex: 13923,
            chunksAcceptedForTransport: 17003,
            remoteGrantedUntilExclusive: 17003,
            chunkCount: 18000);
        SetPrivateProperty(context, "V6RegularNknStateRefreshFailureCount", 3);
        SetPrivateProperty(context, "V6EpochLivenessDeferralCount", 12);
        SetPrivateProperty(context, "PullSenderPipelineCurrentInFlightFrames", 8);
        SetPrivateProperty(context, "PullSenderPipelineCurrentInFlightBytes", 512L * 1024);

        var logStart = GetOperationalLogLength();
        var args = new object?[]
        {
            context,
            TimeSpan.FromMinutes(3),
            null,
        };
        serviceType
            .GetMethod("TryPrepareOutboundV4SparseRuntimeReceiveRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, args);

        var request = Assert.IsType<FileTransferFrontierRequestFrameV6>(args[2]);
        Assert.Equal("state_refresh_stale_inflight", request.Priority);
        Assert.Equal("regular_nkn_state_refresh", request.RecoveryMode);
        Assert.Equal(13923, request.MissingRanges.Single().StartChunkIndex);
        Assert.Equal(0, GetPrivateProperty<int>(context, "PullSenderPipelineCurrentInFlightFrames"));
        Assert.Equal(0L, GetPrivateProperty<long>(context, "PullSenderPipelineCurrentInFlightBytes"));
        Assert.Equal(8, GetPrivateProperty<int>(context, "PullSenderPipelineFailedFramesRecent"));
        Assert.Equal(8, GetPrivateProperty<int>(context, "PullSenderPipelineFailedFramesTotal"));

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_post_tuna_fallback_stale_inflight_repair_recovery_forced;", log, StringComparison.Ordinal);
        Assert.Contains("stale_in_flight_frames=8", log, StringComparison.Ordinal);
        Assert.Contains("state_refresh_failure_count=3", log, StringComparison.Ordinal);
        Assert.Contains("reason=feedback_stalled_with_stale_inflight", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTunaFallbackV6StateRefreshRecoveryRequest_CarriesCurrentLegAuthority()
    {
        using var service = new SessionFileTransferService();
        using var senderTransport = new LoopbackFileTransferTransport("session_post_tuna_fallback_v6_authority_request");
        service.AttachTransport(senderTransport);
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_authority_request");
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        serviceType
            .GetMethod("StartOutboundPostTunaRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "test_authority_start"]);

        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = "session_transfer_post_tuna_fallback_v6_authority_request",
            TransferId = "transfer_post_tuna_fallback_v6_authority_request",
            TransportEpoch = 11,
            RepairRequestId = "v6-regular-nkn-state-refresh:authority",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 100,
                    ChunkCount = 1,
                },
            ],
            Priority = "state_refresh",
            RecoveryMode = "regular_nkn_state_refresh",
        };

        serviceType
            .GetMethod("QueueOutboundV4SparseRuntimeStateRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, new ThrowingDataSession(request.SessionId, request.TransferId), request]);

        await WaitUntilAsync(
            () => senderTransport.ReceiveRecoveryRequests.Any(recoveryRequest =>
                string.Equals(recoveryRequest.Reason, "post_tuna_fallback_state_refresh_failed", StringComparison.Ordinal)),
            timeoutMs: 5000);

        var recovery = Assert.Single(
            senderTransport.ReceiveRecoveryRequests,
            recoveryRequest => string.Equals(recoveryRequest.Reason, "post_tuna_fallback_state_refresh_failed", StringComparison.Ordinal));
        Assert.Equal("post_tuna_fallback_v6", recovery.RouteToken);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, recovery.ProtocolVersion);
        Assert.True(recovery.LiveRouteEpoch >= 0);
        Assert.True(recovery.TransferLegGeneration > 0);
        Assert.Equal(3, recovery.BridgeRecoveryGeneration);
        Assert.Equal(11, recovery.TransportEpoch);
        Assert.Equal("v6-regular-nkn-state-refresh:authority", recovery.CheckpointRequestId);
        Assert.Equal("post_tuna_fallback_state_refresh_failed", recovery.AuthorityReason);
    }

    [Fact]
    public void ReceiveRecoveryRequest_UsesActiveFallbackRouteWhenSnapshotRouteIsStale()
    {
        using var service = new SessionFileTransferService();
        using var senderTransport = new LoopbackFileTransferTransport("session_post_tuna_fallback_v6_stale_liveness_snapshot");
        service.AttachTransport(senderTransport);
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_stale_liveness_snapshot");
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        serviceType
            .GetMethod("StartOutboundPostTunaRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "test_liveness_recovery_authority_start"]);

        service.RequestFileTransferReceiveRecovery(
            new FileTransferReceiveRecoveryRequest(
                "session_transfer_post_tuna_fallback_v6_stale_liveness_snapshot",
                "transfer_post_tuna_fallback_v6_stale_liveness_snapshot",
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending")
            {
                RouteToken = FileTransferRouteResolver.FileTunaV4Token,
                ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                LiveRouteEpoch = 1,
            });

        var request = Assert.Single(senderTransport.ReceiveRecoveryRequests);
        Assert.Equal(FileTransferRouteResolver.PostTunaFallbackV6Token, request.RouteToken);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, request.ProtocolVersion);
        Assert.Equal("session_liveness_timeout_pending", request.Reason);
        Assert.True(request.TransferLegGeneration > 0);
        Assert.Equal(3, request.BridgeRecoveryGeneration);
        Assert.Equal("post_tuna_fallback_session_liveness_timeout_pending", request.AuthorityReason);
    }

    [Fact]
    public void PostTunaFallbackV6StaleCreditRepair_ClampsCreditAndForcesStateRefresh()
    {
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_stale_credit_recovery",
            remoteNextExpectedChunkIndex: 4563,
            chunksAcceptedForTransport: 5458,
            remoteGrantedUntilExclusive: 8490,
            chunkCount: 9000);
        SetPrivateProperty(context, "V6RegularNknStateRefreshFailureCount", 3);
        SetPrivateProperty(context, "V6EpochLivenessDeferralCount", 12);

        var logStart = GetOperationalLogLength();
        var args = new object?[]
        {
            context,
            TimeSpan.FromMinutes(3),
            null,
        };
        serviceType
            .GetMethod("TryPrepareOutboundV4SparseRuntimeReceiveRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, args);

        var request = Assert.IsType<FileTransferFrontierRequestFrameV6>(args[2]);
        Assert.Equal("state_refresh_stale_credit", request.Priority);
        Assert.Equal("regular_nkn_state_refresh", request.RecoveryMode);
        Assert.Equal(4563, request.MissingRanges.Single().StartChunkIndex);
        Assert.Equal(5458, GetPrivateProperty<int>(context, "RemoteGrantedUntilExclusive"));
        Assert.True(GetPrivateProperty<bool>(context, "PullTransportFrontierOnlyRepairActive"));
        Assert.Equal(4563, GetPrivateProperty<int>(context, "PullTransportFrontierOnlyRepairStartChunkIndex"));

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_post_tuna_fallback_stale_credit_recovery_forced;", log, StringComparison.Ordinal);
        Assert.Contains("previous_available_credit_chunks=3032", log, StringComparison.Ordinal);
        Assert.Contains("available_credit_chunks=0", log, StringComparison.Ordinal);
        Assert.Contains("reason=feedback_stalled_with_stale_credit", log, StringComparison.Ordinal);
        Assert.Contains("priority=state_refresh_stale_credit", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6TailReconciliation_ForcesStateRefreshWhenZeroCreditTailStalls()
    {
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_tail_reconciliation",
            remoteNextExpectedChunkIndex: 1975,
            chunksAcceptedForTransport: 1975,
            remoteGrantedUntilExclusive: 1975,
            chunkCount: 3121);
        SetPrivateProperty(context, "V6RegularNknStateRefreshFailureCount", 3);
        SetPrivateProperty(context, "V6EpochLivenessDeferralCount", 12);
        SetPrivateProperty(context, "PullSenderPipelineCurrentInFlightFrames", 1);

        var logStart = GetOperationalLogLength();
        var args = new object?[]
        {
            context,
            TimeSpan.FromMinutes(3),
            null,
        };
        serviceType
            .GetMethod("TryPrepareOutboundV4SparseRuntimeReceiveRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, args);

        var request = Assert.IsType<FileTransferFrontierRequestFrameV6>(args[2]);
        Assert.Equal("state_refresh_tail_reconciliation", request.Priority);
        Assert.Equal("regular_nkn_state_refresh", request.RecoveryMode);
        Assert.Equal(1975, request.MissingRanges.Single().StartChunkIndex);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_fallback_tail_zero_credit_breaker;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_fallback_tail_stale_frontier_retired;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_fallback_tail_reconciliation_requested;", log, StringComparison.Ordinal);
        Assert.Contains("reason=post_tuna_fallback_tail_reconciliation", log, StringComparison.Ordinal);
        Assert.Contains("priority=state_refresh_tail_reconciliation", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6TailReconciliation_TriggersAfterRepeatedZeroCreditRefreshesWithoutFailures()
    {
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_tail_reconciliation_repeat_refresh",
            remoteNextExpectedChunkIndex: 4849,
            chunksAcceptedForTransport: 5385,
            remoteGrantedUntilExclusive: 5385,
            chunkCount: 6200);
        SetPrivateProperty(context, "V6RegularNknStateRefreshFailureCount", 0);
        SetPrivateProperty(context, "V6RegularNknStateRefreshSequence", 3);
        SetPrivateProperty(context, "V6EpochLivenessDeferralCount", 72);

        var logStart = GetOperationalLogLength();
        var args = new object?[]
        {
            context,
            TimeSpan.FromSeconds(90),
            null,
        };
        serviceType
            .GetMethod("TryPrepareOutboundV4SparseRuntimeReceiveRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, args);

        var request = Assert.IsType<FileTransferFrontierRequestFrameV6>(args[2]);
        Assert.Equal("state_refresh_tail_reconciliation", request.Priority);
        Assert.Equal("regular_nkn_state_refresh", request.RecoveryMode);
        Assert.Equal(4849, request.MissingRanges.Single().StartChunkIndex);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_fallback_tail_zero_credit_breaker;", log, StringComparison.Ordinal);
        Assert.Contains("state_refresh_failure_count=0", log, StringComparison.Ordinal);
        Assert.Contains("transport_backlog_chunks=536", log, StringComparison.Ordinal);
        Assert.Contains("reason=post_tuna_fallback_tail_reconciliation", log, StringComparison.Ordinal);
        Assert.Contains("priority=state_refresh_tail_reconciliation", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6TailReconciliation_TriggersWhenLiveCheckpointFrontierStalls()
    {
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_live_checkpoint_tail_stall",
            remoteNextExpectedChunkIndex: 2005,
            chunksAcceptedForTransport: 2005,
            remoteGrantedUntilExclusive: 2389,
            chunkCount: 6200);
        serviceType
            .GetMethod("StartOutboundPostTunaRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "test_live_checkpoint_tail_stall"]);
        SetPrivateProperty(context, "ChunksAcceptedForTransport", 4472);
        SetPrivateProperty(context, "RemoteGrantedUntilExclusive", 2389);

        var handoffType = serviceType.Assembly.GetType("NLink.Core.FileTransfer.TransportHandoffEpoch");
        Assert.NotNull(handoffType);
        var handoff = Activator.CreateInstance(handoffType!)!;
        SetPrivateProperty(handoff, "EpochId", 3L);
        SetPrivateProperty(handoff, "Kind", FileTransferTransportHandoffKind.TunaToNormalFallback);
        SetPrivateProperty(handoff, "SourceTransport", FileTransferTransportKind.Tuna);
        SetPrivateProperty(handoff, "TargetTransport", FileTransferTransportKind.RegularNkn);
        SetPrivateProperty(handoff, "Direction", FileTransferDirection.Outbound);
        SetPrivateProperty(handoff, "Reason", "test_live_checkpoint_tail_stall");
        SetPrivateProperty(handoff, "State", V6TransportHandoffState.FrontierRepairOnly);
        SetPrivateProperty(context, "V6TransportHandoff", handoff);

        var acceptCheckpoint = serviceType
            .GetMethod("TryAcceptOutboundFallbackCheckpointLocked", BindingFlags.Static | BindingFlags.NonPublic)!;
        for (var i = 0; i < 3; i++)
        {
            var receiverState = new FileTransferReceiverStateFrameV6
            {
                SessionId = "session_transfer_post_tuna_fallback_v6_live_checkpoint_tail_stall",
                TransferId = "transfer_post_tuna_fallback_v6_live_checkpoint_tail_stall",
                Epoch = 80 + i,
                ContiguousCommittedChunkIndex = 2005,
                DurableReceivedHighestChunkIndex = 4471,
                CreditUntilChunkIndexExclusive = 2389,
                MissingRanges =
                [
                    new FileTransferRangeV4
                    {
                        StartChunkIndex = 2005,
                        ChunkCount = 384,
                    },
                ],
                BytesCommitted = 2005L * 21 * 1024,
                TransportEpoch = 3,
                RepairRequestId = null,
                Priority = "state_refresh",
                RecoveryMode = "regular_nkn_state_refresh",
            };

            Assert.True((bool)acceptCheckpoint.Invoke(null, [context, receiverState, "receiver_state_sparse_runtime"])!);
        }

        var args = new object?[]
        {
            context,
            null,
        };
        var logStart = GetOperationalLogLength();
        var prepared = (bool)serviceType
            .GetMethod("TryPrepareOutboundPostTunaFallbackLiveCheckpointTailReconciliationLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, args)!;

        Assert.True(prepared);
        var request = Assert.IsType<FileTransferFrontierRequestFrameV6>(args[1]);
        Assert.Equal("state_refresh_tail_reconciliation", request.Priority);
        Assert.Equal("regular_nkn_state_refresh", request.RecoveryMode);
        Assert.Equal(2005, request.MissingRanges.Single().StartChunkIndex);
        Assert.Equal(3, GetPrivateProperty<int>(context, "PostTunaFallbackLiveCheckpointStallCount"));

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_fallback_live_checkpoint_tail_stall_detected;", log, StringComparison.Ordinal);
        Assert.Contains("proven_committed_chunk=2005", log, StringComparison.Ordinal);
        Assert.Contains("proven_highest_observed_chunk=4471", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_fallback_tail_reconciliation_requested;", log, StringComparison.Ordinal);
        Assert.Contains("reason=post_tuna_fallback_live_checkpoint_tail_stalled", log, StringComparison.Ordinal);
        Assert.Contains("priority=state_refresh_tail_reconciliation", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6TailReconciliation_RetiresWedgedNormalStateRefreshSendSlot()
    {
        using var service = new SessionFileTransferService();
        using var senderTransport = new LoopbackFileTransferTransport("session_post_tuna_fallback_v6_tail_reconciliation_retire");
        service.AttachTransport(senderTransport);
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_tail_reconciliation_retire",
            remoteNextExpectedChunkIndex: 1975,
            chunksAcceptedForTransport: 1975,
            remoteGrantedUntilExclusive: 1975,
            chunkCount: 3121);
        SetPrivateProperty(context, "V6RegularNknStateRefreshFailureCount", 3);
        SetPrivateProperty(context, "V6RegularNknStateRefreshSendInFlight", 1);
        SetPrivateProperty(context, "V6RegularNknStateRefreshSendGeneration", 21L);
        SetPrivateProperty(context, "V6RegularNknStateRefreshActiveSendGeneration", 21L);
        SetPrivateProperty(context, "V6RegularNknStateRefreshActiveRequestId", "v6-regular-nkn-state-refresh:old-tail");
        SetPrivateProperty(context, "V6RegularNknStateRefreshActivePriority", "state_refresh");
        SetPrivateProperty(context, "V6RegularNknStateRefreshActiveStartedUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = "session_transfer_post_tuna_fallback_v6_tail_reconciliation_retire",
            TransferId = "transfer_post_tuna_fallback_v6_tail_reconciliation_retire",
            TransportEpoch = 3,
            RepairRequestId = "v6-regular-nkn-state-refresh:new-tail",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 1975,
                    ChunkCount = 1,
                },
            ],
            Priority = "state_refresh_tail_reconciliation",
            RecoveryMode = "regular_nkn_state_refresh",
        };
        var logStart = GetOperationalLogLength();
        serviceType
            .GetMethod("QueueOutboundV4SparseRuntimeStateRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, new CompletingDataSession(request.SessionId, request.TransferId), request]);

        Assert.Equal(0, GetPrivateProperty<int>(context, "V6RegularNknStateRefreshSendInFlight"));
        Assert.Equal(21L, GetPrivateProperty<long>(context, "V6RegularNknStateRefreshRetiredSendGeneration"));
        Assert.Equal("v6-regular-nkn-state-refresh:old-tail", GetPrivateProperty<string>(context, "V6RegularNknStateRefreshRetiredRequestId"));

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_post_tuna_fallback_state_refresh_send_inflight_retired;", log, StringComparison.Ordinal);
        Assert.Contains("reason=tail_reconciliation_forced", log, StringComparison.Ordinal);
        Assert.Contains("replacement_priority=state_refresh_tail_reconciliation", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_post_tuna_fallback_state_refresh_deferred_until_resume;", log, StringComparison.Ordinal);
        Assert.Contains("request_id=v6-regular-nkn-state-refresh:new-tail", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6TailReconciliationCheckpointProof_IsAcceptedForCurrentLeg()
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_tail_reconciliation_accept",
            remoteNextExpectedChunkIndex: 1975,
            chunksAcceptedForTransport: 1975,
            remoteGrantedUntilExclusive: 1975,
            chunkCount: 3121);
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        serviceType
            .GetMethod("StartOutboundPostTunaRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "test_tail_reconciliation_accept"]);

        var receiverState = new FileTransferReceiverStateFrameV6
        {
            SessionId = "session_transfer_post_tuna_fallback_v6_tail_reconciliation_accept",
            TransferId = "transfer_post_tuna_fallback_v6_tail_reconciliation_accept",
            Epoch = 72,
            ContiguousCommittedChunkIndex = 1975,
            DurableReceivedHighestChunkIndex = 2100,
            CreditUntilChunkIndexExclusive = 2231,
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 1975,
                    ChunkCount = 1,
                },
            ],
            BytesCommitted = 1975L * 21 * 1024,
            TransportEpoch = 3,
            RepairRequestId = "v6-regular-nkn-state-refresh:tail",
            Priority = "state_refresh_tail_reconciliation",
            RecoveryMode = "regular_nkn_state_refresh",
        };

        var logStart = GetOperationalLogLength();
        var accepted = (bool)serviceType
            .GetMethod("TryAcceptOutboundFallbackCheckpointLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, receiverState, "receiver_state_sparse_runtime"])!;

        Assert.True(accepted);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_fallback_tail_reconciliation_accepted; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("priority=state_refresh_tail_reconciliation", log, StringComparison.Ordinal);
        Assert.Contains("receiver_highest_observed_chunk=2100", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6TailReconciliationPendingCheckpoint_AcceptsPlainReceiverState()
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_tail_reconciliation_plain_state",
            remoteNextExpectedChunkIndex: 1975,
            chunksAcceptedForTransport: 1975,
            remoteGrantedUntilExclusive: 1975,
            chunkCount: 3121);
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        serviceType
            .GetMethod("StartOutboundPostTunaRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "test_tail_reconciliation_plain_state"]);

        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = "session_transfer_post_tuna_fallback_v6_tail_reconciliation_plain_state",
            TransferId = "transfer_post_tuna_fallback_v6_tail_reconciliation_plain_state",
            TransportEpoch = 3,
            RepairRequestId = "v6-regular-nkn-state-refresh:tail-plain-state",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 1975,
                    ChunkCount = 1,
                },
            ],
            Priority = "state_refresh_tail_reconciliation",
            RecoveryMode = "regular_nkn_state_refresh",
        };
        serviceType
            .GetMethod("MarkOutboundFallbackCheckpointRequestedLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, request, "post_tuna_fallback_tail_reconciliation"]);

        var receiverState = new FileTransferReceiverStateFrameV6
        {
            SessionId = request.SessionId,
            TransferId = request.TransferId,
            Epoch = 73,
            ContiguousCommittedChunkIndex = 1975,
            DurableReceivedHighestChunkIndex = 2100,
            CreditUntilChunkIndexExclusive = 2231,
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 1975,
                    ChunkCount = 1,
                },
            ],
            BytesCommitted = 1975L * 21 * 1024,
            TransportEpoch = 3,
        };

        var logStart = GetOperationalLogLength();
        var accepted = (bool)serviceType
            .GetMethod("TryAcceptOutboundFallbackCheckpointLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, receiverState, "receiver_state_sparse_runtime"])!;

        Assert.True(accepted);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_fallback_tail_reconciliation_accepted; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("checkpoint_request_id=v6-regular-nkn-state-refresh:tail-plain-state", log, StringComparison.Ordinal);
        Assert.Contains("priority=state_refresh_tail_reconciliation", log, StringComparison.Ordinal);
        Assert.Contains("receiver_highest_observed_chunk=2100", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6TailReconciliationPendingCheckpoint_AllowsSafetyReplay()
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_tail_reconciliation_replay",
            remoteNextExpectedChunkIndex: 2201,
            chunksAcceptedForTransport: 3068,
            remoteGrantedUntilExclusive: 3068,
            chunkCount: 3121);
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        serviceType
            .GetMethod("StartOutboundPostTunaRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "test_tail_reconciliation_replay"]);

        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = "session_transfer_post_tuna_fallback_v6_tail_reconciliation_replay",
            TransferId = "transfer_post_tuna_fallback_v6_tail_reconciliation_replay",
            TransportEpoch = 3,
            RepairRequestId = "v6-regular-nkn-state-refresh:tail-replay",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 2201,
                    ChunkCount = 1,
                },
            ],
            Priority = "state_refresh_tail_reconciliation",
            RecoveryMode = "regular_nkn_state_refresh",
        };
        serviceType
            .GetMethod("MarkOutboundFallbackCheckpointRequestedLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, request, "post_tuna_fallback_tail_reconciliation"]);

        var logStart = GetOperationalLogLength();
        serviceType
            .GetMethod("QueueOutboundV4TransportRebindSafetyReplayLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "post_tuna_fallback_peer_silence", true]);

        var repairQueue = Assert.IsAssignableFrom<System.Collections.ICollection>(
            context.GetType()
                .GetProperty("PullV4SenderPumpRepairQueue", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
                .GetValue(context));
        Assert.True(repairQueue.Count > 0);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_transport_rebind_safety_replay_allowed_for_tail_reconciliation;", log, StringComparison.Ordinal);
        Assert.Contains("checkpoint_request_id=v6-regular-nkn-state-refresh:tail-replay", log, StringComparison.Ordinal);
        Assert.DoesNotContain("skip_reason=fallback_checkpoint_pending", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTunaFallbackV6StaleInflightStateRefreshFailure_BypassesCooldownAndRequestsRecovery()
    {
        using var service = new SessionFileTransferService();
        using var senderTransport = new LoopbackFileTransferTransport("session_post_tuna_fallback_v6_stale_inflight_recovery_request");
        service.AttachTransport(senderTransport);
        var serviceType = typeof(SessionFileTransferService);
        var contextType = serviceType.GetNestedType("OutboundTransferContext", BindingFlags.NonPublic)!;
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_stale_inflight_recovery_request");
        SetPrivateProperty(context, "V6LastReceiveRecoveryRequestedUtc", DateTimeOffset.UtcNow);
        SetPrivateProperty(context, "V6RegularNknStateRefreshFailureCount", 3);
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = "session_transfer_post_tuna_fallback_v6_stale_inflight_recovery_request",
            TransferId = "transfer_post_tuna_fallback_v6_stale_inflight_recovery_request",
            TransportEpoch = 3,
            RepairRequestId = "v6-regular-nkn-state-refresh:test-stale-inflight",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 100,
                    ChunkCount = 1,
                },
            ],
            Priority = "state_refresh_stale_inflight",
            RecoveryMode = "regular_nkn_state_refresh",
        };
        var logStart = GetOperationalLogLength();
        serviceType
            .GetMethod("QueueOutboundV4SparseRuntimeStateRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, new ThrowingDataSession(request.SessionId, request.TransferId), request]);

        await WaitUntilAsync(
            () => senderTransport.ReceiveRecoveryRequests.Any(recoveryRequest =>
                recoveryRequest.Direction == FileTransferDirection.Outbound &&
                string.Equals(recoveryRequest.TransferId, request.TransferId, StringComparison.Ordinal) &&
                string.Equals(recoveryRequest.Reason, "post_tuna_fallback_stale_inflight_repair_failed", StringComparison.Ordinal)),
            timeoutMs: 5000);

        var queue = (System.Collections.IEnumerable)contextType
            .GetProperty("PullV4SenderPumpRepairQueue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context)!;
        Assert.NotEmpty(queue.Cast<object>());

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_regular_nkn_state_refresh_send_failed;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_post_tuna_fallback_state_refresh_receive_recovery_requested;", log, StringComparison.Ordinal);
        Assert.Contains("stale_inflight_recovery=1", log, StringComparison.Ordinal);
        Assert.Contains("recovery_reason=post_tuna_fallback_stale_inflight_repair_failed", log, StringComparison.Ordinal);
        Assert.DoesNotContain("suppression_reason=recovery_cooldown", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTunaFallbackV6StaleCreditStateRefreshFailure_BypassesCooldownAndRequestsRecovery()
    {
        using var service = new SessionFileTransferService();
        using var senderTransport = new LoopbackFileTransferTransport("session_post_tuna_fallback_v6_stale_credit_recovery_request");
        service.AttachTransport(senderTransport);
        var serviceType = typeof(SessionFileTransferService);
        var contextType = serviceType.GetNestedType("OutboundTransferContext", BindingFlags.NonPublic)!;
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_stale_credit_recovery_request",
            remoteNextExpectedChunkIndex: 4563,
            chunksAcceptedForTransport: 5458,
            remoteGrantedUntilExclusive: 5458,
            chunkCount: 9000);
        SetPrivateProperty(context, "V6LastReceiveRecoveryRequestedUtc", DateTimeOffset.UtcNow);
        SetPrivateProperty(context, "V6RegularNknStateRefreshFailureCount", 3);
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = "session_transfer_post_tuna_fallback_v6_stale_credit_recovery_request",
            TransferId = "transfer_post_tuna_fallback_v6_stale_credit_recovery_request",
            TransportEpoch = 3,
            RepairRequestId = "v6-regular-nkn-state-refresh:test-stale-credit",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 4563,
                    ChunkCount = 1,
                },
            ],
            Priority = "state_refresh_stale_credit",
            RecoveryMode = "regular_nkn_state_refresh",
        };
        var logStart = GetOperationalLogLength();
        serviceType
            .GetMethod("QueueOutboundV4SparseRuntimeStateRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, new ThrowingDataSession(request.SessionId, request.TransferId), request]);

        await WaitUntilAsync(
            () => senderTransport.ReceiveRecoveryRequests.Any(recoveryRequest =>
                recoveryRequest.Direction == FileTransferDirection.Outbound &&
                string.Equals(recoveryRequest.TransferId, request.TransferId, StringComparison.Ordinal) &&
                string.Equals(recoveryRequest.Reason, "post_tuna_fallback_stale_credit_repair_failed", StringComparison.Ordinal)),
            timeoutMs: 5000);

        var queue = (System.Collections.IEnumerable)contextType
            .GetProperty("PullV4SenderPumpRepairQueue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context)!;
        Assert.NotEmpty(queue.Cast<object>());

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_regular_nkn_state_refresh_send_failed;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_post_tuna_fallback_state_refresh_receive_recovery_requested;", log, StringComparison.Ordinal);
        Assert.Contains("stale_credit_recovery=1", log, StringComparison.Ordinal);
        Assert.Contains("recovery_reason=post_tuna_fallback_stale_credit_repair_failed", log, StringComparison.Ordinal);
        Assert.DoesNotContain("suppression_reason=recovery_cooldown", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTunaFallbackV6StateRefreshSendTimeout_BypassesCooldownAndRequestsRecovery()
    {
        using var service = new SessionFileTransferService();
        using var senderTransport = new LoopbackFileTransferTransport("session_post_tuna_fallback_v6_state_refresh_timeout_recovery_request");
        service.AttachTransport(senderTransport);
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_state_refresh_timeout_recovery_request");
        SetPrivateProperty(context, "PullPostTunaRecoveryActive", false);
        SetPrivateProperty(context, "V6LastReceiveRecoveryRequestedUtc", DateTimeOffset.UtcNow);
        SetPrivateProperty(context, "V6RegularNknStateRefreshFailureCount", 3);
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = "session_transfer_post_tuna_fallback_v6_state_refresh_timeout_recovery_request",
            TransferId = "transfer_post_tuna_fallback_v6_state_refresh_timeout_recovery_request",
            TransportEpoch = 3,
            RepairRequestId = "v6-regular-nkn-state-refresh:test-send-timeout",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 100,
                    ChunkCount = 1,
                },
            ],
            Priority = "state_refresh",
            RecoveryMode = "regular_nkn_state_refresh",
        };
        var logStart = GetOperationalLogLength();
        serviceType
            .GetMethod("StartOutboundPostTunaRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "unit_test_checkpoint_retire"]);
        serviceType
            .GetMethod("MarkOutboundFallbackCheckpointRequestedLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, request, "unit_test_checkpoint_pending"]);
        serviceType
            .GetMethod("RequestOutboundPostTunaFallbackStateRefreshReceiveRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, request, "post_tuna_fallback_state_refresh_send_timeout"]);

        await WaitUntilAsync(
            () => senderTransport.ReceiveRecoveryRequests.Any(recoveryRequest =>
                recoveryRequest.Direction == FileTransferDirection.Outbound &&
                string.Equals(recoveryRequest.TransferId, request.TransferId, StringComparison.Ordinal) &&
                string.Equals(recoveryRequest.Reason, "post_tuna_fallback_state_refresh_failed", StringComparison.Ordinal)),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_post_tuna_fallback_state_refresh_receive_recovery_requested;", log, StringComparison.Ordinal);
        Assert.Contains("state_refresh_send_timeout=1", log, StringComparison.Ordinal);
        Assert.Contains("recovery_reason=post_tuna_fallback_state_refresh_failed", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_fallback_checkpoint_retired; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("retired_checkpoint_request_id=v6-regular-nkn-state-refresh:test-send-timeout", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_safety_replay_started;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("skip_reason=fallback_checkpoint_pending", log, StringComparison.Ordinal);
        Assert.DoesNotContain("suppression_reason=recovery_cooldown", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6SupersededStateRefreshTimeout_DoesNotInterruptNewCheckpoint()
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_superseded_state_refresh_timeout",
            remoteNextExpectedChunkIndex: 1859,
            chunksAcceptedForTransport: 1859,
            remoteGrantedUntilExclusive: 1859,
            chunkCount: 3121);
        SetPrivateProperty(context, "V6RegularNknStateRefreshSendGeneration", 5L);
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        serviceType
            .GetMethod("StartOutboundPostTunaRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "unit_test_superseded_state_refresh"]);

        var currentRequest = new FileTransferFrontierRequestFrameV6
        {
            SessionId = "session_transfer_post_tuna_fallback_v6_superseded_state_refresh_timeout",
            TransferId = "transfer_post_tuna_fallback_v6_superseded_state_refresh_timeout",
            TransportEpoch = 2,
            RepairRequestId = "v6-regular-nkn-state-refresh:7",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 1859,
                    ChunkCount = 1,
                },
            ],
            Priority = "state_refresh",
            RecoveryMode = "regular_nkn_state_refresh",
        };
        serviceType
            .GetMethod("MarkOutboundFallbackCheckpointRequestedLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, currentRequest, "unit_test_current_checkpoint"]);

        var oldRequest = new FileTransferFrontierRequestFrameV6
        {
            SessionId = currentRequest.SessionId,
            TransferId = currentRequest.TransferId,
            TransportEpoch = 0,
            RepairRequestId = "v6-regular-nkn-state-refresh:5",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 1859,
                    ChunkCount = 1,
                },
            ],
            Priority = "state_refresh",
            RecoveryMode = "regular_nkn_state_refresh",
        };

        var logStart = GetOperationalLogLength();
        var observed = (bool)serviceType
            .GetMethod("TryObserveRetiredOutboundV4SparseRuntimeStateRefreshSend", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, oldRequest, 4L, "timeout"])!;

        Assert.True(observed);
        var currentLeg = context.GetType()
            .GetProperty("CurrentTransferLeg", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context);
        Assert.NotNull(currentLeg);
        var checkpointRequestId = currentLeg!.GetType()
            .GetProperty("CheckpointRequestId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(currentLeg) as string;
        Assert.Equal("v6-regular-nkn-state-refresh:7", checkpointRequestId);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_regular_nkn_state_refresh_retired_send_observed;", log, StringComparison.Ordinal);
        Assert.Contains("request_id=v6-regular-nkn-state-refresh:5", log, StringComparison.Ordinal);
        Assert.Contains("active_request_id=(none)", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_fallback_checkpoint_retired;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_post_tuna_fallback_state_refresh_receive_recovery_requested;", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTunaFallbackV6StaleInflightStateRefresh_RetiresWedgedSendSlotAndReplaysFreshProbeAfterResume()
    {
        using var service = new SessionFileTransferService();
        using var senderTransport = new LoopbackFileTransferTransport("session_post_tuna_fallback_v6_state_refresh_retire_slot");
        service.AttachTransport(senderTransport);
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_state_refresh_retire_slot");
        SetPrivateProperty(context, "V6RegularNknStateRefreshFailureCount", 3);
        SetPrivateProperty(context, "V6RegularNknStateRefreshSendInFlight", 1);
        SetPrivateProperty(context, "V6RegularNknStateRefreshSendGeneration", 8L);
        SetPrivateProperty(context, "V6RegularNknStateRefreshActiveSendGeneration", 8L);
        SetPrivateProperty(context, "V6RegularNknStateRefreshActiveRequestId", "v6-regular-nkn-state-refresh:old");
        SetPrivateProperty(context, "V6RegularNknStateRefreshActivePriority", "state_refresh");
        SetPrivateProperty(context, "V6RegularNknStateRefreshActiveStartedUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = "session_transfer_post_tuna_fallback_v6_state_refresh_retire_slot",
            TransferId = "transfer_post_tuna_fallback_v6_state_refresh_retire_slot",
            TransportEpoch = 3,
            RepairRequestId = "v6-regular-nkn-state-refresh:new-stale-inflight",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 100,
                    ChunkCount = 1,
                },
            ],
            Priority = "state_refresh_stale_inflight",
            RecoveryMode = "regular_nkn_state_refresh",
        };
        using var dataSession = new BlockingDataSession(request.SessionId, request.TransferId);
        SetPrivateProperty(context, "DataSession", dataSession);
        var logStart = GetOperationalLogLength();
        serviceType
            .GetMethod("QueueOutboundV4SparseRuntimeStateRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, dataSession, request]);

        await Task.Delay(100);
        Assert.False(dataSession.SendStarted.Task.IsCompleted);
        var deferredStateRefreshProperty = context.GetType()
            .GetProperty("V6RegularNknDeferredStateRefreshRequest", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        Assert.NotNull(deferredStateRefreshProperty.GetValue(context));
        Assert.Equal(0, GetPrivateProperty<int>(context, "V6RegularNknStateRefreshSendInFlight"));
        Assert.Equal(0L, GetPrivateProperty<long>(context, "V6RegularNknStateRefreshActiveSendGeneration"));
        Assert.Equal(8L, GetPrivateProperty<long>(context, "V6RegularNknStateRefreshRetiredSendGeneration"));
        Assert.Equal("v6-regular-nkn-state-refresh:old", GetPrivateProperty<string>(context, "V6RegularNknStateRefreshRetiredRequestId"));

        serviceType
            .GetMethod("QueueDeferredOutboundPostTunaFallbackStateRefreshAfterResume", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "transport_recovered"]);

        await WaitUntilAsync(() => dataSession.SendStarted.Task.IsCompleted, timeoutMs: 5000);

        Assert.Equal(1, GetPrivateProperty<int>(context, "V6RegularNknStateRefreshSendInFlight"));
        Assert.Equal(9L, GetPrivateProperty<long>(context, "V6RegularNknStateRefreshActiveSendGeneration"));
        Assert.Equal("v6-regular-nkn-state-refresh:new-stale-inflight", GetPrivateProperty<string>(context, "V6RegularNknStateRefreshActiveRequestId"));
        Assert.Null(deferredStateRefreshProperty.GetValue(context));
        Assert.Contains(
            senderTransport.ReceiveRecoveryRequests,
            recoveryRequest =>
                recoveryRequest.Direction == FileTransferDirection.Outbound &&
                string.Equals(recoveryRequest.TransferId, request.TransferId, StringComparison.Ordinal) &&
                string.Equals(recoveryRequest.Reason, "post_tuna_fallback_stale_state_refresh_send_retired", StringComparison.Ordinal));

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_post_tuna_fallback_state_refresh_send_inflight_retired;", log, StringComparison.Ordinal);
        Assert.Contains("retired_generation=8", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_post_tuna_fallback_state_refresh_deferred_until_resume;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_post_tuna_fallback_state_refresh_deferred_replayed;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_regular_nkn_state_refresh_send_queued;", log, StringComparison.Ordinal);
        Assert.Contains("generation=9", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_regular_nkn_state_refresh_send_skipped;", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTunaFallbackV6StateRefresh_LateRetiredSendCompletionDoesNotClearFreshGeneration()
    {
        using var service = new SessionFileTransferService();
        using var senderTransport = new LoopbackFileTransferTransport("session_post_tuna_fallback_v6_state_refresh_late_retired");
        service.AttachTransport(senderTransport);
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_state_refresh_late_retired");
        SetPrivateProperty(context, "V6RegularNknStateRefreshFailureCount", 3);
        SetPrivateProperty(context, "V6RegularNknStateRefreshSendInFlight", 1);
        SetPrivateProperty(context, "V6RegularNknStateRefreshSendGeneration", 4L);
        SetPrivateProperty(context, "V6RegularNknStateRefreshActiveSendGeneration", 4L);
        SetPrivateProperty(context, "V6RegularNknStateRefreshActiveRequestId", "v6-regular-nkn-state-refresh:old");
        SetPrivateProperty(context, "V6RegularNknStateRefreshActivePriority", "state_refresh");
        SetPrivateProperty(context, "V6RegularNknStateRefreshActiveStartedUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var oldRequest = new FileTransferFrontierRequestFrameV6
        {
            SessionId = "session_transfer_post_tuna_fallback_v6_state_refresh_late_retired",
            TransferId = "transfer_post_tuna_fallback_v6_state_refresh_late_retired",
            TransportEpoch = 3,
            RepairRequestId = "v6-regular-nkn-state-refresh:old",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 100,
                    ChunkCount = 1,
                },
            ],
            Priority = "state_refresh",
            RecoveryMode = "regular_nkn_state_refresh",
        };
        var newRequest = new FileTransferFrontierRequestFrameV6
        {
            SessionId = oldRequest.SessionId,
            TransferId = oldRequest.TransferId,
            TransportEpoch = 3,
            RepairRequestId = "v6-regular-nkn-state-refresh:new-stale-inflight",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 100,
                    ChunkCount = 1,
                },
            ],
            Priority = "state_refresh_stale_inflight",
            RecoveryMode = "regular_nkn_state_refresh",
        };
        using var freshDataSession = new BlockingDataSession(newRequest.SessionId, newRequest.TransferId);
        SetPrivateProperty(context, "DataSession", freshDataSession);
        var logStart = GetOperationalLogLength();
        serviceType
            .GetMethod("QueueOutboundV4SparseRuntimeStateRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, freshDataSession, newRequest]);
        serviceType
            .GetMethod("QueueDeferredOutboundPostTunaFallbackStateRefreshAfterResume", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "transport_recovered"]);
        await WaitUntilAsync(() => freshDataSession.SendStarted.Task.IsCompleted, timeoutMs: 5000);

        serviceType
            .GetMethod("SendOutboundV4SparseRuntimeStateRefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, new CompletingDataSession(oldRequest.SessionId, oldRequest.TransferId), oldRequest, 4L]);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains(
                "event=filetransfer_v6_regular_nkn_state_refresh_retired_send_observed;",
                StringComparison.Ordinal),
            timeoutMs: 5000);

        Assert.Equal(1, GetPrivateProperty<int>(context, "V6RegularNknStateRefreshSendInFlight"));
        Assert.Equal(5L, GetPrivateProperty<long>(context, "V6RegularNknStateRefreshActiveSendGeneration"));
        Assert.Equal("v6-regular-nkn-state-refresh:new-stale-inflight", GetPrivateProperty<string>(context, "V6RegularNknStateRefreshActiveRequestId"));
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("outcome=sent", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_regular_nkn_state_refresh_sent; transfer_id=transfer_post_tuna_fallback_v6_state_refresh_late_retired; session_id=session_transfer_post_tuna_fallback_v6_state_refresh_late_retired; request_id=v6-regular-nkn-state-refresh:old", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6NormalStateRefresh_StillSkipsWhenSendInFlight()
    {
        using var service = new SessionFileTransferService();
        using var senderTransport = new LoopbackFileTransferTransport("session_post_tuna_fallback_v6_state_refresh_normal_skip");
        service.AttachTransport(senderTransport);
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_state_refresh_normal_skip");
        SetPrivateProperty(context, "V6RegularNknStateRefreshFailureCount", 3);
        SetPrivateProperty(context, "V6RegularNknStateRefreshSendInFlight", 1);
        SetPrivateProperty(context, "V6RegularNknStateRefreshActiveSendGeneration", 2L);
        SetPrivateProperty(context, "V6RegularNknStateRefreshActiveRequestId", "v6-regular-nkn-state-refresh:old");
        SetPrivateProperty(context, "V6RegularNknStateRefreshActivePriority", "state_refresh");
        SetPrivateProperty(context, "V6RegularNknStateRefreshActiveStartedUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = "session_transfer_post_tuna_fallback_v6_state_refresh_normal_skip",
            TransferId = "transfer_post_tuna_fallback_v6_state_refresh_normal_skip",
            TransportEpoch = 3,
            RepairRequestId = "v6-regular-nkn-state-refresh:normal",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 100,
                    ChunkCount = 1,
                },
            ],
            Priority = "state_refresh",
            RecoveryMode = "regular_nkn_state_refresh",
        };
        var logStart = GetOperationalLogLength();
        serviceType
            .GetMethod("QueueOutboundV4SparseRuntimeStateRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, new CompletingDataSession(request.SessionId, request.TransferId), request]);

        Assert.Equal(1, GetPrivateProperty<int>(context, "V6RegularNknStateRefreshSendInFlight"));
        Assert.Equal(2L, GetPrivateProperty<long>(context, "V6RegularNknStateRefreshActiveSendGeneration"));
        Assert.Empty(senderTransport.ReceiveRecoveryRequests);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_regular_nkn_state_refresh_send_skipped;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_post_tuna_fallback_state_refresh_send_inflight_retired;", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6CheckpointRequest_DoesNotMarkUnsentSkippedStateRefreshAsPending()
    {
        using var service = new SessionFileTransferService();
        using var senderTransport = new LoopbackFileTransferTransport("session_post_tuna_fallback_v6_checkpoint_skip");
        service.AttachTransport(senderTransport);
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_checkpoint_skip");
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        serviceType
            .GetMethod("StartOutboundPostTunaRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "test_checkpoint_skip"]);
        SetPrivateProperty(context, "DataSession", new CompletingDataSession(
            "session_transfer_post_tuna_fallback_v6_checkpoint_skip",
            "transfer_post_tuna_fallback_v6_checkpoint_skip"));
        SetPrivateProperty(context, "V6RegularNknStateRefreshSendInFlight", 1);
        SetPrivateProperty(context, "V6RegularNknStateRefreshActiveSendGeneration", 11L);
        SetPrivateProperty(context, "V6RegularNknStateRefreshActiveRequestId", "v6-regular-nkn-state-refresh:active");
        SetPrivateProperty(context, "V6RegularNknStateRefreshActivePriority", "state_refresh");
        SetPrivateProperty(context, "V6RegularNknStateRefreshActiveStartedUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));

        var logStart = GetOperationalLogLength();
        serviceType
            .GetMethod("QueueOutboundPostTunaFallbackCheckpointRequestLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "test_checkpoint_skip"]);

        var leg = context.GetType()
            .GetProperty("CurrentTransferLeg", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context);
        Assert.NotNull(leg);
        var checkpointRequestId = leg!.GetType()
            .GetProperty("CheckpointRequestId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(leg) as string;

        Assert.True(string.IsNullOrWhiteSpace(checkpointRequestId));
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_regular_nkn_state_refresh_send_skipped;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_fallback_checkpoint_requested; direction=outbound;", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6CheckpointProof_ClearsOlderTransportEpochGuards()
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_stale_epoch_clear");
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        serviceType
            .GetMethod("StartOutboundPostTunaRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "test_stale_epoch_clear"]);

        var epochType = serviceType.Assembly.GetType("NLink.Core.FileTransfer.V6TransportEpoch");
        Assert.NotNull(epochType);
        var staleEpoch = Activator.CreateInstance(epochType!)!;
        SetPrivateProperty(staleEpoch, "EpochId", 2L);
        SetPrivateProperty(staleEpoch, "Kind", FileTransferTransportHandoffKind.RegularNknRecovery);
        SetPrivateProperty(staleEpoch, "SourceTransport", FileTransferTransportKind.RegularNkn);
        SetPrivateProperty(staleEpoch, "TargetTransport", FileTransferTransportKind.RegularNkn);
        SetPrivateProperty(staleEpoch, "Direction", FileTransferDirection.Outbound);
        SetPrivateProperty(staleEpoch, "Reason", "stale_recovery");
        SetPrivateProperty(staleEpoch, "State", V6TransportEpochState.WaitingForTargetTransport);
        SetPrivateProperty(context, "V6TransportEpoch", staleEpoch);

        var handoffType = serviceType.Assembly.GetType("NLink.Core.FileTransfer.TransportHandoffEpoch");
        Assert.NotNull(handoffType);
        var staleHandoff = Activator.CreateInstance(handoffType!)!;
        SetPrivateProperty(staleHandoff, "EpochId", 2L);
        SetPrivateProperty(staleHandoff, "Kind", FileTransferTransportHandoffKind.TunaToNormalFallback);
        SetPrivateProperty(staleHandoff, "SourceTransport", FileTransferTransportKind.Tuna);
        SetPrivateProperty(staleHandoff, "TargetTransport", FileTransferTransportKind.RegularNkn);
        SetPrivateProperty(staleHandoff, "Direction", FileTransferDirection.Outbound);
        SetPrivateProperty(staleHandoff, "Reason", "stale_handoff");
        SetPrivateProperty(staleHandoff, "State", V6TransportHandoffState.WaitingForTargetTransport);
        SetPrivateProperty(context, "V6TransportHandoff", staleHandoff);

        var receiverState = new FileTransferReceiverStateFrameV6
        {
            SessionId = "session_transfer_post_tuna_fallback_v6_stale_epoch_clear",
            TransferId = "transfer_post_tuna_fallback_v6_stale_epoch_clear",
            Epoch = 12,
            ContiguousCommittedChunkIndex = 100,
            DurableReceivedHighestChunkIndex = 99,
            CreditUntilChunkIndexExclusive = 164,
            MissingRanges = [],
            BytesCommitted = 100 * 21 * 1024,
            TransportEpoch = 3,
            RepairRequestId = "v6-regular-nkn-state-refresh:3",
            Priority = "state_refresh",
            RecoveryMode = "regular_nkn_state_refresh",
        };

        var logStart = GetOperationalLogLength();
        var accepted = (bool)serviceType
            .GetMethod("TryAcceptOutboundFallbackCheckpointLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, receiverState, "receiver_state_sparse_runtime"])!;

        Assert.True(accepted);
        Assert.Null(context.GetType()
            .GetProperty("V6TransportEpoch", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context));
        Assert.Null(context.GetType()
            .GetProperty("V6TransportHandoff", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context));

        serviceType
            .GetMethod("QueueOutboundV4TransportRebindSafetyReplayLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "post_tuna_fallback_checkpoint_proof", true]);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_fallback_stale_transport_epoch_cleared; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("checkpoint_transport_epoch=3", log, StringComparison.Ordinal);
        Assert.Contains("cleared_transport_epoch=2", log, StringComparison.Ordinal);
        Assert.Contains("cleared_handoff_epoch=2", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_tail_blocked_until_frontier_proof; direction=outbound;", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6CheckpointProof_AdoptsNewerSameLegTransportEpoch()
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_newer_same_leg_epoch");
        SetPrivateProperty(context, "PullTransportRebindGeneration", 1);
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        serviceType
            .GetMethod("StartOutboundPostTunaRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "test_same_leg_epoch"]);

        var acceptCheckpoint = serviceType
            .GetMethod("TryAcceptOutboundFallbackCheckpointLocked", BindingFlags.Static | BindingFlags.NonPublic)!;
        var initialState = new FileTransferReceiverStateFrameV6
        {
            SessionId = "session_transfer_post_tuna_fallback_v6_newer_same_leg_epoch",
            TransferId = "transfer_post_tuna_fallback_v6_newer_same_leg_epoch",
            Epoch = 12,
            ContiguousCommittedChunkIndex = 100,
            DurableReceivedHighestChunkIndex = 110,
            CreditUntilChunkIndexExclusive = 164,
            MissingRanges = [],
            BytesCommitted = 100 * 21 * 1024,
            TransportEpoch = 1,
            RepairRequestId = null,
            Priority = "state_refresh",
            RecoveryMode = "regular_nkn_state_refresh",
        };
        Assert.True((bool)acceptCheckpoint.Invoke(null, [context, initialState, "receiver_state_sparse_runtime"])!);

        var handoffType = serviceType.Assembly.GetType("NLink.Core.FileTransfer.TransportHandoffEpoch");
        Assert.NotNull(handoffType);
        var staleHandoff = Activator.CreateInstance(handoffType!)!;
        SetPrivateProperty(staleHandoff, "EpochId", 1L);
        SetPrivateProperty(staleHandoff, "Kind", FileTransferTransportHandoffKind.TunaToNormalFallback);
        SetPrivateProperty(staleHandoff, "SourceTransport", FileTransferTransportKind.Tuna);
        SetPrivateProperty(staleHandoff, "TargetTransport", FileTransferTransportKind.RegularNkn);
        SetPrivateProperty(staleHandoff, "Direction", FileTransferDirection.Outbound);
        SetPrivateProperty(staleHandoff, "Reason", "stale_handoff");
        SetPrivateProperty(staleHandoff, "State", V6TransportHandoffState.FrontierRepairOnly);
        SetPrivateProperty(context, "V6TransportHandoff", staleHandoff);

        var newerState = new FileTransferReceiverStateFrameV6
        {
            SessionId = "session_transfer_post_tuna_fallback_v6_newer_same_leg_epoch",
            TransferId = "transfer_post_tuna_fallback_v6_newer_same_leg_epoch",
            Epoch = 13,
            ContiguousCommittedChunkIndex = 120,
            DurableReceivedHighestChunkIndex = 130,
            CreditUntilChunkIndexExclusive = 184,
            MissingRanges = [],
            BytesCommitted = 120 * 21 * 1024,
            TransportEpoch = 2,
            RepairRequestId = null,
            Priority = "state_refresh",
            RecoveryMode = "regular_nkn_state_refresh",
        };

        var logStart = GetOperationalLogLength();
        var accepted = (bool)acceptCheckpoint.Invoke(null, [context, newerState, "receiver_state_sparse_runtime"])!;

        Assert.True(accepted);
        Assert.Null(context.GetType()
            .GetProperty("V6TransportHandoff", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context));
        var leg = context.GetType()
            .GetProperty("CurrentTransferLeg", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context);
        Assert.NotNull(leg);
        Assert.Equal(2L, leg!.GetType()
            .GetProperty("TransportEpochId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(leg));

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_fallback_transport_epoch_adopted; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("previous_transport_epoch=1", log, StringComparison.Ordinal);
        Assert.Contains("transport_epoch=2", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_fallback_stale_transport_epoch_cleared; direction=outbound;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("reason=transport_epoch_mismatch", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6BridgeRestartCancellation_DefersInsteadOfTerminalPeerDisconnect()
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_bridge_restart_cancel");
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        serviceType
            .GetMethod("StartOutboundPostTunaRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "test_bridge_restart_cancel"]);

        var logStart = GetOperationalLogLength();
        var deferred = (bool)serviceType
            .GetMethod("TryDeferOutboundPostTunaFallbackDataSessionCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    "sender_pump",
                    new OperationCanceledException("bridge restart canceled active send"),
                ])!;

        Assert.True(deferred);
        Assert.Equal(FileTransferTransferState.Sending, GetPrivateProperty<FileTransferTransferState>(context, "State"));
        Assert.True(GetPrivateProperty<bool>(context, "PullTransportPaused"));
        Assert.Equal("post_tuna_fallback_bridge_restart", GetPrivateProperty<string>(context, "PullTransportPauseReason"));
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_post_tuna_fallback_data_session_cancellation_deferred; direction=outbound;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v4_sender_failed;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("error_code=peer_disconnected", log, StringComparison.Ordinal);
    }

    [Fact]
    public void PostTunaFallbackV6BridgeRestartSendFailure_DefersInsteadOfTerminalPeerDisconnect()
    {
        using var service = new SessionFileTransferService();
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            "transfer_post_tuna_fallback_v6_bridge_restart_send_failure");
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        serviceType
            .GetMethod("StartOutboundPostTunaRecoveryLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [context, "test_bridge_restart_send_failure"]);

        var logStart = GetOperationalLogLength();
        var deferred = (bool)serviceType
            .GetMethod("TryDeferOutboundPostTunaFallbackTransportSendFailure", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    "sender_pump",
                    new InvalidOperationException(
                        "File-transfer V4 sender transport send failed.",
                        new ObjectDisposedException("LoopbackDataSession", "Bridge disconnected during recovery.")),
                ])!;

        Assert.True(deferred);
        Assert.Equal(FileTransferTransferState.Sending, GetPrivateProperty<FileTransferTransferState>(context, "State"));
        Assert.True(GetPrivateProperty<bool>(context, "PullTransportPaused"));
        Assert.Equal("post_tuna_fallback_bridge_restart", GetPrivateProperty<string>(context, "PullTransportPauseReason"));
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_post_tuna_fallback_transport_send_failure_deferred; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("reason=post_tuna_fallback_bridge_restart_send_failure", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v4_sender_failed;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("error_code=peer_disconnected", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V6HeartbeatLoop_StopsAfterLiveReactivationToFileTunaV4()
    {
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = TimeSpan.FromMilliseconds(25);
        SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromMilliseconds(75);
        try
        {
            var logStart = GetOperationalLogLength();
            using var service = new SessionFileTransferService();
            var serviceType = typeof(SessionFileTransferService);
            var context = CreatePostTunaFallbackV6OutboundContext(
                "transfer_post_tuna_fallback_v6_heartbeat_stops_after_tuna_reactivation");
            serviceType
                .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, context);
            serviceType
                .GetMethod("StartOutboundV6HeartbeatLoop", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, "test_post_tuna_fallback"]);

            Assert.True(GetPrivateProperty<bool>(context, "V6HeartbeatLoopStarted"));

            var routeSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
            SetPrivateProperty(context, "RouteSelection", routeSelection);
            SetPrivateProperty(context, "RouteRuntime", routeSelection.RuntimeDescriptor);
            SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV4);
            SetPrivateProperty(context, "PullPostTunaRecoveryActive", false);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v6_heartbeat_stopped; direction=outbound;",
                    StringComparison.Ordinal),
                timeoutMs: 5000);

            var log = ReadOperationalLogTail(logStart);
            Assert.False(GetPrivateProperty<bool>(context, "V6HeartbeatLoopStarted"));
            Assert.Equal(FileTransferTransferState.Sending, GetPrivateProperty<FileTransferTransferState>(context, "State"));
            Assert.Contains("reason=route_runtime_changed", log, StringComparison.Ordinal);
            Assert.Contains("route=file_tuna_v4; protocol_version=4", log, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_heartbeat_timeout; direction=outbound", log, StringComparison.Ordinal);
            Assert.DoesNotContain("error_code=peer_disconnected", log, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = previousHeartbeatInterval;
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = previousPeerLivenessTimeout;
        }
    }

    [Fact]
    public async Task InboundV6HeartbeatLoop_StopsAfterLiveReactivationToFileTunaV4()
    {
        var previousHeartbeatInterval = SessionFileTransferService.V6HeartbeatIntervalOverrideForTests;
        var previousPeerLivenessTimeout = SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests;
        SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = TimeSpan.FromMilliseconds(25);
        SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = TimeSpan.FromMilliseconds(75);
        try
        {
            var logStart = GetOperationalLogLength();
            using var service = new SessionFileTransferService();
            var serviceType = typeof(SessionFileTransferService);
            var context = CreatePostTunaFallbackV6InboundContext(
                "transfer_post_tuna_fallback_v6_inbound_heartbeat_stops_after_tuna_reactivation");
            serviceType
                .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, context);
            serviceType
                .GetMethod("StartInboundV6HeartbeatLoop", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, "test_post_tuna_fallback"]);

            Assert.True(GetPrivateProperty<bool>(context, "V6HeartbeatLoopStarted"));

            var routeSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4);
            SetPrivateProperty(context, "RouteSelection", routeSelection);
            SetPrivateProperty(context, "RouteRuntime", routeSelection.RuntimeDescriptor);
            SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV4);
            SetPrivateProperty(context, "PullPostTunaRecoveryActive", false);

            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v6_heartbeat_stopped; direction=inbound;",
                    StringComparison.Ordinal),
                timeoutMs: 5000);

            var log = ReadOperationalLogTail(logStart);
            Assert.False(GetPrivateProperty<bool>(context, "V6HeartbeatLoopStarted"));
            Assert.Equal(FileTransferTransferState.Receiving, GetPrivateProperty<FileTransferTransferState>(context, "State"));
            Assert.Contains("reason=route_runtime_changed", log, StringComparison.Ordinal);
            Assert.Contains("route=file_tuna_v4; protocol_version=4", log, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_heartbeat_timeout; direction=inbound", log, StringComparison.Ordinal);
            Assert.DoesNotContain("error_code=peer_disconnected", log, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V6HeartbeatIntervalOverrideForTests = previousHeartbeatInterval;
            SessionFileTransferService.V6PeerLivenessTimeoutOverrideForTests = previousPeerLivenessTimeout;
        }
    }

    [Fact]
    public async Task PostTunaFallbackV6StateRefreshFailure_WithFreshPeerProgress_DefersReceiveRecoveryAndFrontierReplay()
    {
        using var service = new SessionFileTransferService();
        using var senderTransport = new LoopbackFileTransferTransport("session_post_tuna_fallback_v6_state_refresh_deferred");
        service.AttachTransport(senderTransport);
        var serviceType = typeof(SessionFileTransferService);
        var contextType = serviceType.GetNestedType("OutboundTransferContext", BindingFlags.NonPublic)!;
        FileTransferReadStreamFactory openReadStreamAsync =
            _ => Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>(), writable: false));
        const int chunkSize = 21 * 1024;
        var descriptor = new FileTransferSendDescriptor(
            "post-tuna-fallback-v6-state-refresh-deferred.bin",
            256L * chunkSize,
            "transfer_post_tuna_fallback_v6_state_refresh_deferred");
        var context = Activator.CreateInstance(
            contextType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [descriptor, openReadStreamAsync],
            culture: null)!;
        var routeSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var bridgePolicyType = serviceType.GetNestedType("FileTransferBridgeRecoveryPolicy", BindingFlags.NonPublic)!;
        SetPrivateProperty(context, "SessionId", "session_post_tuna_fallback_v6_state_refresh_deferred");
        SetPrivateProperty(context, "RouteSelection", routeSelection);
        SetPrivateProperty(context, "RouteRuntime", routeSelection.RuntimeDescriptor);
        SetPrivateProperty(context, "BridgeRecoveryPolicy", Enum.Parse(bridgePolicyType, "PostTunaFallbackStrictRecovery"));
        SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV6);
        SetPrivateProperty(context, "State", FileTransferTransferState.Sending);
        SetPrivateProperty(context, "ChunkSizeBytes", chunkSize);
        SetPrivateProperty(context, "ChunkCount", 256);
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullSessionActive", true);
        SetPrivateProperty(context, "PullPostTunaRecoveryActive", true);
        SetPrivateProperty(context, "PullTransportRebindGeneration", 3);
        SetPrivateProperty(context, "RemoteNextExpectedChunkIndex", 100);
        SetPrivateProperty(context, "RemoteGrantedUntilExclusive", 256);
        SetPrivateProperty(context, "ChunksAcceptedForTransport", 256);
        SetPrivateProperty(context, "PullTransportFrontierOnlyRepairActive", true);
        SetPrivateProperty(context, "PullTransportFrontierOnlyRepairStartChunkIndex", 100);
        SetPrivateProperty(context, "PullV4LastPeerFrameReceivedUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(20));
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = "session_post_tuna_fallback_v6_state_refresh_deferred",
            TransferId = "transfer_post_tuna_fallback_v6_state_refresh_deferred",
            TransportEpoch = 3,
            RepairRequestId = "v6-regular-nkn-state-refresh:test-deferred",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 100,
                    ChunkCount = 1,
                },
            ],
            Priority = "state_refresh",
            RecoveryMode = "regular_nkn_state_refresh",
        };
        var logStart = GetOperationalLogLength();
        serviceType
            .GetMethod("QueueOutboundV4SparseRuntimeStateRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, new ThrowingDataSession(request.SessionId, request.TransferId), request]);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_post_tuna_fallback_state_refresh_receive_recovery_deferred;", StringComparison.Ordinal),
            timeoutMs: 5000);

        Assert.DoesNotContain(
            senderTransport.ReceiveRecoveryRequests,
            recoveryRequest =>
                recoveryRequest.Direction == FileTransferDirection.Outbound &&
                string.Equals(recoveryRequest.TransferId, request.TransferId, StringComparison.Ordinal) &&
                string.Equals(recoveryRequest.Reason, "post_tuna_fallback_state_refresh_failed", StringComparison.Ordinal));

        var queue = (System.Collections.IEnumerable)contextType
            .GetProperty("PullV4SenderPumpRepairQueue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context)!;
        var queuedRepair = queue.Cast<object>().Single();
        var queuedRepairType = queuedRepair.GetType();
        var chunkIndices = ((IEnumerable<int>)queuedRepairType.GetProperty("ChunkIndices")!.GetValue(queuedRepair)!).ToArray();

        Assert.Equal(100, queuedRepairType.GetProperty("FirstStartChunkIndex")!.GetValue(queuedRepair));
        Assert.Equal(164, queuedRepairType.GetProperty("LastEndChunkExclusive")!.GetValue(queuedRepair));
        Assert.Equal(Enumerable.Range(100, 64), chunkIndices);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_regular_nkn_state_refresh_send_failed;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_post_tuna_fallback_state_refresh_receive_recovery_deferred;", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", log, StringComparison.Ordinal);
        Assert.Contains("post_tuna_v6_frontier_sweep=1", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostTunaFallbackV6StateRefreshFailure_DuringTunaActivationPauseSuppressesPauseAndRequestsRecovery()
    {
        using var service = new SessionFileTransferService();
        using var senderTransport = new LoopbackFileTransferTransport("session_post_tuna_fallback_v6_state_refresh_activation_pause");
        service.AttachTransport(senderTransport);
        var serviceType = typeof(SessionFileTransferService);
        var contextType = serviceType.GetNestedType("OutboundTransferContext", BindingFlags.NonPublic)!;
        FileTransferReadStreamFactory openReadStreamAsync =
            _ => Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>(), writable: false));
        const int chunkSize = 21 * 1024;
        var descriptor = new FileTransferSendDescriptor(
            "post-tuna-fallback-v6-state-refresh-activation-pause.bin",
            256L * chunkSize,
            "transfer_post_tuna_fallback_v6_state_refresh_activation_pause");
        var context = Activator.CreateInstance(
            contextType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [descriptor, openReadStreamAsync],
            culture: null)!;
        var routeSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var bridgePolicyType = serviceType.GetNestedType("FileTransferBridgeRecoveryPolicy", BindingFlags.NonPublic)!;
        SetPrivateProperty(context, "SessionId", "session_post_tuna_fallback_v6_state_refresh_activation_pause");
        SetPrivateProperty(context, "RouteSelection", routeSelection);
        SetPrivateProperty(context, "RouteRuntime", routeSelection.RuntimeDescriptor);
        SetPrivateProperty(context, "BridgeRecoveryPolicy", Enum.Parse(bridgePolicyType, "PostTunaFallbackStrictRecovery"));
        SetPrivateProperty(context, "NegotiatedDataProtocolVersion", FileTransferProtocol.ProtocolVersionV6);
        SetPrivateProperty(context, "State", FileTransferTransferState.Sending);
        SetPrivateProperty(context, "ChunkSizeBytes", chunkSize);
        SetPrivateProperty(context, "ChunkCount", 256);
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullSessionActive", true);
        SetPrivateProperty(context, "PullPostTunaRecoveryActive", true);
        SetPrivateProperty(context, "PullTransportRebindGeneration", 3);
        SetPrivateProperty(context, "RemoteNextExpectedChunkIndex", 100);
        SetPrivateProperty(context, "RemoteGrantedUntilExclusive", 256);
        SetPrivateProperty(context, "ChunksAcceptedForTransport", 256);
        SetPrivateProperty(context, "PullTransportFrontierOnlyRepairActive", true);
        SetPrivateProperty(context, "PullTransportFrontierOnlyRepairStartChunkIndex", 100);
        SetPrivateProperty(context, "PullTransportPaused", true);
        SetPrivateProperty(context, "PullTransportPauseReason", "tuna_activation_negotiating");
        SetPrivateProperty(context, "PullTransportLastPauseReason", "tuna_activation_negotiating");
        SetPrivateProperty(context, "PullV4LastPeerFrameReceivedUtc", DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
        serviceType
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = "session_post_tuna_fallback_v6_state_refresh_activation_pause",
            TransferId = "transfer_post_tuna_fallback_v6_state_refresh_activation_pause",
            TransportEpoch = 3,
            RepairRequestId = "v6-regular-nkn-state-refresh:test-activation-pause",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 100,
                    ChunkCount = 1,
                },
            ],
            Priority = "state_refresh",
            RecoveryMode = "regular_nkn_state_refresh",
        };
        var logStart = GetOperationalLogLength();
        serviceType
            .GetMethod("QueueOutboundV4SparseRuntimeStateRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    new ThrowingDataSession(
                        request.SessionId,
                        request.TransferId,
                        "File-transfer data session is unavailable: tuna_activation_negotiating."),
                    request,
                ]);

        await WaitUntilAsync(
            () => senderTransport.ReceiveRecoveryRequests.Any(recoveryRequest =>
                recoveryRequest.Direction == FileTransferDirection.Outbound &&
                string.Equals(recoveryRequest.TransferId, request.TransferId, StringComparison.Ordinal) &&
                string.Equals(recoveryRequest.Reason, "post_tuna_fallback_state_refresh_failed", StringComparison.Ordinal)),
            timeoutMs: 5000);

        Assert.Contains(
            senderTransport.ReceiveRecoveryRequests,
            recoveryRequest =>
                recoveryRequest.Direction == FileTransferDirection.Outbound &&
                string.Equals(recoveryRequest.TransferId, request.TransferId, StringComparison.Ordinal) &&
                string.Equals(recoveryRequest.Reason, "post_tuna_fallback_state_refresh_failed", StringComparison.Ordinal));
        Assert.False(GetPrivateProperty<bool>(context, "PullTransportPaused"));
        Assert.Null(context.GetType()
            .GetProperty("PullTransportPauseReason", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context));

        var queue = (System.Collections.IEnumerable)contextType
            .GetProperty("PullV4SenderPumpRepairQueue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(context)!;
        Assert.NotEmpty(queue.Cast<object>());
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("reason=post_tuna_fallback_state_refresh_send_failed", log, StringComparison.Ordinal);
        Assert.Contains("pause_reason=tuna_activation_negotiating", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_post_tuna_fallback_state_refresh_tuna_activation_pause_suppressed;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_regular_nkn_state_refresh_send_failed;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_post_tuna_fallback_state_refresh_receive_recovery_requested;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_v6_transport_receive_recovery_request_dispatched; direction=outbound", log, StringComparison.Ordinal);
        Assert.Contains("post_tuna_v6_frontier_sweep=1", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_post_tuna_fallback_state_refresh_deferred_for_tuna_activation;", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileTunaV4_LiveSwitchOffRecovery_DoesNotUseLiveV4RepairAndCompletesSameTransfer()
    {
        const string transferId = "transfer_file_tuna_v4_live_switch_off_hung_repair";
        const string sessionId = "session_file_tuna_v4_live_switch_off_hung_repair";
        var previousTimeout = SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests;
        SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests = TimeSpan.FromMilliseconds(100);
        try
        {
            var logStart = ReadOperationalLogText().Length;
            var payload = Enumerable.Range(0, 768_000).Select(static index => (byte)(index % 233)).ToArray();
            var allowFrontierChunk = 0;
            var delayNextLiveFallbackRepair = 0;
            var delayedLiveFallbackRepairCount = 0;
            using var senderTransport = new LoopbackFileTransferTransport(sessionId);
            using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
            ConfigureFileTunaV4RouteForTest(senderTransport, receiverTransport);
            senderTransport.AllowUnavailableV4FallbackRecoveryFramesForTests = true;
            receiverTransport.AllowUnavailableV4FallbackRecoveryFramesForTests = true;
            senderTransport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
            {
                if (frame is FileTransferChunkBatchFrameV4 { StartChunkIndex: 0 } &&
                    Volatile.Read(ref allowFrontierChunk) == 0)
                {
                    return true;
                }

                if (frame is FileTransferChunkBatchFrameV4 { StartChunkIndex: 0 } &&
                    Interlocked.Exchange(ref delayNextLiveFallbackRepair, 0) == 1)
                {
                    Interlocked.Increment(ref delayedLiveFallbackRepairCount);
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }

                return false;
            };
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            using var receiver = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);
            receiver.AttachTransport(receiverTransport);
            using var destination = new NonDisposingMemoryStream();

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("file-tuna-v4-live-switch-off-hung-repair.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision, timeoutMs: 5000);
            await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);
            await WaitUntilAsync(
                () => receiverTransport.SentDataFrames.OfType<FileTransferStateFrameV4>().Any(static state =>
                    state.MissingRanges.Any(static range =>
                        range.StartChunkIndex == 0 &&
                        range.ChunkCount > 0)),
                timeoutMs: 10000);

            senderTransport.SetConnectedDataSessionsUnavailableForTests(
                "header_switch_off",
                FileTransferTransportHandoffKind.TunaToNormalFallback,
                FileTransferTransportKind.RegularNkn);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "route=post_tuna_fallback_v6; protocol_version=6;",
                    StringComparison.Ordinal),
                timeoutMs: 5000);
            Volatile.Write(ref allowFrontierChunk, 1);
            senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

            await WaitUntilAsync(
                () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                      receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
                timeoutMs: 25000);

            Assert.Equal(payload, destination.ToArray()[..payload.Length]);
            Assert.Equal(0, Volatile.Read(ref delayedLiveFallbackRepairCount));
            Assert.Single(senderTransport.SentOffers.Where(offer => offer.TransferId == transferId));
            Assert.Single(senderTransport.SentSessionOpens.Where(open => open.TransferId == transferId));
            Assert.Empty(senderTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
            Assert.Empty(receiverTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
            var log = ReadOperationalLogTail(logStart);
            Assert.Contains("event=filetransfer_v6_epoch_started; direction=outbound;", log, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_v6_epoch_started; direction=inbound;", log, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_fallback_checkpoint_requested; direction=outbound;", log, StringComparison.Ordinal);
            Assert.Contains("event=filetransfer_fallback_checkpoint_accepted; direction=outbound;", log, StringComparison.Ordinal);
            Assert.Contains("route=post_tuna_fallback_v6", log, StringComparison.Ordinal);
            Assert.True(
                senderTransport.SentDataFrames.Where(frame => frame.TransferId == transferId).Any(FileTransferProtocol.IsV6DataFrame),
                "Expected same-transfer fallback V6 sender frames.");
            Assert.True(
                receiverTransport.SentDataFrames.Where(frame => frame.TransferId == transferId).Any(FileTransferProtocol.IsV6DataFrame),
                "Expected same-transfer fallback V6 receiver frames.");
            Assert.Contains(
                receiverTransport.SentDataFrames.Where(frame => frame.TransferId == transferId),
                static frame => frame is FileTransferReceiverStateFrameV6);
            Assert.DoesNotContain("event=filetransfer_post_tuna_fallback_cleanup_completed; direction=outbound;", log, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_live_v4_fallback_cleanup_completed; direction=outbound;", log, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_sender_started;", log, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_v6_receiver_started;", log, StringComparison.Ordinal);
        }
        finally
        {
            SessionFileTransferService.V6RegularNknSparseRuntimeV4TransportSendTimeoutOverrideForTests = previousTimeout;
        }
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V4Sender_TransportRebind_RearmsSafetyReplayWhenNoFeedbackArrives()
    {
        const string transferId = "transfer_v4_sender_transport_rebind_rearm";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_transport_rebind_rearm");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_transport_rebind_rearm");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-transport-rebind-rearm.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count(static batch => batch.StartChunkIndex == 0) == 1,
            timeoutMs: 5000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await WaitUntilAsync(
            () => CountOccurrences(
                ReadOperationalLogTail(logStart),
                "event=filetransfer_transport_rebind_safety_replay_started;") >= 2,
            timeoutMs: 9000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_rebind_safety_replay_rearmed;", log, StringComparison.Ordinal);
        Assert.Contains("reason=post_fallback_sender_wait", log, StringComparison.Ordinal);
        Assert.True(
            senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Count(static batch =>
                    batch.StartChunkIndex == 0 &&
                    batch.BatchProfile == "v4_repair_21k" &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant) >= 2);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V4Sender_PostRebindZeroCredit_EmergencyReplaysExactFrontier()
    {
        const string transferId = "transfer_v4_sender_rebind_zero_credit_emergency";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_rebind_zero_credit_emergency");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_rebind_zero_credit_emergency");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-rebind-zero-credit-emergency.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 0,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        senderTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Any(static batch =>
                    batch.StartChunkIndex == 0 &&
                    batch.ChunkCount == 1 &&
                    batch.BatchProfile == "v4_repair_21k" &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_transport_rebind_emergency_frontier_replay;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_safety_replay_started;", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_escalation_reason=transport_rebind_emergency_frontier", log, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V4Sender_PostRebindDuplicateFrontierState_ReplaysOnlyExactFrontier()
    {
        const string transferId = "transfer_v4_sender_rebind_frontier_only";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_rebind_frontier_only");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_rebind_frontier_only");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-rebind-frontier-only.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0), timeoutMs: 5000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_rebind_generation_started; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);

        var missingFrontierState = new FileTransferReceiverStateFrameV6
        {
            SessionId = offer.SessionId,
            TransferId = transferId,
            Epoch = 2,
                ContiguousCommittedChunkIndex = 4,
                DurableReceivedHighestChunkIndex = 16,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 4, ChunkCount = 32 }],
                BytesCommitted = 4 * 21 * 1024,
            };
        await receiverSession.SendAsync(missingFrontierState, CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Any(static batch =>
                    batch.StartChunkIndex == 4 &&
                    batch.ChunkCount == 1 &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var batchCountBeforeDuplicate = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        var duplicateStart = ReadOperationalLogText().Length;
        await receiverSession.SendAsync(missingFrontierState, CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(duplicateStart).Contains("event=filetransfer_transport_rebind_frontier_only_replay;", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforeDuplicate)
                .Any(static batch =>
                    batch.StartChunkIndex == 4 &&
                    batch.ChunkCount == 1 &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var fullLog = ReadOperationalLogTail(logStart);
        var duplicateLog = ReadOperationalLogTail(duplicateStart);
        Assert.Contains("event=filetransfer_transport_rebind_frontier_only_started;", fullLog, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_frontier_only_replay;", duplicateLog, StringComparison.Ordinal);
        Assert.Contains("replay_start_chunk_index=4; replay_end_chunk_exclusive=5; requested_chunk_count=1", duplicateLog, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_escalation_reason=transport_rebind_frontier_only", duplicateLog, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredPostTunaFallbackSparseRuntimeSkip)]
    public async Task V4Sender_PostFallbackSparseFrontierProofPreservesBackfillRepairRange()
    {
        const string transferId = "transfer_v4_sender_post_fallback_sparse_frontier_backfill";
        const string sessionId = "session_v4_sender_post_fallback_sparse_frontier_backfill";
        var logStart = GetOperationalLogLength();
        var payload = Enumerable.Range(0, 2_000_000).Select(static index => (byte)(index % 251)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId)
        {
            FileTransferTransportProfileKind = FileTransferTransportProfileKind.ConservativeNknStartup,
        };
        ConfigurePostTunaFallbackV6RouteForTest(senderTransport, receiverTransport);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-post-fallback-sparse-frontier-backfill.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex >= 48),
            timeoutMs: 5000);

        senderTransport.RequestAllDataSessionHandoffs(
            "normal_to_tuna_activation",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna);
        await WaitUntilAsync(() => senderTransport.SentTransportEpochs.Any(), timeoutMs: 5000);

        var batchCountBeforeProof = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 24,
                DurableReceivedHighestChunkIndex = 48,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 24, ChunkCount = 32 }],
                BytesCommitted = 24 * 21 * 1024,
                TransportEpoch = 1,
                RepairRequestId = "v6-state-frontier:1:post-fallback-backfill",
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforeProof)
                .Any(static batch =>
                    batch.StartChunkIndex == 24 &&
                    batch.ChunkCount > 1 &&
                    batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v4_repair_scheduled;", log, StringComparison.Ordinal);
        Assert.Contains("repair_request_key=24:32:24:32", log, StringComparison.Ordinal);
        Assert.Contains("requested_chunk_count=32", log, StringComparison.Ordinal);
        Assert.DoesNotContain("repair_request_key=24:1:24:1", log, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V6FallbackSender_HandoffRecoveryUnblocksTailAndIgnoresStaleRecoveryFrames()
    {
        const string transferId = "transfer_v6_fallback_handoff_tail_unblock";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 1_024_000).Select(static index => (byte)(index % 239)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_handoff_tail_unblock");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_handoff_tail_unblock");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-handoff-tail-unblock.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0), timeoutMs: 5000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_rebind_generation_started; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 8,
                DurableReceivedHighestChunkIndex = 7,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 8 * 21 * 1024,
                TransportEpoch = 1,
            },
            CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 3,
                ContiguousCommittedChunkIndex = 16,
                DurableReceivedHighestChunkIndex = 15,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 16 * 21 * 1024,
                TransportEpoch = 1,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_handoff_tail_unblocked; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Any(static batch =>
                    batch.StartChunkIndex >= 16 &&
                    batch.TransportEpoch == 0 &&
                    batch.RepairRequestId is null &&
                    batch.RecoveryMode is null),
            timeoutMs: 5000);

        var staleStart = ReadOperationalLogText().Length;
        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                TransportEpoch = 1,
                RepairRequestId = "stale-after-recovered",
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 8, ChunkCount = 1 }],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(staleStart).Contains("reason=recovered_epoch", StringComparison.Ordinal),
            timeoutMs: 5000);

        var staleLog = ReadOperationalLogTail(staleStart);
        Assert.Contains("event=filetransfer_v6_recovery_frame_ignored; direction=outbound;", staleLog, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_tail_blocked_until_frontier_proof; direction=outbound;", staleLog, StringComparison.Ordinal);

        var reopenedStart = ReadOperationalLogText().Length;
        var batchCountBeforeReopen = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                TransportEpoch = 1,
                RepairRequestId = "frontier-after-recovered",
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 16, ChunkCount = 1 }],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(reopenedStart).Contains("event=filetransfer_v6_handoff_epoch_reopened; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforeReopen)
                .Any(static batch =>
                    batch.StartChunkIndex == 16 &&
                    batch.TransportEpoch == 1 &&
                    string.Equals(batch.RecoveryMode, "frontier_repair_only", StringComparison.Ordinal)),
            timeoutMs: 5000);

        var reopenedLog = ReadOperationalLogTail(reopenedStart);
        Assert.Contains("event=filetransfer_v6_frontier_repair_requested; direction=outbound;", reopenedLog, StringComparison.Ordinal);
        Assert.DoesNotContain("reason=recovered_epoch", reopenedLog, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V6FallbackSender_HandoffSparseBackfillKeepsTailBlockedAndSendsRepairWindow()
    {
        const string transferId = "transfer_v6_fallback_sparse_backfill_window";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 1_024_000).Select(static index => (byte)(index % 237)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_sparse_backfill_window");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_sparse_backfill_window");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-sparse-backfill-window.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0), timeoutMs: 5000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_rebind_generation_started; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);

        var batchCountBeforeSparseProof = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count();
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 8,
                DurableReceivedHighestChunkIndex = 31,
                CreditUntilChunkIndexExclusive = 64,
                MissingRanges = [],
                BytesCommitted = 8 * 21 * 1024,
                TransportEpoch = 1,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("reason=frontier_proof_with_sparse_backfill", StringComparison.Ordinal),
            timeoutMs: 5000);
        var sparseProofLog = ReadOperationalLogTail(logStart);
        Assert.DoesNotContain("event=filetransfer_v6_handoff_tail_unblocked; direction=outbound;", sparseProofLog, StringComparison.Ordinal);

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                TransportEpoch = 1,
                RepairRequestId = "backfill-window",
                Priority = "backfill",
                RecoveryMode = "backfill_repair",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 8, ChunkCount = 12 }],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames
                .OfType<FileTransferChunkBatchFrameV6>()
                .Skip(batchCountBeforeSparseProof)
                .Any(static batch =>
                    batch.StartChunkIndex == 8 &&
                    batch.ChunkCount > 1 &&
                    batch.TransportEpoch == 1 &&
                    string.Equals(batch.RepairRequestId, "backfill-window", StringComparison.Ordinal) &&
                    string.Equals(batch.RecoveryMode, "backfill_repair", StringComparison.Ordinal)),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_frontier_repair_requested; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("priority=backfill", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v6_handoff_tail_unblocked; direction=outbound;", log, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V6FallbackSender_DuplicateTargetHandoffReusesEpochAndAcceptsPeerRepair()
    {
        const string transferId = "transfer_v6_fallback_duplicate_handoff_epoch";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 1_024_000).Select(static index => (byte)(index % 233)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v6_fallback_duplicate_handoff_epoch");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v6_fallback_duplicate_handoff_epoch");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v6-fallback-duplicate-handoff-epoch.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 8,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.StartChunkIndex == 0), timeoutMs: 5000);

        senderTransport.RequestAllDataSessionHandoffs(
            "header_switch_off",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch => batch.TransportEpoch > 0),
            timeoutMs: 5000);
        var handoffEpoch = senderTransport.SentDataFrames
            .OfType<FileTransferChunkBatchFrameV6>()
            .First(static batch => batch.TransportEpoch > 0)
            .TransportEpoch;

        senderTransport.RequestAllDataSessionHandoffs(
            "header_switch_off_duplicate_ready",
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            FileTransferTransportKind.RegularNkn);
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v6_handoff_epoch_reused; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferFrontierRequestFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                TransportEpoch = handoffEpoch,
                RepairRequestId = "duplicate-target-frontier",
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains($"event=filetransfer_v6_frontier_repair_requested; direction=outbound; transfer_id={transferId};", StringComparison.Ordinal) &&
                  ReadOperationalLogTail(logStart).Contains($"transport_epoch={handoffEpoch}", StringComparison.Ordinal),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v6_handoff_epoch_reused; direction=outbound;", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"reason=stale_or_mismatched_epoch; frame_transport_epoch={handoffEpoch}", log, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V4FileOnlyTransfer_TransportRebindSafetyReplay_FillsDroppedFrontierAndCompletes()
    {
        const string transferId = "transfer_v4_transport_rebind_e2e";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 768_000).Select(static index => (byte)(index % 229)).ToArray();
        var allowFrontierChunk = 0;
        var droppedFrontierBatchCount = 0;
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_transport_rebind_e2e");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_transport_rebind_e2e");
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferChunkBatchFrameV6 { StartChunkIndex: 0 } &&
                Volatile.Read(ref allowFrontierChunk) == 0)
            {
                Interlocked.Increment(ref droppedFrontierBatchCount);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        using var destination = new NonDisposingMemoryStream();

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-transport-rebind-e2e.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => receiver.Snapshot.Inbound?.State == FileTransferTransferState.PendingDecision);
        await receiver.AcceptIncomingTransferAsync(transferId, (_, _) => Task.FromResult<Stream>(destination), CancellationToken.None);

        await WaitUntilAsync(
            () => receiverTransport.SentDataFrames.OfType<FileTransferReceiverStateFrameV6>().Any(static state =>
                state.MissingRanges.Any(static range =>
                    range.StartChunkIndex == 0 &&
                    range.ChunkCount > 0)),
            timeoutMs: 10000);
        Assert.True(Volatile.Read(ref droppedFrontierBatchCount) > 0);

        Volatile.Write(ref allowFrontierChunk, 1);
        senderTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");

        await WaitUntilAsync(
            () => sender.Snapshot.Outbound?.State == FileTransferTransferState.Completed &&
                  receiver.Snapshot.Inbound?.State == FileTransferTransferState.Completed,
            timeoutMs: 20000);

        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_transport_rebind_generation_started; direction=outbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_generation_started; direction=inbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_state_forced; direction=inbound;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_safety_replay_started;", log, StringComparison.Ordinal);
        Assert.Contains("event=filetransfer_transport_rebind_recovered; direction=inbound;", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_SuppressesDuplicateMissingRangeRepairKeys()
    {
        const string transferId = "transfer_v4_sender_repair_dedupe";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_dedupe");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_dedupe");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-repair-dedupe.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        var repairState = new FileTransferReceiverStateFrameV6
        {
            SessionId = offer.SessionId,
            TransferId = transferId,
            Epoch = 2,
            ContiguousCommittedChunkIndex = 0,
            DurableReceivedHighestChunkIndex = 8,
            CreditUntilChunkIndexExclusive = 16,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
            BytesCommitted = 0,
        };
        await receiverSession.SendAsync(repairState, CancellationToken.None);
        await receiverSession.SendAsync(repairState, CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_repair_dedupe", StringComparison.Ordinal), timeoutMs: 5000);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_state_received; transfer_id=transfer_v4_sender_repair_dedupe; session_id=session_v4_sender_repair_dedupe; epoch=2; previous_epoch=2; duplicate=1; applied=0", StringComparison.Ordinal), timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Equal(1, CountOccurrences(log, "event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_repair_dedupe"));
        Assert.Contains("duplicate=1; applied=0", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_v4_repair_suppressed; transfer_id=transfer_v4_sender_repair_dedupe", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_RetriedMissingRange_EscalatesRepairDelivery()
    {
        const string transferId = "transfer_v4_sender_repair_escalates";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 227)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_escalates");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_escalates");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-repair-escalates.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        var repairState = new FileTransferReceiverStateFrameV6
        {
            SessionId = offer.SessionId,
            TransferId = transferId,
            Epoch = 2,
            ContiguousCommittedChunkIndex = 0,
            DurableReceivedHighestChunkIndex = 8,
            CreditUntilChunkIndexExclusive = 16,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
            BytesCommitted = 0,
        };

        await receiverSession.SendAsync(repairState, CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                batch.BatchProfile == "v4_repair_21k" &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.BulkOnly),
            timeoutMs: 5000);

        await Task.Delay(900);
        await receiverSession.SendAsync(repairState with { Epoch = 3 }, CancellationToken.None);

        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static batch =>
                batch.BatchProfile == "v4_repair_21k" &&
                batch.RepairDeliveryMode == FileTransferV4RepairDeliveryMode.ControlBulkRedundant),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("repair_delivery_mode=control_bulk_escalated", log, StringComparison.Ordinal);
        Assert.Contains("repair_delivery_escalation_reason=frontier_not_advanced", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_MaxBatchSegmentsEnvironment_CapsNormalBatches()
    {
        const string transferId = "transfer_v4_sender_batch_cap";
        const string envName = "NLINK_FILETRANSFER_V4_MAX_BATCH_SEGMENTS";
        var previousValue = Environment.GetEnvironmentVariable(envName);
        using var unsafeDeveloperMode = EnableUnsafeDeveloperModeForTests();
        Environment.SetEnvironmentVariable(envName, "2");
        try
        {
            var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 241)).ToArray();
            using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_batch_cap");
            using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_batch_cap");
            senderTransport.Connect(receiverTransport);
            using var sender = new SessionFileTransferService();
            sender.AttachTransport(senderTransport);

            await sender.TryStartSendAsync(
                new FileTransferSendDescriptor("v4-batch-cap.bin", payload.Length, transferId),
                _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
            var offer = senderTransport.SentOffers.Single();
            await receiverTransport.SendFileTransferAcceptAsync(
                new FileTransferAcceptV1
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
                },
                CancellationToken.None);
            await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
            await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

            var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
            await receiverSession.SendAsync(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = offer.SessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 0,
                    DurableReceivedHighestChunkIndex = -1,
                    CreditUntilChunkIndexExclusive = 16,
                    MissingRanges = [],
                    BytesCommitted = 0,
                },
                CancellationToken.None);

            await WaitUntilAsync(
                () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Sum(static batch => batch.DataSegments.Count) >= 12,
                timeoutMs: 5000);

            var batches = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().ToArray();
            Assert.NotEmpty(batches);
            Assert.All(batches, static batch => Assert.InRange(batch.DataSegments.Count, 1, 2));
            Assert.Contains(batches, static batch => batch.DataSegments.Count == 2 && batch.BatchProfile == "v4_default_21k_2x");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previousValue);
        }
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_SuppressesSameMissingRange_WhenDurableHighestAdvances()
    {
        const string transferId = "transfer_v4_sender_repair_stable_key";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 229)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_stable_key");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_repair_stable_key");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-repair-stable-key.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_repair_stable_key", StringComparison.Ordinal), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 3,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 12,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_repair_suppressed; transfer_id=transfer_v4_sender_repair_stable_key", StringComparison.Ordinal), timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Equal(1, CountOccurrences(log, "event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_repair_stable_key"));
        Assert.Contains("event=filetransfer_v4_repair_suppressed; transfer_id=transfer_v4_sender_repair_stable_key", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_SuppressesRepair_FromStaleSameFrontierMissingRanges()
    {
        const string transferId = "v4_stale_same";
        const string sessionId = "session_v4_stale_same";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 211)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-stale-missing-range.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 10,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 9,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains($"event=filetransfer_v4_stale_state_missing_ranges_suppressed; transfer_id={transferId}", StringComparison.Ordinal), timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains($"event=filetransfer_v4_state_received; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("stale=1; applied=0", log, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_v4_stale_state_missing_ranges_suppressed; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("reason=stale_epoch", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_stale_state_missing_ranges_applied; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_TreatsSameEpochReceiverStateAsDuplicate()
    {
        const string transferId = "v4_duplicate_epoch";
        const string sessionId = "session_v4_duplicate_epoch";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 211)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-duplicate-epoch.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 10,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 10,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 1,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
                TransferPaused = true,
                TransferPauseReason = "duplicate_pause",
            },
            CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id={sessionId}; epoch=10; previous_epoch=10; duplicate=1; applied=0", StringComparison.Ordinal), timeoutMs: 5000);
        await Task.Delay(250);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains($"event=filetransfer_v4_state_received; transfer_id={transferId}; session_id={sessionId}; epoch=10; previous_epoch=10; duplicate=1; applied=0", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.False(sender.Snapshot.Outbound!.IsPeerPaused);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_SuppressesRepair_FromStaleMovedFrontierMissingRanges()
    {
        const string transferId = "v4_stale_moved";
        const string sessionId = "session_v4_stale_moved";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 256_000).Select(static index => (byte)(index % 211)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-stale-moved-frontier-missing-range.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 10,
                ContiguousCommittedChunkIndex = 1,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [],
                BytesCommitted = 21 * 1024,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 9,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 8,
                CreditUntilChunkIndexExclusive = 16,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains($"event=filetransfer_v4_stale_state_missing_ranges_suppressed; transfer_id={transferId}", StringComparison.Ordinal), timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains($"event=filetransfer_v4_state_received; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("stale=1; applied=0", log, StringComparison.Ordinal);
        Assert.Contains($"event=filetransfer_v4_stale_state_missing_ranges_suppressed; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("reason=frontier_moved", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_stale_state_missing_ranges_applied; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact(Skip = DeferredV6TransportEpochRuntimeSkip)]
    public async Task V4Sender_PostRebind_SuppressesStaleRepairStateBehindCurrentFrontier()
    {
        const string transferId = "v4_post_rebind_stale_behind";
        const string sessionId = "session_v4_post_rebind_stale_behind";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 211)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-post-rebind-stale-behind.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static frame => frame.StartChunkIndex == 0), timeoutMs: 5000);

        senderTransport.SetConnectedDataSessionsUnavailableForTests("transport_rebind");
        senderTransport.SetConnectedDataSessionsAvailableForTests("transport_recovered");
        await WaitUntilAsync(
            () => ReadOperationalLogTail(logStart).Contains("event=filetransfer_transport_rebind_generation_started; direction=outbound;", StringComparison.Ordinal),
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 4,
                DurableReceivedHighestChunkIndex = 16,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges = [],
                BytesCommitted = 4 * 21 * 1024,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains($"contiguous_committed_chunk_index=4", StringComparison.Ordinal), timeoutMs: 5000);
        var staleStart = ReadOperationalLogText().Length;

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 16,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 12 }],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(
            () => ReadOperationalLogTail(staleStart).Contains($"event=filetransfer_v4_stale_state_missing_ranges_suppressed; transfer_id={transferId}", StringComparison.Ordinal),
            timeoutMs: 5000);

        var log = ReadOperationalLogTail(staleStart);
        Assert.Contains($"event=filetransfer_v4_stale_state_missing_ranges_suppressed; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.Contains("reason=frontier_moved", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_rebind_duplicate_state_repair_enqueued; transfer_id={transferId}", log, StringComparison.Ordinal);
        Assert.DoesNotContain($"event=filetransfer_v4_repair_scheduled; transfer_id={transferId}", log, StringComparison.Ordinal);
    }

    [Fact(Skip = RetiredV4CreditRepairRuntimeSkip)]
    public async Task V4Sender_PrioritizesFrontierMissingRange_AndKeepsFarHoles()
    {
        const string transferId = "transfer_v4_sender_frontier_first";
        var logStart = ReadOperationalLogText().Length;
        var payload = Enumerable.Range(0, 512_000).Select(static index => (byte)(index % 223)).ToArray();
        using var senderTransport = new LoopbackFileTransferTransport("session_v4_sender_frontier_first");
        using var receiverTransport = new LoopbackFileTransferTransport("session_v4_sender_frontier_first");
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);

        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor("v4-frontier-first.bin", payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentOffers.TryPeek(out _), timeoutMs: 5000);
        var offer = senderTransport.SentOffers.Single();
        await receiverTransport.SendFileTransferAcceptAsync(
            new FileTransferAcceptV1
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV6,
            },
            CancellationToken.None);
        await WaitUntilAsync(() => senderTransport.SentSessionOpens.Any(), timeoutMs: 5000);
        await WaitUntilAsync(() => senderTransport.SentDataFrames.OfType<FileTransferManifestFrameV6>().Any(), timeoutMs: 5000);

        var receiverSession = await receiverTransport.OpenFileTransferDataSessionAsync(offer.SessionId, transferId, CancellationToken.None);
        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = -1,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges = [],
                BytesCommitted = 0,
            },
            CancellationToken.None);
        await WaitUntilAsync(
            () => senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Any(static frame =>
                frame.StartChunkIndex <= 10 &&
                frame.StartChunkIndex + frame.ChunkCount > 10),
            timeoutMs: 5000);

        await receiverSession.SendAsync(
            new FileTransferReceiverStateFrameV6
            {
                SessionId = offer.SessionId,
                TransferId = transferId,
                Epoch = 2,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 20,
                CreditUntilChunkIndexExclusive = 24,
                MissingRanges =
                [
                    new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 3 },
                    new FileTransferRangeV4 { StartChunkIndex = 10, ChunkCount = 4 },
                ],
                BytesCommitted = 0,
            },
            CancellationToken.None);

        await WaitUntilAsync(() => ReadOperationalLogTail(logStart).Contains("event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_frontier_first", StringComparison.Ordinal), timeoutMs: 5000);

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("event=filetransfer_v4_repair_scheduled; transfer_id=transfer_v4_sender_frontier_first", log, StringComparison.Ordinal);
        Assert.Contains("range_count=2; requested_chunk_count=7; scheduled_chunk_count=7", log, StringComparison.Ordinal);
        Assert.Contains("first_start_chunk_index=0; last_end_chunk_exclusive=14", log, StringComparison.Ordinal);
        Assert.DoesNotContain("filetransfer.request_chunks.v2", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V4Sender_PostRebindMissingRanges_SchedulesExactFrontierRepairOnly()
    {
        await Task.CompletedTask;
        var method = typeof(SessionFileTransferService).GetMethod(
            "SelectV4RepairRangesForSend",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var ranges = new[]
        {
            new FileTransferRangeV4 { StartChunkIndex = 16, ChunkCount = 3 },
            new FileTransferRangeV4 { StartChunkIndex = 30, ChunkCount = 4 },
        };

        var selected = Assert.IsAssignableFrom<IReadOnlyList<FileTransferRangeV4>>(
            method!.Invoke(null, [ranges, 16, FileTransferProtocol.MaxStateMissingChunksV4, true]));

        var range = Assert.Single(selected);
        Assert.Equal(16, range.StartChunkIndex);
        Assert.Equal(3, range.ChunkCount);
    }

    private static void ConfigureDiagnosticRegularNknV6RouteForTest(
        LoopbackFileTransferTransport senderTransport,
        LoopbackFileTransferTransport receiverTransport)
    {
        senderTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
        receiverTransport.IsDiagnosticRegularNknV6RouteEnabled = true;
    }

    private static void ConfigureFileTunaV4RouteForTest(
        LoopbackFileTransferTransport senderTransport,
        LoopbackFileTransferTransport receiverTransport)
    {
        senderTransport.IsFileTunaActiveForRouteSelection = true;
        receiverTransport.IsFileTunaActiveForRouteSelection = true;
    }

    private static void ConfigurePostTunaFallbackV6RouteForTest(
        LoopbackFileTransferTransport senderTransport,
        LoopbackFileTransferTransport receiverTransport)
    {
        senderTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
        receiverTransport.IsPostTunaFileFallbackActiveForRouteSelection = true;
    }

    private static async Task RunCompletedLoopbackTransferAsync(
        SessionFileTransferService sender,
        SessionFileTransferService receiver,
        string transferId,
        string fileName)
    {
        var payload = Enumerable.Range(0, 128_000).Select(static index => (byte)(index % 251)).ToArray();
        using var destination = new NonDisposingMemoryStream();
        await sender.TryStartSendAsync(
            new FileTransferSendDescriptor(fileName, payload.Length, transferId),
            _ => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)),
            CancellationToken.None);
        await WaitUntilAsync(
            () => receiver.Snapshot.Inbound is { } inbound &&
                  inbound.TransferId == transferId &&
                  inbound.State == FileTransferTransferState.PendingDecision,
            timeoutMs: 5000);
        await receiver.AcceptIncomingTransferAsync(
            transferId,
            (_, _) => Task.FromResult<Stream>(destination),
            CancellationToken.None);
        await WaitUntilAsync(
            () => sender.Snapshot.Outbound is { } outbound &&
                  receiver.Snapshot.Inbound is { } inbound &&
                  outbound.TransferId == transferId &&
                  outbound.State == FileTransferTransferState.Completed &&
                  inbound.TransferId == transferId &&
                  inbound.State == FileTransferTransferState.Completed,
            timeoutMs: 20_000);
        Assert.Equal(payload, destination.ToArray()[..payload.Length]);
    }

    private static string ReadV4SenderLogSnapshot(int logStart)
        => ReadOperationalLogTail(logStart) + Environment.NewLine + LocalOperationalLog.GetRecentLogText();

    private sealed class ThrowingDataSession(
        string sessionId,
        string transferId,
        string failureMessage = "Injected state refresh send failure.") : IFileTransferDataSession
    {
        public string SessionId { get; } = sessionId;

        public string TransferId { get; } = transferId;

        public bool IsAvailable => true;

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged
        {
            add { }
            remove { }
        }

        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
            => Task.FromException(new InvalidOperationException(failureMessage));

        public void Dispose()
        {
        }
    }

    private sealed class BlockingDataSession(string sessionId, string transferId) : IFileTransferDataSession
    {
        private readonly TaskCompletionSource sendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string SessionId { get; } = sessionId;

        public string TransferId { get; } = transferId;

        public bool IsAvailable => true;

        public TaskCompletionSource SendStarted => sendStarted;

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged
        {
            add { }
            remove { }
        }

        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public async Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
        {
            sendStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        public void Dispose()
        {
        }
    }

    private sealed class CompletingDataSession(string sessionId, string transferId) : IFileTransferDataSession
    {
        public string SessionId { get; } = sessionId;

        public string TransferId { get; } = transferId;

        public bool IsAvailable => true;

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged
        {
            add { }
            remove { }
        }

        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
            => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static string FilterV4SenderTransferLog(string logText, string transferId)
        => string.Join(
            Environment.NewLine,
            logText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("transfer_id=" + transferId, StringComparison.Ordinal)));

    private static void AssertLiveRouteEpochProofSequence(
        string log,
        params (int Epoch, string PreviousRoute, string Route, int ProtocolVersion, string HandoffKind, string TargetTransport)[] expectations)
    {
        var lines = log.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var cursor = -1;
        foreach (var expectation in expectations)
        {
            var startedIndex = Array.FindIndex(
                lines,
                cursor + 1,
                line =>
                    line.Contains("event=filetransfer_live_route_epoch_started;", StringComparison.Ordinal) &&
                    line.Contains("live_route_epoch=" + expectation.Epoch + ";", StringComparison.Ordinal) &&
                    line.Contains("previous_route=" + expectation.PreviousRoute + ";", StringComparison.Ordinal) &&
                    line.Contains("route=" + expectation.Route + "; protocol_version=" + expectation.ProtocolVersion, StringComparison.Ordinal) &&
                    line.Contains("handoff_kind=" + expectation.HandoffKind, StringComparison.Ordinal) &&
                    line.Contains("target_transport=" + expectation.TargetTransport, StringComparison.Ordinal));
            Assert.True(
                startedIndex >= 0,
                "Missing ordered live-route started proof for epoch " + expectation.Epoch + "." + Environment.NewLine + log);

            var recoveredIndex = Array.FindIndex(
                lines,
                startedIndex + 1,
                line =>
                    line.Contains("event=filetransfer_live_route_epoch_recovered;", StringComparison.Ordinal) &&
                    line.Contains("live_route_epoch=" + expectation.Epoch + ";", StringComparison.Ordinal) &&
                    line.Contains("route=" + expectation.Route + "; protocol_version=" + expectation.ProtocolVersion, StringComparison.Ordinal) &&
                    line.Contains("handoff_kind=" + expectation.HandoffKind, StringComparison.Ordinal) &&
                    line.Contains("target_transport=" + expectation.TargetTransport, StringComparison.Ordinal));
            Assert.True(
                recoveredIndex > startedIndex,
                "Missing ordered live-route recovered proof for epoch " + expectation.Epoch + "." + Environment.NewLine + log);

            cursor = recoveredIndex;
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static object[] GetQueuedV4RepairSends(object context)
    {
        var queue = context.GetType()
            .GetProperty("PullV4SenderPumpRepairQueue", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(context);
        return ((System.Collections.IEnumerable)queue!).Cast<object>().ToArray();
    }

    private static int GetIntProperty(object target, string propertyName)
    {
        var value = target.GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(target);
        return Assert.IsType<int>(value);
    }

    private static string GetStringProperty(object target, string propertyName)
    {
        var value = target.GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(target);
        return Assert.IsType<string>(value);
    }

    private static async Task<TFrame> ReceiveDataFrameOfTypeAsync<TFrame>(IFileTransferDataSession session)
        where TFrame : FileTransferDataFrame
    {
        using var cts = new CancellationTokenSource(5000);
        while (true)
        {
            var received = await session.ReceiveWithMetadataAsync(cts.Token);
            if (received.Frame is TFrame frame)
            {
                return frame;
            }
        }
    }
}
