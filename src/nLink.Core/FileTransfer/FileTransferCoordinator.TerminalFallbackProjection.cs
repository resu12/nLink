namespace NLink.Core.FileTransfer;

internal sealed record FileTransferTerminalFallbackProjectionRetirementCommand(
    RoutePublicationToken Publication,
    int TerminalGeneration,
    long TransportEpoch,
    long DataSessionGeneration);

internal static partial class FileTransferCoordinator
{
    private static FileTransferTerminalFallbackProjectionRetirementCommand? CreateTerminalFallbackProjectionRetirement(
        FileTransferCoordinatorState state, int terminalGeneration)
        => state.CurrentRoutePublicationToken is { } publication &&
           publication.Route == FileTransferRoute.PostTunaFallbackV6 && publication.ProtocolVersion == 6 &&
           state.CurrentLeg is { TransportEpochId: > 0 } leg && state.CurrentDataSessionGeneration > 0 &&
           IsRoutePublicationTokenCurrent(state, publication, out _)
            ? new(publication, terminalGeneration, leg.TransportEpochId, state.CurrentDataSessionGeneration)
            : null;
}
