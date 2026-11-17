using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(FileTransferDirection.Outbound, false)]
    [InlineData(FileTransferDirection.Inbound, false)]
    [InlineData(FileTransferDirection.Outbound, true)]
    [InlineData(FileTransferDirection.Inbound, true)]
    [Trait("FileTransferCoordinatorSuite", "InitialTunaFrontierRepair")]
    [Trait("FileTransferCoordinatorSuite", "RegularV4FrontierRepairPressure")]
    public void InitialTunaFrontierRepair_StagesAndInstallsExactInitialPublication(
        FileTransferDirection direction, bool independentGenerations)
        => VerifyInitialTunaFrontierRepairLifecycle(direction, independentGenerations);

    internal static FileTransferCoordinatorTransitionEnvelope[] CreateInitialTunaRepairSettlementSnapshotFixture(
        FileTransferDirection direction)
    {
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        VerifyInitialTunaFrontierRepairLifecycle(direction, false, envelopes);
        for (var index = 1; index < envelopes.Count; index++)
        {
            Assert.Equal(envelopes[index - 1].Aggregate.AfterStateGeneration, envelopes[index].Aggregate.BeforeStateGeneration);
            Assert.Equal(envelopes[index - 1].Aggregate.AfterStateFingerprint, envelopes[index].Aggregate.BeforeStateFingerprint);
        }
        return envelopes.ToArray();
    }

    private static void VerifyInitialTunaFrontierRepairLifecycle(FileTransferDirection direction,
        bool independentGenerations, List<FileTransferCoordinatorTransitionEnvelope>? envelopes = null)
    {
        var source = CreateInitialTunaBootstrapSourceFixture(direction, independentGenerations, envelopes);
        Action<FileTransferCoordinatorTransitionEnvelope>? capture = envelopes is null ? null : envelopes.Add;
        var (state, repair) = AuthorizeRegularV4FrontierRepair(source, capture: capture);
        Assert.True(FileTransferCoordinator.IsRepairReplayDecisionCurrent(state, repair, out _));
        Assert.Null(state.CurrentRouteCommit);
        var request = new RegularV4FrontierRepairAuthorityRequest(
            "1071:3:1071:3", FrontierChunkIndex: 1071,
            DurableReceivedHighestChunkIndex: 3120, PendingRepairCount: 3,
            DecisionReason: "v4_repair_schedule");

        var staged = FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding(
            state, repair, request, "v4_repair_schedule");

        Assert.True(staged.Status == RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted,
            staged.Reason);
        envelopes?.Add(staged.SnapshotEnvelope!);
        var binding = Assert.IsType<RegularV4FrontierRepairPressureBinding>(staged.Binding);
        var authority = binding.Authority;
        Assert.Equal(repair.AuthorityId, authority.AuthorityId);
        Assert.Equal(direction, authority.Direction);
        Assert.Equal("file_tuna_v4", authority.RouteToken);
        Assert.Equal(4, authority.ProtocolVersion);
        Assert.Equal(FileTransferTransportKind.Tuna, authority.TargetTransport);
        Assert.Equal(0, authority.LiveRouteEpoch);
        Assert.Equal(1, authority.RoutePublicationGeneration);
        Assert.Null(authority.RouteCommitGeneration);
        Assert.Equal(independentGenerations ? 7 : 1, authority.RuntimeGeneration);
        Assert.Equal(independentGenerations ? 13 : 1, authority.LegGeneration);
        Assert.Equal(3, authority.PendingRepairCount);
        Assert.Equal(authority, staged.InstallCommand!.Pressure.RepairAuthority);
        Assert.Null(PressureState(staged.State).InstalledBinding);

        var repeated = FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding(
            staged.State, repair, request, "v4_repair_schedule");
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Idempotent, repeated.Status);
        Assert.Equal(binding, repeated.Binding);

        var installed = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(
            staged.State, new FileTransferRegularV4FrontierRepairPressureInstallReceipt(
                authority, FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed,
                "installed", binding.InstallGeneration, binding.PressureReason));
        Assert.True(installed.Status == RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted,
            installed.Reason);
        envelopes?.Add(installed.SnapshotEnvelope!);
        Assert.Equal(authority, PressureState(installed.State).InstalledBinding!.Value.Authority);
        Assert.Null(PressureState(installed.State).PendingBinding);
        Assert.Equal(source.CurrentRoutePublicationToken, installed.State.CurrentRoutePublicationToken);
        Assert.Null(installed.State.CurrentRouteCommit);
        Assert.True(FileTransferCoordinator.CanStartFallbackBootstrapExchange(installed.State));

        var prepared = FileTransferCoordinator.PrepareRegularV4FrontierRepairPressureSettlement(
            installed.State, RegularV4PressureSettlement(authority,
                FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved));
        Assert.True(prepared.Status == RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted,
            prepared.Reason);
        envelopes?.Add(prepared.SnapshotEnvelope!);
        var receipt = new FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt(
            prepared.SettlementCommand!, FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
            "regular_v4_frontier_repair_pressure_settlement_applied",
            CurrentAuthorityCleared: true, RetainedPredecessorCleared: false);
        foreach (var staleState in new[]
        {
            prepared.State with { LastRouteCommitGeneration = 1 },
            prepared.State with { LastRoutePublicationGeneration = 2 },
            prepared.State with { LastLiveRouteEpochId = 1 },
            prepared.State with { CurrentRoutePublicationToken = null },
        })
        {
            var rejected = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureSettlement(staleState, receipt);
            Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected, rejected.Status);
            Assert.Equal(PressureState(staleState), PressureState(rejected.State));
            Assert.Equal(staleState.CurrentRepairReplayDecision, rejected.State.CurrentRepairReplayDecision);
            Assert.Null(rejected.State.PersistentState.RuntimeUnlockRetryLifecycle.RepairSettlementRelease);
        }
        var settled = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureSettlement(prepared.State, receipt);
        Assert.True(settled.Status == RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted,
            settled.Reason);
        envelopes?.Add(settled.SnapshotEnvelope!);
        Assert.Null(PressureState(settled.State).InstalledBinding);
        Assert.Null(settled.State.CurrentRepairReplayDecision);
        Assert.Null(settled.State.PersistentState.RuntimeUnlockRetryLifecycle.RepairSettlementRelease);
        Assert.Equal(source.CurrentRoutePublicationToken, settled.State.CurrentRoutePublicationToken);
    }

    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    [Trait("FileTransferCoordinatorSuite", "InitialTunaFrontierRepair")]
    public void InitialTunaFrontierRepair_RejectsRetiredOrAlteredScope(FileTransferDirection direction)
    {
        var (state, repair) = AuthorizeRegularV4FrontierRepair(
            CreateInitialTunaBootstrapSourceFixture(direction, false));
        var owner = state.CurrentRuntimeOwner!.Value;
        var leg = state.CurrentLeg!;
        var publication = state.CurrentRoutePublicationToken!.Value;
        foreach (var invalid in new[]
        {
            state with { IsTerminal = true },
            state with { TerminalGeneration = 1 },
            state with { SessionId = "other-session" },
            state with { TransferId = "other-transfer" },
            state with { Direction = direction == FileTransferDirection.Inbound
                ? FileTransferDirection.Outbound : FileTransferDirection.Inbound },
            state with { CurrentRuntimeOwner = null },
            state with { CurrentRuntimeOwner = owner with { RuntimeGeneration = owner.RuntimeGeneration + 1 } },
            state with { LastRuntimeOwnerGeneration = owner.RuntimeGeneration + 1 },
            state with { CurrentLeg = null },
            state with { CurrentLeg = new FileTransferLeg
            {
                LegId = leg.LegId, Generation = leg.Generation,
                RouteSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast),
                ProtocolVersion = leg.ProtocolVersion, LiveRouteEpochId = leg.LiveRouteEpochId,
                TransportEpochId = leg.TransportEpochId, StartedUtc = leg.StartedUtc,
            } },
            state with { CurrentRoutePublicationToken = null },
            state with { CurrentRoutePublicationToken = publication with { PublicationGeneration = 2 } },
            state with { LastRoutePublicationGeneration = 2 },
            state with { LastRouteCommitGeneration = 1 },
            state with { LastLiveRouteEpochId = 1 },
            state with { CurrentRouteCommit = default(RouteCommitToken) },
            state with { RouteSelection = FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6) },
            state with { CurrentRepairReplayDecision = null },
        })
        {
            AssertInitialTunaFrontierRepairRejected(invalid, repair);
        }

        foreach (var altered in new[]
        {
            repair with { Accepted = false },
            repair with { RepairGeneration = repair.RepairGeneration + 1 },
            repair with { CurrentLegGeneration = repair.CurrentLegGeneration + 1 },
            repair with { TransportEpoch = repair.TransportEpoch + 1 },
            repair with { BridgeRecoveryGeneration = repair.BridgeRecoveryGeneration + 1 },
            repair with { Reason = repair.Reason + "_altered" },
            repair with { RepairKind = RepairReplayKind.PeerSilenceReplay },
            repair with { BridgeDecisionGeneration = repair.BridgeDecisionGeneration + 1 },
            repair with { RoutePublicationToken = publication with { RouteCommitGeneration = 1 } },
            repair with { RoutePublicationToken = publication with { PublicationGeneration = 2 } },
        })
        {
            Assert.Equal(repair.AuthorityId, altered.AuthorityId);
            AssertInitialTunaFrontierRepairRejected(state, altered);
        }
    }

    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    [Trait("FileTransferCoordinatorSuite", "InitialTunaFrontierRepair")]
    public void InitialTunaFrontierRepair_RejectsInternallyConsistentNonInitialPublication(
        FileTransferDirection direction)
    {
        var (state, repair) = AuthorizeRegularV4FrontierRepair(
            CreateInitialTunaBootstrapSourceFixture(direction, false));
        var publication = state.CurrentRoutePublicationToken!.Value;
        foreach (var invalidPublication in new[]
        {
            publication with { PublicationGeneration = 2 },
            publication with { ActionKind = FileTransferCoordinatorActionKind.RouteCommitPublication },
            publication with { RouteCommitGeneration = 0 },
            publication with { TargetTransport = FileTransferTransportKind.RegularNkn },
        })
        {
            var invalidRepair = repair with { RoutePublicationToken = invalidPublication };
            var invalidState = state with
            {
                CurrentRoutePublicationToken = invalidPublication,
                LastRoutePublicationGeneration = invalidPublication.PublicationGeneration,
                CurrentRepairReplayDecision = invalidRepair,
            };
            AssertInitialTunaFrontierRepairRejected(invalidState, invalidRepair);
        }
    }

    private static void AssertInitialTunaFrontierRepairRejected(
        FileTransferCoordinatorState state, RepairReplayDecision repair)
    {
        var request = RegularV4PressureAuthorityRequest("initial-tuna-invalid");
        Assert.False(FileTransferCoordinator.TryIssueRegularV4FrontierRepairAuthority(
            state, repair, request, out var authority, out _));
        Assert.Null(authority);
        var rejected = FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding(
            state, repair, request, "v4_repair_schedule");
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected, rejected.Status);
        Assert.Null(rejected.Binding);
        Assert.Null(rejected.InstallCommand);
        Assert.Equal(PressureState(state), PressureState(rejected.State));
        Assert.Equal(state.CurrentRoutePublicationToken, rejected.State.CurrentRoutePublicationToken);
        Assert.Equal(state.CurrentRepairReplayDecision, rejected.State.CurrentRepairReplayDecision);
        Assert.Equal(state.LastRepairReplayGeneration, rejected.State.LastRepairReplayGeneration);
    }
}
