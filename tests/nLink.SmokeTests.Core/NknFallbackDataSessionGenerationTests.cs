using NLink.Core.Configuration;
using NLink.Core.FileTransfer;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [Trait("FileTransferRuntimeSuite", "FallbackDataSessionGeneration")]
    public Task JoinedBootstrapCorrelation_LaterDataSessionSwitchOffPublishesFallback(
        int priorDataSessions, bool stopReceiver)
        => RunJoinedRouteRecoveryAsync(cycle: false, priorDataSessions: priorDataSessions,
            stopReceiver: stopReceiver);
}

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    [Trait("FileTransferCoordinatorSuite", "InitialTunaFallbackTransition")]
    public void InitialTunaFallbackTransition_ProviderReplacementCannotPrepareOrCommit(FileTransferDirection direction)
    {
        var source = CreateCompletedInitialFallbackSource(direction);
        var request = InitialFallbackTransitionRequest(source);
        AssertInitialFallbackPrepareRejected(source with { CurrentDataSessionProviderGeneration = 2 }, request);
        AssertInitialFallbackPrepareRejected(source, request with
        {
            DurableCheckpoint = request.DurableCheckpoint with { DataSessionProviderGeneration = 2 },
        });

        var draining = PrepareInitialFallbackTransition(source);
        var authority = draining.Authority!.Value;
        var replaced = draining.State with { CurrentDataSessionProviderGeneration = 2 };
        Assert.False(FileTransferCoordinator.TryValidateFileTransferRouteTransitionCommitProof(
            replaced, authority, null, out var reason));
        Assert.Equal("filetransfer_route_transition_data_session_provider_stale", reason);
        AssertInitialFallbackCommitRejected(replaced, authority, InitialFallbackDrainReceipt(draining));
        AssertInitialFallbackTransitionApplied(source, CommitInitialFallbackTransition(draining));
    }
}

public sealed partial class FileTransferRouteTransitionInfraTests
{
    [Theory]
    [InlineData(false, FileTransferDirection.Outbound)]
    [InlineData(false, FileTransferDirection.Inbound)]
    [InlineData(true, FileTransferDirection.Outbound)]
    [InlineData(true, FileTransferDirection.Inbound)]
    [Trait("FileTransferInfraSuite", "FileTransferRouteTransition")]
    public async Task TransitionPause_ProviderGenerationIsIndependentAndReplacementRejectsReplay(
        bool useNkn, FileTransferDirection direction)
    {
        var client = new FakeNknClient("provider-generation.invalid");
        IFileTransferSignalingTransport transport = useNkn
            ? new NknSignalingTransport(client, NknTransportOptions.Load(),
                new NknIdentity("provider-generation", client.Address), NknTunaAccelerationOptions.Disabled,
                accelerationLane: null)
            : new DevLocalTransport();
        using var transportLifetime = Assert.IsAssignableFrom<IDisposable>(transport);
        using (await transport.OpenFileTransferDataSessionAsync(
            "route-transition-session", "prior-transfer", CancellationToken.None)) { }
        using var session = await transport.OpenFileTransferDataSessionAsync(
            "route-transition-session", "route-transition-transfer", CancellationToken.None);
        var providerGeneration = GetDataSessionGeneration(session);
        Assert.Equal(2L, providerGeneration);
        var authority = CreateAuthorityForRoute("provider-generation", 1, direction: direction);
        var bridge = Assert.IsAssignableFrom<IFileTransferRouteTransitionBridge>(transport);
        foreach (var invalidProvider in new[] { 0L, 1L, 3L })
        {
            var rejected = bridge.BindFileTransferRouteTransitionPause(new(authority with
            {
                DurableCheckpoint = authority.DurableCheckpoint with { DataSessionProviderGeneration = invalidProvider },
            }));
            Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Rejected, rejected.Status);
            Assert.Equal("filetransfer_route_transition_data_session_not_current", rejected.Reason);
        }
        authority = authority with
        {
            DurableCheckpoint = authority.DurableCheckpoint with { DataSessionProviderGeneration = providerGeneration },
        };
        if (useNkn)
        {
            Assert.True(Assert.IsType<bool>(InvokePrivate(transport,
                "PauseFileTransferDataSessionsForTunaActivationNegotiation", "test_transition_pause",
                "route-transition-session", "unit_test")));
        }
        var command = new FileTransferRouteTransitionPauseBindCommand(authority);
        Assert.Equal(1L, authority.DataSessionGeneration);
        var installed = bridge.BindFileTransferRouteTransitionPause(command);
        Assert.True(installed.Status == FileTransferRouteTransitionPauseBindStatus.Installed, installed.Reason);
        Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Idempotent,
            bridge.BindFileTransferRouteTransitionPause(command).Status);

        session.Dispose();
        Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Rejected,
            bridge.BindFileTransferRouteTransitionPause(command).Status);
        if (!useNkn)
        {
            // DevLocal retains disposed entries until connection teardown; model that removal
            // without changing production lifecycle behavior for this exact-session test.
            var sessions = Assert.IsAssignableFrom<System.Collections.IDictionary>(
                GetPrivateFieldValue(transport, "fileTransferDataSessions"));
            Assert.Same(session, sessions["route-transition-transfer"]);
            sessions.Remove("route-transition-transfer");
        }
        using var replacement = await transport.OpenFileTransferDataSessionAsync(
            "route-transition-session", "route-transition-transfer", CancellationToken.None);
        Assert.Equal(3L, GetDataSessionGeneration(replacement));
        Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Rejected,
            bridge.BindFileTransferRouteTransitionPause(command).Status);
    }
}
