namespace NLink.Core.FileTransfer;

internal enum FileTransferBootstrapInitiationKind
{
    PeerEvidence,
    LocalRuntimeDisable,
    LocalTransportFailure,
}

internal sealed record FileTransferBootstrapFailureOrigin(
    Guid LaneInstanceId, long ClientGeneration, string ProcessRunId, long FailureGeneration)
{
    public bool IsDefault => LaneInstanceId == Guid.Empty || ClientGeneration <= 0 ||
        string.IsNullOrWhiteSpace(ProcessRunId) || FailureGeneration <= 0;
}

// Copied source evidence, not authority. The actual session reference remains at the Service boundary.
internal sealed record FileTransferBootstrapLocalStopScope(
    string LocalIssuer,
    FileTransferRoutePublicationAuthority Publication,
    FileTransferFallbackV6ProofBootstrapTriggerSourceScope Source);

internal sealed record FileTransferBootstrapLocalStopSource(
    FileTransferBootstrapLocalStopScope Scope,
    IFileTransferDataSession DataSession,
    long ProviderGeneration)
{
    public FileTransferBootstrapFailureOrigin? FailureOrigin { get; init; }
}

internal sealed record FileTransferBootstrapLocalStopObservation(
    FileTransferBootstrapLocalStopScope Scope,
    string DataSessionIdentity,
    long DataSessionGeneration)
{
    public FileTransferBootstrapFailureOrigin? FailureOrigin { get; init; }
}

internal static partial class FileTransferCoordinator
{
    // Projects a current Core publication, never transport readiness. Data-session
    // and physical-provider lifetimes remain pinned by each operation using it.
    internal static FileTransferBootstrapLocalStopScope? CaptureFallbackBootstrapSourceScope(
        FileTransferCoordinatorState state, string? coordinatorDecisionReason = null)
    {
        if (state.IsTerminal || state.RouteSelection.Route != FileTransferRoute.FileTunaV4 || state.RouteSelection.ProtocolVersion != 4 ||
            state.PersistentState.FileTransferRouteTransition.BootstrapAgreement is not { IsEnabled: true } agreement ||
            !IsBootstrapAgreementCurrent(state, agreement) ||
            state.CurrentRoutePublicationToken is not { } publication ||
            state.CurrentRuntimeOwner is not { } owner || state.CurrentLeg is not { } leg ||
            publication.RuntimeOwner != owner || leg.RouteSelection.Route != FileTransferRoute.FileTunaV4 ||
            leg.ProtocolVersion != 4 || leg.LiveRouteEpochId != publication.LiveRouteEpoch ||
            state.CurrentDataSessionIdentity is not { Length: > 0 } || state.CurrentDataSessionGeneration <= 0 ||
            !IsRoutePublicationTokenCurrent(state, publication, out _) ||
            publication.PublicationGeneration != state.LastRoutePublicationGeneration)
        {
            return null;
        }
        if (state.CurrentRouteCommit is { } commit)
        {
            if (!IsRouteCommitTokenCurrent(state, commit, out _) || commit.RuntimeOwner != owner ||
                publication.RouteCommitGeneration != commit.CommitGeneration) { return null; }
        }
        else if (publication.ActionKind != FileTransferCoordinatorActionKind.RoutePublication ||
                 publication.PublicationGeneration != 1 || publication.RouteCommitGeneration is not null ||
                 publication.LiveRouteEpoch != 0 || state.CurrentLiveRouteEpoch is not null ||
                 state.LastLiveRouteEpochId != 0 || state.LastRouteCommitGeneration != 0 || state.TerminalGeneration != 0)
        {
            // A missing commit is legitimate only for the exact initial owner,
            // not for a retired commit, route transition, or republication.
            return null;
        }
        return new(CreateLocalFallbackBootstrapIssuer(state),
            CreateRoutePublicationAuthority(publication, coordinatorDecisionReason)!,
            new(publication.RouteToken, publication.ProtocolVersion, publication.LiveRouteEpoch, owner.RuntimeGeneration,
                publication.PublicationGeneration, publication.RouteCommitGeneration ?? 0, state.TerminalGeneration, leg.Generation,
                leg.TransportEpochId, leg.BridgeRecoveryGeneration, leg.CheckpointRequestId ?? "(none)", leg.CheckpointGeneration));
    }

    internal static bool IsFallbackBootstrapLocalStopScopeCurrent(
        FileTransferCoordinatorState state, FileTransferBootstrapLocalStopScope scope)
        => scope is { Publication: { }, Source: { } } &&
            CaptureFallbackBootstrapSourceScope(state, scope.Publication.CoordinatorDecisionReason) is { } current && current == scope;

    internal static bool IsFallbackBootstrapInitiationCurrent(
        FileTransferCoordinatorState state, FileTransferFallbackV6ProofBootstrapObservation observation)
        => observation.InitiationKind == FileTransferBootstrapInitiationKind.PeerEvidence
            ? !observation.IsDefault && observation.LocalStop is null &&
              observation.Source == "correlated_bootstrap_peer_frame" &&
              observation.ProofKind == (state.Direction == FileTransferDirection.Outbound
                  ? "file_transfer_v6_state_frame_received" : "file_transfer_v6_frontier_request_frame_received") &&
              observation.SessionId == state.SessionId && observation.TransferId == state.TransferId && observation.Direction == state.Direction &&
              IsBootstrapHoldCheckpointSourceCurrent(state, observation.DurableCheckpoint)
            : !observation.IsDefault &&
              IsLocalFallbackBootstrapInitiationShapeValid(observation.InitiationKind, observation.Source,
                  observation.ProofKind, observation.LocalStop?.FailureOrigin) &&
              observation.SessionId == state.SessionId && observation.TransferId == state.TransferId && observation.Direction == state.Direction &&
              observation.LocalStop is { } stop && IsFallbackBootstrapLocalStopScopeCurrent(state, stop.Scope) &&
              stop.DataSessionIdentity == state.CurrentDataSessionIdentity && stop.DataSessionGeneration > 0 &&
              stop.DataSessionGeneration == state.CurrentDataSessionGeneration;

    internal static bool IsLocalFallbackBootstrapInitiationShapeValid(FileTransferBootstrapInitiationKind kind,
        string source, string proofKind, FileTransferBootstrapFailureOrigin? origin)
        => kind switch
        {
            FileTransferBootstrapInitiationKind.LocalRuntimeDisable
                => source == "runtime_disabled" && proofKind == "local_runtime_disabled" && origin is null,
            FileTransferBootstrapInitiationKind.LocalTransportFailure
                => source is "sidecar_tuna_write_failed" or "sidecar_tuna_stream_eof" or "sidecar_remote_closed" &&
                   proofKind == "local_transport_failure" && origin is { IsDefault: false },
            _ => false,
        };
}
