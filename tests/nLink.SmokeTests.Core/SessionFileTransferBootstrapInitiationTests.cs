using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    private static FileTransferFallbackV6ProofBootstrapEvaluation LocalStopEvaluationForTest(object context, IFileTransferDataSession session)
    {
        var state = CreateCoordinatorStateForTest(context);
        return new(state.SessionId, state.Direction, 1, 1, "runtime_disabled", "local_runtime_disabled", 10000)
        {
            InitiationKind = FileTransferBootstrapInitiationKind.LocalRuntimeDisable,
            LocalStop = new(FileTransferCoordinatorTests.BootstrapStopScopeForTests(state), session,
                ((IFileTransferDataSessionGenerationProvider)session).Generation),
        };
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)]
    public async Task BootstrapInitiation_ServiceRejectsUnboundOrStaleStopWithoutDispatch(int variant)
    {
        const string transferId = "bootstrap_stop_rejected";
        using var service = new SessionFileTransferService();
        var session = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        using var bridge = new BootstrapPhaseTestBridge(session);
        using var otherBridge = new BootstrapPhaseTestBridge(session);
        var issuer = FileTransferBootstrapCorrelation.CreateIssuer("peer", session.SessionId, transferId, FileTransferDirection.Outbound);
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, session,
            beforeActivation: context => PinBootstrapServiceAgreement(context, issuer));
        var evaluation = LocalStopEvaluationForTest(context, session);
        var source = evaluation.LocalStop!;
        evaluation = variant switch
        {
            0 => evaluation with { LocalStop = null },
            1 => evaluation with { InitiationKind = FileTransferBootstrapInitiationKind.PeerEvidence },
            2 => evaluation with { Source = "remote_runtime_disabled" },
            3 => evaluation with { TriggerTransferId = transferId, TriggerFrameFingerprint = "forged" },
            4 => evaluation with { LocalStop = source with { ProviderGeneration = source.ProviderGeneration + 1 } },
            5 => evaluation with { LocalStop = source with { Scope = source.Scope with { LocalIssuer = new string('a', 64) } } },
            6 => evaluation with { LocalStop = source with { Scope = source.Scope with { Publication = source.Scope.Publication with { TransferId = "other" } } } },
            7 => evaluation with { LocalStop = source with { DataSession = new InitialBootstrapRecordingDataSession(session.SessionId, transferId) } },
            12 => evaluation with { InitiationKind = FileTransferBootstrapInitiationKind.PeerEvidence, LocalStop = null },
            _ => evaluation,
        };
        if (variant == 9) { SetPrivateProperty(context, "UserPaused", true); }
        if (variant == 10) { SetPrivateProperty(context, "State", FileTransferTransferState.Canceled); }
        if (variant == 11) { SetPrivateProperty(context, "DataSession", new InitialBootstrapRecordingDataSession(session.SessionId, transferId)); }
        var before = CreateCoordinatorStateForTest(context);
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, variant == 8 ? otherBridge : bridge, evaluation);
        var after = CreateCoordinatorStateForTest(context);
        Assert.Empty(bridge.Executions);
        Assert.Empty(otherBridge.Executions);
        Assert.Equal(before.PersistentState.FileTransferRouteTransition, after.PersistentState.FileTransferRouteTransition);
        Assert.Equal(before.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task BootstrapInitiation_ServiceOwnsWriteDrainAndDuplicateCannotResumeHeldPhase(int variant)
    {
        using var fixture = await BootstrapWriteFixture.CreateAsync(blockWrite: true);
        var service = fixture.Service;
        var session = fixture.Session;
        var bridge = fixture.Bridge;
        var context = fixture.Context;
        var transferId = session.TransferId;
        var issuer = FileTransferBootstrapCorrelation.CreateIssuer("peer", session.SessionId, transferId, FileTransferDirection.Outbound);
        var evaluation = LocalStopEvaluationForTest(context, session);
        var writing = fixture.WriteAsync();
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        var initialWake = GetPrivatePropertyValue(context, "CurrentFallbackV6ProofBootstrapPendingWriteWake");
        Assert.NotNull(initialWake);
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        Assert.Same(initialWake, GetPrivatePropertyValue(context, "CurrentFallbackV6ProofBootstrapPendingWriteWake"));
        Assert.Empty(bridge.Executions);
        if (variant == 4)
        {
            var solicit = new FileTransferFallbackBootstrapRequestFrameV6
            {
                SessionId = session.SessionId, TransferId = transferId, TransportEpoch = 1,
                SenderRequestId = $"fbc1:{issuer}:371:{new string('b', 64)}",
            };
            Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod("TryHandleBootstrapCorrelationFrame",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
                [FileTransferDirection.Inbound, session, new FileTransferReceivedDataFrame(solicit, FileTransferTransportKind.RegularNkn, "control", DateTimeOffset.UtcNow)])));
            Assert.Empty(bridge.Executions);
            Assert.Same(initialWake, GetPrivatePropertyValue(context, "CurrentFallbackV6ProofBootstrapPendingWriteWake"));
        }
        if (variant == 1) { SetPrivateProperty(context, "UserPaused", true); }
        if (variant == 2) { SetPrivateProperty(context, "DataSession", new InitialBootstrapRecordingDataSession(session.SessionId, transferId)); }
        if (variant == 3) { SetPrivateProperty(context, "DataSessionGeneration", GetPrivateProperty<long>(context, "DataSessionGeneration") + 1); }
        fixture.Stream.CompleteWrite();
        await writing.WaitAsync(TimeSpan.FromSeconds(3));
        if (variant is 1 or 2 or 3)
        {
            Assert.Empty(bridge.Executions);
            return;
        }
        await WaitUntilAsync(() => bridge.Executions.Count == 1, timeoutMs: 3000);
        var execution = Assert.Single(bridge.Executions);
        Assert.Equal(variant == 4 ? FileTransferBootstrapPhase.Response : FileTransferBootstrapPhase.Announce, execution.Command.Phase);
        var held = new FileTransferBootstrapPhaseReceipt(execution.Command, FileTransferBootstrapPhaseReceiptStatus.Held, true, [], "modeled_no_submission");
        execution.Delivery.TrySetResult(held);
        execution.Settlement.TrySetResult(held);
        await WaitUntilAsync(() => CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition.BootstrapExchange!.First.Held, timeoutMs: 3000);
        var beforeDuplicate = CreateCoordinatorStateForTest(context);
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation with { ObservationGeneration = 99 });
        Assert.Single(bridge.Executions);
        Assert.Equal(beforeDuplicate.PersistentState.FileTransferRouteTransition, CreateCoordinatorStateForTest(context).PersistentState.FileTransferRouteTransition);
    }
}
