namespace NLink.Core.FileTransfer;

internal enum FileTransferCoordinatorEventKind
{
    StartTransfer,
    TunaActivated,
    TunaLost,
    FallbackCheckpointRequested,
    FallbackCheckpointAccepted,
    FallbackCheckpointRetired,
    FallbackTailQuiescent,
    FallbackTailFailed,
    BridgeRecoveryStarted,
    BridgeRecoveryCompleted,
    BridgeRecoveryExhausted,
    RuntimeUnlockCommitRequested,
    Terminalized,
}

internal enum FileTransferCoordinatorActionKind
{
    RuntimeOwnerValidation,
    StartTransferLeg,
    FallbackCheckpointBegin,
    FallbackCheckpointAccept,
    FallbackCheckpointReissue,
    FallbackCheckpointRetire,
    ControlPlaneSend,
    FallbackTailQuiescent,
    FallbackTailFailed,
    RepairReplay,
    BridgeRecovery,
    LivenessDeferral,
    ProgressMutation,
    RouteMismatchMark,
    RoutePublication,
}

internal enum RuntimeUnlockAdmissionStatus
{
    Granted,
    WaitingForFallbackOwner,
    RejectedTerminal,
    RejectedStaleOwner,
    RejectedFailedTransaction,
}

internal readonly record struct RuntimeUnlockAdmissionDecision(
    RuntimeUnlockAdmissionStatus Status,
    string Reason)
{
    public bool Granted => Status == RuntimeUnlockAdmissionStatus.Granted;
}

internal sealed record FileTransferCoordinatorState(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    bool IsTerminal,
    FileTransferRouteSelection RouteSelection,
    LiveRouteEpoch? CurrentLiveRouteEpoch,
    FileTransferLeg? CurrentLeg,
    int LastLiveRouteEpochId,
    int LastTransferLegGeneration,
    int LastRuntimeOwnerGeneration,
    int TerminalGeneration,
    RuntimeOwnerToken? CurrentRuntimeOwner,
    IReadOnlyList<FileTransferLeg> LegHistory)
{
    public FileTransferRouteRuntimeDescriptor RouteRuntime => RouteSelection.RuntimeDescriptor;
}

internal readonly record struct FileTransferCoordinatorEvent(
    FileTransferCoordinatorEventKind Kind,
    FileTransferRouteSelection RouteSelection,
    FileTransferTransportHandoffKind HandoffKind,
    FileTransferTransportKind TargetTransport,
    string Reason,
    FileTransferLegState LegState,
    bool CanSendData,
    int CommittedChunkIndex,
    int HighestObservedChunkIndex,
    long TransportEpochId,
    int BridgeRecoveryGeneration,
    string? CheckpointRequestId = null,
    string? CheckpointPriority = null,
    int CheckpointGeneration = 0,
    RuntimeUnlockRouteCommitProof? RuntimeUnlockCommitProof = null,
    bool FreezeCurrentLeg = true);

internal readonly record struct FileTransferCoordinatorActionRequest(
    FileTransferCoordinatorActionKind Kind,
    string Source,
    string Reason,
    RuntimeOwnerToken? RuntimeOwner = null,
    FileTransferRoute? Route = null,
    int? ProtocolVersion = null,
    int? LiveRouteEpoch = null,
    int? LegGeneration = null,
    long? TransportEpochId = null,
    int? BridgeRecoveryGeneration = null,
    string? CheckpointRequestId = null);

internal readonly record struct FileTransferCoordinatorActionDecision(
    FileTransferCoordinatorActionKind Kind,
    bool Accepted,
    string RejectionReason,
    bool TerminalMutationRejected,
    FileTransferRoute CurrentRoute,
    int CurrentProtocolVersion,
    int CurrentLiveRouteEpoch,
    int CurrentRuntimeGeneration,
    int CurrentTerminalGeneration,
    int CurrentLegGeneration);

internal sealed record FileTransferCoordinatorDecision(
    FileTransferCoordinatorState State,
    bool RouteChanged,
    bool TerminalMutationRejected,
    LiveRouteEpoch? StartedLiveRouteEpoch,
    LiveRouteEpoch? RecoveredLiveRouteEpoch,
    FileTransferLeg? StartedLeg,
    FileTransferLeg? FrozenLeg,
    FileTransferLeg? AcceptedCheckpointLeg,
    bool FallbackCheckpointRequired,
    string Reason,
    bool RuntimeUnlockCommitAccepted = false,
    string? RuntimeUnlockCommitRejectedReason = null,
    string? MutationRejectedReason = null,
    FileTransferLeg? MutatedLeg = null);

internal readonly record struct RuntimeOwnerToken(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    FileTransferRoute Route,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int RuntimeGeneration,
    int TerminalGeneration,
    string RuntimeFamily,
    string CreatedReason)
{
    public bool IsDefault => RuntimeGeneration <= 0;
}

internal readonly record struct FileTransferLegStartRequest(
    FileTransferRouteSelection RouteSelection,
    int LastTransferLegGeneration,
    int CurrentLiveRouteEpochId,
    long TransportEpochId,
    int BridgeRecoveryGeneration,
    int StartCommittedChunkIndex,
    int ProvenHighestObservedChunkIndex,
    FileTransferLegState State,
    bool CanSendData,
    DateTimeOffset StartedUtc);

