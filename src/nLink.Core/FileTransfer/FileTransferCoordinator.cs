namespace NLink.Core.FileTransfer;

internal enum FileTransferCoordinatorEventKind
{
    StartTransfer,
    TunaActivated,
    TunaLost,
    FallbackCheckpointRequested,
    FallbackCheckpointAccepted,
    BridgeRecoveryStarted,
    BridgeRecoveryCompleted,
    BridgeRecoveryExhausted,
    RuntimeUnlockCommitRequested,
    Terminalized,
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
    RuntimeUnlockRouteCommitProof? RuntimeUnlockCommitProof = null);

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
    string? RuntimeUnlockCommitRejectedReason = null);

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
}

internal static class FileTransferCoordinator
{
    public static FileTransferCoordinatorDecision Apply(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state)
    {
        var reason = NormalizeReason(coordinatorEvent.Reason);
        if (state.IsTerminal && coordinatorEvent.Kind != FileTransferCoordinatorEventKind.Terminalized)
        {
            return new FileTransferCoordinatorDecision(
                state,
                RouteChanged: false,
                TerminalMutationRejected: true,
                StartedLiveRouteEpoch: null,
                RecoveredLiveRouteEpoch: null,
                StartedLeg: null,
                FrozenLeg: null,
                AcceptedCheckpointLeg: null,
                FallbackCheckpointRequired: false,
                reason);
        }

        return coordinatorEvent.Kind switch
        {
            FileTransferCoordinatorEventKind.StartTransfer => StartTransfer(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.TunaActivated => TransitionToRoute(coordinatorEvent, state, reason, recoverEpoch: true),
            FileTransferCoordinatorEventKind.TunaLost => TransitionToRoute(coordinatorEvent, state, reason, recoverEpoch: false),
            FileTransferCoordinatorEventKind.FallbackCheckpointRequested => MarkFallbackCheckpointRequested(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.FallbackCheckpointAccepted => MarkFallbackCheckpointAccepted(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.BridgeRecoveryStarted => MarkCurrentFallbackLegState(state, FileTransferLegState.RecoveryActive, reason),
            FileTransferCoordinatorEventKind.BridgeRecoveryCompleted => PassThrough(state, reason),
            FileTransferCoordinatorEventKind.BridgeRecoveryExhausted => MarkCurrentFallbackLegState(state, FileTransferLegState.BridgeRestartPending, reason),
            FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested => ApplyRuntimeUnlockCommit(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.Terminalized => Terminalize(state, reason),
            _ => PassThrough(state, reason),
        };
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

    public static FileTransferLeg StartLeg(FileTransferLegStartRequest request)
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

    public static FileTransferLeg? FreezeLeg(FileTransferLeg? leg, DateTimeOffset frozenUtc)
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

    public static void TerminalizeLeg(FileTransferLeg? leg)
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

    public static void MarkFallbackCheckpointAccepted(
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
    }

    public static void MarkFallbackCheckpointRequested(
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
    }

    public static void RetireFallbackCheckpointRequest(FileTransferLeg leg)
    {
        leg.CheckpointRequestId = null;
        leg.CheckpointPriority = null;
        leg.CheckpointRequestedUtc = null;
        leg.State = FileTransferLegState.RecoveryActive;
        leg.CanSendData = true;
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
        var nextState = state with
        {
            RouteSelection = coordinatorEvent.RouteSelection,
            CurrentLeg = leg,
            LastTransferLegGeneration = leg.Generation,
            LegHistory = legHistory,
        };
        return Decision(nextState, reason, startedLeg: leg);
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

        var nextState = state with
        {
            RouteSelection = coordinatorEvent.RouteSelection,
            CurrentLiveRouteEpoch = epoch,
            CurrentLeg = leg,
            LastLiveRouteEpochId = epoch.EpochId,
            LastTransferLegGeneration = leg.Generation,
            LegHistory = legHistory,
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
            return PassThrough(state, reason);
        }

        var currentLeg = leg!;
        MarkFallbackCheckpointRequested(
            currentLeg,
            coordinatorEvent.CheckpointRequestId,
            coordinatorEvent.CheckpointPriority,
            coordinatorEvent.TransportEpochId);
        if (coordinatorEvent.CheckpointGeneration > 0)
        {
            currentLeg.CheckpointGeneration = coordinatorEvent.CheckpointGeneration;
        }

        return Decision(state, reason, startedLeg: null, fallbackCheckpointRequired: true);
    }

    private static FileTransferCoordinatorDecision MarkFallbackCheckpointAccepted(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        var leg = state.CurrentLeg;
        if (!IsCurrentPostTunaFallbackLeg(leg))
        {
            return PassThrough(state, reason);
        }

        MarkFallbackCheckpointAccepted(
            leg!,
            coordinatorEvent.TransportEpochId,
            coordinatorEvent.CommittedChunkIndex,
            coordinatorEvent.HighestObservedChunkIndex);
        return Decision(state, reason, acceptedCheckpointLeg: leg);
    }

    private static FileTransferCoordinatorDecision MarkCurrentFallbackLegState(
        FileTransferCoordinatorState state,
        FileTransferLegState legState,
        string reason)
    {
        if (IsCurrentPostTunaFallbackLeg(state.CurrentLeg))
        {
            state.CurrentLeg!.State = legState;
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
        TerminalizeLeg(state.CurrentLeg);
        return PassThrough(state with { IsTerminal = true }, reason);
    }

    private static FileTransferCoordinatorDecision PassThrough(FileTransferCoordinatorState state, string reason)
        => Decision(state, reason);

    private static FileTransferCoordinatorDecision Decision(
        FileTransferCoordinatorState state,
        string reason,
        FileTransferLeg? startedLeg = null,
        FileTransferLeg? acceptedCheckpointLeg = null,
        bool fallbackCheckpointRequired = false)
        => new(
            state,
            RouteChanged: false,
            TerminalMutationRejected: false,
            StartedLiveRouteEpoch: null,
            RecoveredLiveRouteEpoch: null,
            StartedLeg: startedLeg,
            FrozenLeg: null,
            AcceptedCheckpointLeg: acceptedCheckpointLeg,
            FallbackCheckpointRequired: fallbackCheckpointRequired,
            reason);

    private static string NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason)
            ? "file_transfer_coordinator"
            : reason.Trim();
}
