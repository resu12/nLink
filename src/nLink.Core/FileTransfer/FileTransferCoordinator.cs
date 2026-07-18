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
    RecoveryLeaseClosureLivenessDeferral,
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
    public FileTransferAuthorityId AuthorityId { get; init; }

    public bool IsDefault => TokenGeneration <= 0 || RuntimeOwner.IsDefault || AuthorityId.IsDefault;
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
    string OwnerKey,
    string PairKey,
    string CheckpointRequestIdSnapshot,
    FileTransferRoute CurrentRoute,
    int CurrentProtocolVersion,
    int CurrentLiveRouteEpoch,
    int CurrentRuntimeGeneration,
    int CurrentTerminalGeneration,
    int CurrentLegGeneration,
    int CurrentBridgeRecoveryGeneration,
    FallbackCheckpointLease? CheckpointLease,
    RuntimeUnlockFallbackSurvivalState FallbackSurvivalState)
{
    public FileTransferAuthorityId AuthorityId { get; init; }

    public FileTransferAuthorityId? PredecessorAuthorityId { get; init; }

    public bool IsDefault => YieldGeneration <= 0 || AuthorityId.IsDefault;
}

internal readonly record struct FallbackTailYieldTransportEpochReauthorizationRequest(
    FallbackTailYieldDecision PredecessorDecision,
    RuntimeOwnerToken SourceRuntimeOwner,
    RoutePublicationToken SourceRoutePublicationToken,
    long SourceTransportEpoch,
    long TargetTransportEpoch,
    string Reason)
{
    public FallbackTailYieldTransportEpochReauthorization? PendingReauthorization { get; init; }

    public bool IsDefault => PredecessorDecision.IsDefault ||
                             SourceRuntimeOwner.IsDefault ||
                             SourceRoutePublicationToken.IsDefault ||
                             SourceTransportEpoch <= 0 ||
                             TargetTransportEpoch <= SourceTransportEpoch;
}

internal readonly record struct FallbackTailYieldTransportEpochReauthorization(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    FallbackTailYieldDecision PredecessorDecision,
    RuntimeOwnerToken SourceRuntimeOwner,
    RoutePublicationToken SourceRoutePublicationToken,
    string SourceOwnerKey,
    string SourcePairKey,
    long SourceTransportEpoch,
    long TargetTransportEpoch,
    RuntimeOwnerToken TargetRuntimeOwner,
    int TargetTerminalGeneration,
    int TargetLegGeneration,
    int TargetBridgeRecoveryGeneration,
    RoutePublicationToken? TargetRoutePublicationToken,
    BridgeRecoveryDecision? TargetBridgeRecoveryDecision,
    string TargetCheckpointRequestIdSnapshot,
    int ReauthorizationGeneration,
    string Reason)
{
    public bool IsDefault => string.IsNullOrWhiteSpace(SessionId) ||
                             string.IsNullOrWhiteSpace(TransferId) ||
                             PredecessorDecision.IsDefault ||
                             SourceRuntimeOwner.IsDefault ||
                             SourceRoutePublicationToken.IsDefault ||
                             TargetRuntimeOwner.IsDefault ||
                             string.IsNullOrWhiteSpace(SourceOwnerKey) ||
                             string.IsNullOrWhiteSpace(SourcePairKey) ||
                             SourceTransportEpoch <= 0 ||
                             TargetTransportEpoch <= SourceTransportEpoch ||
                             TargetTerminalGeneration < 0 ||
                             TargetLegGeneration <= 0 ||
                             TargetBridgeRecoveryGeneration < 0 ||
                             TargetRoutePublicationToken is not { IsDefault: false } ||
                             ReauthorizationGeneration <= 0;
}

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
    RuntimeOwnerToken RuntimeOwner,
    FileTransferAuthorityId AuthorityId,
    FileTransferAuthorityId? PredecessorAuthorityId,
    int BridgeDecisionGeneration,
    RoutePublicationToken? RoutePublicationToken)
{
    public bool IsDefault => RepairGeneration <= 0 || RuntimeOwner.IsDefault;
}

internal readonly record struct RegularV4FrontierRepairAuthorityRequest(
    string RepairKey,
    int FrontierChunkIndex,
    int DurableReceivedHighestChunkIndex,
    int PendingRepairCount,
    string DecisionReason);

internal enum RegularV4FrontierRepairPressureBindingState
{
    Pending = 1,
    Installed = 2,
    RetainedPredecessor = 3,
    Retired = 4,
    Invalidated = 5,
}

internal readonly record struct RegularV4FrontierRepairPressureBinding(
    FileTransferRegularV4FrontierRepairAuthority Authority,
    string PressureReason,
    long InstallGeneration,
    RegularV4FrontierRepairPressureBindingState State,
    FileTransferAuthorityId? PredecessorAuthorityId,
    FileTransferRegularV4FrontierRepairSettlement? FinalSettlement)
{
    public bool IsDefault => Authority.AuthorityId.IsDefault ||
                             InstallGeneration <= 0 ||
                             string.IsNullOrWhiteSpace(PressureReason);
}

internal enum RegularV4FrontierRepairPressureBindingDecisionStatus
{
    Accepted,
    Idempotent,
    Rejected,
}

internal readonly record struct RegularV4FrontierRepairPressureBindingDecision(
    RegularV4FrontierRepairPressureBindingDecisionStatus Status,
    string Reason,
    RegularV4FrontierRepairPressureBinding? Binding,
    FileTransferRegularV4FrontierRepairPressureSettlementCommand? SettlementCommand)
{
    public FileTransferCoordinatorState State { get; init; } = null!;
}

internal enum FallbackFrontierRepairSourceKind
{
    Unknown = 0,
    ReceiverRequest = 1,
    PeerRequest = 2,
    SenderTimeoutRetry = 3,
    SenderLivenessRescue = 4,
}

internal readonly record struct FallbackFrontierRepairAuthorityRequest(
    string CanonicalRepairKey,
    string RangeFingerprint,
    int FrontierChunkIndex,
    int DurableReceivedHighestChunkIndex,
    int RangeCount,
    int RequestedChunkCount,
    FallbackFrontierRepairSourceKind SourceKind,
    string ProtocolRepairRequestId,
    string RequestCorrelationId,
    string CheckpointRequestId,
    string Priority,
    string RecoveryMode,
    string DecisionReason);

