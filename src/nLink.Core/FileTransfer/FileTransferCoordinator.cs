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
    FallbackControlPlaneSendAuthorized,
    FallbackControlPlaneSendCompleted,
    FallbackControlPlanePressureUpdated,
    RuntimeUnlockAdmission,
    RuntimeUnlockAdmissionRetired,
    RuntimeUnlockFallbackSurvivalUpdated,
    RuntimeUnlockPreCommitProbePending,
    RuntimeUnlockPreCommitProbeReplayed,
    RuntimeUnlockPreCommitProbeRetired,
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
    ControlPlaneSendCompletion,
    FallbackControlPlanePressure,
    FallbackTailQuiescent,
    FallbackTailFailed,
    RepairReplay,
    BridgeRecovery,
    LivenessDeferral,
    ProgressMutation,
    RouteMismatchMark,
    RoutePublication,
    RuntimeUnlockAdmission,
    RuntimeUnlockRouteCommit,
    RuntimeUnlockOfferCallback,
    RuntimeUnlockAnswerCallback,
    RuntimeUnlockProbeStart,
    RuntimeUnlockProbeAck,
    RuntimeUnlockPendingPreCommitProbeReplay,
    RuntimeUnlockTransactionCallback,
    RouteCommitPublication,
    TransportEpochAdoption,
    PeerEpochPromotion,
    UnmatchedFrontierProof,
    ReceiveRecovery,
    BridgeReceiveStallRecovery,
    RecoveryLivenessLeaseBegin,
    RecoveryLivenessLeaseValidate,
    RecoveryLivenessLeaseRetire,
}

internal enum RuntimeUnlockAdmissionStatus
{
    Granted,
    WaitingForFallbackOwner,
    RejectedTerminal,
    RejectedStaleOwner,
    RejectedFailedTransaction,
}

internal enum FallbackCheckpointLeaseState
{
    None,
    Pending,
    Accepted,
    Retired,
}

internal enum FallbackTailYieldKind
{
    Unknown,
    Quiescent,
    Failed,
}

internal enum RepairReplayKind
{
    Unknown,
    V4Repair,
    V6Repair,
    PeerSilenceReplay,
    TransportRebindSafetyReplay,
    FrontierStallRepair,
    UnmatchedFrontierProof,
}

internal enum TransportEpochActionKind
{
    Unknown,
    Adopt,
    RecoverLiveRoute,
    PromotePeerEpoch,
    MarkRouteMismatch,
}

internal enum BridgeRecoveryKind
{
    Unknown,
    ReceiveRecoveryRequest,
    BridgeReceiveStallRecovery,
}

internal enum RecoveryLivenessLeaseState
{
    None,
    Active,
    Retired,
}

internal readonly record struct InitialTunaRouteSelectionDecision(
    bool Accepted,
    string Reason,
    FileTransferRoute RequestedRoute,
    int RequestedProtocolVersion,
    FileTransferTransportKind RequestedTransport,
    FileTransferRoute SelectedRoute,
    int SelectedProtocolVersion,
    bool FallbackApplied,
    FileTransferInitialTunaReadinessEvidence? Evidence);

internal readonly record struct RuntimeUnlockAdmissionDecision(
    RuntimeUnlockAdmissionStatus Status,
    string Reason,
    RuntimeUnlockAdmissionToken? AdmissionToken = null,
    FileTransferRoute CurrentRoute = default,
    int CurrentProtocolVersion = 0,
    int CurrentLiveRouteEpoch = 0,
    int CurrentRuntimeGeneration = 0,
    int CurrentTerminalGeneration = 0,
    int CurrentLegGeneration = 0,
    RuntimeUnlockFallbackSurvivalState FallbackSurvivalState = default)
{
    public bool Granted => Status == RuntimeUnlockAdmissionStatus.Granted;
}

internal readonly record struct RuntimeUnlockFallbackSurvivalState(
    bool FallbackActive,
    bool FallbackCurrent,
    bool FallbackTerminalized,
    bool CheckpointDeliveryRecoveryPending,
    bool StateRefreshSendInFlight,
    bool DeferredStateRefreshPending,
    bool ProofReplayPending,
    bool ControlPlanePressurePending,
    bool BridgeRecoveryPending,
    bool FallbackTailRepairPending,
    bool PairedFallbackYieldGranted)
{
    public static RuntimeUnlockFallbackSurvivalState Empty { get; } = new();
}

internal readonly record struct RuntimeUnlockAdmissionToken(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    RuntimeOwnerToken RuntimeOwner,
    FileTransferRoute Route,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int RuntimeGeneration,
    int TerminalGeneration,
    int LegGeneration,
    int AdmissionGeneration,
    string Reason,
    RuntimeUnlockFallbackSurvivalState FallbackSurvivalState)
{
    public bool IsDefault => AdmissionGeneration <= 0 || RuntimeOwner.IsDefault;
}

internal readonly record struct RuntimeUnlockAdmissionRequest(
    string Source,
    string Reason,
    RuntimeOwnerToken? RuntimeOwner,
    bool TransactionFailed = false);

internal readonly record struct RuntimeUnlockPendingPreCommitProbeRequest(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    RuntimeOwnerToken RuntimeOwner,
    RuntimeUnlockAdmissionToken AdmissionToken,
    FileTransferRoute Route,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int RuntimeGeneration,
    int TerminalGeneration,
    int LegGeneration,
    long TransactionGeneration,
    long OfferGeneration,
    long TunaPathLeaseGeneration,
    string? TunaPathLeaseListenerRunId,
    FileTransferRoute TargetRoute,
    int TargetProtocolVersion,
    FileTransferTransportKind TargetTransport,
    FileTransferTransportHandoffKind HandoffKind,
    int PendingGeneration,
    string Reason)
{
    public bool IsDefault => PendingGeneration <= 0 ||
                             RuntimeOwner.IsDefault ||
                             AdmissionToken.IsDefault;
}

internal readonly record struct RuntimeUnlockPendingPreCommitProbeDecision(
    bool Accepted,
    string RejectionReason,
    FileTransferRoute CurrentRoute,
    string CurrentRouteToken,
    int CurrentProtocolVersion,
    int CurrentLiveRouteEpoch,
    int CurrentRuntimeGeneration,
    int CurrentTerminalGeneration,
    int CurrentLegGeneration,
    RuntimeUnlockPendingPreCommitProbeRequest? PendingRequest);

internal readonly record struct RouteCommitToken(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    RuntimeOwnerToken RuntimeOwner,
    RuntimeUnlockAdmissionToken RuntimeUnlockAdmission,
    FileTransferRoute Route,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int RuntimeGeneration,
    int TerminalGeneration,
    int LegGeneration,
    long TransactionGeneration,
    long OfferGeneration,
    long TunaPathLeaseGeneration,
    string? TunaPathLeaseListenerRunId,
    string SamePathTunaProbeAckId,
    FileTransferRoute TargetRoute,
    int TargetProtocolVersion,
    FileTransferTransportKind TargetTransport,
    int CommitGeneration,
    string Reason)
{
    public bool IsDefault => CommitGeneration <= 0 ||
                             RuntimeOwner.IsDefault ||
                             RuntimeUnlockAdmission.IsDefault;
}

internal readonly record struct RoutePublicationToken(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    RuntimeOwnerToken RuntimeOwner,
    FileTransferRoute Route,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int RuntimeGeneration,
    int TerminalGeneration,
    int LegGeneration,
    int? RouteCommitGeneration,
    FileTransferRoute TargetRoute,
    int TargetProtocolVersion,
    FileTransferTransportKind TargetTransport,
    int PublicationGeneration,
    FileTransferCoordinatorActionKind ActionKind,
    string Reason)
{
    public bool IsDefault => PublicationGeneration <= 0 || RuntimeOwner.IsDefault;
}

internal readonly record struct RoutePublicationDecision(
    FileTransferCoordinatorActionKind ActionKind,
    bool Accepted,
    string RejectionReason,
    FileTransferRoute CurrentRoute,
    string CurrentRouteToken,
    int CurrentProtocolVersion,
    int CurrentLiveRouteEpoch,
    int CurrentRuntimeGeneration,
    int CurrentTerminalGeneration,
    int CurrentLegGeneration,
    RouteCommitToken? RouteCommitToken,
    RoutePublicationToken? RoutePublicationToken);

internal readonly record struct FallbackControlPlaneToken(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    RuntimeOwnerToken RuntimeOwner,
    FileTransferRoute Route,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int RuntimeGeneration,
    int TerminalGeneration,
    int LegGeneration,
    long TransportEpoch,
    int BridgeRecoveryGeneration,
    string? CheckpointRequestId,
    int CheckpointGeneration,
    FileTransferControlPlaneKind ControlPlaneKind,
    string SendPurpose,
    int TokenGeneration,
    string Reason)
{
    public bool IsDefault => TokenGeneration <= 0 || RuntimeOwner.IsDefault;
}

internal readonly record struct FallbackControlPlanePressureToken(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    RuntimeOwnerToken RuntimeOwner,
    FileTransferRoute Route,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int RuntimeGeneration,
    int TerminalGeneration,
    int LegGeneration,
    long TransportEpoch,
    int BridgeRecoveryGeneration,
    string? CheckpointRequestId,
    int CheckpointGeneration,
    int PressureGeneration,
    string Reason)
{
    public bool IsDefault => PressureGeneration <= 0 || RuntimeOwner.IsDefault;
}

internal readonly record struct FallbackCheckpointLease(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    RuntimeOwnerToken RuntimeOwner,
    FileTransferRoute Route,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int RuntimeGeneration,
    int TerminalGeneration,
    int LegGeneration,
    long TransportEpoch,
    int BridgeRecoveryGeneration,
    string CheckpointRequestId,
    int CheckpointGeneration,
    int LeaseGeneration,
    FallbackCheckpointLeaseState State,
    string Reason)
{
    public bool IsDefault => LeaseGeneration <= 0 ||
                             RuntimeOwner.IsDefault ||
                             string.IsNullOrWhiteSpace(CheckpointRequestId);
}

internal readonly record struct FallbackTailYieldDecision(
    bool Accepted,
    FallbackTailYieldKind YieldKind,
    string Reason,
    int YieldGeneration,
    FileTransferRoute CurrentRoute,
    int CurrentProtocolVersion,
    int CurrentLiveRouteEpoch,
    int CurrentRuntimeGeneration,
    int CurrentTerminalGeneration,
    int CurrentLegGeneration,
    FallbackCheckpointLease? CheckpointLease,
    RuntimeUnlockFallbackSurvivalState FallbackSurvivalState);

internal readonly record struct RepairReplayDecision(
    bool Accepted,
    RepairReplayKind RepairKind,
    string Reason,
    int RepairGeneration,
    FileTransferRoute CurrentRoute,
    int CurrentProtocolVersion,
    int CurrentLiveRouteEpoch,
    int CurrentRuntimeGeneration,
    int CurrentTerminalGeneration,
    int CurrentLegGeneration,
    long TransportEpoch,
    int BridgeRecoveryGeneration,
    RuntimeOwnerToken RuntimeOwner)
{
    public bool IsDefault => RepairGeneration <= 0 || RuntimeOwner.IsDefault;
}

internal readonly record struct TransportEpochDecision(
    bool Accepted,
    TransportEpochActionKind ActionKind,
    string Reason,
    int TransportDecisionGeneration,
    FileTransferRoute CurrentRoute,
    int CurrentProtocolVersion,
    int CurrentLiveRouteEpoch,
    int CurrentRuntimeGeneration,
    int CurrentTerminalGeneration,
    int CurrentLegGeneration,
    long TransportEpoch,
    int BridgeRecoveryGeneration,
    FileTransferRoute TargetRoute,
    int TargetProtocolVersion,
    FileTransferTransportKind TargetTransport,
    RuntimeOwnerToken RuntimeOwner)
{
    public bool IsDefault => TransportDecisionGeneration <= 0 || RuntimeOwner.IsDefault;
}

internal readonly record struct BridgeRecoveryDecision(
    bool Accepted,
    BridgeRecoveryKind RecoveryKind,
    string Reason,
    int BridgeDecisionGeneration,
    FileTransferRoute CurrentRoute,
    int CurrentProtocolVersion,
    int CurrentLiveRouteEpoch,
    int CurrentRuntimeGeneration,
    int CurrentTerminalGeneration,
    int CurrentLegGeneration,
    long TransportEpoch,
    int BridgeRecoveryGeneration,
    RuntimeOwnerToken RuntimeOwner)
{
    public bool IsDefault => BridgeDecisionGeneration <= 0 || RuntimeOwner.IsDefault;
}

