using System.Collections.Concurrent;
using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
[Collection(FakeNknNetworkCollection.Name)]
public sealed class SessionFileTransferRuntimeUnlockRetryAuthorityTests :
    SessionFileTransferServiceTestBase
{
    [Fact]
    public void AttachAndDetach_SubscribeAndUnsubscribeRetryAuthorityBridgeEvents()
    {
        using var bridge = new RetryAuthorityLoopbackTransport("session_retry_subscriptions");
        using var service = new SessionFileTransferService();

        service.AttachTransport(bridge);

        Assert.Equal(1, bridge.AuthorityRequestSubscriberCount);
        Assert.Equal(1, bridge.SettlementReportSubscriberCount);
        Assert.Equal(1, bridge.RegularV4SettlementReportSubscriberCount);
        Assert.Equal(1, bridge.RegularV4LifecycleReportSubscriberCount);

        service.DetachTransport();

        Assert.Equal(0, bridge.AuthorityRequestSubscriberCount);
        Assert.Equal(0, bridge.SettlementReportSubscriberCount);
        Assert.Equal(0, bridge.RegularV4SettlementReportSubscriberCount);
        Assert.Equal(0, bridge.RegularV4LifecycleReportSubscriberCount);
    }

    [Fact]
    public async Task SettlementReport_IsProcessedUnderCoreGate_ReturnedAfterGate_AndDuplicateIsIdempotent()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_settlement",
            "transfer_retry_settlement");
        var contract = Grant(fixture, fixture.InitialRequest);
        var report = SettleMissingDispatch(contract, offerGeneration: 101);
        var coreGate = GetCoreGate(fixture.Service);
        fixture.Bridge.CoreGateForSettlementDecisionProbe = coreGate;

        Task delivery;
        Monitor.Enter(coreGate);
        try
        {
            delivery = Task.Run(() => fixture.Bridge.RaiseSettlementReport(report));
            Assert.False(
                fixture.Bridge.SettlementDecisionObserved.Wait(TimeSpan.FromMilliseconds(100)),
                "Settlement escaped the SessionFileTransferService transfer lock.");
        }
        finally
        {
            Monitor.Exit(coreGate);
        }

        await delivery.WaitAsync(TimeSpan.FromSeconds(3));
        var accepted = fixture.Bridge.DequeueSettlementDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted, accepted.Status);
        Assert.Equal(report, GetLastSettlement(fixture.Context));
        Assert.Same(contract, GetCurrentContract(fixture.Context));
        Assert.True(fixture.Bridge.LastSettlementDecisionObservedWithCoreGateAvailable);

        fixture.Bridge.RaiseSettlementReport(report);

        var duplicate = fixture.Bridge.DequeueSettlementDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent, duplicate.Status);
        Assert.Equal("retry_dispatch_settlement_duplicate", duplicate.Reason);
        Assert.Same(contract, GetCurrentContract(fixture.Context));
        Assert.Equal(report, GetLastSettlement(fixture.Context));
    }

    [Fact]
    public async Task RegularV4RecoverySettlementReport_IsProcessedUnderCoreGate_AndAuthorizesExactRequest()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_regular_v4_handoff",
            "transfer_retry_regular_v4_handoff");
        var report = SeedRegularV4RecoverySettlement(fixture.Context);
        var coreGate = GetCoreGate(fixture.Service);

        Task delivery;
        Monitor.Enter(coreGate);
        try
        {
            delivery = Task.Run(() => fixture.Bridge.RaiseRegularV4SettlementReport(report));
            Assert.False(
                delivery.Wait(TimeSpan.FromMilliseconds(100)),
                "Regular-V4 recovery settlement escaped the Core transfer lock.");
        }
        finally
        {
            Monitor.Exit(coreGate);
        }

        await delivery.WaitAsync(TimeSpan.FromSeconds(3));
        var accepted = fixture.Bridge.DequeueRegularV4SettlementDecision();
        Assert.Equal(
            RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Accepted,
            accepted.Status);
        Assert.Equal(report, GetLastRegularV4Settlement(fixture.Context));

        fixture.Bridge.RaiseRegularV4SettlementReport(report);
        var duplicate = fixture.Bridge.DequeueRegularV4SettlementDecision();
        Assert.Equal(
            RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Idempotent,
            duplicate.Status);

        var settlementRequest = fixture.InitialRequest with
        {
            RegularV4LivenessGeneration = report.RegularV4LivenessGeneration,
            BridgeDecisionGeneration = report.BridgeDecisionGeneration,
        };
        var contract = Grant(fixture, settlementRequest);
        Assert.Equal(
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement,
            contract.Authority.ReleaseKind);
        Assert.Equal(report.Settlement.AuthorityId, contract.Authority.ReleaseAuthorityId);
        Assert.Equal(report.Settlement.DecisionGeneration, contract.Authority.ReleaseGeneration);
    }

    [Fact]
    public async Task RegularV4RecoveryLifecycleReport_IsProcessedUnderCoreGate_AndRetiresLeaseOnce()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_regular_v4_lifecycle",
            "transfer_retry_regular_v4_lifecycle");
        var report = SeedRegularV4RecoveryLifecycle(fixture.Context);
        var coreGate = GetCoreGate(fixture.Service);

        Task delivery;
        Monitor.Enter(coreGate);
        try
        {
            delivery = Task.Run(() => fixture.Bridge.RaiseRegularV4LifecycleReport(report));
            Assert.False(
                delivery.Wait(TimeSpan.FromMilliseconds(100)),
                "Regular-V4 recovery lifecycle escaped the Core transfer lock.");
        }
        finally
        {
            Monitor.Exit(coreGate);
        }

        await delivery.WaitAsync(TimeSpan.FromSeconds(3));
        var accepted = fixture.Bridge.DequeueRegularV4LifecycleDecision();
        Assert.Equal(RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Accepted, accepted.Status);
        Assert.Null(GetOptionalStructProperty<RecoveryLivenessLease>(fixture.Context, "CurrentRecoveryLivenessLease"));
        Assert.NotNull(GetOptionalStructProperty<RecoveryLivenessLease>(fixture.Context, "LastRetiredRecoveryLivenessLease"));
        Assert.Equal(
            report,
            GetOptionalStructProperty<RuntimeUnlockRegularV4RecoveryLifecycleReport>(
                fixture.Context,
                "LastRuntimeUnlockRegularV4RecoveryLifecycle"));
        Assert.True(GetRequiredProperty<bool>(
            fixture.Context,
            "RuntimeUnlockRecoveryLeaseClosureAvailable"));

        fixture.Bridge.RaiseRegularV4LifecycleReport(report);
        var duplicate = fixture.Bridge.DequeueRegularV4LifecycleDecision();
        Assert.Equal(RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Idempotent, duplicate.Status);
        Assert.True(GetRequiredProperty<bool>(
            fixture.Context,
            "RuntimeUnlockRecoveryLeaseClosureAvailable"));
    }

    [Fact]
    public async Task RecoveryLeaseClosureContinuation_DefersOnlyWithinExactRetainedProofWindow()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_closure_liveness",
            "transfer_retry_closure_liveness");
        var lifecycle = Assert.IsType<RuntimeUnlockRegularV4RecoveryLifecycleReport>(
            GetOptionalStructProperty<RuntimeUnlockRegularV4RecoveryLifecycleReport>(
                fixture.Context,
                "LastRuntimeUnlockRegularV4RecoveryLifecycle"));
        var retiredLease = Assert.IsType<RecoveryLivenessLease>(
            GetOptionalStructProperty<RecoveryLivenessLease>(
                fixture.Context,
                "LastRetiredRecoveryLivenessLease"));
        var leg = GetRequiredProperty<FileTransferLeg>(fixture.Context, "CurrentTransferLeg");
        var route = GetRequiredProperty<FileTransferRouteSelection>(fixture.Context, "RouteSelection");
        var maximumDeadline = DateTimeOffset.UtcNow.AddSeconds(90);
        var proof = new RuntimeUnlockRegularV4RecoveryLeaseClosureContinuationProof(
            lifecycle,
            retiredLease,
            RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Accepted,
            "regular_v4_recovery_lifecycle_accepted",
            maximumDeadline);
        var snapshot = new FileTransferRecoveryLivenessSnapshot(
            fixture.InitialRequest.SessionId,
            fixture.TransferId,
            route.TelemetryToken,
            route.ProtocolVersion,
            leg.LiveRouteEpochId,
            leg.Generation,
            leg.BridgeRecoveryGeneration,
            leg.TransportEpochId,
            leg.CheckpointRequestId,
            "bridge_lifecycle_completed",
            FileTransferRecoveryLivenessState.BridgeRecoveryCompletedAwaitingProof,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            maximumDeadline,
            BridgeRecoveryRequested: true,
            BridgeRecoveryStarted: true,
            BridgeRecoveryCompleted: true,
            ReceiveProofObserved: false,
            RecoveryExhausted: false,
            AuthorityCompleted: false,
            TerminalRecommended: false)
        {
            RegularV4RecoveryLeaseClosureContinuationProof = proof,
        };

        Assert.True(fixture.Service.TryAuthorizeRecoveryLivenessDeferral(
            snapshot,
            "session_liveness_recovery_state",
            maximumDeadline,
            out var grant));
        Assert.NotNull(grant);
        Assert.Equal(retiredLease.LeaseGeneration, grant!.LeaseGeneration);
        Assert.Equal(RecoveryLivenessLeaseState.Retired.ToString(), grant.LeaseState);
        Assert.Null(GetOptionalStructProperty<RecoveryLivenessLease>(
            fixture.Context,
            "CurrentRecoveryLivenessLease"));
        Assert.Equal(retiredLease, GetOptionalStructProperty<RecoveryLivenessLease>(
            fixture.Context,
            "LastRetiredRecoveryLivenessLease"));
        Assert.True(GetRequiredProperty<bool>(
            fixture.Context,
            "RuntimeUnlockRecoveryLeaseClosureAvailable"));

        Assert.True(fixture.Service.TryAuthorizeRecoveryLivenessDeferral(
            snapshot,
            "session_liveness_recovery_state",
            maximumDeadline,
            out var duplicateGrant));
        Assert.Equal(grant, duplicateGrant);

        Assert.False(fixture.Service.TryAuthorizeRecoveryLivenessDeferral(
            snapshot,
            "session_liveness_recovery_state",
            maximumDeadline.AddMinutes(7),
            out var excessiveGrant));
        Assert.Null(excessiveGrant);
        Assert.Null(GetOptionalStructProperty<RecoveryLivenessLease>(
            fixture.Context,
            "CurrentRecoveryLivenessLease"));

        Assert.False(fixture.Service.TryAuthorizeRecoveryLivenessDeferral(
            snapshot with { RegularV4RecoveryLeaseClosureContinuationProof = null },
            "session_liveness_recovery_state",
            maximumDeadline,
            out var syntheticGrant));
        Assert.Null(syntheticGrant);

        Assert.False(fixture.Service.TryAuthorizeRecoveryLivenessDeferral(
            snapshot with
            {
                State = FileTransferRecoveryLivenessState.BridgeRecoveryStarted,
            },
            "session_liveness_recovery_state",
            maximumDeadline,
            out var wrongStateGrant));
        Assert.Null(wrongStateGrant);
        Assert.True(GetRequiredProperty<bool>(
            fixture.Context,
            "RuntimeUnlockRecoveryLeaseClosureAvailable"));
    }

    [Fact]
    public async Task RecoveryLeaseClosureContinuation_SameTransferIdInBothDirectionsTargetsProofDirection()
    {
        const string sessionId = "session_retry_closure_direction_collision";
        const string transferId = "transfer_retry_closure_direction_collision";
        using var localTransport = new LoopbackFileTransferTransport(sessionId);
        using var peerTransport = new LoopbackFileTransferTransport(sessionId);
        localTransport.Connect(peerTransport);
        using var localService = new SessionFileTransferService();
        using var peerService = new SessionFileTransferService();
        localService.AttachTransport(localTransport);
        peerService.AttachTransport(peerTransport);

        var localOutbound = await localService.TryStartSendAsync(
            new FileTransferSendDescriptor("local-outbound.bin", 1, transferId, 4096),
            _ => Task.FromResult<Stream>(new MemoryStream([0x11], writable: false)),
            CancellationToken.None);
        var peerOutbound = await peerService.TryStartSendAsync(
            new FileTransferSendDescriptor("peer-outbound.bin", 1, transferId, 4096),
            _ => Task.FromResult<Stream>(new MemoryStream([0x22], writable: false)),
            CancellationToken.None);
        Assert.NotNull(localOutbound);
        Assert.NotNull(peerOutbound);
        await WaitUntilAsync(() =>
            localService.Snapshot.Inbound?.TransferId == transferId);

        var outboundContext = GetOutboundContext(localService);
        var inboundContext = GetInboundContext(localService);
        SeedRetiredRecoveryLivenessLease(
            inboundContext,
            FileTransferDirection.Inbound);
        var lifecycle = Assert.IsType<RuntimeUnlockRegularV4RecoveryLifecycleReport>(
            GetOptionalStructProperty<RuntimeUnlockRegularV4RecoveryLifecycleReport>(
                inboundContext,
                "LastRuntimeUnlockRegularV4RecoveryLifecycle"));
        var retiredLease = Assert.IsType<RecoveryLivenessLease>(
            GetOptionalStructProperty<RecoveryLivenessLease>(
                inboundContext,
                "LastRetiredRecoveryLivenessLease"));
        var leg = GetRequiredProperty<FileTransferLeg>(inboundContext, "CurrentTransferLeg");
        var route = GetRequiredProperty<FileTransferRouteSelection>(inboundContext, "RouteSelection");
        var maximumDeadline = DateTimeOffset.UtcNow.AddSeconds(90);
        var proof = new RuntimeUnlockRegularV4RecoveryLeaseClosureContinuationProof(
            lifecycle,
            retiredLease,
            RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Accepted,
            "regular_v4_recovery_lifecycle_accepted",
            maximumDeadline);
        var snapshot = new FileTransferRecoveryLivenessSnapshot(
            sessionId,
            transferId,
            route.TelemetryToken,
            route.ProtocolVersion,
            leg.LiveRouteEpochId,
            leg.Generation,
            leg.BridgeRecoveryGeneration,
            leg.TransportEpochId,
            leg.CheckpointRequestId,
            "bridge_lifecycle_completed",
            FileTransferRecoveryLivenessState.BridgeRecoveryCompletedAwaitingProof,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            maximumDeadline,
            BridgeRecoveryRequested: true,
            BridgeRecoveryStarted: true,
            BridgeRecoveryCompleted: true,
            ReceiveProofObserved: false,
            RecoveryExhausted: false,
            AuthorityCompleted: false,
            TerminalRecommended: false)
        {
            RegularV4RecoveryLeaseClosureContinuationProof = proof,
        };

        Assert.True(localService.TryAuthorizeRecoveryLivenessDeferral(
            snapshot,
            "session_liveness_recovery_state",
            maximumDeadline,
            out var grant));
        Assert.NotNull(grant);
        Assert.Equal(retiredLease.LeaseGeneration, grant!.LeaseGeneration);
        Assert.True(GetRequiredProperty<bool>(
            inboundContext,
            "RuntimeUnlockRecoveryLeaseClosureAvailable"));
        Assert.Null(GetOptionalStructProperty<RecoveryLivenessLease>(
            outboundContext,
            "LastRetiredRecoveryLivenessLease"));
    }

    [Fact]
    public async Task AuthorityRequest_FromStaleBridge_IsRejectedWithoutInstallingContract()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_stale_bridge",
            "transfer_retry_stale_bridge");
        using var staleBridge = new RetryAuthorityLoopbackTransport(
            "session_retry_stale_bridge");
        var handler = typeof(SessionFileTransferService).GetMethod(
            "OnRuntimeUnlockRetryDispatchAuthorityRequested",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handler);

        handler!.Invoke(
            fixture.Service,
            [
                staleBridge,
                new RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs(
                    fixture.InitialRequest),
            ]);

        var rejected = staleBridge.DequeueAuthorityDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, rejected.Status);
        Assert.Equal("retry_dispatch_transfer_context_missing", rejected.Reason);
        Assert.Null(GetCurrentContract(fixture.Context));
        Assert.Null(fixture.Bridge.InfraCurrentContract);
    }

    [Fact]
    public async Task LateSupersededPredecessorReport_IsDiagnosticOnlyAndCannotClearSuccessor()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_late_predecessor",
            "transfer_retry_late_predecessor");
        var predecessor = Grant(fixture, fixture.InitialRequest);
        var successorRequest = fixture.InitialRequest with
        {
            DispatchAttemptGeneration = 2,
            PredecessorAuthorityId = predecessor.Authority.AuthorityId,
            RequestActivePredecessorSupersession = true,
        };
        var successor = Grant(fixture, successorRequest);
        Assert.True(predecessor.TryCreateSettlementReport(out var predecessorReport));
        Assert.Equal(
            RuntimeUnlockRetryDispatchSettlementOutcome.Superseded,
            predecessorReport.Outcome);

        fixture.Bridge.RaiseSettlementReport(predecessorReport);

        var decision = fixture.Bridge.DequeueSettlementDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent, decision.Status);
        Assert.Same(successor, GetCurrentContract(fixture.Context));
        Assert.Equal(predecessorReport, GetLastSettlement(fixture.Context));
        Assert.Same(successor, fixture.Bridge.InfraCurrentContract);

        var mismatchedLateReport = predecessorReport with
        {
            Reason = "late_predecessor_mismatch",
        };
        fixture.Bridge.RaiseSettlementReport(mismatchedLateReport);

        var rejected = fixture.Bridge.DequeueSettlementDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected, rejected.Status);
        Assert.Same(successor, GetCurrentContract(fixture.Context));
        Assert.Equal(predecessorReport, GetLastSettlement(fixture.Context));
        Assert.Same(successor, fixture.Bridge.InfraCurrentContract);
    }

    [Fact]
    public async Task TerminalBeforeSettlementReport_ClearsOnceAndLateReportIsIdempotent()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_terminal",
            "transfer_retry_terminal");
        var contract = Grant(fixture, fixture.InitialRequest);

        var terminal = await fixture.Service.CancelTransferAsync(
            fixture.TransferId,
            "test_terminal_before_retry_settlement",
            CancellationToken.None);

        Assert.NotNull(terminal);
        Assert.Null(GetCurrentContract(fixture.Context));
        Assert.True(contract.TryCreateSettlementReport(out var terminalReport));
        Assert.Equal(
            RuntimeUnlockRetryDispatchSettlementOutcome.TransferTerminal,
            terminalReport.Outcome);
        Assert.Equal(terminalReport, GetLastSettlement(fixture.Context));

        fixture.Bridge.RaiseSettlementReport(terminalReport);

        var lateDecision = fixture.Bridge.DequeueSettlementDecision();
        Assert.Equal(
            RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent,
            lateDecision.Status);
        Assert.Null(GetCurrentContract(fixture.Context));
        Assert.Equal(terminalReport, GetLastSettlement(fixture.Context));
        Assert.Equal(1, fixture.Bridge.SettlementDecisionCount);
    }

    [Fact]
    public async Task BoundedMissingDispatchRejection_SynchronouslyClearsCoreAndBridgeState()
    {
        using var fixture = await CreateFixtureAsync(
            "session_retry_bounded",
            "transfer_retry_bounded");
        var predecessor = Grant(fixture, fixture.InitialRequest);
        var predecessorReport = SettleMissingDispatch(predecessor, offerGeneration: 201);
        fixture.Bridge.RaiseSettlementReport(predecessorReport);
        Assert.Equal(
            RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted,
            fixture.Bridge.DequeueSettlementDecision().Status);

        var successorRequest = fixture.InitialRequest with
        {
            RetiredOfferGeneration = predecessor.OfferGeneration,
            DispatchAttemptGeneration = 2,
            MissingDispatchSuccessorGeneration = 1,
            PredecessorAuthorityId = predecessor.Authority.AuthorityId,
        };
        var successor = Grant(fixture, successorRequest);
        var successorReport = SettleMissingDispatch(successor, offerGeneration: 202);
        fixture.Bridge.RaiseSettlementReport(successorReport);
        Assert.Equal(
            RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted,
            fixture.Bridge.DequeueSettlementDecision().Status);

        var exhaustedRequest = successorRequest with
        {
            RetiredOfferGeneration = successor.OfferGeneration,
            DispatchAttemptGeneration = 3,
            MissingDispatchSuccessorGeneration = 2,
            PredecessorAuthorityId = successor.Authority.AuthorityId,
        };
        fixture.Bridge.RaiseAuthorityRequest(exhaustedRequest);

        var exhausted = fixture.Bridge.DequeueAuthorityDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, exhausted.Status);
        Assert.Equal("retry_dispatch_missing_successor_limit_exhausted", exhausted.Reason);
        Assert.Null(exhausted.State.CurrentRuntimeUnlockRetryDispatchAuthority);
        Assert.Null(GetCurrentContract(fixture.Context));
        Assert.Null(fixture.Bridge.InfraCurrentContract);
        Assert.Equal(successorReport, GetLastSettlement(fixture.Context));
    }

    private static RuntimeUnlockRetryDispatchContract Grant(
        RetryFixture fixture,
        RuntimeUnlockRetryDispatchAuthorityRequest request)
    {
        fixture.Bridge.RaiseAuthorityRequest(request);
        var decision = fixture.Bridge.DequeueAuthorityDecision();
        Assert.Equal(RuntimeUnlockRetryDispatchDecisionStatus.Granted, decision.Status);
        Assert.Equal(request, decision.Request);
        var contract = Assert.IsType<RuntimeUnlockRetryDispatchContract>(decision.Contract);
        Assert.Same(contract, GetCurrentContract(fixture.Context));
        Assert.Same(contract, fixture.Bridge.InfraCurrentContract);
        return contract;
    }

    private static RuntimeUnlockRetryDispatchSettlementReport SettleMissingDispatch(
        RuntimeUnlockRetryDispatchContract contract,
        long offerGeneration)
    {
        Assert.True(contract.TryBindOfferAlias(offerGeneration, $"nonce-{offerGeneration}"));
        Assert.True(contract.TryBeginSend(out var attempt));
        Assert.True(contract.TryCompleteMissingDispatch(attempt, "retry_dispatch_not_observed"));
        Assert.True(contract.TryCreateSettlementReport(out var report));
        return report;
    }

    private static async Task<RetryFixture> CreateFixtureAsync(
        string sessionId,
        string transferId)
    {
        var bridge = new RetryAuthorityLoopbackTransport(sessionId);
        var peer = new LoopbackFileTransferTransport(sessionId);
        var service = new SessionFileTransferService();
        try
        {
            bridge.Connect(peer);
            service.AttachTransport(bridge);
            var started = await service.TryStartSendAsync(
                new FileTransferSendDescriptor("runtime-unlock-retry.bin", 1, transferId, 4096),
                _ => Task.FromResult<Stream>(new MemoryStream([0x2a], writable: false)),
                CancellationToken.None);
            Assert.NotNull(started);

            var context = GetOutboundContext(service);
            var recoveryClosure = SeedRetiredRecoveryLivenessLease(context);
            var publicationToken = GetRequiredProperty<RoutePublicationToken>(
                context,
                "CurrentRoutePublicationToken");
            var sourcePublication = FileTransferCoordinator.CreateRoutePublicationAuthority(
                publicationToken,
                "session_retry_authority_test");
            Assert.NotNull(sourcePublication);
            var request = new RuntimeUnlockRetryDispatchAuthorityRequest(
                sessionId,
                transferId,
                FileTransferDirection.Outbound,
                sourcePublication!,
                RecoveryContractGeneration: 41,
                RetiredOfferGeneration: 17,
                DispatchAttemptGeneration: 1,
                RetryReason: "runtime_unlock_offer_send_not_observed",
                RecoveryReason: "receive_recovery_lease_retired",
                RegularV4LivenessGeneration: recoveryClosure.LeaseGeneration,
                BridgeDecisionGeneration: recoveryClosure.BridgeDecisionGeneration);
            return new RetryFixture(service, bridge, peer, context, transferId, request);
        }
        catch
        {
            service.Dispose();
            bridge.Dispose();
            peer.Dispose();
            throw;
        }
    }

    private static (int LeaseGeneration, int BridgeDecisionGeneration)
        SeedRetiredRecoveryLivenessLease(
            object context,
            FileTransferDirection direction = FileTransferDirection.Outbound)
    {
        var sessionId = GetRequiredProperty<string>(context, "SessionId");
        var transferId = GetRequiredProperty<string>(context, "TransferId");
        var route = GetRequiredProperty<FileTransferRouteSelection>(context, "RouteSelection");
        var epoch = GetOptionalProperty<LiveRouteEpoch>(context, "CurrentLiveRouteEpoch");
        var leg = GetRequiredProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        var owner = GetRequiredProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var terminalGeneration = GetRequiredProperty<int>(context, "TerminalGeneration");
        const int leaseGeneration = 1;
        const int bridgeDecisionGeneration = 1;
        const string bridgeReason = "test_recovery_bridge";
        const string lifecycleReason = "bridge_lifecycle_completed";
        var activeLease = new RecoveryLivenessLease(
            sessionId,
            transferId,
            direction,
            owner,
            route.Route,
            route.TelemetryToken,
            route.ProtocolVersion,
            epoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            terminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            LeaseGeneration: leaseGeneration,
            new FileTransferAuthorityId(
                $"fta2-test-recovery-lease-{direction.ToString().ToLowerInvariant()}-{transferId}"),
            RecoveryLivenessLeaseState.Active,
            bridgeReason);
        var bridgeDecision = new BridgeRecoveryDecision(
            true,
            BridgeRecoveryKind.BridgeReceiveStallRecovery,
            bridgeReason,
            bridgeDecisionGeneration,
            route.Route,
            route.ProtocolVersion,
            epoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            terminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            owner);
        var bridgeAuthority = new FileTransferBridgeRecoveryAuthority(
            sessionId,
            transferId,
            direction,
            route.TelemetryToken,
            route.ProtocolVersion,
            epoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            terminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            bridgeDecisionGeneration,
            BridgeRecoveryKind.BridgeReceiveStallRecovery.ToString(),
            bridgeReason);
        var lifecycle = new RuntimeUnlockRegularV4RecoveryLifecycleReport(
            new RuntimeUnlockRegularV4RecoveryAuthority(bridgeAuthority, activeLease),
            RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Completed,
            lifecycleReason);
        var retiredLease = activeLease with
        {
            State = RecoveryLivenessLeaseState.Retired,
            Reason = lifecycleReason,
        };
        SetProperty(context, "CurrentRecoveryLivenessLease", null);
        SetProperty(context, "LastRetiredRecoveryLivenessLease", retiredLease);
        SetProperty(context, "LastRecoveryLivenessLeaseGeneration", leaseGeneration);
        SetProperty(context, "CurrentBridgeRecoveryDecision", bridgeDecision);
        SetProperty(context, "LastBridgeRecoveryDecisionGeneration", bridgeDecisionGeneration);
        SetProperty(context, "LastRuntimeUnlockRegularV4RecoveryLifecycle", lifecycle);
        SetProperty(context, "RuntimeUnlockRecoveryLeaseClosureAvailable", true);
        return (leaseGeneration, bridgeDecisionGeneration);
    }

    private static RuntimeUnlockRegularV4RecoverySettlementReport
        SeedRegularV4RecoverySettlement(object context)
    {
        var sessionId = GetRequiredProperty<string>(context, "SessionId");
        var transferId = GetRequiredProperty<string>(context, "TransferId");
        var route = GetRequiredProperty<FileTransferRouteSelection>(context, "RouteSelection");
        var epoch = GetOptionalProperty<LiveRouteEpoch>(context, "CurrentLiveRouteEpoch");
        var leg = GetRequiredProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        var owner = GetRequiredProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var terminalGeneration = GetRequiredProperty<int>(context, "TerminalGeneration");
        var publication = GetRequiredProperty<RoutePublicationToken>(
            context,
            "CurrentRoutePublicationToken");
        const int repairGeneration = 7;
        var authorityId = new FileTransferAuthorityId(
            $"fta2-test-regular-v4-handoff-{transferId}");
        var repairDecision = new RepairReplayDecision(
            Accepted: true,
            RepairReplayKind.V4Repair,
            "test_regular_v4_handoff",
            repairGeneration,
            route.Route,
            route.ProtocolVersion,
            epoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            terminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            owner,
            authorityId,
            PredecessorAuthorityId: null,
            BridgeDecisionGeneration: 0,
            publication);
        SetProperty(context, "CurrentRepairReplayDecision", repairDecision);
        SetProperty(context, "LastRepairReplayGeneration", repairGeneration);

        var settlement = new FileTransferRegularV4FrontierRepairSettlement(
            sessionId,
            transferId,
            FileTransferDirection.Outbound,
            route.TelemetryToken,
            route.ProtocolVersion,
            epoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            terminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            BridgeDecisionGeneration: 0,
            publication.PublicationGeneration,
            publication.RouteCommitGeneration,
            "regular-v4-frontier:0:test",
            FrontierChunkIndex: 0,
            DurableReceivedHighestChunkIndex: -1,
            RequestedChunkCount: 1,
            repairGeneration,
            "test_regular_v4_handoff_consumed",
            FileTransferRegularV4FrontierRepairSettlementOutcome.MissingDispatch,
            FileTransferTransportKind.RegularNkn)
        {
            AuthorityId = authorityId,
        };
        SetProperty(context, "LastCoordinatorIssuedRegularV4RecoverySettlement", settlement);
        SetProperty(context, "RuntimeUnlockRegularV4RecoverySettlementAvailable", false);
        SetProperty(context, "CurrentRecoveryLivenessLease", null);
        SetProperty(context, "LastRetiredRecoveryLivenessLease", null);
        SetProperty(context, "LastRuntimeUnlockRegularV4RecoveryLifecycle", null);
        SetProperty(context, "RuntimeUnlockRecoveryLeaseClosureAvailable", false);
        return new RuntimeUnlockRegularV4RecoverySettlementReport(
            settlement,
            RegularV4LivenessGeneration: 0,
            BridgeDecisionGeneration: 0,
            RuntimeUnlockRegularV4RecoverySettlementDisposition.Consumed,
            "test_regular_v4_handoff_consumed");
    }

    private static RuntimeUnlockRegularV4RecoveryLifecycleReport
        SeedRegularV4RecoveryLifecycle(object context)
    {
        var sessionId = GetRequiredProperty<string>(context, "SessionId");
        var transferId = GetRequiredProperty<string>(context, "TransferId");
        var route = GetRequiredProperty<FileTransferRouteSelection>(context, "RouteSelection");
        var epoch = GetOptionalProperty<LiveRouteEpoch>(context, "CurrentLiveRouteEpoch");
        var leg = GetRequiredProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        var owner = GetRequiredProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var terminalGeneration = GetRequiredProperty<int>(context, "TerminalGeneration");
        const int bridgeDecisionGeneration = 1;
        var bridge = new BridgeRecoveryDecision(
            true,
            BridgeRecoveryKind.BridgeReceiveStallRecovery,
            "test_regular_v4_lifecycle",
            bridgeDecisionGeneration,
            route.Route,
            route.ProtocolVersion,
            epoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            terminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            owner);
        SetProperty(context, "CurrentBridgeRecoveryDecision", bridge);
        SetProperty(context, "LastBridgeRecoveryDecisionGeneration", bridgeDecisionGeneration);
        var lease = new RecoveryLivenessLease(
            sessionId,
            transferId,
            FileTransferDirection.Outbound,
            owner,
            route.Route,
            route.TelemetryToken,
            route.ProtocolVersion,
            epoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            terminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            LeaseGeneration: 1,
            new FileTransferAuthorityId($"fta2-test-lifecycle-lease-{transferId}"),
            RecoveryLivenessLeaseState.Active,
            "test_regular_v4_lifecycle");
        SetProperty(context, "CurrentRecoveryLivenessLease", lease);
        SetProperty(context, "LastRecoveryLivenessLeaseGeneration", 1);
        SetProperty(context, "LastRetiredRecoveryLivenessLease", null);
        SetProperty(context, "LastRuntimeUnlockRegularV4RecoveryLifecycle", null);
        SetProperty(context, "RuntimeUnlockRecoveryLeaseClosureAvailable", false);
        var authority = new FileTransferBridgeRecoveryAuthority(
            sessionId,
            transferId,
            FileTransferDirection.Outbound,
            route.TelemetryToken,
            route.ProtocolVersion,
            epoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            terminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            bridgeDecisionGeneration,
            BridgeRecoveryKind.BridgeReceiveStallRecovery.ToString(),
            "test_regular_v4_lifecycle");
        return new RuntimeUnlockRegularV4RecoveryLifecycleReport(
            new RuntimeUnlockRegularV4RecoveryAuthority(authority, lease),
            RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Completed,
            "bridge_lifecycle_completed");
    }

    private static object GetOutboundContext(SessionFileTransferService service)
    {
        var field = typeof(SessionFileTransferService).GetField(
            "outboundTransfer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var context = field!.GetValue(service);
        Assert.NotNull(context);
        return context!;
    }

    private static object GetInboundContext(SessionFileTransferService service)
    {
        var field = typeof(SessionFileTransferService).GetField(
            "inboundTransfer",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var context = field!.GetValue(service);
        Assert.NotNull(context);
        return context!;
    }

    private static object GetCoreGate(SessionFileTransferService service)
    {
        var field = typeof(SessionFileTransferService).GetField(
            "gate",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<object>(field!.GetValue(service));
    }

    private static T GetRequiredProperty<T>(object context, string name)
    {
        var property = context.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        var value = property!.GetValue(context);
        Assert.NotNull(value);
        return Assert.IsType<T>(value);
    }

    private static T? GetOptionalProperty<T>(object context, string name)
        where T : class
    {
        var property = context.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        return property!.GetValue(context) as T;
    }

    private static T? GetOptionalStructProperty<T>(object context, string name)
        where T : struct
    {
        var property = context.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        var value = property!.GetValue(context);
        return value is null ? null : Assert.IsType<T>(value);
    }

    private static void SetProperty(object context, string name, object? value)
    {
        var property = context.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        property!.SetValue(context, value);
    }

    private static RuntimeUnlockRetryDispatchContract? GetCurrentContract(object context)
        => context.GetType()
            .GetProperty(
                "CurrentRuntimeUnlockRetryDispatchContract",
                BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(context) as RuntimeUnlockRetryDispatchContract;

    private static RuntimeUnlockRetryDispatchSettlementReport? GetLastSettlement(object context)
    {
        var value = context.GetType()
            .GetProperty(
                "LastRuntimeUnlockRetryDispatchSettlement",
                BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(context);
        return value is RuntimeUnlockRetryDispatchSettlementReport report ? report : null;
    }

    private static RuntimeUnlockRegularV4RecoverySettlementReport? GetLastRegularV4Settlement(
        object context)
    {
        var value = context.GetType()
            .GetProperty(
                "LastRuntimeUnlockRegularV4RecoverySettlement",
                BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(context);
        return value is RuntimeUnlockRegularV4RecoverySettlementReport report ? report : null;
    }

    private sealed class RetryFixture(
        SessionFileTransferService service,
        RetryAuthorityLoopbackTransport bridge,
        LoopbackFileTransferTransport peer,
        object context,
        string transferId,
        RuntimeUnlockRetryDispatchAuthorityRequest initialRequest) : IDisposable
    {
        public SessionFileTransferService Service { get; } = service;

        public RetryAuthorityLoopbackTransport Bridge { get; } = bridge;

        public object Context { get; } = context;

        public string TransferId { get; } = transferId;

        public RuntimeUnlockRetryDispatchAuthorityRequest InitialRequest { get; } = initialRequest;

        public void Dispose()
        {
            Service.Dispose();
            Bridge.Dispose();
            peer.Dispose();
        }
    }

    private sealed class RetryAuthorityLoopbackTransport(string sessionId) :
        LoopbackFileTransferTransport(sessionId),
        IRuntimeUnlockRetryDispatchAuthorityBridge
    {
        private readonly ConcurrentQueue<RuntimeUnlockRetryDispatchDecision> authorityDecisions = [];
        private readonly ConcurrentQueue<RuntimeUnlockRetryDispatchSettlementDecision> settlementDecisions = [];
        private readonly ConcurrentQueue<RuntimeUnlockRegularV4RecoverySettlementDecision> regularV4SettlementDecisions = [];
        private readonly ConcurrentQueue<RuntimeUnlockRegularV4RecoveryLifecycleDecision> regularV4LifecycleDecisions = [];
        private EventHandler<RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs>? authorityRequested;
        private EventHandler<RuntimeUnlockRetryDispatchSettlementReportedEventArgs>? settlementReported;
        private EventHandler<RuntimeUnlockRegularV4RecoverySettlementReportedEventArgs>? regularV4SettlementReported;
        private EventHandler<RuntimeUnlockRegularV4RecoveryLifecycleReportedEventArgs>? regularV4LifecycleReported;
        private RuntimeUnlockRetryDispatchAuthorityRequest? pendingRequest;
        private int settlementDecisionObservationCount;

        public event EventHandler<RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs>?
            RuntimeUnlockRetryDispatchAuthorityRequested
        {
            add => authorityRequested += value;
            remove => authorityRequested -= value;
        }

        public event EventHandler<RuntimeUnlockRetryDispatchSettlementReportedEventArgs>?
            RuntimeUnlockRetryDispatchSettlementReported
        {
            add => settlementReported += value;
            remove => settlementReported -= value;
        }

        public event EventHandler<RuntimeUnlockRegularV4RecoverySettlementReportedEventArgs>?
            RuntimeUnlockRegularV4RecoverySettlementReported
        {
            add => regularV4SettlementReported += value;
            remove => regularV4SettlementReported -= value;
        }

        public event EventHandler<RuntimeUnlockRegularV4RecoveryLifecycleReportedEventArgs>?
            RuntimeUnlockRegularV4RecoveryLifecycleReported
        {
            add => regularV4LifecycleReported += value;
            remove => regularV4LifecycleReported -= value;
        }

        public int AuthorityRequestSubscriberCount =>
            authorityRequested?.GetInvocationList().Length ?? 0;

        public int SettlementReportSubscriberCount =>
            settlementReported?.GetInvocationList().Length ?? 0;

        public int RegularV4SettlementReportSubscriberCount =>
            regularV4SettlementReported?.GetInvocationList().Length ?? 0;

        public int RegularV4LifecycleReportSubscriberCount =>
            regularV4LifecycleReported?.GetInvocationList().Length ?? 0;

        public RuntimeUnlockRetryDispatchContract? InfraCurrentContract { get; private set; }

        public object? CoreGateForSettlementDecisionProbe { get; set; }

        public bool LastSettlementDecisionObservedWithCoreGateAvailable { get; private set; }

        public ManualResetEventSlim SettlementDecisionObserved { get; } = new(initialState: false);

        public int SettlementDecisionCount => Volatile.Read(ref settlementDecisionObservationCount);

        public void RaiseAuthorityRequest(RuntimeUnlockRetryDispatchAuthorityRequest request)
        {
            pendingRequest = request;
            authorityRequested?.Invoke(
                this,
                new RuntimeUnlockRetryDispatchAuthorityRequestedEventArgs(request));
        }

        public void RaiseSettlementReport(RuntimeUnlockRetryDispatchSettlementReport report)
        {
            SettlementDecisionObserved.Reset();
            settlementReported?.Invoke(
                this,
                new RuntimeUnlockRetryDispatchSettlementReportedEventArgs(report));
        }

        public void RaiseRegularV4SettlementReport(
            RuntimeUnlockRegularV4RecoverySettlementReport report)
            => regularV4SettlementReported?.Invoke(
                this,
                new RuntimeUnlockRegularV4RecoverySettlementReportedEventArgs(report));

        public void RaiseRegularV4LifecycleReport(
            RuntimeUnlockRegularV4RecoveryLifecycleReport report)
            => regularV4LifecycleReported?.Invoke(
                this,
                new RuntimeUnlockRegularV4RecoveryLifecycleReportedEventArgs(report));

        public RuntimeUnlockRetryDispatchDecision DequeueAuthorityDecision()
        {
            Assert.True(authorityDecisions.TryDequeue(out var decision));
            return decision;
        }

        public RuntimeUnlockRetryDispatchSettlementDecision DequeueSettlementDecision()
        {
            Assert.True(settlementDecisions.TryDequeue(out var decision));
            return decision;
        }

        public RuntimeUnlockRegularV4RecoverySettlementDecision DequeueRegularV4SettlementDecision()
        {
            Assert.True(regularV4SettlementDecisions.TryDequeue(out var decision));
            return decision;
        }

        public RuntimeUnlockRegularV4RecoveryLifecycleDecision DequeueRegularV4LifecycleDecision()
        {
            Assert.True(regularV4LifecycleDecisions.TryDequeue(out var decision));
            return decision;
        }

        public bool TryGetPendingRuntimeUnlockRetryDispatchAuthorityRequest(
            string requestedSessionId,
            string transferId,
            FileTransferDirection direction,
            out RuntimeUnlockRetryDispatchAuthorityRequest request)
        {
            if (pendingRequest is { } pending &&
                string.Equals(pending.SessionId, requestedSessionId, StringComparison.Ordinal) &&
                string.Equals(pending.TransferId, transferId, StringComparison.Ordinal) &&
                pending.Direction == direction)
            {
                request = pending;
                return true;
            }

            request = default;
            return false;
        }

        public bool ObserveRuntimeUnlockRetryDispatchDecision(
            RuntimeUnlockRetryDispatchDecision decision)
        {
            authorityDecisions.Enqueue(decision);
            pendingRequest = null;
            if (decision.Granted)
            {
                InfraCurrentContract = decision.Contract;
            }
            else if (decision.Status == RuntimeUnlockRetryDispatchDecisionStatus.Rejected &&
                     string.Equals(
                         decision.Reason,
                         "retry_dispatch_missing_successor_limit_exhausted",
                         StringComparison.Ordinal))
            {
                InfraCurrentContract = null;
            }

            return true;
        }

        public bool ObserveRuntimeUnlockRetryDispatchSettlementDecision(
            RuntimeUnlockRetryDispatchSettlementDecision decision)
        {
            Interlocked.Increment(ref settlementDecisionObservationCount);
            if (CoreGateForSettlementDecisionProbe is { } coreGate)
            {
                LastSettlementDecisionObservedWithCoreGateAvailable = Task.Run(() =>
                {
                    if (!Monitor.TryEnter(coreGate, TimeSpan.FromMilliseconds(500)))
                    {
                        return false;
                    }

                    Monitor.Exit(coreGate);
                    return true;
                }).GetAwaiter().GetResult();
            }

            settlementDecisions.Enqueue(decision);
            SettlementDecisionObserved.Set();
            return true;
        }

        public bool ObserveRuntimeUnlockRegularV4RecoverySettlementDecision(
            RuntimeUnlockRegularV4RecoverySettlementDecision decision)
        {
            regularV4SettlementDecisions.Enqueue(decision);
            return true;
        }

        public bool ObserveRuntimeUnlockRegularV4RecoveryLifecycleDecision(
            RuntimeUnlockRegularV4RecoveryLifecycleDecision decision)
        {
            regularV4LifecycleDecisions.Enqueue(decision);
            return true;
        }
    }
}