internal readonly record struct FallbackFrontierRepairAuthority(
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
    int BridgeDecisionGeneration,
    int RoutePublicationGeneration,
    int? RouteCommitGeneration,
    string CheckpointRequestId,
    string CanonicalRepairKey,
    string RangeFingerprint,
    int FrontierChunkIndex,
    int DurableReceivedHighestChunkIndex,
    int RangeCount,
    int RequestedChunkCount,
    FallbackFrontierRepairSourceKind SourceKind,
    string ProtocolRepairRequestId,
    string RequestCorrelationId,
    string Priority,
    string RecoveryMode,
    int DecisionGeneration,
    string DecisionReason,
    FileTransferTransportKind TargetTransport,
    FileTransferAuthorityId AuthorityId,
    FileTransferAuthorityId? PredecessorAuthorityId)
{
    public bool IsDefault => AuthorityId.IsDefault ||
                             RuntimeOwner.IsDefault ||
                             DecisionGeneration <= 0 ||
                             string.IsNullOrWhiteSpace(CanonicalRepairKey) ||
                             string.IsNullOrWhiteSpace(RangeFingerprint);
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
    FileTransferAuthorityId AuthorityId,
    RecoveryLivenessLeaseState State,
    string Reason)
{
    public bool IsDefault => LeaseGeneration <= 0 ||
                             AuthorityId.IsDefault ||
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
    int LastRuntimeUnlockPendingPreCommitProbeGeneration = 0,
    RuntimeUnlockRetryDispatchAuthority? CurrentRuntimeUnlockRetryDispatchAuthority = null,
    int LastRuntimeUnlockRetryDispatchAuthorityGeneration = 0,
    RecoveryLivenessLease? LastRetiredRecoveryLivenessLease = null,
    RuntimeUnlockRetryDispatchSettlementReport? LastRuntimeUnlockRetryDispatchSettlement = null,
    RuntimeUnlockRegularV4RecoverySettlementReport? LastRuntimeUnlockRegularV4RecoverySettlement = null,
    FileTransferRegularV4FrontierRepairSettlement? LastCoordinatorIssuedRegularV4RecoverySettlement = null,
    bool RuntimeUnlockRegularV4RecoverySettlementAvailable = false,
    RuntimeUnlockRegularV4RecoveryLifecycleReport? LastRuntimeUnlockRegularV4RecoveryLifecycle = null,
    bool RuntimeUnlockRecoveryLeaseClosureAvailable = false,
    FallbackTailYieldTransportEpochReauthorization? PendingFallbackTailYieldTransportEpochReauthorization = null,
    int LastFallbackTailYieldTransportEpochReauthorizationGeneration = 0,
    RegularV4FrontierRepairPressureBinding? PendingRegularV4FrontierRepairPressureBinding = null,
    RegularV4FrontierRepairPressureBinding? InstalledRegularV4FrontierRepairPressureBinding = null,
    RegularV4FrontierRepairPressureBinding? RetainedRegularV4FrontierRepairPressurePredecessor = null,
    FileTransferRegularV4FrontierRepairSettlement? LastRegularV4FrontierRepairPressureSettlement = null,
    long LastRegularV4FrontierRepairPressureInstallGeneration = 0,
    FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt?
        LastRegularV4FrontierRepairPressureSettlementApplyReceipt = null)
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
    RuntimeUnlockPendingPreCommitProbeRequest? RuntimeUnlockPendingPreCommitProbeRequest = null,
    FallbackTailYieldTransportEpochReauthorizationRequest?
        FallbackTailYieldTransportEpochReauthorizationRequest = null,
    FallbackTailYieldTransportEpochReauthorization?
        FallbackTailYieldTransportEpochReauthorization = null);

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
    RuntimeUnlockRegularV4RecoveryLeaseClosureContinuationProof?
        RegularV4RecoveryLeaseClosureContinuationProof = null,
    DateTimeOffset? LivenessDeferralDeadlineUtc = null,
    int? RecoveryDecisionGeneration = null,
    int? RuntimeGeneration = null,
    int? TerminalGeneration = null,
    RoutePublicationToken? RoutePublicationToken = null,
    RuntimeUnlockPendingPreCommitProbeRequest? RuntimeUnlockPendingPreCommitProbeRequest = null,
    FileTransferAuthorityId? PredecessorAuthorityId = null,
    FallbackTailYieldTransportEpochReauthorization?
        FallbackTailYieldTransportEpochReauthorization = null);

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
    public RoutePublicationToken? RoutePublicationToken { get; set; }

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
    internal const int RuntimeUnlockRetryMissingDispatchSuccessorLimit = 1;

    private static readonly TimeSpan InitialTunaReadinessEvidenceMaxAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SessionReactivationInitialTunaReadinessEvidenceMaxAge = TimeSpan.FromMinutes(30);

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

        var decision = coordinatorEvent.Kind switch
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
        return ForwardFallbackRoutePublicationAuthorityAfterLegMutation(state, decision, reason);
    }

    private static FileTransferCoordinatorDecision ForwardFallbackRoutePublicationAuthorityAfterLegMutation(
        FileTransferCoordinatorState previousState,
        FileTransferCoordinatorDecision decision,
        string reason)
    {
        if (decision.TerminalMutationRejected ||
            decision.MutationRejectedReason is not null ||
            decision.State.IsTerminal ||
            decision.State.CurrentRoutePublicationToken is not null ||
            previousState.CurrentRoutePublicationToken is not
            {
                IsDefault: false,
                Route: FileTransferRoute.PostTunaFallbackV6,
                ProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
                TargetRoute: FileTransferRoute.PostTunaFallbackV6,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
                TargetTransport: FileTransferTransportKind.RegularNkn,
            } previousPublication ||
            decision.State.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            decision.State.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            decision.State.CurrentRuntimeOwner is not { } owner ||
            decision.State.CurrentLeg is not { } leg ||
            previousPublication.SessionId != owner.SessionId ||
            previousPublication.TransferId != owner.TransferId ||
            previousPublication.Direction != owner.Direction ||
            previousPublication.LiveRouteEpoch != (decision.State.CurrentLiveRouteEpoch?.EpochId ?? 0))
        {
            return decision;
        }

        var publication = AuthorizeAction(
            decision.State,
            new FileTransferCoordinatorActionRequest(
                FileTransferCoordinatorActionKind.RoutePublication,
                Source: "coordinator_leg_handoff",
                Reason: string.IsNullOrWhiteSpace(reason)
                    ? "fallback_route_publication_leg_handoff"
                    : reason,
                RuntimeOwner: owner,
                Route: FileTransferRoute.PostTunaFallbackV6,
                ProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
                LiveRouteEpoch: decision.State.CurrentLiveRouteEpoch?.EpochId ?? 0,
                LegGeneration: leg.Generation,
                RuntimeGeneration: owner.RuntimeGeneration,
                TerminalGeneration: decision.State.TerminalGeneration,
                TargetRoute: FileTransferRoute.PostTunaFallbackV6,
                TargetProtocolVersion: FileTransferProtocol.ProtocolVersionV6,
                TargetTransport: FileTransferTransportKind.RegularNkn));
        return publication.Accepted && publication.RoutePublicationToken is { IsDefault: false }
            ? decision with { State = publication.State }
            : decision;
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
              request.RecoveryLivenessLease?.RuntimeOwner ??
              request.RegularV4RecoveryLeaseClosureContinuationProof?.RetiredRecoveryLivenessLease.RuntimeOwner
            : request.RuntimeOwner ??
              request.RuntimeUnlockAdmissionToken?.RuntimeOwner ??
              request.RouteCommitToken?.RuntimeOwner ??
              request.RoutePublicationToken?.RuntimeOwner ??
              request.RuntimeUnlockPendingPreCommitProbeRequest?.RuntimeOwner ??
              request.FallbackControlPlaneToken?.RuntimeOwner ??
              request.FallbackCheckpointLease?.RuntimeOwner ??
              request.FallbackControlPlanePressureToken?.RuntimeOwner ??
              request.RecoveryLivenessLease?.RuntimeOwner ??
              request.RegularV4RecoveryLeaseClosureContinuationProof?.RetiredRecoveryLivenessLease.RuntimeOwner;
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

        if (HasRegularV4FrontierRepairPressureBinding(state) &&
            RequiresRegularV4FrontierRepairPressureRetirement(request.Kind))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                rejectionReason: "regular_v4_frontier_repair_pressure_retirement_required");
        }

        if (!ValidateRegularV4FrontierRepairPressureRepairReplayRequest(
                state,
                request,
                out var idempotentRepairReplayDecision,
                out var pressureRepairReplayRejectionReason))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                pressureRepairReplayRejectionReason);
        }

        if (idempotentRepairReplayDecision is { } existingRepairReplayDecision)
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: true,
                rejectionReason: "accepted",
                repairReplayDecision: existingRepairReplayDecision);
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
                    Math.Max(1, state.LastFallbackTailYieldGeneration + 1),
                    checkpointSnapshotOverride: request.Kind == FileTransferCoordinatorActionKind.FallbackTailQuiescent
                        ? "(none)"
                        : null,
                    predecessorAuthorityId:
                        request.FallbackTailYieldTransportEpochReauthorization?.PredecessorDecision.AuthorityId ??
                        request.PredecessorAuthorityId)
                : (FallbackTailYieldDecision?)null;
        var repairReplayDecision = request.Kind == FileTransferCoordinatorActionKind.RepairReplay
            ? CreateRepairReplayDecision(
                state,
                request.RepairReplayKind ?? RepairReplayKind.Unknown,
                request.Reason,
                Math.Max(1, state.LastRepairReplayGeneration + 1),
                request.PredecessorAuthorityId)
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
            FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery
                when state.RouteSelection.Route == FileTransferRoute.RegularNknV4Fast &&
                     state.RouteSelection.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4
                => IssueRecoveryLivenessLease(
                    state,
                    Math.Max(1, state.LastRecoveryLivenessLeaseGeneration + 1),
                    RecoveryLivenessLeaseState.Active,
                    request.Reason),
            FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin
                => IssueRecoveryLivenessLease(
                    state,
                    Math.Max(1, state.LastRecoveryLivenessLeaseGeneration + 1),
                    RecoveryLivenessLeaseState.Active,
                    request.Reason),
            FileTransferCoordinatorActionKind.LivenessDeferral or
            FileTransferCoordinatorActionKind.RecoveryLivenessLeaseValidate
                => request.RecoveryLivenessLease,
            FileTransferCoordinatorActionKind.RecoveryLeaseClosureLivenessDeferral
                => request.RegularV4RecoveryLeaseClosureContinuationProof?.RetiredRecoveryLivenessLease,
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

        if (request.Kind == FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery &&
            state.RouteSelection.Route == FileTransferRoute.RegularNknV4Fast &&
            state.RouteSelection.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
            (bridgeRecoveryDecision is null || recoveryLivenessLease is null))
        {
            return ActionDecision(
                state,
                request.Kind,
                accepted: false,
                rejectionReason: "regular_v4_recovery_authority_incomplete");
        }

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

        var acceptedState = ApplyIssuedFallbackAuthority(
            state,
            request.Kind,
            issuedControlPlaneToken,
            issuedCheckpointLease,
            issuedPressureToken,
            yieldDecision);
        acceptedState = ApplyIssuedRecoveryAuthority(
            acceptedState,
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

    private static bool TryValidateFallbackTailYieldTransportEpochReauthorizationRestart(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorEvent coordinatorEvent,
        FallbackTailYieldTransportEpochReauthorizationRequest request,
        FallbackTailYieldTransportEpochReauthorization pendingReauthorization,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (state.PendingFallbackTailYieldTransportEpochReauthorization is not { } pending ||
            pending != pendingReauthorization ||
            request.PredecessorDecision != pending.PredecessorDecision ||
            request.SourceRuntimeOwner != pending.SourceRuntimeOwner ||
            request.SourceRoutePublicationToken != pending.SourceRoutePublicationToken ||
            request.SourceTransportEpoch != pending.SourceTransportEpoch)
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_restart_lineage_mismatch";
            return false;
        }

        if (!IsFallbackTailYieldTransportEpochReauthorizationCurrent(
                state,
                pending,
                out rejectionReason))
        {
            return false;
        }

        if (state.CurrentLeg is not { } currentLeg ||
            state.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            state.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            coordinatorEvent.RouteSelection != state.RouteSelection ||
            request.TargetTransportEpoch <= pending.TargetTransportEpoch ||
            request.TargetTransportEpoch <= currentLeg.TransportEpochId ||
            coordinatorEvent.TransportEpochId != request.TargetTransportEpoch ||
            coordinatorEvent.BridgeRecoveryGeneration < currentLeg.BridgeRecoveryGeneration ||
            state.RuntimeUnlockFallbackSurvivalState.PairedFallbackYieldGranted)
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_restart_target_mismatch";
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

    public static bool IsFallbackControlPlaneTokenIssued(
        FileTransferCoordinatorState state,
        FallbackControlPlaneToken token,
        out string rejectionReason)
        => IsFallbackControlPlaneTokenCurrent(
            state,
            token,
            allowNextGeneration: false,
            allowIssuedGeneration: true,
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

        var expectedAuthorityId = IssueAuthorityId(
            "fallback_control_plane",
            state,
            token.TokenGeneration);
        if (token.AuthorityId != expectedAuthorityId)
        {
            rejectionReason = "fallback_control_plane_authority_id_mismatch";
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
                current.TokenGeneration != token.TokenGeneration ||
                current.AuthorityId != token.AuthorityId)
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

        if (current != lease)
        {
            rejectionReason = "recovery_liveness_lease_authority_mismatch";
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
            PendingFallbackTailYieldTransportEpochReauthorization = null,
        });

    private static FileTransferCoordinatorState InvalidateRecoveryAuthority(FileTransferCoordinatorState state)
        => state with
        {
            CurrentRepairReplayDecision = null,
            CurrentTransportEpochDecision = null,
            CurrentBridgeRecoveryDecision = null,
            PendingFallbackTailYieldTransportEpochReauthorization = null,
            CurrentRecoveryLivenessLease = null,
            CurrentRuntimeUnlockRetryDispatchAuthority = null,
            LastRetiredRecoveryLivenessLease = null,
            LastRuntimeUnlockRegularV4RecoverySettlement = null,
            LastCoordinatorIssuedRegularV4RecoverySettlement = null,
            RuntimeUnlockRegularV4RecoverySettlementAvailable = false,
            LastRuntimeUnlockRegularV4RecoveryLifecycle = null,
            RuntimeUnlockRecoveryLeaseClosureAvailable = false,
        };

    private static FileTransferCoordinatorState InvalidateRoutePublicationAuthority(FileTransferCoordinatorState state)
        => state with
        {
            CurrentRoutePublicationToken = null,
            PendingFallbackTailYieldTransportEpochReauthorization = null,
        };

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

        var pendingReauthorization = state.PendingFallbackTailYieldTransportEpochReauthorization;
        if (pendingReauthorization is { } pending)
        {
            if (pending.TargetRoutePublicationToken is null &&
                string.Equals(token.SessionId, pending.SessionId, StringComparison.Ordinal) &&
                string.Equals(token.TransferId, pending.TransferId, StringComparison.Ordinal) &&
                token.Direction == pending.Direction &&
                token.RuntimeOwner == pending.TargetRuntimeOwner &&
                token.Route == FileTransferRoute.PostTunaFallbackV6 &&
                token.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
                token.LiveRouteEpoch == pending.TargetRuntimeOwner.LiveRouteEpoch &&
                token.RuntimeGeneration == pending.TargetRuntimeOwner.RuntimeGeneration &&
                token.TerminalGeneration == pending.TargetTerminalGeneration &&
                token.LegGeneration == pending.TargetLegGeneration &&
                token.TargetRoute == FileTransferRoute.PostTunaFallbackV6 &&
                token.TargetProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
                token.TargetTransport == FileTransferTransportKind.RegularNkn)
            {
                pendingReauthorization = pending with { TargetRoutePublicationToken = token };
            }
            else if (pending.TargetRoutePublicationToken != token)
            {
                pendingReauthorization = null;
            }
        }

        return state with
        {
            CurrentRoutePublicationToken = token,
            LastRoutePublicationGeneration = token.PublicationGeneration,
            PendingFallbackTailYieldTransportEpochReauthorization = pendingReauthorization,
        };
    }

    private static FileTransferCoordinatorState ApplyIssuedFallbackAuthority(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorActionKind kind,
        FallbackControlPlaneToken? controlPlaneToken,
        FallbackCheckpointLease? checkpointLease,
        FallbackControlPlanePressureToken? pressureToken,
        FallbackTailYieldDecision? tailYieldDecision)
    {
        var next = state;
        if (controlPlaneToken is { } token &&
            kind == FileTransferCoordinatorActionKind.ControlPlaneSend)
        {
            next = next with
            {
                CurrentFallbackControlPlaneToken = token,
                LastFallbackControlPlaneTokenGeneration = token.TokenGeneration,
            };
        }

        if (checkpointLease is { } lease &&
            kind == FileTransferCoordinatorActionKind.FallbackCheckpointBegin)
        {
            next = next with
            {
                CurrentFallbackCheckpointLease = lease,
                LastFallbackCheckpointLeaseGeneration = lease.LeaseGeneration,
            };
        }

        if (pressureToken is { } pressure &&
            kind == FileTransferCoordinatorActionKind.FallbackControlPlanePressure)
        {
            next = next with
            {
                CurrentFallbackControlPlanePressureToken = pressure,
                LastFallbackControlPlanePressureGeneration = pressure.PressureGeneration,
            };
        }

        if (tailYieldDecision is { } yield)
        {
            next = next with
            {
                LastFallbackTailYieldDecision = yield,
                LastFallbackTailYieldGeneration = yield.YieldGeneration,
                CurrentFallbackCheckpointLease = kind == FileTransferCoordinatorActionKind.FallbackTailQuiescent
                    ? null
                    : next.CurrentFallbackCheckpointLease,
                PendingFallbackTailYieldTransportEpochReauthorization = null,
            };
        }

        return next;
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
            NormalizeReason(reason))
        {
            AuthorityId = IssueAuthorityId("fallback_control_plane", state, tokenGeneration),
        };
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
        int yieldGeneration,
        string? checkpointSnapshotOverride = null,
        FileTransferAuthorityId? predecessorAuthorityId = null)
    {
        var leg = state.CurrentLeg;
        var checkpointSnapshot = string.IsNullOrWhiteSpace(checkpointSnapshotOverride)
            ? NormalizeFallbackTailYieldCheckpointRequestId(state.CurrentFallbackCheckpointLease)
            : checkpointSnapshotOverride;
        var ownerKey = leg is null
            ? string.Empty
            : BuildFallbackTailYieldOwnerKey(
                state.SessionId,
                state.TransferId,
                state.RouteSelection.TelemetryToken,
                state.RouteSelection.ProtocolVersion,
                state.CurrentLiveRouteEpoch?.EpochId ?? leg.LiveRouteEpochId,
                leg.Generation,
                leg.BridgeRecoveryGeneration,
                leg.TransportEpochId,
                checkpointSnapshot);
        var pairKey = leg is null
            ? string.Empty
            : BuildFallbackTailYieldPairKey(
                state.SessionId,
                state.TransferId,
                state.RouteSelection.TelemetryToken,
                state.RouteSelection.ProtocolVersion,
                state.CurrentLiveRouteEpoch?.EpochId ?? leg.LiveRouteEpochId,
                leg.TransportEpochId,
                checkpointSnapshot);

        return new FallbackTailYieldDecision(
            accepted,
            yieldKind,
            NormalizeReason(reason),
            yieldGeneration,
            ownerKey,
            pairKey,
            checkpointSnapshot,
            state.RouteSelection.Route,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            state.CurrentRuntimeOwner?.RuntimeGeneration ?? state.LastRuntimeOwnerGeneration,
            state.TerminalGeneration,
            leg?.Generation ?? 0,
            leg?.BridgeRecoveryGeneration ?? 0,
            state.CurrentFallbackCheckpointLease,
            NormalizeRuntimeUnlockFallbackSurvivalState(state))
        {
            AuthorityId = IssueAuthorityId("fallback_tail_yield", state, yieldGeneration),
            PredecessorAuthorityId = predecessorAuthorityId is { IsDefault: false }
                ? predecessorAuthorityId
                : state.LastFallbackTailYieldDecision is { IsDefault: false } predecessor
                    ? predecessor.AuthorityId
                    : null,
        };
    }

    public static bool IsFallbackTailYieldDecisionCurrent(
        FileTransferCoordinatorState state,
        FallbackTailYieldDecision decision,
        out string rejectionReason)
    {
        rejectionReason = "current";
        if (decision.IsDefault)
        {
            rejectionReason = "fallback_tail_yield_authority_missing";
            return false;
        }

        if (state.LastFallbackTailYieldDecision is not { } current ||
            current != decision)
        {
            rejectionReason = "fallback_tail_yield_authority_stale";
            return false;
        }

        if (decision.AuthorityId != IssueAuthorityId("fallback_tail_yield", state, decision.YieldGeneration))
        {
            rejectionReason = "fallback_tail_yield_authority_id_mismatch";
            return false;
        }

        var leg = state.CurrentLeg;
        var checkpointSnapshot = NormalizeFallbackTailYieldCheckpointRequestId(
            state.CurrentFallbackCheckpointLease);
        var expectedOwnerKey = leg is null
            ? string.Empty
            : BuildFallbackTailYieldOwnerKey(
                state.SessionId,
                state.TransferId,
                state.RouteSelection.TelemetryToken,
                state.RouteSelection.ProtocolVersion,
                state.CurrentLiveRouteEpoch?.EpochId ?? leg.LiveRouteEpochId,
                leg.Generation,
                leg.BridgeRecoveryGeneration,
                leg.TransportEpochId,
                checkpointSnapshot);
        var expectedPairKey = leg is null
            ? string.Empty
            : BuildFallbackTailYieldPairKey(
                state.SessionId,
                state.TransferId,
                state.RouteSelection.TelemetryToken,
                state.RouteSelection.ProtocolVersion,
                state.CurrentLiveRouteEpoch?.EpochId ?? leg.LiveRouteEpochId,
                leg.TransportEpochId,
                checkpointSnapshot);
        if (!decision.Accepted ||
            leg is null ||
            decision.CurrentRoute != state.RouteSelection.Route ||
            decision.CurrentProtocolVersion != state.RouteSelection.ProtocolVersion ||
            decision.CurrentLiveRouteEpoch != (state.CurrentLiveRouteEpoch?.EpochId ?? 0) ||
            decision.CurrentRuntimeGeneration != (state.CurrentRuntimeOwner?.RuntimeGeneration ?? 0) ||
            decision.CurrentTerminalGeneration != state.TerminalGeneration ||
            decision.CurrentLegGeneration != leg.Generation ||
            decision.CurrentBridgeRecoveryGeneration != leg.BridgeRecoveryGeneration ||
            !string.Equals(
                decision.CheckpointRequestIdSnapshot,
                checkpointSnapshot,
                StringComparison.Ordinal) ||
            !string.Equals(decision.OwnerKey, expectedOwnerKey, StringComparison.Ordinal) ||
            !string.Equals(decision.PairKey, expectedPairKey, StringComparison.Ordinal))
        {
            rejectionReason = "fallback_tail_yield_authority_tuple_mismatch";
            return false;
        }

        return true;
    }

    public static bool IsFallbackTailYieldTransportEpochReauthorizationCurrent(
        FileTransferCoordinatorState state,
        FallbackTailYieldTransportEpochReauthorization reauthorization,
        out string rejectionReason)
    {
        rejectionReason = "current";
        if (reauthorization.IsDefault)
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_missing";
            return false;
        }

        if (state.PendingFallbackTailYieldTransportEpochReauthorization is not { } pending ||
            pending != reauthorization)
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_stale";
            return false;
        }

        if (state.LastFallbackTailYieldDecision is not null)
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_lineage_conflict";
            return false;
        }

        return IsFallbackTailYieldTransportEpochReauthorizationTargetScopeCurrent(
            state,
            reauthorization,
            out rejectionReason);
    }

    internal static bool IsFallbackTailYieldTransportEpochReauthorizationTargetScopeCurrent(
        FileTransferCoordinatorState state,
        FallbackTailYieldTransportEpochReauthorization reauthorization,
        out string rejectionReason)
    {
        rejectionReason = "current";
        if (reauthorization.IsDefault)
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_missing";
            return false;
        }

        if (reauthorization.TargetRoutePublicationToken is not { } targetPublication ||
            state.CurrentRoutePublicationToken != targetPublication ||
            !IsRoutePublicationTokenCurrent(state, targetPublication, out _))
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_publication_stale";
            return false;
        }

        if (state.CurrentBridgeRecoveryDecision != reauthorization.TargetBridgeRecoveryDecision)
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_bridge_decision_stale";
            return false;
        }

        if (state.IsTerminal ||
            state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg)
        {
            rejectionReason = state.IsTerminal
                ? "transfer_terminal"
                : "fallback_tail_yield_transport_epoch_reauthorization_scope_missing";
            return false;
        }

        var checkpointSnapshot = NormalizeFallbackTailYieldCheckpointRequestId(
            state.CurrentFallbackCheckpointLease);
        var sourcePublication = reauthorization.SourceRoutePublicationToken;
        var predecessor = reauthorization.PredecessorDecision;
        if (!string.Equals(reauthorization.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(reauthorization.TransferId, state.TransferId, StringComparison.Ordinal) ||
            reauthorization.Direction != state.Direction ||
            !string.Equals(sourcePublication.SessionId, reauthorization.SessionId, StringComparison.Ordinal) ||
            !string.Equals(sourcePublication.TransferId, reauthorization.TransferId, StringComparison.Ordinal) ||
            sourcePublication.Direction != reauthorization.Direction ||
            sourcePublication.RuntimeOwner != reauthorization.SourceRuntimeOwner ||
            sourcePublication.Route != predecessor.CurrentRoute ||
            sourcePublication.ProtocolVersion != predecessor.CurrentProtocolVersion ||
            sourcePublication.LiveRouteEpoch != predecessor.CurrentLiveRouteEpoch ||
            sourcePublication.RuntimeGeneration != predecessor.CurrentRuntimeGeneration ||
            sourcePublication.TerminalGeneration != predecessor.CurrentTerminalGeneration ||
            sourcePublication.LegGeneration != predecessor.CurrentLegGeneration ||
            sourcePublication.TargetRoute != FileTransferRoute.PostTunaFallbackV6 ||
            sourcePublication.TargetProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            sourcePublication.TargetTransport != FileTransferTransportKind.RegularNkn ||
            reauthorization.TargetRuntimeOwner != owner ||
            reauthorization.TargetTerminalGeneration != state.TerminalGeneration ||
            reauthorization.TargetLegGeneration != leg.Generation ||
            reauthorization.TargetTransportEpoch != leg.TransportEpochId ||
            reauthorization.TargetBridgeRecoveryGeneration != leg.BridgeRecoveryGeneration ||
            !string.Equals(
                reauthorization.TargetCheckpointRequestIdSnapshot,
                checkpointSnapshot,
                StringComparison.Ordinal) ||
            state.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            state.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            predecessor.YieldKind != FallbackTailYieldKind.Quiescent ||
            !predecessor.Accepted ||
            predecessor.AuthorityId.IsDefault ||
            !string.Equals(
                reauthorization.SourceOwnerKey,
                predecessor.OwnerKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                reauthorization.SourcePairKey,
                predecessor.PairKey,
                StringComparison.Ordinal) ||
            (reauthorization.TargetBridgeRecoveryDecision is { } targetBridge &&
             (targetBridge.IsDefault ||
              !targetBridge.Accepted ||
              targetBridge.RuntimeOwner != owner ||
              targetBridge.CurrentTerminalGeneration != state.TerminalGeneration ||
              targetBridge.CurrentLegGeneration != leg.Generation ||
              targetBridge.TransportEpoch != leg.TransportEpochId ||
              targetBridge.BridgeRecoveryGeneration != leg.BridgeRecoveryGeneration)))
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_tuple_mismatch";
            return false;
        }

        return true;
    }

    public static FileTransferCoordinatorDecision RebindFallbackTailYieldTransportEpochReauthorization(
        FileTransferCoordinatorState state,
        FallbackTailYieldTransportEpochReauthorization reauthorization,
        long targetTransportEpoch,
        string reason)
    {
        if (state.PendingFallbackTailYieldTransportEpochReauthorization is not { } pending ||
            pending != reauthorization)
        {
            return MutationRejected(
                state,
                reason,
                "fallback_tail_yield_transport_epoch_reauthorization_stale");
        }

        if (state.IsTerminal ||
            state.LastFallbackTailYieldDecision is not null ||
            state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg ||
            state.CurrentRoutePublicationToken is not { } publication ||
            publication != reauthorization.TargetRoutePublicationToken ||
            !IsRoutePublicationTokenCurrent(state, publication, out _) ||
            state.CurrentBridgeRecoveryDecision != reauthorization.TargetBridgeRecoveryDecision ||
            state.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            state.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            owner != reauthorization.TargetRuntimeOwner ||
            state.TerminalGeneration != reauthorization.TargetTerminalGeneration ||
            leg.Generation != reauthorization.TargetLegGeneration ||
            targetTransportEpoch != leg.TransportEpochId ||
            targetTransportEpoch < reauthorization.TargetTransportEpoch ||
            !string.Equals(
                NormalizeFallbackTailYieldCheckpointRequestId(state.CurrentFallbackCheckpointLease),
                reauthorization.TargetCheckpointRequestIdSnapshot,
                StringComparison.Ordinal))
        {
            return MutationRejected(
                state,
                reason,
                "fallback_tail_yield_transport_epoch_reauthorization_rebind_mismatch");
        }

        if (targetTransportEpoch == reauthorization.TargetTransportEpoch &&
            leg.BridgeRecoveryGeneration == reauthorization.TargetBridgeRecoveryGeneration)
        {
            return Decision(state, reason);
        }

        var nextGeneration = Math.Max(
            reauthorization.ReauthorizationGeneration + 1,
            state.LastFallbackTailYieldTransportEpochReauthorizationGeneration + 1);
        var rebound = reauthorization with
        {
            TargetTransportEpoch = targetTransportEpoch,
            TargetBridgeRecoveryGeneration = leg.BridgeRecoveryGeneration,
            ReauthorizationGeneration = nextGeneration,
            Reason = NormalizeReason(reason),
        };
        return Decision(
            state with
            {
                PendingFallbackTailYieldTransportEpochReauthorization = rebound,
                LastFallbackTailYieldTransportEpochReauthorizationGeneration = nextGeneration,
            },
            reason);
    }

    internal static string NormalizeFallbackTailYieldCheckpointRequestId(FallbackCheckpointLease? checkpointLease)
        => checkpointLease is { State: FallbackCheckpointLeaseState.Pending } lease &&
           !string.IsNullOrWhiteSpace(lease.CheckpointRequestId)
            ? lease.CheckpointRequestId
            : "(none)";

    internal static string BuildFallbackTailYieldOwnerKey(
        string sessionId,
        string transferId,
        string routeToken,
        int protocolVersion,
        int liveRouteEpoch,
        int legGeneration,
        int bridgeRecoveryGeneration,
        long transportEpoch,
        string? checkpointRequestIdSnapshot)
        => string.Join(
            "|",
            sessionId,
            transferId,
            routeToken,
            protocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            liveRouteEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            legGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bridgeRecoveryGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            transportEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(checkpointRequestIdSnapshot) ? "(none)" : checkpointRequestIdSnapshot);

    internal static string BuildFallbackTailYieldPairKey(
        string sessionId,
        string transferId,
        string routeToken,
        int protocolVersion,
        int liveRouteEpoch,
        long transportEpoch,
        string? checkpointRequestIdSnapshot)
        => string.Join(
            "|",
            sessionId,
            transferId,
            routeToken,
            protocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            liveRouteEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            transportEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(checkpointRequestIdSnapshot) ? "(none)" : checkpointRequestIdSnapshot);

    private static RepairReplayDecision? CreateRepairReplayDecision(
        FileTransferCoordinatorState state,
        RepairReplayKind repairKind,
        string reason,
        int repairGeneration,
        FileTransferAuthorityId? predecessorAuthorityId)
    {
        if (state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg)
        {
            return null;
        }

        var authorityId = IssueAuthorityId(
            "repair_replay",
            state,
            repairGeneration);
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
            owner,
            authorityId,
            predecessorAuthorityId is { IsDefault: false } ? predecessorAuthorityId : null,
            state.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration ?? 0,
            state.CurrentRoutePublicationToken);
    }

    internal static bool TryIssueRegularV4FrontierRepairAuthority(
        RepairReplayDecision repairDecision,
        RegularV4FrontierRepairAuthorityRequest request,
        out FileTransferRegularV4FrontierRepairAuthority? authority,
        out string rejectionReason)
    {
        authority = null;
        rejectionReason = "accepted";
        if (repairDecision.IsDefault || repairDecision.AuthorityId.IsDefault)
        {
            rejectionReason = "repair_replay_decision_missing";
            return false;
        }

        if (repairDecision.RoutePublicationToken is not { IsDefault: false } publication)
        {
            rejectionReason = "route_publication_authority_missing";
            return false;
        }

        var owner = repairDecision.RuntimeOwner;
        if (!string.Equals(owner.SessionId, publication.SessionId, StringComparison.Ordinal) ||
            !string.Equals(owner.TransferId, publication.TransferId, StringComparison.Ordinal) ||
            owner.Direction != publication.Direction ||
            owner.RuntimeGeneration != publication.RuntimeGeneration ||
            owner.TerminalGeneration != publication.TerminalGeneration ||
            repairDecision.CurrentRoute != publication.Route ||
            repairDecision.CurrentProtocolVersion != publication.ProtocolVersion ||
            repairDecision.CurrentLiveRouteEpoch != publication.LiveRouteEpoch ||
            repairDecision.CurrentRuntimeGeneration != publication.RuntimeGeneration ||
            repairDecision.CurrentTerminalGeneration != publication.TerminalGeneration ||
            repairDecision.CurrentLegGeneration != publication.LegGeneration)
        {
            rejectionReason = "repair_route_publication_authority_mismatch";
            return false;
        }

        var expectedTransport = repairDecision.CurrentRoute switch
        {
            FileTransferRoute.RegularNknV4Fast => FileTransferTransportKind.RegularNkn,
            FileTransferRoute.FileTunaV4 => FileTransferTransportKind.Tuna,
            _ => FileTransferTransportKind.Unknown,
        };
        if (repairDecision.CurrentProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            expectedTransport == FileTransferTransportKind.Unknown ||
            publication.TargetTransport != expectedTransport ||
            (repairDecision.CurrentRoute == FileTransferRoute.FileTunaV4 &&
             publication.RouteCommitGeneration is not > 0))
        {
            rejectionReason = "regular_v4_repair_route_invariant_mismatch";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.RepairKey))
        {
            rejectionReason = "repair_key_missing";
            return false;
        }

        authority = new FileTransferRegularV4FrontierRepairAuthority(
            owner.SessionId,
            owner.TransferId,
            owner.Direction,
            publication.RouteToken,
            publication.ProtocolVersion,
            publication.LiveRouteEpoch,
            publication.RuntimeGeneration,
            publication.TerminalGeneration,
            publication.LegGeneration,
            repairDecision.TransportEpoch,
            repairDecision.BridgeRecoveryGeneration,
            repairDecision.BridgeDecisionGeneration,
            publication.PublicationGeneration,
            publication.RouteCommitGeneration,
            request.RepairKey.Trim(),
            Math.Max(0, request.FrontierChunkIndex),
            Math.Max(-1, request.DurableReceivedHighestChunkIndex),
            Math.Max(0, request.PendingRepairCount),
            repairDecision.RepairGeneration,
            string.IsNullOrWhiteSpace(request.DecisionReason)
                ? repairDecision.Reason
                : request.DecisionReason.Trim(),
            publication.TargetTransport)
        {
            AuthorityId = repairDecision.AuthorityId,
            PredecessorAuthorityId = repairDecision.PredecessorAuthorityId,
        };
        return true;
    }

    internal static RegularV4FrontierRepairPressureBindingDecision
        StageRegularV4FrontierRepairPressureBinding(
            FileTransferCoordinatorState state,
            RepairReplayDecision repairDecision,
            RegularV4FrontierRepairAuthorityRequest authorityRequest,
            string pressureReason)
    {
        var normalizedReason = NormalizeReason(pressureReason);
        if (state.IsTerminal)
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "terminal_state");
        }

        if (!IsRepairReplayDecisionCurrent(state, repairDecision, out var currentnessReason))
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                currentnessReason);
        }

        if (!TryIssueRegularV4FrontierRepairAuthority(
                repairDecision,
                authorityRequest,
                out var issuedAuthority,
                out var issuanceReason) ||
            issuedAuthority is null)
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                issuanceReason);
        }

        if (state.PendingRegularV4FrontierRepairPressureBinding is { } pending)
        {
            if (pending.Authority == issuedAuthority &&
                string.Equals(pending.PressureReason, normalizedReason, StringComparison.Ordinal))
            {
                return RegularV4PressureBindingDecision(
                    state,
                    RegularV4FrontierRepairPressureBindingDecisionStatus.Idempotent,
                    "regular_v4_frontier_repair_pressure_binding_already_pending",
                    pending);
            }

            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_binding_pending_conflict");
        }

        var installed = state.InstalledRegularV4FrontierRepairPressureBinding;
        if (installed is { } current && current.Authority == issuedAuthority)
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Idempotent,
                "regular_v4_frontier_repair_pressure_binding_already_installed",
                current);
        }

        if (installed is { } active)
        {
            if (issuedAuthority.PredecessorAuthorityId is not { IsDefault: false } predecessorId ||
                predecessorId != active.Authority.AuthorityId)
            {
                return RegularV4PressureBindingDecision(
                    state,
                    RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                    "regular_v4_frontier_repair_pressure_binding_predecessor_mismatch");
            }
        }
        else if (issuedAuthority.PredecessorAuthorityId is { IsDefault: false })
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_binding_predecessor_missing");
        }

        if (state.RetainedRegularV4FrontierRepairPressurePredecessor is not null)
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_binding_predecessor_retirement_pending");
        }

        var nextGeneration = Math.Max(
            1,
            state.LastRegularV4FrontierRepairPressureInstallGeneration + 1);
        var binding = new RegularV4FrontierRepairPressureBinding(
            issuedAuthority,
            normalizedReason,
            nextGeneration,
            RegularV4FrontierRepairPressureBindingState.Pending,
            issuedAuthority.PredecessorAuthorityId,
            FinalSettlement: null);
        var nextState = state with
        {
            PendingRegularV4FrontierRepairPressureBinding = binding,
            LastRegularV4FrontierRepairPressureInstallGeneration = nextGeneration,
        };
        return RegularV4PressureBindingDecision(
            nextState,
            RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted,
            "regular_v4_frontier_repair_pressure_binding_staged",
            binding);
    }

    internal static RegularV4FrontierRepairPressureBindingDecision
        CommitRegularV4FrontierRepairPressureBinding(
            FileTransferCoordinatorState state,
            FileTransferRegularV4FrontierRepairPressureInstallReceipt receipt)
    {
        if (state.InstalledRegularV4FrontierRepairPressureBinding is { } installed &&
            installed.Authority == receipt.Authority &&
            installed.InstallGeneration == receipt.InstallGeneration &&
            string.Equals(installed.PressureReason, receipt.PressureReason, StringComparison.Ordinal))
        {
            if (receipt.Status is not (
                    FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed or
                    FileTransferRegularV4FrontierRepairPressureInstallStatus.Idempotent) ||
                string.IsNullOrWhiteSpace(receipt.Reason) ||
                !string.Equals(receipt.Reason, NormalizeReason(receipt.Reason), StringComparison.Ordinal))
            {
                return RegularV4PressureBindingDecision(
                    state,
                    RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                    "regular_v4_frontier_repair_pressure_binding_install_receipt_mismatch");
            }

            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Idempotent,
                "regular_v4_frontier_repair_pressure_binding_already_committed",
                installed);
        }

        if (state.PendingRegularV4FrontierRepairPressureBinding is not { } pending)
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_binding_pending_missing");
        }

        if (receipt.Status is not (
                FileTransferRegularV4FrontierRepairPressureInstallStatus.Installed or
                FileTransferRegularV4FrontierRepairPressureInstallStatus.Idempotent) ||
            receipt.Authority != pending.Authority ||
            receipt.InstallGeneration != pending.InstallGeneration ||
            !string.Equals(receipt.PressureReason, pending.PressureReason, StringComparison.Ordinal))
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_binding_install_receipt_mismatch");
        }

        var committed = pending with { State = RegularV4FrontierRepairPressureBindingState.Installed };
        var predecessor = state.InstalledRegularV4FrontierRepairPressureBinding is { } current &&
                          pending.PredecessorAuthorityId == current.Authority.AuthorityId
            ? current with { State = RegularV4FrontierRepairPressureBindingState.RetainedPredecessor }
            : state.RetainedRegularV4FrontierRepairPressurePredecessor;
        var nextState = state with
        {
            PendingRegularV4FrontierRepairPressureBinding = null,
            InstalledRegularV4FrontierRepairPressureBinding = committed,
            RetainedRegularV4FrontierRepairPressurePredecessor = predecessor,
        };
        return RegularV4PressureBindingDecision(
            nextState,
            RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted,
            "regular_v4_frontier_repair_pressure_binding_committed",
            committed);
    }

    internal static RegularV4FrontierRepairPressureBindingDecision
        PrepareRegularV4FrontierRepairPressureSettlement(
            FileTransferCoordinatorState state,
            FileTransferRegularV4FrontierRepairSettlement settlement)
    {
        if (state.LastRegularV4FrontierRepairPressureSettlement == settlement)
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Idempotent,
                "regular_v4_frontier_repair_pressure_settlement_already_committed");
        }

        var installed = state.InstalledRegularV4FrontierRepairPressureBinding;
        var retained = state.RetainedRegularV4FrontierRepairPressurePredecessor;
        var settlingRetained = retained is { } predecessor &&
                              predecessor.Authority.AuthorityId == settlement.AuthorityId;
        var settlingCurrent = installed is { } current &&
                             current.Authority.AuthorityId == settlement.AuthorityId;
        if (!settlingRetained && !settlingCurrent)
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_settlement_binding_missing");
        }

        var settlingBinding = settlingRetained ? retained!.Value : installed!.Value;
        if (!RegularV4FrontierRepairPressureBindingMatchesSettlement(
                settlingBinding,
                settlement))
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_settlement_authority_mismatch");
        }

        var retainedSuccessorMatches = settlement.SuccessorAuthorityId is { IsDefault: false } successorId &&
                                      installed is { } successor &&
                                      successor.Authority.AuthorityId == successorId;
        if (settlingRetained &&
            (settlement.Outcome != FileTransferRegularV4FrontierRepairSettlementOutcome.Superseded ||
             !retainedSuccessorMatches))
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_predecessor_settlement_mismatch");
        }

        if (settlingCurrent && settlement.Outcome == FileTransferRegularV4FrontierRepairSettlementOutcome.Superseded)
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_successor_binding_missing");
        }

        if (settlingCurrent && retained is not null)
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_binding_predecessor_retirement_pending");
        }

        var commandCurrentAuthorityId = installed!.Value.Authority.AuthorityId;
        var command = new FileTransferRegularV4FrontierRepairPressureSettlementCommand(
            settlement,
            installed!.Value.InstallGeneration,
            commandCurrentAuthorityId,
            retained?.Authority.AuthorityId);
        return RegularV4PressureBindingDecision(
            state,
            RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted,
            "regular_v4_frontier_repair_pressure_settlement_prepared",
            settlingRetained ? retained : installed,
            command);
    }

    internal static RegularV4FrontierRepairPressureBindingDecision
        CommitRegularV4FrontierRepairPressureSettlement(
            FileTransferCoordinatorState state,
            FileTransferRegularV4FrontierRepairPressureSettlementApplyReceipt receipt)
    {
        var prepared = PrepareRegularV4FrontierRepairPressureSettlement(
            state,
            receipt.Command.Settlement);
        if (prepared.Status == RegularV4FrontierRepairPressureBindingDecisionStatus.Idempotent)
        {
            var committedReceipt = state.LastRegularV4FrontierRepairPressureSettlementApplyReceipt;
            if (committedReceipt is null ||
                committedReceipt.Command != receipt.Command ||
                receipt.Status is not (
                    FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied or
                    FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent) ||
                committedReceipt.CurrentAuthorityCleared != receipt.CurrentAuthorityCleared ||
                committedReceipt.RetainedPredecessorCleared != receipt.RetainedPredecessorCleared)
            {
                return RegularV4PressureBindingDecision(
                    state,
                    RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                    "regular_v4_frontier_repair_pressure_settlement_apply_receipt_mismatch");
            }

            return prepared;
        }

        if (prepared.Status != RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted ||
            prepared.SettlementCommand != receipt.Command ||
            receipt.Status is not (
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Applied or
                FileTransferRegularV4FrontierRepairPressureSettlementApplyStatus.Idempotent))
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_settlement_apply_receipt_mismatch");
        }

        var settlement = receipt.Command.Settlement;
        var settlingRetained = state.RetainedRegularV4FrontierRepairPressurePredecessor is { } retained &&
                              retained.Authority.AuthorityId == settlement.AuthorityId;
        if (settlingRetained &&
            (!receipt.RetainedPredecessorCleared || receipt.CurrentAuthorityCleared))
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_predecessor_clear_scope_mismatch");
        }

        if (!settlingRetained &&
            (!receipt.CurrentAuthorityCleared || receipt.RetainedPredecessorCleared))
        {
            return RegularV4PressureBindingDecision(
                state,
                RegularV4FrontierRepairPressureBindingDecisionStatus.Rejected,
                "regular_v4_frontier_repair_pressure_current_clear_scope_mismatch");
        }

        var nextState = settlingRetained
            ? state with
            {
                RetainedRegularV4FrontierRepairPressurePredecessor = null,
                LastRegularV4FrontierRepairPressureSettlement = settlement,
                LastRegularV4FrontierRepairPressureSettlementApplyReceipt = receipt,
            }
            : state with
            {
                InstalledRegularV4FrontierRepairPressureBinding = null,
                PendingRegularV4FrontierRepairPressureBinding = null,
                LastRegularV4FrontierRepairPressureSettlement = settlement,
                LastRegularV4FrontierRepairPressureSettlementApplyReceipt = receipt,
            };
        return RegularV4PressureBindingDecision(
            nextState,
            RegularV4FrontierRepairPressureBindingDecisionStatus.Accepted,
            "regular_v4_frontier_repair_pressure_settlement_committed");
    }

    private static bool RegularV4FrontierRepairPressureBindingMatchesSettlement(
        RegularV4FrontierRepairPressureBinding binding,
        FileTransferRegularV4FrontierRepairSettlement settlement)
    {
        var authority = binding.Authority;
        return !settlement.AuthorityId.IsDefault &&
               authority.AuthorityId == settlement.AuthorityId &&
               string.Equals(authority.SessionId, settlement.SessionId, StringComparison.Ordinal) &&
               string.Equals(authority.TransferId, settlement.TransferId, StringComparison.Ordinal) &&
               authority.Direction == settlement.Direction &&
               string.Equals(authority.RouteToken, settlement.RouteToken, StringComparison.Ordinal) &&
               authority.ProtocolVersion == settlement.ProtocolVersion &&
               authority.LiveRouteEpoch == settlement.LiveRouteEpoch &&
               authority.RuntimeGeneration == settlement.RuntimeGeneration &&
               authority.TerminalGeneration == settlement.TerminalGeneration &&
               authority.LegGeneration == settlement.LegGeneration &&
               authority.TransportEpoch == settlement.TransportEpoch &&
               authority.BridgeRecoveryGeneration == settlement.BridgeRecoveryGeneration &&
               authority.BridgeDecisionGeneration == settlement.BridgeDecisionGeneration &&
               authority.RoutePublicationGeneration == settlement.RoutePublicationGeneration &&
               authority.RouteCommitGeneration == settlement.RouteCommitGeneration &&
               authority.TargetTransport == settlement.TargetTransport &&
               string.Equals(authority.RepairKey, settlement.RepairKey, StringComparison.Ordinal) &&
               authority.FrontierChunkIndex == settlement.FrontierChunkIndex &&
               authority.DurableReceivedHighestChunkIndex == settlement.DurableReceivedHighestChunkIndex &&
               authority.PendingRepairCount == settlement.RequestedChunkCount &&
               authority.DecisionGeneration == settlement.DecisionGeneration;
    }

    internal static FileTransferCoordinatorState InvalidateRegularV4FrontierRepairPressureBinding(
        FileTransferCoordinatorState state,
        string reason)
        => state with
        {
            PendingRegularV4FrontierRepairPressureBinding = null,
            InstalledRegularV4FrontierRepairPressureBinding = null,
            RetainedRegularV4FrontierRepairPressurePredecessor = null,
        };

    private static RegularV4FrontierRepairPressureBindingDecision RegularV4PressureBindingDecision(
        FileTransferCoordinatorState state,
        RegularV4FrontierRepairPressureBindingDecisionStatus status,
        string reason,
        RegularV4FrontierRepairPressureBinding? binding = null,
        FileTransferRegularV4FrontierRepairPressureSettlementCommand? settlementCommand = null)
        => new(status, NormalizeReason(reason), binding, settlementCommand) { State = state };

    internal static bool TryIssueFallbackFrontierRepairAuthority(
        RepairReplayDecision repairDecision,
        FallbackFrontierRepairAuthorityRequest request,
        out FallbackFrontierRepairAuthority? authority,
        out string rejectionReason)
    {
        authority = null;
        rejectionReason = "accepted";
        if (!repairDecision.Accepted ||
            repairDecision.IsDefault ||
            repairDecision.AuthorityId.IsDefault)
        {
            rejectionReason = "repair_replay_decision_missing";
            return false;
        }

        if (repairDecision.RoutePublicationToken is not { IsDefault: false } publication)
        {
            rejectionReason = "route_publication_authority_missing";
            return false;
        }

        var owner = repairDecision.RuntimeOwner;
        if (!string.Equals(owner.SessionId, publication.SessionId, StringComparison.Ordinal) ||
            !string.Equals(owner.TransferId, publication.TransferId, StringComparison.Ordinal) ||
            owner.Direction != publication.Direction ||
            owner.RuntimeGeneration != publication.RuntimeGeneration ||
            owner.TerminalGeneration != publication.TerminalGeneration ||
            repairDecision.CurrentRoute != publication.Route ||
            repairDecision.CurrentProtocolVersion != publication.ProtocolVersion ||
            repairDecision.CurrentLiveRouteEpoch != publication.LiveRouteEpoch ||
            repairDecision.CurrentRuntimeGeneration != publication.RuntimeGeneration ||
            repairDecision.CurrentTerminalGeneration != publication.TerminalGeneration ||
            repairDecision.CurrentLegGeneration != publication.LegGeneration)
        {
            rejectionReason = "repair_route_publication_authority_mismatch";
            return false;
        }

        if (repairDecision.RepairKind != RepairReplayKind.FrontierStallRepair ||
            repairDecision.CurrentRoute != FileTransferRoute.PostTunaFallbackV6 ||
            repairDecision.CurrentProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            repairDecision.TransportEpoch <= 0 ||
            publication.Route != FileTransferRoute.PostTunaFallbackV6 ||
            publication.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            publication.TargetRoute != FileTransferRoute.PostTunaFallbackV6 ||
            publication.TargetProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            publication.TargetTransport != FileTransferTransportKind.RegularNkn)
        {
            rejectionReason = "fallback_frontier_repair_route_invariant_mismatch";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.CanonicalRepairKey) ||
            string.IsNullOrWhiteSpace(request.RangeFingerprint) ||
            request.FrontierChunkIndex < 0 ||
            request.RangeCount <= 0 ||
            request.RequestedChunkCount <= 0 ||
            request.SourceKind == FallbackFrontierRepairSourceKind.Unknown ||
            string.IsNullOrWhiteSpace(request.ProtocolRepairRequestId) ||
            string.IsNullOrWhiteSpace(request.RequestCorrelationId))
        {
            rejectionReason = "fallback_frontier_repair_identity_missing";
            return false;
        }

        var normalizedRangeFingerprint = request.RangeFingerprint.Trim();
        var expectedCanonicalRepairKey =
            $"fallback-frontier:{request.FrontierChunkIndex}:{request.RequestedChunkCount}:{normalizedRangeFingerprint}";
        if (!normalizedRangeFingerprint.StartsWith("frng-", StringComparison.Ordinal) ||
            !string.Equals(
                request.CanonicalRepairKey.Trim(),
                expectedCanonicalRepairKey,
                StringComparison.Ordinal))
        {
            rejectionReason = "fallback_frontier_repair_identity_mismatch";
            return false;
        }

        authority = new FallbackFrontierRepairAuthority(
            owner.SessionId,
            owner.TransferId,
            owner.Direction,
            owner,
            publication.Route,
            publication.RouteToken,
            publication.ProtocolVersion,
            publication.LiveRouteEpoch,
            publication.RuntimeGeneration,
            publication.TerminalGeneration,
            publication.LegGeneration,
            repairDecision.TransportEpoch,
            repairDecision.BridgeRecoveryGeneration,
            repairDecision.BridgeDecisionGeneration,
            publication.PublicationGeneration,
            publication.RouteCommitGeneration,
            string.IsNullOrWhiteSpace(request.CheckpointRequestId)
                ? "(none)"
                : request.CheckpointRequestId.Trim(),
            request.CanonicalRepairKey.Trim(),
            normalizedRangeFingerprint,
            request.FrontierChunkIndex,
            Math.Max(-1, request.DurableReceivedHighestChunkIndex),
            request.RangeCount,
            request.RequestedChunkCount,
            request.SourceKind,
            request.ProtocolRepairRequestId.Trim(),
            request.RequestCorrelationId.Trim(),
            string.IsNullOrWhiteSpace(request.Priority) ? "(none)" : request.Priority.Trim(),
            string.IsNullOrWhiteSpace(request.RecoveryMode) ? "(none)" : request.RecoveryMode.Trim(),
            repairDecision.RepairGeneration,
            string.IsNullOrWhiteSpace(request.DecisionReason)
                ? repairDecision.Reason
                : request.DecisionReason.Trim(),
            publication.TargetTransport,
            repairDecision.AuthorityId,
            repairDecision.PredecessorAuthorityId is { IsDefault: false }
                ? repairDecision.PredecessorAuthorityId
                : null);
        return true;
    }

    internal static bool IsRepairReplayDecisionCurrent(
        FileTransferCoordinatorState state,
        RepairReplayDecision repairDecision,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (state.IsTerminal)
        {
            rejectionReason = "terminal_state";
            return false;
        }

        if (!repairDecision.Accepted ||
            repairDecision.IsDefault ||
            repairDecision.AuthorityId.IsDefault)
        {
            rejectionReason = "repair_replay_decision_missing";
            return false;
        }

        if (state.CurrentRepairReplayDecision is not { } currentRepair ||
            currentRepair != repairDecision)
        {
            rejectionReason = "repair_replay_decision_superseded";
            return false;
        }

        if (!IsRuntimeOwnerCurrent(state, repairDecision.RuntimeOwner, out rejectionReason))
        {
            return false;
        }

        var leg = state.CurrentLeg;
        if (leg is null ||
            repairDecision.CurrentRoute != state.RouteSelection.Route ||
            repairDecision.CurrentProtocolVersion != state.RouteSelection.ProtocolVersion ||
            repairDecision.CurrentLiveRouteEpoch != (state.CurrentLiveRouteEpoch?.EpochId ?? 0) ||
            repairDecision.CurrentRuntimeGeneration != (state.CurrentRuntimeOwner?.RuntimeGeneration ?? 0) ||
            repairDecision.CurrentTerminalGeneration != state.TerminalGeneration ||
            repairDecision.CurrentLegGeneration != leg.Generation ||
            repairDecision.TransportEpoch != leg.TransportEpochId ||
            repairDecision.BridgeRecoveryGeneration != leg.BridgeRecoveryGeneration)
        {
            rejectionReason = "repair_replay_decision_stale";
            return false;
        }

        if (repairDecision.RoutePublicationToken is not { } publication ||
            !IsRoutePublicationTokenCurrent(state, publication, out rejectionReason))
        {
            rejectionReason = string.IsNullOrWhiteSpace(rejectionReason)
                ? "route_publication_authority_stale"
                : rejectionReason;
            return false;
        }

        return true;
    }

    internal static RuntimeUnlockRetryDispatchDecision AuthorizeRuntimeUnlockRetryDispatch(
        FileTransferCoordinatorState state,
        RuntimeUnlockRetryDispatchAuthorityRequest request,
        RuntimeUnlockRetryDispatchContract? currentContract,
        RuntimeUnlockRetryReleaseKind releaseKind,
        FileTransferAuthorityId? releaseAuthorityId,
        int releaseGeneration,
        string releaseOutcome)
    {
        RuntimeUnlockRetryDispatchDecision Decision(
            RuntimeUnlockRetryDispatchDecisionStatus status,
            string reason,
            RuntimeUnlockRetryDispatchContract? contract = null,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason, contract)
            {
                Request = request,
                State = nextState ?? state,
            };

        if (state.IsTerminal)
        {
            return Decision(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, "terminal_state");
        }

        if (string.IsNullOrWhiteSpace(request.SessionId) ||
            string.IsNullOrWhiteSpace(request.TransferId) ||
            request.SourcePublicationAuthority is null ||
            request.RecoveryContractGeneration <= 0)
        {
            return Decision(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, "retry_dispatch_request_missing");
        }

        if (request.MissingDispatchSuccessorGeneration < 0)
        {
            return Decision(
                RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                "retry_dispatch_missing_successor_generation_invalid");
        }

        if (request.RetiredOfferGeneration < 0)
        {
            return Decision(
                RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                "retry_dispatch_retired_offer_generation_invalid");
        }

        if (!string.Equals(request.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(request.TransferId, state.TransferId, StringComparison.Ordinal) ||
            request.Direction != state.Direction)
        {
            return Decision(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, "retry_dispatch_identity_mismatch");
        }

        if (state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg ||
            state.CurrentRoutePublicationToken is not { } publication)
        {
            return Decision(
                RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                state.CurrentRuntimeOwner is null
                    ? "runtime_owner_missing"
                    : "route_publication_authority_missing");
        }

        if (!IsRuntimeOwnerCurrent(state, owner, out var ownerRejectionReason))
        {
            return Decision(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, ownerRejectionReason);
        }

        if (!IsRoutePublicationTokenCurrent(state, publication, out var publicationRejectionReason))
        {
            return Decision(RuntimeUnlockRetryDispatchDecisionStatus.Rejected, publicationRejectionReason);
        }

        if (!MatchesRuntimeUnlockRetrySourcePublication(request.SourcePublicationAuthority, publication))
        {
            return Decision(
                RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                "retry_dispatch_source_publication_mismatch");
        }

        var sourceRouteAccepted =
            (state.RouteSelection.Route == FileTransferRoute.RegularNknV4Fast &&
             state.RouteSelection.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
             publication.TargetTransport == FileTransferTransportKind.RegularNkn) ||
            (state.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
             state.RouteSelection.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
             publication.TargetTransport == FileTransferTransportKind.RegularNkn);
        if (!sourceRouteAccepted ||
            publication.TargetRoute != state.RouteSelection.Route ||
            publication.TargetProtocolVersion != state.RouteSelection.ProtocolVersion)
        {
            return Decision(
                RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                "runtime_unlock_retry_source_route_invariant_mismatch");
        }

        var predecessorAuthorityId = request.PredecessorAuthorityId is { IsDefault: false }
            ? request.PredecessorAuthorityId
            : null;
        if (currentContract is null &&
            state.CurrentRuntimeUnlockRetryDispatchAuthority is null)
        {
            if (predecessorAuthorityId is not null)
            {
                return Decision(
                    RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                    "retry_dispatch_predecessor_missing");
            }

            if (request.DispatchAttemptGeneration != 1)
            {
                return Decision(
                    RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                    "retry_dispatch_initial_attempt_generation_mismatch");
            }

            if (request.MissingDispatchSuccessorGeneration != 0)
            {
                return Decision(
                    RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                    "retry_dispatch_initial_missing_successor_generation_mismatch");
            }

            if (request.RequestActivePredecessorSupersession)
            {
                return Decision(
                    RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                    "retry_dispatch_initial_supersession_invalid");
            }
        }

        if (currentContract is { } existing &&
            existing.Authority.RecoveryContractGeneration == request.RecoveryContractGeneration &&
            existing.Authority.RetiredOfferGeneration == request.RetiredOfferGeneration &&
            existing.Authority.DispatchAttemptGeneration == request.DispatchAttemptGeneration)
        {
            if (!RuntimeUnlockRetryDispatchAuthorityMatchesStableRequest(
                    existing.Authority,
                    request,
                    out _) ||
                existing.RegularV4LivenessGeneration != request.RegularV4LivenessGeneration ||
                existing.RequestBridgeDecisionGeneration != request.BridgeDecisionGeneration ||
                existing.RequestActivePredecessorSupersession !=
                    request.RequestActivePredecessorSupersession)
            {
                return Decision(
                    RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                    "retry_dispatch_attempt_identity_mismatch");
            }

            return existing.Lifecycle.IsSettled
                ? Decision(
                    RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                    "retry_dispatch_attempt_already_settled")
                : Decision(RuntimeUnlockRetryDispatchDecisionStatus.Granted, "idempotent", existing);
        }

        var authorizedReleaseKind = releaseKind;
        var authorizedReleaseAuthorityId = releaseAuthorityId;
        var authorizedReleaseGeneration = releaseGeneration;
        var authorizedReleaseOutcome = releaseOutcome;
        if (currentContract is { } predecessor)
        {
            if (predecessorAuthorityId != predecessor.Authority.AuthorityId ||
                state.CurrentRuntimeUnlockRetryDispatchAuthority is not { } coordinatorPredecessor ||
                coordinatorPredecessor != predecessor.Authority)
            {
                return Decision(
                    RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                    "retry_dispatch_predecessor_mismatch");
            }

            authorizedReleaseKind = predecessor.Authority.ReleaseKind;
            authorizedReleaseAuthorityId = predecessor.Authority.ReleaseAuthorityId;
            authorizedReleaseGeneration = predecessor.Authority.ReleaseGeneration;
            authorizedReleaseOutcome = predecessor.Authority.ReleaseOutcome;

            if (request.RegularV4LivenessGeneration != predecessor.RegularV4LivenessGeneration ||
                request.BridgeDecisionGeneration != predecessor.RequestBridgeDecisionGeneration)
            {
                return Decision(
                    RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                    "retry_dispatch_recovery_handoff_tuple_mismatch");
            }

            if (!predecessor.Lifecycle.IsSettled)
            {
                if (!request.RequestActivePredecessorSupersession)
                {
                    return Decision(
                        RuntimeUnlockRetryDispatchDecisionStatus.Held,
                        "retry_dispatch_predecessor_active");
                }

                if (request.RecoveryContractGeneration !=
                        predecessor.Authority.RecoveryContractGeneration ||
                    request.DispatchAttemptGeneration !=
                        predecessor.Authority.DispatchAttemptGeneration + 1)
                {
                    return Decision(
                        RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                        "retry_dispatch_successor_attempt_mismatch");
                }

                if (request.MissingDispatchSuccessorGeneration !=
                    predecessor.Authority.MissingDispatchSuccessorGeneration)
                {
                    return Decision(
                        RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                        "retry_dispatch_active_successor_generation_mismatch");
                }

                if (predecessor.AttemptPhase == RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight)
                {
                    return Decision(
                        RuntimeUnlockRetryDispatchDecisionStatus.Held,
                        "retry_dispatch_predecessor_send_in_flight");
                }

            }
            else
            {
                if (predecessor.Lifecycle.SettlementOutcome !=
                    RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch)
                {
                    return Decision(
                        RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                        "retry_dispatch_predecessor_settlement_invalid");
                }

                if (request.RequestActivePredecessorSupersession)
                {
                    return Decision(
                        RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                        "retry_dispatch_missing_successor_supersession_invalid");
                }

                if (!predecessor.TryCreateSettlementReport(out var predecessorSettlement) ||
                    state.LastRuntimeUnlockRetryDispatchSettlement is not { } acceptedPredecessorSettlement ||
                    acceptedPredecessorSettlement != predecessorSettlement)
                {
                    return Decision(
                        RuntimeUnlockRetryDispatchDecisionStatus.Held,
                        "retry_dispatch_predecessor_settlement_unobserved");
                }

                if (request.RecoveryContractGeneration !=
                        predecessor.Authority.RecoveryContractGeneration ||
                    request.DispatchAttemptGeneration !=
                        predecessor.Authority.DispatchAttemptGeneration + 1)
                {
                    return Decision(
                        RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                        "retry_dispatch_successor_attempt_mismatch");
                }

                if (request.MissingDispatchSuccessorGeneration !=
                    predecessor.Authority.MissingDispatchSuccessorGeneration + 1)
                {
                    return Decision(
                        RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                        "retry_dispatch_missing_successor_generation_mismatch");
                }

                var predecessorRetiredOfferGeneration = predecessor.OfferGeneration > 0
                    ? predecessor.OfferGeneration
                    : predecessor.Authority.RetiredOfferGeneration;
                if (request.RetiredOfferGeneration != predecessorRetiredOfferGeneration)
                {
                    return Decision(
                        RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                        "retry_dispatch_successor_retired_offer_mismatch");
                }

                if (request.MissingDispatchSuccessorGeneration >
                    RuntimeUnlockRetryMissingDispatchSuccessorLimit)
                {
                    return Decision(
                        RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                        "retry_dispatch_missing_successor_limit_exhausted",
                        nextState: state with
                        {
                            CurrentRuntimeUnlockRetryDispatchAuthority = null,
                        });
                }
            }
        }
        else if (predecessorAuthorityId is not null ||
                 state.CurrentRuntimeUnlockRetryDispatchAuthority is not null)
        {
            return Decision(
                RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                "retry_dispatch_predecessor_missing");
        }

        var releaseValid = currentContract is not null
            ? TryValidateInheritedRuntimeUnlockRetryRelease(
                state,
                currentContract,
                authorizedReleaseKind,
                authorizedReleaseAuthorityId,
                authorizedReleaseGeneration,
                authorizedReleaseOutcome,
                out var releaseRejectionReason)
            : TryValidateRuntimeUnlockRetryRelease(
                state,
                request,
                authorizedReleaseKind,
                authorizedReleaseAuthorityId,
                authorizedReleaseGeneration,
                authorizedReleaseOutcome,
                out releaseRejectionReason);
        if (!releaseValid)
        {
            var held = releaseRejectionReason is
                "regular_v4_repair_settlement_pending" or
                "regular_v4_recovery_settlement_consumption_pending" or
                "receive_recovery_lease_pending" or
                "fallback_paired_tail_yield_pending" or
                "fallback_owner_work_pending";
            return Decision(
                held
                    ? RuntimeUnlockRetryDispatchDecisionStatus.Held
                    : RuntimeUnlockRetryDispatchDecisionStatus.Rejected,
                releaseRejectionReason);
        }

        var authorityGeneration = Math.Max(
            1,
            state.LastRuntimeUnlockRetryDispatchAuthorityGeneration + 1);
        var authority = new RuntimeUnlockRetryDispatchAuthority(
            state.SessionId,
            state.TransferId,
            state.Direction,
            owner,
            publication,
            state.RouteSelection.Route,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? 0,
            owner.RuntimeGeneration,
            state.TerminalGeneration,
            leg.Generation,
            leg.TransportEpochId,
            leg.BridgeRecoveryGeneration,
            state.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration ?? request.BridgeDecisionGeneration,
            publication.PublicationGeneration,
            publication.RouteCommitGeneration,
            request.RecoveryContractGeneration,
            request.RetiredOfferGeneration,
            request.DispatchAttemptGeneration,
            request.MissingDispatchSuccessorGeneration,
            NormalizeReason(request.RetryReason),
            NormalizeReason(request.RecoveryReason),
            authorizedReleaseKind,
            authorizedReleaseAuthorityId is { IsDefault: false } ? authorizedReleaseAuthorityId : null,
            Math.Max(0, authorizedReleaseGeneration),
            NormalizeReason(authorizedReleaseOutcome),
            FileTransferRoute.FileTunaV4,
            FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportKind.Tuna,
            authorityGeneration,
            IssueAuthorityId("runtime_unlock_retry_dispatch", state, authorityGeneration),
            predecessorAuthorityId);
        var contract = new RuntimeUnlockRetryDispatchContract(
            authority,
            request.RequestActivePredecessorSupersession,
            request.RegularV4LivenessGeneration,
            request.BridgeDecisionGeneration);

        var acceptedState = state with
        {
            CurrentRuntimeUnlockRetryDispatchAuthority = authority,
            LastRuntimeUnlockRetryDispatchAuthorityGeneration = authorityGeneration,
            LastRuntimeUnlockRetryDispatchSettlement =
                state.LastRuntimeUnlockRetryDispatchSettlement,
            RuntimeUnlockRegularV4RecoverySettlementAvailable =
                currentContract is null &&
                authorizedReleaseKind == RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement
                    ? false
                    : state.RuntimeUnlockRegularV4RecoverySettlementAvailable,
            RuntimeUnlockRecoveryLeaseClosureAvailable =
                currentContract is null &&
                authorizedReleaseKind == RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure
                    ? false
                    : state.RuntimeUnlockRecoveryLeaseClosureAvailable,
        };
        return Decision(
            RuntimeUnlockRetryDispatchDecisionStatus.Granted,
            "accepted",
            contract,
            acceptedState);
    }

    internal static RuntimeUnlockRetryDispatchSettlementDecision ObserveRuntimeUnlockRetryDispatchSettlement(
        FileTransferCoordinatorState state,
        RuntimeUnlockRetryDispatchContract? currentContract,
        RuntimeUnlockRetryDispatchSettlementReport? lastSettlement,
        RuntimeUnlockRetryDispatchSettlementReport report)
    {
        RuntimeUnlockRetryDispatchSettlementDecision Decision(
            RuntimeUnlockRetryDispatchSettlementDecisionStatus status,
            string reason,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason, report)
            {
                State = nextState ?? state,
            };

        if (report.IsDefault)
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_report_missing");
        }

        var authority = report.Authority;
        if (!IsCompleteRuntimeUnlockRetryDispatchSettlementAuthority(authority))
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_authority_incomplete");
        }

        if (!string.Equals(authority.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(authority.TransferId, state.TransferId, StringComparison.Ordinal) ||
            authority.Direction != state.Direction)
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_identity_mismatch");
        }

        var normalizedReason = NormalizeReason(report.Reason);
        if (!string.Equals(report.Reason, normalizedReason, StringComparison.Ordinal))
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_reason_not_normalized");
        }

        if (report.Outcome != RuntimeUnlockRetryDispatchSettlementOutcome.Superseded &&
            ((lastSettlement is { } contextSettlement && contextSettlement == report) ||
             (state.LastRuntimeUnlockRetryDispatchSettlement is { } coordinatorSettlement &&
              coordinatorSettlement == report)))
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent,
                "retry_dispatch_settlement_duplicate");
        }

        if (report.Outcome == RuntimeUnlockRetryDispatchSettlementOutcome.Superseded &&
            currentContract is { } successorContract &&
            state.CurrentRuntimeUnlockRetryDispatchAuthority is { } currentSuccessor &&
            successorContract.Authority == currentSuccessor &&
            currentSuccessor.PredecessorAuthorityId == authority.AuthorityId &&
            IsRuntimeUnlockRetryDispatchSupersededReason(report.Reason))
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Idempotent,
                "retry_dispatch_settlement_superseded_predecessor",
                state with
                {
                    LastRuntimeUnlockRetryDispatchSettlement = report,
                });
        }

        if (currentContract is null)
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_contract_missing");
        }

        if (currentContract.Authority != authority)
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_contract_mismatch");
        }

        var currentAuthorityMatches =
            state.CurrentRuntimeUnlockRetryDispatchAuthority is { } currentAuthority &&
            currentAuthority == authority;
        var coordinatorAuthorityRetired =
            state.CurrentRuntimeUnlockRetryDispatchAuthority is null &&
            report.Outcome is RuntimeUnlockRetryDispatchSettlementOutcome.AuthorityRejected or
                RuntimeUnlockRetryDispatchSettlementOutcome.StaleRejected or
                RuntimeUnlockRetryDispatchSettlementOutcome.TerminalRejected or
                RuntimeUnlockRetryDispatchSettlementOutcome.TransferTerminal;
        if (!currentAuthorityMatches && !coordinatorAuthorityRetired)
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_authority_mismatch");
        }

        if (!currentContract.TryCreateSettlementReport(out var lifecycleReport))
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_lifecycle_unsettled");
        }

        if (lifecycleReport != report)
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_lifecycle_mismatch");
        }

        if (report.Outcome == RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved &&
            !currentContract.Lifecycle.DispatchObserved)
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_dispatch_proof_missing");
        }

        if (report.Outcome == RuntimeUnlockRetryDispatchSettlementOutcome.Superseded)
        {
            return Decision(
                RuntimeUnlockRetryDispatchSettlementDecisionStatus.Rejected,
                "retry_dispatch_settlement_successor_missing");
        }

        var retainAuthority =
            report.Outcome == RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch;
        var acceptedState = state with
        {
            CurrentRuntimeUnlockRetryDispatchAuthority = retainAuthority ? authority : null,
            LastRuntimeUnlockRetryDispatchSettlement = report,
        };
        return Decision(
            RuntimeUnlockRetryDispatchSettlementDecisionStatus.Accepted,
            "retry_dispatch_settlement_accepted",
            acceptedState);
    }

    internal static RuntimeUnlockRegularV4RecoverySettlementDecision ObserveRuntimeUnlockRegularV4RecoverySettlement(
        FileTransferCoordinatorState state,
        RuntimeUnlockRegularV4RecoverySettlementReport? lastSettlement,
        RuntimeUnlockRegularV4RecoverySettlementReport report)
    {
        RuntimeUnlockRegularV4RecoverySettlementDecision Decision(
            RuntimeUnlockRegularV4RecoverySettlementDecisionStatus status,
            string reason,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason, report)
            {
                State = nextState ?? state,
            };

        if (report.IsDefault)
        {
            return Decision(
                RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
                "regular_v4_recovery_settlement_report_missing");
        }

        var settlement = report.Settlement;
        if (!string.Equals(settlement.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(settlement.TransferId, state.TransferId, StringComparison.Ordinal) ||
            settlement.Direction != state.Direction)
        {
            return Decision(
                RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
                "regular_v4_recovery_settlement_identity_mismatch");
        }

        if (state.IsTerminal)
        {
            return Decision(
                RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
                "terminal_state");
        }

        if (state.RouteSelection.Route != FileTransferRoute.RegularNknV4Fast ||
            state.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV4)
        {
            return Decision(
                RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
                "regular_v4_recovery_settlement_route_mismatch");
        }

        if (state.CurrentRepairReplayDecision is not { } repairDecision ||
            !IsRepairReplayDecisionCurrent(state, repairDecision, out _) ||
            !RuntimeUnlockRegularV4RecoverySettlementMatchesCurrentRepair(
                state,
                repairDecision,
                settlement))
        {
            return Decision(
                RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
                "regular_v4_recovery_settlement_authority_stale");
        }

        if (state.LastCoordinatorIssuedRegularV4RecoverySettlement is not { } issuedSettlement ||
            !ReferenceEquals(issuedSettlement, settlement) ||
            issuedSettlement != settlement)
        {
            return Decision(
                RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
                "regular_v4_recovery_settlement_not_coordinator_issued");
        }

        var normalizedReason = NormalizeReason(report.Reason);
        if (!string.Equals(report.Reason, normalizedReason, StringComparison.Ordinal))
        {
            return Decision(
                RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
                "regular_v4_recovery_settlement_reason_not_normalized");
        }

        if (!TryValidateRuntimeUnlockRegularV4RecoverySettlementTuple(
                state,
                report,
                out var tupleRejectionReason))
        {
            return Decision(
                RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
                tupleRejectionReason);
        }

        if (report.Disposition != RuntimeUnlockRegularV4RecoverySettlementDisposition.Consumed)
        {
            return Decision(
                RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
                "regular_v4_recovery_settlement_disposition_diagnostic_only");
        }

        if (settlement.Outcome is not (
                FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved or
                FileTransferRegularV4FrontierRepairSettlementOutcome.MissingDispatch or
                FileTransferRegularV4FrontierRepairSettlementOutcome.SettledStalled))
        {
            return Decision(
                RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
                "regular_v4_recovery_settlement_outcome_rejected");
        }

        var retainedSettlement = state.LastRuntimeUnlockRegularV4RecoverySettlement ?? lastSettlement;
        if (retainedSettlement is { } priorSettlement)
        {
            return priorSettlement == report
                ? Decision(
                    RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Idempotent,
                    "regular_v4_recovery_settlement_duplicate")
                : Decision(
                    RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Rejected,
                    "regular_v4_recovery_settlement_final_disposition_conflict");
        }

        var acceptedState = state with
        {
            LastRuntimeUnlockRegularV4RecoverySettlement = report,
            RuntimeUnlockRegularV4RecoverySettlementAvailable = true,
        };
        return Decision(
            RuntimeUnlockRegularV4RecoverySettlementDecisionStatus.Accepted,
            "regular_v4_recovery_settlement_accepted",
            acceptedState);
    }

    internal static RuntimeUnlockRegularV4RecoveryLifecycleDecision ObserveRuntimeUnlockRegularV4RecoveryLifecycle(
        FileTransferCoordinatorState state,
        RuntimeUnlockRegularV4RecoveryLifecycleReport? lastLifecycle,
        RuntimeUnlockRegularV4RecoveryLifecycleReport report)
    {
        RuntimeUnlockRegularV4RecoveryLifecycleDecision Decision(
            RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus status,
            string reason,
            FileTransferCoordinatorState? nextState = null)
            => new(status, reason, report)
            {
                State = nextState ?? state,
            };

        if (report.IsDefault)
        {
            return Decision(
                RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Rejected,
                "regular_v4_recovery_lifecycle_report_missing");
        }

        var authority = report.Authority;
        if (!string.Equals(authority.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(authority.TransferId, state.TransferId, StringComparison.Ordinal) ||
            authority.Direction != state.Direction)
        {
            return Decision(
                RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Rejected,
                "regular_v4_recovery_lifecycle_identity_mismatch");
        }

        var retained = state.LastRuntimeUnlockRegularV4RecoveryLifecycle ?? lastLifecycle;
        if (retained is { } prior)
        {
            return prior == report
                ? Decision(
                    RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Idempotent,
                    "regular_v4_recovery_lifecycle_duplicate")
                : Decision(
                    RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Rejected,
                    "regular_v4_recovery_lifecycle_final_disposition_conflict");
        }

        if (state.IsTerminal)
        {
            return Decision(
                RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Rejected,
                "terminal_state");
        }

        if (!string.Equals(report.Reason, NormalizeReason(report.Reason), StringComparison.Ordinal))
        {
            return Decision(
                RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Rejected,
                "regular_v4_recovery_lifecycle_reason_not_normalized");
        }

        if (state.CurrentRecoveryLivenessLease is null)
        {
            return Decision(
                RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Rejected,
                "regular_v4_recovery_lifecycle_lease_missing");
        }

        if (!RuntimeUnlockRegularV4RecoveryAuthorityMatchesCurrent(
                state,
                report.RecoveryAuthority))
        {
            return Decision(
                RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Rejected,
                "regular_v4_recovery_lifecycle_authority_stale");
        }

        var retiredLease = report.RecoveryAuthority.RecoveryLivenessLease with
        {
            State = RecoveryLivenessLeaseState.Retired,
            Reason = NormalizeReason(report.Reason),
        };

        var acceptedState = state with
        {
            CurrentRecoveryLivenessLease = null,
            LastRetiredRecoveryLivenessLease = retiredLease,
            LastRecoveryLivenessLeaseGeneration = Math.Max(
                state.LastRecoveryLivenessLeaseGeneration,
                retiredLease.LeaseGeneration),
            LastRuntimeUnlockRegularV4RecoveryLifecycle = report,
            RuntimeUnlockRecoveryLeaseClosureAvailable =
                report.Outcome is (
                    RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Completed or
                    RuntimeUnlockRegularV4RecoveryLifecycleOutcome.ReceiveProofObserved),
        };
        return Decision(
            RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Accepted,
            "regular_v4_recovery_lifecycle_accepted",
            acceptedState);
    }

    private static bool RuntimeUnlockRegularV4RecoveryAuthorityMatchesCurrent(
        FileTransferCoordinatorState state,
        RuntimeUnlockRegularV4RecoveryAuthority recoveryAuthority)
    {
        if (recoveryAuthority.IsDefault ||
            state.IsTerminal ||
            state.RouteSelection.Route != FileTransferRoute.RegularNknV4Fast ||
            state.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            state.CurrentRuntimeOwner is not { } owner ||
            state.CurrentLeg is not { } leg ||
            state.CurrentBridgeRecoveryDecision is not { Accepted: true } bridgeDecision ||
            state.CurrentRecoveryLivenessLease is not { } currentLease)
        {
            return false;
        }

        var bridgeAuthority = recoveryAuthority.BridgeRecoveryAuthority;
        var lease = recoveryAuthority.RecoveryLivenessLease;
        if (!string.Equals(bridgeAuthority.Reason, NormalizeReason(bridgeAuthority.Reason), StringComparison.Ordinal) ||
            !string.Equals(lease.Reason, NormalizeReason(lease.Reason), StringComparison.Ordinal) ||
            lease.State != RecoveryLivenessLeaseState.Active ||
            lease != currentLease)
        {
            return false;
        }

        return string.Equals(bridgeAuthority.SessionId, state.SessionId, StringComparison.Ordinal) &&
               string.Equals(bridgeAuthority.TransferId, state.TransferId, StringComparison.Ordinal) &&
               bridgeAuthority.Direction == state.Direction &&
               string.Equals(bridgeAuthority.RouteToken, state.RouteSelection.TelemetryToken, StringComparison.Ordinal) &&
               bridgeAuthority.ProtocolVersion == state.RouteSelection.ProtocolVersion &&
               bridgeAuthority.LiveRouteEpoch == (state.CurrentLiveRouteEpoch?.EpochId ?? 0) &&
               bridgeAuthority.RuntimeGeneration == owner.RuntimeGeneration &&
               bridgeAuthority.TerminalGeneration == state.TerminalGeneration &&
               bridgeAuthority.TransferLegGeneration == leg.Generation &&
               bridgeAuthority.TransportEpoch == leg.TransportEpochId &&
               bridgeAuthority.BridgeRecoveryGeneration == leg.BridgeRecoveryGeneration &&
               bridgeAuthority.BridgeDecisionGeneration == bridgeDecision.BridgeDecisionGeneration &&
               bridgeAuthority.ProtocolVersion == bridgeDecision.CurrentProtocolVersion &&
               bridgeAuthority.LiveRouteEpoch == bridgeDecision.CurrentLiveRouteEpoch &&
               bridgeAuthority.RuntimeGeneration == bridgeDecision.CurrentRuntimeGeneration &&
               bridgeAuthority.TerminalGeneration == bridgeDecision.CurrentTerminalGeneration &&
               bridgeAuthority.TransferLegGeneration == bridgeDecision.CurrentLegGeneration &&
               bridgeAuthority.TransportEpoch == bridgeDecision.TransportEpoch &&
               bridgeAuthority.BridgeRecoveryGeneration == bridgeDecision.BridgeRecoveryGeneration &&
               string.Equals(bridgeAuthority.RecoveryKind, bridgeDecision.RecoveryKind.ToString(), StringComparison.Ordinal) &&
               string.Equals(bridgeAuthority.Reason, bridgeDecision.Reason, StringComparison.Ordinal) &&
               string.Equals(lease.SessionId, bridgeAuthority.SessionId, StringComparison.Ordinal) &&
               string.Equals(lease.TransferId, bridgeAuthority.TransferId, StringComparison.Ordinal) &&
               lease.Direction == bridgeAuthority.Direction &&
               lease.RuntimeOwner == owner &&
               lease.RuntimeOwner == bridgeDecision.RuntimeOwner &&
               lease.Route == bridgeDecision.CurrentRoute &&
               string.Equals(lease.RouteToken, bridgeAuthority.RouteToken, StringComparison.Ordinal) &&
               lease.ProtocolVersion == bridgeAuthority.ProtocolVersion &&
               lease.LiveRouteEpoch == bridgeAuthority.LiveRouteEpoch &&
               lease.RuntimeGeneration == bridgeAuthority.RuntimeGeneration &&
               lease.TerminalGeneration == bridgeAuthority.TerminalGeneration &&
               lease.LegGeneration == bridgeAuthority.TransferLegGeneration &&
               lease.TransportEpoch == bridgeAuthority.TransportEpoch &&
               lease.BridgeRecoveryGeneration == bridgeAuthority.BridgeRecoveryGeneration;
    }

    internal static bool IsRuntimeUnlockRegularV4RecoveryAuthorityCurrent(
        FileTransferCoordinatorState state,
        RuntimeUnlockRegularV4RecoveryAuthority recoveryAuthority)
        => RuntimeUnlockRegularV4RecoveryAuthorityMatchesCurrent(state, recoveryAuthority);

    internal static bool IsRuntimeUnlockRegularV4RecoveryAuthorityCurrentOrAcceptedClosure(
        FileTransferCoordinatorState state,
        RuntimeUnlockRegularV4RecoveryAuthority recoveryAuthority)
    {
        if (RuntimeUnlockRegularV4RecoveryAuthorityMatchesCurrent(state, recoveryAuthority))
        {
            return true;
        }

        if (state.IsTerminal ||
            !state.RuntimeUnlockRecoveryLeaseClosureAvailable ||
            state.LastRuntimeUnlockRegularV4RecoveryLifecycle is not { } lifecycle ||
            lifecycle.RecoveryAuthority != recoveryAuthority ||
            lifecycle.Outcome is not (
                RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Completed or
                RuntimeUnlockRegularV4RecoveryLifecycleOutcome.ReceiveProofObserved) ||
            state.LastRetiredRecoveryLivenessLease is not { } retiredLease ||
            retiredLease != (recoveryAuthority.RecoveryLivenessLease with
            {
                State = RecoveryLivenessLeaseState.Retired,
                Reason = NormalizeReason(lifecycle.Reason),
            }))
        {
            return false;
        }

        return RuntimeUnlockRegularV4RecoveryAuthorityMatchesCurrent(
            state with { CurrentRecoveryLivenessLease = recoveryAuthority.RecoveryLivenessLease },
            recoveryAuthority);
    }

    private static bool TryValidateRuntimeUnlockRegularV4RecoverySettlementTuple(
        FileTransferCoordinatorState state,
        RuntimeUnlockRegularV4RecoverySettlementReport report,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        var livenessGeneration = report.RegularV4LivenessGeneration;
        var bridgeDecisionGeneration = report.BridgeDecisionGeneration;
        if ((livenessGeneration == 0) != (bridgeDecisionGeneration == 0))
        {
            rejectionReason = "regular_v4_recovery_settlement_generation_tuple_incomplete";
            return false;
        }

        if (livenessGeneration == 0)
        {
            if (state.CurrentRecoveryLivenessLease is not null)
            {
                rejectionReason = "regular_v4_recovery_settlement_zero_tuple_recovery_active";
                return false;
            }

            return true;
        }

        var lifecycle = state.LastRuntimeUnlockRegularV4RecoveryLifecycle;
        if (lifecycle is not { } acceptedLifecycle ||
            acceptedLifecycle.RegularV4LivenessGeneration != livenessGeneration ||
            acceptedLifecycle.Authority.BridgeDecisionGeneration != bridgeDecisionGeneration ||
            state.CurrentRecoveryLivenessLease is not null ||
            (state.LastRetiredRecoveryLivenessLease is { } recoveryLease &&
             !RuntimeUnlockRegularV4RecoveryLeaseMatchesCurrentRuntime(state, recoveryLease)))
        {
            rejectionReason = "regular_v4_recovery_settlement_liveness_generation_stale";
            return false;
        }

        if (state.CurrentBridgeRecoveryDecision is { } bridgeDecision &&
            bridgeDecision.BridgeDecisionGeneration != bridgeDecisionGeneration)
        {
            rejectionReason = "regular_v4_recovery_settlement_bridge_generation_stale";
            return false;
        }

        return true;
    }

    private static bool RuntimeUnlockRegularV4RecoveryLeaseMatchesCurrentRuntime(
        FileTransferCoordinatorState state,
        RecoveryLivenessLease lease)
    {
        var leg = state.CurrentLeg;
        return !lease.IsDefault &&
               !state.IsTerminal &&
               string.Equals(lease.SessionId, state.SessionId, StringComparison.Ordinal) &&
               string.Equals(lease.TransferId, state.TransferId, StringComparison.Ordinal) &&
               lease.Direction == state.Direction &&
               lease.RuntimeOwner == state.CurrentRuntimeOwner &&
               lease.Route == state.RouteSelection.Route &&
               string.Equals(lease.RouteToken, state.RouteSelection.TelemetryToken, StringComparison.Ordinal) &&
               lease.ProtocolVersion == state.RouteSelection.ProtocolVersion &&
               lease.LiveRouteEpoch == (state.CurrentLiveRouteEpoch?.EpochId ?? 0) &&
               lease.RuntimeGeneration == (state.CurrentRuntimeOwner?.RuntimeGeneration ?? 0) &&
               lease.TerminalGeneration == state.TerminalGeneration &&
               leg is not null &&
               lease.LegGeneration == leg.Generation &&
               lease.TransportEpoch == leg.TransportEpochId &&
               lease.BridgeRecoveryGeneration == leg.BridgeRecoveryGeneration &&
               lease.State is RecoveryLivenessLeaseState.Active or RecoveryLivenessLeaseState.Retired;
    }

    internal static bool TryValidateRuntimeUnlockRetryDispatchGrantedDecision(
        RuntimeUnlockRetryDispatchDecision decision,
        RuntimeUnlockRetryDispatchAuthorityRequest expectedRequest,
        out string mismatchField)
    {
        mismatchField = "none";
        if (!decision.Granted || decision.Contract is not { } contract)
        {
            return RuntimeUnlockRetryDispatchMismatch("decision_status", out mismatchField);
        }

        if (decision.Request.IsDefault || decision.Request != expectedRequest)
        {
            return RuntimeUnlockRetryDispatchMismatch("decision_request", out mismatchField);
        }

        if (contract.RequestActivePredecessorSupersession !=
            expectedRequest.RequestActivePredecessorSupersession)
        {
            return RuntimeUnlockRetryDispatchMismatch(
                "contract_request_active_predecessor_supersession",
                out mismatchField);
        }

        if (contract.RegularV4LivenessGeneration != expectedRequest.RegularV4LivenessGeneration ||
            contract.RequestBridgeDecisionGeneration != expectedRequest.BridgeDecisionGeneration)
        {
            return RuntimeUnlockRetryDispatchMismatch(
                "contract_recovery_handoff_tuple",
                out mismatchField);
        }

        var state = decision.State;
        if (state is null)
        {
            return RuntimeUnlockRetryDispatchMismatch("coordinator_state", out mismatchField);
        }

        var authority = contract.Authority;
        if (!TryValidateRuntimeUnlockRetryDispatchBinding(
                authority,
                expectedRequest,
                out mismatchField))
        {
            return false;
        }

        if (state.CurrentRuntimeOwner is not { } owner || authority.RuntimeOwner != owner)
        {
            return RuntimeUnlockRetryDispatchMismatch("runtime_owner", out mismatchField);
        }

        if (state.CurrentLeg is not { } leg ||
            authority.LegGeneration != leg.Generation ||
            authority.TransportEpoch != leg.TransportEpochId ||
            authority.BridgeRecoveryGeneration != leg.BridgeRecoveryGeneration)
        {
            return RuntimeUnlockRetryDispatchMismatch("transfer_leg", out mismatchField);
        }

        if (state.CurrentRoutePublicationToken is not { } publication ||
            authority.SourcePublicationAuthority != publication ||
            authority.RoutePublicationGeneration != publication.PublicationGeneration ||
            authority.RouteCommitGeneration != publication.RouteCommitGeneration ||
            publication.RuntimeOwner != owner ||
            !string.Equals(publication.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(publication.TransferId, state.TransferId, StringComparison.Ordinal) ||
            publication.Direction != state.Direction ||
            publication.Route != state.RouteSelection.Route ||
            !string.Equals(publication.RouteToken, state.RouteSelection.TelemetryToken, StringComparison.Ordinal) ||
            publication.ProtocolVersion != state.RouteSelection.ProtocolVersion ||
            publication.LiveRouteEpoch != (state.CurrentLiveRouteEpoch?.EpochId ?? 0) ||
            publication.RuntimeGeneration != owner.RuntimeGeneration ||
            publication.TerminalGeneration != state.TerminalGeneration ||
            publication.LegGeneration != leg.Generation ||
            publication.TargetRoute != state.RouteSelection.Route ||
            publication.TargetProtocolVersion != state.RouteSelection.ProtocolVersion ||
            publication.TargetTransport != FileTransferTransportKind.RegularNkn ||
            publication.ActionKind != FileTransferCoordinatorActionKind.RoutePublication)
        {
            return RuntimeUnlockRetryDispatchMismatch("route_publication", out mismatchField);
        }

        var expectedBridgeDecisionGeneration =
            state.CurrentBridgeRecoveryDecision?.BridgeDecisionGeneration ??
            expectedRequest.BridgeDecisionGeneration;
        if (authority.BridgeDecisionGeneration != expectedBridgeDecisionGeneration)
        {
            return RuntimeUnlockRetryDispatchMismatch("bridge_decision_generation", out mismatchField);
        }

        if (!string.Equals(authority.SessionId, state.SessionId, StringComparison.Ordinal) ||
            !string.Equals(authority.TransferId, state.TransferId, StringComparison.Ordinal) ||
            authority.Direction != state.Direction ||
            authority.Route != state.RouteSelection.Route ||
            !string.Equals(authority.RouteToken, state.RouteSelection.TelemetryToken, StringComparison.Ordinal) ||
            authority.ProtocolVersion != state.RouteSelection.ProtocolVersion ||
            authority.LiveRouteEpoch != (state.CurrentLiveRouteEpoch?.EpochId ?? 0) ||
            authority.RuntimeGeneration != owner.RuntimeGeneration ||
            authority.TerminalGeneration != state.TerminalGeneration)
        {
            return RuntimeUnlockRetryDispatchMismatch("coordinator_runtime_tuple", out mismatchField);
        }

        if (state.CurrentRuntimeUnlockRetryDispatchAuthority is not { } currentAuthority ||
            currentAuthority != authority)
        {
            return RuntimeUnlockRetryDispatchMismatch("coordinator_current_authority", out mismatchField);
        }

        if (authority.TargetRoute != FileTransferRoute.FileTunaV4 ||
            authority.TargetProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            authority.TargetTransport != FileTransferTransportKind.Tuna)
        {
            return RuntimeUnlockRetryDispatchMismatch("target_route", out mismatchField);
        }

        if (authority.ReleaseKind == RuntimeUnlockRetryReleaseKind.Unknown ||
            authority.ReleaseAuthorityId is not { IsDefault: false } ||
            authority.ReleaseGeneration <= 0 ||
            string.IsNullOrWhiteSpace(authority.ReleaseOutcome))
        {
            return RuntimeUnlockRetryDispatchMismatch("release_identity", out mismatchField);
        }

        if ((state.RouteSelection.Route == FileTransferRoute.RegularNknV4Fast &&
             authority.ReleaseKind is not (
                 RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement or
                 RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure)) ||
            (state.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
             authority.ReleaseKind != RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield))
        {
            return RuntimeUnlockRetryDispatchMismatch("release_kind", out mismatchField);
        }

        if (authority.ReleaseKind == RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement &&
            authority.ReleaseOutcome is not (
                nameof(FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved) or
                nameof(FileTransferRegularV4FrontierRepairSettlementOutcome.MissingDispatch) or
                nameof(FileTransferRegularV4FrontierRepairSettlementOutcome.SettledStalled)))
        {
            return RuntimeUnlockRetryDispatchMismatch("release_outcome", out mismatchField);
        }

        if (authority.ReleaseKind == RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure &&
            !string.Equals(
                authority.ReleaseOutcome,
                nameof(RecoveryLivenessLeaseState.Retired),
                StringComparison.Ordinal))
        {
            return RuntimeUnlockRetryDispatchMismatch("release_outcome", out mismatchField);
        }

        return true;
    }

    internal static bool TryIssueRuntimeUnlockRetryDispatchInstallReceipt(
        FileTransferCoordinatorState state,
        RuntimeUnlockRetryDispatchDecision stagedDecision,
        RuntimeUnlockRetryDispatchAuthorityRequest expectedRequest,
        RuntimeUnlockRetryDispatchContract? currentContract,
        RuntimeUnlockRetryDispatchContract? activePredecessorContract,
        RuntimeUnlockRetryDispatchSettlementReport? lastSettlement,
        RuntimeUnlockRetryDispatchInstallReceipt? currentReceipt,
        long lastInstallGeneration,
        out RuntimeUnlockRetryDispatchInstallReceipt receipt,
        out string mismatchField)
    {
        receipt = default;
        mismatchField = "none";
        if (state.IsTerminal)
        {
            return RuntimeUnlockRetryDispatchMismatch("terminal_state", out mismatchField);
        }

        if (lastInstallGeneration < 0 || lastInstallGeneration == long.MaxValue)
        {
            return RuntimeUnlockRetryDispatchMismatch("install_generation", out mismatchField);
        }

        if (!stagedDecision.Granted ||
            stagedDecision.Contract is not { } stagedContract ||
            currentContract is null ||
            !ReferenceEquals(stagedContract, currentContract) ||
            stagedContract.Authority != currentContract.Authority)
        {
            return RuntimeUnlockRetryDispatchMismatch("current_contract", out mismatchField);
        }

        if (currentContract.Lifecycle.IsSettled ||
            currentContract.AttemptPhase == RuntimeUnlockRetryDispatchAttemptPhase.Settled)
        {
            return RuntimeUnlockRetryDispatchMismatch("contract_lifecycle", out mismatchField);
        }

        var currentDecision = stagedDecision with
        {
            State = state,
        };
        if (!TryValidateRuntimeUnlockRetryDispatchGrantedDecision(
                currentDecision,
                expectedRequest,
                out mismatchField))
        {
            return false;
        }

        var authority = stagedContract.Authority;
        if (state.LastRuntimeUnlockRetryDispatchAuthorityGeneration != authority.AuthorityGeneration)
        {
            return RuntimeUnlockRetryDispatchMismatch(
                "authority_generation",
                out mismatchField);
        }

        if (stagedDecision.State is null ||
            stagedDecision.State.CurrentRuntimeUnlockRetryDispatchAuthority != authority)
        {
            return RuntimeUnlockRetryDispatchMismatch(
                "staged_coordinator_authority",
                out mismatchField);
        }

        if (currentReceipt is { } installedReceipt)
        {
            if (installedReceipt.IsDefault ||
                installedReceipt.Authority != authority ||
                installedReceipt.InstallGeneration != lastInstallGeneration)
            {
                return RuntimeUnlockRetryDispatchMismatch(
                    "current_install_receipt",
                    out mismatchField);
            }

            receipt = installedReceipt;
            return true;
        }

        if (authority.PredecessorAuthorityId is { IsDefault: false } predecessorAuthorityId)
        {
            if (stagedContract.RequestActivePredecessorSupersession)
            {
                if (activePredecessorContract is not { } predecessor ||
                    predecessor.Authority.AuthorityId != predecessorAuthorityId ||
                    predecessor.Authority.SessionId != authority.SessionId ||
                    predecessor.Authority.TransferId != authority.TransferId ||
                    predecessor.Authority.Direction != authority.Direction ||
                    predecessor.Authority.RecoveryContractGeneration !=
                        authority.RecoveryContractGeneration ||
                    predecessor.Authority.DispatchAttemptGeneration + 1 !=
                        authority.DispatchAttemptGeneration ||
                    predecessor.Authority.MissingDispatchSuccessorGeneration !=
                        authority.MissingDispatchSuccessorGeneration ||
                    predecessor.Lifecycle.IsSettled ||
                    predecessor.AttemptPhase is
                        RuntimeUnlockRetryDispatchAttemptPhase.SendInFlight or
                        RuntimeUnlockRetryDispatchAttemptPhase.Settled)
                {
                    return RuntimeUnlockRetryDispatchMismatch(
                        "active_predecessor_lineage",
                        out mismatchField);
                }
            }
            else if (activePredecessorContract is not null)
            {
                return RuntimeUnlockRetryDispatchMismatch(
                    "missing_dispatch_predecessor_lineage",
                    out mismatchField);
            }
            else if (lastSettlement is not { } predecessorSettlement ||
                     predecessorSettlement.Authority.AuthorityId != predecessorAuthorityId)
            {
                return RuntimeUnlockRetryDispatchMismatch(
                    "predecessor_settlement",
                    out mismatchField);
            }
            else if (predecessorSettlement.Outcome !=
                     RuntimeUnlockRetryDispatchSettlementOutcome.MissingDispatch)
            {
                return RuntimeUnlockRetryDispatchMismatch(
                    "missing_dispatch_predecessor_lineage",
                    out mismatchField);
            }
        }
        else if (expectedRequest.PredecessorAuthorityId is not null ||
                 stagedContract.RequestActivePredecessorSupersession)
        {
            return RuntimeUnlockRetryDispatchMismatch(
                "initial_predecessor_lineage",
                out mismatchField);
        }

        if (currentReceipt is null)
        {
            var releaseCurrent = authority.PredecessorAuthorityId is null
                ? TryValidateRuntimeUnlockRetryRelease(
                    state,
                    expectedRequest,
                    authority.ReleaseKind,
                    authority.ReleaseAuthorityId,
                    authority.ReleaseGeneration,
                    authority.ReleaseOutcome,
                    out _)
                : TryValidateInheritedRuntimeUnlockRetryRelease(
                    state,
                    currentContract,
                    authority.ReleaseKind,
                    authority.ReleaseAuthorityId,
                    authority.ReleaseGeneration,
                    authority.ReleaseOutcome,
                    out _);
            if (!releaseCurrent)
            {
                return RuntimeUnlockRetryDispatchMismatch(
                    "release_currentness",
                    out mismatchField);
            }
        }

        var installGeneration = Math.Max(1, lastInstallGeneration + 1);
        receipt = new RuntimeUnlockRetryDispatchInstallReceipt(
            authority,
            installGeneration)
        {
            Request = expectedRequest,
        };
        return true;
    }

    private static bool IsRuntimeUnlockRetryDispatchSupersededReason(string reason)
        => string.Equals(
               reason,
               "coordinator_successor_installed",
               StringComparison.Ordinal) ||
           string.Equals(
               reason,
               "coordinator_successor_authorized",
               StringComparison.Ordinal);

    internal static bool TryValidateRuntimeUnlockRetryDispatchBinding(
        RuntimeUnlockRetryDispatchAuthority authority,
        RuntimeUnlockRetryDispatchAuthorityRequest request,
        out string mismatchField)
    {
        if (!RuntimeUnlockRetryDispatchAuthorityMatchesStableRequest(
                authority,
                request,
                out mismatchField))
        {
            return false;
        }

        if (authority.TargetRoute != FileTransferRoute.FileTunaV4 ||
            authority.TargetProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
            authority.TargetTransport != FileTransferTransportKind.Tuna)
        {
            return RuntimeUnlockRetryDispatchMismatch("target_route", out mismatchField);
        }

        if (authority.ReleaseKind == RuntimeUnlockRetryReleaseKind.Unknown ||
            authority.ReleaseAuthorityId is not { IsDefault: false } ||
            authority.ReleaseGeneration <= 0 ||
            string.IsNullOrWhiteSpace(authority.ReleaseOutcome))
        {
            return RuntimeUnlockRetryDispatchMismatch("release_identity", out mismatchField);
        }

        return true;
    }

    private static bool RuntimeUnlockRetryDispatchAuthorityMatchesStableRequest(
        RuntimeUnlockRetryDispatchAuthority authority,
        RuntimeUnlockRetryDispatchAuthorityRequest request,
        out string mismatchField)
    {
        mismatchField = "none";
        if (authority.IsDefault)
        {
            return RuntimeUnlockRetryDispatchMismatch("authority", out mismatchField);
        }

        if (!string.Equals(authority.SessionId, request.SessionId, StringComparison.Ordinal) ||
            !string.Equals(authority.TransferId, request.TransferId, StringComparison.Ordinal) ||
            authority.Direction != request.Direction)
        {
            return RuntimeUnlockRetryDispatchMismatch("request_identity", out mismatchField);
        }

        if (!MatchesRuntimeUnlockRetrySourcePublication(
                request.SourcePublicationAuthority,
                authority.SourcePublicationAuthority))
        {
            return RuntimeUnlockRetryDispatchMismatch("request_source_publication", out mismatchField);
        }

        if (authority.RecoveryContractGeneration != request.RecoveryContractGeneration ||
            authority.RetiredOfferGeneration != request.RetiredOfferGeneration ||
            authority.DispatchAttemptGeneration != request.DispatchAttemptGeneration ||
            authority.MissingDispatchSuccessorGeneration !=
                request.MissingDispatchSuccessorGeneration ||
            authority.PredecessorAuthorityId != request.PredecessorAuthorityId)
        {
            return RuntimeUnlockRetryDispatchMismatch("request_attempt", out mismatchField);
        }

        if (!string.Equals(authority.RetryReason, NormalizeReason(request.RetryReason), StringComparison.Ordinal) ||
            !string.Equals(authority.RecoveryReason, NormalizeReason(request.RecoveryReason), StringComparison.Ordinal))
        {
            return RuntimeUnlockRetryDispatchMismatch("request_reason", out mismatchField);
        }

        return true;
    }

    private static bool RuntimeUnlockRetryDispatchMismatch(
        string field,
        out string mismatchField)
    {
        mismatchField = field;
        return false;
    }

    private static bool MatchesRuntimeUnlockRetrySourcePublication(
        FileTransferRoutePublicationAuthority candidate,
        RoutePublicationToken publication)
        => MatchesRoutePublicationAuthority(candidate, publication);

    internal static bool MatchesRoutePublicationAuthority(
        FileTransferRoutePublicationAuthority candidate,
        RoutePublicationToken publication)
        => string.Equals(candidate.SessionId, publication.SessionId, StringComparison.Ordinal) &&
           string.Equals(candidate.TransferId, publication.TransferId, StringComparison.Ordinal) &&
           candidate.Direction == publication.Direction &&
           string.Equals(candidate.RouteToken, publication.RouteToken, StringComparison.Ordinal) &&
           candidate.ProtocolVersion == publication.ProtocolVersion &&
           candidate.LiveRouteEpoch == publication.LiveRouteEpoch &&
           candidate.RuntimeGeneration == publication.RuntimeGeneration &&
           candidate.TerminalGeneration == publication.TerminalGeneration &&
           candidate.LegGeneration == publication.LegGeneration &&
           candidate.RoutePublicationGeneration == publication.PublicationGeneration &&
           candidate.RouteCommitGeneration == publication.RouteCommitGeneration &&
           string.Equals(
               candidate.TargetRouteToken,
               RuntimeUnlockRetryRouteToken(publication.TargetRoute),
               StringComparison.Ordinal) &&
           candidate.TargetProtocolVersion == publication.TargetProtocolVersion &&
           candidate.TargetTransport == publication.TargetTransport &&
           string.Equals(
               candidate.RoutePublicationActionKind,
               publication.ActionKind.ToString(),
               StringComparison.Ordinal);

    internal static FileTransferRoutePublicationAuthority? CreateRoutePublicationAuthority(
        RoutePublicationToken? publication,
        string? coordinatorDecisionReason = null)
    {
        if (publication is not { IsDefault: false } token)
        {
            return null;
        }

        return new FileTransferRoutePublicationAuthority(
            token.SessionId,
            token.TransferId,
            token.Direction,
            token.RouteToken,
            token.ProtocolVersion,
            token.LiveRouteEpoch,
            token.RuntimeGeneration,
            token.TerminalGeneration,
            token.LegGeneration,
            token.PublicationGeneration,
            token.RouteCommitGeneration,
            RuntimeUnlockRetryRouteToken(token.TargetRoute),
            token.TargetProtocolVersion,
            token.TargetTransport,
            token.ActionKind.ToString(),
            coordinatorDecisionReason ?? token.Reason);
    }

    private static bool TryValidateRuntimeUnlockRetryRelease(
        FileTransferCoordinatorState state,
        RuntimeUnlockRetryDispatchAuthorityRequest request,
        RuntimeUnlockRetryReleaseKind releaseKind,
        FileTransferAuthorityId? releaseAuthorityId,
        int releaseGeneration,
        string releaseOutcome,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (state.RouteSelection.Route == FileTransferRoute.RegularNknV4Fast)
        {
            if (releaseKind == RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement)
            {
                if (releaseAuthorityId is not { IsDefault: false } || releaseGeneration <= 0)
                {
                    rejectionReason = "regular_v4_repair_settlement_pending";
                    return false;
                }

                if (state.CurrentRepairReplayDecision is not { } repairDecision ||
                    repairDecision.AuthorityId != releaseAuthorityId ||
                    repairDecision.RepairGeneration != releaseGeneration)
                {
                    rejectionReason = "regular_v4_repair_settlement_stale";
                    return false;
                }

                if (releaseOutcome is not (
                        nameof(FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved) or
                        nameof(FileTransferRegularV4FrontierRepairSettlementOutcome.MissingDispatch) or
                        nameof(FileTransferRegularV4FrontierRepairSettlementOutcome.SettledStalled)))
                {
                    rejectionReason = string.Equals(
                        releaseOutcome,
                        nameof(FileTransferRegularV4FrontierRepairSettlementOutcome.Superseded),
                        StringComparison.Ordinal)
                        ? "regular_v4_repair_settlement_pending"
                        : "regular_v4_repair_settlement_rejected";
                    return false;
                }

                if (state.LastRuntimeUnlockRegularV4RecoverySettlement is not { } handoff)
                {
                    rejectionReason = "regular_v4_recovery_settlement_consumption_pending";
                    return false;
                }

                if (!state.RuntimeUnlockRegularV4RecoverySettlementAvailable)
                {
                    rejectionReason = "regular_v4_recovery_settlement_consumption_already_used";
                    return false;
                }

                if (handoff.Disposition !=
                    RuntimeUnlockRegularV4RecoverySettlementDisposition.Consumed)
                {
                    rejectionReason = "regular_v4_recovery_settlement_consumption_rejected";
                    return false;
                }

                if (state.LastCoordinatorIssuedRegularV4RecoverySettlement is not { } issuedSettlement ||
                    !ReferenceEquals(issuedSettlement, handoff.Settlement) ||
                    issuedSettlement != handoff.Settlement ||
                    !RuntimeUnlockRegularV4RecoverySettlementMatchesCurrentRepair(
                        state,
                        repairDecision,
                        handoff.Settlement) ||
                    handoff.Settlement.AuthorityId != releaseAuthorityId ||
                    handoff.Settlement.DecisionGeneration != releaseGeneration ||
                    !string.Equals(
                        handoff.Settlement.Outcome.ToString(),
                        releaseOutcome,
                        StringComparison.Ordinal) ||
                    handoff.RegularV4LivenessGeneration != request.RegularV4LivenessGeneration ||
                    handoff.BridgeDecisionGeneration != request.BridgeDecisionGeneration)
                {
                    rejectionReason = "regular_v4_recovery_settlement_consumption_stale";
                    return false;
                }

                return true;
            }

            if (releaseKind == RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure)
            {
                if (state.CurrentRecoveryLivenessLease is not null ||
                    !state.RuntimeUnlockRecoveryLeaseClosureAvailable ||
                    state.LastRetiredRecoveryLivenessLease is not { } retiredLease ||
                    state.LastRuntimeUnlockRegularV4RecoveryLifecycle is not { } lifecycle ||
                    lifecycle.Outcome is not (
                        RuntimeUnlockRegularV4RecoveryLifecycleOutcome.Completed or
                        RuntimeUnlockRegularV4RecoveryLifecycleOutcome.ReceiveProofObserved) ||
                    state.CurrentBridgeRecoveryDecision is not
                    {
                        Accepted: true,
                        RecoveryKind: BridgeRecoveryKind.BridgeReceiveStallRecovery,
                    } bridgeDecision ||
                    state.CurrentLeg is not { } currentLeg ||
                    releaseAuthorityId is not { IsDefault: false } ||
                    retiredLease.AuthorityId != releaseAuthorityId ||
                    retiredLease.LeaseGeneration != releaseGeneration ||
                    request.RegularV4LivenessGeneration != retiredLease.LeaseGeneration ||
                    request.BridgeDecisionGeneration != bridgeDecision.BridgeDecisionGeneration ||
                    lifecycle.RegularV4LivenessGeneration != retiredLease.LeaseGeneration ||
                    lifecycle.Authority.BridgeDecisionGeneration != request.BridgeDecisionGeneration ||
                    retiredLease.State != RecoveryLivenessLeaseState.Retired ||
                    retiredLease != (lifecycle.RecoveryAuthority.RecoveryLivenessLease with
                    {
                        State = RecoveryLivenessLeaseState.Retired,
                        Reason = NormalizeReason(lifecycle.Reason),
                    }) ||
                    !string.Equals(
                        releaseOutcome,
                        nameof(RecoveryLivenessLeaseState.Retired),
                        StringComparison.Ordinal) ||
                    retiredLease.RuntimeOwner != state.CurrentRuntimeOwner ||
                    retiredLease.Route != state.RouteSelection.Route ||
                    retiredLease.ProtocolVersion != state.RouteSelection.ProtocolVersion ||
                    !string.Equals(
                        retiredLease.RouteToken,
                        state.RouteSelection.TelemetryToken,
                        StringComparison.Ordinal) ||
                    retiredLease.LiveRouteEpoch != (state.CurrentLiveRouteEpoch?.EpochId ?? 0) ||
                    retiredLease.TerminalGeneration != state.TerminalGeneration ||
                    retiredLease.LegGeneration != currentLeg.Generation ||
                    retiredLease.TransportEpoch != currentLeg.TransportEpochId ||
                    retiredLease.BridgeRecoveryGeneration != currentLeg.BridgeRecoveryGeneration ||
                    lifecycle.Authority.SessionId != state.SessionId ||
                    lifecycle.Authority.TransferId != state.TransferId ||
                    lifecycle.Authority.Direction != state.Direction ||
                    lifecycle.Authority.RouteToken != state.RouteSelection.TelemetryToken ||
                    lifecycle.Authority.ProtocolVersion != state.RouteSelection.ProtocolVersion ||
                    lifecycle.Authority.LiveRouteEpoch != (state.CurrentLiveRouteEpoch?.EpochId ?? 0) ||
                    lifecycle.Authority.RuntimeGeneration != state.CurrentRuntimeOwner?.RuntimeGeneration ||
                    lifecycle.Authority.TerminalGeneration != state.TerminalGeneration ||
                    lifecycle.Authority.TransferLegGeneration != currentLeg.Generation ||
                    lifecycle.Authority.TransportEpoch != currentLeg.TransportEpochId ||
                    lifecycle.Authority.BridgeRecoveryGeneration != currentLeg.BridgeRecoveryGeneration ||
                    lifecycle.Authority.BridgeDecisionGeneration != bridgeDecision.BridgeDecisionGeneration)
                {
                    rejectionReason = "receive_recovery_lease_stale";
                    return false;
                }

                return true;
            }

            rejectionReason = "regular_v4_repair_settlement_pending";
            return false;
        }

        if (state.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            releaseKind != RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield ||
            state.LastFallbackTailYieldDecision is not { Accepted: true } tailYield ||
            releaseAuthorityId is not { IsDefault: false } ||
            tailYield.AuthorityId != releaseAuthorityId ||
            releaseGeneration != tailYield.YieldGeneration ||
            !state.RuntimeUnlockFallbackSurvivalState.PairedFallbackYieldGranted)
        {
            rejectionReason = "fallback_paired_tail_yield_pending";
            return false;
        }

        if (state.CurrentFallbackCheckpointLease is not null ||
            state.CurrentFallbackControlPlaneToken is not null ||
            state.CurrentFallbackControlPlanePressureToken is not null)
        {
            rejectionReason = "fallback_owner_work_pending";
            return false;
        }

        return true;
    }

    private static bool RuntimeUnlockRegularV4RecoverySettlementMatchesCurrentRepair(
        FileTransferCoordinatorState state,
        RepairReplayDecision repairDecision,
        FileTransferRegularV4FrontierRepairSettlement settlement)
    {
        var publication = repairDecision.RoutePublicationToken;
        return settlement is not null &&
               !settlement.AuthorityId.IsDefault &&
               settlement.AuthorityId == repairDecision.AuthorityId &&
               settlement.DecisionGeneration == repairDecision.RepairGeneration &&
               string.Equals(settlement.SessionId, state.SessionId, StringComparison.Ordinal) &&
               string.Equals(settlement.TransferId, state.TransferId, StringComparison.Ordinal) &&
               settlement.Direction == state.Direction &&
               string.Equals(
                   settlement.RouteToken,
                   state.RouteSelection.TelemetryToken,
                   StringComparison.Ordinal) &&
               settlement.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
               settlement.LiveRouteEpoch == (state.CurrentLiveRouteEpoch?.EpochId ?? 0) &&
               settlement.RuntimeGeneration == (state.CurrentRuntimeOwner?.RuntimeGeneration ?? 0) &&
               settlement.TerminalGeneration == state.TerminalGeneration &&
               settlement.LegGeneration == state.CurrentLeg?.Generation &&
               settlement.TransportEpoch == state.CurrentLeg?.TransportEpochId &&
               settlement.BridgeRecoveryGeneration == state.CurrentLeg?.BridgeRecoveryGeneration &&
               settlement.BridgeDecisionGeneration == repairDecision.BridgeDecisionGeneration &&
               settlement.RoutePublicationGeneration == publication?.PublicationGeneration &&
               settlement.RouteCommitGeneration == publication?.RouteCommitGeneration &&
               settlement.TargetTransport == FileTransferTransportKind.RegularNkn;
    }

    private static bool TryValidateInheritedRuntimeUnlockRetryRelease(
        FileTransferCoordinatorState state,
        RuntimeUnlockRetryDispatchContract currentContract,
        RuntimeUnlockRetryReleaseKind releaseKind,
        FileTransferAuthorityId? releaseAuthorityId,
        int releaseGeneration,
        string releaseOutcome,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (releaseAuthorityId is not { IsDefault: false } || releaseGeneration <= 0)
        {
            rejectionReason = "retry_dispatch_predecessor_release_invalid";
            return false;
        }

        if (releaseKind == RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement)
        {
            if (state.LastRuntimeUnlockRegularV4RecoverySettlement is not
                {
                    Disposition: RuntimeUnlockRegularV4RecoverySettlementDisposition.Consumed,
                } handoff ||
                state.LastCoordinatorIssuedRegularV4RecoverySettlement is not { } issuedSettlement ||
                !ReferenceEquals(issuedSettlement, handoff.Settlement) ||
                issuedSettlement != handoff.Settlement ||
                handoff.Settlement.AuthorityId != releaseAuthorityId ||
                handoff.Settlement.DecisionGeneration != releaseGeneration ||
                !string.Equals(
                    handoff.Settlement.Outcome.ToString(),
                    releaseOutcome,
                    StringComparison.Ordinal) ||
                currentContract.Authority.ReleaseKind != releaseKind ||
                currentContract.Authority.ReleaseAuthorityId != releaseAuthorityId ||
                currentContract.Authority.ReleaseGeneration != releaseGeneration ||
                !string.Equals(
                    currentContract.Authority.ReleaseOutcome,
                    releaseOutcome,
                    StringComparison.Ordinal))
            {
                rejectionReason = "retry_dispatch_predecessor_recovery_handoff_stale";
                return false;
            }
        }

        var outcomeValid = releaseKind switch
        {
            RuntimeUnlockRetryReleaseKind.RegularV4RepairSettlement => releaseOutcome is
                nameof(FileTransferRegularV4FrontierRepairSettlementOutcome.ProgressObserved) or
                nameof(FileTransferRegularV4FrontierRepairSettlementOutcome.MissingDispatch) or
                nameof(FileTransferRegularV4FrontierRepairSettlementOutcome.SettledStalled),
            RuntimeUnlockRetryReleaseKind.ReceiveRecoveryLeaseClosure => string.Equals(
                releaseOutcome,
                nameof(RecoveryLivenessLeaseState.Retired),
                StringComparison.Ordinal),
            RuntimeUnlockRetryReleaseKind.FallbackPairedTailYield => string.Equals(
                releaseOutcome,
                "PairedYieldGranted",
                StringComparison.Ordinal),
            _ => false,
        };
        if (!outcomeValid)
        {
            rejectionReason = "retry_dispatch_predecessor_release_invalid";
            return false;
        }

        return true;
    }

    private static string RuntimeUnlockRetryRouteToken(FileTransferRoute route)
        => route switch
        {
            FileTransferRoute.RegularNknV4Fast => FileTransferRouteResolver.RegularNknV4FastToken,
            FileTransferRoute.FileTunaV4 => FileTransferRouteResolver.FileTunaV4Token,
            FileTransferRoute.PostTunaFallbackV6 => FileTransferRouteResolver.PostTunaFallbackV6Token,
            _ => "unknown",
        };

    private static FileTransferAuthorityId IssueAuthorityId(
        string authorityKind,
        FileTransferCoordinatorState state,
        int generation)
    {
        var owner = state.CurrentRuntimeOwner;
        var leg = state.CurrentLeg;
        var canonical = string.Join(
            "|",
            FileTransferAuthoritySchema.Current.ToString(System.Globalization.CultureInfo.InvariantCulture),
            NormalizeReason(authorityKind),
            state.SessionId,
            state.TransferId,
            state.Direction.ToString(),
            (owner?.RuntimeGeneration ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            state.TerminalGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (leg?.Generation ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical));
        return new FileTransferAuthorityId(
            $"fta{FileTransferAuthoritySchema.Current}-{NormalizeReason(authorityKind)}-{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}");
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
            IssueAuthorityId("recovery_liveness_lease", state, leaseGeneration),
            leaseState,
            NormalizeReason(reason));
    }

    private static bool TryValidateFallbackTailYieldTransportEpochReauthorizationStart(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorEvent coordinatorEvent,
        FallbackTailYieldTransportEpochReauthorizationRequest request,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (request.IsDefault)
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_request_invalid";
            return false;
        }

        if (request.PendingReauthorization is { } pendingReauthorization)
        {
            return TryValidateFallbackTailYieldTransportEpochReauthorizationRestart(
                state,
                coordinatorEvent,
                request,
                pendingReauthorization,
                out rejectionReason);
        }

        if (state.CurrentRuntimeOwner is not { } sourceOwner ||
            state.CurrentLeg is not { } sourceLeg)
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_source_missing";
            return false;
        }

        if (state.CurrentRoutePublicationToken is not { } sourcePublication ||
            sourcePublication != request.SourceRoutePublicationToken ||
            !IsRoutePublicationTokenCurrent(state, sourcePublication, out _) ||
            sourcePublication.TargetRoute != FileTransferRoute.PostTunaFallbackV6 ||
            sourcePublication.TargetProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            sourcePublication.TargetTransport != FileTransferTransportKind.RegularNkn)
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_publication_missing";
            return false;
        }

        var predecessor = request.PredecessorDecision;
        var sourceEpochAlreadyAdopted =
            sourceLeg.TransportEpochId == request.TargetTransportEpoch;
        var checkpointSnapshot = NormalizeFallbackTailYieldCheckpointRequestId(
            state.CurrentFallbackCheckpointLease);
        var expectedSourceOwnerKey = BuildFallbackTailYieldOwnerKey(
            state.SessionId,
            state.TransferId,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? sourceLeg.LiveRouteEpochId,
            predecessor.CurrentLegGeneration,
            predecessor.CurrentBridgeRecoveryGeneration,
            request.SourceTransportEpoch,
            checkpointSnapshot);
        var expectedSourcePairKey = BuildFallbackTailYieldPairKey(
            state.SessionId,
            state.TransferId,
            state.RouteSelection.TelemetryToken,
            state.RouteSelection.ProtocolVersion,
            state.CurrentLiveRouteEpoch?.EpochId ?? sourceLeg.LiveRouteEpochId,
            request.SourceTransportEpoch,
            checkpointSnapshot);

        if (state.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            state.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            coordinatorEvent.RouteSelection != state.RouteSelection ||
            request.SourceRuntimeOwner != sourceOwner ||
            state.LastFallbackTailYieldDecision is not { } lastYield ||
            lastYield != predecessor ||
            !predecessor.Accepted ||
            predecessor.YieldKind != FallbackTailYieldKind.Quiescent ||
            predecessor.AuthorityId != IssueAuthorityId(
                "fallback_tail_yield",
                state,
                predecessor.YieldGeneration) ||
            predecessor.CurrentRoute != state.RouteSelection.Route ||
            predecessor.CurrentProtocolVersion != state.RouteSelection.ProtocolVersion ||
            predecessor.CurrentLiveRouteEpoch != (state.CurrentLiveRouteEpoch?.EpochId ?? 0) ||
            predecessor.CurrentRuntimeGeneration != sourceOwner.RuntimeGeneration ||
            predecessor.CurrentTerminalGeneration != state.TerminalGeneration ||
            predecessor.CurrentLegGeneration != sourceLeg.Generation ||
            (!sourceEpochAlreadyAdopted &&
             predecessor.CurrentBridgeRecoveryGeneration != sourceLeg.BridgeRecoveryGeneration) ||
            (sourceEpochAlreadyAdopted &&
             sourceLeg.BridgeRecoveryGeneration < predecessor.CurrentBridgeRecoveryGeneration) ||
            !string.Equals(
                predecessor.CheckpointRequestIdSnapshot,
                checkpointSnapshot,
                StringComparison.Ordinal) ||
            !string.Equals(predecessor.OwnerKey, expectedSourceOwnerKey, StringComparison.Ordinal) ||
            !string.Equals(predecessor.PairKey, expectedSourcePairKey, StringComparison.Ordinal) ||
            (sourceLeg.TransportEpochId != request.SourceTransportEpoch &&
             sourceLeg.TransportEpochId != request.TargetTransportEpoch) ||
            coordinatorEvent.TransportEpochId != request.TargetTransportEpoch ||
            coordinatorEvent.BridgeRecoveryGeneration != sourceLeg.BridgeRecoveryGeneration ||
            state.RuntimeUnlockFallbackSurvivalState.PairedFallbackYieldGranted)
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_source_mismatch";
            return false;
        }

        return true;
    }

    private static FallbackTailYieldTransportEpochReauthorization
        CreateFallbackTailYieldTransportEpochReauthorization(
            FileTransferCoordinatorState sourceState,
            FileTransferCoordinatorState targetState,
            FallbackTailYieldTransportEpochReauthorizationRequest request)
    {
        var targetOwner = targetState.CurrentRuntimeOwner!.Value;
        var targetLeg = targetState.CurrentLeg!;
        return new FallbackTailYieldTransportEpochReauthorization(
            targetState.SessionId,
            targetState.TransferId,
            targetState.Direction,
            request.PredecessorDecision,
            request.SourceRuntimeOwner,
            request.SourceRoutePublicationToken,
            request.PredecessorDecision.OwnerKey,
            request.PredecessorDecision.PairKey,
            request.SourceTransportEpoch,
            request.TargetTransportEpoch,
            targetOwner,
            targetState.TerminalGeneration,
            targetLeg.Generation,
            targetLeg.BridgeRecoveryGeneration,
            targetState.CurrentRoutePublicationToken,
            targetState.CurrentBridgeRecoveryDecision,
            NormalizeFallbackTailYieldCheckpointRequestId(targetState.CurrentFallbackCheckpointLease),
            Math.Max(
                1,
                sourceState.LastFallbackTailYieldTransportEpochReauthorizationGeneration + 1),
            NormalizeReason(request.Reason));
    }

    private static FileTransferCoordinatorDecision StartTransfer(
        FileTransferCoordinatorEvent coordinatorEvent,
        FileTransferCoordinatorState state,
        string reason)
    {
        var reauthorizationRequest =
            coordinatorEvent.FallbackTailYieldTransportEpochReauthorizationRequest;
        if (reauthorizationRequest is { } requestedReauthorization &&
            !TryValidateFallbackTailYieldTransportEpochReauthorizationStart(
                state,
                coordinatorEvent,
                requestedReauthorization,
                out var reauthorizationRejectionReason))
        {
            return MutationRejected(state, reason, reauthorizationRejectionReason);
        }

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

        if (HasRegularV4FrontierRepairPressureBinding(state))
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_frontier_repair_pressure_retirement_required",
                initialTunaRouteSelectionDecision: initialTunaDecision);
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
        if (reauthorizationRequest is { } reauthorization)
        {
            var pendingReauthorization = CreateFallbackTailYieldTransportEpochReauthorization(
                state,
                nextState,
                reauthorization);
            nextState = nextState with
            {
                PendingFallbackTailYieldTransportEpochReauthorization = pendingReauthorization,
                LastFallbackTailYieldTransportEpochReauthorizationGeneration =
                    pendingReauthorization.ReauthorizationGeneration,
            };
        }

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
        var isSessionReactivationEvidence = string.Equals(
            evidence.ReadinessScope,
            FileTransferInitialTunaReadinessScopes.SessionReactivation,
            StringComparison.Ordinal);
        var evidenceMaxAge = isSessionReactivationEvidence
            ? SessionReactivationInitialTunaReadinessEvidenceMaxAge
            : InitialTunaReadinessEvidenceMaxAge;
        if (isSessionReactivationEvidence &&
            !HasValidSessionReactivationInitialTunaEvidence(evidence, state, now))
        {
            rejectionReason = "initial_tuna_session_reactivation_evidence_invalid";
            return false;
        }

        if (evidence.CapturedUtc == default ||
            evidence.CapturedUtc > now ||
            now - evidence.CapturedUtc > evidenceMaxAge)
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

    private static bool HasValidSessionReactivationInitialTunaEvidence(
        FileTransferInitialTunaReadinessEvidence evidence,
        FileTransferCoordinatorState state,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(evidence.SourceTransferId) ||
            string.Equals(evidence.SourceTransferId.Trim(), state.TransferId, StringComparison.Ordinal) ||
            evidence.SnapshotGeneration <= 0 ||
            evidence.SourceCapturedUtc == default ||
            evidence.SourceCapturedUtc > now ||
            now - evidence.SourceCapturedUtc > SessionReactivationInitialTunaReadinessEvidenceMaxAge)
        {
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
        if (HasRegularV4FrontierRepairPressureBinding(state))
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_frontier_repair_pressure_retirement_required");
        }

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

        if (HasRegularV4FrontierRepairPressureBinding(state) &&
            coordinatorEvent.TransportEpochId > 0 &&
            coordinatorEvent.TransportEpochId != leg.TransportEpochId)
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_frontier_repair_pressure_retirement_required");
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

        if (HasRegularV4FrontierRepairPressureBinding(state) &&
            coordinatorEvent.TransportEpochId > 0 &&
            coordinatorEvent.TransportEpochId != leg.TransportEpochId)
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_frontier_repair_pressure_retirement_required");
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

        if (HasRegularV4FrontierRepairPressureBinding(state) &&
            coordinatorEvent.TransportEpochId > 0 &&
            coordinatorEvent.TransportEpochId != leg.TransportEpochId)
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_frontier_repair_pressure_retirement_required");
        }

        var reauthorization =
            coordinatorEvent.FallbackTailYieldTransportEpochReauthorization;
        if (reauthorization is { } suppliedReauthorization)
        {
            if (!IsFallbackTailYieldTransportEpochReauthorizationCurrent(
                    state,
                    suppliedReauthorization,
                    out var reauthorizationRejectionReason))
            {
                return MutationRejected(state, reason, reauthorizationRejectionReason);
            }
        }
        else if (state.PendingFallbackTailYieldTransportEpochReauthorization is not null)
        {
            return MutationRejected(
                state,
                reason,
                "fallback_tail_yield_transport_epoch_reauthorization_missing");
        }

        var previousTransportEpoch = leg.TransportEpochId;
        MarkFallbackTailQuiescent(
            leg,
            coordinatorEvent.TransportEpochId,
            coordinatorEvent.CommittedChunkIndex,
            coordinatorEvent.HighestObservedChunkIndex);
        var yieldGeneration = Math.Max(1, state.LastFallbackTailYieldGeneration + 1);
        var tailState = state with
        {
            CurrentFallbackCheckpointLease = null,
        };
        var yieldDecision = CreateFallbackTailYieldDecision(
            tailState,
            accepted: true,
            FallbackTailYieldKind.Quiescent,
            reason,
            yieldGeneration,
            predecessorAuthorityId:
                reauthorization?.PredecessorDecision.AuthorityId);
        var nextState = tailState with
        {
            LastFallbackTailYieldDecision = yieldDecision,
            LastFallbackTailYieldGeneration = yieldGeneration,
            PendingFallbackTailYieldTransportEpochReauthorization = null,
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
                PendingFallbackTailYieldTransportEpochReauthorization = null,
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

            if (coordinatorEvent.ControlPlaneKind != FileTransferControlPlaneKind.Unknown &&
                coordinatorEvent.ControlPlaneKind != suppliedToken.ControlPlaneKind)
            {
                return MutationRejected(state, reason, "fallback_control_plane_kind_mismatch");
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

        if (coordinatorEvent.ControlPlaneKind != FileTransferControlPlaneKind.Unknown &&
            coordinatorEvent.ControlPlaneKind != token.ControlPlaneKind)
        {
            return MutationRejected(state, reason, "fallback_control_plane_kind_mismatch");
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
    {
        if (HasRegularV4FrontierRepairPressureBinding(state))
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_frontier_repair_pressure_retirement_required");
        }

        return Decision(
            state with
            {
                CurrentRuntimeUnlockAdmission = null,
                CurrentRouteCommit = null,
                CurrentRoutePublicationToken = null,
                PendingFallbackTailYieldTransportEpochReauthorization = null,
                CurrentRuntimeUnlockPendingPreCommitProbe = null,
            },
            reason);
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
        if (transition.MutationRejectedReason is { } transitionRejectionReason)
        {
            return transition with
            {
                RuntimeUnlockCommitRejectedReason = transitionRejectionReason,
            };
        }

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
        if (HasRegularV4FrontierRepairPressureBinding(state))
        {
            return MutationRejected(
                state,
                reason,
                "regular_v4_frontier_repair_pressure_retirement_required");
        }

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
                LastRuntimeUnlockRegularV4RecoverySettlement = null,
                LastCoordinatorIssuedRegularV4RecoverySettlement = null,
                RuntimeUnlockRegularV4RecoverySettlementAvailable = false,
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
            if (next.PendingFallbackTailYieldTransportEpochReauthorization is { } pending &&
                pending.TargetBridgeRecoveryDecision != bridgeRecovery)
            {
                if (CanBindFallbackTailYieldTransportEpochReauthorizationBridgeDecision(
                        next,
                        pending,
                        bridgeRecovery))
                {
                    var reauthorizationGeneration = Math.Max(
                        pending.ReauthorizationGeneration + 1,
                        next.LastFallbackTailYieldTransportEpochReauthorizationGeneration + 1);
                    next = next with
                    {
                        PendingFallbackTailYieldTransportEpochReauthorization = pending with
                        {
                            TargetBridgeRecoveryDecision = bridgeRecovery,
                            ReauthorizationGeneration = reauthorizationGeneration,
                            Reason = NormalizeReason(bridgeRecovery.Reason),
                        },
                        LastFallbackTailYieldTransportEpochReauthorizationGeneration =
                            reauthorizationGeneration,
                    };
                }
                else
                {
                    next = next with
                    {
                        PendingFallbackTailYieldTransportEpochReauthorization = null,
                    };
                }
            }

            next = recoveryLivenessLease is null
                ? next with
                {
                    CurrentBridgeRecoveryDecision = bridgeRecovery,
                    LastBridgeRecoveryDecisionGeneration = bridgeRecovery.BridgeDecisionGeneration,
                    CurrentRecoveryLivenessLease = null,
                    LastRetiredRecoveryLivenessLease = null,
                    LastRuntimeUnlockRegularV4RecoveryLifecycle = null,
                    RuntimeUnlockRecoveryLeaseClosureAvailable = false,
                }
                : next with
                {
                    CurrentBridgeRecoveryDecision = bridgeRecovery,
                    LastBridgeRecoveryDecisionGeneration = bridgeRecovery.BridgeDecisionGeneration,
                };
        }

        if (recoveryLivenessLease is { } livenessLease)
        {
            if (kind == FileTransferCoordinatorActionKind.RecoveryLeaseClosureLivenessDeferral)
            {
                return next;
            }

            var startsRegularV4Recovery =
                kind == FileTransferCoordinatorActionKind.BridgeReceiveStallRecovery &&
                bridgeRecoveryDecision is not null;
            next = kind == FileTransferCoordinatorActionKind.RecoveryLivenessLeaseRetire
                ? next with
                {
                    CurrentRecoveryLivenessLease = null,
                    LastRetiredRecoveryLivenessLease = livenessLease,
                    LastRecoveryLivenessLeaseGeneration = Math.Max(
                        next.LastRecoveryLivenessLeaseGeneration,
                        livenessLease.LeaseGeneration),
                }
                : next with
                {
                    CurrentRecoveryLivenessLease = livenessLease,
                    LastRetiredRecoveryLivenessLease = null,
                    LastRecoveryLivenessLeaseGeneration = Math.Max(
                        next.LastRecoveryLivenessLeaseGeneration,
                        livenessLease.LeaseGeneration),
                    LastRuntimeUnlockRegularV4RecoverySettlement =
                        kind == FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin ||
                        startsRegularV4Recovery
                            ? null
                            : next.LastRuntimeUnlockRegularV4RecoverySettlement,
                    LastCoordinatorIssuedRegularV4RecoverySettlement =
                        startsRegularV4Recovery
                            ? null
                            : next.LastCoordinatorIssuedRegularV4RecoverySettlement,
                    LastRuntimeUnlockRegularV4RecoveryLifecycle =
                        kind == FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin ||
                        startsRegularV4Recovery
                            ? null
                            : next.LastRuntimeUnlockRegularV4RecoveryLifecycle,
                    RuntimeUnlockRecoveryLeaseClosureAvailable =
                        kind == FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin ||
                        startsRegularV4Recovery
                            ? false
                            : next.RuntimeUnlockRecoveryLeaseClosureAvailable,
                    RuntimeUnlockRegularV4RecoverySettlementAvailable =
                        kind == FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin ||
                        startsRegularV4Recovery
                            ? false
                            : next.RuntimeUnlockRegularV4RecoverySettlementAvailable,
                };
        }

        return next;
    }

    private static bool CanBindFallbackTailYieldTransportEpochReauthorizationBridgeDecision(
        FileTransferCoordinatorState state,
        FallbackTailYieldTransportEpochReauthorization pending,
        BridgeRecoveryDecision bridgeRecovery)
    {
        var initialTargetBinding = pending.TargetBridgeRecoveryDecision is null;
        var authorizedNewerTargetBinding =
            pending.TargetBridgeRecoveryDecision is { } previous &&
            pending.TargetTransportEpoch > previous.TransportEpoch &&
            bridgeRecovery.BridgeDecisionGeneration > previous.BridgeDecisionGeneration;
        return (initialTargetBinding || authorizedNewerTargetBinding) &&
               !state.IsTerminal &&
               bridgeRecovery.Accepted &&
               !bridgeRecovery.IsDefault &&
               state.CurrentRuntimeOwner == pending.TargetRuntimeOwner &&
               state.CurrentLeg is { } leg &&
               leg.Generation == pending.TargetLegGeneration &&
               leg.TransportEpochId == pending.TargetTransportEpoch &&
               leg.BridgeRecoveryGeneration == pending.TargetBridgeRecoveryGeneration &&
               bridgeRecovery.RuntimeOwner == pending.TargetRuntimeOwner &&
               bridgeRecovery.CurrentRoute == FileTransferRoute.PostTunaFallbackV6 &&
               bridgeRecovery.CurrentProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
               bridgeRecovery.CurrentLiveRouteEpoch == pending.TargetRuntimeOwner.LiveRouteEpoch &&
               bridgeRecovery.CurrentRuntimeGeneration == pending.TargetRuntimeOwner.RuntimeGeneration &&
               bridgeRecovery.CurrentTerminalGeneration == pending.TargetTerminalGeneration &&
               bridgeRecovery.CurrentLegGeneration == pending.TargetLegGeneration &&
               bridgeRecovery.TransportEpoch == pending.TargetTransportEpoch &&
               bridgeRecovery.BridgeRecoveryGeneration == pending.TargetBridgeRecoveryGeneration &&
               pending.TargetRoutePublicationToken is { } publication &&
               state.CurrentRoutePublicationToken == publication &&
               IsRoutePublicationTokenCurrent(state, publication, out _);
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

            var completionToken = request.FallbackControlPlaneToken.Value;
            if (request.ControlPlaneKind is { } controlPlaneKind &&
                controlPlaneKind != completionToken.ControlPlaneKind)
            {
                rejectionReason = "fallback_control_plane_kind_mismatch";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(request.ControlPlanePurpose) &&
                !string.Equals(
                    NormalizeReason(request.ControlPlanePurpose),
                    completionToken.SendPurpose,
                    StringComparison.Ordinal))
            {
                rejectionReason = "fallback_control_plane_purpose_mismatch";
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

        if (request.FallbackTailYieldTransportEpochReauthorization is { } reauthorization)
        {
            if (request.Kind != FileTransferCoordinatorActionKind.FallbackTailQuiescent)
            {
                rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_action_invalid";
                return false;
            }

            if (!IsFallbackTailYieldTransportEpochReauthorizationCurrent(
                    state,
                    reauthorization,
                    out rejectionReason))
            {
                return false;
            }

            if (request.PredecessorAuthorityId is { } suppliedPredecessor &&
                suppliedPredecessor != reauthorization.PredecessorDecision.AuthorityId)
            {
                rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_predecessor_mismatch";
                return false;
            }
        }
        else if (request.Kind == FileTransferCoordinatorActionKind.FallbackTailQuiescent &&
                 state.PendingFallbackTailYieldTransportEpochReauthorization is not null)
        {
            rejectionReason = "fallback_tail_yield_transport_epoch_reauthorization_missing";
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

        if (request.Kind == FileTransferCoordinatorActionKind.RecoveryLivenessLeaseBegin &&
            state.RouteSelection.Route == FileTransferRoute.RegularNknV4Fast &&
            state.RouteSelection.ProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
            state.CurrentBridgeRecoveryDecision is
            {
                Accepted: true,
                RecoveryKind: BridgeRecoveryKind.BridgeReceiveStallRecovery,
            })
        {
            rejectionReason = "regular_v4_recovery_liveness_lease_must_be_issued_atomically";
            return false;
        }

        if (request.Kind is FileTransferCoordinatorActionKind.LivenessDeferral or
            FileTransferCoordinatorActionKind.RecoveryLivenessLeaseValidate or
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

        if (request.Kind == FileTransferCoordinatorActionKind.RecoveryLeaseClosureLivenessDeferral &&
            !TryValidateRuntimeUnlockRegularV4RecoveryLeaseClosureContinuation(
                state,
                request,
                out rejectionReason))
        {
            return false;
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

    private static bool HasRegularV4FrontierRepairPressureBinding(FileTransferCoordinatorState state)
        => state.PendingRegularV4FrontierRepairPressureBinding is not null ||
           state.InstalledRegularV4FrontierRepairPressureBinding is not null ||
           state.RetainedRegularV4FrontierRepairPressurePredecessor is not null;

    private static bool ValidateRegularV4FrontierRepairPressureRepairReplayRequest(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorActionRequest request,
        out RepairReplayDecision? idempotentDecision,
        out string rejectionReason)
    {
        idempotentDecision = null;
        rejectionReason = "accepted";
        if (request.Kind != FileTransferCoordinatorActionKind.RepairReplay ||
            !HasRegularV4FrontierRepairPressureBinding(state))
        {
            return true;
        }

        var governingBinding =
            state.PendingRegularV4FrontierRepairPressureBinding ??
            state.InstalledRegularV4FrontierRepairPressureBinding;
        if (governingBinding is not { } binding ||
            state.CurrentRepairReplayDecision is not { } currentDecision ||
            !IsRepairReplayDecisionCurrent(state, currentDecision, out _) ||
            currentDecision.AuthorityId != binding.Authority.AuthorityId ||
            currentDecision.RepairGeneration != binding.Authority.DecisionGeneration)
        {
            rejectionReason =
                "regular_v4_frontier_repair_pressure_binding_repair_replay_decision_mismatch";
            return false;
        }

        var requestedPredecessor = request.PredecessorAuthorityId is { IsDefault: false }
            ? request.PredecessorAuthorityId
            : null;
        var exactRepeat =
            (request.RepairReplayKind ?? RepairReplayKind.Unknown) == currentDecision.RepairKind &&
            string.Equals(
                NormalizeReason(request.Reason),
                currentDecision.Reason,
                StringComparison.Ordinal) &&
            requestedPredecessor == currentDecision.PredecessorAuthorityId;
        if (exactRepeat)
        {
            idempotentDecision = currentDecision;
            return true;
        }

        if (state.PendingRegularV4FrontierRepairPressureBinding is not null)
        {
            rejectionReason =
                "regular_v4_frontier_repair_pressure_binding_repair_replay_pending_conflict";
            return false;
        }

        if (state.RetainedRegularV4FrontierRepairPressurePredecessor is not null)
        {
            rejectionReason =
                "regular_v4_frontier_repair_pressure_binding_predecessor_retirement_pending";
            return false;
        }

        if (state.InstalledRegularV4FrontierRepairPressureBinding is not { } installed ||
            requestedPredecessor != installed.Authority.AuthorityId)
        {
            rejectionReason =
                "regular_v4_frontier_repair_pressure_binding_predecessor_mismatch";
            return false;
        }

        return true;
    }

    private static bool RequiresRegularV4FrontierRepairPressureRetirement(
        FileTransferCoordinatorActionKind kind)
        => IsRoutePublicationAuthorityAction(kind) ||
           IsTransportEpochAuthorityAction(kind) ||
           IsBridgeRecoveryAuthorityAction(kind);

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
            FileTransferRoute.DiagnosticRegularNknV6 => FileTransferProtocol.ProtocolVersionV6,
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

    private static bool TryValidateRuntimeUnlockRegularV4RecoveryLeaseClosureContinuation(
        FileTransferCoordinatorState state,
        FileTransferCoordinatorActionRequest request,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (request.RegularV4RecoveryLeaseClosureContinuationProof is not { } proof ||
            proof.IsDefault)
        {
            rejectionReason = "regular_v4_recovery_lease_closure_continuation_proof_missing";
            return false;
        }

        if (request.LivenessDeferralDeadlineUtc is not { } requestedDeadline ||
            requestedDeadline == default)
        {
            rejectionReason = "regular_v4_recovery_lease_closure_continuation_deadline_missing";
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (requestedDeadline <= now ||
            proof.MaximumLivenessDeferralDeadlineUtc <= now)
        {
            rejectionReason = "regular_v4_recovery_lease_closure_continuation_deadline_expired";
            return false;
        }

        if (requestedDeadline > proof.MaximumLivenessDeferralDeadlineUtc)
        {
            rejectionReason = "regular_v4_recovery_lease_closure_continuation_deadline_exceeded";
            return false;
        }

        var expectedDecisionReason = proof.DecisionStatus switch
        {
            RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Accepted
                => "regular_v4_recovery_lifecycle_accepted",
            RuntimeUnlockRegularV4RecoveryLifecycleDecisionStatus.Idempotent
                => "regular_v4_recovery_lifecycle_duplicate",
            _ => string.Empty,
        };
        if (string.IsNullOrWhiteSpace(expectedDecisionReason) ||
            !string.Equals(proof.DecisionReason, NormalizeReason(proof.DecisionReason), StringComparison.Ordinal) ||
            !string.Equals(proof.DecisionReason, expectedDecisionReason, StringComparison.Ordinal))
        {
            rejectionReason = "regular_v4_recovery_lease_closure_continuation_decision_invalid";
            return false;
        }

        if (state.CurrentRecoveryLivenessLease is not null ||
            state.LastRuntimeUnlockRegularV4RecoveryLifecycle != proof.LifecycleReport ||
            state.LastRetiredRecoveryLivenessLease != proof.RetiredRecoveryLivenessLease ||
            request.RecoveryLivenessLease is { } suppliedLease &&
            suppliedLease != proof.RetiredRecoveryLivenessLease ||
            !IsRuntimeUnlockRegularV4RecoveryAuthorityCurrentOrAcceptedClosure(
                state,
                proof.LifecycleReport.RecoveryAuthority))
        {
            rejectionReason = "regular_v4_recovery_lease_closure_continuation_stale";
            return false;
        }

        return true;
    }

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
                   FileTransferCoordinatorActionKind.RecoveryLivenessLeaseRetire or
                   FileTransferCoordinatorActionKind.RecoveryLeaseClosureLivenessDeferral;

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

    private static bool IsCompleteRuntimeUnlockRetryDispatchSettlementAuthority(
        RuntimeUnlockRetryDispatchAuthority authority)
    {
        var owner = authority.RuntimeOwner;
        var publication = authority.SourcePublicationAuthority;
        return !authority.IsDefault &&
               !string.IsNullOrWhiteSpace(authority.SessionId) &&
               !string.IsNullOrWhiteSpace(authority.TransferId) &&
               authority.Direction is FileTransferDirection.Outbound or FileTransferDirection.Inbound &&
               !string.IsNullOrWhiteSpace(authority.RouteToken) &&
               authority.ProtocolVersion > 0 &&
               authority.LiveRouteEpoch >= 0 &&
               authority.RuntimeGeneration > 0 &&
               authority.TerminalGeneration >= 0 &&
               authority.LegGeneration > 0 &&
               authority.TransportEpoch >= 0 &&
               authority.BridgeRecoveryGeneration >= 0 &&
               authority.BridgeDecisionGeneration >= 0 &&
               authority.RoutePublicationGeneration > 0 &&
               authority.RouteCommitGeneration is not <= 0 &&
               authority.RecoveryContractGeneration > 0 &&
               authority.RetiredOfferGeneration >= 0 &&
               authority.DispatchAttemptGeneration > 0 &&
               authority.MissingDispatchSuccessorGeneration >= 0 &&
               !string.IsNullOrWhiteSpace(authority.RetryReason) &&
               !string.IsNullOrWhiteSpace(authority.RecoveryReason) &&
               authority.ReleaseKind != RuntimeUnlockRetryReleaseKind.Unknown &&
               authority.ReleaseAuthorityId.HasValue &&
               !authority.ReleaseAuthorityId.Value.IsDefault &&
               authority.ReleaseGeneration > 0 &&
               !string.IsNullOrWhiteSpace(authority.ReleaseOutcome) &&
               authority.TargetRoute == FileTransferRoute.FileTunaV4 &&
               authority.TargetProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
               authority.TargetTransport == FileTransferTransportKind.Tuna &&
               !string.IsNullOrWhiteSpace(owner.SessionId) &&
               !string.IsNullOrWhiteSpace(owner.TransferId) &&
               !string.IsNullOrWhiteSpace(owner.RouteToken) &&
               !string.IsNullOrWhiteSpace(owner.RuntimeFamily) &&
               !string.IsNullOrWhiteSpace(owner.CreatedReason) &&
               owner.SessionId == authority.SessionId &&
               owner.TransferId == authority.TransferId &&
               owner.Direction == authority.Direction &&
               owner.Route == authority.Route &&
               owner.RouteToken == authority.RouteToken &&
               owner.ProtocolVersion == authority.ProtocolVersion &&
               owner.LiveRouteEpoch == authority.LiveRouteEpoch &&
               owner.RuntimeGeneration == authority.RuntimeGeneration &&
               owner.TerminalGeneration == authority.TerminalGeneration &&
               !string.IsNullOrWhiteSpace(publication.SessionId) &&
               !string.IsNullOrWhiteSpace(publication.TransferId) &&
               !string.IsNullOrWhiteSpace(publication.RouteToken) &&
               !string.IsNullOrWhiteSpace(publication.Reason) &&
               publication.SessionId == authority.SessionId &&
               publication.TransferId == authority.TransferId &&
               publication.Direction == authority.Direction &&
               publication.RuntimeOwner == owner &&
               publication.Route == authority.Route &&
               publication.RouteToken == authority.RouteToken &&
               publication.ProtocolVersion == authority.ProtocolVersion &&
               publication.LiveRouteEpoch == authority.LiveRouteEpoch &&
               publication.RuntimeGeneration == authority.RuntimeGeneration &&
               publication.TerminalGeneration == authority.TerminalGeneration &&
               publication.LegGeneration == authority.LegGeneration &&
               publication.RouteCommitGeneration == authority.RouteCommitGeneration &&
               publication.TargetRoute == authority.Route &&
               publication.TargetProtocolVersion == authority.ProtocolVersion &&
               publication.TargetTransport == FileTransferTransportKind.RegularNkn &&
               publication.ActionKind == FileTransferCoordinatorActionKind.RoutePublication &&
               publication.PublicationGeneration == authority.RoutePublicationGeneration;
    }

    private static string NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason)
            ? "file_transfer_coordinator"
            : reason.Trim();
}
