using System.Collections.Immutable;

namespace NLink.Core.FileTransfer;

internal sealed record InitialFallbackDataSessionRequest(
    string CoordinatorInstanceId, string SessionId, string TransferId, FileTransferDirection Direction,
    RuntimeOwnerToken RuntimeOwner, RoutePublicationToken Publication,
    string DataSessionIdentity, long DataSessionGeneration);

internal sealed record InitialFallbackDataSessionBinding(
    string DataSessionIdentity, long DataSessionGeneration);

internal sealed record InitialFallbackDataSessionDecision(
    FileTransferCoordinatorState State, string Reason,
    InitialFallbackDataSessionBinding? Binding = null,
    FileTransferCoordinatorTransitionEnvelope? SnapshotEnvelope = null);

internal sealed record InitialFallbackDataSessionSnapshot(
    FileTransferCoordinatorStateBoundarySnapshot SourceBoundary,
    FileTransferCoordinatorStateBoundarySnapshot EffectiveBoundary,
    string RuntimeOwnerFingerprint, string PublicationFingerprint);

internal static partial class FileTransferCoordinator
{
    internal static InitialFallbackDataSessionDecision BindInitialFallbackDataSession(
        FileTransferCoordinatorState state, InitialFallbackDataSessionRequest request)
    {
        const string reason = "initial_fallback_data_session_bound";
        if (string.IsNullOrWhiteSpace(state.PersistentState.CoordinatorInstanceId) ||
            state.IsTerminal || state.TerminalGeneration != 0 ||
            state.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            state.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            state.CurrentLiveRouteEpoch is not null || state.LastLiveRouteEpochId != 0 ||
            state.CurrentDataSessionIdentity is not (null or "" or "(none)") || state.CurrentDataSessionGeneration != 0 ||
            state.CurrentRoutePublicationToken is not { IsDefault: false, PublicationGeneration: 1,
                Route: FileTransferRoute.PostTunaFallbackV6, ProtocolVersion: 6,
                TargetRoute: FileTransferRoute.PostTunaFallbackV6, TargetProtocolVersion: 6,
                TargetTransport: FileTransferTransportKind.RegularNkn, ActionKind: FileTransferCoordinatorActionKind.RoutePublication,
                RouteCommitGeneration: null } || state.LastRoutePublicationGeneration != 1 ||
            state.CurrentRouteCommit is not null || state.CurrentTransportEpochDecision is not null ||
            state.CurrentLeg is not { State: FileTransferLegState.Active, CanSendData: true,
                TransportEpochId: 0, BridgeRecoveryGeneration: 0, CheckpointGeneration: 0 } leg ||
            !string.IsNullOrWhiteSpace(leg.CheckpointRequestId) ||
            state.CurrentFallbackCheckpointLease is not null || state.CurrentFallbackControlPlaneToken is not null ||
            state.CurrentFallbackControlPlanePressureToken is not null || state.CurrentRepairReplayDecision is not null ||
            state.CurrentRuntimeUnlockAdmission is not null || state.CurrentRuntimeUnlockPendingPreCommitProbe is not null ||
            state.PersistentState.FileTransferRouteTransition.CurrentOperation is not null ||
            state.PersistentState.FileTransferReceiveRecovery.CurrentOperation is not null ||
            state.PersistentState.FallbackTailYield.CurrentOperation is not null ||
            state.PersistentState.FallbackTailYield.PendingReauthorization is not null ||
            HasRegularV4FrontierRepairPressureBinding(state) || RequiresRuntimeUnlockRetryLifecycleScopeInvalidation(state))
        {
            return new(state, "initial_fallback_data_session_scope_invalid");
        }
        if (request.CoordinatorInstanceId != state.PersistentState.CoordinatorInstanceId ||
            request.SessionId != state.SessionId || request.TransferId != state.TransferId ||
            request.Direction != state.Direction ||
            request.RuntimeOwner != state.CurrentRuntimeOwner || request.Publication != state.CurrentRoutePublicationToken ||
            !IsRuntimeOwnerCurrent(state, request.RuntimeOwner, out _) ||
            !IsRoutePublicationTokenCurrent(state, request.Publication, out _) ||
            request.Publication.RuntimeOwner != request.RuntimeOwner ||
            request.DataSessionGeneration != 1 ||
            request.DataSessionIdentity != $"{state.TransferId}:1")
        {
            return new(state, "initial_fallback_data_session_request_invalid");
        }

        var binding = new InitialFallbackDataSessionBinding(request.DataSessionIdentity, 1);
        var after = state with { CurrentDataSessionIdentity = binding.DataSessionIdentity, CurrentDataSessionGeneration = 1 };
        var payload = new InitialFallbackDataSessionSnapshot(CreateStateBoundarySnapshot(state),
            CreateStateBoundarySnapshot(after), CalculateSnapshotFingerprint(request.RuntimeOwner),
            CalculateSnapshotFingerprint(request.Publication));
        var command = CalculateSnapshotFingerprint(new { state.PersistentState.CoordinatorInstanceId, Request = request });
        var receipt = CalculateSnapshotFingerprint(new { Command = command, Binding = payload, Status = "Accepted", Reason = reason });
        const string role = "initial_data_session_binding";
        const string kind = "InitialFallbackDataSessionBinding";
        var operation = new FileTransferCoordinatorOperationTransitionSnapshot(
            "route_runtime", role, CreateCoordinatorOperationKey("route_runtime", role, command, 1, kind),
            kind, command, "(none)", 1, "None", "Bound", command, 1, command,
            command + ":receipt", 1, "Accepted", receipt, true, true, null, null,
            InitialDataSessionBinding: payload);
        var snapshot = CreateTransitionSnapshot(state, after, state.PersistentState.CoordinatorInstanceId!,
            state.PersistentState.SnapshotStateGeneration, nameof(BindInitialFallbackDataSession), "Accepted", reason,
            ImmutableArray.Create(operation), suppliedOperationsAreComplete: true);
        return new(snapshot.State, reason, binding, snapshot.Envelope);
    }
}
