namespace NLink.Core.FileTransfer;

internal enum FileTransferBootstrapInitiationKind
{
    PeerEvidence,
    LocalRuntimeDisable,
}

// Copied source evidence, not authority. The actual session reference remains at the Service boundary.
internal sealed record FileTransferBootstrapLocalStopScope(
    string LocalIssuer,
    FileTransferRoutePublicationAuthority Publication,
    FileTransferFallbackV6ProofBootstrapTriggerSourceScope Source);

internal sealed record FileTransferBootstrapLocalStopSource(
    FileTransferBootstrapLocalStopScope Scope,
    IFileTransferDataSession DataSession,
    long ProviderGeneration);

internal sealed record FileTransferBootstrapLocalStopObservation(
    FileTransferBootstrapLocalStopScope Scope,
    string DataSessionIdentity,
    long DataSessionGeneration);

internal static partial class FileTransferCoordinator
{
    internal static bool IsFallbackBootstrapLocalStopScopeCurrent(
        FileTransferCoordinatorState state, FileTransferBootstrapLocalStopScope scope)
    {
        if (scope is not { Publication: { }, Source: { } } || state.IsTerminal || state.RouteSelection.Route != FileTransferRoute.FileTunaV4 ||
            state.PersistentState.FileTransferRouteTransition.BootstrapAgreement is not { IsEnabled: true } agreement ||
            !IsBootstrapAgreementCurrent(state, agreement) ||
            scope.LocalIssuer != CreateLocalFallbackBootstrapIssuer(state) ||
            state.CurrentRoutePublicationToken is not { } publication || state.CurrentRouteCommit is not { } commit ||
            state.CurrentRuntimeOwner is not { } owner || state.CurrentLeg is not { } leg ||
            !IsRoutePublicationTokenCurrent(state, publication, out _) ||
            scope.Publication != CreateRoutePublicationAuthority(publication, scope.Publication.CoordinatorDecisionReason))
        {
            return false;
        }
        return scope.Source == new FileTransferFallbackV6ProofBootstrapTriggerSourceScope(
            commit.RouteToken, commit.ProtocolVersion, commit.LiveRouteEpoch, owner.RuntimeGeneration,
            publication.PublicationGeneration, commit.CommitGeneration, state.TerminalGeneration, leg.Generation,
            leg.TransportEpochId, leg.BridgeRecoveryGeneration, leg.CheckpointRequestId ?? "(none)", leg.CheckpointGeneration);
    }

    private static bool IsFallbackBootstrapInitiationCurrent(
        FileTransferCoordinatorState state, FileTransferFallbackV6ProofBootstrapObservation observation)
        => observation.InitiationKind == FileTransferBootstrapInitiationKind.PeerEvidence
            ? observation.LocalStop is null && observation.ProofKind != "local_runtime_disabled"
            : observation.InitiationKind == FileTransferBootstrapInitiationKind.LocalRuntimeDisable &&
              !observation.IsDefault && observation.Source == "runtime_disabled" && observation.ProofKind == "local_runtime_disabled" &&
              observation.SessionId == state.SessionId && observation.TransferId == state.TransferId && observation.Direction == state.Direction &&
              observation.LocalStop is { } stop && IsFallbackBootstrapLocalStopScopeCurrent(state, stop.Scope) &&
              stop.DataSessionIdentity == state.CurrentDataSessionIdentity && stop.DataSessionGeneration > 0 &&
              stop.DataSessionGeneration == state.CurrentDataSessionGeneration;
}