internal readonly record struct RecoveryLivenessLease(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    RuntimeOwnerToken RuntimeOwner,
    FileTransferRoute Route,
    string RouteToken,
    int ProtocolVersion,
    int LiveRouteEpoch,
    int RuntimeGeneration,
    int TerminalGeneration,
    int LegGeneration,
    long TransportEpoch,
    int BridgeRecoveryGeneration,
    int LeaseGeneration,
    RecoveryLivenessLeaseState State,
    string Reason)
{
    public bool IsDefault => LeaseGeneration <= 0 ||
                             RuntimeOwner.IsDefault ||
                             State == RecoveryLivenessLeaseState.None;
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
    IReadOnlyList<FileTransferLeg> LegHistory,
    RuntimeUnlockFallbackSurvivalState RuntimeUnlockFallbackSurvivalState = default,
    RuntimeUnlockAdmissionToken? CurrentRuntimeUnlockAdmission = null,
    int LastRuntimeUnlockAdmissionGeneration = 0,
    RouteCommitToken? CurrentRouteCommit = null,
    int LastRouteCommitGeneration = 0,
    FallbackControlPlaneToken? CurrentFallbackControlPlaneToken = null,
    int LastFallbackControlPlaneTokenGeneration = 0,
    FallbackCheckpointLease? CurrentFallbackCheckpointLease = null,
    int LastFallbackCheckpointLeaseGeneration = 0,
    FallbackControlPlanePressureToken? CurrentFallbackControlPlanePressureToken = null,
    int LastFallbackControlPlanePressureGeneration = 0,
    FallbackTailYieldDecision? LastFallbackTailYieldDecision = null,
    int LastFallbackTailYieldGeneration = 0,
    RepairReplayDecision? CurrentRepairReplayDecision = null,
    int LastRepairReplayGeneration = 0,
    TransportEpochDecision? CurrentTransportEpochDecision = null,
    int LastTransportEpochDecisionGeneration = 0,
    BridgeRecoveryDecision? CurrentBridgeRecoveryDecision = null,
    int LastBridgeRecoveryDecisionGeneration = 0,
    RecoveryLivenessLease? CurrentRecoveryLivenessLease = null,
    int LastRecoveryLivenessLeaseGeneration = 0,
    RoutePublicationToken? CurrentRoutePublicationToken = null,
    int LastRoutePublicationGeneration = 0,
    RuntimeUnlockPendingPreCommitProbeRequest? CurrentRuntimeUnlockPendingPreCommitProbe = null,
    int LastRuntimeUnlockPendingPreCommitProbeGeneration = 0)
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
    RuntimeUnlockAdmissionRequest? RuntimeUnlockAdmissionRequest = null,
    RuntimeUnlockFallbackSurvivalState? RuntimeUnlockFallbackSurvivalState = null,
    bool FreezeCurrentLeg = true,
    FallbackControlPlaneToken? FallbackControlPlaneToken = null,
    FallbackCheckpointLease? FallbackCheckpointLease = null,
    FallbackControlPlanePressureToken? FallbackControlPlanePressureToken = null,
    FileTransferControlPlaneKind ControlPlaneKind = FileTransferControlPlaneKind.Unknown,
    string? ControlPlanePurpose = null,
    bool ControlPlanePeerVisible = false,
    bool ControlPlaneAcceptedAny = false,
    FallbackTailYieldKind FallbackTailYieldKind = FallbackTailYieldKind.Unknown,
    FileTransferInitialTunaReadinessEvidence? InitialTunaReadinessEvidence = null,
    bool AllowInitialRouteFallback = false,
    FileTransferRouteSelection? InitialRouteFallbackSelection = null,
    RuntimeUnlockPendingPreCommitProbeRequest? RuntimeUnlockPendingPreCommitProbeRequest = null);

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
    string? CheckpointRequestId = null,
    RuntimeUnlockAdmissionToken? RuntimeUnlockAdmissionToken = null,
    RouteCommitToken? RouteCommitToken = null,
    long? RuntimeUnlockTransactionGeneration = null,
    long? RuntimeUnlockOfferGeneration = null,
    long? RuntimeUnlockTunaPathLeaseGeneration = null,
    string? RuntimeUnlockTunaPathLeaseListenerRunId = null,
    string? RuntimeUnlockProbeId = null,
    bool? RuntimeUnlockProbeAcked = null,
    string? RuntimeUnlockSamePathTunaProbeAckId = null,
    int? RouteCommitGeneration = null,
    FileTransferRoute? TargetRoute = null,
    int? TargetProtocolVersion = null,
    FileTransferTransportKind? TargetTransport = null,
    FallbackControlPlaneToken? FallbackControlPlaneToken = null,
    FallbackCheckpointLease? FallbackCheckpointLease = null,
    FallbackControlPlanePressureToken? FallbackControlPlanePressureToken = null,
    FileTransferControlPlaneKind? ControlPlaneKind = null,
    string? ControlPlanePurpose = null,
    bool? ControlPlanePeerVisible = null,
    bool? ControlPlaneAcceptedAny = null,
    int? CheckpointGeneration = null,
    FallbackTailYieldKind? FallbackTailYieldKind = null,
    RepairReplayKind? RepairReplayKind = null,
    TransportEpochActionKind? TransportEpochActionKind = null,
    BridgeRecoveryKind? BridgeRecoveryKind = null,
    RecoveryLivenessLease? RecoveryLivenessLease = null,
    int? RecoveryDecisionGeneration = null,
    int? RuntimeGeneration = null,
    int? TerminalGeneration = null,
    RoutePublicationToken? RoutePublicationToken = null,
    RuntimeUnlockPendingPreCommitProbeRequest? RuntimeUnlockPendingPreCommitProbeRequest = null);

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
    int CurrentLegGeneration,
    FallbackControlPlaneToken? FallbackControlPlaneToken = null,
    FallbackCheckpointLease? FallbackCheckpointLease = null,
    FallbackControlPlanePressureToken? FallbackControlPlanePressureToken = null,
    FallbackTailYieldDecision? FallbackTailYieldDecision = null,
    bool ControlPlanePeerVisibleProofAccepted = false,
    RepairReplayDecision? RepairReplayDecision = null,
    TransportEpochDecision? TransportEpochDecision = null,
    BridgeRecoveryDecision? BridgeRecoveryDecision = null,
    RecoveryLivenessLease? RecoveryLivenessLease = null,
    RoutePublicationDecision? RoutePublicationDecision = null,
    RoutePublicationToken? RoutePublicationToken = null,
    RuntimeUnlockPendingPreCommitProbeDecision? RuntimeUnlockPendingPreCommitProbeDecision = null,
    RuntimeUnlockPendingPreCommitProbeRequest? RuntimeUnlockPendingPreCommitProbeRequest = null)
{
    public FileTransferCoordinatorState State { get; init; } = null!;
}

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
    FileTransferLeg? MutatedLeg = null,
    RuntimeUnlockAdmissionDecision? RuntimeUnlockAdmissionDecision = null,
    RouteCommitToken? RouteCommitToken = null,
    FallbackControlPlaneToken? FallbackControlPlaneToken = null,
    FallbackCheckpointLease? FallbackCheckpointLease = null,
    FallbackControlPlanePressureToken? FallbackControlPlanePressureToken = null,
    FallbackTailYieldDecision? FallbackTailYieldDecision = null,
    bool ControlPlanePeerVisibleProofAccepted = false,
    InitialTunaRouteSelectionDecision? InitialTunaRouteSelectionDecision = null,
    RuntimeUnlockPendingPreCommitProbeDecision? RuntimeUnlockPendingPreCommitProbeDecision = null);

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
    private static readonly TimeSpan InitialTunaReadinessEvidenceMaxAge = TimeSpan.FromSeconds(30);

    public static FileTransferCoordinatorDecision Apply(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state)
    {
        var reason = NormalizeReason(coordinatorEvent.Reason);
        if (state.IsTerminal)
        {
            if (coordinatorEvent.Kind == FileTransferCoordinatorEventKind.RuntimeUnlockAdmission)
            {
                var admission = AdmissionDecision(
                    state,
                    RuntimeUnlockAdmissionStatus.RejectedTerminal,
                    "transfer_terminal",
                    NormalizeRuntimeUnlockFallbackSurvivalState(state));
                return Decision(state, reason, runtimeUnlockAdmissionDecision: admission);
            }

            if (coordinatorEvent.Kind == FileTransferCoordinatorEventKind.RuntimeUnlockCommitRequested)
            {
                return Decision(state, reason) with
                {
                    RuntimeUnlockCommitRejectedReason = "transfer_terminal",
                };
            }

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
            FileTransferCoordinatorEventKind.FallbackControlPlaneSendAuthorized => AuthorizeFallbackControlPlaneSend(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.FallbackControlPlaneSendCompleted => CompleteFallbackControlPlaneSend(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.FallbackControlPlanePressureUpdated => UpdateFallbackControlPlanePressure(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.RuntimeUnlockFallbackSurvivalUpdated => UpdateRuntimeUnlockFallbackSurvival(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.RuntimeUnlockAdmission => ApplyRuntimeUnlockAdmission(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.RuntimeUnlockAdmissionRetired => RetireRuntimeUnlockAdmission(state, reason),
            FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbePending => ApplyRuntimeUnlockPreCommitProbePending(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbeReplayed => ApplyRuntimeUnlockPreCommitProbeReplayed(coordinatorEvent, state, reason),
            FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbeRetired => RetireRuntimeUnlockPreCommitProbe(coordinatorEvent, state, reason),
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

        var effectiveRuntimeOwner = RequiresRouteCommitToken(request.Kind)
            ? request.RouteCommitToken?.RuntimeOwner ??
              request.RuntimeOwner ??
              request.RuntimeUnlockAdmissionToken?.RuntimeOwner ??
              request.RoutePublicationToken?.RuntimeOwner ??
              request.RuntimeUnlockPendingPreCommitProbeRequest?.RuntimeOwner ??
              request.FallbackControlPlaneToken?.RuntimeOwner ??
              request.FallbackCheckpointLease?.RuntimeOwner ??
              request.FallbackControlPlanePressureToken?.RuntimeOwner ??
              request.RecoveryLivenessLease?.RuntimeOwner
            : request.RuntimeOwner ??
              request.RuntimeUnlockAdmissionToken?.RuntimeOwner ??
              request.RouteCommitToken?.RuntimeOwner ??
              request.RoutePublicationToken?.RuntimeOwner ??
              request.RuntimeUnlockPendingPreCommitProbeRequest?.RuntimeOwner ??
              request.FallbackControlPlaneToken?.RuntimeOwner ??
              request.FallbackCheckpointLease?.RuntimeOwner ??
              request.FallbackControlPlanePressureToken?.RuntimeOwner ??
              request.RecoveryLivenessLease?.RuntimeOwner;
        if (effectiveRuntimeOwner is { } runtimeOwner)
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

        if (request.RuntimeGeneration is { } runtimeGeneration &&
            runtimeGeneration != (state.CurrentRuntimeOwner?.RuntimeGeneration ?? 0))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                rejectionReason: "runtime_generation_mismatch");
        }

        if (request.TerminalGeneration is { } terminalGeneration &&
            terminalGeneration != state.TerminalGeneration)
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                rejectionReason: "terminal_generation_mismatch");
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

        if (request.Kind != FileTransferCoordinatorActionKind.FallbackCheckpointBegin &&
            request.CheckpointRequestId is { } checkpointRequestId &&
            (state.CurrentLeg is null ||
             !string.Equals(checkpointRequestId, state.CurrentLeg.CheckpointRequestId, StringComparison.Ordinal)))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                rejectionReason: state.CurrentLeg is null ? "leg_missing" : "checkpoint_request_mismatch");
        }

        if (request.CheckpointGeneration is { } checkpointGeneration &&
            (state.CurrentLeg is null || checkpointGeneration != state.CurrentLeg.CheckpointGeneration))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                rejectionReason: state.CurrentLeg is null ? "leg_missing" : "checkpoint_generation_mismatch");
        }

        if (RequiresRuntimeUnlockAdmissionToken(request.Kind))
        {
            if (request.RuntimeUnlockAdmissionToken is not { } admissionToken)
            {
                return ActionDecision(
                    state,
                    request.Kind,
                    accepted: false,
                    rejectionReason: "runtime_unlock_admission_missing");
            }

            if (!IsRuntimeUnlockAdmissionTokenCurrent(state, admissionToken, out var admissionRejectionReason))
            {
                return ActionDecision(
                    state,
                    request.Kind,
                    accepted: false,
                    admissionRejectionReason);
            }
        }
        else if (request.RouteCommitToken is null &&
                 !RequiresRouteCommitToken(request.Kind) &&
                 request.RuntimeUnlockAdmissionToken is { } optionalAdmissionToken &&
                 !IsRuntimeUnlockAdmissionTokenCurrent(state, optionalAdmissionToken, out var optionalAdmissionRejectionReason))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                optionalAdmissionRejectionReason);
        }

        if (RequiresRouteCommitToken(request.Kind))
        {
            if (request.RouteCommitToken is not { } routeCommitToken)
            {
                return ActionDecision(
                    state,
                    request.Kind,
                    accepted: false,
                    rejectionReason: "route_commit_token_missing");
            }

            if (!IsRouteCommitTokenCurrent(state, routeCommitToken, out var routeCommitRejectionReason))
            {
                return ActionDecision(
                    state,
                    request.Kind,
                    accepted: false,
                    routeCommitRejectionReason);
            }
        }
        else if (request.RouteCommitToken is { } optionalRouteCommitToken &&
                 !IsRouteCommitTokenCurrent(state, optionalRouteCommitToken, out var optionalRouteCommitRejectionReason))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                optionalRouteCommitRejectionReason);
        }

        if (request.RoutePublicationToken is { } routePublicationToken &&
            !IsRoutePublicationTokenCurrent(state, routePublicationToken, out var routePublicationRejectionReason))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                routePublicationRejectionReason);
        }

        if (!ValidateRuntimeUnlockCallbackMetadata(request, out var callbackRejectionReason))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                callbackRejectionReason);
        }

        if (!ValidateRuntimeUnlockPendingPreCommitProbeAction(state, request, out var pendingProbeRejectionReason))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                pendingProbeRejectionReason);
        }

        if (!ValidateRoutePublicationAction(state, request, out var publicationRejectionReason))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                publicationRejectionReason);
        }

        if (!ValidateFallbackOwnershipAction(state, request, out var fallbackRejectionReason))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                fallbackRejectionReason);
        }

        if (!ValidateRecoveryAuthorityAction(state, request, out var recoveryRejectionReason))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                recoveryRejectionReason);
        }

        var issuedControlPlaneToken = request.Kind == FileTransferCoordinatorActionKind.ControlPlaneSend
            ? IssueFallbackControlPlaneToken(
                state,
                request.ControlPlaneKind ?? FileTransferControlPlaneKind.Unknown,
                request.ControlPlanePurpose ?? request.Reason,
                Math.Max(1, state.LastFallbackControlPlaneTokenGeneration + 1),
                request.Reason)
            : request.FallbackControlPlaneToken;
        var issuedCheckpointLease = request.Kind == FileTransferCoordinatorActionKind.FallbackCheckpointBegin
            ? IssueFallbackCheckpointLease(
                state,
                request.CheckpointRequestId,
                request.TransportEpochId,
                request.BridgeRecoveryGeneration,
                request.CheckpointGeneration,
                Math.Max(1, state.LastFallbackCheckpointLeaseGeneration + 1),
                FallbackCheckpointLeaseState.Pending,
                request.Reason)
            : request.FallbackCheckpointLease;
        var issuedPressureToken = request.Kind == FileTransferCoordinatorActionKind.FallbackControlPlanePressure
            ? IssueFallbackControlPlanePressureToken(
                state,
                Math.Max(1, state.LastFallbackControlPlanePressureGeneration + 1),
                request.Reason)
            : request.FallbackControlPlanePressureToken;
        var yieldDecision = request.Kind is FileTransferCoordinatorActionKind.FallbackTailQuiescent or
            FileTransferCoordinatorActionKind.FallbackTailFailed
                ? CreateFallbackTailYieldDecision(
                    state,
                    accepted: true,
                    request.FallbackTailYieldKind ??
                    (request.Kind == FileTransferCoordinatorActionKind.FallbackTailFailed
                        ? FallbackTailYieldKind.Failed
                        : FallbackTailYieldKind.Quiescent),
                    "accepted",
                    Math.Max(1, state.LastFallbackTailYieldGeneration + 1))
                : (FallbackTailYieldDecision?)null;
        var repairReplayDecision = request.Kind == FileTransferCoordinatorActionKind.RepairReplay
            ? CreateRepairReplayDecision(
                state,
                request.RepairReplayKind ?? RepairReplayKind.Unknown,
                request.Reason,
                Math.Max(1, state.LastRepairReplayGeneration + 1))
            : (RepairReplayDecision?)null;
        var transportEpochDecision = IsTransportEpochAuthorityAction(request.Kind)
            ? CreateTransportEpochDecision(
                state,
                request.TransportEpochActionKind ?? DefaultTransportEpochActionKind(request.Kind),
                request.Reason,
                Math.Max(1, state.LastTransportEpochDecisionGeneration + 1),
                request.TargetRoute,
                request.TargetProtocolVersion,
                request.TargetTransport)
            : (TransportEpochDecision?)null;
        var bridgeRecoveryDecision = IsBridgeRecoveryAuthorityAction(request.Kind)
            ? CreateBridgeRecoveryDecision(
                state,
                request.BridgeRecoveryKind ?? DefaultBridgeRecoveryKind(request.Kind),
                request.Reason,
                Math.Max(1, state.LastBridgeRecoveryDecisionGeneration + 1))
            : (BridgeRecoveryDecision?)null;
        var recoveryLivenessLease = request.Kind switch
        {
            FileTransferCoordinatorActionKind.LivenessDeferral or
                FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin
                => IssueRecoveryLivenessLease(
                    state,
                    Math.Max(1, state.LastRecoveryLivenessLeaseGeneration + 1),
                    RecoveryLivenessLeaseState.Active,
                    request.Reason),
            FileTransferCoordinatorActionKind.RecoveryLivenessLeaseValidate
                => request.RecoveryLivenessLease,
            FileTransferCoordinatorActionKind.RecoveryLivenessLeaseRetire
                => request.RecoveryLivenessLease is { } lease
                    ? lease with
                    {
                        State = RecoveryLivenessLeaseState.Retired,
                        Reason = NormalizeReason(request.Reason),
                    }
                    : null,
            _ => null,
        };

        var routePublicationDecision = IsRoutePublicationAuthorityAction(request.Kind)
            ? CreateRoutePublicationDecision(
                state,
                request,
                accepted: true,
                rejectionReason: "accepted",
                Math.Max(1, state.LastRoutePublicationGeneration + 1))
            : (RoutePublicationDecision?)null;
        var pendingPreCommitProbeDecision =
            request.Kind == FileTransferCoordinatorActionKind.RuntimeUnlockPendingPreCommitProbeReplay
                ? CreateRuntimeUnlockPendingPreCommitProbeDecision(
                    state,
                    accepted: true,
                    rejectionReason: "accepted",
                    request.RuntimeUnlockPendingPreCommitProbeRequest)
                : (RuntimeUnlockPendingPreCommitProbeDecision?)null;

        var acceptedState = ApplyIssuedRecoveryAuthority(
            state,
            request.Kind,
            repairReplayDecision,
            transportEpochDecision,
            bridgeRecoveryDecision,
            recoveryLivenessLease);
        acceptedState = ApplyIssuedRoutePublicationAuthority(acceptedState, routePublicationDecision);

        return ActionDecision(
            acceptedState,
            request.Kind,
            accepted: true,
            rejectionReason: "accepted",
            fallbackControlPlaneToken: issuedControlPlaneToken,
            fallbackCheckpointLease: issuedCheckpointLease,
            fallbackControlPlanePressureToken: issuedPressureToken,
            fallbackTailYieldDecision: yieldDecision,
            controlPlanePeerVisibleProofAccepted:
                request.Kind == FileTransferCoordinatorActionKind.ControlPlaneSendCompletion &&
                request.ControlPlanePeerVisible == true,
            repairReplayDecision: repairReplayDecision,
            transportEpochDecision: transportEpochDecision,
            bridgeRecoveryDecision: bridgeRecoveryDecision,
            recoveryLivenessLease: recoveryLivenessLease,
            routePublicationDecision: routePublicationDecision,
            routePublicationToken: routePublicationDecision?.RoutePublicationToken,
            runtimeUnlockPendingPreCommitProbeDecision: pendingPreCommitProbeDecision,
            runtimeUnlockPendingPreCommitProbeRequest: pendingPreCommitProbeDecision?.PendingRequest);
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

    public static bool IsRuntimeUnlockAdmissionTokenCurrent(
        FileTransferCoordinatorState state,
        RuntimeUnlockAdmissionToken token,
        out string rejectionReason)
    {
        rejectionReason = "current";
        if (token.IsDefault)
        {
            rejectionReason = "runtime_unlock_admission_missing";
            return false;
        }

        if (!string.Equals(token.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(token.TransferId, state.TransferId, StringComparison.Ordinal) ||
            token.Direction != state.Direction)
        {
            rejectionReason = "session_transfer_or_direction_mismatch";
            return false;
        }

        if (state.CurrentRuntimeUnlockAdmission is not { } current ||
            current.AdmissionGeneration != token.AdmissionGeneration)
        {
            rejectionReason = "runtime_unlock_admission_generation_mismatch";
            return false;
        }

        if (!IsRuntimeOwnerCurrent(state, token.RuntimeOwner, out rejectionReason))
        {
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

        if (token.TerminalGeneration != state.TerminalGeneration)
        {
            rejectionReason = "terminal_generation_mismatch";
            return false;
        }

        if (token.LegGeneration != (state.CurrentLeg?.Generation ?? 0))
        {
            rejectionReason = "leg_generation_mismatch";
            return false;
        }

        return true;
    }

    public static bool IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(
        FileTransferCoordinatorState state,
        RuntimeUnlockPendingPreCommitProbeRequest request,
        out string rejectionReason)
    {
        rejectionReason = "current";
        if (request.IsDefault)
        {
            rejectionReason = "runtime_unlock_pending_precommit_probe_missing";
            return false;
        }

        if (!string.Equals(request.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(request.TransferId, state.TransferId, StringComparison.Ordinal) ||
            request.Direction != state.Direction)
        {
            rejectionReason = "session_transfer_or_direction_mismatch";
            return false;
        }

        if (state.IsTerminal)
        {
            rejectionReason = "transfer_terminal";
            return false;
        }

        if (state.CurrentRuntimeUnlockPendingPreCommitProbe is not { } current ||
            current.PendingGeneration != request.PendingGeneration)
        {
            rejectionReason = "runtime_unlock_pending_precommit_probe_generation_mismatch";
            return false;
        }

        if (!IsRuntimeOwnerCurrent(state, request.RuntimeOwner, out rejectionReason))
        {
            return false;
        }

        if (!IsRuntimeUnlockAdmissionTokenCurrent(state, request.AdmissionToken, out rejectionReason))
        {
            return false;
        }

        if (request.Route != state.RouteSelection.Route ||
            request.ProtocolVersion != state.RouteSelection.ProtocolVersion ||
            !string.Equals(request.RouteToken, state.RouteSelection.TelemetryToken, StringComparison.Ordinal))
        {
            rejectionReason = "route_or_protocol_mismatch";
            return false;
        }

        var expectedLiveRouteEpoch = state.CurrentLiveRouteEpoch?.EpochId ?? 0;
        if (request.LiveRouteEpoch != expectedLiveRouteEpoch)
        {
            rejectionReason = "live_route_epoch_mismatch";
            return false;
        }

        if (request.RuntimeGeneration != (state.CurrentRuntimeOwner?.RuntimeGeneration ?? 0))
        {
            rejectionReason = "runtime_generation_mismatch";
            return false;
        }

        if (request.TerminalGeneration != state.TerminalGeneration)
        {
            rejectionReason = "terminal_generation_mismatch";
            return false;
        }

        if (request.LegGeneration != (state.CurrentLeg?.Generation ?? 0))
        {
            rejectionReason = "leg_generation_mismatch";
            return false;
        }

        if (!RuntimeUnlockPendingPreCommitProbeMetadataIsValid(request, out rejectionReason))
        {
            return false;
        }

        if (!RuntimeUnlockPendingPreCommitProbeMetadataMatches(current, request, out rejectionReason))
        {
            return false;
        }

        return true;
    }

    public static bool IsRouteCommitTokenCurrent(
        FileTransferCoordinatorState state,
        RouteCommitToken token,
        out string rejectionReason)
    {
        rejectionReason = "current";
        if (token.IsDefault)
        {
            rejectionReason = "route_commit_token_missing";
            return false;
        }

        if (!string.Equals(token.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(token.TransferId, state.TransferId, StringComparison.Ordinal) ||
            token.Direction != state.Direction)
        {
            rejectionReason = "session_transfer_or_direction_mismatch";
            return false;
        }

        if (state.CurrentRouteCommit is not { } current ||
            current.CommitGeneration != token.CommitGeneration)
        {
            rejectionReason = "route_commit_generation_mismatch";
            return false;
        }

        if (!IsRuntimeOwnerCurrent(state, token.RuntimeOwner, out rejectionReason))
        {
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

        if (token.TerminalGeneration != state.TerminalGeneration)
        {
            rejectionReason = "terminal_generation_mismatch";
            return false;
        }

        if (token.LegGeneration != (state.CurrentLeg?.Generation ?? 0))
        {
            rejectionReason = "leg_generation_mismatch";
            return false;
        }

        if (token.TargetRoute != FileTransferRoute.FileTunaV4 ||
            token.TargetProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            token.TargetTransport != FileTransferTransportKind.Tuna)
        {
            rejectionReason = "route_commit_metadata_invalid";
            return false;
        }

        if (state.IsTerminal)
        {
            rejectionReason = "transfer_terminal";
            return false;
        }

        return true;
    }

    public static bool IsRoutePublicationTokenCurrent(
        FileTransferCoordinatorState state,
        RoutePublicationToken token,
        out string rejectionReason)
    {
        rejectionReason = "current";
        if (token.IsDefault)
        {
            rejectionReason = "route_publication_token_missing";
            return false;
        }

        if (!string.Equals(token.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(token.TransferId, state.TransferId, StringComparison.Ordinal) ||
            token.Direction != state.Direction)
        {
            rejectionReason = "session_transfer_or_direction_mismatch";
            return false;
        }

        if (state.IsTerminal)
        {
            rejectionReason = "transfer_terminal";
            return false;
        }

        if (state.CurrentRoutePublicationToken is not { } current ||
            current.PublicationGeneration != token.PublicationGeneration)
        {
            rejectionReason = "route_publication_generation_mismatch";
            return false;
        }

        if (current.ActionKind != token.ActionKind ||
            current.RouteCommitGeneration != token.RouteCommitGeneration ||
            current.TargetRoute != token.TargetRoute ||
            current.TargetProtocolVersion != token.TargetProtocolVersion ||
            current.TargetTransport != token.TargetTransport)
        {
            rejectionReason = "route_publication_token_mismatch";
            return false;
        }

        if (!IsRuntimeOwnerCurrent(state, token.RuntimeOwner, out rejectionReason))
        {
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

        if (token.RuntimeGeneration != (state.CurrentRuntimeOwner?.RuntimeGeneration ?? 0))
        {
            rejectionReason = "runtime_generation_mismatch";
            return false;
        }

        if (token.TerminalGeneration != state.TerminalGeneration)
        {
            rejectionReason = "terminal_generation_mismatch";
            return false;
        }

        if (token.LegGeneration != (state.CurrentLeg?.Generation ?? 0))
        {
            rejectionReason = "leg_generation_mismatch";
            return false;
        }

        if (token.ActionKind == FileTransferCoordinatorActionKind.RouteCommitPublication)
        {
            if (token.RouteCommitGeneration is not { } routeCommitGeneration ||
                state.CurrentRouteCommit?.CommitGeneration != routeCommitGeneration)
            {
                rejectionReason = "route_commit_generation_mismatch";
                return false;
            }
        }
        else if (token.RouteCommitGeneration is { } routeCommitGeneration &&
                 state.CurrentRouteCommit?.CommitGeneration != routeCommitGeneration)
        {
            rejectionReason = "route_commit_generation_mismatch";
            return false;
        }

        if (!IsLegalRoutePublicationTarget(
                token.TargetRoute,
                token.TargetProtocolVersion,
                token.TargetTransport,
                out rejectionReason))
        {
            return false;
        }

        return true;
    }

    public static bool IsFallbackControlPlaneTokenCurrent(
        FileTransferCoordinatorState state,
        FallbackControlPlaneToken token,
        out string rejectionReason)
        => IsFallbackControlPlaneTokenCurrent(
            state,
            token,
            allowNextGeneration: false,
            allowIssuedGeneration: false,
            out rejectionReason);

    private static bool IsFallbackControlPlaneTokenCurrent(
        FileTransferCoordinatorState state,
        FallbackControlPlaneToken token,
        bool allowNextGeneration,
        bool allowIssuedGeneration,
        out string rejectionReason)
    {
        rejectionReason = "current";
        if (token.IsDefault)
        {
            rejectionReason = "fallback_control_plane_token_missing";
            return false;
        }

        if (!string.Equals(token.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(token.TransferId, state.TransferId, StringComparison.Ordinal) ||
            token.Direction != state.Direction)
        {
            rejectionReason = "session_transfer_or_direction_mismatch";
            return false;
        }

        if (!IsRuntimeOwnerCurrent(state, token.RuntimeOwner, out rejectionReason))
        {
            return false;
        }

        if (allowIssuedGeneration)
        {
            if (token.TokenGeneration <= 0 ||
                token.TokenGeneration > state.LastFallbackControlPlaneTokenGeneration)
            {
                rejectionReason = "fallback_control_plane_token_generation_mismatch";
                return false;
            }
        }
        else if (!allowNextGeneration)
        {
            if (state.CurrentFallbackControlPlaneToken is not { } current ||
                current.TokenGeneration != token.TokenGeneration)
            {
                rejectionReason = "fallback_control_plane_token_generation_mismatch";
                return false;
            }
        }
        else if (token.TokenGeneration != Math.Max(1, state.LastFallbackControlPlaneTokenGeneration + 1) &&
                 token.TokenGeneration != state.CurrentFallbackControlPlaneToken?.TokenGeneration)
        {
            rejectionReason = "fallback_control_plane_token_generation_mismatch";
            return false;
        }

        return ValidateFallbackAuthorityFields(
            state,
            token.Route,
            token.RouteToken,
            token.ProtocolVersion,
            token.LiveRouteEpoch,
            token.RuntimeGeneration,
            token.TerminalGeneration,
            token.LegGeneration,
            token.TransportEpoch,
            token.BridgeRecoveryGeneration,
            token.CheckpointRequestId,
            token.CheckpointGeneration,
            out rejectionReason);
    }

    public static bool IsFallbackCheckpointLeaseCurrent(
        FileTransferCoordinatorState state,
        FallbackCheckpointLease lease,
        out string rejectionReason)
    {
        rejectionReason = "current";
        if (lease.IsDefault)
        {
            rejectionReason = "fallback_checkpoint_lease_missing";
            return false;
        }

        if (!string.Equals(lease.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(lease.TransferId, state.TransferId, StringComparison.Ordinal) ||
            lease.Direction != state.Direction)
        {
            rejectionReason = "session_transfer_or_direction_mismatch";
            return false;
        }

        if (lease.State != FallbackCheckpointLeaseState.Pending)
        {
            rejectionReason = "fallback_checkpoint_lease_not_pending";
            return false;
        }

        if (state.CurrentFallbackCheckpointLease is not { } current ||
            current.LeaseGeneration != lease.LeaseGeneration)
        {
            rejectionReason = "fallback_checkpoint_lease_generation_mismatch";
            return false;
        }

        if (!IsRuntimeOwnerCurrent(state, lease.RuntimeOwner, out rejectionReason))
        {
            return false;
        }

        return ValidateFallbackAuthorityFields(
            state,
            lease.Route,
            lease.RouteToken,
            lease.ProtocolVersion,
            lease.LiveRouteEpoch,
            lease.RuntimeGeneration,
            lease.TerminalGeneration,
            lease.LegGeneration,
            lease.TransportEpoch,
            lease.BridgeRecoveryGeneration,
            lease.CheckpointRequestId,
            lease.CheckpointGeneration,
            out rejectionReason);
    }

    public static bool IsFallbackControlPlanePressureTokenCurrent(
        FileTransferCoordinatorState state,
        FallbackControlPlanePressureToken token,
        out string rejectionReason)
        => IsFallbackControlPlanePressureTokenCurrent(state, token, allowNextGeneration: false, out rejectionReason);

    private static bool IsFallbackControlPlanePressureTokenCurrent(
        FileTransferCoordinatorState state,
        FallbackControlPlanePressureToken token,
        bool allowNextGeneration,
        out string rejectionReason)
    {
        rejectionReason = "current";
        if (token.IsDefault)
        {
            rejectionReason = "fallback_control_plane_pressure_token_missing";
            return false;
        }

        if (!string.Equals(token.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(token.TransferId, state.TransferId, StringComparison.Ordinal) ||
            token.Direction != state.Direction)
        {
            rejectionReason = "session_transfer_or_direction_mismatch";
            return false;
        }

        if (!IsRuntimeOwnerCurrent(state, token.RuntimeOwner, out rejectionReason))
        {
            return false;
        }

        if (!allowNextGeneration)
        {
            if (state.CurrentFallbackControlPlanePressureToken is not { } current ||
                current.PressureGeneration != token.PressureGeneration)
            {
                rejectionReason = "fallback_control_plane_pressure_generation_mismatch";
                return false;
            }
        }
        else if (token.PressureGeneration != Math.Max(1, state.LastFallbackControlPlanePressureGeneration + 1) &&
                 token.PressureGeneration != state.CurrentFallbackControlPlanePressureToken?.PressureGeneration)
        {
            rejectionReason = "fallback_control_plane_pressure_generation_mismatch";
            return false;
        }

        return ValidateFallbackAuthorityFields(
            state,
            token.Route,
            token.RouteToken,
            token.ProtocolVersion,
            token.LiveRouteEpoch,
            token.RuntimeGeneration,
            token.TerminalGeneration,
            token.LegGeneration,
            token.TransportEpoch,
            token.BridgeRecoveryGeneration,
            token.CheckpointRequestId,
            token.CheckpointGeneration,
            out rejectionReason);
    }

    public static bool IsRecoveryLivenessLeaseCurrent(
        FileTransferCoordinatorState state,
        RecoveryLivenessLease lease,
        out string rejectionReason)
    {
        rejectionReason = "current";
        if (lease.IsDefault)
        {
            rejectionReason = "recovery_liveness_lease_missing";
            return false;
        }

        if (!string.Equals(lease.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(lease.TransferId, state.TransferId, StringComparison.Ordinal) ||
            lease.Direction != state.Direction)
        {
            rejectionReason = "session_transfer_or_direction_mismatch";
            return false;
        }

        if (lease.State != RecoveryLivenessLeaseState.Active)
        {
            rejectionReason = "recovery_liveness_lease_not_active";
            return false;
        }

        if (state.CurrentRecoveryLivenessLease is not { } current ||
            current.LeaseGeneration != lease.LeaseGeneration)
        {
            rejectionReason = "recovery_liveness_lease_generation_mismatch";
            return false;
        }

        if (!IsRuntimeOwnerCurrent(state, lease.RuntimeOwner, out rejectionReason))
        {
            return false;
        }

        if (lease.Route != state.RouteSelection.Route ||
            lease.ProtocolVersion != state.RouteSelection.ProtocolVersion ||
            !string.Equals(lease.RouteToken, state.RouteSelection.TelemetryToken, StringComparison.Ordinal))
        {
            rejectionReason = "route_or_protocol_mismatch";
            return false;
        }

        if (lease.LiveRouteEpoch != (state.CurrentLiveRouteEpoch?.EpochId ?? 0))
        {
            rejectionReason = "live_route_epoch_mismatch";
            return false;
        }

        if (lease.RuntimeGeneration != (state.CurrentRuntimeOwner?.RuntimeGeneration ?? 0))
        {
            rejectionReason = "runtime_generation_mismatch";
            return false;
        }

        if (lease.TerminalGeneration != state.TerminalGeneration)
        {
            rejectionReason = "terminal_generation_mismatch";
            return false;
        }

        if (state.CurrentLeg is not { } leg)
        {
            rejectionReason = "leg_missing";
            return false;
        }

        if (lease.LegGeneration != leg.Generation)
        {
            rejectionReason = "leg_generation_mismatch";
            return false;
        }

        if (lease.TransportEpoch != leg.TransportEpochId)
        {
            rejectionReason = "transport_epoch_mismatch";
            return false;
        }

        if (lease.BridgeRecoveryGeneration != leg.BridgeRecoveryGeneration)
        {
            rejectionReason = "bridge_recovery_generation_mismatch";
            return false;
        }

        if (state.IsTerminal)
        {
            rejectionReason = "transfer_terminal";
            return false;
        }

        return true;
    }

    private static bool IsTransportEpochDecisionCurrent(
        FileTransferCoordinatorState state,
        TransportEpochDecision decision,
        out string rejectionReason)
    {
        rejectionReason = "current";
        if (decision.IsDefault)
        {
            rejectionReason = "transport_epoch_decision_missing";
            return false;
        }

        if (state.CurrentTransportEpochDecision is not { } current ||
            current.TransportDecisionGeneration != decision.TransportDecisionGeneration)
        {
            rejectionReason = "transport_epoch_decision_generation_mismatch";
            return false;
        }

        if (!IsRuntimeOwnerCurrent(state, decision.RuntimeOwner, out rejectionReason))
        {
            return false;
        }

        if (decision.CurrentRoute != state.RouteSelection.Route ||
            decision.CurrentProtocolVersion != state.RouteSelection.ProtocolVersion)
        {
            rejectionReason = "route_or_protocol_mismatch";
            return false;
        }

        if (decision.CurrentLiveRouteEpoch != (state.CurrentLiveRouteEpoch?.EpochId ?? 0))
        {
            rejectionReason = "live_route_epoch_mismatch";
            return false;
        }

        if (decision.CurrentRuntimeGeneration != (state.CurrentRuntimeOwner?.RuntimeGeneration ?? 0))
        {
            rejectionReason = "runtime_generation_mismatch";
            return false;
        }

        if (decision.CurrentTerminalGeneration != state.TerminalGeneration)
        {
            rejectionReason = "terminal_generation_mismatch";
            return false;
        }

        if (state.CurrentLeg is not { } leg)
        {
            rejectionReason = "leg_missing";
            return false;
        }

        if (decision.CurrentLegGeneration != leg.Generation)
        {
            rejectionReason = "leg_generation_mismatch";
            return false;
        }

        if (decision.TransportEpoch != leg.TransportEpochId)
        {
            rejectionReason = "transport_epoch_mismatch";
            return false;
        }

        if (decision.BridgeRecoveryGeneration != leg.BridgeRecoveryGeneration)
        {
            rejectionReason = "bridge_recovery_generation_mismatch";
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

    private static RuntimeUnlockAdmissionDecision EvaluateRuntimeUnlockAdmission(
        FileTransferCoordinatorState state,
        RuntimeUnlockAdmissionRequest request,
        int admissionGeneration)
    {
        var survivalState = NormalizeRuntimeUnlockFallbackSurvivalState(state);
        if (state.IsTerminal)
        {
            return AdmissionDecision(
                state,
                RuntimeUnlockAdmissionStatus.RejectedTerminal,
                "transfer_terminal",
                survivalState);
        }

        if (request.RuntimeOwner is not { } owner)
        {
            return AdmissionDecision(
                state,
                RuntimeUnlockAdmissionStatus.RejectedStaleOwner,
                "runtime_owner_missing",
                survivalState);
        }

        if (!IsRuntimeOwnerCurrent(state, owner, out var ownerRejectionReason))
        {
            return AdmissionDecision(
                state,
                RuntimeUnlockAdmissionStatus.RejectedStaleOwner,
                ownerRejectionReason,
                survivalState);
        }

        if (request.TransactionFailed)
        {
            return AdmissionDecision(
                state,
                RuntimeUnlockAdmissionStatus.RejectedFailedTransaction,
                "runtime_unlock_transaction_failed",
                survivalState);
        }

        if (state.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
            survivalState.FallbackTerminalized)
        {
            var terminalizedToken = new RuntimeUnlockAdmissionToken(
                state.SessionId,
                state.TransferId,
                state.Direction,
                owner,
                state.RouteSelection.Route,
                state.RouteSelection.TelemetryToken,
                state.RouteSelection.ProtocolVersion,
                state.CurrentLiveRouteEpoch?.EpochId ?? 0,
                state.LastRuntimeOwnerGeneration,
                state.TerminalGeneration,
                state.CurrentLeg?.Generation ?? 0,
                Math.Max(1, admissionGeneration),
                "fallback_terminalized",
                survivalState);
            return AdmissionDecision(
                state,
                RuntimeUnlockAdmissionStatus.Granted,
                "fallback_terminalized",
                survivalState,
                terminalizedToken);
        }

        var policy = EvaluateRuntimeUnlockAdmission(
            state.IsTerminal,
            runtimeOwnerCurrent: true,
            state.RouteRuntime.UsesPostTunaFallbackV6Runtime,
            state.CurrentLeg,
            survivalState.CheckpointDeliveryRecoveryPending,
            survivalState.StateRefreshSendInFlight,
            survivalState.DeferredStateRefreshPending,
            survivalState.ProofReplayPending,
            survivalState.BridgeRecoveryPending,
            survivalState.ControlPlanePressurePending,
            survivalState.FallbackTailRepairPending,
            transactionFailed: false,
            survivalState.PairedFallbackYieldGranted);

        if (!policy.Granted)
        {
            return AdmissionDecision(state, policy.Status, policy.Reason, survivalState);
        }

        var token = new RuntimeUnlockAdmissionToken(
            state.SessionId,
            state.TransferId,
            state.Direction,
            owner,
            state.RouteSelection.Route,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            state.LastRuntimeOwnerGeneration,
            state.TerminalGeneration,
            state.CurrentLeg?.Generation ?? 0,
            Math.Max(1, admissionGeneration),
            policy.Reason,
            survivalState);

        return AdmissionDecision(state, policy.Status, policy.Reason, survivalState, token);
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

    private static RuntimeUnlockFallbackSurvivalState NormalizeRuntimeUnlockFallbackSurvivalState(
        FileTransferCoordinatorState state)
    {
        var configured = state.RuntimeUnlockFallbackSurvivalState;
        var fallbackActive = configured.FallbackActive ||
            state.RouteRuntime.UsesPostTunaFallbackV6Runtime ||
            state.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6;
        var fallbackCurrent = configured.FallbackCurrent || IsCurrentPostTunaFallbackLeg(state.CurrentLeg);
        var fallbackTerminalized = configured.FallbackTerminalized ||
            state.CurrentLeg?.State == FileTransferLegState.Terminal;

        return configured with
        {
            FallbackActive = fallbackActive,
            FallbackCurrent = fallbackCurrent,
            FallbackTerminalized = fallbackTerminalized,
        };
    }

    private static RuntimeUnlockAdmissionDecision AdmissionDecision(
        FileTransferCoordinatorState state,
        RuntimeUnlockAdmissionStatus status,
        string reason,
        RuntimeUnlockFallbackSurvivalState survivalState,
        RuntimeUnlockAdmissionToken? token = null)
        => new(
            status,
            NormalizeReason(reason),
            token,
            state.RouteSelection.Route,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            state.CurrentRuntimeOwner?.RuntimeGeneration ?? state.LastRuntimeOwnerGeneration,
            state.TerminalGeneration,
            state.CurrentLeg?.Generation ?? 0,
            survivalState);

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

    private static FileTransferCoordinatorState InvalidateFallbackOwnership(FileTransferCoordinatorState state)
        => InvalidateRecoveryAuthority(state with
        {
            CurrentFallbackControlPlaneToken = null,
            CurrentFallbackCheckpointLease = null,
            CurrentFallbackControlPlanePressureToken = null,
            LastFallbackTailYieldDecision = null,
        });

    private static FileTransferCoordinatorState InvalidateRecoveryAuthority(FileTransferCoordinatorState state)
        => state with
        {
            CurrentRepairReplayDecision = null,
            CurrentTransportEpochDecision = null,
            CurrentBridgeRecoveryDecision = null,
            CurrentRecoveryLivenessLease = null,
        };

    private static FileTransferCoordinatorState InvalidateRoutePublicationAuthority(FileTransferCoordinatorState state)
        => state with { CurrentRoutePublicationToken = null };

    private static RuntimeUnlockPendingPreCommitProbeDecision? CreateRuntimeUnlockPendingPreCommitProbeDecision(
        FileTransferCoordinatorState state,
        bool accepted,
        string rejectionReason,
        RuntimeUnlockPendingPreCommitProbeRequest? pendingRequest)
    {
        if (pendingRequest is null)
        {
            return null;
        }

        return new RuntimeUnlockPendingPreCommitProbeDecision(
            accepted,
            rejectionReason,
            state.RouteSelection.Route,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            state.CurrentRuntimeOwner?.RuntimeGeneration ?? 0,
            state.TerminalGeneration,
            state.CurrentLeg?.Generation ?? 0,
            pendingRequest);
    }

    private static bool ValidateRuntimeUnlockPendingPreCommitProbeRequest(
        FileTransferCoordinatorState state,
        RuntimeUnlockPendingPreCommitProbeRequest request,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (state.IsTerminal)
        {
            rejectionReason = "transfer_terminal";
            return false;
        }

        if (!string.Equals(request.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(request.TransferId, state.TransferId, StringComparison.Ordinal) ||
            request.Direction != state.Direction)
        {
            rejectionReason = "session_transfer_or_direction_mismatch";
            return false;
        }

        if (!IsRuntimeOwnerCurrent(state, request.RuntimeOwner, out rejectionReason))
        {
            return false;
        }

        if (!IsRuntimeUnlockAdmissionTokenCurrent(state, request.AdmissionToken, out rejectionReason))
        {
            return false;
        }

        if (request.Route != state.RouteSelection.Route ||
            request.ProtocolVersion != state.RouteSelection.ProtocolVersion ||
            !string.Equals(request.RouteToken, state.RouteSelection.TelemetryToken, StringComparison.Ordinal))
        {
            rejectionReason = "route_or_protocol_mismatch";
            return false;
        }

        if (request.LiveRouteEpoch != (state.CurrentLiveRouteEpoch?.EpochId ?? 0))
        {
            rejectionReason = "live_route_epoch_mismatch";
            return false;
        }

        if (request.RuntimeGeneration != (state.CurrentRuntimeOwner?.RuntimeGeneration ?? 0))
        {
            rejectionReason = "runtime_generation_mismatch";
            return false;
        }

        if (request.TerminalGeneration != state.TerminalGeneration)
        {
            rejectionReason = "terminal_generation_mismatch";
            return false;
        }

        if (request.LegGeneration != (state.CurrentLeg?.Generation ?? 0))
        {
            rejectionReason = "leg_generation_mismatch";
            return false;
        }

        return RuntimeUnlockPendingPreCommitProbeMetadataIsValid(request, out rejectionReason);
    }

    private static bool ValidateRuntimeUnlockPendingPreCommitProbeAction(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorActionRequest request,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (request.Kind != FileTransferCoordinatorActionKind.RuntimeUnlockPendingPreCommitProbeReplay)
        {
            return true;
        }

        if (request.RuntimeUnlockPendingPreCommitProbeRequest is not { } pendingRequest)
        {
            rejectionReason = "runtime_unlock_pending_precommit_probe_missing";
            return false;
        }

        if (!IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(state, pendingRequest, out rejectionReason))
        {
            return false;
        }

        if (request.RuntimeUnlockTransactionGeneration is { } transactionGeneration &&
            pendingRequest.TransactionGeneration != transactionGeneration)
        {
            rejectionReason = "runtime_unlock_transaction_generation_mismatch";
            return false;
        }

        if (request.RuntimeUnlockOfferGeneration is { } offerGeneration &&
            pendingRequest.OfferGeneration != offerGeneration)
        {
            rejectionReason = "runtime_unlock_offer_generation_mismatch";
            return false;
        }

        if (request.RuntimeUnlockTunaPathLeaseGeneration is { } leaseGeneration &&
            pendingRequest.TunaPathLeaseGeneration != leaseGeneration)
        {
            rejectionReason = "runtime_unlock_tuna_path_lease_generation_mismatch";
            return false;
        }

        if (request.RuntimeUnlockTunaPathLeaseListenerRunId is { } listenerRunId &&
            !string.Equals(pendingRequest.TunaPathLeaseListenerRunId, listenerRunId, StringComparison.Ordinal))
        {
            rejectionReason = "runtime_unlock_tuna_path_lease_listener_mismatch";
            return false;
        }

        if (request.TargetRoute is { } targetRoute && pendingRequest.TargetRoute != targetRoute)
        {
            rejectionReason = "route_commit_metadata_invalid";
            return false;
        }

        if (request.TargetProtocolVersion is { } targetProtocol && pendingRequest.TargetProtocolVersion != targetProtocol)
        {
            rejectionReason = "route_commit_metadata_invalid";
            return false;
        }

        if (request.TargetTransport is { } targetTransport && pendingRequest.TargetTransport != targetTransport)
        {
            rejectionReason = "route_commit_metadata_invalid";
            return false;
        }

        return true;
    }

    private static bool RuntimeUnlockPendingPreCommitProbeMetadataIsValid(
        RuntimeUnlockPendingPreCommitProbeRequest request,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (request.TransactionGeneration <= 0)
        {
            rejectionReason = "runtime_unlock_transaction_generation_missing";
            return false;
        }

        if (request.OfferGeneration <= 0)
        {
            rejectionReason = "runtime_unlock_offer_generation_missing";
            return false;
        }

        if (request.TunaPathLeaseGeneration <= 0)
        {
            rejectionReason = "runtime_unlock_tuna_path_lease_generation_missing";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.TunaPathLeaseListenerRunId))
        {
            rejectionReason = "runtime_unlock_tuna_path_lease_listener_missing";
            return false;
        }

        if (request.TargetRoute != FileTransferRoute.FileTunaV4 ||
            request.TargetProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            request.TargetTransport != FileTransferTransportKind.Tuna)
        {
            rejectionReason = "route_commit_metadata_invalid";
            return false;
        }

        if (request.HandoffKind != FileTransferTransportHandoffKind.NormalToTunaActivation)
        {
            rejectionReason = "runtime_unlock_handoff_kind_mismatch";
            return false;
        }

        return true;
    }

    private static bool RuntimeUnlockPendingPreCommitProbeMetadataMatches(
        RuntimeUnlockPendingPreCommitProbeRequest current,
        RuntimeUnlockPendingPreCommitProbeRequest attempted,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (current.TransactionGeneration != attempted.TransactionGeneration)
        {
            rejectionReason = "runtime_unlock_transaction_generation_mismatch";
            return false;
        }

        if (current.OfferGeneration != attempted.OfferGeneration)
        {
            rejectionReason = "runtime_unlock_offer_generation_mismatch";
            return false;
        }

        if (current.TunaPathLeaseGeneration != attempted.TunaPathLeaseGeneration)
        {
            rejectionReason = "runtime_unlock_tuna_path_lease_generation_mismatch";
            return false;
        }

        if (!string.Equals(current.TunaPathLeaseListenerRunId, attempted.TunaPathLeaseListenerRunId, StringComparison.Ordinal))
        {
            rejectionReason = "runtime_unlock_tuna_path_lease_listener_mismatch";
            return false;
        }

        if (current.TargetRoute != attempted.TargetRoute ||
            current.TargetProtocolVersion != attempted.TargetProtocolVersion ||
            current.TargetTransport != attempted.TargetTransport ||
            current.HandoffKind != attempted.HandoffKind)
        {
            rejectionReason = "route_commit_metadata_invalid";
            return false;
        }

        return true;
    }

    private static RoutePublicationToken? IssueRoutePublicationToken(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorActionRequest request,
        int publicationGeneration)
    {
        if (state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is null)
        {
            return null;
        }

        var targetRoute = request.TargetRoute ?? state.RouteSelection.Route;
        var targetProtocol = request.TargetProtocolVersion ?? state.RouteSelection.ProtocolVersion;
        var targetTransport = request.TargetTransport is { } transport and not FileTransferTransportKind.Unknown
            ? transport
            : ExpectedRoutePublicationTransport(targetRoute);
        var routeCommitGeneration = request.RouteCommitToken?.CommitGeneration ?? request.RouteCommitGeneration;
        return new RoutePublicationToken(
            state.SessionId,
            state.TransferId,
            state.Direction,
            owner,
            state.RouteSelection.Route,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            state.TerminalGeneration,
            state.CurrentLeg.Generation,
            routeCommitGeneration,
            targetRoute,
            targetProtocol,
            targetTransport,
            publicationGeneration,
            request.Kind,
            NormalizeReason(request.Reason));
    }

    private static RoutePublicationDecision? CreateRoutePublicationDecision(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorActionKind kind,
        bool accepted,
        string rejectionReason,
        RoutePublicationToken? routePublicationToken)
    {
        if (!IsRoutePublicationAuthorityAction(kind))
        {
            return null;
        }

        return new RoutePublicationDecision(
            kind,
            accepted,
            rejectionReason,
            state.RouteSelection.Route,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            state.CurrentRuntimeOwner?.RuntimeGeneration ?? 0,
            state.TerminalGeneration,
            state.CurrentLeg?.Generation ?? 0,
            state.CurrentRouteCommit,
            routePublicationToken);
    }

    private static RoutePublicationDecision CreateRoutePublicationDecision(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorActionRequest request,
        bool accepted,
        string rejectionReason,
        int publicationGeneration)
    {
        var token = accepted
            ? IssueRoutePublicationToken(state, request, publicationGeneration)
            : null;
        return CreateRoutePublicationDecision(
            state,
            request.Kind,
            accepted,
            rejectionReason,
            token)!.Value;
    }

    private static FileTransferCoordinatorState ApplyIssuedRoutePublicationAuthority(
        FileTransferCoordinatorState state,
        RoutePublicationDecision? routePublicationDecision)
    {
        if (routePublicationDecision?.RoutePublicationToken is not { } token)
        {
            return state;
        }

        return state with
        {
            CurrentRoutePublicationToken = token,
            LastRoutePublicationGeneration = token.PublicationGeneration,
        };
    }

    private static FallbackControlPlaneToken? IssueFallbackControlPlaneToken(
        FileTransferCoordinatorState state,
        FileTransferControlPlaneKind controlPlaneKind,
        string sendPurpose,
        int tokenGeneration,
        string reason)
    {
        if (state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg)
        {
            return null;
        }

        return new FallbackControlPlaneToken(
            state.SessionId,
            state.TransferId,
            state.Direction,
            owner,
            state.RouteSelection.Route,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            state.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            leg.CheckpointRequestId,
            leg.CheckpointGeneration,
            controlPlaneKind,
            NormalizeReason(sendPurpose),
            tokenGeneration,
            NormalizeReason(reason));
    }

    private static FallbackControlPlanePressureToken? IssueFallbackControlPlanePressureToken(
        FileTransferCoordinatorState state,
        int pressureGeneration,
        string reason)
    {
        if (state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg)
        {
            return null;
        }

        return new FallbackControlPlanePressureToken(
            state.SessionId,
            state.TransferId,
            state.Direction,
            owner,
            state.RouteSelection.Route,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            state.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            leg.CheckpointRequestId,
            leg.CheckpointGeneration,
            pressureGeneration,
            NormalizeReason(reason));
    }

    private static FallbackCheckpointLease? IssueFallbackCheckpointLease(
        FileTransferCoordinatorState state,
        string? checkpointRequestId,
        long? transportEpoch,
        int? bridgeRecoveryGeneration,
        int? checkpointGeneration,
        int leaseGeneration,
        FallbackCheckpointLeaseState leaseState,
        string reason)
    {
        if (state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg ||
            string.IsNullOrWhiteSpace(checkpointRequestId))
        {
            return null;
        }

        return new FallbackCheckpointLease(
            state.SessionId,
            state.TransferId,
            state.Direction,
            owner,
            state.RouteSelection.Route,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            state.TerminalGeneration,
            leg.Generation,
            transportEpoch is > 0 ? transportEpoch.Value : leg.TransportEpochId,
            bridgeRecoveryGeneration is > 0 ? bridgeRecoveryGeneration.Value : leg.BridgeRecoveryGeneration,
            checkpointRequestId,
            checkpointGeneration is > 0 ? checkpointGeneration.Value : leg.CheckpointGeneration,
            leaseGeneration,
            leaseState,
            NormalizeReason(reason));
    }

    private static FallbackTailYieldDecision CreateFallbackTailYieldDecision(
        FileTransferCoordinatorState state,
        bool accepted,
        FallbackTailYieldKind yieldKind,
        string reason,
        int yieldGeneration)
        => new(
            accepted,
            yieldKind,
            NormalizeReason(reason),
            yieldGeneration,
            state.RouteSelection.Route,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            state.CurrentRuntimeOwner?.RuntimeGeneration ?? state.LastRuntimeOwnerGeneration,
            state.TerminalGeneration,
            state.CurrentLeg?.Generation ?? 0,
            state.CurrentFallbackCheckpointLease,
            NormalizeRuntimeUnlockFallbackSurvivalState(state));

    private static RepairReplayDecision? CreateRepairReplayDecision(
        FileTransferCoordinatorState state,
        RepairReplayKind repairKind,
        string reason,
        int repairGeneration)
    {
        if (state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg)
        {
            return null;
        }

        return new RepairReplayDecision(
            true,
            repairKind,
            NormalizeReason(reason),
            repairGeneration,
            state.RouteSelection.Route,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            state.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            owner);
    }

    private static TransportEpochDecision? CreateTransportEpochDecision(
        FileTransferCoordinatorState state,
        TransportEpochActionKind actionKind,
        string reason,
        int transportDecisionGeneration,
        FileTransferRoute? targetRoute,
        int? targetProtocolVersion,
        FileTransferTransportKind? targetTransport)
    {
        if (state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg)
        {
            return null;
        }

        return new TransportEpochDecision(
            true,
            actionKind,
            NormalizeReason(reason),
            transportDecisionGeneration,
            state.RouteSelection.Route,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            state.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            targetRoute ?? state.RouteSelection.Route,
            targetProtocolVersion ?? state.RouteSelection.ProtocolVersion,
            targetTransport ?? FileTransferTransportKind.Unknown,
            owner);
    }

    private static BridgeRecoveryDecision? CreateBridgeRecoveryDecision(
        FileTransferCoordinatorState state,
        BridgeRecoveryKind recoveryKind,
        string reason,
        int bridgeDecisionGeneration)
    {
        if (state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg)
        {
            return null;
        }

        return new BridgeRecoveryDecision(
            true,
            recoveryKind,
            NormalizeReason(reason),
            bridgeDecisionGeneration,
            state.RouteSelection.Route,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            state.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            owner);
    }

    private static RecoveryLivenessLease? IssueRecoveryLivenessLease(
        FileTransferCoordinatorState state,
        int leaseGeneration,
        RecoveryLivenessLeaseState leaseState,
        string reason)
    {
        if (state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg)
        {
            return null;
        }

        return new RecoveryLivenessLease(
            state.SessionId,
            state.TransferId,
            state.Direction,
            owner,
            state.RouteSelection.Route,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            state.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            leaseGeneration,
            leaseState,
            NormalizeReason(reason));
    }

    private static FileTransferCoordinatorDecision StartTransfer(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        var routeSelection = coordinatorEvent.RouteSelection;
        InitialTunaRouteSelectionDecision? initialTunaDecision = null;
        if (routeSelection.Route == FileTransferRoute.FileTunaV4)
        {
            if (!CanStartInitialFileTunaRoute(
                    state,
                    coordinatorEvent,
                    routeSelection,
                    out var initialTunaRejectionReason))
            {
                if (coordinatorEvent.AllowInitialRouteFallback &&
                    coordinatorEvent.InitialRouteFallbackSelection is { } fallbackSelection)
                {
                    if (!IsValidInitialTunaFallbackSelection(fallbackSelection))
                    {
                        var fallbackRejected = new InitialTunaRouteSelectionDecision(
                            Accepted: false,
                            Reason: "initial_tuna_fallback_route_invalid",
                            RequestedRoute: routeSelection.Route,
                            RequestedProtocolVersion: routeSelection.ProtocolVersion,
                            RequestedTransport: coordinatorEvent.TargetTransport,
                            SelectedRoute: state.RouteSelection.Route,
                            SelectedProtocolVersion: state.RouteSelection.ProtocolVersion,
                            FallbackApplied: false,
                            Evidence: coordinatorEvent.InitialTunaReadinessEvidence);
                        return MutationRejected(
                            state,
                            reason,
                            "initial_tuna_fallback_route_invalid",
                            initialTunaRouteSelectionDecision: fallbackRejected);
                    }

                    initialTunaDecision = new InitialTunaRouteSelectionDecision(
                        Accepted: false,
                        Reason: initialTunaRejectionReason,
                        RequestedRoute: routeSelection.Route,
                        RequestedProtocolVersion: routeSelection.ProtocolVersion,
                        RequestedTransport: coordinatorEvent.TargetTransport,
                        SelectedRoute: fallbackSelection.Route,
                        SelectedProtocolVersion: fallbackSelection.ProtocolVersion,
                        FallbackApplied: true,
                        Evidence: coordinatorEvent.InitialTunaReadinessEvidence);
                    routeSelection = fallbackSelection;
                }
                else
                {
                    var rejected = new InitialTunaRouteSelectionDecision(
                        Accepted: false,
                        Reason: initialTunaRejectionReason,
                        RequestedRoute: routeSelection.Route,
                        RequestedProtocolVersion: routeSelection.ProtocolVersion,
                        RequestedTransport: coordinatorEvent.TargetTransport,
                        SelectedRoute: state.RouteSelection.Route,
                        SelectedProtocolVersion: state.RouteSelection.ProtocolVersion,
                        FallbackApplied: false,
                        Evidence: coordinatorEvent.InitialTunaReadinessEvidence);
                    return MutationRejected(
                        state,
                        reason,
                        initialTunaRejectionReason,
                        initialTunaRouteSelectionDecision: rejected);
                }
            }
            else
            {
                initialTunaDecision = new InitialTunaRouteSelectionDecision(
                    Accepted: true,
                    Reason: "accepted",
                    RequestedRoute: routeSelection.Route,
                    RequestedProtocolVersion: routeSelection.ProtocolVersion,
                    RequestedTransport: coordinatorEvent.TargetTransport,
                    SelectedRoute: routeSelection.Route,
                    SelectedProtocolVersion: routeSelection.ProtocolVersion,
                    FallbackApplied: false,
                    Evidence: coordinatorEvent.InitialTunaReadinessEvidence);
            }
        }

        var frozenLeg = coordinatorEvent.FreezeCurrentLeg
            ? FreezeLeg(state.CurrentLeg, DateTimeOffset.UtcNow)
            : null;
        var legHistory = state.LegHistory.ToList();
        var leg = StartLeg(new FileTransferLegStartRequest(
            routeSelection,
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
            RouteSelection = routeSelection,
            CurrentLeg = leg,
            LastTransferLegGeneration = leg.Generation,
            LastRuntimeOwnerGeneration = runtimeGeneration,
            LegHistory = legHistory,
            CurrentRuntimeUnlockAdmission = null,
            CurrentRouteCommit = null,
            CurrentRoutePublicationToken = null,
            CurrentRuntimeUnlockPendingPreCommitProbe = null,
        };
        nextState = InvalidateFallbackOwnership(nextState);
        nextState = nextState with
        {
            CurrentRuntimeOwner = IssueRuntimeOwnerToken(
                nextState,
                routeSelection,
                nextState.CurrentLiveRouteEpoch?.EpochId ?? 0,
                runtimeGeneration,
                nextState.TerminalGeneration,
                reason),
        };
        return Decision(
            nextState,
            reason,
            startedLeg: leg,
            frozenLeg: frozenLeg,
            initialTunaRouteSelectionDecision: initialTunaDecision);
    }

    private static bool IsValidInitialTunaFallbackSelection(FileTransferRouteSelection routeSelection)
        => routeSelection.Route == FileTransferRoute.RegularNknV4Fast &&
           routeSelection.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4;

    private static bool CanStartInitialFileTunaRoute(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferRouteSelection routeSelection,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (routeSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            coordinatorEvent.TargetTransport != FileTransferTransportKind.Tuna)
        {
            rejectionReason = "initial_tuna_target_invalid";
            return false;
        }

        if (coordinatorEvent.InitialTunaReadinessEvidence is not { } evidence)
        {
            rejectionReason = "initial_tuna_readiness_missing";
            return false;
        }

        if (!string.Equals(evidence.SessionId, state.SessionId, StringComparison.Ordinal))
        {
            rejectionReason = "initial_tuna_session_mismatch";
            return false;
        }

        if (!string.Equals(evidence.TransferId, state.TransferId, StringComparison.Ordinal))
        {
            rejectionReason = "initial_tuna_transfer_mismatch";
            return false;
        }

        if (evidence.Direction != state.Direction)
        {
            rejectionReason = "initial_tuna_direction_mismatch";
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (evidence.CapturedUtc == default ||
            evidence.CapturedUtc > now ||
            now - evidence.CapturedUtc > InitialTunaReadinessEvidenceMaxAge)
        {
            rejectionReason = "initial_tuna_evidence_not_current";
            return false;
        }

        if (evidence.LeaseGeneration <= 0 ||
            string.IsNullOrWhiteSpace(evidence.ListenerRunId) ||
            !evidence.LeaseCurrent ||
            !evidence.ListenerReady)
        {
            rejectionReason = "initial_tuna_listener_or_lease_not_ready";
            return false;
        }

        if (evidence.OfferGeneration <= 0 ||
            !evidence.PeerVisibleProof ||
            !evidence.AnswerReceived)
        {
            rejectionReason = "initial_tuna_peer_visible_answer_missing";
            return false;
        }

        return true;
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
            CurrentRuntimeUnlockAdmission = null,
            CurrentRouteCommit = null,
            CurrentRoutePublicationToken = null,
            CurrentRuntimeUnlockPendingPreCommitProbe = null,
        };
        nextState = InvalidateFallbackOwnership(nextState);
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

        var previousTransportEpoch = leg.TransportEpochId;
        MarkFallbackCheckpointRequested(
            leg,
            coordinatorEvent.CheckpointRequestId,
            coordinatorEvent.CheckpointPriority,
            coordinatorEvent.TransportEpochId);
        if (coordinatorEvent.CheckpointGeneration > 0)
        {
            leg.CheckpointGeneration = coordinatorEvent.CheckpointGeneration;
        }

        var leaseGeneration = Math.Max(1, state.LastFallbackCheckpointLeaseGeneration + 1);
        var lease = IssueFallbackCheckpointLease(
            state,
            coordinatorEvent.CheckpointRequestId,
            coordinatorEvent.TransportEpochId,
            coordinatorEvent.BridgeRecoveryGeneration,
            leg.CheckpointGeneration,
            leaseGeneration,
            FallbackCheckpointLeaseState.Pending,
            reason);
        var nextState = state with
        {
            CurrentFallbackCheckpointLease = lease,
            LastFallbackCheckpointLeaseGeneration = leaseGeneration,
        };
        if (leg.TransportEpochId != previousTransportEpoch)
        {
            nextState = InvalidateRecoveryAuthority(nextState);
        }

        return Decision(
            nextState,
            reason,
            mutatedLeg: leg,
            fallbackCheckpointRequired: true,
            fallbackCheckpointLease: lease);
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

        if (state.CurrentFallbackCheckpointLease is not { } currentLease)
        {
            return MutationRejected(state, reason, "fallback_checkpoint_lease_missing");
        }

        if (!IsFallbackCheckpointLeaseCurrent(state, currentLease, out var leaseRejectionReason))
        {
            return MutationRejected(state, reason, leaseRejectionReason);
        }

        var previousTransportEpoch = leg.TransportEpochId;
        MarkFallbackCheckpointAccepted(
            leg,
            coordinatorEvent.TransportEpochId,
            coordinatorEvent.CommittedChunkIndex,
            coordinatorEvent.HighestObservedChunkIndex);
        var acceptedLease = currentLease with { State = FallbackCheckpointLeaseState.Accepted, Reason = reason };
        var nextState = state with
        {
            CurrentFallbackCheckpointLease = null,
        };
        if (leg.TransportEpochId != previousTransportEpoch)
        {
            nextState = InvalidateRecoveryAuthority(nextState);
        }

        return Decision(
            nextState,
            reason,
            acceptedCheckpointLeg: leg,
            mutatedLeg: leg,
            fallbackCheckpointLease: acceptedLease);
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

        if (state.CurrentFallbackCheckpointLease is not { } currentLease)
        {
            return MutationRejected(state, reason, "fallback_checkpoint_lease_missing");
        }

        if (!IsFallbackCheckpointLeaseCurrent(state, currentLease, out var leaseRejectionReason))
        {
            return MutationRejected(state, reason, leaseRejectionReason);
        }

        RetireFallbackCheckpointRequest(leg!);
        var retiredLease = currentLease with { State = FallbackCheckpointLeaseState.Retired, Reason = reason };
        return Decision(
            state with
            {
                CurrentFallbackCheckpointLease = null,
            },
            reason,
            mutatedLeg: leg,
            fallbackCheckpointLease: retiredLease);
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

        var previousTransportEpoch = leg.TransportEpochId;
        MarkFallbackTailQuiescent(
            leg,
            coordinatorEvent.TransportEpochId,
            coordinatorEvent.CommittedChunkIndex,
            coordinatorEvent.HighestObservedChunkIndex);
        var yieldGeneration = Math.Max(1, state.LastFallbackTailYieldGeneration + 1);
        var yieldDecision = CreateFallbackTailYieldDecision(
            state,
            accepted: true,
            FallbackTailYieldKind.Quiescent,
            reason,
            yieldGeneration);
        var nextState = state with
        {
            LastFallbackTailYieldDecision = yieldDecision,
            LastFallbackTailYieldGeneration = yieldGeneration,
        };
        if (leg.TransportEpochId != previousTransportEpoch)
        {
            nextState = InvalidateRecoveryAuthority(nextState);
        }

        return Decision(
            nextState,
            reason,
            mutatedLeg: leg,
            fallbackTailYieldDecision: yieldDecision);
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
        var yieldGeneration = Math.Max(1, state.LastFallbackTailYieldGeneration + 1);
        var yieldDecision = CreateFallbackTailYieldDecision(
            state,
            accepted: true,
            FallbackTailYieldKind.Failed,
            reason,
            yieldGeneration);
        return Decision(
            state with
            {
                LastFallbackTailYieldDecision = yieldDecision,
                LastFallbackTailYieldGeneration = yieldGeneration,
            },
            reason,
            mutatedLeg: leg,
            fallbackTailYieldDecision: yieldDecision);
    }

    private static FileTransferCoordinatorDecision AuthorizeFallbackControlPlaneSend(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        if (!IsCurrentPostTunaFallbackLeg(state.CurrentLeg))
        {
            return MutationRejected(state, reason, state.CurrentLeg is null ? "leg_missing" : "fallback_leg_not_current");
        }

        var token = coordinatorEvent.FallbackControlPlaneToken;
        if (token is { } suppliedToken)
        {
            if (!IsFallbackControlPlaneTokenCurrent(
                    state,
                    suppliedToken,
                    allowNextGeneration: true,
                    allowIssuedGeneration: false,
                    out var tokenRejectionReason))
            {
                return MutationRejected(state, reason, tokenRejectionReason);
            }
        }
        else
        {
            token = IssueFallbackControlPlaneToken(
                state,
                coordinatorEvent.ControlPlaneKind,
                coordinatorEvent.ControlPlanePurpose ?? reason,
                Math.Max(1, state.LastFallbackControlPlaneTokenGeneration + 1),
                reason);
        }

        if (token is not { } issuedToken)
        {
            return MutationRejected(state, reason, "fallback_control_plane_token_missing");
        }

        var nextState = state with
        {
            CurrentFallbackControlPlaneToken = issuedToken,
            LastFallbackControlPlaneTokenGeneration = Math.Max(
                state.LastFallbackControlPlaneTokenGeneration,
                issuedToken.TokenGeneration),
        };
        return Decision(
            nextState,
            reason,
            fallbackControlPlaneToken: issuedToken);
    }

    private static FileTransferCoordinatorDecision CompleteFallbackControlPlaneSend(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        if (coordinatorEvent.FallbackControlPlaneToken is not { } token)
        {
            return MutationRejected(state, reason, "fallback_control_plane_token_missing");
        }

        if (!IsFallbackControlPlaneTokenCurrent(
                state,
                token,
                allowNextGeneration: false,
                allowIssuedGeneration: true,
                out var tokenRejectionReason))
        {
            return MutationRejected(state, reason, tokenRejectionReason);
        }

        if (!coordinatorEvent.ControlPlanePeerVisible)
        {
            return MutationRejected(state, reason, coordinatorEvent.ControlPlaneAcceptedAny
                ? "peer_visible_proof_missing"
                : "control_plane_delivery_missing");
        }

        var clearCurrentToken =
            state.CurrentFallbackControlPlaneToken?.TokenGeneration == token.TokenGeneration;
        return Decision(
            state with
            {
                CurrentFallbackControlPlaneToken = clearCurrentToken
                    ? null
                    : state.CurrentFallbackControlPlaneToken,
            },
            reason,
            fallbackControlPlaneToken: token,
            controlPlanePeerVisibleProofAccepted: true);
    }

    private static FileTransferCoordinatorDecision UpdateFallbackControlPlanePressure(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        if (!IsCurrentPostTunaFallbackLeg(state.CurrentLeg))
        {
            return MutationRejected(state, reason, state.CurrentLeg is null ? "leg_missing" : "fallback_leg_not_current");
        }

        var token = coordinatorEvent.FallbackControlPlanePressureToken;
        if (token is { } suppliedToken)
        {
            if (!IsFallbackControlPlanePressureTokenCurrent(state, suppliedToken, allowNextGeneration: true, out var tokenRejectionReason))
            {
                return MutationRejected(state, reason, tokenRejectionReason);
            }
        }
        else
        {
            token = IssueFallbackControlPlanePressureToken(
                state,
                Math.Max(1, state.LastFallbackControlPlanePressureGeneration + 1),
                reason);
        }

        if (token is not { } issuedToken)
        {
            return MutationRejected(state, reason, "fallback_control_plane_pressure_token_missing");
        }

        var survivalState = NormalizeRuntimeUnlockFallbackSurvivalState(state) with
        {
            ControlPlanePressurePending = true,
        };
        var nextState = state with
        {
            CurrentFallbackControlPlanePressureToken = issuedToken,
            LastFallbackControlPlanePressureGeneration = Math.Max(
                state.LastFallbackControlPlanePressureGeneration,
                issuedToken.PressureGeneration),
            RuntimeUnlockFallbackSurvivalState = survivalState,
        };
        return Decision(
            nextState,
            reason,
            fallbackControlPlanePressureToken: issuedToken);
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

    private static FileTransferCoordinatorDecision UpdateRuntimeUnlockFallbackSurvival(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
        => Decision(
            state with
            {
                RuntimeUnlockFallbackSurvivalState =
                    coordinatorEvent.RuntimeUnlockFallbackSurvivalState ??
                    RuntimeUnlockFallbackSurvivalState.Empty,
            },
            reason);

    private static FileTransferCoordinatorDecision ApplyRuntimeUnlockAdmission(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        var request = coordinatorEvent.RuntimeUnlockAdmissionRequest ??
            new RuntimeUnlockAdmissionRequest("runtime_unlock_admission", reason, state.CurrentRuntimeOwner);
        var admissionGeneration = Math.Max(1, state.LastRuntimeUnlockAdmissionGeneration + 1);
        var admission = EvaluateRuntimeUnlockAdmission(state, request, admissionGeneration);
        var nextState = state with
        {
            RuntimeUnlockFallbackSurvivalState = admission.FallbackSurvivalState,
            LastRuntimeUnlockAdmissionGeneration = admissionGeneration,
            CurrentRuntimeUnlockAdmission = admission.AdmissionToken,
        };
        return Decision(
            nextState,
            reason,
            runtimeUnlockAdmissionDecision: admission);
    }

    private static FileTransferCoordinatorDecision ApplyRuntimeUnlockPreCommitProbePending(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        if (coordinatorEvent.RuntimeUnlockPendingPreCommitProbeRequest is not { } pendingRequest)
        {
            return MutationRejected(state, reason, "runtime_unlock_pending_precommit_probe_missing");
        }

        if (!ValidateRuntimeUnlockPendingPreCommitProbeRequest(state, pendingRequest, out var rejectionReason))
        {
            return Decision(
                state,
                reason,
                runtimeUnlockPendingPreCommitProbeDecision:
                    CreateRuntimeUnlockPendingPreCommitProbeDecision(
                        state,
                        accepted: false,
                        rejectionReason: rejectionReason,
                        pendingRequest: pendingRequest));
        }

        var existing = state.CurrentRuntimeUnlockPendingPreCommitProbe;
        var acceptedRequest =
            existing is { } current &&
            IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(state, current, out _) &&
            RuntimeUnlockPendingPreCommitProbeMetadataMatches(current, pendingRequest, out _)
                ? current
                : pendingRequest with
                {
                    PendingGeneration = Math.Max(1, state.LastRuntimeUnlockPendingPreCommitProbeGeneration + 1),
                    Reason = NormalizeReason(reason),
                };
        var nextState = state with
        {
            CurrentRuntimeUnlockPendingPreCommitProbe = acceptedRequest,
            LastRuntimeUnlockPendingPreCommitProbeGeneration = Math.Max(
                state.LastRuntimeUnlockPendingPreCommitProbeGeneration,
                acceptedRequest.PendingGeneration),
        };
        return Decision(
            nextState,
            reason,
            runtimeUnlockPendingPreCommitProbeDecision:
                CreateRuntimeUnlockPendingPreCommitProbeDecision(
                    nextState,
                    accepted: true,
                    rejectionReason: "accepted",
                    pendingRequest: acceptedRequest));
    }

    private static FileTransferCoordinatorDecision ApplyRuntimeUnlockPreCommitProbeReplayed(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        if (coordinatorEvent.RuntimeUnlockPendingPreCommitProbeRequest is not { } pendingRequest)
        {
            return MutationRejected(state, reason, "runtime_unlock_pending_precommit_probe_missing");
        }

        if (!IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(state, pendingRequest, out var rejectionReason))
        {
            return Decision(
                state,
                reason,
                runtimeUnlockPendingPreCommitProbeDecision:
                    CreateRuntimeUnlockPendingPreCommitProbeDecision(
                        state,
                        accepted: false,
                        rejectionReason: rejectionReason,
                        pendingRequest: pendingRequest));
        }

        return Decision(
            state,
            reason,
            runtimeUnlockPendingPreCommitProbeDecision:
                CreateRuntimeUnlockPendingPreCommitProbeDecision(
                    state,
                    accepted: true,
                    rejectionReason: "accepted",
                    pendingRequest: pendingRequest));
    }

    private static FileTransferCoordinatorDecision RetireRuntimeUnlockPreCommitProbe(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        if (coordinatorEvent.RuntimeUnlockPendingPreCommitProbeRequest is { } pendingRequest &&
            !IsRuntimeUnlockPendingPreCommitProbeRequestCurrent(state, pendingRequest, out var rejectionReason))
        {
            return Decision(
                state,
                reason,
                runtimeUnlockPendingPreCommitProbeDecision:
                    CreateRuntimeUnlockPendingPreCommitProbeDecision(
                        state,
                        accepted: false,
                        rejectionReason: rejectionReason,
                        pendingRequest: pendingRequest));
        }

        return Decision(
            state with { CurrentRuntimeUnlockPendingPreCommitProbe = null },
            reason,
            runtimeUnlockPendingPreCommitProbeDecision:
                CreateRuntimeUnlockPendingPreCommitProbeDecision(
                    state,
                    accepted: true,
                    rejectionReason: "accepted",
                    pendingRequest: coordinatorEvent.RuntimeUnlockPendingPreCommitProbeRequest ??
                        state.CurrentRuntimeUnlockPendingPreCommitProbe));
    }

    private static FileTransferCoordinatorDecision RetireRuntimeUnlockAdmission(
        FileTransferCoordinatorState state,
        string reason)
        => Decision(
            state with
            {
                CurrentRuntimeUnlockAdmission = null,
                CurrentRouteCommit = null,
                CurrentRoutePublicationToken = null,
                CurrentRuntimeUnlockPendingPreCommitProbe = null,
            },
            reason);

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

        if (!RuntimeUnlockCommitEventMatchesProof(coordinatorEvent, proof, out rejectionReason))
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
        if (transition.State.CurrentRuntimeOwner is not { } runtimeOwner ||
            proof.AdmissionToken is not { } admissionToken)
        {
            return Decision(state, reason) with
            {
                RuntimeUnlockCommitRejectedReason = "runtime_owner_missing",
            };
        }

        var commitGeneration = Math.Max(1, state.LastRouteCommitGeneration + 1);
        var routeCommitToken = new RouteCommitToken(
            transition.State.SessionId,
            transition.State.TransferId,
            transition.State.Direction,
            runtimeOwner,
            admissionToken,
            transition.State.RouteSelection.Route,
            transition.State.RouteSelection.TelemetryToken,
            transition.State.RouteSelection.ProtocolVersion,
            transition.State.CurrentLiveRouteEpoch?.EpochId ?? 0,
            transition.State.LastRuntimeOwnerGeneration,
            transition.State.TerminalGeneration,
            transition.State.CurrentLeg?.Generation ?? 0,
            proof.TransactionGeneration,
            proof.OfferGeneration,
            proof.TunaPathLeaseGeneration,
            proof.TunaPathLeaseListenerRunId,
            proof.SamePathTunaProbeAckId ?? proof.PathProbeId ?? string.Empty,
            proof.TargetRoute,
            proof.ProtocolVersion,
            proof.TargetTransport,
            commitGeneration,
            reason);
        var committedState = transition.State with
        {
            CurrentRouteCommit = routeCommitToken,
            LastRouteCommitGeneration = commitGeneration,
            CurrentRuntimeUnlockAdmission = null,
            CurrentRoutePublicationToken = null,
            CurrentRuntimeUnlockPendingPreCommitProbe = null,
        };
        return transition with
        {
            State = committedState,
            RuntimeUnlockCommitAccepted = true,
            RuntimeUnlockCommitRejectedReason = null,
            RouteCommitToken = routeCommitToken,
        };
    }

    private static bool RuntimeUnlockCommitEventMatchesProof(
        FileTransferCoordinatorEvent coordinatorEvent,
        RuntimeUnlockRouteCommitProof proof,
        out string rejectionReason)
    {
        rejectionReason = "none";
        if (coordinatorEvent.RouteSelection.Route != proof.TargetRoute ||
            coordinatorEvent.RouteSelection.ProtocolVersion != proof.ProtocolVersion ||
            coordinatorEvent.HandoffKind != proof.HandoffKind ||
            coordinatorEvent.TargetTransport != proof.TargetTransport)
        {
            rejectionReason = "route_commit_event_target_mismatch";
            return false;
        }

        var expectedRoute = FileTransferRouteResolver.Resolve(proof.TargetRoute);
        if (!string.Equals(coordinatorEvent.RouteSelection.TelemetryToken, expectedRoute.TelemetryToken, StringComparison.Ordinal))
        {
            rejectionReason = "route_commit_event_target_mismatch";
            return false;
        }

        return true;
    }

    private static FileTransferCoordinatorDecision Terminalize(
        FileTransferCoordinatorState state,
        string reason)
    {
        var leg = state.CurrentLeg;
        TerminalizeLeg(leg);
        var nextTerminalGeneration = Math.Max(1, state.TerminalGeneration + 1);
        return Decision(
            InvalidateFallbackOwnership(state with
            {
                IsTerminal = true,
                TerminalGeneration = nextTerminalGeneration,
                CurrentRuntimeOwner = state.CurrentRuntimeOwner is { } owner
                    ? owner with { TerminalGeneration = nextTerminalGeneration }
                    : null,
                CurrentRuntimeUnlockAdmission = null,
                CurrentRouteCommit = null,
                CurrentRoutePublicationToken = null,
                CurrentRuntimeUnlockPendingPreCommitProbe = null,
            }),
            reason,
            mutatedLeg: leg);
    }

    private static FileTransferCoordinatorDecision PassThrough(FileTransferCoordinatorState state, string reason)
        => Decision(state, reason);

    private static FileTransferCoordinatorDecision MutationRejected(
        FileTransferCoordinatorState state,
        string reason,
        string rejectionReason,
        bool terminalMutationRejected = false,
        InitialTunaRouteSelectionDecision? initialTunaRouteSelectionDecision = null)
        => Decision(state, reason) with
        {
            TerminalMutationRejected = terminalMutationRejected,
            MutationRejectedReason = rejectionReason,
            InitialTunaRouteSelectionDecision = initialTunaRouteSelectionDecision,
        };

    private static FileTransferCoordinatorDecision Decision(
        FileTransferCoordinatorState state,
        string reason,
        FileTransferLeg? startedLeg = null,
        FileTransferLeg? frozenLeg = null,
        FileTransferLeg? acceptedCheckpointLeg = null,
        FileTransferLeg? mutatedLeg = null,
        bool fallbackCheckpointRequired = false,
        RuntimeUnlockAdmissionDecision? runtimeUnlockAdmissionDecision = null,
        FallbackControlPlaneToken? fallbackControlPlaneToken = null,
        FallbackCheckpointLease? fallbackCheckpointLease = null,
        FallbackControlPlanePressureToken? fallbackControlPlanePressureToken = null,
        FallbackTailYieldDecision? fallbackTailYieldDecision = null,
        bool controlPlanePeerVisibleProofAccepted = false,
        InitialTunaRouteSelectionDecision? initialTunaRouteSelectionDecision = null,
        RuntimeUnlockPendingPreCommitProbeDecision? runtimeUnlockPendingPreCommitProbeDecision = null)
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
            MutatedLeg: mutatedLeg,
            RuntimeUnlockAdmissionDecision: runtimeUnlockAdmissionDecision,
            FallbackControlPlaneToken: fallbackControlPlaneToken,
            FallbackCheckpointLease: fallbackCheckpointLease,
            FallbackControlPlanePressureToken: fallbackControlPlanePressureToken,
            FallbackTailYieldDecision: fallbackTailYieldDecision,
            ControlPlanePeerVisibleProofAccepted: controlPlanePeerVisibleProofAccepted,
            InitialTunaRouteSelectionDecision: initialTunaRouteSelectionDecision,
            RuntimeUnlockPendingPreCommitProbeDecision: runtimeUnlockPendingPreCommitProbeDecision);

    private static FileTransferCoordinatorActionDecision ActionDecision(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorActionKind kind,
        bool accepted,
        string rejectionReason,
        bool terminalMutationRejected = false,
        FallbackControlPlaneToken? fallbackControlPlaneToken = null,
        FallbackCheckpointLease? fallbackCheckpointLease = null,
        FallbackControlPlanePressureToken? fallbackControlPlanePressureToken = null,
        FallbackTailYieldDecision? fallbackTailYieldDecision = null,
        bool controlPlanePeerVisibleProofAccepted = false,
        RepairReplayDecision? repairReplayDecision = null,
        TransportEpochDecision? transportEpochDecision = null,
        BridgeRecoveryDecision? bridgeRecoveryDecision = null,
        RecoveryLivenessLease? recoveryLivenessLease = null,
        RoutePublicationDecision? routePublicationDecision = null,
        RoutePublicationToken? routePublicationToken = null,
        RuntimeUnlockPendingPreCommitProbeDecision? runtimeUnlockPendingPreCommitProbeDecision = null,
        RuntimeUnlockPendingPreCommitProbeRequest? runtimeUnlockPendingPreCommitProbeRequest = null)
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
            state.CurrentLeg?.Generation ?? 0,
            fallbackControlPlaneToken,
            fallbackCheckpointLease,
            fallbackControlPlanePressureToken,
            fallbackTailYieldDecision,
            controlPlanePeerVisibleProofAccepted,
            repairReplayDecision,
            transportEpochDecision,
            bridgeRecoveryDecision,
            recoveryLivenessLease,
            routePublicationDecision ?? CreateRoutePublicationDecision(
                state,
                kind,
                accepted,
                rejectionReason,
                routePublicationToken),
            routePublicationToken,
            runtimeUnlockPendingPreCommitProbeDecision ?? CreateRuntimeUnlockPendingPreCommitProbeDecision(
                state,
                accepted,
                rejectionReason,
                runtimeUnlockPendingPreCommitProbeRequest),
            runtimeUnlockPendingPreCommitProbeRequest)
        {
            State = state,
        };

    private static FileTransferCoordinatorState ApplyIssuedRecoveryAuthority(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorActionKind kind,
        RepairReplayDecision? repairReplayDecision,
        TransportEpochDecision? transportEpochDecision,
        BridgeRecoveryDecision? bridgeRecoveryDecision,
        RecoveryLivenessLease? recoveryLivenessLease)
    {
        var next = state;
        if (repairReplayDecision is { } repair)
        {
            next = next with
            {
                CurrentRepairReplayDecision = repair,
                LastRepairReplayGeneration = repair.RepairGeneration,
            };
        }

        if (transportEpochDecision is { } transportEpoch)
        {
            next = next with
            {
                CurrentTransportEpochDecision = transportEpoch,
                LastTransportEpochDecisionGeneration = transportEpoch.TransportDecisionGeneration,
            };
        }

        if (bridgeRecoveryDecision is { } bridgeRecovery)
        {
            next = next with
            {
                CurrentBridgeRecoveryDecision = bridgeRecovery,
                LastBridgeRecoveryDecisionGeneration = bridgeRecovery.BridgeDecisionGeneration,
            };
        }

        if (recoveryLivenessLease is { } livenessLease)
        {
            next = kind == FileTransferCoordinatorActionKind.RecoveryLivenessLeaseRetire
                ? next with
                {
                    CurrentRecoveryLivenessLease = null,
                    LastRecoveryLivenessLeaseGeneration = Math.Max(
                        next.LastRecoveryLivenessLeaseGeneration,
                        livenessLease.LeaseGeneration),
                }
                : next with
                {
                    CurrentRecoveryLivenessLease = livenessLease,
                    LastRecoveryLivenessLeaseGeneration = Math.Max(
                        next.LastRecoveryLivenessLeaseGeneration,
                        livenessLease.LeaseGeneration),
                };
        }

        return next;
    }

    private static bool ValidateRoutePublicationAction(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorActionRequest request,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (!IsRoutePublicationAuthorityAction(request.Kind))
        {
            return true;
        }

        if (state.CurrentLeg is null)
        {
            rejectionReason = "leg_missing";
            return false;
        }

        if (!IsLegalRoutePublicationTarget(
                state.RouteSelection.Route,
                state.RouteSelection.ProtocolVersion,
                ExpectedRoutePublicationTransport(state.RouteSelection.Route),
                out rejectionReason))
        {
            return false;
        }

        var targetRoute = request.TargetRoute ?? state.RouteSelection.Route;
        var targetProtocolVersion = request.TargetProtocolVersion ?? state.RouteSelection.ProtocolVersion;
        var targetTransport = request.TargetTransport is { } transport and not FileTransferTransportKind.Unknown
            ? transport
            : ExpectedRoutePublicationTransport(targetRoute);

        if (!IsLegalRoutePublicationTarget(
                targetRoute,
                targetProtocolVersion,
                targetTransport,
                out rejectionReason))
        {
            return false;
        }

        if (request.Kind == FileTransferCoordinatorActionKind.RouteCommitPublication)
        {
            if (targetRoute != FileTransferRoute.FileTunaV4 ||
                targetProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
                targetTransport != FileTransferTransportKind.Tuna)
            {
                rejectionReason = "route_commit_metadata_invalid";
                return false;
            }

            return true;
        }

        if (targetRoute != state.RouteSelection.Route ||
            targetProtocolVersion != state.RouteSelection.ProtocolVersion ||
            targetTransport != ExpectedRoutePublicationTransport(state.RouteSelection.Route))
        {
            rejectionReason = "route_publication_target_mismatch";
            return false;
        }

        return true;
    }

    private static bool ValidateFallbackOwnershipAction(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorActionRequest request,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (!IsFallbackOwnershipAction(request.Kind))
        {
            return true;
        }

        if (!IsCurrentPostTunaFallbackLeg(state.CurrentLeg))
        {
            rejectionReason = state.CurrentLeg is null ? "leg_missing" : "fallback_leg_not_current";
            return false;
        }

        if (request.Kind != FileTransferCoordinatorActionKind.ControlPlaneSendCompletion &&
            request.FallbackControlPlaneToken is { } controlPlaneToken &&
            !IsFallbackControlPlaneTokenCurrent(state, controlPlaneToken, out rejectionReason))
        {
            return false;
        }

        if (request.FallbackControlPlanePressureToken is { } pressureToken &&
            !IsFallbackControlPlanePressureTokenCurrent(state, pressureToken, out rejectionReason))
        {
            return false;
        }

        if (request.FallbackCheckpointLease is { } lease &&
            !IsFallbackCheckpointLeaseCurrent(state, lease, out rejectionReason))
        {
            return false;
        }

        if (request.Kind == FileTransferCoordinatorActionKind.ControlPlaneSendCompletion)
        {
            if (request.FallbackControlPlaneToken is null)
            {
                rejectionReason = "fallback_control_plane_token_missing";
                return false;
            }

            if (!IsFallbackControlPlaneTokenCurrent(
                    state,
                    request.FallbackControlPlaneToken.Value,
                    allowNextGeneration: false,
                    allowIssuedGeneration: true,
                    out rejectionReason))
            {
                return false;
            }

            if (request.ControlPlanePeerVisible != true)
            {
                rejectionReason = request.ControlPlaneAcceptedAny == true
                    ? "peer_visible_proof_missing"
                    : "control_plane_delivery_missing";
                return false;
            }
        }

        if (RequiresFallbackCheckpointLease(request.Kind) &&
            request.FallbackCheckpointLease is null)
        {
            rejectionReason = "fallback_checkpoint_lease_missing";
            return false;
        }

        if (request.Kind == FileTransferCoordinatorActionKind.FallbackControlPlanePressure &&
            request.FallbackControlPlanePressureToken is { } suppliedPressureToken &&
            !IsFallbackControlPlanePressureTokenCurrent(state, suppliedPressureToken, out rejectionReason))
        {
            return false;
        }

        return true;
    }

    private static bool ValidateRecoveryAuthorityAction(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorActionRequest request,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (!IsRecoveryAuthorityAction(request.Kind))
        {
            return true;
        }

        if (IsControlPlanePressureAuthorityRefresh(request))
        {
            rejectionReason = "control_plane_pressure_authority_refresh_diagnostic_only";
            return false;
        }

        if (state.CurrentLeg is null)
        {
            rejectionReason = "leg_missing";
            return false;
        }

        if (request.RouteCommitGeneration is { } routeCommitGeneration &&
            routeCommitGeneration != state.CurrentRouteCommit?.CommitGeneration)
        {
            rejectionReason = "route_commit_generation_mismatch";
            return false;
        }

        if (request.RecoveryDecisionGeneration is { } recoveryDecisionGeneration &&
            !IsCurrentRecoveryDecisionGeneration(state, request.Kind, recoveryDecisionGeneration))
        {
            rejectionReason = "recovery_decision_generation_mismatch";
            return false;
        }

        if (request.Kind is FileTransferCoordinatorActionKind.RecoveryLivenessLeaseValidate or
            FileTransferCoordinatorActionKind.RecoveryLivenessLeaseRetire)
        {
            if (request.RecoveryLivenessLease is not { } lease)
            {
                rejectionReason = "recovery_liveness_lease_missing";
                return false;
            }

            if (!IsRecoveryLivenessLeaseCurrent(state, lease, out rejectionReason))
            {
                return false;
            }
        }

        if (request.Kind == FileTransferCoordinatorActionKind.PeerEpochPromotion)
        {
            if (request.TargetRoute is not { } targetRoute ||
                request.TargetProtocolVersion is not { } targetProtocolVersion)
            {
                rejectionReason = "peer_epoch_target_missing";
                return false;
            }

            if (targetRoute == FileTransferRoute.FileTunaV4 &&
                targetProtocolVersion != FileTransferProtocol.ProtocolVersionV4)
            {
                rejectionReason = "file_tuna_v6_rejected";
                return false;
            }

            var targetSelection = FileTransferRouteResolver.Resolve(targetRoute);
            if (targetSelection.ProtocolVersion != targetProtocolVersion)
            {
                rejectionReason = "target_route_protocol_mismatch";
                return false;
            }

            if (request.TargetTransport is null or FileTransferTransportKind.Unknown)
            {
                rejectionReason = "target_transport_missing";
                return false;
            }

            if (request.TransportEpochActionKind is { } actionKind &&
                actionKind != TransportEpochActionKind.PromotePeerEpoch)
            {
                rejectionReason = "transport_epoch_action_mismatch";
                return false;
            }

            if (state.CurrentTransportEpochDecision is not { } currentDecision ||
                currentDecision.IsDefault)
            {
                rejectionReason = "transport_epoch_decision_missing";
                return false;
            }

            if (!IsTransportEpochDecisionCurrent(state, currentDecision, out rejectionReason))
            {
                return false;
            }

            if (currentDecision.TargetRoute != targetRoute ||
                currentDecision.TargetProtocolVersion != targetProtocolVersion ||
                currentDecision.TargetTransport != request.TargetTransport)
            {
                rejectionReason = "peer_epoch_target_decision_mismatch";
                return false;
            }
        }

        return true;
    }

    private static bool ValidateFallbackAuthorityFields(
        FileTransferCoordinatorState state,
        FileTransferRoute route,
        string routeToken,
        int protocolVersion,
        int liveRouteEpoch,
        int runtimeGeneration,
        int terminalGeneration,
        int legGeneration,
        long transportEpoch,
        int bridgeRecoveryGeneration,
        string? checkpointRequestId,
        int checkpointGeneration,
        out string rejectionReason)
    {
        rejectionReason = "current";
        if (route != state.RouteSelection.Route ||
            protocolVersion != state.RouteSelection.ProtocolVersion ||
            !string.Equals(routeToken, state.RouteSelection.TelemetryToken, StringComparison.Ordinal))
        {
            rejectionReason = "route_or_protocol_mismatch";
            return false;
        }

        if (liveRouteEpoch != (state.CurrentLiveRouteEpoch?.EpochId ?? 0))
        {
            rejectionReason = "live_route_epoch_mismatch";
            return false;
        }

        if (runtimeGeneration != (state.CurrentRuntimeOwner?.RuntimeGeneration ?? 0))
        {
            rejectionReason = "runtime_generation_mismatch";
            return false;
        }

        if (terminalGeneration != state.TerminalGeneration)
        {
            rejectionReason = "terminal_generation_mismatch";
            return false;
        }

        if (state.CurrentLeg is not { } leg)
        {
            rejectionReason = "leg_missing";
            return false;
        }

        if (legGeneration != leg.Generation)
        {
            rejectionReason = "leg_generation_mismatch";
            return false;
        }

        if (transportEpoch != leg.TransportEpochId)
        {
            rejectionReason = "transport_epoch_mismatch";
            return false;
        }

        if (bridgeRecoveryGeneration != leg.BridgeRecoveryGeneration)
        {
            rejectionReason = "bridge_recovery_generation_mismatch";
            return false;
        }

        if (!string.Equals(checkpointRequestId, leg.CheckpointRequestId, StringComparison.Ordinal))
        {
            rejectionReason = "checkpoint_request_mismatch";
            return false;
        }

        if (checkpointGeneration != leg.CheckpointGeneration)
        {
            rejectionReason = "checkpoint_generation_mismatch";
            return false;
        }

        if (state.IsTerminal)
        {
            rejectionReason = "transfer_terminal";
            return false;
        }

        return true;
    }

    private static bool ValidateRuntimeUnlockCallbackMetadata(
        FileTransferCoordinatorActionRequest request,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (!IsRuntimeUnlockCallbackAction(request.Kind) &&
            request.Kind != FileTransferCoordinatorActionKind.RouteCommitPublication)
        {
            return true;
        }

        if (request.Kind == FileTransferCoordinatorActionKind.RouteCommitPublication)
        {
            if (request.TargetRoute is { } targetRoute &&
                targetRoute != FileTransferRoute.FileTunaV4)
            {
                rejectionReason = "route_commit_metadata_invalid";
                return false;
            }

            if (request.TargetProtocolVersion is { } targetProtocolVersion &&
                targetProtocolVersion != FileTransferProtocol.ProtocolVersionV4)
            {
                rejectionReason = "route_commit_metadata_invalid";
                return false;
            }

            if (request.TargetTransport is { } targetTransport &&
                targetTransport != FileTransferTransportKind.Tuna)
            {
                rejectionReason = "route_commit_metadata_invalid";
                return false;
            }
        }

        if (request.RouteCommitToken is { } routeCommitToken)
        {
            if (request.RuntimeUnlockTransactionGeneration is { } transactionGeneration &&
                routeCommitToken.TransactionGeneration != transactionGeneration)
            {
                rejectionReason = "runtime_unlock_transaction_generation_mismatch";
                return false;
            }

            if (request.RuntimeUnlockOfferGeneration is { } offerGeneration &&
                routeCommitToken.OfferGeneration != offerGeneration)
            {
                rejectionReason = "runtime_unlock_offer_generation_mismatch";
                return false;
            }

            if (request.RuntimeUnlockTunaPathLeaseGeneration is { } leaseGeneration &&
                routeCommitToken.TunaPathLeaseGeneration != leaseGeneration)
            {
                rejectionReason = "runtime_unlock_tuna_path_lease_generation_mismatch";
                return false;
            }

            if (request.RuntimeUnlockTunaPathLeaseListenerRunId is { } listenerRunId &&
                !string.Equals(routeCommitToken.TunaPathLeaseListenerRunId, listenerRunId, StringComparison.Ordinal))
            {
                rejectionReason = "runtime_unlock_tuna_path_lease_listener_mismatch";
                return false;
            }

            if (request.RuntimeUnlockProbeId is { } probeId &&
                !string.Equals(routeCommitToken.SamePathTunaProbeAckId, probeId, StringComparison.Ordinal))
            {
                rejectionReason = "runtime_unlock_probe_id_mismatch";
                return false;
            }

            if (request.RuntimeUnlockSamePathTunaProbeAckId is { } ackId &&
                !string.Equals(routeCommitToken.SamePathTunaProbeAckId, ackId, StringComparison.Ordinal))
            {
                rejectionReason = "same_path_tuna_probe_ack_mismatch";
                return false;
            }

            if (request.RouteCommitGeneration is { } commitGeneration &&
                routeCommitToken.CommitGeneration != commitGeneration)
            {
                rejectionReason = "route_commit_generation_mismatch";
                return false;
            }
        }

        if (request.Kind is FileTransferCoordinatorActionKind.RuntimeUnlockProbeStart or
            FileTransferCoordinatorActionKind.RuntimeUnlockProbeAck or
            FileTransferCoordinatorActionKind.RuntimeUnlockTransactionCallback)
        {
            if (request.RuntimeUnlockTransactionGeneration is not > 0)
            {
                rejectionReason = "runtime_unlock_transaction_generation_missing";
                return false;
            }

            if (request.RuntimeUnlockOfferGeneration is not > 0)
            {
                rejectionReason = "runtime_unlock_offer_generation_missing";
                return false;
            }
        }

        if (request.Kind is FileTransferCoordinatorActionKind.RuntimeUnlockProbeStart or
            FileTransferCoordinatorActionKind.RuntimeUnlockProbeAck)
        {
            if (request.RuntimeUnlockTunaPathLeaseGeneration is not > 0)
            {
                rejectionReason = "runtime_unlock_tuna_path_lease_generation_missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.RuntimeUnlockProbeId))
            {
                rejectionReason = "runtime_unlock_probe_id_missing";
                return false;
            }

            if (request.RuntimeUnlockSamePathTunaProbeAckId is { } ackId &&
                !string.Equals(ackId, request.RuntimeUnlockProbeId, StringComparison.Ordinal))
            {
                rejectionReason = "same_path_tuna_probe_ack_mismatch";
                return false;
            }
        }

        return true;
    }

    private static bool RequiresRuntimeOwner(FileTransferCoordinatorActionKind kind)
        => kind is not FileTransferCoordinatorActionKind.StartTransferLeg and
                   not FileTransferCoordinatorActionKind.RouteCommitPublication;

    private static bool IsRoutePublicationAuthorityAction(FileTransferCoordinatorActionKind kind)
        => kind is FileTransferCoordinatorActionKind.RoutePublication or
                   FileTransferCoordinatorActionKind.RouteCommitPublication;

    private static bool IsLegalRoutePublicationTarget(
        FileTransferRoute route,
        int protocolVersion,
        FileTransferTransportKind transport,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (route == FileTransferRoute.FileTunaV4 &&
            protocolVersion != FileTransferProtocol.ProtocolVersionV4)
        {
            rejectionReason = "file_tuna_v6_rejected";
            return false;
        }

        var expectedProtocol = route switch
        {
            FileTransferRoute.RegularNknV4Fast => FileTransferProtocol.ProtocolVersionV4,
            FileTransferRoute.FileTunaV4 => FileTransferProtocol.ProtocolVersionV4,
            FileTransferRoute.PostTunaFallbackV6 => FileTransferProtocol.ProtocolVersionV6,
            _ => 0,
        };
        if (protocolVersion != expectedProtocol)
        {
            rejectionReason = "target_route_protocol_mismatch";
            return false;
        }

        var expectedTransport = ExpectedRoutePublicationTransport(route);
        if (transport != FileTransferTransportKind.Unknown &&
            transport != expectedTransport)
        {
            rejectionReason = "route_publication_target_mismatch";
            return false;
        }

        return true;
    }

    private static FileTransferTransportKind ExpectedRoutePublicationTransport(FileTransferRoute route)
        => route == FileTransferRoute.FileTunaV4
            ? FileTransferTransportKind.Tuna
            : FileTransferTransportKind.RegularNkn;

    private static bool IsFallbackOwnershipAction(FileTransferCoordinatorActionKind kind)
        => kind is FileTransferCoordinatorActionKind.ControlPlaneSend or
                   FileTransferCoordinatorActionKind.ControlPlaneSendCompletion or
                   FileTransferCoordinatorActionKind.FallbackControlPlanePressure or
                   FileTransferCoordinatorActionKind.FallbackCheckpointBegin or
                   FileTransferCoordinatorActionKind.FallbackCheckpointAccept or
                   FileTransferCoordinatorActionKind.FallbackCheckpointReissue or
                   FileTransferCoordinatorActionKind.FallbackCheckpointRetire or
                   FileTransferCoordinatorActionKind.FallbackTailQuiescent or
                   FileTransferCoordinatorActionKind.FallbackTailFailed;

    private static bool IsRecoveryAuthorityAction(FileTransferCoordinatorActionKind kind)
        => kind is FileTransferCoordinatorActionKind.RepairReplay or
                   FileTransferCoordinatorActionKind.TransportEpochAdoption or
                   FileTransferCoordinatorActionKind.PeerEpochPromotion or
                   FileTransferCoordinatorActionKind.UnmatchedFrontierProof or
                   FileTransferCoordinatorActionKind.RouteMismatchMark or
                   FileTransferCoordinatorActionKind.BridgeRecovery or
                   FileTransferCoordinatorActionKind.ReceiveRecovery or
                   FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery or
                   FileTransferCoordinatorActionKind.LivenessDeferral or
                   FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin or
                   FileTransferCoordinatorActionKind.RecoveryLivenessLeaseValidate or
                   FileTransferCoordinatorActionKind.RecoveryLivenessLeaseRetire;

    private static bool IsTransportEpochAuthorityAction(FileTransferCoordinatorActionKind kind)
        => kind is FileTransferCoordinatorActionKind.TransportEpochAdoption or
                   FileTransferCoordinatorActionKind.PeerEpochPromotion or
                   FileTransferCoordinatorActionKind.UnmatchedFrontierProof or
                   FileTransferCoordinatorActionKind.RouteMismatchMark;

    private static bool IsBridgeRecoveryAuthorityAction(FileTransferCoordinatorActionKind kind)
        => kind is FileTransferCoordinatorActionKind.BridgeRecovery or
                   FileTransferCoordinatorActionKind.ReceiveRecovery or
                   FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery;

    private static TransportEpochActionKind DefaultTransportEpochActionKind(FileTransferCoordinatorActionKind kind)
        => kind switch
        {
            FileTransferCoordinatorActionKind.PeerEpochPromotion => TransportEpochActionKind.PromotePeerEpoch,
            FileTransferCoordinatorActionKind.RouteMismatchMark => TransportEpochActionKind.MarkRouteMismatch,
            FileTransferCoordinatorActionKind.UnmatchedFrontierProof => TransportEpochActionKind.RecoverLiveRoute,
            _ => TransportEpochActionKind.Adopt,
        };

    private static BridgeRecoveryKind DefaultBridgeRecoveryKind(FileTransferCoordinatorActionKind kind)
        => kind switch
        {
            FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery => BridgeRecoveryKind.BridgeReceiveStallRecovery,
            _ => BridgeRecoveryKind.ReceiveRecoveryRequest,
        };

    private static bool IsCurrentRecoveryDecisionGeneration(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorActionKind kind,
        int generation)
        => generation > 0 &&
           (kind switch
           {
               FileTransferCoordinatorActionKind.RepairReplay => state.CurrentRepairReplayDecision?.RepairGeneration,
               FileTransferCoordinatorActionKind.TransportEpochAdoption or
                   FileTransferCoordinatorActionKind.PeerEpochPromotion or
                   FileTransferCoordinatorActionKind.UnmatchedFrontierProof or
                   FileTransferCoordinatorActionKind.RouteMismatchMark
                   => state.CurrentTransportEpochDecision?.TransportDecisionGeneration,
               FileTransferCoordinatorActionKind.BridgeRecovery or
                   FileTransferCoordinatorActionKind.ReceiveRecovery or
                   FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery
                   => state.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration,
               FileTransferCoordinatorActionKind.LivenessDeferral or
                   FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin or
                   FileTransferCoordinatorActionKind.RecoveryLivenessLeaseValidate or
                   FileTransferCoordinatorActionKind.RecoveryLivenessLeaseRetire
                   => state.CurrentRecoveryLivenessLease?.LeaseGeneration,
               _ => null,
           }) == generation;

    private static bool IsControlPlanePressureAuthorityRefresh(FileTransferCoordinatorActionRequest request)
        => IsPressureRefreshValue(request.Reason) ||
           IsPressureRefreshValue(request.Source) ||
           IsPressureRefreshValue(request.ControlPlanePurpose);

    private static bool IsPressureRefreshValue(string? value)
        => string.Equals(value, "control_plane_pressure_authority_refresh", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresFallbackCheckpointLease(FileTransferCoordinatorActionKind kind)
        => kind is FileTransferCoordinatorActionKind.FallbackCheckpointAccept or
                   FileTransferCoordinatorActionKind.FallbackCheckpointReissue or
                   FileTransferCoordinatorActionKind.FallbackCheckpointRetire;

    private static bool RequiresRuntimeUnlockAdmissionToken(FileTransferCoordinatorActionKind kind)
        => kind is FileTransferCoordinatorActionKind.RuntimeUnlockOfferCallback or
                   FileTransferCoordinatorActionKind.RuntimeUnlockAnswerCallback or
                   FileTransferCoordinatorActionKind.RuntimeUnlockProbeStart or
                   FileTransferCoordinatorActionKind.RuntimeUnlockProbeAck or
                   FileTransferCoordinatorActionKind.RuntimeUnlockPendingPreCommitProbeReplay or
                   FileTransferCoordinatorActionKind.RuntimeUnlockTransactionCallback;

    private static bool RequiresRouteCommitToken(FileTransferCoordinatorActionKind kind)
        => kind == FileTransferCoordinatorActionKind.RouteCommitPublication;

    private static bool IsRuntimeUnlockCallbackAction(FileTransferCoordinatorActionKind kind)
        => RequiresRuntimeUnlockAdmissionToken(kind);

    private static string NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason)
            ? "file_transfer_coordinator"
            : reason.Trim();
}
