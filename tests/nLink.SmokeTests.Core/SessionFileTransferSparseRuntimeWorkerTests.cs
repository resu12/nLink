using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using NLink.Core.FileTransfer;
using NLink.Core.Logging;
using Xunit;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackTailUnblockRecheck")]
    public void FallbackTailUnblockRecheck_V6PeerRepairSuccessorCarriesHeldRequestAndRechecksOnce()
    {
        const string transferId =
            "transfer_fallback_tail_v6_peer_successor_recheck";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport(
            $"session_{transferId}");
        service.AttachTransport(transport);
        var serviceType = typeof(SessionFileTransferService);
        var context = CreatePostTunaFallbackV6OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 128,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 256,
            chunkCount: 256);
        var contextType = context.GetType();
        serviceType
            .GetField(
                "outboundTransfer",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var leg = Assert.IsType<FileTransferLeg>(
            GetPrivatePropertyValue(context, "CurrentTransferLeg"));
        leg.State = FileTransferLegState.Active;
        leg.CanSendData = true;
        leg.CheckpointRequestId = null;
        SeedPairedFallbackTailYieldForTest(context, leg);
        var retryRequest = Assert.IsType<RuntimeUnlockRetryDispatchAuthorityRequest>(
            GetCoordinatorFallbackTailYieldSubstate(context)
                .PendingRetryRecheckRequest);

        var applyPeerRequest = serviceType.GetMethod(
            "ApplyOutboundV6FrontierRequest",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var firstRequest = CreateRequest(
            "v6-frontier:3:128:17",
            "frq-v6-peer-successor-17",
            chunkCount: 1);
        applyPeerRequest.Invoke(
            service,
            [context, firstRequest, FileTransferTransportKind.RegularNkn]);
        var predecessor = GetPrivatePropertyValue(
            context,
            "CurrentFallbackFrontierRepairContract");
        Assert.NotNull(predecessor);
        var predecessorAuthority = GetPrivateProperty<
            FallbackFrontierRepairAuthority>(predecessor!, "Authority");

        var authorizeRetry = serviceType
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method =>
            {
                if (!string.Equals(
                        method.Name,
                        "AuthorizeRuntimeUnlockRetryDispatchLocked",
                        StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 2 &&
                       parameters[0].ParameterType == contextType &&
                       parameters[1].ParameterType ==
                           typeof(RuntimeUnlockRetryDispatchAuthorityRequest);
            });
        var heldDecision = Assert.IsType<RuntimeUnlockRetryDispatchDecision>(
            authorizeRetry.Invoke(null, [context, retryRequest]));
        Assert.Equal(
            RuntimeUnlockRetryDispatchDecisionStatus.Held,
            heldDecision.Status);
        var initialHold = Assert.IsType<
            FileTransferFallbackTailYieldRetryDispatchHold>(
                GetCoordinatorFallbackTailYieldSubstate(context)
                    .CurrentRetryDispatchHold);
        Assert.Equal(predecessorAuthority, initialHold.BlockingRepair);
        Assert.Equal(1, initialHold.HoldGeneration);

        var logStart = GetOperationalLogLength();
        var successorRequest = CreateRequest(
            "v6-frontier:3:128:18",
            "frq-v6-peer-successor-18",
            chunkCount: 2);
        applyPeerRequest.Invoke(
            service,
            [context, successorRequest, FileTransferTransportKind.RegularNkn]);
        var successor = GetPrivatePropertyValue(
            context,
            "CurrentFallbackFrontierRepairContract");
        Assert.NotNull(successor);
        Assert.NotSame(predecessor, successor);
        var successorAuthority = GetPrivateProperty<
            FallbackFrontierRepairAuthority>(successor!, "Authority");
        Assert.Equal(
            predecessorAuthority.AuthorityId,
            successorAuthority.PredecessorAuthorityId);
        var carriedHold = Assert.IsType<
            FileTransferFallbackTailYieldRetryDispatchHold>(
                GetCoordinatorFallbackTailYieldSubstate(context)
                    .CurrentRetryDispatchHold);
        Assert.Equal(initialHold.Request, carriedHold.Request);
        Assert.Equal(initialHold.Release, carriedHold.Release);
        Assert.Equal(
            initialHold.DataSessionIdentity,
            carriedHold.DataSessionIdentity);
        Assert.Equal(
            initialHold.DataSessionGeneration,
            carriedHold.DataSessionGeneration);
        Assert.Equal(successorAuthority, carriedHold.BlockingRepair);
        Assert.Equal(2, carriedHold.HoldGeneration);
        Assert.Null(
            GetCoordinatorFallbackTailYieldSubstate(context)
                .CurrentReservation);

        var settleProgress = serviceType.GetMethod(
            "TrySettleOutboundFallbackFrontierProgressLocked",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        settleProgress.Invoke(
            service,
            [context, 128, 129, "unit_test_successor_progress"]);
        var settled = GetCoordinatorFallbackTailYieldSubstate(context);
        Assert.Equal(1, settled.LastRetryDispatchRecheckGeneration);
        var receipt = Assert.IsType<
            FileTransferFallbackTailYieldRetryDispatchRecheckReceipt>(
                settled.LastRetryDispatchRecheckReceipt);
        Assert.Equal(
            FileTransferFallbackTailYieldRetryDispatchRecheckReceiptStatus
                .Rejected,
            receipt.Status);
        Assert.Equal(carriedHold, receipt.Command.Hold);
        Assert.Equal(
            successorAuthority,
            receipt.Command.Observation.SettledRepair);

        var queuePairedRetry = serviceType
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method =>
            {
                if (!string.Equals(
                        method.Name,
                        "TryQueueFallbackPairedTailYieldRuntimeUnlockRetryDispatchLocked",
                        StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 3 &&
                       parameters[0].ParameterType == contextType;
            });
        queuePairedRetry.Invoke(
            service,
            [context, carriedHold.Release, "unit_test_ordinary_queue_after_rejected_recheck"]);
        var afterOrdinaryQueue = GetCoordinatorFallbackTailYieldSubstate(context);
        Assert.Equal(1, afterOrdinaryQueue.LastRetryDispatchRecheckGeneration);
        Assert.Equal(receipt, afterOrdinaryQueue.LastRetryDispatchRecheckReceipt);
        Assert.Null(afterOrdinaryQueue.PendingRetryDispatchRecheckCommand);

        settleProgress.Invoke(
            service,
            [context, 129, 130, "unit_test_successor_progress_duplicate"]);
        Assert.Equal(
            1,
            GetCoordinatorFallbackTailYieldSubstate(context)
                .LastRetryDispatchRecheckGeneration);
        var log = ReadOperationalLogTail(logStart);
        Assert.Equal(
            1,
            CountOccurrences(
                log,
                "event=runtime_unlock_retry_dispatch_fallback_tail_hold_binding_decided;"));
        Assert.Contains("binding_mode=repair_successor;", log, StringComparison.Ordinal);
        Assert.Contains("decision_status=Accepted;", log, StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountOccurrences(
                log,
                "event=runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_decided;"));
        Assert.Contains(
            "decision_reason=fallback_tail_retry_dispatch_recheck_terminal_receipt_idempotent;",
            log,
            StringComparison.Ordinal);
        Assert.Contains("retired_offer_generation=", log, StringComparison.Ordinal);
        Assert.Contains("listener_rearm_requirement=", log, StringComparison.Ordinal);
        Assert.Contains("listener_execution_mode=", log, StringComparison.Ordinal);
        Assert.Contains("regular_v4_liveness_generation=", log, StringComparison.Ordinal);
        Assert.Contains(
            "source_publication_route_commit_generation=",
            log,
            StringComparison.Ordinal);
        Assert.Contains(
            $"source_publication_route_commit_generation_present={(retryRequest.SourcePublicationAuthority.RouteCommitGeneration.HasValue ? 1 : 0)};",
            log,
            StringComparison.Ordinal);

        FileTransferFrontierRequestFrameV6 CreateRequest(
            string repairRequestId,
            string correlationId,
            int chunkCount)
            => new()
            {
                SessionId = $"session_{transferId}",
                TransferId = transferId,
                TransportEpoch = 3,
                RepairRequestId = repairRequestId,
                RequestCorrelationId = correlationId,
                MissingRanges =
                [
                    new FileTransferRangeV4
                    {
                        StartChunkIndex = 128,
                        ChunkCount = chunkCount,
                    },
                ],
                Priority = "frontier",
                RecoveryMode =
                    "regular_nkn_frontier_stall_control_bulk",
            };
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public void OutboundRegularV4WriteHandoff_DrainWithoutAdmissionOrPendingPreCommitFailsClosed()
    {
        var transferId = $"transfer_regular_v4_send_drain_missing_precommit_{Guid.NewGuid():N}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        ReplaceOutboundDataSession(
            service,
            context,
            new RecordingDataSession($"session_{transferId}", transferId));
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "tuna_activation_answer_ack");

        var attempted = TryPrepareRegularV4RouteTransitionWithoutAuthoritySeeding(
            service,
            context,
            transport,
            "unit_test_missing_precommit_authority");

        Assert.False(attempted.Accepted);
        Assert.Null(attempted.Drain);
        Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockAdmissionToken"));
        Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockPendingPreCommitProbe"));
        Assert.Null(GetCurrentFileTransferRouteTransitionDrain(context));
        Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockRouteCommitToken"));
        Assert.False(GetPrivateProperty<bool>(context, "PullTransportPaused"));
        Assert.Equal(
            FileTransferRoute.RegularNknV4Fast,
            GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner").Route);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public void OutboundRegularV4WriteHandoff_LateAckCannotCreatePendingPreCommitAuthority()
    {
        var transferId = $"transfer_regular_v4_send_drain_late_ack_{Guid.NewGuid():N}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        ReplaceOutboundDataSession(
            service,
            context,
            new RecordingDataSession($"session_{transferId}", transferId));
        SeedRuntimeUnlockAdmissionForTest(
            context,
            FileTransferDirection.Outbound,
            "unit_test_late_ack_without_pending");
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "unit_test_late_ack_without_pending");

        var attempted = TryPrepareRegularV4RouteTransitionWithoutAuthoritySeeding(
            service,
            context,
            transport,
            "unit_test_late_ack_without_pending");

        Assert.False(attempted.Accepted);
        Assert.Null(attempted.Drain);
        Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockPendingPreCommitProbe"));
        Assert.Null(GetCurrentFileTransferRouteTransitionDrain(context));
        Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockRouteCommitToken"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public void OutboundRegularV4WriteHandoff_MismatchedExistingPendingPreCommitIsNotReplaced()
    {
        var transferId = $"transfer_regular_v4_send_drain_pending_mutation_{Guid.NewGuid():N}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        ReplaceOutboundDataSession(
            service,
            context,
            new RecordingDataSession($"session_{transferId}", transferId));
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "unit_test_pending_mutation");
        var staged = StageRegularV4RouteCommitSendDrain(
            service,
            context,
            "unit_test_pending_mutation");
        var originalDrain = Assert.IsAssignableFrom<object>(staged.Drain);
        var originalPending = GetPrivateProperty<RuntimeUnlockPendingPreCommitProbeRequest>(
            context,
            "RuntimeUnlockPendingPreCommitProbe");
        var mutatedPending = originalPending with
        {
            OfferGeneration = originalPending.OfferGeneration + 1,
        };
        SetPrivateProperty(context, "RuntimeUnlockPendingPreCommitProbe", mutatedPending);
        var previousPendingGeneration = GetPrivateProperty<int>(
            context,
            "LastRuntimeUnlockPendingPreCommitProbeGeneration");
        var previousTransition =
            GetFileTransferRouteTransitionSubstate(context);

        var attempted = TryPrepareRegularV4RouteTransitionWithoutAuthoritySeeding(
            service,
            context,
            transport,
            "unit_test_pending_mutation_retry");

        Assert.False(attempted.Accepted);
        Assert.Null(attempted.Drain);
        Assert.Equal(
            mutatedPending,
            GetPrivateProperty<RuntimeUnlockPendingPreCommitProbeRequest>(
                context,
                "RuntimeUnlockPendingPreCommitProbe"));
        Assert.Equal(
            Assert.IsType<FileTransferRouteTransitionPhysicalDrainCommand>(
                originalDrain),
            GetCurrentFileTransferRouteTransitionDrain(context));
        Assert.Equal(
            previousPendingGeneration,
            GetPrivateProperty<int>(context, "LastRuntimeUnlockPendingPreCommitProbeGeneration"));
        Assert.Equal(
            previousTransition,
            GetFileTransferRouteTransitionSubstate(context));
        Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockRouteCommitToken"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public void OutboundRegularV4WriteHandoff_CurrentDrainRejectsLeaseBeforeSendSubmission()
    {
        var transferId = $"transfer_regular_v4_send_drain_wins_{Guid.NewGuid():N}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var dataSession = new RecordingDataSession($"session_{transferId}", transferId);
        ReplaceOutboundDataSession(service, context, dataSession);
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "tuna_activation_answer_ack");
        var staged = StageRegularV4RouteCommitSendDrain(
            service,
            context,
            "tuna_activation_answer_ack");
        Assert.True(staged.Accepted);
        Assert.NotNull(staged.Drain);
        transport.RuntimeUnlockRouteCommitSnapshot = null;

        var frame = new FileTransferChunkBatchFrameV4
        {
            SessionId = $"session_{transferId}",
            TransferId = transferId,
            StartChunkIndex = 0,
            ChunkCount = 1,
            DataSegments = [new byte[] { 1 }],
            ForceRegularNknBulk = true,
        };
        var registration = RegisterRegularV4PhysicalSendLease(
            service,
            context,
            dataSession,
            frame);

        Assert.False(registration.Accepted);
        Assert.Null(registration.Lease);
        Assert.Equal(
            "regular_v4_to_tuna_transition_draining",
            registration.Reason);
        Assert.Empty(dataSession.SentFrames);
        Assert.Equal(0L, GetPrivateProperty<long>(context, "OutboundPhysicalSendRegisteredCount"));
        Assert.Empty(Assert.IsAssignableFrom<System.Collections.IDictionary>(
            GetPrivatePropertyValue(context, "ActiveOutboundPhysicalSendLeases")));
        Assert.Equal(
            Assert.IsType<FileTransferRouteTransitionPhysicalDrainCommand>(
                staged.Drain),
            GetCurrentFileTransferRouteTransitionDrain(context));

        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "divergent_provider_after_drain",
            transactionGeneration: 99,
            offerGeneration: 101,
            leaseGeneration: 103);
        var divergentRegistration = RegisterRegularV4PhysicalSendLease(
            service,
            context,
            dataSession,
            frame with { StartChunkIndex = 1 });
        Assert.False(divergentRegistration.Accepted);
        Assert.Null(divergentRegistration.Lease);
        Assert.Equal(
            "regular_v4_to_tuna_transition_draining",
            divergentRegistration.Reason);
        Assert.Equal(
            Assert.IsType<FileTransferRouteTransitionPhysicalDrainCommand>(
                staged.Drain),
            GetCurrentFileTransferRouteTransitionDrain(context));
        Assert.Empty(dataSession.SentFrames);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task RuntimeUnlockPreCommitProbe_BindsPendingDrainToAcceptedLaunchGenerationOnce()
    {
        var transferId = $"precommit_launch_binds_drain_{Guid.NewGuid():N}";
        var sessionId = $"session_{transferId}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport(sessionId);
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        SetPrivateProperty(context, "V4LastStateEpoch", 1);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var dataSession = new RecordingDataSession(sessionId, transferId);
        ReplaceOutboundDataSession(service, context, dataSession);
        SeedRuntimeUnlockAdmissionForTest(
            context,
            FileTransferDirection.Outbound,
            "unit_test_launch_binding");
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "unit_test_launch_binding",
            transactionGeneration: 7,
            offerGeneration: 11,
            leaseGeneration: 13,
            launchGeneration: 0);
        var preProbe = CreatePreProbeRouteCommitSnapshotForTest(
            Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(
                transport.RuntimeUnlockRouteCommitSnapshot));
        transport.RuntimeUnlockRouteCommitSnapshot = preProbe;
        Assert.Null(
            GetFileTransferRouteTransitionSubstate(context).CurrentOperation);

        var launch = preProbe with { LaunchGeneration = 17 };
        object?[] arguments = [context, dataSession, launch, "unit_test_launch_binding"];
        var handled = Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod(
                "TryHandleOutboundRuntimeUnlockPreCommitProbeLaunchSnapshotLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, arguments));
        Assert.True(handled);
        await WaitUntilAsync(
            () => dataSession.SentFrames.OfType<FileTransferRuntimeUnlockPreCommitProbeFrame>().Any(),
            timeoutMs: 5000);

        var reboundDrain = Assert.IsType<FileTransferRouteTransitionPhysicalDrainCommand>(
            GetCurrentFileTransferRouteTransitionDrain(context));
        var currentOperation = AssertFileTransferRouteTransitionOperation(
            context,
            FileTransferRouteTransitionState.Draining);
        Assert.Equal(
            1L,
            currentOperation.Authority.TransitionGeneration);
        Assert.Equal(
            currentOperation.Authority.TransitionGeneration,
            reboundDrain.Authority.TransitionGeneration);
        var activationBoundary =
            Assert.IsType<FileTransferTunaActivationTransitionProofBoundary>(
                currentOperation.Authority.ProofBoundary);
        Assert.Equal(17L, activationBoundary.LaunchGeneration);

        var mutated = launch with { OfferGeneration = launch.OfferGeneration + 1 };
        object?[] mutatedArguments = [context, dataSession, mutated, "unit_test_launch_binding_mutated"];
        var mutationHandled = Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod(
                "TryHandleOutboundRuntimeUnlockPreCommitProbeLaunchSnapshotLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, mutatedArguments));
        Assert.False(mutationHandled);
        Assert.Equal(
            reboundDrain,
            GetCurrentFileTransferRouteTransitionDrain(context));
        Assert.Equal(
            currentOperation,
            AssertFileTransferRouteTransitionOperation(
                context,
                FileTransferRouteTransitionState.Draining));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundRegularV4WriteHandoff_AvailabilityCallbackDeferredDrainDoesNotReportResumeOrRebind()
    {
        const string transferId = "v4_avail_drain_deferred";
        const string sessionId = "session_v4_avail_drain_deferred";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peerTransport = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peerTransport);
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 4,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        SetPrivateProperty(context, "V4LastStateEpoch", 1);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var dataSession = await transport.OpenFileTransferDataSessionAsync(
            sessionId,
            transferId,
            CancellationToken.None);
        ReplaceOutboundDataSession(service, context, dataSession);
        SeedRuntimeUnlockAdmissionForTest(
            context,
            FileTransferDirection.Outbound,
            "unit_test_availability_deferred_drain");
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "tuna_activation_answer_ack");

        var frame = new FileTransferChunkBatchFrameV4
        {
            SessionId = sessionId,
            TransferId = transferId,
            StartChunkIndex = 0,
            ChunkCount = 1,
            DataSegments = [new byte[] { 1 }],
            ForceRegularNknBulk = true,
        };
        var registration = RegisterRegularV4PhysicalSendLease(
            service,
            context,
            dataSession,
            frame);
        Assert.True(registration.Accepted, registration.Reason);
        Assert.NotNull(registration.Lease);
        var rebindGeneration = GetPrivateProperty<int>(context, "PullTransportRebindGeneration");
        var acceptedChunks = GetPrivateProperty<int>(context, "ChunksAcceptedForTransport");
        var acceptedBytes = GetPrivateProperty<long>(context, "BytesAcceptedForTransport");
        var lastResumeUtc = GetPrivatePropertyValue(context, "PullTransportLastResumeUtc");
        var logStart = GetOperationalLogLength();

        var launchSnapshot = CreatePreProbeRouteCommitSnapshotForTest(
            Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(transport.RuntimeUnlockRouteCommitSnapshot)) with
        {
            LaunchGeneration = 1,
        };
        transport.RequestAllDataSessionHandoffsWithSnapshot(
            "tuna_activation_answer_ack",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            launchSnapshot);

        await WaitUntilAsync(
            () => GetCurrentFileTransferRouteTransitionDrain(context) is not null &&
                  GetPrivateProperty<bool>(context, "PullTransportPaused"),
            timeoutMs: 5000);
        await Task.Delay(100);

        Assert.Equal(
            FileTransferRoute.RegularNknV4Fast,
            GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner").Route);
        Assert.Equal(rebindGeneration, GetPrivateProperty<int>(context, "PullTransportRebindGeneration"));
        Assert.Equal(acceptedChunks, GetPrivateProperty<int>(context, "ChunksAcceptedForTransport"));
        Assert.Equal(acceptedBytes, GetPrivateProperty<long>(context, "BytesAcceptedForTransport"));
        Assert.Equal(lastResumeUtc, GetPrivatePropertyValue(context, "PullTransportLastResumeUtc"));
        var log = ReadV4SenderLogSnapshot(logStart, transferId);
        Assert.DoesNotContain("event=filetransfer_transport_resumed; direction=outbound", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_transport_rebind_generation_started; direction=outbound", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_transport_rebind_skipped_for_tuna_activation; direction=outbound", log, StringComparison.Ordinal);

        await WaitUntilAsync(
            () => transport.SentDataFrames
                .OfType<FileTransferRuntimeUnlockPreCommitProbeFrame>()
                .Any(),
            timeoutMs: 5000);
        var probe = Assert.Single(
            transport.SentDataFrames.OfType<FileTransferRuntimeUnlockPreCommitProbeFrame>());
        transport.RuntimeUnlockRouteCommitSnapshot = launchSnapshot with
        {
            TransactionState = RuntimeUnlockTransactionState.RouteCommitPending.ToString(),
            PathProbeId = probe.ProbeId,
            PathProbeState = RuntimeUnlockPathProbeState.Acked.ToString(),
            PathProbeTransport = FileTransferTransportKind.Tuna,
            PathProbeAckedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        await ObserveRegularV4PhysicalSendLease(
            service,
            context,
            registration.Lease!,
            Task.CompletedTask);
        await WaitUntilAsync(
            () => GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner").Route ==
                  FileTransferRoute.FileTunaV4,
            timeoutMs: 5000);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task RuntimeUnlockPreCommitProbe_SuppliedLaunchSnapshotStagesDrainWithoutProviderRequery()
    {
        const string transferId = "precommit_launch_snapshot_direct";
        const string sessionId = "session_precommit_launch_snapshot_direct";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peerTransport = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peerTransport);
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 4,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        SetPrivateProperty(context, "V4LastStateEpoch", 1);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var dataSession = await transport.OpenFileTransferDataSessionAsync(
            sessionId,
            transferId,
            CancellationToken.None);
        ReplaceOutboundDataSession(service, context, dataSession);
        SeedRuntimeUnlockAdmissionForTest(
            context,
            FileTransferDirection.Outbound,
            "unit_test_supplied_launch_snapshot");
        SetPrivateProperty(context, "PullTransportPaused", true);
        SetPrivateProperty(context, "PullTransportPausedSinceUtc", DateTimeOffset.UtcNow);
        SetPrivateProperty(context, "PullTransportPauseReason", "tuna_activation_negotiating");
        SetPrivateProperty(context, "PullTransportResumeRequestPending", true);
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "tuna_activation_answer_ack",
            transactionGeneration: 7,
            offerGeneration: 11,
            leaseGeneration: 13,
            launchGeneration: 17);
        var supplied = Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(
            transport.RuntimeUnlockRouteCommitSnapshot) with
        {
            TransactionState = RuntimeUnlockTransactionState.AnswerReceived.ToString(),
            PathProbeId = null,
            PathProbeState = RuntimeUnlockPathProbeState.None.ToString(),
            PathProbeTransport = FileTransferTransportKind.Unknown,
            PathProbeAckedUtcMs = 0,
        };
        transport.RuntimeUnlockRouteCommitSnapshot = supplied with
        {
            TransactionGeneration = supplied.TransactionGeneration + 100,
        };
        var logStart = GetOperationalLogLength();

        transport.RequestAllDataSessionHandoffsWithSnapshot(
            "tuna_activation_answer_ack",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            supplied);

        await WaitUntilAsync(
            () => transport.SentDataFrames
                .OfType<FileTransferRuntimeUnlockPreCommitProbeFrame>()
                .Any(),
            timeoutMs: 5000);
        var probe = Assert.Single(
            transport.SentDataFrames.OfType<FileTransferRuntimeUnlockPreCommitProbeFrame>());
        var drain = GetCurrentFileTransferRouteTransitionDrain(context);
        Assert.NotNull(drain);
        Assert.True(GetPrivateProperty<bool>(context, "PullTransportPaused"));
        Assert.Equal(
            FileTransferRoute.RegularNknV4Fast,
            GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner").Route);

        transport.RuntimeUnlockRouteCommitSnapshot = supplied with
        {
            PathProbeId = probe.ProbeId,
            PathProbeState = RuntimeUnlockPathProbeState.Acked.ToString(),
            PathProbeTransport = FileTransferTransportKind.Tuna,
            PathProbeAckedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TransactionState = RuntimeUnlockTransactionState.RouteCommitPending.ToString(),
        };
        var afterAck = CompleteRegularV4RouteCommitSendDrain(
            service,
            context,
            drain,
            "unit_test_after_fresh_ack");
        Assert.True(afterAck.Accepted, afterAck.Reason);

        transport.RequestAllDataSessionHandoffsWithSnapshot(
            "tuna_activation_answer_ack_duplicate",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            supplied);
        transport.RequestAllDataSessionHandoffsWithSnapshot(
            "tuna_activation_answer_ack_mutated",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            supplied with { OfferGeneration = supplied.OfferGeneration + 1 });
        await Task.Delay(100);
        Assert.Single(transport.SentDataFrames.OfType<FileTransferRuntimeUnlockPreCommitProbeFrame>());

        var log = ReadV4SenderLogSnapshot(logStart, transferId);
        Assert.Contains(
            "event=filetransfer_runtime_unlock_precommit_probe_launch_handoff_decided;",
            log,
            StringComparison.Ordinal);
        Assert.Contains("launch_generation=17; status=Accepted;", log, StringComparison.Ordinal);
        Assert.Contains("launch_generation=17; status=Idempotent;", log, StringComparison.Ordinal);
        Assert.Contains("decision_reason=launch_generation_tuple_mutation", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_transport_resumed; direction=outbound", log, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task RuntimeUnlockPreCommitProbe_PostTunaFallbackLaunchPreparesExactTransitionAuthority()
    {
        const string transferId = "precommit_launch_post_tuna_fallback";
        const string sessionId = "session_precommit_launch_post_tuna_fallback";
        using var service = new SessionFileTransferService();
        using var transport = Assert.IsAssignableFrom<LoopbackFileTransferTransport>(
            Activator.CreateInstance(
                typeof(SessionFileTransferRuntimeUnlockRetryAuthorityTests).GetNestedType(
                    "RetryAuthorityLoopbackTransport", BindingFlags.NonPublic)!,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [sessionId],
                culture: null));
        service.AttachTransport(transport);
        var context = CreatePostTunaFallbackV6OutboundContext(
            transferId,
            startCoordinatorSnapshotStream: false);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var dataSession = new RecordingDataSession(sessionId, transferId);
        SetPrivateProperty(context, "DataSession", dataSession);
        SetPrivateProperty(context, "DataSessionGeneration", 2L);
        SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 99);
        SetPrivateProperty(context, "PullPostTunaRecoveryActive", false);
        SetPrivateProperty(context, "PullTransportFrontierOnlyRepairActive", false);
        var leg = Assert.IsType<FileTransferLeg>(
            GetPrivatePropertyValue(context, "CurrentTransferLeg"));
        StartCoordinatorSnapshotStreamForTest(context);
        SeedPairedFallbackTailYieldForTest(context, leg);
        var state = CreateCoordinatorStateForTest(context);
        var operation = Assert.IsType<FileTransferFallbackTailYieldOperation>(
            state.PersistentState.FallbackTailYield.CurrentOperation);
        var request = Assert.IsType<RuntimeUnlockRetryDispatchAuthorityRequest>(
            state.PersistentState.FallbackTailYield.PendingRetryRecheckRequest);
        var authorization = FileTransferCoordinator.AuthorizeRuntimeUnlockRetryDispatch(
            state,
            request,
            RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield,
            operation.Decision.AuthorityId,
            operation.Decision.YieldGeneration,
            FallbackTailYieldKind.Quiescent.ToString());
        Assert.True(authorization.Granted, authorization.Reason);
        typeof(SessionFileTransferService)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method =>
                method.Name == "ApplyRuntimeUnlockRetryDispatchAuthorityDecisionLocked" &&
                method.GetParameters()[0].ParameterType == context.GetType())
            .Invoke(null, [context, request, authorization]);
        typeof(SessionFileTransferService)
            .GetMethod("ProcessRuntimeUnlockRetryDispatchDecision", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [transport, authorization, GetPrivateProperty<long>(context, "DataSessionGeneration")]);
        Assert.True(FileTransferCoordinator.TryGetCurrentRuntimeUnlockRetryDispatchInstallReceipt(
            CreateCoordinatorStateForTest(context), out var installed));
        Assert.Equal(authorization.Contract!.Authority, installed.Authority);
        var admissionDecision = Assert.IsType<RuntimeUnlockAdmissionDecision>(typeof(SessionFileTransferService)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method =>
                method.Name == "RequestRuntimeUnlockAdmissionLocked" &&
                method.GetParameters()[0].ParameterType == context.GetType())
            .Invoke(null, [context, "route_hint", "unit_test_post_tuna_fallback_launch", false]));
        Assert.True(admissionDecision.Granted, admissionDecision.Reason);
        var admission = Assert.IsType<RuntimeUnlockAdmissionToken>(admissionDecision.AdmissionToken);
        var snapshot = new RuntimeUnlockRouteCommitSnapshot(
            sessionId,
            transferId,
            TransactionGeneration: 5,
            OfferGeneration: 8,
            PeerVisibleProof: true,
            PeerReceived: true,
            AnswerReceived: true,
            TargetRoute: FileTransferRoute.FileTunaV4,
            ProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
            HandoffKind: FileTransferTransportHandoffKind.NormalToTunaActivation,
            TargetTransport: FileTransferTransportKind.Tuna,
            TransactionState: RuntimeUnlockTransactionState.AnswerReceived.ToString(),
            Reason: "unit_test_post_tuna_fallback_launch",
            PathProbeState: RuntimeUnlockPathProbeState.None.ToString(),
            TunaPathLeaseRequired: true,
            TunaPathLeaseGeneration: 3,
            TunaPathLeaseState: RuntimeUnlockTunaPathLeaseState.ListenerReady.ToString(),
            TunaPathLeaseListenerRunId: "listener-post-fallback-3",
            TunaPathLeaseCurrent: true,
            LaunchGeneration: 9);
        transport.RuntimeUnlockRouteCommitSnapshot = snapshot;
        var args = new object?[]
        {
            context,
            admission,
            "unit_test_post_tuna_fallback_launch",
            null,
            0L,
            null,
            snapshot,
        };
        var logStart = GetOperationalLogLength();

        var started = Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod("StartOutboundRuntimeUnlockPreCommitProbe", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, args));

        Assert.True(
            started,
            Assert.IsType<string>(args[3]) + Environment.NewLine +
            ReadOperationalLogTail(logStart));
        await WaitUntilAsync(
            () => dataSession.SentFrames.OfType<FileTransferRuntimeUnlockPreCommitProbeFrame>().Any(),
            timeoutMs: 5000);
        Assert.Single(dataSession.SentFrames.OfType<FileTransferRuntimeUnlockPreCommitProbeFrame>());
        Assert.True(Assert.IsType<long>(args[4]) > 0);
        Assert.NotNull(GetPrivatePropertyValue(context, "RuntimeUnlockPendingPreCommitProbe"));
        Assert.NotNull(GetCurrentFileTransferRouteTransitionDrain(context));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public void RuntimeUnlockPreCommitProbe_LateOlderDispositionCannotRegressNewerLaunch()
    {
        const string transferId = "precommit_launch_disposition_monotonic";
        using var service = new SessionFileTransferService();
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        var snapshot = new RuntimeUnlockRouteCommitSnapshot(
            $"session_{transferId}",
            transferId,
            TransactionGeneration: 2,
            OfferGeneration: 3,
            PeerVisibleProof: true,
            PeerReceived: true,
            AnswerReceived: true,
            TargetRoute: FileTransferRoute.FileTunaV4,
            ProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
            HandoffKind: FileTransferTransportHandoffKind.NormalToTunaActivation,
            TargetTransport: FileTransferTransportKind.Tuna,
            TransactionState: RuntimeUnlockTransactionState.AnswerReceived.ToString(),
            Reason: "unit_test_launch_disposition",
            TunaPathLeaseRequired: true,
            TunaPathLeaseGeneration: 4,
            TunaPathLeaseState: RuntimeUnlockTunaPathLeaseState.ListenerReady.ToString(),
            TunaPathLeaseListenerRunId: "listener-monotonic-4",
            TunaPathLeaseCurrent: true,
            LaunchGeneration: 12);
        var record = typeof(SessionFileTransferService).GetMethod(
            "RecordRuntimeUnlockPreCommitProbeLaunchDispositionLocked",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        record.Invoke(service, [context, snapshot, "Accepted", "accepted", 7L]);
        record.Invoke(
            service,
            [context, snapshot with { LaunchGeneration = 11 }, "Invalidated", "late_old_callback", 0L]);
        record.Invoke(service, [context, snapshot, "Accepted", "duplicate_newer_handoff", 7L]);
        record.Invoke(
            service,
            [context, snapshot with { OfferGeneration = 99 }, "Rejected", "tuple_mutation", 0L]);

        var disposition = GetPrivatePropertyValue(
            context,
            "LastRuntimeUnlockPreCommitProbeLaunchDisposition");
        Assert.NotNull(disposition);
        Assert.Equal(12L, GetPrivateProperty<long>(disposition, "LaunchGeneration"));
        Assert.Equal("Accepted", GetPrivateProperty<string>(disposition, "Status"));
        Assert.Equal(snapshot, GetPrivateProperty<RuntimeUnlockRouteCommitSnapshot>(disposition, "Snapshot"));
        Assert.Equal(7L, GetPrivateProperty<long>(disposition, "DrainGeneration"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundRegularV4WriteHandoff_AvailabilityCallbackCommittedDrainReportsResume()
    {
        const string transferId = "v4_avail_drain_committed";
        const string sessionId = "session_v4_avail_drain_committed";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peerTransport = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peerTransport);
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        SetPrivateProperty(context, "V4LastStateEpoch", 1);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var dataSession = await transport.OpenFileTransferDataSessionAsync(
            sessionId,
            transferId,
            CancellationToken.None);
        ReplaceOutboundDataSession(service, context, dataSession);
        SeedRuntimeUnlockAdmissionForTest(
            context,
            FileTransferDirection.Outbound,
            "unit_test_availability_committed_drain");
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "tuna_activation_answer_ack");
        var frame = new FileTransferChunkBatchFrameV4
        {
            SessionId = sessionId,
            TransferId = transferId,
            StartChunkIndex = 0,
            ChunkCount = 1,
            DataSegments = [new byte[] { 1 }],
            ForceRegularNknBulk = true,
        };
        var registration = RegisterRegularV4PhysicalSendLease(
            service,
            context,
            dataSession,
            frame);
        Assert.True(registration.Accepted, registration.Reason);
        Assert.NotNull(registration.Lease);
        var logStart = GetOperationalLogLength();

        var launchSnapshot = CreatePreProbeRouteCommitSnapshotForTest(
            Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(transport.RuntimeUnlockRouteCommitSnapshot)) with
        {
            LaunchGeneration = 1,
        };
        transport.RequestAllDataSessionHandoffsWithSnapshot(
            "tuna_activation_answer_ack",
            FileTransferTransportHandoffKind.NormalToTunaActivation,
            FileTransferTransportKind.Tuna,
            launchSnapshot);

        await WaitUntilAsync(
            () => GetCurrentFileTransferRouteTransitionDrain(context) is not null &&
                  GetPrivateProperty<bool>(context, "PullTransportPaused"),
            timeoutMs: 5000);
        await WaitUntilAsync(
            () => transport.SentDataFrames
                .OfType<FileTransferRuntimeUnlockPreCommitProbeFrame>()
                .Any(),
            timeoutMs: 5000);
        var probe = Assert.Single(
            transport.SentDataFrames.OfType<FileTransferRuntimeUnlockPreCommitProbeFrame>());
        transport.RuntimeUnlockRouteCommitSnapshot = launchSnapshot with
        {
            TransactionState = RuntimeUnlockTransactionState.RouteCommitPending.ToString(),
            PathProbeId = probe.ProbeId,
            PathProbeState = RuntimeUnlockPathProbeState.Acked.ToString(),
            PathProbeTransport = FileTransferTransportKind.Tuna,
            PathProbeAckedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        await ObserveRegularV4PhysicalSendLease(
            service,
            context,
            registration.Lease!,
            Task.CompletedTask);
        await WaitUntilAsync(
            () => GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner").Route ==
                  FileTransferRoute.FileTunaV4,
            timeoutMs: 5000);
        await Task.Delay(200);

        Assert.False(GetPrivateProperty<bool>(context, "PullTransportPaused"));
        Assert.Contains(
            "event=filetransfer_route_transition_committed;",
            ReadV4SenderLogSnapshot(logStart, transferId),
            StringComparison.Ordinal);
        Assert.Contains(
            "event=filetransfer_route_transition_disposition_applied;",
            ReadV4SenderLogSnapshot(logStart, transferId),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundRegularV4WriteHandoff_DrainStagedAfterInitialCheckPreventsActualSendSubmission()
    {
        var transferId = $"transfer_regular_v4_send_drain_pre_reservation_{Guid.NewGuid():N}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var dataSession = new RecordingDataSession($"session_{transferId}", transferId);
        ReplaceOutboundDataSession(service, context, dataSession);
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "tuna_activation_answer_ack");

        var barrierReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousBarrier = SessionFileTransferService.OutboundRegularV4PhysicalSendPreReservationBarrierForTests;
        SessionFileTransferService.OutboundRegularV4PhysicalSendPreReservationBarrierForTests = async (observedTransferId, _) =>
        {
            if (!string.Equals(observedTransferId, transferId, StringComparison.Ordinal))
            {
                return;
            }

            barrierReached.TrySetResult();
            await releaseBarrier.Task.ConfigureAwait(false);
        };

        try
        {
            await using var stream = new MemoryStream(
                new byte[Math.Max(1, GetPrivateProperty<int>(context, "ChunkSizeBytes"))],
                writable: false);
            var sendTask = Assert.IsAssignableFrom<Task<bool>>(typeof(SessionFileTransferService)
                .GetMethod("SendChunkIndicesV4Async", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(
                    service,
                    [
                        context,
                        stream,
                        dataSession,
                        new List<int> { 0 },
                        false,
                        "(none)",
                        null,
                        null,
                        null,
                        FileTransferV4RepairDeliveryMode.BulkOnly,
                        "normal",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        CancellationToken.None,
                    ])!);

            await barrierReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var staged = StageRegularV4RouteCommitSendDrain(
                service,
                context,
                "tuna_activation_answer_ack");
            Assert.True(staged.Accepted);
            Assert.NotNull(staged.Drain);

            releaseBarrier.TrySetResult();
            Assert.False(await sendTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(dataSession.SentFrames);
            Assert.Equal(0L, GetPrivateProperty<long>(context, "OutboundPhysicalSendRegisteredCount"));
            Assert.Empty(Assert.IsAssignableFrom<System.Collections.IDictionary>(
                GetPrivatePropertyValue(context, "ActiveOutboundPhysicalSendLeases")));
        }
        finally
        {
            releaseBarrier.TrySetResult();
            SessionFileTransferService.OutboundRegularV4PhysicalSendPreReservationBarrierForTests = previousBarrier;
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundRegularV4WriteHandoff_ReservationAndPhysicalSubmissionCompleteBeforeDraining()
    {
        var transferId =
            $"transfer_regular_v4_send_drain_reservation_wins_{Guid.NewGuid():N}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        using var dataSession = new BlockingPhysicalSendDataSession(
            $"session_{transferId}",
            transferId,
            () => GetFileTransferRouteTransitionSubstate(context)
                .CurrentOperation?.State);
        ReplaceOutboundDataSession(service, context, dataSession);
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "tuna_activation_answer_ack");

        await using var stream = new MemoryStream(
            new byte[Math.Max(1, GetPrivateProperty<int>(context, "ChunkSizeBytes"))],
            writable: false);
        var sendTask = Task.Run(
            async () => await Assert.IsAssignableFrom<Task<bool>>(
                    typeof(SessionFileTransferService)
                        .GetMethod(
                            "SendChunkIndicesV4Async",
                            BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(
                            service,
                            [
                                context,
                                stream,
                                dataSession,
                                new List<int> { 0 },
                                false,
                                "(none)",
                                null,
                                null,
                                null,
                                FileTransferV4RepairDeliveryMode.BulkOnly,
                                "normal",
                                null,
                                null,
                                null,
                                null,
                                null,
                                null,
                                CancellationToken.None,
                            ])!)
                .ConfigureAwait(false));

        await dataSession.SubmissionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var serviceGate = typeof(SessionFileTransferService)
            .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        var stageTask = Task.Run(
            () =>
            {
                lock (serviceGate)
                {
                    return StageRegularV4RouteCommitSendDrain(
                        service,
                        context,
                        "tuna_activation_answer_ack");
                }
            });
        await Task.Delay(100);

        Assert.False(stageTask.IsCompleted);
        Assert.NotEqual(
            FileTransferRouteTransitionState.Draining,
            dataSession.TransitionStateAtSubmission);

        dataSession.ReleaseSubmission.TrySetResult();
        var staged = await stageTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(staged.Accepted);
        Assert.NotNull(staged.Drain);
        Assert.Equal(1, dataSession.SubmissionCount);
        Assert.Equal(
            1,
            AssertFileTransferRouteTransitionOperation(
                    context,
                    FileTransferRouteTransitionState.Draining)
                .DrainCommand!.Value.CapturedPhysicalSendCount);
        Assert.True(await sendTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(Assert.IsAssignableFrom<System.Collections.IDictionary>(
            GetPrivatePropertyValue(
                context,
                "ActiveOutboundPhysicalSendLeases")));

        dataSession.PhysicalCompletion.TrySetResult();
        await WaitUntilAsync(
            () => Assert.IsAssignableFrom<System.Collections.IDictionary>(
                    GetPrivatePropertyValue(
                        context,
                        "ActiveOutboundPhysicalSendLeases"))
                .Count == 0,
            timeoutMs: 5000);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public void OutboundRegularV4WriteHandoff_DuplicateEmptyDrainCompletesIdempotently()
    {
        var transferId = $"transfer_regular_v4_empty_send_drain_{Guid.NewGuid():N}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var dataSession = new RecordingDataSession($"session_{transferId}", transferId);
        ReplaceOutboundDataSession(service, context, dataSession);
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "tuna_activation_answer_ack");

        var first = StageRegularV4RouteCommitSendDrain(
            service,
            context,
            "tuna_activation_answer_ack");

        Assert.True(first.Accepted);
        Assert.NotNull(first.Drain);
        var current = IsRegularV4RouteCommitSendDrainCurrent(service, context, first.Drain!);
        Assert.True(current.Accepted, current.Reason);
        var duplicate = StageRegularV4RouteCommitSendDrain(
            service,
            context,
            "tuna_activation_answer_ack");
        Assert.True(duplicate.Accepted);
        Assert.Equal(
            Assert.IsType<FileTransferRouteTransitionPhysicalDrainCommand>(
                first.Drain),
            Assert.IsType<FileTransferRouteTransitionPhysicalDrainCommand>(
                duplicate.Drain));
        var draining = AssertFileTransferRouteTransitionOperation(
            context,
            FileTransferRouteTransitionState.Draining);
        Assert.Equal(1L, draining.Authority.TransitionGeneration);

        var firstCompletion = CompleteRegularV4RouteCommitSendDrain(
            service,
            context,
            first.Drain!,
            "unit_test_empty_drain_complete");
        var duplicateCompletion = CompleteRegularV4RouteCommitSendDrain(
            service,
            context,
            first.Drain!,
            "unit_test_duplicate_empty_drain_complete");

        Assert.True(firstCompletion.Accepted, firstCompletion.Reason);
        Assert.True(duplicateCompletion.Accepted, duplicateCompletion.Reason);
        var terminal =
            GetFileTransferRouteTransitionSubstate(context).LastTerminalOperation;
        Assert.True(terminal.HasValue);
        Assert.Equal(
            FileTransferRouteTransitionState.Committed,
            terminal.Value.State);
        Assert.Equal(draining.Authority, terminal.Value.Authority);
        Assert.Equal(
            Assert.IsType<FileTransferRouteTransitionPhysicalDrainCommand>(
                first.Drain),
            terminal.Value.DrainCommand);
        Assert.Null(
            GetFileTransferRouteTransitionSubstate(context).CurrentOperation);
        Assert.NotNull(
            GetPrivatePropertyValue(context, "RuntimeUnlockRouteCommitToken"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundPhysicalSendLease_RetiresIndependentlyOfDeliveryObservation()
    {
        var transferId = $"transfer_physical_delivery_separation_{Guid.NewGuid():N}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var dataSession = new RecordingDataSession($"session_{transferId}", transferId);
        ReplaceOutboundDataSession(service, context, dataSession);
        var frame = new FileTransferChunkBatchFrameV4
        {
            SessionId = $"session_{transferId}",
            TransferId = transferId,
            StartChunkIndex = 0,
            ChunkCount = 1,
            DataSegments = [new byte[] { 1 }],
            ForceRegularNknBulk = true,
        };
        var registration = RegisterRegularV4PhysicalSendLease(
            service,
            context,
            dataSession,
            frame);
        Assert.True(registration.Accepted, registration.Reason);
        Assert.NotNull(registration.Lease);

        var physicalCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveryCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var leaseObserver = ObserveRegularV4PhysicalSendLease(
            service,
            context,
            registration.Lease!,
            physicalCompletion.Task);
        var deliveryObserver = ObserveRegularV4PhysicalSendFailure(
            service,
            context,
            registration.Lease!,
            deliveryCompletion.Task);

        physicalCompletion.TrySetResult();
        await leaseObserver;
        Assert.Equal(
            0,
            Assert.IsAssignableFrom<System.Collections.IDictionary>(
                    GetPrivatePropertyValue(context, "ActiveOutboundPhysicalSendLeases"))
                .Count);
        Assert.False(deliveryObserver.IsCompleted);

        deliveryCompletion.TrySetResult();
        await deliveryObserver;
        Assert.Equal(1L, GetPrivateProperty<long>(context, "OutboundPhysicalSendRetiredCount"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundRegularV4WriteHandoff_NormalAndRepairTasksDrainBeforeCommitAndLateCallbacksAreIdempotent()
    {
        var transferId = $"transfer_regular_v4_multi_send_drain_{Guid.NewGuid():N}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var dataSession = new RecordingDataSession($"session_{transferId}", transferId);
        ReplaceOutboundDataSession(service, context, dataSession);
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "tuna_activation_answer_ack");
        SeedRuntimeUnlockAdmissionForTest(
            context,
            FileTransferDirection.Outbound,
            "unit_test_multi_send_drain");

        var normalFrame = new FileTransferChunkBatchFrameV4
        {
            SessionId = $"session_{transferId}",
            TransferId = transferId,
            StartChunkIndex = 0,
            ChunkCount = 1,
            DataSegments = [new byte[] { 1 }],
            ForceRegularNknBulk = true,
        };
        var repairFrame = normalFrame with
        {
            StartChunkIndex = 1,
            DataSegments = [new byte[] { 2 }],
        };
        var normal = RegisterRegularV4PhysicalSendLease(
            service,
            context,
            dataSession,
            normalFrame);
        var repair = RegisterRegularV4PhysicalSendLease(
            service,
            context,
            dataSession,
            repairFrame,
            repairSend: true,
            repairRequestKey: "unit_test_repair:1:1");
        Assert.True(normal.Accepted, normal.Reason);
        Assert.NotNull(normal.Lease);
        Assert.True(repair.Accepted, repair.Reason);
        Assert.NotNull(repair.Lease);

        var staged = StageRegularV4RouteCommitSendDrain(
            service,
            context,
            "tuna_activation_answer_ack");
        Assert.True(staged.Accepted);
        Assert.NotNull(staged.Drain);
        Assert.Equal(
            2,
            Assert.IsAssignableFrom<System.Collections.IDictionary>(
                GetPrivatePropertyValue(context, "ActiveOutboundPhysicalSendLeases")).Count);

        var normalCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repairCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observeNormal = ObserveRegularV4PhysicalSendLease(
            service,
            context,
            normal.Lease!,
            normalCompletion.Task);
        var observeRepair = ObserveRegularV4PhysicalSendLease(
            service,
            context,
            repair.Lease!,
            repairCompletion.Task);

        normalCompletion.TrySetResult();
        await observeNormal;
        Assert.Equal(1L, GetPrivateProperty<long>(context, "OutboundPhysicalSendRetiredCount"));
        AssertFileTransferRouteTransitionOperation(
            context,
            FileTransferRouteTransitionState.Draining);
        Assert.Equal(
            FileTransferRoute.RegularNknV4Fast,
            GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner").Route);

        await ObserveRegularV4PhysicalSendLease(
            service,
            context,
            normal.Lease!,
            Task.CompletedTask);
        Assert.Equal(1L, GetPrivateProperty<long>(context, "OutboundPhysicalSendRetiredCount"));

        repairCompletion.TrySetResult();
        await observeRepair;
        await WaitUntilAsync(
            () => GetFileTransferRouteTransitionSubstate(context)
                      .LastTerminalOperation is
                      { State: FileTransferRouteTransitionState.Committed } &&
                  GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner").Route ==
                  FileTransferRoute.FileTunaV4,
            timeoutMs: 5000);

        var installedOwner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var installedPublication = GetPrivateProperty<RoutePublicationToken>(
            context,
            "CurrentRoutePublicationToken");
        var installedCommit = GetPrivatePropertyValue(context, "RuntimeUnlockRouteCommitToken");
        var terminalGeneration = GetPrivateProperty<int>(context, "TerminalGeneration");
        var retiredCount = GetPrivateProperty<long>(context, "OutboundPhysicalSendRetiredCount");

        await ObserveRegularV4PhysicalSendLease(
            service,
            context,
            repair.Lease!,
            Task.CompletedTask);
        await RecheckRegularV4RouteCommitSendDrain(service, context, staged.Drain!);
        await RecheckRegularV4RouteCommitSendDrain(service, context, staged.Drain!);

        Assert.Equal(retiredCount, GetPrivateProperty<long>(context, "OutboundPhysicalSendRetiredCount"));
        Assert.Equal(installedOwner, GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"));
        Assert.Equal(
            installedPublication,
            GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken"));
        Assert.Equal(installedCommit, GetPrivatePropertyValue(context, "RuntimeUnlockRouteCommitToken"));
        Assert.Equal(terminalGeneration, GetPrivateProperty<int>(context, "TerminalGeneration"));
        var terminalTransition =
            GetFileTransferRouteTransitionSubstate(context).LastTerminalOperation;
        Assert.True(terminalTransition.HasValue);
        Assert.Equal(
            FileTransferRouteTransitionState.Committed,
            terminalTransition.Value.State);
        Assert.Equal(1L, terminalTransition.Value.Authority.TransitionGeneration);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundRegularV4WriteHandoff_FirstSuccessDoesNotRetireLeaseBeforeRedundantPhysicalCopyCompletes()
    {
        var transferId = $"transfer_regular_v4_physical_copy_drain_{Guid.NewGuid():N}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var dataSession = new RecordingDataSession($"session_{transferId}", transferId);
        ReplaceOutboundDataSession(service, context, dataSession);
        transport.SeedRuntimeUnlockRouteCommitProofForTests(transferId, "tuna_activation_answer_ack");
        SeedRuntimeUnlockAdmissionForTest(
            context,
            FileTransferDirection.Outbound,
            "unit_test_physical_copy_drain");
        var frame = new FileTransferChunkBatchFrameV4
        {
            SessionId = $"session_{transferId}",
            TransferId = transferId,
            StartChunkIndex = 0,
            ChunkCount = 1,
            DataSegments = [new byte[] { 1 }],
            RepairDeliveryMode = FileTransferV4RepairDeliveryMode.ControlBulkRedundant,
        };
        var registration = RegisterRegularV4PhysicalSendLease(
            service,
            context,
            dataSession,
            frame,
            repairSend: true,
            repairRequestKey: "unit_test_redundant_repair:0:1");
        Assert.True(registration.Accepted, registration.Reason);
        Assert.NotNull(registration.Lease);
        var staged = StageRegularV4RouteCommitSendDrain(
            service,
            context,
            "tuna_activation_answer_ack");
        Assert.True(staged.Accepted);
        Assert.NotNull(staged.Drain);
        var secondaryPhysicalCopy = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FileTransferPhysicalSendSubmission submission = new(
            Task.CompletedTask,
            secondaryPhysicalCopy.Task);
        var observer = ObserveRegularV4PhysicalSendLease(
            service,
            context,
            registration.Lease!,
            submission.PhysicalCompletionTask);

        await submission.DeliveryTask;
        Assert.False(observer.IsCompleted);
        var deferred = CompleteRegularV4RouteCommitSendDrain(
            service,
            context,
            staged.Drain!,
            "unit_test_delivery_completed_physical_pending");
        Assert.False(deferred.Accepted);
        Assert.Equal("regular_v4_physical_sends_in_flight", deferred.Reason);
        Assert.Equal(
            FileTransferRoute.RegularNknV4Fast,
            GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner").Route);

        secondaryPhysicalCopy.TrySetResult();
        await observer;
        await WaitUntilAsync(
            () => GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner").Route ==
                  FileTransferRoute.FileTunaV4,
            timeoutMs: 5000);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task OutboundRegularV4WriteHandoff_NewDrainCapturesLeaseFromReplacedDataSessionInSourceScope()
    {
        var transferId = $"transfer_regular_v4_replaced_scope_drain_{Guid.NewGuid():N}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var firstDataSession = new RecordingDataSession($"session_{transferId}", transferId);
        ReplaceOutboundDataSession(service, context, firstDataSession);
        var frame = new FileTransferChunkBatchFrameV4
        {
            SessionId = $"session_{transferId}",
            TransferId = transferId,
            StartChunkIndex = 0,
            ChunkCount = 1,
            DataSegments = [new byte[] { 1 }],
        };
        var registration = RegisterRegularV4PhysicalSendLease(
            service,
            context,
            firstDataSession,
            frame);
        Assert.True(registration.Accepted, registration.Reason);
        Assert.NotNull(registration.Lease);
        var oldSessionPhysicalCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = ObserveRegularV4PhysicalSendLease(
            service,
            context,
            registration.Lease!,
            oldSessionPhysicalCompletion.Task);

        var replacementDataSession = new RecordingDataSession($"session_{transferId}", transferId);
        ReplaceOutboundDataSession(service, context, replacementDataSession);
        transport.SeedRuntimeUnlockRouteCommitProofForTests(transferId, "tuna_activation_answer_ack");
        SeedRuntimeUnlockAdmissionForTest(
            context,
            FileTransferDirection.Outbound,
            "unit_test_replaced_scope_drain");
        var staged = StageRegularV4RouteCommitSendDrain(
            service,
            context,
            "tuna_activation_answer_ack");
        Assert.True(staged.Accepted);
        Assert.NotNull(staged.Drain);
        Assert.Equal(
            1,
            GetPrivateProperty<int>(
                staged.Drain!,
                "CapturedPhysicalSendCount"));
        Assert.Equal(
            GetPrivateProperty<long>(registration.Lease!, "LeaseGeneration"),
            GetPrivateProperty<long>(
                staged.Drain!,
                "CapturedThroughPhysicalSendGeneration"));
        Assert.False(observer.IsCompleted);
        Assert.Equal(
            FileTransferRoute.RegularNknV4Fast,
            GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner").Route);

        oldSessionPhysicalCompletion.TrySetResult();
        await observer;
        await WaitUntilAsync(
            () => GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner").Route ==
                  FileTransferRoute.FileTunaV4,
            timeoutMs: 5000);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public void OutboundRegularV4WriteHandoff_MatchingProbeFailureCallbackInvalidatesDrainWithoutRouteEffects()
    {
        var transferId = $"transfer_regular_v4_probe_failure_{Guid.NewGuid():N}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        ReplaceOutboundDataSession(
            service,
            context,
            new RecordingDataSession($"session_{transferId}", transferId));
        transport.SeedRuntimeUnlockRouteCommitProofForTests(transferId, "tuna_activation_answer_ack");
        var staged = StageRegularV4RouteCommitSendDrain(
            service,
            context,
            "tuna_activation_answer_ack");
        Assert.True(staged.Accepted);
        Assert.NotNull(staged.Drain);
        var snapshot = Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(
            transport.RuntimeUnlockRouteCommitSnapshot);
        var owner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var publication = GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken");
        var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        var legScope = (
            leg.LegId,
            leg.Generation,
            leg.RouteSelection,
            leg.ProtocolVersion,
            leg.LiveRouteEpochId,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            leg.State,
            leg.CanSendData,
            leg.CheckpointRequestId,
            leg.CheckpointGeneration);

        ReportRuntimeUnlockPreCommitProbeFailure(
            service,
            FileTransferDirection.Outbound,
            transferId,
            $"session_{transferId}",
            snapshot,
            snapshot.PathProbeId ?? string.Empty,
            "unit_test_matching_probe_failure");

        Assert.Null(GetCurrentFileTransferRouteTransitionDrain(context));
        var invalidatedTransition =
            GetFileTransferRouteTransitionSubstate(context);
        Assert.Null(invalidatedTransition.CurrentOperation);
        Assert.True(invalidatedTransition.LastTerminalOperation.HasValue);
        Assert.Equal(
            FileTransferRouteTransitionState.Invalidated,
            invalidatedTransition.LastTerminalOperation.Value.State);
        Assert.Equal(
            1L,
            invalidatedTransition.LastTerminalOperation.Value.Authority.TransitionGeneration);
        Assert.Equal(owner, GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"));
        Assert.Equal(publication, GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken"));
        var currentLeg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        Assert.NotSame(leg, currentLeg);
        Assert.Equal(
            legScope,
            (
                currentLeg.LegId,
                currentLeg.Generation,
                currentLeg.RouteSelection,
                currentLeg.ProtocolVersion,
                currentLeg.LiveRouteEpochId,
                currentLeg.TransportEpochId,
                currentLeg.BridgeRecoveryGeneration,
                currentLeg.State,
                currentLeg.CanSendData,
                currentLeg.CheckpointRequestId,
                currentLeg.CheckpointGeneration));
        Assert.Equal(
            legScope,
            (
                leg.LegId,
                leg.Generation,
                leg.RouteSelection,
                leg.ProtocolVersion,
                leg.LiveRouteEpochId,
                leg.TransportEpochId,
                leg.BridgeRecoveryGeneration,
                leg.State,
                leg.CanSendData,
                leg.CheckpointRequestId,
                leg.CheckpointGeneration));
        Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockRouteCommitToken"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public void OutboundRegularV4WriteHandoff_StaleProbeFailureCallbackCannotInvalidateCurrentDrain()
    {
        var transferId = $"transfer_regular_v4_stale_probe_failure_{Guid.NewGuid():N}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        ReplaceOutboundDataSession(
            service,
            context,
            new RecordingDataSession($"session_{transferId}", transferId));
        transport.SeedRuntimeUnlockRouteCommitProofForTests(transferId, "tuna_activation_answer_ack");
        var staged = StageRegularV4RouteCommitSendDrain(
            service,
            context,
            "tuna_activation_answer_ack");
        Assert.True(staged.Accepted);
        Assert.NotNull(staged.Drain);
        var snapshot = Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(
            transport.RuntimeUnlockRouteCommitSnapshot);
        var owner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var publication = GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken");

        ReportRuntimeUnlockPreCommitProbeFailure(
            service,
            FileTransferDirection.Outbound,
            transferId,
            $"session_{transferId}",
            snapshot with { TransactionGeneration = snapshot.TransactionGeneration + 1 },
            snapshot.PathProbeId ?? string.Empty,
            "unit_test_stale_transaction_probe_failure");
        ReportRuntimeUnlockPreCommitProbeFailure(
            service,
            FileTransferDirection.Outbound,
            transferId,
            $"session_{transferId}",
            snapshot,
            "unit_test_replaced_probe_id",
            "unit_test_stale_probe_id_failure");

        Assert.Equal(
            Assert.IsType<FileTransferRouteTransitionPhysicalDrainCommand>(
                staged.Drain),
            GetCurrentFileTransferRouteTransitionDrain(context));
        Assert.Equal(owner, GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"));
        Assert.Equal(publication, GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken"));
        Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockRouteCommitToken"));
    }

    [Theory]
    [InlineData("terminal", "filetransfer_route_transition_execution_stale")]
    [InlineData("runtime_owner", "filetransfer_route_transition_execution_stale")]
    [InlineData("route_publication", "filetransfer_route_transition_execution_stale")]
    [InlineData("leg", "filetransfer_route_transition_execution_stale")]
    [InlineData("transport", "filetransfer_route_transition_execution_stale")]
    [InlineData("bridge", "filetransfer_route_transition_execution_stale")]
    [InlineData("data_session", "filetransfer_route_transition_execution_stale")]
    [InlineData("transaction", "filetransfer_route_transition_activation_proof_mismatch")]
    [InlineData("offer", "filetransfer_route_transition_activation_proof_mismatch")]
    [InlineData("path_lease", "filetransfer_route_transition_activation_proof_mismatch")]
    [InlineData("path_lease_listener", "filetransfer_route_transition_activation_proof_mismatch")]
    [InlineData("probe_failure", "filetransfer_route_transition_activation_proof_mismatch")]
    [InlineData("target_route", "filetransfer_route_transition_activation_proof_mismatch")]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public void OutboundRegularV4WriteHandoff_DrainRejectsInvalidatedCoordinatorTuple(
        string invalidation,
        string expectedReason)
    {
        var transferId = $"transfer_regular_v4_send_drain_{invalidation}_{Guid.NewGuid():N}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 64,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var dataSession = new RecordingDataSession($"session_{transferId}", transferId);
        ReplaceOutboundDataSession(service, context, dataSession);
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "tuna_activation_answer_ack");
        var staged = StageRegularV4RouteCommitSendDrain(
            service,
            context,
            "tuna_activation_answer_ack");
        Assert.True(staged.Accepted);
        Assert.NotNull(staged.Drain);
        var current = IsRegularV4RouteCommitSendDrainCurrent(service, context, staged.Drain!);
        Assert.True(current.Accepted, current.Reason);

        InvalidateRegularV4RouteCommitSendDrainTuple(context, transport, invalidation);
        var completed = CompleteRegularV4RouteCommitSendDrain(
            service,
            context,
            staged.Drain!,
            "unit_test_invalidated_drain");

        Assert.False(completed.Accepted);
        Assert.Equal(expectedReason, completed.Reason);
        Assert.Equal(
            Assert.IsType<FileTransferRouteTransitionPhysicalDrainCommand>(
                staged.Drain),
            GetCurrentFileTransferRouteTransitionDrain(context));
        Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockRouteCommitToken"));
    }

    [Theory]
    [InlineData("terminal", "transfer_terminal")]
    [InlineData("runtime_owner", "runtime_owner_replaced")]
    [InlineData("route_publication", "route_publication_replaced")]
    [InlineData("leg", "coordinator_tuple_mismatch")]
    [InlineData("data_session", "data_session_replaced")]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public void OutboundSparseWorker_CommitRejectsInvalidatedCoordinatorTuple(
        string invalidation,
        string expectedReason)
    {
        using var harness = new SparseWorkerHarness($"transfer_sparse_worker_commit_{invalidation}");
        var staged = StageSparsePumpInstall(
            harness.Service,
            harness.Context,
            predecessor: null,
            "unit_test_stage");
        Assert.True(staged.Accepted);
        Assert.NotNull(staged.Install);
        var pending = staged.Install!;

        InvalidateSparsePumpInstallTuple(harness, invalidation);

        var committed = CommitSparsePumpInstall(
            harness.Service,
            harness.Context,
            pending,
            harness.DataSession);

        Assert.False(committed.Accepted);
        Assert.Null(committed.Install);
        Assert.Equal(expectedReason, committed.Reason);
        Assert.Null(GetPrivatePropertyValue(harness.Context, "PendingOutboundSparsePumpInstall"));
        Assert.Null(GetPrivatePropertyValue(harness.Context, "InstalledOutboundSparsePump"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseWorker_DuplicateStageAndCommitAreIdempotent()
    {
        var suffix = new string(
            Enumerable.Range(0, 12)
                .Select(static _ => (char)('g' + Random.Shared.Next(20)))
                .ToArray());
        var transferId = $"transfer_sparse_worker_idempotent_{suffix}";
        using var harness = new SparseWorkerHarness(transferId);

        var firstStage = StageSparsePumpInstall(
            harness.Service,
            harness.Context,
            predecessor: null,
            "unit_test_stage");
        Assert.NotNull(firstStage.Install);
        var pending = firstStage.Install!;
        Assert.True(firstStage.Accepted);
        Assert.Equal("accepted", firstStage.Reason);

        var duplicateStage = StageSparsePumpInstall(
            harness.Service,
            harness.Context,
            predecessor: null,
            "ignored_duplicate_reason");
        Assert.True(duplicateStage.Accepted);
        Assert.Equal("idempotent", duplicateStage.Reason);
        Assert.Same(pending, duplicateStage.Install);
        Assert.Equal(1L, GetPrivateProperty<long>(harness.Context, "LastOutboundSparsePumpInstallGeneration"));

        var firstCommit = CommitSparsePumpInstall(
            harness.Service,
            harness.Context,
            pending,
            harness.DataSession);
        Assert.True(firstCommit.Accepted);
        Assert.Equal("accepted", firstCommit.Reason);
        Assert.Same(pending, firstCommit.Install);

        var duplicateCommit = CommitSparsePumpInstall(
            harness.Service,
            harness.Context,
            pending,
            harness.DataSession);
        Assert.True(duplicateCommit.Accepted);
        Assert.Equal("idempotent", duplicateCommit.Reason);
        Assert.Same(pending, duplicateCommit.Install);
        Assert.Null(GetPrivatePropertyValue(harness.Context, "PendingOutboundSparsePumpInstall"));
        Assert.Same(pending, GetPrivatePropertyValue(harness.Context, "InstalledOutboundSparsePump"));

        string ReadScopedLog() =>
            FilterV4SenderTransferLog(LocalOperationalLog.GetRecentLogText(), transferId);
        await WaitUntilAsync(
            () => ReadScopedLog().Contains(
                "event=filetransfer_outbound_sparse_worker_handoff_installed;",
                StringComparison.Ordinal),
            timeoutMs: 5000);
        var log = ReadScopedLog();
        Assert.Equal(1, CountOccurrences(log, "event=filetransfer_outbound_sparse_worker_handoff_required;"));
        Assert.Equal(1, CountOccurrences(log, "event=filetransfer_outbound_sparse_worker_handoff_installed;"));
        Assert.Contains("installation_reason=unit_test_stage;", log, StringComparison.Ordinal);
        Assert.Contains("descriptor_terminal_generation=", log, StringComparison.Ordinal);
        Assert.Contains("runtime_owner_created_reason=", log, StringComparison.Ordinal);
        Assert.Contains("route_publication_action_kind=", log, StringComparison.Ordinal);
        Assert.Contains("route_publication_reason=", log, StringComparison.Ordinal);
        Assert.Contains("route_publication_owner_runtime_generation=", log, StringComparison.Ordinal);
        Assert.DoesNotContain("event=filetransfer_outbound_sparse_worker_handoff_rejected;", log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceive_PendingInstallStagesProofAndCommitIsTheOnlyDispatchBoundary(
        bool invalidateBeforeCommit)
    {
        var transferSuffix = new string(
            Enumerable.Range(0, 12)
                .Select(static _ => (char)('g' + Random.Shared.Next(20)))
                .ToArray());
        var transferId = $"transfer_sparse_staged_frontier_{invalidateBeforeCommit}_{transferSuffix}";
        var requestCorrelationId = invalidateBeforeCommit
            ? "frq-pending-reject"
            : "frq-pending-commit";
        var repairRequestId = invalidateBeforeCommit
            ? "pending-reject"
            : "pending-commit";
        var installationReason = invalidateBeforeCommit
            ? "unit_test_pending_install_reject"
            : "unit_test_pending_install_commit";
        using var harness = new SparseWorkerHarness(transferId);
        var initialStage = StageSparsePumpInstall(
            harness.Service,
            harness.Context,
            predecessor: null,
            "unit_test_initial_install");
        Assert.True(initialStage.Accepted, initialStage.Reason);
        Assert.NotNull(initialStage.Install);
        var predecessor = initialStage.Install!;
        Assert.True(CommitSparsePumpInstall(
            harness.Service,
            harness.Context,
            predecessor,
            harness.DataSession).Accepted);

        object pending;
        lock (GetServiceGate(harness.Service))
        {
            ReplaceSparseAuthorityRetainingOwner(harness.Context, "route_publication");
            var successorStage = StageSparsePumpInstall(
                harness.Service,
                harness.Context,
                predecessor,
                installationReason);
            Assert.True(successorStage.Accepted, successorStage.Reason);
            Assert.NotNull(successorStage.Install);
            pending = successorStage.Install!;
            SetPrivateProperty(harness.Context, "ActiveOutboundSparseReceiveSupervisorGeneration", 1L);
        }

        var pendingInstallGeneration = GetPrivateProperty<long>(pending, "InstallGeneration");
        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = GetPrivateProperty<string>(harness.Context, "SessionId"),
            TransferId = transferId,
            TransportEpoch = 3,
            RepairRequestId = repairRequestId,
            RequestCorrelationId = requestCorrelationId,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 100, ChunkCount = 1 }],
            Priority = "frontier",
            RecoveryMode = "frontier_repair_only",
        };
        var initialFrontier = GetPrivateProperty<int>(harness.Context, "RemoteNextExpectedChunkIndex");
        var initialQueueCount = Assert.IsAssignableFrom<System.Collections.ICollection>(
            GetPrivatePropertyValue(harness.Context, "PullV4SenderPumpRepairQueue")).Count;
        var logStart = GetOperationalLogLength();
        string ReadTestLog() =>
            FilterV4SenderTransferLog(
                ReadOperationalLogTail(logStart) + Environment.NewLine + LocalOperationalLog.GetRecentLogText(),
                transferId);
        static string[] UniqueLogLines(string log) => log
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Split("; log_writer_id=", 2, StringSplitOptions.None)[0])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        typeof(SessionFileTransferService)
            .GetMethod("ApplyOutboundV6RepairRequestCore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                harness.Service,
                [
                    harness.Context,
                    request,
                    FileTransferTransportKind.RegularNkn,
                    pending,
                    GetPrivateProperty<RuntimeOwnerToken>(pending, "RuntimeOwner"),
                    harness.DataSession,
                    GetPrivateProperty<long>(harness.Context, "DataSessionGeneration"),
                    1L,
                ]);

        await WaitUntilAsync(
            () => UniqueLogLines(ReadTestLog()).Any(line =>
                line.Contains(
                    "event=filetransfer_v6_frontier_request_receive_dispatch_staged;",
                    StringComparison.Ordinal) &&
                line.Contains(
                    $"request_correlation_id={requestCorrelationId};",
                    StringComparison.Ordinal)),
            timeoutMs: 5000);
        var stagedLog = ReadTestLog();
        var stagedCorrelationLines = UniqueLogLines(stagedLog)
            .Where(line => line.Contains(
                $"request_correlation_id={requestCorrelationId};",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Single(stagedCorrelationLines, line => line.Contains(
            "event=filetransfer_v6_frontier_request_receive_dispatch_staged;",
            StringComparison.Ordinal));
        Assert.DoesNotContain(stagedCorrelationLines, line => line.Contains(
            "event=filetransfer_v6_frontier_request_receive_dispatched;",
            StringComparison.Ordinal));
        Assert.DoesNotContain(UniqueLogLines(stagedLog), line =>
            line.Contains("event=filetransfer_outbound_sparse_worker_handoff_installed;", StringComparison.Ordinal) &&
            line.Contains($"installation_reason={installationReason};", StringComparison.Ordinal));
        Assert.Equal(initialFrontier, GetPrivateProperty<int>(harness.Context, "RemoteNextExpectedChunkIndex"));
        Assert.Equal(initialQueueCount, Assert.IsAssignableFrom<System.Collections.ICollection>(
            GetPrivatePropertyValue(harness.Context, "PullV4SenderPumpRepairQueue")).Count);

        if (invalidateBeforeCommit)
        {
            lock (GetServiceGate(harness.Service))
            {
                ReplaceSparseAuthorityRetainingOwner(harness.Context, "route_publication");
            }
        }

        var commit = CommitSparsePumpInstall(
            harness.Service,
            harness.Context,
            pending,
            harness.DataSession);
        await WaitUntilAsync(
            () => UniqueLogLines(ReadTestLog()).Any(line =>
                line.Contains(
                    invalidateBeforeCommit
                        ? "event=filetransfer_v6_frontier_request_receive_dispatch_rejected;"
                        : "event=filetransfer_v6_frontier_request_receive_dispatched;",
                    StringComparison.Ordinal) &&
                line.Contains(
                    $"request_correlation_id={requestCorrelationId};",
                    StringComparison.Ordinal)),
            timeoutMs: 5000);
        var finalLog = ReadTestLog();
        if (invalidateBeforeCommit)
        {
            Assert.False(commit.Accepted);
            Assert.DoesNotContain(UniqueLogLines(finalLog), line =>
                line.Contains("event=filetransfer_outbound_sparse_worker_handoff_installed;", StringComparison.Ordinal) &&
                line.Contains($"installation_reason={installationReason};", StringComparison.Ordinal));
            Assert.DoesNotContain(
                UniqueLogLines(finalLog)
                    .Where(line => line.Contains(
                        $"request_correlation_id={requestCorrelationId};",
                        StringComparison.Ordinal)),
                line => line.Contains(
                    "event=filetransfer_v6_frontier_request_receive_dispatched;",
                    StringComparison.Ordinal));
            Assert.Equal(initialFrontier, GetPrivateProperty<int>(harness.Context, "RemoteNextExpectedChunkIndex"));
            Assert.Equal(initialQueueCount, Assert.IsAssignableFrom<System.Collections.ICollection>(
                GetPrivatePropertyValue(harness.Context, "PullV4SenderPumpRepairQueue")).Count);
            return;
        }

        Assert.True(commit.Accepted, commit.Reason);
        var lines = UniqueLogLines(finalLog);
        var installedIndex = Array.FindIndex(lines, line =>
            line.Contains("event=filetransfer_outbound_sparse_worker_handoff_installed;", StringComparison.Ordinal) &&
            line.Contains($"install_generation={pendingInstallGeneration};", StringComparison.Ordinal) &&
            line.Contains($"installation_reason={installationReason};", StringComparison.Ordinal));
        var dispatchLines = lines
            .Select((line, index) => (line, index))
            .Where(pair =>
                pair.line.Contains("event=filetransfer_v6_frontier_request_receive_dispatched;", StringComparison.Ordinal) &&
                pair.line.Contains($"request_correlation_id={requestCorrelationId};", StringComparison.Ordinal))
            .ToArray();
        var dispatch = Assert.Single(dispatchLines);
        Assert.True(installedIndex >= 0, finalLog);
        Assert.True(installedIndex < dispatch.index);
        var installedLine = lines[installedIndex];
        var installedDescriptorStart = installedLine.IndexOf("install_generation=", StringComparison.Ordinal);
        var installedDescriptorEnd = installedLine.IndexOf("; decision_reason=", installedDescriptorStart, StringComparison.Ordinal);
        var dispatchDescriptorStart = dispatch.line.IndexOf("install_generation=", StringComparison.Ordinal);
        var dispatchDescriptorEnd = dispatch.line.IndexOf("; transport_epoch=", dispatchDescriptorStart, StringComparison.Ordinal);
        Assert.True(installedDescriptorStart >= 0 && installedDescriptorEnd > installedDescriptorStart);
        Assert.True(dispatchDescriptorStart >= 0 && dispatchDescriptorEnd > dispatchDescriptorStart);
        Assert.Equal(
            installedLine[installedDescriptorStart..installedDescriptorEnd],
            dispatch.line[dispatchDescriptorStart..dispatchDescriptorEnd]);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceive_DrainsMoreThanPriorityReserveWhileSuccessorSourceReopenIsBlocked()
    {
        var transferId = $"transfer_sparse_blocked_reopen_drain_{Guid.NewGuid():N}";
        var sessionId = $"session_{transferId}";
        const int frontierFrameCount = 80;
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[128 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: 128);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var sourceReopenReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSourceReopen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousBarrier = SessionFileTransferService.OutboundSparseSourceReopenBarrierForTests;
        var supervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            await WaitUntilAsync(
                () => transport.GetDataSessionReceiveStateForTests(transferId).Active &&
                      GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is not null,
                timeoutMs: 5000);
            var predecessor = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump")!;
            var predecessorGeneration = GetPrivateProperty<long>(predecessor, "InstallGeneration");
            var sessionOpenCount = transport.SentSessionOpens.Count(open => open.TransferId == transferId);
            var manifestCount = transport.SentDataFrames.OfType<FileTransferManifestFrameV6>()
                .Count(frame => frame.TransferId == transferId);
            var receiveOrdinal = transport.GetDataSessionReceiveStateForTests(transferId).Ordinal;
            var logStart = GetOperationalLogLength();
            SessionFileTransferService.OutboundSparseSourceReopenBarrierForTests = async (observedTransferId, _) =>
            {
                if (!string.Equals(observedTransferId, transferId, StringComparison.Ordinal))
                {
                    return;
                }

                sourceReopenReached.TrySetResult();
                await releaseSourceReopen.Task;
            };

            RoutePublicationToken successorPublication;
            lock (GetServiceGate(service))
            {
                successorPublication = ReplaceSparseAuthorityRetainingOwner(context, "route_publication");
            }

            await sourceReopenReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < frontierFrameCount; index++)
            {
                transport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
                transport.ReceiveDeliveredDataFrame(new FileTransferFrontierRequestFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = 3,
                    RepairRequestId = $"blocked-reopen-frontier-{index}",
                    RequestCorrelationId = $"frq-blocked-reopen-frontier-{index}",
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                    Priority = "frontier",
                    RecoveryMode = "frontier_repair_only",
                });
                if ((index + 1) % 8 == 0)
                {
                    var expectedOrdinal = receiveOrdinal + index + 1;
                    await WaitUntilAsync(
                        () => transport.GetDataSessionReceiveStateForTests(transferId).Ordinal >= expectedOrdinal,
                        timeoutMs: 5000);
                }
            }

            await WaitUntilAsync(
                () => transport.GetDataSessionReceiveStateForTests(transferId).Active &&
                      transport.GetDataSessionReceiveStateForTests(transferId).Ordinal >=
                      receiveOrdinal + frontierFrameCount,
                timeoutMs: 5000);
            Assert.False(releaseSourceReopen.Task.IsCompleted);
            Assert.Equal(1, Assert.IsAssignableFrom<System.Collections.IDictionary>(
                GetPrivatePropertyValue(context, "PendingOutboundSparseFrontierDispatchProofs")).Count);
            Assert.Empty(Assert.IsAssignableFrom<System.Collections.ICollection>(
                GetPrivatePropertyValue(context, "PullV4SenderPumpRepairQueue")).Cast<object>());
            Assert.Equal(0, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
            var stagedLog = LocalOperationalLog.GetRecentLogText();
            var stagedFrameLines = stagedLog
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("request_correlation_id=frq-blocked-reopen-frontier-", StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(
                frontierFrameCount,
                stagedFrameLines.Count(line => line.Contains(
                    "event=filetransfer_v6_frontier_request_receive_dispatch_staged;",
                    StringComparison.Ordinal)));
            Assert.DoesNotContain(stagedFrameLines, line => line.Contains(
                "event=filetransfer_v6_frontier_request_receive_dispatched;",
                StringComparison.Ordinal));

            releaseSourceReopen.TrySetResult();
            await WaitUntilAsync(
                () => GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is { } installed &&
                      GetPrivateProperty<long>(installed, "InstallGeneration") > predecessorGeneration &&
                      GetPrivateProperty<RoutePublicationToken>(installed, "RoutePublication") == successorPublication &&
                      GetPrivateProperty<int>(context, "ChunksAcceptedForTransport") >= 1,
                timeoutMs: 5000);

            var finalLog = LocalOperationalLog.GetRecentLogText();
            var finalFrameLines = finalLog
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("request_correlation_id=frq-blocked-reopen-frontier-", StringComparison.Ordinal))
                .ToArray();
            Assert.Single(finalFrameLines, line => line.Contains(
                "event=filetransfer_v6_frontier_request_receive_dispatched;",
                StringComparison.Ordinal));
            Assert.Contains(
                $"request_correlation_id=frq-blocked-reopen-frontier-{frontierFrameCount - 1};",
                finalLog,
                StringComparison.Ordinal);
            Assert.DoesNotContain("priority_control_queue_capacity_exceeded", finalLog, StringComparison.Ordinal);
            Assert.Equal(sessionOpenCount, transport.SentSessionOpens.Count(open => open.TransferId == transferId));
            Assert.Equal(
                manifestCount,
                transport.SentDataFrames.OfType<FileTransferManifestFrameV6>()
                    .Count(frame => frame.TransferId == transferId));
            Assert.True(transport.GetDataSessionReceiveStateForTests(transferId).Active);
        }
        finally
        {
            releaseSourceReopen.TrySetResult();
            SessionFileTransferService.OutboundSparseSourceReopenBarrierForTests = previousBarrier;
            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceive_NormalPumpCompletionKeepsSupervisorForLaterRepair()
    {
        var transferId = $"transfer_sparse_normal_completion_repair_{Guid.NewGuid():N}";
        var sessionId = $"session_{transferId}";
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var previousCompletion = SessionFileTransferService.OutboundSparseSenderPumpNormalCompletionForTests;
        var allowCompletion = 0;
        SessionFileTransferService.OutboundSparseSenderPumpNormalCompletionForTests = (observedTransferId, _) =>
            string.Equals(observedTransferId, transferId, StringComparison.Ordinal) &&
            Volatile.Read(ref allowCompletion) == 1 &&
            Interlocked.Exchange(ref allowCompletion, 0) == 1;
        var logStart = GetOperationalLogLength();
        var supervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            await WaitUntilAsync(
                () => transport.GetDataSessionReceiveStateForTests(transferId).Active &&
                      GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is not null,
                timeoutMs: 5000);
            const int seededChunks = 2;
            const long seededBytes = seededChunks * 21L * 1024;
            lock (GetServiceGate(service))
            {
                SetPrivateProperty(context, "RemoteNextExpectedChunkIndex", seededChunks);
                SetPrivateProperty(context, "RemoteGrantedUntilExclusive", seededChunks);
                SetPrivateProperty(context, "ChunksAcceptedForTransport", seededChunks);
                SetPrivateProperty(context, "BytesAcceptedForTransport", seededBytes);
                SetPrivateProperty(context, "ChunksTransferred", seededChunks);
                SetPrivateProperty(context, "BytesTransferred", seededBytes);
                SetPrivateProperty(context, "PullTransportFrontierOnlyRepairStartChunkIndex", seededChunks);
            }

            Volatile.Write(ref allowCompletion, 1);
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart).Contains(
                    "event=filetransfer_v4_receive_loop_kept_alive_after_sender_pump_completed;",
                    StringComparison.Ordinal),
                timeoutMs: 5000);
            var completedInstall = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump")!;
            var completedInstallGeneration = GetPrivateProperty<long>(completedInstall, "InstallGeneration");
            var sessionOpenCount = transport.SentSessionOpens.Count(open => open.TransferId == transferId);
            var manifestCount = transport.SentDataFrames.OfType<FileTransferManifestFrameV6>()
                .Count(frame => frame.TransferId == transferId);
            var chunksAcceptedBeforeRepair = GetPrivateProperty<int>(context, "ChunksAcceptedForTransport");
            var bytesAcceptedBeforeRepair = GetPrivateProperty<long>(context, "BytesAcceptedForTransport");
            var chunksTransferredBeforeRepair = GetPrivateProperty<int>(context, "ChunksTransferred");
            var bytesTransferredBeforeRepair = GetPrivateProperty<long>(context, "BytesTransferred");
            Assert.Equal(seededChunks, chunksAcceptedBeforeRepair);
            Assert.Equal(seededBytes, bytesAcceptedBeforeRepair);
            Assert.Equal(seededChunks, chunksTransferredBeforeRepair);
            Assert.Equal(seededBytes, bytesTransferredBeforeRepair);

            transport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
            transport.ReceiveDeliveredDataFrame(new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 3,
                RepairRequestId = "repair-after-normal-pump-completion",
                RequestCorrelationId = "frq-repair-after-normal-pump-completion",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = seededChunks, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            });

            await WaitUntilAsync(
                () => GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is { } installed &&
                      GetPrivateProperty<long>(installed, "InstallGeneration") > completedInstallGeneration &&
                      GetPrivateProperty<int>(context, "ChunksAcceptedForTransport") >= seededChunks + 1,
                timeoutMs: 5000);
            Assert.True(transport.GetDataSessionReceiveStateForTests(transferId).Active);
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
            Assert.Equal(sessionOpenCount, transport.SentSessionOpens.Count(open => open.TransferId == transferId));
            Assert.Equal(
                manifestCount,
                transport.SentDataFrames.OfType<FileTransferManifestFrameV6>()
                    .Count(frame => frame.TransferId == transferId));
            Assert.True(GetPrivateProperty<int>(context, "ChunksAcceptedForTransport") >= chunksAcceptedBeforeRepair);
            Assert.True(GetPrivateProperty<long>(context, "BytesAcceptedForTransport") >= bytesAcceptedBeforeRepair);
            Assert.True(GetPrivateProperty<int>(context, "ChunksTransferred") >= chunksTransferredBeforeRepair);
            Assert.True(GetPrivateProperty<long>(context, "BytesTransferred") >= bytesTransferredBeforeRepair);
        }
        finally
        {
            SessionFileTransferService.OutboundSparseSenderPumpNormalCompletionForTests = previousCompletion;
            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public void OutboundSparseWorker_SuccessorRequiresInstalledPredecessorThroughCommit()
    {
        using var harness = new SparseWorkerHarness("transfer_sparse_worker_predecessor");
        var initialStage = StageSparsePumpInstall(
            harness.Service,
            harness.Context,
            predecessor: null,
            "unit_test_initial");
        Assert.NotNull(initialStage.Install);
        var initial = initialStage.Install!;
        Assert.True(CommitSparsePumpInstall(
            harness.Service,
            harness.Context,
            initial,
            harness.DataSession).Accepted);

        var missingPredecessor = StageSparsePumpInstall(
            harness.Service,
            harness.Context,
            predecessor: null,
            "unit_test_missing_predecessor");
        Assert.False(missingPredecessor.Accepted);
        Assert.Null(missingPredecessor.Install);
        Assert.Equal("predecessor_install_missing", missingPredecessor.Reason);
        Assert.Same(initial, GetPrivatePropertyValue(harness.Context, "InstalledOutboundSparsePump"));

        var successorStage = StageSparsePumpInstall(
            harness.Service,
            harness.Context,
            initial,
            "unit_test_successor");
        Assert.True(successorStage.Accepted);
        Assert.NotNull(successorStage.Install);
        var successor = successorStage.Install!;
        Assert.Equal(2L, GetPrivateProperty<long>(successor, "InstallGeneration"));
        Assert.Equal(1L, GetPrivateProperty<long>(successor, "PredecessorInstallGeneration"));
        Assert.True(CommitSparsePumpInstall(
            harness.Service,
            harness.Context,
            successor,
            harness.DataSession).Accepted);

        var lateStage = StageSparsePumpInstall(
            harness.Service,
            harness.Context,
            successor,
            "unit_test_late_successor");
        Assert.True(lateStage.Accepted);
        Assert.NotNull(lateStage.Install);
        var lateSuccessor = lateStage.Install!;
        Assert.Equal(3L, GetPrivateProperty<long>(lateSuccessor, "InstallGeneration"));
        Assert.Equal(2L, GetPrivateProperty<long>(lateSuccessor, "PredecessorInstallGeneration"));

        var supersedingInstall = CreateSparsePumpInstall(
            harness.Context,
            installGeneration: 4,
            predecessorInstallGeneration: 3);
        SetPrivateProperty(harness.Context, "InstalledOutboundSparsePump", supersedingInstall);

        var rejected = CommitSparsePumpInstall(
            harness.Service,
            harness.Context,
            lateSuccessor,
            harness.DataSession);
        Assert.False(rejected.Accepted);
        Assert.Equal("predecessor_install_mismatch", rejected.Reason);
        Assert.Same(supersedingInstall, GetPrivatePropertyValue(harness.Context, "InstalledOutboundSparsePump"));
        Assert.Null(GetPrivatePropertyValue(harness.Context, "PendingOutboundSparsePumpInstall"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseWorker_CurrentOwnerFailureRetainsTerminalBehavior()
    {
        const string transferId = "transfer_sparse_worker_current_owner_failure";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreatePostTunaFallbackV6OutboundContext(transferId);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var currentOwner = Assert.IsType<RuntimeOwnerToken>(
            GetPrivatePropertyValue(context, "CurrentRuntimeOwner"));

        var failure = Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
            .GetMethod("FailOutboundV4Async", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    null,
                    currentOwner,
                    FileTransferResultCodes.ReadFailed,
                    "Injected current-owner read failure.",
                    false,
                    null,
                ]));
        await failure;

        Assert.True(GetPrivateProperty<bool>(context, "IsTerminal"));
        Assert.Equal(FileTransferTransferState.Failed, GetPrivateProperty<FileTransferTransferState>(context, "State"));
        Assert.Equal(FileTransferResultCodes.ReadFailed, GetPrivateProperty<string>(context, "ErrorCode"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public void OutboundSparseWorker_DataSessionReplacementInvalidatesPendingAndInstalledBindings()
    {
        const string transferId = "transfer_sparse_worker_data_session_replaced";
        using var service = new SessionFileTransferService();
        var context = CreatePostTunaFallbackV6OutboundContext(transferId);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var initialDataSessionGeneration =
            GetPrivateProperty<long>(context, "DataSessionGeneration");
        var firstSession = new RecordingDataSession($"session_{transferId}", transferId);
        ReplaceOutboundDataSession(service, context, firstSession);
        Assert.Equal(
            initialDataSessionGeneration + 1,
            GetPrivateProperty<long>(context, "DataSessionGeneration"));

        var install = CreateSparsePumpInstall(context, installGeneration: 1, predecessorInstallGeneration: 0);
        SetPrivateProperty(context, "PendingOutboundSparsePumpInstall", install);
        SetPrivateProperty(context, "InstalledOutboundSparsePump", install);

        ReplaceOutboundDataSession(service, context, firstSession);
        Assert.Equal(
            initialDataSessionGeneration + 1,
            GetPrivateProperty<long>(context, "DataSessionGeneration"));
        Assert.Same(install, GetPrivatePropertyValue(context, "PendingOutboundSparsePumpInstall"));
        Assert.Same(install, GetPrivatePropertyValue(context, "InstalledOutboundSparsePump"));

        var secondSession = new RecordingDataSession($"session_{transferId}", transferId);
        ReplaceOutboundDataSession(service, context, secondSession);

        Assert.Equal(
            initialDataSessionGeneration + 2,
            GetPrivateProperty<long>(context, "DataSessionGeneration"));
        Assert.Null(GetPrivatePropertyValue(context, "PendingOutboundSparsePumpInstall"));
        Assert.Null(GetPrivatePropertyValue(context, "InstalledOutboundSparsePump"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceiveSupervisor_NonterminalExitCancelsAndObservesActiveReceive()
    {
        const string transferId = "transfer_sparse_receive_supervisor_nonterminal_exit";
        var sessionId = $"session_{transferId}";
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var peerTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(peerTransport);
        using var service = new SessionFileTransferService();
        service.AttachTransport(senderTransport);
        var context = CreatePostTunaFallbackV6OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        using var dataSession = await senderTransport.OpenFileTransferDataSessionAsync(
            sessionId,
            transferId,
            CancellationToken.None);
        ReplaceOutboundDataSession(service, context, dataSession);

        var runtimeKindType = typeof(SessionFileTransferService)
            .GetNestedType("FileTransferSparseCreditRuntimeKind", BindingFlags.NonPublic)!;
        var runtimeKind = Enum.Parse(runtimeKindType, "PrimaryRegularNknV4");
        var supervisor = Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
            .GetMethod("RunOutboundSparseCreditSenderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, runtimeKind]));

        await WaitUntilAsync(
            () => senderTransport.GetDataSessionReceiveStateForTests(transferId).Active,
            timeoutMs: 5000);
        var activeReceive = senderTransport.GetDataSessionReceiveStateForTests(transferId);
        Assert.True(activeReceive.Active);

        // Losing the current session is a nonterminal supervisor exit. The outstanding
        // receive must be canceled and observed before the supervisor reports that it stopped.
        SetPrivateProperty(context, "DataSession", null);
        await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => !senderTransport.GetDataSessionReceiveStateForTests(transferId).Active,
            timeoutMs: 5000);

        Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
        Assert.Equal(FileTransferTransferState.Sending, GetPrivateProperty<FileTransferTransferState>(context, "State"));
        Assert.Empty(senderTransport.SentErrors.Where(error => error.TransferId == transferId));
        Assert.Empty(senderTransport.SentCancels.Where(cancel => cancel.TransferId == transferId));
        Assert.Equal(0L, GetPrivateProperty<long>(context, "ActiveOutboundSparseReceiveSupervisorGeneration"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseWorker_SendCompletionSupersededBeforeMutationCannotAlterSuccessor()
    {
        const string transferId = "transfer_sparse_send_completion_superseded";
        using var harness = new SparseWorkerHarness(transferId);
        SetPrivateProperty(harness.Context, "RemoteNextExpectedChunkIndex", 0);
        SetPrivateProperty(harness.Context, "RemoteGrantedUntilExclusive", 8);
        SetPrivateProperty(harness.Context, "ChunksAcceptedForTransport", 0);
        SetPrivateProperty(harness.Context, "BytesAcceptedForTransport", 0L);
        SetPrivateProperty(harness.Context, "ChunkCount", 8);

        var staged = StageSparsePumpInstall(
            harness.Service,
            harness.Context,
            predecessor: null,
            "unit_test_send_completion");
        Assert.NotNull(staged.Install);
        var initialInstall = staged.Install!;
        Assert.True(CommitSparsePumpInstall(
            harness.Service,
            harness.Context,
            initialInstall,
            harness.DataSession).Accepted);

        var completionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousBarrier = SessionFileTransferService.OutboundSparseSendCompletionBarrierForTests;
        var logStart = GetOperationalLogLength();
        SessionFileTransferService.OutboundSparseSendCompletionBarrierForTests = async (observedTransferId, _) =>
        {
            if (!string.Equals(observedTransferId, transferId, StringComparison.Ordinal))
            {
                return;
            }

            completionReached.TrySetResult();
            await releaseCompletion.Task;
        };

        try
        {
            using var source = new MemoryStream(new byte[8 * 21 * 1024], writable: false);
            var sendTask = Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
                .GetMethod("SendChunkIndicesV4Async", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(
                    harness.Service,
                    [
                        harness.Context,
                        source,
                        harness.DataSession,
                        new List<int> { 0 },
                        false,
                        "(none)",
                        null,
                        null,
                        null,
                        FileTransferV4RepairDeliveryMode.BulkOnly,
                        "normal",
                        null,
                        null,
                        null,
                        null,
                        null,
                        initialInstall,
                        CancellationToken.None,
                    ]));
            await completionReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(harness.DataSession.SentFrames.OfType<FileTransferChunkBatchFrameV6>());

            var successorInstall = CreateSparsePumpInstall(
                harness.Context,
                installGeneration: 2,
                predecessorInstallGeneration: 1);
            SetPrivateProperty(harness.Context, "LastOutboundSparsePumpInstallGeneration", 2L);
            SetPrivateProperty(harness.Context, "InstalledOutboundSparsePump", successorInstall);

            var expectedChunksAccepted = GetPrivateProperty<int>(harness.Context, "ChunksAcceptedForTransport");
            var expectedBytesAccepted = GetPrivateProperty<long>(harness.Context, "BytesAcceptedForTransport");
            var expectedRawBytes = GetPrivateProperty<long>(harness.Context, "PullSenderRawBytesTotal");
            var expectedBatchFrames = GetPrivateProperty<int>(harness.Context, "PullSenderBatchFramesTotal");
            var expectedChunkCount = GetPrivateProperty<int>(harness.Context, "PullSenderChunkCountTotal");
            var expectedCompletedFrames = GetPrivateProperty<int>(harness.Context, "PullSenderPipelineCompletedFramesRecent");
            var expectedFailedFrames = GetPrivateProperty<int>(harness.Context, "PullSenderPipelineFailedFramesTotal");

            releaseCompletion.TrySetResult();
            await sendTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Same(successorInstall, GetPrivatePropertyValue(harness.Context, "InstalledOutboundSparsePump"));
            Assert.Equal(expectedChunksAccepted, GetPrivateProperty<int>(harness.Context, "ChunksAcceptedForTransport"));
            Assert.Equal(expectedBytesAccepted, GetPrivateProperty<long>(harness.Context, "BytesAcceptedForTransport"));
            Assert.Equal(expectedRawBytes, GetPrivateProperty<long>(harness.Context, "PullSenderRawBytesTotal"));
            Assert.Equal(expectedBatchFrames, GetPrivateProperty<int>(harness.Context, "PullSenderBatchFramesTotal"));
            Assert.Equal(expectedChunkCount, GetPrivateProperty<int>(harness.Context, "PullSenderChunkCountTotal"));
            Assert.Equal(expectedCompletedFrames, GetPrivateProperty<int>(harness.Context, "PullSenderPipelineCompletedFramesRecent"));
            Assert.Equal(expectedFailedFrames, GetPrivateProperty<int>(harness.Context, "PullSenderPipelineFailedFramesTotal"));
            Assert.Equal(0, GetPrivateProperty<int>(harness.Context, "PullSenderPipelineCurrentInFlightFrames"));
            Assert.Equal(0L, GetPrivateProperty<long>(harness.Context, "PullSenderPipelineCurrentInFlightBytes"));
            Assert.Equal(0, Assert.IsAssignableFrom<System.Collections.IDictionary>(
                GetPrivatePropertyValue(harness.Context, "SentAwaitingAck")).Count);
            Assert.Equal(0, Assert.IsAssignableFrom<System.Collections.IDictionary>(
                GetPrivatePropertyValue(harness.Context, "LastChunkSentUtc")).Count);
            var log = FilterV4SenderTransferLog(ReadOperationalLogTail(logStart), transferId);
            Assert.Contains(
                log.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
                static line =>
                    line.Contains("event=filetransfer_outbound_sparse_worker_handoff_rejected;", StringComparison.Ordinal) &&
                    line.Contains("stage=send_completion", StringComparison.Ordinal) &&
                    line.Contains("decision_reason=stale_pump_install", StringComparison.Ordinal));
        }
        finally
        {
            releaseCompletion.TrySetResult();
            SessionFileTransferService.OutboundSparseSendCompletionBarrierForTests = previousBarrier;
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseWorker_TailReconciliationSupersededBeforeQueueCannotAlterSuccessor()
    {
        const string transferId = "transfer_sparse_tail_reconciliation_superseded";
        using var harness = new SparseWorkerHarness(transferId);
        var serviceType = typeof(SessionFileTransferService);
        var context = harness.Context;
        var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        SetPrivateProperty(context, "ChunkCount", 6200);
        SetPrivateProperty(context, "RemoteNextExpectedChunkIndex", 2005);
        SetPrivateProperty(context, "ChunksAcceptedForTransport", 4472);
        SetPrivateProperty(context, "RemoteGrantedUntilExclusive", 2389);
        SetPrivateProperty(context, "PostTunaFallbackLiveCheckpointStallCount", 3);
        SetPrivateProperty(leg, "State", FileTransferLegState.RecoveryActive);
        SetPrivateProperty(leg, "CanSendData", true);
        SetPrivateProperty(leg, "ProvenCommittedChunkIndex", 2005);
        SetPrivateProperty(leg, "ProvenHighestObservedChunkIndex", 4471);
        SetPrivateProperty(leg, "TransportEpochId", 3L);

        var handoffType = serviceType.Assembly.GetType("NLink.Core.FileTransfer.TransportHandoffEpoch");
        Assert.NotNull(handoffType);
        var handoff = Activator.CreateInstance(handoffType!)!;
        SetPrivateProperty(handoff, "EpochId", 3L);
        SetPrivateProperty(handoff, "Kind", FileTransferTransportHandoffKind.TunaToNormalFallback);
        SetPrivateProperty(handoff, "SourceTransport", FileTransferTransportKind.Tuna);
        SetPrivateProperty(handoff, "TargetTransport", FileTransferTransportKind.RegularNkn);
        SetPrivateProperty(handoff, "Direction", FileTransferDirection.Outbound);
        SetPrivateProperty(handoff, "Reason", "unit_test_tail_reconciliation_superseded");
        SetPrivateProperty(handoff, "State", V6TransportHandoffState.FrontierRepairOnly);
        SetPrivateProperty(context, "V6TransportHandoff", handoff);

        var staged = StageSparsePumpInstall(
            harness.Service,
            context,
            predecessor: null,
            "unit_test_tail_reconciliation_predecessor");
        Assert.True(staged.Accepted, staged.Reason);
        Assert.NotNull(staged.Install);
        var predecessorInstall = staged.Install!;
        var committed = CommitSparsePumpInstall(
            harness.Service,
            context,
            predecessorInstall,
            harness.DataSession);
        Assert.True(committed.Accepted, committed.Reason);

        var queueReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseQueue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousBarrier = SessionFileTransferService.OutboundSparseTailReconciliationQueueBarrierForTests;
        SessionFileTransferService.OutboundSparseTailReconciliationQueueBarrierForTests = async (observedTransferId, _) =>
        {
            if (!string.Equals(observedTransferId, transferId, StringComparison.Ordinal))
            {
                return;
            }

            queueReached.TrySetResult();
            await releaseQueue.Task;
        };

        using var source = new MemoryStream(Array.Empty<byte>(), writable: false);
        Task? pumpTask = null;
        try
        {
            pumpTask = Assert.IsAssignableFrom<Task>(serviceType
                .GetMethod("RunOutboundV4SenderPumpAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(
                    harness.Service,
                    [
                        context,
                        source,
                        harness.DataSession,
                        CancellationToken.None,
                        predecessorInstall,
                    ]));
            await queueReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

            object successorInstall;
            lock (GetServiceGate(harness.Service))
            {
                ReplaceSparseAuthorityRetainingOwner(context, "route_publication");
                var successorStage = StageSparsePumpInstall(
                    harness.Service,
                    context,
                    predecessorInstall,
                    "unit_test_tail_reconciliation_successor");
                Assert.True(successorStage.Accepted, successorStage.Reason);
                Assert.NotNull(successorStage.Install);
                var successorCommit = CommitSparsePumpInstall(
                    harness.Service,
                    context,
                    successorStage.Install!,
                    harness.DataSession);
                Assert.True(successorCommit.Accepted, successorCommit.Reason);
                successorInstall = successorCommit.Install!;
            }

            var expectedCheckpointRequestId = GetPrivatePropertyValue(leg, "CheckpointRequestId");
            var expectedLegState = GetPrivateProperty<FileTransferLegState>(leg, "State");
            var expectedCheckpointGeneration = GetPrivateProperty<int>(leg, "CheckpointGeneration");
            var expectedSendGeneration = GetPrivateProperty<long>(context, "V6RegularNknStateRefreshSendGeneration");
            var expectedSendInFlight = GetPrivateProperty<int>(context, "V6RegularNknStateRefreshSendInFlight");
            var expectedActiveRequestId = GetPrivatePropertyValue(context, "V6RegularNknStateRefreshActiveRequestId");
            var expectedFailureCount = GetPrivateProperty<int>(context, "V6RegularNknStateRefreshFailureCount");
            var expectedLastRecovery = GetPrivatePropertyValue(context, "V6LastReceiveRecoveryRequestedUtc");
            var expectedResumePending = GetPrivateProperty<bool>(context, "PullTransportResumeRequestPending");
            var expectedEpochLivenessDeferrals = GetPrivateProperty<int>(context, "V6EpochLivenessDeferralCount");
            var expectedPeerLivenessDeferrals = GetPrivateProperty<int>(context, "V6PeerLivenessRecoveryDeferralCount");
            var expectedSentFrameCount = harness.DataSession.SentFrames.Count;

            releaseQueue.TrySetResult();
            await pumpTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Same(successorInstall, GetPrivatePropertyValue(context, "InstalledOutboundSparsePump"));
            Assert.Equal(expectedCheckpointRequestId, GetPrivatePropertyValue(leg, "CheckpointRequestId"));
            Assert.Equal(expectedLegState, GetPrivateProperty<FileTransferLegState>(leg, "State"));
            Assert.Equal(expectedCheckpointGeneration, GetPrivateProperty<int>(leg, "CheckpointGeneration"));
            Assert.Equal(expectedSendGeneration, GetPrivateProperty<long>(context, "V6RegularNknStateRefreshSendGeneration"));
            Assert.Equal(expectedSendInFlight, GetPrivateProperty<int>(context, "V6RegularNknStateRefreshSendInFlight"));
            Assert.Equal(expectedActiveRequestId, GetPrivatePropertyValue(context, "V6RegularNknStateRefreshActiveRequestId"));
            Assert.Equal(expectedFailureCount, GetPrivateProperty<int>(context, "V6RegularNknStateRefreshFailureCount"));
            Assert.Equal(expectedLastRecovery, GetPrivatePropertyValue(context, "V6LastReceiveRecoveryRequestedUtc"));
            Assert.Equal(expectedResumePending, GetPrivateProperty<bool>(context, "PullTransportResumeRequestPending"));
            Assert.Equal(expectedEpochLivenessDeferrals, GetPrivateProperty<int>(context, "V6EpochLivenessDeferralCount"));
            Assert.Equal(expectedPeerLivenessDeferrals, GetPrivateProperty<int>(context, "V6PeerLivenessRecoveryDeferralCount"));
            Assert.Equal(expectedSentFrameCount, harness.DataSession.SentFrames.Count);
            Assert.Empty(harness.DataSession.SentFrames.OfType<FileTransferFrontierRequestFrameV6>());
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
        }
        finally
        {
            releaseQueue.TrySetResult();
            SessionFileTransferService.OutboundSparseTailReconciliationQueueBarrierForTests = previousBarrier;
            if (pumpTask is { IsCompleted: false })
            {
                TryCancelContextLifetime(context);
                await pumpTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseWorker_CoordinatorResumedCheckpointAppliesDeliveredAcrossInstallReplacement()
    {
        const string transferId = "transfer_sparse_checkpoint_delivery_install_replaced";
        using var harness = new SparseWorkerHarness(transferId);
        var service = harness.Service;
        var context = harness.Context;
        var serviceType = typeof(SessionFileTransferService);
        var adoptRecovery = serviceType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "AdoptCoordinatorStateLocked" &&
                method.GetParameters().Length == 2 &&
                method.GetParameters()[0].ParameterType == context.GetType() &&
                method.GetParameters()[1].ParameterType == typeof(FileTransferReceiveRecoveryDecision));
        void AdoptRecovery(FileTransferReceiveRecoveryDecision decision)
        {
            lock (GetServiceGate(service))
            {
                adoptRecovery.Invoke(null, [context, decision]);
            }
        }
        object? successorInstall = null;
        RecordingDataSession? dataSession = null;
        dataSession = new RecordingDataSession(
            $"session_{transferId}",
            transferId,
            (_, _) =>
            {
                lock (GetServiceGate(service))
                {
                    if (successorInstall is not null)
                    {
                        return;
                    }

                    var stagedSuccessor = StageSparsePumpInstall(
                        service,
                        context,
                        GetPrivatePropertyValue(context, "InstalledOutboundSparsePump"),
                        "unit_test_checkpoint_delivery_sparse_successor");
                    Assert.True(stagedSuccessor.Accepted, stagedSuccessor.Reason);
                    var committedSuccessor = CommitSparsePumpInstall(
                        service,
                        context,
                        stagedSuccessor.Install!,
                        dataSession!);
                    Assert.True(committedSuccessor.Accepted, committedSuccessor.Reason);
                    successorInstall = committedSuccessor.Install;
                }
            });
        ReplaceOutboundDataSession(service, context, dataSession);

        var coordinatorState = CreateCoordinatorStateForTest(context);
        var owner = Assert.IsType<RuntimeOwnerToken>(coordinatorState.CurrentRuntimeOwner);
        var publication = Assert.IsType<RoutePublicationToken>(
            coordinatorState.CurrentRoutePublicationToken);
        var leg = Assert.IsType<FileTransferLeg>(coordinatorState.CurrentLeg);
        var observation = new FileTransferReceiveRecoveryObservation(
            coordinatorState.SessionId,
            coordinatorState.TransferId,
            coordinatorState.Direction,
            coordinatorState.RouteSelection.Route,
            coordinatorState.RouteSelection.TelemetryToken,
            coordinatorState.RouteSelection.ProtocolVersion,
            coordinatorState.CurrentLiveRouteEpoch?.EpochId ?? 0,
            owner,
            publication,
            coordinatorState.CurrentRouteCommit,
            coordinatorState.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            leg.CheckpointRequestId,
            coordinatorState.CurrentDataSessionIdentity!,
            coordinatorState.CurrentDataSessionGeneration,
            "unit_test",
            "fallback_checkpoint_sparse_handoff",
            "fallback-checkpoint-sparse-handoff-observation",
            1,
            1);
        var issued = FileTransferCoordinator.IssueReceiveRecoveryOperation(
            coordinatorState,
            observation,
            BridgeRecoveryKind.ReceiveRecoveryRequest,
            leg.CheckpointRequestId);
        Assert.True(issued.Accepted, issued.Reason);
        AdoptRecovery(issued);
        var authority = Assert.IsType<FileTransferReceiveRecoveryOperationAuthority>(
            issued.Authority);
        var terminalReceipt = new FileTransferBridgeRecoveryExecutionReceipt(
            authority.ExecutionCommand,
            FileTransferBridgeRecoveryExecutionOutcome.Completed,
            "soft",
            "bridge_recovery_completed");
        var started = FileTransferCoordinator.ObserveReceiveRecoveryExecutionStart(
            issued.State,
            new FileTransferBridgeRecoveryExecutionStartResult(
                authority.ExecutionCommand,
                FileTransferBridgeRecoveryExecutionStartStatus.Started,
                "bridge_recovery_execution_started",
                Task.FromResult(terminalReceipt)),
            observedUtcMs: 10);
        Assert.True(started.Accepted, started.Reason);
        AdoptRecovery(started);

        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = coordinatorState.SessionId,
            TransferId = coordinatorState.TransferId,
            TransportEpoch = leg.TransportEpochId,
            RepairRequestId = "v6-regular-nkn-state-refresh:1",
            RequestCorrelationId =
                $"receive-recovery-fallback-checkpoint:{authority.OperationGeneration}:v6-regular-nkn-state-refresh:1",
            MissingRanges =
            [
                new FileTransferRangeV4
                {
                    StartChunkIndex = 100,
                    ChunkCount = 1,
                },
            ],
            Priority = "state_refresh",
            RecoveryMode = "post_tuna_fallback_state_refresh",
        };
        serviceType
            .GetMethod("MarkOutboundFallbackCheckpointRequestedLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, request, "unit_test_checkpoint_delivery"]);
        var boundState = CreateCoordinatorStateForTest(context);
        var command = Assert.IsType<FileTransferReceiveRecoveryFallbackCheckpointDeliveryCommand>(
            FileTransferCoordinator.GetReceiveRecoverySubstate(boundState)
                .PendingFallbackCheckpointDeliveryCommand);
        var held = FileTransferCoordinator.ApplyReceiveRecoveryFallbackCheckpointDeliveryReceipt(
            boundState,
            new FileTransferReceiveRecoveryFallbackCheckpointDeliveryReceipt(
                command,
                FileTransferReceiveRecoveryFallbackCheckpointDeliveryState.Held,
                command.Binding.FrameIdentity.Sha256,
                "submission_barrier_closed"),
            observedUtcMs: 15);
        Assert.True(held.Accepted, held.Reason);
        AdoptRecovery(held);
        var completed = FileTransferCoordinator.ObserveReceiveRecoveryExecutionReceipt(
            held.State,
            terminalReceipt,
            observedUtcMs: 20);
        Assert.True(completed.Accepted, completed.Reason);
        AdoptRecovery(completed);
        var resumed = FileTransferCoordinator.ReserveReceiveRecoveryFallbackCheckpointDeliveryResume(
            completed.State,
            new FileTransferReceiveRecoveryFallbackCheckpointResumeEvidence(
                command,
                FileTransferReceiveRecoveryFallbackCheckpointResumeTrigger.RecoveryCompleted,
                21));
        Assert.True(resumed.Accepted, resumed.Reason);
        var reservation = Assert.IsType<FileTransferReceiveRecoveryFallbackCheckpointResumeReservation>(
            resumed.FallbackCheckpointResumeReservation);
        AdoptRecovery(resumed);

        var stagedPredecessor = StageSparsePumpInstall(
            service,
            context,
            predecessor: null,
            "unit_test_checkpoint_delivery_sparse_predecessor");
        Assert.True(stagedPredecessor.Accepted, stagedPredecessor.Reason);
        var committedPredecessor = CommitSparsePumpInstall(
            service,
            context,
            stagedPredecessor.Install!,
            dataSession);
        Assert.True(committedPredecessor.Accepted, committedPredecessor.Reason);
        var predecessorInstall = committedPredecessor.Install!;
        var predecessorOwner = Assert.IsType<RuntimeOwnerToken>(
            GetPrivatePropertyValue(predecessorInstall, "RuntimeOwner"));
        var dataSessionGeneration = GetPrivateProperty<long>(context, "DataSessionGeneration");

        serviceType
            .GetMethod("QueueOutboundV4SparseRuntimeStateRefreshIfCurrent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    dataSession,
                    request,
                    "unit_test_checkpoint_delivery_resume",
                    predecessorInstall,
                    predecessorOwner,
                    dataSession,
                    dataSessionGeneration,
                    reservation,
                ]);

        await WaitUntilAsync(
            () => FileTransferCoordinator.GetReceiveRecoverySubstate(
                    CreateCoordinatorStateForTest(context))
                .LastFallbackCheckpointDeliveryReceipt?.State ==
                FileTransferReceiveRecoveryFallbackCheckpointDeliveryState.Delivered,
            timeoutMs: 5000);

        Assert.NotNull(successorInstall);
        Assert.Same(successorInstall, GetPrivatePropertyValue(context, "InstalledOutboundSparsePump"));
        Assert.Single(dataSession.SentFrames.OfType<FileTransferFrontierRequestFrameV6>());
        var recovery = FileTransferCoordinator.GetReceiveRecoverySubstate(
            CreateCoordinatorStateForTest(context));
        Assert.Null(recovery.PendingFallbackCheckpointDeliveryCommand);
        Assert.Null(recovery.PendingFallbackCheckpointResumeReservation);
        Assert.Equal(
            command,
            Assert.IsType<FileTransferReceiveRecoveryFallbackCheckpointDeliveryReceipt>(
                recovery.LastFallbackCheckpointDeliveryReceipt).Command);

        var deliveredReceipt = Assert.IsType<
            FileTransferReceiveRecoveryFallbackCheckpointDeliveryReceipt>(
                recovery.LastFallbackCheckpointDeliveryReceipt);
        var redeliveryDecision = FileTransferCoordinator
            .RecheckReceiveRecoveryFallbackCheckpointDelivery(
                CreateCoordinatorStateForTest(context),
                new FileTransferReceiveRecoveryFallbackCheckpointResponseTimeoutObservation(
                    deliveredReceipt,
                    ResponseTimeoutMs: 1,
                    ObservedUtcMs:
                        recovery.LastFallbackCheckpointDeliveredObservedUtcMs + 1));
        Assert.True(redeliveryDecision.Accepted, redeliveryDecision.Reason);
        var redeliveryCommand = Assert.IsType<
            FileTransferReceiveRecoveryFallbackCheckpointDeliveryCommand>(
                redeliveryDecision.FallbackCheckpointDeliveryCommand);
        AdoptRecovery(redeliveryDecision);
        var currentInstall =
            GetPrivatePropertyValue(context, "InstalledOutboundSparsePump");
        Assert.NotNull(currentInstall);
        var currentRuntimeOwner = Assert.IsType<RuntimeOwnerToken>(
            GetPrivatePropertyValue(currentInstall, "RuntimeOwner"));
        var currentDataSessionGeneration = GetPrivateProperty<long>(
            currentInstall,
            "DataSessionGeneration");

        serviceType
            .GetMethod("QueueOutboundV4SparseRuntimeStateRefreshIfCurrent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    dataSession,
                    redeliveryCommand.Frame,
                    "unit_test_checkpoint_response_timeout_redelivery",
                    currentInstall,
                    currentRuntimeOwner,
                    dataSession,
                    currentDataSessionGeneration,
                    null,
                ]);

        await WaitUntilAsync(
            () => FileTransferCoordinator.GetReceiveRecoverySubstate(
                    CreateCoordinatorStateForTest(context))
                .LastFallbackCheckpointDeliveryReceipt?.Command ==
                    redeliveryCommand,
            timeoutMs: 5000);

        Assert.Equal(
            2,
            dataSession.SentFrames
                .OfType<FileTransferFrontierRequestFrameV6>()
                .Count());
        var redeliveryState = FileTransferCoordinator.GetReceiveRecoverySubstate(
            CreateCoordinatorStateForTest(context));
        var redeliveryReceipt = Assert.IsType<
            FileTransferReceiveRecoveryFallbackCheckpointDeliveryReceipt>(
                redeliveryState.LastFallbackCheckpointDeliveryReceipt);
        Assert.Equal(
            FileTransferReceiveRecoveryFallbackCheckpointDeliveryState.Delivered,
            redeliveryReceipt.State);
        Assert.Equal(redeliveryCommand, redeliveryReceipt.Command);
        Assert.Equal(command.Binding, redeliveryCommand.Binding);
        Assert.Equal(command.Frame, redeliveryCommand.Frame);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseWorker_TailReconciliationSupersededAfterQueueBeforeSendCannotAlterSuccessor()
    {
        const string transferId = "transfer_sparse_tail_reconciliation_pre_send_superseded";
        using var harness = new SparseWorkerHarness(transferId);
        var serviceType = typeof(SessionFileTransferService);
        var context = harness.Context;
        var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        SetPrivateProperty(context, "ChunkCount", 6200);
        SetPrivateProperty(context, "RemoteNextExpectedChunkIndex", 2005);
        SetPrivateProperty(context, "ChunksAcceptedForTransport", 4472);
        SetPrivateProperty(context, "RemoteGrantedUntilExclusive", 2389);
        SetPrivateProperty(context, "PostTunaFallbackLiveCheckpointStallCount", 3);
        SetPrivateProperty(leg, "State", FileTransferLegState.RecoveryActive);
        SetPrivateProperty(leg, "CanSendData", true);
        SetPrivateProperty(leg, "ProvenCommittedChunkIndex", 2005);
        SetPrivateProperty(leg, "ProvenHighestObservedChunkIndex", 4471);
        SetPrivateProperty(leg, "TransportEpochId", 3L);

        var handoffType = serviceType.Assembly.GetType("NLink.Core.FileTransfer.TransportHandoffEpoch");
        Assert.NotNull(handoffType);
        var handoff = Activator.CreateInstance(handoffType!)!;
        SetPrivateProperty(handoff, "EpochId", 3L);
        SetPrivateProperty(handoff, "Kind", FileTransferTransportHandoffKind.TunaToNormalFallback);
        SetPrivateProperty(handoff, "SourceTransport", FileTransferTransportKind.Tuna);
        SetPrivateProperty(handoff, "TargetTransport", FileTransferTransportKind.RegularNkn);
        SetPrivateProperty(handoff, "Direction", FileTransferDirection.Outbound);
        SetPrivateProperty(handoff, "Reason", "unit_test_tail_reconciliation_pre_send_superseded");
        SetPrivateProperty(handoff, "State", V6TransportHandoffState.FrontierRepairOnly);
        SetPrivateProperty(context, "V6TransportHandoff", handoff);

        var staged = StageSparsePumpInstall(
            harness.Service,
            context,
            predecessor: null,
            "unit_test_tail_reconciliation_pre_send_predecessor");
        Assert.True(staged.Accepted, staged.Reason);
        Assert.NotNull(staged.Install);
        var predecessorInstall = staged.Install!;
        var committed = CommitSparsePumpInstall(
            harness.Service,
            context,
            predecessorInstall,
            harness.DataSession);
        Assert.True(committed.Accepted, committed.Reason);

        var preSendReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousBarrier = SessionFileTransferService.OutboundSparseStateRefreshPreSendBarrierForTests;
        SessionFileTransferService.OutboundSparseStateRefreshPreSendBarrierForTests = async (observedTransferId, _) =>
        {
            if (!string.Equals(observedTransferId, transferId, StringComparison.Ordinal))
            {
                return;
            }

            preSendReached.TrySetResult();
            await releasePreSend.Task;
        };

        using var source = new MemoryStream(Array.Empty<byte>(), writable: false);
        Task? pumpTask = null;
        try
        {
            pumpTask = Assert.IsAssignableFrom<Task>(serviceType
                .GetMethod("RunOutboundV4SenderPumpAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(
                    harness.Service,
                    [
                        context,
                        source,
                        harness.DataSession,
                        CancellationToken.None,
                        predecessorInstall,
                    ]));
            await preSendReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

            object successorInstall;
            RoutePublicationToken successorPublication;
            lock (GetServiceGate(harness.Service))
            {
                successorPublication = ReplaceSparseAuthorityRetainingOwner(context, "route_publication");
                var successorStage = StageSparsePumpInstall(
                    harness.Service,
                    context,
                    predecessorInstall,
                    "unit_test_tail_reconciliation_pre_send_successor");
                Assert.True(successorStage.Accepted, successorStage.Reason);
                Assert.NotNull(successorStage.Install);
                var successorCommit = CommitSparsePumpInstall(
                    harness.Service,
                    context,
                    successorStage.Install!,
                    harness.DataSession);
                Assert.True(successorCommit.Accepted, successorCommit.Reason);
                successorInstall = successorCommit.Install!;
            }

            var expectedCheckpointRequestId = GetPrivatePropertyValue(leg, "CheckpointRequestId");
            var expectedLegState = GetPrivateProperty<FileTransferLegState>(leg, "State");
            var expectedCheckpointGeneration = GetPrivateProperty<int>(leg, "CheckpointGeneration");
            var expectedFailureCount = GetPrivateProperty<int>(context, "V6RegularNknStateRefreshFailureCount");
            var expectedLastRecovery = GetPrivatePropertyValue(context, "V6LastReceiveRecoveryRequestedUtc");
            var expectedResumePending = GetPrivateProperty<bool>(context, "PullTransportResumeRequestPending");
            var expectedEpochLivenessDeferrals = GetPrivateProperty<int>(context, "V6EpochLivenessDeferralCount");
            var expectedPeerLivenessDeferrals = GetPrivateProperty<int>(context, "V6PeerLivenessRecoveryDeferralCount");
            var expectedTransportPaused = GetPrivateProperty<bool>(context, "PullTransportPaused");
            var expectedGraceDeadline = GetPrivatePropertyValue(context, "PullTransportGraceDeadlineUtc");
            var expectedChunksAccepted = GetPrivateProperty<int>(context, "ChunksAcceptedForTransport");
            var expectedBytesAccepted = GetPrivateProperty<long>(context, "BytesAcceptedForTransport");
            var expectedSentFrameCount = harness.DataSession.SentFrames.Count;

            releasePreSend.TrySetResult();
            await pumpTask.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => GetPrivateProperty<int>(context, "V6RegularNknStateRefreshSendInFlight") == 0,
                timeoutMs: 5000);

            Assert.Same(successorInstall, GetPrivatePropertyValue(context, "InstalledOutboundSparsePump"));
            Assert.Equal(successorPublication, GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken"));
            Assert.Equal(expectedCheckpointRequestId, GetPrivatePropertyValue(leg, "CheckpointRequestId"));
            Assert.Equal(expectedLegState, GetPrivateProperty<FileTransferLegState>(leg, "State"));
            Assert.Equal(expectedCheckpointGeneration, GetPrivateProperty<int>(leg, "CheckpointGeneration"));
            Assert.Equal(expectedFailureCount, GetPrivateProperty<int>(context, "V6RegularNknStateRefreshFailureCount"));
            Assert.Equal(expectedLastRecovery, GetPrivatePropertyValue(context, "V6LastReceiveRecoveryRequestedUtc"));
            Assert.Equal(expectedResumePending, GetPrivateProperty<bool>(context, "PullTransportResumeRequestPending"));
            Assert.Equal(expectedEpochLivenessDeferrals, GetPrivateProperty<int>(context, "V6EpochLivenessDeferralCount"));
            Assert.Equal(expectedPeerLivenessDeferrals, GetPrivateProperty<int>(context, "V6PeerLivenessRecoveryDeferralCount"));
            Assert.Equal(expectedTransportPaused, GetPrivateProperty<bool>(context, "PullTransportPaused"));
            Assert.Equal(expectedGraceDeadline, GetPrivatePropertyValue(context, "PullTransportGraceDeadlineUtc"));
            Assert.Equal(expectedChunksAccepted, GetPrivateProperty<int>(context, "ChunksAcceptedForTransport"));
            Assert.Equal(expectedBytesAccepted, GetPrivateProperty<long>(context, "BytesAcceptedForTransport"));
            Assert.Equal(expectedSentFrameCount, harness.DataSession.SentFrames.Count);
            Assert.Empty(harness.DataSession.SentFrames.OfType<FileTransferFrontierRequestFrameV6>());
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
        }
        finally
        {
            releasePreSend.TrySetResult();
            SessionFileTransferService.OutboundSparseStateRefreshPreSendBarrierForTests = previousBarrier;
            if (pumpTask is { IsCompleted: false })
            {
                TryCancelContextLifetime(context);
                await pumpTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceive_ResultFromRetiredAuthorityCannotAlterSuccessorState()
    {
        const string transferId = "transfer_sparse_receive_retired_authority";
        var sessionId = $"session_{transferId}";
        var logStart = GetOperationalLogLength();
        using var senderTransport = new LoopbackFileTransferTransport(sessionId);
        using var peerTransport = new LoopbackFileTransferTransport(sessionId);
        senderTransport.Connect(peerTransport);
        using var service = new SessionFileTransferService();
        service.AttachTransport(senderTransport);
        var context = CreatePostTunaFallbackV6OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        using var dataSession = await senderTransport.OpenFileTransferDataSessionAsync(
            sessionId,
            transferId,
            CancellationToken.None);
        ReplaceOutboundDataSession(service, context, dataSession);

        var completionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousBarrier = SessionFileTransferService.OutboundSparseReceiveCompletionBarrierForTests;
        SessionFileTransferService.OutboundSparseReceiveCompletionBarrierForTests = async (observedTransferId, _) =>
        {
            if (!string.Equals(observedTransferId, transferId, StringComparison.Ordinal))
            {
                return;
            }

            completionReached.TrySetResult();
            await releaseCompletion.Task;
        };

        var runtimeKindType = typeof(SessionFileTransferService)
            .GetNestedType("FileTransferSparseCreditRuntimeKind", BindingFlags.NonPublic)!;
        var runtimeKind = Enum.Parse(runtimeKindType, "PrimaryRegularNknV4");
        var supervisor = Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
            .GetMethod("RunOutboundSparseCreditSenderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, runtimeKind]));

        try
        {
            await WaitUntilAsync(
                () => senderTransport.GetDataSessionReceiveStateForTests(transferId).Active &&
                      GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is not null,
                timeoutMs: 5000);
            Assert.NotNull(GetPrivatePropertyValue(context, "InstalledOutboundSparsePump"));
            var retiredInstall = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump")!;
            var retiredOwner = Assert.IsType<RuntimeOwnerToken>(GetPrivatePropertyValue(context, "CurrentRuntimeOwner"));
            var retiredPublication = Assert.IsType<RoutePublicationToken>(GetPrivatePropertyValue(context, "CurrentRoutePublicationToken"));

            senderTransport.ReceiveDeliveredDataFrame(new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 100,
                ContiguousCommittedChunkIndex = 4,
                DurableReceivedHighestChunkIndex = 4,
                CreditUntilChunkIndexExclusive = 8,
                MissingRanges = [],
                BytesCommitted = 4L * 21 * 1024,
                TransportEpoch = 3,
                RepairRequestId = "stale-receive:retired-authority",
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            });
            await completionReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var successorOwner = retiredOwner with
            {
                RuntimeGeneration = retiredOwner.RuntimeGeneration + 1,
                CreatedReason = "unit_test_receive_authority_handoff",
            };
            var successorPublication = retiredPublication with
            {
                RuntimeOwner = successorOwner,
                RuntimeGeneration = successorOwner.RuntimeGeneration,
                PublicationGeneration = retiredPublication.PublicationGeneration + 1,
                RouteCommitGeneration = (retiredPublication.RouteCommitGeneration ?? 0) + 1,
                Reason = "unit_test_receive_authority_handoff",
            };
            var gate = typeof(SessionFileTransferService)
                .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service)!;
            object successorInstall;
            lock (gate)
            {
                SetPrivateProperty(context, "CurrentRuntimeOwner", successorOwner);
                SetPrivateProperty(context, "CurrentRoutePublicationToken", successorPublication);
                successorInstall = CreateSparsePumpInstall(
                    context,
                    installGeneration: GetPrivateProperty<long>(retiredInstall, "InstallGeneration") + 1,
                    predecessorInstallGeneration: GetPrivateProperty<long>(retiredInstall, "InstallGeneration"));
                SetPrivateProperty(
                    context,
                    "LastOutboundSparsePumpInstallGeneration",
                    GetPrivateProperty<long>(successorInstall, "InstallGeneration"));
                SetPrivateProperty(context, "InstalledOutboundSparsePump", successorInstall);
            }

            var expectedRemoteNext = GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex");
            var expectedGrant = GetPrivateProperty<int>(context, "RemoteGrantedUntilExclusive");
            var expectedAcknowledged = GetPrivateProperty<long>(context, "BytesAcknowledgedByReceiver");
            var expectedLastPeerFrame = (DateTimeOffset?)GetPrivatePropertyValue(context, "PullV4LastPeerFrameReceivedUtc");
            var expectedPeerLiveness = (DateTimeOffset?)GetPrivatePropertyValue(context, "V6LastPeerLivenessUtc");
            var expectedGraceDeadline = (DateTimeOffset?)GetPrivatePropertyValue(context, "PullTransportGraceDeadlineUtc");
            var expectedRecoveryActive = GetPrivateProperty<bool>(context, "PullPostTunaRecoveryActive");
            var expectedRepairQueueCount = Assert.IsAssignableFrom<System.Collections.ICollection>(
                GetPrivatePropertyValue(context, "PullV4SenderPumpRepairQueue")).Count;

            releaseCompletion.TrySetResult();
            await WaitUntilAsync(
                () => ReadOperationalLogTail(logStart)
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Any(line =>
                        line.Contains("event=filetransfer_outbound_sparse_worker_handoff_rejected;", StringComparison.Ordinal) &&
                        line.Contains("transfer_id=" + transferId, StringComparison.Ordinal) &&
                        line.Contains("stage=receive_result", StringComparison.Ordinal) &&
                        line.Contains("decision_reason=runtime_owner_replaced", StringComparison.Ordinal)),
                timeoutMs: 5000);

            Assert.Equal(expectedRemoteNext, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
            Assert.Equal(expectedGrant, GetPrivateProperty<int>(context, "RemoteGrantedUntilExclusive"));
            Assert.Equal(expectedAcknowledged, GetPrivateProperty<long>(context, "BytesAcknowledgedByReceiver"));
            Assert.Equal(expectedLastPeerFrame, (DateTimeOffset?)GetPrivatePropertyValue(context, "PullV4LastPeerFrameReceivedUtc"));
            Assert.Equal(expectedPeerLiveness, (DateTimeOffset?)GetPrivatePropertyValue(context, "V6LastPeerLivenessUtc"));
            Assert.Equal(expectedGraceDeadline, (DateTimeOffset?)GetPrivatePropertyValue(context, "PullTransportGraceDeadlineUtc"));
            Assert.Equal(expectedRecoveryActive, GetPrivateProperty<bool>(context, "PullPostTunaRecoveryActive"));
            Assert.Equal(expectedRepairQueueCount, Assert.IsAssignableFrom<System.Collections.ICollection>(
                GetPrivatePropertyValue(context, "PullV4SenderPumpRepairQueue")).Count);
            Assert.Equal(successorOwner, GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"));
            Assert.Equal(successorPublication, GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken"));
            Assert.NotNull(GetPrivatePropertyValue(context, "InstalledOutboundSparsePump"));
            var currentInstall = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump")!;
            Assert.Equal(successorOwner, GetPrivateProperty<RuntimeOwnerToken>(currentInstall, "RuntimeOwner"));
            Assert.Equal(successorPublication, GetPrivateProperty<RoutePublicationToken>(currentInstall, "RoutePublication"));
            Assert.True(GetPrivateProperty<long>(currentInstall, "InstallGeneration") >= 2);
            Assert.True(GetPrivateProperty<long>(currentInstall, "PredecessorInstallGeneration") >= 1);
        }
        finally
        {
            releaseCompletion.TrySetResult();
            SessionFileTransferService.OutboundSparseReceiveCompletionBarrierForTests = previousBarrier;
            try
            {
                GetPrivateProperty<CancellationTokenSource>(context, "LifetimeCts").Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseWorker_TerminalReadyCompletionSupersededBeforeMutationCannotCompleteSuccessor()
    {
        const string transferId = "transfer_sparse_terminal_ready_superseded";
        const int chunkCount = 256;
        using var harness = new SparseWorkerHarness(transferId);
        SetPrivateProperty(harness.Context, "RemoteNextExpectedChunkIndex", chunkCount);
        SetPrivateProperty(harness.Context, "RemoteGrantedUntilExclusive", chunkCount);
        SetPrivateProperty(harness.Context, "ChunksAcceptedForTransport", chunkCount);
        SetPrivateProperty(harness.Context, "BytesAcceptedForTransport", chunkCount * 21L * 1024);
        SetPrivateProperty(harness.Context, "BytesTransferred", chunkCount * 21L * 1024);
        SetPrivateProperty(harness.Context, "BytesAcknowledgedByReceiver", chunkCount * 21L * 1024);
        SetPrivateProperty(harness.Context, "PullSenderPipelineCurrentInFlightFrames", 0);
        SetPrivateProperty(harness.Context, "V4TerminalReady", true);

        var staged = StageSparsePumpInstall(
            harness.Service,
            harness.Context,
            predecessor: null,
            "unit_test_terminal_ready");
        Assert.NotNull(staged.Install);
        var retiredInstall = staged.Install!;
        Assert.True(CommitSparsePumpInstall(
            harness.Service,
            harness.Context,
            retiredInstall,
            harness.DataSession).Accepted);

        var completionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousBarrier = SessionFileTransferService.OutboundSparseTerminalCompletionBarrierForTests;
        SessionFileTransferService.OutboundSparseTerminalCompletionBarrierForTests = async (observedTransferId, _) =>
        {
            if (!string.Equals(observedTransferId, transferId, StringComparison.Ordinal))
            {
                return;
            }

            completionReached.TrySetResult();
            await releaseCompletion.Task;
        };

        try
        {
            using var source = new MemoryStream(new byte[chunkCount * 21 * 1024], writable: false);
            var pump = Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
                .GetMethod("RunOutboundV4SenderPumpAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(
                    harness.Service,
                    [
                        harness.Context,
                        source,
                        harness.DataSession,
                        CancellationToken.None,
                        retiredInstall,
                    ]));
            await completionReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

            object successorInstall;
            lock (GetServiceGate(harness.Service))
            {
                ReplaceSparseAuthorityRetainingOwner(harness.Context, "route_publication");
                successorInstall = CreateSparsePumpInstall(
                    harness.Context,
                    installGeneration: 2,
                    predecessorInstallGeneration: 1);
                SetPrivateProperty(harness.Context, "LastOutboundSparsePumpInstallGeneration", 2L);
                SetPrivateProperty(harness.Context, "InstalledOutboundSparsePump", successorInstall);
            }

            releaseCompletion.TrySetResult();
            await pump.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(GetPrivateProperty<bool>(harness.Context, "IsTerminal"));
            Assert.Equal(
                FileTransferTransferState.Sending,
                GetPrivateProperty<FileTransferTransferState>(harness.Context, "State"));
            Assert.Same(successorInstall, GetPrivatePropertyValue(harness.Context, "InstalledOutboundSparsePump"));
            Assert.Equal(2L, GetPrivateProperty<long>(successorInstall, "InstallGeneration"));
            Assert.Empty(harness.DataSession.SentFrames.OfType<FileTransferCompleteFrameV6>());
        }
        finally
        {
            releaseCompletion.TrySetResult();
            SessionFileTransferService.OutboundSparseTerminalCompletionBarrierForTests = previousBarrier;
        }
    }

    [Theory]
    [InlineData("route_publication")]
    [InlineData("leg")]
    [InlineData("data_session")]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseWorker_SourceOpenFailureAfterAuthorityReplacementIsDiagnosticOnly(
        string invalidation)
    {
        var transferId = $"transfer_sparse_source_open_{invalidation}_{Guid.NewGuid():N}";
        var sourceOpenStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceOpenResult = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        FileTransferReadStreamFactory sourceFactory =
            async ct =>
            {
                sourceOpenStarted.TrySetResult();
                return await sourceOpenResult.Task.WaitAsync(ct);
            };
        using var harness = new SparseWorkerHarness(transferId, sourceFactory);
        var originalOwner = GetPrivateProperty<RuntimeOwnerToken>(harness.Context, "CurrentRuntimeOwner");

        var installTask = Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
            .GetMethod("TryInstallOutboundSparsePumpAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                harness.Service,
                [
                    harness.Context,
                    null,
                    null,
                    true,
                    "unit_test_delayed_source_open",
                    CancellationToken.None,
                ]));
        await sourceOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        RecordingDataSession? replacementSession = null;
        var gate = GetServiceGate(harness.Service);
        lock (gate)
        {
            switch (invalidation)
            {
                case "route_publication":
                    ReplaceSparseAuthorityRetainingOwner(harness.Context, "route_publication");
                    break;
                case "leg":
                    ReplaceSparseAuthorityRetainingOwner(harness.Context, "leg");
                    break;
                case "data_session":
                    replacementSession = new RecordingDataSession($"session_{transferId}", transferId);
                    ReplaceOutboundDataSession(harness.Service, harness.Context, replacementSession);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(invalidation), invalidation, null);
            }
        }

        sourceOpenResult.TrySetException(new IOException("Injected delayed source-open failure."));
        await installTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(GetPrivateProperty<bool>(harness.Context, "IsTerminal"));
        Assert.Equal(
            FileTransferTransferState.Sending,
            GetPrivateProperty<FileTransferTransferState>(harness.Context, "State"));
        Assert.Equal(originalOwner, GetPrivateProperty<RuntimeOwnerToken>(harness.Context, "CurrentRuntimeOwner"));
        Assert.DoesNotContain(harness.DataSession.SentFrames, static frame => frame is FileTransferErrorFrameV4);
        Assert.DoesNotContain(harness.DataSession.SentFrames, static frame => frame is FileTransferCancelFrameV4);
        Assert.DoesNotContain(harness.DataSession.SentFrames, static frame => frame is FileTransferCompleteFrameV4);
        replacementSession?.Dispose();
    }

    [Theory]
    [InlineData("route_publication")]
    [InlineData("leg")]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceive_InitialAuthorityReplacementRetainingOwnerRejectsFrame(
        string invalidation)
    {
        var transferId = $"transfer_sparse_initial_receive_{invalidation}_{Guid.NewGuid():N}";
        var sessionId = $"session_{transferId}";
        using var dataSession = new CoordinatedInitialReceiveDataSession(sessionId, transferId);
        using var transport = new ControlledOpenLoopbackTransport(sessionId)
        {
            OpenOverrideAsync = (_, _, _, _) => Task.FromResult<IFileTransferDataSession>(dataSession),
        };
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var originalOwner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var logStart = GetOperationalLogLength();
        var supervisorInvocation = Task.Factory.StartNew(
            () => StartSparseReceiveSupervisor(service, context),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        try
        {
            await dataSession.ReceiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lock (GetServiceGate(service))
            {
                ReplaceSparseAuthorityRetainingOwner(context, invalidation);
            }

            dataSession.Deliver(
                new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    Epoch = 1,
                    ContiguousCommittedChunkIndex = 4,
                    DurableReceivedHighestChunkIndex = 4,
                    CreditUntilChunkIndexExclusive = 8,
                    MissingRanges = [],
                    BytesCommitted = 4L * 21 * 1024,
                    TransportEpoch = 3,
                    RecoveryMode = "frontier_repair_only",
                },
                FileTransferTransportKind.RegularNkn);
            dataSession.ReleaseReceiveStart();
            var supervisor = await supervisorInvocation.WaitAsync(TimeSpan.FromSeconds(5));

            await WaitUntilAsync(
                () => (ReadOperationalLogTail(logStart) + Environment.NewLine +
                       LocalOperationalLog.GetRecentLogText()).Contains(
                    "event=filetransfer_outbound_sparse_worker_handoff_rejected;",
                    StringComparison.Ordinal),
                timeoutMs: 5000);
            Assert.Equal(0, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
            Assert.Equal(0, GetPrivateProperty<int>(context, "RemoteGrantedUntilExclusive"));
            Assert.Equal(0L, GetPrivateProperty<long>(context, "BytesAcknowledgedByReceiver"));
            Assert.Equal(originalOwner, GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"));
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));

            GetPrivateProperty<CancellationTokenSource>(context, "LifetimeCts").Cancel();
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            dataSession.ReleaseReceiveStart();
            TryCancelContextLifetime(context);
            if (supervisorInvocation.IsCompletedSuccessfully)
            {
                await supervisorInvocation.Result.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("complete")]
    [InlineData("error")]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceive_DelayedLifecycleFrameAcrossPublicationHandoffHasNoEffect(
        string frameKind)
    {
        var transferId = $"transfer_sparse_delayed_{frameKind}_{Guid.NewGuid():N}";
        var sessionId = $"session_{transferId}";
        const int chunkCount = 8;
        var sha256 = Convert.ToBase64String(new byte[32]);
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[chunkCount * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: chunkCount);
        SetPrivateProperty(context, "Sha256Base64", sha256);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var originalOwner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var logStart = GetOperationalLogLength();
        var dispatchReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousBarrier = SessionFileTransferService.OutboundSparseReceiveDispatchBarrierForTests;
        SessionFileTransferService.OutboundSparseReceiveDispatchBarrierForTests = async (observedTransferId, _) =>
        {
            if (!string.Equals(observedTransferId, transferId, StringComparison.Ordinal))
            {
                return;
            }

            dispatchReached.TrySetResult();
            await releaseDispatch.Task;
        };
        var supervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            await WaitUntilAsync(
                () => transport.GetDataSessionReceiveStateForTests(transferId).Active &&
                      GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is not null,
                timeoutMs: 5000);

            FileTransferDataFrame frame = frameKind switch
            {
                "probe" => new FileTransferTransportProbeFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    TransportEpoch = 3,
                    ProbeId = $"probe:{transferId}",
                    TargetTransport = "regular_nkn",
                },
                "complete" => new FileTransferCompleteFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    FileSizeBytes = chunkCount * 21L * 1024,
                    Sha256Base64 = sha256,
                    TransportEpoch = 3,
                    RecoveryMode = "post_tuna_fallback",
                },
                "error" => new FileTransferErrorFrameV6
                {
                    SessionId = sessionId,
                    TransferId = transferId,
                    ErrorCode = FileTransferResultCodes.PeerDisconnected,
                    Message = "Injected stale receive error.",
                    TransportEpoch = 3,
                },
                _ => throw new ArgumentOutOfRangeException(nameof(frameKind), frameKind, null),
            };
            transport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
            transport.ReceiveDeliveredDataFrame(frame);
            await dispatchReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            RoutePublicationToken replacementPublication;
            lock (GetServiceGate(service))
            {
                replacementPublication = ReplaceSparseAuthorityRetainingOwner(context, "route_publication");
            }

            releaseDispatch.TrySetResult();
            await WaitUntilAsync(
                () =>
                {
                    var installed = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump");
                    return installed is not null &&
                           GetPrivateProperty<RoutePublicationToken>(installed, "RoutePublication") ==
                           replacementPublication &&
                           transport.GetDataSessionReceiveStateForTests(transferId).Active;
                },
                timeoutMs: 5000);
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
            Assert.Equal(FileTransferTransferState.Sending, GetPrivateProperty<FileTransferTransferState>(context, "State"));
            Assert.Equal(originalOwner, GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"));
            Assert.Empty(transport.SentTransportProbes);
            Assert.Empty(transport.SentErrors);
            Assert.Empty(transport.SentCancels);
            Assert.Empty(transport.SentCompletes);

            GetPrivateProperty<CancellationTokenSource>(context, "LifetimeCts").Cancel();
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseDispatch.TrySetResult();
            SessionFileTransferService.OutboundSparseReceiveDispatchBarrierForTests = previousBarrier;
            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceive_PreCommitProbeAckReleasedAfterPublicationHandoffHasNoEffect()
    {
        const string transferId = "transfer_sparse_precommit_ack_race";
        var sessionId = $"session_{transferId}";
        var ackSendReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAckSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRepairContinuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousRepairContinuationBarrier =
            SessionFileTransferService.OutboundSparseRepairContinuationBarrierForTests;
        SessionFileTransferService.OutboundSparseRepairContinuationBarrierForTests =
            async (observedTransferId, observedSessionId, stage) =>
            {
                if (string.Equals(observedTransferId, transferId, StringComparison.Ordinal) &&
                    string.Equals(stage, "idle_delay", StringComparison.Ordinal))
                {
                    await releaseRepairContinuation.Task;
                    return;
                }

                if (previousRepairContinuationBarrier is not null)
                {
                    await previousRepairContinuationBarrier(observedTransferId, observedSessionId, stage);
                }
            };
        using var transport = new LoopbackFileTransferTransport(sessionId)
        {
            OutboundDataFrameDeliveryOverrideAsync = async (_, frame, _) =>
            {
                if (frame is not FileTransferRuntimeUnlockPreCommitProbeAckFrame ack ||
                    !string.Equals(ack.Reason, "precommit_probe_received", StringComparison.Ordinal))
                {
                    return false;
                }

                ackSendReached.TrySetResult();
                await releaseAckSend.Task;
                return true;
            },
        };
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        SetPrivateProperty(context, "PullPostTunaRecoveryActive", false);
        SetPrivateProperty(context, "PullTransportFrontierOnlyRepairActive", false);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        using var dataSession = await transport.OpenFileTransferDataSessionAsync(
            sessionId,
            transferId,
            CancellationToken.None);
        ReplaceOutboundDataSession(service, context, dataSession);
        var leg = Assert.IsType<FileTransferLeg>(GetPrivatePropertyValue(context, "CurrentTransferLeg"));
        SeedPairedFallbackTailYieldForTest(context, leg);
        SeedRuntimeUnlockAdmissionForTest(
            context,
            FileTransferDirection.Outbound,
            "unit_test_sparse_precommit_ack_race");
        var logStart = GetOperationalLogLength();
        string ReadTestLog() => FilterV4SenderTransferLog(ReadOperationalLogTail(logStart), transferId);
        var supervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            await WaitUntilAsync(
                () => transport.GetDataSessionReceiveStateForTests(transferId).Active &&
                      GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is not null,
                timeoutMs: 5000);

            var probe = new FileTransferRuntimeUnlockPreCommitProbeFrame
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransactionGeneration = 3,
                OfferGeneration = 4,
                TunaPathLeaseGeneration = 4,
                ProbeId = $"runtime-unlock-precommit:3:4:{transferId}",
                TargetRoute = FileTransferRouteResolver.FileTunaV4Token,
                TargetProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
                TargetTransport = "tuna",
                HandoffKind = "normal_to_tuna_activation",
                SentUnixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            using var peerDataSession = await peer.OpenFileTransferDataSessionAsync(
                sessionId,
                transferId,
                CancellationToken.None);
            transport.NextDataFrameTransportKind = FileTransferTransportKind.Tuna;
            await Assert.IsAssignableFrom<IFileTransferRuntimeUnlockTunaProbeSender>(peerDataSession)
                .SendRuntimeUnlockTunaPreCommitProbeAsync(probe, CancellationToken.None);
            await ackSendReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

            RoutePublicationToken successorPublication;
            FileTransferRouteSelection expectedRoute;
            object? expectedRouteCommit;
            object? expectedLastRecovery;
            int expectedRemoteNext;
            int expectedRemoteGrant;
            int expectedAcceptedChunks;
            long expectedAcknowledgedBytes;
            bool expectedRecoveryActive;
            int expectedEpochLivenessDeferrals;
            int expectedPeerLivenessDeferrals;
            int expectedReceiveRecoveryRequests;
            RuntimeUnlockRouteCommitSnapshot? expectedProof;
            string[] expectedProbeEvents;
            lock (GetServiceGate(service))
            {
                successorPublication = ReplaceSparseAuthorityRetainingOwner(context, "route_publication");
                expectedRoute = GetPrivateProperty<FileTransferRouteSelection>(context, "RouteSelection");
                expectedRouteCommit = GetPrivatePropertyValue(context, "RuntimeUnlockRouteCommitToken");
                expectedLastRecovery = GetPrivatePropertyValue(context, "V6LastReceiveRecoveryRequestedUtc");
                expectedRemoteNext = GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex");
                expectedRemoteGrant = GetPrivateProperty<int>(context, "RemoteGrantedUntilExclusive");
                expectedAcceptedChunks = GetPrivateProperty<int>(context, "ChunksAcceptedForTransport");
                expectedAcknowledgedBytes = GetPrivateProperty<long>(context, "BytesAcknowledgedByReceiver");
                expectedRecoveryActive = GetPrivateProperty<bool>(context, "PullPostTunaRecoveryActive");
                expectedEpochLivenessDeferrals = GetPrivateProperty<int>(context, "V6EpochLivenessDeferralCount");
                expectedPeerLivenessDeferrals = GetPrivateProperty<int>(context, "V6PeerLivenessRecoveryDeferralCount");
                expectedReceiveRecoveryRequests = transport.ReceiveRecoveryRequests.Count;
                expectedProof = transport.RuntimeUnlockRouteCommitSnapshot;
                expectedProbeEvents = transport.RuntimeUnlockProbeEvents.ToArray();
                Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockPendingPeerPreCommitProbe"));
            }

            releaseAckSend.TrySetResult();
            await WaitUntilAsync(
                () => ReadTestLog().Contains(
                    "stage=precommit_probe_after_ack_send;",
                    StringComparison.Ordinal),
                timeoutMs: 5000);

            Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockPendingPeerPreCommitProbe"));
            Assert.Equal(successorPublication, GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken"));
            Assert.Equal(expectedRoute, GetPrivateProperty<FileTransferRouteSelection>(context, "RouteSelection"));
            Assert.Equal(expectedRouteCommit, GetPrivatePropertyValue(context, "RuntimeUnlockRouteCommitToken"));
            Assert.Equal(expectedRemoteNext, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
            Assert.Equal(expectedRemoteGrant, GetPrivateProperty<int>(context, "RemoteGrantedUntilExclusive"));
            Assert.Equal(expectedAcceptedChunks, GetPrivateProperty<int>(context, "ChunksAcceptedForTransport"));
            Assert.Equal(expectedAcknowledgedBytes, GetPrivateProperty<long>(context, "BytesAcknowledgedByReceiver"));
            Assert.Equal(expectedRecoveryActive, GetPrivateProperty<bool>(context, "PullPostTunaRecoveryActive"));
            Assert.Equal(expectedLastRecovery, GetPrivatePropertyValue(context, "V6LastReceiveRecoveryRequestedUtc"));
            Assert.Equal(expectedEpochLivenessDeferrals, GetPrivateProperty<int>(context, "V6EpochLivenessDeferralCount"));
            Assert.Equal(expectedPeerLivenessDeferrals, GetPrivateProperty<int>(context, "V6PeerLivenessRecoveryDeferralCount"));
            Assert.Equal(expectedReceiveRecoveryRequests, transport.ReceiveRecoveryRequests.Count);
            Assert.Equal(expectedProof, transport.RuntimeUnlockRouteCommitSnapshot);
            Assert.Equal(expectedProbeEvents, transport.RuntimeUnlockProbeEvents.ToArray());
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));

            var log = ReadTestLog();
            Assert.Contains("event=filetransfer_outbound_sparse_worker_handoff_rejected;", log, StringComparison.Ordinal);
            Assert.Contains("stage=precommit_probe_after_ack_send;", log, StringComparison.Ordinal);
            Assert.DoesNotContain("event=runtime_unlock_precommit_peer_probe_tracked;", log, StringComparison.Ordinal);
            Assert.DoesNotContain("event=filetransfer_route_transitioned; direction=outbound;", log, StringComparison.Ordinal);
        }
        finally
        {
            releaseAckSend.TrySetResult();
            releaseRepairContinuation.TrySetResult();
            SessionFileTransferService.OutboundSparseRepairContinuationBarrierForTests =
                previousRepairContinuationBarrier;
            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceive_SameSessionSuccessorRebindsOutstandingReceiveBeforeOneShotFrontier()
    {
        const string transferId = "transfer_sparse_same_session_receive_rebind";
        var sessionId = $"session_{transferId}";
        using var dataSession = new RebindableSparseReceiveDataSession(sessionId, transferId);
        using var transport = new ControlledOpenLoopbackTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var openedSession = new TaskCompletionSource<IFileTransferDataSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.OpenOverrideAsync = (_, _, _, _) => openedSession.Task;
        var supervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            openedSession.TrySetResult(dataSession);
            await WaitUntilAsync(
                () => dataSession.ReceiveCount == 1 &&
                      GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is not null,
                timeoutMs: 5000);
            var predecessorInstall = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump")!;
            var predecessorGeneration = GetPrivateProperty<long>(predecessorInstall, "InstallGeneration");
            Assert.Same(dataSession, GetPrivatePropertyValue(context, "DataSession"));

            RoutePublicationToken successorPublication;
            lock (GetServiceGate(service))
            {
                successorPublication = ReplaceSparseAuthorityRetainingOwner(context, "route_publication");
            }

            await WaitUntilAsync(
                () =>
                {
                    var installed = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump");
                    return installed is not null &&
                           GetPrivateProperty<long>(installed, "InstallGeneration") > predecessorGeneration &&
                           GetPrivateProperty<RoutePublicationToken>(installed, "RoutePublication") ==
                           successorPublication;
                },
                timeoutMs: 5000);

            // This is deliberately a one-shot delivery. The session-scoped receive remains the
            // sole reader across a same-session sender install, then binds this authority request
            // to the exact installed successor at dispatch time.
            dataSession.Deliver(new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 3,
                RepairRequestId = "one-shot-frontier-after-same-session-handoff",
                RequestCorrelationId = "frq-one-shot-frontier-after-same-session-handoff",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            });

            Assert.False(dataSession.FirstReceiveCanceled.Task.IsCompleted);
            dataSession.ReleaseOneShotDelivery();
            await WaitUntilAsync(
                () => GetPrivateProperty<int>(context, "ChunksAcceptedForTransport") >= 1,
                timeoutMs: 5000);
            await WaitUntilAsync(() => dataSession.ReceiveCount >= 2, timeoutMs: 5000);

            Assert.Same(dataSession, GetPrivatePropertyValue(context, "DataSession"));
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
        }
        finally
        {
            TryCancelContextLifetime(context);
            dataSession.Dispose();
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceive_StaleSynchronousReceiveStartFailureRebindsSupervisorAfterTerminalRejection()
    {
        const string transferId = "transfer_sparse_stale_outer_receive_start_failure";
        var sessionId = $"session_{transferId}";
        using var dataSession = new SynchronousFailingSparseReceiveDataSession(sessionId, transferId);
        using var transport = new ControlledOpenLoopbackTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var openedSession = new TaskCompletionSource<IFileTransferDataSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.OpenOverrideAsync = (_, _, _, _) => openedSession.Task;
        var supervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            openedSession.TrySetResult(dataSession);
            await dataSession.ReceiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is not null,
                timeoutMs: 5000);

            RuntimeOwnerToken successorOwner;
            RoutePublicationToken successorPublication;
            lock (GetServiceGate(service))
            {
                successorOwner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
                successorPublication = ReplaceSparseAuthorityRetainingOwner(context, "route_publication");
            }

            // ReceiveWithMetadataAsync throws synchronously after this release, so the exception
            // reaches the supervisor's outer failure path. Its captured predecessor authority is
            // stale and its exact terminal action must be rejected; that rejection cannot retire
            // the session-scoped receive supervisor.
            dataSession.ReleaseFirstReceiveFailure();

            await WaitUntilAsync(
                () => dataSession.ReceiveCount >= 2 &&
                      GetPrivateProperty<long>(context, "ActiveOutboundSparseReceiveSupervisorGeneration") > 0 &&
                      GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is { } installed &&
                      GetPrivateProperty<RuntimeOwnerToken>(installed, "RuntimeOwner") == successorOwner &&
                      GetPrivateProperty<RoutePublicationToken>(installed, "RoutePublication") ==
                      successorPublication,
                timeoutMs: 5000);

            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
            Assert.Equal(FileTransferTransferState.Sending, GetPrivateProperty<FileTransferTransferState>(context, "State"));
            Assert.Same(dataSession, GetPrivatePropertyValue(context, "DataSession"));
            Assert.DoesNotContain(dataSession.SentFrames, static frame => frame is FileTransferErrorFrameV4);
            Assert.DoesNotContain(dataSession.SentFrames, static frame => frame is FileTransferCancelFrameV4);
        }
        finally
        {
            dataSession.ReleaseFirstReceiveFailure();
            TryCancelContextLifetime(context);
            dataSession.Dispose();
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceive_DequeuedFrontierConcurrentWithSuccessorInstallDispatchesExactlyOnce()
    {
        var transferId = $"transfer_sparse_dequeued_frontier_successor_install_{Guid.NewGuid():N}";
        var sessionId = $"session_{transferId}";
        const string repairRequestId = "one-shot-dequeued-before-successor-install";
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var receiveDequeued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDequeuedReceive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousBarrier = SessionFileTransferService.OutboundSparseReceiveCompletionBarrierForTests;
        SessionFileTransferService.OutboundSparseReceiveCompletionBarrierForTests = async (observedTransferId, _) =>
        {
            if (!string.Equals(observedTransferId, transferId, StringComparison.Ordinal))
            {
                return;
            }

            receiveDequeued.TrySetResult();
            await releaseDequeuedReceive.Task;
        };
        var supervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            await WaitUntilAsync(
                () => transport.GetDataSessionReceiveStateForTests(transferId).Active &&
                      GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is not null,
                timeoutMs: 5000);
            var predecessorReceive = transport.GetDataSessionReceiveStateForTests(transferId);
            var predecessorInstall = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump")!;
            var dataSession = Assert.IsAssignableFrom<IFileTransferDataSession>(
                GetPrivatePropertyValue(context, "DataSession"));

            transport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
            transport.ReceiveDeliveredDataFrame(new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 3,
                RepairRequestId = repairRequestId,
                RequestCorrelationId = "frq-one-shot-dequeued-before-successor-install",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            });
            await receiveDequeued.Task.WaitAsync(TimeSpan.FromSeconds(5));

            RoutePublicationToken successorPublication;
            object successorInstall;
            lock (GetServiceGate(service))
            {
                successorPublication = ReplaceSparseAuthorityRetainingOwner(context, "route_publication");
                var staged = StageSparsePumpInstall(
                    service,
                    context,
                    predecessorInstall,
                    "unit_test_dequeued_receive_successor_install");
                Assert.True(staged.Accepted, staged.Reason);
                Assert.NotNull(staged.Install);
                var committed = CommitSparsePumpInstall(
                    service,
                    context,
                    staged.Install!,
                    dataSession);
                Assert.True(committed.Accepted, committed.Reason);
                Assert.Same(staged.Install, committed.Install);
                successorInstall = committed.Install!;
            }

            releaseDequeuedReceive.TrySetResult();
            await WaitUntilAsync(
                () =>
                {
                    var receive = transport.GetDataSessionReceiveStateForTests(transferId);
                    return receive.Active && receive.Ordinal > predecessorReceive.Ordinal;
                },
                timeoutMs: 5000);
            await WaitUntilAsync(
                () => GetPrivateProperty<int>(context, "ChunksAcceptedForTransport") >= 1,
                timeoutMs: 5000);

            Assert.Single(transport.SentDataFrames.Where(frame =>
                string.Equals(frame.TransferId, transferId, StringComparison.Ordinal) &&
                frame is FileTransferChunkBatchFrameV6));
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
            Assert.Equal(successorPublication, GetPrivateProperty<RoutePublicationToken>(successorInstall, "RoutePublication"));
        }
        finally
        {
            releaseDequeuedReceive.TrySetResult();
            SessionFileTransferService.OutboundSparseReceiveCompletionBarrierForTests = previousBarrier;
            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData("session_open")]
    [InlineData("manifest")]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceiveSupervisor_PreLoopAuthorityRejectionCannotOrphanNonterminalTransfer(
        string rejectionWindow)
    {
        var transferId = $"transfer_sparse_startup_{rejectionWindow}_authority_rejected";
        var sessionId = $"session_{transferId}";
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var authorityChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutePublicationToken? replacementPublication = null;
        var authorityChangeCount = 0;
        void ReplaceStartupAuthorityOnce()
        {
            if (Interlocked.CompareExchange(ref authorityChangeCount, 1, 0) != 0)
            {
                return;
            }

            lock (GetServiceGate(service))
            {
                replacementPublication = ReplaceSparseAuthorityRetainingOwner(context, "route_publication");
            }

            authorityChanged.TrySetResult();
        }

        EventHandler<SessionFileTransferSnapshotChangedEventArgs>? transferChanged = null;
        if (string.Equals(rejectionWindow, "session_open", StringComparison.Ordinal))
        {
            transferChanged = (_, args) =>
            {
                if (args.Snapshot.Outbound is { } outbound &&
                    string.Equals(outbound.TransferId, transferId, StringComparison.Ordinal) &&
                    outbound.State == FileTransferTransferState.AwaitingStart)
                {
                    ReplaceStartupAuthorityOnce();
                }
            };
            service.TransferChanged += transferChanged;
        }
        else
        {
            transport.OutboundSessionOpenDeliveryOverrideAsync = (_, message, _) =>
            {
                if (string.Equals(message.TransferId, transferId, StringComparison.Ordinal))
                {
                    ReplaceStartupAuthorityOnce();
                }

                return Task.FromResult(true);
            };
        }

        var supervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            await authorityChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () =>
                {
                    if (supervisor.IsCompleted || GetPrivateProperty<bool>(context, "IsTerminal"))
                    {
                        return true;
                    }

                    var installed = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump");
                    return replacementPublication is { } replacement &&
                           installed is not null &&
                           GetPrivateProperty<RoutePublicationToken>(installed, "RoutePublication") == replacement;
                },
                timeoutMs: 5000);

            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
            Assert.True(
                GetPrivateProperty<long>(context, "ActiveOutboundSparseReceiveSupervisorGeneration") > 0,
                "An exact-authority startup rejection left a nonterminal transfer without a receive supervisor.");
            var installed = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump");
            Assert.NotNull(installed);
            Assert.Equal(
                replacementPublication,
                GetPrivateProperty<RoutePublicationToken>(installed, "RoutePublication"));
        }
        finally
        {
            if (transferChanged is not null)
            {
                service.TransferChanged -= transferChanged;
            }

            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceiveSupervisor_RegularV4FrontierRepairPressureInstalledDuringOpenRejectsLateSession()
    {
        const string transferId = "transfer_sparse_open_pressure_session_race";
        var sessionId = $"session_{transferId}";
        using var lateOpenedSession = new DisposableTrackingDataSession(sessionId, transferId);
        using var attachedSession = new RecordingDataSession(sessionId, transferId);
        using var transport = new ControlledOpenLoopbackTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 256,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        var initialSession = Assert.IsAssignableFrom<IDisposable>(
            GetPrivatePropertyValue(context, "DataSession"));
        initialSession.Dispose();
        SetPrivateProperty(context, "DataSession", null);
        SetPrivateProperty(context, "DataSessionGeneration", 0L);
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(
            context,
            "PullV4LastPeerFrameReceivedUtc",
            DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var openStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.OpenOverrideAsync = async (_, _, _, ct) =>
        {
            openStarted.TrySetResult();
            await releaseOpen.Task.WaitAsync(ct);
            return lateOpenedSession;
        };
        var supervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            await openStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            object installedBinding;
            lock (GetServiceGate(service))
            {
                ReplaceOutboundDataSession(service, context, attachedSession);
                Assert.Same(attachedSession, GetPrivatePropertyValue(context, "DataSession"));
                Assert.True((bool)typeof(SessionFileTransferService)
                    .GetMethod(
                        "MaybeQueueOutboundRegularNknV4PeerSilenceSafetyReplayLocked",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, [context, "session_open_race_pressure_install"])!);
                installedBinding = Assert.IsType<RegularV4FrontierRepairPressureBinding>(
                    GetPrivatePropertyValue(
                        context,
                        "InstalledRegularV4FrontierRepairPressureBinding"));
                SetPrivateProperty(context, "UserPaused", true);
            }

            releaseOpen.TrySetResult();
            await lateOpenedSession.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            lock (GetServiceGate(service))
            {
                Assert.Same(attachedSession, GetPrivatePropertyValue(context, "DataSession"));
                Assert.Equal(1L, GetPrivateProperty<long>(context, "DataSessionGeneration"));
                Assert.Equal(
                    installedBinding,
                    GetPrivatePropertyValue(
                        context,
                        "InstalledRegularV4FrontierRepairPressureBinding"));
                Assert.Null(GetPrivatePropertyValue(
                    context,
                    "PendingRegularV4FrontierRepairPressureBinding"));
            }

            Assert.Empty(transport.RegularV4FrontierRepairSettlements);
            Assert.Equal(1, lateOpenedSession.DisposeCount);
        }
        finally
        {
            releaseOpen.TrySetResult();
            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceiveSupervisor_StaleOpenedSessionDisposedWhenNextStartupOpenFails()
    {
        const string transferId = "transfer_sparse_stale_open_disposed_after_next_failure";
        var sessionId = $"session_{transferId}";
        using var staleOpenedSession = new DisposableTrackingDataSession(sessionId, transferId);
        using var transport = new ControlledOpenLoopbackTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: 8);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var firstOpenStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondOpenStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.OpenOverrideAsync = async (attempt, _, _, ct) =>
        {
            if (attempt == 1)
            {
                firstOpenStarted.TrySetResult();
                await releaseFirstOpen.Task.WaitAsync(ct);
                return staleOpenedSession;
            }

            secondOpenStarted.TrySetResult();
            throw new InvalidOperationException("Injected second sparse startup open failure.");
        };
        var supervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            await firstOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lock (GetServiceGate(service))
            {
                ReplaceSparseAuthorityRetainingOwner(context, "route_publication");
            }

            releaseFirstOpen.TrySetResult();
            await secondOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, staleOpenedSession.DisposeCount);
            Assert.NotSame(staleOpenedSession, GetPrivatePropertyValue(context, "DataSession"));
        }
        finally
        {
            releaseFirstOpen.TrySetResult();
            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceiveSupervisor_PublicationReplacementDuringStartupRetriesOneBoundTuple()
    {
        const string transferId = "transfer_sparse_startup_publication_replaced";
        var sessionId = $"session_{transferId}";
        using var transport = new ControlledOpenLoopbackTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var firstOpenRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementDataSession = new RebindableSparseReceiveDataSession(sessionId, transferId);
        transport.OpenOverrideAsync = async (attempt, openSessionId, openTransferId, ct) =>
        {
            if (attempt == 1)
            {
                transport.FirstOpenStarted.TrySetResult();
                await firstOpenRelease.Task.WaitAsync(ct);
                return await transport.OpenBaseAsync(openSessionId, openTransferId, ct);
            }

            return replacementDataSession;
        };
        var supervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            await transport.FirstOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            RoutePublicationToken replacementPublication;
            lock (GetServiceGate(service))
            {
                replacementPublication = ReplaceSparseAuthorityRetainingOwner(context, "route_publication");
            }

            firstOpenRelease.TrySetResult();
            await WaitUntilAsync(
                () => transport.OpenAttemptCount >= 2 &&
                      transport.SentSessionOpens.Count(open => open.TransferId == transferId) == 1 &&
                      replacementDataSession.SentFrames.OfType<FileTransferManifestFrameV6>()
                          .Count(frame => frame.TransferId == transferId) == 1,
                timeoutMs: 5000);

            var sessionOpen = Assert.Single(transport.SentSessionOpens.Where(open => open.TransferId == transferId));
            var manifest = Assert.Single(replacementDataSession.SentFrames
                .OfType<FileTransferManifestFrameV6>()
                .Where(frame => frame.TransferId == transferId));
            Assert.Equal(replacementPublication.TargetProtocolVersion, sessionOpen.ProtocolVersion);
            Assert.Equal(replacementPublication.RouteToken, sessionOpen.FileTransferRoute);
            Assert.Equal(sessionOpen.SessionId, manifest.SessionId);
            var attachedSession = Assert.IsAssignableFrom<IFileTransferDataSession>(
                GetPrivatePropertyValue(context, "DataSession"));
            Assert.Same(replacementDataSession, attachedSession);
            var manifestAuthority = Assert.Single(replacementDataSession.SentAuthorities);
            Assert.Equal(replacementPublication.PublicationGeneration, manifestAuthority.RoutePublicationGeneration);
            Assert.Equal(replacementPublication.RuntimeGeneration, manifestAuthority.RuntimeGeneration);
            Assert.Equal(replacementPublication.LegGeneration, manifestAuthority.LegGeneration);
            var installed = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump");
            Assert.NotNull(installed);
            Assert.Equal(
                typeof(SessionFileTransferService).GetNestedType("OutboundSparsePumpInstall", BindingFlags.NonPublic),
                installed!.GetType());
            Assert.Equal(replacementPublication, GetPrivateProperty<RoutePublicationToken>(installed, "RoutePublication"));
        }
        finally
        {
            firstOpenRelease.TrySetResult();
            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceive_FrontierApplyRebindsLatestInstallAndDispatchesExactlyOnce()
    {
        var transferId = $"transfer_sparse_frontier_final_apply_{Guid.NewGuid():N}";
        var sessionId = $"session_{transferId}";
        const string repairRequestId = "frontier-final-apply-race";
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var applyReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousBarrier = SessionFileTransferService.OutboundSparseFrontierApplyBarrierForTests;
        SessionFileTransferService.OutboundSparseFrontierApplyBarrierForTests = async (observedTransferId, _) =>
        {
            if (!string.Equals(observedTransferId, transferId, StringComparison.Ordinal))
            {
                return;
            }

            applyReached.TrySetResult();
            await releaseApply.Task;
        };
        var logStart = GetOperationalLogLength();
        var supervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            await WaitUntilAsync(
                () => transport.GetDataSessionReceiveStateForTests(transferId).Active &&
                      GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is not null,
                timeoutMs: 5000);
            var predecessorInstall = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump")!;
            var dataSession = Assert.IsAssignableFrom<IFileTransferDataSession>(
                GetPrivatePropertyValue(context, "DataSession"));
            transport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
            transport.ReceiveDeliveredDataFrame(new FileTransferFrontierRequestFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                TransportEpoch = 3,
                RepairRequestId = repairRequestId,
                RequestCorrelationId = $"frq:{repairRequestId}",
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            });
            await applyReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

            object successorInstall;
            lock (GetServiceGate(service))
            {
                ReplaceSparseAuthorityRetainingOwner(context, "route_publication");
                var staged = StageSparsePumpInstall(
                    service,
                    context,
                    predecessorInstall,
                    "unit_test_frontier_final_apply_successor");
                Assert.True(staged.Accepted, staged.Reason);
                Assert.NotNull(staged.Install);
                var committed = CommitSparsePumpInstall(service, context, staged.Install!, dataSession);
                Assert.True(committed.Accepted, committed.Reason);
                successorInstall = committed.Install!;
            }

            releaseApply.TrySetResult();
            await WaitUntilAsync(
                () => GetPrivateProperty<int>(context, "ChunksAcceptedForTransport") >= 1,
                timeoutMs: 5000);

            var repairRequests = Assert.IsAssignableFrom<System.Collections.IDictionary>(
                GetPrivatePropertyValue(context, "PullV4SenderPumpRepairRequests"));
            Assert.Single(repairRequests.Keys.Cast<object>());
            var currentInstall = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump");
            Assert.NotNull(currentInstall);
            Assert.Equal(
                GetPrivatePropertyValue(successorInstall, "RuntimeOwner"),
                GetPrivatePropertyValue(currentInstall!, "RuntimeOwner"));
            Assert.Equal(
                GetPrivatePropertyValue(successorInstall, "RoutePublication"),
                GetPrivatePropertyValue(currentInstall!, "RoutePublication"));
            Assert.True(
                Assert.IsType<long>(GetPrivatePropertyValue(currentInstall!, "InstallGeneration")) >=
                Assert.IsType<long>(GetPrivatePropertyValue(successorInstall, "InstallGeneration")));
            var dispatchLine = LocalOperationalLog.GetRecentLogText()
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Single(line =>
                    line.Contains("event=filetransfer_v6_frontier_request_receive_dispatched;", StringComparison.Ordinal) &&
                    line.Contains($"request_correlation_id=frq:{repairRequestId};", StringComparison.Ordinal));
            Assert.Contains(
                $"install_generation={GetPrivateProperty<long>(successorInstall, "InstallGeneration")};",
                dispatchLine,
                StringComparison.Ordinal);
            Assert.Contains(
                $"route_publication_generation={GetPrivateProperty<RoutePublicationToken>(successorInstall, "RoutePublication").PublicationGeneration};",
                dispatchLine,
                StringComparison.Ordinal);
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
        }
        finally
        {
            releaseApply.TrySetResult();
            SessionFileTransferService.OutboundSparseFrontierApplyBarrierForTests = previousBarrier;
            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceive_StateSideEffectsCannotCrossSuccessorInstall()
    {
        var transferId = $"transfer_sparse_state_side_effect_race_{Guid.NewGuid():N}";
        var sessionId = $"session_{transferId}";
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var stateApplyReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStateApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousBarrier = SessionFileTransferService.OutboundSparseStateApplyBarrierForTests;
        SessionFileTransferService.OutboundSparseStateApplyBarrierForTests = async (observedTransferId, _) =>
        {
            if (!string.Equals(observedTransferId, transferId, StringComparison.Ordinal))
            {
                return;
            }

            stateApplyReached.TrySetResult();
            await releaseStateApply.Task;
        };
        var supervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            await WaitUntilAsync(
                () => transport.GetDataSessionReceiveStateForTests(transferId).Active &&
                      GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is not null,
                timeoutMs: 5000);
            var predecessorInstall = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump")!;
            var dataSession = Assert.IsAssignableFrom<IFileTransferDataSession>(
                GetPrivatePropertyValue(context, "DataSession"));
            transport.NextDataFrameTransportKind = FileTransferTransportKind.RegularNkn;
            transport.ReceiveDeliveredDataFrame(new FileTransferReceiverStateFrameV6
            {
                SessionId = sessionId,
                TransferId = transferId,
                Epoch = 1,
                ContiguousCommittedChunkIndex = 0,
                DurableReceivedHighestChunkIndex = 0,
                CreditUntilChunkIndexExclusive = 0,
                MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                BytesCommitted = 0,
                TransportEpoch = 3,
                RepairRequestId = "state-side-effect-race",
                Priority = "frontier",
                RecoveryMode = "frontier_repair_only",
            });
            await stateApplyReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

            object successorInstall;
            int repairQueueCount;
            int repairRequestCount;
            int sentChunkBatchCount;
            lock (GetServiceGate(service))
            {
                ReplaceSparseAuthorityRetainingOwner(context, "route_publication");
                var staged = StageSparsePumpInstall(
                    service,
                    context,
                    predecessorInstall,
                    "unit_test_state_side_effect_successor");
                Assert.True(staged.Accepted, staged.Reason);
                Assert.NotNull(staged.Install);
                var committed = CommitSparsePumpInstall(service, context, staged.Install!, dataSession);
                Assert.True(committed.Accepted, committed.Reason);
                successorInstall = committed.Install!;
                repairQueueCount = Assert.IsAssignableFrom<System.Collections.ICollection>(
                    GetPrivatePropertyValue(context, "PullV4SenderPumpRepairQueue")).Count;
                repairRequestCount = Assert.IsAssignableFrom<System.Collections.ICollection>(
                    GetPrivatePropertyValue(context, "PullV4SenderPumpRepairRequests")).Count;
                sentChunkBatchCount = transport.SentDataFrames.Count(frame => frame is FileTransferChunkBatchFrameV6);
            }

            releaseStateApply.TrySetResult();
            await Task.Delay(250);

            Assert.Equal(
                repairQueueCount,
                Assert.IsAssignableFrom<System.Collections.ICollection>(
                    GetPrivatePropertyValue(context, "PullV4SenderPumpRepairQueue")).Count);
            Assert.Equal(
                repairRequestCount,
                Assert.IsAssignableFrom<System.Collections.ICollection>(
                    GetPrivatePropertyValue(context, "PullV4SenderPumpRepairRequests")).Count);
            Assert.Equal(
                sentChunkBatchCount,
                transport.SentDataFrames.Count(frame => frame is FileTransferChunkBatchFrameV6));
            var currentInstall = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump");
            Assert.NotNull(currentInstall);
            Assert.Equal(
                GetPrivatePropertyValue(successorInstall, "RuntimeOwner"),
                GetPrivatePropertyValue(currentInstall!, "RuntimeOwner"));
            Assert.Equal(
                GetPrivatePropertyValue(successorInstall, "RoutePublication"),
                GetPrivatePropertyValue(currentInstall!, "RoutePublication"));
            Assert.True(
                Assert.IsType<long>(GetPrivatePropertyValue(currentInstall!, "InstallGeneration")) >=
                Assert.IsType<long>(GetPrivatePropertyValue(successorInstall, "InstallGeneration")));
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
        }
        finally
        {
            releaseStateApply.TrySetResult();
            SessionFileTransferService.OutboundSparseStateApplyBarrierForTests = previousBarrier;
            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceive_ExceptionRecoveryAwaitsSameSessionReaderRetirement()
    {
        var transferId = $"transfer_sparse_exception_reader_retirement_{Guid.NewGuid():N}";
        var sessionId = $"session_{transferId}";
        using var dataSession = new SlowCancellationSparseReceiveDataSession(sessionId, transferId);
        using var failingStream = new CoordinatedDisconnectingReadStream();
        var sourceOpenCount = 0;
        using var transport = new ControlledOpenLoopbackTransport(sessionId)
        {
            OpenOverrideAsync = (_, _, _, _) => Task.FromResult<IFileTransferDataSession>(dataSession),
        };
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(
                Interlocked.Increment(ref sourceOpenCount) == 1
                    ? failingStream
                    : new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 1,
            chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var supervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            await dataSession.FirstReceiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var initialInstall = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump");
            Assert.NotNull(initialInstall);
            var initialInstallGeneration = GetPrivateProperty<long>(initialInstall!, "InstallGeneration");
            lock (GetServiceGate(service))
            {
                SetPrivateProperty(context, "RemoteGrantedUntilExclusive", 1);
                SetPrivateProperty(context, "ChunksAcceptedForTransport", 0);
            }
            typeof(SessionFileTransferService)
                .GetMethod("SignalOutboundSparseSenderPump", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, null, null, null, 0L]);
            await failingStream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            failingStream.ReleaseFailure();
            await WaitUntilAsync(
                () => GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is { } installed &&
                      GetPrivateProperty<long>(installed, "InstallGeneration") > initialInstallGeneration,
                timeoutMs: 5000);

            Assert.False(
                dataSession.SecondReceiveStarted.Task.IsCompleted,
                "Exceptional sender recovery started a second reader on the still-attached data session.");
            Assert.False(
                dataSession.FirstReceiveCancellationObserved.Task.IsCompleted,
                "Same-session sender recovery canceled the session-scoped receive reader.");
            Assert.Equal(1, dataSession.MaxConcurrentReaders);
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
        }
        finally
        {
            dataSession.ReleaseFirstReaderUnwind();
            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseWorker_FailureDeferralSupersededBeforeMutationCannotAlterSuccessor()
    {
        var transferId = $"transfer_sparse_failure_deferral_superseded_{Guid.NewGuid():N}";
        var sessionId = $"session_{transferId}";
        using var dataSession = new SlowCancellationSparseReceiveDataSession(sessionId, transferId);
        using var failingStream = new CoordinatedDisconnectingReadStream();
        var sourceOpenCount = 0;
        using var transport = new ControlledOpenLoopbackTransport(sessionId)
        {
            OpenOverrideAsync = (_, _, _, _) => Task.FromResult<IFileTransferDataSession>(dataSession),
        };
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(
                Interlocked.Increment(ref sourceOpenCount) == 1
                    ? failingStream
                    : new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 1,
            chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var deferralReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDeferral = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalAuthorizationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTerminalAuthorization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferralBarrierCount = 0;
        var previousDeferralBarrier = SessionFileTransferService.OutboundSparseFailureDeferralBarrierForTests;
        var previousTerminalBarrier = SessionFileTransferService.OutboundSparseFailureAuthorizationBarrierForTests;
        SessionFileTransferService.OutboundSparseFailureDeferralBarrierForTests = async (observedTransferId, _) =>
        {
            if (!string.Equals(observedTransferId, transferId, StringComparison.Ordinal) ||
                Interlocked.Increment(ref deferralBarrierCount) != 1)
            {
                return;
            }

            deferralReached.TrySetResult();
            await releaseDeferral.Task;
        };
        SessionFileTransferService.OutboundSparseFailureAuthorizationBarrierForTests = async (observedTransferId, _) =>
        {
            if (!string.Equals(observedTransferId, transferId, StringComparison.Ordinal))
            {
                return;
            }

            terminalAuthorizationReached.TrySetResult();
            await releaseTerminalAuthorization.Task;
        };
        var supervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            await dataSession.FirstReceiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var predecessorInstall = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump");
            Assert.NotNull(predecessorInstall);
            lock (GetServiceGate(service))
            {
                SetPrivateProperty(context, "RemoteGrantedUntilExclusive", 1);
                SetPrivateProperty(context, "ChunksAcceptedForTransport", 0);
            }
            typeof(SessionFileTransferService)
                .GetMethod("SignalOutboundSparseSenderPump", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, null, null, null, 0L]);
            await failingStream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            failingStream.ReleaseFailure();
            await deferralReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

            object successorInstall;
            RoutePublicationToken successorPublication;
            lock (GetServiceGate(service))
            {
                successorPublication = ReplaceSparseAuthorityRetainingOwner(context, "route_publication");
                var successorStage = StageSparsePumpInstall(
                    service,
                    context,
                    predecessorInstall,
                    "unit_test_failure_deferral_successor");
                Assert.True(successorStage.Accepted, successorStage.Reason);
                Assert.NotNull(successorStage.Install);
                var successorCommit = CommitSparsePumpInstall(
                    service,
                    context,
                    successorStage.Install!,
                    dataSession);
                Assert.True(successorCommit.Accepted, successorCommit.Reason);
                successorInstall = successorCommit.Install!;
                SetPrivateProperty(context, "RemoteGrantedUntilExclusive", 0);
                SetPrivateProperty(context, "ChunksAcceptedForTransport", 0);
            }

            var expectedTransportPaused = GetPrivateProperty<bool>(context, "PullTransportPaused");
            var expectedPausedSince = GetPrivatePropertyValue(context, "PullTransportPausedSinceUtc");
            var expectedGraceDeadline = GetPrivatePropertyValue(context, "PullTransportGraceDeadlineUtc");
            var expectedPauseReason = GetPrivatePropertyValue(context, "PullTransportPauseReason");
            var expectedLastPauseReason = GetPrivatePropertyValue(context, "PullTransportLastPauseReason");
            var expectedResumePending = GetPrivateProperty<bool>(context, "PullTransportResumeRequestPending");
            var expectedStatusMessage = GetPrivateProperty<string>(context, "StatusMessage");
            var expectedSparseWakeReason = GetPrivateProperty<string>(context, "SparseSenderPumpLastWakeReason");
            var expectedV6WakeReason = GetPrivateProperty<string>(context, "V6SenderPumpLastWakeReason");
            var expectedLastRecovery = GetPrivatePropertyValue(context, "V6LastReceiveRecoveryRequestedUtc");
            var expectedEpochLivenessDeferrals = GetPrivateProperty<int>(context, "V6EpochLivenessDeferralCount");
            var expectedPeerLivenessDeferrals = GetPrivateProperty<int>(context, "V6PeerLivenessRecoveryDeferralCount");

            releaseDeferral.TrySetResult();
            await terminalAuthorizationReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Same(successorInstall, GetPrivatePropertyValue(context, "InstalledOutboundSparsePump"));
            Assert.Equal(successorPublication, GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken"));
            Assert.Equal(expectedTransportPaused, GetPrivateProperty<bool>(context, "PullTransportPaused"));
            Assert.Equal(expectedPausedSince, GetPrivatePropertyValue(context, "PullTransportPausedSinceUtc"));
            Assert.Equal(expectedGraceDeadline, GetPrivatePropertyValue(context, "PullTransportGraceDeadlineUtc"));
            Assert.Equal(expectedPauseReason, GetPrivatePropertyValue(context, "PullTransportPauseReason"));
            Assert.Equal(expectedLastPauseReason, GetPrivatePropertyValue(context, "PullTransportLastPauseReason"));
            Assert.Equal(expectedResumePending, GetPrivateProperty<bool>(context, "PullTransportResumeRequestPending"));
            Assert.Equal(expectedStatusMessage, GetPrivateProperty<string>(context, "StatusMessage"));
            Assert.Equal(expectedSparseWakeReason, GetPrivateProperty<string>(context, "SparseSenderPumpLastWakeReason"));
            Assert.Equal(expectedV6WakeReason, GetPrivateProperty<string>(context, "V6SenderPumpLastWakeReason"));
            Assert.Equal(expectedLastRecovery, GetPrivatePropertyValue(context, "V6LastReceiveRecoveryRequestedUtc"));
            Assert.Equal(expectedEpochLivenessDeferrals, GetPrivateProperty<int>(context, "V6EpochLivenessDeferralCount"));
            Assert.Equal(expectedPeerLivenessDeferrals, GetPrivateProperty<int>(context, "V6PeerLivenessRecoveryDeferralCount"));
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
        }
        finally
        {
            releaseDeferral.TrySetResult();
            releaseTerminalAuthorization.TrySetResult();
            SessionFileTransferService.OutboundSparseFailureDeferralBarrierForTests = previousDeferralBarrier;
            SessionFileTransferService.OutboundSparseFailureAuthorizationBarrierForTests = previousTerminalBarrier;
            dataSession.ReleaseFirstReaderUnwind();
            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceiveSupervisor_PostInstallFailureRestartsWithoutBootstrapReplay()
    {
        var transferId = $"transfer_sparse_post_install_restart_{Guid.NewGuid():N}";
        var sessionId = $"session_{transferId}";
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var firstInstallReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartedInstallReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrierCount = 0;
        var previousBarrier = SessionFileTransferService.OutboundSparsePostInstallBarrierForTests;
        SessionFileTransferService.OutboundSparsePostInstallBarrierForTests = (observedTransferId, _) =>
        {
            if (!string.Equals(observedTransferId, transferId, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            if (Interlocked.Increment(ref barrierCount) == 1)
            {
                firstInstallReached.TrySetResult();
                throw new OperationCanceledException("Injected nonterminal transport cancellation after sparse install.");
            }

            restartedInstallReached.TrySetResult();
            return Task.CompletedTask;
        };
        var firstSupervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            await firstInstallReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var sessionOpenCount = transport.SentSessionOpens.Count(open => open.TransferId == transferId);
            var manifestCount = transport.SentDataFrames.OfType<FileTransferManifestFrameV6>()
                .Count(frame => frame.TransferId == transferId);
            Assert.Equal(1, sessionOpenCount);
            Assert.Equal(1, manifestCount);

            await firstSupervisor.WaitAsync(TimeSpan.FromSeconds(5));
            await restartedInstallReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => GetPrivateProperty<long>(context, "ActiveOutboundSparseReceiveSupervisorGeneration") > 0 &&
                      transport.GetDataSessionReceiveStateForTests(transferId).Active,
                timeoutMs: 5000);

            Assert.Equal(sessionOpenCount, transport.SentSessionOpens.Count(open => open.TransferId == transferId));
            Assert.Equal(
                manifestCount,
                transport.SentDataFrames.OfType<FileTransferManifestFrameV6>()
                    .Count(frame => frame.TransferId == transferId));
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
            Assert.True(GetPrivateProperty<long>(context, "ActiveOutboundSparseReceiveSupervisorGeneration") > 0);
        }
        finally
        {
            SessionFileTransferService.OutboundSparsePostInstallBarrierForTests = previousBarrier;
            TryCancelContextLifetime(context);
            await firstSupervisor.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => GetPrivateProperty<long>(context, "ActiveOutboundSparseReceiveSupervisorGeneration") == 0,
                timeoutMs: 5000);
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceiveSupervisor_IncompatiblePostSessionOpenCompletionRetriesCurrentAuthority()
    {
        var transferId = $"transfer_sparse_incompatible_session_open_{Guid.NewGuid():N}";
        var sessionId = $"session_{transferId}";
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: 8);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var routeChanged = 0;
        transport.OutboundSessionOpenDeliveryOverrideAsync = (_, message, _) =>
        {
            if (string.Equals(message.TransferId, transferId, StringComparison.Ordinal) &&
                Interlocked.CompareExchange(ref routeChanged, 1, 0) == 0)
            {
                lock (GetServiceGate(service))
                {
                    ReplaceSparseRouteAuthority(context, FileTransferRoute.RegularNknV4Fast);
                }
            }

            return Task.FromResult(true);
        };
        var supervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            await WaitUntilAsync(
                () => Volatile.Read(ref routeChanged) == 1 &&
                      GetPrivatePropertyValue(context, "InstalledOutboundSparsePump") is { } installed &&
                      GetPrivateProperty<RoutePublicationToken>(installed, "RoutePublication").Route ==
                      FileTransferRoute.RegularNknV4Fast &&
                      GetPrivateProperty<long>(context, "ActiveOutboundSparseReceiveSupervisorGeneration") > 0,
                timeoutMs: 5000);

            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
            var currentInstall = GetPrivatePropertyValue(context, "InstalledOutboundSparsePump");
            Assert.NotNull(currentInstall);
            Assert.Equal(
                FileTransferRoute.RegularNknV4Fast,
                GetPrivateProperty<RoutePublicationToken>(currentInstall!, "RoutePublication").Route);
            Assert.Empty(transport.SentErrors.Where(error => error.TransferId == transferId));
            Assert.Empty(transport.SentCancels.Where(cancel => cancel.TransferId == transferId));
        }
        finally
        {
            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task OutboundSparseReceiveSupervisor_AuthorityAbsentAfterFailedAttachDisposesSessionPromptly()
    {
        var transferId = $"transfer_sparse_unbound_session_authority_absent_{Guid.NewGuid():N}";
        var sessionId = $"session_{transferId}";
        using var unattachedSession = new DisposableTrackingDataSession(sessionId, transferId);
        using var transport = new ControlledOpenLoopbackTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            chunkCount: 8);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var openStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.OpenOverrideAsync = async (_, _, _, ct) =>
        {
            openStarted.TrySetResult();
            await releaseOpen.Task.WaitAsync(ct);
            return unattachedSession;
        };
        var supervisor = StartSparseReceiveSupervisor(service, context);

        try
        {
            await openStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lock (GetServiceGate(service))
            {
                SetPrivateProperty(context, "CurrentRuntimeOwner", null);
                SetPrivateProperty(context, "CurrentRoutePublicationToken", null);
            }

            releaseOpen.TrySetResult();
            await unattachedSession.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, unattachedSession.DisposeCount);
            Assert.Equal(1, transport.OpenAttemptCount);
            Assert.Null(GetPrivatePropertyValue(context, "DataSession"));
            Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
            Assert.False(supervisor.IsCompleted);
        }
        finally
        {
            releaseOpen.TrySetResult();
            TryCancelContextLifetime(context);
            await supervisor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static object GetServiceGate(SessionFileTransferService service)
        => typeof(SessionFileTransferService)
            .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;

    private static Task StartSparseReceiveSupervisor(
        SessionFileTransferService service,
        object context)
    {
        var runtimeKindType = typeof(SessionFileTransferService)
            .GetNestedType("FileTransferSparseCreditRuntimeKind", BindingFlags.NonPublic)!;
        var runtimeKind = Enum.Parse(runtimeKindType, "PrimaryRegularNknV4");
        return Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
            .GetMethod("RunOutboundSparseCreditSenderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, runtimeKind]));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "PostTunaFallbackV6RecoverySubmissionFailure")]
    public void PostTunaFallbackV6RecoverySubmissionFailure_CurrentEpochBarrierIsDeferredWithoutNewAuthority()
    {
        var transferId = $"transfer_post_tuna_epoch_deferral_{Guid.NewGuid():N}";
        var sessionId = $"session_{transferId}";
        var logStart = GetOperationalLogLength();
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var service = new SessionFileTransferService();
        using var dataSession = new RecordingDataSession(sessionId, transferId);
        service.AttachTransport(transport);
        var context = CreateSparsePostTunaContextWithSourceFactory(
            transferId,
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[8 * 21 * 1024], writable: false)),
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 1,
            chunkCount: 8);
        SetPrivateProperty(context, "DataSession", dataSession);
        SetPrivateProperty(context, "DataSessionGeneration", 1L);
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        object install;
        lock (GetServiceGate(service))
        {
            var staged = StageSparsePumpInstall(service, context, predecessor: null, "unit_test_epoch_deferral");
            Assert.True(staged.Accepted, staged.Reason);
            var committed = CommitSparsePumpInstall(service, context, staged.Install!, dataSession);
            Assert.True(committed.Accepted, committed.Reason);
            install = committed.Install!;
        }

        Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod("TryRequestFileTransferReceiveRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    new FileTransferReceiveRecoveryRequest(
                        sessionId,
                        transferId,
                        FileTransferDirection.Outbound,
                        "session_liveness_timeout_pending"),
                ])));
        var before = GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState")
            .FileTransferReceiveRecovery.CurrentOperation;
        Assert.NotNull(before);
        Assert.Equal(FileTransferReceiveRecoveryExecutionState.Running, before.Value.ExecutionState);
        Assert.True(before.Value.LivenessDeferralDeadlineUtcMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var graceBefore = GetPrivatePropertyValue(context, "PullTransportGraceDeadlineUtc");
        SetPrivateProperty(context, "PullTransportPaused", true);
        SetPrivateProperty(context, "PullTransportPauseReason", "post_tuna_fallback_bridge_restart");
        SetPrivateProperty(context, "PullTransportResumeRequestPending", true);

        var holdArguments = new object?[] { context, null, null };
        Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod(
                "TryGetOutboundReceiveRecoveryPayloadSubmissionHoldLocked",
                BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, holdArguments)));
        Assert.Equal(before, Assert.IsType<FileTransferReceiveRecoveryOperation>(holdArguments[1]));

        var exception = new FileTransferDataSessionSubmissionUnavailableException(
            FileTransferDataSessionSubmissionUnavailableReason.SubmissionEpochChanged,
            "send_pre_submit",
            "bulk",
            expectedSubmissionEpoch: 3,
            currentSubmissionEpoch: 4,
            expectedProcessEpoch: 7,
            currentProcessEpoch: 8,
            recoveryBarrierActive: true,
            "NKN bridge process changed during payload submission.");
        var invokeArguments = new object?[]
        {
            context,
            exception,
            install,
            dataSession,
            91L,
            "fallback-frontier:test",
            null,
        };
        var deferred = Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod(
                "TryDeferCurrentPostTunaFallbackRecoverySubmissionFailure",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, invokeArguments));

        Assert.True(deferred);
        Assert.Equal(
            FileTransferReceiveRecoverySubmissionFailureDisposition.ActiveHold,
            Assert.IsType<FileTransferReceiveRecoverySubmissionFailureDisposition>(
                invokeArguments[6]));
        Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
        Assert.True(GetPrivateProperty<bool>(context, "PullTransportPaused"));
        Assert.True(GetPrivateProperty<bool>(context, "PullTransportResumeRequestPending"));
        Assert.Equal(
            "post_tuna_fallback_receive_recovery_submission_epoch_transition",
            GetPrivateProperty<string>(context, "PullTransportPauseReason"));
        Assert.Equal(
            1L,
            GetPrivateProperty<long>(
                context,
                "LastPostTunaFallbackReceiveRecoveryPayloadHoldGeneration"));
        Assert.NotNull(GetPrivatePropertyValue(
            context,
            "CurrentPostTunaFallbackReceiveRecoveryPayloadHold"));
        Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod(
                "TryDeferCurrentPostTunaFallbackReceiveRecoveryPayloadHoldIfCurrent",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    "sender_pump",
                    new InvalidOperationException(
                        "File-transfer V4 sender transport send failed.",
                        exception),
                    install,
                    dataSession,
                ])));
        Assert.Equal(graceBefore, GetPrivatePropertyValue(context, "PullTransportGraceDeadlineUtc"));
        var after = GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState")
            .FileTransferReceiveRecovery.CurrentOperation;
        Assert.Equal(before, after);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains(
            "event=filetransfer_post_tuna_fallback_recovery_submission_failure_deferred;",
            log,
            StringComparison.Ordinal);
        Assert.Contains("physical_send_lease_generation=91", log, StringComparison.Ordinal);
        Assert.Contains("typed_rejection_reason=SubmissionEpochChanged", log, StringComparison.Ordinal);
    }

    private static object CreateSparsePostTunaContextWithSourceFactory(
        string transferId,
        FileTransferReadStreamFactory sourceFactory,
        int remoteNextExpectedChunkIndex,
        int chunksAcceptedForTransport,
        int remoteGrantedUntilExclusive,
        int chunkCount,
        bool startCoordinatorSnapshotStream = true)
    {
        var serviceType = typeof(SessionFileTransferService);
        var contextType = serviceType.GetNestedType("OutboundTransferContext", BindingFlags.NonPublic)!;
        const int chunkSize = 21 * 1024;
        var descriptor = new FileTransferSendDescriptor(
            $"{transferId}.bin",
            (long)chunkCount * chunkSize,
            transferId);
        var context = Activator.CreateInstance(
            contextType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [descriptor, sourceFactory],
            culture: null)!;
        var routeSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6);
        var bridgePolicyType = serviceType.GetNestedType("FileTransferBridgeRecoveryPolicy", BindingFlags.NonPublic)!;
        SetPrivateProperty(context, "SessionId", $"session_{transferId}");
        SetPrivateProperty(context, "RouteSelection", routeSelection);
        SetPrivateProperty(context, "RouteRuntime", routeSelection.RuntimeDescriptor);
        SetPrivateProperty(
            context,
            "BridgeRecoveryPolicy",
            Enum.Parse(bridgePolicyType, "PostTunaFallbackStrictRecovery"));
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
        SetPrivateProperty(
            context,
            "PullTransportFrontierOnlyRepairStartChunkIndex",
            remoteNextExpectedChunkIndex);
        var leg = SeedCurrentRuntimeOwnerForTest(
            context,
            FileTransferDirection.Outbound,
            "unit_test_sparse_post_tuna_fallback_v6",
            startCoordinatorSnapshotStream);
        leg.TransportEpochId = 3;
        leg.BridgeRecoveryGeneration = 1;
        return context;
    }

    private static RoutePublicationToken ReplaceSparseAuthorityRetainingOwner(
        object context,
        string invalidation)
    {
        var publication = GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken");
        switch (invalidation)
        {
            case "route_publication":
            {
                var replacement = publication with
                {
                    PublicationGeneration = publication.PublicationGeneration + 1,
                    Reason = "unit_test_publication_replaced_retaining_owner",
                };
                SetPrivateProperty(context, "CurrentRoutePublicationToken", replacement);
                SetPrivateProperty(context, "LastRoutePublicationGeneration", replacement.PublicationGeneration);
                return replacement;
            }
            case "leg":
            {
                var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
                SetPrivateProperty(leg, "Generation", leg.Generation + 1);
                SetPrivateProperty(context, "LastTransferLegGeneration", leg.Generation);
                return publication;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidation), invalidation, null);
        }
    }

    private static RoutePublicationToken ReplaceSparseRouteAuthority(
        object context,
        FileTransferRoute route)
    {
        var routeSelection = FileTransferRouteResolver.Resolve(route);
        var owner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var publication = GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken");
        var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        SetPrivateProperty(leg, "Generation", leg.Generation + 1);
        SetPrivateProperty(leg, "RouteSelection", routeSelection);
        SetPrivateProperty(leg, "ProtocolVersion", routeSelection.ProtocolVersion);
        var replacementOwner = owner with
        {
            Route = routeSelection.Route,
            RouteToken = routeSelection.TelemetryToken,
            ProtocolVersion = routeSelection.ProtocolVersion,
            RuntimeGeneration = owner.RuntimeGeneration + 1,
            CreatedReason = "unit_test_incompatible_session_open_route_handoff",
        };
        var targetTransport = routeSelection.Route == FileTransferRoute.FileTunaV4
            ? FileTransferTransportKind.Tuna
            : FileTransferTransportKind.RegularNkn;
        var replacementPublication = publication with
        {
            RuntimeOwner = replacementOwner,
            Route = routeSelection.Route,
            RouteToken = routeSelection.TelemetryToken,
            ProtocolVersion = routeSelection.ProtocolVersion,
            RuntimeGeneration = replacementOwner.RuntimeGeneration,
            LegGeneration = leg.Generation,
            RouteCommitGeneration = null,
            TargetRoute = routeSelection.Route,
            TargetProtocolVersion = routeSelection.ProtocolVersion,
            TargetTransport = targetTransport,
            PublicationGeneration = publication.PublicationGeneration + 1,
            ActionKind = FileTransferCoordinatorActionKind.RoutePublication,
            Reason = "unit_test_incompatible_session_open_route_handoff",
        };
        SetPrivateProperty(context, "RouteSelection", routeSelection);
        SetPrivateProperty(context, "RouteRuntime", routeSelection.RuntimeDescriptor);
        SetPrivateProperty(context, "NegotiatedDataProtocolVersion", routeSelection.ProtocolVersion);
        SetPrivateProperty(context, "CurrentRuntimeOwner", replacementOwner);
        SetPrivateProperty(context, "LastRuntimeOwnerGeneration", replacementOwner.RuntimeGeneration);
        SetPrivateProperty(context, "CurrentRoutePublicationToken", replacementPublication);
        SetPrivateProperty(context, "LastRoutePublicationGeneration", replacementPublication.PublicationGeneration);
        SetPrivateProperty(context, "LastTransferLegGeneration", leg.Generation);
        return replacementPublication;
    }

    private static void TryCancelContextLifetime(object context)
    {
        try
        {
            GetPrivateProperty<CancellationTokenSource>(context, "LifetimeCts").Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class ControlledOpenLoopbackTransport(
        string sessionId) : LoopbackFileTransferTransport(sessionId), IFileTransferSignalingTransport
    {
        private int openAttemptCount;

        public int OpenAttemptCount => Volatile.Read(ref openAttemptCount);

        public TaskCompletionSource FirstOpenStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<int, string, string, CancellationToken, Task<IFileTransferDataSession>>?
            OpenOverrideAsync { get; set; }

        public Task<IFileTransferDataSession> OpenBaseAsync(
            string openSessionId,
            string transferId,
            CancellationToken ct)
            => base.OpenFileTransferDataSessionAsync(openSessionId, transferId, ct);

        Task<IFileTransferDataSession> IFileTransferSignalingTransport.OpenFileTransferDataSessionAsync(
            string openSessionId,
            string transferId,
            CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref openAttemptCount);
            return OpenOverrideAsync?.Invoke(attempt, openSessionId, transferId, ct) ??
                   base.OpenFileTransferDataSessionAsync(openSessionId, transferId, ct);
        }
    }

    private sealed class CoordinatedInitialReceiveDataSession(
        string sessionId,
        string transferId) : IFileTransferDataSession, IFileTransferDataPathAuthoritySession
    {
        private readonly ManualResetEventSlim receiveStartRelease = new(initialState: false);
        private readonly TaskCompletionSource<FileTransferReceivedDataFrame> firstReceive =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<FileTransferReceivedDataFrame> laterReceive =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int receiveCount;
        private int disposed;

        public string SessionId { get; } = sessionId;

        public string TransferId { get; } = transferId;

        public bool IsAvailable => Volatile.Read(ref disposed) == 0;

        public TaskCompletionSource ReceiveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<FileTransferDataFrame> SentFrames { get; } = [];

        public ConcurrentQueue<FileTransferDataPathSendAuthority> SentAuthorities { get; } = [];

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged;

        public async ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => (await ReceiveWithMetadataAsync(ct)).Frame;

        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (Interlocked.Increment(ref receiveCount) == 1)
            {
                ReceiveEntered.TrySetResult();
                receiveStartRelease.Wait(ct);
                return new ValueTask<FileTransferReceivedDataFrame>(firstReceive.Task.WaitAsync(ct));
            }

            return new ValueTask<FileTransferReceivedDataFrame>(laterReceive.Task.WaitAsync(ct));
        }

        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SentFrames.Enqueue(frame);
            return Task.CompletedTask;
        }

        public Task SendAsync(
            FileTransferDataFrame frame,
            FileTransferDataPathSendAuthority authority,
            CancellationToken ct)
        {
            SentAuthorities.Enqueue(authority);
            return SendAsync(frame, ct);
        }

        public void Deliver(FileTransferDataFrame frame, FileTransferTransportKind transportKind)
        {
            var lane = transportKind switch
            {
                FileTransferTransportKind.Tuna => "tuna",
                FileTransferTransportKind.RegularNkn => "regular_nkn",
                _ => "unknown",
            };
            firstReceive.TrySetResult(
                new FileTransferReceivedDataFrame(frame, transportKind, lane, DateTimeOffset.UtcNow));
        }

        public void ReleaseReceiveStart() => receiveStartRelease.Set();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            receiveStartRelease.Set();
            firstReceive.TrySetCanceled();
            laterReceive.TrySetCanceled();
            receiveStartRelease.Dispose();
            _ = AvailabilityChanged;
        }
    }

    private sealed class RebindableSparseReceiveDataSession(
        string sessionId,
        string transferId) : IFileTransferDataSession, IFileTransferDataPathAuthoritySession
    {
        private readonly System.Threading.Channels.Channel<FileTransferReceivedDataFrame> receivedFrames =
            System.Threading.Channels.Channel.CreateUnbounded<FileTransferReceivedDataFrame>();
        private readonly TaskCompletionSource oneShotDeliveryRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int receiveCount;
        private int disposed;

        public string SessionId { get; } = sessionId;

        public string TransferId { get; } = transferId;

        public bool IsAvailable => Volatile.Read(ref disposed) == 0;

        public int ReceiveCount => Volatile.Read(ref receiveCount);

        public TaskCompletionSource FirstReceiveCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<FileTransferDataFrame> SentFrames { get; } = [];

        public ConcurrentQueue<FileTransferDataPathSendAuthority> SentAuthorities { get; } = [];

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged;

        public async ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => (await ReceiveWithMetadataAsync(ct)).Frame;

        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var ordinal = Interlocked.Increment(ref receiveCount);
            return ObserveReceiveAsync(ordinal, ct);
        }

        private async ValueTask<FileTransferReceivedDataFrame> ObserveReceiveAsync(
            int ordinal,
            CancellationToken ct)
        {
            try
            {
                await oneShotDeliveryRelease.Task.WaitAsync(ct);
                return await receivedFrames.Reader.ReadAsync(ct);
            }
            catch (OperationCanceledException) when (ordinal == 1)
            {
                FirstReceiveCanceled.TrySetResult();
                throw;
            }
        }

        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SentFrames.Enqueue(frame);
            return Task.CompletedTask;
        }

        public Task SendAsync(
            FileTransferDataFrame frame,
            FileTransferDataPathSendAuthority authority,
            CancellationToken ct)
        {
            SentAuthorities.Enqueue(authority);
            return SendAsync(frame, ct);
        }

        public void Deliver(FileTransferDataFrame frame)
        {
            var delivered = receivedFrames.Writer.TryWrite(
                new FileTransferReceivedDataFrame(
                    frame,
                    FileTransferTransportKind.RegularNkn,
                    "regular_nkn",
                    DateTimeOffset.UtcNow));
            Assert.True(delivered, "The one-shot frontier could not be queued for the active data session.");
        }

        public void ReleaseOneShotDelivery() => oneShotDeliveryRelease.TrySetResult();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            oneShotDeliveryRelease.TrySetResult();
            receivedFrames.Writer.TryComplete();
            _ = AvailabilityChanged;
        }
    }

    private sealed class SynchronousFailingSparseReceiveDataSession(
        string sessionId,
        string transferId) : IFileTransferDataSession, IFileTransferDataPathAuthoritySession
    {
        private readonly ManualResetEventSlim firstReceiveFailureRelease = new(initialState: false);
        private readonly TaskCompletionSource<FileTransferReceivedDataFrame> laterReceive =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int receiveCount;
        private int disposed;

        public string SessionId { get; } = sessionId;

        public string TransferId { get; } = transferId;

        public bool IsAvailable => Volatile.Read(ref disposed) == 0;

        public int ReceiveCount => Volatile.Read(ref receiveCount);

        public TaskCompletionSource ReceiveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<FileTransferDataFrame> SentFrames { get; } = [];

        public ConcurrentQueue<FileTransferDataPathSendAuthority> SentAuthorities { get; } = [];

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged;

        public async ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => (await ReceiveWithMetadataAsync(ct)).Frame;

        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (Interlocked.Increment(ref receiveCount) == 1)
            {
                ReceiveEntered.TrySetResult();
                firstReceiveFailureRelease.Wait(ct);
                throw new InvalidOperationException("Injected synchronous sparse receive-start failure.");
            }

            return new ValueTask<FileTransferReceivedDataFrame>(laterReceive.Task.WaitAsync(ct));
        }

        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SentFrames.Enqueue(frame);
            return Task.CompletedTask;
        }

        public Task SendAsync(
            FileTransferDataFrame frame,
            FileTransferDataPathSendAuthority authority,
            CancellationToken ct)
        {
            SentAuthorities.Enqueue(authority);
            return SendAsync(frame, ct);
        }

        public void ReleaseFirstReceiveFailure() => firstReceiveFailureRelease.Set();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            firstReceiveFailureRelease.Set();
            laterReceive.TrySetCanceled();
            firstReceiveFailureRelease.Dispose();
            _ = AvailabilityChanged;
        }
    }

    private sealed class SlowCancellationSparseReceiveDataSession(
        string sessionId,
        string transferId) : IFileTransferDataSession, IFileTransferDataPathAuthoritySession
    {
        private readonly TaskCompletionSource firstReaderUnwindRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int activeReaders;
        private int maxConcurrentReaders;
        private int receiveCount;
        private int disposed;

        public string SessionId { get; } = sessionId;

        public string TransferId { get; } = transferId;

        public bool IsAvailable => Volatile.Read(ref disposed) == 0;

        public int MaxConcurrentReaders => Volatile.Read(ref maxConcurrentReaders);

        public TaskCompletionSource FirstReceiveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstReceiveCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondReceiveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<FileTransferDataFrame> SentFrames { get; } = [];

        public ConcurrentQueue<FileTransferDataPathSendAuthority> SentAuthorities { get; } = [];

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged;

        public async ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => (await ReceiveWithMetadataAsync(ct)).Frame;

        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var ordinal = Interlocked.Increment(ref receiveCount);
            return ObserveReceiveAsync(ordinal, ct);
        }

        private async ValueTask<FileTransferReceivedDataFrame> ObserveReceiveAsync(
            int ordinal,
            CancellationToken ct)
        {
            var readers = Interlocked.Increment(ref activeReaders);
            UpdateMaximum(ref maxConcurrentReaders, readers);
            if (ordinal == 1)
            {
                FirstReceiveStarted.TrySetResult();
            }
            else if (ordinal == 2)
            {
                SecondReceiveStarted.TrySetResult();
            }

            try
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException) when (ordinal == 1)
                {
                    FirstReceiveCancellationObserved.TrySetResult();
                    await firstReaderUnwindRelease.Task;
                    throw;
                }

                throw new InvalidOperationException("The injected sparse receive unexpectedly completed.");
            }
            finally
            {
                Interlocked.Decrement(ref activeReaders);
            }
        }

        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SentFrames.Enqueue(frame);
            return Task.CompletedTask;
        }

        public Task SendAsync(
            FileTransferDataFrame frame,
            FileTransferDataPathSendAuthority authority,
            CancellationToken ct)
        {
            SentAuthorities.Enqueue(authority);
            return SendAsync(frame, ct);
        }

        public void ReleaseFirstReaderUnwind() => firstReaderUnwindRelease.TrySetResult();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            firstReaderUnwindRelease.TrySetResult();
            _ = AvailabilityChanged;
        }

        private static void UpdateMaximum(ref int target, int candidate)
        {
            while (true)
            {
                var observed = Volatile.Read(ref target);
                if (observed >= candidate || Interlocked.CompareExchange(ref target, candidate, observed) == observed)
                {
                    return;
                }
            }
        }
    }

    private sealed class CoordinatedDisconnectingReadStream : Stream
    {
        private readonly TaskCompletionSource releaseFailure =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => 8L * 21 * 1024;

        public override long Position { get; set; }

        public void ReleaseFailure() => releaseFailure.TrySetResult();

        public override int Read(byte[] buffer, int offset, int count)
            => throw new OperationCanceledException("Injected recoverable sparse source cancellation.");

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await releaseFailure.Task.WaitAsync(cancellationToken);
            throw new OperationCanceledException("Injected recoverable sparse source cancellation.");
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return Position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }
    }

    private sealed class DisposableTrackingDataSession(
        string sessionId,
        string transferId) : IFileTransferDataSession
    {
        private int disposed;
        private int disposeCount;

        public string SessionId { get; } = sessionId;

        public string TransferId { get; } = transferId;

        public bool IsAvailable => Volatile.Read(ref disposed) == 0;

        public int DisposeCount => Volatile.Read(ref disposeCount);

        public TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged;

        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
            => throw new NotSupportedException();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            Interlocked.Increment(ref disposeCount);
            Disposed.TrySetResult();
            _ = AvailabilityChanged;
        }
    }

    private static (
        RuntimeUnlockAdmissionToken Admission,
        RuntimeUnlockPendingPreCommitProbeRequest PendingPreCommit,
        RuntimeUnlockRouteCommitSnapshot Snapshot)
        SeedUncommittedTunaPreCommitAuthoritiesForTest(
        SessionFileTransferService service,
        object context,
        LoopbackFileTransferTransport transport,
        string reason)
    {
        var admission = SeedRuntimeUnlockAdmissionForTest(
            context,
            FileTransferDirection.Outbound,
            reason);
        var transferId = GetPrivateProperty<string>(context, "TransferId");
        var sessionId = GetPrivateProperty<string>(context, "SessionId");
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            reason,
            transactionGeneration: 7,
            offerGeneration: 11,
            leaseGeneration: 13,
            launchGeneration: 17);
        var snapshot = Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(
            transport.RuntimeUnlockRouteCommitSnapshot);
        var owner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var pendingSnapshot = CreatePreProbeRouteCommitSnapshotForTest(snapshot);
        var recorded = Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod(
                "TryRecordRuntimeUnlockPendingPreCommitProbeLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    FileTransferDirection.Outbound,
                    sessionId,
                    transferId,
                    owner,
                    admission,
                    pendingSnapshot,
                    reason,
                ]));
        Assert.True(recorded);
        var pending = Assert.IsType<RuntimeUnlockPendingPreCommitProbeRequest>(
            GetPrivatePropertyValue(context, "RuntimeUnlockPendingPreCommitProbe"));
        Assert.Equal(admission, pending.AdmissionToken);
        Assert.Equal(snapshot.TransactionGeneration, pending.TransactionGeneration);
        Assert.Equal(snapshot.OfferGeneration, pending.OfferGeneration);
        Assert.Equal(snapshot.TunaPathLeaseGeneration, pending.TunaPathLeaseGeneration);
        Assert.Equal(snapshot.TunaPathLeaseListenerRunId, pending.TunaPathLeaseListenerRunId);
        Assert.Equal("Acked", snapshot.PathProbeState, ignoreCase: true);
        Assert.Equal(
            admission,
            GetPrivateProperty<RuntimeUnlockAdmissionToken>(context, "RuntimeUnlockAdmissionToken"));
        Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockRouteCommitToken"));
        return (admission, pending, snapshot);
    }

    private static (bool Accepted, object? Drain) StageRegularV4RouteCommitSendDrain(
        SessionFileTransferService service,
        object context,
        string reason,
        bool seedAcceptedPeerState = true)
    {
        if (seedAcceptedPeerState &&
            GetPrivateProperty<int>(context, "V4LastStateEpoch") < 0)
        {
            SetPrivateProperty(context, "V4LastStateEpoch", 0);
        }

        EnsureRuntimeUnlockPreCommitAuthoritiesForDrainForTest(service, context, reason);
        var transport = Assert.IsType<LoopbackFileTransferTransport>(
            typeof(SessionFileTransferService)
                .GetField(
                    "transport",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service));
        var snapshot = Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(
            transport.RuntimeUnlockRouteCommitSnapshot);
        object?[] args = [context, snapshot, reason, null];
        bool accepted;
        var serviceGate = typeof(SessionFileTransferService)
            .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        lock (serviceGate)
        {
            accepted = Assert.IsType<bool>(typeof(SessionFileTransferService)
                .GetMethod(
                    "TryPrepareOutboundRegularV4ToTunaRouteTransitionLocked",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, args));
            if (accepted)
            {
                StabilizeFileTransferRouteTransitionDrainForTest(context);
            }
        }

        return (
            accepted,
            accepted
                ? GetCurrentFileTransferRouteTransitionDrain(context) ??
                  GetFileTransferRouteTransitionSubstate(context)
                      .LastTerminalOperation?.DrainCommand
                : null);
    }

    private static (bool Accepted, object? Drain)
        TryPrepareRegularV4RouteTransitionWithoutAuthoritySeeding(
            SessionFileTransferService service,
            object context,
            LoopbackFileTransferTransport transport,
            string reason)
    {
        var snapshot = Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(
            transport.RuntimeUnlockRouteCommitSnapshot);
        object?[] args = [context, snapshot, reason, null];
        var accepted = Assert.IsType<bool>(
            typeof(SessionFileTransferService)
                .GetMethod(
                    "TryPrepareOutboundRegularV4ToTunaRouteTransitionLocked",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, args));
        return (
            accepted,
            accepted
                ? GetCurrentFileTransferRouteTransitionDrain(context)
                : null);
    }

    private static FileTransferRouteTransitionSubstate
        GetFileTransferRouteTransitionSubstate(object context)
        => GetPrivateProperty<FileTransferCoordinatorPersistentState>(
                context,
                "CoordinatorPersistentState")
            .FileTransferRouteTransition;

    private static FileTransferRouteTransitionPhysicalDrainCommand?
        GetCurrentFileTransferRouteTransitionDrain(object context)
        => GetFileTransferRouteTransitionSubstate(context)
            .CurrentOperation?.DrainCommand;

    private static object? GetCurrentFileTransferRouteTransitionExecution(
        object context)
        => GetPrivatePropertyValue(
            context,
            "FileTransferRouteTransitionExecution");

    private static void StabilizeFileTransferRouteTransitionDrainForTest(
        object context)
    {
        var execution = GetCurrentFileTransferRouteTransitionExecution(context);
        if (execution is null ||
            !GetPrivateProperty<bool>(execution, "DrainRecheckQueued"))
        {
            return;
        }

        var executionType = execution.GetType();
        var constructor = executionType
            .GetConstructors(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Any(parameter =>
                string.Equals(
                    parameter.Name,
                    "DrainRecheckQueued",
                    StringComparison.OrdinalIgnoreCase)));
        var arguments = constructor.GetParameters()
            .Select(parameter =>
            {
                var property = executionType.GetProperty(
                    parameter.Name!,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.IgnoreCase)!;
                return string.Equals(
                    property.Name,
                    "DrainRecheckQueued",
                    StringComparison.Ordinal)
                    ? false
                    : property.GetValue(execution);
            })
            .ToArray();
        SetPrivateProperty(
            context,
            "FileTransferRouteTransitionExecution",
            constructor.Invoke(arguments));
    }

    private static FileTransferRouteTransitionOperation
        AssertFileTransferRouteTransitionOperation(
            object context,
            FileTransferRouteTransitionState expectedState)
    {
        var current =
            GetFileTransferRouteTransitionSubstate(context).CurrentOperation;
        Assert.True(current.HasValue);
        Assert.Equal(expectedState, current.Value.State);
        return current.Value;
    }

    private static void EnsureRuntimeUnlockPreCommitAuthoritiesForDrainForTest(
        SessionFileTransferService service,
        object context,
        string reason)
    {
        if (GetPrivatePropertyValue(context, "RuntimeUnlockAdmissionToken") is
                RuntimeUnlockAdmissionToken { IsDefault: false } &&
            GetPrivatePropertyValue(context, "RuntimeUnlockPendingPreCommitProbe") is
                RuntimeUnlockPendingPreCommitProbeRequest { IsDefault: false })
        {
            return;
        }

        var transport = Assert.IsType<LoopbackFileTransferTransport>(
            typeof(SessionFileTransferService)
                .GetField("transport", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service));
        var snapshot = Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(
            transport.RuntimeUnlockRouteCommitSnapshot);
        var admission = GetPrivatePropertyValue(context, "RuntimeUnlockAdmissionToken") is
                RuntimeUnlockAdmissionToken { IsDefault: false } currentAdmission
            ? currentAdmission
            : RequestRuntimeUnlockAdmissionForDrainForTest(context, reason);
        var owner = Assert.IsType<RuntimeOwnerToken>(
            GetPrivatePropertyValue(context, "CurrentRuntimeOwner"));
        var pendingSnapshot = CreatePreProbeRouteCommitSnapshotForTest(snapshot);
        var recorded = Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod(
                "TryRecordRuntimeUnlockPendingPreCommitProbeLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    FileTransferDirection.Outbound,
                    GetPrivateProperty<string>(context, "SessionId"),
                    GetPrivateProperty<string>(context, "TransferId"),
                    owner,
                    admission,
                    pendingSnapshot,
                    reason,
                ]));
        Assert.True(recorded);
        Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockRouteCommitToken"));
    }

    private static RuntimeUnlockAdmissionToken RequestRuntimeUnlockAdmissionForDrainForTest(
        object context,
        string reason)
    {
        var method = typeof(SessionFileTransferService)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(candidate =>
            {
                if (!string.Equals(
                        candidate.Name,
                        "RequestRuntimeUnlockAdmissionLocked",
                        StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length == 4 && parameters[0].ParameterType == context.GetType();
            });
        var decision = Assert.IsType<RuntimeUnlockAdmissionDecision>(
            method.Invoke(null, [context, "precommit_probe_start", reason, false]));
        Assert.True(decision.Granted, decision.Reason);
        return Assert.IsType<RuntimeUnlockAdmissionToken>(decision.AdmissionToken);
    }

    private static RuntimeUnlockRouteCommitSnapshot CreatePreProbeRouteCommitSnapshotForTest(
        RuntimeUnlockRouteCommitSnapshot snapshot)
        => snapshot with
        {
            TransactionState = RuntimeUnlockTransactionState.AnswerReceived.ToString(),
            PathProbeId = null,
            PathProbeState = RuntimeUnlockPathProbeState.None.ToString(),
            PathProbeTransport = FileTransferTransportKind.Unknown,
            PathProbeAckedUtcMs = 0,
            PathProbeFailureReason = null,
        };

    private static (bool Accepted, string Reason) CompleteRegularV4RouteCommitSendDrain(
        SessionFileTransferService service,
        object context,
        object drain,
        string reason)
    {
        var expectedDrain =
            Assert.IsType<FileTransferRouteTransitionPhysicalDrainCommand>(drain);
        var execution = GetCurrentFileTransferRouteTransitionExecution(context);
        if (execution is null)
        {
            var terminal =
                GetFileTransferRouteTransitionSubstate(context)
                    .LastTerminalOperation;
            return terminal is
                {
                    State: FileTransferRouteTransitionState.Committed,
                    DrainCommand: { } committedDrain,
                } &&
                committedDrain == expectedDrain
                    ? (true, "filetransfer_route_transition_already_committed")
                    : (false, "filetransfer_route_transition_not_current");
        }

        Assert.Equal(
            expectedDrain,
            Assert.IsType<FileTransferRouteTransitionPhysicalDrainCommand>(
                GetPrivatePropertyValue(execution, "DrainCommand")));
        var physicalTasksComplete = Assert.IsType<bool>(
            typeof(SessionFileTransferService)
                .GetMethod(
                    "AreOutboundFileTransferRouteTransitionPhysicalTasksCompleteLocked",
                    BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [context, execution]));
        if (!physicalTasksComplete)
        {
            return (
                false,
                "regular_v4_physical_sends_in_flight");
        }

        object?[] args = [context, execution, null, null];
        var method = typeof(SessionFileTransferService)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate =>
            {
                if (!string.Equals(
                        candidate.Name,
                        "TryCommitOutboundFileTransferRouteTransitionLocked",
                        StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length == 4 &&
                       parameters[0].ParameterType == context.GetType();
            });
        var accepted = Assert.IsType<bool>(method.Invoke(service, args));
        return (
            accepted,
            args[3] as string ??
            (accepted ? "none" : "filetransfer_route_transition_rejected"));
    }

    private static (bool Accepted, string Reason) IsRegularV4RouteCommitSendDrainCurrent(
        SessionFileTransferService service,
        object context,
        object drain)
    {
        var expectedDrain =
            Assert.IsType<FileTransferRouteTransitionPhysicalDrainCommand>(drain);
        var execution = GetCurrentFileTransferRouteTransitionExecution(context);
        if (execution is null)
        {
            return (false, "filetransfer_route_transition_not_current");
        }

        Assert.Equal(
            expectedDrain,
            Assert.IsType<FileTransferRouteTransitionPhysicalDrainCommand>(
                GetPrivatePropertyValue(execution, "DrainCommand")));
        object?[] args = [context, execution, null];
        var method = typeof(SessionFileTransferService)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(candidate =>
            {
                if (!string.Equals(
                        candidate.Name,
                        "IsOutboundFileTransferRouteTransitionExecutionCurrentLocked",
                        StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length == 3 &&
                       parameters[0].ParameterType == context.GetType();
            });
        var accepted = Assert.IsType<bool>(method.Invoke(null, args));
        return (accepted, Assert.IsType<string>(args[2]));
    }

    private static (bool Accepted, object? Lease, string? Reason) RegisterRegularV4PhysicalSendLease(
        SessionFileTransferService service,
        object context,
        IFileTransferDataSession dataSession,
        FileTransferChunkBatchFrameV4 frame,
        bool repairSend = false,
        string repairRequestKey = "")
    {
        object?[] args = [
            context,
            dataSession,
            frame.StartChunkIndex,
            frame.ChunkCount,
            repairSend,
            repairRequestKey,
            null,
            null,
        ];
        var accepted = Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod(
                "TryRegisterOutboundPhysicalSendLeaseLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, args));
        return (accepted, args[6], args[7] as string);
    }

    private static Task ObserveRegularV4PhysicalSendLease(
        SessionFileTransferService service,
        object context,
        object lease,
        Task sendTask)
        => Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
            .GetMethod(
                "ObserveOutboundPhysicalSendLeaseAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, lease, sendTask]));

    private static Task ObserveRegularV4PhysicalSendFailure(
        SessionFileTransferService service,
        object context,
        object lease,
        Task deliveryTask)
        => Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService)
            .GetMethod(
                "ObserveOutboundPhysicalSendFailureAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, lease, deliveryTask]));

    private static Task RecheckRegularV4RouteCommitSendDrain(
        SessionFileTransferService service,
        object context,
        object drain)
    {
        var expectedDrain =
            Assert.IsType<FileTransferRouteTransitionPhysicalDrainCommand>(drain);
        var execution = GetCurrentFileTransferRouteTransitionExecution(context);
        if (execution is null)
        {
            return Task.CompletedTask;
        }

        Assert.Equal(
            expectedDrain,
            Assert.IsType<FileTransferRouteTransitionPhysicalDrainCommand>(
                GetPrivatePropertyValue(execution, "DrainCommand")));
        return Assert.IsAssignableFrom<Task>(
            typeof(SessionFileTransferService)
                .GetMethod(
                    "RecheckOutboundFileTransferRouteTransitionDrainAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, execution]));
    }

    private static void ReportRuntimeUnlockPreCommitProbeFailure(
        SessionFileTransferService service,
        FileTransferDirection direction,
        string transferId,
        string sessionId,
        RuntimeUnlockRouteCommitSnapshot snapshot,
        string probeId,
        string reason)
        => typeof(SessionFileTransferService)
            .GetMethod(
                "LogRuntimeUnlockPreCommitProbeFailed",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    direction,
                    transferId,
                    sessionId,
                    snapshot.TransactionGeneration,
                    snapshot.OfferGeneration,
                    snapshot.TunaPathLeaseGeneration,
                    probeId,
                    reason,
                ]);

    private static void InvalidateRegularV4RouteCommitSendDrainTuple(
        object context,
        LoopbackFileTransferTransport transport,
        string invalidation)
    {
        switch (invalidation)
        {
            case "terminal":
                SetPrivateProperty(
                    context,
                    "TerminalGeneration",
                    GetPrivateProperty<int>(context, "TerminalGeneration") + 1);
                SetPrivateProperty(context, "State", FileTransferTransferState.Failed);
                break;

            case "runtime_owner":
            {
                var owner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
                SetPrivateProperty(
                    context,
                    "CurrentRuntimeOwner",
                    owner with
                    {
                        RuntimeGeneration = owner.RuntimeGeneration + 1,
                        CreatedReason = "unit_test_drain_owner_replaced",
                    });
                break;
            }

            case "route_publication":
            {
                var publication = GetPrivateProperty<RoutePublicationToken>(
                    context,
                    "CurrentRoutePublicationToken");
                SetPrivateProperty(
                    context,
                    "CurrentRoutePublicationToken",
                    publication with
                    {
                        PublicationGeneration = publication.PublicationGeneration + 1,
                        Reason = "unit_test_drain_publication_replaced",
                    });
                break;
            }

            case "leg":
            {
                var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
                SetPrivateProperty(leg, "Generation", leg.Generation + 1);
                break;
            }

            case "transport":
            {
                var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
                SetPrivateProperty(leg, "TransportEpochId", leg.TransportEpochId + 1);
                break;
            }

            case "bridge":
            {
                var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
                SetPrivateProperty(
                    leg,
                    "BridgeRecoveryGeneration",
                    leg.BridgeRecoveryGeneration + 1);
                break;
            }

            case "data_session":
                SetPrivateProperty(
                    context,
                    "DataSession",
                    new RecordingDataSession(
                        GetPrivateProperty<string>(context, "SessionId"),
                        GetPrivateProperty<string>(context, "TransferId")));
                break;

            case "transaction":
                transport.RuntimeUnlockRouteCommitSnapshot = transport.RuntimeUnlockRouteCommitSnapshot! with
                {
                    TransactionGeneration =
                        transport.RuntimeUnlockRouteCommitSnapshot!.TransactionGeneration + 1,
                };
                break;

            case "offer":
                transport.RuntimeUnlockRouteCommitSnapshot = transport.RuntimeUnlockRouteCommitSnapshot! with
                {
                    OfferGeneration = transport.RuntimeUnlockRouteCommitSnapshot!.OfferGeneration + 1,
                };
                break;

            case "path_lease":
                transport.RuntimeUnlockRouteCommitSnapshot = transport.RuntimeUnlockRouteCommitSnapshot! with
                {
                    TunaPathLeaseGeneration =
                        transport.RuntimeUnlockRouteCommitSnapshot!.TunaPathLeaseGeneration + 1,
                };
                break;

            case "path_lease_listener":
                transport.RuntimeUnlockRouteCommitSnapshot = transport.RuntimeUnlockRouteCommitSnapshot! with
                {
                    TunaPathLeaseListenerRunId = "unit_test_replaced_listener",
                };
                break;

            case "probe_id":
                transport.RuntimeUnlockRouteCommitSnapshot = transport.RuntimeUnlockRouteCommitSnapshot! with
                {
                    PathProbeId = "unit_test_replaced_probe",
                };
                break;

            case "probe_failure":
                transport.RuntimeUnlockRouteCommitSnapshot = transport.RuntimeUnlockRouteCommitSnapshot! with
                {
                    PathProbeState = RuntimeUnlockPathProbeState.Failed.ToString(),
                };
                break;

            case "target_route":
                transport.RuntimeUnlockRouteCommitSnapshot = transport.RuntimeUnlockRouteCommitSnapshot! with
                {
                    TargetRoute = FileTransferRoute.PostTunaFallbackV6,
                };
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(invalidation),
                    invalidation,
                    "Unknown route-commit drain invalidation.");
        }
    }

    private static object CreateSparsePumpInstall(
        object context,
        long installGeneration,
        long predecessorInstallGeneration)
    {
        var installType = typeof(SessionFileTransferService)
            .GetNestedType("OutboundSparsePumpInstall", BindingFlags.NonPublic)!;
        var runtimeOwner = Assert.IsType<RuntimeOwnerToken>(
            GetPrivatePropertyValue(context, "CurrentRuntimeOwner"));
        var publication = Assert.IsType<RoutePublicationToken>(
            GetPrivatePropertyValue(context, "CurrentRoutePublicationToken"));
        var leg = Assert.IsType<FileTransferLeg>(GetPrivatePropertyValue(context, "CurrentTransferLeg"));

        return Activator.CreateInstance(
            installType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                installGeneration,
                predecessorInstallGeneration,
                runtimeOwner,
                publication,
                GetPrivateProperty<int>(context, "TerminalGeneration"),
                leg.Generation,
                GetPrivateProperty<long>(context, "DataSessionGeneration"),
                "unit_test_sparse_worker_handoff",
            ],
            culture: null)!;
    }

    private static (bool Accepted, object? Install, string Reason) StageSparsePumpInstall(
        SessionFileTransferService service,
        object context,
        object? predecessor,
        string reason)
    {
        object?[] args = [context, predecessor, reason, null, null];
        var accepted = Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod("TryStageOutboundSparsePumpInstallLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, args));
        return (accepted, args[3], Assert.IsType<string>(args[4]));
    }

    private static (bool Accepted, object? Install, string Reason) CommitSparsePumpInstall(
        SessionFileTransferService service,
        object context,
        object pending,
        IFileTransferDataSession dataSession)
    {
        object?[] args = [context, pending, dataSession, null, null];
        var accepted = Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod("TryCommitOutboundSparsePumpInstallLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, args));
        return (accepted, args[3], Assert.IsType<string>(args[4]));
    }

    private static void InvalidateSparsePumpInstallTuple(SparseWorkerHarness harness, string invalidation)
    {
        switch (invalidation)
        {
            case "terminal":
                SetPrivateProperty(harness.Context, "TerminalGeneration", 1);
                SetPrivateProperty(harness.Context, "State", FileTransferTransferState.Failed);
                break;

            case "runtime_owner":
            {
                var owner = Assert.IsType<RuntimeOwnerToken>(
                    GetPrivatePropertyValue(harness.Context, "CurrentRuntimeOwner"));
                SetPrivateProperty(
                    harness.Context,
                    "CurrentRuntimeOwner",
                    owner with
                    {
                        RuntimeGeneration = owner.RuntimeGeneration + 1,
                        CreatedReason = "unit_test_owner_replaced",
                    });
                break;
            }

            case "route_publication":
            {
                var publication = Assert.IsType<RoutePublicationToken>(
                    GetPrivatePropertyValue(harness.Context, "CurrentRoutePublicationToken"));
                SetPrivateProperty(
                    harness.Context,
                    "CurrentRoutePublicationToken",
                    publication with
                    {
                        PublicationGeneration = publication.PublicationGeneration + 1,
                        Reason = "unit_test_publication_replaced",
                    });
                break;
            }

            case "leg":
            {
                var leg = Assert.IsType<FileTransferLeg>(
                    GetPrivatePropertyValue(harness.Context, "CurrentTransferLeg"));
                SetPrivateProperty(leg, "Generation", leg.Generation + 1);
                break;
            }

            case "data_session":
                SetPrivateProperty(
                    harness.Context,
                    "DataSession",
                    new RecordingDataSession(
                        GetPrivateProperty<string>(harness.Context, "SessionId"),
                        GetPrivateProperty<string>(harness.Context, "TransferId")));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(invalidation), invalidation, "Unknown install invalidation.");
        }
    }

    private static void ReplaceOutboundDataSession(
        SessionFileTransferService service,
        object context,
        IFileTransferDataSession dataSession)
        => typeof(SessionFileTransferService)
            .GetMethod("ReplaceOutboundDataSessionLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, dataSession]);

    private sealed class SparseWorkerHarness : IDisposable
    {
        public SparseWorkerHarness(
            string transferId,
            FileTransferReadStreamFactory? sourceFactory = null)
        {
            Service = new SessionFileTransferService();
            Context = sourceFactory is null
                ? CreatePostTunaFallbackV6OutboundContext(transferId)
                : CreateSparsePostTunaContextWithSourceFactory(
                    transferId,
                    sourceFactory,
                    remoteNextExpectedChunkIndex: 100,
                    chunksAcceptedForTransport: 256,
                    remoteGrantedUntilExclusive: 256,
                    chunkCount: 256);
            typeof(SessionFileTransferService)
                .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Service, Context);
            DataSession = new RecordingDataSession($"session_{transferId}", transferId);
            ReplaceOutboundDataSession(Service, Context, DataSession);
        }

        public SessionFileTransferService Service { get; }

        public object Context { get; }

        public RecordingDataSession DataSession { get; }

        public void Dispose() => Service.Dispose();
    }

    private sealed class BlockingPhysicalSendDataSession(
        string sessionId,
        string transferId,
        Func<FileTransferRouteTransitionState?> transitionStateProvider) :
        IFileTransferDataSession,
        IFileTransferPhysicalSendSubmissionSession
    {
        private int submissionCount;

        public string SessionId { get; } = sessionId;

        public string TransferId { get; } = transferId;

        public bool IsAvailable => true;

        public int SubmissionCount => Volatile.Read(ref submissionCount);

        public FileTransferRouteTransitionState? TransitionStateAtSubmission
        {
            get;
            private set;
        }

        public TaskCompletionSource SubmissionEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSubmission { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PhysicalCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>?
            AvailabilityChanged
        {
            add { }
            remove { }
        }

        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
            => Task.CompletedTask;

        public FileTransferPhysicalSendSubmission SubmitPhysicalSend(
            FileTransferDataFrame frame,
            FileTransferDataPathSendAuthority? authority,
            CancellationToken ct)
        {
            Interlocked.Increment(ref submissionCount);
            TransitionStateAtSubmission = transitionStateProvider();
            SubmissionEntered.TrySetResult();
            ReleaseSubmission.Task.GetAwaiter().GetResult();
            return new FileTransferPhysicalSendSubmission(
                Task.CompletedTask,
                PhysicalCompletion.Task);
        }

        public void Dispose()
        {
            ReleaseSubmission.TrySetResult();
            PhysicalCompletion.TrySetResult();
        }
    }
}
