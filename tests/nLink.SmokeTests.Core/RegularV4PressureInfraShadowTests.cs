using NLink.Core.FileTransfer;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class RegularV4PressureInfraShadowTests
{
    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "RegularV4FrontierRepairPressure")]
    [Fact]
    public void RegularV4Pressure_InvalidCommandIsRejectedWithoutAuthorityReconstruction()
    {
        var command = new FileTransferRegularV4FrontierRepairPressureInstallCommand(
            new FileTransferRegularV4ControlFeedbackPressure(
                "pressure-session",
                "pressure-transfer",
                CreditExhaustedTimeMs: 0,
                FrontierLagChunks: 12,
                PendingRepairCount: 12,
                Reason: "regular_v4_frontier_repair_pressure"),
            InstallGeneration: 1);

        using var devLocal = new DevLocalTransport();
        var devLocalReceipt =
            ((IFileTransferRegularV4FrontierRepairPressureInstallBridge)devLocal)
            .InstallRegularV4FrontierRepairPressure(command);
        Assert.Equal(FileTransferRegularV4FrontierRepairPressureInstallStatus.Rejected, devLocalReceipt.Status);
        Assert.Equal("repair_authority_missing", devLocalReceipt.Reason);
        Assert.Null(devLocalReceipt.Authority);

        using var nkn = new RealNknClientAdapter(
            new NknIdentity("pressure-shadow-test", "pressure-shadow-test.invalid"),
            NknTransportOptions.Load());
        var nknReceipt = nkn.InstallRegularV4FrontierRepairPressure(command);
        Assert.Equal(FileTransferRegularV4FrontierRepairPressureInstallStatus.Rejected, nknReceipt.Status);
        Assert.Equal("repair_authority_invalid", nknReceipt.Reason);
        Assert.Null(nknReceipt.Authority);
    }

    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "RegularV4FrontierRepairPressure")]
    [Fact]
    public void Nkn_RegularV4Pressure_DuplicateDoesNotRefreshEvidenceAge()
    {
        var authority = CreateAuthority("nkn-pressure-evidence", decisionGeneration: 1);
        var command = CreateInstallCommand(authority, installGeneration: 1);
        using var adapter = new RealNknClientAdapter(
            new NknIdentity("pressure-evidence-test", "pressure-evidence-test.invalid"),
            NknTransportOptions.Load());
        adapter.RegisterActiveFileTransferDataSession(authority.TransferId);
        adapter.RegisterActiveFileTransferRuntime(authority.TransferId);
        try
        {
            Assert.Equal(
                FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed,
                adapter.InstallRegularV4FrontierRepairPressure(command).Status);
            Thread.Sleep(20);
            Assert.True(adapter.TryGetFileTransferRegularV4ActivationSendPressure(out _, out var ageBeforeDuplicateMs));
            Assert.Equal(
                FileTransferRegularV4FrontierRepairPressureInstallStatus.Idempotent,
                adapter.InstallRegularV4FrontierRepairPressure(command).Status);
            Assert.True(adapter.TryGetFileTransferRegularV4ActivationSendPressure(out _, out var ageAfterDuplicateMs));
            Assert.True(ageAfterDuplicateMs >= ageBeforeDuplicateMs);
        }
        finally
        {
            adapter.UnregisterActiveFileTransferRuntime(authority.TransferId);
            adapter.UnregisterActiveFileTransferDataSession(authority.TransferId);
        }
    }

    [Trait("Category", "LegacySmoke")]
    [Trait("FileTransferInfraSuite", "RegularV4FrontierRepairPressure")]
    [Fact]
    public void DevLocal_RegularV4Pressure_UsesExactImmutableCommandShadow()
    {
        using var transport = new DevLocalTransport();
        var bridge = Assert.IsAssignableFrom<IFileTransferRegularV4FrontierRepairPressureInstallBridge>(
            transport);
        var predecessor = CreateAuthority("devlocal-pressure-predecessor", decisionGeneration: 1);
        var predecessorCommand = CreateInstallCommand(predecessor, installGeneration: 1);

        var installed = bridge.InstallRegularV4FrontierRepairPressure(predecessorCommand);
        Assert.Equal(FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed, installed.Status);
        Assert.Equal(
            FileTransferRegularV4FrontierRepairPressureInstallStatus.Idempotent,
            bridge.InstallRegularV4FrontierRepairPressure(predecessorCommand).Status);

        var mutatedCommand = predecessorCommand with
        {
            Pressure = predecessorCommand.Pressure with
            {
                Reason = "mutated_pressure_reason",
            },
        };
        Assert.Equal(
            FileTransferRegularV4FrontierRepairPressureInstallStatus.Rejected,
            bridge.InstallRegularV4FrontierRepairPressure(mutatedCommand).Status);

        var successor = CreateAuthority(
            "devlocal-pressure-successor",
            decisionGeneration: 2) with
        {
            PredecessorAuthorityId = predecessor.AuthorityId,
        };
        var successorCommand = CreateInstallCommand(successor, installGeneration: 2);
        Assert.Equal(
            FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed,
            bridge.InstallRegularV4FrontierRepairPressure(successorCommand).Status);

        var predecessorSettlement = CreateSettlement(
            predecessor,
            FileTransferRegularV4FrontierRepairSettlementOutcome.Superseded) with
        {
            SuccessorAuthorityId = successor.AuthorityId,
        };
        var predecessorSettlementCommand =
            new FileTransferRegularV4FrontierRepairPressureSettlementCommand(
                predecessorSettlement,
                InstallGeneration: 2,
                CurrentAuthorityId: successor.AuthorityId,
                RetainedPredecessorAuthorityId: predecessor.AuthorityId);
        var rejectedReceipt = bridge.ApplyRegularV4FrontierRepairPressureSettlement(
            predecessorSettlementCommand with { InstallGeneration = 3 });
        Assert.Equal(
            FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Rejected,
            rejectedReceipt.Status);

        var predecessorReceipt =
            bridge.ApplyRegularV4FrontierRepairPressureSettlement(predecessorSettlementCommand);
        Assert.Equal(
            FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
            predecessorReceipt.Status);
        Assert.False(predecessorReceipt.CurrentAuthorityCleared);
        Assert.True(predecessorReceipt.RetainedPredecessorCleared);

        var terminalSettlement = CreateSettlement(
            successor,
            FileTransferRegularV4FrontierRepairSettlementOutcome.TransferTerminal);
        var terminalCommand =
            new FileTransferRegularV4FrontierRepairPressureSettlementCommand(
                terminalSettlement,
                InstallGeneration: 2,
                CurrentAuthorityId: successor.AuthorityId,
                RetainedPredecessorAuthorityId: null);
        var terminalReceipt = bridge.ApplyRegularV4FrontierRepairPressureSettlement(terminalCommand);
        Assert.Equal(
            FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied,
            terminalReceipt.Status);
        Assert.True(terminalReceipt.CurrentAuthorityCleared);
        Assert.Equal(
            FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent,
            bridge.ApplyRegularV4FrontierRepairPressureSettlement(terminalCommand).Status);
    }

    private static FileTransferRegularV4FrontierRepairPressureInstallCommand CreateInstallCommand(
        FileTransferRegularV4FrontierRepairAuthority authority,
        long installGeneration)
        => new(
            new FileTransferRegularV4ControlFeedbackPressure(
                authority.SessionId,
                authority.TransferId,
                CreditExhaustedTimeMs: 0,
                FrontierLagChunks: authority.PendingRepairCount,
                PendingRepairCount: authority.PendingRepairCount,
                Reason: "regular_v4_frontier_repair_pressure",
                authority),
            installGeneration);

    private static FileTransferRegularV4FrontierRepairAuthority CreateAuthority(
        string authoritySuffix,
        int decisionGeneration)
        => new(
            SessionId: "devlocal-pressure-session",
            TransferId: "devlocal-pressure-transfer",
            Direction: FileTransferDirection.Outbound,
            RouteToken: "regular_nkn_v4_fast",
            ProtocolVersion: FileTransferProtocol.ProtocolVersionV4,
            LiveRouteEpoch: 1,
            RuntimeGeneration: 1,
            TerminalGeneration: 0,
            LegGeneration: 1,
            TransportEpoch: 1,
            BridgeRecoveryGeneration: 1,
            BridgeDecisionGeneration: 1,
            RoutePublicationGeneration: 1,
            RouteCommitGeneration: null,
            RepairKey: $"devlocal-pressure:{decisionGeneration}:0:12",
            FrontierChunkIndex: 0,
            DurableReceivedHighestChunkIndex: -1,
            PendingRepairCount: 12,
            DecisionGeneration: decisionGeneration,
            DecisionReason: "test_regular_v4_pressure",
            TargetTransport: FileTransferTransportKind.RegularNkn)
        {
            AuthorityId = new FileTransferAuthorityId($"fta2-{authoritySuffix}"),
        };

    private static FileTransferRegularV4FrontierRepairSettlement CreateSettlement(
        FileTransferRegularV4FrontierRepairAuthority authority,
        FileTransferRegularV4FrontierRepairSettlementOutcome outcome)
        => new(
            authority.SessionId,
            authority.TransferId,
            authority.Direction,
            authority.RouteToken,
            authority.ProtocolVersion,
            authority.LiveRouteEpoch,
            authority.RuntimeGeneration,
            authority.TerminalGeneration,
            authority.LegGeneration,
            authority.TransportEpoch,
            authority.BridgeRecoveryGeneration,
            authority.BridgeDecisionGeneration,
            authority.RoutePublicationGeneration,
            authority.RouteCommitGeneration,
            authority.RepairKey,
            authority.FrontierChunkIndex,
            authority.DurableReceivedHighestChunkIndex,
            authority.PendingRepairCount,
            authority.DecisionGeneration,
            outcome.ToString(),
            outcome,
            authority.TargetTransport)
        {
            AuthorityId = authority.AuthorityId,
        };
}
