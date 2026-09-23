using System.Reflection;
using NLink.Core.FileTransfer;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknAccelerationTransportTests
{
    private static FileTransferRouteTransitionDispositionCommand CreateFallbackPrerequisiteDisposition(
        IFileTransferDataSession dataSession, FileTransferDirection direction)
    {
        var generation = Assert.IsType<long>(dataSession.GetType()
            .GetProperty("Generation", BindingFlags.Instance | BindingFlags.Public)!.GetValue(dataSession));
        return FileTransferCoordinatorTests.CreateFallbackTransportPrerequisite(
            dataSession.SessionId, dataSession.TransferId, direction, generation);
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
        string sessionId, string transferId, FileTransferDirection direction, long dataSessionGeneration)
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer,
            regular, state: FileTransferLegState.Active, canSendData: true),
            EmptyState(regular, direction) with { SessionId = sessionId, TransferId = transferId });
        var source = direction == FileTransferDirection.Inbound
            ? ActivateInitialResponderTunaForFallbackFixture(started.State)
            : ActivateTuna(started.State, FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4), 4);
        return CreateCheckpointContinuityFallbackBeforeDisposition(
            source.State with { CurrentDataSessionGeneration = dataSessionGeneration }, static _ => { }).Disposition;
    }
}
