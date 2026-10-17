using System.Globalization;

namespace NLink.Core.FileTransfer;

internal enum RuntimeUnlockDataSessionGenerationKind
{
    TransferLocal = 0,
    Provider = 1,
}

internal static partial class FileTransferCoordinator
{
    // The adapter cannot infer a local lifetime from its provider-wide counter.
    // Project the binding only as part of a coordinator-issued hold decision,
    // after its current-scope or exact retained-hold validation. Invalidation
    // carries the retired scope for cleanup, never for a new activation.
    private static RuntimeUnlockInitialActivationReceiveRecoveryDataSessionBinding?
        CreateInitialActivationHoldDataSessionBinding(
            in RuntimeUnlockInitialActivationReceiveRecoveryHoldRequest request,
            RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus status,
            in FileTransferReceiveRecoveryOperation? operation)
    {
        if (status == RuntimeUnlockInitialActivationReceiveRecoveryHoldStatus.Rejected || operation is null)
            return null;
        var authority = operation.Value.Authority;
        return new RuntimeUnlockInitialActivationReceiveRecoveryDataSessionBinding(
            request, authority.AuthorityId, authority.OperationGeneration,
            authority.Observation.DataSessionIdentity, authority.Observation.DataSessionGeneration);
    }

    // Transport observations use a provider-wide counter. Authority and repair
    // scopes use the transfer-local lifetime. Only Core may associate the two.
    internal static bool RuntimeUnlockDataSessionScopeMatches(
        FileTransferCoordinatorState state, string identity, long generation,
        RuntimeUnlockDataSessionGenerationKind kind)
    {
        if (state.IsTerminal || string.IsNullOrWhiteSpace(state.CurrentDataSessionIdentity) ||
            state.CurrentDataSessionGeneration <= 0 || generation <= 0)
            return false;

        return kind switch
        {
            RuntimeUnlockDataSessionGenerationKind.TransferLocal =>
                identity == state.CurrentDataSessionIdentity && generation == state.CurrentDataSessionGeneration,
            RuntimeUnlockDataSessionGenerationKind.Provider =>
                generation == state.CurrentDataSessionProviderGeneration &&
                identity == $"{state.TransferId}:{generation.ToString(CultureInfo.InvariantCulture)}",
            _ => false,
        };
    }
}