internal sealed class LiveRouteEpoch
{
    public required int EpochId { get; init; }

    public required FileTransferRouteSelection RouteSelection { get; init; }

    public required FileTransferTransportHandoffKind HandoffKind { get; init; }

    public required FileTransferTransportKind TargetTransport { get; init; }

    public required string Reason { get; init; }

    public required string State { get; set; }
}

internal enum FileTransferLegState
{
    Active,
    Frozen,
    CheckpointPending,
    RecoveryActive,
    BridgeRestartPending,
    Terminal,
}

internal sealed class FileTransferLeg
{
    public required string LegId { get; init; }

    public required int Generation { get; init; }

    public required FileTransferRouteSelection RouteSelection { get; init; }

    public required int ProtocolVersion { get; init; }

    public required int LiveRouteEpochId { get; init; }

    public required long TransportEpochId { get; set; }

    public required DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset? FrozenUtc { get; set; }

    public FileTransferLegState State { get; set; } = FileTransferLegState.Active;

    public int StartCommittedChunkIndex { get; init; }

    public int ProvenCommittedChunkIndex { get; set; } = -1;

    public int ProvenHighestObservedChunkIndex { get; set; } = -1;

    public string? CheckpointRequestId { get; set; }

    public string? CheckpointPriority { get; set; }

    public DateTimeOffset? CheckpointRequestedUtc { get; set; }

    public int CheckpointGeneration { get; set; }

    public int BridgeRecoveryGeneration { get; set; }

    public bool CanSendData { get; set; } = true;

    public bool RuntimeUnlockProbeAllowedAfterTransportEpochAdoption { get; set; }
}

internal static class FileTransferCoordinator
{
    public static FileTransferCoordinatorDecision Apply(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state)
    {
        var reason = NormalizeReason(coordinatorEvent.Reason);
        if (state.IsTerminal)
        {
            return MutationRejected(state, reason, "terminal_state", terminalMutationRejected: true);
        }

        return coordinatorEvent.Kind switch
        {
            FileTransferCoordinatorEventKind.StartTransfer => StartTransfer(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.TunaActivated => TransitionToRoute(coordinatorEvent, state, reason, recoverEpoch: true),
            FileTransferCoordinatorEventKind.TunaLost => TransitionToRoute(coordinatorEvent, state, reason, recoverEpoch: false),
            FileTransferCoordinatorEventKind.FallbackCheckpointRequested => MarkFallbackCheckpointRequested(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.FallbackCheckpointAccepted => MarkFallbackCheckpointAccepted(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.FallbackCheckpointRetired => RetireFallbackCheckpointRequest(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.FallbackTailQuiescent => MarkFallbackTailQuiescent(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.FallbackTailFailed => MarkFallbackTailFailed(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.BridgeRecoveryStarted => MarkCurrentFallbackLegState(state, FileTransferLegState.RecoveryActive, reason),
            FileTransferCoordinatorEventKind.BridgeRecoveryCompleted => PassThrough(state, reason),
            FileTransferCoordinatorEventKind.BridgeRecoveryExhausted => MarkCurrentFallbackLegState(state, FileTransferLegState.BridgeRestartPending, reason),
            FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested => ApplyRuntimeUnlockCommit(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.Terminalized => Terminalize(state, reason),
            _ => PassThrough(state, reason),
        };
    }

    public static FileTransferCoordinatorActionDecision AuthorizeAction(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorActionRequest request)
    {
        if (state.IsTerminal)
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                rejectionReason: "terminal_state",
                terminalMutationRejected: true);
        }

        if (request.RuntimeOwner is { } runtimeOwner)
        {
            if (!IsRuntimeOwnerCurrent(state, runtimeOwner, out var ownerRejectionReason))
            {
                return ActionDecision(
                    state,
                    request.Kind,
                    accepted: false,
                    ownerRejectionReason);
            }
        }
        else if (RequiresRuntimeOwner(request.Kind))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                rejectionReason: "runtime_owner_missing");
        }

        if (request.Route is { } route && route != state.RouteSelection.Route)
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                rejectionReason: "route_or_protocol_mismatch");
        }

        if (request.ProtocolVersion is { } protocolVersion &&
            protocolVersion != state.RouteSelection.ProtocolVersion)
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                rejectionReason: "route_or_protocol_mismatch");
        }

