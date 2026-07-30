using System.Collections.Concurrent;
using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Pressure_CoreContextsAndContractsDoNotMirrorCoordinatorAuthority()
    {
        using var service = new SessionFileTransferService();
        var inboundContext = CreateProjectionInboundContext(
            "transfer_regular_v4_pressure_structural_inbound");
        var outboundContext = CreateRegularNknV4OutboundContext(
            "transfer_regular_v4_pressure_structural_outbound",
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        var bindingFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var removedContextProperties = new[]
        {
            "PendingRegularV4FrontierRepairPressureBinding",
            "InstalledRegularV4FrontierRepairPressureBinding",
            "RetainedRegularV4FrontierRepairPressurePredecessor",
            "LastRegularV4FrontierRepairPressureSettlement",
            "LastRegularV4FrontierRepairPressureInstallGeneration",
            "LastRegularV4FrontierRepairPressureSettlementApplyReceipt",
            "LastRegularV4FrontierRepairPressureRecoveryHandoff",
        };

        foreach (var context in new[] { inboundContext, outboundContext })
        {
            Assert.NotNull(context.GetType().GetProperty("CoordinatorPersistentState", bindingFlags));
            foreach (var propertyName in removedContextProperties)
            {
                Assert.Null(context.GetType().GetProperty(propertyName, bindingFlags));
            }
        }

        var contractType = typeof(SessionFileTransferService).GetNestedType(
            "RegularV4FrontierRepairContract",
            BindingFlags.NonPublic);
        Assert.NotNull(contractType);
        Assert.Null(contractType!.GetProperty("PressureBinding", bindingFlags));
        Assert.Null(contractType.GetProperty("PressureInstallReceipt", bindingFlags));
        foreach (var propertyName in new[]
                 {
                     "PendingPressureInstallPredecessorContract",
                     "PendingPressureInstallReason",
                     "PendingPressureInstallFrontierLagChunks",
                     "PendingPressureInstallRepairCount",
                     "PendingPressureInstallBoundary",
                     "PressureInstallDataSessionGeneration",
                 })
        {
            Assert.Null(contractType.GetProperty(propertyName, bindingFlags));
        }

        var pendingRetryProperty = outboundContext.GetType().GetProperty(
            "PendingRegularV4FrontierRepairPressureInstallRetry",
            bindingFlags);
        Assert.NotNull(pendingRetryProperty);
        Assert.Equal(
            "RegularV4FrontierRepairPressureInstallExecutionIdentity",
            pendingRetryProperty!.PropertyType.Name);
        Assert.Equal(
            new[] { "AuthorityId", "DataSessionGeneration", "InstallGeneration" },
            pendingRetryProperty.PropertyType
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(static property => property.Name)
                .OrderBy(static name => name)
                .ToArray());
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void FileTransferCoordinatorSnapshot_CoreApplicationClonesEpochAndLegGraph()
    {
        using var service = new SessionFileTransferService();
        var inboundContext = CreateProjectionInboundContext(
            "transfer_coordinator_snapshot_core_inbound");
        var outboundContext = CreateRegularNknV4OutboundContext(
            "transfer_coordinator_snapshot_core_outbound",
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        SeedCurrentRuntimeOwnerForTest(
            inboundContext,
            FileTransferDirection.Inbound,
            "coordinator_snapshot_core_inbound");
        SeedCurrentRuntimeOwnerForTest(
            outboundContext,
            FileTransferDirection.Outbound,
            "coordinator_snapshot_core_outbound");

        foreach (var context in new[] { inboundContext, outboundContext })
        {
            var sourceState = CreateCoordinatorStateForTest(context);
            var sourceEpoch = Assert.IsType<LiveRouteEpoch>(sourceState.CurrentLiveRouteEpoch);
            var sourceLeg = Assert.IsType<FileTransferLeg>(sourceState.CurrentLeg);
            var sourceEpochState = sourceEpoch.State;
            var sourceLegState = sourceLeg.State;
            var decision = new FileTransferCoordinatorDecision(
                sourceState,
                RouteChanged: false,
                TerminalMutationRejected: false,
                StartedLiveRouteEpoch: null,
                RecoveredLiveRouteEpoch: null,
                StartedLeg: null,
                FrozenLeg: null,
                AcceptedCheckpointLeg: null,
                FallbackCheckpointRequired: false,
                Reason: "unit_test_core_snapshot_application");
            var apply = typeof(SessionFileTransferService)
                .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(candidate =>
                    candidate.Name == "ApplyCoordinatorDecisionToContextLocked" &&
                    candidate.GetParameters()[0].ParameterType == context.GetType());
            var arguments = apply.GetParameters().Length == 3
                ? new object?[] { context, decision, true }
                : new object?[] { context, decision };

            apply.Invoke(null, arguments);

            var appliedEpoch = Assert.IsType<LiveRouteEpoch>(
                GetPrivatePropertyValue(context, "CurrentLiveRouteEpoch"));
            var appliedLeg = Assert.IsType<FileTransferLeg>(
                GetPrivatePropertyValue(context, "CurrentTransferLeg"));
            var appliedHistory = GetPrivateProperty<List<FileTransferLeg>>(
                context,
                "TransferLegHistory");
            Assert.NotSame(sourceEpoch, appliedEpoch);
            Assert.NotSame(sourceLeg, appliedLeg);
            Assert.Same(
                appliedLeg,
                Assert.Single(
                    appliedHistory,
                    candidate =>
                        candidate.Generation == appliedLeg.Generation &&
                        string.Equals(candidate.LegId, appliedLeg.LegId, StringComparison.Ordinal)));

            sourceEpoch.State = "mutated_after_core_application";
            sourceLeg.State = FileTransferLegState.Terminal;
            Assert.Equal(sourceEpochState, appliedEpoch.State);
            Assert.Equal(sourceLegState, appliedLeg.State);
        }
    }

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
        SetPrivateProperty(context, "HighestBufferedChunkIndex", 8);
        SetPrivateProperty(context, "DataSessionGeneration", 1L);
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
        var applyCount = 0;
        transport.RegularV4PressureSettlementApplyOverride = command =>
        {
            Interlocked.Increment(ref applyCount);
            return new(
                command,
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Rejected,
                "unit_test_route_commit_retirement_rejected",
                CurrentAuthorityCleared: false,
                RetainedPredecessorCleared: false);
        };

        var promoted = PrepareInboundRegularV4ToTunaRouteTransitionForPressureTest(
            service,
            context,
            "pressure_route_commit_rejected");
        var committed = CommitInboundFileTransferRouteTransitionForPressureTest(
            service,
            context);

        Assert.True(promoted);
        Assert.False(committed.Accepted);
        Assert.Equal(
            "unit_test_route_commit_retirement_rejected",
            committed.Reason);
        Assert.Equal(
            FileTransferRoute.RegularNknV4Fast,
            GetPrivateProperty<FileTransferRouteSelection>(context, "RouteSelection").Route);
        Assert.Equal(originalOwner, GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"));
        Assert.Equal(
            installedBinding,
            GetPrivatePropertyValue(context, "InstalledRegularV4FrontierRepairPressureBinding"));
        Assert.True(GetPrivateProperty<bool>(context, "PullTransportPaused"));
        Assert.Null(GetPrivatePropertyValue(context, "RuntimeUnlockRouteCommitToken"));
        Assert.Equal(1, Volatile.Read(ref applyCount));
        var transition = GetPrivateProperty<FileTransferCoordinatorPersistentState>(
            context,
            "CoordinatorPersistentState").FileTransferRouteTransition;
        Assert.True(transition.CurrentOperation.HasValue);
        Assert.Equal(
            FileTransferRouteTransitionState.Draining,
            transition.CurrentOperation.Value.State);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4Pressure_RouteCommitProofRejectionPreservesBinding()
    {
        const string transferId = "transfer_regular_v4_pressure_route_commit_proof_rejected";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "pressure_route_commit_proof_rejected");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        SetPrivateProperty(context, "HighestBufferedChunkIndex", 8);
        SetPrivateProperty(context, "DataSessionGeneration", 1L);
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
        var prepared = PrepareInboundRegularV4ToTunaRouteTransitionForPressureTest(
            service,
            context,
            "unit_test_pressure_route_commit_proof_rejected");
        transport.RuntimeUnlockRouteCommitSnapshot = null;
        var committed = CommitInboundFileTransferRouteTransitionForPressureTest(
            service,
            context);

        Assert.True(prepared);
        Assert.False(committed.Accepted);
        Assert.Equal(
            "filetransfer_route_transition_final_proof_missing",
            committed.Reason);
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
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        transport.SeedRuntimeUnlockRouteCommitProofForTests(transferId, "pressure_route_commit");
        service.AttachTransport(transport);
        var context = CreateProjectionInboundContext(transferId);
        SetPrivateProperty(context, "HighestBufferedChunkIndex", 8);
        SetPrivateProperty(context, "DataSessionGeneration", 1L);
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

        var promoted = PrepareInboundRegularV4ToTunaRouteTransitionForPressureTest(
            service,
            context,
            "pressure_route_commit");
        var committed = CommitInboundFileTransferRouteTransitionForPressureTest(
            service,
            context);

        Assert.True(promoted);
        Assert.True(committed.Accepted, committed.Reason);
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
        var transferLog = string.Join(
            Environment.NewLine,
            ReadOperationalLogTail(logStart)
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Where(line =>
                    line.Contains(
                        "direction=inbound;",
                        StringComparison.Ordinal) &&
                    line.Contains(
                        "projection_source=coordinator_route_transition",
                        StringComparison.Ordinal)));
        foreach (var eventName in new[]
                 {
                     "filetransfer_route_transitioned",
                     "filetransfer_route_selected",
                     "filetransfer_live_route_epoch_started",
                     "filetransfer_live_route_epoch_recovered",
                 })
        {
            var projectionLines = transferLog.Split(
                    Environment.NewLine,
                    StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains(
                    $"event={eventName};",
                    StringComparison.Ordinal))
                .ToArray();
            Assert.True(
                projectionLines.Length == 1,
                $"event={eventName}{Environment.NewLine}{ReadOperationalLogTail(logStart)}");
        }
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
        var executionCommand =
            Assert.IsType<FileTransferBridgeRecoveryExecutionCommand>(
                recoveryRequest.BridgeRecoveryExecutionCommand);
        Assert.NotNull(executionCommand.RecoveryLivenessLease);
        Assert.Equal(binding.Authority.BridgeDecisionGeneration + 1, recoveryRequest.BridgeRecoveryAuthority!.BridgeDecisionGeneration);
        Assert.Equal(
            recoveryRequest.BridgeRecoveryAuthority.BridgeDecisionGeneration,
            executionCommand.Decision.BridgeDecisionGeneration);
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
    public void RegularV4FrontierRepairPressure_InboundRecoveryDetachesUnmaterializedProjectionContractAndAuthorizesFreshGap()
    {
        const string transferId = "transfer_regular_v4_pressure_inbound_unmaterialized_projection_handoff";
        var logStart = GetOperationalLogLength();
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4InboundContext(
            transferId,
            chunkCount: 1024,
            nextChunkIndex: 489,
            durableHighestChunkIndex: 488,
            creditUntilChunkIndexExclusive: 501);
        SetPrivateProperty(
            context,
            "DataSession",
            new RecordingDataSession($"session_{transferId}", transferId));
        var written = Assert.IsType<System.Collections.BitArray>(
            GetPrivatePropertyValue(context, "ReceiverSparseChunksWritten"));
        for (var chunkIndex = 489; chunkIndex < 501; chunkIndex++)
        {
            written[chunkIndex] = false;
        }
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        SeedCurrentRuntimeOwnerForTest(
            context,
            FileTransferDirection.Inbound,
            "pressure_inbound_unmaterialized_projection_handoff");

        _ = CreateInboundState(service, context, "frontier_stall_repair_due", forceMissingRange: true);
        Assert.Null(GetPrivatePropertyValue(context, "V4RegularFrontierRepairProjection"));
        var originalContract = GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract");
        Assert.NotNull(originalContract);
        var originalAuthority = GetPrivateProperty<FileTransferRegularV4FrontierRepairAuthority>(
            originalContract!,
            "Authority");
        var repairState = GetSingleInboundRepairState(context);
        var originalAttemptCount = GetPrivateProperty<int>(repairState, "AttemptCount");
        Assert.Same(
            originalContract,
            GetPrivatePropertyValue(repairState, "RegularV4RepairContract"));

        var requested = InvokeReceiveRecovery(
            service,
            new FileTransferReceiveRecoveryRequest(
                $"session_{transferId}",
                transferId,
                FileTransferDirection.Inbound,
                "session_liveness_timeout_pending"));

        Assert.True(requested);
        Assert.Null(GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract"));
        Assert.Null(GetPrivatePropertyValue(repairState, "RegularV4RepairContract"));
        Assert.Equal(originalAttemptCount, GetPrivateProperty<int>(repairState, "AttemptCount"));
        Assert.Equal("489:12:489:12", GetPrivateProperty<string>(repairState, "RepairRequestKey"));
        var recoveryRequest = Assert.Single(transport.ReceiveRecoveryRequests);
        var executionCommand =
            Assert.IsType<FileTransferBridgeRecoveryExecutionCommand>(
                recoveryRequest.BridgeRecoveryExecutionCommand);
        var executionReceipt = transport.CompleteBridgeRecoveryExecution(
            executionCommand,
            FileTransferBridgeRecoveryExecutionOutcome.Completed,
            "unit_test_unmaterialized_projection_recovery_completed");
        Assert.True(
            SpinWait.SpinUntil(
                () => transport.BridgeRecoveryExecutionReceiptDecisions.Any(
                    decision =>
                        decision.Receipt == executionReceipt &&
                        decision.Accepted),
                TimeSpan.FromSeconds(2)));

        SetPrivateProperty(context, "PullHighestReceivedChunkIndex", 581);
        var reboundState = CreateInboundState(
            service,
            context,
            "frontier_stall_repair_due",
            forceMissingRange: true);
        var freshContract = GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract");
        Assert.True(freshContract is not null, ReadOperationalLogTail(logStart));
        var freshAuthority = GetPrivateProperty<FileTransferRegularV4FrontierRepairAuthority>(
            freshContract!,
            "Authority");
        Assert.NotSame(originalContract, freshContract);
        Assert.NotEqual(originalAuthority.AuthorityId, freshAuthority.AuthorityId);
        Assert.Null(freshAuthority.PredecessorAuthorityId);
        Assert.Equal(
            executionCommand.Decision.BridgeDecisionGeneration,
            freshAuthority.BridgeDecisionGeneration);
        Assert.Equal("489:12:489:12", freshAuthority.RepairKey);
        var missingRange = Assert.Single(reboundState.MissingRanges);
        Assert.Equal(489, missingRange.StartChunkIndex);
        Assert.Equal(12, missingRange.ChunkCount);
        Assert.Equal(501, reboundState.CreditUntilChunkIndexExclusive);

        _ = CreateInboundState(service, context, "chunk_batch_committed");
        Assert.Same(
            freshContract,
            GetPrivatePropertyValue(context, "CurrentRegularV4FrontierRepairContract"));

        var log = ReadOperationalLogTail(logStart);
        Assert.Contains(
            "event=filetransfer_regular_v4_frontier_repair_pressure_recovery_handoff_contract_detached;",
            log,
            StringComparison.Ordinal);
        Assert.Contains("detached_repair_state_count=1", log, StringComparison.Ordinal);
        Assert.Contains("decision_reason=coordinator_recovery_contract_detached", log, StringComparison.Ordinal);
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
        Assert.NotNull(recoveryRequest.BridgeRecoveryExecutionCommand);
        Assert.NotNull(
            recoveryRequest.BridgeRecoveryExecutionCommand
                ?.RecoveryLivenessLease);
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
        var repairState = GetSingleInboundRepairState(context);
        Assert.Same(contract, GetPrivatePropertyValue(repairState, "RegularV4RepairContract"));
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
        Assert.Same(contract, GetPrivatePropertyValue(repairState, "RegularV4RepairContract"));
        Assert.Empty(transport.ReceiveRecoveryRequests);
        Assert.Null(GetReceiveRecoverySubstate(context).CurrentOperation);
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
        var repairState = GetSingleInboundRepairState(context);
        Assert.Same(contract, GetPrivatePropertyValue(repairState, "RegularV4RepairContract"));
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
        Assert.Same(contract, GetPrivatePropertyValue(repairState, "RegularV4RepairContract"));
        Assert.Empty(transport.ReceiveRecoveryRequests);
        Assert.Null(GetReceiveRecoverySubstate(context).CurrentOperation);
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
        var repairState = GetSingleInboundRepairState(context);
        Assert.Same(contract, GetPrivatePropertyValue(repairState, "RegularV4RepairContract"));
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
        Assert.Same(contract, GetPrivatePropertyValue(repairState, "RegularV4RepairContract"));
        Assert.Empty(recoveryOnlyTransport.ReceiveRecoveryRequests);
        Assert.Null(GetReceiveRecoverySubstate(context).CurrentOperation);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "RegularV4FrontierRepairProjection")]
    public void RegularV4FrontierRepairPressure_ControllerFailureInvalidatesIssuedRecoveryWithoutReapplyingPressure()
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
        var receiveRecovery = GetReceiveRecoverySubstate(context);
        Assert.Null(receiveRecovery.CurrentOperation);
        Assert.Equal(
            FileTransferReceiveRecoveryExecutionState.Invalidated,
            Assert.IsType<FileTransferReceiveRecoveryOperation>(
                receiveRecovery.LastTerminalOperation).ExecutionState);
        Assert.Empty(transport.ReceiveRecoveryRequests);
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains(
            "event=filetransfer_receive_recovery_operation_invalidation_decided;",
            log,
            StringComparison.Ordinal);
        Assert.Contains(
            "invalidation_reason=bridge_recovery_execution_begin_InvalidOperationException",
            log,
            StringComparison.Ordinal);
        Assert.Contains("decision_status=Accepted", log, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "event=filetransfer_receive_recovery_operation_execution_receipt_decision_observed;",
            log,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Inbound, true)]
    [InlineData(FileTransferDirection.Outbound, true)]
    [Trait("FileTransferRuntimeSuite", "ReceiveRecoveryOperation")]
    public void FileTransferReceiveRecoveryOperation_CompletedHealthEvaluationDispatchesOneExactSuccessor(
        FileTransferDirection direction,
        bool receiveProofObserved)
    {
        var transferId =
            $"transfer_completed_unproven_successor_{direction.ToString().ToLowerInvariant()}";
        using var service = new SessionFileTransferService();
        using var transport =
            new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(transport);
        object context;
        string contextField;
        if (direction == FileTransferDirection.Inbound)
        {
            context = CreateProjectionInboundContext(transferId);
            contextField = "inboundTransfer";
        }
        else
        {
            context = CreateRegularNknV4OutboundContext(
                transferId,
                remoteNextExpectedChunkIndex: 100,
                chunksAcceptedForTransport: 256,
                remoteGrantedUntilExclusive: 256,
                lastGrantReceivedUtc:
                    DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10));
            contextField = "outboundTransfer";
        }

        typeof(SessionFileTransferService)
            .GetField(
                contextField,
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        SeedCurrentRuntimeOwnerForTest(
            context,
            direction,
            "completed_unproven_successor");

        Assert.True(
            InvokeReceiveRecovery(
                service,
                new FileTransferReceiveRecoveryRequest(
                    $"session_{transferId}",
                    transferId,
                    direction,
                    "session_liveness_timeout_pending")));
        var predecessorCommand =
            Assert.Single(transport.BridgeRecoveryExecutionCommands);
        var predecessorReceipt = transport.CompleteBridgeRecoveryExecution(
            predecessorCommand,
            FileTransferBridgeRecoveryExecutionOutcome.Completed,
            "unit_test_completed_unproven_predecessor");
        Assert.True(
            SpinWait.SpinUntil(
                () => GetReceiveRecoverySubstate(context).CurrentOperation is
                {
                    ExecutionState:
                        FileTransferReceiveRecoveryExecutionState.Completed,
                    ClosureState:
                        FileTransferReceiveRecoveryClosureState.Retained,
                },
                TimeSpan.FromSeconds(2)));
        var predecessor = Assert.IsType<FileTransferReceiveRecoveryOperation>(
            GetReceiveRecoverySubstate(context).CurrentOperation);
        Assert.Equal(predecessorReceipt, predecessor.ExecutionReceipt);
        if (receiveProofObserved)
        {
            transport.RaiseReceiveRecoveryProof(
                predecessorCommand,
                "completed-proven-successor-proof");
            Assert.True(
                SpinWait.SpinUntil(
                    () => GetReceiveRecoverySubstate(context)
                        .CurrentOperation?.ClosureState ==
                        FileTransferReceiveRecoveryClosureState.Adopted,
                    TimeSpan.FromSeconds(2)));
            predecessor = Assert.IsType<FileTransferReceiveRecoveryOperation>(
                GetReceiveRecoverySubstate(context).CurrentOperation);
        }

        var successorBeginCount = 0;
        transport.BridgeRecoveryExecutionBeginObserverForTests =
            _ => Interlocked.Increment(ref successorBeginCount);
        var observedUtcMs = Math.Max(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Math.Max(
                predecessor.ExecutionReceiptObservedUtcMs,
                predecessor.ReceiveProof?.ObservedUtcMs ?? 0) + 1);
        var evaluation = new FileTransferBridgeRecoveryEvaluationRequest(
            $"session_{transferId}",
            transferId,
            direction,
            receiveProofObserved
                ? FileTransferBridgeRecoveryEvaluationKind
                    .SessionLivenessTimeout
                : FileTransferBridgeRecoveryEvaluationKind.PingTimeout,
            receiveProofObserved
                ? "session_liveness_timeout"
                : "bridge_ping_timeout",
            receiveProofObserved
                ? "session_liveness_timeout_pending"
                : "bridge_ping_timeout",
            ObservationGeneration: 1,
            ObservedUtcMs: observedUtcMs,
            ConsecutiveObservations: 1);

        if (receiveProofObserved)
        {
            Assert.True(
                service.RequestFileTransferReceiveRecovery(
                    new FileTransferReceiveRecoveryRequest(
                        $"session_{transferId}",
                        transferId,
                        direction,
                        "session_liveness_timeout_pending")
                    {
                        HealthEvaluationRequest = evaluation,
                    }));
        }
        else
        {
            transport.RaiseBridgeRecoveryEvaluation(evaluation);
        }

        Assert.True(
            SpinWait.SpinUntil(
                () =>
                    transport.BridgeRecoveryExecutionCommands.Count == 2 &&
                    Volatile.Read(ref successorBeginCount) == 1,
                TimeSpan.FromSeconds(2)));
        var successor = Assert.IsType<FileTransferReceiveRecoveryOperation>(
            GetReceiveRecoverySubstate(context).CurrentOperation);
        Assert.Equal(
            predecessor.Authority.AuthorityId,
            successor.Authority.PredecessorAuthorityId);
        Assert.Equal(
            predecessor.Authority.OperationGeneration,
            successor.Authority.PredecessorOperationGeneration);
        Assert.Equal(
            predecessor.Authority.OperationGeneration + 1,
            successor.Authority.OperationGeneration);
        var superseded =
            Assert.IsType<FileTransferReceiveRecoveryOperation>(
                GetReceiveRecoverySubstate(context).LastTerminalOperation);
        Assert.Equal(
            FileTransferReceiveRecoveryExecutionState.Invalidated,
            superseded.ExecutionState);
        Assert.Equal(
            FileTransferReceiveRecoveryClosureState.None,
            superseded.ClosureState);
        Assert.Equal(predecessorReceipt, superseded.ExecutionReceipt);

        if (receiveProofObserved)
        {
            Assert.False(
                service.RequestFileTransferReceiveRecovery(
                    new FileTransferReceiveRecoveryRequest(
                        $"session_{transferId}",
                        transferId,
                        direction,
                        "session_liveness_timeout_pending")
                    {
                        HealthEvaluationRequest = evaluation,
                    }));
        }
        else
        {
            transport.RaiseBridgeRecoveryEvaluation(evaluation);
        }

        Assert.False(
            SpinWait.SpinUntil(
                () =>
                    transport.BridgeRecoveryExecutionCommands.Count > 2 ||
                    Volatile.Read(ref successorBeginCount) > 1,
                TimeSpan.FromMilliseconds(250)));
        Assert.Equal(2, transport.BridgeRecoveryExecutionCommands.Count);
        Assert.Equal(1, Volatile.Read(ref successorBeginCount));
    }

    private static bool PrepareInboundRegularV4ToTunaRouteTransitionForPressureTest(
        SessionFileTransferService service,
        object context,
        string reason)
    {
        var serviceGate = typeof(SessionFileTransferService)
            .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        lock (serviceGate)
        {
            var prepared = Assert.IsType<bool>(
                typeof(SessionFileTransferService)
                    .GetMethod(
                        "TryPromoteInboundRegularNknV4ToFileTunaV4Locked",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(
                        service,
                        [
                            context,
                            reason,
                            FileTransferTransportHandoffKind.NormalToTunaActivation,
                            FileTransferTransportKind.Tuna,
                        ]));
            if (prepared &&
                GetFileTransferRouteTransitionSubstate(context)
                    .CurrentOperation is not null)
            {
                StabilizeFileTransferRouteTransitionDrainForTest(context);
            }

            return prepared;
        }
    }

    private static (bool Accepted, string Reason)
        CommitInboundFileTransferRouteTransitionForPressureTest(
            SessionFileTransferService service,
            object context)
    {
        var execution = Assert.IsAssignableFrom<object>(
            GetCurrentFileTransferRouteTransitionExecution(context));
        object?[] arguments = [context, execution, null, null];
        var serviceGate = typeof(SessionFileTransferService)
            .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        bool accepted;
        lock (serviceGate)
        {
            accepted = Assert.IsType<bool>(
                typeof(SessionFileTransferService)
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .Single(candidate =>
                    {
                        if (!string.Equals(
                                candidate.Name,
                                "TryCommitInboundFileTransferRouteTransitionLocked",
                                StringComparison.Ordinal))
                        {
                            return false;
                        }

                        var parameters = candidate.GetParameters();
                        return parameters.Length == 4 &&
                               parameters[0].ParameterType == context.GetType();
                    })
                    .Invoke(service, arguments));
        }

        return (
            accepted,
            arguments[3] as string ??
            (accepted ? "none" : "filetransfer_route_transition_rejected"));
    }

    private static bool InvokeReceiveRecovery(
        SessionFileTransferService service,
        FileTransferReceiveRecoveryRequest request)
    {
        var serviceType = typeof(SessionFileTransferService);
        var context = serviceType
            .GetField(
                request.Direction == FileTransferDirection.Inbound
                    ? "inboundTransfer"
                    : "outboundTransfer",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service);
        Assert.NotNull(context);
        Assert.NotNull(GetPrivatePropertyValue(context, "DataSession"));
        if (GetPrivateProperty<long>(context, "DataSessionGeneration") <= 0)
        {
            // These reflection fixtures install their data session directly.
            // Mirror the production installation boundary before capturing the
            // exact coordinator observation.
            SetPrivateProperty(context, "DataSessionGeneration", 1L);
        }
        var observation = Assert.IsType<FileTransferReceiveRecoveryObservationSnapshot>(
            serviceType
                .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(candidate =>
                {
                    if (!string.Equals(
                            candidate.Name,
                            "CreateReceiveRecoveryObservationSnapshotLocked",
                            StringComparison.Ordinal))
                    {
                        return false;
                    }

                    var parameters = candidate.GetParameters();
                    return parameters.Length == 7 &&
                           parameters[0].ParameterType == context.GetType();
                })
                .Invoke(
                    null,
                    [
                        context,
                        request.Direction,
                        "unit_test_receive_recovery",
                        request.Reason,
                        null,
                        null,
                        null,
                    ]));
        var exactRequest = request with
        {
            Observation = observation,
        };
        Assert.Equal(observation, exactRequest.Observation);
        return Assert.IsType<bool>(
            serviceType
                .GetMethod(
                    "TryRequestFileTransferReceiveRecoveryIfCurrent",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [exactRequest, null, null, false]));
    }

    private static FileTransferReceiveRecoverySubstate GetReceiveRecoverySubstate(
        object context)
        => GetPrivateProperty<FileTransferCoordinatorPersistentState>(
            context,
            "CoordinatorPersistentState").FileTransferReceiveRecovery;

    private static object GetSingleInboundRepairState(object context)
    {
        var requests = GetPrivatePropertyValue(context, "V4ReceiverRepairRequests");
        Assert.NotNull(requests);
        var values = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            requests!.GetType().GetProperty("Values")!.GetValue(requests));
        return Assert.Single(values.Cast<object>());
    }

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
