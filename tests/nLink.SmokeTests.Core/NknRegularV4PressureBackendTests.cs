using System.Collections;
using System.Reflection;
using NLink.Core;
using NLink.Core.FileTransfer;
using NLink.Core.SessionConnect;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "RegularV4PressureBackend")]
    public Task NknPressureBackend_UnsupportedOrInactiveCannotCommit(bool supplyBackend)
        => WithPressureBackendAsync(supplyBackend, (host, _, _, sessionId, _) =>
        {
            var staged = StageNknPressure(sessionId);
            var bridge = (IFileTransferRegularV4FrontierRepairPressureInstallBridge)host;
            var receipt = bridge.InstallRegularV4FrontierRepairPressure(staged.InstallCommand!);
            Assert.Equal(FileTransferRegularV4FrontierRepairPressureInstallStatus.Rejected, receipt.Status);
            Assert.Equal(supplyBackend ? "current_transfer_not_active" :
                "regular_v4_frontier_repair_pressure_install_unsupported", receipt.Reason);
            var committed = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(staged.State, receipt);
            Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected, committed.Status);
            Assert.Equal(staged.Binding, PressureState(committed.State).PendingBinding);
            Assert.Null(PressureState(committed.State).InstalledBinding);
            if (!supplyBackend)
            {
                var authority = staged.Binding!.Value.Authority;
                var settlement = new FileTransferRegularV4FrontierRepairPressureSettlementCommand(
                    RegularV4PressureSettlement(authority, FileTransferRegularV4FrontierRepairSettlementOutcome.TransferTerminal),
                    1, authority.AuthorityId, null);
                Assert.Equal("regular_v4_frontier_repair_pressure_settlement_apply_unsupported",
                    bridge.ApplyRegularV4FrontierRepairPressureSettlement(settlement).Reason);
            }
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved)]
    [InlineData(FileTransferRegularV4FrontierRepairSettlementOutcome.TransferTerminal)]
    [Trait("FileTransferInfraSuite", "RegularV4PressureBackend")]
    public Task NknPressureBackend_RealReceiptsPreserveExactSuccessorOwnership(
        FileTransferRegularV4FrontierRepairSettlementOutcome outcome)
        => WithPressureBackendAsync(true, async (host, unusedPeer, adapter, sessionId, ct) =>
        {
            using var session = await host.OpenFileTransferDataSessionAsync(sessionId, "pressure-backend", ct);
            Assert.Same(session, await host.OpenFileTransferDataSessionAsync(sessionId, session.TransferId, ct));
            AssertPressureBackendActivity(adapter, dataSessions: 1, runtimes: 0);
            var staged = StageNknPressure(sessionId);
            var command = staged.InstallCommand!;
            var bridge = (IFileTransferRegularV4FrontierRepairPressureInstallBridge)host;
            var receipt = bridge.InstallRegularV4FrontierRepairPressure(command);
            Assert.Equal(FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed, receipt.Status);
            var installed = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(staged.State, receipt);
            Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted, installed.Status);
            Assert.Null(PressureState(installed.State).PendingBinding);
            Assert.Equal(FileTransferRegularV4FrontierRepairPressureInstallStatus.Idempotent,
                bridge.InstallRegularV4FrontierRepairPressure(command).Status);
            host.ObserveRegularV4ControlFeedbackPressure(command.Pressure);
            Assert.True(adapter.TryGetFileTransferRegularV4ActivationSendPressure(out _, out _));
            foreach (var malformed in new[]
            {
                command with { InstallGeneration = command.InstallGeneration + 1 },
                command with { Pressure = command.Pressure with { Reason = "mutated" } },
                command with { Pressure = command.Pressure with { RepairAuthority = command.Pressure.RepairAuthority! with
                    { PredecessorAuthorityId = new FileTransferAuthorityId("fta2-wrong-predecessor") } } },
            })
            {
                var rejected = bridge.InstallRegularV4FrontierRepairPressure(malformed);
                Assert.Equal(FileTransferRegularV4FrontierRepairPressureInstallStatus.Rejected, rejected.Status);
                var decision = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(installed.State, rejected);
                Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected, decision.Status);
                Assert.Equal(PressureState(installed.State).InstalledBinding, PressureState(decision.State).InstalledBinding);
            }

            var predecessor = staged.Binding!.Value.Authority;
            var authorized = AuthorizeRegularV4FrontierRepair(installed.State, predecessor.AuthorityId);
            var successor = FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding(
                authorized.State, authorized.Decision, RegularV4PressureAuthorityRequest("pressure-backend-successor"),
                "regular_v4_frontier_repair_successor_pressure");
            var successorReceipt = bridge.InstallRegularV4FrontierRepairPressure(successor.InstallCommand!);
            Assert.Equal(FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed, successorReceipt.Status);
            var current = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(successor.State, successorReceipt);
            Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted, current.Status);
            var currentAuthority = successor.Binding!.Value.Authority;
            var prepared = FileTransferCoordinator.PrepareRegularV4FrontierRepairPressureSettlement(current.State,
                RegularV4PressureSettlement(predecessor, FileTransferRegularV4FrontierRepairSettlementOutcome.Superseded,
                    currentAuthority.AuthorityId));
            var settlement = prepared.SettlementCommand!;
            Assert.NotNull(settlement);
            host.ObserveRegularV4FrontierRepairSettlement(settlement.Settlement);
            Assert.False(adapter.IsRegularV4FrontierRepairSettlementApplied(settlement.Settlement));
            foreach (var malformed in new[]
            {
                settlement with { InstallGeneration = settlement.InstallGeneration + 1 },
                settlement with { CurrentAuthorityId = predecessor.AuthorityId },
                settlement with { RetainedPredecessorAuthorityId = null },
                settlement with { Settlement = settlement.Settlement with { DecisionGeneration = 99 } },
            })
            {
                var rejected = bridge.ApplyRegularV4FrontierRepairPressureSettlement(malformed);
                Assert.Equal(FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Rejected, rejected.Status);
                Assert.False(rejected.CurrentAuthorityCleared);
                Assert.False(rejected.RetainedPredecessorCleared);
                var decision = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureSettlement(prepared.State, rejected);
                Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected, decision.Status);
                Assert.Equal(PressureState(current.State).InstalledBinding, PressureState(decision.State).InstalledBinding);
                Assert.Equal(PressureState(current.State).RetainedPredecessorBinding, PressureState(decision.State).RetainedPredecessorBinding);
            }
            var applied = bridge.ApplyRegularV4FrontierRepairPressureSettlement(settlement);
            Assert.Equal(FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied, applied.Status);
            Assert.False(applied.CurrentAuthorityCleared);
            Assert.True(applied.RetainedPredecessorCleared);
            var settled = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureSettlement(prepared.State, applied);
            Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted, settled.Status);
            Assert.Equal(currentAuthority, PressureState(settled.State).InstalledBinding!.Value.Authority);
            Assert.Null(PressureState(settled.State).RetainedPredecessorBinding);
            Assert.Equal(FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent,
                bridge.ApplyRegularV4FrontierRepairPressureSettlement(settlement).Status);
            Assert.True(adapter.IsRegularV4FrontierRepairSettlementApplied(settlement.Settlement));
            host.ObserveRegularV4FrontierRepairSettlement(settlement.Settlement);

            var final = FileTransferCoordinator.PrepareRegularV4FrontierRepairPressureSettlement(settled.State,
                RegularV4PressureSettlement(currentAuthority, outcome));
            var finalReceipt = bridge.ApplyRegularV4FrontierRepairPressureSettlement(final.SettlementCommand!);
            Assert.Equal(FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied, finalReceipt.Status);
            Assert.True(finalReceipt.CurrentAuthorityCleared);
            var finalState = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureSettlement(final.State, finalReceipt);
            Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted, finalState.Status);
            Assert.Null(PressureState(finalState.State).InstalledBinding);
            // A late genuine receipt is evidence, not permission to resurrect retired Core authority.
            var late = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(finalState.State, receipt);
            Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected, late.Status);
            Assert.False(PressureState(late.State).HasBinding);
            Assert.Equal(PressureState(finalState.State).LastSettlement, PressureState(late.State).LastSettlement);
        });

    [Theory]
    [InlineData("close")]
    [InlineData("replacement")]
    [InlineData("terminal")]
    [InlineData("reset")]
    [Trait("FileTransferInfraSuite", "RegularV4PressureBackend")]
    public Task NknPressureBackend_UsesActualSessionAndRuntimeLifecycle(string boundary)
        => WithPressureBackendAsync(true, async (host, helper, adapter, sessionId, ct) =>
        {
            const string transferId = "pressure-backend";
            using var session = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, ct);
            AssertPressureBackendActivity(adapter, 1, 0);
            session.Dispose();
            AssertPressureBackendActivity(adapter, 0, 0);
            var staged = StageNknPressure(sessionId);
            var bridge = (IFileTransferRegularV4FrontierRepairPressureInstallBridge)host;
            Assert.Equal("current_transfer_not_active", bridge.InstallRegularV4FrontierRepairPressure(staged.InstallCommand!).Reason);
            if (boundary == "close") { return; }
            if (boundary == "replacement")
            {
                using var replacement = await host.OpenFileTransferDataSessionAsync(sessionId, transferId, ct);
                Assert.NotSame(session, replacement);
                session.Dispose(); // Late old disposal must not unregister its replacement.
                AssertPressureBackendActivity(adapter, 1, 0);
                Assert.IsAssignableFrom<IFileTransferPhysicalSendSubmissionSession>(replacement);
                Assert.Equal(FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed,
                    bridge.InstallRegularV4FrontierRepairPressure(staged.InstallCommand!).Status);
                return;
            }

            var offered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.FileTransferOfferReceived += (_, _) => offered.TrySetResult();
            await helper.SendFileTransferOfferAsync(new FileTransferOfferV2
            {
                SessionId = sessionId, TransferId = transferId, FileName = "pressure.bin", FileSizeBytes = 1024,
                PreferredDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            }, ct);
            await offered.Task.WaitAsync(ct);
            await host.SendFileTransferAcceptAsync(new FileTransferAcceptV1
            {
                SessionId = sessionId, TransferId = transferId, AcceptedDataProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            }, ct);
            AssertPressureBackendActivity(adapter, 0, 1);
            if (boundary == "terminal")
            {
                await host.SendFileTransferCancelAsync(new FileTransferCancelV1
                    { SessionId = sessionId, TransferId = transferId, Reason = "user_cancel" }, ct);
            }
            else { host.Dispose(); }
            AssertPressureBackendActivity(adapter, 0, 0);
            var lateReceipt = bridge.InstallRegularV4FrontierRepairPressure(staged.InstallCommand!);
            Assert.Equal("current_transfer_not_active", lateReceipt.Reason);
            var rejected = FileTransferCoordinator.CommitRegularV4FrontierRepairPressureBinding(staged.State, lateReceipt);
            Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected, rejected.Status);
            Assert.Equal(staged.Binding, PressureState(rejected.State).PendingBinding);
        });

    [Fact]
    [Trait("FileTransferInfraSuite", "RegularV4PressureBackend")]
    public void NknPressureBackend_ProductionClientRemainsTheOnlyBackend()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("pressure-production", "pressure-production.invalid");
        using var client = new RealNknClientAdapter(identity, options);
        using var unused = new RealNknClientAdapter(identity, options);
        using var transport = new NknSignalingTransport(client, options, identity,
            NknTunaAccelerationOptions.Disabled, null, unused);
        Assert.Same(client, typeof(NknSignalingTransport).GetField("regularV4PressureAdapter",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport));
        Assert.True(((IFileTransferRouteTransitionBridge)transport).SupportsFallbackBootstrapCorrelation);
    }

    [Fact]
    [Trait("FileTransferInfraSuite", "RegularV4PressureBackend")]
    public void NknPressureBackend_ParameterlessConstructorUsesItsRealClient()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var directory = Path.Combine(CoreSmokeTestsBase.FindRepoRoot(), "artifacts",
            "regular-terminal-rca-20260922", "constructor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var keyPath = Path.Combine(directory, "identity.json");
        using var unsafeMode = CoreSmokeTestsBase.EnableUnsafeDeveloperModeForTests();
        using var keyOverride = new EnvironmentOverride("NLINK_NKN_KEY_PATH", keyPath);
        using var identifierOverride = new EnvironmentOverride("NLINK_NKN_IDENTIFIER", "regular-terminal-constructor-test");
        using var tunaOverride = new EnvironmentOverride("NLINK_NKN_TUNA_ENABLED", "0");
        using var logOverride = new EnvironmentOverride("NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH", Path.Combine(directory, "probe.log"));
        Assert.Equal(keyPath, NknTransportOptions.Load().KeyPath);

        // Construct only: no Connect/Host call, bridge bootstrap or live NKN traffic.
        using var transport = new NknSignalingTransport();
        var client = Assert.IsType<RealNknClientAdapter>(typeof(NknSignalingTransport)
            .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport));
        var backend = typeof(NknSignalingTransport).GetField("regularV4PressureAdapter",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport);
        var staged = StageNknPressure("regular-terminal-constructor-test");
        var receipt = ((IFileTransferRegularV4FrontierRepairPressureInstallBridge)transport)
            .InstallRegularV4FrontierRepairPressure(staged.InstallCommand!);
        File.WriteAllText(Path.Combine(directory, "observation.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            ClientType = client.GetType().Name,
            BackendType = backend?.GetType().Name,
            SameClient = ReferenceEquals(client, backend),
            InstallStatus = receipt.Status.ToString(),
            receipt.Reason,
            LiveConnectionStarted = false,
        }));

        Assert.Equal("current_transfer_not_active", receipt.Reason);
        Assert.Same(client, backend);
    }

    private static RegularV4FrontierRepairPressureBindingDecision StageNknPressure(string sessionId)
    {
        var route = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var initial = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer,
            route, state: FileTransferLegState.Active, canSendData: true),
            EmptyState(route) with { SessionId = sessionId, TransferId = "pressure-backend" });
        var authorized = AuthorizeRegularV4FrontierRepair(initial.State);
        var staged = FileTransferCoordinator.StageRegularV4FrontierRepairPressureBinding(authorized.State,
            authorized.Decision, RegularV4PressureAuthorityRequest("pressure-backend-initial"), "regular_v4_receiver_frontier_repair_due");
        Assert.Equal(RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted, staged.Status);
        return staged;
    }

    private static void AssertPressureBackendActivity(RealNknClientAdapter adapter, int dataSessions, int runtimes)
    {
        object Field(string name) => typeof(RealNknClientAdapter).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter)!;
        Assert.Equal(dataSessions, (int)Field("activeFileTransferDataSessions"));
        Assert.Equal(dataSessions, ((IDictionary)Field("activeFileTransferDataSessionTransfers")).Count);
        Assert.Equal(runtimes, ((IDictionary)Field("activeFileTransferRuntimeTransfers")).Count);
    }

    private static async Task WithPressureBackendAsync(bool supplyBackend,
        Func<NknSignalingTransport, NknSignalingTransport, RealNknClientAdapter, string, CancellationToken, Task> test)
    {
        FakeNknClient.ResetNetwork();
        using var unsafeMode = CoreSmokeTestsBase.EnableUnsafeDeveloperModeForTests();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var client = new FakeNknClient("host.pressure.backend.address");
            var peer = new FakeNknClient("helper.pressure.backend.address");
            var identity = new NknIdentity("host-pressure-backend", client.Address);
            using var adapter = new RealNknClientAdapter(identity, options);
            using var host = new NknSignalingTransport(client, options, identity,
                NknTunaAccelerationOptions.Disabled, null, supplyBackend ? adapter : null);
            using var helper = new NknSignalingTransport(peer, options, new NknIdentity("helper-pressure-backend", peer.Address));
            var sessionId = await CoreSmokeTestsBase.ApproveNknSessionAsync(host, helper, cts.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer);
            await test(host, helper, adapter, sessionId, cts.Token);
            host.Dispose();
            AssertPressureBackendActivity(adapter, 0, 0);
        }
        finally { FakeNknClient.ResetNetwork(); }
    }
}