        var currentLiveRouteEpoch = state.CurrentLiveRouteEpoch?.EpochId ?? 0;
        if (request.LiveRouteEpoch is { } liveRouteEpoch &&
            liveRouteEpoch != currentLiveRouteEpoch)
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                rejectionReason: "live_route_epoch_mismatch");
        }

        var currentLegGeneration = state.CurrentLeg?.Generation ?? 0;
        if (request.LegGeneration is { } legGeneration &&
            legGeneration != currentLegGeneration)
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                rejectionReason: "leg_generation_mismatch");
        }

        if (request.TransportEpochId is { } transportEpochId &&
            (state.CurrentLeg is null || transportEpochId != state.CurrentLeg.TransportEpochId))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                rejectionReason: state.CurrentLeg is null ? "leg_missing" : "transport_epoch_mismatch");
        }

        if (request.BridgeRecoveryGeneration is { } bridgeRecoveryGeneration &&
            (state.CurrentLeg is null || bridgeRecoveryGeneration != state.CurrentLeg.BridgeRecoveryGeneration))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                rejectionReason: state.CurrentLeg is null ? "leg_missing" : "bridge_recovery_generation_mismatch");
        }

        if (request.CheckpointRequestId is { } checkpointRequestId &&
            (state.CurrentLeg is null ||
             !string.Equals(checkpointRequestId, state.CurrentLeg.CheckpointRequestId, StringComparison.Ordinal)))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                rejectionReason: state.CurrentLeg is null ? "leg_missing" : "checkpoint_request_mismatch");
        }

        return ActionDecision(
            state,
            request.Kind,
            accepted: true,
            rejectionReason: "accepted");
    }

    public static LiveRouteEpoch StartLiveRouteEpoch(
        int previousEpochId,
        FileTransferRouteSelection routeSelection,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport,
        string reason)
        => new()
        {
            EpochId = Math.Max(1, previousEpochId + 1),
            RouteSelection = routeSelection,
            HandoffKind = handoffKind,
            TargetTransport = targetTransport,
            Reason = NormalizeReason(reason),
            State = "started",
        };

    private static FileTransferLeg StartLeg(FileTransferLegStartRequest request)
    {
        var generation = Math.Max(
            request.LastTransferLegGeneration + 1,
            Math.Max(1, request.CurrentLiveRouteEpochId));
        var committed = Math.Max(0, request.StartCommittedChunkIndex);
        return new FileTransferLeg
        {
            LegId = $"leg:{generation}",
            Generation = generation,
            RouteSelection = request.RouteSelection,
            ProtocolVersion = request.RouteSelection.ProtocolVersion,
            LiveRouteEpochId = request.CurrentLiveRouteEpochId,
            TransportEpochId = request.TransportEpochId,
            StartedUtc = request.StartedUtc,
            State = request.State,
            StartCommittedChunkIndex = committed,
            ProvenCommittedChunkIndex = request.CanSendData ? committed : -1,
            ProvenHighestObservedChunkIndex = request.CanSendData ? request.ProvenHighestObservedChunkIndex : -1,
            BridgeRecoveryGeneration = request.BridgeRecoveryGeneration,
            CanSendData = request.CanSendData,
        };
    }

    private static FileTransferLeg? FreezeLeg(FileTransferLeg? leg, DateTimeOffset frozenUtc)
    {
        if (leg is null || leg.State is FileTransferLegState.Frozen or FileTransferLegState.Terminal)
        {
            return null;
        }

        leg.FrozenUtc = frozenUtc;
        leg.State = FileTransferLegState.Frozen;
        leg.CanSendData = false;
        return leg;
    }

    private static void TerminalizeLeg(FileTransferLeg? leg)
    {
        if (leg is null)
        {
            return;
        }

        leg.State = FileTransferLegState.Terminal;
        leg.CanSendData = false;
        leg.CheckpointRequestId = null;
        leg.CheckpointPriority = null;
        leg.CheckpointRequestedUtc = null;
    }

    public static bool IsCurrentPostTunaFallbackLeg(FileTransferLeg? leg)
        => leg is
        {
            RouteSelection.Route: FileTransferRoute.PostTunaFallbackV6,
            ProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
        };

    public static RuntimeOwnerToken IssueRuntimeOwnerToken(
        FileTransferCoordinatorState state,
        FileTransferRouteSelection routeSelection,
        int liveRouteEpoch,
        int runtimeGeneration,
        int terminalGeneration,
        string reason)
        => new(
            state.SessionId,
            state.TransferId,
            state.Direction,
            routeSelection.Route,
            routeSelection.TelemetryToken,
            routeSelection.ProtocolVersion,
            liveRouteEpoch,
            Math.Max(1, runtimeGeneration),
            Math.Max(0, terminalGeneration),
            ResolveRuntimeFamily(routeSelection.RuntimeDescriptor),
            NormalizeReason(reason));

    public static bool IsRuntimeOwnerCurrent(
        FileTransferCoordinatorState state,
        RuntimeOwnerToken token,
        out string rejectionReason)
    {
        rejectionReason = "current";
        if (token.IsDefault)
        {
            rejectionReason = "runtime_owner_missing";
            return false;
        }

        if (!string.Equals(token.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(token.TransferId, state.TransferId, StringComparison.Ordinal) ||
            token.Direction != state.Direction)
        {
            rejectionReason = "session_transfer_or_direction_mismatch";
            return false;
        }

        if (token.RuntimeGeneration != state.CurrentRuntimeOwner?.RuntimeGeneration ||
            token.RuntimeGeneration != state.LastRuntimeOwnerGeneration)
        {
            rejectionReason = "runtime_generation_mismatch";
            return false;
        }

        if (token.TerminalGeneration != state.TerminalGeneration)
        {
            rejectionReason = "terminal_generation_mismatch";
            return false;
        }

        if (token.Route != state.RouteSelection.Route ||
            token.ProtocolVersion != state.RouteSelection.ProtocolVersion ||
            !string.Equals(token.RouteToken, state.RouteSelection.TelemetryToken, StringComparison.Ordinal))
        {
            rejectionReason = "route_or_protocol_mismatch";
            return false;
        }

        var expectedLiveRouteEpoch = state.CurrentLiveRouteEpoch?.EpochId ?? 0;
        if (token.LiveRouteEpoch != expectedLiveRouteEpoch)
        {
            rejectionReason = "live_route_epoch_mismatch";
            return false;
        }

        if (state.IsTerminal)
        {
            rejectionReason = "transfer_terminal";
            return false;
        }

        return true;
    }

    public static string ResolveRuntimeFamily(FileTransferRouteRuntimeDescriptor runtime)
    {
        if (runtime.UsesRegularNknV4FastRuntime)
        {
            return "regular_v4";
        }

        if (runtime.UsesFileTunaV4Runtime)
        {
            return "file_tuna_v4";
        }

        if (runtime.UsesPostTunaFallbackV6Runtime)
        {
            return "post_tuna_fallback_v6";
        }

        if (runtime.UsesV6SparsePump)
        {
            return "diagnostic_v6";
        }

        return "unknown";
    }

    public static bool IsCurrentPostTunaFallbackLegCheckpointPending(FileTransferLeg? leg)
        => IsCurrentPostTunaFallbackLeg(leg) &&
           !leg!.CanSendData &&
           !string.IsNullOrWhiteSpace(leg.CheckpointRequestId) &&
           leg.State is FileTransferLegState.CheckpointPending or FileTransferLegState.BridgeRestartPending;

    public static bool IsCurrentPostTunaFallbackLegAwaitingCheckpoint(FileTransferLeg? leg)
        => IsCurrentPostTunaFallbackLeg(leg) &&
           !leg!.CanSendData &&
           !string.IsNullOrWhiteSpace(leg.CheckpointRequestId) &&
           leg.State is FileTransferLegState.CheckpointPending or FileTransferLegState.BridgeRestartPending;

    public static bool CanAdmitRuntimeUnlockWhilePostTunaFallback(
        bool routeUsesPostTunaFallbackRuntime,
        FileTransferLeg? leg,
        bool checkpointDeliveryRecoveryPending,
        bool stateRefreshSendInFlight,
        bool deferredStateRefreshPending,
        bool proofReplayPending,
        out string rejectionReason)
    {
        var decision = EvaluateRuntimeUnlockAdmission(
            isTerminal: false,
            runtimeOwnerCurrent: true,
            routeUsesPostTunaFallbackRuntime,
            leg,
            checkpointDeliveryRecoveryPending,
            stateRefreshSendInFlight,
            deferredStateRefreshPending,
            proofReplayPending,
            bridgeRecoveryPending: false,
            controlPlanePressurePending: false,
            fallbackTailRepairPending: false,
            transactionFailed: false);

        rejectionReason = decision.Reason;
        return decision.Granted;
    }

    public static RuntimeUnlockAdmissionDecision EvaluateRuntimeUnlockAdmission(
        bool isTerminal,
        bool runtimeOwnerCurrent,
        bool routeUsesPostTunaFallbackRuntime,
        FileTransferLeg? leg,
        bool checkpointDeliveryRecoveryPending,
        bool stateRefreshSendInFlight,
        bool deferredStateRefreshPending,
        bool proofReplayPending,
        bool bridgeRecoveryPending,
        bool controlPlanePressurePending,
        bool fallbackTailRepairPending,
        bool transactionFailed,
        bool pairedFallbackYieldGranted = false)
    {
        if (isTerminal)
        {
            return new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.RejectedTerminal,
                "transfer_terminal");
        }

        if (!runtimeOwnerCurrent)
        {
            return new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.RejectedStaleOwner,
                "runtime_owner_stale");
        }

        if (transactionFailed)
        {
            return new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.RejectedFailedTransaction,
                "runtime_unlock_transaction_failed");
        }

        if (!routeUsesPostTunaFallbackRuntime)
        {
            return new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.Granted,
                "not_post_tuna_fallback");
        }

        if (!IsCurrentPostTunaFallbackLeg(leg))
        {
            return new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner,
                "fallback_leg_not_current");
        }

        if (!leg!.CanSendData)
        {
            var reason = string.IsNullOrWhiteSpace(leg.CheckpointRequestId)
                ? "fallback_checkpoint_ownerless_pending"
                : "fallback_checkpoint_pending";
            return new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner,
                reason);
        }

        if (leg.State is FileTransferLegState.CheckpointPending or FileTransferLegState.RecoveryActive or FileTransferLegState.BridgeRestartPending)
        {
            return new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner,
                FormatLegState(leg.State));
        }

        if (!string.IsNullOrWhiteSpace(leg.CheckpointRequestId))
        {
            return new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner,
                "fallback_checkpoint_owner_active");
        }

        if (checkpointDeliveryRecoveryPending)
        {
            return new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner,
                "fallback_checkpoint_delivery_recovery_pending");
        }

        if (stateRefreshSendInFlight)
        {
            return new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner,
                "fallback_state_refresh_send_in_flight");
        }

        if (deferredStateRefreshPending)
        {
            return new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner,
                "fallback_deferred_state_refresh_pending");
        }

        if (proofReplayPending)
        {
            return new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner,
                "fallback_proof_replay_pending");
        }

        if (bridgeRecoveryPending)
        {
            return new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner,
                "fallback_bridge_recovery_pending");
        }

        if (controlPlanePressurePending)
        {
            return new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner,
                "fallback_control_plane_pressure_pending");
        }

        if (fallbackTailRepairPending)
        {
            return new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner,
                "fallback_tail_repair_pending");
        }

        if (!pairedFallbackYieldGranted)
        {
            return new RuntimeUnlockAdmissionDecision(
                RuntimeUnlockAdmissionStatus.WaitingForFallbackOwner,
                "paired_fallback_tail_yield_missing");
        }

        return new RuntimeUnlockAdmissionDecision(
            RuntimeUnlockAdmissionStatus.Granted,
            "fallback_quiescent");
    }

    public static bool TryValidateCurrentFallbackCheckpointProof(
        FileTransferLeg? leg,
        FileTransferRoute currentRoute,
        int protocolVersion,
        int currentLiveRouteEpochId,
        long proofTransportEpochId,
        string? proofCheckpointRequestId,
        out string rejectionReason)
    {
        rejectionReason = "ok";
        if (!IsCurrentPostTunaFallbackLeg(leg))
        {
            rejectionReason = "not_current_post_tuna_fallback_leg";
            return false;
        }

        if (currentRoute != FileTransferRoute.PostTunaFallbackV6 ||
            protocolVersion != FileTransferProtocol.ProtocolVersionV6)
        {
            rejectionReason = "route_or_protocol_mismatch";
            return false;
        }

        if (!IsCurrentPostTunaFallbackLegAwaitingCheckpoint(leg))
        {
            rejectionReason = "checkpoint_not_pending";
            return false;
        }

        if (leg!.LiveRouteEpochId > 0 &&
            currentLiveRouteEpochId > 0 &&
            currentLiveRouteEpochId != leg.LiveRouteEpochId)
        {
            rejectionReason = "live_route_epoch_mismatch";
            return false;
        }

        if (leg.TransportEpochId <= 0 ||
            proofTransportEpochId <= 0 ||
            proofTransportEpochId != leg.TransportEpochId)
        {
            rejectionReason = "transport_epoch_mismatch";
            return false;
        }

        if (string.IsNullOrWhiteSpace(leg.CheckpointRequestId))
        {
            rejectionReason = "checkpoint_request_missing_current_leg";
            return false;
        }

        if (string.IsNullOrWhiteSpace(proofCheckpointRequestId))
        {
            rejectionReason = "checkpoint_request_missing_proof";
            return false;
        }

        if (!string.Equals(leg.CheckpointRequestId, proofCheckpointRequestId, StringComparison.Ordinal))
        {
            rejectionReason = "checkpoint_request_mismatch";
            return false;
        }

        return true;
    }

    public static bool CanTransitionToRoute(
        FileTransferRouteRuntimeDescriptor currentRuntime,
        int negotiatedProtocolVersion,
        FileTransferRoute targetRoute,
        FileTransferTransportHandoffKind handoffKind,
        FileTransferTransportKind targetTransport)
        => targetRoute switch
        {
            FileTransferRoute.FileTunaV4 =>
                handoffKind == FileTransferTransportHandoffKind.NormalToTunaActivation &&
                targetTransport == FileTransferTransportKind.Tuna &&
                ((currentRuntime.UsesRegularNknV4FastRuntime &&
                  negotiatedProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
                  currentRuntime.FrameFamily == FileTransferFrameFamily.V4) ||
                 (currentRuntime.UsesPostTunaFallbackV6Runtime &&
                  negotiatedProtocolVersion >= FileTransferProtocol.ProtocolVersionV6 &&
                  currentRuntime.FrameFamily == FileTransferFrameFamily.V6)),
            FileTransferRoute.PostTunaFallbackV6 =>
                handoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
                negotiatedProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
                currentRuntime.FrameFamily == FileTransferFrameFamily.V4 &&
                (currentRuntime.UsesFileTunaV4Runtime || currentRuntime.UsesRegularNknV4FastRuntime),
            _ => false,
        };

    private static void MarkFallbackCheckpointAccepted(
        FileTransferLeg leg,
        long transportEpochId,
        int provenCommittedChunkIndex,
        int provenHighestObservedChunkIndex)
    {
        leg.State = FileTransferLegState.RecoveryActive;
        leg.CanSendData = true;
        leg.TransportEpochId = transportEpochId > 0 ? transportEpochId : leg.TransportEpochId;
        leg.ProvenCommittedChunkIndex = Math.Max(0, provenCommittedChunkIndex);
        leg.ProvenHighestObservedChunkIndex = Math.Max(-1, provenHighestObservedChunkIndex);
        leg.CheckpointRequestId = null;
        leg.CheckpointPriority = null;
        leg.CheckpointRequestedUtc = null;
        leg.RuntimeUnlockProbeAllowedAfterTransportEpochAdoption = false;
    }

    private static void MarkFallbackTailQuiescent(
        FileTransferLeg leg,
        long transportEpochId,
        int provenCommittedChunkIndex,
        int provenHighestObservedChunkIndex)
    {
        leg.State = FileTransferLegState.Active;
        leg.CanSendData = true;
        leg.TransportEpochId = transportEpochId > 0 ? transportEpochId : leg.TransportEpochId;
        leg.ProvenCommittedChunkIndex = Math.Max(0, provenCommittedChunkIndex);
        leg.ProvenHighestObservedChunkIndex = Math.Max(-1, provenHighestObservedChunkIndex);
        leg.CheckpointRequestId = null;
        leg.CheckpointPriority = null;
        leg.CheckpointRequestedUtc = null;
        leg.RuntimeUnlockProbeAllowedAfterTransportEpochAdoption = false;
    }

    private static void MarkFallbackTailFailed(FileTransferLeg leg)
    {
        if (leg.State is not FileTransferLegState.Terminal and not FileTransferLegState.Frozen)
        {
            leg.State = FileTransferLegState.RecoveryActive;
        }

        leg.RuntimeUnlockProbeAllowedAfterTransportEpochAdoption = false;
    }

    private static void MarkFallbackCheckpointRequested(
        FileTransferLeg leg,
        string? checkpointRequestId,
        string? checkpointPriority,
        long transportEpochId,
        DateTimeOffset? requestedUtc = null)
    {
        leg.State = FileTransferLegState.CheckpointPending;
        leg.CanSendData = false;
        leg.CheckpointRequestId = checkpointRequestId;
        leg.CheckpointPriority = checkpointPriority;
        leg.CheckpointRequestedUtc = requestedUtc ?? DateTimeOffset.UtcNow;
        leg.CheckpointGeneration++;
        leg.TransportEpochId = transportEpochId > 0 ? transportEpochId : leg.TransportEpochId;
        leg.RuntimeUnlockProbeAllowedAfterTransportEpochAdoption = false;
    }

    private static void RetireFallbackCheckpointRequest(FileTransferLeg leg)
    {
        leg.CheckpointRequestId = null;
        leg.CheckpointPriority = null;
        leg.CheckpointRequestedUtc = null;
        leg.State = FileTransferLegState.RecoveryActive;
        leg.CanSendData = true;
        leg.RuntimeUnlockProbeAllowedAfterTransportEpochAdoption = false;
    }

    public static string FormatLegState(FileTransferLegState state)
        => state switch
        {
            FileTransferLegState.Active => "active",
            FileTransferLegState.Frozen => "frozen",
            FileTransferLegState.CheckpointPending => "checkpoint_pending",
            FileTransferLegState.RecoveryActive => "recovery_active",
            FileTransferLegState.BridgeRestartPending => "bridge_restart_pending",
            FileTransferLegState.Terminal => "terminal",
            _ => "unknown",
        };

    private static FileTransferCoordinatorDecision StartTransfer(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        var frozenLeg = coordinatorEvent.FreezeCurrentLeg
            ? FreezeLeg(state.CurrentLeg, DateTimeOffset.UtcNow)
            : null;
        var legHistory = state.LegHistory.ToList();
        var leg = StartLeg(new FileTransferLegStartRequest(
            coordinatorEvent.RouteSelection,
            state.LastTransferLegGeneration,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            coordinatorEvent.TransportEpochId,
            coordinatorEvent.BridgeRecoveryGeneration,
            coordinatorEvent.CommittedChunkIndex,
            coordinatorEvent.HighestObservedChunkIndex,
            coordinatorEvent.LegState,
            coordinatorEvent.CanSendData,
            DateTimeOffset.UtcNow));
        legHistory.Add(leg);
        var runtimeGeneration = Math.Max(1, state.LastRuntimeOwnerGeneration + 1);
        var nextState = state with
        {
            RouteSelection = coordinatorEvent.RouteSelection,
            CurrentLeg = leg,
            LastTransferLegGeneration = leg.Generation,
            LastRuntimeOwnerGeneration = runtimeGeneration,
            LegHistory = legHistory,
        };
        nextState = nextState with
        {
            CurrentRuntimeOwner = IssueRuntimeOwnerToken(
                nextState,
                coordinatorEvent.RouteSelection,
                nextState.CurrentLiveRouteEpoch?.EpochId ?? 0,
                runtimeGeneration,
                nextState.TerminalGeneration,
                reason),
        };
        return Decision(nextState, reason, startedLeg: leg, frozenLeg: frozenLeg);
    }

    private static FileTransferCoordinatorDecision TransitionToRoute(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason,
        bool recoverEpoch)
    {
        var frozenLeg = FreezeLeg(state.CurrentLeg, DateTimeOffset.UtcNow);
        var epoch = StartLiveRouteEpoch(
            state.LastLiveRouteEpochId,
            coordinatorEvent.RouteSelection,
            coordinatorEvent.HandoffKind,
            coordinatorEvent.TargetTransport,
            reason);
        if (recoverEpoch)
        {
            epoch.State = "recovered";
        }

        var legHistory = state.LegHistory.ToList();
        var leg = StartLeg(new FileTransferLegStartRequest(
            coordinatorEvent.RouteSelection,
            state.LastTransferLegGeneration,
            epoch.EpochId,
            coordinatorEvent.TransportEpochId,
            coordinatorEvent.BridgeRecoveryGeneration,
            coordinatorEvent.CommittedChunkIndex,
            coordinatorEvent.HighestObservedChunkIndex,
            coordinatorEvent.LegState,
            coordinatorEvent.CanSendData,
            DateTimeOffset.UtcNow));
        legHistory.Add(leg);
        var runtimeGeneration = Math.Max(1, state.LastRuntimeOwnerGeneration + 1);

        var nextState = state with
        {
            RouteSelection = coordinatorEvent.RouteSelection,
            CurrentLiveRouteEpoch = epoch,
            CurrentLeg = leg,
            LastLiveRouteEpochId = epoch.EpochId,
            LastTransferLegGeneration = leg.Generation,
            LastRuntimeOwnerGeneration = runtimeGeneration,
            LegHistory = legHistory,
        };
        nextState = nextState with
        {
            CurrentRuntimeOwner = IssueRuntimeOwnerToken(
                nextState,
                coordinatorEvent.RouteSelection,
                epoch.EpochId,
                runtimeGeneration,
                nextState.TerminalGeneration,
                reason),
        };

        return new FileTransferCoordinatorDecision(
            nextState,
            RouteChanged: state.RouteSelection.Route != coordinatorEvent.RouteSelection.Route ||
                          state.RouteSelection.ProtocolVersion != coordinatorEvent.RouteSelection.ProtocolVersion,
            TerminalMutationRejected: false,
            StartedLiveRouteEpoch: epoch,
            RecoveredLiveRouteEpoch: recoverEpoch ? epoch : null,
            StartedLeg: leg,
            FrozenLeg: frozenLeg,
            AcceptedCheckpointLeg: null,
            FallbackCheckpointRequired: !coordinatorEvent.CanSendData &&
                                        coordinatorEvent.RouteSelection.RuntimeDescriptor.UsesPostTunaFallbackV6Runtime,
            reason);
    }

    private static FileTransferCoordinatorDecision MarkFallbackCheckpointRequested(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        var leg = state.CurrentLeg;
        if (!IsCurrentPostTunaFallbackLeg(leg))
        {
            return MutationRejected(state, reason, leg is null ? "leg_missing" : "fallback_leg_not_current");
        }

        if (string.IsNullOrWhiteSpace(coordinatorEvent.CheckpointRequestId))
        {
            return MutationRejected(state, reason, "checkpoint_request_missing");
        }

        if (leg!.TransportEpochId > 0 &&
            coordinatorEvent.TransportEpochId > 0 &&
            leg.TransportEpochId != coordinatorEvent.TransportEpochId)
        {
            return MutationRejected(state, reason, "transport_epoch_mismatch");
        }

        MarkFallbackCheckpointRequested(
            leg,
            coordinatorEvent.CheckpointRequestId,
            coordinatorEvent.CheckpointPriority,
            coordinatorEvent.TransportEpochId);
        if (coordinatorEvent.CheckpointGeneration > 0)
        {
            leg.CheckpointGeneration = coordinatorEvent.CheckpointGeneration;
        }

        return Decision(state, reason, mutatedLeg: leg, fallbackCheckpointRequired: true);
    }

    private static FileTransferCoordinatorDecision MarkFallbackCheckpointAccepted(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        var leg = state.CurrentLeg;
        if (!IsCurrentPostTunaFallbackLeg(leg))
        {
            return MutationRejected(state, reason, leg is null ? "leg_missing" : "fallback_leg_not_current");
        }

        if (leg!.TransportEpochId > 0 &&
            coordinatorEvent.TransportEpochId > 0 &&
            coordinatorEvent.TransportEpochId < leg.TransportEpochId)
        {
            return MutationRejected(state, reason, "transport_epoch_mismatch");
        }

        if (!string.IsNullOrWhiteSpace(coordinatorEvent.CheckpointRequestId) &&
            !string.IsNullOrWhiteSpace(leg.CheckpointRequestId) &&
            !string.Equals(leg.CheckpointRequestId, coordinatorEvent.CheckpointRequestId, StringComparison.Ordinal))
        {
            return MutationRejected(state, reason, "checkpoint_request_mismatch");
        }

        MarkFallbackCheckpointAccepted(
            leg,
            coordinatorEvent.TransportEpochId,
            coordinatorEvent.CommittedChunkIndex,
            coordinatorEvent.HighestObservedChunkIndex);
        return Decision(state, reason, acceptedCheckpointLeg: leg, mutatedLeg: leg);
    }

    private static FileTransferCoordinatorDecision RetireFallbackCheckpointRequest(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        var leg = state.CurrentLeg;
        if (!IsCurrentPostTunaFallbackLeg(leg))
        {
            return MutationRejected(state, reason, leg is null ? "leg_missing" : "fallback_leg_not_current");
        }

        if (!string.IsNullOrWhiteSpace(coordinatorEvent.CheckpointRequestId) &&
            !string.Equals(leg!.CheckpointRequestId, coordinatorEvent.CheckpointRequestId, StringComparison.Ordinal))
        {
            return MutationRejected(state, reason, "checkpoint_request_mismatch");
        }

        RetireFallbackCheckpointRequest(leg!);
        return Decision(state, reason, mutatedLeg: leg);
    }

    private static FileTransferCoordinatorDecision MarkFallbackTailQuiescent(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        var leg = state.CurrentLeg;
        if (!IsCurrentPostTunaFallbackLeg(leg))
        {
            return MutationRejected(state, reason, leg is null ? "leg_missing" : "fallback_leg_not_current");
        }

        if (leg!.TransportEpochId > 0 &&
            coordinatorEvent.TransportEpochId > 0 &&
            coordinatorEvent.TransportEpochId < leg.TransportEpochId)
        {
            return MutationRejected(state, reason, "transport_epoch_mismatch");
        }

        MarkFallbackTailQuiescent(
            leg,
            coordinatorEvent.TransportEpochId,
            coordinatorEvent.CommittedChunkIndex,
            coordinatorEvent.HighestObservedChunkIndex);
        return Decision(state, reason, mutatedLeg: leg);
    }

    private static FileTransferCoordinatorDecision MarkFallbackTailFailed(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        var leg = state.CurrentLeg;
        if (!IsCurrentPostTunaFallbackLeg(leg))
        {
            return MutationRejected(state, reason, leg is null ? "leg_missing" : "fallback_leg_not_current");
        }

        MarkFallbackTailFailed(leg!);
        return Decision(state, reason, mutatedLeg: leg);
    }

    private static FileTransferCoordinatorDecision MarkCurrentFallbackLegState(
        FileTransferCoordinatorState state,
        FileTransferLegState legState,
        string reason)
    {
        if (IsCurrentPostTunaFallbackLeg(state.CurrentLeg))
        {
            state.CurrentLeg!.State = legState;
            return Decision(state, reason, mutatedLeg: state.CurrentLeg);
        }

        return PassThrough(state, reason);
    }

    private static FileTransferCoordinatorDecision ApplyRuntimeUnlockCommit(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        var rejectionReason = "transaction_proof_missing";
        if (coordinatorEvent.RuntimeUnlockCommitProof is not { } proof ||
            !RuntimeUnlockTransaction.CanCommitRoute(proof, state, out rejectionReason))
        {
            return Decision(state, reason) with
            {
                RuntimeUnlockCommitRejectedReason = rejectionReason,
            };
        }

        var transition = TransitionToRoute(
            coordinatorEvent,
            state,
            reason,
            recoverEpoch: true);
        return transition with
        {
            RuntimeUnlockCommitAccepted = true,
            RuntimeUnlockCommitRejectedReason = null,
        };
    }

    private static FileTransferCoordinatorDecision Terminalize(
        FileTransferCoordinatorState state,
        string reason)
    {
        var leg = state.CurrentLeg;
        TerminalizeLeg(leg);
        var nextTerminalGeneration = Math.Max(1, state.TerminalGeneration + 1);
        return Decision(
            state with
            {
                IsTerminal = true,
                TerminalGeneration = nextTerminalGeneration,
                CurrentRuntimeOwner = state.CurrentRuntimeOwner is { } owner
                    ? owner with { TerminalGeneration = nextTerminalGeneration }
                    : null,
            },
            reason,
            mutatedLeg: leg);
    }

    private static FileTransferCoordinatorDecision PassThrough(FileTransferCoordinatorState state, string reason)
        => Decision(state, reason);

    private static FileTransferCoordinatorDecision MutationRejected(
        FileTransferCoordinatorState state,
        string reason,
        string rejectionReason,
        bool terminalMutationRejected = false)
        => Decision(state, reason) with
        {
            TerminalMutationRejected = terminalMutationRejected,
            MutationRejectedReason = rejectionReason,
        };

    private static FileTransferCoordinatorDecision Decision(
        FileTransferCoordinatorState state,
        string reason,
        FileTransferLeg? startedLeg = null,
        FileTransferLeg? frozenLeg = null,
        FileTransferLeg? acceptedCheckpointLeg = null,
        FileTransferLeg? mutatedLeg = null,
        bool fallbackCheckpointRequired = false)
        => new(
            state,
            RouteChanged: false,
            TerminalMutationRejected: false,
            StartedLiveRouteEpoch: null,
            RecoveredLiveRouteEpoch: null,
            StartedLeg: startedLeg,
            FrozenLeg: frozenLeg,
            AcceptedCheckpointLeg: acceptedCheckpointLeg,
            FallbackCheckpointRequired: fallbackCheckpointRequired,
            reason,
            MutatedLeg: mutatedLeg);

    private static FileTransferCoordinatorActionDecision ActionDecision(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorActionKind kind,
        bool accepted,
        string rejectionReason,
        bool terminalMutationRejected = false)
        => new(
            kind,
            accepted,
            rejectionReason,
            terminalMutationRejected,
            state.RouteSelection.Route,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            state.CurrentRuntimeOwner?.RuntimeGeneration ?? 0,
            state.TerminalGeneration,
            state.CurrentLeg?.Generation ?? 0);

    private static bool RequiresRuntimeOwner(FileTransferCoordinatorActionKind kind)
        => kind != FileTransferCoordinatorActionKind.StartTransferLeg;

    private static string NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason)
            ? "file_transfer_coordinator"
            : reason.Trim();
}
