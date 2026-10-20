using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    private static FileTransferRouteTransitionDispositionCommand CreateFallbackPrerequisiteDisposition(
        IFileTransferDataSession dataSession, FileTransferDirection direction)
    {
        var providerGeneration = Assert.IsAssignableFrom<IFileTransferDataSessionGenerationProvider>(dataSession).Generation;
        Assert.True(providerGeneration > 0);
        var command = FileTransferCoordinatorTests.CreateFallbackTransportPrerequisite(
            dataSession.SessionId, dataSession.TransferId, direction, providerGeneration);
        Assert.Equal(dataSession.SessionId, command.Authority.SessionId);
        Assert.Equal(dataSession.TransferId, command.Authority.TransferId);
        Assert.Equal(direction, command.Authority.Direction);
        Assert.Equal(providerGeneration, command.Authority.DataSessionProviderGeneration);
        return command;
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(7L)]
    public void FallbackPrerequisite_PreservesProviderGenerationSeparatelyFromAttachment(long providerGeneration)
    {
        var command = FileTransferCoordinatorTests.CreateFallbackTransportPrerequisite(
            "session_fallback_provider_generation", "transfer_fallback_provider_generation",
            FileTransferDirection.Outbound, providerGeneration);

        Assert.Equal(FileTransferRouteTransitionDispositionKind.Committed, command.Kind);
        Assert.Equal(1L, command.Authority.DataSessionGeneration);
        Assert.Equal(providerGeneration, command.Authority.DataSessionProviderGeneration);
        Assert.Equal(providerGeneration, command.Authority.DurableCheckpoint.DataSessionProviderGeneration);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6, command.Authority.TargetRoute);
        Assert.Equal(FileTransferProtocol.ProtocolVersionV6, command.Authority.TargetProtocolVersion);
    }

    private static async Task ApplyFallbackPrerequisiteDispositionAsync(
        NknSignalingTransport transport, FileTransferRouteTransitionDispositionCommand command)
    {
        var bridge = (IFileTransferRouteTransitionBridge)transport;
        Assert.Equal(FileTransferRouteTransitionPauseBindStatus.Installed,
            bridge.BindFileTransferRouteTransitionPause(new(command.Authority)).Status);
        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied,
            bridge.ApplyFileTransferRouteTransitionDisposition(command).Status);
        await WaitUntilAsync(() => GetPrivateField(transport, "fileTransferRouteTransitionExecutionShadow")!
            .GetType().GetProperty("PendingAvailabilityDisposition")!
            .GetValue(GetPrivateField(transport, "fileTransferRouteTransitionExecutionShadow")) is null,
            TimeSpan.FromSeconds(2));
    }
}

public sealed partial class FileTransferCoordinatorTests
{
    internal static FileTransferRouteTransitionDispositionCommand CreateFallbackTransportPrerequisite(
        string sessionId, string transferId, FileTransferDirection direction, long dataSessionProviderGeneration)
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer,
            regular, state: FileTransferLegState.Active, canSendData: true),
            EmptyState(regular, direction) with { SessionId = sessionId, TransferId = transferId });
        var source = direction == FileTransferDirection.Inbound
            ? ActivateInitialResponderTunaForFallbackFixture(started.State)
            : ActivateTuna(started.State, FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4), 4);
        return CreateCheckpointContinuityFallbackBeforeDisposition(
            source.State with
            {
                CurrentDataSessionGeneration = 1,
                CurrentDataSessionProviderGeneration = dataSessionProviderGeneration,
            }, static _ => { }).Disposition;
    }
}
