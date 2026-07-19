using System.Collections.Concurrent;
using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Pressure_StartTransferRetiresExactBindingBeforeScopeReplacement()
    {
        const string transferId = "transfer_regular_v4_pressure_start_transfer";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var originalLeg = SeedCurrentRuntimeOwnerForTest(
            context,
            FileTransferDirection.Inbound,
            "pressure_start_transfer");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var installedBinding = GetPrivatePropertyValue(
            context,
            "InstalledRegularV4FrontierRepairPressureBinding");
        Assert.NotNull(installedBinding);

        var started = InvokeStartInboundTransferLeg(
            service,
            context,
            FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast),
            "unit_test_pressure_scope_replacement",
            originalLeg.TransportEpochId + 1,
            originalLeg.BridgeRecoveryGeneration + 1);

        Assert.NotNull(started);
        Assert.NotSame(originalLeg, started);
        Assert.Null(GetPrivatePropertyValue(context, "PendingRegularV4FrontierRepairPressureBinding"));
        Assert.Null(GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));
        Assert.Null(GetPrivatePropertyValue(context, "RetainedRegularV4FrontierRepairPressurePredecessor"));
        var settlement = Assert.Single(transport.RegularV4FrontierRepairSettlements);
        Assert.Equal(FileTransferRegularV4FrontierRepairSettlementOutcome.StaleRejected, settlement.Outcome);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Pressure_StartTransferRetirementRejectionPreservesOldScope()
    {
        const string transferId = "transfer_regular_v4_pressure_start_transfer_rejected";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var originalLeg = SeedCurrentRuntimeOwnerForTest(
            context,
            FileTransferDirection.Inbound,
            "pressure_start_transfer_rejected");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var originalOwner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var installedBinding = GetPrivatePropertyValue(
            context,
            "InstalledRegularV4FrontierRepairPressureBinding");
        Assert.NotNull(installedBinding);
        transport.RegularV4PressureSettlementApplyOverride = command => new(
            command,
            FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Rejected,
            "unit_test_scope_retirement_rejected",
            CurrentAuthorityCleared: false,
            RetainedPredecessorCleared: false);

        var started = InvokeStartInboundTransferLeg(
            service,
            context,
            FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast),
            "unit_test_pressure_scope_replacement_rejected",
            originalLeg.TransportEpochId + 1,
            originalLeg.BridgeRecoveryGeneration + 1);

        Assert.Null(started);
        Assert.Same(originalLeg, GetPrivatePropertyValue(context, "CurrentTransferLeg"));
        Assert.Equal(originalOwner, GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"));
        Assert.Equal(
            installedBinding,
            GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));
        Assert.NotNull(GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Pressure_StartTransferReadinessRejectionPreservesBinding()
    {
        const string transferId = "transfer_regular_v4_pressure_start_transfer_readiness_rejected";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var originalLeg = SeedCurrentRuntimeOwnerForTest(
            context,
            FileTransferDirection.Inbound,
            "pressure_start_transfer_readiness_rejected");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var installedBinding = GetPrivatePropertyValue(
            context,
            "InstalledRegularV4FrontierRepairPressureBinding");
        Assert.NotNull(installedBinding);

        var started = InvokeStartInboundTransferLeg(
            service,
            context,
            FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4),
            "unit_test_pressure_readiness_rejected",
            originalLeg.TransportEpochId + 1,
            originalLeg.BridgeRecoveryGeneration + 1);

        Assert.Null(started);
        Assert.Same(originalLeg, GetPrivatePropertyValue(context, "CurrentTransferLeg"));
        Assert.Equal(
            installedBinding,
            GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));
        Assert.Empty(transport.RegularV4FrontierRepairSettlements);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Pressure_TunaPauseRetirementRejectionDoesNotPauseOrClearRepair()
    {
        const string transferId = "transfer_regular_v4_pressure_tuna_pause_rejected";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "pressure_tuna_pause_rejected");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var installedBinding = GetPrivatePropertyValue(
            context,
            "InstalledRegularV4FrontierRepairPressureBinding");
        var projection = GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection");
        Assert.NotNull(installedBinding);
        Assert.NotNull(projection);
        transport.RegularV4PressureSettlementApplyOverride = command => new(
            command,
            FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Rejected,
            "unit_test_pause_retirement_rejected",
            CurrentAuthorityCleared: false,
            RetainedPredecessorCleared: false);

        var paused = Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod("TryPauseInboundTransportLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "tuna_activation_negotiating", false]));

        Assert.False(paused);
        Assert.False(GetPrivateProperty<bool>(context, "PullTransportPaused"));
        Assert.Equal(
            installedBinding,
            GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));
        Assert.Equal(projection, GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public async Task RegularV4Pressure_TerminalRetirementRejectionPreservesNonterminalScope()
    {
        const string transferId = "transfer_regular_v4_pressure_terminal_rejected";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "pressure_terminal_rejected");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var originalState = GetPrivateProperty<FileTransferTransferState>(context, "State");
        var installedBinding = GetPrivatePropertyValue(
            context,
            "InstalledRegularV4FrontierRepairPressureBinding");
        Assert.NotNull(installedBinding);
        transport.RegularV4PressureSettlementApplyOverride = command => new(
            command,
            FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Rejected,
            "unit_test_terminal_retirement_rejected",
            CurrentAuthorityCleared: false,
            RetainedPredecessorCleared: false);

        await (Task)typeof(SessionFileTransferService)
            .GetMethod("TransitionInboundToTerminalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    FileTransferTransferState.Failed,
                    "unit_test_failure",
                    "unit test failure",
                    false,
                    null,
                    null,
                    CancellationToken.None,
                ])!;

        Assert.Equal(originalState, GetPrivateProperty<FileTransferTransferState>(context, "State"));
        Assert.Equal(
            installedBinding,
            GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));
        Assert.NotNull(GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Pressure_RouteCommitRetirementRejectionPreservesRegularV4Scope()
    {
        const string transferId = "transfer_regular_v4_pressure_route_commit_rejected";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        transport.SeedRuntimeUnlockRouteCommitProofForTests(transferId, "pressure_route_commit_rejected");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "pressure_route_commit_rejected");
        SeedRuntimeUnlockAdmissionForTest(context, FileTransferDirection.Inbound, "pressure_route_commit_rejected");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var originalOwner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var installedBinding = GetPrivatePropertyValue(
            context,
            "InstalledRegularV4FrontierRepairPressureBinding");
        Assert.NotNull(installedBinding);
        transport.RegularV4PressureSettlementApplyOverride = command => new(
            command,
            FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Rejected,
            "unit_test_route_commit_retirement_rejected",
            CurrentAuthorityCleared: false,
            RetainedPredecessorCleared: false);

        var promoted = Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod("TryPromoteInboundRegularNknV4ToFileTunaV4Locked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    "pressure_route_commit_rejected",
                    FileTransferTransportHandoffKind.NormalToTunaActivation,
                    FileTransferTransportKind.Tuna,
                ]));

        Assert.False(promoted);
        Assert.Equal(
            FileTransferRoute.RegularNknV4Fast,
            GetPrivateProperty<FileTransferRouteSelection>(context, "RouteSelection").Route);
        Assert.Equal(originalOwner, GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"));
        Assert.Equal(
            installedBinding,
            GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));
        Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockRouteCommitToken"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Pressure_RouteCommitProofRejectionPreservesBinding()
    {
        const string transferId = "transfer_regular_v4_pressure_route_commit_proof_rejected";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "pressure_route_commit_proof_rejected");
        SeedRuntimeUnlockAdmissionForTest(context, FileTransferDirection.Inbound, "pressure_route_commit_proof_rejected");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var installedBinding = GetPrivatePropertyValue(
            context,
            "InstalledRegularV4FrontierRepairPressureBinding");
        Assert.NotNull(installedBinding);
        var method = typeof(SessionFileTransferService)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate =>
                string.Equals(candidate.Name, "TryAcceptRuntimeUnlockRouteCommitLocked", StringComparison.Ordinal) &&
                candidate.GetParameters() is { Length: 5 } parameters &&
                string.Equals(parameters[0].ParameterType.Name, "InboundTransferContext", StringComparison.Ordinal));
        object?[] arguments =
        [
            context,
            FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4),
            "unit_test_pressure_route_commit_proof_rejected",
            null,
            null,
        ];

        var accepted = Assert.IsType<bool>(method.Invoke(service, arguments));

        Assert.False(accepted);
        Assert.Equal("transaction_proof_missing", Assert.IsType<string>(arguments[3]));
        Assert.Equal(
            installedBinding,
            GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));
        Assert.Empty(transport.RegularV4FrontierRepairSettlements);
        Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockRouteCommitToken"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Pressure_RouteCommitRetiresExactBindingBeforeTunaOwnerInstall()
    {
        const string transferId = "transfer_regular_v4_pressure_route_commit";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        transport.SeedRuntimeUnlockRouteCommitProofForTests(transferId, "pressure_route_commit");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        SeedCurrentRuntimeOwnerForTest(
            context,
            FileTransferDirection.Inbound,
            "pressure_route_commit");
        var originalOwner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        SeedRuntimeUnlockAdmissionForTest(context, FileTransferDirection.Inbound, "pressure_route_commit");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        Assert.NotNull(GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));

        var promoted = Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod("TryPromoteInboundRegularNknV4ToFileTunaV4Locked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(
                service,
                [
                    context,
                    "pressure_route_commit",
                    FileTransferTransportHandoffKind.NormalToTunaActivation,
                    FileTransferTransportKind.Tuna,
                ]));

        Assert.True(promoted);
        Assert.Equal(
            FileTransferRoute.FileTunaV4,
            GetPrivateProperty<FileTransferRouteSelection>(context, "RouteSelection").Route);
        Assert.NotEqual(
            originalOwner.RuntimeGeneration,
            GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner").RuntimeGeneration);
        Assert.Null(GetPrivatePropertyValue(context, "PendingRegularV4FrontierRepairPressureBinding"));
        Assert.Null(GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));
        Assert.Null(GetPrivatePropertyValue(context, "RetainedRegularV4FrontierRepairPressurePredecessor"));
        Assert.NotNull(GetPrivatePropertyValue(context, "RuntimeUnlockRouteCommitToken"));
        var settlement = Assert.Single(transport.RegularV4FrontierRepairSettlements);
        Assert.Equal(FileTransferRegularV4FrontierRepairSettlementOutcome.StaleRejected, settlement.Outcome);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4FrontierRepairPressure_InboundReceiveRecoveryAtomicallyRetiresBindingAndDispatchesAuthority()
    {
        const string transferId = "transfer_regular_v4_pressure_inbound_recovery_handoff";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "pressure_inbound_recovery_handoff");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var binding = Assert.IsType<RegularV4FrontierRepairPressureBinding>(
            GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));
        var applyCount = 0;
        transport.RegularV4PressureSettlementApplyOverride = command =>
        {
            Interlocked.Increment(ref applyCount);
            return new(
                command,
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
                "unit_test_recovery_handoff_applied",
                CurrentAuthorityCleared: true,
                RetainedPredecessorCleared: false);
        };
        var serviceGate = typeof(SessionFileTransferService)
            .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        transport.ReceiveRecoveryRequestObserver = _ => Assert.False(Monitor.IsEntered(serviceGate));

        var requested = InvokeReceiveRecovery(
            service,
            new FileTransferReceiveRecoveryRequest(
                $"session_{transferId}",
                transferId,
                FileTransferDirection.Inbound,
                "session_liveness_timeout_pending"));

        Assert.True(requested);
        Assert.Null(GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));
        Assert.Null(GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract"));
        var recoveryRequest = Assert.Single(transport.ReceiveRecoveryRequests);
        Assert.NotNull(recoveryRequest.BridgeRecoveryAuthority);
        Assert.NotNull(recoveryRequest.RegularV4RecoveryAuthority);
        Assert.Equal(binding.Authority.BridgeDecisionGeneration + 1, recoveryRequest.BridgeRecoveryAuthority!.BridgeDecisionGeneration);
        Assert.Equal(1, Volatile.Read(ref applyCount));
        Assert.True(
            SpinWait.SpinUntil(
                () => transport.RegularV4FrontierRepairSettlements.Count == 1,
                TimeSpan.FromSeconds(2)));
        var settlement = Assert.Single(transport.RegularV4FrontierRepairSettlements);
        Assert.Equal(binding.Authority.AuthorityId, settlement.AuthorityId);
        Assert.Equal(FileTransferRegularV4FrontierRepairSettlementOutcome.StaleRejected, settlement.Outcome);
        Assert.Equal("bridge_receive_stall_recovery_pressure_handoff", settlement.DecisionReason);

        var log = ReadOperationalLogTail(logStart);
        var prepared = log.IndexOf(
            "event=filetransfer_regular_v4_frontier_repair_pressure_recovery_handoff_prepared;",
            StringComparison.Ordinal);
        var committed = log.IndexOf(
            "event=filetransfer_regular_v4_frontier_repair_pressure_recovery_handoff_committed;",
            StringComparison.Ordinal);
        var granted = log.IndexOf("event=filetransfer_bridge_recovery_authority_granted;", StringComparison.Ordinal);
        Assert.True(prepared >= 0, log);
        Assert.True(committed > prepared, log);
        Assert.True(granted > committed, log);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4FrontierRepairPressure_OutboundReceiveRecoveryUsesTheSameAtomicHandoff()
    {
        const string transferId = "transfer_regular_v4_pressure_outbound_recovery_handoff";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 100,
            chunksAcceptedForTransport: 256,
            remoteGrantedUntilExclusive: 256,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Outbound, "pressure_outbound_recovery_handoff");
        SetPrivateProperty(context, "PullSourceCanSeek", true);
        SetPrivateProperty(context, "PullV4LastPeerFrameReceivedUtc", DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
        typeof(SessionFileTransferService)
            .GetField("outboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod("MaybeQueueOutboundRegularNknV4PeerSilenceSafetyReplayLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, "session_liveness_timeout_pending"])));
        var binding = Assert.IsType<RegularV4FrontierRepairPressureBinding>(
            GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));

        var requested = InvokeReceiveRecovery(
            service,
            new FileTransferReceiveRecoveryRequest(
                $"session_{transferId}",
                transferId,
                FileTransferDirection.Outbound,
                "session_liveness_timeout_pending"));

        Assert.True(requested);
        Assert.Null(GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));
        Assert.Null(GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract"));
        var recoveryRequest = Assert.Single(transport.ReceiveRecoveryRequests);
        Assert.NotNull(recoveryRequest.RegularV4RecoveryAuthority);
        Assert.True(
            SpinWait.SpinUntil(
                () => transport.RegularV4FrontierRepairSettlements.Count == 1,
                TimeSpan.FromSeconds(2)));
        var settlement = Assert.Single(transport.RegularV4FrontierRepairSettlements);
        Assert.Equal(binding.Authority.AuthorityId, settlement.AuthorityId);
        Assert.Equal(FileTransferRegularV4FrontierRepairSettlementOutcome.StaleRejected, settlement.Outcome);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4FrontierRepairPressure_RecoveryApplyRejectionRetainsBindingAndIssuesNoAuthority()
    {
        const string transferId = "transfer_regular_v4_pressure_recovery_handoff_rejected";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "pressure_recovery_handoff_rejected");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var binding = GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding");
        var contract = GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract");
        transport.RegularV4PressureSettlementApplyOverride = command => new(
            command,
            FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Rejected,
            "unit_test_recovery_handoff_apply_rejected",
            CurrentAuthorityCleared: false,
            RetainedPredecessorCleared: false);

        var requested = InvokeReceiveRecovery(
            service,
            new FileTransferReceiveRecoveryRequest(
                $"session_{transferId}",
                transferId,
                FileTransferDirection.Inbound,
                "session_liveness_timeout_pending"));

        Assert.False(requested);
        Assert.Equal(binding, GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));
        Assert.Same(contract, GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract"));
        Assert.Empty(transport.ReceiveRecoveryRequests);
        Assert.Null(GetPrivatePropertyValue(context, "CurrentRecoveryLivenessLease"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4FrontierRepairPressure_RecoveryApplyThrowRetainsBindingAndIssuesNoAuthority()
    {
        const string transferId = "transfer_regular_v4_pressure_recovery_handoff_throw";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "pressure_recovery_handoff_throw");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var binding = GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding");
        var contract = GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract");
        transport.RegularV4PressureSettlementApplyOverride = _ =>
            throw new InvalidOperationException("unit_test_pressure_apply_throw");

        var requested = InvokeReceiveRecovery(
            service,
            new FileTransferReceiveRecoveryRequest(
                $"session_{transferId}",
                transferId,
                FileTransferDirection.Inbound,
                "session_liveness_timeout_pending"));

        Assert.False(requested);
        Assert.Equal(binding, GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));
        Assert.Same(contract, GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract"));
        Assert.Empty(transport.ReceiveRecoveryRequests);
        Assert.Null(GetPrivatePropertyValue(context, "CurrentRecoveryLivenessLease"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4FrontierRepairPressure_MissingApplyBridgeRetainsBindingAndIssuesNoAuthority()
    {
        const string transferId = "transfer_regular_v4_pressure_recovery_handoff_bridge_missing";
        using var service = new SessionFileTransferService();
        using var installTransport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(installTransport);
        var context = CreateProjectionInboundContext(transferId);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "pressure_recovery_handoff_bridge_missing");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var binding = GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding");
        var contract = GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract");
        var recoveryOnlyTransport = new RecoveryControllerWithoutPressureBridgeTransport(installTransport);
        typeof(SessionFileTransferService)
            .GetField("transport", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, recoveryOnlyTransport);

        var requested = InvokeReceiveRecovery(
            service,
            new FileTransferReceiveRecoveryRequest(
                $"session_{transferId}",
                transferId,
                FileTransferDirection.Inbound,
                "session_liveness_timeout_pending"));

        Assert.False(requested);
        Assert.Equal(binding, GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));
        Assert.Same(contract, GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract"));
        Assert.Empty(recoveryOnlyTransport.ReceiveRecoveryRequests);
        Assert.Null(GetPrivatePropertyValue(context, "CurrentRecoveryLivenessLease"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4FrontierRepairPressure_ControllerFailureDiscardsIssuedRecoveryWithoutReapplyingPressure()
    {
        const string transferId = "transfer_regular_v4_pressure_recovery_handoff_controller_throw";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}")
        {
            ReceiveRecoveryException = new InvalidOperationException("unit_test_recovery_controller_throw"),
        };
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        SeedCurrentRuntimeOwnerForTest(context, FileTransferDirection.Inbound, "pressure_recovery_handoff_controller_throw");
        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        var applyCount = 0;
        transport.RegularV4PressureSettlementApplyOverride = command =>
        {
            Interlocked.Increment(ref applyCount);
            return new(
                command,
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
                "unit_test_recovery_handoff_applied",
                CurrentAuthorityCleared: true,
                RetainedPredecessorCleared: false);
        };

        var requested = InvokeReceiveRecovery(
            service,
            new FileTransferReceiveRecoveryRequest(
                $"session_{transferId}",
                transferId,
                FileTransferDirection.Inbound,
                "session_liveness_timeout_pending"));

        Assert.False(requested);
        Assert.Equal(1, Volatile.Read(ref applyCount));
        Assert.Null(GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));
        Assert.Null(GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract"));
        Assert.Null(GetPrivatePropertyValue(context, "CurrentRecoveryLivenessLease"));
        Assert.Empty(transport.ReceiveRecoveryRequests);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains("lifecycle_outcome=Discarded", log, StringComparison.Ordinal);
        Assert.Contains("decision_status=Accepted", log, StringComparison.Ordinal);
    }

    private static bool InvokeReceiveRecovery(
        SessionFileTransferService service,
        FileTransferReceiveRecoveryRequest request)
        => Assert.IsType<bool>(typeof(SessionFileTransferService)
            .GetMethod("TryRequestFileTransferReceiveRecovery", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [request]));

    private sealed class RecoveryControllerWithoutPressureBridgeTransport(
        IFileTransferSignalingTransport inner) :
        IFileTransferSignalingTransport,
        IFileTransferReceiveRecoveryController
    {
        public ConcurrentQueue<FileTransferReceiveRecoveryRequest> ReceiveRecoveryRequests { get; } = [];

        public event EventHandler<FileTransferOfferReceivedEventArgs>? FileTransferOfferReceived
        {
            add => inner.FileTransferOfferReceived += value;
            remove => inner.FileTransferOfferReceived -= value;
        }

        public event EventHandler<FileTransferAcceptReceivedEventArgs>? FileTransferAcceptReceived
        {
            add => inner.FileTransferAcceptReceived += value;
            remove => inner.FileTransferAcceptReceived -= value;
        }

        public event EventHandler<FileTransferDeclineReceivedEventArgs>? FileTransferDeclineReceived
        {
            add => inner.FileTransferDeclineReceived += value;
            remove => inner.FileTransferDeclineReceived -= value;
        }

        public event EventHandler<FileTransferSessionOpenReceivedEventArgs>? FileTransferSessionOpenReceived
        {
            add => inner.FileTransferSessionOpenReceived += value;
            remove => inner.FileTransferSessionOpenReceived -= value;
        }

        public event EventHandler<FileTransferCancelReceivedEventArgs>? FileTransferCancelReceived
        {
            add => inner.FileTransferCancelReceived += value;
            remove => inner.FileTransferCancelReceived -= value;
        }

        public event EventHandler<FileTransferErrorReceivedEventArgs>? FileTransferErrorReceived
        {
            add => inner.FileTransferErrorReceived += value;
            remove => inner.FileTransferErrorReceived -= value;
        }

        public event EventHandler<FileTransferCompleteReceivedEventArgs>? FileTransferCompleteReceived
        {
            add => inner.FileTransferCompleteReceived += value;
            remove => inner.FileTransferCompleteReceived -= value;
        }

        public event EventHandler<FileTransferPauseControlReceivedEventArgs>? FileTransferPauseControlReceived
        {
            add => inner.FileTransferPauseControlReceived += value;
            remove => inner.FileTransferPauseControlReceived -= value;
        }

        public event EventHandler<FileTransferHeartbeatReceivedEventArgs>? FileTransferHeartbeatReceived
        {
            add => inner.FileTransferHeartbeatReceived += value;
            remove => inner.FileTransferHeartbeatReceived -= value;
        }

        public event EventHandler<FileTransferTransportEpochReceivedEventArgs>? FileTransferTransportEpochReceived
        {
            add => inner.FileTransferTransportEpochReceived += value;
            remove => inner.FileTransferTransportEpochReceived -= value;
        }

        public event EventHandler<FileTransferTransportProbeReceivedEventArgs>? FileTransferTransportProbeReceived
        {
            add => inner.FileTransferTransportProbeReceived += value;
            remove => inner.FileTransferTransportProbeReceived -= value;
        }

        public event EventHandler<FileTransferRepairProofReceivedEventArgs>? FileTransferRepairProofReceived
        {
            add => inner.FileTransferRepairProofReceived += value;
            remove => inner.FileTransferRepairProofReceived -= value;
        }

        public event EventHandler<FileTransferFallbackTailYieldProofReceivedEventArgs>? FileTransferFallbackTailYieldProofReceived
        {
            add => inner.FileTransferFallbackTailYieldProofReceived += value;
            remove => inner.FileTransferFallbackTailYieldProofReceived -= value;
        }

        public Task SendFileTransferOfferAsync(FileTransferOfferV2 message, CancellationToken ct)
            => inner.SendFileTransferOfferAsync(message, ct);

        public Task SendFileTransferAcceptAsync(FileTransferAcceptV1 message, CancellationToken ct)
            => inner.SendFileTransferAcceptAsync(message, ct);

        public Task SendFileTransferDeclineAsync(FileTransferDeclineV1 message, CancellationToken ct)
            => inner.SendFileTransferDeclineAsync(message, ct);

        public Task SendFileTransferSessionOpenAsync(FileTransferSessionOpenV2 message, CancellationToken ct)
            => inner.SendFileTransferSessionOpenAsync(message, ct);

        public Task SendFileTransferCancelAsync(FileTransferCancelV1 message, CancellationToken ct)
            => inner.SendFileTransferCancelAsync(message, ct);

        public Task SendFileTransferErrorAsync(FileTransferErrorV1 message, CancellationToken ct)
            => inner.SendFileTransferErrorAsync(message, ct);

        public Task SendFileTransferCompleteAsync(FileTransferCompleteV1 message, CancellationToken ct)
            => inner.SendFileTransferCompleteAsync(message, ct);

        public Task SendFileTransferPauseControlAsync(FileTransferPauseControlV6 message, CancellationToken ct)
            => inner.SendFileTransferPauseControlAsync(message, ct);

        public Task SendFileTransferHeartbeatAsync(FileTransferHeartbeatV6 message, CancellationToken ct)
            => inner.SendFileTransferHeartbeatAsync(message, ct);

        public Task SendFileTransferTransportEpochAsync(FileTransferTransportEpochV6 message, CancellationToken ct)
            => inner.SendFileTransferTransportEpochAsync(message, ct);

        public Task SendFileTransferTransportProbeAsync(FileTransferTransportProbeV6 message, CancellationToken ct)
            => inner.SendFileTransferTransportProbeAsync(message, ct);

        public Task SendFileTransferRepairProofAsync(FileTransferRepairProofV6 message, CancellationToken ct)
            => inner.SendFileTransferRepairProofAsync(message, ct);

        public Task<IFileTransferDataSession> OpenFileTransferDataSessionAsync(
            string sessionId,
            string transferId,
            CancellationToken ct)
            => inner.OpenFileTransferDataSessionAsync(sessionId, transferId, ct);

        public void RequestFileTransferReceiveRecovery(FileTransferReceiveRecoveryRequest request)
            => ReceiveRecoveryRequests.Enqueue(request);
    }

    private static FileTransferLeg? InvokeStartInboundTransferLeg(
        SessionFileTransferService service,
        object context,
        FileTransferRouteSelection route,
        string reason,
        long transportEpoch,
        int bridgeRecoveryGeneration)
    {
        var method = typeof(SessionFileTransferService)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate =>
                string.Equals(candidate.Name, "StartInboundTransferLegLocked", StringComparison.Ordinal));
        return (FileTransferLeg?)method.Invoke(
            service,
            [
                context,
                route,
                reason,
                FileTransferLegState.RecoveryActive,
                false,
                true,
                transportEpoch,
                bridgeRecoveryGeneration,
                null,
                null,
            ]);
    }
}
