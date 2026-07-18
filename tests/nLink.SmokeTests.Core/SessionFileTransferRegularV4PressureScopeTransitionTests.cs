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
